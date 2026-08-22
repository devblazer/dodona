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
    // ---------------------------------------------------------------- asking (P4.1/P4.5)

    /// <summary>
    /// `git init` plus the first commit, on ONE named project. Extracted from the `repo-init`
    /// case so that answering the repo question runs **the same code** the command runs
    /// (LOCATIONS-PLAN P4.3, D-L4: one answer path). A second copy behind the overlay is
    /// exactly the two-systems-one-tested divergence Phase 4 exists to prevent — and it would
    /// be the copy that ran `git init`, which is the one act here that nothing can undo.
    /// </summary>
    void RepoInitOp(string project, bool adopt, StreamWriter w)
    {
        var cfg = ConfigForProject(project);
        if (Git.IsRepo(project) && Git.HasCommit(project))
        { w.WriteLine($"error: {project} is already a git repository with commits"); return; }

        if (!Git.IsRepo(project))
        {
            var (ic, io) = Git.Run(project, "init", "-b", cfg.Main);
            if (ic != 0) { w.WriteLine($"error: git init failed: {io}"); w.WriteLine("##exit 1"); return; }
            w.WriteLine($"initialized empty repository on '{cfg.Main}'");
        }

        // Dodona's own state is never repo content: worktrees, the store and the
        // deployed gate files all live under .dodona/ and would otherwise be
        // committed by an agent's `git add -A` (the bug M1's test caught).
        var ignore = Path.Combine(project, ".gitignore");
        var ignoreText = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
        if (!ignoreText.Split('\n').Any(l => l.Trim() == ".dodona/"))
        {
            File.AppendAllText(ignore, (ignoreText.Length > 0 && !ignoreText.EndsWith("\n") ? "\n" : "") + ".dodona/\n");
            w.WriteLine("added .dodona/ to .gitignore");
        }

        if (!Git.HasCommit(project))
        {
            // An empty repo has no branch, so no worktree can be cut from it. What
            // goes into the first commit is the user's call, not ours: adopt takes
            // the files that are already here, otherwise the commit is empty and
            // they stay untracked.
            if (adopt) Git.Run(project, "add", "-A");
            var args = new List<string> { "commit", "-m", adopt ? "Initial commit" : "Initial commit (empty)" };
            if (!adopt) args.Insert(1, "--allow-empty");
            var (cc, co) = Git.Run(project, args.ToArray());
            if (cc != 0) { w.WriteLine($"error: initial commit failed: {co}"); w.WriteLine("##exit 1"); return; }
            w.WriteLine(adopt ? "committed the existing files as the initial commit" : "made an empty initial commit; existing files left untracked");
        }
        _store.Event("repo_init", null, $"{project} main={cfg.Main} adopt={adopt}");
        Announce($"[dodona] git repository ready on '{cfg.Main}' — tickets can branch now");
        w.WriteLine($"ready: {project} is a git repository on '{cfg.Main}'");
    }

    /// <summary>
    /// Answer one of this workspace's open questions. **This is THE answer path** — the
    /// `answer` command, the ask overlay's buttons and `dodona ui answer` all arrive here,
    /// which is what makes D-L4's "only pixels diverge" a fact about the code rather than a
    /// hope (P4.3). It mirrors <c>Concierge.Answer</c> deliberately, line for line where it
    /// can: same guard against answering twice, same "the row is the record" shape.
    ///
    /// An answer the question does not offer is REFUSED, not guessed. Asking exists because
    /// guessing was wrong; a fuzzy answer would reintroduce the guess at the one moment the
    /// operator had actually told us the truth.
    /// </summary>
    async Task<List<string>> AnswerQuestion(long id, string answer)
    {
        var lines = new List<string>();
        var q = _store.Question(id);
        if (q is null) { lines.Add($"error: no question {id}"); return lines; }
        if (q.State != "open") { lines.Add($"error: question {id} is already {q.State}"); return lines; }

        var choices = Ask.Choices(q.Candidates);
        var picked = Ask.Match(choices, answer);
        if (picked is null)
        {
            lines.Add($"error: \"{answer}\" is not one of the answers to question {id}" +
                      (choices.Count > 0 ? $" ({string.Join(" / ", choices.Select(c => c.Value))})" : ""));
            return lines;
        }

        // A ROUTE ANSWER IS RESOLVED BEFORE THE ROW IS CLOSED, and that ordering is the whole
        // guard. `QuestionAnswer` is guarded on `state='open'`, so there is no re-opening a
        // question -- and a route question closed without delivering loses the held sentence,
        // which is the one thing this rung exists to protect. Every other kind is safe to close
        // first because a failed action leaves nothing unrecoverable behind.
        string? answeredProject = null;
        if (q.Kind == Ask.KindRoute)
        {
            answeredProject = ProjectLadder.ByName(ProjectPaths(), picked.Value);
            if (answeredProject is null)
            {
                // Only reachable if the project was detached between the ask and the answer --
                // trap T4 arriving on the answer path. Say what un-sticks it and leave the
                // question open, so the sentence is still deliverable to a project that is here.
                lines.Add($"error: \"{picked.Label}\" is no longer a project of workspace {_wsName} " +
                          $"(projects here: {string.Join(", ", ProjectPaths().Select(ProjectLadder.Leaf))}) — " +
                          $"question {id} is still open; answer it with one of those");
                _store.Event("question_answer_refused", null, $"question {id} kind={q.Kind} answer={picked.Value}: project gone");
                return lines;
            }
        }

        // `withdrawn`, not `answered`, for a declined question: the two are different facts and
        // a later "why is there no repo" wants to know which one happened.
        //
        // A ROUTE QUESTION HAS NO DECLINATION, and excluding it is not tidiness: its choices are
        // project names, so a project in a folder called `no` would otherwise have a perfectly
        // good answer recorded as `withdrawn` and its sentence silently never delivered.
        var declined = q.Kind != Ask.KindRoute && picked.Value.Equals("no", StringComparison.OrdinalIgnoreCase);
        _store.QuestionAnswer(id, picked.Value, declined ? "withdrawn" : "answered");
        _store.Event("question_answered", null, $"question {id} kind={q.Kind} -> {picked.Value}");
        lines.Add($"answered: question {id} -> {picked.Label}");

        switch (q.Kind)
        {
            case Ask.KindRepoInit when !declined:
                // `adopt: true` because the files are already there — that is the whole shape of
                // this question. A GUI that made a git repo and then left the operator's own
                // files untracked would have answered a question they did not ask.
                foreach (var line in RepoInitLines(q.Subject)) lines.Add(line);
                break;
            case Ask.KindRepoInit:
                Announce("[dodona] no repo made — lanes keep working without git; only tickets need one");
                lines.Add("nothing was created; ask again by creating a ticket");
                break;
            // ROUTING'S RUNG 4 (LOCATIONS-PLAN P3.A, part 2). The sentence has been sitting in
            // `subject` undelivered since the ladder held it; the operator has now said where, so
            // deliver it — through `SpawnForAsync`, the ONE spawn path, with the answered project
            // forced past a ladder that has already admitted it does not know.
            //
            // THE LANE IS CREATED HERE AND NOWHERE EARLIER. `brain:held_input_invents_no_lane` and
            // `workspace:a_held_sentence_invents_no_lane` are the two checks that hold the other
            // half of it: holding invents nothing, and answering is what makes a lane exist.
            case Ask.KindRoute:
            {
                var (laneId, msg, choice) = await SpawnForAsync(q.Subject, null, null, answeredProject);
                if (laneId < 0) { lines.Add(msg); break; }
                // The routing row the hold could not write: it recorded tier `ask` with no lane,
                // and this is the same sentence finally reaching one. Two rows for one sentence is
                // the honest record — it WAS asked about, and it WAS then delivered.
                _store.RoutingInsert(q.Subject, "answered", laneId, laneId, "operator");
                lines.Add($"delivered to {msg} in {ProjectLadder.Leaf(answeredProject!)} " +
                          $"on {choice.Describe} — undo: dodona lane-stop {laneId}");
                break;
            }
            // THE OPERATOR'S YES ON A MERGE (R6, D-R11), and it is the one legitimate approval
            // path there is. `case "approve"` and this arm are the ONLY two callers of
            // `ApproveTicket`, and both of them are a person: D-R10 gives the manager the block
            // and keeps the bless, so nothing that is not a person may answer a question of kind
            // `land` — no timeout answers it, no default answers it, and `ManagerReview` has no
            // path to here (`brain:a_manager_approval_grants_nothing` is what goes red if one
            // ever appears).
            case Ask.KindLand when !declined:
            {
                if (!long.TryParse(q.Subject, out var ltid) || _store.Ticket(ltid) is not { } lt)
                {
                    // Only reachable if the ticket was deleted between the ask and the answer.
                    // Refusing beats approving something we cannot name.
                    lines.Add($"error: question {id} is about ticket '{q.Subject}', which this workspace does not have");
                    _store.Event("question_answer_refused", null, $"question {id} kind={q.Kind}: no such ticket");
                    break;
                }
                if (lt.State != "open")
                {
                    lines.Add($"ticket {ltid} is {lt.State} — there is nothing left to approve");
                    break;
                }
                ApproveTicket(ltid, $"answered question {id}");
                lines.Add($"approved ticket {ltid} — the merge can proceed (dodona token-request {ltid}, then dodona land {ltid})");
                break;
            }
            // "not yet" (R6). The row goes `withdrawn` above and the TICKET IS UNTOUCHED: the
            // agent keeps working, and the next completed turn that moves the worktree opens a
            // fresh question. That is what makes declining safe to offer — neither answer can
            // lose the ticket, and only one of them advances a ref.
            case Ask.KindLand:
                lines.Add($"not approved — ticket {q.Subject} stays open, and you are asked again when its work changes");
                break;
            // A kind with no case here answers the ROW and does nothing else, which is the right
            // default for a question that was only ever "tell me which one" — the caller reads the
            // answer off the row.
        }
        return lines;
    }

    /// <summary>
    /// Open (or re-find) the "this project has no git repo; create one?" question — P4.5.
    ///
    /// **Idempotent on purpose.** A ticket-create that is refused twice must not leave two
    /// identical open questions: the overlay renders one at a time, so the second would appear
    /// the instant the first was answered and read as the system not having listened. Existing
    /// open question of the same kind and subject wins, and its id is reported again.
    /// </summary>
    List<string> AskForRepo(string project, string forWhat)
    {
        var lines = new List<string>();
        var existing = _store.OpenQuestions()
            .FirstOrDefault(q => q.Kind == Ask.KindRepoInit &&
                                 q.Subject.Equals(project, StringComparison.OrdinalIgnoreCase));
        var (leaf, text) = RepoInitAsk(project, forWhat);
        var id = existing?.Id ?? _store.QuestionOpen(text, Ask.RepoInitCandidates(leaf), Ask.KindRepoInit, project);
        if (existing is null)
        {
            _store.Event("question_opened", null, $"question {id} kind={Ask.KindRepoInit} subject={project}");
            // The announcement is what puts it in the feed, which is where a person who closed
            // the window finds it again. It carries the command as well as the words, for the
            // same reason every other announcement does: the overlay is the fast path, not the
            // only one.
            Announce($"[dodona] {text} answer: dodona answer {id} yes|no");
        }
        lines.Add(text);
        lines.Add($"answer it in the window, or: dodona answer {id} yes   (or: dodona answer {id} no)");
        return lines;
    }

    /// <summary>
    /// Open (or re-find) the "which project is this sentence for?" question — LOCATIONS-PLAN
    /// P3.A, part 1. Returns its id, which every announcement and reply carries so the sentence
    /// can be released from anywhere.
    ///
    /// **The candidates are NAMES.** No paths reach the question row: §3.1 has no folder UI, and
    /// a routing question names projects rather than offering somewhere to browse. The answer
    /// comes back as a name and <see cref="ProjectLadder.ByName"/> resolves it against the
    /// projects this workspace still has.
    ///
    /// **`subject` is the held sentence, whole and untruncated**, because answering DELIVERS it.
    /// That is the one column that must survive verbatim; the `input` column is the question a
    /// person reads, so it is the one that gets shortened.
    ///
    /// **Idempotent on (kind, subject)**, for the reason <see cref="AskForRepo"/> is: the overlay
    /// renders one question at a time, so a second identical row would appear the instant the
    /// first was answered and read as the system not having listened. Two DIFFERENT held
    /// sentences are two genuine questions and do queue — oldest first, which is the order the
    /// uncertainty was created in.
    /// </summary>
    long AskWhichProject(string text, IReadOnlyList<string> candidates)
    {
        var existing = _store.OpenQuestions()
            .FirstOrDefault(q => q.Kind == Ask.KindRoute &&
                                 q.Subject.Equals(text, StringComparison.Ordinal));
        if (existing is not null) return existing.Id;
        var names = candidates.Select(ProjectLadder.Leaf).ToList();
        var id = _store.QuestionOpen(WhichProjectAskText(text),
                                     Ask.RouteCandidates(names), Ask.KindRoute, text);
        _store.Event("question_opened", null,
            $"question {id} kind={Ask.KindRoute} candidates={(names.Count == 0 ? "none" : string.Join(",", names))} " +
            $"subject={Truncate(text, 80)}");
        return id;
    }

    // ------------------------------------- the approval ask (R6, D-R11) --------------------

    /// <summary>
    /// THE OPERATOR'S YES, in one implementation. `dodona approve` and the approval ask both
    /// land here, so there is no second copy of "what approving does" to drift — and the ask
    /// answers through `MainWindow.AnswerAsk`, which is the same method a button click lands in
    /// (D-L4's one answer path), so a click and a verb and a typed command are three surfaces
    /// over one act.
    ///
    /// **THE CALLER LIST IS THE LOAD-BEARING PART, not the method** (D-R10). Approval advances a
    /// ref that has no undo, so both callers are a PERSON: the operator typing, and the operator
    /// answering. There is no timeout that answers a `land` question, no default, no
    /// auto-approve when the manager says `ok`, and no path from <see cref="ManagerReview"/> to
    /// here at all — a model as the sole gate on the irreversible step is *a prompt providing
    /// safety*, which `WORK-ISOLATION-PLAN` §2 forbids however the model is dressed. If a third
    /// caller ever appears, the question to ask of it is "is this a person?";
    /// `brain:a_manager_approval_grants_nothing` is what goes red when the answer is no.
    /// </summary>
    void ApproveTicket(long tid, string how)
    {
        _store.TicketApprove(tid);
        _store.Event("ticket_approved", null, $"ticket {tid}: {how}");
        // Unblock the lane: presence back to idle, receipt in the pane.
        if (_store.Ticket(tid)?.LaneId is long alid)
        {
            _store.LanePresence(alid, "idle");
            _store.PaneEvent(alid, "announcement", $"ticket {tid} approved — merge unblocked", null, null);
        }
        // An ask that is still standing has been SUPERSEDED rather than answered — the operator
        // said yes through the other surface. Answering through the ask itself has already
        // closed the row (`QuestionAnswer` is guarded on `state='open'`), so this is a no-op on
        // that path and a tidy-up on the other.
        _store.WithdrawQuestions(Ask.KindLand, tid.ToString(), $"approved: {how}");
    }

    /// <summary>
    /// Raise — or refresh — the one question that asks the operator to approve a ticket's merge
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` R6, D-R11; `WORK-ISOLATION-PLAN` D-7 and P5, which this
    /// absorbs). The write-up the manager wrote for a person is finally put in front of that
    /// person: approving becomes a two-second decision instead of a diff-reading session, which
    /// is the payoff for R4's record and R5's reviewer both.
    ///
    /// **IT MUST NOT DEPEND ON A REVIEW EXISTING, and that is the single most important
    /// property here.** Four ordinary things leave a ticket with no `manager_review` row:
    /// `DODONA_NO_AUTOSTART` (D-R17), `"brain": false` for the project, a cheap tier that timed
    /// out, and the send-back bound being spent (D-R18). If the ask only appeared when a note
    /// existed, approving a merge would be gated on a model having answered — judgement
    /// switched off would mean nothing could ever be merged, which is the fail-closed mirror of
    /// the trap D-R10 is about. So it is raised by THE RECORD EXISTING, from
    /// <see cref="BuildRecord"/>, and every no-review case renders as words that say so, over
    /// facts CODE knows: what changed, the verify state, the drop check, uncommitted work.
    ///
    /// **ONE ROW PER TICKET, REFRESHED IN PLACE** (`Store.QuestionUpsert`). The record arrives
    /// first and the review seconds later, so the text has to be able to change under a question
    /// already on screen; a second row would be a queue of overlays for one decision.
    ///
    /// **IT IS RAISED WHATEVER THE VERDICT, including a send-back.** The manager's objection is
    /// RENDERED, not enforced: blocking the agent is its job, and hiding the operator's own
    /// question behind its opinion would quietly promote it to the gatekeeper D-R10 says it may
    /// never be. The operator sees "sent this back, round 2 of 3: <why>" and decides.
    ///
    /// **NOTHING TO ASK IS A STATE, NOT A FAILURE.** An `auto` ticket needs no approval, an
    /// approved one has its answer, and a ticket that is no longer open has nothing to merge —
    /// each returns silently, because a question nobody can act on is worse than no question.
    /// </summary>
    long AskToLand(Store.TicketRow t, long laneId, string? recordJson)
    {
        var tid = t.Id;
        if (t.MergeMode != "on-approval") return 0;
        // RE-READ rather than trusting the row this was called with: the record is assembled off
        // the pipe and the review takes up to 25 s, and the operator can approve — or the land
        // can happen — in between. Asking to approve something already approved is the "outdated"
        // half of CLAUDE.md §0.1.
        var fresh = _store.Ticket(tid);
        if (fresh is null || fresh.State != "open" || fresh.Approved) return 0;

        // R7 / D-R28: IN A `delivery: pr` REPOSITORY THERE IS NOTHING HERE TO APPROVE, so nothing
        // is asked. `yes` means `Store.TicketApprove`, whose entire purpose is to unblock
        // `token-request` — and that command refuses outright in pr mode, so the question would be
        // offering a merge it cannot deliver. This method's own rule already decides it: nothing
        // to ask is a STATE, and a question nobody can act on is worse than no question.
        //
        // AND NOTHING REPLACES IT, WHICH IS THE PART WORTH DEFENDING (D-R29). The record is still
        // written and still readable (`dodona ticket-record`), the manager still reviews it and a
        // send-back still reaches the lane's own pane — none of that is approval machinery. What
        // goes is the one surface that existed only because Dodona held the merge. Announcing a
        // "ready for its PR" line instead was written and removed: `AskToLand` is called once by
        // the record and again by the review's `finally`, so it would be two pane lines per turn
        // for a decision Dodona is not part of — D-R18's never-stuck fix turning into never-quiet.
        if (RepoOf(fresh) is { } prRepo && Config.For(_primary, prRepo.Path).IsPr)
        {
            _store.Event("land_ask_skipped_pr_mode", laneId,
                $"ticket {tid}: {prRepo.Name} is delivery: pr — the forge's merge button is the gate, so there is no approval to ask for");
            return 0;
        }

        var (id, opened) = _store.QuestionUpsert(Ask.KindLand, tid.ToString(),
                                                 LandAskText(fresh, recordJson), Ask.LandCandidates(tid));
        if (!opened) return id;
        _store.Event("question_opened", laneId, $"question {id} kind={Ask.KindLand} subject={tid}");
        // ONCE, on opening, never on every refresh. The overlay is the fast path and the feed is
        // where somebody who closed the window finds it again — but a line per manager round
        // would be the never-stuck fix turning into never-quiet (D-R18's reasoning, one surface
        // over).
        //
        // AND IT ARRIVES ACKED, which is the deliberate half. A badge is a DEMAND for attention
        // (§8), and the demand here is the overlay itself — the feed line is the durable copy for
        // somebody who closed the window. The moment that genuinely earns a badge is the agent
        // BLOCKING on the token, which `token-request` still raises exactly as it did; two
        // unacked lines for one decision would be this phase making the feed noisier while
        // claiming to make deciding easier. `m3`'s badge checks are what would have gone red, and
        // they are right: they measure attention, not events.
        var text = $"ready to merge: ticket {tid} '{t.Title}' — answer it in the window, or: dodona answer {id} yes|no";
        if (t.LaneId is long qlid) _store.PaneEvent(qlid, "announcement", text, null, null, acked: true);
        else Announce($"[dodona] {text}");
        return id;
    }

    /// <summary>
    /// What the approval ask SAYS. The manager's `note` when there is one, and what code knows
    /// when there is not — never an empty box, and never a blank where a reason belongs
    /// (`land_drop_check_moot` and R4's `verify: not-run` are the pattern being copied).
    ///
    /// **WHICH EVENT IS NEWEST IS HOW IT KNOWS WHETHER THE REVIEW IS THIS TURN'S.** One query
    /// over the record kind AND every review-outcome kind, newest by id: if the record is on top,
    /// no review has come back for it yet, and showing the PREVIOUS turn's note against new work
    /// would be a write-up about a diff that no longer exists. That is a fact about ordering
    /// rather than a guess, which is what `Store.LastTicketEvent` is for.
    ///
    /// Bounded, because this is a paragraph in an overlay and not a report: three file names,
    /// the manager's note as written (R5 already caps it at 240), and the whole thing truncated.
    /// </summary>
    string LandAskText(Store.TicketRow t, string? recordJson)
    {
        // THE SECOND CALLER HANDS NOTHING (`token-request`'s unapproved refusal), so the record
        // is looked up. There may not be one: `completion_record_impossible` is a real state — a
        // ticket can outlive its worktree — and a ticket can also ask for the token before any
        // turn of its has ended. Either way the question is legitimate and must be asked; what it
        // must NOT do is print "0 files" and pass an absence off as a measurement.
        recordJson ??= _store.LastTicketEvent(t.Id, "completion_record") is { Detail: string rd } &&
                       rd.IndexOf('{') is int b && b >= 0 ? rd[b..] : null;
        // THE BOUND IS A COUNT, NOT AN EVENT ORDERING (D-R12/D-R18), and asking it FIRST is a
        // correction rather than a tidy-up: it was written as one more arm of the switch below
        // and `brain` caught it. Past the bound no further review will ever run, so any later
        // turn's record lands on top of `manager_bound_reached` and the ask reverted to "not
        // reviewed yet" — permanently, for a ticket that is precisely the one the operator has
        // been handed. Counted in the store for the same reason the bound itself is: a daemon
        // restarts on every publish.
        //
        // THESE ARE THE STORE READS, AND THEY STAY HERE (seam, `docs/TEST-ARCHITECTURE-PLAN.md`
        // §4/W8): everything below the call is a paragraph assembled out of them, and it is the
        // paragraph the `ui-ask` checks are about. `priorReviews` is read eagerly where the
        // switch below used to read it lazily in one arm — one indexed COUNT per turn-end, and
        // the words it produces are unchanged.
        return LandAskText(t.Id, t.Title, recordJson,
                           _store.CountTicketEvents(t.Id, "manager_sent_back"),
                           _store.LastTicketEvent(t.Id, "completion_record", "manager_review",
                                                  "manager_review_skipped", "manager_review_failed"),
                           _store.CountTicketEvents(t.Id, "manager_review"));
    }

    /// <summary>The words themselves, over facts a caller has already read. See the instance
    /// overload above for what each one is and why it is read where it is.</summary>
    internal static string LandAskText(long ticketId, string ticketTitle, string? recordJson,
                                       int rounds, (string Kind, string Ts, string Detail)? last, int priorReviews)
    {
        var files = 0; var uncommitted = 0;
        var verify = "not-run"; var drop = "moot";
        var names = new List<string>();
        var haveRecord = false;
        try
        {
            if (recordJson is null) throw new JsonException("no completion record");
            using var d = JsonDocument.Parse(recordJson);
            var r = d.RootElement;
            if (r.TryGetProperty("files", out var f) && f.TryGetInt32(out var fi)) files = fi;
            if (r.TryGetProperty("uncommitted", out var u) && u.TryGetInt32(out var ui)) uncommitted = ui;
            if (r.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array)
                names = ch.EnumerateArray().Take(3).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            if (r.TryGetProperty("verify", out var v) && v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty("state", out var vs)) verify = vs.GetString() ?? "not-run";
            if (r.TryGetProperty("drop", out var dp) && dp.ValueKind == JsonValueKind.Object &&
                dp.TryGetProperty("state", out var ds)) drop = ds.GetString() ?? "moot";
            haveRecord = true;
        }
        // A record we cannot parse still gets an ask: the facts line just says less. Refusing to
        // ask because a field was unreadable would make the merge unapprovable from the window.
        catch (JsonException) { }

        var facts = new List<string>
        {
            haveRecord ? files == 1 ? "1 file" : $"{files} files"
                       : "no completion record for it yet — the agent has asked for the merge token",
        };
        if (haveRecord && names.Count > 0) facts[0] += $" ({string.Join(", ", names)}{(files > names.Count ? ", …" : "")})";
        if (haveRecord) facts.Add($"verify {verify}");
        // `moot` and `clean` are the ordinary values and reading them out would be noise; a real
        // DROP is the one thing on this line a person must not skim past (D-R4).
        if (drop == "dropped") facts.Add("IT RESOLVED BY DISCARDING SOMETHING MAIN CHANGED");
        if (uncommitted > 0) facts.Add($"{uncommitted} uncommitted");

        var review = rounds >= SendBackBound
            ? $"the manager sent this back {rounds} times, which is the bound — it is yours to judge now"
            : last?.Kind switch
            {
                "manager_review" => ReviewLine(last.Value.Detail),
                "manager_review_skipped" => $"no review ran ({Tail(last.Value.Detail)})",
                "manager_review_failed" => $"the review did not finish ({Tail(last.Value.Detail)})",
                // The record is on top, so no review has come back for THIS change yet. Showing
                // the previous turn's note here would be a write-up about a diff that is gone.
                _ => priorReviews > 0
                        ? "the manager has not reviewed this latest change yet"
                        : "no review has run",
            };

        return Truncate($"ticket {ticketId} \"{ticketTitle}\" is ready to merge — {string.Join(", ", facts)}.\n" +
                        $"{review}\nApprove the merge?", 700);

        // The manager's own words. A verdict with no note is reported AS a verdict with no note:
        // D-R11 says the write-up is the point, so its absence is worth a person seeing.
        static string ReviewLine(string detail)
        {
            var brace = detail.IndexOf('{');
            if (brace < 0) return "a review ran; its row could not be read";
            try
            {
                using var d = JsonDocument.Parse(detail[brace..]);
                var r = d.RootElement;
                string S(string n) => r.TryGetProperty(n, out var x) ? x.ToString() : "";
                var sentBack = S("verdict") == "send-back";
                // R8/D-R24 AT THE SURFACE THE OPERATOR READS. "Round 2 of 3" for an objection
                // that spent no round would misreport how many chances are left, on the one
                // screen where that number is being used to make a decision.
                var exempt = r.TryGetProperty("exempt", out var ex) && ex.ValueKind == JsonValueKind.True;
                var where = sentBack
                    ? exempt ? $"sent this back because the verify on record is RED, which spends none of its {S("bound")} rounds"
                             : $"sent this back, round {S("round")} of {S("bound")}"
                    : "raised no objection";
                // D-R23: an escape hatch nobody can see being used is one nobody can judge. It
                // is a suffix rather than a fact on the first line because it is about how the
                // review was reached, not about what the operator has to decide.
                var read = r.TryGetProperty("details", out var dt) && dt.ValueKind == JsonValueKind.Array && dt.GetArrayLength() > 0
                    ? $" (it asked to read {string.Join(", ", dt.EnumerateArray().Select(x => x.GetString()))})"
                    : "";
                var note = S("note");
                return note.Length > 0 ? $"the manager {where}: {note}{read}"
                                       : $"the manager {where} and left no note{read}";
            }
            catch (JsonException) { return "a review ran; its row could not be read"; }
        }

        // `ticket 7: <why>` -> `<why>`. The ticket number is already the first word of the ask.
        static string Tail(string detail)
        {
            var colon = detail.IndexOf(':');
            return Truncate(colon >= 0 ? detail[(colon + 1)..].Trim() : detail.Trim(), 200);
        }
    }

    /// <summary>`RepoInitOp` writes to a pipe; an answer needs its words as a list. One buffer
    /// rather than two implementations — the alternative was a second repo-init, which is the
    /// thing P4.3 forbids.</summary>
    List<string> RepoInitLines(string project)
    {
        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms) { AutoFlush = true };
        RepoInitOp(project, adopt: true, sw);
        ms.Position = 0;
        using var sr = new StreamReader(ms);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (!line.StartsWith("##")) lines.Add(line);
        return lines;
    }

}
