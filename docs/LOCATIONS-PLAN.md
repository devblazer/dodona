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
One residual, benign and recorded so it is not re-diagnosed: two lanes starting a proof at the same
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
  **Resolution order, settled while building Phase 0c:** `--workspace` → explicit `--root` →
  `DODONA_WORKSPACE` → an inherited cwd that is *already owned* → refuse. Rejected: putting
  `DODONA_WORKSPACE` ahead of `--root`, which is how P0c.2 was originally worded — an environment
  variable that overrules a typed argument compiles clean and acts on the wrong workspace, and it
  would break every suite run from inside a lane (they pass `--root`; their workspaces live in an
  isolated `DODONA_HOME`). Also rejected: making a stale `DODONA_WORKSPACE` fatal — it strands a
  lane over a leftover variable; it is announced and stepped past instead.
