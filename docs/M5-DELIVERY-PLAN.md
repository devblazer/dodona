# M5 — Delivery: foreign git processes, multi-repo tickets, and ships that outlive an agent

Status: **plan, not built.** Written 2026-08-18 after tracing two real skills (`start-ticket`
and `finish-ticket`, used across the operator's SG/NSG work projects) against Dodona as it
stands. This document is the authority for the work; `ORCHESTRATOR-DESIGN.md` §7.1/§7.2
introduced the delivery-mode idea and are **superseded on two points**, corrected in §2 and
§9 below.

## 0. The one-line statement of the problem

Dodona was designed as the thing that merges. The projects it will actually run in already
own their ticket lifecycle end to end — two repos, a naming convention, a push, a PR, a
Copilot pass, a human peer review in Slack, a merge, an npm publish, a lockfile reconcile, a
deploy — and that process **spans days and expects to be asked questions**. Dodona's job in
those repos is not to merge. It is to provide four things the project cannot provide for
itself:

1. **Isolation** — N lanes that cannot see or corrupt each other's checkout.
2. **Serialization** — of the few resources a forge cannot serialize (a version number).
3. **An answer channel** — so "stop and ask the user" is a badge, not a dead end.
4. **Continuity** — the work outliving the agent, the window, and the daemon.

Everything below serves those four. Where Dodona currently does something the project wants
to own, Dodona stops doing it.

## 1. Principles this plan is held to

- **The project owns ceremony; Dodona owns isolation, serialization, continuity and the
  answer channel.** Branch names, base refs, PRs, reviews, merges, deploys and version
  policy are ceremony.
- **Nothing here requires editing the project's skills.** Most repos are not the operator's
  to edit, and any skill can be rewritten by someone else next week. Skill-side cooperation
  is an optimization (§12), never a prerequisite.
- **Enforcement in code, not instruction** (CLAUDE.md §0). Every guarantee below is a lock, a
  hook, or a check. Prompts are used only to remove friction, never to provide safety.
- **Deny with a rewrite, never a wall.** A denied command that names its substitute keeps the
  project's own skill running; a bare refusal strands it.
- **Never hung, halted, stuck, or outdated** (CLAUDE.md §0.1). A wait is allowed only when the
  thing that un-sticks it is named, visible and badged — and stale waits escalate.
- **Atomicity across repos was never available.** Ordering plus idempotent recovery is the
  honest goal, and it is sufficient. That is what replaces the one-repo rule.

## 2. Multi-repo tickets are first-class (supersedes the one-repo rule)

`Repos.ForClaims` currently refuses a ticket whose claims span two repositories: *"a ticket
lands by fast-forwarding one repository, and two fast-forwards cannot be atomic; split it
into one ticket per repository."* The atomicity observation is true; the conclusion was
wrong. The real workflow **always** touches two repos on purpose (core and the current game),
with a genuine ordering dependency — core merges and publishes before the game's lockfile can
be reconciled. Splitting the ticket destroys exactly the coupling that makes it correct.

**A ticket becomes a group of repo members.**

```
tickets         id, title, mode, delivery, state, created_ts
ticket_repos    ticket_id, repo, order_index, branch, worktree,
                state, pr_url, pr_state, checked_ts       -- 1..N per ticket
ticket_claims   ticket_id, repo, kind, value              -- repo-qualified
```

- `order_index` expresses the dependency (core = 0, game = 1). The observer (§10) uses it: a
  member is *blocked* until its predecessors reach a terminal state.
- `Repos.ForClaims` keeps inferring the repo per claim; it stops collapsing to one, and stops
  refusing on more than one. The refusal survives in exactly one place — `delivery:
  local-merge` with two members — where it becomes an **ordered sequence of lands with
  idempotent recovery**, announced as such, rather than a pretence of atomicity.
- Ticket-level state is **derived** from its members, never stored twice.

### Monorepos are a different problem, and also solved

A monorepo is **one** member with many path claims — the rule above never bound it. Its
problem is *materialization*: a worktree of a large monorepo is a full checkout plus a full
dependency install per lane. Addressed in §5, including claims-driven sparse checkout.

## 3. Delivery is a per-repo mode

In that repo's `dodona.json` (`Config.For` already falls back per-repository, so no new
plumbing):

```json
{ "delivery": "pr",
  "protectedBranches": ["main", "develop", "release/*"],
  "install": ["yarn install"],
  "verify": ["yarn lint"] }
```

- **`local-merge`** (default) — today's behaviour. Dodona names the branch, holds the merge
  token, executes the ff-only land, runs post-land verify, prunes.
- **`pr`** — Dodona never merges, never deletes a branch, never requests a merge token. It
  supplies isolation, the release slot (§9), the answer channel (§8) and the observer (§10).
  The project owns branch naming, push, PR, review and merge.

Detection assist, never a guess: at `ticket-create`, if the repo carries skills or docs
matching ticket/branch/PR words while `delivery` is unset, announce the suggestion once and
proceed on the default (§11 act, announce, allow undo).

## 4. Worktree layout: siblings must stay siblings

The breaking discovery. `start-ticket`'s first guard resolves "the current primary game" by
scanning **the parent directory of core** for a sibling git repo whose `package.json` depends
on `@accamax/slotsmax-ui-core`. Dodona puts worktrees at `<member>/.dodona/wt/t<N>`, so from
a core worktree the siblings are other tickets' worktrees. The scan finds nothing, the skill
hits *"zero or more than one → ask the user"*, and the lane strands on its first step. The
game half would meanwhile be the operator's live tree — no isolation at all.

**A grouped ticket gets one parent directory, with its members as siblings inside it:**

```
<workspace-primary>/.dodona/wt/t7/
    slotsmax-ui-core/        worktree of core          (order_index 0)
    nsg-lucky-sevens/        worktree of the game      (order_index 1)
```

Rules:

- The parent lives under the **workspace primary**, which for this layout is already the
  container folder holding core and the games — so it is on the same volume as every member.
  Members on different volumes are refused at `ticket-create` with a clear message: git
  worktrees do not span volumes usefully, and pretending otherwise fails later and worse.
- **Each child directory's basename equals its repo's real basename.** Not `core`/`game`
  aliases — sibling resolution and local package resolution can key off real names.
- **Single-member tickets keep today's path exactly** (`<member>/.dodona/wt/t<N>`). No
  existing behaviour moves and no suite is rewritten; the group form is purely additive.
- The parent is Dodona's, not repo content — `.dodona/` is already git-ignored (CLAUDE.md §5)
  and gate files stay in `.git/info/exclude`.
- Directory names stay **short and opaque** (`t7`, never `t7-NSG-1670-long-slug`): `MAX_PATH`
  is 260 and `node_modules` will be nested under this. The branch inside a worktree can be
  named anything the project likes — **directory name and branch name are unrelated**, and
  nothing outside Dodona ever sees the directory.

CLAUDE.md §5 says worktrees stay beside their member. That stays true for single-member
tickets and gains a stated exception for groups: one parent cannot be beside two members, so
it goes beside their common ancestor.

## 5. Materialization cost, and how a lane starts warm

A worktree is a real checkout, and a game or monorepo lane needs a real dependency install.
`finish-ticket` runs `yarn install` and `yarn lint`, so this sits on the critical path.

- **The package cache is already global.** The cost is disk and link time, not download. Say
  that plainly rather than treating installs as prohibitive.
- **`"install": [...]` runs at worktree creation, before the agent starts.** The lane opens
  onto a tree that is already installed and green — the single biggest buttery win available,
  because the skill's Phase 0 preflight finds its work already done.
- **Never share `node_modules` between worktrees.** Two lanes legitimately sit on different
  core pins; sharing would corrupt both. Share the cache, not the modules.
- **Claims-driven sparse checkout for large repos** — `git worktree add --no-checkout`, then
  `git sparse-checkout set <claimed paths>`. The claims already say what the ticket touches.
  Opt-in per repo (`"sparse": true`), because a build usually needs more than the claims;
  offered for monorepos where a full checkout is the actual blocker.
- **Reap on merge, not on land.** Steady-state worktree count is "open PRs", not "every
  ticket ever" (§10).

## 6. Base-branch protection: the branch lock

Git refuses to check out a branch that is already checked out in another worktree. That is
the enforcement mechanism, and it costs nothing.

- For every repo, for every branch in `protectedBranches`, Dodona holds a sentinel worktree
  created with **`git worktree add --no-checkout`** at
  `<workspace-primary>/.dodona/locks/<repo>/<branch>`. HEAD points at the branch; no files are
  populated; disk cost is negligible even on a large repo.
- Effect: `git checkout main` inside any lane worktree becomes `fatal: 'main' is already
  checked out at ...` — the dangerous **silent** case becomes a loud one, with no hook
  involved.
- Locks are established **idempotently** at daemon start and at every worktree creation, and
  announced. A missing lock is created, never a refusal to start (§11).
- The operator asked instead whether a lane should refuse to start unless the project is on
  `main`. Same instinct, weaker mechanism: it makes safety depend on where the operator
  happens to have their own checkout, and it lapses silently the moment they switch branches
  for their own reasons. Recorded as considered and superseded.

## 7. A worktree already at latest `origin/main`, and the Bash gate

### 7.1 Pre-satisfy the precondition

`start-ticket` step 2 runs `git checkout main && git pull` in **both** repos. Its *intent* is
"start from up-to-date main", and that intent is satisfiable with no checkout at all:

- `ticket-create` runs `git fetch origin`, then `git worktree add --detach <dir>
  origin/<main>`.
- **Detached on purpose** — Dodona must not pre-name a branch that the project's convention
  will name (`NSG-1670-<slug>`). Dodona **learns** the branch after the project creates it
  (`rev-parse --abbrev-ref HEAD` into the member's `branch` column) and re-reads it at every
  turn-final as drift detection.
- The lane's prompt states the tree is already at freshly-fetched `origin/main` so the step
  can be skipped; the gate rewrites it if attempted anyway.

### 7.2 The gate is a deny-list with rewrites, never an allow-list

The deployed gate's matcher extends from `Edit|Write|MultiEdit|NotebookEdit` to include
**`Bash`**, inspecting `tool_input.command`. **Polarity matters:** default allow, deny only
the enumerated list. An allow-list would break the legitimate exotic git these skills depend
on — the `git commit-tree` patch-id probe, and the two-commit temp-name round-trip for
casing-only renames on Windows.

| Denied | Rewrite handed back |
|---|---|
| `checkout` / `switch` of an **existing** branch | "you are already on `<branch>` in a dedicated worktree — skip this step" |
| `checkout main` then `pull` (the refresh intent) | "already at latest `origin/main`; to refresh: `git fetch origin && git rebase origin/main`" |
| `git stash` in any form | "commit WIP to your own branch instead — the stash is one shared ref across every worktree" |
| `push --force` or `push --delete` of a shared branch | denied hard: outward-facing and unrecoverable |
| `worktree remove` / `prune`, `branch -D <protected>` | denied: Dodona owns worktree lifecycle |

Explicitly allowed and never questioned: `checkout -b`, `switch -c`, `commit`, `push`, `push
-u origin HEAD`, `fetch`, `pull --rebase`, `merge origin/main`, `commit-tree`, `cherry`,
`merge-base`, `rev-parse`, `git mv`, and every `gh` invocation.

The predicate is **"no checking out branches that already exist"** — *not* "no branch but
main". A lane must be able to cut `NSG-1670-<slug>`; that is the PR flow itself.

Fails **open** with a bypass log, like the existing gate: a broken gate must not brick a lane,
and the branch lock is the structural backstop underneath it.

## 8. The answer channel — where buttery smooth is won or lost

Across the two skills there are **nine** points that stop and ask: zero-or-many games; a dirty
tree; a branch carrying unshipped work; a broken `main` pull; an ambiguous migration call; an
unavailable Jira connector; unauthenticated `gh`; the Phase 3 pre-review go-ahead; and the
Phase 7 merge-versus-deploy choice. Today every one of them strands a headless lane (CLAUDE.md
§7). **These are not defects in the skills** — the last two are deliberate human decision
gates and must stay.

The machinery already exists and is proven: the daemon sets `presence = "waiting on you:
merge"` with a pane announcement, and the UI renders that as a blocked lane with a badge. It
is wired for exactly one case. Generalize it:

- **`dodona ask <lane> "<question>" [--option a --option b]`** — lane to daemon. Records a
  pending question, sets `waiting on you: <question>`, badges the lane, writes the feed row,
  and registers with the concierge question queue.
- **`dodona answer <id> "<text>"`**, or a click in the pane — injects the answer through the
  existing `[DISPATCHER]` channel, spike 3's proven mid-turn path. If the agent's turn has
  already ended, the answer arrives through `lane-respawn --resume` plus injection. Both paths
  exist today.
- One line in the lane's prompt: *"to ask the operator, run `dodona ask`; never end your turn
  on an unanswered question."* That covers all nine sites without touching the skills.
- **Staleness escalates** (§0.1): an unanswered question ages to the top of the feed and, past
  a configured age, notifies. A wait is legitimate only while it stays visible.

This is what turns a days-long human-gated pipeline from "a lane that died quietly" into "a
badge you answer when you get to it".

## 9. Serialization: three locks, named separately (corrects design §7.1)

`ORCHESTRATOR-DESIGN.md` §7.1 says the merge token "has no job" in `delivery: pr`. **That is
wrong**, and these skills are the counterexample. Two concurrent core ships each add a top
entry to `releaseNotes.md` and each bump `package.json` to the next minor: a textual conflict
on the two most contended files in the repo, plus a semantic race in which both mint
`3.91.0`. The forge cannot serialize that — it happens before a PR exists.

Three distinct locks, so nothing is overloaded:

- **Merge token** — `local-merge` only. Unchanged.
- **Release slot** (new) — per repo, held while a lane edits the release-file set
  (`releaseNotes.md`, the `package.json` version, a game's served notes), released at push.
  This is the existing token with a resource key: `TokenRead(repo)` becomes `TokenRead(repo,
  resource)`. Cheap, and it is the lock the workflow actually needs.
- **Claims** — unchanged, path-level, now repo-qualified (§2).

A related fix: the release-file set is **not** in a ticket's work claims, so the gate would
deny the release-notes and version-bump edits mid-ship. A `delivery: pr` ticket therefore gets
a **standing ship claim** covering that set at creation. (`yarn.lock` and `node_modules` are
written by yarn through Bash, not the edit tools, so the gate never sees them.)

## 10. Continuity: the ship outlives the agent

`finish-ticket` states it outright — *"spans hours to days… you will be invoked many times for
one ticket"* — and detects its own state from git and `gh` on every run. That is exactly the
shape Dodona lacks and nearly has.

**Member states:** `working → pushed → in-review → changes-requested → merged → published`,
plus `abandoned`. Terminal is `merged`, or `published` where a registry step follows.

- **The agent retires, the lane goes dormant, and the worktree SURVIVES.** A review comment
  three days later needs that exact branch back. Today's `land` force-removes it, which is
  backwards.
- **The observer arms itself** — nobody types `dodona land`. Per member it polls `gh pr view
  --json state,reviewDecision,mergeStateStatus,statusCheckRollup`, and for a registry step the
  published version.
- **Adopt the skills' own cadence rather than inventing one**: tight polling for bot and CI
  events (a Copilot check-run, a publish), and a business-hours-aware backoff for human waits.
  Take the timezone and window from config — the skills hard-code SAST and Dodona must not
  bake in one office's hours.
- **Events wake the lane.** `changes-requested` → respawn with the session resumed and inject
  *"review landed, N comments"*; the skill's own state detection takes over from there. Core
  `merged` → watch for the publish → wake the **game** member to reconcile the lockfile. The
  operator does nothing and the ship advances itself. This is the payoff for §2's
  `order_index`.
- **Merged-ness is decided by patch identity, never ancestry.** After a squash-merge a branch
  is not an ancestor of `main`, so `git branch --merged` and `rev-list --count` are both
  permanently wrong. Use the probe these skills already carry — `merge-base`, `commit-tree` the
  branch tree over it, then `git cherry origin/main <probe>` — and **adopt it verbatim** rather
  than inventing one; they have already been burned into getting it right.
- **Prune on terminal state only**, and never force-remove a worktree carrying work that is
  neither pushed nor merged: hold it and announce, with the restore path.

## 11. Preflight: never discover a missing credential at hour three

Before a ship lane starts, the daemon checks and announces in one line each: `gh auth status`;
the `workflow` token scope; presence of the MCP connectors the flow needs (Atlassian, Slack —
interactively authorized, therefore frequently **absent** in a headless lane); the toolchain
(`yarn`); protected-branch locks; member trees clean; disk for the worktree.

Anything missing is announced **with the exact fix command** — and **the lane still starts**.
The flow is resumable by design and its connector steps are best-effort; refusing to start
would be the halt §0.1 forbids. What must not happen is silently discovering it at Phase 5.

## 12. What the operator may optionally change in their own skills

Insurance, never a prerequisite. Where the operator owns the skill, four env vars injected by
the shim (`AttachShimAsync` already injects two) remove the remaining friction:

`DODONA_WORKTREE=1`, `DODONA_TICKET=<id>`, `DODONA_BRANCH=<branch or empty>`,
`DODONA_SIBLING_ROOT=<the ticket parent>`.

Then: make step 2's checkout-and-pull conditional; resolve the primary game from
`DODONA_SIBLING_ROOT` instead of scanning; replace any `git stash` with a WIP commit; and call
`dodona ask` where the skill says "stop and ask". Each is a two-line change and each is
strictly optional — Dodona must be correct against the **unmodified** skill.

## 13. Cost note: a ship lane is an expensive lane

`finish-ticket` **mandates** independent subagents — a QA author, a migration deriver, and two
adversaries — and it is right to: it is defending against the authoring bias of an agent that
has been inside the diff for hours. That collides with §0.1's quota discipline, and the
resolution is not to prevent it. The policy table gains a `ship` class, and the lane announces
expected cost when it starts. Suites stay model-free regardless (§17).

## 14. Build order — each step independently shippable and verifiable

| Step | Work | Why in this position |
|---|---|---|
| **M5.1** | Lane `cwd` in the store; fix `RespawnLaneAsync`'s hardcoded `_primary`; rewrite `LaneSystemPrompt` | Prerequisite for everything, and closes a **live bug**: a resumed ticket lane runs in the operator's tree while its prompt says otherwise — catastrophic for a days-long ship |
| **M5.2** | `dodona ask` / `answer`, generalized `waiting on you`, staleness escalation | Turns nine strand points into badges; the largest usability gain per line |
| **M5.3** | Branch lock; `Bash` matcher on the gate with rewrites | Makes the silent cases loud and the loud cases self-correcting |
| **M5.4** | Ticket groups; repo-qualified claims; sibling worktree layout; the `install` hook | Makes the real workflow expressible at all |
| **M5.5** | `delivery: pr`; detached worktree at `origin/main`; learned branch; member states; patch-id prune guard | Stops Dodona doing what the project owns. **PARTLY BUILT, 2026-08-21, out of order** — `REVIEW-AND-MERGE-PLAN` R7 shipped the **field and the refusals** (no merge, no merge token, no branch deletion, no approval question) on their own, ahead of M5.3 and M5.4, on this table's own principle that each step be independently shippable; D-R28 carries the dependency argument. **Still M5.5's:** the detached worktree at `origin/main`, the learned branch, member states and the patch-id prune guard. Dodona still names and records `ticket/N`, so a lane that cuts the project's own branch leaves that record stale — harmless today only because nothing destructive reads it in pr mode. |
| **M5.6** | The observer; wake-on-event; registry watch; ordered members | The buttery part — ships advance themselves |
| **M5.7** | Release slot; standing ship claim; preflight | Removes the last two ways two lanes can collide |

### Testing (§17: model-free, collides with nothing)

- **A fake `gh`** on `PATH` returning scripted JSON, exactly as the suites already use a fake
  agent. Drives every observer transition deterministically and free.
- **A local bare repo as `origin`**, so push, PR, merge and squash-merge flows are real git
  with no network — including a **squash-merge fixture**, because the patch-id prune guard is
  the one piece where being wrong holds every worktree for ever.
- **A two-repo fixture** (a stub "core" and a stub "game" whose `package.json` depends on it)
  plus stub skills, driven by the fake agent, asserting that the sibling scan resolves inside
  the group worktree and that step 2 is skipped.
- New suite `tests/delivery-acceptance.ps1` (planned). `ui-use` gains checks for the ask badge and for
  answering from the window, because a new interactive affordance needs a use test, not only a
  dump assertion.

## 15. Rejected, with reasons — do not re-propose

- **Rewriting the projects' skills as the primary fix.** Most repos are not the operator's,
  and any skill can be changed by someone else next week. Skill cooperation is §12.
- **An allow-list Bash gate.** Breaks the legitimate `git commit-tree` patch-id probe and the
  Windows casing round-trip. Deny-list with rewrites, or nothing.
- **Sharing `node_modules` across worktrees.** Two lanes legitimately sit on different core
  pins; sharing corrupts both.
- **Ancestry (`git branch --merged`, `rev-list --count`) for merged-ness.** Squash-merge makes
  it permanently wrong, and the observer would hold every worktree for ever.
- **A plain `git diff origin/main..<branch>` for "is this repo in play".** Non-empty whenever
  the branch is merely behind `main`.
- **Refusing to start a lane unless the project is on `main`.** Considered; the branch lock is
  strictly stronger and does not lapse when the operator switches branches.
- **Dodona performing the merge in a `pr` repo.** The forge already serializes merges, with CI
  and required reviews, better than a local token can.
- **One ticket per repository as a product rule.** The atomicity argument was true and the
  conclusion was not: ordering plus idempotent recovery is what the workflow needs, and
  splitting the ticket destroys the coupling that makes it correct.

## 16. The honest strategic consequence

For a `delivery: pr` repo, Dodona is **not a merge coordinator**. The forge is. Dodona is a
parallel-lane orchestrator that supplies isolation, the few locks a forge cannot provide, an
answer channel for a human-gated pipeline, and continuity across days. That is a narrower role
than §7 of the design doc assumes — and it is the role these repos actually need.
