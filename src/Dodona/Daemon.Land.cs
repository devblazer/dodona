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
    /// <summary>
    /// The merge that brought <paramref name="main"/> into <paramref name="branch"/>: returns
    /// that merge commit and its FIRST parent — the branch's state immediately before main
    /// arrived. Empty strings when no such merge exists.
    ///
    /// **This replaced a fork-point calculation that was measured wrong, and the way it was
    /// wrong is worth keeping.** `REVIEW-AND-MERGE-PLAN` §10 says the drop check must diff
    /// against the merge base rather than main's tip, and the reason is sound: once main has
    /// been merged in, main IS an ancestor of the branch, so `merge-base main branch` returns
    /// main's own tip and a branch that reverted main's change looks identical to one that
    /// never saw it. The first implementation therefore recovered a fork point from the
    /// branch's merge commits — and took the OLDEST one, reasoning that a wider window catches
    /// more.
    ///
    /// It caught nothing. `git rev-list --first-parent --merges &lt;branch&gt;` walks the whole
    /// ancestry, and a ticket branch's ancestry CONTAINS MAIN'S OWN MERGE HISTORY — every
    /// previous ticket that landed. So "oldest merge" resolved to an ancient merge on main and
    /// the fork point came out as the repository's **init commit**, identically for every
    /// ticket (measured: `fork=adc8bfb` for tickets 1 through 7). Against init the dropped file
    /// did not exist yet, so the pre-image comparison could never match and the check passed
    /// everything. A check that is blind while looking armed — CLAUDE.md §0.3 exactly.
    ///
    /// So there is no fork point here at all. The reference is **M^1**, the branch tip just
    /// before the merge, which is defined by the merge itself and cannot be confused with
    /// anything in main's history. §10's intent is honoured — the comparison is emphatically
    /// not against main's tip — while the quantity used is one git can hand over exactly.
    /// The NEWEST qualifying merge is the right one: anything reverted before an earlier merge
    /// was brought back in by the later one.
    /// </summary>
    static (string Merge, string PreMerge) MainMergeOnBranch(string workDir, string main, string branch)
    {
        var (lc, list) = Git.Run(workDir, "rev-list", "--first-parent", "--merges", branch);
        if (lc != 0) return ("", "");
        foreach (var raw in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))   // newest first
        {
            var m = raw.Trim();
            if (m.Length == 0) continue;
            var (pc, parents) = Git.Run(workDir, "rev-list", "--parents", "-n", "1", m);
            if (pc != 0) continue;
            var parts = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;             // <self> <p1> <p2>...
            var p1 = parts[1]; var p2 = parts[2];
            // The second parent must be part of main — otherwise this is some other merge the
            // branch carries (including one it inherited from main itself, which is what made
            // the first version of this function useless).
            if (Git.Run(workDir, "merge-base", "--is-ancestor", p2, main).Code != 0) continue;
            // ...and the merge must be the branch's OWN, not one it inherited: an inherited
            // merge is an ancestor of main, and the branch did not perform it.
            if (Git.Run(workDir, "merge-base", "--is-ancestor", m, main).Code == 0) continue;
            return (m, p1);
        }
        return ("", "");
    }

    /// <summary>
    /// Files where main's change has gone missing from the branch (D-R4). A path counts as a
    /// silent drop when all three hold: the merge changed it (so main contributed something
    /// there), and the branch's final version is byte-identical to the PRE-MERGE version, and
    /// therefore main's contribution is simply absent. That is a fact, not a judgement, and it
    /// is the one failure an agent's own report will never mention — the tests still pass,
    /// because nothing references the discarded code.
    ///
    /// A resolution that COMBINES both sides differs from the pre-merge version, so it is not
    /// flagged. That is the common, legitimate case and it must stay quiet.
    /// </summary>
    static List<string> SilentDrops(string workDir, string preMerge, string mergeCommit, string branch)
    {
        var drops = new List<string>();
        var (dc, changed) = Git.Run(workDir, "diff", "--name-only", preMerge, mergeCommit);
        if (dc != 0) return drops;
        foreach (var raw in changed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.Trim();
            if (f.Length == 0) continue;
            // A missing path is a real state, not an error: main may have DELETED the file, and
            // a branch that put its copy back has dropped that deletion too. ShaOrEmpty makes
            // absence a value rather than an exception, which is why it exists (see Git.cs).
            var atPre = Git.ShaOrEmpty(workDir, $"{preMerge}:{f}");
            var atMerge = Git.ShaOrEmpty(workDir, $"{mergeCommit}:{f}");
            var atBranch = Git.ShaOrEmpty(workDir, $"{branch}:{f}");
            if (atMerge != atPre && atBranch == atPre) drops.Add(f);
        }
        return drops;
    }

    /// <summary>One land, in flight or finished (R3.5). The whole of the phase's state, and it
    /// is deliberately in memory: a daemon that restarts forgets a land it did not finish, which
    /// is the correct answer rather than a gap — a persisted "landing" row is exactly the kind of
    /// thing that outlives its reason and goes quietly stale (CLAUDE.md §0.1). The recovery is
    /// re-running `land`, which is idempotent by construction: the trunk moves only in the last
    /// step, so an interrupted land has merged main in (a no-op next time) and nothing else.</summary>
    sealed class LandRun
    {
        public LandRun(long ticket) { Ticket = ticket; StartedUtc = DateTime.UtcNow; }
        public long Ticket { get; }
        public DateTime StartedUtc { get; }
        /// <summary>WRITTEN LAST, and volatile, so a reader that sees `Done` also sees `Ok` and
        /// `Message`. The writer is the land's own task; every reader is the control pipe.</summary>
        public volatile bool Done;
        public bool Ok;
        public string Message = "";
        public void Finish(bool ok, string msg) { Ok = ok; Message = msg; Done = true; }
    }

    /// <summary>Lands by ticket id. A finished entry is kept, so the outcome stays readable after
    /// the caller has gone, and is replaced when the same ticket lands again.</summary>
    readonly ConcurrentDictionary<long, LandRun> _lands = new();

    /// <summary>Everything the expensive half of a land needs, resolved by the cheap half that
    /// still runs on the pipe. Passing it forward rather than re-resolving is not a micro-
    /// optimisation: `RepoOf` and `Config.For` read the registry and the filesystem, and a land
    /// that answered "your repository is fine" and then acted on a different one would be P0.1's
    /// wrong-main incident with a race in front of it.</summary>
    sealed record LandPlan(Store.TicketRow Ticket, string RepoPath, Config Cfg, Store.RepoId TokenId);

    /// <summary>THE LAND IS NOT ON THE CONTROL PIPE ANY MORE (R3.5, decision D-R14). This is what
    /// `case "land"` calls, and it returns in milliseconds.
    ///
    /// **The freeze it removes, measured 2026-08-20.** The pipe is serial — one
    /// `NamedPipeServerStream` instance, `HandleAsync` awaited inline — and the land ran on it. So
    /// for the whole duration of a land's verify the daemon answered *nothing*: no UI, no lane
    /// input, no `say`, no other repository's land. The narrow verify this repo settled on holds
    /// it ~20 s; the full `dev gate` would hold it **4.6 minutes**. That is CLAUDE.md §0.1's
    /// *never hung* on the one operation an operator is certainly watching.
    ///
    /// **The protocol, which is the part that changes for callers.** The cheap gate stays here —
    /// ticket open, repository resolvable, token held, lease alive, and the trunk actually checked
    /// out in the shared checkout — because it costs milliseconds and a caller deserves those
    /// refusals on the spot. Past that point the reply is *landing…*, and the outcome arrives
    /// three ways: an announcement in the ticket's pane, an event in the store, and
    /// `land-status &lt;ticket&gt;`. `dodona land` polls that last one so a shell still gets an
    /// exit code (see `LandCli` in Program.cs) — the daemon is free either way, which is the
    /// whole point.
    ///
    /// **Two constraints this had to preserve, both load-bearing (plan §5).**
    ///
    /// * **The token is held across the WHOLE flow.** Nothing here releases or re-checks it: the
    ///   in-worktree merge and the fast-forward stay inside one task, so no window exists in
    ///   which main can move between them. D-R2's fast-forward-as-an-assertion depends on it, and
    ///   a swap cannot cut a land in half either — `Blockers` already refuses to swap while a
    ///   merge token is held.
    /// * **A failed land still leaves the worktree clean and main untouched.** Unchanged, because
    ///   `LandFlow` is the same code in the same order: every giving-up path aborts its merge and
    ///   returns before the fast-forward. What the split adds is that the failure is now
    ///   *reported* asynchronously, so it has to announce itself — and it does, on every path
    ///   inside `LandFlow`, plus the two this wrapper covers (success, and a throw).
    ///
    /// A second `land` for a ticket already landing is refused rather than run twice. That was
    /// impossible before — the serial pipe made it impossible — and it is the one new race the
    /// split creates, so it is closed here rather than left to be discovered.</summary>
    string LandBegin(long tid, out bool started)
    {
        started = false;
        if (_lands.TryGetValue(tid, out var already) && !already.Done)
            return $"refused: ticket {tid} is already landing ({(int)(DateTime.UtcNow - already.StartedUtc).TotalSeconds}s so far) — dodona land-status {tid}";

        var refusal = LandGate(tid, out var plan);
        if (refusal is not null) return refusal;

        var run = new LandRun(tid);
        _lands[tid] = run;
        started = true;
        _store.Event("land_started", null, $"ticket {tid}");
        _ = Task.Run(() =>
        {
            string msg;
            var ok = false;
            try { msg = LandFlow(plan!, out ok); }
            catch (Exception ex)
            {
                // The pipe used to catch this and turn it into `error: …` on the caller's
                // reply. Nobody is holding that reply now, so an unhandled throw would be a
                // land that simply stopped existing — the silent failure this codebase pays for
                // most (§3's dead routing ladder). It announces, and it says what to do.
                msg = $"error: the land threw — {ex.Message}";
                _store.Event("land_threw", null, $"ticket {tid}: {ex}");
                Announce(plan!.Ticket, $"ticket {tid}'s land threw: {ex.Message} — nothing was lost (the trunk moves only in the last step); re-run dodona land {tid}");
            }
            run.Finish(ok, msg);
            _store.Event(ok ? "land_finished" : "land_refused_async", null, $"ticket {tid}: {msg}");
            // Success is the one outcome LandFlow does not announce in its own words: it writes
            // "agent retired" into the lane's pane and used to return the receipt to a caller
            // that was still waiting. There is no such caller now, so the receipt is announced —
            // which is also the only announcement a ticket with no lane would ever get.
            if (ok) Announce(plan!.Ticket, msg);
        });
        return $"landing ticket {tid} — merge, verify and fast-forward run off the control pipe; the outcome announces itself and dodona land-status {tid} reports it";
    }

    /// <summary>The cheap half: milliseconds, and therefore still answered on the pipe. Returns a
    /// refusal, or null with the plan the expensive half runs on.</summary>
    string? LandGate(long tid, out LandPlan? plan)
    {
        plan = null;
        var t = _store.Ticket(tid);
        if (t is null || t.State != "open") return $"refused: ticket {tid} not open";

        // THE FALLBACK HERE USED TO BE `_primary`, AND IT COULD FAST-FORWARD THE WRONG MAIN.
        // `Repos.ByName(repos, t.Repo)` returns null as soon as the naming rule moves under an
        // open ticket — attach a second project and every "." ticket resolves to nothing — and
        // the land then ran `git merge --ff-only ticket/N` in the FIRST PROJECT'S repository.
        // A ref advance is the one irreversible act in this system, so there is no default for
        // "which repository": it is the recorded one or it is a refusal (P0.1).
        var repo = RepoOf(t);
        if (repo is null)
        {
            _store.Event("land_refused", null, $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
            return $"refused: ticket {tid}'s repository is no longer in this workspace " +
                   $"({(t.RepoPath.Length > 0 ? t.RepoPath : $"'{t.Repo}'")}) — re-attach it or abandon the ticket";
        }
        var repoPath = repo.Path;
        var cfg = Config.For(_primary, repoPath);
        var where = t.Repo == "." ? "project root" : $"repository {t.Repo}";

        // R7 / D-R28: DODONA DOES NOT MERGE A `delivery: pr` REPOSITORY, AND THIS IS WHERE THAT
        // IS TRUE. Refusing in the cheap half makes `LandFlow` unreachable for such a repo, so
        // the guarantee is structural rather than a set of conditionals sprinkled down the flow:
        // nothing merges main in, nothing fast-forwards, no worktree is pruned and no branch is
        // deleted, because none of that code runs. It sits ahead of the token check so the answer
        // is "there is no merge here" rather than "you do not hold a token" — which would send
        // someone to `token-request`, which refuses for this same reason.
        if (cfg.IsPr)
        {
            _store.Event("land_refused_pr_mode", null, $"ticket {tid}: {where} is delivery: pr");
            Announce(t, $"ticket {tid} is not Dodona's to land: {where} is delivery: pr — push {t.Branch} and open a PR; {cfg.Main} is untouched");
            return $"refused: {where} is delivery: pr — Dodona does not merge here. Push {t.Branch} and open a PR " +
                   $"(dodona ticket-record {tid} is what it did); {cfg.Main} is unchanged and {t.Branch} is kept.";
        }

        var tokenId = TokenIdOf(t);
        var tok = _store.TokenRead(tokenId);
        if (tok.Holder != tid) { _store.Event("land_refused", null, $"ticket {tid}: not holder of {t.Repo} (holder={tok.Holder?.ToString() ?? "none"})"); return $"refused: ticket {tid} does not hold {t.Repo}'s merge token"; }
        if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() < DateTime.UtcNow)
        { _store.Event("land_refused", null, $"ticket {tid}: lease expired"); return "refused: merge-token lease expired; re-request"; }

        // Checked BEFORE the merge and the verify, because those cost minutes and this costs
        // milliseconds — and ANNOUNCED rather than failing quietly (plan §10). It is true
        // while CLAUDE.md §0.0 keeps the operator on main in the shared checkout, so the one
        // way to see this is a state nobody expected, which is exactly when a silent refusal
        // in a daemon log is the wrong place for the sentence.
        var (hc, head) = Git.Run(repoPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (hc != 0 || head != cfg.Main)
        {
            _store.Event("land_refused", null, $"ticket {tid}: {where} has '{head}' checked out, not '{cfg.Main}'");
            Announce(t, $"ticket {tid} cannot land: {where} has '{head}' checked out, not '{cfg.Main}' — check out {cfg.Main} there and re-run dodona land {tid}");
            return $"refused: {where} has '{head}' checked out, not '{cfg.Main}'";
        }

        plan = new LandPlan(t, repoPath, cfg, tokenId);
        return null;
    }

    /// <summary>The land (§7, and `docs/REVIEW-AND-MERGE-PLAN.md` §3): the daemon executes
    /// the one atomic ref advance — but it now does the ordinary developer flow first
    /// (D-R1), in this order and under the merge token throughout:
    ///
    /// <code>
    ///   git merge &lt;main&gt;    IN THE WORKTREE, on the ticket branch
    ///   &lt;verify&gt;             IN THE WORKTREE, on the merged result
    ///   git merge --ff-only  in the shared checkout: now guaranteed
    /// </code>
    ///
    /// **What changed and why.** This used to be `merge --ff-only` and nothing else: when
    /// main had moved it refused with *"rebase &lt;branch&gt; onto &lt;main&gt; and re-verify
    /// first"* — and **nothing in the tree performed that rebase**, so concurrent work
    /// could not land at all. Worse, verify ran AFTER the ref advance, in the repository
    /// that had just changed, so a red verify had already shipped.
    ///
    /// **ff-only is now an ASSERTION rather than a policy (D-R2).** After main has been
    /// merged into the branch, the merge back *is* a fast-forward — measured, not assumed:
    /// git itself reports `Fast-forward` and main's tree comes out byte-identical to the
    /// branch tip that was verified. That identity is the whole reason verifying the
    /// worktree is equivalent to verifying main (`WORK-ISOLATION-PLAN` D-5), and it is why
    /// verify may move ahead of the merge at all. So if ff-only fails *now*, main moved
    /// despite the token — a real fault, and refusing is correct.
    ///
    /// **The ordering is the trap, not the merge** (plan §10). The in-worktree merge must
    /// happen while the token is HELD, which is why it lives here, below the holder check,
    /// and never in `token-request` before the grant: otherwise two lanes both merge main
    /// in, both believe they verified against current main, and the second one's
    /// fast-forward is against a main that moved underneath it.
    ///
    /// **AND IT NO LONGER RUNS ON THE CONTROL PIPE** (R3.5 / D-R14). `LandBegin` answers the
    /// caller; this is what its task runs. See `LandBegin` for the protocol and for the two
    /// constraints the split had to preserve.</summary>
    string LandFlow(LandPlan plan, out bool ok)
    {
        ok = false;
        var (t, repoPath, cfg, tokenId) = plan;
        var tid = t.Id;

        // ---- D-R1 step 1: bring main INTO the branch, in the agent's own worktree --------
        //
        // Measured before this was written (the premise the whole phase rests on): `git merge
        // <main>` inside a linked worktree SUCCEEDS while main is checked out in the shared
        // checkout, leaves the shared checkout's HEAD and main sha untouched, and leaves the
        // worktree clean. Only `checkout` of a branch held elsewhere is refused; merging a ref
        // into the current branch never checks it out.
        var mergeMsg = "already current with " + cfg.Main;
        if (t.Worktree.Length > 0 && Directory.Exists(t.Worktree))
        {
            // A dirty worktree first, because `git merge` refuses one and its complaint does
            // not say what to do about it. NEVER `git stash` here: the stash is repo-global,
            // one shared ref in the common dir, so two lanes stashing interleave one stack and
            // `pop` takes the other lane's work (CLAUDE.md §5.2). Commit to the branch instead.
            var (sc, dirty) = Git.Run(t.Worktree, "status", "--porcelain");
            if (sc == 0 && dirty.Length > 0)
            {
                _store.Event("land_refused", null, $"ticket {tid}: worktree has uncommitted changes");
                Announce(t, $"ticket {tid} cannot land: uncommitted changes in its worktree — commit them to {t.Branch} (never git stash: it is repo-global) and re-run dodona land {tid}");
                return $"refused: ticket {tid}'s worktree has uncommitted changes — commit them to {t.Branch} " +
                       $"(do NOT git stash: the stash is repo-global and another lane's pop would take them) and re-run land";
            }

            var (bmc, bmOut) = Git.Run(t.Worktree, "merge", cfg.Main, "-m", $"merge {cfg.Main} into {t.Branch} before landing ticket {tid}");
            if (bmc != 0)
            {
                // A conflict the daemon must not guess at (D-R3). Code does not resolve —
                // the agent does, and it keeps its context to do it. What code owes here is a
                // CLEAN TREE: a half-merged worktree makes every later check lie, so the abort
                // is not optional and it is not best-effort.
                var (uc, conflicted) = Git.Run(t.Worktree, "diff", "--name-only", "--diff-filter=U");
                var names = uc == 0 && conflicted.Length > 0
                    ? string.Join(", ", conflicted.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                    : "(git named none — see the daemon log)";
                var (ac, aOut) = Git.Run(t.Worktree, "merge", "--abort");
                if (ac != 0) _store.Event("land_merge_abort_failed", null, $"ticket {tid}: {aOut}");
                _store.Event("land_conflict", null, $"ticket {tid}: merging {cfg.Main} into {t.Branch} conflicted in {names}");
                Announce(t, $"ticket {tid}: merging {cfg.Main} in conflicts in {names} — resolve it in {t.Worktree}, commit, then re-run dodona land {tid}");
                return $"refused: merging {cfg.Main} into {t.Branch} conflicts in {names} — the merge was aborted, " +
                       $"so the worktree is clean. Resolve it there, commit, then land again.";
            }
            // "Already up to date." is the common case and costs one git call: main has not
            // moved since the branch was cut, so no merge commit is created and the land is
            // byte-for-byte what it always was.
            mergeMsg = bmOut.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
                ? $"already current with {cfg.Main}"
                : $"merged {cfg.Main} in";
            if (mergeMsg != $"already current with {cfg.Main}")
                _store.Event("land_merged_main", null, $"ticket {tid}: {cfg.Main} -> {t.Branch}");
        }
        else
        {
            // No worktree to merge in. Not fatal — a ticket can outlive its checkout — but it
            // means ff-only below is back to being a policy rather than an assertion, so say so
            // rather than letting the land look like it did the flow.
            mergeMsg = "no worktree: could not merge " + cfg.Main + " in";
            _store.Event("land_no_worktree", null, $"ticket {tid}: {t.Worktree}");
        }

        // ---- D-R4: the SILENT DROP, which is the failure a report will not mention ---------
        //
        // The dangerous resolution is not the messy one. It is the quiet one: the agent
        // resolves by discarding what main brought in, and the tests still pass because
        // nothing references the discarded code. Nobody's judgement is needed for that — it is
        // mechanically detectable — and no report will mention it, which is why code asks.
        {
            var dropDir = t.Worktree.Length > 0 && Directory.Exists(t.Worktree) ? t.Worktree : repoPath;
            var (mergeCommit, preMerge) = MainMergeOnBranch(dropDir, cfg.Main, t.Branch);
            if (mergeCommit.Length == 0)
            {
                // No merge of main on this branch, so main contributed nothing here for the
                // branch to have discarded — there is genuinely nothing to check, which is a
                // different thing from a check that failed to run. Recorded either way, because
                // a check that quietly does nothing is the fail-open this codebase has paid for
                // twice (§3's dead routing ladder, GateHook's BOM).
                _store.Event("land_drop_check_moot", null, $"ticket {tid}: no merge of {cfg.Main} on {t.Branch}, nothing to drop");
            }
            else
            {
                var drops = SilentDrops(dropDir, preMerge, mergeCommit, t.Branch);
                _store.Event("land_drop_check", null,
                    $"ticket {tid}: {drops.Count} drop(s) against pre-merge {preMerge[..Math.Min(8, preMerge.Length)]} (merge {mergeCommit[..Math.Min(8, mergeCommit.Length)]})");
                if (drops.Count > 0)
                {
                    var names = string.Join(", ", drops);
                    _store.Event("land_silent_drop", null, $"ticket {tid}: reverted {cfg.Main}'s change to {names} (pre-merge {preMerge[..Math.Min(8, preMerge.Length)]})");
                    Announce(t, $"ticket {tid} did not land: it reverts {cfg.Main}'s change to {names}. If that resolution was deliberate, re-apply it as an edit on top of {cfg.Main}'s version rather than as the pre-merge file.");
                    return $"refused: {t.Branch} reverts {cfg.Main}'s change to {names} — the branch carries the PRE-MERGE version of " +
                           $"{(drops.Count == 1 ? "that file" : "those files")}, so merging {cfg.Main} in delivered the change and something put it back. " +
                           $"Take {cfg.Main}'s version (or resolve on top of it) and land again.";
                }
            }
        }

        // ---- D-R1 step 2: verify the MERGED RESULT, in the worktree, BEFORE the ref moves --
        //
        // This used to run after `LandCommit`, in the repository that had just changed — so a
        // red verify had already shipped and there was nothing left to refuse
        // (`WORK-ISOLATION-PLAN` D-5). It is exactly equivalent here and strictly safer: the
        // fast-forward below makes main's tree byte-identical to the tip verified here (D-R2).
        var verifyMsg = "no verify steps configured";
        var verifyDir = t.Worktree.Length > 0 && Directory.Exists(t.Worktree) ? t.Worktree : repoPath;
        foreach (var step in cfg.Verify)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = verifyDir };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(step);
            using var p = Process.Start(psi)!;
            var errT = Task.Run(() => p.StandardError.ReadToEnd());
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                _store.Event("verify_red", null, $"ticket {tid} step '{step}': {so}{errT.Result}".Trim());
                // The merge commit STAYS. It is legitimate work the agent will fix on top of,
                // and throwing it away would mean resolving the same conflict again next round.
                // What matters is that main did not move: this returns before the ff-only.
                Announce(t, $"ticket {tid} did not land: verify RED at '{step}' after merging {cfg.Main} in — {cfg.Main} is unchanged. Fix it in {t.Worktree} and re-run dodona land {tid}");
                return $"refused: VERIFY RED at '{step}' ({mergeMsg}) — {cfg.Main} unchanged. " +
                       $"Fix it on {t.Branch} and land again.";
            }
        }
        if (cfg.Verify.Length > 0) { _store.Event("verify_green", null, $"ticket {tid}"); verifyMsg = "verify green"; }

        // ---- D-R1 step 3: the fast-forward, which is now an assertion (D-R2) --------------
        var (mc, mergeOut) = Git.Run(repoPath, "merge", "--ff-only", t.Branch);
        if (mc != 0)
        {
            // Reaching here means main moved WHILE THIS TICKET HELD THE TOKEN — the one thing
            // the token exists to prevent. It is not "the agent needs to rebase" any more, and
            // saying so would send someone to do work that is already done.
            _store.Event("land_not_ff_under_token", null, $"ticket {tid}: {cfg.Main} moved while ticket held the token. {mergeOut}");
            Announce(t, $"ticket {tid} did not land: {cfg.Main} moved while this ticket held the merge token — nothing was merged. Re-run dodona land {tid}");
            return $"refused: not fast-forward AFTER merging {cfg.Main} in — {cfg.Main} moved while ticket {tid} " +
                   $"held the merge token, which the token exists to prevent. Nothing landed; re-run land. {mergeOut}";
        }

        if (!_store.LandCommit(tid, tokenId, out var reason))
        {
            // Merge advanced main but the fence refused in the same instant (lease raced
            // out). Reconcile-from-git heals: branch is an ancestor of main.
            _store.Event("land_inconsistent", null, $"ticket {tid}: {reason}");
            return $"landed on main but store fence refused ({reason}) — run reconcile";
        }

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
                _lanes.TryRemove(landedLane, out _);
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
        // Says what the flow DID, not just that it finished: "merged main in" is the
        // difference between a land that resolved against current main and one that never
        // had to, and the operator reading a receipt cannot tell them apart otherwise.
        return $"landed ticket {tid} on {(t.Repo == "." ? "" : t.Repo + "/")}{cfg.Main}; {mergeMsg}; {verifyMsg}";
    }

    /// <summary>
    /// Deploy the gate for a lane: a PreToolUse hook that asks the daemon whether a write is
    /// allowed. Returns the settings file to hand the agent, or null when this lane gets no gate.
    ///
    /// **IT WRITES NOTHING INTO ANYBODY'S REPOSITORY (D-17).** It used to write
    /// `.claude/settings.local.json` into the ticket worktree plus a block in the repo's shared
    /// `.git/info/exclude`. The operator's challenge is what killed that, and it is correct: a
    /// hook in a project's settings file binds EVERYTHING that runs Claude Code in that folder,
    /// including the operator's own IDE session. Only the process Dodona started should be gated.
    /// So the file lives in workspace state and is passed on the launch line with `--settings`.
    ///
    /// Three hazards died with it, and the first was live:
    ///
    ///  * `File.WriteAllText` on `settings.local.json` is a WHOLE-FILE OVERWRITE. Safe until now
    ///    only by accident -- a ticket worktree is a fresh checkout and the file is untracked, so
    ///    there was never one there to destroy. This phase gates the shared checkout too, where
    ///    that write would have silently wiped the developer's own allowed-commands list with
    ///    nothing in git to restore from.
    ///  * both footprints in a repo that is not the operator's to modify.
    ///  * the stale `dodona-gate.ps1` cleanup, and the generated script whose parse failure it
    ///    existed to sweep up.
    ///
    /// **`--settings` is a PRECEDENCE LAYER, NOT A REPLACEMENT**, which is the property that makes
    /// this safe: command-line settings sit above Local and Project, so the project's own settings
    /// still load, and hook entries MERGE across levels rather than replacing each other -- a
    /// repo's own PreToolUse hooks keep firing alongside this one. Two constraints fall out, both
    /// easy to get wrong and both deliberate here:
    ///
    ///  * THE FILE CONTAINS ONLY THE HOOK. Command-line settings outrank the project on any
    ///    colliding key, so a second key here would silently override what the project chose.
    ///  * NO `--setting-sources` FOR A WORK LANE. `ClaudeArgs` passes `--setting-sources user`
    ///    for utility roles on purpose; doing that to a work lane would cut the project's own
    ///    settings and hooks out of the agent doing the work -- manufacturing exactly the problem
    ///    this decision exists to avoid.
    ///
    /// MEASURED, 2026-08-20, because "the flag exists" is not "the hook fires": a PreToolUse hook
    /// supplied via `--settings <file>` DOES fire under `-p --permission-mode bypassPermissions`,
    /// and its deny is enforced -- the write never happened and the agent was told why. A control
    /// run without the flag wrote the file, so the absence is the refusal and not the model
    /// declining. (The pre-existing measurement was taken against a hook in a PROJECT file, which
    /// is a different route and no longer the one used.)
    ///
    /// AND HOOKS ARE FIXED AT SESSION START, ALSO MEASURED: a two-turn stream-json session kept
    /// firing the hook on turn 2 after it had been REMOVED from the settings file between turns.
    /// So this file is read once, at launch, and rewriting it under a live agent does nothing --
    /// which is why the gate names the LANE and lets the daemon look up the rest. A lane's ticket,
    /// claims and worktree all change during its life; the lane id does not.
    /// </summary>
    string? DeployGate(long laneId, long ticketId = 0, string? worktree = null)
    {
        var exe = Environment.ProcessPath ?? "dodona.exe";
        var cmd = $"\"{exe}\" gate-hook --lane {laneId} --workspace \"{_instanceId}\"" +
                  (ticketId > 0 ? $" --ticket {ticketId}" : "") +
                  (worktree is { Length: > 0 } ? $" --worktree \"{worktree}\"" : "");
        var hookCmd = JsonSerializer.Serialize(cmd);
        var dir = Paths.WorkspaceDir(_instanceId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"gate-lane{laneId}.json");
        // ONE KEY. See the remarks: anything else in here outranks the project's own choice.
        File.WriteAllText(file, $$"""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Edit|Write|MultiEdit|NotebookEdit",
                    "hooks": [
                      {
                        "type": "command",
                        "command": {{hookCmd}}
                      }
                    ]
                  }
                ]
              }
            }
            """);
        return file;
    }

}
