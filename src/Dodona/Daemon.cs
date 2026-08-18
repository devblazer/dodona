using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Dodona;

/// <summary>Per-project config, dodona.json at the project root (design §10).</summary>
/// <summary>
/// Per-project config, dodona.json at the project root (design §10), plus the model and
/// effort policy (§9's lever, made settable).
///
/// Effort was previously never passed at all, which is worth stating: a lane is its own
/// `claude -p` process and inherits none of the operator's interactive session settings,
/// so "I always run high" was silently not true of any agent Dodona started. It is now a
/// decision with a name and a default rather than an omission.
/// </summary>
sealed record Config(string Main, string[] Verify, string Agent = "claude",
                     string Model = "opus", string Effort = "high",
                     string RouterModel = "haiku", string RouterEffort = "low",
                     string CompressorModel = "haiku", string CompressorEffort = "low",
                     // 0 = no pool unless asked for by hand. Off by default because a warm
                     // pool is real quota drawn from the same window as the lanes (§2.6),
                     // and because every acceptance suite runs on a root with no
                     // dodona.json — a default of 2 would silently put real Haiku sessions
                     // inside seven deliberately model-free suites.
                     int Compressors = 0,
                     PolicyRule[]? Policy = null, string[]? AllowedTools = null,
                     string PermissionMode = "bypassPermissions",
                     // Live updates are not sometimes (operator's standing order): when the
                     // sources are newer than the running image, the daemon publishes and
                     // swaps ITSELF. Every M4 guard applies — a build that fails, or a
                     // successor that never answers, leaves the running system untouched
                     // and announces why. autoPublishProject defaults to the root; the
                     // debounce waits for the tree to go quiet so half-saved edits are
                     // never built.
                     bool AutoPublish = false, string? AutoPublishProject = null,
                     int AutoPublishDebounceSec = 45,
                     // The dispatcher brain (§3's middle rung): management decisions only —
                     // naming, routing, is-this-ticket-worthy. Cheap model first; when it
                     // says its own confidence is low, the SAME question goes to the
                     // expensive tier (operator: "unless it's not very confident, then
                     // route to a more expensive model"). Silent unless it disagrees.
                     bool Brain = true, string BrainModel = "haiku", string BrainEffort = "low")
{
    public PolicyRule[] Rules => Policy ?? Dodona.Policy.Default;

    /// <summary>What a lane may run without asking, beyond edits (§2.9 made concrete —
    /// found by dogfooding: acceptEdits covers edits but not shell, headless mode
    /// auto-denies what it cannot ask about, so the first real lane wrote its change and
    /// then could not build it. Claude allowedTools syntax, e.g. "Bash(dotnet build:*)".
    /// Empty means edits only, which is the safe default for a repo you do not know.</summary>
    public string[] Allowed => AllowedTools ?? Array.Empty<string>();

    /// <summary>A repository's config, falling back to the workspace's. Verify steps and
    /// even the name of `main` belong to the repository, not to the workspace holding
    /// it — one repo may be on `main` and another still on `master`.</summary>
    public static Config For(string workspaceRoot, string repoPath) =>
        File.Exists(Path.Combine(repoPath, "dodona.json")) ? Load(repoPath) : Load(workspaceRoot);

    public static Config Load(string root)
    {
        var path = Path.Combine(root, "dodona.json");
        if (!File.Exists(path)) return new Config("main", Array.Empty<string>());
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        var main = d.RootElement.TryGetProperty("main", out var m) ? m.GetString() ?? "main" : "main";
        var verify = d.RootElement.TryGetProperty("verify", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
        // "agent" is which binary a lane runs. It exists so a project can point at a
        // specific claude, and so the acceptance suite can point at the fake agent and
        // test the paths where the daemon spawns an agent on its own initiative.
        var agent = d.RootElement.TryGetProperty("agent", out var a) ? a.GetString() ?? "claude" : "claude";
        string Str(string key, string fallback) =>
            d.RootElement.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? fallback : fallback;
        int Num(string key, int fallback) =>
            d.RootElement.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : fallback;
        string[]? allowed = null;
        if (d.RootElement.TryGetProperty("allowedTools", out var at) && at.ValueKind == JsonValueKind.Array)
            allowed = at.EnumerateArray().Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray();

        PolicyRule[]? policy = null;
        if (d.RootElement.TryGetProperty("policy", out var p) && p.ValueKind == JsonValueKind.Array)
            policy = p.EnumerateArray().Select(r => new PolicyRule(
                r.TryGetProperty("when", out var wq) ? wq.GetString() ?? "" : "",
                r.TryGetProperty("model", out var mq) ? mq.GetString() ?? "opus" : "opus",
                r.TryGetProperty("effort", out var eq) ? eq.GetString() ?? "high" : "high",
                r.TryGetProperty("why", out var yq) ? yq.GetString() ?? "" : "")).ToArray();

        return new Config(main, verify, agent,
            Str("model", "opus"), Str("effort", "high"),
            Str("routerModel", "haiku"), Str("routerEffort", "low"),
            Str("compressorModel", "haiku"), Str("compressorEffort", "low"), Num("compressors", 2),
            policy, allowed,
            Str("permissionMode", "bypassPermissions"),
            d.RootElement.TryGetProperty("autoPublish", out var ap) && ap.ValueKind == JsonValueKind.True,
            Str("autoPublishProject", ""), Num("autoPublishDebounceSec", 45),
            !(d.RootElement.TryGetProperty("brain", out var br) && br.ValueKind == JsonValueKind.False),
            Str("brainModel", "haiku"), Str("brainEffort", "low"));
    }
}

sealed class Daemon
{
    /// <summary>_instanceId is the WORKSPACE ID (WORKSPACES-CONCIERGE.md §1) — a generated
    /// slug, no longer a hash of a path. _primary is the workspace's primary member: the
    /// folder that stands in wherever this code used to say "the project root" (where a
    /// lane spawns, which dodona.json we fall back to, what repo-init acts on). For a
    /// one-member workspace they are the same thing they always were.</summary>
    readonly string _primary, _instanceId, _wsName, _ctlPipe;
    readonly Store _store;
    readonly Dictionary<long, LaneRuntime> _lanes = new();
    readonly SemaphoreSlim _routerLock = new(1, 1);   // one classification at a time on the warm session
    readonly SemaphoreSlim _brainLoLock = new(1, 1), _brainHiLock = new(1, 1);
    long _brainLo = -1, _brainHi = -1;                // lane ids of the two brain tiers
    // One lock per compressor session, not one for the pool: the point of a pool is that
    // two lanes finishing at once compress concurrently. A single lock would rebuild the
    // serialization point §3 forbids the dispatcher to be (§5).
    readonly Dictionary<long, SemaphoreSlim> _compressorLocks = new();
    int _compressorNext;                              // round-robin cursor
    Config _config;

    /// <summary>This workspace's members, re-read from the registry on demand — the same
    /// doctrine as repo discovery below: a member attached while the daemon runs must be
    /// usable without a restart. Falls back to the primary alone if the registry cannot be
    /// opened, so a locked or missing registry degrades to today's single-root behaviour
    /// rather than to a daemon that can find no repositories at all.</summary>
    List<Member> Members()
    {
        try
        {
            using var reg = new Registry();
            var ws = reg.ById(_instanceId);
            if (ws is not null && ws.Members.Count > 0) return ws.Members;
        }
        catch { }
        return new List<Member> { new(_primary, _primary.ToLowerInvariant(), Registry.LooksLikeRepo(_primary), "") };
    }

    /// <summary>The workspace's repositories, rediscovered on demand — git is the truth,
    /// the registry is a cache of it (§12). A repo added to the workspace while the
    /// daemon runs must be usable without a restart.</summary>
    List<RepoRef> Repositories() => Repos.Discover(Members());

    /// <summary>Where a ticket's git work happens. Falls back to the primary member, so a
    /// ticket written before its repo disappeared still reports honestly rather than
    /// throwing.</summary>
    string RepoPath(string repoName) =>
        Repos.ByName(Repositories(), repoName)?.Path ?? _primary;

    Config ConfigFor(string repoName) => Config.For(_primary, RepoPath(repoName));

    Daemon(string primary, string wsId, string wsName, string ctlPipe, Store store)
    {
        _primary = primary;
        _instanceId = wsId;
        _wsName = wsName;
        _ctlPipe = ctlPipe;
        _store = store;
        _config = Config.Load(primary);
    }

    public static async Task<int> RunAsync(string primary, string wsId, string wsName, string ctlPipe, bool successor)
    {
        // A successor waits its turn BEFORE touching anything: it handshakes with the
        // predecessor, then waits for it to actually exit. Only then is it safe to take
        // the mutex, open the store (a migration must never race a live writer) and
        // adopt the shim pipes (a shim serves one client at a time).
        int predecessor = 0;
        if (successor)
        {
            predecessor = await HandshakeAsSuccessorAsync(wsId);
            if (predecessor < 0) { Console.Error.WriteLine("successor handshake failed; predecessor keeps running"); return 4; }
        }

        // One daemon per WORKSPACE, enforced at the OS (design §14). The id is now the
        // registry's slug rather than a path hash, so what this mutex protects is the
        // workspace's store — and repo-exclusivity (Registry's three layers) is what keeps
        // two workspaces from ever aiming two of these at one main.
        Mutex? mutex = null;
        for (int i = 0; i < (successor ? 80 : 1); i++)
        {
            mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{wsId}", out bool createdNew);
            if (createdNew) break;
            mutex.Dispose();
            mutex = null;
            await Task.Delay(250);
        }
        if (mutex is null)
        {
            Console.Error.WriteLine($"another daemon already owns workspace {wsName} ({wsId})");
            return 3;
        }
        using (mutex)
        {
            using var store = new Store(Paths.Store(wsId));
            return await new Daemon(primary, wsId, wsName, ctlPipe, store).LoopAsync(predecessor);
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
            $"pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath} " +
            $"workspace={_wsName} ({_instanceId}) primary={_primary}" +
            (predecessorPid > 0 ? $" successor_of={predecessorPid}" : ""));
        Console.WriteLine($"dodona daemon: workspace {_wsName} ({_instanceId}), ctl pipe {_ctlPipe}, " +
                          $"pid {Environment.ProcessId}, build {Ver.Build}, store {Paths.Store(_instanceId)}");

        // Reconcile (design §12): rows are the claim; the pipe is the proof. A successor
        // is adopting shims the predecessor only just let go of, so give them room.
        foreach (var l in _store.LanesAll().Where(l => l.State == "alive" && l.Role != "dispatcher"))
        {
            var rt = new LaneRuntime(l.Id, l.Pipe, _store);
            HookCompression(rt, l.Role);
            // A WORK lane gets the patient retry: it may hold a real agent mid-turn, and a
            // successor is adopting shims the predecessor only just let go of. A UTILITY lane
            // gets one attempt — a brain, router or compressor whose pipe does not answer
            // immediately is simply gone, and nothing about it is worth waiting for.
            //
            // This is not a micro-optimisation. Reconcile runs BEFORE the control pipe server,
            // so every wasted attempt is time the daemon is not answerable at all. Measured on
            // a copy of the operator's store, carrying 14 leaked brain lanes with dead pipes:
            // ~2.4s each, about 35 seconds of a daemon that looked hung and refused
            // `stop-daemon` because it had not started listening yet.
            var patient = l.Role == "work";
            var attempts = patient ? (predecessorPid > 0 ? 20 : 3) : 1;
            if (await rt.ConnectAndPumpAsync(attempts)) _lanes[l.Id] = rt;
            else { _store.LaneState(l.Id, "unreachable"); _store.Event("lane_unreachable", l.Id, $"reconcile: pipe did not answer in {attempts} attempt(s)"); }
            // An adopted pool member needs its lock back, or its turns would never gate.
            if (l.Role == "compressor" && _lanes.ContainsKey(l.Id)) _compressorLocks[l.Id] = new SemaphoreSlim(1, 1);

            // ...and an adopted BRAIN needs its tier pointer back, for exactly the same
            // reason. Without this, `_brainLo` was still -1 after reconcile, so the startup
            // warm-up decided no brain existed and spawned a fresh lane — every single
            // daemon start, while the previous brain sat connected, idle and unreachable.
            //
            // Measured on the operator's own instance: 14 BRAIN lanes (lane6…lane19), one per
            // daemon start across a morning of auto-publish swaps, each an idle `claude -p`
            // process nobody could reach. Compressors never leaked because the line above
            // has always re-adopted them; the brain was simply missed when it was added.
            //
            // No quota was burned (LANE-LIFECYCLE §2: quota is consumed by turns, not by
            // existing) — but it grows without bound and buries `dodona status`.
            if (_lanes.ContainsKey(l.Id))
            {
                if (l.Role == "brain") _brainLo = l.Id;
                else if (l.Role == "brain-hi") _brainHi = l.Id;
            }
        }
        // Retire UTILITY lanes whose shim is gone. A brain, router or compressor is fungible
        // infrastructure with no thread: nothing resumes it, nobody reads its transcript, and
        // leaving the row `alive` means every future start spends attempts reconnecting to a
        // pipe that will never answer — 14 leaked brains cost about twelve seconds of dead
        // reconnects at every startup.
        //
        // WORK lanes are deliberately untouched. An unreachable work lane stays visible
        // because that one is a problem to notice, and `lane-respawn` can bring its session
        // back (LANE-LIFECYCLE §1: agents are disposable, the lane is the thread).
        foreach (var l in _store.LanesAll()
                     .Where(l => l.State == "unreachable" && l.Role is "brain" or "brain-hi" or "router" or "compressor")
                     .ToList())
        {
            _store.LaneState(l.Id, "dead");
            _store.Event("utility_lane_reaped", l.Id, $"role={l.Role}: shim gone, nothing to resume");
        }

        // Retire brain lanes the old bug already leaked. Adopting one is what stops NEW ones
        // appearing; this clears the ones a store has accumulated, so an existing instance
        // heals itself on the next start instead of needing the operator to go and count
        // processes. Utility roles only — a work lane is never retired behind the operator's
        // back (LANE-LIFECYCLE §2: no eviction, and a parked lane is often deliberate).
        foreach (var role in new[] { "brain", "brain-hi" })
        {
            var keep = role == "brain" ? _brainLo : _brainHi;
            var surplus = _store.LanesAll()
                .Where(l => l.Role == role && l.State == "alive" && l.Id != keep)
                .ToList();
            foreach (var l in surplus)
            {
                if (_lanes.TryGetValue(l.Id, out var rt)) { rt.Shutdown(); _lanes.Remove(l.Id); }
                _store.LaneState(l.Id, "dead");
                _store.Event("brain_surplus_retired", l.Id, $"role={role}; kept lane {keep}");
            }
            if (surplus.Count > 0)
                Announce($"[dodona] retired {surplus.Count} duplicate {role.ToUpperInvariant()} lane(s) left by a fixed leak — one per daemon start; kept lane {keep}");
        }

        // A leak this quiet needs to be visible in the chain, not just absent.
        _store.Event("reconcile_done", null,
            $"connected={_lanes.Count} brain={(_brainLo > 0 ? _brainLo.ToString() : "-")} " +
            $"brain-hi={(_brainHi > 0 ? _brainHi.ToString() : "-")} compressors={_compressorLocks.Count}");
        if (predecessorPid > 0)
        {
            Announce($"[dodona] swapped to build {Ver.Build} — {_lanes.Count} lane(s) adopted, nothing interrupted");
            GcOldBuilds();
        }
        StartSwapTicker();
        StartDriftWatcher();

        // Warm the compressor pool at daemon start (§5) — a pool that has to be summoned
        // by hand after every restart is a pool that is cold exactly when the first turn
        // finishes. Fire-and-forget: spawning sessions must not delay the daemon becoming
        // answerable, and a pool that fails to start costs nothing but full-length panes.
        if (_config.Compressors > 0 && Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") != "1")
            _ = Task.Run(async () =>
            {
                try
                {
                    var msg = await StartCompressorsAsync(_config.Agent, _config.CompressorModel,
                                                          _config.CompressorEffort, _config.Compressors);
                    _store.Event("compressor_pool", null, msg);
                }
                catch (Exception ex) { _store.Event("compressor_pool_failed", null, ex.Message); }
            });

        // Warm the brain's cheap tier the same way; the expensive tier is spawned lazily
        // on the first escalation — most days it is never needed at all.
        if (_config.Brain && Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") != "1")
            _ = Task.Run(async () =>
            {
                try { _store.Event("brain_started", null, await EnsureBrainAsync(hi: false) >= 0 ? "cheap tier warm" : "failed to warm"); }
                catch (Exception ex) { _store.Event("brain_failed", null, ex.Message); }
            });

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
                var childArgs = e.TryGetProperty("childArgs", out var ca) && ca.ValueKind == JsonValueKind.Array
                    ? ca.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();

                // No --child means the real thing. A lane with no ticket has no claim and
                // therefore no gate — it is plain Claude Code in the workspace, which is
                // fine for one lane and is why isolated work wants a ticket instead.
                if (child is null)
                {
                    w.WriteLine((await SpawnAgentLaneAsync(title, Pick(e, "model", _config.Model), Pick(e, "effort", _config.Effort))).Msg);
                    break;
                }
                w.WriteLine((await SpawnLaneAsync(title, "work", _primary, child, childArgs)).Msg);
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
            case "lane-rename":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var title = e.GetProperty("title").GetString()!.Trim().ToUpperInvariant();
                if (title.Length == 0 || title.Contains(' ')) { w.WriteLine("error: one word"); break; }
                var old = _store.LanesAll().FirstOrDefault(l => l.Id == lane)?.Title;
                if (old is null) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneTitle(lane, title);
                _store.Event("lane_renamed", lane, $"{old} → {title} (operator)");
                _store.PaneEvent(lane, "announcement", $"renamed to {title} (was {old})", null, null, acked: true);
                w.WriteLine($"renamed lane {lane}: {old} → {title}");
                break;
            }
            case "lane-respawn":
            {
                // Agents are fungible; the lane is the thread (§11). A dormant lane (its
                // ticket landed) or an unreachable one (its shim died) comes back as a
                // fresh process resuming the recorded session — spike 1 measured that
                // `--resume` restores full context with the same id and no fork. This is
                // what makes retiring agents cheap enough to do automatically.
                var lane = e.GetProperty("lane").GetInt64();
                var row = _store.LanesAll().FirstOrDefault(l => l.Id == lane);
                if (row is null) { w.WriteLine($"error: no lane {lane}"); break; }
                if (_lanes.TryGetValue(lane, out var lrt2) && lrt2.Connected) { w.WriteLine($"lane {lane} is already connected"); break; }

                var child2 = _config.Agent;
                var args2 = new List<string>();
                if (IsClaude(child2))
                {
                    args2 = ClaudeArgs(_config.Model, _config.Effort, LaneSystemPrompt(row.Title), acceptEdits: true);
                    if (row.Session is { Length: > 0 } sess && !sess.StartsWith("fake-"))
                    { args2.Add("--resume"); args2.Add(sess); }
                }
                // The pipe name is deterministic per lane, and the old shim is gone —
                // the name is free to reclaim, which is the whole point of never keying
                // anything to pids (§13).
                var (rid, rmsg) = await RespawnLaneAsync(row.Id, row.Title, args2, child2);
                if (rid > 0)
                {
                    _store.LaneState(lane, "alive");
                    _store.LanePresence(lane, "idle");
                    _store.PaneEvent(lane, "announcement",
                        row.Session is { Length: > 0 } ? "agent respawned — session resumed, context intact" : "agent respawned — fresh session", null, null, acked: true);
                    _store.Event("lane_respawned", lane, $"session={row.Session ?? "-"}");
                }
                w.WriteLine(rmsg);
                break;
            }
            case "lane-stop":
            {
                // The undo for an auto-started lane. The shim owns the agent, so stopping
                // is a message to the shim, not a kill: it takes the child down with it
                // and the pane's rows stay exactly where they are (§12 — nothing is
                // deleted, the lane is simply no longer alive).
                var lane = e.GetProperty("lane").GetInt64();
                if (_lanes.TryGetValue(lane, out var srt))
                {
                    srt.Shutdown();
                    _lanes.Remove(lane);
                }
                _store.LaneState(lane, "dead");
                if (_store.KvGet("focused_lane") == lane.ToString()) _store.KvSet("focused_lane", "");
                _store.Event("lane_stopped", lane, "operator");
                w.WriteLine($"stopped lane {lane}");
                break;
            }
            case "tail":
                foreach (var row in _store.Tail(e.GetProperty("lane").GetInt64(), e.GetProperty("n").GetInt32()))
                    w.WriteLine(row);
                break;
            case "status":
                w.WriteLine($"daemon pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath}");
                w.WriteLine($"workspace {_wsName} ({_instanceId})  store={Paths.Store(_instanceId)}");
                w.WriteLine($"members: {string.Join(", ", Members().Select(m => m.Path))}");
                w.WriteLine($"lanes: model={_config.Model} effort={(_config.Effort is { Length: > 0 } ? _config.Effort : "cli default")}  " +
                            $"router: model={_config.RouterModel} effort={(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}  " +
                            $"agent={_config.Agent}");
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
            case "lane-collapse":
            {
                // A view choice, but a durable one, so it goes through the daemon like every
                // other write (m3: the UI owns nothing). Collapsing NEVER touches the lane's
                // life — no agent stops, no slot frees, nothing is demoted. It only says how
                // much room you want it to take, which is why LANE-LIFECYCLE §2's rejection of
                // slot-pressure eviction is untouched by it: this is the operator's hand, not
                // the system reclaiming space.
                var lane = e.GetProperty("lane").GetInt64();
                var on = !e.TryGetProperty("collapsed", out var cv) || cv.ValueKind != JsonValueKind.False;
                if (_store.LanesAll().All(l => l.Id != lane)) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneCollapsed(lane, on);
                _store.Event(on ? "lane_collapsed" : "lane_expanded", lane, "operator");
                w.WriteLine($"{(on ? "collapsed" : "expanded")} lane {lane}");
                break;
            }
            case "input":
            {
                var text = e.GetProperty("text").GetString()!;
                w.WriteLine(await RouteInput(text));
                break;
            }
            case "router-start":
            {
                var child = e.TryGetProperty("child", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()! : _config.Agent;
                // The router is a mechanical classifier, not a thinker: cheap model, low
                // effort, deliberately not the project's lane policy (§9's ladder — spend
                // where judgement compounds, and this is not where it compounds).
                var model = Pick(e, "model", _config.RouterModel);
                var effort = Pick(e, "effort", _config.RouterEffort);
                // The operator's routing policy, verbatim intent (2026-08-18): a GENERIC
                // remark ("don't do that", "stop", "try again") belongs to the focused
                // lane, full stop — no cleverness. A remark CLEARLY AIMED by content
                // ("make the skybox red") goes to the lane it names, cheap thought.
                // Only text that is neither obviously generic nor obviously aimed earns
                // expensive thought (the brain's high tier picks it up).
                // Four verdicts (WORKSPACES-CONCIERGE.md §5). The old vocabulary was
                // generic|specific|unclear, where `specific` meant "an existing lane's title" —
                // so no rung of the ladder could ever answer "this deserves a fresh lane", and
                // while any lane was alive every input was a continuation of something.
                //
                // The tie-break and its REASON are both in the prompt deliberately: the cheap
                // model has to know WHY the tie breaks toward new-task, or it will break it the
                // other way whenever the input is vague.
                var sys = "You are Dodona's input router. You are given some lanes (each an agent working on " +
                          "something), which lane the operator is looking at, and one line of operator input. " +
                          "You decide where it goes. Reply with ONLY one line of JSON, no prose, no markdown: " +
                          "{\"kind\":\"generic|addendum|new-task|unclear\",\"target\":\"<LANE TITLE, for addendum only>\"," +
                          "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars, say WHY\"}\n" +
                          "kind=generic — the remark could apply to any ongoing work: stop, no, try again, yes, " +
                          "an acknowledgement, a correction naming no subject. It belongs to the FOCUSED lane. " +
                          "target must be omitted.\n" +
                          "kind=addendum — it continues an existing lane's thread. Two ways that happens, and both " +
                          "are common: it is aimed at what that lane is doing NOW (reason: direct), or it is a small " +
                          "correction or refinement of what that lane JUST FINISHED (reason: tweak). target names " +
                          "that lane.\n" +
                          "kind=new-task — a distinct piece of work. It gets its own fresh lane. Do not name a target.\n" +
                          "kind=unclear — you genuinely cannot tell. Say so; someone with more budget looks, and " +
                          "then the operator is asked. Nothing is delivered meanwhile, so unclear is SAFE.\n" +
                          "WHEN TORN BETWEEN addendum AND new-task, CHOOSE new-task. Here is why, and it should " +
                          "change how you weigh it: a wrong new lane costs one command to undo and pollutes nothing. " +
                          "A wrong addendum cannot be undone at all — the agent has already been told, may already " +
                          "be acting, and its context is spoiled. Prefer the mistake that is free.\n" +
                          "But do not overcorrect: an operator interrupting a working agent is completely normal, " +
                          "and the length of the input tells you nothing about which kind it is. What tells you is " +
                          "the SUBJECT — does the input concern what that lane is about, or something else?\n" +
                          "Be willing to say unclear or confidence low — an honest unsure is cheap here, and a " +
                          "confident wrong guess is the one error that cannot be taken back.";
                var args = IsClaude(child) ? ClaudeArgs(model, effort, sys, acceptEdits: false, utility: true) : new List<string>();
                w.WriteLine((await SpawnLaneAsync("ROUTER", "router", NeutralCwd(), child, args)).Msg);
                break;
            }
            case "brain-start":
            {
                // For suites (NO_AUTOSTART skips the warm-at-start) and for restarting a
                // brain by hand after changing its config.
                var lo = await EnsureBrainAsync(hi: false);
                var wantHi = e.TryGetProperty("hi", out var bh) && bh.ValueKind == JsonValueKind.True;
                var hi2 = wantHi ? await EnsureBrainAsync(hi: true) : -2;
                w.WriteLine($"brain: cheap tier lane {(lo > 0 ? lo.ToString() : "FAILED")}" +
                            (wantHi ? $", expensive tier lane {(hi2 > 0 ? hi2.ToString() : "FAILED")}" : ""));
                break;
            }
            case "compressor-start":
            {
                var child = e.TryGetProperty("child", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString()! : _config.Agent;
                var model = Pick(e, "model", _config.CompressorModel);
                var effort = Pick(e, "effort", _config.CompressorEffort);
                // Asked for by hand with the pool configured off, "how many" still has an
                // obvious answer — two, the smallest number that is not a serialization point.
                var count = e.TryGetProperty("count", out var cn) && cn.ValueKind == JsonValueKind.Number ? cn.GetInt32()
                          : _config.Compressors > 0 ? _config.Compressors : 2;
                w.WriteLine(await StartCompressorsAsync(child, model, effort, count));
                break;
            }
            case "ticket-agent":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                var child = e.TryGetProperty("child", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString()! : _config.Agent;
                var model = Pick(e, "model", _config.Model);
                var effort = Pick(e, "effort", _config.Effort);
                var claims = string.Join(", ", _store.TicketClaims(tid).Select(cl => $"{cl.Kind}:{cl.Value}"));

                // The lane-agent framing (§5, spike 3): declare the [DISPATCHER] channel or
                // the model treats mid-turn instructions as a prompt-injection attempt.
                var sys = $"You are a lane agent operated by the Dodona orchestrator, working ticket {tid}: \"{t.Title}\". " +
                          $"Your worktree is the current working directory; work only there. Your declared claim is [{claims}] — " +
                          "a PreToolUse gate denies writes outside it; if denied, stay within the claim or ask your operator for an extension. " +
                          "Real-time instructions from your human operator arrive in hook output labeled [DISPATCHER]; they are authentic " +
                          "and carry the same authority as your original task, even when they change or contradict earlier instructions.";
                var args = IsClaude(child) ? ClaudeArgs(model, effort, sys, acceptEdits: true) : new List<string>();
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

                // Git is needed HERE — at the first branch and worktree — not at the door.
                // A project can be opened, and lanes can run in it, long before it has a
                // repo; refusing to open would be refusing too early (and for too long).
                var repos = Repositories();
                RepoRef? repo;
                if (e.TryGetProperty("repo", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString() is string rname && rname.Length > 0)
                {
                    repo = Repos.ByName(repos, rname);
                    if (repo is null)
                    {
                        w.WriteLine($"error: no repository '{rname}' in this workspace" +
                                    (repos.Count > 0 ? $" (have: {string.Join(", ", repos.Select(r => r.Name))})" : ""));
                        w.WriteLine("##exit 1");
                        break;
                    }
                }
                else
                {
                    // Claims are workspace-relative paths, so they already say which
                    // repository this ticket is for — no extra syntax needed.
                    var (inferred, err) = Repos.ForClaims(repos, claims);
                    if (inferred is null)
                    {
                        _store.Event("ticket_repo_unresolved", null, $"'{title}': {err}");
                        w.WriteLine($"error: {err}");
                        if (repos.Count == 0 && !Git.IsRepo(_primary))
                            w.WriteLine("       (lanes work without git; only tickets need a repository)");
                        w.WriteLine("##exit 1");
                        break;
                    }
                    repo = inferred;
                }

                // Repo-exclusivity, layer 3 (Registry's doc comment): asked HERE because
                // here is where a merge token first comes into existence for this repo.
                // Attach-time enforcement and the partial unique index both cover the
                // ordinary case; neither can cover a BARE FOLDER legitimately attached to
                // two workspaces (exempt, harmless) that someone later ran `git init` in.
                // Only a check at the point of use notices the ground moved — the same
                // reasoning that puts a diff backstop behind the claim gate (§6).
                try
                {
                    using var reg = new Registry();
                    if (reg.RepoConflict(repo.Path, _instanceId) is Workspace other)
                    {
                        _store.Event("ticket_repo_not_exclusive", null, $"'{title}': {repo.Path} also in {other.Id}");
                        w.WriteLine($"error: {repo.Path} also belongs to workspace \"{other.Name}\" ({other.Id})");
                        w.WriteLine("       a repo belongs to at most ONE workspace at a time — two workspaces over one");
                        w.WriteLine("       repo is two merge tokens over one main, the race this system exists to prevent");
                        w.WriteLine($"       move it:  dodona workspace-move --member \"{repo.Path}\" --workspace \"{_wsName}\"");
                        w.WriteLine("##exit 1");
                        break;
                    }
                }
                catch (Exception ex) { _store.Event("registry_unreadable", null, ex.Message); }

                var repoCfg = Config.For(_primary, repo.Path);
                if (!Git.HasCommit(repo.Path))
                {
                    w.WriteLine($"error: {repo.Name} is a git repository with no commits, so there is no '{repoCfg.Main}' to branch from");
                    w.WriteLine("       run `dodona repo-init` to make the first commit");
                    w.WriteLine("##exit 1");
                    break;
                }

                var (id, conflicts) = _store.TicketCreate(null, title, mode, repo.Name, claims);
                if (id < 0)
                {
                    _store.Event("claim_conflict", null, $"'{title}': {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"conflict: {cf}");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Branch names are workspace-unique because ticket ids are. The worktree
                // lives beside the MEMBER holding this repository — worktrees are the one
                // piece of state that deliberately did NOT move into workspace territory
                // (WORKSPACES-CONCIERGE.md §1: they are volume- and path-sensitive, and
                // moving them buys nothing). For a one-member workspace this is the exact
                // path it has always been.
                var branch = $"ticket/{id}";
                var wt = Path.Combine(Paths.Worktrees(repo.MemberPath), $"t{id}");
                var (code, output) = Git.Run(repo.Path, "worktree", "add", "-b", branch, wt, repoCfg.Main);
                if (code != 0)
                {
                    _store.TicketState(id, "abandoned");
                    _store.Event("ticket_git_failed", null, $"ticket {id} repo {repo.Name}: {output}");
                    w.WriteLine($"error: worktree add failed in {repo.Name}: {output}");
                    break;
                }
                _store.TicketSetGit(id, branch, wt);
                DeployGate(wt, id, repo);
                _store.Event("ticket_created", null, $"ticket {id} '{title}' repo {repo.Name} branch {branch} claims [{string.Join(", ", specs)}]");
                // A single-repo project never sees the word "repo": there is only one, and
                // naming it would be noise in the ordinary case.
                w.WriteLine($"ticket {id}{RepoTag(repo.Name)} branch {branch} worktree {wt}");
                break;
            }
            case "claim-check":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }

                // Claims are workspace-relative, but the agent writes inside a worktree of
                // one repository — so a path resolved against the worktree must be put
                // back into workspace terms before it can be matched. For a single-repo
                // project the prefix is empty and this is exactly the old behaviour.
                var ticketRepo = Repos.ByName(Repositories(), t.Repo);
                var prefix = ticketRepo?.ClaimPrefix ?? "";
                var full = Path.GetFullPath(path, t.Worktree).Replace('\\', '/');
                string? rel = null;
                foreach (var (baseDir, addPrefix) in new[] { (t.Worktree, true), (_primary, false) })
                {
                    var b = Path.GetFullPath(baseDir).Replace('\\', '/').TrimEnd('/') + "/";
                    if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = (addPrefix ? prefix : "") + full[b.Length..];
                        break;
                    }
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
            {
                var multi = _store.Tickets().Any(t => t.Repo != ".");
                foreach (var t in _store.Tickets())
                    w.WriteLine($"ticket {t.Id}  {t.Title,-12}  {(multi ? $"repo={t.Repo,-10}  " : "")}state={t.State}  mode={t.MergeMode}  approved={t.Approved}  branch={t.Branch}");
                break;
            }
            case "repos":
            {
                var found = Repositories();
                if (found.Count == 0)
                {
                    w.WriteLine($"no git repository in {_primary}");
                    w.WriteLine("run `dodona repo-init` to make this folder one (lanes work meanwhile; only tickets need git)");
                    break;
                }
                foreach (var r in found)
                {
                    var cfg = Config.For(_primary, r.Path);
                    var tok = _store.TokenRead(r.Name);
                    var open = _store.Tickets().Count(t => t.Repo == r.Name && t.State == "open");
                    w.WriteLine($"{r.Name,-14} main={cfg.Main,-8} open-tickets={open}  token={(tok.Holder?.ToString() ?? "free"),-6} verify={cfg.Verify.Length} step(s)  {r.Path}");
                }
                break;
            }

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
                var reqRepo = Repos.ByName(Repositories(), t.Repo);
                var reqPath = reqRepo?.Path ?? _primary;
                var reqPrefix = reqRepo?.ClaimPrefix ?? "";
                var reqCfg = Config.For(_primary, reqPath);
                var (dc, diff) = Git.Run(reqPath, "diff", "--name-only", $"{reqCfg.Main}...{t.Branch}");
                if (dc == 0 && diff.Length > 0)
                {
                    var ticketClaims = _store.TicketClaims(tid);
                    var outside = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => Claims.Normalize(reqPrefix + f))   // git speaks repo-relative; claims are workspace-relative
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

                var (status, gen, pos) = _store.TokenRequest(tid, t.Repo, lease, () => Git.Sha(reqPath, reqCfg.Main));
                w.WriteLine(status == "granted"
                    ? $"granted ticket {tid} generation {gen}{RepoTag(t.Repo)}"
                    : $"queued ticket {tid} position {pos}{RepoTag(t.Repo)}");
                break;
            }
            case "token-renew":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                if (_store.TokenRenew(tid, rt.Repo, lease)) w.WriteLine($"renewed ticket {tid}");
                else { w.WriteLine($"refused: ticket {tid} is not the live holder"); w.WriteLine("##exit 1"); }
                break;
            }
            case "token-release":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                _store.TokenRelease(tid, rt.Repo);
                w.WriteLine("released");
                break;
            }
            case "token-status":
            {
                // One token per repository: they land in parallel, so they report in
                // parallel too.
                var tokens = _store.TokensAll();
                if (tokens.Count == 0) tokens = new List<Store.TokenRow> { _store.TokenRead(".") };
                var manyRepos = tokens.Any(x => x.Repo != ".");
                foreach (var tok in tokens)
                    w.WriteLine($"{(manyRepos ? $"repo={tok.Repo,-12} " : "")}holder={(tok.Holder?.ToString() ?? "none")} generation={tok.Generation} expires={tok.ExpiresTs ?? "-"} main={(tok.MainSha is { Length: >= 8 } s ? s[..8] : "-")}");
                break;
            }
            case "land":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                w.WriteLine(LandOp(tid, out var landOk));
                if (!landOk) w.WriteLine("##exit 1");
                break;
            }
            case "policy":
            {
                // Inspectable without spawning anything: ask what a sentence would get.
                var probe = e.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString()! : "";
                if (probe.Length > 0)
                {
                    var (clean, om, oe) = Policy.StripOverrides(probe);
                    var c = Policy.Resolve(clean, _config.Rules, _config.Model, _config.Effort, om, oe);
                    w.WriteLine($"{c.Model} {(c.Effort is { Length: > 0 } ? c.Effort : "-")}  {c.Describe}");
                    if (clean != probe.Trim()) w.WriteLine($"prompt: {clean}");
                    break;
                }
                w.WriteLine($"default    {_config.Model,-8} {_config.Effort}");
                w.WriteLine($"router     {_config.RouterModel,-8} {(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}");
                foreach (var r in _config.Rules)
                    w.WriteLine($"rule       {r.Model,-8} {r.Effort,-7} {(r.Why is { Length: > 0 } ? r.Why : "-"),-12} {r.When}");
                w.WriteLine("override   @opus @max <text>   (model and effort are fixed when a lane starts)");
                break;
            }
            case "repo-status":
            {
                // What the picker (and anyone else) needs to know before offering a fix.
                var isRepo = Git.IsRepo(_primary);
                var nested = isRepo ? new List<string>() : Git.FindRepos(_primary);
                var entries = Directory.Exists(_primary)
                    ? Directory.EnumerateFileSystemEntries(_primary).Where(p => Path.GetFileName(p) is not ".dodona" and not ".git").Take(1).Count()
                    : 0;
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    root = _primary,
                    isRepo,
                    hasCommit = isRepo && Git.HasCommit(_primary),
                    empty = entries == 0,
                    nested = nested.Select(r => Path.GetRelativePath(_primary, r)).ToList(),
                    main = _config.Main,
                }));
                break;
            }
            case "repo-init":
            {
                if (Git.IsRepo(_primary) && Git.HasCommit(_primary)) { w.WriteLine($"error: {_primary} is already a git repository with commits"); break; }
                var adopt = e.TryGetProperty("adopt", out var ad) && ad.ValueKind == JsonValueKind.True;

                if (!Git.IsRepo(_primary))
                {
                    var (ic, io) = Git.Run(_primary, "init", "-b", _config.Main);
                    if (ic != 0) { w.WriteLine($"error: git init failed: {io}"); w.WriteLine("##exit 1"); break; }
                    w.WriteLine($"initialized empty repository on '{_config.Main}'");
                }

                // Dodona's own state is never repo content: worktrees, the store and the
                // deployed gate files all live under .dodona/ and would otherwise be
                // committed by an agent's `git add -A` (the bug M1's test caught).
                var ignore = Path.Combine(_primary, ".gitignore");
                var ignoreText = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
                if (!ignoreText.Split('\n').Any(l => l.Trim() == ".dodona/"))
                {
                    File.AppendAllText(ignore, (ignoreText.Length > 0 && !ignoreText.EndsWith("\n") ? "\n" : "") + ".dodona/\n");
                    w.WriteLine("added .dodona/ to .gitignore");
                }

                if (!Git.HasCommit(_primary))
                {
                    // An empty repo has no branch, so no worktree can be cut from it. What
                    // goes into the first commit is the user's call, not ours: adopt takes
                    // the files that are already here, otherwise the commit is empty and
                    // they stay untracked.
                    if (adopt) Git.Run(_primary, "add", "-A");
                    var args = new List<string> { "commit", "-m", adopt ? "Initial commit" : "Initial commit (empty)" };
                    if (!adopt) args.Insert(1, "--allow-empty");
                    var (cc, co) = Git.Run(_primary, args.ToArray());
                    if (cc != 0) { w.WriteLine($"error: initial commit failed: {co}"); w.WriteLine("##exit 1"); break; }
                    w.WriteLine(adopt ? "committed the existing files as the initial commit" : "made an empty initial commit; existing files left untracked");
                }
                _store.Event("repo_init", null, $"{_primary} main={_config.Main} adopt={adopt}");
                Announce($"[dodona] git repository ready on '{_config.Main}' — tickets can branch now");
                w.WriteLine($"ready: {_primary} is a git repository on '{_config.Main}'");
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

        // Any repository mid-merge blocks the swap — the tokens are independent, but the
        // daemon that would vanish underneath them is not.
        foreach (var tok in _store.TokensAll())
        {
            if (tok.Holder is not long h) continue;
            if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() <= DateTime.UtcNow) continue;
            var t = _store.Ticket(h);
            blockers.Add($"{t?.Title ?? $"ticket {h}"} is mid-merge{(tok.Repo == "." ? "" : $" in {tok.Repo}")}");
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
            var psi = new ProcessStartInfo(nb.Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _primary };
            psi.ArgumentList.Add("daemon");
            psi.ArgumentList.Add("--workspace");
            psi.ArgumentList.Add(_instanceId);
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

    /// <summary>
    /// Live updates are not sometimes. The daemon watches its own source tree, and when
    /// the sources are newer than the image it is running, it publishes and swaps ITSELF —
    /// no person and no agent has to remember to. This exists because "work done but not
    /// live" blocked the operator three separate times in one day (edited-not-built,
    /// built-not-published, published-not-committed), and instructions in CLAUDE.md are
    /// advisory while a watcher is not — the same reasoning as the claim gate (§6).
    ///
    /// Safety is inherited, not added: the publish it runs goes through the ordinary M4
    /// path, so a build that fails changes nothing and is announced; a successor that
    /// never answers leaves this daemon running; a mid-merge lane blocks the swap with
    /// the usual three answers. The debounce waits for the tree to go quiet so a
    /// half-saved edit is never built.
    /// </summary>
    void StartDriftWatcher()
    {
        if (!_config.AutoPublish) return;
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1") return;   // suites own their lifetime
        var project = string.IsNullOrEmpty(_config.AutoPublishProject) ? _primary : Path.GetFullPath(_config.AutoPublishProject);
        if (!File.Exists(Path.Combine(project, "src", "Dodona", "Dodona.csproj")))
        {
            _store.Event("autopublish_misconfigured", null, $"{project} is not a Dodona source tree");
            Announce($"[dodona] autoPublish is on, but {project} has no src/Dodona — nothing is being watched");
            return;
        }
        _store.Event("autopublish_watching", null, $"{project} (debounce {_config.AutoPublishDebounceSec}s)");

        _ = Task.Run(async () =>
        {
            DateTime lastMax = DateTime.MinValue, stableSince = DateTime.MinValue, lastTried = DateTime.MinValue;
            DateTime dirtySince = DateTime.MinValue;
            bool dirtyAnnounced = false;
            int tick = 0;

            // Consecutive-failure backoff. Each edit-batch produces a new source timestamp,
            // so `lastTried` alone only prevents retrying the SAME edit — it never notices
            // that the last N attempts all failed for the same reason.
            //
            // Measured on the operator's instance: 16 attempts, 16 failures, in one afternoon.
            // Every one ran a full three-project publish (minutes of CPU) and put another
            // identical "auto-publish FAILED" row in the decision feed, until the failure that
            // mattered was buried under fifteen copies of itself. The cause was legitimate and
            // unfixable-by-retrying — a build whose successor could not hand off — which is
            // exactly the shape where retrying forever is the wrong answer.
            //
            // So: give up after three in a row, say so ONCE, and stay quiet until something
            // changes. A publish by hand clears it, because a successful swap ends this
            // process entirely.
            const int giveUpAfter = 3;
            int consecutiveFailures = 0;
            bool surrendered = false;

            while (true)
            {
                await Task.Delay(15000);
                try
                {
                    // ---- source drift: newer than the running image? ----
                    var maxSrc = NewestSource(project);
                    var image = File.GetLastWriteTimeUtc(Ver.ExePath);
                    if (maxSrc > image && maxSrc != lastTried && !surrendered)
                    {
                        if (maxSrc != lastMax) { lastMax = maxSrc; stableSince = DateTime.UtcNow; }
                        else if ((DateTime.UtcNow - stableSince).TotalSeconds >= _config.AutoPublishDebounceSec)
                        {
                            lastTried = maxSrc;
                            _store.Event("autopublish_started", null, $"sources {maxSrc:o} > image {image:o}");
                            Announce("[dodona] sources changed — building and swapping to stay live");
                            var (code, output) = RunPublish(project);
                            if (code != 0)
                            {
                                var reason = output.Split('\n').LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                                             ?? output.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "unknown";
                                consecutiveFailures++;
                                _store.Event("autopublish_failed", null,
                                    $"attempt {consecutiveFailures}/{giveUpAfter}: {Truncate(output, 800)}");
                                if (consecutiveFailures >= giveUpAfter)
                                {
                                    surrendered = true;
                                    _store.Event("autopublish_surrendered", null,
                                        $"{consecutiveFailures} consecutive failures; watching stopped until a manual publish");
                                    Announce($"[dodona] auto-publish has failed {consecutiveFailures} times running and has STOPPED trying — " +
                                             $"the live app stays behind the sources until you publish by hand. Last reason: {Truncate(reason.Trim(), 140)}");
                                }
                                else
                                    Announce($"[dodona] auto-publish FAILED — the live app is now BEHIND the sources: {Truncate(reason.Trim(), 160)}");
                            }
                            else consecutiveFailures = 0;
                            // success: the swap arrives through our own control pipe and this
                            // daemon exits mid-handoff — nothing more to do here. A parked
                            // swap (mid-merge blocker) already announced its three answers.
                        }
                    }
                    else if (maxSrc <= image) { lastMax = DateTime.MinValue; }

                    // ---- git drift: published-but-uncommitted was nearly lost work once ----
                    if (++tick % 20 == 0)   // every ~5 minutes
                    {
                        var (gc, gout) = Git.Run(project, "status", "--porcelain");
                        var dirty = gc == 0 && gout.Trim().Length > 0;
                        if (!dirty) { dirtySince = DateTime.MinValue; dirtyAnnounced = false; }
                        else if (dirtySince == DateTime.MinValue) dirtySince = DateTime.UtcNow;
                        else if (!dirtyAnnounced && (DateTime.UtcNow - dirtySince).TotalMinutes >= 30)
                        {
                            dirtyAnnounced = true;
                            _store.Event("autopublish_dirty_tree", null, Truncate(gout, 400));
                            Announce("[dodona] the working tree has been dirty for 30m — the live app runs work git does not have; commit it");
                        }
                    }
                }
                catch (Exception ex) { _store.Event("autopublish_error", null, ex.Message); }
            }
        });
    }

    static DateTime NewestSource(string project)
    {
        var newest = DateTime.MinValue;
        void Scan(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (f.Contains(@"\bin\") || f.Contains(@"\obj\")) continue;
                if (!(f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj"))) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
        }
        Scan(Path.Combine(project, "src"));
        var dj = Path.Combine(project, "dodona.json");
        if (File.Exists(dj) && File.GetLastWriteTimeUtc(dj) > newest) newest = File.GetLastWriteTimeUtc(dj);
        return newest;
    }

    (int Code, string Output) RunPublish(string project)
    {
        var psi = new ProcessStartInfo(Ver.ExePath)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _primary };
        // Scoped to THIS workspace, never --all: an auto-publish is one workspace noticing
        // its own sources moved, and broadcasting a swap to every daemon on the machine
        // because one repo was edited is exactly what §7 set out to stop.
        foreach (var a in new[] { "publish", "--project", project, "--workspace", _instanceId }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errT = Task.Run(() => p.StandardError.ReadToEnd());
        var so = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(600000)) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, "publish timed out after 10 minutes"); }
        return (p.ExitCode, so + "\n" + errT.Result);
    }

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

    /// <summary>The argv every claude lane is started with — one place, so model and
    /// effort are policy rather than four scattered literals. `--effort` is omitted when
    /// blank so a project can opt out of setting it at all.</summary>
    /// <summary>Where management-role agents live — see <see cref="Paths.NeutralCwd"/>.
    /// The definition moved there because the concierge (§2) runs its own management models
    /// and must get the identical treatment: utility roles get no project context at all,
    /// and their whole job description is their system prompt.</summary>
    static string NeutralCwd() => Paths.NeutralCwd();

    List<string> ClaudeArgs(string model, string effort, string systemPrompt, bool acceptEdits, bool utility = false)
    {
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                                      "--verbose", "--model", model };
        if (!string.IsNullOrWhiteSpace(effort)) { args.Add("--effort"); args.Add(effort); }
        // Belt to the neutral-cwd braces: even if a future claude finds project context
        // some other way, utility roles ask for user-level settings only.
        if (utility) { args.Add("--setting-sources"); args.Add("user"); }
        // A lane has no way to ASK. The operator's own session carries a permission-prompt
        // tool wired to a dialog, so an unapproved command becomes a question; a headless
        // `-p` lane has no such channel, so the same command is denied outright and the
        // agent is simply stuck — it edits fine and then cannot build what it edited.
        // Hence the default matches what the operator's IDE grants in auto mode.
        //
        // This does NOT loosen Dodona's own guarantees, and that is not an assumption:
        // measured, a PreToolUse hook still fires under bypassPermissions. The claim gate
        // IS a PreToolUse hook, so a ticket lane is still bounded to its claim, and the
        // merge-time diff backstop still refuses anything that slips. The safety model
        // never rested on Claude's permission prompt — it rests on the gate and the fence.
        if (acceptEdits) { args.Add("--permission-mode"); args.Add(_config.PermissionMode); }
        if (acceptEdits && _config.Allowed.Length > 0)
        {
            // Work lanes get the project's allowlist; the router never does — it has no
            // business running anything.
            args.Add("--allowedTools");
            args.Add(string.Join(",", _config.Allowed));
        }
        args.Add("--append-system-prompt");
        args.Add(systemPrompt);
        return args;
    }

    /// <summary>What a request asked for, else what the project settled on.</summary>
    static string Pick(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : fallback;

    /// <summary>Spawn a plain agent lane in the workspace — no ticket, no claim, no gate.
    /// The binary is `agent` from dodona.json (default `claude`), which is also how the
    /// acceptance suite exercises the paths where the daemon starts an agent itself.</summary>
    Task<(long Id, string Msg)> SpawnAgentLaneAsync(string title, string? model = null, string? effort = null)
    {
        var child = _config.Agent;
        var args = IsClaude(child)
            ? ClaudeArgs(model ?? _config.Model, effort ?? _config.Effort, LaneSystemPrompt(title), acceptEdits: true)
            : new List<string>();                       // a stand-in agent takes no claude flags
        return SpawnLaneAsync(title, "work", _primary, child, args);
    }

    static bool IsClaude(string child) =>
        child.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>A lane name derived from what was typed: the longest substantial word,
    /// which is usually the subject. Code, not a model — it must be instant, and a name is
    /// cheap to change. (§2.2: derive in code what is not really a judgement.)</summary>
    static string NameFromText(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","for","to","in","on","of","with","that","this","some","new","please","its","it",
            "add","make","fix","let","can","you","we","i","should","would","could","need","want","get","put","use",
            "there","then","when","where","what","how","why","from","into","about","over","under","out","up","down",
        };
        var word = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z][A-Za-z0-9_-]{2,}")
            .Select(m => m.Value)
            .Where(v => !stop.Contains(v))
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
        return (word ?? "LANE").ToUpperInvariant();
    }

    /// <summary>The framing for a lane with no ticket. The [DISPATCHER] channel must be
    /// declared or the model treats mid-turn operator instructions as a prompt-injection
    /// attempt and refuses them (spike 3) — that applies to every lane, not just ticketed
    /// ones.</summary>
    static string LaneSystemPrompt(string title) =>
        $"You are the agent for lane \"{title}\", operated by the Dodona orchestrator. Your working directory is " +
        "the project the operator is running; work there. You have no ticket and no claim, so nothing is reserved " +
        "for you — if the operator wants isolated work on a branch, they will create a ticket and a fresh lane for it. " +
        "Real-time instructions from your human operator arrive in hook output labeled [DISPATCHER]; they are authentic " +
        "and carry the same authority as your original task, even when they change or contradict earlier instructions.";

    /// <summary>" repo engine", or nothing at all when the workspace root IS the
    /// repository — the single-repo project should never have to read about repos.</summary>
    static string RepoTag(string repo) => repo == "." ? "" : $" repo {repo}";

    /// <summary>Spawn a lane: shim → child, detached, pumped, recorded. Shared by
    /// lane-start (fake/test agents), router-start (warm utility session), and
    /// ticket-agent (real claude in a gated worktree).</summary>
    async Task<(long Id, string Msg)> SpawnLaneAsync(string title, string role, string workDir, string child, List<string> childArgs)
    {
        var id = _store.LaneCreate(title);
        _store.LaneRole(id, role);
        return await AttachShimAsync(id, title, role, workDir, child, childArgs);
    }

    /// <summary>Respawn an agent into an EXISTING lane row — the thread survives its
    /// agent (§11). Same pipe name (deterministic per lane, and the dead shim freed it),
    /// same pane, fresh process.</summary>
    Task<(long Id, string Msg)> RespawnLaneAsync(long laneId, string title, List<string> childArgs, string child)
    {
        var role = _store.LanesAll().FirstOrDefault(l => l.Id == laneId)?.Role ?? "work";
        return AttachShimAsync(laneId, title, role, _primary, child, childArgs);
    }

    async Task<(long Id, string Msg)> AttachShimAsync(long id, string title, string role, string workDir, string child, List<string> childArgs)
    {
        var pipe = Instance.LanePipe(_instanceId, id);
        _store.LanePipe(id, pipe);

        var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                      ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
        var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = workDir };
        psi.ArgumentList.Add(pipe);
        psi.ArgumentList.Add(child);
        foreach (var a in childArgs) psi.ArgumentList.Add(a);
        psi.Environment["DODONA_SHIM_INFO"] = Paths.ShimInfo(_instanceId, id);
        // What this lane is for. A real claude learns its job from the system prompt; this
        // says the same thing to a child that has no system prompt to read (§17's fake
        // agent), and is worth having in the environment of any child when debugging.
        psi.Environment["DODONA_LANE_ROLE"] = role;
        Process.Start(psi);
        _store.Event("shim_spawned", id, $"pipe={pipe} child={child} cwd={workDir}");

        var rt = new LaneRuntime(id, pipe, _store);
        HookCompression(rt, role);
        if (await rt.ConnectAndPumpAsync(attempts: 20))
        {
            _lanes[id] = rt;
            _store.Event("lane_started", id, $"{title} role={role}");
            return (id, $"lane {id} title {title} role {role} pipe {pipe}");
        }
        _store.LaneState(id, "unreachable");
        return (-1, $"error: lane {id} shim pipe never answered");
    }

    // ------------------------------------------------------------- selective compression (§5)

    /// <summary>Only WORK lanes get their turn-finals compressed. A compressor whose own
    /// result was compressed would ask itself to summarise its summary, forever.</summary>
    void HookCompression(LaneRuntime rt, string role)
    {
        if (role == "work") rt.OnResult = CompressResult;
    }

    /// <summary>
    /// The compressor pool (§5). Warm sessions, so a turn lands on ~1s of latency instead
    /// of a cold start, and a POOL of them rather than one: a single compressor
    /// accumulating six lanes' turn-finals is exactly the unbounded serialization point
    /// §3 forbids the dispatcher to be. Cheap model, low effort — shortening a paragraph
    /// is not where judgement compounds (§9's ladder), and it runs 5–10× more often than
    /// anything else in the system.
    /// </summary>
    async Task<string> StartCompressorsAsync(string child, string model, string effort, int count)
    {
        count = Math.Clamp(count, 1, 4);
        // A schema, not an instruction to be brief: "be concise" is advice a model may
        // decline, a character cap on a named field is not (§4/§5).
        var sys = "You are Dodona's pane compressor. You will be given one agent's turn-final message. " +
                  "Reply with ONLY one line of JSON, no prose, no markdown, no code fence: " +
                  "{\"headline\":\"<=90 characters\",\"needs_you\":true|false,\"options\":[\"<a few words>\"]} " +
                  "headline is what the operator must know, written for someone glancing at one pane of six: " +
                  "past tense for work that happened, imperative for what is wanted. No preamble, no markdown, " +
                  "never mention 'the user', never restate the question. " +
                  "needs_you is true only when the work cannot continue without a human decision. " +
                  "options lists those choices, at most three, and is [] whenever needs_you is false.";
        var args = IsClaude(child) ? ClaudeArgs(model, effort, sys, acceptEdits: false, utility: true) : new List<string>();

        var alive = _store.LanesAll().Count(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id));
        if (alive >= count) return $"compressor pool already warm ({alive})";
        var started = new List<long>();
        for (int i = alive; i < count; i++)
        {
            var (id, msg) = await SpawnLaneAsync($"COMPRESS{i + 1}", "compressor", NeutralCwd(), child, args);
            if (id < 0) return started.Count > 0
                ? $"compressor pool partially up: {started.Count} warm, then: {msg}"
                : $"error: {msg}";
            _compressorLocks[id] = new SemaphoreSlim(1, 1);
            started.Add(id);
        }
        return $"compressor pool warm: {alive + started.Count} session(s) on {model}" +
               (effort is { Length: > 0 } ? $"/{effort}" : "") + $" — lanes {string.Join(", ", started)}";
    }

    /// <summary>
    /// A turn ended (§5). The row is already in the store and already on screen; this only
    /// ever fills in a shorter rendering of it, so every failure path below simply leaves
    /// the operator reading the agent's own words — which is the current behaviour, and
    /// therefore a safe floor. Nothing here is ever awaited by the wire pump.
    /// </summary>
    void CompressResult(long laneId, long paneEventId, string body)
    {
        // Already the length a compressor would produce: spending a model call here would
        // be exactly the no-judgment volume §2.2 says not to buy.
        if (body.Length <= 120 && !body.Contains('\n')) return;

        var pool = _store.LanesAll()
            .Where(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id))
            .ToList();
        if (pool.Count == 0) return;                  // no pool warm: the full text stands

        var pick = pool[(int)((uint)Interlocked.Increment(ref _compressorNext) % pool.Count)];
        if (!_compressorLocks.TryGetValue(pick.Id, out var gate)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? reply;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await gate.WaitAsync();
                try { reply = await _lanes[pick.Id].AskAsync(body, 25000); }
                finally { gate.Release(); }
                if (reply is null) { _store.Event("compressor_timeout", pick.Id, $"lane={laneId} row={paneEventId}"); return; }

                var open = reply.IndexOf('{');
                var close = reply.LastIndexOf('}');
                if (open < 0 || close <= open) { _store.Event("compressor_failed", pick.Id, $"no json in reply: {Truncate(reply, 120)}"); return; }
                using var d = JsonDocument.Parse(reply[open..(close + 1)]);
                var headline = d.RootElement.TryGetProperty("headline", out var h) ? h.GetString() ?? "" : "";
                if (headline.Trim().Length == 0) { _store.Event("compressor_failed", pick.Id, "empty headline"); return; }

                var needsYou = d.RootElement.TryGetProperty("needs_you", out var ny) && ny.ValueKind == JsonValueKind.True;
                var options = new List<string>();
                if (d.RootElement.TryGetProperty("options", out var op) && op.ValueKind == JsonValueKind.Array)
                    options.AddRange(op.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3));

                // The fixed shape from §5. The lane's name is NOT repeated here the way the
                // design sketch shows it: in a pane the row already sits under that lane's
                // own coloured header, and in the feed the title is already the first thing
                // on the row. Printing it a third time is noise, not structure.
                var flat = headline.Trim().ReplaceLineEndings(" ");
                // A model that already opened with the word would otherwise render
                // "BLOCKED — BLOCKED ..." — the prefix is structure, so it is added exactly
                // once and never echoed.
                if (flat.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase))
                    flat = flat[7..].TrimStart(' ', ':', '-', '—');
                var text = new StringBuilder();
                if (needsYou) text.Append("BLOCKED — ");
                text.Append(Truncate(flat, 90));
                if (needsYou && options.Count > 0) text.Append("\n   options: ").Append(string.Join(" / ", options));

                _store.PaneCompressed(paneEventId, text.ToString());
                _store.Event("compressed", pick.Id,
                    $"{sw.ElapsedMilliseconds}ms lane={laneId} row={paneEventId} {body.Length}->{text.Length} chars needs_you={needsYou}");
            }
            catch (Exception ex) { _store.Event("compressor_failed", pick.Id, ex.Message); }
        });
    }

    // ------------------------------------------------------------- the dispatcher brain (§3)

    /// <summary>The middle rung of the escalation ladder: management judgement between
    /// code-that-checks-facts and the operator-who-decides-intent. Two warm sessions —
    /// cheap for the everyday calls, expensive only when the cheap one says it is not
    /// sure (operator's rule). It is deliberately kept AWAY from code: neutral cwd, no
    /// project CLAUDE.md, no skills, no tools it could run — its whole world is the
    /// management question in front of it.</summary>
    async Task<long> EnsureBrainAsync(bool hi)
    {
        var current = hi ? _brainHi : _brainLo;
        if (current > 0 && _lanes.TryGetValue(current, out var live) && live.Connected) return current;
        if (!_config.Brain) return -1;

        var sys = "You are Dodona's dispatcher brain. You make MANAGEMENT decisions for a multi-agent " +
                  "orchestrator: what a piece of work should be called, which lane an input belongs to, whether work " +
                  "deserves its own ticket and which paths that ticket should claim. You never read or write code, " +
                  "never run tools, and never do the work yourself — you are the coordinator's judgement, not a worker. " +
                  "Answer ONLY in the single-line JSON schema each request specifies: no prose, no markdown, no code fences. " +
                  "State your confidence honestly — saying low is how hard questions reach someone with more budget than you.";
        var model = hi ? _config.Model : _config.BrainModel;
        var effort = hi ? _config.Effort : _config.BrainEffort;
        var args = IsClaude(_config.Agent) ? ClaudeArgs(model, effort, sys, acceptEdits: false, utility: true) : new List<string>();
        var (id, msg) = await SpawnLaneAsync(hi ? "BRAIN-HI" : "BRAIN", hi ? "brain-hi" : "brain", NeutralCwd(), _config.Agent, args);
        if (id < 0) { _store.Event("brain_failed", null, msg); return -1; }
        if (hi) _brainHi = id; else _brainLo = id;
        return id;
    }

    /// <summary>Ask the expensive tier (spawning it on first use). Null when the brain is
    /// off, failed to start, or timed out — callers treat null as "the status quo stands",
    /// because the brain is an improver, never a gate.</summary>
    async Task<JsonElement?> AskBrainHiAsync(string question)
    {
        var id = await EnsureBrainAsync(hi: true);
        if (id < 0) return null;
        await _brainHiLock.WaitAsync();
        string? reply;
        try { reply = await _lanes[id].AskAsync(question, 30000); }
        finally { _brainHiLock.Release(); }
        if (reply is null) { _store.Event("brain_timeout", id, Truncate(question, 120)); return null; }
        try
        {
            var doc = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]);
            return doc.RootElement.Clone();
        }
        catch { _store.Event("brain_failed", id, $"unparseable: {Truncate(reply, 120)}"); return null; }
    }

    /// <summary>Post-hoc review of an auto-created lane — the §4 pattern applied to
    /// judgement: code already acted (lane exists, message delivered), the brain runs
    /// BEHIND and corrects visibly. Silent unless it disagrees (operator's rule #3):
    /// a rename is applied and announced as a receipt with its undo; a ticket is only
    /// ever SUGGESTED, because a wrong claim strands an agent behind the gate.</summary>
    void BrainReview(long laneId, string text, string chosenName, Choice choice)
    {
        if (!_config.Brain) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var loId = await EnsureBrainAsync(hi: false);
                if (loId < 0) return;
                var lanes = string.Join(", ", _store.LanesAll().Where(l => l.Role == "work" && l.State == "alive").Select(l => l.Title));
                var repos = string.Join(", ", Repositories().Select(r => r.Name));
                var q = $"A lane was just auto-created from operator input.\n" +
                        $"Input: {text}\nChosen name: {chosenName} (derived by code)\nModel policy: {choice.Describe}\n" +
                        $"Existing lanes: [{lanes}]\nRepositories in this workspace: [{repos}]\n" +
                        "Reply ONLY one line of JSON: {\"agree\":true|false,\"confidence\":\"high|medium|low\"," +
                        "\"better_name\":\"<ONE WORD, only if the chosen name is bad>\"," +
                        "\"ticket\":{\"title\":\"<name>\",\"claims\":[\"subtree:<path>\"]} (only if this work should be isolated on a branch)," +
                        "\"reason\":\"<=60 chars\"}";

                await _brainLoLock.WaitAsync();
                string? reply;
                try { reply = await _lanes[loId].AskAsync(q, 25000); }
                finally { _brainLoLock.Release(); }
                if (reply is null) { _store.Event("brain_timeout", loId, $"review lane {laneId}"); return; }

                JsonElement v;
                try { v = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]).RootElement.Clone(); }
                catch { _store.Event("brain_failed", loId, $"unparseable: {Truncate(reply, 120)}"); return; }

                // Cheap tier unsure → same question, expensive tier (operator's rule #1).
                var conf = v.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                if (conf == "low")
                {
                    _store.Event("brain_escalated", loId, $"review lane {laneId}");
                    var hiV = await AskBrainHiAsync(q);
                    if (hiV is not null) v = hiV.Value;
                }

                var agree = v.TryGetProperty("agree", out var ag) && ag.ValueKind == JsonValueKind.True;
                var reason = v.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                _store.Event("brain_review", laneId, $"agree={agree} conf={conf} reason={reason}");
                if (agree) return;                                     // silent unless disagreeing

                if (v.TryGetProperty("better_name", out var bn) && bn.ValueKind == JsonValueKind.String &&
                    bn.GetString() is { Length: > 0 } newName && !newName.Contains(' ') &&
                    !newName.Equals(chosenName, StringComparison.OrdinalIgnoreCase))
                {
                    var clean = newName.ToUpperInvariant();
                    _store.LaneTitle(laneId, clean);
                    _store.Event("brain_renamed", laneId, $"{chosenName} → {clean}: {reason}");
                    _store.PaneEvent(laneId, "announcement",
                        $"renamed to {clean} by the dispatcher (was {chosenName}) — undo: dodona lane-rename {laneId} {chosenName}",
                        null, null, acked: true);
                }

                if (v.TryGetProperty("ticket", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                    tk.TryGetProperty("title", out var tt) && tt.GetString() is { Length: > 0 } title)
                {
                    var claims = tk.TryGetProperty("claims", out var cl) && cl.ValueKind == JsonValueKind.Array
                        ? cl.EnumerateArray().Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList()
                        : new List<string?>();
                    var cmd = $"dodona ticket-create --title {title.ToUpperInvariant()}" +
                              string.Concat(claims.Select(c => $" --claim {c}"));
                    _store.Event("brain_suggested_ticket", laneId, cmd);
                    _store.PaneEvent(laneId, "announcement",
                        $"dispatcher: this looks ticket-worthy ({reason}) — {cmd}", null, null);
                }
            }
            catch (Exception ex) { _store.Event("brain_failed", null, ex.Message); }
        });
    }

    /// <summary>
    /// Lane granularity (docs/WORKSPACES-CONCIERGE.md §5, mechanism decided by the operator
    /// 2026-08-18): **a distinct task gets its own lane.** New agent, new context, and lanes
    /// are cheap. An existing lane keeps the input only when it clearly continues that thread.
    ///
    /// THE ERROR ASYMMETRY IS THE WHOLE DESIGN, and it is why this method stopped being
    /// optimistic:
    ///   * A WRONG CONTINUATION IS UNRECOVERABLE. `Say` delivers immediately; a later retarget
    ///     re-sends the text to the right lane, but the wrong agent already received it, may
    ///     already be acting on it, and its warm context is polluted. You cannot unsay a
    ///     sentence to an agent.
    ///   * A WRONG NEW LANE IS FREE. `dodona lane-stop N`, nothing polluted, nothing consumed
    ///     but a process spawn.
    ///
    /// So §4's "deliver instantly, correct behind" no longer holds for input that might be new
    /// work: correcting is exactly what is impossible. Nothing is delivered until the cheap
    /// classifier answers (operator's call, 2026-08-18, on ~1s of latency being the honest
    /// price). Two paths stay instant and model-free because they are free to decide in code:
    /// a `LANE:` prefix, and an unmistakable generic.
    ///
    /// The four verdicts:
    ///   generic   — "stop", "no", "try again". Focused lane, never second-guessed.
    ///   addendum  — continues an existing lane. Reason `direct` (talking to its ongoing work)
    ///               or `tweak` (a small correction to what it just finished). Same
    ///               destination, distinguished because the operator named both and the
    ///               distinction is worth having in the data.
    ///   new-task  — distinct work. Spawn and deliver.
    ///   unclear   — escalate to the expensive tier, then to the operator. Deliver NOTHING.
    /// </summary>
    async Task<string> RouteInput(string rawText)
    {
        // The operator's override is dispatch syntax, not content — strip it before the
        // sentence reaches any agent.
        var (text, ovModel, ovEffort) = Policy.StripOverrides(rawText);

        var work = _store.LanesAll().Where(l => l.Role == "work" && l.State == "alive").ToList();

        // ---- tier 0: an explicit prefix names its target. Code only, instant. ------------
        // `\s+`, not `\s*`: the documented form is `LANE: text`, and requiring the space
        // stops a colon inside the sentence being read as a target. Found by a test whose
        // directive `routekind:` became a LANE TITLED "ROUTEKIND", after which every later
        // `routekind:...` line was silently delivered to it as a tier-0 prefix — and the same
        // shape bites for real with a lane called HTTP and a sentence containing `http://`.
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Za-z0-9_-]+):\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
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

        var live = work.Where(l => _lanes.TryGetValue(l.Id, out var r) && r.Connected).ToList();

        // ---- nothing live: there is nothing to disambiguate, so start the work. ----------
        // A first sentence on an empty project is not an error condition, it is the beginning
        // of the work (§11: act, announce, allow undo).
        if (live.Count == 0)
        {
            var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
            if (id < 0) return msg;
            _store.RoutingInsert(text, "first", id, id, "only");
            return $"-> {msg} (started on {choice.Describe})";
        }

        // ---- who is focused. With no focus, pick rather than refuse (§11). ---------------
        long fid;
        var focused = _store.KvGet("focused_lane");
        if (focused is not null && long.TryParse(focused, out var f0) && live.Any(l => l.Id == f0)) fid = f0;
        else
        {
            var pick = live[^1];                       // the newest lane is the one you just made
            fid = pick.Id;
            _store.KvSet("focused_lane", fid.ToString());
            if (live.Count > 1)
                _store.PaneEvent(fid, "announcement", $"↦ focused {pick.Title} (nothing was focused)", null, null);
        }
        var frt = _lanes[fid];
        var focusedRow = work.First(l => l.Id == fid);

        // ---- tier 0.5: an unmistakable generic. Code, instant, no model. -----------------
        // The operator's rule, unchanged: a generic remark belongs to the focused lane, full
        // stop, no cleverness. Doing the obvious ones here makes the most common interjections
        // free AND keeps them out of the ~1s wait below — "stop" must never be slow.
        if (IsObviousGeneric(text))
        {
            frt.Say(text);
            _store.RoutingInsert(text, "generic", fid, fid, "explicit");
            return $"-> {focusedRow.Title} (generic)";
        }

        // ---- the classifier decides, and we WAIT for it. --------------------------------
        var router = _store.LanesAll().FirstOrDefault(l => l.Role == "router" && _lanes.ContainsKey(l.Id));
        if (router is null)
        {
            // No judgement available, so keep the old, well-understood default rather than
            // inventing one. Spawning on every sentence would be worse than this: generics are
            // already handled above, but "make it blue instead" would still become a lane, and
            // a system that cannot tell continuation from new work should not pretend it can.
            // The four-verdict behaviour needs the brain on, which is the default in
            // dodona.json; the suites deliberately run without it.
            frt.Say(text);
            _store.RoutingInsert(text, "focus", null, fid, "no-classifier");
            var stale0 = ovModel is not null || ovEffort is not null
                ? "  (model/effort is set when a lane starts — this one is already running)" : "";
            return $"-> {focusedRow.Title} (focus, no classifier warm){stale0}";
        }

        var verdict = await ClassifyAsync(router.Id, text, work, focusedRow);

        // A classifier that timed out or answered nonsense has no opinion. Same reasoning as
        // above: fall back to the known default rather than guessing in either direction.
        if (verdict is null)
        {
            frt.Say(text);
            _store.RoutingInsert(text, "focus", null, fid, "classifier-silent");
            return $"-> {focusedRow.Title} (focus, classifier did not answer)";
        }

        var (kind, target, conf, reason) = verdict.Value;

        // ---- generic: the focused lane, never second-guessed. ---------------------------
        if (kind == "generic")
        {
            frt.Say(text);
            _store.RoutingInsert(text, "generic", fid, fid, conf);
            return $"-> {focusedRow.Title} (generic)";
        }

        // ---- addendum: an existing lane's thread continues. -----------------------------
        if (kind == "addendum" && conf != "low")
        {
            var tLane = work.FirstOrDefault(l => l.Title.Equals(target ?? "", StringComparison.OrdinalIgnoreCase));
            if (tLane is not null && _lanes.TryGetValue(tLane.Id, out var trt))
            {
                trt.Say(text);
                _store.RoutingInsert(text, "addendum", tLane.Id, tLane.Id, conf);
                _store.Event("routed_addendum", tLane.Id, $"{reason}: {Truncate(text, 80)}");
                if (tLane.Id != fid)
                    _store.PaneEvent(tLane.Id, "announcement", $"→ continued here rather than {focusedRow.Title} ({reason})", null, null);
                return $"-> {tLane.Title} (addendum{(reason.Length > 0 ? ", " + reason : "")})";
            }
        }

        // ---- new-task: spawn and deliver. The cheap, undoable side of the asymmetry. -----
        if (kind == "new-task" && conf != "low")
        {
            var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
            if (id < 0) return msg;
            _store.RoutingInsert(text, "new-task", id, id, conf);
            _store.Event("routed_new_task", id, $"conf={conf} reason={reason}");
            return $"-> {msg} (new task, started on {choice.Describe})";
        }

        // ---- unclear, or a shaky guess: the expensive tier, then the operator. ----------
        // NOTHING has been delivered yet, and that is the point. Guessing here is the one
        // mistake that cannot be taken back, so the ladder's top rung is a question.
        var laneList = string.Join("\n", work.Select(l => $"- {l.Title} (lane {l.Id})"));
        var hi = await AskBrainHiAsync(
            "Decide where one line of operator input belongs in a multi-agent orchestrator.\n" +
            FactSheet(text, work, focusedRow) +
            "A distinct task should get its OWN new lane — new agent, clean context, and lanes are cheap. " +
            "An existing lane keeps it only when the input clearly continues that lane's thread: either it is " +
            "aimed at work that lane is doing now, or it is a small correction to what that lane just finished.\n" +
            "Reply ONLY one line of JSON: {\"kind\":\"generic|addendum|new-task|unclear\",\"target\":\"<LANE TITLE for addendum>\"," +
            "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars\"}");

        string? hKind = null, hTarget = null, hReason = "";
        var hConf = "low";
        if (hi is JsonElement he)
        {
            if (he.TryGetProperty("kind", out var k2)) hKind = k2.GetString();
            if (he.TryGetProperty("target", out var t2)) hTarget = t2.GetString();
            if (he.TryGetProperty("confidence", out var c2)) hConf = c2.GetString() ?? "low";
            if (he.TryGetProperty("reason", out var r2)) hReason = r2.GetString() ?? "";
        }
        _store.Event("classified_escalated", null, $"kind={hKind} target={hTarget} conf={hConf} input={Truncate(text, 80)}");

        if (hConf != "low")
        {
            if (hKind == "new-task")
            {
                var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
                if (id < 0) return msg;
                _store.RoutingInsert(text, "new-task", id, id, "escalated");
                _store.Event("routed_new_task", id, $"escalated reason={hReason}");
                return $"-> {msg} (new task, escalated, started on {choice.Describe})";
            }
            var hLane = work.FirstOrDefault(l => l.Title.Equals(hTarget ?? "", StringComparison.OrdinalIgnoreCase));
            if (hKind is "addendum" or "generic" && (hLane is not null || hKind == "generic"))
            {
                var dest = hLane ?? focusedRow;
                if (_lanes.TryGetValue(dest.Id, out var drt))
                {
                    drt.Say(text);
                    _store.RoutingInsert(text, hKind, dest.Id, dest.Id, "escalated");
                    return $"-> {dest.Title} ({hKind}, escalated)";
                }
            }
        }

        // ---- double uncertainty: ask, and hold the sentence. ----------------------------
        // The operator's own policy for ambiguity (§4) was "leave it with the focused lane",
        // but that was written when delivery was already done and the question was only whether
        // to retarget. Here nothing has been said yet, and delivering to the wrong lane is the
        // unrecoverable error — so the honest thing is to hold it and ask. Undoing a wait costs
        // nothing; undoing a polluted context costs the lane.
        var candidates = string.Join(" / ", work.Select(l => l.Title).Take(4));
        var rowId = _store.RoutingInsert(text, "ask", null, null, "unsure");
        _store.Event("routing_clarification", fid, $"decision {rowId}: {Truncate(text, 120)}");
        Announce($"[dodona] not sure whether “{Truncate(text, 45)}” is new work or continues something — " +
                 $"NOT delivered yet. Send it with a lane prefix ({candidates}) to continue one, " +
                 $"or `dodona lane-start --title <NAME>` then say it there for new work.");
        return $"held: not sure if this is new work or a continuation — nothing was delivered. " +
               $"Prefix a lane ({candidates}) to continue, or start a new lane.";
    }

    /// <summary>
    /// Unmistakable generics — the ones worth deciding in code so they are instant and free.
    ///
    /// Deliberately SHORT and anchored. This list exists to make "stop" fast, not to
    /// second-guess the classifier: anything not obviously one of these goes to the model,
    /// because the cost of a wrong guess here is a polluted lane. It matches the whole input,
    /// so "stop the nightly build from running" is not a generic — it is work.
    /// </summary>
    static bool IsObviousGeneric(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^(stop|wait|hold on|no|nope|yes|yep|ok|okay|continue|carry on|go on|go ahead|" +
            @"try again|again|retry|undo|undo that|revert that|never ?mind|cancel|abort|" +
            @"that'?s wrong|wrong|not that|do'?nt|don'?t do that|scrap that)[.!]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// What the classifier is told, as FACTS rather than things to guess at (§2.2: derive in
    /// code what is not really a judgement).
    ///
    /// Two of these facts exist because of specific operator corrections:
    ///   * MID-TURN IS A SIGNAL TOWARD THE LANE, not away from it. "some mid turn comments are
    ///     definately meant for the lane" — talking to a working agent is normal and common, so
    ///     the prompt says so rather than letting the model treat busy as "must be new work".
    ///   * LENGTH IS NOT A SIGNAL AT ALL. An earlier draft treated short input as probably a
    ///     continuation; the operator rejected it: "a short 'add this' on an existing lane might
    ///     mean a new work on that workspace". So no word count is given, and the discriminator
    ///     offered instead is SUBJECT — does the sentence name what this lane is about, or
    ///     something else.
    /// </summary>
    string FactSheet(string text, List<Store.LaneRow> work, Store.LaneRow focusedRow)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Input: ").Append(text).Append('\n');
        sb.Append("Lanes:\n");
        foreach (var l in work)
        {
            var last = _store.Tail(l.Id, 1).FirstOrDefault() ?? "";
            var busy = l.Presence.Length > 0 && l.Presence is not ("idle" or "landed" or "system");
            sb.Append($"- {l.Title}: {(busy ? "WORKING NOW" : "idle")}");
            if (l.Id == focusedRow.Id) sb.Append(" [FOCUSED — the operator is looking at this one]");
            if (last.Length > 0) sb.Append($"; last: {Truncate(last, 110)}");
            sb.Append('\n');
        }
        // Referring expressions point at something already under discussion. A fact, not a rule.
        var refs = System.Text.RegularExpressions.Regex.Matches(text, @"\b(that|it|this|instead|also|still|again|those|them)\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                   .Select(x => x.Value.ToLowerInvariant()).Distinct().ToList();
        if (refs.Count > 0)
            sb.Append($"The input refers back with: {string.Join(", ", refs)} — it may be about something already being discussed.\n");
        sb.Append("A lane that is WORKING NOW is a perfectly normal thing to talk to: operators interrupt working " +
                  "agents constantly, and that is usually an addendum, not new work.\n");
        return sb.ToString();
    }

    /// <summary>Ask the warm cheap classifier and WAIT. Null when it has no usable opinion —
    /// every caller treats that as "fall back", never as "guess".</summary>
    async Task<(string Kind, string? Target, string Conf, string Reason)?> ClassifyAsync(
        long routerId, string text, List<Store.LaneRow> work, Store.LaneRow focusedRow)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _routerLock.WaitAsync();
        string? reply;
        try { reply = await _lanes[routerId].AskAsync(FactSheet(text, work, focusedRow), 20000); }
        finally { _routerLock.Release(); }

        if (reply is null) { _store.Event("classifier_timeout", routerId, Truncate(text, 100)); return null; }
        try
        {
            using var d = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]);
            var kind = d.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "unclear" : "unclear";
            var target = d.RootElement.TryGetProperty("target", out var t) ? t.GetString() : null;
            var conf = d.RootElement.TryGetProperty("confidence", out var c) ? c.GetString() ?? "low" : "low";
            var reason = d.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            _store.Event("classified", routerId, $"{sw.ElapsedMilliseconds}ms kind={kind} target={target} confidence={conf} reason={reason} input={Truncate(text, 80)}");
            return (kind, target, conf, reason);
        }
        catch { _store.Event("classifier_failed", routerId, Truncate(reply, 120)); return null; }
    }

    /// <summary>
    /// Spawn a lane for this input and deliver to it — the `new-task` action, and also the
    /// first-lane case. Name derived in code, model/effort from the policy table (a claude
    /// process cannot change model mid-session, so this is decided where the lane is born), and
    /// `BrainReview` corrects the name or suggests a ticket from behind — machinery that already
    /// existed and needed no change.
    /// </summary>
    async Task<(long Id, string Msg, Choice Choice)> SpawnForAsync(string text, string? ovModel, string? ovEffort)
    {
        var name = NameFromText(text);
        var choice = Policy.Resolve(text, _config.Rules, _config.Model, _config.Effort, ovModel, ovEffort);
        var (newId, msg) = await SpawnAgentLaneAsync(name, choice.Model, choice.Effort);
        if (newId < 0) return (-1, $"error: could not start a lane for this: {msg}", choice);

        _store.Event("policy_choice", newId, $"{choice.Model}/{choice.Effort} why={choice.Why} overridden={choice.Overridden} text={text}");
        _store.KvSet("focused_lane", newId.ToString());
        _store.Event("lane_auto_created", newId, $"from input: {text}");
        _store.PaneEvent(newId, "announcement",
            $"started this lane on {choice.Describe} for “{Truncate(text, 45)}” — undo: dodona lane-stop {newId}",
            null, null, acked: true);   // a receipt: it badged the lane the instant it was born, which was a lie
        _lanes[newId].Say(text);
        BrainReview(newId, text, name, choice);   // fire-and-forget: corrects behind, never gates
        return (newId, name, choice);
    }


    /// <summary>The land (§7): the daemon executes the one atomic ref advance. The agent
    /// already rebased and verified in its own worktree; ff-only IS the freshness check —
    /// a branch that does not contain current main cannot land.</summary>
    string LandOp(long tid, out bool ok)
    {
        ok = false;
        var t = _store.Ticket(tid);
        if (t is null || t.State != "open") return $"refused: ticket {tid} not open";

        var repo = Repos.ByName(Repositories(), t.Repo);
        var repoPath = repo?.Path ?? _primary;
        var cfg = Config.For(_primary, repoPath);
        var where = t.Repo == "." ? "project root" : $"repository {t.Repo}";

        var tok = _store.TokenRead(t.Repo);
        if (tok.Holder != tid) { _store.Event("land_refused", null, $"ticket {tid}: not holder of {t.Repo} (holder={tok.Holder?.ToString() ?? "none"})"); return $"refused: ticket {tid} does not hold {t.Repo}'s merge token"; }
        if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() < DateTime.UtcNow)
        { _store.Event("land_refused", null, $"ticket {tid}: lease expired"); return "refused: merge-token lease expired; re-request"; }

        var (hc, head) = Git.Run(repoPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (hc != 0 || head != cfg.Main) return $"refused: {where} has '{head}' checked out, not '{cfg.Main}'";

        var (mc, mergeOut) = Git.Run(repoPath, "merge", "--ff-only", t.Branch);
        if (mc != 0)
        {
            _store.Event("land_refused", null, $"ticket {tid}: ff-only failed — rebase needed. {mergeOut}");
            return $"refused: not fast-forward — rebase {t.Branch} onto {cfg.Main} and re-verify first. {mergeOut}";
        }

        if (!_store.LandCommit(tid, t.Repo, out var reason))
        {
            // Merge advanced main but the fence refused in the same instant (lease raced
            // out). Reconcile-from-git heals: branch is an ancestor of main.
            _store.Event("land_inconsistent", null, $"ticket {tid}: {reason}");
            return $"landed on main but store fence refused ({reason}) — run reconcile";
        }

        // Post-land verify (§10): the daemon — code, not a model — runs the configured
        // steps, in the repository that just changed.
        var verifyMsg = "no verify steps configured";
        foreach (var step in cfg.Verify)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repoPath };
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
        if (cfg.Verify.Length > 0) { _store.Event("verify_green", null, $"ticket {tid}"); verifyMsg = "verify green"; }
        verified:

        // Landing retires the agent BEFORE the ground is pulled from under it
        // (docs/LANE-LIFECYCLE.md §3): the prune below deletes the directory the agent is
        // standing in, and an agent left running in a deleted worktree was this system's
        // most confusing possible state. The LANE stays — dormant, visible, its thread
        // intact — because §8 says lanes group sequential work and the next ticket in
        // this area belongs here. The session id is recorded, so a future respawn can
        // resume the context.
        if (t.LaneId is long landedLane)
        {
            if (_lanes.TryGetValue(landedLane, out var lrt))
            {
                lrt.Shutdown();
                _lanes.Remove(landedLane);
            }
            _store.LaneState(landedLane, "dormant");
            _store.LanePresence(landedLane, "landed");
            _store.PaneEvent(landedLane, "announcement",
                $"ticket {tid} landed — agent retired, lane keeps this thread", null, null, acked: true);
            _store.Event("lane_dormant", landedLane, $"ticket {tid} landed");
        }

        // Worktree prune — retryable, never silent (§15).
        var (wc, wOut) = Git.Run(repoPath, "worktree", "remove", "--force", t.Worktree);
        if (wc == 0) { Git.Run(repoPath, "branch", "-D", t.Branch); _store.Event("worktree_pruned", null, $"ticket {tid}"); }
        else _store.Event("worktree_prune_failed", null, $"ticket {tid}: {wOut}");

        ok = true;
        return $"landed ticket {tid} on {(t.Repo == "." ? "" : t.Repo + "/")}{cfg.Main}; {verifyMsg}";
    }

    /// <summary>Deploy the claim gate (§6 enforcement layer 1) into a ticket's worktree:
    /// a PreToolUse hook that asks the daemon whether the write is covered. Fails OPEN
    /// (logged) — the merge-time backstop catches what slips; a broken gate must not
    /// brick the lane.</summary>
    void DeployGate(string worktree, long ticketId, RepoRef repo)
    {
        // The gate files are deployment, not repo content: register them in the repo's
        // shared info/exclude (applies to every worktree) so `git add -A` by an agent
        // can never commit them — a ticket-1 gate landing on main conflicts with every
        // other ticket's gate on rebase. (Found by the M1 acceptance test.)
        // The exclude file belongs to the TICKET'S repository, not the workspace.
        //
        // The gate lives in settings.LOCAL.json, and that is the whole answer to "what
        // about a repo with its own tracked .claude/": Claude Code merges local settings
        // over project settings, so the repo's tracked settings.json is never touched,
        // never shows as modified in the worktree, and its own hooks keep running
        // alongside the gate. Writing settings.json here used to OVERWRITE the tracked
        // file in the working copy — info/exclude does not untrack anything, so the
        // agent saw a dirty file it did not change and the repo lost its hooks.
        var exclude = Path.Combine(repo.Path, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        var marker = "# dodona-gate deployment files";
        if (!File.Exists(exclude) || !File.ReadAllText(exclude).Contains(marker))
            File.AppendAllText(exclude, $"\n{marker}\n.claude/settings.local.json\ndodona-gate.ps1\n.dodona-bypass.log\n");

        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        File.WriteAllText(Path.Combine(worktree, ".claude", "settings.local.json"), """
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
            & '__DODONA__' claim-check __TICKET__ "$fp" --workspace '__WSID__' > $null 2> $null
            if ($LASTEXITCODE -eq 0) { exit 0 }
            if ($LASTEXITCODE -eq 1) {
                $reason = "outside ticket __TICKET__'s claim: $fp. Stay within claimed paths, or request an extension: dodona claim-extend __TICKET__ --claim <spec> --workspace '__WSID__'"
                @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } | ConvertTo-Json -Compress
                exit 0
            }
            Add-Content '__WT__\.dodona-bypass.log' ("{0:o} gate fail-open: {1}" -f (Get-Date), $fp)
            exit 0
            """;
        gate = gate.Replace("__DODONA__", Environment.ProcessPath ?? "dodona.exe")
                   .Replace("__TICKET__", ticketId.ToString())
                   .Replace("__WSID__", _instanceId)
                   .Replace("__WT__", worktree);
        File.WriteAllText(Path.Combine(worktree, "dodona-gate.ps1"), gate);
    }
}
