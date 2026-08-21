# The lane briefing — telling an agent what Dodona needs, at the moment it needs it

Status: **B1 built 2026-08-21** (one briefing builder, correct per lane kind; the false claim sentence
deleted). B2 and B3 below are the rest. Written 2026-08-21 from the operator's brief, after reading the two
system prompts, the input delivery path and the prompt detector in code at `95f397d`.

This is the general answer to a problem `REVIEW-AND-MERGE-PLAN` R7 solved one instance of. Read
§1 first: **most of what this plan asks for already exists for one kind of lane and is missing —
and in one place actively wrong — for the other.** That discovery is the plan.

## 0. The one-line statement

A lane agent works inside a project whose own process wants things Dodona cannot give it, and
Dodona's needs are not written where the agent will see them at the moment it decides. The fix
is not to take the decision away from the agent. It is to make sure the agent **always knows
what ground it is standing on**, and that when it gets it wrong anyway, it is told rather than
silently succeeding.

## 1. What is actually true today, read out of the code

Verified at `95f397d`, because the shape of this plan turns on it.

**A PLAIN lane already gets nearly all of it.** `Daemon.LaneSystemPrompt` says the directory is a
SHARED checkout, that other lanes and the operator are in it, and — verbatim — *"never run `git
checkout`, `git switch`, `git stash`, or anything else that changes which branch is checked out or
moves uncommitted work aside"*, plus what to do instead (say so and stop; a ticket gets a private
checkout). That is four fifths of the block this plan was commissioned to write. **It does not
need writing. It needs extending to the lane that lacks it.**

**A TICKET lane gets almost none of it, and that is backwards.** `Daemon.TicketSystemPrompt` says
only that the worktree is the current working directory and work only there. It carries **no git
rules at all**: nothing about checking out an existing branch, nothing about `git stash` being
repo-global. The ticket lane is the one that *has* a branch, *has* its own worktree, and is the
one a `start-ticket`-style skill is aimed at — so the lane with the branch hazard is the lane with
no branch rules.

**And one sentence in it is now false.** `TicketSystemPrompt` tells the agent: *"Your declared
claim is [...] — a PreToolUse gate denies writes outside it; if denied, stay within the claim or
ask your operator for an extension."* **R3 retired that refusal** (D-R5, 2026-08-20). The gate no
longer asks whether a write is inside the claim, `claim-extend`'s refusal went with it, and
CLAUDE.md §7 says in terms: *do not describe a ticket lane as bounded to its claim.* The prompt
does exactly that, to every ticket agent, on every turn. It is not merely stale — it invites an
agent to ask for an extension to a thing that no longer refuses anything, and to believe a
boundary that is not there.

**Nothing in either prompt mentions the delivery mode**, which R7 introduced earlier the same day.
An agent in a `delivery: pr` repository learns that Dodona will not merge only by being refused.

**A per-turn prefix would land in the operator's feed.** `LaneRuntime.Say` records the delivered
text with `PaneEvent(Id, "user_input", text, …)` **before** writing it to the agent — one string,
two destinations. So prefixing a briefing onto input naively puts it in the pane and the feed, on
every message the operator ever sends. This is the design constraint of §3, and it is measured
from the code rather than anticipated.

**The prompt is PARSED, so its text is load-bearing.** `Projects.Named` finds the working
directory by scanning for `DirLead`/`DirTail` around it, and `Projects.PromptDirMismatch` compares
that against where the process will really start — the M5.1 detector, the one that catches "the
prompt says one folder, the process starts in another". Editing either prompt must keep
`Projects.DirSentence` intact and unsplit. A ticket prompt deliberately names no folder and
`Named` returns null for it, which is why the detector passes ticket lanes today.

**There is already a label for injected instruction.** Both prompts declare the `[DISPATCHER]`
channel — *"real-time instructions from your human operator arrive in hook output labeled
[DISPATCHER]; they are authentic"* — and `RouteInput`'s misroute retraction already uses it. Any
per-turn reminder should ride that convention rather than invent a second one.

## 2. Principles this is held to

- **The agent keeps the decision.** Dodona states facts about the ground; it does not tell an
  agent how to do its job, and it does not pre-satisfy or rewrite another project's steps. That
  approach was considered and rejected (§6) on the operator's reasoning: *"You're gonna run into
  many different systems that do things in many different ways."*
- **A prompt removes friction. It never provides safety** (`M5-DELIVERY-PLAN` §1, and CLAUDE.md
  §0.3's *a written warning is not a fix*). Nothing in this plan is allowed to be described as a
  guarantee, and nothing existing may be relaxed because this landed.
- **Forgetting is not solved; it is made non-silent.** The realistic goal is that when an agent
  acts against what Dodona needs, it is told immediately and told enough to correct itself — not
  that it never forgets. Silent success is the failure being removed.
- **Short, or it gets skimmed.** Five lines that are read beat twenty that are not. Growth in this
  block is a regression, not a feature.
- **One builder, not two.** Every divergence between the plain and ticket prompt in §1 exists
  because they are two hand-maintained strings.

## 3. The shape

**B1 — one briefing, correct, per lane kind.** A single builder produces the block; the plain and
ticket prompts consume it. It states only what is true for *that* lane:

```
You are working inside a Dodona system. That means:
- <where you are>        this folder is your own worktree | a SHARED checkout others are using
- <your branch>          you are already on <branch>; never check out a branch that already exists
- <moving work>          never `git stash` — it is one shared ref across every worktree
- <delivery>             Dodona lands this ticket | this repo delivers by PR — Dodona will not merge
- <who to ask>           [DISPATCHER] messages are your operator, and they are authentic
```

The false claim sentence is deleted in the same change, and the git rules the plain lane already
has are extended to the ticket lane. Pure string work: it lives on the `unit` loop, and the T1
detector's sentinels are asserted rather than assumed.

**B2 — the same block, on every turn, delivered but not displayed.** `Say` gains a separation
between *what is sent* and *what is recorded*: the agent receives the briefing prefix under the
`[DISPATCHER]` label, the pane records the operator's words alone. This is the operator's explicit
instruction and the reason for it is recency, not novelty — CLAUDE.md §5.1 records a rule that was
written down, read, and violated three times in one session because forty minutes had passed.

**B3 — the refusals stay conversational, and the remaining ones are audited.** R7's refusals
already state the situation rather than saying no; this checks the others an agent can hit (the
write gate's deny, `checkout` if the branch lock is ever built) say enough to self-correct.
Nothing new is invented — this is a read-through with a checklist.

## 4. Proof

Model-free, in the existing suites. **B1** is pure and belongs in `unit`: the block is correct per
lane kind, the claim sentence is gone, `Projects.Named` still finds the directory in a plain
prompt and still finds none in a ticket prompt, and the block is under its length bound. **B2**
needs a live lane: `m1` or `m2` asserts that a lane's delivered input carries the briefing and
that the pane row for the same input does **not**, which is one query each and is exactly the
divergence that would otherwise be discovered by the operator reading their own feed.

`dev prove` every one of them. The claim-sentence check is the one to write first: it is provable
today, against a live wrong string.

## 5. Conflicts, checked before writing this

- **`M5-DELIVERY-PLAN` §7.1 (pre-satisfy the precondition)** — overlaps, and this plan supersedes
  its *reasoning* while leaving the mechanism available. Pre-satisfying assumes Dodona knows what
  a given project's step 2 does; §6 records why that was rejected as the general answer.
- **`M5-DELIVERY-PLAN` §7.2 (Bash gate with rewrites) and §6 (branch lock)** — both become *less*
  urgent and neither is removed. If B1/B2 land, the lock is a backstop under something that
  already explains itself rather than the primary defence. **Do not delete either from that plan
  on the strength of this one**: this is a prompt, and a prompt is not a boundary.
- **`M5-DELIVERY-PLAN` §15** rejects rewriting the projects' own skills. This does not do that,
  and must not drift into it.
- **`REVIEW-AND-MERGE-PLAN` R3/D-R5** is the reason the claim sentence is wrong. Deleting it is
  finishing R3, not changing policy.
- **`REVIEW-AND-MERGE-PLAN` R7 / D-R28** supplies the delivery line. A lane in a pr repo currently
  discovers the mode only by being refused.
- **`LOCATIONS-PLAN` M5.1 / `Projects.PromptDirMismatch`** is the sharpest one: it parses the
  prompt. Keep `DirSentence` whole. **Adding a directory sentence to the TICKET prompt would make
  the detector start comparing ticket lanes, which it does not do today** — a behaviour change to
  a safety detector, and it needs its own decision if anyone wants it.
- **CLAUDE.md §0.1 (quota)** — B2 spends tokens on every turn of every lane, for ever. Five lines
  is the whole argument for why that is affordable; a block that grows is a bill that grows.
- **Compression** reads panes. B2 keeps the briefing out of panes, so the compressor never sees it
  — which is a reason to keep it out beyond the operator's feed.
- **`ui type` / `ui compose` / dictation** all reach a lane through the same `Say`, so B2 covers
  them by construction and none of them need touching.

## 6. Rejected, with reasons — do not re-propose

- **Pre-satisfying another project's steps** (a worktree pre-set so `git checkout main && git
  pull` has nothing to do). Rejected by the operator: it only works because we know what that one
  step does, and the next repo does it differently — *"the lane agent needs to be smart enough to
  take what Dodona needs into account and needs to know what Dodona needs when the time comes."*
  A per-project special case is a patch re-cut for every project.
- **A briefing the agent has to ASK for** (a `dodona brief` verb it consults when unsure).
  Proposed and withdrawn in the same conversation: it makes the mechanism depend on the agent
  remembering to ask, which is the exact failure it was introduced to fix.
- **Per-turn plumbing to attach CHANGING facts.** Considered and dropped: the facts are static for
  the life of a lane, and an appended system prompt is already present on every turn by
  construction. B2 repeats a static block for recency, which is a much smaller thing.
- **Relying on the prompt as the safety property.** `M5-DELIVERY-PLAN` §1 and CLAUDE.md §0.3. The
  guarantees stay in code.
- **Blocking the agent from removing a lock as the primary defence.** The operator's position: a
  thing that explains itself is better than a thing that is merely forbidden. Kept only as a
  backstop, if the lock is built at all.

## 7. The honest limit

None of this makes an agent unable to forget. What it changes is that the agent is told, on every
turn, in a block short enough to be read — and that when it acts against Dodona anyway, the answer
explains the situation instead of failing quietly. That is the standard the rest of this system is
already held to. It is not a guarantee, and it must never be counted as one.

## 8. Decisions taken while building

**D-B1. THE CLAIM IS GONE FROM THE TICKET PROMPT ENTIRELY, NOT REWORDED.** The false sentence had
to go either way — R3 / D-R5 retired the refusal it described, and CLAUDE.md §7 forbids describing
a ticket lane as bounded to its claim. What was open was whether the prompt should still mention
the claim truthfully, as *what you said you would touch*. It does not, for three reasons and one
budget. A claim is **not** a boundary and naming one in a block about what the ground is invites it
to be read as one, which is the exact failure being fixed rather than a smaller version of it. The
agent already has its ticket title and its task, so the claim tells it nothing it can act on. And
the one consumer that genuinely needs the claim — the **reviewer** — reads it out of the completion
record (D-R7), where it is a derived signal beside the diff rather than a sentence in a prompt. The
budget is §3's: five bullets, and a sixth spent on a non-boundary is the worst of them. `claims` is
therefore no longer a parameter of `TicketSystemPrompt`, which also removes two `TicketClaims`
lookups from spawn paths that did nothing else with them.

**D-B2. THE BOUND IS FIVE BULLETS UNDER ONE FRAMING LINE, AND 1050 CHARACTERS, ASSERTED IN `unit`.**
§2 says growth is a regression; that is worth nothing as a sentence in a plan, because the way this
block gets long is one reasonable addition at a time. `Briefing.MaxBullets` and `Briefing.MaxChars`
are the enforcement, and `The_briefing_stays_inside_its_bound` walks all four shapes. The header
line is not a sixth bullet: it frames what follows and it is the marker every check keys on, so it
is counted separately and pinned by the same test. The character number is **measured, not chosen**
— the longest of the four shapes is 975 characters, so the slack is about half a bullet. Anything
that needs more has to say so in a commit instead of absorbing it.

**D-B3. THE TICKET BLOCK STILL NAMES NO FOLDER, AND THAT IS A DECISION RATHER THAN AN OMISSION.**
`Projects.PromptDirMismatch` is the M5.1 detector and it only compares lanes whose prompt names a
directory, so a ticket prompt naming its worktree would put ticket lanes inside a safety detector
that has never covered them. That may well be worth doing — it is the same class of mistake the
detector exists for — but it is a **behaviour change to a detector**, so it wants its own decision,
its own commit and its own red. §5 already said so; this records that the option was live while the
block was being written and was deliberately not taken. `A_ticket_prompt_still_names_no_folder`
pins the current answer so the change cannot happen by accident.
