# Lane lifecycle and attention — decided, rejected, and left to build

Working notes for two things that are **not built yet**: how a lane ends, and when the UI
is allowed to ask for your attention. Written down at the point the decisions were made
so that whoever picks this up — including a future session of me — does not re-propose
the ideas that were already looked at and thrown out.

Companion to the design doc (`ORCHESTRATOR-DESIGN.md`, §8 attention, §11 lifecycle) and
the milestone plan (`ORCHESTRATOR-REVIEW.md`), both alongside this file.

---

## 1. The frame: lanes are durable, agents are disposable

The design's own rule, and everything below depends on it: **the unit is the lane, not
the agent.** A lane is rows in the store — a thread of work with a name, a colour, a
fixed slot and a transcript. An agent is a `claude` process with a session id, and
`--resume` rebuilds its context (measured in spike 1). Killing an agent is cheap and
recoverable. Ending a lane is neither, because the lane *is* the thread.

So "closing" is really two different acts, and they should never be conflated:

| Act | Cost | Reversible? |
|---|---|---|
| Retire the **agent** | a respawn, ~seconds | yes, via `--resume` |
| End the **lane** | the thread stops being a live place to work | only by starting a new one |

---

## 2. Rejected — do not propose these again

**Idle timers.** "Retire an agent after N minutes of no activity." Rejected, and the
reason is the important part: **an idle session costs nothing.** Quota is consumed by
turns, not by existing. Meanwhile a parked lane is often *deliberate* — work you have
looked at, understood, and chosen not to continue until tomorrow. A timer cannot tell
"abandoned" from "deferred", and it would destroy warm context to save a resource that
was never being spent. If the process count ever genuinely matters, that is a machine
problem to measure first, not a lifecycle policy.

**Slot-pressure eviction.** "When a seventh lane wants in, demote the least recently
active one." Rejected because it optimises the wrong scarce thing. The slot count is a
number we choose, and **the intention is to raise it** precisely so lanes can sit idle
and visible. A design that evicts under pressure gets worse as slots grow, not better —
and it would make lanes feel disposable at exactly the moment the operator is relying on
them being persistent.

**The tray as an eviction target.** Follows from the above: the tray is for lanes that
have not started, not for lanes being pushed out of the way.

**"The agent said it was done."** A model says it is finished at the end of every turn.
That is turn-completion, not work-completion, and it is wrong often enough that it must
never on its own end anything.

---

## 3. What ending a lane is actually about

It is about **work being finished**, and nothing else. The signal is concrete and
code-derived:

- the lane's ticket **landed** (merged to main), and
- there is **no further ticket** in flight for it.

At that point exactly one question remains, and it is genuinely a judgement:

> **Is there more coming in this thread, or is this lane finished?**

That is the only place a model belongs in this whole area. It is asked **once, at
completion** — not on a timer, not under pressure — and it should read the lane's recent
transcript to answer. §8 says a lane groups *sequential* work in the same area, so the
default answer leans toward "keep it": the next ticket touching those paths should route
straight back into this lane, reusing the name, colour and slot the eye has already
learned.

**Outcome when it decides "finished":** announce it, end the lane, free the slot, offer
undo — the standard contract. **When it decides "more coming":** the lane stays, the
agent may be retired (its context is stale once the branch is gone), and the pane shows
the thread waiting for its next ticket.

### Preconditions — checkable in code, no model involved

Nothing may be ended automatically unless all of these hold, because only then is the act
recoverable:

- the worktree is clean (no uncommitted changes)
- presence is not `working…` — never interrupt a live turn
- the lane holds no merge token
- no unacked *blocked on you* announcement
- the session id is recorded, so the thread could be resumed

If the only failure is uncommitted work, **do not refuse — make the state safe**: commit
it to the ticket branch as WIP first, then the act destroys nothing. The branch is the
durable artifact; use it.

### The one unambiguous bug to fix first — **FIXED (2026-08-18)**

`land` currently prunes the worktree and deletes the branch **but leaves the agent
running with its working directory deleted underneath it.** That is not a policy
question, it is a broken state the system can currently be in. Landing should retire the
agent, announce it, and leave the lane idle and reusable. Do this before any of the
judgement work above — it needs no policy at all.

---

## 4. Attention: the badge is firing at the wrong moment — **IMPLEMENTED (2026-08-18)**

*(Deferral is live: the badge count excludes announcements in lanes that are mid-turn,
and flushes when presence returns to idle. Blocked-on-you stays immediate. Asserted
mid-turn in `ui-use-acceptance`. The rest of this section stands as the rationale.)*

**Observed.** The notification badge appears almost as soon as the agent produces
output — while the agent is *still working*. Nothing is being asked of the operator at
that moment, so the signal is noise, and §8's whole attention model rests on badges
never becoming noise.

**The principle it violates.** An attention signal must mean **"you are needed"**, not
"something happened". Those are different events and only the first deserves a badge.

**The rule to implement.** Attention is owed only when the lane has genuinely stopped and
is waiting on a person. In wire terms that is the `result` event — the end of a turn with
nothing further running — not intermediate assistant text, and not a system receipt
written while work is in flight.

Proposed mechanics (a proposal, not a decision):

- Give a lane an explicit attention state: **working** → **awaiting you** →
  **blocked on you**. Presence already derives the first from tool events in code.
- Announcements created while presence is `working…` are **deferred**: recorded as rows
  immediately (nothing is ever hidden from the transcript or the feed) but not counted
  toward the badge.
- When the turn ends, deferred announcements flush and the badge appears once — a single
  signal at the moment it becomes true, instead of a count that ticks up during work.
- **Blocked on you** is unaffected and stays immediate. Waiting on a merge approval is
  true the instant it happens, and it is categorically distinct (§8: glyph + border, and
  the only thing allowed to raise a toast).

**Test it the way the dead end was caught.** This is a timing behaviour, so a dump taken
after everything settles will pass while the live experience is wrong.
`tests/ui-use-acceptance.ps1` should assert *during* a long turn that the badge is
absent, and only after the result that it appears.

---

## 5. Liveness: "is it working, or is it stuck?" — **IMPLEMENTED (2026-08-18)**

*(The pane clock is live: `working… 40s` after 10s of silence, `quiet Nm` past five
minutes, `unreachable`/`landed` from lane state. Also live: `land` retires the agent and
leaves the lane dormant; `dodona lane-respawn <id>` / the pane's `wake` button bring it
back, resuming the recorded session for real claude. The rest stands as rationale.)*

**What already exists.** Each pane's presence line is the busy indicator, and it is
derived in code from the agent's wire events, never from a model: `idle`, `working…`, or
the live tool — `write: clouds.hlsl`, `bash: dotnet test`. It flips to `idle` on the turn's
`result`. So the question "is it doing something" is already answered on screen.

**What it does not answer.** Presence is a *static string*. A wedged agent, a hung tool
call and an agent thinking hard for four minutes all render identically as `working…`
forever. The word cannot distinguish alive from stuck, which is exactly the worry — and
watching a motionless label is precisely how you end up not trusting the UI.

**What would answer it.** Three additions, cheapest first, all from data already in the
store:

- **Make it tick.** Show elapsed time since the lane's last wire event next to presence —
  `working… 2m14s`. Free: `pane_events.ts` already carries it. A number that moves proves
  both that the agent is being heard from and that the UI itself is live, which a static
  word can never do. Something subtly animated while `working…` would do the same job for
  peripheral vision, which is how these panes are actually read.
- **Name the quiet.** Past a threshold with no wire activity at all, say so plainly —
  `quiet 6m` — as a neutral state, not an error. Long silences are legitimate (a big
  think, a slow build), so this reports rather than accuses. Calibrate the threshold from
  real turns, not a guess.
- **Separate "quiet" from "gone", because we can prove it.** This does not need
  heuristics: the shim owns the agent process and knows whether its child is still alive,
  and the daemon knows whether the shim's pipe still answers (it already logs
  `lane_pipe_lost` and marks lanes `unreachable` on reconcile). Those are facts. The pane
  should show three distinct things — **working** (recent activity), **quiet Nm** (alive,
  nothing said lately), **unreachable** (the process or pipe is actually gone) — instead
  of collapsing all of them into `working…`.

The third is the one that turns "has it gone to sleep?" from a feeling into an answer.

---

## 6. Open questions

- After a lane's ticket lands and the lane is kept, should the agent be retired
  immediately (context is stale, the branch is gone) or kept until the next ticket
  arrives? Retiring is tidier; keeping is faster to resume. Probably retire, since the
  next ticket will want a fresh worktree anyway.
- Should "finished" ever be decided without a landed ticket — e.g. a lane that never had
  one? Likely operator-only: with no completion signal there is nothing objective to
  judge.
- Where does the undo for "lane ended" live once the UI has a real affordance? Today the
  feed says `undo: dodona lane-stop 3`, which is a GUI telling you to type a command —
  the same fault as the empty-project dead end, and it needs a button.
