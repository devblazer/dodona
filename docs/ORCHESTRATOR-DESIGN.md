# Multi-agent orchestrator — design decisions

Working notes. Lives outside every clone, so no branch, merge or reset touches it.

Amended 2026-08-17 after design review. [ORCHESTRATOR-REVIEW.md](ORCHESTRATOR-REVIEW.md)
holds the rationale, evidence and machine measurements behind every change; this file holds
only the decisions.

Goal: issue tasks by voice at conversational speed, have several agents work them in
parallel on the same project, and never have two agents build competing versions of the
same thing — without the coordination itself becoming the bottleneck.

---

## 1. The governing rules

**A model is only in the loop when there is an actual judgment to make.**

Everything else — routing a message that names its target, checking whether two claims
overlap, answering "what is lane 3 doing", handing out a merge token — is data, and is
answered by code in microseconds.

Corollary: **anything with one context is a serialization point.** Any component that
agents must wait on has to be stateless and short-lived, or throughput collapses to one
turn at a time.

**No model is ever between a keystroke and visible feedback.** Responsiveness is the
requirement; latency numbers are implementation detail. Code acknowledges instantly,
models refine what code already did, asynchronously and visibly. Measured reality on this
machine: a one-shot `claude -p` call is 3.6–3.8s and a warm model turn ~1s — so anything
that must *feel* instant must not contain a model at all.

---

## 2. Runtime

- **Primitive:** `claude -p --input-format stream-json --output-format stream-json`.
  A bidirectional streaming session over stdio. Language-agnostic, drivable from any
  process, and it runs on the Claude Code subscription because it *is* the CLI.
  The Agent SDK is a convenience wrapper over the same binary — optional, not required.
  - The stream-json wire schema is **not formally published**. The .NET driver is written
    against observed behavior: pin the CLI version per Dodona release, and a protocol
    smoke test runs in the build.
  - Sessions are JSONL on disk, keyed to the **cwd** — worktree removal interacts with
    session storage, and default retention is 30 days (`cleanupPeriodDays`): set it high.
- **One OS process per lane**, each in its own **git worktree** (not a clone: cheap,
  instant, own branch, own build output).
- **Registry is a local store, never a service.** A service can be busy. See §12 — it is
  SQLite in WAL mode, one writer per table, and it holds claims, lane state, ticket
  state, session ids, the merge token, the pane-event log, per-lane instruction queues,
  decision rows, and the audit/routing logs.

### The daemon interface

Agents and the dispatcher talk to the daemon through **MCP, not a shelled-out CLI**: a
~300-line C# stdio proxy (`ModelContextProtocol` NuGet) that connects to the daemon's
per-instance named pipe, passed via `--mcp-config` inline JSON at spawn. Typed tools the
model cannot malform, visible in context so the protocol is discoverable rather than
memorized, auto-approvable via an `mcp__dodona__*` allowlist, and it reconnects across
daemon hot-swaps exactly like the shim (§13).

- Agent tools: `claim_declare`, `claim_extend`, `merge_token_request`,
  `merge_token_release`, `land_ticket`, `status_update`, `ticket_info`.
- Dispatcher adds: `lane_create`, `ticket_create`, `agent_kill`.
- A small human-facing `dodona` CLI talks to the same pipe for debugging by hand, and is
  the debug surface for an outside session (§12, *Debuggable from a different chair*).
  Agents never shell out to it.

### Stack

C#/.NET end-to-end; a second language buys nothing for local I/O-bound plumbing.
Daemon: Generic Host console app, normal user process — never a Windows Service (session 0,
and claude auth lives in the user profile). Shim: dependency-free, self-contained/AOT
publish so "essentially never changes" stays true; the pipe protocol is versioned anyway.
Pipes: `System.IO.Pipes`, length-prefixed JSON frames. Store: `Microsoft.Data.Sqlite`,
plain SQL, `PRAGMA user_version` migrations — the daemon holds the sole write connection;
the UI reads via a read-only WAL connection and sends writes over the pipe. UI: WPF; if
streaming-markdown fidelity ever disappoints, host the grid in one WebView2 later — cheap
precisely because the UI is disposable (§13).

---

## 3. Roles

Three, and conflating any two of them is the failure mode.

| Role | Persistent? | In whose path | Job |
|---|---|---|---|
| **Dispatcher** | session, recycled | yours | Your conversation partner. Routes input, spins lanes up and down, creates tickets. |
| **Adjudicator** | **no** — stateless short-lived calls | the agents' | Resolves claim conflicts. Reads the decision log, answers one question, appends. |
| **You** | — | — | Final say, anywhere in the ladder. Priority, scope, product intent, and override of anything already decided. |

The dispatcher blocking **agents** is what kills throughput. The dispatcher blocking
**you** for two seconds is fine — you are serial anyway.

**The dispatcher's context holds lane titles, states and your recent instructions —
never agent transcripts.** The moment it ingests what agents say it grows unbounded and
becomes the slow thing it was built to avoid.

**The dispatcher session is disposable like the agents.** Everything it needs lives in
the registry, so at ~50–100 turns a fresh session is seeded from a registry-generated
brief and blue/green swapped at an idle moment — the pane never blanks, and in-session
auto-compaction never fires at an unpredictable moment in the one component whose
responsiveness matters most.

**Adjudication costs minutes, not seconds** — a cold start plus a grounded read of the
codebase, 30s–3min. Fine at its volume (~10% of claims), but nothing may be designed
against a seconds-scale assumption. Apply §9's own rule to it: pre-seed the call with
both claims, the relevant decision-log excerpt and the overlapping paths, so it rules on
evidence handed over rather than re-discovered.

### Escalation ladder

1. **Registry (code)** — do these claims overlap? Facts. Nobody wakes up.
2. **Adjudicator (model)** — answerable from the codebase and existing conventions.
   Decides, then tells you.
3. **You** — nothing in the repo answers it. Intent.

You are not a tier at the top; you are an **override that can land anywhere**. The
decision record is therefore editable, not append-only-and-final.

---

## 4. Routing — instant by default, corrected visibly

Everything goes through the router. No bypass.

**Focus is a prior, not a target.** Typing into the SKYBOX pane is evidence, not a
command; content that is clearly meant for another lane overrides it.

**Delivery is optimistic.** Input goes to the focused lane *immediately* (tier 0 excepted
— an explicit prefix is its own answer). The classifier runs behind it as an async second
opinion; when it disagrees, the message is visibly retargeted. The router's latency is
therefore off the critical path entirely — it went from a gate in front of every message
to a correction that occasionally fires. When confidence is low *and* the focus prior is
weak (voice, eyes on the game window), delivery may hold for a ~2s grace so undo is a
true recall.

Tiers get the **same question and more evidence** — you buy context, never thinking time:

| Tier | Latency | Inputs |
|---|---|---|
| 0. Explicit prefix (`water: …`) | ~0ms | code only |
| 1. Warm Haiku, async | ~1s | lane ids + titles, focused lane, your last turn or two → `{intent, target, confidence, cleaned_text}` |
| 2. Bigger model | ~2–4s | + lane briefs, what each agent is currently doing, recent decisions |
| 3. Ask you | one line back | only when tier 2 is still split |

Tier 1 is a **persistent warm session** owned by the daemon, never a one-shot spawn
(§1's measurement). It also does the input cleaning in the same turn — router and cleaner
read the same text, so they are one call, and `cleaned_text` is used when
`intent = instruction`. Blue/green recycle at ~100 turns, replacement warmed with a no-op
turn before cutover.

Tier 1 must be willing to say **unsure**. A small model that guesses confidently is
worse than no router at all — hence the explicit confidence field and a tunable
threshold. **Every routing decision is a row**: input, tier reached, confidence, target,
undone-by-you flag. The undo keystroke is free labeled data; the threshold is tuned from
it, not by feel.

Tier 3 is slow because *you* are in it, never because a model is grinding. There is no
tier where something sits and reasons about where to put a sentence.

**A retarget must be visible.** Message lands in the destination pane; the focused pane
shows a `→ sent to WATER` receipt, undoable with one keystroke. A silent correct
redirect and a silent wrong redirect look identical. **Undo beats accuracy** — a fast
wrong guess you can revoke in one key beats a slow careful one. Undo knows whether the
instruction was **consumed**: unconsumed → true recall; consumed → a structured
retraction ("disregard message N, misrouted") is injected and the message redelivered.
You cannot unread an instruction, so the undo compensates instead of pretending.

---

## 5. Talking to agents

**Instruction injection.** Pushing a message into a mid-turn agent makes it re-evaluate
everything. Cheaper: the instruction lands *inside* the turn already running — one extra
tool result instead of a turn restart. Mechanism (measured, spike 3: pickup ~335ms after
enqueue, at the next tool boundary; a contradicting instruction was applied mid-turn):
hooks return `additionalContext` (≤10k chars) into the live turn. Protocol: instructions
are rows in a per-lane queue in the store; the hook fetches unseen-since-cursor through
the shim, and the cursor advance is the ack. No scratch files — one writer, no torn
reads, and an amendment nobody read is distinguishable from agreement.

**The channel must be declared, or the model refuses it.** Every lane agent's spawn line
carries a system-prompt sentence naming the `[DISPATCHER]` label as its operator's
authentic real-time voice — without it, the agent treats a mid-turn contradiction as a
prompt-injection attempt and completes the original task instead (measured, spike 3).
That refusal is a **free security property**: declare exactly one channel, and an
instruction planted anywhere else — a file the agent reads, a tool result — dies the
same death.

**Compression at the boundary — but most of it is not a judgment.** stream-json already
carries structured tool events, so presence lines (`editing Water.cs`, `running tests`,
`idle`) are computed by **code, zero model calls**. The Haiku pass is reserved for the
messages where compression is a judgment: turn-final summaries, BLOCKED/needs-you, and
announcements — a 5–10× volume cut. Forced into a fixed schema before it reaches you:

```
[SKYBOX] BLOCKED — needs a name for the water-in-frame rule
   options: contest / harder-wins
```

Structured output, so it cannot ramble. Raw text one keystroke away. The compressor is a
small **pool** (2–3 warm sessions, round-robin, recycled like the router) — never one
session, or it becomes the unbounded serialization point §3 forbids the dispatcher to be.

Your own input is cleaned by the router's same turn (§4) — voice dictation is messy, and
cleaning it before injection costs nothing extra.

**Response feel.** Split your inputs by what they are:

- **Queries** ("what's 3 doing", "who's touching the fluid code") — registry state,
  answered by code, ~0ms. Probably 70% of what you say.
- **Instructions** — instant deterministic ack (`→ SKYBOX`), model work happens async
  and invisibly.
- **Real questions** — the only place a model turn is in your path. Rare, and the
  answer is *structurally* short (output schema with a line cap, not a "be concise"
  instruction it will ignore).
- **Honest budgets:** the ack is <100ms and is the felt latency of every input; a model's
  first token is 1–2s. Stream it, and render a thinking indicator so the pre-token gap
  reads as alive rather than dead.

---

## 6. Claims and collision

**Claims are declared before work, not at merge.** An agent's first act is "here is what
I will touch": paths, new symbol names, doc sections, and one sentence of **intent**.

**The claim algebra is deliberately small.** Path claims are literal paths,
directory-subtree prefixes, and declared-new literal paths — intersection is
equality/prefix checks, microseconds, and files that don't exist yet are covered by their
subtree. No arbitrary globs: glob-vs-glob intersection is a language-theory problem, not
a set operation, and expanding against the current tree is blind to exactly the files
agents create. A rename claims both old and new paths. Claims are released **in the same
transaction** that marks the ticket landed or abandoned.

No overlap → accepted synchronously, microseconds, no model, no adjudicator awake. That
is ~90% of claims. Only an actual intersection escalates. Contention surfaces at plan
time, when redirecting is free, instead of at merge, when it costs the whole task.

**Claims are enforced, not advisory.** Two code-only layers:

1. A **PreToolUse hook** in every worktree gates writes against the lane's current claim.
   A violation denies with "request a claim extension" — and `claim_extend` re-runs the
   intersection, which makes extension the natural escalation trigger.
2. A **merge-time backstop**: at token request the daemon diffs the branch against its
   merge base and refuses the token if any touched path is outside the claim. This
   catches what hooks can't see.

**Semantic collisions have no file overlap** — two agents building `WaterFlowController`
and `FluidFlowManager` in disjoint new files intersect on nothing, and that is the
headline failure this system exists for. The `intent` sentence closes it: after the code
intersection passes, one cheap model comparison against active tickets' intents; above a
similarity threshold → adjudicator. Consistent with §1 — concept overlap *is* a judgment,
so a model belongs in it, and the code-only fast path still carries the ~90%.

---

## 7. Merging

Agents do the work — rebase, conflict resolution, verify. **Neither the dispatcher nor
the app does this**; both are single-threaded chokepoints. What is serialized is the
*permission*, and the one atomic act of exercising it:

```
agent: rebases onto current main, runs verify (§10 config) in its own worktree
agent → daemon: request merge token            (grant records the main SHA verified against)
daemon (code, FIFO): granted / queued          (lease: heartbeat every N s, expiry 3N)
main unchanged since verify → daemon lands it: one atomic ref update,
    lease owner + generation checked in the same store transaction that records the land
main moved → agent re-rebases and re-verifies  (build-only when the intervening
    landings had disjoint claims), then lands
agent → daemon: release                        (claims freed in the same transaction)
```

**Verify happens *before* the token**, so the token is held for seconds, not builds —
otherwise six lanes serialize behind full builds and a one-line change queues behind a
fifteen-minute one. **The final ref update is executed by the daemon**, which is not the
daemon doing the work — it is the permission being exercised as one fenced write. An
expired holder physically cannot land, and git's hazards around updating the branch
checked out in your own primary worktree are sidestepped.

Recovery is mechanical: on restart or lease expiry, an ancestry check says landed-or-not,
and every cleanup step is idempotent and re-runnable.

Per-ticket flag: `merge: on-approval` (the ticket does not enter the FIFO until you tick
it — an unapproved ticket can never block the queue head) or `merge: auto` for work you
do not care to review. `merge: auto` requires a green verify run inside the token window,
post-rebase — never the agent's self-assessment.

At the git layer, in `delivery: local-merge` (§7.1), the token is what an agent must hold
to reach main at all — landing exists only as the daemon's `land_ticket`, which checks it.
An earlier revision of this section said each worktree's permission policy *denies `git
push` and merges into main*. That deny was **never implemented** (the deployed gate hooks
`Edit|Write|MultiEdit|NotebookEdit` only), and it must not be, because a `delivery: pr`
repo reaches main precisely by pushing. §7.1 replaces it.

**General pattern: code hands out permission, agents do the work.**

### 7.1 Delivery is a per-repo mode: who merges, and when the ticket ends

Recorded 2026-08-18, after the operator asked what happens to a work repo whose own
CLAUDE.md and skills own the ticket process end to end — branch off `develop`, push, open a
PR, a human reviews, the forge merges. Everything above assumes **Dodona performs the
merge**. For those repos it never does, and the whole token/FIFO/fence tier has no job: the
forge already serializes merges, with CI and required reviews, strictly better than a local
token can.

**Superseded in two places by [M5-DELIVERY-PLAN.md](M5-DELIVERY-PLAN.md)**, which is the
authority for this work: (a) a ticket is no longer one repository — it is an ordered GROUP of
repo members, because the real workflow always touches core AND a game with a dependency
between them; (b) the token is NOT jobless here — it becomes a per-repo **release slot**, since
two concurrent ships both minting the next version number is a race no forge can serialize.

So delivery is one per-repo axis in that repo's `dodona.json` (`Config.For` already falls
back per-repository, so this needs no new plumbing):

- **`delivery: local-merge`** (the default; everything above) — Dodona names the branch,
  holds the token, executes the ff-only land, runs post-land verify, prunes.
- **`delivery: pr`** — Dodona provides *isolation only*. It never merges, never deletes a
  branch, never requests a MERGE token (it still takes a release slot — plan doc §9). The
  project's skills own branch naming, push, and the PR.

**Isolation and ceremony are separable, and that is the whole design.** The worktree is
isolation — it is what stops two lanes fighting over one index, and it costs the project
nothing: `push`, `pull --rebase` and `gh pr create` all work from a worktree, because push
is repo-level and the remote cannot tell. Branch naming, base ref, PR and merge are
ceremony, and ceremony belongs to the project.

Consequences for `delivery: pr`, each a real behaviour change:

- **The worktree is created detached** — no `-b`. Dodona must not pre-name a branch that
  the project's convention will name (`feature/ABC-123`); it **learns** the name afterwards
  (`rev-parse --abbrev-ref HEAD` → `TicketSetGit`). The worktree DIRECTORY keeps its short
  opaque `t<N>`: directory name and branch name are independent, nothing outside Dodona
  ever sees the directory, and short paths are a Windows `MAX_PATH` margin once an
  enterprise repo's `node_modules` sits under it.
- **The end of the agent is not the end of the ticket.** States become
  `open | in-review | landed | abandoned`. With the PR open the agent retires and the lane
  goes dormant, but **the worktree survives** — a review comment three days later needs
  that exact branch back. Today's `land` force-removes it, which is backwards.
- **Landed is observed, not performed**, and per §11's never-ask rule the observer arms
  itself instead of waiting for someone to type `dodona land`.
- **Never test merged-ness by ancestry.** After a squash-merge the branch is *not* an
  ancestor of main, so `git branch --merged` says no forever and every worktree would be
  held for ever. Key on PR state (`gh pr view --json state`) or `git cherry`.
- **The claim gate and the diff backstop are unaffected** — a base-ref diff needs no merge,
  so §6 layer 2 keeps working without a land.

### 7.2 The worktree compatibility contract for project-owned skills

A skill written for a normal clone says things like "switch to develop, pull, cut your
branch". Sorted by what that actually does inside a worktree:

- **Fine**: `commit`, `fetch`, `push -u origin HEAD`, `pull --rebase`, `gh pr create`,
  `checkout -b <new>`, `reset --hard` (scoped to this worktree).
- **Fails loudly, therefore safe**: `checkout develop` while develop is checked out in
  another worktree — `fatal: 'develop' is already checked out at ...`. Likewise `branch -d`
  of a branch held elsewhere. The agent sees an error and adapts.
- **Succeeds and silently does the wrong thing — the entire problem**: `checkout main` when
  main is checked out *nowhere* else, so the worktree wanders off its branch and the
  recorded branch goes stale; **`git stash`**, because the stash is a single shared ref in
  the common dir, so two lanes interleave one stack and `pop` takes another lane's work;
  and anything treating `.git` as a directory, since in a worktree it is a file.

Four defences, cheapest first:

1. **A branch lock: Dodona holds every shared branch itself.** Git refuses to check out a
   branch that is already checked out in another worktree, so one sentinel worktree per
   protected branch (`main`, `develop`, `release/*`) makes the worst silent case LOUD:
   `checkout develop` inside a lane worktree becomes `fatal: already checked out at ...`.
   Create it with `git worktree add --no-checkout` — that sets HEAD to the branch without
   populating any files, so the lock costs near-zero disk even on a large enterprise repo.
   The operator asked whether a lane should instead REFUSE TO START unless the project is
   on main (2026-08-18). Same instinct, weaker mechanism: it makes safety depend on where
   the operator happens to have their own checkout, and it lapses silently the moment they
   switch branches for their own reasons. The lock does not. And per §11 a missing lock is
   CREATED and announced at lane start, never a refusal to start — an invariant that
   establishes itself, not a human who has to be on the right branch.
2. **Enforcement, not instruction** (CLAUDE.md §0: advisory gets skipped): extend the
   deployed gate's matcher to `Bash`, inspect `tool_input.command`, and deny with a
   **rewrite** rather than a refusal. The predicate is not "no branch but main" — a lane
   MUST be able to cut `feature/ABC-123`, that IS the PR flow. It is: **allow `checkout -b`
   / `switch -c` (creating your own branch), deny checking out a branch that already
   exists**, and deny `git stash`. Each deny hands back the substitution that satisfies the
   skill's intent — *"you are already on `feature/X` in a dedicated worktree, skip the
   checkout"*; *"refresh without a checkout: `git fetch origin && git rebase
   origin/develop`"*; *"commit WIP to your own branch instead of stashing"*. A deny that
   says what to do instead keeps the project's own skill running rather than stranding it.
3. **Drift detection** for what slips: record the branch on first observation, re-read it
   at each turn-final (one git call), announce and badge on unexpected change.
4. **`DODONA_WORKTREE=1` plus the branch in the shim environment** (`AttachShimAsync`
   already injects two such vars) so a skill the operator *does* own can make its
   switch-and-pull step conditional instead of being denied.

Defence 4 is insurance, never a prerequisite: Dodona must be safe against an **unmodified**
skill, because most repos are not the operator's to edit, and any skill can be changed by
someone else next week.

---

## 8. UI

Windows app. All agents presented as separate conversations, indistinguishable from
talking to individuals, even though it is one process behind them.

**Even grid, stable order.** *(Revised 2026-08-18 by the operator: "6 slots was your idea. I
want self dividing self optimising layout that grows splits as needed. Ability to also collapse
tiles if im not currently dealing with them." and "i dont want scroll. if needed user will just
collapse more." The fixed 3×2 and the six-lane cap below are superseded; what follows is what
survived and why.)*

The grid **divides itself** as lanes arrive — 1 fills the pane, 2 side by side, 3–4 at 2×2,
5–6 at 3×2, 7–9 at 3×3, and on. Panes shrink; **nothing scrolls**, and crowding is the
operator's cue to collapse rather than the system's cue to hide. The operator collapses any
tile to a one-line chip that keeps its colour, presence, badge and blocked glyph, and clicking
it expands again. Collapse is a store row, so it survives closing the window.

**What the fixed grid was protecting is kept, because it was right: stable ORDER.** Lanes are
ordered by creation and never reshuffle, so "SKYBOX is the second tile" stays true all session
and you do not re-read titles on every glance. Growth appends; a lane that dies leaves its
neighbours' order alone. Colour still means the lane.

**Attention-sized panes stay rejected** — tiles are uniform. Growing with the count is not the
same as sizing by who wants attention, which is the thing that was harder to follow.

One keystroke **overlay-maximizes** a pane (raw transcripts and diffs need somewhere to
render); Esc restores; the grid never reflows underneath.

**The unit is a lane, not an agent.** Agents are fungible — one can die, restart, or be
swapped for a fresh context, and the thread must survive that. A lane groups related
tickets; sequential work in the same area shares one.

- Colour + one-word title (`SKYBOX`, `WATER`, `UI`).
- **Colour means the lane, not the state.** A pane that changes colour moves in your
  peripheral vision.
- **No cap on the grid.** *(Superseded 2026-08-18.)* The cap was justified as a feature —
  it stops you starting nine things and tracking none — but the operator now favours a new
  lane per distinct task (WORKSPACES-CONCIERGE.md §5), so a cap fights the routing policy
  instead of protecting them from it. The grid grows; the operator collapses.
  **Trayed lanes are still dormant** — the tray means "not started", nothing else.
  **An active-but-invisible lane is still forbidden**, and that clause is the reason the cap
  had to go rather than merely being relaxed: the implementation had drifted into violating
  it. A seventh live lane appeared only as a NAME in the tray — agent running, badge
  unseeable, and it could have been blocked on you with no visible signal. Now every live
  lane has a tile, expanded or a chip, and a collapsed chip still carries its badge and its
  blocked glyph. `tests/ui-use-acceptance.ps1` asserts exactly that.
- **Dispatcher pane fixed in the right column**, outside the grid, always in the same
  place. Right, not bottom: conversation history is vertical, the panes stay squarer, and
  the column is the natural home for the decision feed and the tray.

**Attention.** Message classes, not one badge for everything — or badges are blind in a
week, the same failure the toast rule avoids:

- **Progress never badges.** The presence line already shows `working…` / `idle`.
- **Announcements** increment a count badge.
- **Blocked on you** is categorically distinct: glyph + border highlight (border, not
  fill — colour still means the lane). A `merge: on-approval` request is a blocked state:
  presence reads `waiting on you: merge`, and the announcement offers one-key
  approve/deny.
- Windows toast **only** when the app lacks focus **and** a lane is blocked on you.
  Never for progress, or they get muted within a day.
- **The decision feed** is pinned in the dispatcher column: every announcement appears in
  its lane *and* here, persists until acked, undone, or its undo validity expires, and
  each row is an undo target. A decision the adjudicator made on your behalf is the one
  message type where missing it costs a wrong build — it does not get to scroll away
  between six panes.

---

## 9. Keeping agents fast

Three real levers, no vague ones:

- **Reasoning effort per ticket.** Mechanical work runs low, design work runs high.
- **Small tickets.** A vague one makes the model spend its whole budget deciding what
  you meant.
- **Pre-seed context.** The claim already names the files — hand them over instead of
  letting the agent spend four minutes rediscovering them.

### The budget is the subscription window

Six concurrent lanes ≈ six model-hours per wall-clock hour, drawn from the same pool as
the dispatcher, router and compressor. An Opus-default fleet can exhaust a weekly cap in
under a day; an intra-day burst can lock *everything* out mid-turn. The **quota governor**
is a first-class daemon component, not polish:

- stream-json carries per-message usage — metered per lane and model into the registry.
- Model per ticket type is policy: Sonnet default, Haiku/low effort for mechanical
  `merge: auto` work, Opus only for explicitly design-tier tickets.
- As the window fills, admit fewer lanes and queue non-urgent tickets — never let all six
  stall mid-turn at the cap.
- A usage presence line lives in the dispatcher pane. Calibrated from measured telemetry,
  not published numbers.

---

## 10. Seeing what an agent sees

Worktree per lane means the build is already isolated. The app launches it —
`run skybox` — with a per-lane port/instance so several can be up at once. **You look
yourself**; an agent screenshotting for you is slower and less trustworthy.

**The daemon owns what it launches.** Pid recorded at launch; window title stamped with
lane name + colour so six running instances are tellable apart; `run skybox` focuses an
existing instance instead of spawning a second. On ticket land, a live instance is closed
gracefully with an announcement (`SKYBOX build closed to land ticket`) or the worktree is
lazily pruned on exit — never a silent failure (§15).

Per-project config, `dodona.json` at the project root (cached in the store): the verify
steps (`dotnet build MassWorks.sln -c Release -warnaserror`, then `dotnet test`),
timeouts, the definition of *land* (ff-only merge to main; push optional), and the
per-lane run commands and ports. The daemon — code, not a model — runs the post-merge
verify on main itself; red auto-creates a blocked ticket routed per §11, and a red verify
while holding the token auto-releases it.

---

## 11. Lifecycle — lane, ticket, agent, worktree

**Three different lifetimes, explicitly not one-to-one.**

| Thing | Lifetime | Owned by |
|---|---|---|
| **Lane** | long-lived, spans many tickets | you — it is a pane and a subject |
| **Ticket** | one unit of work that lands | the queue |
| **Agent** | disposable | nothing — killed and replaced freely |

**Git sits on the ticket.** A branch is a thing that lands, and that is exactly what a
ticket is.

- Ticket created → worktree + branch created.
- Agent killed or replaced mid-ticket → the new agent **attaches to the existing
  worktree**. Nothing is lost; this is what makes agents disposable.
- Ticket lands → worktree removed. The lane stays.

**Worktree creation deploys the permission policy**: a `.claude/settings.json` with
acceptEdits; Read/Edit/Write scoped to the worktree; a Bash allowlist for
`dotnet build/test` and branch-local git; an explicit **deny on `git push` and merges
into main** (§7); the claim gate hook (§6); and residual permission prompts routed
through `--permission-prompt-tool` to a daemon tool — auto-approve in-worktree, anything
else flips the lane to `waiting on you`. A headless agent with no policy stalls at its
first edit; one with `--dangerously-skip-permissions` is unbounded. Neither.

### Branch from main, or continue an in-flight branch?

Answered by the **same set intersection** the claim registry already does. No model.

- **New ticket's claim ∩ in-flight ticket's claim = ∅** → branch from `main`, run in
  parallel. No question asked.
- **Overlap** → the new ticket **queues behind the in-flight one in the same lane, with
  its own worktree** branched from that ticket's head (or from main once it lands). It
  never shares a worktree — a branch *is* a ticket, and "ticket lands → worktree removed"
  must never pull the rug from under a second ticket.

A brand-new ticket has no claim yet. So the cheap call at ticket creation is not "should
this wait?" but **"what will this touch?"** — one warm-Haiku pass over the ticket text
plus the lane's file history (~1–2s, async — the lifecycle decision must never be built
on synchronously), producing a provisional claim *and the intent sentence* (§6). The
agent's first act is **claim refinement, which mandatorily re-runs the intersection**:

- refined ⊆ provisional → proceed;
- overlap with an in-flight ticket → delete the just-created worktree (seconds, by this
  design's own economics) and re-branch per the rule above;
- ambiguous → adjudicator.

### Never ask — act, announce, allow undo

**Nothing an agent does is irreversible until it merges, and merging needs a token
(§7).** So a wrong automatic decision costs a killed lane and a deleted worktree —
seconds.

- Every lifecycle decision — new lane vs fold in, wait vs continue, kill and restart an
  agent — is made **automatically**.
- It appears as a **one-line announcement** in the pane and the decision feed, never as
  a prompt.
- **One keystroke reverses it.**
- You are asked only when an action cannot be undone, which given the merge token is
  effectively never.

**The undo record is concrete, not a promise.** Every automatic decision writes a row:
`{id, ts, kind, lane, ticket, params, inverse_op, snapshot, validity}` — the snapshot
captures what reversal needs at decision time (branch SHAs, session ids, message id).
The announcement carries the decision id; undo executes the inverse. The set of inverses
is closed:

| Decision | Inverse |
|---|---|
| retarget | unconsumed → recall; consumed → structured retraction + redeliver (§4) |
| fold into lane | kill agent, reset ticket branch to snapshot SHA, spawn new lane seeded with the extracted brief |
| new lane | kill lane, delete worktree, deliver brief to the target lane |
| kill-and-restart | kill replacement, `--resume` the old session, reset worktree to snapshot |

Undo is honestly "kill + reset + reseed, seconds" — not a perfect state rewind, and it
does not need to be. The `validity` predicate (e.g. *until the ticket merges*) makes an
expired undo refuse with a one-line reason instead of doing something surprising. An
undo writes its own decision row, so redo is free.

Policy is set once, not per case. The tier-3 "ask you" path survives only for genuine
product intent — *should we build this at all* — which is the one thing no keystroke
recovers.

### Where a ticket's brief comes from

The same place the ticket does. You said something; the dispatcher decided a ticket was
needed. What you said **is** the brief, cleaned by the router's pass (§4). No separate
briefing step, no form to fill. But the loop closes:

- The ticket announcement shows the distilled brief **verbatim** — not a paraphrase —
  and is undoable like any decision (undo = cancel before real work accrues).
- The **raw dictation is stored beside the cleaned brief**; you and the agent can both
  consult it when the cleaning reads oddly. Cleaning makes mishearings *more* convincing,
  not less — the raw is the arbitration record.
- Brief edits are first-class: a small edit injects `brief revised: <diff>`; large drift
  kills and reseeds the agent — already priced as cheap.
- The cleaner is fed the lane titles and recent ticket nouns, so mishearings correct
  toward known vocabulary instead of being normalized into something plausible-but-wrong.

### Voice

The goal says voice; the design is input-agnostic, so voice is **its own late milestone**
and costs nothing to defer. Until then: text, with Win+H dictation into the input box as
a zero-code stand-in that exercises the cleaning pass on real dictation. The input
boundary is designed now: the router consumes `{text, modality, focused_lane, ts}`, and
voice input down-weights the focus prior — while talking you are usually looking at the
game, so focus is stale evidence (§4's grace hold).

### A red verify after merge

The dispatcher decides — it is a judgment call, so a model belongs in it. If it is
genuinely unsure it asks you, **in whichever pane is most relevant**. Only when no lane
is relevant does the question go to the dispatcher's own pane.

---

## 12. Durability — bulletproof against corruption and timing

The whole system is a fiction over one process. That is only safe if the state
underneath it cannot be corrupted or raced.

**SQLite, WAL mode, `synchronous=FULL`. Not atomic-write JSON.** JSON whole-file writes
are last-writer-wins: two agents claiming at once and one claim silently vanishes — the
exact failure this system exists to prevent. FULL because at this write rate its cost is
nothing, and NORMAL's power-loss window is a lost claim row that silently re-enables
collisions after a reboot.

**One writer per table.** The daemon is the sole mutator of all *state* — claims,
tickets, tokens, lane state — which removes multi-process write races on the things that
matter *by construction*. Each **shim writes its own append-only inbox table**: while the
daemon is down mid-swap, inbound messages still become rows instead of dying in pipe
buffers, per-lane ordering comes free from rowid, and disjoint writers on WAL are safe.
This is what makes §13's "nothing lost during a swap" actually true rather than asserted.

**The writer is code. There is no model anywhere in it.** Accepting a claim, granting a
merge token, updating lane state — these are set operations and row writes, decided by
arithmetic, not deliberated. A model may *ask* the daemon for something; it is never the
thing carrying out the write. The instant a model sits in the write path, every agent in
the system queues behind something that thinks.

**Every state change is one transaction.** No read-modify-write spanning a process
boundary. Claim-check and claim-insert happen in the same transaction, or two agents
both pass the check and both proceed.

**Leases, not locks.** The merge token has an owner, a generation number, and an expiry,
renewed by heartbeat (§7). A crashed agent holding a lock forever is otherwise a deadlock
you fix by hand at 2am.

**Idempotent operations, keyed.** A retry after a crash must not create a second
worktree or land a branch twice. Request ids from day one — retrofitting them after the
first crash-retry bug means redesigning the protocol.

**Git is the truth for git.** The registry is a cache of it. On daemon start, reconcile
against actual branches and worktrees rather than trusting the row. That is the recovery
path from any inconsistency, and it is cheap.

**Everything the UI shows is a row.** `pane_events(lane, seq, ts, kind, body,
decision_id?, raw_ref?)` — user inputs, compressed agent lines, announcements, receipts.
A pane is a replay of the last N rows; `raw_ref` points at session-id + message-uuid so
"raw one keystroke away" survives a restart too. Draining the swap queue *moves* rows
into pane_events, never deletes them. Without this table, §13's "a pane is a view" is a
slogan; with it, it is a query.

**The audit trail is schema, not logging.** An append-only `events` table (every claim,
token op, lifecycle action), the `routing_decisions` table (§4), raw stream-json teed to
`logs/<ticket>/`. A self-hosting concurrent system with no audit trail is brutal to
debug, and §4's "tunable threshold" is untunable without recorded decisions.
`PRAGMA user_version` stamps the schema from day one — §14's "detect a non-hot-swappable
update" requires a version to compare.

### Debuggable from a different chair

The system's primary debugger is **another Claude Code session** — the one building
Dodona, in a separate place, told "go look at what happened here." That session is not
telepathic, so observability is a contract, not a habit:

- **A complete causal chain.** Every row names its cause: action → event row →
  decision row → routing/input row → raw transcript ref, linked by correlation ids
  (ticket, lane, decision, request) carried on every row. "Why did WATER do that?" is a
  mechanical walk up the chain, never an investigation. **If a state change can happen
  without an event row naming why, that is a bug by definition.**
- **Nothing consequential is memory-only.** A decision or action that never became a row
  did not happen. The store is already the state and the queue; it is also the flight
  recorder.
- **Readable from outside, daemon dead or alive.** WAL gives concurrent read-only
  connections to any process for free — the daemon never holds exclusive locks, logs are
  append-only JSONL flushed promptly, and every path is deterministic from the project
  root. Reading requires nothing to be running.
- **Documented for an AI reader.** `DEBUGGING.md` in the Dodona repo, written for a
  model: where the store lives, what each table means, the id conventions, worked example
  queries. Pointing a debugging session at that one file makes it self-serving.
- **Two CLI verbs for convenience:** `dodona trace <ticket>` (the full causal chain,
  chronological) and `dodona explain <decision-id>` (the decision row, its inputs, its
  announcement, whether it was undone). Convenience only — the documented schema is the
  real contract, and a debugging agent can always query the store directly.
- **Retention serves debugging, under a hard disk budget.** The costs are lopsided: the
  structured tables are text rows — megabytes over months, the causal chain is cheap to
  keep — while the gigabytes live in exactly one place, the raw stream-json transcripts.
  So: a configurable global budget for `logs/` (default a couple of GB), enforced by the
  daemon's sweep with a stated eviction order — happy-path raw logs of landed tickets
  first, oldest first; structured rows of long-landed tickets next; **failed, abandoned,
  undone, or explicitly pinned material last**. Eviction is itself an event row, never
  silent — a debugging session that finds a dangling `raw_ref` can see when and why the
  raw went away. Per-lane raw tees are also size-capped with rotation, so one looping
  agent cannot eat the budget alone.

**None of this costs speed.** A local WAL transaction is tens of microseconds —
thousands of times faster than the cheapest model call. Durability is not on the hot
path; models are. The things that would actually make the system slow are the ones
already designed out: a persistent coordinator everything queues behind, a model in the
routing path, agents blocking on each other.

---

## 13. Updating the system while it is running

It will be under continuous build and debug while in use. No session or in-flight state
may be lost across an update.

This is trivial rather than clever, and for one reason: **a pane was never a connection
to an agent.** It is a view over lane state in the store (§12, pane_events). Rebuilding
the UI is rebuilding a view. The same fact is why retargeting works, why agents are
disposable, and why compression can sit in the middle.

**Split the process in two:**

| Process | Owns | Update cost |
|---|---|---|
| **Daemon** | agent processes, registry, merge queue, worktrees | rare; see below |
| **UI** | nothing — a dumb view that reconnects | close, rebuild, reopen |

Most changes are UI changes: everything keeps running and the agents never notice.

For the daemon:

- **Agents are detached processes, not children.** The daemon can die without taking six
  agents with it. **The shim's named pipe is the daemon's sole reattach handle** —
  deterministic name per instance + lane. Never pids: Windows reuses them aggressively.
  The shim itself disambiguates its child's liveness by pid + process start time.
- **Session ids are stored the moment a lane starts.** Claude Code persists sessions, so
  `claude --resume <session-id>` rebuilds a dead agent's full context (measured, spike 1:
  kill mid-turn → resume → same id, full context, no fork). Worst case — a machine
  reboot — every lane resumes from the registry. A session id has exactly one owner at a
  time: two processes resuming one id interleave into a single transcript.
- **A hard kill loses the in-flight message** (measured, spike 1: a prompt sent 1.5s
  before the kill was absent from the session file after resume — the CLI persists
  completed turns, not in-flight ones). This is why delivery is store-first with cursor
  acks (§5): an instruction is a row before it is injected, and a resumed lane gets its
  unacked rows redelivered. A claude process is never the only copy of anything.

### Hot-swapping the daemon without skipping a beat

The target: an agent lands an improvement to the orchestrator itself, you say "publish",
and every running instance picks it up with nothing interrupted.

Three pieces make this work:

**An agent mid-turn does not need the daemon.** It is talking to Anthropic. A
one-second daemon swap is invisible to it — there is no beat to skip.

**A shim owns each agent's stdio.** Each `claude` process is owned by a minimal
supervisor exposing a **named pipe**; the daemon connects to pipes rather than owning
stdio directly. The shim is ~140 lines and essentially never changes. This is the piece
that makes hot-swap actually work on Windows: a new daemon build can connect to
processes it never spawned. (Measured, spike 2: shim and claude survive their launcher's
mid-turn death; a new client received seqs 18–38 produced while no daemon was attached;
zero loss, zero duplicates, context intact.)

**Queue in the store, not in memory.** Messages in both directions are rows — the shims'
inbox tables keep accepting while no daemon runs (§12). The new daemon drains them into
pane_events. Nothing lost, ordering preserved.

Publish flow — **successor handoff, no separate supervisor component**:

```
agent lands the update
  → build produces a new daemon binary, in a fresh versioned directory
    (Windows locks the image file of a running exe — never overwrite in place)
  → old daemon spawns the new binary; new one opens the store, signals ready
    on a control pipe
  → old daemon finishes its current transaction and exits
  → new daemon reconciles from SQLite + git, adopts the shim pipes, drains the inboxes
```

Sub-second, and you keep typing throughout. Crash recovery is **start-on-demand**: any
client that fails to connect to the daemon pipe launches it — which also honors "the
registry is a store, never a service": the store is always there; the daemon is summoned.
Old binary directories are garbage-collected once no instance runs them.

---

## 14. Multiple instances

Several instances run at once — one per project (personal, work, and so on). They must
not interfere in any way.

**Everything is scoped to the project root — the *canonical* project root.** One path has
many spellings on Windows (case, 8.3 names, junctions, subst drives); the instance id is
derived from the filesystem's final resolved path, and the daemon additionally holds a
**named mutex keyed to it**, so a second daemon on the same repo refuses to start no
matter how it derived its id. Two registries over one git repo is two merge tokens over
one main — the exact race this design exists to prevent, reintroduced by a string
comparison. The mutex is five lines; both guards are kept.

Each instance gets its own:

- store file
- named-pipe namespace
- worktree directory
- agent processes and session ids
- port, if anything listens on one

There is no shared mutable state between instances. Nothing global, no common lock file,
no shared registry — the only thing they have in common is the binary on disk, and the
running process holds its own copy.

**Updates hit every instance immediately.** There is no reason to hold one back from an
improvement, and no version pinning. A published build swaps into all running instances
at once, each independently, each invisible — the swap costs under a second and no agent
notices (§13).

This is only safe because the swap is genuinely seamless. **The moment a swap can lose a
message, drop a lane or interrupt a turn, this rule is wrong** — the correctness of
"update everything, always" rests entirely on the queue being in the store and the agents
being detached behind their shims.

**When a swap cannot be seamless, arm it.** *(Revised 2026-08-18 — never-stuck. The
original design asked a three-way question here; the operator lost a morning to updates
parked behind it, and directed that nothing hang, halt, stick, or go stale.)* A blocked
swap now answers itself with the middle option: the daemon records the proposal as
**armed** and swaps the instant the blocking condition clears — defer to a condition,
not to a timer, with no input from anyone. The announcement carries the two overrides:

```
[dodona] update <build> armed — lands the instant this clears: WATER is mid-merge
        (dodona swap-answer now to force, hold to park)
```

What still blocks, and what stopped blocking:

- **A lane mid-merge holding the token** — blocks, arms, lands when the merge does.
- **A store schema migration** — no longer blocks at all. It used to be the exception to
  *act, announce, allow undo* (§11) because a half-applied migration is not undoable with
  a keystroke; the fix was to make it undoable rather than to keep asking. The daemon
  backs up the store (SQLite online backup, announced with the restore path) before the
  successor migrates it. Only a schema *downgrade* still refuses outright — a build that
  cannot read the store may not open it.
- **A shim protocol change** — blocks only when a live shim speaks a *newer* protocol
  than the candidate build. Shims can never be swapped (they own their child's stdio), so
  every daemon commits to speaking all protocols ≤ its own; old shims therefore never
  hold an update hostage.

---

## 15. Windows realities

- **Out of OneDrive, out of Documents.** OneDrive touching a live WAL file or `.git`
  internals is a known corruption vector, and Defender real-time scanning taxes exactly
  this design's hot operations (worktree adds, WAL churn, builds, every node spawn).
  Project roots and worktrees live at short paths — `C:\src`, `C:\w\mw\<ticket>` — which
  also dissolves MAX_PATH (plus `core.longpaths=true` and the registry key). Defender
  exclusions for the source root, worktree root, and store directory.
- **Worktree removal is a daemon-owned, retryable operation.** MSBuild nodes,
  VBCSCompiler (lingers ~10 min), testhost, and the running game all hold locks on
  bin/obj — and §10 *tells you* to have the game running. Kill the lane's tracked
  children, `dotnet build-server shutdown`, retry with backoff; on persistent failure
  surface the lock holder as a lane announcement. Never fire-and-forget, never silent.
- **Build semaphore.** 2–3 concurrent verifies, or six MSBuild invocations starve the
  machine that is also running the game and six agents.

---

## 16. Build order

Three pieces are **unretrofittable** and exist from the first commit, however primitive:
the stdio-owning shim (retrofitting stdio ownership later means killing every live
session, repeatedly, during exactly the self-hosting phase this is for), the
queue-in-store, and the schema version + request-id idempotency.

Week-one spikes, each a day or less, before architecture is poured around the
assumptions: resume-after-kill/reboot (and long-lived multi-message stream-json);
the real shim surviving daemon death with zero message loss; mid-turn
`additionalContext` injection behavior; six concurrent sessions for an hour to calibrate
the quota governor and warm-turn latency.

Milestones: **M0** walking skeleton — shim, minimal daemon, one lane, console client,
the **fake agent** (§17 — it is also how the shim is tested without burning tokens), and
`DEBUGGING.md` written alongside the schema it describes (the causal-chain ids are
schema, so they are unretrofittable too); acceptance test: *kill the daemon
mid-agent-turn and the session must not notice*.
**M1** claims + hook gate + fenced merge token + `dodona.json` verify; two lanes on
MassWorks, on-approval only. **M2** dispatcher, tier-0, optimistic delivery + warm tier-1,
code-derived presence, selective compression. **M3** WPF grid over pane_events, decision
feed, badges, and the `dodona ui dump` / `dodona ui screenshot` verbs (§17) landing with
the UI they observe. **M4** hot-swap end-to-end. **M5** adjudicator, tier-2,
intent-similarity check, `merge: auto`, quota governor. **M6** voice.

**Do not dogfood Dodona-on-Dodona until M4's swap test passes.** Until then it is built
in plain Claude Code sessions — self-hosting before hot-swap works means every daemon
iteration kills live sessions.

---

## 17. Built and tested by an agent

The developer and the tester are Claude Code sessions; the human mostly watches. So
"runnable and testable" means **drivable and observable from a terminal, with exit
codes** — no test may require a human hand, and no behavior may be verifiable only by a
human eye.

**The test seam is the shim pipe.** Anything that speaks the pipe protocol *is* an agent
as far as the daemon knows. A **fake agent** — a scripted stand-in the shim hosts instead
of a real `claude` process — is deterministic, instant, and free, and it is how all
orchestration logic is tested: claims, extensions, token FIFO, lease expiry, lifecycle
decisions, merges against real scratch-repo git fixtures. Real-model tests exist, but
they are rare, explicit, and drawn against the quota governor like any lane. This is not
just economy: fake agents make race tests *reproducible* (two claims in the same
millisecond, a crash between verify and land) in a way live models never are.

**Headless is the primary mode.** The daemon is driven through the same pipe/CLI the
system already has; assertions are store rows. The causal chain (§12) doubles as the
assertion surface — a test asserts on events and decisions, not on internals, which means
tests survive refactors and every test failure is already a trace.

**The UI can testify.** Two debug verbs on the UI process:

- `dodona ui dump` — panes, badges, presence lines, decision feed as JSON. Most "does the
  UI show X" questions are text questions; answer them as text.
- `dodona ui screenshot [--pane WATER] --out <png>` — the UI renders its own visual tree
  (`RenderTargetBitmap`) to PNG, which the building agent then reads as an image. Self-
  rendering beats external screen capture on every axis: no window-finding, no occlusion,
  no DPI drift. Screenshots are for layout and visual judgment; dumps are for everything
  else.

A UI smoke test is one command: launch against a seeded store, dump, screenshot, close.
Most interactions need no UI driving at all — the view is dumb, so clicking a button and
writing its pipe message are the same thing, and tests inject the message. The few
genuinely visual behaviors (pane focus, the undo keystroke, overlay-maximize) get UI
Automation tests (WPF exposes UIA natively; FlaUI) — kept few on purpose.

**During UI development, the capture loop IS the inner loop** — edit → build → launch
seeded → screenshot → look — so it is built to be tight, not merely possible: one
command, a couple of seconds, and **deterministic**. Fixed window size, and a set of
seeded poses (`dodona ui pose <name>`) that put the UI into each visual state on
demand — all six panes populated, badges at several counts, a blocked lane, the decision
feed full, the overlay open, an empty slot, the tray occupied. "Screenshot whatever state
the app happens to be in" cannot verify a layout change; posing the state can, and the
poses double as the fixture set for visual regression later.

**A bug report is a store file.** Event-sourced state means reproduction is: copy the
store + logs, point a daemon (or just a debugging session, §12) at it, replay. Test
fixtures are the same thing in reverse — seeded stores checked into the repo.

**Test instances collide with nothing.** Tests run under a temp project root, and §14's
scoping makes isolation structural rather than disciplined. `dodona dev up` starts a
daemon with fake lanes and a seeded store in one command; `dodona dev down` tears it
down, always, exit code honest.
