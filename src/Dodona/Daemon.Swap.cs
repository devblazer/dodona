using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Dodona;

// Part of Daemon, split out of a 6,791-line file (issue #23). Same class, same
// behaviour -- only the file boundary is new.
sealed partial class Daemon
{
    // ------------------------------------------------------------- hot swap (§13/§14)

    /// <summary>Internal rather than private because the CONCIERGE swaps too now (issue #9)
    /// and asks the same question of the same binary. One prober, so the two can never
    /// disagree about what a build is.</summary>
    internal sealed record NewBuild(string Exe, string Build, int Schema, int ShimProtocol, int ConciergeSchema);

    /// <summary>Ask a candidate binary what it is. Running `<exe> version --json` is the
    /// only honest way — the file name proves nothing, and we must know its schema and
    /// shim protocol BEFORE it touches the store.</summary>
    internal static NewBuild? Probe(string exe, out string error)
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
                d.RootElement.GetProperty("shimProtocol").GetInt32(),
                // Defaulted, not required: `conciergeSchema` was added when the concierge
                // learned to swap, and every build before that had a v1 concierge store —
                // the only version there has ever been. Demanding the field would turn
                // "swap to a slightly older build" into an unreadable JSON error.
                d.RootElement.TryGetProperty("conciergeSchema", out var cs) && cs.ValueKind == JsonValueKind.Number
                    ? cs.GetInt32() : 1);
        }
        catch (Exception ex) { error = $"binary did not answer `version --json` ({ex.Message})"; return null; }
    }

    /// <summary>What stands in the way of a seamless swap (§14). Empty means go.
    ///
    /// A schema MIGRATION is deliberately not here any more: it used to park every
    /// migrating build behind a question, and the answer was always "yes, but keep an
    /// undo" — so HandoffAsync now takes the backup itself and proceeds (never-stuck,
    /// 2026-08-18). Only a DOWNGRADE still refuses, in ConsiderSwapAsync, because a
    /// build that cannot read the store cannot be allowed to open it at all.</summary>
    List<string> Blockers(NewBuild nb)
    {
        var blockers = new List<string>();

        // The live shims are the authority, not our own constant: they were spawned by
        // whichever binary was running then, and the successor has to talk to THEM.
        // Only a shim NEWER than the candidate blocks: shims can never be swapped (they
        // own their child's stdio), so every daemon commits to speaking all protocols
        // ≤ its own — that commitment is what keeps protocol bumps from freezing
        // updates for as long as one long-lived lane survives. Bump Ver.ShimProtocol
        // WITHOUT keeping the old dialect readable and this line is what breaks.
        var stranded = _lanes.Values.Where(l => l.Connected && l.ShimProtocol > nb.ShimProtocol).ToList();
        if (stranded.Count > 0)
            blockers.Add($"shim protocol v{stranded[0].ShimProtocol}→v{nb.ShimProtocol} with {stranded.Count} live shim(s)");

        // Any repository mid-merge blocks the swap — the tokens are independent, but the
        // daemon that would vanish underneath them is not.
        //
        // THIS IS ALSO WHAT COVERS AN IN-FLIGHT LAND (R3.5). Since the land left the control pipe
        // a swap could arrive in the middle of one, and cutting a land in half is exactly what
        // this list exists to prevent — it needs no new entry only because R3.5's first
        // load-bearing constraint is that the token is held across the WHOLE flow. Break that
        // constraint and this stops covering it, silently.
        foreach (var tok in _store.TokensAll())
        {
            if (tok.Holder is not long h) continue;
            if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() <= DateTime.UtcNow) continue;
            var t = _store.Ticket(h);
            blockers.Add($"{t?.Title ?? $"ticket {h}"} is mid-merge{(tok.Repo == "." ? "" : $" in {tok.Repo}")}");
        }
        return blockers;
    }

    /// <summary>The swap decision. Clear road → hand off. Something in the way → arm:
    /// record the proposal and fire it the instant the blocker clears, announcing both.
    /// Nothing waits on a human (never-stuck, 2026-08-18) — the operator lost a morning
    /// to updates parked behind questions. `swap-answer hold` parks one on purpose;
    /// `swap-answer now` forces through a blocker. Only a schema DOWNGRADE refuses.</summary>
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
            // Blocked → armed, not asked. The ticker fires it the moment the blocker
            // clears; the announcement carries the two overrides. "ask" survives only
            // as the wire default's name — its behavior is now when-it-lands.
            var id = _store.SwapCreate(nb.Exe, nb.Build, nb.Schema, nb.ShimProtocol, blocker, "when-it-lands", "armed");
            _store.Event("swap_armed", null, $"swap {id} build {nb.Build}: {blocker}");
            Announce($"[dodona] update {nb.Build} armed — lands the instant this clears: {blocker} (dodona swap-answer now to force, hold to park)");
            lines.Add($"armed: update {nb.Build} fires when this clears — {blocker}");
            lines.Add("override: dodona swap-answer now | hold");
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
            // A migrating successor gets an undo BEFORE it exists (§14 revised): back up
            // the store, then proceed. This is what turned "schema migration" from a
            // parked question into an ordinary swap — act, announce, allow undo (§11).
            // The backup is taken by the ONE writer while it is still the one writer.
            if (nb.Schema > Ver.Schema)
            {
                var bak = Paths.Store(_instanceId) + $".pre-v{Ver.Schema}";
                _store.Backup(bak);
                _store.Event("store_backed_up", null, $"swap {swapId}: schema v{Ver.Schema}→v{nb.Schema}, backup {bak}");
                Announce($"[dodona] store backed up before migration v{Ver.Schema}→v{nb.Schema} — undo: dodona stop-daemon, then restore {bak} over store.db");
            }

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
                // Fired. On success this process is exiting anyway; on failure the swap
                // row is 'failed' and SwapLive() goes quiet — but blocked swaps now arm
                // THEMSELVES (auto-arm), so a later one needs this ticker still alive.
                // A `return` here left the first failed fire as the last fire ever.
            }
            catch { /* next tick */ }
        }
    });

    /// <summary>
    /// Publish-on-drift: when <c>main</c> moves, this daemon builds that commit and swaps
    /// itself to it, so "work is done but not live" cannot happen and no person and no agent
    /// has to remember to publish. It exists because edited-not-built, built-not-published and
    /// published-not-committed each blocked the operator once in a single day, and an
    /// instruction in CLAUDE.md is advisory while a watcher is not -- the claim-gate reasoning
    /// (design §6).
    ///
    /// THE QUESTION IS NOW EXACT (RECOVERY-PHASES P2.3). It used to be "is any source file
    /// newer than the running image?", which needed five separate guards to behave and looped
    /// 64 times in one afternoon regardless. It is now <c>git rev-parse main</c> against the
    /// commit this build was made from: a comparison of two SHAs, with no clock, no filesystem,
    /// no partial-write window and no three-projects-versus-one-binary asymmetry.
    /// <see cref="Ver.Provenance"/> carries the numbers of the failure this replaced.
    ///
    /// WHAT WENT WITH IT, all of it deletion (P2.4): the debounce (a commit is already atomic
    /// and quiet), the <c>.built-from</c> stamp (the commit is in the assembly),
    /// <c>kv.autopublish_last_tried</c> (the SHA is its own guard -- if main has not moved there
    /// is nothing to do, and if it has, doing it once is correct), and the 30-minute dirty-tree
    /// nag (uncommitted work can no longer reach the app at all, so nagging about it answers a
    /// question nobody can now ask).
    ///
    /// WHAT STAYED, because it was never about mtimes: consecutive-failure surrender. Measured
    /// on the operator's instance at 16 attempts and 16 failures in one afternoon, each a full
    /// three-project build, until the failure that mattered was buried under fifteen copies of
    /// itself. A broken main must not rebuild forever.
    ///
    /// Safety is inherited, not added: the publish it runs goes through the ordinary M4 path, so
    /// a failed build changes nothing and is announced, the new binary must answer
    /// <c>version --json</c> before anything is promoted, the desktop shortcut moves only onto a
    /// build a daemon accepted, and a mid-merge lane blocks the swap with the usual three
    /// answers. None of those guards are about mtimes, so none of them were touched.
    /// </summary>
    void StartDriftWatcher()
    {
        if (!_config.AutoPublish) return;
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1") return;   // suites own their lifetime

        // A build that cannot say what commit it came from cannot ask the exact question, and
        // it must NOT fall back to guessing -- the old code degraded to the image's own mtime in
        // exactly this case, which is the loop-prone comparison wearing a fallback.
        //
        // BUT PARKING HERE WAS WRONG TOO, and it is the operator's standing directive that says
        // so: never hung, halted, stuck or outdated, and when you add a wait, name the thing
        // that un-sticks it -- a condition, never a person (CLAUDE.md 0.1). The first version
        // announced "I am not watching" and stopped, which left auto-publish silently OFF until
        // somebody read a line in the feed. That happened for real: the very first publish of
        // this feature was performed by the PREVIOUS binary, which had no stamping code, so the
        // installed build came out unlabelled and its watcher declined. Nobody would have known.
        //
        // THIS IS A BOOTSTRAP, and a bootstrap is exactly the shape that should arm itself: the
        // action is bounded (one publish), reversible (a failed build changes nothing), and it
        // can only ever publish MAIN, which is the one thing this daemon is already allowed to
        // publish. So: do it once, say what was done, and if the result STILL carries no
        // provenance, surrender loudly rather than trying again -- an unstamped build that
        // republishes itself is the 64-iteration loop with a new cause.
        if (Ver.NoProvenance)
        {
            _store.Event("autopublish_no_provenance", null, $"build {Ver.Build} exe {Ver.ExePath}");
            if (_store.KvGet("autopublish_bootstrap_tried") == "1")
            {
                _store.Event("autopublish_surrendered", null, "bootstrap publish did not produce a stamped build");
                Announce("[dodona] auto-publish is ON, but the build it produced STILL carries no commit stamp, so " +
                         "it has stopped trying. The live app will not follow main until this is fixed. " +
                         "(A build made by `dev build`, or published with --exe, cannot be stamped.)");
                return;
            }
            _store.KvSet("autopublish_bootstrap_tried", "1");
            Announce("[dodona] this build carries no commit stamp (it was built before stamping existed, or with " +
                     "--exe), so it cannot tell whether main has moved. Publishing main ONCE to arm itself — " +
                     "nothing else changes, and a failed build changes nothing at all.");
            _ = Task.Run(() =>
            {
                try
                {
                    var boot = string.IsNullOrEmpty(_config.AutoPublishProject) ? _primary : _config.AutoPublishProject;
                    if (string.IsNullOrWhiteSpace(boot) || !Path.IsPathRooted(boot) || !Git.IsRepo(boot)) return;
                    var (bc, bout) = Git.Run(boot, "rev-parse", ConfigFor(".").Main);
                    if (bc != 0 || bout.Trim().Length != 40) return;
                    var (code, output) = RunPublish(Path.GetFullPath(boot), bout.Trim());
                    if (code != 0)
                        _store.Event("autopublish_failed", null, $"bootstrap publish: {Truncate(output, 800)}");
                }
                catch (Exception ex) { _store.Event("autopublish_error", null, $"bootstrap: {ex.Message}"); }
            });
            return;     // a successful bootstrap hands off and this process exits
        }

        var project = string.IsNullOrEmpty(_config.AutoPublishProject) ? _primary : _config.AutoPublishProject;

        // AN ABSOLUTE PATH OR NOTHING, and this is not defensive dressing -- it is the bug this
        // guard was found by. A workspace with no attached member has an EMPTY _primary, so
        // `Path.Combine("", "src", ...)` is a RELATIVE path, resolved against whatever directory
        // this daemon happened to be started from. Measured: a test workspace with no members
        // watched the OPERATOR'S live repo, found its main, and published it -- because the
        // daemon's cwd was that repo. An actor silently building a tree it does not own is the
        // whole failure class Phase 2 exists to close (RECOVERY-PHASES section 0), so the answer
        // is to refuse and say which, never to resolve it against a convenient default.
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathRooted(project))
        {
            _store.Event("autopublish_misconfigured", null,
                $"no absolute project to watch (first project='{_primary}', autoPublishProject='{_config.AutoPublishProject}')");
            Announce("[dodona] autoPublish is on, but this workspace has no absolute source tree to watch " +
                     "(no member attached, and no autoPublishProject set) — nothing is being watched");
            return;
        }
        project = Path.GetFullPath(project);
        if (!File.Exists(Path.Combine(project, "src", "Dodona", "Dodona.csproj")))
        {
            _store.Event("autopublish_misconfigured", null, $"{project} is not a Dodona source tree");
            Announce($"[dodona] autoPublish is on, but {project} has no src/Dodona — nothing is being watched");
            return;
        }
        if (!Git.IsRepo(project))
        {
            // The question is `git rev-parse main`. Without a repo there is no question, and
            // guessing from mtimes is what this phase deleted.
            _store.Event("autopublish_misconfigured", null, $"{project} is not a git repository");
            Announce($"[dodona] autoPublish is on, but {project} is not a git repository — nothing is being watched");
            return;
        }

        // We know our own commit, so any previous bootstrap succeeded. Clearing this is what
        // keeps the guard from outliving the problem: it is a one-shot for THIS situation, not a
        // permanent mark against the workspace.
        if (_store.KvGet("autopublish_bootstrap_tried") == "1") _store.KvSet("autopublish_bootstrap_tried", "");

        var mainBranch = ConfigFor(".").Main;
        _store.Event("autopublish_watching", null,
            $"{project} tracking {mainBranch}; running {Ver.Short(Ver.Commit)}, baseline {Ver.Short(Ver.MainBaseline)}");
        if (Ver.IsTrial)
            Announce($"[dodona] running a TRIAL of {Ver.Branch}@{Ver.Short(Ver.Commit)} — the next commit to " +
                     $"{mainBranch} replaces it (it was cut at {Ver.Short(Ver.MainBaseline)})");

        _ = Task.Run(async () =>
        {
            // Give up after three consecutive failures, say so ONCE, and stay quiet until
            // something changes. A publish by hand clears it, because a successful swap ends
            // this process entirely.
            const int giveUpAfter = 3;
            int consecutiveFailures = 0;
            bool surrendered = false;

            while (true)
            {
                await Task.Delay(15000);
                if (surrendered) continue;
                try
                {
                    // TWO SHAs. That is the whole comparison.
                    //
                    // The baseline is Ver.MainBaseline and not Ver.Commit, which is what makes a
                    // trial behave as P2.5 promises: a trial carries the main SHA it was cut
                    // against, so it sits still until main moves PAST that point and is then
                    // replaced. For an ordinary main build the two are the same value, so this
                    // costs no special case.
                    // Git.Run and not Git.Sha: Sha THROWS when the ref does not resolve, so a
                    // `target.Length == 0` guard after it is dead code that reads like a check.
                    // A missing main is a normal transient state (a fresh repo, a fetch in
                    // flight), not an error worth an event every 15 seconds.
                    var (rc, rout) = Git.Run(project, "rev-parse", mainBranch);
                    if (rc != 0) continue;                          // no such ref yet; nothing to compare
                    var target = rout.Trim();
                    if (target.Length != 40) continue;              // not a resolved sha
                    if (target == Ver.MainBaseline) continue;       // up to date, exactly

                    _store.Event("autopublish_started", null,
                        $"{mainBranch} at {Ver.Short(target)}, this build baselined {Ver.Short(Ver.MainBaseline)}");
                    Announce($"[dodona] {mainBranch} moved to {Ver.Short(target)} — building that commit and swapping to stay live");

                    var (code, output) = RunPublish(project, target);
                    if (code != 0)
                    {
                        var reason = output.Split('\n').LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                                     ?? output.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "unknown";
                        consecutiveFailures++;
                        _store.Event("autopublish_failed", null,
                            $"attempt {consecutiveFailures}/{giveUpAfter} for {Ver.Short(target)}: {Truncate(output, 800)}");
                        if (consecutiveFailures >= giveUpAfter)
                        {
                            surrendered = true;
                            _store.Event("autopublish_surrendered", null,
                                $"{consecutiveFailures} consecutive failures; watching stopped until a manual publish");
                            Announce($"[dodona] auto-publish has failed {consecutiveFailures} times running and has STOPPED trying — " +
                                     $"the live app stays behind {mainBranch} until you publish by hand. Last reason: {Truncate(reason.Trim(), 140)}");
                        }
                        else
                            Announce($"[dodona] auto-publish FAILED — the live app is now BEHIND {mainBranch}: {Truncate(reason.Trim(), 160)}");
                    }
                    else consecutiveFailures = 0;
                    // success: the swap arrives through our own control pipe and this daemon
                    // exits mid-handoff — nothing more to do here. A parked swap (mid-merge
                    // blocker) already announced its three answers.
                }
                catch (Exception ex) { _store.Event("autopublish_error", null, ex.Message); }
            }
        });
    }

    /// <summary>Publish <paramref name="commit"/> out of <paramref name="project"/>.
    ///
    /// <c>--from &lt;sha&gt;</c> is what keeps the publisher out of everybody's way (P2.3): it
    /// makes publish check that commit out into a detached worktree of its OWN and build there,
    /// so the tree an operator or another session is working in is never touched and its
    /// <c>obj/</c> is never contended for. Publish then sees HEAD == main inside that worktree
    /// and stamps main provenance, with no flag to pass and no special case.</summary>
    (int Code, string Output) RunPublish(string project, string commit)
    {
        var psi = new ProcessStartInfo(Ver.ExePath)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _primary };
        // Scoped to THIS workspace, never --all: an auto-publish is one workspace noticing
        // its own sources moved, and broadcasting a swap to every daemon on the machine
        // because one repo was edited is exactly what §7 set out to stop.
        foreach (var a in new[] { "publish", "--project", project, "--from", commit, "--workspace", _instanceId }) psi.ArgumentList.Add(a);
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

            // NEVER COLLECT A STAMP NEWER THAN OUR OWN — A PUBLISH IN FLIGHT IS NOT GARBAGE.
            //
            // Measured on the operator's own machine, 2026-08-21, twice in one evening. Merging to
            // main and then running `publish` by hand means TWO publishes: the manual one, and
            // `autoPublish`'s watcher noticing the same commit within its 15 s poll. They build
            // into stamps a second or two apart. The first to finish swaps its daemon in, that
            // daemon reaches this loop, and "not mine, not locked" was true of the OTHER
            // publish's brand-new directory — so it deleted it. What the operator saw:
            //
            //   building Dodona → ...\bin\20260820-222405        (succeeded; dodona.exe verified)
            //   error: no such binary: ...\bin\20260820-222405\dodona.exe
            //
            // and, when a file happened to be locked partway through `Directory.Delete`, a
            // HALF-DELETED corpse left behind: `20260820-221926` reduced to a single dodona.exe,
            // `20260820-115855` to two files. Those corpses are the second half of the damage —
            // they are newest-by-name, so anything resolving the installed binary that way gets a
            // directory with no `dodona.dll` and the unreadable "The application to execute does
            // not exist" (CLAUDE.md §2's snippet did exactly this until the same day).
            //
            // The rule needs no coordination, no lock file and no knowledge of who else is
            // publishing: a stamp NEWER than the running image is either a publish in flight or a
            // successor about to take over, and neither is garbage. Old ones are the entire point
            // of a garbage collector, and they still go.
            //
            // Only STAMPS are compared, and only when both parse. `yyyyMMdd-HHmmss` is ordinal-
            // sortable by construction, which is why the compare is a string compare. A directory
            // whose name is not a stamp is judged exactly as it was before — no new retention, so
            // nothing starts accumulating on the strength of a name nobody recognises.
            var dirStamp = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('\\'));
            var myStamp = Path.GetFileName(mine);
            if (IsStamp(dirStamp) && IsStamp(myStamp) && string.CompareOrdinal(dirStamp, myStamp) > 0)
            {
                _store.Event("binary_gc_kept", null, $"{dir}: newer than this build ({myStamp}) — a publish in flight is not garbage");
                continue;
            }
            // AND NOT ONE A LIVE AGENT'S GATE STILL POINTS AT (WORK-ISOLATION-PLAN D-17).
            //
            // Hooks are read at SESSION START and never re-read (measured 2026-08-20 -- see the
            // note where redeployment used to be), so an agent launched by an older build holds
            // that build's exe path for its whole life. Collecting the directory therefore breaks
            // its gate, and a gate whose command cannot even start is a FAIL-OPEN that never
            // reaches our code to be logged -- which under layer 1 is a write into the operator's
            // live checkout. That is the 2026-08-18 incident, and rewriting the gate file was
            // never able to fix it.
            //
            // A text scan over small files, across EVERY workspace rather than just this one: any
            // daemon may be the one that swapped, and another workspace's agent is no less live
            // for it. Dead lanes' gate files are deleted in reconcile, so nothing pins a
            // directory once its agent is gone.
            if (GateFilesNaming(dir)) { _store.Event("binary_gc_kept", null, $"{dir}: a live lane's gate names it"); continue; }
            try { Directory.Delete(dir, recursive: true); _store.Event("binary_gc", null, dir); }
            catch (Exception ex) { _store.Event("binary_gc_skipped", null, $"{dir}: {ex.Message}"); }
        }
    }

    /// <summary>Is this directory name a publish stamp — `yyyyMMdd-HHmmss`? Only stamps are
    /// compared for age in <see cref="GcOldBuilds"/>, so a directory named anything else is
    /// judged exactly as it was before the newer-than-mine rule existed. Shape only, never
    /// `DateTime.TryParse`: the format is ordinal-sortable by construction and parsing it would
    /// drag in a culture that has no business deciding whether a build is garbage.</summary>
    static bool IsStamp(string name) =>
        name.Length == 15 && name[8] == '-' &&
        name.All(c => char.IsAsciiDigit(c) || c == '-');

    /// <summary>Does any workspace's per-lane gate file name this build directory? See
    /// <see cref="GcOldBuilds"/> for why that must veto collection. Text, not JSON: the question
    /// is whether the path appears at all, and a parse failure must not read as "no".</summary>
    static bool GateFilesNaming(string buildDir)
    {
        var needle = Path.GetFullPath(buildDir).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            if (!Directory.Exists(Paths.WorkspacesDir)) return false;
            foreach (var ws in Directory.GetDirectories(Paths.WorkspacesDir))
                foreach (var f in Directory.GetFiles(ws, "gate-lane*.json"))
                    try
                    {
                        // UNESCAPED FIRST. The file is JSON, so every backslash in the command is
                        // DOUBLED -- a raw Contains() against a Windows path with single
                        // backslashes can never match, and the retention silently retains
                        // nothing while looking installed. m4 carries the same trap for the
                        // same file from the reading side, and this code walked straight into
                        // it: caught by tightening the check to demand that a retention
                        // actually FIRED rather than that few directories survived.
                        var text = File.ReadAllText(f).Replace("\\\\", "\\");
                        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { return true; }   // unreadable: assume it names us rather than break a gate
        }
        catch { return true; }               // ditto -- a stale directory costs disk, the other way
                                             // costs enforcement
        return false;
    }

}
