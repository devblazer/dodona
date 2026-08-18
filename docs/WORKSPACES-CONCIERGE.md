# Dodona Workspaces & the Concierge

Status: **agreed design, pre-implementation — HIGH PRIORITY** (operator, 2026-08-18:
everything in this document is high priority; it is the next body of work, not a
someday list). The outcome of the 2026-08-18 planning session with the operator. Decisions here were taken deliberately; §8 records the ideas
already rejected *with their reasons*, in the LANE-LIFECYCLE §2 style, so they are never
re-proposed. This document is authored in-repo (no `..\MassWorks\` master exists for it).

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
3. **Bounded discovery — expensive tier, with a fence.** The name is unknown but there is
   signal: an explicit path in the prompt attaches that folder outright (explicit info
   never triggers a search); otherwise the resolver may *look* — but only within the
   fence: parent directories of every member ever registered, plus configured search
   roots. This deliberately carves one narrow exception to "management brains never run
   tools": the resolver gets exactly one capability, enumerating candidates inside the
   fence. A classifier with a flashlight, not a crawler. On failure it falls to rung 4
   fast — **the fence never widens itself**.
4. **Ask, with a guess — and teach.** Double uncertainty lands in the merged feed
   carrying its best candidates ("Did you mean C:\repos\blazing-trumpets, or is this
   new?"). The answer becomes a registry alias; rung 4 decays toward rung 1 with use.

Creation follows the same shape: confident-new creates the workspace and announces,
reversibly (a workspace is a registry row and a store directory — no daemon until work
arrives); unconfident-new asks.

Consequences: **the shell boots to zero** (a window with no workspace awake — just feed
and input — is a real state, with a pose and a ui-use check), and rungs 1–2 handle the
steady state so rung 3 stays rare by construction (quota discipline, CLAUDE.md §0.1).
All four rungs sit behind the same fake-agent seam as the router (§17): the fence makes
rung 3 deterministic under a fixture directory tree.

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

1. **Lane granularity (§5)** — severable, ships first, on today's system: the four-kind
   verdict, spawn-and-deliver for new-task, suite coverage in brain-acceptance.
2. **Workspace identity + registry** — id-slug instances, store relocation, member
   attachment, repo-exclusivity enforcement + workspace-suite test, migration of
   root-anchored instances.
3. **Concierge daemon** — registry service, resolver rungs behind the fake-agent seam,
   group ladder, review-behind, merged-feed spine.
4. **Shell read side** — bands, merged feed, boot-to-zero, `(workspace, lane)` dump keys,
   poses, ui-use checks.
5. **Prompt-first wiring + publish scoping** — input box through the concierge,
   wake/create-on-prompt, `publish` targeting, and the suite `--all` never had.
