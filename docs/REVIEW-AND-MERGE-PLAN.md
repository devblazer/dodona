# Review and merge — the ordinary developer flow, with a manager as the reviewer

Status: **R1–R8 BUILT** (R6 and R8 on 2026-08-21, R7 the same day — every row of §8 is in). R8 is
D-R23/D-R24, the operator's two amendments to D-R12, plus D-R25/D-R26/D-R27 decided while
building them. R7 is the delivery mode, and it is the **refusal** half of `M5-DELIVERY-PLAN`'s
M5.5 shipped on its own — D-R28 says what that does and does not include, and it is the first
thing to read before assuming pr mode is finished. Written 2026-08-20 from the operator's brief, after tracing the land
path in code and measuring what the manager is actually told today.

The authority for how a ticket's work gets reviewed and lands on main. It **supersedes
`WORK-ISOLATION-PLAN.md` D-4 and P8** (elastic claims — the whole idea is retired here, not
rescheduled), and it **absorbs that plan's P3, P4 and P5** into R1/R4/R6 below, because "verify
before the merge", "examine at end of turn" and "raise the approval question" turn out to be three
slices of one flow rather than three phases. `WORK-ISOLATION-PLAN.md` keeps everything about
*isolation* — layers 1, 2 and 3 — which is built and landed for the first two.

`M5-DELIVERY-PLAN.md` still owns a **foreign** project's ceremony. This owns Dodona's own, and §7
says how the two meet.

## 0. The one-line statement

Dodona's merge is `git merge --ff-only` and nothing else: no bringing main in, no conflict
resolution, no rebase. When main has moved it refuses and tells the agent to rebase, and nothing
rebases. Everything built to compensate for that — declared file claims, refusing two tickets over
one path, diffing a branch against its claim before granting the token — is prediction machinery
standing in for the one step every developer on the planet already does. Do the step instead.

## 1. What exists today, from the code

- **`token-request <ticket>`** refuses when the ticket is `on-approval` and not approved, then runs
  the **merge-time backstop**: `git diff --name-only <main>...<branch>`, and any touched path
  outside the ticket's declared claim refuses the token.
- **`land <ticket>`** requires the token, an unexpired lease, and `main` checked out **in the shared
  checkout**; then `git merge --ff-only <branch>`. On failure:
  *"refused: not fast-forward — rebase `<branch>` onto `<main>` and re-verify first."*
  **Nothing in the tree performs that rebase.** There is no `git rebase` and no `git merge <main>`
  anywhere in the daemon.
- **Verify runs AFTER main has already advanced** — the configured steps execute in the repository
  that just changed. A red verify has already shipped.
- **The manager reviews your PROMPTS, and only your prompts.** `BrainReview` fires once, at lane
  creation, fire-and-forget. It is handed: your sentence, the name code derived, the model policy,
  the titles of existing live lanes, and the repository names. It replies with
  `agree/confidence/better_name/ticket/reason`, and may rename the lane or *announce* a ticket
  suggestion that nothing acts on. **It never sees a lane agent's output.**
- **The end-of-turn hook exists and is already taken.** `rt.OnResult` is set for work lanes — to
  the compressor, which shortens pane output. Nothing else consumes a turn's result.
- The approval gate, the question row and the ask overlay all exist (`approve`, `Ask.cs`,
  `dodona ui answer`), and the merge token is one per repository.

So: the branch, the worktree, the diff, a verify runner, a lane you can talk to, a review-behind
pattern, an approval gate and a serialising lock are all present. **A pseudo-PR is those parts in a
cycle, not new machinery.**

## 2. Principles this is held to

- **Code answers facts; a model answers judgement.** Builds, tests, fast-forwards, dropped-nothing
  are facts. "Is this the right change" is judgement. Never swap them.
- **Block, don't bless** (D-R6). The asymmetry that makes a model reviewer safe.
- **Nothing is irreversible until the operator says yes**, and only one thing ever is: the ref
  advance (`WORK-ISOLATION-PLAN` D-8).
- **Quota is the scarce resource** (CLAUDE.md 2.6). A reviewer that reads every diff in full is a
  reviewer that cannot be afforded.
- **Never hung, halted, stuck, or outdated** (CLAUDE.md 0.1) — which cuts both ways here: an
  infinitely polite review loop is stuck.
- **Derive in code what is not really a judgement.** What a branch touched is `git diff`, not a
  declaration.

## 3. The merge flow: the one every developer already uses

**D-R1. Bring main into the branch, resolve and re-verify there, then fast-forward main.**
Operator's decision, 2026-08-20, and the reasoning is theirs: this is standard operating procedure
because the ambiguous work — conflict resolution — happens inside the branch, where nothing is at
stake for anyone else and the result can be tested before it touches the trunk. The merge back is
then trivial.

```
token-request           # serialise: one merge at a time per repository (already exists)
  git merge <main>      # IN THE WORKTREE, on the ticket branch
  <resolve>             # the agent, see D-R3
  dev gate              # re-verify IN THE WORKTREE, on the merged result
  git merge --ff-only   # in the shared checkout: now guaranteed, see D-R2
token-release
```

**D-R2. `--ff-only` stays, as an ASSERTION rather than a policy.** After main has been merged into
the branch and re-verified, the merge back *is* a fast-forward — so ff-only stops being the thing
that refuses work and becomes the check that the flow above actually happened. If it ever fails now,
main moved despite the token, which is a real fault and refusing is correct.

This also preserves the property the current design leans on and that `WORK-ISOLATION-PLAN` D-5
needs: after a fast-forward, main's tree is **byte-identical** to the branch tip that was verified.
Verifying the worktree is therefore exactly equivalent to verifying main, which is what makes
verify-before-merge possible at all. The operator's flow does not cost this property — it is how the
property gets earned on every land instead of only on the first.

**D-R3. The agent resolves ordinary conflicts.** Operator's decision, 2026-08-20: *"if there is
complexity to resolve, often it's expected, and often the agent just does it."* Correct — that is
what a developer does, and a system that refuses to let an agent resolve a conflict is a system that
forbids ordinary development. The oversight is §4 and §5, not prevention.

A conflict the agent cannot resolve is a **stop and say so**, not a guess: `git merge --abort`
returns the worktree to a clean state, the ticket stays open, and the operator gets the question.
Announced with what conflicted.

**D-R4. Code checks for the SILENT DROP, because that is the failure a report will not mention.**
The dangerous resolution is not the messy one — it is the quiet one, where the agent resolves by
discarding what main brought in and the tests still pass because nothing references the discarded
code. That is mechanically detectable and needs nobody's judgement: after the merge, the branch must
not be **reverting** anything main changed. Diff against the merge base, never against main's tip
after the fact (§9).

## 4. Retiring the reservations

**D-R5. Declared file claims stop being a lock.** Three refusals go:

| refusal | where | why it goes |
|---|---|---|
| a write outside the ticket's claim | the `PreToolUse` gate (`claim-check`) | the write is inside the agent's **own private checkout** and harms nobody. Blocking it is blocking an agent from doing the work it was given. |
| a second ticket over a claimed path | `ticket-create` | **two agents on one file is normal.** Files are not the unit of work: a feature spans files and features overlap. |
| a branch touching outside its claim | the `token-request` backstop | it asks whether reality matched a prediction. §5 reads reality instead. |
| a claim EXTENDED over another ticket's path | `Store.ClaimExtend` | **added in R3, 2026-08-20.** Not in the original three, and retiring it is forced rather than chosen: leaving it would mean a claim you may freely CREATE over another ticket's path is one you may not EXTEND onto, so the identical end state would be permitted or refused depending on which command you used. It also breaks in practice the moment `ticket-create` stops refusing, because overlapping tickets then exist and every wide extension hits one. A **bad spec** still refuses — that is unparseable input, not an overlap. |

The operator's reasoning, recorded because it is the decision: *"You give the sheriff to agents about
to work on the same file. That's often the case, very often the case. And if that is problematic in
some way, it's the manager's job to say something about it."*

And a second reason, found while tracing the merge: **the reservation never solved the problem it
was compensating for.** Two agents with entirely disjoint claims still both fail `--ff-only` as soon
as one of them lands, because main moved under the other. What makes concurrent work land is §3's
flow. Prediction was never the mechanism.

**What layer 1 already replaced.** Once an agent physically cannot write outside its own worktree
(`WORK-ISOLATION-PLAN` P1, landed), "did this branch touch a file it never declared" stops being a
safety question and becomes a review question — and a human approves the land anyway. The claim
gate's remaining job was already gone; this only says so.

**D-R6 is the exception that keeps the token.** One merge at a time per repository is not a
prediction and it stays exactly as it is. It is what makes D-R2's fast-forward guaranteed.

**D-R7. Ownership shown to the manager is DERIVED, never declared.**
`git diff --name-only <main>...<branch>` per open ticket is what each branch has *actually* touched.
A fact beats a prediction, it needs no ceremony from the agent, and it cannot go stale.

## 5. The pseudo-PR: a manager who reviews the work

**D-R8. Completion produces a PR-shaped record**, assembled by code and carrying no opinions:

- the ticket, its branch, its worktree
- `git diff --stat <merge-base>...<branch>` — what changed and how much
- the verify result from §3's re-verify, in the worktree
- the silent-drop check (D-R4)
- **the agent's own end-of-turn report** — the closest thing to a PR description, and the thing the
  manager has never once been shown

**D-R9. The manager reviews that record and may SEND IT BACK.** This is the part that makes it a
review rather than a rubber stamp, and it is cheap here because the lane is a thread: "request
changes" means delivering the review notes to the lane as input, through the same path a typed
sentence takes. The agent keeps its context and carries on.

**D-R10. THE MANAGER CAN BLOCK, BUT NOT BLESS.** The load-bearing rule of this whole plan.
Rejection is free and reversible — worst case it costs a round. Approval advances a ref that has no
undo. So the manager may send work back on its own judgement, freely and as often as the bound in
D-R12 allows, and **the yes is never its to give**: that stays the operator's, plus the code facts
of §3 and D-R4. A model as the sole gate on the one irreversible step is *a prompt providing
safety*, which `WORK-ISOLATION-PLAN` §2 forbids and which this plan is not allowed to reintroduce
just because the model is called a manager.

**D-R11. The write-up is the point, not the verdict.** The manager's most valuable output is the
sentence that makes the operator's approval a two-second decision instead of a diff-reading session:
*"WATER merged main in, resolved one conflict in `Store.cs`, 4 files, tests green — one thing looks
off: it changed the schema and its report does not mention it."* That is what the reservation system
was groping at and never reached. It renders in the approval ask (`WORK-ISOLATION-PLAN` D-7).

**D-R12. Bound the reading and bound the loop**, both because unbounded is the failure mode:

- **Reading**: the diffstat plus the agent's report first, on the cheap tier. File contents are
  pulled only when that tier says it is unsure — the escalate-on-low-confidence pattern
  `BrainReview` already uses. A reviewer that reads every diff in full on every turn cannot be
  afforded (CLAUDE.md 2.6).
- **Loop**: three send-backs. Then it goes to the operator regardless, with the review history
  attached. An infinitely polite loop is CLAUDE.md 0.1's "never stuck" violated in a costume where
  everyone is being reasonable.

**D-R13. The review fires on COMPLETION, not on every turn.** A `result` is the end of *a* turn, not
of the conversation (`LANE-LIFECYCLE.md` §2), and re-reviewing a chatty lane every turn would burn
the machine and the quota. Gate it on the worktree having changed since the last review.

**D-R14. THE LAND MUST STOP BLOCKING THE DAEMON, and it is a prerequisite for R4 rather than a
tidy-up.** Found while building R1, 2026-08-20. The control pipe is **serial** — one
`NamedPipeServerStream` instance, `HandleAsync` awaited inline — and `LandOp` runs on it. So for
the whole duration of a land's verify the daemon answers *nothing*: no UI, no lane input, no `say`,
no other workspace's command. Measured: the full `dev gate` would freeze it for **4.6 minutes**, and
even the deliberately narrow subset R1 settled on holds it for ~20 s. That is CLAUDE.md §0.1's
*never hung* violated on the one operation an operator is certainly watching, and it is
pre-existing — verify has always run inside `LandOp` — rather than something R1 introduced.

It is listed as its own phase (R3.5) for three reasons:

- **It changes the land's PROTOCOL, not just its threading.** The caller stops receiving the
  outcome and starts receiving *landing…*, with the result arriving later as an announcement. Every
  caller and every check that reads `land`'s reply is affected, so it cannot be smuggled into
  another phase.
- **R4 hangs off the same code path** and would inherit the freeze: the completion record assembles
  a diffstat and a verify result, in the worktree, at exactly the moment the land is running.
  Building R4 first means building it inside a blocking call and then moving it.
- **It is what makes the verify budget a real choice again.** With the land off the pipe, the
  narrowness of `dodona.json`'s `verify` becomes a question about *quota and wall clock* rather than
  about whether the app locks up — though note that the operator's standing directive (CLAUDE.md
  §0.1) governs regardless: the heavy suites are not a default, and this decision does not reopen
  that. See §11.

Two constraints that must survive the change, both load-bearing: **the token stays held across the
whole flow** (§10's race — the in-worktree merge and the fast-forward must not be separated by a
window in which main can move), and **a land that fails must still leave the worktree clean and
main untouched**, which is harder to guarantee once the failure is reported asynchronously.

### BUILT, 2026-08-20 — and the shape it settled into

Measured with a real concurrent call, not reasoned about, because reasoning about which thread runs
what is exactly what kept this invisible. The fixture's verify sleeps ~7 s and `land-status`
confirms the land is still running at the moment each probe is answered:

| | before | after |
|---|---|---|
| `dodona land` returns | when the whole land finishes | **142 ms** |
| `dodona status` during a land | after the land finishes (~20 s here, 4.6 min under the full gate) | **131 ms** |
| `dodona say` during a land | ditto — the agent never hears it | **161 ms**, and the agent received it |

Four things worth knowing before touching it:

- **The cheap gate stayed ON the pipe.** `LandGate` — ticket open, repository resolvable, token
  held, lease alive, trunk actually checked out — costs milliseconds, so those refusals still
  arrive on the caller's own reply with a non-zero exit. Only past that point does the reply become
  *landing…*. That is what made §8's "the existing land checks pass unchanged" true rather than
  aspirational: all 88 of `m1`'s checks passed on the first run against the new protocol.
- **`dodona land` still blocks the SHELL, deliberately.** The daemon is free; the caller is not.
  A `land` that returned 0 the instant it started would report success for a land that goes on to be
  refused, putting a fail-open in every script and every agent that lands and checks the exit code.
  So the CLI polls `land-status` and exits with the land's own verdict, bounded by
  `DODONA_LAND_WAIT_SEC` (900 s) whose timeout says in as many words that it is *not* a refusal.
  `--no-wait` is the opt-in for fire-and-forget — and it is also what makes the asynchrony reachable
  from a check at all (CLAUDE.md §3.1: an affordance no verb can reach is where the next defect
  lives).
- **Two new races existed the moment the land left the pipe, because the serial pipe had been
  preventing them for free.** A second `land` for a ticket already landing is refused by name; and a
  `stop-daemon` can now arrive mid-land, which announces and records `land_interrupted` rather than
  vanishing — re-running `land` is idempotent, since the trunk moves only in the last step. A hot
  SWAP needed no new guard, but only because of the first constraint above: `Blockers` already
  refuses to swap while a merge token is held, and the token is held across the whole flow. Break
  that constraint and a swap starts cutting lands in half, silently.
- **R4 INHERITS A TIGHT I7 BUDGET, AND SHOULD READ THIS FIRST.** Proving the asynchrony needs a
  verify that genuinely takes seconds, so `m1`'s fixture sleeps ~7 s once: m1 went **20 s → 29.5 s**
  and it is a `SoloSuites` member, so that lands on the wall clock directly. `dev gate` measured
  **292.8 s against the 300 s I7 budget** on 2026-08-20 — passing, with 7 s of headroom. R4 and R5
  add checks to the same suite. Do not silently raise the budget to make room, and do not delete
  coverage to fit: measure, and if earned coverage growth pushes past 300 s, raise it against that
  measurement the way the 120 s → 180 s raise was justified (CLAUDE.md §1).
- **`_lanes` had to become a `ConcurrentDictionary`.** The land's tail retires the lane, so a plain
  `Dictionary` was now written from a background thread for minutes at a time while the control pipe
  read and wrote the same buckets. `_brainLo` already carried that reasoning for the same reason;
  `_lanes` only became unsafe when something long-running left the pipe.

**D-R15. THE RECORD REPORTS THE VERIFY RESULT; IT NEVER RUNS ONE.** Decided while building R4,
2026-08-20, because D-R8 and D-R13 name two different moments and the phase cannot be written
without choosing between them. D-R8 wants the record to carry *the verify result from §3's
re-verify* and *the silent-drop check* — both produced by `LandFlow`, which runs at **land** time.
D-R13 fires the record at **completion**, the end of a turn. Those are not the same moment, and the
two ways of reconciling them are not equally good:

- **Assembling the record on the land path is not merely late, it is the wrong order.** The record
  exists so the manager can read it and send the work back (D-R9) *before* the operator's yes. But
  `land` needs the merge token, `token-request` needs `approve`, and this repo is
  `"landing": "on-approval"` (§6). So a record assembled inside a land is a record produced *after*
  the approval it was supposed to inform, and the manager's block — the load-bearing asymmetry of
  D-R10 — has nothing left to act on. Completion is the only moment at which the record can still
  change anything.
- **Running a verify at completion buys a number nothing gates on, at the cost that matters most.**
  Measured for R1 in a cold worktree on this machine: `dev gate` 273.1 s, the narrow subset this
  repo settled on ~17 s plus a build — per completed turn-with-changes, per ticket. Quota and wall
  clock are the scarce resource (CLAUDE.md §0.1/§2.6), and the operator's standing directive on the
  heavy suites governs here as everywhere else.
- **And it would be a different question wearing the same word.** D-R1's verify runs on the
  **merged** result, in the worktree, under the token, immediately before the ref moves. A verify at
  completion runs on a branch main has *not* been merged into, so its green says nothing about the
  tree that would land — while reading exactly as though it did. That is CLAUDE.md §0.3's
  *believing a green check*, manufactured on purpose.

So the record is assembled **entirely at completion, from facts that are free**, and the verify slot
carries the most recent verify **already recorded** for the ticket — `LandFlow`'s own `verify_green`
/ `verify_red` — or the words `not-run` when there is none. Four of D-R8's five items cost nothing
at completion: the ticket, branch and worktree are a store read; `git diff --stat <main>...<branch>`
is one git call (and the three-dot form is *already* merge-base-relative, so §10's merge-base trap
is about the drop check and not about this); the agent's report is the argument the trigger arrives
with; and the drop check is pure git — `MainMergeOnBranch` plus `SilentDrops`, no build and no test
— so it **does** run at completion, and says `moot` in as many words until main has been merged in.

Two consequences, stated so they are not rediscovered:

- **`not-run` must be said, never left blank.** A verify slot that was absent or empty would be
  indistinguishable from a verify that had failed to run, which is `land_drop_check_moot`'s whole
  reason for existing.
- **R5 inherits a first read of diffstat + report + drop-check, knowing verify has not run** — which
  is exactly the bound D-R12 already sets (*the diffstat plus the agent's report first, on the cheap
  tier*). Nothing is lost, and the verify that **gates** is still the one in `LandFlow`, before the
  ref moves, and stays the only one.

### BUILT, 2026-08-20 — and the three decisions the phase could not be written without

The manager exists. `ManagerReview` reads the record, `SendBackAsync` delivers the objection to the
lane as input, and there is no path from either to `TicketApprove`. Seven new checks, all seen
red: six in `brain` (69 checks, 61.8 s measured here) and one in `m1` (113 checks, 31.8 s). An
eighth was written and deleted — `dev prove` called it VACUOUS, correctly, because it asserted
that `ticket-create` makes a worktree, which already works. Its value went into the first real
check's failure detail instead.

Two properties are worth stating before the decisions, because both are load-bearing and neither
is obvious from the code:

- **A SEND-BACK CANNOT REVIEW ITSELF, and D-R13's gate is what guarantees it.** The send-back is
  delivered with `Say`, which starts a turn, which ends, which arrives back at `CompletionRecord`.
  That turn moved no files, so the digest matches, so there is no record and therefore no second
  review. The loop terminates **on a fact**, not on a model deciding to stop — which is what
  "bounded" has to mean when a model is in the loop. R4's gate turns out to be R5's terminator.
- **The trigger is THE RECORD EXISTING, not a third consumer of `rt.OnResult`.** §10's trap says
  that field is an assignment, and it already carries two consumers. A review wired there would
  also have to re-derive D-R13's gate to know whether anything was new to review — and the record
  *is* that answer, since it exists exactly when the worktree moved.

**D-R16. THE SEND-BACK BOUND COUNTS ITS OWN EVENT KIND, not `brain_review`.** Appendix A said to
count `brain_review` events for the ticket. The reasoning behind that — count in the store, not in
a field, and no schema bump for one integer — is kept and is the whole of `Store.CountTicketEvents`.
The *kind* is not: `brain_review` is the spawn-time review (§1), it is lane-scoped, and it fires for
plain lanes that have no ticket at all, so counting it would count reviews rather than send-backs.
D-R12 bounds **send-backs**, and a review that agreed must not spend one of an agent's three
chances. So `manager_sent_back` is a distinct, ticket-scoped kind, written **only after the message
reached the wire** — an undelivered send-back counts as no round, because nothing was said.

**D-R17. THE REVIEW HONOURS `DODONA_NO_AUTOSTART`, because it is the daemon starting a model agent
on its own initiative.** Not in answer to operator input — the same class as the startup warm-up,
the drift watcher and `EnsureRouterAsync`, and all four now agree on what *do not start things by
yourself* means. This is not a test hook and it is not optional: without it every model-free suite
that finishes a ticket turn spawns a real `claude -p --model haiku`, because a fixture with no
`agent` in its `dodona.json` gets the real CLI by default — which `m1` is exactly, and which is
CLAUDE.md §3.2's incident (a "quick health check" that left four model-backed processes on a
machine the operator believed was idle). The operator never sets the variable. `m1` asserts the
guard from the outside (the skip is recorded, and no brain lane exists in that store);
`brain-acceptance` clears it for the R5 section, as it already does for the classifier's, so the
review is proved on the operator's own path.

**D-R18. AT THE BOUND THERE IS NO FOURTH MODEL CALL, AND THE OPERATOR IS TOLD ONCE.** D-R12's *then
it goes to the operator regardless* is not *then it asks a fourth time*: a fourth review would
spend quota producing a note nothing can act on, since the only lever it has — the send-back — is
spent. So at three the model is not asked at all; `manager_bound_reached` is written, and the
ticket's pane carries one announcement naming the count and quoting what the manager asked for in
order. **Once**, gated on that event rather than on a flag, for the same reason the bound itself is:
a daemon restarts on every publish, and an announcement per subsequent turn would be the
never-stuck fix turning into never-quiet.

**What R6 inherits.** `manager_review` is written for **every** verdict, including `ok`, and carries
the `note` — because D-R11 says the write-up is the point and the verdict is not. It is
ticket-scoped in R4's `ticket <id> {json}` shape, so `Store.LastTicketEvent` finds it and R6 parses
it. The pane is deliberately *not* where a review lands (§4: attention is owed when a person is
needed, and a machine handling its own round is not that) — except at the bound, where a person is
genuinely needed, and when a send-back could not be delivered.

### BUILT, 2026-08-20 — R6, and the four decisions it could not be written without

The write-up reaches a person. `AskToLand` raises one `questions` row per ticket, the overlay
renders it, `dodona ui answer yes` lands in `MainWindow.AnswerAsk` — the method a button click
lands in — and that answer is `ApproveTicket`, which is what `dodona approve` has always called.
12 new acceptance checks, all seen red (`dev prove`), plus 2 `unit` ones; one was written,
called **VACUOUS** by `dev prove` and rewritten rather than kept, because "the overlay is gone"
is true of a build that never put one up.

**D-R19. THE ASK IS RAISED BY THE RECORD EXISTING, NOT BY THE REVIEW, and it is refreshed in
place.** This is the decision the phase turns on. Four ordinary things leave a ticket with no
`manager_review` row — `DODONA_NO_AUTOSTART` (D-R17), `"brain": false` for the project, a cheap
tier that timed out, and the send-back bound being spent (D-R18) — so an ask that appeared only
once a note existed would make **approving a merge conditional on a model having answered**. That
is D-R10's trap in its fail-closed mirror: judgement switched off would mean nothing could ever
be merged. So `BuildRecord` fires the review and then raises the ask, unconditionally and in its
own `try`; the review's own `finally` refreshes the text on **every** one of its six exits. The
consequence to accept knowingly: the question appears saying "no review has run" and changes
seconds later to carry the note. `Store.QuestionUpsert` is what makes that one row rather than
two — a second row would be a queue of overlays for one decision.

**D-R20. IT IS RAISED WHATEVER THE VERDICT, INCLUDING A SEND-BACK.** The manager's objection is
*rendered*, not enforced: the ask reads "the manager sent this back, round 2 of 3: <why>" and the
operator decides. Hiding their question behind the model's opinion would quietly promote it to
the gatekeeper D-R10 says it may never be — a block the operator cannot see is a block they
cannot overrule. At the bound the ask says so in as many words, because that is a different
question from an ordinary approval, and **the bound is read as a COUNT rather than as the newest
event**: the first draft made it one more arm of the switch, and `brain` caught it — past the
bound no review will ever run again, so any later turn's record lands on top of
`manager_bound_reached` and the ask reverted to "not reviewed yet", permanently, for exactly the
ticket the operator has been handed.

**D-R21. THE WITHDRAWAL IS WIRED INTO THE TICKET'S OWN STATE CHANGES, in the store.** An ask
standing over a ticket that has already landed or been abandoned offers a decision that has
already been made (CLAUDE.md §0.1's *outdated*). Four call sites move a ticket out of `open`, so
the rule lives in `Store.TicketState` and inside `LandCommit`'s **own transaction** — there is no
window in which the overlay offers to approve a merge that has happened. A rule the daemon has to
remember at four sites is a rule that gets skipped at the fifth (§0's *enforcement in code*).

**D-R22. THE PROOF IS SPLIT ACROSS TWO SUITES, on the I7 budget.** §8 asks for *the ask carries
the summary and answering it grants the token, at a live window*. `ui-use` is a `SoloSuites`
member, so every second there lands on the gate's wall clock directly, and a real review needs
autostart cleared plus a manager agent — which `brain` already stands up and which is **not**
solo. So `brain` proves the note reaches the row (and that no verdict ever answers it), and
`ui-use` proves the row reaches the screen and that answering grants the token, on the no-review
path it gets for free. Measured: `ui-use` 99.6 s → **104.8 s**, `brain` 61.8 s → **65.4 s**.
Also absorbed here is `WORK-ISOLATION-PLAN` P5 as literally written — `token-request`'s
unapproved refusal now raises the same row and names it (`m1`) instead of printing a command at
somebody who has a window — but the **primary moment is completion**, because that is where the
manager's write-up is and it means the operator is not waiting for an agent to bump into a wall
first.

### DECIDED 2026-08-21, BUILT THE SAME DAY — R8, and both decisions amend D-R12

The operator answered §11's two remaining questions in one breath, and they are one decision seen
from two sides: **the reviewer must not become a thing that holds agents up.** Recorded here
because a decision that lives only in a conversation is one the next session re-litigates.

**D-R23. THE REVIEWER MAY ASK FOR THE DETAILS; IT NEVER GETS THEM BY DEFAULT.** D-R12 bounds the
reading to the diffstat, the changed-file names and the agent's own report, and that stays the
first pass **permanently** — this is no longer a stopgap awaiting a measurement. What is added is
a way for the review to say *"I need to see `Store.cs` before I can judge this"* and get that one
file, when something in particular concerns it. Three properties it has to have, and each is a way
the feature could go wrong:

- **Named and narrow.** It asks for specific files, not "the diff". A request that can widen to
  everything is the expensive reviewer arriving through the door marked cheap.
- **Bounded and once.** One round of details per review, and a cap on what comes back. A reviewer
  that can ask, read, and ask again is an unbounded loop wearing D-R12's clothes — the same
  failure the send-back bound exists for, one level down.
- **Recorded.** Which files it asked for and why, in the `manager_review` row. That is the only
  way anyone finds out later whether the escape hatch is being used for judgement or as a habit,
  and it is what makes "is this affordable" answerable from rows rather than argument.

The operator's own framing, which settles more than the mechanism: **a review genuinely worth a
full read is a person's job** — theirs, or an agent they ask directly — and this reviewer is not
the place to buy one. *"These kinds of systems — Dodona itself is geared towards performance and
speed. Move fast and break things."* So the trade is made knowingly and in this direction: this
reviewer will miss things a thorough one would catch, and that is preferred to agents waiting and
quota ballooning. It can still block, and the operator still approves every merge (D-R10), which
is what makes missing things survivable.

**D-R24. A MECHANICAL OBJECTION IS NOT A STRIKE, AND CODE DECIDES THAT IT WAS ONE.** A send-back
whose grounds are `verify: red` does not spend one of D-R12's three rounds. The reasoning is the
asymmetry: a strike is a judgement the agent has to answer, while red tests are a fact it can
already see, so counting them means an agent gets fewer real chances for having had a bad build at
the wrong moment.

R5's objection to this — *an exemption the model itself classifies is a bound the model can talk
its way out of* — **is not overturned; it is what fixes the implementation.** The model does not
classify anything. The verify state is already a code fact in R4's record (`verify_green` /
`verify_red`, written by `LandFlow`, reported and never re-run — D-R15), so the exemption keys on
what the RECORD says at the moment of the review. A reviewer claiming it only objected because the
tests were red earns nothing by saying so. Note the interaction with `not-run`, which is the
normal value: `not-run` is not red, so it grants no exemption — and R5's prompt already tells the
reviewer that `not-run` is correct and never grounds for anything.

### The three decisions R8 could not be written without

**D-R25. AN EXEMPT SEND-BACK IS ITS OWN EVENT KIND** — `manager_sent_back_mechanical`, beside
`manager_sent_back` — **and not a flag inside the JSON.** D-R24 says a mechanical objection does
not spend a round; the implementation question it leaves open is where the exempt round is
*recorded*, and the two answers are not equally safe. The bound counts `manager_sent_back`
(D-R16), and **R6's approval ask counts the very same kind again, independently**, to decide
whether to word itself *"you are at the bound"* — so there are already two counters, in two
phases, written months apart. A flag inside the row would have had to be learned by both, and by
every counter added after, and would have been silently wrong in whichever one was missed. A
distinct kind keeps both readers correct **with no new logic in either**, which is the cheapest
correct answer and the one this codebase's history argues for: a bound that quietly stops
bounding is its most repeated failure (§3's dead routing ladder, R4's digest, the store-counted
bound itself). `SendBackHistory` reads **both** kinds, because the exemption is about the count
and not about what the agent was told — a history that dropped the mechanical rounds would let
round three repeat what round one already said, which is the one thing that history exists to
prevent.

**D-R26. ONE EXEMPTION PER VERIFY RESULT, NOT PER SEND-BACK.** This is the terminator D-R24
needs and does not itself supply. `verify_red` is written by `LandFlow` and **never re-run**
(D-R15), so the record goes on saying `red` for as long as no further land happens — including
after the agent has fixed the very thing. An exemption keyed only on that word would therefore be
an **unbounded** send-back loop available for free: D-R12's failure mode restored by the fix
written to honour it, and CLAUDE.md §0.1's *never stuck* violated in the newest costume yet. So
the exemption is keyed on the verify event's own **timestamp**, carried in the record and written
into the exempt event: the first mechanical objection about a given red is excused, and a second
one about that same red counts like any other. The reasoning is D-R24's own — *red tests are a
fact the agent can already see* — read one step further: the first telling carries information,
and the second carries none. Three repeats reach the bound and the operator, which is where an
argument the machine cannot settle is supposed to end up.

Note what this deliberately does **not** do: it puts no cap on exemptions across *different* red
verifies. Each of those costs a land, and a land needs the operator's approval, so a person is
already in that loop.

**D-R27. THE DETAILS ROUND IS SHARED BY BOTH TIERS, AND WHAT MAY BE NAMED IS THE RECORD'S OWN
`changed` LIST.** Two implementation choices, both of which are D-R23's properties made
enforceable rather than requested:

- **Shared, not one round each.** The cheap tier may ask, or the expensive one may, and whichever
  asks first spends it; the other reads what was bought. Worst case is therefore **three** model
  calls for one review — a tier that asks, plus an escalation — against a floor of one, which is
  still the ordinary case. Giving each tier its own round would have made four the ceiling for a
  feature whose entire justification is that it fires rarely, and every one of these is per
  finished turn, per ticket.
- **The `changed` list is the namespace.** `GrantDetails` refuses anything that is not a file the
  record already told the reviewer about, which is what makes *named and narrow* a property of
  the code rather than of the prompt — `*`, `the diff`, a path in another repository and a
  traversal out of the worktree are all simply not names it can use, by the same one refusal. A
  prompt asking for restraint is not a boundary (`WORK-ISOLATION-PLAN` §2), and this is the exact
  place where "the expensive reviewer arriving through the door marked cheap" would get in. Byte
  caps sit beside the file cap because one generated file is a whole diff by itself.
- **A refused request still spends the round.** Otherwise a reviewer could retry after every
  refusal and have its unbounded loop back for the price of one bad path.

### BUILT 2026-08-21 — R7, and the four decisions it could not be written without

**D-R28. R7 IS THE REFUSAL, AND IT SHIPS ALONE — NOT BEHIND M5.3 AND M5.4.** Two current plans
size this phase differently and both were live. This document's Appendix A is narrow: add the
field to `Config`, branch the land on it. `M5-DELIVERY-PLAN` §14 puts `delivery: pr` at **M5.5**,
behind M5.3's branch lock and M5.4's ticket groups and sibling worktree layout. **The narrow one
is what was built**, on M5 §14's own stated principle applied to itself — *each step independently
shippable* — because a phase that can only land behind three others has not been scoped, it has
been sequenced.

It genuinely stands alone, and that is a claim about dependencies rather than a preference:

- **The branch lock (M5.3)** stops `checkout <existing-branch>` walking a worktree off its branch.
  That hazard is identical in both delivery modes and is live in `local-merge` today; pr mode
  neither creates it nor depends on its fix.
- **Ticket groups and the sibling layout (M5.4)** make a *two-repo* ticket expressible. A one-repo
  ticket in a pr repo works now, and every refusal here keys on the **repository** (`Config.For`)
  rather than on the ticket — so it is already the right shape for one member of a group when
  groups arrive.
- **What is NOT built, and must not be mistaken for built:** the detached worktree at
  `origin/main` and the learned branch (M5 §7.1), the Bash gate's rewrites (M5 §7.2), the observer
  (M5 §10), and any contact with a forge at all. Dodona still names `ticket/N` and still records
  it, so a lane that cuts its own branch the project's way leaves that record stale. **Nothing
  destructive reads it in pr mode** — the land is unreachable and the one remaining branch
  deletion is switched off (D-R30's neighbour, in `AbandonTicket`) — which is why the stale record
  is a gap and not a hazard. It is M5.5's to close.

**And the half that ships is the half a suite can prove.** §17 forbids a suite that touches the
network, and mocking a forge into the daemon to get one would be testing the mock. So R7 is the
refusal — no merge, no merge token, no branch deletion, no approval question — at 18 checks
across `m1` and `m2`, **14 of them seen red**. The other four are the *stays unchanged* controls
(a pr repo still gets its worktree, still assembles the record, still promotes a refused write,
still prunes on abandon); they are VACUOUS against HEAD by construction, because HEAD has no such
field, and they are kept and labelled for the same reason `m2`'s
`stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is kept.

**D-R29. THE RECORD BECOMES THE PR DESCRIPTION BY BEING READABLE, NOT BY DODONA FORMATTING ONE.**
§7 says the record *becomes* the PR description and the review *becomes* PR review comments, and
that reads like a renderer — a `ticket-record --pr` emitting a body. **Rejected.** A PR body's
shape is ceremony: the title convention, the checklist, the issue-closing keyword, the template
the repo already has. M5 §1 assigns ceremony to the project, so an opinionated Dodona-rendered
body would be Dodona doing precisely what R7 exists to stop doing. What R7 owes is that the
material **exists and can be fetched at the moment it is wanted**, and it does: `dodona
ticket-record <ticket>` returns the whole record as JSON, the review is in `manager_review`, and
the pr-mode token refusal **names that command in its rewrite**, so the agent meets it exactly
when it is about to write the PR. The agent writes the description its project's way, out of
facts Dodona assembled.

**D-R30. A `pr` REPOSITORY RAISES NO APPROVAL QUESTION, AND NOTHING REPLACES IT.** R6's ask exists
because Dodona holds the merge: `yes` is `Store.TicketApprove`, whose only job is to unblock
`token-request` — and that command refuses outright in pr mode. So the question would be
offering a merge Dodona will never perform, and `AskToLand`'s own rule already decides it:
*nothing to ask is a state, and a question nobody can act on is worse than no question.* **D-R10
is untouched, in the safest possible direction** — the forge's merge button is further from the
manager than the operator's `approve` ever was, and there is still no path from a review to
`TicketApprove`.

A *"ready for its PR"* pane announcement in its place was written and then removed. `AskToLand`
is called twice per finished turn — once by the record, once by the review's `finally` — so it
would have been two pane lines per turn about a decision Dodona is not part of: D-R18's
never-stuck fix turning into never-quiet. Nothing a person needs is lost. The record is still
assembled and still readable, the manager still reviews it, and a send-back still lands in the
lane's own pane exactly as in `local-merge`. What goes is only the surface that existed because
Dodona held the merge.

**D-R31. AN UNRECOGNISED `delivery` VALUE READS AS `pr`, NOT AS `local-merge`.** Only the absent
key and the exact word `local-merge` — trimmed and case-folded — permit merging. `"PR"`, a typo,
`true` and an empty string all read as `pr`. **The fold is asymmetric on purpose, and which way
it leans is the whole safety argument.** Wrong towards `pr` refuses a land: loud, immediate, and
repaired by correcting one word. Wrong towards `local-merge` advances a ref in a repository whose
owner said not to, which is the single irreversible act in this system (P0.1's reasoning, one
field over). Nothing existing can break on the strict reading, because at `ee3464f` no repository
carries the key at all. `Config.Load` reads a non-string through `ToString()` for the same reason
— so `"delivery": true` cannot arrive as `local-merge` by way of `Str()`'s fallback.

## 6. Where the human still is

Unchanged, and deliberately: `approve` gates `token-request`, and this repo stays
`"landing": "on-approval"`. The whole flow above happens *before* that yes, which is exactly why a
manager's objection can still do something — until the operator answers, it is only a branch. Two
consequences worth stating:

- If auto-land is ever switched on for a project, **the manager's block becomes the only reviewer**,
  and D-R10's asymmetry is gone. So `"landing": "auto-when-green"` must additionally require the
  manager not to have objected. Named here so it is not discovered later.
- The operator answers **one** question per finished ticket, and it arrives in front of them
  (`WORK-ISOLATION-PLAN` D-7's question row + overlay). Nothing to hunt.

## 7. PR-mode repositories get the same thing

**BUILT 2026-08-21 as R7 — the refusal half of it; read D-R28 for the line between what is in
and what is still M5.5's.** For a repository whose `dodona.json` says `"delivery": "pr"`
(CLAUDE.md §5.2, `M5-DELIVERY-PLAN.md`), §3's local merge is not Dodona's to perform. The same record becomes the
**PR description**, and the manager's review becomes **PR review comments** — same assembly, same
send-back, different back end, and the forge's own merge button replaces D-R2. The write-up is worth
more there, not less: it is what a human reviewer reads first.

## 8. Phases

| | what | proof |
|---|---|---|
| **R1 — BUILT** | §3's flow: `land` merges main into the branch, re-verifies **in the worktree**, then fast-forwards. Verify moves ahead of the merge (absorbs `WORK-ISOLATION` P4). | `m1`: a ticket whose main has moved lands without human intervention; a red verify leaves main's sha **unchanged**. `dev prove` first — the phase most likely to look green against the old order. |
| **R2 — BUILT** | D-R4's silent-drop check. | `m1`: a branch that resolves by reverting a file main changed is refused, and the message names the file. Fixture: land one ticket, then have a second resolve by discarding it. |
| **R3 — BUILT** | D-R5: retire the three refusals (**four**, see below). Re-aim `m1`'s two gate checks and `m2`'s backstop check rather than deleting them. | `m1`: two tickets over one path both get created; an agent writes freely across its own worktree; the gate still refuses the **shared checkout** (layer 1 untouched). |
| **R3.5 — BUILT** | D-R14: the land comes OFF the serial control pipe. `land` returns *landing…* and the outcome arrives as an announcement; the token stays held across the whole flow and a failure still leaves main untouched. | `m1`: a land whose verify takes seconds does not block a concurrent `status`/`say` on the same daemon; the outcome still reaches the caller; the existing land checks pass unchanged against the new reply shape. **All three held, measured** — see D-R14. |
| **R4 — BUILT** | D-R8's record, assembled at completion. Gated on the worktree having changed (D-R13). The verify result is **reported, never run** — see D-R15, which is the decision this phase could not be written without. | `m1`: a finished ticket produces exactly one record carrying diffstat, verify result, drop-check and the agent's report; a chatty lane produces no second one; and **an adopted lane still produces one after a daemon restart** — the wiring lives in one place called from both the spawn and reconcile, which is where §3's dead-routing-ladder failure would otherwise reappear. 13 new checks, all seen red; `m0` gains the no-summon assertion for the read verb. |
| **R5 — BUILT** | D-R9/D-R10/D-R12: the manager reads it, may send back, bounded at three, and **cannot approve**. Triggered by the record existing rather than by a third `OnResult` consumer, and fired after `_recordLocks` is released — see D-R16/D-R17/D-R18. | `brain`: a send-back reaches the lane as input; a manager "approval" grants **nothing** (unapproved, no `ticket_approved`, `token-request` still refuses); the cheap tier escalates when unsure; and **three send-backs is the bound with a daemon restart in the middle of them**, because the count lives in the store. `m1` asserts the other side of D-R17: with autostart off no judgement agent is started at all. 7 new checks, all seen red. |
| **R6 — BUILT** | D-R11: the write-up renders in the approval ask (absorbs `WORK-ISOLATION` P5). Raised by the RECORD, never by the review — see D-R19, the decision the phase turns on. | `ui-use`: the ask renders at a live window with no review having run, carries what code knows, offers no path, and answering it takes the ticket from *token refused* to *token granted*. `brain`: the manager's note is in the row, the bound says so, and a verdict — `approve` included — never answers it. `m1`: the unapproved refusal names the question. 12 checks, all seen red. |
| **R7 — BUILT** | §7 as a **refusal**: `"delivery": "pr"` means Dodona never merges, never grants a merge token, never deletes a branch and raises no approval question. D-R28 (scope), D-R29 (the record is the description by being readable), D-R30 (no ask, and nothing replaces it), D-R31 (an unrecognised value reads as `pr`). | `m1`, not `publish`/`workspace` as this row first guessed: the land, the token and the record all live in m1's fixture, and a dodona.json rewrite flips the mode with no daemon restart (`Config.For` re-reads per call), so it needed no second fixture. `m1`: a pr repo grants no token even once approved, its land leaves main's sha unchanged and keeps branch and worktree, `land_started` is never written — and the SAME ticket lands once the repo is `local-merge` again, which is what stops the whole section reading as "landing is broken". `m2`: the other branch deletion, on abandon, is switched off while the worktree prune stays. 18 checks, 14 seen red; the 4 vacuous ones are the stays-unchanged controls and say so. |
| **R8 — BUILT** | D-R23/D-R24: the reviewer may REQUEST named files when something concerns it — narrow, once, and recorded — and a send-back on `verify: red` does not spend a round. Both amend D-R12. Three decisions came out of building them: D-R25 (the exempt round is its own event kind), D-R26 (one exemption per verify RESULT, which is the terminator D-R24 lacks), D-R27 (the details round is shared by both tiers and bounded by the record's own `changed` list). | `brain`: a review that asks for a file gets that file and no more, asks only once, and its request is in the `manager_review` row; a send-back with `verify: red` in the record leaves the send-back count unchanged, and one with `not-run` still counts. **The exemption must be proved from the RECORD, never from the model's stated reason** — a fake manager claiming red must earn no exemption, and that is the check most worth writing first. |

**R1–R3 are the correction and come first**: R1 makes concurrent landing work at all, R2 makes
agent-resolved conflicts safe, and R3 removes the machinery that was standing in for R1. **R3.5
comes next and before R4** (D-R14): R4 assembles its record on the land path, so building it first
means building it inside a call that freezes the daemon and then moving it. R4–R6 are the review.
R7 is the foreign-repo case and could wait — it waited, and then landed narrow (D-R28).

**AND R3 LEFT A PROMISE R5 HAS TO KEEP — KEPT, 2026-08-20.** R3 retired the file reservations on
the operator's reasoning that *"if that is problematic in some way, it's the manager's job to say
something about it"* — and that manager did not exist. Overlaps and branch touches were recorded
(`claim_overlap`, `branch_touched`) and **nothing read either**. Nothing was unsafe, because the
real gate never moved: every land still needs the operator's explicit `approve`, and this repo is
`on-approval` by default (verified in code, 2026-08-20). But the second pair of eyes R3 assumed
was an empty chair, which is the honest reason R4–R6 were not optional polish.

**The chair is filled now, and one half of the promise is still outstanding.** R5's manager reads
the completion record — the diff's shape and the agent's own words — and can send the work back.
What it is not yet handed is `claim_overlap`: *another ticket is working the same paths* is a fact
R3 records and the review does not read, because it is a statement about a SECOND ticket and the
record is about one. That is duplicated effort — the overlap the operator actually cared about —
and it belongs to whoever adds a cross-ticket view, not to a single ticket's review. Said here so
it is not mistaken for done.

## 9. Rejected — do not re-propose

- **Declared file claims as a lock.** The operator's decision above, plus the measurement that
  disjoint claims do not make concurrent landing work anyway. Kept only as a derived signal (D-R7).
- **Preventing two agents from touching one file.** Files are not the unit of work. This forbids
  ordinary development, and the overlap that genuinely matters is duplicated *effort*, which is a
  judgement for the manager to raise, not a lock.
- **The manager approving a land.** D-R10. It may block. A model as sole gate on an irreversible ref
  advance is the "a prompt providing safety" trap, whatever the model is called.
- **Asking the operator to resolve every conflict.** D-R3: ordinary conflicts are ordinary. Reserve
  the interruption for the ones the agent could not resolve.
- **Letting the agent resolve a conflict it does not understand, silently.** The other side of the
  same coin: abort, report, ask.
- **Feeding the whole diff to the manager on every turn.** Quota, and D-R13's completion trigger.
- **An unbounded send-back loop.** Everyone polite, nothing finished.
- **Keeping the `token-request` backstop "just in case".** It answers whether reality matched a
  prediction; with the prediction retired it has no question left. §5 reads reality directly.
- **Rebasing the ticket branch instead of merging main into it.** Either is defensible in general,
  but a rebase replays every commit and can hit the same conflict repeatedly, which for an agent
  means resolving the same thing N times. Merging main in resolves once. (If linear history on main
  is later wanted, squash at land — not rebase mid-flight.)

## 10. Traps this will hit

- **`rt.OnResult` is an ASSIGNMENT, not `+=`.** A second consumer overwrites the compressor and
  silently kills selective compression, which presents as "the panes went verbose" with nothing
  pointing here. (`WORK-ISOLATION-PLAN` Appendix A already carries this; it is now R4's problem.)
- **The silent-drop check must diff against the MERGE BASE**, not against main's tip after the merge.
  Against the tip, a branch that reverted main's change and a branch that never saw it look
  identical. **The first half of that is right and the prescription is wrong — CORRECTED IN R2,
  2026-08-20, after it was built as written and measured blind.** "Not against main's tip" is
  correct and load-bearing. But "the merge base" cannot be obtained once main has been merged in:
  `merge-base main branch` *is* main's tip at that point, so the naive form is the very thing the
  trap warns about. Recovering a fork point from the branch's merge commits does not rescue it
  either, and this is the part that had to be measured: **a ticket branch's ancestry contains
  main's own merge history**, so walking `rev-list --first-parent --merges <branch>` finds the
  merges of every previously landed ticket, and the oldest resolved to the repository's **init
  commit** — identically for tickets 1 through 7. Against init the dropped file did not exist yet,
  the pre-image never matched, and the check passed everything while looking armed.

  The reference point that works is **`M^1`**: the branch tip immediately before the merge that
  brought main in (`MainMergeOnBranch` finds the newest such merge and returns it with its first
  parent). A path is a drop when the merge changed it and the branch's final version is
  byte-identical to `M^1`'s. That is defined by the merge itself, cannot be confused with anything
  in main's history, and honours the trap's actual intent. Two guards keep it honest: the merge's
  second parent must be an ancestor of main, and the merge itself must **not** be — otherwise an
  inherited merge qualifies and the blindness returns.
- **`land` needs `main` checked out in the shared checkout** and refuses otherwise. True while
  CLAUDE.md §0.0 keeps the operator there, but it must **announce** rather than fail quietly.
- **A conflicted merge leaves the worktree dirty.** Anything that gives up must `git merge --abort`,
  or the next turn starts in a half-merged tree and every subsequent check lies.
- **Retiring the claim lock touches live checks, and deleting them would drop coverage silently.**
  `m1`'s `gate_allows_inside_claim` / `gate_denies_outside_claim` and `m2`'s
  `backstop_refuses_outside_claim` must be **re-aimed at layer 1** (the shared checkout), which is
  the guarantee that remains. A green suite with fewer real assertions is the failure this project
  keeps paying for.
- **The merge must happen while the token is held**, or main moves between the merge and the
  fast-forward and D-R2's guarantee evaporates. The token already exists; the ordering is the trap.
- **Two lanes must never both merge main in and then both ff.** Same trap, stated as the race it is:
  the token is what serialises it, so `token-request` has to come **before** the in-worktree merge,
  not before the fast-forward.
- **`git stash` is repo-global** (CLAUDE.md §5.2). Nothing in this flow may reach for it to park a
  dirty worktree; commit to the ticket branch instead.

## 11. Open questions

- ~~Does the manager review need the diff **content** for a first pass?~~ **ANSWERED BY THE
  OPERATOR, 2026-08-21 — and not by measurement, because it is a question about what this system
  is FOR.** The reviewer may **ask** for the details when something in particular concerns it, and
  otherwise must not. Their words: *"I think a reviewer can request the details if it thinks it's
  necessary. If there's something in particular that concerns it. Otherwise, definitely shouldn't.
  We don't wanna hold agents up forever, because your costs are gonna balloon dramatically if you
  do stuff like that. A person that's serious about reviews can ask for it themselves, or get you,
  mister, to do it. These kinds of systems — Dodona itself is geared towards performance and speed.
  Move fast and break things."*

  So the default is unchanged and now permanent: **diffstat plus the agent's own report, and the
  reviewer never reads the diff by default.** What is new is the escape hatch, D-R23. Note what the
  operator also settled in passing: a review that is genuinely worth a full read is a **person's**
  job — theirs, or an agent they ask directly — and this reviewer is not the place to buy it. That
  closes the "affordable reviewer versus expensive one" framing rather than answering it: an
  expensive one is not wanted at any price, because the cost is paid in **agents held up**, which
  is the resource this system exists to protect.
- ~~Should a send-back count against the loop bound when the manager's objection was
  **mechanical** (tests red) rather than judgement?~~ **ANSWERED BY THE OPERATOR, 2026-08-21: it
  DEFINITELY does not count.** A strike is a judgement the agent has to answer; tests being red is
  a fact the agent can already see, and spending one of three chances on it means an agent gets
  fewer real rounds for having had a red build at the wrong moment. D-R24.

  **The earlier objection to this stands and is what constrains the implementation, rather than
  what refuses it.** R5's position was: *an exemption the model itself classifies is a bound the
  model can talk its way out of* — and that is still true, so the model does not get to classify
  it. **Code does.** The verify state is a code fact already in R4's record (`verify_green` /
  `verify_red`, written by `LandFlow` and reported, never re-run — D-R15), so the exemption keys on
  what the record SAYS, not on the reviewer's account of why it objected. A model claiming "I only
  sent it back because the tests were red" earns nothing.
- ~~Where does the review write-up live so the operator can read it after the fact — a pane
  announcement, the question row, or both?~~ **ANSWERED BY R6: BOTH, and they are different
  jobs.** The **row** is where the decision is made, because it is the thing that can be
  *answered* — one open `questions` row of kind `land` per ticket, refreshed in place as the
  review comes back, rendered by the overlay live and by `ui dump`'s `ask` key headless. The
  **pane** carries one announcement when that row OPENS (so somebody who closed the window finds
  it again), and R5's two — the bound, and an undelivered send-back — and nothing else: a line
  per manager round would be the never-stuck fix turning into never-quiet. What is deliberately
  NOT rendered anywhere is the `message` written for the agent; it is already in the lane's pane
  as the send-back itself.
- ~~`dev gate` in a cold worktree is ~250 s. Re-verifying after merging main in doubles the verify
  cost on every land. Is the touched-suites subset the right default, with the full gate only at the
  land itself?~~ **ANSWERED BY MEASUREMENT, R1, 2026-08-20 — and the answer turned on something
  this question did not anticipate.** Measured in a cold worktree on this machine: `dev build`
  **2.5 s**, then `dev gate` **273.1 s** (suites 259.3 s, inside the 300 s I7 budget), **275.6 s**
  total; against **~17 s** for `dev test unit m1 m2`. Two findings:

  - **`dev gate` alone cannot be the verify step at all.** A ticket worktree is a fresh checkout
    that has never been built, and `dev test`/`dev gate` *refuse* an absent build output rather
    than testing the previous binary (P1.5). So D-6's literal `dev gate` refuses **every** land
    with `has never been built`. `verify` is an array; the first step is `dev build`.
  - **The full gate would hang the daemon for 4.6 minutes per land.** The control pipe is serial —
    one `NamedPipeServerStream` instance, `HandleAsync` awaited inline — and `LandOp` runs on it.
    So the whole daemon answers nothing for the duration of verify: no UI, no lane input, no
    `say`. At 275 s that is CLAUDE.md §0.1's *never hung* violated by four and a half minutes, on
    the one operation an operator is definitely watching.

  **And the full set was never authorized here at all** — which is the part D-6 got wrong, not the
  timing. The operator's standing directive (CLAUDE.md §0.1, 2026-08-20): *"I don't have the
  bandwidth to run those heavy handed test suites all the time. Run it sparsely only when you
  actually absolutely have to. And then when you do run it, don't run everything. Run modules that
  matter."* D-6 wrote `dev gate` into **every land** without that having been asked for. So the
  subset is **the default, not a stopgap**, and this question is closed rather than deferred: do
  not widen it back on the grounds that a subset might miss something.

  The asynchronous land is still worth doing — but for its own reason, which is that a land must
  not freeze the daemon, **not** as a way to afford the full gate. It now HAS its own decision and
  phase: **D-R14 and R3.5, landed 2026-08-20**. Landing it did not reopen this question, and
  `dodona.json`'s `//verify` block now says so at the point of use — the freeze argument is
  repaired there rather than deleted, precisely so a future reader cannot mistake a repaired
  argument for permission to widen the array back. Note that the verify cost is **not** doubled the way
  this question assumed: R1 does not add a verify, it *moves* the one that already existed from
  after the ref advance to before it. What is genuinely new is that the agent's own verify during
  development and the land's verify are now two runs of the same thing.

## Appendix A — implementation touchpoints

Named symbols, verified against the tree at `bc65ff8`, so a session picking this up does not have to
re-derive the map. **Line numbers rot; names do not.** This appendix exists because the isolation
plan shipped without one and handing it off meant re-deriving everything (`a403fbc`).

**One phase per commit**, in §8's order, each with its proof.

### R1 — the merge flow

- **`LandOp(long tid, out bool ok)`** (`Daemon.cs`) — the whole land. Today, in order: ticket open,
  repo resolved (`RepoOf`), token holder + lease, `main` checked out in the shared checkout, then
  `Git.Run(repoPath, "merge", "--ff-only", t.Branch)`, then `_store.LandCommit`, then the verify
  loop over `cfg.Verify`. **Two changes:** the in-worktree merge and re-verify go BEFORE the
  fast-forward, and the verify loop moves with them (`WorkingDirectory = t.Worktree`).
- **`Git.Run(string workDir, params string[] args)`** (`Git.cs`) — every git call. The new ones are
  `merge <main>` and `merge-base` run with `workDir = t.Worktree`, not `repoPath`.
- **The `token-request` handler** (`Daemon.cs`, `case "token-request"`) — the merge-main-in step must
  happen while the token is HELD, so it belongs after the grant here or at the top of `LandOp`, never
  before the grant (§10's race).
- **`cfg.Verify`** via **`Config.For(_primary, repoPath)`** — the steps. D-6: `dodona.json`'s
  `verify` becomes `dev gate`, because a bare `dotnet build` reports a locked output as
  `Build FAILED` (CLAUDE.md §1) and nobody is watching when this runs.
- **`_store.LandCommit(tid, tokenId, out var reason)`** — the store fence. Unchanged, still after the
  fast-forward.

### R2 — the silent-drop check

- Model it on the **existing backstop** in the `token-request` handler, which already does
  `Git.Run(reqPath, "diff", "--name-only", $"{reqCfg.Main}...{t.Branch}")`. That block is what R3
  deletes; R2 replaces its *question* rather than its plumbing.
- **The merge base is mandatory** (§10): `git merge-base <main> <branch>`, then diff base…branch.
  Against main's tip a branch that reverted main's change and one that never saw it are identical.

### R3 — retire the three refusals

- **`GateHook()`** (`Program.cs`) — question 2, the `claim-check` call. **Delete question 2, keep
  question 1** (`tree-check`, which fails CLOSED and is layer 1). The ordering comment there explains
  why the two exist; it needs rewriting rather than trimming.
- **`case "claim-check"`** (`Daemon.cs`) — the handler. Keep the command (it is a useful read and
  `workspace`'s drift check uses it) but nothing gates on it.
- **`Store.TicketCreate` → `FindConflicts`** (`Store.cs`) — the overlap refusal, `WHERE t.state =
  'open'`. Stop refusing; the conflict list becomes information.
- **`MakeTicket`** (`Daemon.cs`) — `TicketMade.Conflicts` is how both callers see it. Promotion
  (`PromoteLane`) currently degrades to a refusal on conflict; after R3 it proceeds and the overlap
  becomes the manager's business (D-R5).
- **`PromoteLane`** — the seed claim `path:<rel>` goes. A ticket with no claim must be legal:
  `ticket-create` currently refuses `claims.Count == 0` ("at least one --claim required").
- **`Claims.cs`** stays — the algebra is still how a derived signal is compared, and `claim-extend`
  still exists for anyone who wants to annotate a ticket by hand.
- **Checks to RE-AIM, never delete** (§10): `m1:gate_allows_inside_claim`,
  `m1:gate_denies_outside_claim`, `m1:overlap_refused_at_plan_time`,
  `m2:backstop_refuses_outside_claim`. Point them at layer 1 — the shared checkout is still refused —
  and at the new facts. A suite that keeps its count while asserting less is this project's
  most-repeated failure.

### R3.5 — the land off the pipe (BUILT; this is the map R4 builds on)

- **`LandBegin(long tid, out bool started)`** (`Daemon.cs`) — what `case "land"` calls now. The
  cheap gate, then `Task.Run(...)`, then the *landing…* reply. Its doc comment carries the protocol
  and the two constraints; read it before adding anything to the land path.
- **`LandGate(long tid, out LandPlan? plan)`** — the milliseconds that stay on the pipe. Returns a
  refusal string, or null plus the `LandPlan` the expensive half runs on (`Ticket`, `RepoPath`,
  `Cfg`, `TokenId`, resolved once so the two halves cannot disagree about which repository).
- **`LandFlow(LandPlan plan, out bool ok)`** — the old `LandOp` body from the in-worktree merge
  onward, unchanged in content and order. **R4's record assembles here or beside here**, which is
  the reason D-R14 came first: this is off the pipe, and `LandOp` was not.
- **`LandRun`** + **`_lands`** (`ConcurrentDictionary<long, LandRun>`) — one land, in flight or
  finished, in memory only. `Done` is volatile and written last, so a reader that sees it also sees
  `Ok` and `Message`.
- **`case "land-status"`** (`Daemon.cs`) — `state=running elapsed=Ns` | `state=done ok=0|1` plus the
  outcome | `state=none`. **It must never summon a daemon** (CLAUDE.md §3.2).
- **`LandCli(long tid)`** (`Program.cs`) — the polling client, and `--no-wait`. `Client` grew an
  optional `capture` list (so the poll does not print every tick) and a `neverSummon` flag (inside
  `LandCli` the parsed `cmd` is still `"land"`, so the name-based no-summon test would miss it).
  `--no-wait` had to be added to `boolFlags` — a valueless flag that is not declared there eats the
  next argument, which is the incident that comment records.
- **`_lanes` is a `ConcurrentDictionary`** now, and `.Remove(k)` became `.TryRemove(k, out _)` at
  eight sites.
- **`Blockers(NewBuild nb)`** — unchanged, and it is what stops a swap cutting a land in half. It
  covers an in-flight land only because the token is held throughout.

### R4 — the completion record (BUILT; and it does NOT share R1's verify runner — D-R15)

- **`HookTurnEnd(LaneRuntime rt, string role)`** (`Daemon.cs`) — was `HookCompression`, renamed
  because it now wires TWO consumers and a name that says "compression" is how the next person
  overwrites one of them. `OnResult` is a single delegate field, so the trap §10 named is real:
  the composition is one lambda in one method, both consumers are called by name, and **each is
  in its own `try`** — `OnResult` is invoked from the wire pump *outside* its try/catch
  (`LaneRuntime.OnLine`), so one consumer throwing would take the other and the pump with it.
  **Called from BOTH construction sites**: `SpawnLaneAsync`, and reconcile's adoption loop. The
  second is the one that goes quietly dead — a daemon restarts on every publish, so a record
  wired only at spawn stops happening for every lane the operator already had, which is §3's
  routing ladder exactly. `m1` restarts the daemon and demands a record from an adopted lane.
- **`CompletionRecord(long laneId, long paneEventId, string body)`** — the trigger: finds the
  lane's open ticket (silent when there is none — a plain lane's turn is the common case and has
  no PR to shape) and hands off to a `Task.Run`, because this shells out to git several times and
  the pump is what delivers the agent's output to the pane.
- **`BuildRecord(...)`** — the assembly, and every giving-up path names itself:
  `completion_record_impossible` (no repository, no worktree, or git could not read the tree),
  `completion_record_unchanged` (D-R13's gate), `completion_record_failed` (it threw). An empty
  record, or a silent return where one was expected, is the fail-open this codebase has paid for
  twice.
- **`Digest` / `DigestOf`** — D-R13's gate value: 16 hex of SHA256 over the branch tip plus
  `git status --porcelain`, so committed *and* uncommitted work move it. Read back **out of the
  previous record's own event**, never held in memory: an in-memory digest would emit a duplicate
  record on the first turn after every publish and still look correct in a single-daemon test.
  An unreadable digest compares UNEQUAL on purpose — one duplicate record is a cost, a gate that
  silently swallows every completion is a phase that does nothing.
- **`_recordLocks`** (`ConcurrentDictionary<long, object>`, one per ticket) — held across
  read-decide-write. Concurrent for the same reason `_lanes` became concurrent in R3.5: written
  from background threads while the control pipe reads the store beside it.
- **`Store.LastTicketEvent(long ticket, params string[] kinds)`** — the newest event of those
  kinds *about* this ticket. `LastEventDetail` could not serve: the land's events are all
  lane-less (`Event(kind, null, …)`), and `LIKE 'ticket 7%'` also matches ticket 71 — a verify
  result attributed to a neighbouring ticket is exactly the quiet wrongness this phase removes,
  so the boundary is spelled out in SQL.
- **`case "ticket-record"`** (`Daemon.cs`) + **`"ticket-record"`** (`Program.cs`) — a READ, on the
  **no-summon list** beside `status` and `land-status`: it is what R5 and any script will poll, so
  a summoning version turns "read the record" into four warm-up model processes, repeatedly
  (CLAUDE.md §3.2). It assembles nothing — a command that built a record on demand would be a
  second, differently-timed producer of one artifact, which is `MakeTicket`'s lesson. It exists
  because R6 is the surface a person reads this through, and until then an affordance no verb can
  reach is where the next defect lives.
- **No pane row, deliberately.** A record needs nobody, and §4's rule is that attention is owed
  when a person is *needed*. R6 is where it reaches people.
- **`m1:main_advanced` had to be RE-AIMED**, not because R4 changed the land but because it
  compared main's tip *subject* to the literal `'water v2'` and R4's fixture adds commits to the
  same branch. It now compares main's sha to the branch tip captured before the land, which is
  D-R2's actual property and strictly stronger than a commit message. `dev prove` calls that
  VACUOUS by design (it is a stale-test repair, not a code claim).

### R5 — the manager review (BUILT; this is the map R6 builds on)

- **`ManagerReview(Store.TicketRow t, long laneId, string project, string recordJson)`**
  (`Daemon.cs`) — the whole phase. Called from **the tail of `BuildRecord`, after `_recordLocks`
  has been released**, and it does its work on a task of its own: belt and braces, because either
  one alone is easy to lose in a later edit, and firing inside the lock would hold one ticket's
  lock across a 25 s model call. Its doc comment carries D-R10 and both bounds; read it before
  adding anything.
- **`ManagerQuestion(t, recordJson, rounds)`** — the prompt. D-R12's bound on *reading* is in it:
  diffstat, changed-file names, uncommitted count, drop check and the agent's own report; never the
  diff content (§9). **It spells out what `verify: not-run` and `drop: moot` mean**, because both
  are the normal case and a reviewer that read either as red would send every ticket back forever.
- **`SendBackAsync(t, laneId, round, message, note)`** — `LaneRuntime.Say`, the same path a typed
  sentence takes, so the agent keeps its warm context. Waits for a disconnected lane with a
  deadline (20 s) and then **announces the message plus the two commands that deliver it by hand**
  rather than dropping it; it does not respawn, because that is forty lines `case "lane-respawn"`
  already owns and a second copy would drift. `Say` writes the pane's `user_input` row itself, so
  the `[manager review, round N of 3]` prefix is what distinguishes a send-back from the
  operator's own sentence.
- **`SendBackHistory(long tid)`** + **`Store.TicketEvents(ticket, limit, kinds)`** — D-R12's *with
  the history attached*. The rows are the only copy.
- **`Store.CountTicketEvents(ticket, kinds)`** and **`SendBackBound = 3`** — the loop bound. See
  D-R16 for why the counted kind is `manager_sent_back` and not `brain_review`, and
  `Store.AboutTicket` for the `LIKE 'ticket 7%'` boundary the two readers now share.
- **Reused from `BrainReview` rather than extended:** `EnsureBrainAsync(hi: false, project)`
  (ensure at the point of use, never look up — CLAUDE.md §3), `BrainLock(loId)`,
  `_lanes[loId].AskAsync(q, 25000)`, and the low-confidence escalation to
  `AskBrainHiAsync(q, project)`. Which tier answered is a **field of the one review row**
  (`tier: lo|hi`) rather than an event of its own: one review, one row, and R6 reads that row.
- **Event kinds, and every giving-up path has one** (`DEBUGGING.md` carries them):
  `manager_review` (every verdict, with the note), `manager_sent_back` (a delivered send-back —
  the countable one), `manager_bound_reached`, `manager_review_skipped` (autostart off, or no
  judgement agent for the project), `manager_review_failed` (timed out, unparseable, threw, or a
  send-back with nothing to say), `manager_send_back_undelivered`.
- **D-R10 in code:** `send-back` is the only verdict that does anything, so a reply of
  `{"verdict":"approve"}` lands as *no objection*. There is no call to `Store.TicketApprove`
  anywhere in this path and `case "approve"` stays reachable only from the operator's `approve` /
  `dodona ui answer`. `brain:a_manager_approval_grants_nothing` asks the fake manager for
  `approve` on purpose and then checks three things: the ticket is unapproved, no `ticket_approved`
  event exists, and `token-request` still refuses.
- **The fake agent grew `mgrverdict:` / `mgrmsg:` / `mgrlow`**, recognised by the question's own
  first line the way brain-hi's granularity question and the concierge's review-behind are. They
  ride in on **the agent's own report**, which is the only channel that could carry them — and
  that is D-R8's point restated: the report is the thing the manager had never once been shown.
- **A fixture trap that cost one run:** a `say` used only to prove the lane was re-adopted ended
  its turn while the round's file sat uncommitted, so the digest had genuinely moved and R4 wrote
  a record for it. The product was right; the fixture had asked the wrong question. Commit before
  asking the lane anything, and make the adoption wait be the round's own `say`. Related: **do not
  assert a total count of reviews** — any turn that moves the worktree earns one. `round` is
  derived from the store, so `no review at round 4` is the precise form of "no fourth objection".

### R6 — the write-up in the ask (BUILT; this is the map R7 and anything touching approval builds on)

- **`AskToLand(Store.TicketRow t, long laneId, string? recordJson)`** (`Daemon.cs`) — the phase.
  Returns the question id, or 0 when there is nothing to ask (an `auto` ticket, an approved one,
  one that is no longer open — each a STATE, not a failure). Two callers: the tail of
  `BuildRecord`, which is the primary moment and the one D-R19 is about, and `token-request`'s
  unapproved refusal, which hands it no record. Its doc comment carries D-R19/D-R20; read it
  before adding a third caller.
- **`LandAskText(t, recordJson)`** — what the ask SAYS. The manager's `note` when there is one and
  what code knows when there is not, never an empty box. Two things in it are load-bearing and
  neither is obvious: **the bound is read as a COUNT** (`CountTicketEvents(tid,
  "manager_sent_back") >= SendBackBound`) and not as an event ordering, or the ask reverts to "not
  reviewed yet" for ever past the bound; and **which event is NEWEST** — one `LastTicketEvent` over
  `completion_record` plus the three review-outcome kinds — is how it knows whether a note belongs
  to *this* change or to a diff that no longer exists.
- **`ApproveTicket(long tid, string how)`** — the operator's yes, in one implementation.
  `case "approve"` and `AnswerQuestion`'s `case Ask.KindLand when !declined` are its ONLY callers
  and **both are a person**. That caller list is the load-bearing part, not the method: no
  timeout answers a `land` question, no default does, and `ManagerReview` has no path to it
  (D-R10). `brain:a_manager_verdict_never_answers_the_approval_ask` is what goes red when a third
  caller appears.
- **`Store.QuestionUpsert(kind, subject, input, candidates)`** — open, or refresh the open one,
  atomically. The record's task and the review's task are two threads writing the same row; the
  store's lock is the only serialisation there is, because `_recordLocks` is deliberately
  released before the review fires.
- **`Store.WithdrawQuestions` / `Store.TicketState` / `LandCommit`** — D-R21. The withdrawal is
  inside the ticket's own state changes, and inside the land's own TRANSACTION.
- **`Ask.KindLand` / `Ask.LandCandidates(ticket)`** (`Ask.cs`) — the third kind. Values `yes`/`no`
  because `AnswerQuestion` reads a literal `no` as a DECLINATION (`withdrawn`, not `answered`),
  and no path, folder or drive letter anywhere — `unit` pins both.
- **`MainWindow.xaml`'s ask overlay** — the question TextBlock is inside a `MaxHeight` ScrollViewer
  now, because R6 made the question a paragraph and an unbounded one pushes the CHOICES off the
  bottom of the window: a question a person can read and cannot answer.
- **No new answer path, and no UI code at all beyond that ScrollViewer.** The overlay,
  `ui dump`'s `ask` key, `ui answer` and `Esc` are Phase 4's and render any kind — which is what
  D-L4's "one component" bought, and it is the reason this phase is mostly daemon.

### R8 — the escape hatch and the exemption (BUILT; both amend D-R12, so read it first)

- **`AskTier(bool hi)`** — a local function inside `ManagerReview`, and the whole of D-R23's
  mechanism. It asks one tier, and **at most once per review** turns a `need` reply into files
  and asks that same tier again. The "once" is a captured `detailsSpent` flag shared by both
  tiers (D-R27), not a sentence in the prompt: a reviewer that can ask, read and ask again is
  D-R12's send-back loop one level down and would look, untimed, exactly like a slow model.
- **`GrantDetails(t, recordJson, want, granted, refused)`** — where the request meets the record.
  Three refusals, each closing a different way of asking for everything: not in the record's own
  `changed` list, past `DetailsFileCap`/the byte budget, or resolving outside the worktree. **A
  refused request still spends the round.** Everything refused comes back for the row, because an
  escape hatch nobody can see being used is one nobody can judge (D-R23's third property).
- **`ManagerQuestion(..., string? details)`** — one parameter, two shapes. Without details it
  offers the hatch and says the default is permanent; with them it says *this is your ONE look,
  a second request will not be read*. Telling a reviewer it may ask, while holding what it asked
  for, would make the refusal look like the system swallowing its question.
- **`RecordVerify` / `VerifyWhenOf`** — D-R24's code fact and D-R26's identity for it. Both fail
  toward **spending** a round on anything unreadable, which is the safe direction: the failure
  being avoided is a bound that quietly stops bounding.
- **The `manager_review` row gained five fields**: `verify` (the code fact the exemption keys
  on), `exempt`, `details`, `detailsWhy`, `detailsRefused` — and `round` now means *the count
  after this send-back*, so an exempt round leaves it where it was. R6's `ReviewLine` is the
  other end and had to change with it: *"round 2 of 3"* for an objection that spent no round
  would misreport how many chances are left, on the one screen where that number is being used
  to decide something.
- **The check most worth having is `brain:a_manager_claiming_its_objection_was_mechanical_earns_no_exemption`.**
  `dev prove` can only show it red against HEAD, where the fields do not exist yet — so the
  claim itself was proved the other way, by keying the exemption on the model's words on purpose
  and reading the red: `"verify":"not-run","exempt":true` against a fake manager that had merely
  *said* the tests were red. That is R5's objection reproduced as a failing check, and it is why
  D-R24's implementation is code and not classification.
- **The fixture writes no `verify_red` by hand.** It approves, requests the token, swaps
  `dodona.json` to `verify: ["exit 3"]` and runs a REAL land, because `LandFlow` is the only
  producer of that event and a hand-inserted one would prove the suite's idea of the record
  rather than the land's. 6 new checks, all seen red.

### R7 — PR mode (BUILT 2026-08-21; D-R28 is the scope, and read it before extending this)

- **`Config.Delivery`**, with **`Config.IsPr`** and the static **`Config.DeliveryIsPr`** beside it
  (`Daemon.cs`). One spelling, owned by `M5-DELIVERY-PLAN.md` §3: `"pr" | "local-merge"`, default
  `local-merge`. The fold is asymmetric — D-R31.
- **`LandGate`** (`Daemon.cs`) refuses a pr repo, ahead of the token check. That is the whole
  structural guarantee: `LandFlow` is the only code that merges main in, fast-forwards, prunes a
  worktree or deletes a branch, and the cheap half returning early makes all of it unreachable —
  so it is one refusal rather than four conditionals sprinkled down a flow. `land_refused_pr_mode`,
  and `land_started` is consequently never written.
- **`case "token-request"`** refuses **ahead of the approval gate**, or an `on-approval` ticket is
  sent to `dodona approve` to unlock a token it can never be given. `token_refused_pr_mode`. The
  repository is resolved at the top of the handler as a read; the refusal for an *unresolvable*
  one stays where it was, so no existing refusal changed order.
- **`AskToLand`** returns 0 for a pr repo and records `land_ask_skipped_pr_mode` — D-R30.
- **`AbandonTicket`** keeps the branch and still prunes the worktree, and the receipt says `KEPT`.
  This is the second and more dangerous of the two branch deletions: the branch may already be
  pushed with a PR open on it, and an abandon undoes *Dodona's* ticket, not the project's work.
- **`case "repos"`** prints `delivery=pr` when it is true and nothing when it is not: it changes
  what half that line MEANS (a token nobody can hold, a land nobody will run).
- **Unchanged on purpose, and asserted:** `ticket-create`'s worktree, R4's completion record, R5's
  manager review and its send-back. §7's *same assembly, same send-back, different back end* is a
  property to preserve, not a thing to rebuild.

### How to work on it

Per CLAUDE.md, and none of it optional: a worktree of your own; `dev check` before starting;
`dev test unit` (~1 s) while iterating and the one or two suites the change touches; **`dev prove`
every new check before believing it** — and note that `dev prove` does not judge `unit` checks, so
for those break the function on purpose, read the red, revert, and record what the red said;
`dev gate` once before merging to main; `/ship` to deliver, whose landing step is now explicit about
a project's own process. A suite that prints no tally is a failure, not a shrug.
