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

**Delivered 2026-08-19, and three corrections to the text above** (from verifying every citation
before changing what it pointed at):

- **A fourth route, which this plan did not name, and it is the worst one.** `LandOp`'s repo
  lookup fell back to `_primary`: `var repoPath = repo?.Path ?? _primary`. So a ticket whose name
  had drifted did not merely share a token — the daemon ran `git merge --ff-only ticket/N` **in
  the first project's repository**. A ref advance is the one irreversible act in this system and
  it had a default. There is none now: unresolvable is a refusal, in `LandOp`, `token-request`
  and `claim-extend` alike.
- **The line numbers are consistently one or two low** (`Repos.cs:82` is at 83, `:63` at 65–72,
  `:69` at 70, `:141` at 142; `Daemon.cs:401` at 402, `:719` at 721). Every claim they support
  holds. `PureLogicTests.cs` is at `tests/Dodona.Tests/PureLogicTests.cs`, and `:339-371` is
  `InstanceCanonicalTests`, as stated. `Daemon.cs:857` is exact.
- **Schema 9 is spent.** P5.1's `lanes.project` is **v10**.

**How the two identities divide, because the next phase will have to know:** a ticket's
`repo_path` is WHERE it is and `repo` is what it was CALLED when its claims were written. The
name is frozen for an open ticket on purpose — its `claims` rows are workspace-relative to it,
so refreshing the name would move its claim namespace underneath it and its own gate would start
denying its own files. Reconciling names ACROSS tickets is Phase 0b's job, and P0.3's migration
announces every row it could not resolve rather than guessing.

**Prove red first:** attach a second project to a `"."` workspace, then show the pre-existing
ticket still resolves to **one** token; detach-and-reattach cannot transfer open tickets between
repos; `--repo tools --claim path:engine/...` is refused.

**Must not break:** `m1:77-82` (`overlap_refused_at_plan_time`, `disjoint_parallel`),
`m1:194-195`, `workspace:111-119` (per-repo tokens), all of `ClaimsTests`.

---

## Phase 0b — claim scoping  *(BUILT, 2026-08-19, branch `loc-p0b`)*

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

### What was built, and the one judgement call in it

**The false NEGATIVE turned out to be the real defect, and the plan text above understates it.**
It reads as though the drift were a reason to be careful about deleting the symbol detector. It
is worse than that: the drift is *itself* a live hole, and the symbol claim was never a detector
of it in any useful sense — `symbol:` is optional, almost nothing uses it, and two tickets over
one folder with only path claims were **already invisible to each other**. `workspace-acceptance`
carried a comment saying exactly this and treating it as acceptable ("after the rename it CANNOT
be seen to \[overlap\] … that half is Phase 0b's problem"). So the fix is not a narrowing with a
compensation; it is a *widening* that happens to make the narrowing safe.

| item | what |
|---|---|
| P0b.1 | `Claims.Held(RepoKey, RepoName, Kind, Value)` + `Claims.Overlap(Held, Held)`: reduce both claims to **repo-relative** terms and compare only within one `repo_path`. `Store.FindConflicts` takes a `RepoId` and selects `t.repo_path, t.repo` alongside each stored claim. |
| P0b.2 | `Store.ClaimExtend` reads the ticket's repo **inside its own transaction** (`TxRepoId`) rather than being handed one — a caller that can pass the wrong repository can scope a conflict search to the wrong place. |
| P0b.3 | `Claims.Prefix(repoName)` is the single definition of the claim prefix; `RepoRef.ClaimPrefix` calls it. `Claims.cs` is now linked into `DodonaUi.csproj` beside `Repos.cs`, which needs it. |
| P0b.4 | `subtree:/` (and `.`, `./`) is **the whole tree**; `Claims.Under` gives an empty subtree value its meaning in both `Overlap` and `Covers`. `Normalize` folds a leading `./`. An empty value is **refused** for `path`/`new`/`symbol`. |

**Symbol claims narrow by repo path. The reasoning, since this is the judgement someone will
want to revisit:** the brief said to bias toward the false positive, and this decision looks like
the opposite — so the argument has to be that it is not a false-negative risk at all, and it is.
A symbol claim can only be held by a ticket, a ticket lands in exactly one repository, and
`Repos.CheckClaims`/`ForClaims` refuse any path claim outside it — so an agent holding
`symbol:Config` in `engine` **cannot reach** `tools`, and `tools`'s `Config` is a different file.
There is no shared resource, so scoping removes a refusal that protected nothing. What made the
old behaviour *look* protective was that it was the only rung still firing after a rename; P0b.1
fixes the rung that should have been firing, and the check
`the_same_claim_string_in_the_SAME_repo_is_still_refused` exists so that a future "tidy-up"
cannot delete the symbol rule and read as green.

**Every uncertainty falls through to the unscoped comparison** — that is where the bias lives,
and there are three of them: a ticket with no `repo_path` (pre-schema-9, unresolvable name); a
claim that is not inside its own ticket's repository (only reachable for a row written while P0.6
was open, e.g. `--repo tools --claim path:engine/sim.cs`); and an empty subtree value in a
prefixed repository, which is wider than any one repo. In a **one-repository workspace** every
ticket shares one key and the prefix is empty, so the scoped comparison *is* the old one —
asserted eight ways by `unit:In_a_single_repository_workspace_scoping_changes_nothing`.

**`subtree:/` is the whole tree rather than a parse error**, and the choice is not arbitrary.
The empty value is needed internally regardless: reducing `subtree:proj` to repo-relative terms
inside repo `proj` yields exactly `""`, so `Overlap` and `Covers` have to answer it correctly
whatever the parser does. And in a one-repo workspace, where the prefix is empty, `subtree:/` is
the *only* spelling of "I am refactoring the whole repository, claim all of it" — refusing it
would leave a legitimate and maximally-blocking claim inexpressible. A bare `subtree:` with
nothing after the colon stays refused: `/` and `.` are deliberate, an empty tail is a typo.
`path:/` and `symbol:   ` are refused by name, because HEAD created a ticket holding a claim over
nothing and reported success — P0.5's silently-dropped spec from the other side.

Demonstrated, `dev prove`-d against HEAD (verdicts in the commit message):

| check | suite | what HEAD did instead |
|---|---|---|
| `one_folder_under_two_names_is_one_claim` | workspace | created ticket 3 in a folder ticket 1 held |
| `claim_extend_cannot_widen_across_a_rename` | workspace | `extended ticket 2` |
| `the_same_claim_string_is_free_in_a_different_repo` | workspace | `conflict: symbol:Config … held by ticket 5` |
| `the_whole_tree_claim_conflicts_with_an_open_claim` | m1 | created the ticket — the claim blocked nobody |
| `the_whole_tree_covers_a_file_no_other_claim_names` | m1 | `denied: src/sky/box.cs not covered` — the gate refused the whole tree's own holder |
| `the_whole_tree_is_claimable_when_nothing_else_holds_anything` | m1 | `conflict: subtree: overlaps subtree:` — HEAD's phantom WHOLE ticket blocked it |
| `an_empty_path_claim_is_refused_rather_than_created` | m1 | created a ticket claiming nothing |

Three more are **VACUOUS by construction** and kept deliberately, in the style of Phase 0c's
`an_explicit_root_beats_the_inherited_env`: `the_same_claim_string_in_the_SAME_repo_is_still_refused`,
`a_symbol_claim_can_be_held_in_one_repo` and `a_disjoint_directory_in_the_renamed_repository_is_still_free`.
Each asserts that a verdict HEAD *already* reaches is still reached, so no code state makes them
red — they are regression guards against the deletion this phase could plausibly be "simplified"
into, not evidence of the fix. **A refusal that HEAD already makes cannot be proven red. Say so
rather than reporting the verdict as proof.**

**A trap in proving any of this, worth one paragraph because it produced a false VACUOUS.**
Every check here is *"HEAD permits something it should refuse"* — so against HEAD the earlier
ones SUCCEED, and each success leaves a claim behind that refuses the next. The first attempt put
the `claim-extend` case third; HEAD's own defect had already created ticket 3 over that folder, so
the extend was refused, the check passed, and `dev prove` reported VACUOUS for a check that
demonstrably works. **The order of the three drift checks is load-bearing**, the claim-extend case
goes first, and no two of them name paths that contain one another (`a.cs`/`b.cs` are siblings,
`src/three` is elsewhere). The suite says so in a comment; re-prove all three if they are
reordered.

**Becomes impossible:** two tickets holding one folder under two spellings of its repository's
name; a claim that reads "the whole tree" and blocks nobody; a gate that denies the holder of a
whole-tree claim every file in it; a `path:`/`symbol:` claim stored with an empty value.

**Not done, deliberately:** in a MULTI-repo workspace `--claim subtree:/` is refused by
`ForClaims` as "in no repository", which is correct (a ticket lands in one repo, and the whole
workspace is not one) but the refusal does not name the alternative spelling `subtree:<repo>`.
`Claims.Spec` at least makes it read as `subtree:/ (the whole tree)` rather than a truncated
line. Improving the message is a Phase 3-or-later nicety on a path that already refuses.

**Must not break:** `m1:77-82` (`overlap_refused_at_plan_time`, `disjoint_parallel`),
`m1:194-195`, all of `ClaimsTests` and `ReposIdentityTests`, `workspace`'s per-repo token block
and Phase 0's repo-identity block. All green: `unit` 123/0 (was 88), `m1` 43/0 (was 39), `workspace` 90/0 (was 86).

---

## Phase 0c — Dodona stops inventing workspaces  *(BUILT, 2026-08-19, branch `loc-p0c`)*

**Operator, 2026-08-19: creating a workspace is a user action. Dodona must never do it on its
own.** It used to.

`Ws()` uses `--workspace` when given, otherwise `WorkspaceResolve.ForPath(reg, root)` where
`root = Environment.CurrentDirectory` ([Program.cs:1262](../src/Dodona/Program.cs#L1262)) — and
`ForPath` **creates a workspace, attaches the folder, and may move a legacy store**
([WorkspaceResolve.cs:42](../src/Dodona/WorkspaceResolve.cs#L42)).

The operator's own input never has this problem — the box knows its workspace. The gap is an
**agent inside a lane** running a `dodona` command: the lane's environment carries
`DODONA_SHIM_INFO` and `DODONA_LANE_ROLE` ([Daemon.cs:1797-1801](../src/Dodona/Daemon.cs#L1797))
but **not the workspace id**, so the CLI falls back to guessing from a folder.

Every citation above was checked against the source before it was changed and all of them held,
including that `DODONA_WORKSPACE` appeared **nowhere** in `src/`, `tests/` or `tools/`.

| item | what | built |
|---|---|---|
| P0c.1 | `psi.Environment["DODONA_WORKSPACE"] = _instanceId` at the spawn site. | yes — `AttachShimAsync`, beside `DODONA_SHIM_INFO`/`DODONA_LANE_ROLE`. The shim does not touch its child's environment, so it reaches the agent. |
| P0c.2 | `Ws()` honours it **before** ever looking at a folder. Order: `--workspace` → env → path. | yes, **with the middle pair swapped** — see below. |
| P0c.3 | Path resolution **creates only when the path was explicit** (`--root` typed by a person). An inherited cwd that nothing owns is a loud refusal, never a creation. | yes — `PathSource.Explicit` / `.Inherited`, carried out of `ParseArgs` and passed into `ForPath`. |

**The order as built is `--workspace` → explicit `--root` → `DODONA_WORKSPACE` → inherited cwd,
and the deviation from P0c.2's wording is deliberate** (see D-L9 below for the decision). "Path"
in P0c.2 meant *guessing from a folder*; a typed `--root` is not a guess. Putting the environment
ahead of it would have been an environment variable silently overruling a typed argument — the
exact compiles-clean/acts-on-the-wrong-workspace failure this phase exists to remove — and it
would break every acceptance suite the moment one was run from inside a lane, because they all
pass `--root` while their workspaces live in an isolated `DODONA_HOME` that the inherited id
knows nothing about.

Two shapes worth copying:

- **The refusal names the commands that un-stick it** (CLAUDE.md §0.1): the workspaces that do
  exist, `workspace-create --name <NAME> --member "<path>"`, and `--root "<path>"` with the note
  that an explicit path *does* create. It refuses; it never waits for anybody.
- **A stale `DODONA_WORKSPACE` is announced and then ignored**, not obeyed and not fatal. It
  happens for real (the workspace was forgotten; `DODONA_HOME` moved) and a silent degrade is a
  bug (CLAUDE.md §3's dead routing ladder), while hard-failing on a leftover variable would
  strand a lane for nothing.

**Becomes impossible:** a phantom workspace named after whatever folder a process happened to be
in; a store relocated because an agent ran `dodona tickets`.

Demonstrated, all `dev prove`-d **PROVEN** against HEAD:

| check | suite | what HEAD did instead |
|---|---|---|
| `inherited_cwd_creates_no_workspace` | workspace | `workspaces 6 → 7` |
| `inherited_cwd_does_not_move_a_legacy_store` | workspace | moved `.dodona\store.db` out of the folder |
| `the_refusal_names_the_command_that_makes_a_workspace` | workspace | printed a migration notice instead of a refusal |
| `env_workspace_is_used_before_any_folder` | workspace | resolved `dodona-stray-…` instead of the named workspace |
| `a_stale_env_workspace_is_announced_and_still_creates_nothing` | workspace | created, then complained the daemon was down |
| `a_lane_agent_is_told_its_workspace` | m0 | the agent reported `(unset)` |

`an_explicit_root_beats_the_inherited_env` (workspace) is **VACUOUS by construction** and kept
anyway: it pins the precedence above, which the fix does not change, so no code state makes it go
red. It exists so a later reordering cannot pass quietly.

`a_lane_agent_is_told_its_workspace` was additionally seen red *for the right reason* by deleting
only the one `psi.Environment` line, rebuilding, and re-running m0: 1 of 26 failed, with the
detail `agent reported: … result (unset)`. `DodonaFakeAgent` gained an `env:NAME` directive to
make that possible — before it, a spawn-site environment was observable only by reading the code
that set it.

**Must not break:** `workspace:304-317` (the legacy-store migration set) — auto-create on an
**explicit** `--root` is what makes that migration invisible and must survive. **It does, and it
is exercising the explicit route:** `Get-WorkspacePaths` in `tests/_workspace.ps1` calls
`dodona where --root $root --json`. All four migration checks stay green.

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
| P1.5 | **`dev test <suite>` does not build, so it silently tests the PREVIOUS binary.** Found 2026-08-19 while proving Phase 0c: the one `psi.Environment` line was deleted, `dev test m0` was run, and it reported **26 checks, 0 failed** — a clean green against a defect that was present in the source. `dev build` then `dev test m0` reported 1 failed. The cause is structural: `Use-TestBinaries` copies out of `src\...\bin\Release\...` ([tests/_workspace.ps1:130](../tests/_workspace.ps1#L130)) and **only `prove` builds** — `suites` has no build step either, and `gate`’s two builds are `dev build` runs in two *detached worktrees of HEAD* (the I2 row), not a build of the tree being gated, so **`dev gate` also gates stale binaries**. This is a false-green generator for **every** lane in this wave, not a Phase 0c problem. **Do not "fix" it by building inside `dev test`** — `dev test unit` is 1–2 s by the operator's explicit requirement (CLAUDE.md §1) and a ~6 s incremental build would end that. The shape that fits is a REFUSAL: `dev test` compares the build output against the tracked sources it is about to test and, when the output is older, names `dev build` and stops. Deliberately *not* done from a phase lane: `tools/dev.ps1` is the one file all three concurrent lanes invoke, and this belongs with whoever owns the tooling. **DONE 2026-08-19** — `StaleProjects` / `Assert-FreshBuild`, called from `Run-Suites` so `test`, `suites` and `gate` reach one verdict, plus a second call at the top of `Do-Gate` so the merge authority refuses in its first second rather than after its preamble. `prove` is exempt on purpose: it builds its own baseline. **Compared PER PROJECT**, each project’s sources against its own assembly — tree-wide is precisely the question auto-publish looped 64 times on (CLAUDE.md §2), and it is not hypothetical here: after touching only `src\DodonaUi\MainWindow.xaml.cs` and rebuilding, `Dodona.dll` is measurably **older** than that source (16:24:46.287 against 16:24:46.811), because MSBuild does not rewrite an untouched project’s output. A tree-wide check would refuse a correctly built tree. Sources are `.cs`/`.csproj`/`.xaml`/`.resx` plus `Dodona.sln` and `Directory.Build.props`; **not** `tests\*.ps1`, because a refusal that fired on every check edit would be routed around. Costs ~30 ms (refusal seen in 0.49 s wall); `dev test unit` stays 1.8–2.3 s. |
| P1.6 | **Done 2026-08-19 (Phase 0), recorded here because it is a `dev prove` fix, not a Phase 0 one.** `dev prove` built `Dodona.sln` in its HEAD worktree, which includes `Dodona.Tests` — and `prove` copies `tests\` from the WORKING tree over HEAD's `src\`. So a unit test naming a method the fix introduces cannot compile there, by construction, and the whole proof aborted with `HEAD does not build` plus nine `CS0117`s in a project no acceptance suite loads. Measured: 12 checks across 2 suites, **zero verdicts**, for a change whose acceptance checks were all provable. It now builds the four executables unless `unit` is itself among the suites being proved. The residual limit is inherent and the abort message states it: **a unit test for a NEW API can never be proven red**, so prove the acceptance check and say so in the report. |

**Becomes impossible:** landing any later phase unobserved.
### What Phase 1 measured, and what it corrected  *(landed 2026-08-19)*

Every citation in the table above was checked against the source before it was changed. Four
things turned out differently from what the audits said, and all four matter to Phase 2.

**1. `m3:186-187` does NOT pin the cwd rung ORDER.** The work order said the ticket-worktree
branch must keep winning over the recorded-cwd branch or those checks go red. Measured, by
reversing the order in `Daemon.ResolveLaneCwd` and rebuilding: **m3 stayed 31/31 green.** The
reason is that for a normally-spawned ticket lane the two rungs name the SAME folder --
`AttachShimAsync` recorded the worktree as `lanes.cwd` at spawn -- so the order only matters when
they disagree (a lane older than the column with an open ticket, or a worktree recreated after
the lane spawned). The M5.1 bug was `_primary` hardcoded with no recorded-cwd rung at all, and
the recorded-cwd rung alone would have fixed it.
`unit:A_ticket_worktree_wins_over_the_recorded_cwd` is now the only thing in the tree that pins
the order, which is precisely what P1.3 was for.

**2. `m3` does not cover the ticket-lane SPAWN path at all, only the respawn.** Measured, by
making `ticket-agent` spawn in `_primary` instead of the worktree ([Daemon.cs:709](../src/Dodona/Daemon.cs#L709))
and rebuilding: **m3 stayed green**, because it asserts on the `lane-respawn` event, and respawn
re-derives the worktree from the ticket regardless of where the first spawn went. Five of the new
`workspace` checks go red on it. So Phase 2 must not read a green m3 as evidence about a spawn
site.

**3. A lane with NO recorded cwd must say nothing, not `none`.** Found by m3 going red on real
output: the `DODONA` dispatcher lane is a UI row and nothing else, so it has no cwd. `Projects.Field`
returns null for an empty cwd -- `AttachShimAsync` writes the column before `Process.Start`, so an
empty one can only mean "never spawned".

**4. `dev prove` could not prove ANY of this, three separate ways** (all in `Do-Prove`, all fixed
or refused out loud in the same commit):
 * it compiled `tests\Dodona.Tests` into the HEAD baseline, so a unit test naming a symbol the
   change ADDS made prove abort with *"HEAD does not build"* -- and take every provable
   acceptance check in the same run down with it. Now it builds the four product projects only.
 * `Start-Suite 'unit'` would then abort with *"no suite 'unit'"*.
 * and `Run-Unit` builds and tests `$repo`, the WORKING TREE -- so a unit verdict would have been
   the change measured against itself. **`dev prove unit:<check>` now refuses**, naming the honest
   limit: a new pure function cannot be failed by a HEAD that does not contain it.

**5. `dev prove`'s worktree cache is SHARED BETWEEN LANES.** `$env:TEMP\dodona-prove\<commit12>`
is keyed on the commit alone, so three lanes at one HEAD share one worktree, one `tests\`
directory and one `stderr.tmp`. Observed: `workspace-acceptance` died on its FIRST command with
*"the process cannot access the file ... stderr.tmp"*, and prove reported eleven perfectly good
checks as MISSING. This is `f9aaf25`'s two-lanes-one-tree failure reappearing inside the tool
that verifies. A private `TEMP` is the workaround; the fix is P1.7, **which is now done** — see its row below.
**A SECOND residual, found 2026-08-19 by Phase 3 and NOT benign: the tree is keyed on
`(owner, commit)`, so ONE lane running TWO proofs concurrently collides with itself.** Observed
directly — a `dev prove workspace:...` was still running when a `dev prove concierge ...` started
from the same worktree at the same commit, and the second printed
`Copy-Item ... stderr.tmp ... IOException` before reaching its verdict. This time the verdict
survived (PROVEN, with real red output), which is exactly why it is dangerous: it is the P1.7
failure mode wearing a different hat, and a suite that dies on its first command reports its
checks as MISSING rather than as a collision. The fix is the same shape as P1.7's — the key needs
a third component that distinguishes concurrent proofs from the same tree (a pid, or a run id) —
and it belongs with whoever owns `tools/dev.ps1`, not with a phase lane. **Until then: do not run
two `dev prove` commands from one worktree at once**; the grouped form
`dev prove <suite>:<check> ...` already batches every check of one suite into a single run, so
the only way to hit this is to launch two suites' proofs separately.

One further residual, benign and recorded so it is not re-diagnosed: two lanes starting a proof at the same
moment can both prune the *same* orphaned tree and both report having done it. The remove is
`-ErrorAction SilentlyContinue` and the next run re-prunes whatever is left, so the worst case is a
duplicated log line, never a lost tree.

| item | what |
|---|---|
| P1.7 | `dev prove`'s worktree must be per-LANE, not per-commit (finding 5 above). **Not the same defect as P1.6**, which was prove's build TARGET and is done; this is prove's working DIRECTORY and is open. Two lanes proving at one HEAD corrupt each other's run, and the failure looks like a crashed suite rather than a collision. **DONE 2026-08-19** — the tree is now `<TreeKey($repo)>-<commit12>`: the commit still names its CONTENT, the caller’s own path now names its OWNER (`TreeKey` = 6 hex of SHA-256 over the canonical lowercased path, short because %TEMP% + MAX_PATH). Both P7.4 properties survive — still reused across proofs at one commit, still pruned. **The prune is owner-aware**, which is the part that could have re-created the bug: it removes all of *mine* at other commits and anything whose owning worktree `git worktree list` no longer knows, but **leaves a live lane’s tree alone**, because deleting another lane’s tree mid-proof is the collision this removes. Legacy bare-`<commit12>` directories have no owner prefix and are reclaimed by the same rule, so the old naming self-heals. Verified with a **genuine concurrent test**: two `dev prove workspace discovers_repos` runs launched simultaneously from two real worktrees at one commit (`0b8bb69`) built and ran in `2a8b08-…` and `278b4c-…`, both reached a real verdict (VACUOUS, correct for a pre-existing check), nothing on stderr, no MISSING, no `stderr.tmp` collision. Then: a second proof from A printed *"left another lane’s prove tree alone: 278b4c-…"*, and after B’s worktree was removed the next proof printed *"pruned a prove tree with no live owner: 278b4c-…"*. |
| P1.8 | **`dev lint` (I8) does not catch non-ASCII in a BOM-less `.ps1`** — found 2026-08-19 while patching `tools/dev.ps1`, which itself held two em dashes in comments. This is CLAUDE.md §0.2's *first* trap: in a BOM-less file the byte is read as ANSI and a pattern built from it matches nothing, which is a silent no-op rather than an error. The lint reads every tracked `.ps1` byte-by-byte already (control bytes, mixed line endings), so the rule costs nothing to add — the work is the seven tracked files that would go red: `spikes/spike1.ps1`, `spikes/spike2/spike2.ps1`, `spikes/spike3/hook.ps1`, `spikes/spike3/spike3.ps1`, `tests/_workspace.ps1`, `tests/concierge-acceptance.ps1` and `tools/dev.ps1` (the last one is fixed). BOM-ful suites are fine and must stay exempt: eleven of them carry non-ASCII legitimately. **Not done here** — it touches six files owned by other lanes’ work, and a lint change that reddens `dev gate` for everybody mid-wave is the wrong thing to land beside a tooling fix. |

**Enforcement that replaced a check.** P1.4 began as an acceptance check that `registry.db` is
under `DODONA_HOME`. Proving it red required breaking `Paths.Registry` -- and the concierge suite
then promptly wrote three workspaces (`harbour`, `lighthouse` + the alias `rotation`, and `work`)
into the operator's REAL registry, and two unrelated concierge checks went red because the
group-scope ladder was resolving against the operator's real workspaces. A check that reports
after the fact is not enough for machine-wide state, so `Assert-IsolatedRegistry` in
`tests/_workspace.ps1` now **refuses to run any suite** whose registry is not under
`DODONA_HOME`. Called from `Use-TestBinaries`, so all twelve suites get it with no per-suite edit.
Re-verified with the same break: the suite aborts in 2.1 s and writes nothing.


---

## Phase 2 — a lane opens in a project  *(LANDED 2026-08-19, branch `loc-p2`)*

**What landed, and the four things this section got wrong.** Read these before Phase 3, 4 or 5:
they are corrections to *this document*, established by experiment rather than by reading.

1. **`m3:186-187` never pinned the cwd rung order** (found by Phase 1, confirmed here). Reversing
   the rungs left m3 green, because for a normally-spawned ticket lane both rungs name the same
   folder. This section's claim that a red there "is a correctness incident" was untestable.
   `LaneCwdPrecedenceTests` on the `unit` loop is what pins the order now.
2. **`m3` does not cover the ticket-lane SPAWN path at all, only respawn.** So a green m3 is not
   evidence about any spawn site, and every check for this phase went into
   `workspace-acceptance`'s two-project fixture (P1.1) instead. 17 acceptance checks + 11 unit
   tests, all proved.
3. **T2 was wider than stated.** The section named the plain-lane path; `ticket-agent` read
   `_config` the same way, so a GATED lane in repo B also ran with the first project's permission
   mode. Both go through `Config.For` now.
4. **T4 needed a CLI change, not only a daemon one.** `workspace-detach`/`-move` are registry
   writes performed in `Program.cs`; the daemon could not observe them at all. It is told
   (`project-gone`) **only if it is already live** — summoning one would start the four warm-up
   haiku processes as a side effect of a registry edit, which is CLAUDE.md §3.2's incident again.

**T7 IS NOW REAL, AND IT IS THE COST OF THIS PHASE.** A plain lane is completely ungated
(`GateHook` returns 0 with no `--ticket`), and `lane-start --project <B>` puts such an agent into
a second repository — one with its own `main` and its own merge token. That is an **expansion of
the ungated surface**, not a regression, and it is the reason P2.1 refuses a folder no project
owns rather than substituting one: "ungated" must at least mean "ungated somewhere this
workspace knows about". Nothing in this phase narrows it. Gating plain lanes, or giving each one
a worktree of its own, is unbuilt work (`M5-DELIVERY-PLAN.md` §4).

**What is enforced rather than instructed.** T1 was the most dangerous item and "one parameter,
used twice" is an instruction, so it became a check: `AttachShimAsync` compares the folder named
in the `--append-system-prompt` against the `ProcessStartInfo`'s working directory at **every**
spawn in the product, and refuses the spawn on a mismatch (`shim_spawn_refused`). It can only
fire on a code defect, so it fires in a suite and never on the operator's machine.

**What did NOT change, deliberately.** Typed input still spawns in the first project
(`SpawnForAsync`) — choosing a project from a sentence is Phase 3's four rungs, and Phase 3
changes that one line. `NeutralCwd()` for brains and routers is untouched (T3). A one-project
workspace is byte-for-byte identical: `TryProject(null)` is the first project, `Config.For` of the
first project IS `_config`, and the claim-check base list reduces to the two bases it always had.

---

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
| P2.1 | A project is resolved at the spawn site and **must be a registered project or inside one** — refuse anything else, loudly. **DONE** — `Daemon.TryProject`: nothing requested is the first project (byte-identical), a requested folder resolves UP to its owning project through `Projects.Of`, and anything else is a refusal naming the projects it does know plus the `workspace-attach` that would add the one it does not. `dodona lane-start --project <path>`. |
| P2.2 | `SpawnAgentLaneAsync`, `lane-start` and `LaneSystemPrompt` take **one** project value (T1). **DONE, AND ENFORCED RATHER THAN INSTRUCTED** — `SpawnAgentLaneAsync(title, project, …)` has no default and uses `project` three times (config, prompt, working directory), but "one parameter, used twice" is an instruction, and this trap has already survived one. So `Projects.DirSentence` WRITES the prompt's folder sentence and `Projects.Named` READS it back, and `AttachShimAsync` — the single funnel every spawn in the product passes through — compares the named folder against the `ProcessStartInfo`'s working directory and REFUSES on a mismatch (`shim_spawn_refused`). Unreachable by configuration, so it can only fire on a code defect. |
| P2.3 | Lane config from `Config.For(project)` (T2). **DONE, and wider than written** — `ClaudeArgs` is now `internal static` and takes a `Config`, so it is on the `unit` loop; `ConfigForProject` feeds the plain-lane spawn AND the respawn, and `ticket-agent` (which had the identical defect, unmentioned above) now uses `ConfigFor(t.Repo)`. New `lane_config` event: which project configured a lane and what mode it got — previously unanswerable from outside the process. |
| P2.4 | `repo-init` / `repo-status` take a project (T5). **DONE** — `--project` is an assertion (validated, refused if unowned, because `git init` in the wrong folder is not undoable), and the client's `cwd` is a hint (an agent in project B means B; an unowned cwd falls back to the first project, which is what these always did, and both commands print the path they acted on). |
| P2.5 | Fix the claim-check bases (T6). **DONE** — worktree first (with the ticket's recorded claim prefix), then every REPOSITORY longest-first with its own `ClaimPrefix`, then every PROJECT unprefixed. The last rung is only there to keep the one-project message identical; it cannot produce a false *cover* in a multi-project workspace, because the bare relative form it yields can match only an unprefixed claim, and `Repos.Discover` prefixes every repo name the moment a second project is attached. |
| P2.6 | `workspace-detach` / `-move` reconcile lanes in the departing project — stop or re-home them (T4). **DONE, and it needed a CLI change too** — those are registry writes in `Program.cs`, invisible to the daemon; `TellIfLive` sends `project-gone` **only when a daemon is already up** (summoning one would start the four warm-up haiku processes as a side effect of a registry edit). The daemon stops those agents over their own shim pipes and marks the lanes `unreachable`; rows and transcripts stay (§12). `lane-respawn` then refuses a folder no project owns and names `lane-respawn <lane> --project <project>`, which re-homes it. A TICKET lane cannot be re-homed: its gate is deployed into its worktree. |
| P2.7 | **OPEN, found while landing P2.6.** `workspace-forget` deletes every `members` row too, and it is **not** wired to `project-gone` — so forgetting a workspace with a live daemon leaves its agents running in folders the registry no longer records, and that daemon's `Members()` silently degrades to `_primary` alone (the documented fallback for an unopenable registry, which is a different situation). Deliberately not done here: `forget` also orphans the DAEMON, not only the lanes, so the honest fix is "stop the daemon and its lanes, keep the store" — a lifecycle decision that belongs with Phase 5's reaping (P5.5 already owns "reap a brain whose project is no longer attached") rather than bolted onto a detach path. |

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
  **RENAMED to `a_lane_needs_no_repository`**, which is what its assertion always actually
  tested — the name was the wrong half, not the check. `DEBUGGING.md`'s matching bullet is
  corrected too; `Paths.cs` and `WORKSPACES-CONCIERGE.md` §93 still say "workspace-wide" and are
  left alone on purpose, because there they mean *the STORE is per workspace*, which is unchanged.
- `m3:186-187` survives **only if the ticket-worktree branch still wins** over the project
  branch. A red here is a correctness incident, not a test problem. **But do not read its
  green as evidence:** Phase 1 measured that it stays green both with the rung order
  reversed AND with `ticket-agent` spawning in the first project -- it covers the RESPAWN
  path only. The checks that cover the spawn sites are the new `workspace` ones and
  `unit:A_ticket_worktree_wins_over_the_recorded_cwd`.

---

## Phase 3 — the router's memory, and the four rungs

**Extend what exists.** `members` already *is* every project ever attached, and `registry.db` is
already WAL + `busy_timeout` and built for several writers
([Workspaces.cs:66](../src/Dodona/Workspaces.cs#L66)). Missing: **spoken-name handles per
project** (`aliases` maps alias → *workspace* today, [Workspaces.cs:102](../src/Dodona/Workspaces.cs#L102))
and **recency** for ordering. No new parallel table — fewer owned things is this project's whole
failure mode. The concierge stays the registry's sole writer; a daemon that learns a project
tells it.

**What Phase 2 left you, so you do not rebuild it.** The spawn side is finished and takes a
project already: `SpawnAgentLaneAsync(title, project, model, effort)`, validated by
`Daemon.TryProject` (which is also what you must call on whatever a rung decides — do not resolve
a folder yourself, and do not substitute one when a rung is wrong). **The one line this phase
changes is in `SpawnForAsync`**, which still passes the first project and says so in a comment:
that is the whole of "typed input has no project in it yet". Rung 2 reads the distinct projects of
live lanes — `Projects.Of(ProjectPaths(), l.Cwd)` over `Store.LanesAll()`, with liveness from
`LaneLiveness` and never one instantaneous pipe read (D-L6). Rung 4 must write **no lane row**
before the project is known; `brain:220 held_input_invents_no_lane` and
`workspace:a_refused_lane_leaves_no_row_behind` are the two checks that catch it if it does.

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

**Handed to Phase 3 by Phase 5 (deliberately NOT numbered — two lanes have already collided on
one P-number, so this is prose for whoever owns Phase 3 to number as they see fit).** A brain is
now per project, and `EnsureBrainAsync` / `AskBrainHiAsync` take an optional project which
defaults to the workspace's first — byte-for-byte today's behaviour. **Two call sites inside
`RouteInput` still take that default**, and they are the two Phase 3 owns: the escalation to the
expensive tier ([Daemon.cs:3393](../src/Dodona/Daemon.cs#L3393)) and whatever rung 2/3 ends up
asking. Once a sentence has been resolved to a project, pass it — a classifier reasoning about
project B's lanes while the answer comes from project A's manager is the cross-project confusion
this phase removed everywhere it could reach. `BrainReview` already does this: it resolves the
reviewed lane's own registration and asks that project's brain.

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


### Delivered 2026-08-19, branch `loc-p3` — and five corrections to the text above

Every citation above was checked against the source before it was changed. What the phase
actually became, rung by rung:

| rung | decided by | where |
|---|---|---|
| **only** — one project | code, and it is checked FIRST, before the liveness read, the registry read and any model | `ProjectLadder.Decide` |
| **named** — the sentence says where: exact leaf → taught handle → the leaf said as words (`project zed` → `project-zed`) | code, free | `ProjectLadder.NameMatch` |
| **live** — a project holds a live lane: free when exactly ONE does (`how=sole-live`), one cheap call when several (`how=classified`) | code, then the warm router | `Projects.Live` + `Daemon.ClassifyProjectAsync` |
| **ask** — nothing to go on, or the classifier would not choose | code: HOLD the sentence, no lane row | `Daemon.SpawnForAsync` |

Rung 1 (a comment for an existing lane) was already built and is untouched: by the time any
of this runs, the four-verdict lane ladder has already said "this is new work".

**`SpawnForAsync` was the one line, as Phase 2 promised, and the answer goes through
`TryProject` before a lane row exists** — belt and braces on purpose: every candidate came out
of `members`, so a refusal there can only mean the project was detached between the ladder's
read and the spawn, which is trap T4 arriving on the typed-input path (`project_gone_at_spawn`).

**1. `named` runs BEFORE the classifier, and the operator's 2-then-3 ordering is preserved in
substance rather than in sequence.** This is the same rule the concierge's own ladder already
applies one level up (rung 0/1 before rung 2 fuzzy): *explicit information never triggers a
search*. The two rungs agree wherever both have an answer; where they disagree the named one is
right, and asking the model first would let "fix `<B>`'s header" open a lane in A because A
happens to be busy — a confident wrong answer, made instantly, to a question the operator had
already answered for free. Pinned by
`unit:A_named_project_is_not_overruled_by_a_busy_one` and, at the spawn site, by
`workspace:a_named_project_is_not_overruled_by_a_busy_one` — which asserts the whole claim as one
thing: the lane landed in the taught project **and** `classified_project` did not move.

**2. Recency is derived from the store's lane rows, NOT from a `members.last_used_ts`
column.** D-L5 asked for "recency for ordering" and the obvious shape was a registry column —
rejected, because only a daemon knows a project was used, so that column needs a SECOND writer
into the machine-wide registry the concierge is meant to own, i.e. a cross-process channel and
a fact that then exists in two places and can disagree. `lanes.cwd` + `lanes.id` already record
it, workspace-locally and transactionally, in the store the daemon owns outright
(`Daemon.ProjectsByRecency`). So the registry change is **one nullable column**:
`aliases.member_key` (**registry schema 2**), which is the half `members` genuinely could not
carry — what the operator *calls* a project. Written by `dodona project-alias <name> --member
<path>`, an operator-explicit registry edit in the CLI exactly like `workspace-create`; the
daemon only ever READS it.

**3. Rung 2's liveness is a pure function with three inputs, because no suite can construct the
window D-L6 is about.** `Projects.Live(projects, lanes, byPipe, byRecord, byConnection)` — the
pipe namespace, a recorded shim pid that is a live `DodonaShim`, and a runtime this daemon is
holding an open handle to. A lane counts if ANY of them says yes. The measured blink (8 reads of
192 over 1.5 s with the shim alive and instantly connectable, synchronised with a daemon
restart) cannot be reproduced from a `.ps1` — it would have to hold the OS pipe namespace
still — so the part that can be got wrong, *which answers count*, was lifted onto the ~1 second
`unit` loop where narrowing it back to one read is a red check rather than a plausible tidy-up
(`unit:A_lane_alive_only_by_its_shim_record_still_counts` and its two siblings). Stated plainly
so it is not mistaken for full coverage: **the acceptance suites prove the union is WIRED, the
unit checks prove which answers it accepts, and neither reproduces the blink itself.**

**4. The four booby-trapped concierge checks, each by name.**

- **`concierge:226-228`, the loop over literal rung names** (`only, registry, path, fuzzy,
  discovery, ask`) — **untouched, all six still asserted, no rung renamed.** `discovery` is now
  produced by the `look` affordance instead of automatically, so the fixture that feeds the loop
  moved but the loop did not.
- **`concierge:152` `fence_never_reaches_outside_itself`** — it asserted only `rung -eq 'ask'`,
  and with the fence demoted **that would have gone green having tested nothing**: every
  unresolved sentence reaches `ask` now, fence or no fence. Decided and stated: it is **rewritten
  to be strictly stronger** — the sentence asks, the answer `look` explicitly runs the fence, and
  the check demands the look came back empty, named no `atlantis`, and left the question open. It
  now proves the fence *ran* and still did not reach outside itself, which the old form never did.
- **`concierge:107/111`** (`explicit_path_attaches_outright` / `explicit_path_reuses_its_workspace`)
  — **still green and still meaningful.** The memory added here records nothing on first sight:
  `aliases.member_key` is written only by an explicit `project-alias`, and recency is derived
  rather than stored, so `WorkspaceResolve.ForPath` and the meaning of `created` are unchanged.
- **`concierge:167-172`** (`answer_teaches_an_alias`, `rung_4_decays_to_rung_1`) — **still green.**
  `AnswerAsync` gained one branch in front (`look`); the plain-name branch, `Teach`, and the
  `registry` rung it decays to are byte-for-byte as they were.
- **`concierge:80-127`** (the fixed workspace-creation order, `fuzzy_match_on_the_cheap_tier`
  indexing `$names[1]`) — **nothing was inserted before it.** The new checks all sit in the
  rung-3 block, which is after it, and the one workspace they create (`bay`, via `look`) is
  forgotten again in the same block, as the old fixture did.

**5. What the plan text got wrong or left implicit.**

- `Concierge.cs:512` is `Ask`, as stated, and it is where rung 4 lands at group scope. **The
  daemon's project rung 4 does NOT open a `questions` row, and that is a deliberate deviation
  with a reason** — see P3.1 below. It holds the sentence in the daemon's own established
  hold-and-ask shape (`routing_decisions` tier `ask` + a `project_unknown` event + an announce
  naming every candidate and the command that un-sticks it), which is the same shape the lane
  ladder's own top rung uses and is what keeps `held_input_invents_no_lane`'s guarantee true one
  level down.
- **The warm router is now asked two different questions**, so `RouterPrompt` names both, each by
  the first line it arrives with (`Daemon.ProjectQuestionLead`). Without that a cheap model told
  "reply with ONLY this JSON" answers in the four-verdict schema whatever it is asked. A second
  router lane would have been a second `claude -p` per workspace for one extra sentence of prompt.
- **`Concierge.Mentions` moved into `ProjectLadder.Mentions`** rather than being copied. Both
  ladders ask "did the operator say this name", and two implementations of that drift the moment
  one learns something.
- The fake agent's project directive is **`routeproject:N`, an INDEX**, for the reason `cxpick:N`
  exists one level up: a project NAME written into a test sentence is matched in code by `named`
  before any model is asked, so a rung-2 check written with a name passes at rung 3 having never
  reached the tier — proving the opposite of what it claims.

| item | what | built |
|---|---|---|
| P3.1 | Rung 4 produces a question. **Deviation:** it holds and announces in the WORKSPACE, and does not write a concierge `questions` row. Three reasons: the daemon has no coupling to the concierge at all today and every suite (and a sleeping-concierge machine) runs daemons without one; *"no workspace daemon ever reads its store"* plus §2's authority cap deliberately keep per-workspace work-routing state out of that store; and `questions` has **no column saying which workspace or which scope a question belongs to**, so a project question cannot be told from a group question in it. Adding one is a **row-shape decision, which Phase 4 owns** (P4.1 wants one component over one source) — flagged here rather than made from this lane. | yes, as described |
| P3.2 | `ProjectLadder` — the four rungs, pure, on the `unit` loop. | yes |
| P3.3 | Memory: `aliases.member_key` (registry schema 2), `Registry.ProjectHandles` / `AddProjectAlias`, `dodona project-alias`. Recency derived from lane rows (correction 2). | yes |
| P3.4 | `Projects.Live` — rung 2's evidence as the union of three liveness answers (D-L6). | yes |
| P3.5 | The fence demoted below the ask (D-L3): `Concierge.LookAsync`, reached only by answering an open question with `look`. `Fence.cs` unchanged and not deleted. | yes |

**Becomes impossible:** a typed sentence opening a lane in the first project because nothing
knew which project it meant; a project name in the sentence being overruled by whichever
project happens to be busy; a rung-2 answer resting on one instantaneous pipe read; the
discovery fence spending the expensive tier on a sentence nobody asked it to search for.


**`dev prove` verdicts — 21 PROVEN, 2 VACUOUS-by-construction, both declared.** Grouped form,
one run per suite, every red read back from the real output:

| check | suite | what HEAD did instead |
|---|---|---|
| `a_typed_sentence_with_no_project_to_infer_is_held` | workspace | `-> HEADER (started on opus/high (default))` — it spawned |
| `a_held_sentence_invents_no_lane` | workspace | `before=0 after=1` |
| `the_project_hold_offers_every_project_it_knows` | workspace | no `project_unknown` row at all |
| `a_typed_sentence_naming_a_project_opens_a_lane_there` | workspace | `cwd=…dodona-proja-… want=…dodona-projb-…` |
| `a_new_task_joins_the_only_project_with_a_live_lane` | workspace | the same, project A |
| `the_lane_opens_in_the_project_the_classifier_chose` | workspace | `lanes=3->4 cwd=…proja… want=…projb…` |
| `several_live_projects_reach_the_cheap_tier` | workspace | `classified_project events=0` |
| `the_classified_rung_records_that_a_model_answered` | workspace | no `project_chosen` row |
| `a_classifier_that_will_not_choose_holds_the_sentence` | workspace | `-> COMPLETELY (new task, started on opus/high)` |
| `an_unchosen_project_invents_no_lane` | workspace | `before=4 after=5` |
| `a_project_can_be_taught_a_spoken_handle` | workspace | `unknown command: project-alias` |
| `a_handle_for_a_folder_that_is_not_a_project_is_refused` | workspace | the same |
| `a_project_handle_is_stored_against_the_project_not_only_the_workspace` | workspace | `(aliases has no member_key column: registry schema is pre-2) cols=alias,display,workspace_id,created_ts` |
| `a_taught_handle_opens_a_lane_in_its_project` | workspace | `lanes=5->6 cwd=…proja… want=…projb…` |
| `the_alias_rung_records_that_evidence` | workspace | no `project_chosen` row |
| `a_named_project_is_not_overruled_by_a_busy_one` | workspace | `cwd=…proja… want=…projb… classified_project went 0 -> 0` |
| `the_project_ladder_is_live_on_the_path_the_operator_uses` | workspace | `lanes=6->7 cwd=…proja… want=…projb…` |
| `the_fence_never_runs_unbidden` | concierge | `{"rung":"discovery","workspace":"bay-8f06","created":true}` — it went looking |
| `looking_on_request_finds_a_folder_in_the_fence` | concierge | `concierge-answer: a numeric argument was expected` |
| `looking_closes_the_question_it_answered` | concierge | the 'bay wall' question was never opened, so nothing to close |
| `fence_never_reaches_outside_itself` (rewritten) | concierge | `error: no workspace "look"` |
| `a_look_that_found_nothing_leaves_the_question_open` | concierge | the same |

**VACUOUS, declared rather than dressed up as proof** — both pin a property no code state can
currently redden, kept for the reason `an_explicit_root_beats_the_inherited_env` is (Phase 0c):

- `naming_a_project_costs_no_model` — HEAD writes no `classified_project` row at all, so "the
  count is zero" cannot fail against it. Its provable sibling is
  `a_named_project_is_not_overruled_by_a_busy_one`, which asserts the same freeness *alongside* a
  destination HEAD gets wrong.
- The `unit` checks: **`dev prove unit:` refuses on purpose** and the refusal is right — a HEAD
  that does not contain `ProjectLadder` cannot fail a test of it. The 19 unit checks are pinned
  by the 22 acceptance PROVENs above, not by themselves, and that is stated rather than implied.

**Suite tallies, this tree, after the fix:** `unit` 152/0 · `workspace` 133/0 (63.7 s) ·
`concierge` 48/0 · `brain` 45/0 · `m2` 12/0. `dev gate` and `dev suites` deliberately NOT run:
two sibling lanes were working and nine concurrent suites is the contention that reddens
`ui-use`. **One measurement worth carrying:** `workspace` ran 202.5 s while a `dev prove` was
running beside it and 63.7 s alone — the same 3x contention effect `dev gate` prints its
leaked-process count for.

**A rule for anyone writing a project check, learned from four VACUOUS verdicts in one run:
NEVER ASSERT THE FIRST PROJECT.** `Members[0]` is `_primary`, which is what every spawn site
answered before this wave — so "the lane landed in project A" is an assertion no build can fail,
and `dev prove` correctly called four checks vacuous for it (`the_lane_opens_in_the_project_the_
classifier_chose`, `a_taught_handle_opens_a_lane_in_its_project`, `the_project_ladder_is_live_on_
the_path_the_operator_uses`, `a_named_project_is_not_overruled_by_a_busy_one`). Every one of them
names **B** now. Two related shapes, both real:

- **A cwd-only assertion goes green on the HELD case.** If a rung holds the sentence, no lane is
  created and "the newest work lane" is the PREVIOUS one — which, in a section that keeps
  choosing B, is also in B. So the lane COUNT belongs in the assertion, not only in the
  `Wait-Until` above it.
- **"No event was written" is unprovable on its own** when HEAD writes that event kind never.
  `a_named_project_is_not_overruled_by_a_busy_one` asserted only that `classified_project` did
  not move; HEAD has no such row at all, so it passed having tested nothing. It now asserts the
  whole claim in one check — the name won, *and* it cost no model call.

**A trap worth the next lane's attention, because it cost a full 200-second proof run.**
`Invoke-StoreSql` THROWS on a sqlite error (deliberately — a hand-rolled version that swallowed
stderr is how a check naming a non-existent column passed against every build ever made). So a
check that names a column your migration ADDS **kills the suite** against HEAD: `dev prove`
reported all twelve of this phase's `workspace` checks as MISSING with *"NO TALLY: the suite
never reported"*, which reads as a crash rather than as "the schema is not there yet". A check
must be able to fail against HEAD without taking the suite down with it — so the query is
`pragma_table_info`-guarded and the absent-column case becomes the check's own FAIL detail.

**Left undone, named so it is not re-discovered:** the one-project workspace is proved
byte-for-byte identical by construction (`Decide` returns before every read, and
`project_chosen` is not written) and by the eleven suites that run one-project workspaces
staying green — but there is no check that *counts* the events a one-project workspace writes,
so a future rung added ahead of the `only` short-circuit would not be caught by name. That is a
one-line check somebody should add.

---

### P3.A delivered, branch `loc-p3a` (2026-08-19) — and the three items closed with it

**P3.A: rung 4 asked nobody, for two days, and every check passed the whole time.** Phase 3
wrote a `routing_decisions` row at tier `ask`, a `project_unknown` event and an announcement;
Phase 4 built an overlay that renders a `questions` row. Nothing connected them. The fix is the
two parts Phase 4 specified, plus the one thing neither had needed yet:

- **Part 1, at the hold site** (`Daemon.SpawnForAsync`, one call): `AskWhichProject` opens a
  `questions` row with `kind='route'`, `subject` = **the held sentence, whole**, and candidates
  built by `Ask.RouteCandidates`. Idempotent on (kind, subject) for `AskForRepo`'s reason. The
  row goes in the **workspace** store — D-L11 stands untouched, and no scope column was added
  anywhere, because scope is which store the row is in.
- **Part 2, in `Daemon.AnswerQuestion`**: `Ask.KindRoute` and a `case` that delivers
  `q.Subject` through `SpawnForAsync`, which gained ONE optional parameter (`answeredProject`).
  That parameter is the only input that skips the ladder, and it still goes through
  `TryProject`, so `held_input_invents_no_lane` holds and the lane is created **by answering**
  and at no earlier moment. `AnswerQuestion` became `async` (its single caller already was).
- **The candidates are NAMES, never paths** (CLAUDE.md §3.1, and the operator's directive):
  `Ask.RouteCandidates` takes leaves, and `ProjectLadder.ByName` resolves an answer back over
  a closed list — the same closed-list match `ClassifyProjectAsync` already made on the cheap
  tier's reply, whose inline copy was **deleted in favour of the shared one**. `Ask.cs` is
  linked into `DodonaUi` and `ProjectLadder.cs` is not, which is why the names cross that
  boundary rather than the resolver.

**Two orderings are load-bearing, and both are one line each.**

- **A route answer is resolved BEFORE the row is closed.** `QuestionAnswer` is guarded on
  `state='open'`, so there is no re-opening a question — and a route question closed without
  delivering loses the held sentence for good. A project detached between the ask and the answer
  therefore prints what un-sticks it, writes `question_answer_refused`, and leaves the row open.
- **A route question has no declination.** `declined` was `picked.Value == "no"` for every kind;
  a project in a folder called `no` would have had a perfectly good answer recorded as
  `withdrawn` and its sentence silently never delivered.

**The escalation now asks the focused lane's own project's manager.** Phase 5 handed this over as
prose: the lane ladder's escalation inside `RouteInput` passed the DEFAULT project while the fact
sheet it sends describes the **focused** lane and its siblings. It passes
`RegistrationKey(focusedRow, ProjectPaths())` now, following `BrainReview`'s shape — and that
returns `""` for a lane no project owns, which `BrainProject` turns back into the first project,
so a one-project workspace is unchanged. Proved red: `brain-hi projects=[…proja…]` against work
living in B.

**The one-project event count exists now, as a WHITELIST rather than a total** — a plain count
would go red for a reason nobody could read.
`workspace:a_one_project_workspace_writes_no_project_ladder_event` demands that the event kinds
written while a one-project workspace routes one typed sentence are exactly
`lane_auto_created, lane_config, lane_connected, lane_started, policy_choice, say, shim_spawned`,
and names anything else in its own detail string. Demonstrated red by adding a rung ahead of the
`only` short-circuit: *`outside the allowed set=[project_guessed,question_opened]`* — which is
the "caught by name" the note above asked for. Its sibling
`a_one_project_workspace_opens_no_question` covers the other half.

**`ProjectPaths()`'s one registry read per typed sentence is still invisible, and deliberately
so.** It writes no event and prints nothing, so an event whitelist cannot see it; the honest
record stays the code comment in `ResolveProjectAsync` that admits it, rather than a claim of
byte-for-byte identity that is not quite true.

**`dev prove`: 19 PROVEN, 6 VACUOUS-by-construction — and every one of the six was demonstrated
red by breaking the behaviour.** The reds that mattered most, read back from the real output:

| check | suite | what HEAD did instead |
|---|---|---|
| `the_project_hold_opens_a_question_row` | workspace | `questions=` — no row at all |
| `the_question_offers_every_project_by_name` | workspace | `ids=[] blob=` |
| `a_route_answer_naming_nothing_offered_is_refused` | workspace | `error: no question -1` |
| `answering_the_project_question_opens_the_lane_there` | workspace | `error: no question -1 lanes=6->6` |
| `the_answered_rung_records_that_the_operator_decided` | workspace | `rung=named how=leaf` (the previous decision) |
| `the_escalation_asks_the_focused_lanes_own_projects_manager` | brain | `brain-hi projects=[…proja…] want B=…projb…` |
| `the_routing_question_reaches_the_operators_window` | ui-use | `ask` was null, forever |
| `the_ui_answer_verb_reaches_a_routing_question` | ui-use | `error: nothing is being asked` |
| `a_project_choice_is_a_real_button_a_person_can_click` | ui-use | `no automation element named ask:<projb>` |
| `answering_in_the_window_delivers_the_held_sentence_to_the_chosen_project` | ui-use | `lanes=` — nothing was ever created |

The six VACUOUS ones are negative pins and one precondition; each was demonstrated by a
deliberate break, rebuilt, read red, and reverted:

- `a_one_project_workspace_writes_no_project_ladder_event` / `a_one_project_workspace_opens_no_question`
  / `opening_a_question_still_invents_no_lane` — a rung ahead of the short-circuit plus a lane
  spawned at the hold: `outside the allowed set=[project_guessed,question_opened]`,
  `1|route|open|make the header quite a lot taller`, `before=0 after=1`.
- `an_ambiguous_sentence_is_held_rather_than_placed` / `a_rendered_routing_question_still_invents_no_lane`
  — the bottom rung made to return the first project: `-> HEADER (started on opus/high (default))`
  and `1|HEADER|work|…proja…`.
- `the_routing_overlay_closes_when_its_row_closes` — `Shell.OpenAsk` made unconditional (Phase 4's
  own technique): `{"id":99,…,"question":"DELIBERATE BREAK","shown":true,…}`.

**`ui-use` now covers the gap Phase 4 named.** A real two-project workspace, an ambiguous
sentence, the overlay rendering the router's own question, `dodona ui answer <project>` through
`MainWindow.AnswerAsk` — the button's own method — and the held sentence arriving in a lane in
the project that was picked. Nothing in that section fakes a row.

**Becomes impossible:** a rung called `ask` that asks nobody; a held sentence that can only be
released by retyping it; a routing question offering somewhere to browse; an expensive-tier
manager reasoning about another project's lanes; a rung inserted ahead of the one-project
short-circuit passing unnoticed.

**Left undone, deliberately:** a route question is not withdrawn if the same sentence is later
delivered another way (by naming a project and retyping). It stays open, answerable, and
answering it delivers that sentence again — which is what the operator asked for both times.
Visible and bounded, so it is recorded rather than guessed at. Also still open from Phase 4: the
group-scope question has no acceptance check, because producing a real concierge question needs
the concierge's model tiers and the suite that has that fixture has no window.

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

### What Phase 4 built  *(2026-08-19, branch `loc-p4`)*

| item | built |
|---|---|
| P4.1 | `Dodona.Ask` — the pure half: `Choices(candidatesJson)`, `Match`, `IsFreeForm`, `RepoInitCandidates`. Linked into `DodonaUi.csproj` as SOURCE, so the daemon that WRITES `candidates` and the window that PARSES it cannot drift. **One row SHAPE, two stores** — see the correction below; "one source" as written is not achievable and should not be. |
| P4.2 | **Live:** the ask overlay in `MainWindow.xaml`, last in the Grid so it renders above the transcript overlay. **Headless:** `ui dump`'s new `ask` key — `{id, scope, scopeLabel, question, shown, dismissed, choices[]}`, `null` when nothing is being asked. Two deterministic poses, `ask` and `ask-group`. |
| P4.3 | `MainWindow.AnswerAsk(choice)` is the one answer path: the button's `Click` handler and the new `dodona ui answer <choice>` verb both call it, and it sends the SAME `{cmd:"answer", id, answer}` the CLI's `dodona answer` / `dodona concierge-answer` sends. Pipe-addressed by the question's scope. |
| P4.4 | Two negative checks, both **demonstrated red** by making the ask unconditional (below). |
| P4.5 | `ticket-create`'s no-repo refusal opens a question instead of printing `run dodona repo-init`. Answering `yes` runs `RepoInitOp` — the method the `repo-init` command was refactored to call, so there is not a second `git init` behind the overlay. |

### P4.1's "one source" is wrong, and must be corrected rather than diverged from

**The wording:** *"One `Ask` component with one source: the open `questions` row."* Taken
literally that is unachievable, and achieving it would be a mistake. Phase 3's **D-L11** settled
the half that forced the issue: the daemon has **zero coupling to the concierge** and must keep
it — a workspace daemon may never read the concierge's store (§2's hard rule, the thing that
stops the concierge becoming the one queue §12 designed out), and every suite plus any machine
whose concierge is asleep runs daemons without one. A routing question that needed a live
concierge would be undeliverable in precisely the cases routing matters.

**The correct wording, as built: one row SHAPE, one answer path, two stores normalised at the
edge.** Of D-L4's three invariants, the load-bearing one is **one answer path**, and it is
intact: `MainWindow.AnswerAsk` is the only thing that answers anything, for either store.
"One component, two render modes" is about live-versus-headless — the reason a modal was rejected
is testability, not source count — and that also holds.

**Scope is WHICH STORE the row is in.** That is the answer to "`questions` has no column for
scope": it does not need one, and **no scope or workspace column was added to the concierge's
table**. Group-scope questions stay in `ConciergeStore.questions`, written only by the concierge;
project-scope questions live in the workspace's own store, written only by its daemon. Neither
reads the other, `Shell.OpenAsk` reads both and prefers the focused workspace's, and the answer
goes to whichever control pipe owns the row.

**Two `questions` tables, one row shape — and NO schema bump.** The concierge's table already
existed; the workspace store now carries one with the same seven columns plus `kind`/`subject`
(what answering *does*, and to what — the UI never reads them). It is created by an unconditional
`CREATE TABLE IF NOT EXISTS` rather than a versioned block, deliberately: `Ver.Schema` exists for
exactly one purpose, the daemon's refusal to hot-swap DOWN across it, and a purely additive table
no older binary names gives that refusal nothing to protect. Bumping would have spent a version
number in a wave where **Phase 5 is already assigned v10** — the collision P5.1's own note warns
about. If an integrator wants it versioned, it is a two-line change.

**A question is one at a time, and the focused workspace's beats the concierge's.** A stack of
overlays is a queue of modals. A BANDED workspace's question is deliberately not shown — the band
already carries its badge, and a decision about a workspace you are not looking at must not land
on top of the one you are.

**Escape puts it down; the row stays open.** View state (`MainVm.DismissAsk`), keyed by question
id so the next question still appears. An overlay that cannot be put down is a modal in all but
name, and §0.1's "never hung, halted, stuck" applies to the operator's screen too. `ui key escape`
lands in `Window_KeyDown`'s own body via `EscapePressed()`.

**20 of 24 named checks `dev prove`-d PROVEN against HEAD**, including
`the_dump_reports_an_ask_field_at_all`, `the_refusal_opens_a_question_row`,
`a_choice_is_a_real_button_a_person_can_click` (UIA finds a Button named `ask:yes`),
`the_ui_answer_verb_reaches_the_ask`, `answering_yes_actually_creates_the_repo` and
`the_ticket_the_question_was_blocking_now_works`.

The rest are **negative pins**, VACUOUS by construction and kept for that reason —
`ticket_create_without_a_repo_still_refuses` (a must-not-break), plus
`a_one_project_workspace_is_never_asked_anything` and `a_bare_launch_is_never_asked_anything`,
which were instead **demonstrated red by breaking the behaviour**: `Shell.OpenAsk` was made to
return a question unconditionally, rebuilt, and `ui-use` went to 14 failed with
`{"id":99,…,"question":"DELIBERATE BREAK"}` in both details. `the_overlay_closes_when_the_row_closes`
and `the_ask_offers_no_filesystem_navigation` went red in the same run — the second one only after
it was rewritten, because an EMPTY choice list contains no paths either and the first draft was
green against a build with no ask at all.

**What Phase 4 could NOT test, and why.** Two gaps, both named rather than papered over — a
component that renders one source while claiming to render both is worse than one that renders one
and says so.

- **Rung 4 is not rendered yet, and it is one call away.** Phase 3's rung 4 records a
  `routing_decisions` row at tier `ask` plus a `project_unknown` event and an announce; it does
  **not** open a question row, so the overlay never sees it. The overlay needs nothing new: the
  workspace `questions` table is concierge-free by construction, and `kind`/`subject` exist for
  exactly this. See **P3.A** below for the two-part hook. A fixture faking a rung-4 question was
  deliberately not written — a green over a row no code path produces proves nothing.
- **The group-scope source is rendered and posed (`ask-group`), but has no acceptance check.**
  `ConciergeReader.OpenQuestions` reads it and `Ask.Choices` parses the concierge's own
  `[{id,name}]` shape (pinned by `unit:The_concierges_candidate_shape_parses_too`), but producing a
  real concierge question inside `ui-use` needs the concierge's model tiers, which that suite runs
  without. The suite that HAS that fixture is `concierge`, which has no window.

| item | what | for whom |
|---|---|---|
| P3.A **(BUILT, branch `loc-p3a` — see Phase 3's P3.A section)** | **Rung 4 opens a question row.** Part 1, at the hold site: alongside the `routing_decisions` row, `_store.QuestionOpen(<the held sentence>, <candidate projects as `[{id,name,why}]`>, kind: "route", subject: <the held input>)`. The overlay then renders it with no further change. Part 2, in `Daemon.AnswerQuestion`: a new `Ask.KindRoute` constant beside `Ask.KindRepoInit` (deliberately NOT added by Phase 4 — an unused constant reads as support that is not there) and a `case` for it that delivers the held sentence to a lane in the chosen project — that is `SpawnForAsync`, which is Phase 3's, which is why this is filed here and not built in Phase 4. `Ask.RepoInitCandidates` is the shape to copy. | Phase 3 |

**Becomes impossible:** a refusal that tells a GUI user to go and type a command; a question that
evaporates when the window closes; an ask with a second answer path behind it; a question rendered
as a modal, which is to say a question that cannot be tested at all.

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
| P5.1 | A `lanes.project` column (**schema v10** — Phase 0 spent 9 on the repo-identity migration; this row said v9 and two changes would have collided on one version number). Note the daemon **refuses to hot-swap down** across a schema version ([Daemon.cs:1249](../src/Dodona/Daemon.cs#L1249)), so this blocks rollback; take the pre-migration backup seriously. |
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

**Becomes impossible:** a healthy brain shut down because another project has one; a brain
answering questions about a project the workspace no longer has; one wedged utility agent
blocking judgement for every project at once; N projects quietly becoming 2N model-backed
processes with nothing capping them.

### Delivered 2026-08-19 (branch `loc-p5`), and what verifying it corrected

**Both of the plan's claims about the code were verified against the source, and one of them
needed restating.**

- **`_shutdownAsked` really is never cleared.** Two references in the whole tree — one `Add`,
  one declaration. No `Remove`, no `Clear`. It is now `_shutdownAttempts`, a bounded three
  pokes per lane per daemon, *and* scoped by (role, project): the first fixes "a shim that
  would have accepted a second `##shutdown` is never asked again", the second fixes "one
  wedged brain refuses to let a brain be created for every project".
- **"`EnsureBrainAsync` for project A can silently return project B's session" is a claim about
  the code AFTER a project parameter exists, and the row should have said so.** Before this
  phase `EnsureBrainAsync(bool hi)` took no project at all, so there was exactly one brain per
  role per workspace and no cross-project confusion was reachable. What was verified is the
  mechanism the row is really about, and it is worse than a wrong return: **reconcile's
  adoption loop assigned `_brainLo = l.Id` unconditionally for every brain row it adopted**, so
  with two brains the scalar held whichever was iterated last — and the retirement loop's
  `keep` was that same single value, so the other healthy session was shut down and announced
  as a repair. The scalar was not a latent risk; it was the live mechanism of the bug.

**The registration, in one line:** `lanes.project` (v10) is the row that says a manager should
exist for (role, project). Reconcile reaps a management lane whose project is no longer
attached, and a **second claimant on one slot** — never the Nth brain. Compressors are exempt
from the duplicate rule because a pool is *meant* to have several per project (§5).

**Two safety properties that are easy to lose and were the most dangerous part of the phase:**

- **The reaper only runs on a membership list the registry actually gave us**
  (`Daemon.TrustedProjects`). `Members()` degrades to `{_primary}` when the registry cannot be
  opened — deliberately — and fed to a reaper that reads as *every project but the first is
  gone*, i.e. every brain outside the first project killed. That is this phase's own bug in the
  costume of its fix. A forgotten workspace lands here too (`ById` returns null), which is
  correct: forget is handled explicitly by P2.7 below, where the intent is known.
- **`RegistrationKey` never answers `""` for a manager.** An empty registration reads as
  unregistered, which the reaper acts on — so a v10 stamp that failed quietly (locked store,
  store copied out of a suite) would kill a healthy brain. A manager with no stamp resolves to
  the first project, which is exactly what "the brain" meant before this phase.

**The v10 migration** adds one `COLLATE NOCASE` column and takes the pre-migration backup the
`Store` constructor already writes (`store.db.pre-v9`), announced with its path — the daemon
refuses to hot-swap DOWN across a schema version, so this is a one-way door unless the operator
knows where the key is. The stamping pass follows P0.3's shape: a work lane is stamped from its
cwd (a fact), a MANAGEMENT lane is stamped to the first project and that is **announced as an
assumption** (`lane_project_assumed`), and anything unresolvable is reported and left blank
(`lane_project_unresolved`) rather than guessed.

**P5.7's cap** is `maxBrains` in `dodona.json`, default **6** — three projects fully warm, or
six on the cheap tier, and deliberately out of reach of a one-project workspace (which can want
at most 2). It **refuses and never evicts**: making room by shutting a brain down is the
count-and-kill loop growing back somewhere else. The refusal is announced once per daemon and
names the setting, because a project with no judgement is a degrade and a silent degrade is a
bug.

**P5.8 held.** Brains keep `NeutralCwd()`; `key` is passed as a separate `scope` argument, and
the comment at the spawn site says the two are two arguments because they are two facts.

| check | suite | what HEAD did instead |
|---|---|---|
| `two_projects_get_two_distinct_brains` | brain | one brain for the workspace; the second request returned the first |
| `a_brains_project_is_recorded_on_its_lane_row` | brain | `no such column: project` |
| `status_names_the_project_a_brain_is_scoped_to` | brain | no `scope=` field exists |
| `restart_adopts_a_brain_for_every_project` | brain | one brain adopted, the other retired |
| `no_healthy_brain_is_retired_as_a_surplus` | brain | a `brain_surplus_retired` row |
| `reconcile_lists_a_brain_per_project` | brain | `brain=<one id>` |
| `a_detached_projects_brain_is_reaped` | brain | the brain stayed alive, scoped to a project that had left |
| `a_wedged_brain_in_one_project_does_not_block_another` | brain | no brain for the other project at all |
| `the_brain_cap_refuses_a_new_project` | brain | no cap exists |
| `forgetting_a_workspace_stops_its_agents` | workspace | the shim was still alive |
| `forgetting_a_workspace_stops_its_orphaned_daemon` | workspace | the daemon was still running |

**Two checks came back VACUOUS and were REWRITTEN rather than shrugged at**, and both were
vacuous the same way — the defect is unreachable at HEAD, so the assertion was true for the
wrong reason:

- `restart_adopts_a_brain_for_every_project` compared `before` against `after`. Against a build
  with one brain per WORKSPACE both lists are `[1]`, they match, and it passes having asserted
  nothing. `$after.Count -eq 2` is now part of the assertion.
- `no_healthy_brain_is_retired_as_a_surplus` asserted zero `brain_surplus_retired` rows. A build
  that never makes two brains never makes a surplus either. It now also requires both brains to
  still be alive.

Five are **negative guards, VACUOUS by construction** and kept for the reason Phase 0c kept
`an_explicit_root_beats_the_inherited_env`: `a_one_project_workspace_says_nothing_about_scope`,
`a_brain_in_a_project_that_stayed_is_untouched`, `the_wedged_brain_was_never_called_gone`,
`the_brain_cap_never_evicts_an_existing_brain`, and
`a_forgotten_workspaces_transcripts_survive`. Each is the only line in the tree that would
notice a reaper going too wide, a cap that evicts, or a one-project workspace starting to print
a new field. `dev prove unit:` refuses by design (P1.6), so `Projects.ScopeField`'s six unit
checks are stated as unprovable rather than reported as verdicts.

**Both guards were then seen RED by deliberate break, because "no code state makes it fail" is
not the same as "it works"** (CLAUDE.md §0.3: a new check is worth nothing until it has been seen
red). Two one-line breaks, each built and run, then reverted:

| break | brain suite | what it proves |
|---|---|---|
| the reap slot keyed on `role` only (`$"{l.Role}\|DELIBERATE-BREAK"`) — the deleted loop's own rule | **6 failed**, `restart_adopts_a_brain_for_every_project: before=[1,2] after=[1]`, project B's row `dead` | the (role, project) key is what stops a healthy brain being killed. This is the original bug, reproduced on demand. |
| the not-attached test forced true (`Projects.Of(livePro, key) is null` → `true`) | **12 failed**, including `a_brain_in_a_project_that_stayed_is_untouched` and `restart_adopts_the_brain_it_already_had: before=[1] after=[]` in the ONE-project workspace | a reaper that widens until everything matches kills every brain everywhere, and the negative guards catch it. |

The second break also earned a suite fix on its own: `the_wedged_brain_is_provably_alive` reported
`<could not hold pipe 'dodona-…-lane-1': The operation has timed out.>` as a **failure detail**
where it would previously have thrown out of a `finally`-only `try`, printed no tally, and made
`dev prove` report every check in the file as MISSING. That happened for real on the first proof
of this phase — twenty MISSING verdicts for a suite that had simply died in a probe.

**Checks that had DEGRADED, each fixed rather than relaxed** — the point of the plan's warning
was that all three would have kept passing:

- `brain:333`'s `LIMIT 1` now selects the brain **for this project**. With N it picked an
  arbitrary row and the whole wedge/refusal/never-called-gone block became a green about
  whichever one sqlite happened to return first.
- `brain:256-258`'s `brain=\d+` now matches `brains=[<leaf>=<lane>]` for the named project. The
  event key was renamed *so that the old pattern cannot match*, which is what turns a silent
  degrade into a red.
- `brain:58-59, 246-248, 250-253`'s `COUNT(*) = 1` are per-project counts. The value is
  unchanged in a one-project workspace; the QUESTION changed, and it stays meaningful with N.

**A fixture bug this cost, worth knowing before adding v11.** `workspace-acceptance`'s pre-v9
section stands the store back up in the v8 shape and sets `PRAGMA user_version = 8` — but it did
not drop v10's column, so v10's `ADD COLUMN` failed with "duplicate column" **inside the `Store`
constructor**. The daemon died before opening its control pipe and **four checks about repo
identity went red pointing at nothing that was broken**. The drop is now keyed on the column
existing rather than on a version number, so it survives v11 and still works under `dev prove`
against a build that has no v10.

### P2.7, handed over by Phase 2 and delivered here

`workspace-forget` deletes every `members` row in one transaction and was wired to nothing, so
forgetting a live workspace stranded agents in folders the registry no longer records — the same
trap-T4 state Phase 2 closed for `detach`. It also **orphans the daemon**, which is why it
belonged with this phase's reaping: `publish --all` resolves swap targets by id *from the
registry*, so a daemon whose workspace has been forgotten can never be hot-swapped again. It
becomes an un-updatable process holding agents nothing lists.

`workspace-forget` now sends `workspace-forgotten` through the same `TellIfLive` path detach
uses — **never summoning** (a summoned daemon's warm-up is four real haiku processes, and a
registry edit that starts them is the §3.2 incident in a new costume) and **never failing** (the
edit has already succeeded and is what the operator asked for). The daemon stops every agent,
retires the managers, marks work lanes `unreachable` with their transcripts intact (§12), and
then stops itself. It is reversible and says so: forget keeps the store directory, so
re-creating the workspace brings all of it back.

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
  **Settled while building Phase 3:** the handle half is `aliases.member_key` (registry schema 2),
  written only by an operator-explicit `dodona project-alias`; the **recency half is derived from
  the store's own lane rows, not stored in the registry at all**. Rejected: `members.last_used_ts`
  — only a daemon knows a project was used, so it needs a second writer into a machine-wide table
  the concierge owns, and `lanes.cwd` + `lanes.id` already record the fact workspace-locally and
  transactionally.
- **D-L6. Liveness is `LaneLiveness`, never one instantaneous pipe read.** Already one near-miss.
- **D-L7. A project is a location — a folder.** It may be a repo, hold one, hold several, or
  hold none. Merging serialises per **repo**; everything operator-facing is per **project**.
- **D-L8. Track brains, do not count them.** *(operator, 2026-08-19)* Validity is a
  registration keyed by (role, project). Rejected: keeping the count-and-kill loop — it cannot
  tell "five brains because five projects" from "five brains because of a bug", and it fails as
  healing.
- **D-L9. Dodona never creates a workspace on its own.** *(operator, 2026-08-19)* Only an
  explicit `--root` or an operator action creates. An inherited cwd refuses.
  **Resolution order, settled while building Phase 0c:** `--workspace` → explicit `--root` →
  `DODONA_WORKSPACE` → an inherited cwd that is *already owned* → refuse. Rejected: putting
  `DODONA_WORKSPACE` ahead of `--root`, which is how P0c.2 was originally worded — an environment
  variable that overrules a typed argument compiles clean and acts on the wrong workspace, and it
  would break every suite run from inside a lane (they pass `--root`; their workspaces live in an
  isolated `DODONA_HOME`). Also rejected: making a stale `DODONA_WORKSPACE` fatal — it strands a
  lane over a leftover variable; it is announced and stepped past instead.
- **D-L10. A project NAMED in the sentence is decided in code, before the classifier is asked.**
  *(settled while building Phase 3)* The operator's precedence list reads 2-then-3 (an open lane's
  project, then a remembered one), and this keeps it in substance while inverting the sequence: it
  is the same rule the concierge's ladder already applies one level up — *explicit information
  never triggers a search*. The two rungs agree wherever both have an answer; where they disagree
  the named one is right. Rejected: consulting the cheap tier first, which lets "fix `<B>`'s
  header" open a lane in A because A happens to be busy — a confident wrong answer, made
  instantly, to a question the operator had already answered for free.
- **D-L11. The router's project rung 4 holds in the WORKSPACE and does not open a concierge
  `questions` row.** *(settled while building Phase 3; Phase 4 may revisit)* Three reasons: the
  daemon has no coupling to the concierge at all and every suite — and any machine whose concierge
  is asleep — runs daemons without one; *"no workspace daemon ever reads its store"* plus §2's
  authority cap deliberately keep per-workspace work-routing state out of that store; and
  `questions` has no column saying which workspace or which scope a question belongs to, so a
  project question cannot be told from a group question in it. Adding one is a row-shape decision
  and P4.1 (*one component, one source*) owns it. Rejected: writing project questions into the
  concierge store from the daemon, and rejected: a second answer path in `Answer` that Phase 4
  would have to undo.
