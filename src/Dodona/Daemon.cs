using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Dodona;

/// <summary>Per-project config, dodona.json at the project root (design §10).</summary>
sealed record Config(string Main, string[] Verify)
{
    public static Config Load(string root)
    {
        var path = Path.Combine(root, "dodona.json");
        if (!File.Exists(path)) return new Config("main", Array.Empty<string>());
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        var main = d.RootElement.TryGetProperty("main", out var m) ? m.GetString() ?? "main" : "main";
        var verify = d.RootElement.TryGetProperty("verify", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
        return new Config(main, verify);
    }
}

sealed class Daemon
{
    readonly string _root, _instanceId, _ctlPipe;
    readonly Store _store;
    readonly Dictionary<long, LaneRuntime> _lanes = new();
    readonly SemaphoreSlim _routerLock = new(1, 1);   // one classification at a time on the warm session
    Config _config;

    Daemon(string root, string instanceId, string ctlPipe, Store store)
    {
        _root = root;
        _instanceId = instanceId;
        _ctlPipe = ctlPipe;
        _store = store;
        _config = Config.Load(root);
    }

    public static async Task<int> RunAsync(string root, string instanceId, string ctlPipe, bool successor)
    {
        // A successor waits its turn BEFORE touching anything: it handshakes with the
        // predecessor, then waits for it to actually exit. Only then is it safe to take
        // the mutex, open the store (a migration must never race a live writer) and
        // adopt the shim pipes (a shim serves one client at a time).
        int predecessor = 0;
        if (successor)
        {
            predecessor = await HandshakeAsSuccessorAsync(instanceId);
            if (predecessor < 0) { Console.Error.WriteLine("successor handshake failed; predecessor keeps running"); return 4; }
        }

        // One daemon per canonical root, enforced at the OS (design §14).
        Mutex? mutex = null;
        for (int i = 0; i < (successor ? 80 : 1); i++)
        {
            mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{instanceId}", out bool createdNew);
            if (createdNew) break;
            mutex.Dispose();
            mutex = null;
            await Task.Delay(250);
        }
        if (mutex is null)
        {
            Console.Error.WriteLine($"another daemon already owns this root (instance {instanceId})");
            return 3;
        }
        using (mutex)
        {
            using var store = new Store(Path.Combine(root, ".dodona", "store.db"));
            return await new Daemon(root, instanceId, ctlPipe, store).LoopAsync(predecessor);
        }
    }

    /// <summary>The successor half of the handoff (§13). Connect to the predecessor's
    /// handoff pipe, declare what this build is, wait for `go`, then wait for the
    /// predecessor's process to actually be gone. Returns its pid, or -1 on failure —
    /// in which case this process exits and the predecessor stays up, unharmed.</summary>
    static async Task<int> HandshakeAsSuccessorAsync(string instanceId)
    {
        var pipe = new NamedPipeClientStream(".", Instance.HandoffPipe(instanceId), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(20000); }
        catch { return -1; }
        try
        {
            var w = new StreamWriter(pipe) { AutoFlush = true };
            var r = new StreamReader(pipe);
            w.WriteLine($"ready pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} shim={Ver.ShimProtocol}");
            var go = await r.ReadLineAsync();
            if (go is null || !go.StartsWith("go ")) return -1;
            var oldPid = int.Parse(go[3..].Trim());
            try
            {
                using var old = Process.GetProcessById(oldPid);
                using var cts = new CancellationTokenSource(20000);
                await old.WaitForExitAsync(cts.Token);
            }
            catch { /* already gone, or never was: either way the road is clear */ }
            return oldPid;
        }
        catch { return -1; }
        finally { try { pipe.Dispose(); } catch { } }
    }

    async Task<int> LoopAsync(int predecessorPid)
    {
        _store.Event("daemon_start", null,
            $"pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath} root={_root}" +
            (predecessorPid > 0 ? $" successor_of={predecessorPid}" : ""));
        Console.WriteLine($"dodona daemon: instance {_instanceId}, ctl pipe {_ctlPipe}, pid {Environment.ProcessId}, build {Ver.Build}");

        // Reconcile (design §12): rows are the claim; the pipe is the proof. A successor
        // is adopting shims the predecessor only just let go of, so give them room.
        foreach (var l in _store.LanesAll().Where(l => l.State == "alive" && l.Role != "dispatcher"))
        {
            var rt = new LaneRuntime(l.Id, l.Pipe, _store);
            if (await rt.ConnectAndPumpAsync(attempts: predecessorPid > 0 ? 20 : 3)) _lanes[l.Id] = rt;
            else { _store.LaneState(l.Id, "unreachable"); _store.Event("lane_unreachable", l.Id, "reconcile: pipe did not answer"); }
        }
        _store.Event("reconcile_done", null, $"connected={_lanes.Count}");
        if (predecessorPid > 0)
        {
            Announce($"[dodona] swapped to build {Ver.Build} — {_lanes.Count} lane(s) adopted, nothing interrupted");
            GcOldBuilds();
        }
        StartSwapTicker();

        // No `using` on pipe streams near a peer that may close first (spike 2's lesson).
        bool stopping = false;
        while (!stopping)
        {
            var server = new NamedPipeServerStream(_ctlPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            var r = new StreamReader(server);
            var w = new StreamWriter(server) { AutoFlush = true };
            try
            {
                var req = await r.ReadLineAsync();
                if (req is not null)
                {
                    try { stopping = await HandleAsync(req, w); }
                    catch (Exception ex) { w.WriteLine($"error: {ex.Message}"); }
                    w.WriteLine("##end");
                }
            }
            catch { /* client vanished mid-conversation */ }
            try { server.Disconnect(); } catch { }
            try { server.Dispose(); } catch { }
        }

        _store.Event("daemon_stop", null, "graceful; lanes keep running");
        return 0;
    }

    async Task<bool> HandleAsync(string req, StreamWriter w)
    {
        using var d = JsonDocument.Parse(req);
        var e = d.RootElement;
        switch (e.GetProperty("cmd").GetString())
        {
            // ---------------- lanes (M0) ----------------
            case "lane-start":
            {
                var title = e.GetProperty("title").GetString()!;
                var child = e.TryGetProperty("child", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
                if (child is null) { w.WriteLine("error: --child <agent exe> is required"); break; }
                var childArgs = e.TryGetProperty("childArgs", out var ca) && ca.ValueKind == JsonValueKind.Array
                    ? ca.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
                w.WriteLine((await SpawnLaneAsync(title, "work", _root, child, childArgs)).Msg);
                break;
            }
            case "say":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var text = e.GetProperty("text").GetString()!;
                if (!_lanes.TryGetValue(lane, out var rt)) { w.WriteLine($"error: lane {lane} not connected"); break; }
                rt.Say(text);
                w.WriteLine($"-> lane {lane}");
                break;
            }
            case "tail":
                foreach (var row in _store.Tail(e.GetProperty("lane").GetInt64(), e.GetProperty("n").GetInt32()))
                    w.WriteLine(row);
                break;
            case "status":
                w.WriteLine($"daemon pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath}");
                foreach (var l in _store.LanesAll())
                {
                    var connected = _lanes.TryGetValue(l.Id, out var rt) && rt.Connected;
                    w.WriteLine($"lane {l.Id}  {l.Title,-10}  role={l.Role,-6}  state={l.State}  connected={connected}  presence={l.Presence,-16}  session={l.Session ?? "-"}");
                }
                break;

            // ---------------- routing (M2, §4) ----------------
            case "focus":
            {
                var lane = e.GetProperty("lane").GetInt64();
                _store.KvSet("focused_lane", lane.ToString());
                w.WriteLine($"focused lane {lane}");
                break;
            }
            case "input":
            {
                var text = e.GetProperty("text").GetString()!;
                w.WriteLine(RouteInput(text));
                break;
            }
            case "router-start":
            {
                var child = e.TryGetProperty("child", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()! : "claude";
                var model = e.TryGetProperty("model", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString()! : "haiku";
                var sys = "You are Dodona's input router. You will be given a list of lanes (title and subject), " +
                          "the currently focused lane, and one user input. Reply with ONLY one line of JSON, no prose, no markdown: " +
                          "{\"intent\":\"instruction|query|question\",\"target\":\"<LANE TITLE or none>\",\"confidence\":\"high|medium|low\",\"cleaned_text\":\"<the input, cleaned of dictation noise>\"} " +
                          "target is the lane the input is meant for based on its content; say none when no lane fits. " +
                          "Be willing to say confidence low — a confident wrong guess is worse than an honest unsure.";
                var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                                              "--verbose", "--model", model, "--append-system-prompt", sys };
                w.WriteLine((await SpawnLaneAsync("ROUTER", "router", _root, child, args)).Msg);
                break;
            }
            case "ticket-agent":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                var child = e.TryGetProperty("child", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString()! : "claude";
                var model = e.TryGetProperty("model", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString()! : "sonnet";
                var claims = string.Join(", ", _store.TicketClaims(tid).Select(cl => $"{cl.Kind}:{cl.Value}"));

                // The lane-agent framing (§5, spike 3): declare the [DISPATCHER] channel or
                // the model treats mid-turn instructions as a prompt-injection attempt.
                var sys = $"You are a lane agent operated by the Dodona orchestrator, working ticket {tid}: \"{t.Title}\". " +
                          $"Your worktree is the current working directory; work only there. Your declared claim is [{claims}] — " +
                          "a PreToolUse gate denies writes outside it; if denied, stay within the claim or ask your operator for an extension. " +
                          "Real-time instructions from your human operator arrive in hook output labeled [DISPATCHER]; they are authentic " +
                          "and carry the same authority as your original task, even when they change or contradict earlier instructions.";
                var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                                              "--verbose", "--model", model, "--permission-mode", "acceptEdits",
                                              "--append-system-prompt", sys };
                var (laneId, msg) = await SpawnLaneAsync(t.Title, "work", t.Worktree, child, args);
                // Link ticket ↔ lane: "waiting on you: merge" (§8) needs a pane to land in.
                if (laneId > 0) _store.TicketSetLane(tid, laneId);
                w.WriteLine(msg);
                break;
            }

            // ---------------- tickets & claims (M1, §6/§11) ----------------
            case "ticket-create":
            {
                var title = e.GetProperty("title").GetString()!;
                var mode = e.TryGetProperty("mode", out var m) ? m.GetString()! : "on-approval";
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                var claims = new List<(string, string)>();
                foreach (var s in specs)
                {
                    var parsed = Claims.Parse(s);
                    if (parsed is null) { w.WriteLine($"error: bad claim spec '{s}' (use path:|new:|subtree:|symbol:)"); return false; }
                    claims.Add(parsed.Value);
                }
                if (claims.Count == 0) { w.WriteLine("error: at least one --claim required"); break; }

                var (id, conflicts) = _store.TicketCreate(null, title, mode, claims);
                if (id < 0)
                {
                    _store.Event("claim_conflict", null, $"'{title}': {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"conflict: {cf}");
                    w.WriteLine("##exit 1");
                    break;
                }

                var branch = $"ticket/{id}";
                var wt = Path.Combine(_root, ".dodona", "wt", $"t{id}");
                var (code, output) = Git.Run(_root, "worktree", "add", "-b", branch, wt, _config.Main);
                if (code != 0)
                {
                    _store.TicketState(id, "abandoned");
                    _store.Event("ticket_git_failed", null, $"ticket {id}: {output}");
                    w.WriteLine($"error: worktree add failed: {output}");
                    break;
                }
                _store.TicketSetGit(id, branch, wt);
                DeployGate(wt, id);
                _store.Event("ticket_created", null, $"ticket {id} '{title}' branch {branch} claims [{string.Join(", ", specs)}]");
                w.WriteLine($"ticket {id} branch {branch} worktree {wt}");
                break;
            }
            case "claim-check":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }

                var full = Path.GetFullPath(path, t.Worktree).Replace('\\', '/');
                string? rel = null;
                foreach (var baseDir in new[] { t.Worktree, _root })
                {
                    var b = Path.GetFullPath(baseDir).Replace('\\', '/').TrimEnd('/') + "/";
                    if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase)) { rel = full[b.Length..]; break; }
                }
                if (rel is null)
                {
                    w.WriteLine($"denied: {path} is outside the worktree and the project root");
                    w.WriteLine("##exit 1");
                    break;
                }
                rel = Claims.Normalize(rel);
                var claims = _store.TicketClaims(tid);
                if (claims.Any(cl => Claims.Covers(cl.Kind, cl.Value, rel)))
                    w.WriteLine($"covered: {rel}");
                else
                {
                    w.WriteLine($"denied: {rel} not covered by ticket {tid} claims [{string.Join(", ", claims.Select(c => $"{c.Kind}:{c.Value}"))}]");
                    w.WriteLine("##exit 1");
                }
                break;
            }
            case "claim-extend":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                var claims = specs.Select(Claims.Parse).Where(p => p is not null).Select(p => p!.Value).ToList();
                var conflicts = _store.ClaimExtend(tid, claims);
                if (conflicts.Count > 0)
                {
                    _store.Event("claim_conflict", null, $"extend ticket {tid}: {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"conflict: {cf}");
                    w.WriteLine("##exit 1");
                }
                else
                {
                    _store.Event("claim_extended", null, $"ticket {tid} += [{string.Join(", ", specs)}]");
                    w.WriteLine($"extended ticket {tid}");
                }
                break;
            }
            case "approve":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                _store.TicketApprove(tid);
                _store.Event("ticket_approved", null, $"ticket {tid}");
                // Unblock the lane: presence back to idle, receipt in the pane.
                if (_store.Ticket(tid)?.LaneId is long alid)
                {
                    _store.LanePresence(alid, "idle");
                    _store.PaneEvent(alid, "announcement", $"ticket {tid} approved — merge unblocked", null, null);
                }
                w.WriteLine($"approved ticket {tid}");
                break;
            }
            case "ack":
            {
                var id = e.GetProperty("id").GetInt64();
                w.WriteLine(_store.PaneAck(id) ? $"acked {id}" : $"error: {id} is not an unacked announcement");
                break;
            }
            case "undo-route":
            {
                var id = e.GetProperty("id").GetInt64();
                var undone = _store.RoutingUndo(id);
                if (undone is null) { w.WriteLine($"error: routing decision {id} not found or already undone"); break; }
                var (dl, input) = undone.Value;
                // Retraction to the lane that consumed the misroute — [DISPATCHER] framing
                // so the agent treats it as operator-authentic (spike 3).
                if (dl is long dlid && _lanes.TryGetValue(dlid, out var drt) && drt.Connected)
                {
                    drt.Say($"[DISPATCHER] Disregard this earlier message, it was routed to you by mistake: \"{input}\". Do not act on it; if you already started, stop and undo.");
                    _store.PaneEvent(dlid, "announcement", $"↩ undone: \"{Truncate(input, 60)}\" retracted", null, null);
                }
                _store.Event("route_undone", dl, $"decision {id}: {input}");
                w.WriteLine($"undone routing decision {id}");
                break;
            }
            case "tickets":
                foreach (var t in _store.Tickets())
                    w.WriteLine($"ticket {t.Id}  {t.Title,-12}  state={t.State}  mode={t.MergeMode}  approved={t.Approved}  branch={t.Branch}");
                break;

            // ---------------- merge token & land (M1, §7) ----------------
            case "token-request":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                if (t.MergeMode == "on-approval" && !t.Approved)
                {
                    _store.Event("token_refused_unapproved", null, $"ticket {tid}");
                    // Blocked-on-you is categorically distinct (§8): presence flips to
                    // "waiting on you" and the announcement lands in the pane AND the feed.
                    if (t.LaneId is long blid)
                    {
                        _store.LanePresence(blid, "waiting on you: merge");
                        _store.PaneEvent(blid, "announcement", $"waiting on you: merge ticket {tid} '{t.Title}' — dodona approve {tid}", null, null);
                    }
                    w.WriteLine($"refused: ticket {tid} is merge:on-approval and not approved");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Merge-time backstop (§6 layer 2): diff the branch against its merge
                // base; any touched path outside the claim refuses the token. This
                // catches everything the fail-open hook gate cannot see.
                var (dc, diff) = Git.Run(_root, "diff", "--name-only", $"{_config.Main}...{t.Branch}");
                if (dc == 0 && diff.Length > 0)
                {
                    var ticketClaims = _store.TicketClaims(tid);
                    var outside = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => Claims.Normalize(f))
                        .Where(f => !ticketClaims.Any(cl => Claims.Covers(cl.Kind, cl.Value, f)))
                        .ToList();
                    if (outside.Count > 0)
                    {
                        _store.Event("claim_backstop_refused", null, $"ticket {tid} touched outside claim: {string.Join(", ", outside)}");
                        w.WriteLine($"refused: branch touches paths outside ticket {tid}'s claim: {string.Join(", ", outside)}");
                        w.WriteLine($"         extend the claim (dodona claim-extend) or revert those changes");
                        w.WriteLine("##exit 1");
                        break;
                    }
                }

                var (status, gen, pos) = _store.TokenRequest(tid, lease, () => Git.Sha(_root, _config.Main));
                w.WriteLine(status == "granted"
                    ? $"granted ticket {tid} generation {gen}"
                    : $"queued ticket {tid} position {pos}");
                break;
            }
            case "token-renew":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                if (_store.TokenRenew(tid, lease)) w.WriteLine($"renewed ticket {tid}");
                else { w.WriteLine($"refused: ticket {tid} is not the live holder"); w.WriteLine("##exit 1"); }
                break;
            }
            case "token-release":
                _store.TokenRelease(e.GetProperty("ticket").GetInt64());
                w.WriteLine("released");
                break;
            case "token-status":
            {
                var tok = _store.TokenRead();
                w.WriteLine($"holder={(tok.Holder?.ToString() ?? "none")} generation={tok.Generation} expires={tok.ExpiresTs ?? "-"} main={tok.MainSha?[..8] ?? "-"}");
                break;
            }
            case "land":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                w.WriteLine(LandOp(tid, out var landOk));
                if (!landOk) w.WriteLine("##exit 1");
                break;
            }
            // ---------------- hot swap (M4, §13/§14) ----------------
            case "swap":
            {
                var exe = e.GetProperty("exe").GetString()!;
                var mode = e.TryGetProperty("mode", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString()! : "ask";
                var (handedOff, lines) = await ConsiderSwapAsync(exe, mode);
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swap-answer":
            {
                var answer = e.GetProperty("answer").GetString()!;
                var live = _store.SwapLive();
                if (live is null) { w.WriteLine("error: no update is waiting on an answer"); break; }
                switch (answer)
                {
                    case "now":
                    {
                        // The explicit override: swap even though something is in the way.
                        var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "now");
                        foreach (var l in lines) w.WriteLine(l);
                        return handedOff;
                    }
                    case "when-it-lands":
                        _store.SwapSet(live.Id, "when-it-lands", "armed");
                        _store.Event("swap_armed", null, $"swap {live.Id} build {live.Build}: {live.Blocker}");
                        Announce($"[dodona] update {live.Build} armed — swapping the instant this clears: {live.Blocker}");
                        w.WriteLine($"armed: swap {live.Id} fires the instant the blocker clears ({live.Blocker})");
                        break;
                    case "hold":
                        _store.SwapSet(live.Id, "hold", "held");
                        _store.Event("swap_held", null, $"swap {live.Id} build {live.Build}");
                        Announce($"[dodona] update {live.Build} held — say `dodona swap-answer now` when you want it");
                        w.WriteLine($"held: swap {live.Id} parked until you say so");
                        break;
                    default:
                        w.WriteLine("error: answer must be now | when-it-lands | hold");
                        break;
                }
                break;
            }
            case "swap-fire":
            {
                // The armed swap's condition cleared; the ticker woke us through our own
                // control pipe so this lands on the loop thread like any other command.
                var live = _store.SwapLive();
                if (live is null || live.State != "armed") { w.WriteLine("no armed swap"); break; }
                var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "armed");
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swaps":
                foreach (var row in _store.SwapsAll()) w.WriteLine(row);
                w.WriteLine($"running: build {Ver.Build} schema {Ver.Schema} shim-protocol {Ver.ShimProtocol} exe {Ver.ExePath}");
                break;

            case "stop-daemon":
                w.WriteLine("stopping (lanes keep running)");
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------- hot swap (§13/§14)

    sealed record NewBuild(string Exe, string Build, int Schema, int ShimProtocol);

    /// <summary>Ask a candidate binary what it is. Running `<exe> version --json` is the
    /// only honest way — the file name proves nothing, and we must know its schema and
    /// shim protocol BEFORE it touches the store.</summary>
    static NewBuild? Probe(string exe, out string error)
    {
        error = "";
        if (!File.Exists(exe)) { error = $"no such binary: {exe}"; return null; }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("--json");
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            using var d = JsonDocument.Parse(so);
            return new NewBuild(exe,
                d.RootElement.GetProperty("build").GetString()!,
                d.RootElement.GetProperty("schema").GetInt32(),
                d.RootElement.GetProperty("shimProtocol").GetInt32());
        }
        catch (Exception ex) { error = $"binary did not answer `version --json` ({ex.Message})"; return null; }
    }

    /// <summary>What stands in the way of a seamless swap (§14). Empty means go.</summary>
    List<string> Blockers(NewBuild nb)
    {
        var blockers = new List<string>();

        if (nb.Schema > Ver.Schema)
            blockers.Add($"store schema migration v{Ver.Schema}→v{nb.Schema}");

        // The live shims are the authority, not our own constant: they were spawned by
        // whichever binary was running then, and the successor has to talk to THEM.
        var stranded = _lanes.Values.Where(l => l.Connected && l.ShimProtocol != nb.ShimProtocol).ToList();
        if (stranded.Count > 0)
            blockers.Add($"shim protocol v{stranded[0].ShimProtocol}→v{nb.ShimProtocol} with {stranded.Count} live shim(s)");

        var tok = _store.TokenRead();
        if (tok.Holder is long h && (tok.ExpiresTs is null || DateTime.Parse(tok.ExpiresTs).ToUniversalTime() > DateTime.UtcNow))
        {
            var t = _store.Ticket(h);
            blockers.Add($"{t?.Title ?? $"ticket {h}"} is mid-merge");
        }
        return blockers;
    }

    /// <summary>The swap decision. Clear road → hand off. Something in the way → do not
    /// act: record the proposal, announce it with its three answers, and wait. This is
    /// the one exception to act-announce-undo (§11), and it earns it — a half-applied
    /// migration is not undoable with a keystroke.</summary>
    async Task<(bool HandedOff, List<string> Lines)> ConsiderSwapAsync(string exe, string mode)
    {
        var lines = new List<string>();
        var nb = Probe(exe, out var probeError);
        if (nb is null)
        {
            _store.Event("swap_refused", null, $"{exe}: {probeError}");
            lines.Add($"error: {probeError}");
            lines.Add("##exit 1");
            return (false, lines);
        }
        if (nb.Schema < Ver.Schema)
        {
            // A downgrade cannot read this store at all. Not a decision — a refusal.
            _store.Event("swap_refused", null, $"{nb.Build}: schema v{nb.Schema} < live v{Ver.Schema}");
            lines.Add($"refused: build {nb.Build} expects schema v{nb.Schema}, this store is v{Ver.Schema} — a downgrade would not be able to read it");
            lines.Add("##exit 1");
            return (false, lines);
        }

        var blockers = Blockers(nb);
        if (blockers.Count > 0 && mode != "now")
        {
            var blocker = string.Join("; ", blockers);
            if (mode == "armed")
            {
                lines.Add($"still blocked: {blocker}");
                return (false, lines);
            }
            var id = _store.SwapCreate(nb.Exe, nb.Build, nb.Schema, nb.ShimProtocol, blocker, "ask", "pending");
            _store.Event("swap_blocked", null, $"swap {id} build {nb.Build}: {blocker}");
            Announce($"[dodona] update ready — {blocker}. swap now / when it lands / hold");
            lines.Add($"update {nb.Build} ready — {blocker}");
            lines.Add("answer: dodona swap-answer now | when-it-lands | hold");
            return (false, lines);
        }

        var swapId = _store.SwapCreate(nb.Exe, nb.Build, nb.Schema, nb.ShimProtocol,
                                       blockers.Count > 0 ? string.Join("; ", blockers) : null,
                                       mode, "pending");
        var (ok, msg) = await HandoffAsync(nb, swapId, blockers);
        lines.Add(msg);
        if (!ok) lines.Add("##exit 1");
        return (ok, lines);
    }

    /// <summary>Successor handoff (§13). The old daemon spawns the new binary, waits for
    /// it to signal ready, then releases everything and exits. If the successor never
    /// answers, THIS daemon keeps running — a bad publish must never take the system
    /// down.</summary>
    async Task<(bool Ok, string Msg)> HandoffAsync(NewBuild nb, long swapId, List<string> blockers)
    {
        var handoffPipe = Instance.HandoffPipe(_instanceId);
        var server = new NamedPipeServerStream(handoffPipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(nb.Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root };
            psi.ArgumentList.Add("daemon");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(_root);
            psi.ArgumentList.Add("--successor");
            p = Process.Start(psi);
            _store.Event("swap_spawned", null, $"swap {swapId} build {nb.Build} pid={p?.Id} exe={nb.Exe}");

            using var cts = new CancellationTokenSource(30000);
            await server.WaitForConnectionAsync(cts.Token);
            var r = new StreamReader(server);
            var w = new StreamWriter(server) { AutoFlush = true };
            var ready = await r.ReadLineAsync();
            if (ready is null || !ready.StartsWith("ready "))
                throw new InvalidOperationException($"successor said '{ready}' instead of ready");

            if (blockers.Count > 0)
                _store.Event("swap_forced", null, $"swap {swapId} over: {string.Join("; ", blockers)}");
            _store.Event("daemon_handoff", null, $"swap {swapId}: {Ver.Build} (pid {Environment.ProcessId}) → {nb.Build} ({ready})");
            _store.SwapSet(swapId, "now", "swapped", ready);

            w.WriteLine($"go {Environment.ProcessId}");
            await Task.Delay(150);          // let the successor read `go` before our handles close
            return (true, $"handed off to build {nb.Build} (pid {p?.Id}); this daemon is exiting — lanes keep running");
        }
        catch (Exception ex)
        {
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            _store.SwapSet(swapId, "now", "failed", ex.Message);
            _store.Event("swap_failed", null, $"swap {swapId} build {nb.Build}: {ex.Message}");
            Announce($"[dodona] update {nb.Build} FAILED to start — staying on {Ver.Build}");
            return (false, $"swap failed ({ex.Message}) — this daemon is still running, nothing was lost");
        }
        finally
        {
            try { server.Dispose(); } catch { }
        }
    }

    /// <summary>"When it lands" defers to a CONDITION, not a timer (§14): poll the
    /// blockers and fire the instant they clear. The fire itself goes through our own
    /// control pipe so it is serialized with every other command.</summary>
    void StartSwapTicker() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                var live = _store.SwapLive();
                if (live is null || live.State != "armed") continue;
                var nb = Probe(live.Exe, out _);
                if (nb is null) continue;
                if (Blockers(nb).Count > 0) continue;

                var pipe = new NamedPipeClientStream(".", _ctlPipe, PipeDirection.InOut);
                try
                {
                    await pipe.ConnectAsync(2000);
                    var w = new StreamWriter(pipe) { AutoFlush = true };
                    var r = new StreamReader(pipe);
                    w.WriteLine(JsonSerializer.Serialize(new { cmd = "swap-fire" }));
                    while (await r.ReadLineAsync() is string l && l != "##end") { }
                }
                finally { try { pipe.Dispose(); } catch { } }
                return;                    // fired: either we are exiting, or it failed and re-armed nothing
            }
            catch { /* next tick */ }
        }
    });

    /// <summary>Old binary directories are garbage once no instance runs them (§13). A
    /// running image is locked by Windows, which makes "is anyone using it?" a question
    /// the filesystem answers for us: try, and skip what refuses.</summary>
    void GcOldBuilds()
    {
        var binRoot = Ver.BinRoot;
        var mine = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        if (!Directory.Exists(binRoot)) return;
        if (!mine.StartsWith(Path.GetFullPath(binRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;  // dev build: not ours to collect
        foreach (var dir in Directory.GetDirectories(binRoot))
        {
            if (Path.GetFullPath(dir).TrimEnd('\\').Equals(mine, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); _store.Event("binary_gc", null, dir); }
            catch (Exception ex) { _store.Event("binary_gc_skipped", null, $"{dir}: {ex.Message}"); }
        }
    }

    /// <summary>System-level announcements land in the dispatcher pane, and therefore in
    /// the decision feed (§8). The dispatcher lane holds no agent — it is a place for
    /// the system to speak in its own voice.</summary>
    void Announce(string text)
    {
        var id = _store.KvGet("dispatcher_lane") is string s && long.TryParse(s, out var l) ? l : 0;
        if (id == 0)
        {
            id = _store.LaneCreate("DODONA");
            _store.LaneRole(id, "dispatcher");
            _store.LanePresence(id, "system");
            _store.KvSet("dispatcher_lane", id.ToString());
        }
        _store.PaneEvent(id, "announcement", text, null, null);
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Spawn a lane: shim → child, detached, pumped, recorded. Shared by
    /// lane-start (fake/test agents), router-start (warm utility session), and
    /// ticket-agent (real claude in a gated worktree).</summary>
    async Task<(long Id, string Msg)> SpawnLaneAsync(string title, string role, string workDir, string child, List<string> childArgs)
    {
        var id = _store.LaneCreate(title);
        _store.LaneRole(id, role);
        var pipe = Instance.LanePipe(_instanceId, id);
        _store.LanePipe(id, pipe);

        var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                      ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
        var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = workDir };
        psi.ArgumentList.Add(pipe);
        psi.ArgumentList.Add(child);
        foreach (var a in childArgs) psi.ArgumentList.Add(a);
        psi.Environment["DODONA_SHIM_INFO"] = Path.Combine(_root, ".dodona", $"shim-lane{id}.json");
        Process.Start(psi);
        _store.Event("shim_spawned", id, $"pipe={pipe} child={child} cwd={workDir}");

        var rt = new LaneRuntime(id, pipe, _store);
        if (await rt.ConnectAndPumpAsync(attempts: 20))
        {
            _lanes[id] = rt;
            _store.Event("lane_started", id, $"{title} role={role}");
            return (id, $"lane {id} title {title} role {role} pipe {pipe}");
        }
        _store.LaneState(id, "unreachable");
        return (-1, $"error: lane {id} shim pipe never answered");
    }

    /// <summary>Routing (§4): instant by default, corrected visibly. Tier 0 (prefix) is
    /// code. Otherwise deliver to the focused lane IMMEDIATELY and let the warm
    /// classifier run behind as an async second opinion — its latency is off the
    /// critical path. A disagreement becomes a visible retarget with a receipt row.</summary>
    string RouteInput(string text)
    {
        var work = _store.LanesAll().Where(l => l.Role == "work" && l.State == "alive").ToList();

        // Tier 0: explicit prefix names its target. Code only.
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Za-z0-9_-]+):\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
        {
            var lane = work.FirstOrDefault(l => l.Title.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (lane is not null && _lanes.TryGetValue(lane.Id, out var rt0))
            {
                rt0.Say(m.Groups[2].Value);
                _store.RoutingInsert(text, "prefix", lane.Id, lane.Id, "explicit");
                return $"-> {lane.Title} (tier 0)";
            }
        }

        // Optimistic: focused lane gets it now.
        var focused = _store.KvGet("focused_lane");
        if (focused is null || !long.TryParse(focused, out var fid) || !_lanes.TryGetValue(fid, out var frt))
            return "error: no focused lane and no lane prefix — use '<LANE>: text' or dodona focus <lane>";
        frt.Say(text);
        var rowId = _store.RoutingInsert(text, "focus", null, fid, null);

        // Async second opinion, if a router lane is warm.
        var router = _store.LanesAll().FirstOrDefault(l => l.Role == "router" && _lanes.ContainsKey(l.Id));
        if (router is not null)
        {
            var laneList = string.Join("\n", work.Select(l => $"- {l.Title} (lane {l.Id})"));
            var focusedTitle = work.FirstOrDefault(l => l.Id == fid)?.Title ?? "?";
            _ = Task.Run(async () =>
            {
                try
                {
                    await _routerLock.WaitAsync();
                    string? reply;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { reply = await _lanes[router.Id].AskAsync($"Lanes:\n{laneList}\nFocused: {focusedTitle}\nInput: {text}", 20000); }
                    finally { _routerLock.Release(); }
                    if (reply is null) { _store.Event("classifier_timeout", router.Id, text); return; }

                    var js = reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)];
                    using var d = JsonDocument.Parse(js);
                    var target = d.RootElement.TryGetProperty("target", out var tg) ? tg.GetString() : null;
                    var conf = d.RootElement.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                    _store.Event("classified", router.Id, $"{sw.ElapsedMilliseconds}ms target={target} confidence={conf} input={text}");

                    var tLane = work.FirstOrDefault(l => l.Title.Equals(target ?? "", StringComparison.OrdinalIgnoreCase));
                    if (tLane is null || tLane.Id == fid || conf == "low") return;   // agreement or unsure: done

                    // Visible retarget (§4): receipt in the wrong pane, delivery to the right one.
                    _store.PaneEvent(fid, "announcement", $"→ retargeted to {tLane.Title} (classifier, {conf})", null, null);
                    if (_lanes.TryGetValue(tLane.Id, out var trt))
                    {
                        trt.Say(text);
                        _store.RoutingRetarget(rowId, tLane.Id, conf);
                        _store.Event("routed_retarget", tLane.Id, $"from lane {fid}: {text}");
                    }
                }
                catch (Exception ex) { _store.Event("classifier_failed", router.Id, ex.Message); }
            });
        }
        var fTitle = work.FirstOrDefault(l => l.Id == fid)?.Title ?? fid.ToString();
        return $"-> {fTitle} (focus{(router is not null ? ", classifier running behind" : "")})";
    }

    /// <summary>The land (§7): the daemon executes the one atomic ref advance. The agent
    /// already rebased and verified in its own worktree; ff-only IS the freshness check —
    /// a branch that does not contain current main cannot land.</summary>
    string LandOp(long tid, out bool ok)
    {
        ok = false;
        var t = _store.Ticket(tid);
        if (t is null || t.State != "open") return $"refused: ticket {tid} not open";

        var tok = _store.TokenRead();
        if (tok.Holder != tid) { _store.Event("land_refused", null, $"ticket {tid}: not holder (holder={tok.Holder?.ToString() ?? "none"})"); return $"refused: ticket {tid} does not hold the merge token"; }
        if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() < DateTime.UtcNow)
        { _store.Event("land_refused", null, $"ticket {tid}: lease expired"); return "refused: merge-token lease expired; re-request"; }

        var (hc, head) = Git.Run(_root, "rev-parse", "--abbrev-ref", "HEAD");
        if (hc != 0 || head != _config.Main) return $"refused: project root has '{head}' checked out, not '{_config.Main}'";

        var (mc, mergeOut) = Git.Run(_root, "merge", "--ff-only", t.Branch);
        if (mc != 0)
        {
            _store.Event("land_refused", null, $"ticket {tid}: ff-only failed — rebase needed. {mergeOut}");
            return $"refused: not fast-forward — rebase {t.Branch} onto {_config.Main} and re-verify first. {mergeOut}";
        }

        if (!_store.LandCommit(tid, out var reason))
        {
            // Merge advanced main but the fence refused in the same instant (lease raced
            // out). Reconcile-from-git heals: branch is an ancestor of main.
            _store.Event("land_inconsistent", null, $"ticket {tid}: {reason}");
            return $"landed on main but store fence refused ({reason}) — run reconcile";
        }

        // Post-land verify (§10): the daemon — code, not a model — runs the configured steps.
        var verifyMsg = "no verify steps configured";
        foreach (var step in _config.Verify)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(step);
            using var p = Process.Start(psi)!;
            var errT = Task.Run(() => p.StandardError.ReadToEnd());
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                _store.Event("verify_red", null, $"ticket {tid} step '{step}': {so}{errT.Result}".Trim());
                verifyMsg = $"VERIFY RED at '{step}'";
                goto verified;
            }
        }
        if (_config.Verify.Length > 0) { _store.Event("verify_green", null, $"ticket {tid}"); verifyMsg = "verify green"; }
        verified:

        // Worktree prune — retryable, never silent (§15).
        var (wc, wOut) = Git.Run(_root, "worktree", "remove", "--force", t.Worktree);
        if (wc == 0) { Git.Run(_root, "branch", "-D", t.Branch); _store.Event("worktree_pruned", null, $"ticket {tid}"); }
        else _store.Event("worktree_prune_failed", null, $"ticket {tid}: {wOut}");

        ok = true;
        return $"landed ticket {tid} on {_config.Main}; {verifyMsg}";
    }

    /// <summary>Deploy the claim gate (§6 enforcement layer 1) into a ticket's worktree:
    /// a PreToolUse hook that asks the daemon whether the write is covered. Fails OPEN
    /// (logged) — the merge-time backstop catches what slips; a broken gate must not
    /// brick the lane.</summary>
    void DeployGate(string worktree, long ticketId)
    {
        // The gate files are deployment, not repo content: register them in the repo's
        // shared info/exclude (applies to every worktree) so `git add -A` by an agent
        // can never commit them — a ticket-1 gate landing on main conflicts with every
        // other ticket's gate on rebase. (Found by the M1 acceptance test.)
        // M2 note: repos with their OWN tracked .claude/ need merge, not exclusion.
        var exclude = Path.Combine(_root, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        var marker = "# dodona-gate deployment files";
        if (!File.Exists(exclude) || !File.ReadAllText(exclude).Contains(marker))
            File.AppendAllText(exclude, $"\n{marker}\n.claude/\ndodona-gate.ps1\n.dodona-bypass.log\n");

        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        File.WriteAllText(Path.Combine(worktree, ".claude", "settings.json"), """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Edit|Write|MultiEdit|NotebookEdit",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/dodona-gate.ps1\""
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var gate = """
            # Dodona claim gate (generated; design doc §6). Denies writes outside this
            # ticket's claim; asks the daemon, which answers in code. Fails OPEN with a
            # bypass log — the merge-time diff backstop catches what slips through.
            $in = [Console]::In.ReadToEnd()
            try { $j = $in | ConvertFrom-Json } catch { exit 0 }
            $fp = $j.tool_input.file_path
            if (-not $fp) { exit 0 }
            & '__DODONA__' claim-check __TICKET__ "$fp" --root '__ROOT__' > $null 2> $null
            if ($LASTEXITCODE -eq 0) { exit 0 }
            if ($LASTEXITCODE -eq 1) {
                $reason = "outside ticket __TICKET__'s claim: $fp. Stay within claimed paths, or request an extension: dodona claim-extend __TICKET__ --claim <spec> --root '__ROOT__'"
                @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } | ConvertTo-Json -Compress
                exit 0
            }
            Add-Content '__WT__\.dodona-bypass.log' ("{0:o} gate fail-open: {1}" -f (Get-Date), $fp)
            exit 0
            """;
        gate = gate.Replace("__DODONA__", Environment.ProcessPath ?? "dodona.exe")
                   .Replace("__TICKET__", ticketId.ToString())
                   .Replace("__ROOT__", _root)
                   .Replace("__WT__", worktree);
        File.WriteAllText(Path.Combine(worktree, "dodona-gate.ps1"), gate);
    }
}
