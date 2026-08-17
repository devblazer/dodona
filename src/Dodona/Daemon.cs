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
    Config _config;

    Daemon(string root, string instanceId, string ctlPipe, Store store)
    {
        _root = root;
        _instanceId = instanceId;
        _ctlPipe = ctlPipe;
        _store = store;
        _config = Config.Load(root);
    }

    public static async Task<int> RunAsync(string root, string instanceId, string ctlPipe)
    {
        // One daemon per canonical root, enforced at the OS (design §14).
        using var mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{instanceId}", out bool createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine($"another daemon already owns this root (instance {instanceId})");
            return 3;
        }
        using var store = new Store(Path.Combine(root, ".dodona", "store.db"));
        return await new Daemon(root, instanceId, ctlPipe, store).LoopAsync();
    }

    async Task<int> LoopAsync()
    {
        _store.Event("daemon_start", null, $"pid={Environment.ProcessId} root={_root}");
        Console.WriteLine($"dodona daemon: instance {_instanceId}, ctl pipe {_ctlPipe}, pid {Environment.ProcessId}");

        // Reconcile (design §12): rows are the claim; the pipe is the proof.
        foreach (var l in _store.LanesAll().Where(l => l.State == "alive"))
        {
            var rt = new LaneRuntime(l.Id, l.Pipe, _store);
            if (await rt.ConnectAndPumpAsync(attempts: 3)) _lanes[l.Id] = rt;
            else { _store.LaneState(l.Id, "unreachable"); _store.Event("lane_unreachable", l.Id, "reconcile: pipe did not answer"); }
        }
        _store.Event("reconcile_done", null, $"connected={_lanes.Count}");

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

                var id = _store.LaneCreate(title);
                var pipe = $"dodona-{_instanceId}-lane{id}";
                _store.LanePipe(id, pipe);

                var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                              ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
                var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = _root };
                psi.ArgumentList.Add(pipe);
                psi.ArgumentList.Add(child);
                foreach (var a in childArgs) psi.ArgumentList.Add(a);
                psi.Environment["DODONA_SHIM_INFO"] = Path.Combine(_root, ".dodona", $"shim-lane{id}.json");
                Process.Start(psi);
                _store.Event("shim_spawned", id, $"pipe={pipe} child={child}");

                var rt = new LaneRuntime(id, pipe, _store);
                if (await rt.ConnectAndPumpAsync())
                {
                    _lanes[id] = rt;
                    _store.Event("lane_started", id, title);
                    w.WriteLine($"lane {id} title {title} pipe {pipe}");
                }
                else
                {
                    _store.LaneState(id, "unreachable");
                    w.WriteLine($"error: lane {id} shim pipe never answered");
                }
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
                foreach (var l in _store.LanesAll())
                {
                    var connected = _lanes.TryGetValue(l.Id, out var rt) && rt.Connected;
                    w.WriteLine($"lane {l.Id}  {l.Title,-10}  state={l.State}  connected={connected}  session={l.Session ?? "-"}");
                }
                break;

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
                w.WriteLine($"approved ticket {tid}");
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
                    w.WriteLine($"refused: ticket {tid} is merge:on-approval and not approved");
                    w.WriteLine("##exit 1");
                    break;
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
            case "stop-daemon":
                w.WriteLine("stopping (lanes keep running)");
                return true;
        }
        return false;
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
