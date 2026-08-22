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
    /// A turn ended on a work lane holding an open ticket, so the ticket gets a PR-shaped record
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` D-R8). Assembled by CODE and carrying NO OPINIONS: the
    /// ticket, its branch and worktree, what the branch changed, the verify result, the
    /// silent-drop check, and **the agent's own end-of-turn report** -- which is the closest
    /// thing this system has to a PR description and the one thing the manager has never once
    /// been shown (§1: `BrainReview` fires at lane creation and never sees a lane agent's output).
    ///
    /// **It writes no judgement and it decides nothing.** The manager reading it is R5; the
    /// operator's `approve` is still the only yes (§6, D-R10). R4 is the assembly.
    ///
    /// **THE VERIFY RESULT IS REPORTED, NEVER RUN (D-R15).** This is the phase's one real design
    /// decision, so it is written out in the plan rather than left implicit here. In short: a
    /// record assembled on the LAND path would be produced after the approval it exists to
    /// inform, so completion is the only moment it can change anything; and a verify run *here*
    /// would cost a build plus suites per completed turn (quota and wall clock, CLAUDE.md §0.1)
    /// to answer a different question from D-R1's -- this branch has not had main merged into it,
    /// so a green here says nothing about the tree that would land while reading as though it
    /// did. So the slot carries the newest verify already recorded for the ticket, and says
    /// `not-run` in as many words when there is none.
    ///
    /// **The drop check DOES run here**, because it is pure git -- `MainMergeOnBranch` plus
    /// `SilentDrops`, no build and no test. Until a land has merged main in there is nothing for
    /// the branch to have discarded, and that is `moot`: a real state, said out loud, and not the
    /// same thing as a check that failed to run. `land_drop_check_moot` is the pattern.
    ///
    /// **Gated on the worktree having CHANGED since the last record (D-R13).** A `result` is the
    /// end of a turn, not of the conversation (`LANE-LIFECYCLE.md` §2 -- "the agent said it was
    /// done" is turn-completion), so a chatty lane must produce ONE record and not one per turn.
    /// The digest is the branch tip plus a hash of `git status --porcelain`: committed *and*
    /// uncommitted work, because a turn that edited without committing has changed the worktree
    /// and a reviewer wants to know it (the land refuses a dirty worktree outright).
    ///
    /// **NO PANE ROW, DELIBERATELY.** A record is a machine-shaped artifact for a reviewer, and
    /// an announcement per completed turn would put a JSON blob in the operator's pane and press
    /// on the badge -- while §4's rule is that attention is owed when a person is NEEDED, and
    /// nobody is needed by a record. It reaches people through R6's write-up in the approval ask;
    /// until then `dodona ticket-record &lt;ticket&gt;` reads it, which is also what makes it
    /// reachable from a check at all (CLAUDE.md §3.1: an affordance no verb can reach is where
    /// the next defect lives).
    ///
    /// Every giving-up path below records WHY. An empty record, or a silent return where a record
    /// was expected, is the fail-open this codebase has paid for twice (§3's dead routing ladder,
    /// `GateHook`'s BOM) -- so "there is nothing to record" and "the record could not be built"
    /// are different events with different names.
    /// </summary>
    void CompletionRecord(long laneId, long paneEventId, string body)
    {
        // A plain lane's turn is the overwhelmingly common case and there is no PR to shape, so
        // it is silent rather than event-per-turn noise. Every case PAST this point is a ticket
        // lane, where saying nothing would be indistinguishable from being broken.
        var t = _store.Tickets().FirstOrDefault(x => x.State == "open" && x.LaneId == laneId);
        if (t is null) return;

        // Off the pump thread: this shells out to git several times, and the pump is what
        // delivers the agent's output to the pane. Nothing here is ever awaited by anybody.
        _ = Task.Run(() =>
        {
            try { BuildRecord(t, laneId, paneEventId, body); }
            catch (Exception ex)
            {
                _store.Event("completion_record_failed", laneId, $"ticket {t.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    void BuildRecord(Store.TicketRow t, long laneId, long paneEventId, string body)
    {
        var tid = t.Id;
        // Resolved the same way the land resolves them, and for the same reason: a record that
        // named a different repository from the one the land will fast-forward would be a report
        // about a tree nobody is going to ship (P0.1's wrong-main incident, one step upstream).
        var repo = RepoOf(t);
        if (repo is null)
        {
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
            return;
        }
        var cfg = Config.For(_primary, repo.Path);

        // NO WORKTREE, NO RECORD -- and it says so. `git diff --stat main...branch` would still
        // answer from the shared checkout, so this is a place where a plausible-looking record
        // could be assembled about a tree the agent is not standing in. A ticket can legitimately
        // outlive its checkout (the land carries the same case), so this is a state and not an
        // error; what it is not is something to paper over.
        if (t.Worktree.Length == 0 || !Directory.Exists(t.Worktree))
        {
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: no worktree at '{t.Worktree}' -- nothing to diff or digest");
            return;
        }

        var (headCode, head) = Git.Run(t.Worktree, "rev-parse", "HEAD");
        var (statusCode, porcelain) = Git.Run(t.Worktree, "status", "--porcelain");
        if (headCode != 0 || statusCode != 0)
        {
            // git itself could not answer, so there is no digest and therefore no way to honour
            // D-R13 either. Refusing beats writing a record whose gate is a guess.
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: git could not read the worktree (rev-parse={headCode} status={statusCode}) at {t.Worktree}");
            return;
        }
        var digest = Digest(head + "\n" + porcelain);
        // The record's own JSON, set inside the lock and read after it -- see the comment on
        // the `ManagerReview` call below for why R5 fires from OUTSIDE the lock and not inside.
        string? written = null;

        lock (_recordLocks.GetOrAdd(tid, _ => new object()))
        {
            // D-R13's gate. The previous record is read back out of its own event rather than
            // held in memory: a daemon restarts on every publish, and an in-memory digest would
            // make the first turn after every restart produce a duplicate record -- which is the
            // "outlives its reason" failure in reverse, a gate that quietly stops gating.
            var prev = _store.LastTicketEvent(tid, "completion_record");
            if (prev is { Detail: string pd } && DigestOf(pd) == digest)
            {
                _store.Event("completion_record_unchanged", laneId,
                    $"ticket {tid}: worktree unchanged since the last record ({digest}) -- one record per change, not per turn (D-R13)");
                return;
            }

            // What the branch changed. THE THREE-DOT FORM IS ALREADY MERGE-BASE-RELATIVE:
            // `git diff A...B` diffs from the merge base of A and B to B, which is precisely
            // D-R8's `<merge-base>...<branch>`. §10's merge-base trap is about the DROP check --
            // where the reference point has to survive main having been merged in -- and does not
            // apply here: before that merge this is the fork-point diff, and after it, it is the
            // branch's net contribution over main's tip. Both are what a PR shows.
            var range = $"{cfg.Main}...{t.Branch}";
            var (dsCode, diffstat) = Git.Run(t.Worktree, "diff", "--stat", range);
            var (nmCode, names) = Git.Run(t.Worktree, "diff", "--name-only", range);
            var changed = nmCode == 0
                ? names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
                : new List<string>();

            // The silent drop (D-R4), run here because it is free. `moot` until a land has
            // merged main in; meaningful from the second land attempt onward, which is exactly
            // the D-R3 flow -- the land refuses on a conflict, the agent resolves and commits,
            // the turn ends, and this is the record the manager reads BEFORE the next land.
            var (mergeCommit, preMerge) = MainMergeOnBranch(t.Worktree, cfg.Main, t.Branch);
            var drops = mergeCommit.Length == 0 ? new List<string>() : SilentDrops(t.Worktree, preMerge, mergeCommit, t.Branch);
            var dropState = mergeCommit.Length == 0 ? "moot" : drops.Count == 0 ? "clean" : "dropped";

            // D-R15: reported, never run. `not-run` is a value here, not an omission.
            var v = _store.LastTicketEvent(tid, "verify_green", "verify_red");
            var verifyState = v?.Kind switch { "verify_green" => "green", "verify_red" => "red", _ => "not-run" };

            var record = new
            {
                ticket = tid,
                title = t.Title,
                branch = t.Branch,
                worktree = t.Worktree,
                repo = t.Repo,
                main = cfg.Main,
                head,
                digest,
                row = paneEventId,          // the transcript row the report came from, for R6
                range,
                files = changed.Count,
                changed = changed.Take(60).ToList(),
                diffstat = dsCode == 0 ? diffstat : $"(git diff --stat failed: {diffstat})",
                uncommitted = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                verify = new
                {
                    state = verifyState,
                    when = v?.Ts ?? "",
                    // D-R15 in one sentence, IN the record, because the record is what a reviewer
                    // reads and "verify: not-run" with no reason invites someone to add a verify
                    // run here.
                    detail = v?.Detail ?? "no verify has run for this ticket; the one that gates is the land's own, on the merged result (D-R15)",
                },
                drop = new { state = dropState, files = drops, merge = mergeCommit, preMerge },
                // The agent's own words, whole unless they are enormous. This is the field that
                // did not exist anywhere before R4.
                report = Truncate(body, 4000),
            };
            var json = JsonSerializer.Serialize(record);
            // `ticket <id> {json}` -- the house shape for a ticket event (`Store.LastTicketEvent`
            // matches on it), with the JSON starting at the first brace.
            _store.Event("completion_record", laneId, $"ticket {tid} {json}");
            written = json;
        }

        // R5 FIRES FROM HERE, AND BOTH HALVES OF THAT ARE DELIBERATE.
        //
        // OUTSIDE THE LOCK, and on a task of its own -- belt and braces, because either one
        // alone is easy to lose in a later edit. `_recordLocks` is per TICKET and is held above
        // across read-decide-write; the manager review is a model call with a 25 s timeout, so
        // firing it inside would hold one ticket's lock across that call and serialise every
        // later turn of the same ticket behind a manager thinking.
        //
        // TRIGGERED BY THE RECORD EXISTING, not by a third consumer of `rt.OnResult`.
        // `HookTurnEnd` is an ASSIGNMENT with two consumers already (plan §10), and a review
        // wired there would have to re-derive D-R13's gate to know whether anything was new to
        // review. The record IS that answer: it exists exactly when the worktree moved.
        //
        // AND THE ASK IS RAISED AFTER THE REVIEW IS FIRED, NEVER BEHIND WHETHER IT ANSWERS (R6,
        // D-R11). Both calls are unconditional on the record existing and neither is conditional
        // on the other's outcome: `ManagerReview` returns immediately in every case (its work is
        // on a task, and its one synchronous path is the `DODONA_NO_AUTOSTART` skip), and the ask
        // then renders whatever the store now says — including "no review ran (...)" when that
        // skip is what just happened. Ordering them the other way would leave that case reading
        // "no review has run" for ever, and making the ask wait for a verdict would gate the
        // operator's approval on a model having answered, which is the one thing R6 may not do.
        // Each is in its OWN try: the record is already written, and neither the reviewer nor the
        // question is allowed to take the other down.
        if (written is not null)
        {
            try { ManagerReview(t, laneId, repo.Path, written); }
            catch (Exception ex) { _store.Event("manager_review_failed", laneId, $"ticket {tid}: firing the review threw: {ex.Message}"); }
            try { AskToLand(t, laneId, written); }
            catch (Exception ex) { _store.Event("land_ask_failed", laneId, $"ticket {tid}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>The D-R13 gate's value: 16 hex over the branch tip plus the porcelain status, so
    /// committed and uncommitted work both move it. Short because it is read by people in event
    /// details, and a full SHA256 in a log line is noise.</summary>
    static string Digest(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>The digest out of a stored record's detail, or "" when it cannot be read. A
    /// record whose digest is unreadable must compare UNEQUAL, so the next turn writes a fresh
    /// record rather than skipping on a value nobody could parse -- one duplicate record is a
    /// cost, a gate that silently swallows every completion is a phase that does nothing.</summary>
    static string DigestOf(string detail)
    {
        var brace = detail.IndexOf('{');
        if (brace < 0) return "";
        try
        {
            using var d = JsonDocument.Parse(detail[brace..]);
            return d.RootElement.TryGetProperty("digest", out var g) ? g.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    // ------------------------------------------ the manager's review (R5, D-R9/D-R10/D-R12)

    /// <summary>How many times one ticket may be sent back before it goes to the operator
    /// regardless (D-R12). An unbounded send-back loop is CLAUDE.md §0.1's *never stuck*
    /// violated in a costume where everyone is being reasonable.</summary>
    const int SendBackBound = 3;

    /// <summary>D-R23's caps, and they are the difference between an escape hatch and the
    /// expensive reviewer arriving through the door marked cheap. Three files, because a
    /// request that can widen to everything is a full diff read with extra steps; and a byte
    /// budget as well as a file count, because one generated file is a whole diff by itself.
    /// Both are enforced in <see cref="GrantDetails"/> against the record's own changed list —
    /// in CODE, never by the prompt asking nicely.</summary>
    const int DetailsFileCap = 3;
    const int DetailsBytesPerFile = 6000;
    const int DetailsBytesTotal = 12000;

    /// <summary>
    /// The manager reads R4's completion record and MAY SEND THE WORK BACK
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` D-R9). This is the chair R3 left empty: the file
    /// reservations were retired on the operator's reasoning that *"if that is problematic in
    /// some way, it's the manager's job to say something about it"*, and until this method
    /// existed `claim_overlap` and `branch_touched` were recorded and nothing read either.
    ///
    /// **IT CAN BLOCK, BUT IT CANNOT BLESS (D-R10) — the load-bearing rule of the whole plan.**
    /// There is deliberately no path from here to `Store.TicketApprove`, and `case "approve"`
    /// stays reachable only from the operator's own `approve` / `dodona ui answer`. Rejection is
    /// free and reversible — worst case it costs a round; approval advances a ref that has no
    /// undo. A model as the sole gate on that step is *a prompt providing safety*, which
    /// `WORK-ISOLATION-PLAN` §2 forbids and which this phase is not allowed to reintroduce just
    /// because the model is called a manager. So the schema offers `ok | send-back` and nothing
    /// else, **anything that is not literally `send-back` is read as no objection**, and `ok`
    /// grants nothing — `brain:a_manager_approval_grants_nothing` asks for `approve` on purpose
    /// and watches it change nothing at all.
    ///
    /// **BOTH ENDS ARE BOUNDED (D-R12).** Reading: the diffstat, the changed-file NAMES and the
    /// agent's own report, on the cheap tier, escalating to the expensive one only when the
    /// cheap tier says its own confidence is low — the pattern `BrainReview` already uses. Never
    /// the diff CONTENT, which plan §9 rejects by name: a reviewer that reads every diff in full
    /// is a reviewer that cannot be afforded (CLAUDE.md §0.1). Loop: three send-backs, then the
    /// ticket goes to the operator with the history attached and no fourth model call.
    ///
    /// **THE BOUND IS COUNTED IN THE STORE, NEVER IN MEMORY** (`Store.CountTicketEvents`). A
    /// daemon restarts on every publish, so a field or a counter would reset the bound at
    /// exactly the moment three rounds have gone by — the same reason R4 reads its previous
    /// digest back out of its own event, and §3's dead routing ladder wearing a third costume.
    /// `brain` restarts the daemon between round two and round three for that reason and no
    /// other.
    ///
    /// **A SEND-BACK CANNOT REVIEW ITSELF, and D-R13's gate is what guarantees it.** The
    /// send-back is delivered with `Say`, which starts a turn, which ends, which arrives back at
    /// `CompletionRecord`. That turn has not moved the worktree, so the digest matches, so there
    /// is no record and therefore no second review. The loop terminates on a fact rather than on
    /// a model choosing to stop — which is what "bounded" has to mean here.
    /// </summary>
    void ManagerReview(Store.TicketRow t, long laneId, string project, string recordJson)
    {
        var tid = t.Id;
        // THE DAEMON ACTING ON ITS OWN INITIATIVE, not on operator input — so it honours the same
        // guard as the startup warm-up, the drift watcher and `EnsureRouterAsync`, and all four
        // now agree on what "do not start things by yourself" means. This is not a test hook:
        // without it every model-free suite that finishes a ticket turn would spawn a real
        // `claude -p --model haiku`, because a fixture with no `agent` in its dodona.json gets
        // the real CLI by default (m1 is exactly that) — the one thing a model-free suite may
        // never do (CLAUDE.md §3.2's incident). The operator never sets it; `brain-acceptance`
        // clears it for this phase's checks, the way it already does for the classifier's.
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1")
        {
            _store.Event("manager_review_skipped", laneId,
                $"ticket {tid}: DODONA_NO_AUTOSTART=1, so this daemon starts no judgement agent of its own");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // ALREADY AT THE BOUND: no model call, and the operator hears about it ONCE.
                // D-R12's "then it goes to the operator regardless" is not "then it asks a
                // fourth time", and an announcement on every later turn would be the never-stuck
                // fix turning into never-quiet — so the announcement is gated on its own event,
                // in the store, the same way the bound itself is counted.
                var rounds = _store.CountTicketEvents(tid, "manager_sent_back");
                if (rounds >= SendBackBound)
                {
                    if (_store.CountTicketEvents(tid, "manager_bound_reached") == 0)
                    {
                        var history = SendBackHistory(tid);
                        _store.Event("manager_bound_reached", laneId,
                            $"ticket {tid}: {rounds} send-backs is the bound (D-R12) — to the operator, history: {Truncate(history, 900)}");
                        Announce(t, $"manager review: ticket {tid} '{t.Title}' has been sent back {rounds} times, which is the bound — " +
                                    $"it is yours to judge now. What the manager asked for, in order: {Truncate(history, 600)}");
                    }
                    return;
                }

                // ENSURE AT THE POINT OF USE, NEVER LOOK UP (CLAUDE.md §3). A lookup that misses
                // is indistinguishable from one that was never going to hit, which is how the
                // routing ladder stayed fully green and dead in production for two days.
                var loId = await EnsureBrainAsync(hi: false, project);
                if (loId < 0)
                {
                    // A review that could not run SAYS SO. A check that quietly does nothing is
                    // worse than no check, and "judgement is switched off for this project" must
                    // not look identical to "the review is broken" from the store
                    // (`completion_record_impossible` is the pattern being copied).
                    _store.Event("manager_review_skipped", laneId,
                        $"ticket {tid}: no judgement agent for {project} — brain off in dodona.json, failed to start, or the maxBrains cap");
                    return;
                }

                // D-R23'S ESCAPE HATCH, AND ITS "ONCE" IS A FLAG HERE RATHER THAN A SENTENCE
                // IN THE PROMPT. The reviewer may say "I need to see Store.cs before I can judge
                // this" and get that one file. What it may never do is ask, read and ask again,
                // which is D-R12's send-back loop one level down wearing the same clothes.
                //
                // THE TWO TIERS SHARE THE ROUND. Whichever asks first spends it and buys the
                // files; the other reads what was bought rather than shopping again. So the
                // worst case is three model calls for one review (a tier that asks, plus an
                // escalation) and the ordinary case is still exactly ONE — which is the whole
                // point, because every one of these is per finished turn, per ticket, and that
                // is the cost the operator's 2026-08-21 directive is about.
                var granted = new List<string>();
                var refused = new List<string>();
                var detailsWhy = "";
                var detailsSpent = false;
                string? details = null;

                async Task<JsonElement?> AskTier(bool hi)
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var q = ManagerQuestion(t, recordJson, rounds, details);
                        JsonElement? asked;
                        if (hi) asked = await AskBrainHiAsync(q, project);
                        else
                        {
                            var gate = BrainLock(loId);
                            await gate.WaitAsync();
                            string? reply;
                            try { reply = await _lanes[loId].AskAsync(q, 25000); }
                            finally { gate.Release(); }
                            if (reply is null)
                            {
                                _store.Event("manager_review_failed", laneId, $"ticket {tid}: the cheap tier did not answer in 25s");
                                return null;
                            }
                            try { asked = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]).RootElement.Clone(); }
                            catch
                            {
                                _store.Event("manager_review_failed", laneId, $"ticket {tid}: unparseable reply: {Truncate(reply, 160)}");
                                return null;
                            }
                        }
                        if (asked is null) return null;
                        // THE SECOND REPLY STANDS AS IT IS. A request arriving on the details
                        // pass, or after the round has been spent by the other tier, is simply
                        // not read — and since anything that is not `send-back` is no objection
                        // (D-R10), a reviewer that asked instead of judging costs the agent
                        // nothing. That is the failure mode this bound is chosen to have.
                        if (attempt == 1 || detailsSpent) return asked.Value;
                        var want = Requested(asked.Value);
                        if (want.Count == 0) return asked.Value;
                        detailsSpent = true;
                        detailsWhy = asked.Value.TryGetProperty("needWhy", out var wy) ? Truncate(wy.GetString() ?? "", 200) : "";
                        details = GrantDetails(t, recordJson, want, granted, refused);
                        // RECORDED — D-R23's third property, and the refusals with it. The only
                        // way anyone ever finds out whether this hatch is judgement or habit is
                        // by counting rows, and a refusal that left no row would make "it never
                        // asks for the whole diff" an argument instead of a query.
                        _store.Event("manager_details_granted", laneId, $"ticket {tid} " + JsonSerializer.Serialize(new
                        {
                            ticket = tid, tier = hi ? "hi" : "lo", granted, refused, why = detailsWhy,
                        }));
                        if (details is null) return asked.Value;   // nothing survived the narrowing
                    }
                    return null;
                }

                var first = await AskTier(hi: false);
                if (first is null) return;                 // the tier said so in its own event
                var v = first.Value;

                // Cheap tier unsure -> the SAME question, expensive tier (D-R12's bound on
                // reading, and the operator's rule #1). Which tier answered is a FIELD of the
                // one review row rather than an event of its own: one review, one row, and R6
                // reads that row.
                var conf = v.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                var tier = "lo";
                if (conf == "low")
                {
                    var hiV = await AskTier(hi: true);
                    if (hiV is not null)
                    {
                        v = hiV.Value;
                        tier = "hi";
                        conf = v.TryGetProperty("confidence", out var cf2) ? cf2.GetString() ?? "low" : "low";
                    }
                }

                // D-R10 IN ONE LINE: `send-back` is the only verdict that does anything. There is
                // no branch below this that grants, approves or advances anything, and a reply of
                // `{"verdict":"approve"}` lands here as no objection.
                var verdict = v.TryGetProperty("verdict", out var vd) ? vd.GetString() ?? "ok" : "ok";
                var sendBack = verdict == "send-back";
                var note = v.TryGetProperty("note", out var nt) ? Truncate(nt.GetString() ?? "", 240) : "";
                var message = v.TryGetProperty("message", out var mg) ? Truncate(mg.GetString() ?? "", 1200) : "";

                // D-R24: A MECHANICAL OBJECTION IS NOT A STRIKE — AND CODE DECIDES THAT IT WAS
                // ONE. R5's objection to this decision was never overturned; it is what fixes
                // the implementation. *An exemption the model classifies is a bound the model
                // can talk its way out of*, so the model classifies nothing: the verify state is
                // already a code fact in R4's record (`verify_green` / `verify_red`, written by
                // `LandFlow`, reported and never re-run — D-R15), and the exemption keys on what
                // the RECORD says at the moment of this review. A reviewer claiming it only
                // objected because the tests were red earns exactly nothing by saying so.
                //
                // `not-run` IS NOT RED, and that is the whole load-bearing half of this. It is
                // the NORMAL value — no verify has run for most tickets, by design — so treating
                // it as red would exempt every send-back there has ever been and the bound would
                // simply stop existing.
                //
                // ONE EXEMPTION PER VERIFY RESULT (D-R26), keyed on the verify event's own
                // timestamp. The first mechanical objection carries information the agent can
                // act on; the second one about the SAME red carries none, and an exemption with
                // no terminator is CLAUDE.md §0.1's *never stuck* violated by the very fix that
                // was written to honour it. A repeat therefore counts, and three of them reach
                // the bound and the operator like any other judgement.
                var (verifyState, verifyWhen) = RecordVerify(recordJson);
                var lastMech = _store.LastTicketEvent(tid, "manager_sent_back_mechanical");
                var repeat = lastMech is { Detail: string md } && VerifyWhenOf(md) == verifyWhen;
                var exempt = sendBack && verifyState == "red" && !repeat;

                // THE WRITE-UP IS THE POINT, NOT THE VERDICT (D-R11): R6 renders `note` in the
                // approval ask so the operator's yes is a two-second decision instead of a
                // diff-reading session. So the row is written whatever the verdict, in the same
                // `ticket <id> {json}` shape R4's record uses — `LastTicketEvent` finds it and R6
                // parses it.
                var row = JsonSerializer.Serialize(new
                {
                    ticket = tid,
                    verdict = sendBack ? "send-back" : "ok",
                    asked = verdict,        // what the model actually said, including `approve`
                    confidence = conf,
                    tier,
                    // The count AFTER this send-back, which is what makes `exempt` legible: an
                    // exempt round leaves the count where it was (D-R24), so `round == rounds`
                    // and `exempt: true` say the same thing twice on purpose — R6's ask reads
                    // one of them and a person reading the row reads the other.
                    round = exempt ? rounds : rounds + 1,
                    bound = SendBackBound,
                    // THE CODE FACT THE EXEMPTION KEYS ON, in the row, so "was this exemption
                    // earned" is answerable by reading rather than by re-deriving it later.
                    verify = verifyState,
                    exempt,
                    // D-R23's record: which files it asked for and why, and what it was refused.
                    details = granted,
                    detailsWhy,
                    detailsRefused = refused,
                    note,
                    message,
                });
                _store.Event("manager_review", laneId, $"ticket {tid} {row}");
                if (!sendBack) return;                 // silent on agreement (operator's rule #3)

                var text = message.Length > 0 ? message : note;
                if (text.Length == 0)
                {
                    // A send-back with nothing to say cannot be delivered, and quietly treating
                    // it as agreement would be a block that evaporated.
                    _store.Event("manager_review_failed", laneId,
                        $"ticket {tid}: verdict send-back with no message and no note — nothing to send, so nothing was sent");
                    return;
                }
                await SendBackAsync(t, laneId, exempt ? rounds : rounds + 1, text, note, exempt, verifyWhen);
            }
            catch (Exception ex) { _store.Event("manager_review_failed", laneId, $"ticket {tid}: {ex.GetType().Name}: {ex.Message}"); }
            // R6 (D-R11): the note is written FOR THE OPERATOR, so every way out of this method
            // ends by refreshing the question the operator is deciding in. A `finally` rather
            // than a line beside each `return` on purpose — there are six exits (the bound, no
            // judgement agent, no answer in 25 s, an unparseable reply, agreement, a delivered
            // send-back) and a rule that has to be remembered at six sites is a rule that gets
            // skipped at the seventh. The ask itself already exists by now, opened by
            // `BuildRecord`, so this only ever changes what it SAYS.
            finally
            {
                try { AskToLand(t, laneId, recordJson); }
                catch (Exception ex) { _store.Event("land_ask_failed", laneId, $"ticket {tid}: after the review: {ex.Message}"); }
            }
        });
    }

    /// <summary>What the review asked to READ, narrowed to strings and capped before anything
    /// touches a disk — D-R23's *named and narrow*, at the first point it can be enforced. The
    /// cap here is not the grant cap: it only stops a reply listing five hundred paths from
    /// costing a five-hundred-iteration loop. <see cref="GrantDetails"/> is where the request
    /// meets the record and most of it is refused.</summary>
    static List<string> Requested(JsonElement v) =>
        v.TryGetProperty("need", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
               .Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length > 0).Take(8).ToList()
            : new List<string>();

    /// <summary>The verify state and the verify event's timestamp, out of R4's record. The
    /// TIMESTAMP is the part that is not obvious: D-R26 exempts one mechanical send-back per
    /// verify RESULT, so the exemption needs an identity for the red it is excusing, and the
    /// record already carries one. A record that will not parse yields `("", "")`, which is not
    /// `red`, so an unreadable record grants no exemption — the safe direction, because the
    /// failure mode being avoided is a bound that quietly stops bounding.</summary>
    static (string State, string When) RecordVerify(string recordJson)
    {
        try
        {
            using var d = JsonDocument.Parse(recordJson);
            if (!d.RootElement.TryGetProperty("verify", out var vv) || vv.ValueKind != JsonValueKind.Object) return ("", "");
            return (vv.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "",
                    vv.TryGetProperty("when", out var wn) ? wn.GetString() ?? "" : "");
        }
        catch (JsonException) { return ("", ""); }
    }

    /// <summary>The `verifyWhen` a previous mechanical send-back was excused for. Unreadable
    /// compares unequal, which spends a round rather than granting a free one.</summary>
    static string VerifyWhenOf(string detail)
    {
        var brace = detail.IndexOf('{');
        if (brace < 0) return "";
        try
        {
            using var d = JsonDocument.Parse(detail[brace..]);
            return d.RootElement.TryGetProperty("verifyWhen", out var w) ? w.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>D-R23's *named and narrow*, and every word of it is enforced HERE rather than
    /// asked for in the prompt — a reviewer that could widen its own request is the expensive
    /// reviewer arriving through the door marked cheap, and a prompt is not a boundary
    /// (`WORK-ISOLATION-PLAN` §2).
    ///
    /// Three refusals, and each closes a different way of asking for everything:
    ///
    ///  * **Not in the record's own `changed` list** — so `*`, `.`, `the diff`, a path in
    ///    another repository and a file this branch never touched are all simply not files it
    ///    can name. The list is the record's, which is the same list the question showed it.
    ///  * **More than <see cref="DetailsFileCap"/> files, or more than the byte budget** — one
    ///    generated file is a whole diff by itself, so a file count alone would not bound this.
    ///  * **Anything that resolves outside the worktree** — `changed` membership already makes
    ///    this unreachable, and it is checked anyway because the cost of being wrong is reading
    ///    an arbitrary file off the operator's disk into a model prompt.
    ///
    /// Returns the block to attach to the question, or null when nothing survived — in which
    /// case the round is still SPENT (the caller sets that before calling), because a reviewer
    /// that could retry after a refusal would have an unbounded loop for the price of one bad
    /// path. Everything refused is handed back in <paramref name="refused"/> for the row.</summary>
    string? GrantDetails(Store.TicketRow t, string recordJson, List<string> want,
                         List<string> granted, List<string> refused)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var d = JsonDocument.Parse(recordJson);
            if (d.RootElement.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var x in ch.EnumerateArray())
                    if (x.GetString() is { Length: > 0 } c) changed.Add(c.Replace('\\', '/'));
        }
        catch (JsonException) { }

        var root = Path.GetFullPath(t.Worktree);
        var sb = new StringBuilder();
        var budget = DetailsBytesTotal;
        foreach (var raw in want)
        {
            var name = raw.Replace('\\', '/').TrimStart('/');
            if (granted.Count >= DetailsFileCap) { refused.Add($"{raw} (the cap is {DetailsFileCap} files for one review)"); continue; }
            if (!changed.Contains(name)) { refused.Add($"{raw} (not one of the files this change touched)"); continue; }
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { refused.Add($"{raw} (not a readable file inside the worktree)"); continue; }
            string body;
            try { body = File.ReadAllText(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { refused.Add($"{raw} ({e.GetType().Name})"); continue; }
            var take = Math.Min(Math.Min(body.Length, DetailsBytesPerFile), budget);
            if (take <= 0) { refused.Add($"{raw} (the review's byte budget was already spent)"); continue; }
            budget -= take;
            granted.Add(name);
            sb.Append($"--- {name} ---\n").Append(body[..take]);
            if (take < body.Length) sb.Append($"\n--- truncated at {take} of {body.Length} chars ---");
            sb.Append('\n');
        }
        return granted.Count == 0 ? null : sb.ToString();
    }

    /// <summary>The question, and D-R12's bound on reading is IN it: the diffstat, the
    /// changed-file NAMES and the agent's own report — never the diff content, which plan §9
    /// rejects by name.
    ///
    /// **TWO OF THE RECORD'S FIELDS WOULD MAKE A NAIVE REVIEWER BLOCK EVERY TICKET, so the
    /// prompt says what they mean out loud.** `verify.state = not-run` is the NORMAL value and it
    /// is correct — D-R15: the verify that gates is the land's own, on the result of merging main
    /// in, and one run here would answer a different question while reading as though it did not.
    /// `drop.state = moot` means main has not been merged into the branch yet, not that a check
    /// failed. A manager that read either as red would send every ticket back forever, which is
    /// D-R12's infinite politeness arriving through the front door.
    ///
    /// The history goes in too, so round three does not repeat round one — the same rows the
    /// operator gets at the bound.</summary>
    string ManagerQuestion(Store.TicketRow t, string recordJson, int rounds, string? details)
    {
        static string S(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var x) ? x.ToString() : "";
        using var d = JsonDocument.Parse(recordJson);
        var r = d.RootElement;
        var changed = r.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? string.Join(", ", ch.EnumerateArray().Take(40).Select(x => x.GetString())) : "";
        var verify = r.TryGetProperty("verify", out var vv) ? vv : default;
        var drop = r.TryGetProperty("drop", out var dd) ? dd : default;
        var hist = SendBackHistory(t.Id);
        return "Review the completed work on a ticket and decide whether to SEND IT BACK.\n" +
               $"Ticket {t.Id}: {t.Title}   branch {t.Branch}\n" +
               $"Files changed ({S(r, "files")}): {changed}\n" +
               $"Diffstat:\n{Truncate(S(r, "diffstat"), 1500)}\n" +
               $"Uncommitted files in the worktree: {S(r, "uncommitted")}\n" +
               $"Verify: {S(verify, "state")} — `not-run` is the NORMAL value and it is CORRECT: no verify has run " +
               "yet, and the one that gates runs at land time on the result of merging main in. Never send work back " +
               "for that, and never for missing tests.\n" +
               $"Silent-drop check: {S(drop, "state")} — `moot` means main has not been merged into the branch yet, " +
               "which is the ordinary state before a land. `dropped` means the branch discarded something main " +
               $"changed, and THAT is worth raising: {S(drop, "files")}\n" +
               $"The agent's own report of the turn it just finished:\n{Truncate(S(r, "report"), 2500)}\n" +
               (hist.Length > 0
                   ? $"You have already sent this ticket back {rounds} time(s): {Truncate(hist, 800)}\n" +
                     "Do not repeat a point it has already dealt with.\n"
                   : "") +
               "You may BLOCK and you may NOT BLESS: you cannot approve anything, and `ok` grants nothing — the " +
               "operator's approval is the only yes and it is not yours to give. Send work back only for something " +
               "real: work that does not match the ticket, a change the report does not mention, a discarded file, " +
               "a schema or interface change slipped in quietly. Not for style.\n" +
               // D-R23'S ESCAPE HATCH IS OFFERED ONLY WHILE IT IS STILL AVAILABLE, and the
               // details pass says so out loud. A reviewer told it may ask, holding the files it
               // asked for, would ask again — and the refusal to read a second request would
               // then look to it like the system swallowing its question.
               (details is null
                   ? "You have NOT been shown the contents of any file, and that is deliberate and permanent: this " +
                     "review is cheap on purpose, and a review genuinely worth a full read is a PERSON'S job. If one " +
                     "specific thing concerns you and the summary above truly cannot settle it, you may name up to " +
                     $"{DetailsFileCap} files FROM THE CHANGED LIST ABOVE in `need` and you will be asked once more with " +
                     "their contents. You get ONE such round, it is spent whether or not what comes back helps, and " +
                     "anything not in that list is refused. Judge without it whenever you can.\n"
                   : "You asked to see these files and this is your ONE look at them — decide now, because a second " +
                     $"request will not be read:\n{details}\n") +
               "Reply ONLY one line of JSON, no prose, no markdown, no code fence: " +
               "{\"verdict\":\"ok|send-back\",\"confidence\":\"high|medium|low\"," +
               "\"note\":\"<=200 chars, written for the operator deciding whether to merge\"," +
               "\"message\":\"<what to tell the agent, only when send-back>\"" +
               (details is null ? ",\"need\":[\"<a changed file, ONLY if you cannot judge without it>\"],\"needWhy\":\"<why>\"}" : "}");
    }

    /// <summary>What the manager has already asked for on this ticket, oldest first — D-R12's
    /// "with the history attached". Read out of the events because they are the only copy: no
    /// field, no column, and it survives the publish that restarts the daemon.</summary>
    string SendBackHistory(long tid)
    {
        var parts = new List<string>();
        // BOTH KINDS (D-R25). An exempt send-back is a different EVENT so that the bound's two
        // counters stay right with no new logic — but it is the same objection to the agent, and
        // a history that omitted it would let round three repeat what round one already said,
        // which is the one thing this history exists to prevent. The read is generous because
        // exempt rounds are not bounded by `SendBackBound`; both callers Truncate.
        foreach (var (kind, _, detail) in _store.TicketEvents(tid, SendBackBound * 3, "manager_sent_back", "manager_sent_back_mechanical"))
        {
            var brace = detail.IndexOf('{');
            if (brace < 0) continue;
            try
            {
                using var d = JsonDocument.Parse(detail[brace..]);
                var round = d.RootElement.TryGetProperty("round", out var rd) ? rd.ToString() : "?";
                var said = d.RootElement.TryGetProperty("message", out var mg) && mg.GetString() is { Length: > 0 } m
                    ? m
                    : d.RootElement.TryGetProperty("note", out var nt) ? nt.GetString() ?? "" : "";
                parts.Add($"({round}{(kind == "manager_sent_back_mechanical" ? ", on a red verify — no round spent" : "")}) {said}");
            }
            catch (JsonException) { }      // a row we cannot read is worth less than the ones we can
        }
        return string.Join("  ", parts);
    }

    /// <summary>Deliver the manager's objection to the lane AS INPUT — `LaneRuntime.Say`, the
    /// same path a typed sentence takes, so the agent keeps its warm context and simply carries
    /// on (D-R9). That is what makes "request changes" cheap here: the lane is a thread, not a
    /// pull request. `Say` also writes the pane's `user_input` row, so the operator sees the
    /// send-back exactly where they see their own sentences, and the `[manager review …]` prefix
    /// is what tells the two apart — no announcement, deliberately, because a machine handling
    /// its own round is not a person being needed (§4).
    ///
    /// **IT MUST NOT VANISH.** `Say` throws when the lane is not connected, and a send-back that
    /// disappeared would be the silent-failure class this codebase pays for most. So this waits
    /// for the lane to come back — a shim reconnecting, or a reconcile adopting it — and if it
    /// does not, records the whole message and puts it in front of the operator with the two
    /// commands that deliver it by hand. It does NOT respawn from here: that is forty lines the
    /// `lane-respawn` handler already owns (project ownership, the lane's own config, the ticket
    /// system prompt, the resume args), and a second implementation of it would drift on exactly
    /// the cases that matter — `MakeTicket`'s lesson. An undelivered send-back also counts as NO
    /// ROUND against the bound, because nothing was said.</summary>
    async Task SendBackAsync(Store.TicketRow t, long laneId, int round, string message, string note,
                             bool exempt, string verifyWhen)
    {
        // THE AGENT IS TOLD WHICH KIND OF OBJECTION THIS IS, because the two mean different
        // things to it: a strike is a judgement it has to answer, and a red verify is a fact it
        // can already see (D-R24). Telling it "round 2 of 3" for something that spent no round
        // would be the record and the message disagreeing in front of the one reader who cannot
        // check.
        var head = exempt
            ? $"[manager review — the verify on record is RED, so this is not one of your {SendBackBound} rounds]"
            : $"[manager review, round {round} of {SendBackBound}]";
        var text = $"{head} {message}" + "\n\n" +
                   "This is a review of the turn you just finished, not a new task. Address it on this branch and " +
                   "commit; nothing has been merged and nothing has been approved.";
        // A CONDITION WITH A DEADLINE, never a sleep — CLAUDE.md §3's rule for waits, in code. The
        // turn that produced this record came off this lane's own wire, so it is normally
        // connected already and this loop runs exactly once; 20 s covers a shim reconnecting or a
        // reconcile adopting it, and what un-sticks the wait is named in the refusal below.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_lanes.TryGetValue(laneId, out var rt) && rt.Connected)
            {
                try
                {
                    // The event is written AFTER the write to the wire, because what it records
                    // is a send-back that was DELIVERED — a round burned on a message the lane
                    // never received would be the bound eating the agent's chances silently.
                    rt.Say(text);
                    // D-R25: AN EXEMPT SEND-BACK IS ITS OWN EVENT KIND. The bound counts
                    // `manager_sent_back` and R6's ask counts it again, independently, to word
                    // "you are at the bound" — so a separate kind keeps BOTH readers correct
                    // with no new logic in either, where a flag inside the JSON would have
                    // needed every counter in the tree to learn to read it and would have been
                    // wrong in whichever one was missed.
                    _store.Event(exempt ? "manager_sent_back_mechanical" : "manager_sent_back", laneId,
                        $"ticket {t.Id} " + JsonSerializer.Serialize(new
                    {
                        ticket = t.Id, lane = laneId, round, bound = SendBackBound, exempt, verifyWhen, note, message,
                    }));
                    return;
                }
                catch (InvalidOperationException) { }   // it dropped between the check and the write: keep waiting
            }
            await Task.Delay(250);
        }
        _store.Event("manager_send_back_undelivered", laneId, $"ticket {t.Id} " + JsonSerializer.Serialize(new
        {
            ticket = t.Id, lane = laneId, round, note, message,
        }));
        Announce(t, $"manager review: ticket {t.Id} '{t.Title}' should go back to its agent, but lane {laneId} has not " +
                    $"answered for 20s — `dodona lane-respawn {laneId}`, then `dodona say {laneId} \"{Truncate(message, 200)}\"`. " +
                    "It was NOT delivered and it counts as no round against the bound.");
    }

}
