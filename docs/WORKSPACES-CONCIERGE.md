# Dodona Workspaces & the Concierge

Status: **all five slices BUILT (2026-08-18).** Slice 1's mechanism was the operator's to
specify and is recorded in §5.1. Sections 1.1, 2.1 and 6.1 record the decisions implementation forced that the
planning session did not reach, with their reasons.

Originally the outcome of the 2026-08-18 planning session with the operator, whose standing
note was that everything here is high priority and is the next body of work rather than a
someday list. Decisions were taken deliberately; §8 records the ideas already rejected *with
their reasons*, in the LANE-LIFECYCLE §2 style, so they are never re-proposed. This document
is authored in-repo (no `..\MassWorks\` master exists for it).

The problem it answers: the operator runs work and personal projects at the same time and
wants **one app** over both — one window, one input box, one prompting system — without
giving up any guarantee the per-root instance model provides today. And a "project" is not
one folder: it may be this folder and that folder and that folder.

---

## 1. The model: workspaces are named, not located

A **Dodona workspace** is a named, durable session group — "work", "personal",
"dodona-dev". The workspace, not any folder, anchors an instance. It owns everything a
root-anchored instance owns today: the daemon, the store, the dispatcher brain, the grid,
the lanes, the tickets, the merge tokens of its member repos.

- **Identity flips from path to name — but the id is generated, not the display name.**
  Today `Instance.Id()` hashes the canonical root path. It becomes a stable generated
  slug; pipes, the OS mutex, and the store path key off the slug. Renaming "personal" to
  "home" re-derives nothing and orphans nothing. Name is display; id is identity.
- **Members are attachments, not structure.** A member is a repo or a bare folder (lanes
  need no git — already true today). Members are attached up front or ad hoc the moment
  work arrives for somewhere new. A folder that happens to contain several repos is a
  bulk-add convenience during attach, not a distinct concept.
- **State moves out of user folders entirely.** A workspace has no natural folder, so its
  store lives in Dodona's own territory: `%LOCALAPPDATA%\Dodona\workspaces\<id>\`. This
  extends CLAUDE.md §5 (Dodona state is never repo content) to: not even repo-adjacent.
  Exception: **git worktrees stay near their repo** — they are volume- and path-sensitive,
  and moving them to workspace-land buys nothing.
- **The old "workspace" term is subsumed, not forked.** The codebase's current meaning
  (a root with repos ≤2 levels under it, `Repos.cs`) becomes one way to bulk-attach
  members. One concept keeps the word.
- **Degenerate case = today.** A single workspace with a single member behaves exactly as
  the current root-anchored instance, which is what keeps the existing suites meaningful
  through the migration. Every existing instance migrates trivially: a workspace named
  after its root, that root as sole member.

Naming decision: **workspace**, not "session" — a session implies something that ends
when you stand up; this thing has a registry entry, a store, and a history. The name
should say durable.

## 1.1 Decided while building milestone 2 — questions §1 left open

Implementation forced answers the design session did not reach. They are recorded here
rather than in commit messages alone, because the next person to read §1 will hit the same
questions in the same order (CLAUDE.md §0).

- **The registry is a SQLite store at `%LOCALAPPDATA%\Dodona\concierge\registry.db`** —
  the concierge's territory from the start, even though milestone 2 ships no concierge.
  In M2 every writer is a CLI process, serialized by SQLite's own transactions with a
  partial unique index as the real arbiter. M3 makes the concierge the sole *writer*;
  **reads stay direct forever**, for the same reason the UI reads stores directly rather
  than asking the daemon — a dead manager must never blind you. Rejected: putting the
  registry under a workspace (it belongs to none) and holding it in a JSON file (the
  exclusivity invariant wants an index, not a linear scan and a hope).

- **`DODONA_HOME` redirects the whole tree** (registry, workspace stores, shim-info,
  neutral cwd). Not a convenience: the suites must be able to create workspaces and to
  test the *refusal* path without touching the operator's real registry or refusing one of
  their real repos. Same reasoning as `DODONA_BIN_ROOT`, and the same reasoning as §17's
  "tests collide with nothing".

- **Every workspace has a PRIMARY MEMBER** — the first attached — and it stands in
  everywhere the old code said "the project root": where a lane spawns, which `dodona.json`
  the workspace falls back to, what `repo-init`/`repo-status` act on, what auto-publish
  watches. For a one-member workspace it is literally the old root, which is most of why
  the degenerate case is indistinguishable. **Open, and deliberately not answered yet:**
  which member a lane should work in when a workspace has several. Today it is the primary;
  a per-lane working directory is the obvious extension and nobody has needed it.

- **Repo names gain a member prefix only when they must.** One member → the names are
  byte-for-byte what they always were (`.`, `engine`, `tools`), which is the property that
  keeps claims, gate prefixes, tickets and every pre-existing suite meaningful. Several
  members → `<member-leaf>/<member-relative>`, or just `<member-leaf>` when the member is
  itself a repo. Two members with the same leaf get `leaf~2`, because a display collision
  here would become a claim-routing collision.

- **Worktrees live at `<member>\.dodona\wt\t<N>`** — §1's stated exception, made concrete.
  Beside the member holding the repo, not beside the workspace and not beside the primary.
  For a one-member workspace this is the exact path it has always been.

- **Shim-info files moved into the workspace directory.** `shim-lane<N>.json` was per-root;
  lanes are workspace-wide and a workspace has N members, so there is no single root left
  to put them under. CLAUDE.md §4 (resolve pids from these, never kill by name) now points
  at `%LOCALAPPDATA%\Dodona\workspaces\<id>\`.

- **Repo-exclusivity is enforced in THREE layers, and the third answers a case §3 did not
  cover.** §3 says bare folders are exempt — correctly, no merge token exists to split. But
  a bare folder legitimately attached to two workspaces can be `git init`-ed afterwards, and
  at that instant two workspaces hold one repo with every registry row still valid. So
  besides (1) the partial unique index and (2) the loud attach-time refusal with its move
  command, there is (3) a check at **ticket-create**, which is where a merge token first
  comes into existence. Layers 1 and 2 cover intent; layer 3 covers drift. This is the same
  two-layer shape as the claim gate and the merge-time diff backstop (§6), for the same
  reason: a check at the point of decision and a check at the point of use catch different
  things.

- **An unowned `--root` gets a workspace made for it, on the spot.** That IS the migration
  mechanism: the first command to address a pre-workspace project turns it into "a workspace
  named after its root, with that root as sole member" and moves its store (with both WAL
  twins, and the shim-info files) into workspace territory. It is announced on stderr, and
  `dodona workspace-forget` reverses it. **`--workspace <name>` never creates** — naming a
  workspace that does not exist is a typo, and inventing one would hide the typo. Migration
  refuses outright while a pre-workspace daemon still holds that store: a live WAL file is
  not a file you move, and `Instance.LegacyId` survives for exactly that check.

- **`dodona where [--json]` is new, and is a debugging affordance rather than a
  convenience.** DEBUGGING.md's first section was a table of `<root>\.dodona\...` paths;
  once a store's location derives from a generated id, "where is my store" stops being
  answerable by looking. The suites use it instead of reconstructing paths.

- **`workspace-forget` deletes registry rows and keeps the store directory.** Undoing a
  workspace made by accident must never be able to mean deleting six lanes of transcript
  (§12: nothing in this system deletes history).

- **A renamed workspace keeps its id, and therefore an id whose slug half is stale.**
  Accepted. `personal-3f9a` renamed to "home" still reads `personal-3f9a` in its pipe name.
  The alternative — an id that means the name — would make a rename move a store directory,
  which is precisely what §1 exists to prevent. Name is display; id is identity.

- **The build that flips identity cannot hot-swap into a running pre-workspace daemon, and
  that is not fixable.** The handoff pipe name is `dodona-<instance>-handoff`, and
  `<instance>` is exactly what changed: the old daemon opens the pipe under its path hash
  and the successor would listen under a workspace slug. They cannot meet. What happens is
  safe and is the M4 contract working as designed — the successor exits before touching
  anything, the old daemon records `swap_failed` and **keeps running**, nothing is lost —
  but the swap does not land. **This build needs one manual daemon restart**, once, per
  pre-workspace instance. Migration refuses while that daemon is alive, for the WAL reason
  above, so the order is: stop the daemon, then any command migrates it.
  The migration leaves a `MOVED-TO-WORKSPACE.txt` breadcrumb where the store used to be —
  it is the one irreversible step in this change, and the first place anyone will look for
  a store that is no longer there is the folder it left.

Found and fixed on the way, unrelated to workspaces but the same complaint: **`--test-window`
now survives a UI hot swap.** The successor was spawned without it, so a swap turned an
invisible test window into a visible one that activates — reintroducing the focus-stealing
the flag exists to prevent.

## 2. The concierge

One per machine. It is **daemon-natured, not view-natured**: it holds pending operator
questions and routing state that must survive a window close, and m3 doctrine says the
UI owns nothing — so it cannot live in the UI process. It is a small daemon with its own
store, its own ctl pipe, running management models in neutral cwd with
`--setting-sources user`, exactly like the existing brain (the commit-19dad3d lesson:
a manager reading a worker's orders is how a classifier ends up running /ship).

The concierge owns exactly three things:

1. **The registry** — workspace names, ids, aliases, members, repo→workspace ownership.
   This is the "memory" of the system: every operator clarification it absorbs becomes an
   alias, so asking decays over time (§4, rung 4).
2. **The group-scope routing ladder** — full depth, not a shallow first level (§8 records
   why shallow was rejected): cheap classifier → expensive fallback → ask the operator in
   the merged feed. Its question is narrow: *which workspace, and how confident*. Lane
   choice, naming, tickets, claims stay with the per-workspace brain — two full ladders,
   one question each, never one merged brain (§8).
3. **A review-behind net for group-misses.** Optimistic focused-lane delivery skips the
   concierge entirely; the per-workspace brain can catch wrong-lane-within-workspace but
   structurally cannot catch wrong-workspace (it does not know other workspaces exist,
   §14). So the concierge's cheap tier reviews optimistic deliveries behind —
   fire-and-forget, silent on agreement, the `BrainReview` pattern one level up.

Its config (models/effort for its two tiers, the discovery fence of §4) lives with its
own store — the same pattern as `dodona.json`, one level up. No workspace's config can
serve, because the concierge belongs to no workspace.

**The hard cap on its authority:** registry + routing + resolution, nothing else. It
holds no lanes, no claims, no merge tokens, and no workspace daemon ever reads its store.
The moment the concierge coordinates work rather than routing sentences, it is the
persistent-coordinator serialization point §12 designed out.

## 2.1 Decided while building milestone 3 — questions §2 and §4 left open

- **§2.2's tiers and §4's rungs are ONE ladder, implemented once.** §2.2 describes it as
  cheap classifier → expensive fallback → ask; §4 describes it as exact/alias → fuzzy →
  bounded discovery → ask-and-teach. Both answer the same narrow question — *which
  workspace, and how confident* — and both escalate in the same order, so they are one
  implementation. Two would eventually disagree, and a router that disagrees with itself is
  worse than either half. The rungs as built:

  | rung | what | cost |
  |---|---|---|
  | 0 | an explicit path in the prompt | code — and it never searches (§4 is emphatic) |
  | 1 | exact name or alias | code |
  | 1b | only one workspace exists | code — there is no group question to answer |
  | 2 | fuzzy | cheap tier |
  | 3 | bounded discovery inside the fence | expensive tier, one capability |
  | 4 | ask the operator, and teach an alias | a row, a feed line, no model |

  **Rung 1b is not in the doc and matters most in practice**: until the operator makes a
  second workspace there is nothing to disambiguate, so a single-workspace machine never
  reaches a model in the concierge at all. That is quota discipline (CLAUDE.md §0.1) rather
  than an optimisation — the common case must be free.

- **The concierge owns the registry as the thing that RESOLVES and LEARNS from it; it is not
  the only process allowed to write the file.** §2.1 says it owns the registry, and it does
  own every judgement about it — aliases, creation-on-resolve, which workspace a sentence
  means. But `workspace-attach` and friends still write directly, with the partial unique
  index as the arbiter. Putting those writes behind the concierge's pipe would mean a
  registry you cannot edit because a daemon will not start, and it would buy nothing: the
  exclusivity invariant is enforced by the index regardless of who holds the pen. Registry
  *reads* stay direct everywhere for the same reason a UI reads stores directly — a dead
  manager must never blind you.

- ~~**The concierge does not hot-swap. It is stopped and re-summoned.**~~ **REVERSED, and the
  reversal is an incident** (issue #9, 2026-08-21). This bullet used to argue that a handoff
  would be "ceremony protecting nothing": the concierge holds no work agents, no lanes, no
  claims and no merge tokens, everything of its that must survive is rows, so *a publish stops
  it and the next command revives it*.

  **Neither half of that was ever built.** Publish sends `swap` and has never sent `stop`; the
  concierge's dispatch understood ten commands, `swap` was not one of them, and it had no
  `default:` branch — so the command was discarded in silence and the process simply aged.
  Measured on the operator's machine at `f346b76`, immediately after a publish that printed
  `— swapping concierge`: the workspace daemon on build `20260821-105924`, the concierge on
  `20260819-212126`. Two days, many publishes, `dodona ps` reporting it healthy throughout. It
  was healthy. It was just old. That is precisely the quietly-stale that CLAUDE.md §0.1 calls a
  bug rather than a safety feature.

  **It hot-swaps now, and always takes the swap immediately** — no `swap-answer`, no `swaps`, no
  arming. Two things settle it against re-deciding for stop-and-revive: `stop` shuts both
  management tiers down, so stop-and-revive would kill two model agents and lose their sessions
  on *every publish*, where a handoff lets the successor adopt them; and a revived concierge is
  spawned by whichever CLI happens to summon it next, which may be any build on the machine,
  where a handoff starts the exact binary publish just verified. The arm/hold machinery stays
  unbuilt for the reason the old bullet was right about: `Daemon.Blockers` has two entries and
  neither can exist here, so it would have no reachable state. The only refusals are a candidate
  that will not answer `version --json` and a concierge-store schema downgrade
  (`Ver.ConciergeSchema`).

- **It reuses the shim wire, not the workspace store.** `LaneRuntime` — stream-json parsing,
  presence derivation, exactly-once seq dedup, turn-final detection — now takes an
  `ILaneSink` interface, which the concierge's own store implements. The alternative was
  giving the concierge a full workspace store and letting it keep rows in tables it would
  never use, which would also have meant bumping `Ver.Schema` for every workspace — and a
  schema bump is the one thing that makes an ordinary swap non-seamless (§14). Six method
  signatures were cheaper. **Sharing machinery is not sharing authority**: the concierge
  suite asserts its store holds no `lanes`, `tickets`, `claims`, `merge_token` or
  `token_queue` table, so the §2 cap is enforced by a test rather than by good intentions.

- **Its two model sessions are called TIERS, not lanes**, with fixed ids 1 and 2 so their
  pipe names are stable across restarts, and it reconciles them exactly as the daemon
  reconciles lanes (the rows are the claim, the pipe is the proof).

- **The review-behind never retracts, and says so.** §5's error asymmetry applies at group
  scope too: you cannot unsay a sentence to an agent. So a confidently-disagreeing review
  reports where the sentence went, where it thinks it belonged, and the command to resend —
  and explicitly says it was *already delivered*, rather than implying a fix. Silent on
  agreement, and silent on low confidence (operator's rule #3).

- **`cxpick:N` exists in the fake agent for a reason worth keeping.** The obvious directive,
  `cxws:<NAME>`, spells a workspace *name* into the operator's sentence — and rung 1 matches
  names in the sentence in code, so a rung-2 test written that way passes at rung 1 having
  never reached the tier. It proved the opposite of what it claimed until the check failed
  with `rung=registry`. `cxpick:N` picks the Nth workspace out of the list the concierge
  handed the tier, which keeps every name out of the text and is closer to what a real model
  does anyway.

- **The fence excludes drive roots.** `Fence.Roots` takes the *parent* of every member, and
  a member sitting directly on `C:\` would otherwise contribute `C:\` — turning the fence
  into the filesystem. One carelessly-placed member must not be able to widen it silently,
  which is the §8 rejection enforced in code rather than trusted to a prompt.

## 3. Invariants that move, and one that must be rebuilt

Path-derived identity was not aesthetic: two spellings of one repo hash to one id, one
mutex, one merge token — `Instance.cs`'s own doc comment calls two daemons over one main
"exactly the race this system exists to prevent." Named workspaces delete that structural
enforcement, so the invariant moves up a level and becomes concierge law:

> **A repo belongs to at most one workspace at a time.** Membership dedup canonicalizes
> paths (`GetFinalPathNameByHandle` moves here; it does not die). Attaching a repo owned
> by another workspace is refused loudly, with an explicit "move it" affordance —
> reassignment is legitimate, silent double-ownership never is.

This is enforcement-in-code territory (CLAUDE.md §0): it gets a check in the concierge
and a test in the workspace suite, not a doc sentence. **Non-git member folders are
exempt** — no merge token exists to split, and a shared notes folder in two workspaces
harms nothing.

Unchanged and load-bearing: workspace daemons remain mutually ignorant (§14's no-shared-
mutable-state survives — the concierge's registry is its own, sole-writer, and carries no
coordination state); the claim gate and merge backstop are untouched; lanes remain
workspace-wide; tickets remain per-repo; cross-repo tickets remain refused.

## 4. Prompt-first: the input box is the only front door

Doctrine: **clicking is for looking; typing is for starting.** "Do X on blazing-trumpets"
must work identically whether that workspace is focused, open, asleep, or has never had a
daemon woken. The per-workspace half already exists (start-on-demand revives daemons;
lane auto-create turns a sentence into a lane). The missing layer is name→workspace
resolution when nothing is running — the registry's real job. Four rungs, cheapest first:

1. **Exact/alias match — code, no model.** Registry hit → wake the daemon if asleep,
   hand the sentence to its brain. The steady-state path.
2. **Fuzzy match — cheap model.** Voice-typed "blazing some of the trumpets" against
   registry + recents. Confident → act, announce in the feed with undo (§11 applied to
   workspace wake).
3. **Ask, with a guess — and teach.** Double uncertainty lands in the merged feed
   carrying its best candidates ("Did you mean C:\repos\blazing-trumpets, or is this
   new?"). The answer becomes a registry alias; asking decays toward rung 1 with use.
   An explicit path in the prompt is handled *above* all of this and attaches that folder
   outright — explicit information never triggers a search.
4. **Bounded discovery — expensive tier, with a fence. BELOW the ask now, and never
   automatic** (D-L3, operator 2026-08-19; it used to be rung 3). It is an affordance:
   answering an open question with `look` sends the resolver to search, and only within the
   fence — parent directories of every member ever registered, plus configured search roots.
   This deliberately carves one narrow exception to "management brains never run tools": the
   resolver gets exactly one capability, enumerating candidates inside the fence. A classifier
   with a flashlight, not a crawler. **The fence never widens itself**, and a look that finds
   nothing leaves the question open rather than answering it.

   **Why it moved.** It ran automatically on the way to a question we were going to ask
   anyway — a directory walk plus an expensive-tier call, spent to avoid asking, when asking
   is the cheapest correct rung there is. And searching unbidden is the wrong default:
   occasionally going to look is exactly right, which is why `Fence.cs` stays and was not
   deleted, but it must be something the operator asks for rather than something that happens
   to them.

Creation follows the same shape: confident-new creates the workspace and announces,
reversibly (a workspace is a registry row and a store directory — no daemon until work
arrives); unconfident-new asks.

Consequences: **the shell boots to zero** (a window with no workspace awake — just feed
and input — is a real state, with a pose and a ui-use check), and rungs 1–2 handle the
steady state so the expensive tier stays rare by construction (quota discipline,
CLAUDE.md §0.1) — now doubly so, since it is only reached when the operator asks for it.
All four rungs sit behind the same fake-agent seam as the router (§17): the fence makes
discovery deterministic under a fixture directory tree.

**Note the two ladders, and that they are different questions.** This one answers *which
workspace*. Inside a workspace the router answers *which lane* (§5.1's four verdicts) and
then, for a new lane, *which project* — one project answers itself, a sentence naming a
project is decided in code, otherwise the project holding a live lane, and on genuine
uncertainty the sentence is held (`docs/LOCATIONS-PLAN.md` Phase 3). Both hold rather than
guess for the same reason: the wrong destination cannot be unsaid.

## 5. Lane granularity: new lane vs continuation

**Finding (2026-08-18, from the code): this decision does not exist today.** The routing
path (`Daemon.cs RouteInput`) can only (a) honor a `LANE:` prefix, (b) deliver to the
focused lane, or (c) auto-create a lane *when zero live lanes exist*. The classifier's
verdict vocabulary is `generic | specific | unclear` where `specific` means *an existing
lane's title*, and brain-hi's question is "route to the right lane, **or none to leave it
with the focused lane**." No rung of the ladder can answer "this deserves a fresh lane."
While any lane is alive, every input is a continuation of something. This is the known
carried gap (`ORCHESTRATOR-REVIEW.md` "the dispatcher's own session"), now with a sharper
edge on it.

The operator's prior, now design: **a distinct task defaults to a new lane** — new agent,
new branch, new PR, clean context, and lanes are cheap. An addendum goes to an existing
lane only when it clearly continues that lane's thread.

The error asymmetry justifies the bias, and it is worth stating because it inverts the
current optimistic default for task-shaped input:

- **A wrong continuation is the expensive error and is not undoable.** `frt.Say(text)`
  delivers immediately; a later retarget re-sends the text to the right lane but the
  wrong agent already received it, may already be acting on it, and its warm context is
  polluted. You cannot unsay a sentence to an agent.
- **A wrong new lane is the cheap error and is fully undoable.** `dodona lane-stop <id>`,
  nothing polluted, nothing consumed but a process spawn.

### 5.1 BUILT 2026-08-18 — the mechanism, as the operator specified it

The operator's rule, in their words: **favour new tasks "unless they are direct messageing to
existing tasks, or very obvious small post work done tweaks that should not be treated as
seperate work"**. That gives the two exceptions their names, and both are `addendum` with a
recorded reason (`direct`, `tweak`) — same destination, distinguished because the operator named
them separately and the distinction is worth having in the data.

**The doctrine that had to change.** §4's "deliver instantly, correct behind" cannot survive
contact with this, because correcting is exactly what is impossible: once an agent has the
words, they cannot be unsaid. So **nothing is delivered until the cheap classifier answers**
(the operator accepted ~1s as the honest price). Two paths stay instant and model-free:

| path | decided by | why |
|---|---|---|
| `LANE: text` | code | the operator said where |
| an unmistakable generic (`stop`, `no`, `try again`…) | code, whole-input match | "stop" must never be slow |

Everything else waits. On double uncertainty the sentence is **held, not delivered** — which
reverses §4's old ambiguity default of "leave it with the focused lane". That default was
written when delivery had already happened and the only question was whether to retarget; now
nothing has been said yet, and undoing a wait costs nothing while undoing a polluted context
costs the lane.

**Only two hard rules exist in code.** Everything else is evidence handed to the classifier as
FACTS (§2.2 — derive in code what is not really a judgement): which lane is focused, whether
each is working now or idle, what it last said, and whether the input refers back (*that, it,
instead, also, still*). Three tempting hard rules were considered and rejected, two of them on
the operator's own correction:

- ~~"never spawn while the focused lane is mid-turn"~~ — **rejected.** The operator: "some mid
  turn comments are definately meant for the lane." Mid-turn is therefore a signal *toward* the
  lane, and the prompt says so outright: interrupting a working agent is normal and usually an
  addendum. Making it a *block*, though, would reintroduce the unrecoverable error for genuinely
  new work said while watching something build.
- ~~"never spawn from short input"~~ — **rejected by the operator**: "Length of input generally
  doesnt have bearing on new vs existing. Because a short 'add this' on an existing lane might
  mean a new work on that workspace." No word count is given to the classifier at all. The
  discriminator offered instead is **subject** — does the sentence concern what this lane is
  about, or something else.
- ~~"never spawn if it would land in the tray"~~ — moot: the grid grows now (ORCHESTRATOR-DESIGN
  §8 as revised).

Two invariants: never more than one lane per input, and every spawn announces itself carrying
`undo: dodona lane-stop N`.

**Without a classifier warm, behaviour is unchanged** — the input goes to the focused lane. That
is deliberate: generics are already handled in code, but spawning a lane for "make it blue
instead" would be worse than the status quo, and a system that cannot tell continuation from new
work should not pretend it can. The four verdicts need the brain on, which is the default in
`dodona.json`; the suites run without it, which is why every pre-existing routing check stayed
green.

**A footgun found while testing, and fixed:** the tier-0 prefix regex accepted `word:text` with
no space, so a lane whose title collided with any `word:` prefix silently swallowed later input.
A test directive `routekind:` became a lane named ROUTEKIND and then hijacked every subsequent
`routekind:…` line. It now requires `LANE: text` with whitespace — which also stops a sentence
containing `http://` being read as a target for a lane called HTTP.

Design changes this implies:

- The classifier verdict grows a fourth kind: `generic | addendum | new-task | unclear`
  (with `addendum` naming its target lane). `generic` (stop / try again / status) still
  goes to focus, never second-guessed — that tier is untouched.
- For `new-task` verdicts the optimistic action becomes **spawn-and-deliver**, with the
  existing `BrainReview` running behind it for name/ticket/model — that machinery already
  exists and needs no change.
- The distinction is per-workspace judgement, so it lands in the **workspace brain's
  ladder, not the concierge's** — and it is severable: it can ship before any workspace
  work, on today's single-instance system.
- Weighting for the classifier prompt: when torn between addendum and new-task, choose
  new-task — the asymmetry above is the reason, and belongs in the prompt so the cheap
  model knows *why* the tie breaks that way.

## 6. The shell: one window over N workspaces

m3 doctrine is what makes this feasible: the UI owns nothing and a pane is a replay of
store rows, so a multi-workspace window is N read-only readers and N pipes with a
`(workspace, lane)` key — a bigger view, not a new authority.

Chosen shape (**B** — §8 records what lost): **the focused workspace gets the full 3×2
grid; every other awake workspace renders as a compact band** — one row of lane chips
with attention badges, the tray idiom at workspace scale. Click a band to swap which
workspace holds the grid. Simultaneous *awareness* without halving every pane.

Plus one genuinely merged element: **the dispatcher feed becomes a union across
workspaces** — a read-only merge with a workspace chip per row — and the input box
beneath it routes through the concierge. Group-scope clarifications (concierge rung 4)
render in the merged feed as the system's own voice; they belong to no workspace's
column by definition.

Per-store semantics do not blur: the six-slot cap, `focused_lane`, and the dispatcher
lane stay per-workspace concepts. Bands are a **view** choice — no lane is ever demoted,
evicted, or trayed by the shell (LANE-LIFECYCLE §2 stands). Writes stay pipe-addressed:
every click resolves to one daemon's pipe; concierge messages go to the concierge's.

Costs owned up front: new deterministic poses (bands, merged feed, boot-to-zero) and a
ui-use check per new affordance (CLAUDE.md §3); `dodona ui dump` grows a workspace
dimension.

## 6.1 Decided while building milestones 4 and 5

- **`DodonaUi.exe --shell` is how you open a window over everything**, and it is what makes
  boot-to-zero reachable at all. `--root` still opens one workspace and the picker still
  browses for a folder; without a third way in, a window always had a workspace and §4's
  "no workspace awake" state could never occur. The shell owns no store and no daemon — its
  only resource is a pipe (`dodona-shell-ui`), so `dodona ui dump --shell` addresses it.
  It cannot borrow one workspace's ui pipe, because it shows all of them.

  **Revised 2026-08-18 — the folder picker is gone, and folder UI must not come back.**
  The operator found the shell still carried the per-root era's chrome: the header name was
  a dropdown into the old folder picker (recents, Browse…, repo statuses, repo-init), and
  picking there spawned a second old-style window per project. That contradicted §1's whole
  premise — the app's only user-facing identity is the workspace NAME; locations are the
  router's business, attached by the concierge as work arrives (§5). So now: a bare launch
  of `DodonaUi.exe` lands in the shell (boot-to-zero if nothing is awake — typing is how you
  leave it), `PickerWindow` survives only as the **workspace switcher** (names + awake/asleep,
  opened from the header or Ctrl+P; picking wakes the workspace and hands it the grid through
  `FocusWorkspace`, the same path a band click takes), and `--root`/`--workspace` remain the
  direct doors tests and shortcuts use. Browse…, the recents file (`projects.json`), the
  shortest-unique-suffix window label, and the picker's repo-init button are deleted;
  `repo-init` remains a daemon command, reached by typing. **Do not reintroduce a folder
  list, a Browse dialog, or a path in the window chrome** — that is the §8 anchor-folder
  rejection wearing UI clothes.

- **`ui workspace <name|id>` is the band click without a mouse.** A band is a `Border`; UIA
  can find one but cannot invoke it, and `SendKeys` is banned because it needs focus (the
  operator's keyboard was stolen mid-work by exactly that). So the verb goes through
  `FocusWorkspace` — the identical code path a click takes — for the same reason `ui type`
  goes through `SubmitInput`.

- **Which door the input box opens depends on whether there is a question to answer.** One
  workspace on screen → straight to its daemon: there is nothing to disambiguate (the
  concierge's own rung 1b), so a hop through the concierge would buy nothing and would mean
  the input box stopped working whenever the concierge would not start. Several workspaces, or
  none awake → through the concierge's `route`, which resolves, wakes, creates-if-new, and
  then hands **the whole sentence** to that workspace's own dispatcher. The concierge stops at
  the workspace boundary: it never picks the lane, names it, or judges whether it deserves a
  ticket (§8's two-ladders decision, and this is where it would be easiest to break).

- **A rung-4 "ask" delivers nothing at all.** `route` holds the sentence rather than guessing,
  because a wrong workspace is not undoable — §5's error asymmetry applies at group scope too.
  The feed row carries the answer command; answering delivers.

- **Workspaces get their own colour palette, not the six lane hues.** A workspace is not a
  seventh lane, and reusing the lane palette would say it was — the same reasoning that gives
  Dodona's own feed rows the oak's trunk colour and a *round* chip. The focused workspace holds
  palette slot 0 so its colour is stable as bands come and go.

- **The feed's workspace chip is suppressed when only one workspace is on screen.** That axis
  carries no information there, and a chip answering a question nobody asked is exactly the
  noise a single-project operator should never meet — the same rule as the repo tag on a pane.

- **Acking a merged-feed row goes back to the store it came from.** Row ids are only unique
  within one store, so a single `ack` path would have cleared an unrelated row that happened to
  share a number. Concierge rows ack to the concierge's pipe; workspace rows to that daemon's.

- **The band strip scrolls and is capped.** Ten awake workspaces is a shape this design
  invites, and a strip that grew without bound would silently squeeze the panes it exists to
  complement.

- **`DODONA_HOME` scopes the concierge's and the shell's pipe names too**, and that turned out
  to be load-bearing rather than tidy. The concierge is machine-global by design — one mutex,
  one pipe — but `DODONA_HOME` creates a separate logical machine. Without the suffix, a
  concierge started under one home kept serving clients pointing at a *different* registry,
  because the mutex made the second refuse to start and the CLI talked to the first. Measured:
  a concierge leaked by the ui-use suite answered the concierge suite's questions using
  ui-use's workspaces, failing 21 checks that passed in isolation. Under the default home the
  ids are still plain `concierge` and `shell`, so nothing about an installation changes.

- **Workspace resolution had to become LAZY.** Doing it eagerly meant *any* command
  created-or-migrated a workspace for whatever the cwd happened to be, purely as a side effect
  of being run there — and worse, when migration was legitimately refused (a pre-workspace
  daemon still holding the store) commands that never needed a workspace failed with it. Found
  live: `publish` run in a source tree whose own daemon was running could not publish, because
  resolving a workspace it did not need refused first. Nothing resolves now until something
  asks, and `publish` says so and still exits 0 when only the swap could not happen — the build
  is real either way.

## 7. `publish --all` gets instance scoping

`--all` currently broadcasts a swap to every ctl pipe on the machine, which
`ORCHESTRATOR-REVIEW.md` already flags as untestable (a suite would hot-swap the
operator's live instances). A machine with a concierge and N workspace daemons makes
scoping overdue: publish targets named workspaces (or the concierge) explicitly, and the
suite can finally exist because a test can name only its own fixtures.

## 8. Rejected in this session — do not re-propose

- **Anchor-folder identity** (a thin folder whose path anchors the workspace). Rejected
  for named registry identity: it forces the operator to create and place folders whose
  only content is a manifest, ties identity back to disk layout — the thing this design
  exists to escape — and makes "rename" mean "move a folder".
- **"Session" as the name.** Too temporary. The thing has a registry entry, a store, and
  a history; the name should say durable. It is a *workspace*.
- **One merged manager over all workspaces.** Mechanically feasible (its context is
  titles), but: mixing domains manufactures title ambiguity, converting cheap confident
  routes into escalations; policy (models, effort, permissions) is per-workspace and a
  merged brain would carry per-workspace policy anyway; and it is a step toward the
  §12 persistent-coordinator serialization point. Two managers wearing one trench coat.
- **A shallow group router without its own escalation ladder.** Group-uncertainty cannot
  be delegated downward — a per-workspace brain does not know other workspaces exist, so
  there is nobody below the concierge to escalate to. The concierge owns cheap →
  expensive → ask in full.
- **A single merged grid across workspaces.** The slot cap, `focused_lane`, and the
  dispatcher lane are per-store concepts with no defined merged meaning, and §8's
  fixed-position doctrine fights it. Chips on twelve mixed panes read worse than bands.
- **Tabs as the only multi-workspace view.** Fails the stated requirement: the operator
  wants both lives *visible at once*. Tabs hide exactly what should stay ambient.
- **Concierge authority beyond registry + routing + resolution.** Any coordination state
  in the machine-global component is the serialization point §12 designed out.
- **A self-widening discovery fence** (rung 3 searching beyond its configured roots when
  it fails). It fails fast into asking instead. A fence that grows itself is how a
  classifier with a flashlight becomes a crawler with opinions.

## 9. Slicing — this is the high-priority list, all of it

Operator, 2026-08-18: every item below is high priority. This list is the enumeration —
nothing in this document is background material to the slices; each slice IS one of the
things decided in the planning session. Each ships alone; current two-window behavior
keeps working throughout.

1. **Lane granularity (§5)** — **done** (2026-08-18), mechanism specified by the operator and
   recorded in §5.1. Four verdicts, the wait instead of optimistic delivery, hold-and-ask on
   double uncertainty, and two hard code rules only.
2. **Workspace identity + registry** — **done** (commit "Workspaces are named, not
   located"). Decisions in §1.1.
3. **Concierge daemon** — **done** (commit "The concierge: one per machine"). Decisions in
   §2.1.
4. **Shell read side** — **done.** Bands, merged feed, boot-to-zero, the workspace dimension
   in `ui dump`, three new poses (`bands`, `merged-feed`, `boot-zero`), thirteen new
   ui-use checks. Decisions in §6.1.
5. **Prompt-first wiring + publish scoping** — **done.** Input box through the concierge when
   there is a group-scope question to answer, wake- and create-on-prompt via `dodona route`,
   explicit `publish` targeting, and `tests/publish-acceptance.ps1` — the suite `--all` never
   had, now possible because targets resolve through the registry rather than by scraping the
   OS pipe namespace.

Eleven model-free suites, 281 checks, green together and individually. The degenerate case —
one workspace, one member — stayed green throughout, which is what the whole design rested on.
