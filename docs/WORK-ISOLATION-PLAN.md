# Work isolation — deciding that a sentence is load-bearing, and ending what it starts

Status: **plan, not built.** Written 2026-08-20 from the operator's brief, after tracing the
ticket path in code. **Revised twice the same day**, both times on the operator's correction:

1. The first draft made a model's guess the thing standing between an agent and the shared
   checkout. That is a prompt providing safety, which §0 forbids — §3 now inverts it.
2. The second draft gave coordination to the **router**. Wrong by the glossary's own
   definitions: the *manager* is "the per-project scope — claims, tickets, merge tokens, plus
   its brain", and the router merely "decides where a typed sentence goes". §4 now puts
   coordination where it belongs, and behind the fast path rather than in front of it.

**SUPERSEDED IN PART, 2026-08-20, by `REVIEW-AND-MERGE-PLAN.md`.** After P1 and P2 landed, the
operator challenged the claim system and was right: declared file claims are retired as a LOCK, so
**D-4 and P8 are dead** (not rescheduled — the idea is gone), and **P3, P4 and P5 move** into that
plan's R1/R4/R6, because "verify before the merge", "examine at end of turn" and "raise the approval
question" are three slices of one review flow rather than three phases. Read §5 and §6 here with
that in mind; every marker is in place below. What this plan still owns, and what is built, is
**isolation**: layers 1, 2 and 3.

The authority for this work. It extends `WORKSPACES-CONCIERGE.md` §5/§5.1 (the routing ladder)
and consumes `LANE-LIFECYCLE.md` §2/§3 (how a lane ends, and what was already rejected there)
rather than re-deciding them. `M5-DELIVERY-PLAN.md` owns a *foreign* project's ceremony; this
owns Dodona's own.

## 0. The one-line statement

The operator drives this by voice, which means text in the box, which means `RouteInput`. That
ladder answers **which lane**. Nothing answers **is this load-bearing work, does it need a
checkout of its own, does it collide with another lane, and is it finished** — and nothing
starts or ends the ticket process from where the operator is sitting. So the isolation this
system was built to provide is reachable only by typing a CLI command in a terminal, which by
voice is not reachable at all.

## 1. What exists today, from the code

- `RouteInput` has four verdicts — `generic | addendum | new-task | unclear`. `new-task` calls
  `SpawnForAsync`, which opens a **plain lane in the project directory**. No rung can answer
  "isolate this".
- The brain can only *suggest* a ticket, as a pane announcement carrying a command string
  (`brain_suggested_ticket`). Nothing acts on it.
- The only UI path to a ticket is clicking an empty grid slot → `StartLaneWindow`, a modal with
  no coverage, which D-L4 already ruled out as the shape for asking anything.
- **A plain lane on a one-project workspace cannot commit.** Its cwd is the shared checkout and
  `.githooks/pre-commit` refuses commits from there. So today's default destination for
  load-bearing work is a tree that cannot deliver it.
- **The claim gate exists only inside ticket worktrees.** A plain lane has no `PreToolUse` hook
  at all, so nothing in code stops it writing anywhere in the operator's live tree. That is the
  hole the operator named.
- **`BrainReview` already runs review-behind** — `_ = Task.Run(…)`, fire-and-forget after the
  lane exists, able to rename it and to suggest a ticket. The pattern §4 needs is already there;
  it is simply not being asked the coordination questions.
- **Nothing examines whether a ticket is finished.** `LandOp` retires the agent, prunes the
  worktree, deletes the branch and leaves the lane dormant — all correct, and all only reachable
  by someone typing `dodona land`.
- The ticket spine is otherwise sound and covered by `m1`. **`ticket-agent` has never run with a
  real `claude` in any of the thirteen suites** — only the fake agent (`m3`, `workspace`), plus
  `tests/m2-live.ps1`, which is not one of them.

Measured, because the async decision turns on it: `git worktree add` on this repo is
**sub-second** (0.72 s for the whole `dev worktree` verb, ~136 ms of that merely starting
PowerShell). The expensive part of a fresh ticket is the **cold build** in a tree with no
`bin`/`obj` — the agent's first task, not the box's wait.

## 2. Principles this is held to

- **Enforcement in code, never a prompt** (§0). A guarantee that depends on a model answering
  correctly is not a guarantee.
- **Responsive first.** The box already pays ~1 s for the classifier. This feature is allowed
  **zero additional model calls on the input path**; every judgement it adds happens *behind* the
  operator, never in front of them.
- **Derive in code what is not really a judgement** (§2.2). Who owns which path, who holds the
  token, whether a tree is clean, whether it builds: all facts.
- **Deny with a rewrite, never a wall** (`M5-DELIVERY-PLAN.md` §1).
- **Act, announce, allow undo** (§11). No dialog, no modal, no blocking question.
- **Never hung, halted, stuck, or outdated** (§0.1).

## 3. Isolation: three layers, and the model is the OUTER one

The first draft put the classifier's `isolate` verdict in front of the shared checkout — get it
right and the lane is isolated, get it wrong and an agent does real work in the operator's live
tree. That fails worst in the case that matters most: a lane that starts as a lookup ("why is
the pane blank") and becomes work three sentences later. **No spawn-time decision can catch
that.**

**Layer 1 — code, unconditional. No agent writes into a project outside a worktree.** The claim
gate is deployed to **every** lane, not only ticket lanes. A write whose path resolves inside a
project but outside a worktree is refused, naming the holder (D-13). Same shape as the
pre-commit hook that already refuses commits from the shared checkout, one step earlier, and it
fires whether the router guessed right, guessed wrong, or was never consulted.

**Layer 2 — the refusal is a promotion, not a wall.** On that first refused write the daemon
creates the ticket, materialises the worktree, deploys the gate, and **respawns the lane into the
worktree resuming its session** (`RespawnLaneAsync` already takes a working directory; `--resume`
rebuilds context, measured in spike 1). Nothing has been written yet, so there is nothing to
move. Announced, with the undo.

**Layer 3 — `isolate` on the verdict is an OPTIMISATION.** When the router can already tell the
sentence is load-bearing, the lane is born in a worktree and layer 2 never fires. Wrong in either
direction and layer 1 catches it. **The model's judgement buys latency, never correctness.**

Read-only work needs no worktree and gets none — not a carve-out, but a consequence: **the write
attempt is the first moment isolation is knowable for certain.**

## 4. Coordination belongs to the MANAGER, and it happens behind the fast path

The glossary is explicit and the second draft of this plan contradicted it. **Manager**: the
per-project scope — that project's claims, tickets and merge tokens, plus its brain.
**Router**: decides where one typed sentence goes. So:

- **The router stays triage.** One sentence, one destination, ~1 s, and it is told only what
  triage needs. It is not asked to weigh the collective, and nothing about coordination is
  allowed to slow it down.
- **The manager coordinates, as review-behind.** `BrainReview` already fires after the routing
  decision and can rename a lane or suggest a ticket. The coordination questions go there:
  *is this the same work another lane is already doing? does this undercut what a lane is
  mid-way through? has this lane finished?* If it disagrees, it corrects and announces —
  the pattern the concierge already calls the review-behind net.

**D-14. Coordination is review-behind, never in-line.** The operator's stated goal is "I say go
and things happen". A manager consulted *before* anything happens is a second wait bolted onto
the one they already agreed to; a manager consulted *after* costs them nothing and can still fix
almost everything, because the expensive errors in this system are recoverable in the direction
that matters — a wrong new lane is free, a wrong ticket is undone by D-9, and a wrong *delivery*
is prevented by the router holding uncertain input rather than guessing (§5.1).

**Facts settled in code — no model involved.**

| collision | already handled by | gap |
|---|---|---|
| two lanes editing one file | claim conflict at `ticket-create` | the refusal does not say *who* holds it |
| two lanes merging at once | the merge token, one per repo | none |
| two lanes building in one tree | a worktree per ticket has its own `bin`/`obj` | plain lanes share the live tree — layer 1 closes it |
| work in the shared checkout | nothing today | layer 1 |

**What each side gets told.** The router's fact sheet gains only what changes a *destination*:
whether a lane is isolated, and whether the sentence names a path another lane holds. The
manager's review gets the full ownership picture — claims, worktrees, who holds the token, who
is mid-turn. All store reads, all free. Today `FactSheet` carries titles, busy/idle and the last
readable line, and **nothing about what any lane owns**, which is why coordination is not merely
poor but absent.

**D-12. The operator's words pass through verbatim; facts are attached alongside.** The router
decides *where*, never *what*. A paraphrase is a sentence, and a wrong one cannot be unsaid. What
the agent receives is the operator's own text plus a derived context block: which lane it is,
which tree it owns, which paths another lane holds.

**D-13. A refusal names the holder.** "Denied: `src/Dodona/Daemon.cs` is claimed by lane WATER
(ticket 4)" is actionable. "Denied: outside your claim" sends the reader hunting (§0.3).

## 5. Finishing: three questions that keep getting confused as one

The operator's question — *can the manager be smart enough to know when it is done?* — comes
apart into three, and only one of them is a judgement at all.

**MOVED, 2026-08-20.** This whole section is the finishing side, and it is now
`REVIEW-AND-MERGE-PLAN.md` §3–§6: (a) is that plan's §3 and D-R4, (b) is its D-R8, and the manager's
role in it is D-R9/D-R10 — where it gained the power to send work back, and the rule that it may
**block but never bless**. The three-questions framing below is unchanged and still correct; only the
phases moved.

**(a) Is the work CORRECT? — code, never an opinion.** It builds, the suites pass, the diff is
inside the claim, and it fast-forwards. That is `dev gate` plus the existing backstop, and it is
the whole of "clearly done" in any sense worth acting on.

**(b) Is the agent FINISHED? — a detectable state, and never the agent's own claim.**
`LANE-LIFECYCLE.md` §2 already rejected *"the agent said it was done"*: a model announces
completion at the end of every turn, which is turn-completion, not work-completion. But the
turn's `result` event **is** a reliable hook, and at that moment the store knows everything
needed: the lane has a ticket, no turn is in flight, and the worktree is clean or dirty. So:

**D-15. Done-ness is examined at end of turn, by code, in the worktree.** When a ticket lane goes
idle with its work committed, the daemon runs the project's verify **in that worktree** — its own
`bin`/`obj`, nothing shared, nobody interrupted. Green means the work has passed the only test
that is not an opinion. Red means it has not, and the lane is told why and keeps working: no
ask, no interruption, no queue of half-finished things for the operator to police.

**D-16. The ask is EARNED, and it is the only manual step.** A green examination raises one
question row (D-7): *WATER is green — 4 files, inside its claim, fast-forwards. Land it?* It
renders in the ask overlay, answers by click or `ui answer`, and you say yes. It stays manual
because a ref advance is the one irreversible act in this system (D-8) — not because the
examination could not be automated. Everything before the yes is mechanical; after the yes,
`LandOp` already retires the agent, prunes the worktree, deletes the branch and leaves the lane
dormant.

So the honest answer to *is it impossible to handle automatically*: the **examination** is fully
automatable and should never involve a model; the **decision to merge** stays a person's, and the
responsiveness comes from that question arriving in front of you rather than waiting to be
hunted.

**(c) Should the LANE close? — judgement, already decided, and not here.**
`LANE-LIFECYCLE.md` §3: after a land with no further ticket, ask once whether more is coming in
this thread, with the code-checkable preconditions listed there. That is the one place a model
belongs in this whole area, and this plan re-decides none of it.

**Where the manager genuinely helps.** Not (a), which is code. Not (b), which is a state. But:
a ticket lane that has gone idle with **uncommitted** changes is probably stuck rather than
finished, and whether that is worth raising is a judgement — as is (c). Both are review-behind
work, and both are cheap because they fire once, at completion, not on a timer (§2's rejection of
idle timers stands).

## 6. Decisions (isolation)

**D-1. Ticket-worthiness is a flag on the existing `new-task` verdict** —
`{"kind":…,"target":…,"isolate":true|false,"confidence":…,"reason":…}`, read only when `kind` is
`new-task`. Per §3 an optimisation, not the mechanism.

**D-2. `isolate` defaults false and needs positive evidence** — asked as *will this change files
in the repository*; not length, not imperative mood (§5.1 rejected length on the operator's own
correction). A wrong `false` is cheap now, because layer 1 catches it.

**D-3. The verdict is synchronous; materialisation is not.** Lane row and pane appear the instant
the verdict lands, presence `preparing…`, sentence delivered when the agent attaches.
`LaneCreate` already precedes `AttachShimAsync`.

**D-4. DEAD, 2026-08-20 — see `REVIEW-AND-MERGE-PLAN.md` D-R5.** Elastic claims were the fix for a
frozen wrong claim stranding an agent. The operator's challenge went one level deeper: the claim
should not bound the agent's own worktree at all, because *"two agents about to work on the same file
— that's often the case, very often the case, and if that is problematic it's the manager's job to
say something about it."* Two agents on one file is ordinary development; files are not the unit of
work. So there is nothing left to make elastic. Two things also came out of tracing the merge while
implementing P1/P2, and either alone would have retired this: **a disjoint claim does not make
concurrent landing work** (both branches still fail `--ff-only` the moment one lands, because main
moved), and **layer 1 already replaced the gate's remaining job** — an agent that physically cannot
write outside its worktree makes "did it touch what it declared" a review question, not a safety one.
The original text follows, kept because the stranding case it names is real and R3 must not
reintroduce it from the other side.

**D-4 (superseded). Claims are elastic while the ticket is scoping.** A spoken sentence cannot supply claim
paths, and a frozen wrong claim strands the agent mid-work. While `scoping`, an out-of-claim
write **auto-extends and announces**; the gate refuses only what another **open ticket** claims,
naming it (D-13). The claim freezes at the first `token-request`.

This changes what the claim gate is *for*: from bounding an agent to a prediction, to preventing
collision between lanes — the operator's own framing of why tickets exist. Defensible because the
gate only matches `Edit|Write|MultiEdit|NotebookEdit`, never saw a `sed` or a heredoc, and fails
open by design; the invariant that matters (no two tickets over one path, no two merge tokens
over one main) is untouched and still refuses. Review moves to the backstop at `token-request`,
where the diff is a fact rather than a guess.

**D-5. Verify runs BEFORE the merge, in the worktree.** Today `LandOp` merges, commits the fence,
and *then* verifies — a red verify has already advanced main. Because the merge is `--ff-only`,
main's post-merge tree is byte-identical to the branch tip, making worktree verification
**exactly equivalent** and strictly safer. This is also what D-15 reuses.

**D-6. Verify must go through `tools/dev.ps1`.** `dodona.json`'s verify is a bare
`dotnet build Dodona.sln -c Release -warnaserror` via `cmd /c`; a locked output file makes that
report `Build FAILED` with ten screens of MSB3026 — the exact false diagnosis §1 mandates the
wrapper to prevent, arriving when nobody is watching. It becomes `dev gate`.

**D-7. Approval is rendered, not hunted** — an `open` row in `questions`, so the ask overlay
renders it live, `ui dump`'s `ask` key shows it headless, and one answer path serves both (D-L4).
Voice composes the word, the operator presses Enter.

**D-8. Auto-land stays opt-in per project.** `"landing": "auto-when-green"` exists for a project
that wants it; this repo stays `on-approval`.

**D-9. `lane-stop` on an isolated lane abandons its ticket** — prune, delete the branch, release
the claims, announce. The undo line has to be true.

**D-10. The agent is told the tail, and its two traps are enforced rather than written down.**
`TicketSystemPrompt` names commit → `token-request` → `land`; it names none of them today, so an
agent follows `/ship` instead — whose step 4 runs `dodona publish --project .`, building **that
worktree** and hot-swapping the operator's live app to an unfinished branch. So `publish` refuses
a `--project` inside `.dodona/wt/` unless `--from` is explicit, and `dev worktree` refuses to run
from inside a ticket worktree.

**D-11. How the lane ends is already decided** — `LANE-LIFECYCLE.md` §3. Wire into it.

**D-17. The gate is handed to the agent at LAUNCH, not written into the project.** The operator's
challenge, and it is correct: a hook in a project's `settings.local.json` binds **anything** that
runs Claude Code in that folder — including the operator's own IDE session. Only the process
Dodona started should be gated.

The CLI already supports this, and `ClaudeArgs` already uses the neighbouring flag
(`--setting-sources user` for utility roles):

- **`--settings <file-or-json>`** — a **precedence layer, not a replacement**. Confirmed against
  the settings documentation rather than inferred from the word "additional" in `--help`: the
  order is Managed → **command-line arguments (temporary session overrides)** → Local → Project
  → User. So a project's own settings still load and still apply; command-line settings only win
  where the *same key* collides.
- **Hooks specifically MERGE, they do not replace.** From the hooks documentation: *"Hook entries
  merge across settings levels rather than replacing each other"*, and *"All matching hooks run in
  parallel. If you define the same handler in more than one settings file, it runs once."* So a
  project's own `PreToolUse` hooks keep firing alongside the gate — which is also what
  `DeployGate`'s existing comment had already observed for the local-over-project case.
- **`--setting-sources user,project,local`** — restricts which sources load at all.

Two constraints fall straight out of that, and both are easy to get wrong:

- **Never pass `--setting-sources` for a work lane.** `ClaudeArgs` passes
  `--setting-sources user` for *utility* roles today, deliberately cutting project context out of
  a manager. Doing that to a work lane would cut the project's own settings and hooks out of the
  agent doing the work — manufacturing exactly the problem this decision exists to avoid.
- **The gate file must contain ONLY the hook.** Command-line settings outrank Local and Project on
  a colliding key, so any other key in that file silently overrides whatever the project chose.
  One hook, nothing else, and a comment saying why.

So the gate becomes a per-lane settings file under `<DODONA_HOME>\workspaces\<id>\`, passed as one
more argument in `ClaudeArgs`. A **file rather than inline JSON**: the command line stays short and
the gate stays inspectable when something goes wrong, and `ProcessStartInfo.ArgumentList` escaping
is not a thing to bet a quoting bug on.

What this deletes outright:

- **The overwrite hazard, entirely.** `DeployGate` writes `settings.local.json` with
  `File.WriteAllText` — a whole-file overwrite. Safe until now purely by accident: a ticket
  worktree is a fresh checkout and the file is untracked, so there has never been one there to
  destroy. Deploying into a live project tree would have silently wiped the developer's own
  allowed-commands list with nothing in git to restore from. Nothing is written to a project now,
  so there is no file to merge and no backup to remember.
- **Both footprints in a repo that is not Dodona's** — the settings file and the appended
  `.git/info/exclude` block. Most repos this will drive are not the operator's to modify.
- **The stale-artifact cleanup**: the `dodona-gate.ps1` removal, and the reason it exists.
- **Scope creep onto the human.** The operator's own session in the same folder is untouched,
  which is the whole point of the change.

**Both of these were MEASURED on 2026-08-20, before P1 relied on either, and the second answer
inverted an assumption** — see below the questions for what came back.

1. **Does a `PreToolUse` hook supplied via `--settings` actually fire under `-p` with
   `bypassPermissions`?** The existing measurement (hooks fire under `bypassPermissions`) was
   taken against a hook in a project file. One `claude -p` turn answers it. This is the first
   task of P1 and everything else in the phase depends on it.
2. **Are hooks re-read after a publish, or fixed at session start?** The gate command names
   `Environment.ProcessPath`, and `GcOldBuilds` deletes old build directories — which is exactly
   why gate *redeployment* exists today (`gate_redeploy_failed` carries the incident: a hook
   pointing at an exe that had been collected). If hooks are fixed at session start, a live agent
   keeps the old path either way and redeployment was never solving it for a running lane; if they
   are re-read, the per-lane file must be rewritten on swap the same way the worktree copy is
   today. **Do not delete the redeployment machinery until this is answered** — it is the one part
   of `DeployGate` this decision may not subsume.

**ANSWER 1 — YES, IT FIRES, AND THE DENY IS ENFORCED.** A `PreToolUse` hook supplied via
`--settings <file>` fired under `claude -p --permission-mode bypassPermissions`, the write never
happened, and the agent was told why ("blocked by a settings hook"). Run with a CONTROL — the same
prompt in the same shape without the flag wrote the file — so the absence is the refusal and not the
model declining. That control is the whole value of the measurement: without it, "no file appeared"
proves nothing.

**ANSWER 2 — HOOKS ARE FIXED AT SESSION START. NEVER RE-READ.** A two-turn
`--input-format stream-json` session kept firing its hook on turn 2 after the hook had been
**removed from the settings file between turns**. Three consequences, and the third was not
predicted here:

- The per-lane file is written once, at launch, and rewriting it under a live agent reaches
  nothing. So **the gate names only the LANE** and the daemon resolves the ticket, the claims and
  the worktree from current state on every write. A lane's ticket changes during its life (P2
  promotes a plain lane into one); its lane id does not. `--ticket` on a launch line is
  structurally unable to stay correct, so it is not what the decision rests on.
- **Redeployment never fixed the incident it was written for.** `gate_redeploy_failed` carries a
  live 2026-08-18 failure — a hook pointing at an exe `GcOldBuilds` had collected — and rewriting
  the file could not reach the live agents that had lost enforcement. The lanes it appeared to fix
  were the ones that respawned afterwards, which `AttachShimAsync` now writes a fresh file for
  anyway. It is deleted.
- **Deleting it was not enough: it had become actively harmful, and the real fix is in the GC.** A
  live agent holds its launch-time exe path for its whole life, so refreshing the file with the new
  path is precisely what lets `GcOldBuilds` delete the directory that agent's gate still needs. So
  **`GcOldBuilds` now refuses to collect a build directory named by any live lane's gate file**, and
  dead lanes' gate files are removed in reconcile so nothing pins a directory forever. A gate whose
  command cannot even start is a fail-open that never reaches Dodona's code to be logged — under
  layer 1, a write into the operator's live checkout.

## 7. Phases

| | what | proof |
|---|---|---|
| **P1** | **Layer 1**: the gate on every lane; refuse a write inside a project but outside a worktree, naming the holder. | `m1`: a plain lane's write into the shared checkout is refused. `dev prove` red first — today there is no gate there at all. |
| **P2** | **Layer 2**: promotion on that refusal — ticket, worktree, gate, respawn-with-resume, announce, undo. | `m2`: a plain lane attempting a write ends in a worktree, session resumed, nothing written to the live tree. |
| ~~P3~~ | **MOVED to `REVIEW-AND-MERGE-PLAN.md` R4.** The end-of-turn examination is where that plan's PR-shaped record is assembled, so it gained the agent's own report and the diffstat. |
| ~~P4~~ | **MOVED to R1.** Verify before the merge is one step of the standard merge flow (bring main in, re-verify in the worktree, then fast-forward), not a phase of its own. D-6 (`dev gate` rather than a bare `dotnet build`) travels with it. |
| ~~P5~~ | **MOVED to R6, and BUILT there** (2026-08-20). The approval row is the same row; what changed is that it now carries the manager's write-up, which is what makes answering it a two-second decision. One deviation from the wording below, decided as R6's **D-R22**: the row is raised primarily at **completion**, not at `token-request`'s refusal — that is where the write-up exists, and it means the operator is not waiting for an agent to bump into a wall first. The refusal raises it too (and names it), so a ticket whose record was impossible still asks rather than instructs. |
| **P6** | §4: the manager's review-behind gains the ownership picture and the coordination questions; the router's fact sheet gains only what changes a destination. | `unit`: both renderings. `m2`: a colliding sentence is corrected behind, announced, not blocked. |
| **P7** | **Layer 3**: `isolate` on the verdict; isolated spawn; fallback to a plain lane when `ticket-create` refuses. | `unit`: verdict parses, defaults false, ignored unless `new-task`. `m2`: an isolating sentence is born in a worktree, skipping promotion. |
| ~~P8~~ | **DEAD — see D-4 above and `REVIEW-AND-MERGE-PLAN.md` R3**, which deletes the three claim refusals instead of making one of them elastic. |
| **P9** | D-10: the prompt tail, the `publish` refusal, the `dev worktree` refusal. | `publish`: `publish --project <a ticket worktree>` refuses and names `--from`. |
| **P10** | D-11: wire landing into `LANE-LIFECYCLE.md` §3's completion question. | `m1` + `m3`: after a land the lane is dormant, the question asked once, preconditions gating it. |

**P1–P2 are the safety and are BUILT** (committed 2026-08-20): they make the operator's named
failure — real work started outside a worktree — structurally impossible, with no model work at all.
**The finishing side is now `REVIEW-AND-MERGE-PLAN.md` R1–R6**, which is what turns "it got its own
branch" into "it got off it again". What remains here is **P6, P7, P9 and P10** — the
responsiveness, the isolating verdict, and the prompt tail — and R1–R3 come before any of them,
because until the merge flow works a landed ticket is still a manual rebase.

## 8. Rejected — do not re-propose

- **A separate ticket-worthiness model call.** Two round trips on the input path, double the
  latency the operator agreed to once, and quota is the scarce resource.
- **Making the classifier's verdict the thing that keeps agents out of the shared checkout.**
  This plan's own first draft: a prompt providing safety (§0), blind to lookup-turns-into-work.
- **Giving coordination to the router.** This plan's own second draft, and contrary to the
  glossary: the router decides where one sentence goes, the manager owns claims, tickets and
  tokens. Putting it in the router also puts it *in front of* the operator, which D-14 rejects
  on latency alone.
- **Trusting "the agent said it was done".** Already rejected in `LANE-LIFECYCLE.md` §2 and
  re-confirmed here: a model announces completion at the end of every turn.
- **A model judging whether the work is correct.** That is `dev gate`'s job, and a fact beats an
  opinion. The manager is asked whether a lane looks *stuck*, never whether the code is *right*.
- **Brain-proposed claims, frozen at creation.** The stranding case: the agent needs one file
  nobody predicted, the gate denies, and by voice there is no way to extend.
- **Claims as a lock at all** (added 2026-08-20, operator's decision — see D-4 and
  `REVIEW-AND-MERGE-PLAN.md` D-R5/§9). Two agents on one file is ordinary; a lock on the agent's own
  private checkout blocks the work it was given; and a disjoint claim never made concurrent landing
  work anyway. Kept only as a signal, and derived from `git diff` rather than declared.
- **Preventing two agents from touching one file.** Files are not the unit of work. The overlap that
  matters is duplicated *effort*, which the manager raises (§4) rather than a mechanism forbids.
- **A manager that can APPROVE a land.** It may block. `REVIEW-AND-MERGE-PLAN.md` D-R10: rejection is
  reversible, a ref advance is not, and a model as sole gate on the irreversible step is this plan's
  own §2 violated with the model renamed.
- **A whole-repo `path:.` claim instead.** Bounds nothing, makes every other ticket a conflict.
- **Promotion AFTER a lane has edited the shared checkout.** The edits are in the wrong tree and
  `git stash` is repo-global (§5.2), so two lanes stashing interleave one stack. Layer 2 promotes
  on the *attempt*, before anything is written — which is the whole reason layer 1 sits at the
  write and not at the commit.
- **Auto-landing by default.** A ref advance has no undo.
- **Idle timers for done-ness.** `LANE-LIFECYCLE.md` §2: an idle session costs nothing, and a
  timer cannot tell "abandoned" from "deferred". The examination fires on the turn's `result`,
  once, which is an event and not a clock.
- **A "create a ticket?" dialog.** §3.1 and D-L4: no modals — a test window is forbidden from
  producing one, so it would be permanently untestable.
- **The router rewriting or summarising the operator.** D-12.
- **Deciding isolation from sentence length or mood.** §5.1 rejected length on the operator's own
  correction.

## 9. Traps this will hit

- **A gate on every lane is a `PreToolUse` hook on every lane, and hooks cost.** The two deleted
  in D-7 cost 255 ms per edit, 136 ms of it merely starting PowerShell (§0.0). This one is
  `dodona.exe gate-hook` — one process, not a script shelling out to a binary — but it must be
  **measured** before P1 is called done, not assumed.

  **MEASURED, 2026-08-20, warm, 20 invocations per case on this machine:**

  | case | per edit |
  |---|---|
  | `dodona.exe` process start alone (`version`) | 47.8 ms |
  | plain lane, ALLOW — tree-check, write in a worktree | 99.3 ms |
  | plain lane, DENY — tree-check, write in the shared checkout | 105.4 ms |
  | ticket lane, ALLOW — tree-check **and** claim-check | 99.9 ms |
  | ticket lane, DENY — tree-check refuses first | 100.1 ms |

  So **~99 ms per write**, of which **48 ms is starting the process at all** — under half of the
  255 ms that got the last two hooks deleted, and the second daemon round trip a ticket lane pays is
  essentially free (99.9 vs 99.3 ms). It is not nothing, and it is stated rather than waved at: the
  cost falls on every `Edit`/`Write` of every work lane, and the floor cannot be improved without
  making the gate something other than a process.
- **`GateHook` fails open, and layer 1 makes that load-bearing.** Today a fail-open gate means a
  ticket lane's write slips to the backstop; under layer 1 it means a write into the live tree.
  Every fail-open path needs re-reading against that — fail-open has already cost this project
  one silently dead gate (the BOM incident, §3).

  **Re-read and RESOLVED BY ORDERING, in P1.** The two questions now fail in opposite directions:
  the **tree** question is asked first and **fails CLOSED**, the **claim** question is asked second
  and still fails open exactly as before. That ordering is what keeps the second one safe — because
  the tree answer has already been obtained, every remaining claim fail-open can only let a write
  through *inside a worktree*, which is the case the merge-time backstop covers and the case the
  original rationale was actually about.

  This **reverses a rationale recorded beside `m1`'s own check** — *"layer 1 failing closed would
  strand a lane that has no way to ask a human for permission (CLAUDE.md §7). The backstop is layer
  2."* True for a gate that lived only inside worktrees; false once the gate covers the shared
  checkout, where there is nothing behind the fail-open: an allowed write is already in the
  operator's live tree, next to their uncommitted work, and no backstop ever sees it because it was
  never going to be merged. A refused write is announced, recorded and retryable; and the daemon it
  needs an answer from is the same one already pumping that lane's output, so a refusal there costs
  a message rather than work. `m1`'s check now asserts the refusal, and says all of this in place.

- **A TICKET lane was never bounded to its worktree either — found in P1 by reading the rungs.**
  `claim-check` resolves an absolute path through its repository and project rungs, so a ticket
  agent writing the ABSOLUTE path of a file *its own claim covers* — in the operator's live checkout
  instead of its worktree — resolved to the same claim-relative string and was **allowed**.
  Reachable since multi-repo landed. Layer 1 therefore applies to ticket lanes too; that is not
  belt-and-braces, it closes a hole the claim algebra cannot see.

- **The retention scan must unescape the file it reads.** The gate file is JSON, so every backslash
  in the command is doubled; a raw `Contains()` against a Windows path with single backslashes can
  never match, and the retention silently retains **nothing** while looking installed. `m4` already
  carried this exact trap from the reading side and the new code walked straight into it anyway.
  Caught only because the check was tightened to demand that a retention actually FIRED rather than
  that few directories survived — the permissive version passed while the mechanism did nothing.
- **`ticket-create` refuses on claim conflict**, so promotion can fail at the moment it is
  needed. It must degrade to a refused write with a named holder, never to a silent allow.
- **`lane-respawn` refuses to re-home a ticket lane** — deliberately. Layer 2 goes the other way
  (plain → ticket), which that refusal does not cover, so it needs its own path and its own check.
- **The end-of-turn examination must not run on a lane the operator is still talking to.** A
  `result` is the end of *a* turn, not of the conversation, and re-running `dev gate` on every
  turn of a chatty lane would burn the machine. Gate it on the worktree having *changed* since
  the last examination.
- **`land` demands main checked out in the shared checkout.** True while §0.0 keeps the operator
  there, but it must announce rather than fail quietly.
- **ff-only refuses when main moved under an open ticket**, and nothing rebases. Under D-8 this
  is where a "never stuck" wait belongs: arm the land behind a rebase, announce, do not park.
- **A ticket worktree lives at `<member>/.dodona/wt/t<N>`, inside the repo.** Anything walking the
  tree must keep skipping `.dodona`.

## 10. Open questions

- Does an isolating verdict need a name for the ticket, or is the lane title enough? The title is
  already brain-chosen and reviewed, so a second name is probably noise.
- Should `isolate` be suppressed while the same project already has N open tickets? Cheap to
  count in code, and unclear whether that is a real limit or an invented one. Measure first.
- Layer 1 refuses writes inside a project outside a worktree. What about a write **outside every
  project** — a scratch file in `%TEMP%`, a note in the operator's home? `claim-check` refuses it
  for ticket lanes today. Refusing it for every lane may be right, or may break ordinary work in
  ways only use will show.
- D-15 runs the project's verify per finished turn. On this repo that is `dev gate` at ~115 s in
  a cold worktree. Cheap in wall-clock terms because nobody waits on it — but it is real CPU, and
  whether a lighter examination (`dev test unit` plus the touched suites) is the better default
  needs measuring against how often it would say green when the full gate would not.

## Appendix A — implementation touchpoints

Named symbols, so a session picking this up does not have to re-derive the map. Line numbers
rot; names do not. **One phase per commit**, each with its proof, in the order §7 gives.

### P1 — the gate on every lane

- **`Program.cs` `GateHook()`** — the early `if (string.IsNullOrEmpty(ticketArg)) return 0;` is
  the exact line that makes a plain lane ungated. With no `--ticket` but a `--lane`, it must ask
  the daemon instead of allowing.
- **A new daemon command** (`tree-check`, lane + path) answering *is this path inside a project
  but outside a worktree*. Use **the same stateless test both existing layers already use**:
  `.git` is a **file** in a worktree and a **directory** in the shared checkout
  (`.githooks/pre-commit`, and `dev.ps1`'s `--git-common-dir` handling). Resolve the path's
  toplevel with `git rev-parse --show-toplevel`, then test `.git`. No registry, no state, nothing
  that can go stale.
- **Deployment funnel: `AttachShimAsync`.** Every lane spawn goes through it (`SpawnLaneAsync`
  and `RespawnLaneAsync` both call it), so deploying there cannot be forgotten by a call site —
  the same correction `DaemonClient.Send` needed for start-on-demand (§3.1). Deploy for
  `role == "work"` only: management lanes run in the neutral directory and write nothing.
- **`DeployGate(worktree, ticketId, repo)`** gains a lane-only form writing `--lane` instead of
  `--ticket`. Keep one function with the ticket optional; two would drift.
- **The gate is passed at launch, not written into the project (D-17).** No
  `settings.local.json`, no `info/exclude` block, nothing in anybody's tree. See D-17 for why
  this replaces `DeployGate`'s file entirely and what has to be measured first.
- **Measure before calling it done** (§9): time an `Edit` with and without the hook. The two
  hooks deleted in D-7 cost 255 ms each, 136 ms of that merely starting PowerShell. This one is
  `dodona.exe` directly rather than a script shelling out to it, so it should be far cheaper —
  *should* is not a measurement.
- **Re-read every fail-open path in `GateHook` first.** Under layer 1 a fail-open means a write
  into the live tree, not a slip to the backstop.

### P2 — promotion on the refused write

- Factor the body of the **`ticket-create`** handler into a method the promotion can call —
  it currently only exists inline in the command switch.
- **`RespawnLaneAsync(laneId, title, childArgs, child, workDir)`** already takes a working
  directory and resumes the session; this is the whole of the move.
- **Trap:** the **`lane-respawn`** handler deliberately refuses to re-home a *ticket* lane. Layer
  2 goes the other way (plain → ticket), which that refusal does not cover — so it needs its own
  path and its own check, not a relaxation of that one.
- The denial returned to the agent is a **rewrite, not a wall**: name the new worktree and the
  same relative path inside it.

### P3 — the end-of-turn examination

- **`Daemon.cs`: `if (role == "work") rt.OnResult = CompressResult;`** — an **assignment, not
  `+=`**. A second consumer that overwrites it silently kills selective compression, which would
  present as "the panes went verbose" with nothing pointing here.
- Gate the examination on **the worktree having changed since the last one** (record the examined
  sha or a status digest), or a chatty lane re-runs the whole verify every turn (§9).
- Runs in the ticket's own worktree, with its own `bin`/`obj`. Shares its runner with P4.

### P4 — verify before the merge

- **`LandOp`** — move the verify loop **above** `git merge --ff-only` and run it with
  `WorkingDirectory = t.Worktree`. Equivalence is guaranteed by the fast-forward: main's
  post-merge tree is byte-identical to the branch tip.
- **`dodona.json`** `verify` becomes `dev gate` (D-6).
- `dev prove` this one red before trusting it; it is the phase most likely to pass against the
  old order.

### P5 — the approval ask (BUILT as `REVIEW-AND-MERGE-PLAN.md` R6; its Appendix A is the map)

- The **`on-approval` refusal branch in `token-request`** raises a `questions` row instead of only
  announcing — it does, and it names the id. But the row is raised **first at completion**, by
  `Daemon.AskToLand` off R4's record, which is where the manager's write-up is (R6's D-R19/D-R22).
  `Ask.cs`, the `answer` handler and `ui answer` already existed and needed no new answer path;
  the overlay already renders an open row (D-L4). `dodona approve <id>` stays as the CLI form,
  and both it and the ask now go through one `Daemon.ApproveTicket`.

### P6 — the manager's ownership picture

- **`FactSheet`** — router side, and only what changes a *destination*.
- **`BrainReview`** — manager side, full ownership: claims, worktrees, who holds the token, who is
  mid-turn. It already runs `_ = Task.Run(…)` behind the decision, which is the property D-14
  wants.

### P7 — the `isolate` flag

- **`ClassifyAsync`** (parse the field), the escalation prompt string in `RouteInput`, and
  **`SpawnForAsync`** (the isolated spawn, with the plain-lane fallback when `ticket-create`
  refuses).

### P8–P10

- **P8** elastic claims: `Claims.cs` for the algebra, the `claim-check` handler for the widen, the
  `token-request` handler for the freeze.
- **P9** the prompt tail: `TicketSystemPrompt`; the refusals: `Publish()` in `Program.cs` and
  `Do-Worktree` in `tools/dev.ps1`.
- **P10** `LandOp`'s dormant path, plus `LANE-LIFECYCLE.md` §3's preconditions.

### How to work on it

Per CLAUDE.md, and none of it optional: a worktree of your own; `dev check` before starting;
`dev test unit` (~1 s) while iterating and the one or two suites the change touches; **`dev prove`
every new check before believing it** — a check that has not been seen red is worth nothing;
`dev gate` once before merging to main; `/ship` to deliver. A suite that prints no tally is a
failure, not a shrug.
