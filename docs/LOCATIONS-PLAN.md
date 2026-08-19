# Projects — lanes that open in the right project, and a router that remembers where projects are

**Status: work order, ready to assign.** Designed with the operator 2026-08-19, grounded in four
read-only audits of the tree. Words are as defined in [GLOSSARY.md](GLOSSARY.md) — read that
first; several rounds of this design were lost to *project* / *repo* / *member* confusion.

Every phase below is **independently assignable to one lane**, states what becomes
**impossible**, and is not finished until it can name something it made inexpressible
(`RECOVERY-PHASES.md` §0's rule).

**Every lane works in its own worktree** (`dev worktree <name>`, CLAUDE.md §0.0) — a commit from
the shared checkout is refused by a hook, and two lanes in one tree is how `f9aaf25` carried
another lane's work into an unrelated fix.

---

## 0. What the operator asked for

```
workspace —1:1— router —1:many— manager (one per project) —1:many— lane
```

Order of precedence for a typed sentence:

1. it is a comment for an **existing lane**
2. it is a new lane in a project **an open lane is already in**
3. it is a new lane in a project we have a **memory** of
4. we do not know the project — **ask**. Do not search; searching takes too long.

Plus: *"the router has memory of every project ever."*

### How it lands in the code

A **manager is a scope, not a process**: coordination state here is a transaction, not a
conversation. `Store.TicketCreate` intersects claims and inserts them atomically
([Store.cs:486](../src/Dodona/Store.cs#L486)) precisely so correctness never depends on a
coordinator being alive, and the merge token is already per repo
([Store.cs:169](../src/Dodona/Store.cs#L169)).

So "the manager for project X" is: **rows keyed by X**, plus **the live lanes recorded in X**,
plus **X's brain**. One store per workspace throughout — `M5-DELIVERY-PLAN.md` §2 requires an
ordered ticket spanning two repos, and separate per-project stores would leave it nowhere
transactional to live.

---

## Phase 0 — repo identity  *(blocks everything; a live bug)*

**This is broken today, with no changes from us.**

`Repos.Discover` recomputes repo names from the registry and filesystem on every call
([Daemon.cs:131](../src/Dodona/Daemon.cs#L131)). `tickets.repo` is written once
([Store.cs:497](../src/Dodona/Store.cs#L497)) and **never updated**. The naming rule changes
with project count: a lone project that *is* a repo is named `"."`
([Repos.cs:82](../src/Dodona/Repos.cs#L82)); attach a second project and the same repository
becomes `proj` ([Repos.cs:63](../src/Dodona/Repos.cs#L63)).

Then `TokenRead(".")` and `TokenRead("proj")` are **two merge-token rows for one repo**
([Store.cs:658](../src/Dodona/Store.cs#L658) — rows materialise on demand) while both `LandOp`
calls resolve to the same folder ([Daemon.cs:2611](../src/Dodona/Daemon.cs#L2611)). Two agents
can each believe they hold the token and both fast-forward `main`.

Worse: `Repos.ByName(repos, ".")` returns null → `gate_redeploy_failed`
([Daemon.cs:401](../src/Dodona/Daemon.cs#L401)) → `GcOldBuilds` deletes the exe the stale gate
invokes → **the claim gate fails open** ([Daemon.cs:389](../src/Dodona/Daemon.cs#L389)).

Three more routes to the same drift: `--bulk` attach yields one repo under two live names;
`leaf~2` is **recyclable**, so a later-attached repo inherits another's open tickets and token
rows ([Repos.cs:69](../src/Dodona/Repos.cs#L69)); and repo names come from live disk casing
([Repos.cs:90](../src/Dodona/Repos.cs#L90)) while SQLite `=` is binary-collated.

| item | what |
|---|---|
| P0.1 | A **stable repo key** on the ticket — the repo's canonical path (`Instance.Canonical`, unit-tested at `PureLogicTests.cs:339-371`). `tickets.repo` stays as the display name; identity is the new column. |
| P0.2 | `merge_token` and `token_queue` key on the same identity. This is where the two-token bug bites. |
| P0.3 | Migration: resolve each existing `tickets.repo` through the current `Discover` and stamp the path. A row that no longer resolves is **announced**, never silently dropped. |
| P0.4 | Compare repo names case-insensitively wherever they are compared in SQL. |
| P0.5 | `claim-extend` must fetch the ticket's repo — it has none today ([Daemon.cs:853](../src/Dodona/Daemon.cs#L853)), and `Store.ClaimExtend` takes only a ticket id. It also **silently drops unparseable specs and reports success** ([Daemon.cs:857](../src/Dodona/Daemon.cs#L857)); make it loud like `ticket-create`. |
| P0.6 | `ticket-create --repo X` **skips `ForClaims` entirely** ([Daemon.cs:719](../src/Dodona/Daemon.cs#L719)), so claims are never validated against the named repo. Validate them. |

**Becomes impossible:** two merge tokens over one `main`; a ticket whose gate silently stops
being redeployed; a recycled `~2` name inheriting another repo's tickets.

**Prove red first:** attach a second project to a `"."` workspace, then show the pre-existing
ticket still resolves to **one** token; detach-and-reattach cannot transfer open tickets between
repos; `--repo tools --claim path:engine/...` is refused.

**Must not break:** `m1:77-82` (`overlap_refused_at_plan_time`, `disjoint_parallel`),
`m1:194-195`, `workspace:111-119` (per-repo tokens), all of `ClaimsTests`.

---

## Phase 0b — claim scoping  *(after 0, never before)*

Scoping `FindConflicts` per repo was proposed as a free win. **It is not**, and this records why
so the naive form is not re-proposed:

- *"a ticket's claims all live in one repo"* — **false**, bypassed by P0.5 and P0.6.
- *"cross-repo claims can never overlap"* — **false once names drift.** After `.`→`proj`, the
  path claims `"src/water"` and `"proj/src/water"` already fail to overlap, leaving the
  **`symbol:` claim as the last remaining detector** — which is exactly what naive scoping
  would delete.

The `symbol:` **false positive** is nonetheless real: symbol values carry no repo
([Claims.cs:29](../src/Dodona/Claims.cs#L29)), `ForClaims` skips them
([Repos.cs:141](../src/Dodona/Repos.cs#L141)), `Overlap` compares bare equality
([Claims.cs:36](../src/Dodona/Claims.cs#L36)). So `symbol:Config` in one repo blocks it in
another.

**Do:** scope on P0.1's stable key. Path claims may narrow by name; symbol claims narrow by repo
path or stay workspace-global. Also fix `subtree:/`, which parses to an empty value that
overlaps nothing and covers nothing — a claim reading "the whole tree" that blocks nobody.

**Ship with the check this exists for:** two open tickets in *different* repos holding the
*same* claim string. That check cannot be written today.

---

## Phase 0c — Dodona stops inventing workspaces  *(independent; can run in parallel with 0)*

**Operator, 2026-08-19: creating a workspace is a user action. Dodona must never do it on its
own.** Today it does.

`Ws()` uses `--workspace` when given, otherwise `WorkspaceResolve.ForPath(reg, root)` where
`root = Environment.CurrentDirectory` ([Program.cs:1262](../src/Dodona/Program.cs#L1262)) — and
`ForPath` **creates a workspace, attaches the folder, and may move a legacy store**
([WorkspaceResolve.cs:42](../src/Dodona/WorkspaceResolve.cs#L42)).

The operator's own input never has this problem — the box knows its workspace. The gap is an
**agent inside a lane** running a `dodona` command: the lane's environment carries
`DODONA_SHIM_INFO` and `DODONA_LANE_ROLE` ([Daemon.cs:1797-1801](../src/Dodona/Daemon.cs#L1797))
but **not the workspace id**, so the CLI falls back to guessing from a folder.

| item | what |
|---|---|
| P0c.1 | `psi.Environment["DODONA_WORKSPACE"] = _instanceId` at the spawn site. |
| P0c.2 | `Ws()` honours it **before** ever looking at a folder. Order: `--workspace` → env → path. |
| P0c.3 | Path resolution **creates only when the path was explicit** (`--root` typed by a person). An inherited cwd that nothing owns is a loud refusal, never a creation. |

**Becomes impossible:** a phantom workspace named after whatever folder a process happened to be
in; a store relocated because an agent ran `dodona tickets`.

**Must not break:** `workspace:304-317` (the legacy-store migration set) — auto-create on an
**explicit** `--root` is what makes that migration invisible and must survive.

---

## Phase 1 — build the ability to see  *(blocks 2–5)*

**Nothing in this plan is observable today.** From the coverage audit:

- Exactly **two** checks in the tree assert a lane's working directory (`m3:183-187`).
- **No suite reads `lanes.cwd` at all.** The `shim_spawned` event detail
  ([Daemon.cs:1839](../src/Dodona/Daemon.cs#L1839)) is the *only* observable surface for a
  lane's project in the whole product — not `status`, not `ui dump`.
- **No suite ever starts a plain lane, or a brain, in a two-project workspace.** The only
  two-project fixture with a daemon (`workspace:329-353`) makes a ticket and stops.
- **No check asserts `registry.db` lives under `DODONA_HOME`.** A machine-wide table reaching
  for `%LOCALAPPDATA%` directly would pass every suite while writing into the operator's real
  registry.

| item | what |
|---|---|
| P1.1 | A two-project fixture that **starts plain lanes** and stays up long enough to assert on them. Extend `pair` or add a sibling. |
| P1.2 | A lane's project appears in `dodona status` and in `ui dump`'s slot shape. A wrong project is currently invisible to a person as well as to a check. |
| P1.3 | Extract the cwd precedence decision (ticket worktree → recorded cwd → first project, [Daemon.cs:549](../src/Dodona/Daemon.cs#L549), [:1779](../src/Dodona/Daemon.cs#L1779)) into a **static**, like `IsObviousGeneric` and `LanePrefix`. Three nullable strings, no I/O — it belongs on the 1-second `unit` loop. |
| P1.4 | A check that `registry.db` is under `DODONA_HOME`. |

**Becomes impossible:** landing any later phase unobserved.

---

## Phase 2 — a lane opens in a project

**Half-built already.** Schema v8 (`lanes.cwd`) landed with M5.1;
`AttachShimAsync` is fully parameterised on `workDir` and records it for every spawn
([Daemon.cs:1789](../src/Dodona/Daemon.cs#L1789)); a **ticket lane already runs outside the
first project today** ([Daemon.cs:692](../src/Dodona/Daemon.cs#L692)). Three sites hardcode
`_primary`: [:501](../src/Dodona/Daemon.cs#L501) (`lane-start`),
[:1695](../src/Dodona/Daemon.cs#L1695) (`SpawnAgentLaneAsync`, the typed-input path), and
[:1693](../src/Dodona/Daemon.cs#L1693) (the system prompt).

Ticket lanes already prove the spawn chain is cwd-transparent, `lanes.cwd` round-trips, and — most
reassuring — **an agent's own `dodona` commands resolve correctly from a non-primary folder**,
because `Registry.Owner` does longest-ancestor matching
([Workspaces.cs:199](../src/Dodona/Workspaces.cs#L199)).

### The traps, each cited

| # | trap |
|---|---|
| T1 | **The prompt must move with the process.** [:1693](../src/Dodona/Daemon.cs#L1693) writes *"Your working directory is X — work there"* into the agent's instructions; [:1695](../src/Dodona/Daemon.cs#L1695) sets the real one. Change one and not the other and the agent is told a folder it is not in, then works there. **Compiles clean.** Already happened once ([Daemon.cs:541-546](../src/Dodona/Daemon.cs#L541)). **One parameter, used twice.** |
| T2 | **Config is loaded once from the first project** ([Daemon.cs:163](../src/Dodona/Daemon.cs#L163)) and hands lanes `permissionMode` and `allowedTools` ([:1669](../src/Dodona/Daemon.cs#L1669)). A lane in project B would run with project A's permissions — **a repo deliberately kept on a leash loses it.** `Config.For` already exists ([:60](../src/Dodona/Daemon.cs#L60)) and has never been used to configure a lane. Use it. |
| T3 | **Work lanes SHOULD load the project's `CLAUDE.md` and skills** — that is the point of opening there. Brains must not, and keep `NeutralCwd()`. Do not "fix" T3. |
| T4 | **`lanes.cwd` outliving its project.** `workspace-detach`/`-move` ([Workspaces.cs:379](../src/Dodona/Workspaces.cs#L379)) touch no lane row; respawn only checks `Directory.Exists`. The folder still exists — it just belongs to another workspace now. |
| T5 | **`repo-init` and `repo-status` act on the first project only** ([Daemon.cs:1047-1101](../src/Dodona/Daemon.cs#L1047)). An agent in project B running `repo-init` initialises project A. Silently. |
| T6 | **Multi-project claim-check is already broken** ([Daemon.cs:823-841](../src/Dodona/Daemon.cs#L823)): the two bases are the worktree and the first project, so a write in project B resolves to neither and is denied. Projects make this normal, so the latent hole starts firing. |
| T7 | **A plain lane is completely ungated** — `GateHook` returns 0 with no `--ticket` ([Program.cs:732](../src/Dodona/Program.cs#L732)). This phase puts an ungated agent into a second repo. Not a regression; an expansion of the ungated surface. Say it out loud rather than let it be discovered. |

| item | what |
|---|---|
| P2.1 | A project is resolved at the spawn site and **must be a registered project or inside one** — refuse anything else, loudly. |
| P2.2 | `SpawnAgentLaneAsync`, `lane-start` and `LaneSystemPrompt` take **one** project value (T1). |
| P2.3 | Lane config from `Config.For(project)` (T2). |
| P2.4 | `repo-init` / `repo-status` take a project (T5). |
| P2.5 | Fix the claim-check bases (T6). |
| P2.6 | `workspace-detach` / `-move` reconcile lanes in the departing project — stop or re-home them (T4). |

**Becomes impossible:** a lane in a folder no workspace owns; a lane inheriting another
project's permission mode; a prompt naming a folder the process is not in; a detached project
leaving live lanes behind.

**Checks that must change** — each a warning sign; **the one-project case must stay
byte-for-byte identical:**

- `ui-use:132-161, 259, 280-282` — the "typing into an empty project just works" cluster.
  `typing_never_tells_you_to_use_the_cli` (`:133`) is the sharpest.
- `brain:169-179, 194-195`. `held_input_invents_no_lane` (`:220`) is a **negative** check: if
  choosing a project writes a lane row before the project is known, it fires.
- **`workspace:146-150` `lanes_are_workspace_wide`** — its stated premise is *"lanes are
  workspace-wide"*. This phase contradicts it. The one check whose **name** must change.
- `m3:186-187` survives **only if the ticket-worktree branch still wins** over the project
  branch. A red here is a correctness incident, not a test problem.

---

## Phase 3 — the router's memory, and the four rungs

**Extend what exists.** `members` already *is* every project ever attached, and `registry.db` is
already WAL + `busy_timeout` and built for several writers
([Workspaces.cs:66](../src/Dodona/Workspaces.cs#L66)). Missing: **spoken-name handles per
project** (`aliases` maps alias → *workspace* today, [Workspaces.cs:102](../src/Dodona/Workspaces.cs#L102))
and **recency** for ordering. No new parallel table — fewer owned things is this project's whole
failure mode. The concierge stays the registry's sole writer; a daemon that learns a project
tells it.

| rung | decided by | cost |
|---|---|---|
| 1. comment for an existing lane | `addendum` / `generic`, built | one cheap call |
| 2. new lane in a project an open lane is in | classifier picks from the **distinct projects of live lanes** | one cheap call |
| 3. new lane in a remembered project | **code**: exact leaf → alias → normalised (`project zed` → `project-zed`) | free |
| 4. unknown | **ask** (Phase 4) | free |

**The discovery fence is demoted below the ask** (operator, 2026-08-19): it becomes an explicit
*"go and look for it"* affordance, never automatic. `Fence.cs` stays.

**Rung 2's trap — this nearly shipped.** "Is something already live in this project?" **must not
be one instantaneous pipe read.** A shim's pipe name blinks out of the namespace between clients
— 8 of 192 reads over 1.5 s saw nothing while the shim was alive — and a single read declared
4–7 lanes dead per restart. Use `LaneLiveness` (the union of pipe **and** live recorded pid).

**Checks that must change:**
- `concierge:226-228` loops over literal rung names. A new rung needs a name here **and** a
  fixture producing it. Renaming any rung breaks six checks at once.
- `concierge:152` `fence_never_reaches_outside_itself` expects `ask` — a memory rung ahead of
  discovery could redden it for correct behaviour, or leave it green having tested nothing.
- `concierge:107/111` — a memory that records a folder on first sight changes what `created`
  means on the repeat call.
- `concierge:167-172` — rung 4 teaches an *alias* today; `rung_4_decays_to_rung_1` asserts the
  decay lands at `registry`.
- `concierge:80-127` — the fixture creates workspaces in a fixed order and
  `fuzzy_match_on_the_cheap_tier` indexes `$names[1]`. One round has already been lost to this.

---

## Phase 4 — asking, as one component with two render modes

**The question already exists as a row** — `questions` in the concierge store, opened by `Ask`
([Concierge.cs:512](../src/Dodona/Concierge.cs#L512)), rendered today as a feed line answered by
typing `dodona concierge-answer <id> <name>`. So the UI is not inventing a dialog; it is
rendering a question it already has. That is what keeps this compatible with two recorded rules:
CLAUDE.md §3.1 (*"No folder UI, ever"*, echoed in `PickerWindow.xaml.cs:9-22`) and
[MainWindow.xaml.cs:597](../src/DodonaUi/MainWindow.xaml.cs#L597) (*"the UI does not get to
invent a dialog… deciding where work goes is the system's job"*).

**Operator's shape (2026-08-19): one bare-bones asking component, built once, rendered
differently live vs headless.** The window already has an overlay layer (Escape closes it; there
is an `overlay` pose).

| item | what |
|---|---|
| P4.1 | One `Ask` component with one source: the open `questions` row. |
| P4.2 | **Live:** an in-window overlay. **Headless/test:** reported through `ui dump`. Never a modal — a test window is forbidden from producing one ([MainWindow.xaml.cs:327](../src/DodonaUi/MainWindow.xaml.cs#L327)), so a modal is permanently untestable. |
| P4.3 | **The answer path is identical in both modes** — same daemon command as `concierge-answer`. Only pixels diverge. This is what makes the divergence honest. |
| P4.4 | Unreachable from a bare launch. `ui-use:496` exists because the folder picker used to answer one; its comment says a failure means *"the front door regressed to a dialog"*. A question appears only in response to something the operator typed. |
| P4.5 | **`repo-init` asks instead of instructing.** Today ticket-create refuses with *"lanes work without git; only tickets need a repository"* and tells you to run a command — a GUI telling you to type. Same overlay, second question: *"this project has no git repo; create one?"* |

**Note:** `ui dump` has **no field for a dialog** today, which is why `PickerWindow` and
`StartLaneWindow` are entirely untested. P4.2 is what makes any of this checkable.

---

## Phase 5 — a brain per project

**No longer deferred.** The operator's correction (2026-08-19) dissolves the blocker:

> *"You just use a global system to keep track of that stuff. If it's not tracked, it's not
> valid. Why must you do some weird kill to count?"*

Correct. The count-and-kill exists only because **nothing records which brains are supposed to
exist**. [Daemon.cs:373-387](../src/Dodona/Daemon.cs#L373) keeps a single `keep` id per role and
shuts down the rest as a leak — with N brains that kills N-1 healthy sessions on every restart
(including every auto-publish swap) and **announces it as a repair**. Verified directly against
the source.

Replace the heuristic with a fact: **a brain is valid iff a row says it should exist for
(role, project).** Then "surplus" is not a count, it is an unmatched registration — and nothing
healthy is ever killed.

| item | what |
|---|---|
| P5.1 | A `lanes.project` column (schema v9). Note the daemon **refuses to hot-swap down** across a schema version ([Daemon.cs:1249](../src/Dodona/Daemon.cs#L1249)), so this blocks rollback; take the pre-migration backup seriously. |
| P5.2 | Registration replaces counting. **Delete the surplus-retirement loop** ([:373-387](../src/Dodona/Daemon.cs#L373)); reap only what has no valid registration. |
| P5.3 | `_brainLo`/`_brainHi` become dictionaries keyed by project; locks become per-project, mirroring `_compressorLocks` and for the reason stated there. Scalars today mean `EnsureBrainAsync` for project A can silently return **project B's session**. |
| P5.4 | `ClearOfLivePredecessorsAsync` filters by `(role, project)` ([:2079](../src/Dodona/Daemon.cs#L2079)). `_shutdownAsked` is a `HashSet` **never cleared** ([:2110](../src/Dodona/Daemon.cs#L2110)) — verified — so one wedged brain would block creation for every project. Give it a bounded retry or per-project scope. |
| P5.5 | Reap a brain whose project is no longer attached — a lifecycle event that does not exist today, and the obvious source of the next leak. |
| P5.6 | `status` and `reconcile_done` list brains **per project**. `reconcile_done` regexes `brain=\d+`, so `brain=3,7` would keep matching while asserting nothing (`brain:256-258`). |
| P5.7 | A configurable cap on concurrent brains. Measured: each lane is two OS processes; today's steady state is 4 lanes / 8 processes; ten projects would be 13 / 26, peaking at 23 / 46 with every `brain-hi` warm — **ten of them opus**. |
| P5.8 | Brains keep `NeutralCwd()`. Per-project means *scoped to*, never *running in* — a manager that loads a project's `CLAUDE.md` and skills can run `/ship`. |

**Checks that must change — both degrade dangerously rather than redden:**
- `brain:333` picks a brain with `LIMIT 1`. With N it picks an arbitrary one and the section
  becomes **a green proving nothing**.
- `brain:256-258` regexes `brain=\d+` — see P5.6.
- `brain:58-59, 246-248, 250-253` all assert `COUNT(*) = 1`; they become per-project counts.

**Prove red first:** two projects get two distinct brains; a restart adopts both and retires
neither; a stuck brain in project A does not block project B; a detached project's brain is
reaped.

---

## Order

```
Phase 0   repo identity            ─┐
Phase 0c  no invented workspaces   ─┴─ parallel; both independent
Phase 0b  claim scoping             ← needs 0
Phase 1   the ability to see        ← needs nothing; do it early
Phase 2   lanes open in a project   ← needs 0, 1
Phase 3   memory + four rungs       ← needs 2
Phase 4   the ask overlay           ← needs 3 for rung 4; P4.5 stands alone
Phase 5   a brain per project       ← needs 2 (without it, N brains serve one project)
```

Every phase leaves the **one-project case byte-for-byte identical** — the property the whole
workspace migration rested on, and the one this plan is most likely to break.

**Every new check is `dev prove`-d red before it is believed** (CLAUDE.md §0.3), using the
grouped form `dev prove <suite>:<check> ...` — one run per suite, not one per check.

---

## Decisions taken — do not re-propose

- **D-L1. A manager is a scope, not a process.** Coordination state is a transaction. Rejected:
  a per-project coordinating process — it either shares the store and owns nothing, or owns its
  own and makes M5's ordered cross-repo ticket need two-phase commit.
- **D-L2. One store per workspace**, rows keyed by project. Rejected: per-project stores (same
  reason).
- **D-L3. The discovery fence is demoted below the ask, not deleted.** *(operator)* Rejected:
  deleting `Fence.cs` — occasionally the right answer, just never unbidden.
- **D-L4. The ask renders an existing `questions` row.** One component, two render modes, **one
  answer path**. Rejected: a modal (untestable by construction).
- **D-L5. Project memory extends `members`/`aliases`.** Rejected: a new parallel `places` table.
- **D-L6. Liveness is `LaneLiveness`, never one instantaneous pipe read.** Already one near-miss.
- **D-L7. A project is a location — a folder.** It may be a repo, hold one, hold several, or
  hold none. Merging serialises per **repo**; everything operator-facing is per **project**.
- **D-L8. Track brains, do not count them.** *(operator, 2026-08-19)* Validity is a
  registration keyed by (role, project). Rejected: keeping the count-and-kill loop — it cannot
  tell "five brains because five projects" from "five brains because of a bug", and it fails as
  healing.
- **D-L9. Dodona never creates a workspace on its own.** *(operator, 2026-08-19)* Only an
  explicit `--root` or an operator action creates. An inherited cwd refuses.
