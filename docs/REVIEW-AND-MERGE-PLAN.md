# Review and merge — the ordinary developer flow, with a manager as the reviewer

Status: **plan, not built.** Written 2026-08-20 from the operator's brief, after tracing the land
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

For a repository whose `dodona.json` says `"delivery": "pr"` (CLAUDE.md §5.2,
`M5-DELIVERY-PLAN.md`), §3's local merge is not Dodona's to perform. The same record becomes the
**PR description**, and the manager's review becomes **PR review comments** — same assembly, same
send-back, different back end, and the forge's own merge button replaces D-R2. The write-up is worth
more there, not less: it is what a human reviewer reads first.

## 8. Phases

| | what | proof |
|---|---|---|
| **R1** | §3's flow: `land` merges main into the branch, re-verifies **in the worktree**, then fast-forwards. Verify moves ahead of the merge (absorbs `WORK-ISOLATION` P4). | `m1`: a ticket whose main has moved lands without human intervention; a red verify leaves main's sha **unchanged**. `dev prove` first — the phase most likely to look green against the old order. |
| **R2** | D-R4's silent-drop check. | `m1`: a branch that resolves by reverting a file main changed is refused, and the message names the file. Fixture: land one ticket, then have a second resolve by discarding it. |
| **R3** | D-R5: retire the three refusals. Re-aim `m1`'s two gate checks and `m2`'s backstop check rather than deleting them. | `m1`: two tickets over one path both get created; an agent writes freely across its own worktree; the gate still refuses the **shared checkout** (layer 1 untouched). |
| **R4** | D-R8's record, assembled at completion. Gated on the worktree having changed (D-R13). | `m1`: a finished ticket produces exactly one record carrying diffstat, verify result, drop-check and the agent's report; a chatty lane produces no second one. |
| **R5** | D-R9/D-R10/D-R12: the manager reads it, may send back, bounded at three, and **cannot approve**. | `brain`: a send-back reaches the lane as input; the fourth round goes to the operator; a manager "approval" grants **nothing**. |
| **R6** | D-R11: the write-up renders in the approval ask (absorbs `WORK-ISOLATION` P5). | `ui-use`: the ask carries the summary and answering it grants the token, at a live window. |
| **R7** | §7: PR-mode assembles a PR description and review comments instead. | `publish`/`workspace`: a `"delivery": "pr"` repo performs no local merge. |

**R1–R3 are the correction and come first**: R1 makes concurrent landing work at all, R2 makes
agent-resolved conflicts safe, and R3 removes the machinery that was standing in for R1. R4–R6 are
the review. R7 is the foreign-repo case and can wait.

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
  identical.
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

- Does the manager review need the diff **content** for a first pass, or is diffstat-plus-report
  enough to catch anything worth catching? Measure before deciding — this is the difference between
  an affordable reviewer and an expensive one.
- Should a send-back count against the loop bound when the manager's objection was **mechanical**
  (tests red) rather than judgement? Code already caught that, so arguably it should never have
  reached the manager.
- Where does the review write-up live so the operator can read it after the fact — a pane
  announcement, the question row, or both? The row is answerable; the pane is scrollable.
- `dev gate` in a cold worktree is ~250 s. Re-verifying after merging main in doubles the verify
  cost on every land. Is the touched-suites subset the right default, with the full gate only at the
  land itself? Needs measuring against how often the subset would say green when the gate would not.
