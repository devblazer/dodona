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
    /// WHICH PROJECT CONFIGURED THIS LANE, as a row (T2's only observable surface). A lane's
    /// permission mode was previously unanswerable from outside the process: it lands in a
    /// claude argv nobody reads back, and `IsClaude` is false for the acceptance suites' fake
    /// agent, so no suite could ever see the flag at all.
    ///
    /// It reports the argv when there IS one and the config otherwise, and says which — so a
    /// fake-agent lane still proves *which project's config was resolved* without the event
    /// pretending to be evidence of an argv that was never built.
    /// </summary>
    /// <summary>The OUTCOME of trying to materialise a ticket: the row, its branch and its
    /// worktree, or why not.
    ///
    /// **`Conflicts` is no longer a refusal (D-R5, R3) — it is an OVERLAP REPORT.** It used to
    /// be separate from `Error` because a claim conflict was the one refusal a caller might act
    /// on rather than print: promotion degraded to a refused write naming the holder, while
    /// `ticket-create` printed the conflicts and stopped. Both of those are gone. The list is
    /// kept, and is still the reason it is a separate field, because a ticket that overlaps
    /// another open one is worth SAYING — it is the derived signal D-R7 wants in front of the
    /// manager — but `Ok` no longer consults it, and no caller may refuse on it.</summary>
    sealed record TicketMade(long Id, string Branch, string Worktree,
                             string? Error, bool Exit1, List<string> Conflicts)
    {
        public bool Ok => Id > 0 && Error is null;
        public static TicketMade Failed(string error, bool exit1 = true) =>
            new(-1, "", "", error, exit1, new List<string>());
    }

    /// <summary>
    /// Create a ticket and materialise its branch and worktree. FACTORED OUT OF THE
    /// `ticket-create` HANDLER, where it existed only inline, so that layer 2 can call it: a
    /// refused write in the shared checkout promotes itself into a ticket
    /// (docs/WORK-ISOLATION-PLAN.md section 3, P2), and two implementations of "make a ticket"
    /// would drift on exactly the checks that matter -- repo exclusivity and claim conflict.
    ///
    /// Every refusal in here is a refusal for BOTH callers, which is the point. The messages keep
    /// their CLI shape (leading spaces, several lines) because `ticket-create` prints them
    /// verbatim; `Exit1` carries the one case that deliberately did not set `##exit 1`.
    /// </summary>
    TicketMade MakeTicket(RepoRef repo, string title, string mode,
                          List<(string, string)> claims, IEnumerable<string> specs)
    {
        // Attach-time enforcement and the partial unique index both cover the
        // ordinary case; neither can cover a BARE FOLDER legitimately attached to
        // two workspaces (exempt, harmless) that someone later ran `git init` in.
        // Only a check at the point of use notices the ground moved -- the same
        // reasoning that puts a diff backstop behind the claim gate (section 6).
        try
        {
            using var reg = new Registry();
            if (reg.RepoConflict(repo.Path, _instanceId) is Workspace other)
            {
                _store.Event("ticket_repo_not_exclusive", null, $"'{title}': {repo.Path} also in {other.Id}");
                return TicketMade.Failed(
                    $"error: {repo.Path} also belongs to workspace \"{other.Name}\" ({other.Id})\n" +
                    "       a repo belongs to at most ONE workspace at a time -- two workspaces over one\n" +
                    "       repo is two merge tokens over one main, the race this system exists to prevent\n" +
                    $"       move it:  dodona workspace-move --member \"{repo.Path}\" --workspace \"{_wsName}\"");
            }
        }
        catch (Exception ex) { _store.Event("registry_unreadable", null, ex.Message); }

        var repoCfg = Config.For(_primary, repo.Path);
        if (!Git.HasCommit(repo.Path))
            return TicketMade.Failed(
                $"error: {repo.Name} is a git repository with no commits, so there is no '{repoCfg.Main}' to branch from\n" +
                "       run `dodona repo-init` to make the first commit");

        // Both: the display name the claims were written relative to, and the canonical
        // path that says WHICH repository this is whatever it gets called later (P0.1).
        var (id, conflicts) = _store.TicketCreate(null, title, mode, repo.Name, Repos.Key(repo.Path), claims);
        // AN OVERLAP IS NOW RECORDED AND CARRIED ON WITH (D-R5). The event stays -- it is the
        // trail a reviewer reads, and two tickets over one file is a thing worth knowing even
        // though it is not a thing worth blocking. `TicketCreate` no longer returns -1 for an
        // overlap at all, so there is nothing to branch on here.
        if (conflicts.Count > 0)
            _store.Event("claim_overlap", null, $"'{title}' (ticket {id}) overlaps: {string.Join(" | ", conflicts)}");

        // Branch names are workspace-unique because ticket ids are. The worktree
        // lives beside the MEMBER holding this repository -- worktrees are the one
        // piece of state that deliberately did NOT move into workspace territory
        // (WORKSPACES-CONCIERGE.md section 1: they are volume- and path-sensitive, and
        // moving them buys nothing). For a one-member workspace this is the exact
        // path it has always been.
        var branch = $"ticket/{id}";
        var wt = Path.Combine(Paths.Worktrees(repo.MemberPath), $"t{id}");
        var (code, output) = Git.Run(repo.Path, "worktree", "add", "-b", branch, wt, repoCfg.Main);
        if (code != 0)
        {
            _store.TicketState(id, "abandoned");
            _store.Event("ticket_git_failed", null, $"ticket {id} repo {repo.Name}: {output}");
            // NO `##exit 1` here, preserved from the inline original: this path printed the error
            // and broke without one, and changing an exit code while moving code is how a
            // refactor becomes a behaviour change nobody asked for.
            return TicketMade.Failed($"error: worktree add failed in {repo.Name}: {output}", exit1: false);
        }
        _store.TicketSetGit(id, branch, wt);
        // NO GATE DEPLOYED HERE (D-17). A ticket has no lane yet -- `ticket-agent` creates it, or
        // promotion attaches the one it came from -- and the gate is per-LANE and handed over on
        // the launch line, so `AttachShimAsync` is the one place that writes it. Same funnel
        // correction `DaemonClient.Send` needed for start-on-demand: a call site cannot forget
        // what it does not do.
        _store.Event("ticket_created", null,
            $"ticket {id} '{title}' repo {repo.Name} branch {branch} claims [{string.Join(", ", specs)}]");
        // THE OVERLAPS TRAVEL WITH THE SUCCESS NOW. This returned `new List<string>()` while the
        // overlap branch above returned the real list -- correct while an overlap was a refusal
        // (the two were mutually exclusive), and silently wrong the moment an overlap became
        // something a SUCCESSFUL create has to report. Caught by the re-aimed
        // `the_overlap_is_reported_and_names_the_holder`, which is the whole argument for
        // re-aiming a check rather than deleting it.
        return new TicketMade(id, branch, wt, null, false, conflicts);
    }

    /// <summary>
    /// LAYER 2: THE REFUSAL IS A PROMOTION, NOT A WALL (WORK-ISOLATION-PLAN section 3, P2).
    ///
    /// A plain lane has just tried to write into the shared checkout and layer 1 refused it. So
    /// give it the thing it actually needed: a ticket, a worktree, a gate, and the same session
    /// carried into it. **Nothing has been written yet** -- that is the whole reason layer 1 sits
    /// at the write attempt and not at the commit, because moving edits afterwards is not
    /// available: they would be in the wrong tree and `git stash` is repo-global, so two lanes
    /// stashing interleave one stack (CLAUDE.md 5.2).
    ///
    /// This is NOT a relaxation of `lane-respawn`'s refusal to re-home a ticket lane, which is
    /// correct and untouched. That refusal is about moving a lane OUT of its worktree; this moves
    /// a plain lane IN, which it never covered.
    ///
    /// Returns the message the gate hands back to the agent: a REWRITE naming where the same file
    /// now lives, never a bare "no".
    /// </summary>
    (string Message, Action? Move) PromoteLane(Store.LaneRow row, RepoRef repo, string relForClaim, string deniedPath)
    {
        // NO SEED CLAIM ANY MORE (D-R5, R3), and this is the edge that decision was largely
        // about. Promotion used to open the ticket holding `path:<the file that was refused>` --
        // the only path it could possibly know, because a promotion happens on the FIRST write
        // and the agent has not said what else it intends to touch. The claim gate then bounded
        // the agent to that one file, so the SECOND file it needed was refused by the very
        // ticket that had just been created to unblock it. A promoted agent could write exactly
        // one file. Nothing declared here is nothing to be wrongly bounded by, and what the
        // branch actually touches is read from `git diff` when it matters (D-R7).
        var made = MakeTicket(repo, row.Title, "on-approval",
                              new List<(string, string)>(),
                              Array.Empty<string>());
        if (!made.Ok)
        {
            // THE PROMOTION CAN FAIL AT THE MOMENT IT IS NEEDED, and it must degrade to a refused
            // write naming the reason -- never to a silent allow (section 9). What can still fail
            // here is real: repo exclusivity, a repository with no commits, `git worktree add`.
            // A claim conflict is no longer among them and cannot be: overlap does not refuse.
            var why = (made.Error ?? "unknown").Replace("\n", " ").Trim();
            _store.Event("promotion_refused", row.Id, $"{deniedPath}: {why}");
            return ($"denied: {deniedPath} is in the SHARED CHECKOUT, not a worktree, and a private " +
                    $"checkout could not be opened for it: {why}. Nothing was written.", null);
        }

        _store.TicketSetLane(made.Id, row.Id);
        var moved = Path.Combine(made.Worktree, relForClaim.Replace('/', Path.DirectorySeparatorChar));

        // THE MOVE ITSELF IS BEHIND THE ANSWER, not in front of it. `git worktree add` is already
        // done (it has to be -- the message names the new path), but the RESPAWN kills this
        // agent's process, and it is the process currently waiting on this reply. So the reply is
        // written first by the caller and the respawn runs after it, fire-and-forget, the same
        // shape `BrainReview` uses for work that must not be in front of the operator (D-14).
        Action move = () => _ = Task.Run(async () =>
        {
            try
            {
                var cfg = ConfigForProject(Projects.Of(ProjectPaths(), made.Worktree) ?? repo.Path);
                // THE SAME BINARY IT WAS ALREADY RUNNING, not whatever the config names now.
                //
                // This read `cfg.Agent`, and the bug it caused is worth the whole comment: a lane
                // started with `--child <stand-in>` came back from promotion as `claude`. In m2 that
                // meant a REAL model process spawned inside a model-free suite -- and it then held
                // the new worktree as its working directory, so D-9's undo could not prune it and
                // git reported "Permission denied". One wrong line produced a quota leak and a
                // broken undo, and the undo failure is what surfaced it.
                //
                // Recovered from the lane's own `shim_spawned` record rather than from a column: a
                // schema bump for one field costs a migration and every older-store fixture has to
                // drop it (CLAUDE.md 0.2 carries that trap), which is more risk than this is worth.
                // The fallback is the config, which is the right answer for every lane that never
                // overrode its binary.
                var child = ChildOfLane(row.Id) ?? cfg.Agent;
                var args = new List<string>();
                if (IsClaude(child))
                {
                    var sys = TicketSystemPrompt(made.Id, row.Title, made.Branch, Config.For(_primary, repo.Path).IsPr);
                    args = ClaudeArgs(cfg, cfg.Model, cfg.Effort, sys, acceptEdits: true);
                    // THE SESSION IS THE POINT. Promotion is only free because `--resume` rebuilds
                    // the context the agent already has (spike 1: same id, no fork) -- without it
                    // the operator's sentence and everything the agent worked out would be gone,
                    // and a promotion that costs the conversation is worse than a refusal.
                    args.AddRange(Projects.ResumeArgs(row.Session));
                }
                // ASK IT TO GO, THEN WAIT FOR IT TO BE GONE. The pipe name is deterministic per
                // lane, so the new shim cannot own it until the old one has actually exited -- see
                // `WaitLaneProcessesGone` for what racing this produced.
                if (_lanes.TryGetValue(row.Id, out var old)) { old.Shutdown(); _lanes.TryRemove(row.Id, out _); }
                if (!WaitLaneProcessesGone(row.Id, 15000))
                    _store.Event("lane_promotion_slow_exit", row.Id,
                        "the previous shim did not exit in 15s; respawning anyway (the pipe name may still be held)");
                var (rid, rmsg) = await RespawnLaneAsync(row.Id, row.Title, args, child, made.Worktree);
                if (rid > 0)
                {
                    _store.LaneState(row.Id, "alive");
                    _store.LanePresence(row.Id, "idle");
                    _store.Event("lane_promoted", row.Id,
                        $"ticket {made.Id} branch {made.Branch} worktree {made.Worktree} session={row.Session ?? "-"}");
                    _store.PaneEvent(row.Id, "announcement",
                        $"moved into its own checkout for ticket {made.Id} — {made.Branch}, session resumed. " +
                        $"Undo: dodona lane-stop {row.Id} (abandons the ticket and prunes the worktree)",
                        null, null, acked: true);
                    Announce($"[dodona] {row.Title} tried to write in the shared checkout, so it now has ticket " +
                             $"{made.Id} and a checkout of its own ({made.Worktree}). Undo: dodona lane-stop {row.Id}");
                }
                else
                {
                    // The ticket and worktree exist and the agent is still where it was. Say so:
                    // the alternative is a lane that looks promoted and is not.
                    _store.Event("lane_promotion_respawn_failed", row.Id, $"ticket {made.Id}: {rmsg}");
                    Announce($"[dodona] {row.Title} has ticket {made.Id} and a worktree at {made.Worktree}, but could " +
                             $"not be moved into it: {rmsg}. It is still in the shared checkout and still refused there.");
                }
            }
            catch (Exception ex) { _store.Event("lane_promotion_failed", row.Id, $"{ex.GetType().Name}: {ex.Message}"); }
        });

        return ($"denied: {deniedPath} is in the SHARED CHECKOUT, which is nobody's workspace -- other lanes and " +
                $"your operator are working in it, and its pre-commit hook refuses commits from it. You now have " +
                $"ticket {made.Id} and a private checkout of your own, and your session is being resumed there. " +
                $"Write this file at {moved} instead. Nothing was written to the shared tree.", move);
    }

    /// <summary>Which OPEN ticket, if any, holds a path -- named so a layer-1 refusal can say
    /// who (D-13). The path is put back into workspace-relative claim terms with the same
    /// repository-then-project rungs `claim-check` uses, minus the worktree rung: the caller has
    /// already established this path is NOT in a worktree, which is why it is being refused.
    ///
    /// A store read and a string compare, so it costs nothing on the write path -- which matters,
    /// because this runs behind a PreToolUse hook.</summary>
    /// <summary>Undo a ticket that should never have existed: prune the worktree, delete the
    /// branch, mark it abandoned. Claims are released by the state change alone -- the conflict
    /// query in `Store.FindConflicts` only looks at `state = 'open'`.
    ///
    /// Reached from `lane-stop` for a PROMOTED lane (D-9). The prune is retryable and never silent,
    /// the same discipline `LandOp` uses: a worktree nobody removed is a checkout of an old commit
    /// sitting in the repository forever.</summary>
    List<string> AbandonTicket(Store.TicketRow t, string why)
    {
        var lines = new List<string>();
        _store.TicketState(t.Id, "abandoned");
        var repoPath = RepoOf(t)?.Path ?? (t.RepoPath.Length > 0 ? t.RepoPath : _primary);
        if (t.Worktree.Length > 0 && Directory.Exists(t.Worktree))
        {
            // THE AGENT HAS TO LET GO OF THE DIRECTORY FIRST, and this is measured rather than
            // guessed: `lane-stop` asks the shim to shut down and returns immediately, so the child
            // is still alive with the worktree as its WORKING DIRECTORY -- and Windows refuses to
            // delete a directory that is any process's cwd. git reported exactly that:
            // "failed to delete '...wt/t2': Permission denied", so the ticket was abandoned while
            // its worktree and branch survived. An undo that half-happens is the announcement
            // lying, which is what D-9 exists to prevent.
            //
            // A CONDITION WITH A DEADLINE, never a sleep (CLAUDE.md 0.1): what un-sticks it is the
            // shim's own exit, read off the OS from this lane's recorded pids. On timeout it falls
            // through and prints the manual command rather than parking -- nothing waits on a person.
            if (t.LaneId is long stopping) WaitLaneProcessesGone(stopping, 10000);
            var (wc, wOut) = Git.Run(repoPath, "worktree", "remove", "--force", t.Worktree);
            if (wc == 0)
            {
                // R7 / D-R28: DODONA NEVER DELETES A BRANCH IN A `delivery: pr` REPOSITORY, and
                // this is the site that matters — the land is the other one, and pr mode makes it
                // unreachable. Here the branch may already be pushed with a PR open on it, and an
                // abandon undoes DODONA'S ticket, not the project's work. The worktree still goes
                // (it is Dodona's, and a checkout of an old commit left behind for ever is what
                // the prune exists to prevent); the branch stays, and the receipt says so rather
                // than quietly reporting a deletion that did not happen.
                var prKeepsBranch = Config.For(_primary, repoPath).IsPr && t.Branch.Length > 0;
                if (t.Branch.Length > 0 && !prKeepsBranch) Git.Run(repoPath, "branch", "-D", t.Branch);
                _store.Event(prKeepsBranch ? "branch_kept_pr_mode" : "worktree_pruned", null, $"ticket {t.Id} abandoned: {why}");
                if (prKeepsBranch) _store.Event("worktree_pruned", null, $"ticket {t.Id} abandoned: {why}");
                lines.Add($"abandoned ticket {t.Id}: worktree pruned, " +
                          (prKeepsBranch ? $"branch {t.Branch} KEPT (delivery: pr — it may have a PR open on it)" : $"branch {t.Branch} deleted") +
                          ", claims released");
            }
            else
            {
                // WHAT HOLDS IT, not merely that it is held. Windows says "Permission denied" and
                // nothing about which process, and CLAUDE.md 0.3 is explicit that a message naming
                // the wrong cause sends the next reader hunting through their own code. The pids
                // come from this lane's own shim-info record (section 4: never by process name).
                var holders = t.LaneId is long hl
                    ? string.Join(", ", LaneLiveness.Records(Paths.WorkspaceDir(_instanceId))
                        .Where(r => r.Lane == hl)
                        .Select(r => $"shim {r.Shim}={(LaneLiveness.PidAlive(r.Shim, "DodonaShim") ? "alive" : "gone")} " +
                                     $"child {r.Child}={(LaneLiveness.PidAlive(r.Child, "") ? "alive" : "gone")}"))
                    : "no lane";
                _store.Event("worktree_prune_failed", null, $"ticket {t.Id}: {wOut} [{holders}]");
                lines.Add($"abandoned ticket {t.Id} and released its claims, but its worktree could not be pruned: {wOut.Trim()}");
                lines.Add($"       remove it by hand:  git -C \"{repoPath}\" worktree remove --force \"{t.Worktree}\"");
            }
        }
        else
        {
            _store.Event("ticket_abandoned", null, $"ticket {t.Id}: {why} (no worktree on disk)");
            lines.Add($"abandoned ticket {t.Id}: claims released");
        }
        Announce($"[dodona] ticket {t.Id} abandoned ({why}) — worktree and branch removed, claims released");
        return lines;
    }

}
