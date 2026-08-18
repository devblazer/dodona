# Recovery — the phases

Derived from [INVESTIGATION-2026-08-18.md](INVESTIGATION-2026-08-18.md). **This is a work
order, not a design document**, and it has an expiry: P5.5 deletes it. Everything in it that
survives does so as code, a deletion, or a check — because a design document and a new tool
are the two things this project reaches for when stuck, and both have already failed here
(CLAUDE.md §0.3).

Every phase states what becomes **impossible**, not what becomes better. A phase that cannot
name something it makes inexpressible is not finished.

---

## 0. The organizing idea

Every failure in the investigation was **two actors touching one thing that nobody owns**:

| shared thing | who collides | cost |
|---|---|---|
| `src\*\bin\Release\` | compiler ↔ suites ↔ autostarted daemons ↔ live app | the hour of 2026-08-18 |
| the checkout | agent ↔ agent | `f9aaf25` carrying another lane's work |
| `obj/` | agent ↔ `m4`'s internal build ↔ the autoPublish daemon | unattributable build failures |
| the process table | daemon ↔ orphan ↔ `stop-all` | 10 live agents nothing can stop |
| `tests/*-output/` in git | every suite run ↔ every diff | `git status` at 48:1 noise |
| the machine-wide registry | one agent's cleanup ↔ another's work | `dev build` kills your daemons |
| CLAUDE.md | every session ↔ nothing that verifies | a rule falsified 62 minutes after writing |

So: **the repository stops being a workspace and becomes a source of truth. Nothing builds in
it. Nothing runs from it. Nothing writes to it but commits.** Every actor — each agent, each
suite run, the publisher, the live app — gets its own tree and its own bin. What remains
shared is `main`, and `main` already has merge tokens, which work.

---

## 1. The invariants — this program's acceptance criteria

Each is enforced, not documented. The enforcement is the deliverable; the sentence is a label.

| # | Invariant | Enforced by |
|---|---|---|
| I1 | Nothing executes from a build output | suites run from `$DODONA_HOME\bin`; `Autostart` refuses a `\bin\` image |
| I2 | Every actor builds in a tree it owns | per-session worktrees; the publisher builds a detached worktree of `main` |
| I3 | Liveness is read from the OS, never from a file | `Instance.LivePipes()` — already the authority for daemons |
| I4 | Every process Dodona starts can die on its own | shim exits with its child; lane lease |
| I5 | The working tree contains only source | `tests/*-output/` untracked |
| I6 | Destructive commands reach only a named target | `stop-all` gets `publish`'s registry scoping |
| I7 | Verification costs seconds | condition-waits; parallel runner; unit project |
| I8 | Every rule that can be executed, is | repo lint; one executable suite list |

**I4 is the one that closes the orphan class permanently.** Today a shim can only be stopped
by the daemon that has already forgotten it. A lease means an orphan cannot survive its own
irrelevance, even when a daemon is killed with `-Force`.

---

## Phase 0 — Stop the bleeding

**Effort: ~30 minutes. No dependencies. Do this first, today.**

| item | what |
|---|---|
| P0.1 | Stop the 11 orphan shims and 10 live `claude.exe` agents **by pid, from the process tree** (parent = `DodonaShim`). Never by name (CLAUDE.md §4). |
| P0.2 | Delete `ClearBlockers`'s `& $dodona stop-all` call from `Do-Build` in [tools/dev.ps1](../tools/dev.ps1). See §5 D-4. |
| P0.3 | `git rm -r --cached tests/*-output/`; add `tests/*-output/` to `.gitignore`. 80 files, 1.94 MB, never read — all 11 suites wipe the directory at start. |
| P0.4 | Delete `git add -A` from `.claude/skills/ship/SKILL.md` step 3; use explicit pathspecs. |

**Becomes impossible:** `dev build` killing another agent's or the operator's daemons; a
`git status` you cannot read; the literal mechanism of `f9aaf25`.

**Verify (<30 s):**
```powershell
git status --porcelain                      # only real source changes
grep 'add -A' .claude/skills/ship/SKILL.md  # nothing
```

**Cost / breaks:** A reviewer loses screenshot diffs in commits — never a real workflow, since
the suites regenerate them locally in seconds. Explicit pathspecs mean a forgotten new file
surfaces at review rather than at commit.

---

## Phase 1 — Nothing runs from the build output  *(I1)*

**Effort: ~half a day. Depends on P0.3.**

| item | what |
|---|---|
| P1.1 | `Use-TestBinaries` in [tests/_workspace.ps1](../tests/_workspace.ps1) (already dot-sourced by all 11 suites): copy `src\*\bin\Release\` into `$env:DODONA_HOME\bin` once, return the path. Every suite's `$dodona`, `$fake` and `$env:DODONA_SHIM` point there. Fixes `m1`'s missing `DODONA_SHIM` for free — all three binaries land in one directory, the way a published build does. |
| P1.2 | `Autostart` and `Cx` refuse to daemonize when `Environment.ProcessPath` is under a `\bin\` directory, naming `publish` as the substitute. A build output is not an installation. |
| P1.3 | Post-suite assertion: no process may be running from `$repo\src\*\bin\`. Fails loudly in the suite that leaked it, naming the pid. |
| P1.4 | Repoint CLAUDE.md §2 and `/ship` step 4 at the published binary, not `.\src\Dodona\bin\Release\net8.0\dodona.exe`. |

**Becomes impossible:** **a blocked build.** This alone deletes the hour of 2026-08-18 and the
reason a second copy of the tree was ever made.

**Verify (<30 s):**
```powershell
powershell -File tools\dev.ps1 suites
powershell -File tools\dev.ps1 check     # must read: in the build output: nothing
```
Today, after a suite run, it prints three pids.

**Cost / breaks:** One ~10 MB directory copy per suite run, into a home the suite already
creates and deletes. P1.2 changes behaviour for anyone deliberately running a daemon from
`bin\Release` — the practice being removed.

**P1.1 and P1.2 must not be split across agents:** between them lies a window where the suites
still launch from `bin\Release` while `Autostart` refuses to. Every suite goes red.

---

## Phase 2 — Nothing builds in the shared checkout, and `main` is the only publish source  *(I2)*

**Effort: ~1–1.5 days. Depends on Phase 1 and P0.2.**

Two coupled changes: give every actor its own tree, and make the live instance's provenance a
commit instead of a timestamp.

### 2a — Per-session trees

| item | what |
|---|---|
| P2.1 | ~~CLAUDE.md §0 names worktrees the **only** supported way to run two sessions.~~ **SUPERSEDED BY §5 D-7**: naming it in prose is the shape D-6 forbids. Implemented as a tracked `pre-commit` hook that refuses commits from the shared checkout, plus `dev worktree <name>` and one deliberate override. A per-edit `PreToolUse` hook was tried and removed — 255 ms on every operation for a rule the tree separation already makes structural. |
| P2.2 | ~~`PreToolUse` hook denying `git add -A`, `git add .`, `git commit -a`.~~ **DROPPED, see §5 D-7**: with one tree per session a broad `git add` can only stage your own files, so the hook taxed every shell command (255 ms) to prevent something the separation already prevents. `/ship` uses explicit pathspecs (P0.4), and that remains the house rule. |

### 2b — `main` is the only thing that publishes itself

Today the drift watcher compares **file mtimes** — `Ver.NewestSource(project)` against
`Ver.ImageBuiltFrom(Ver.ExePath)`, every 15 s
([Daemon.cs:1391-1400](../src/Dodona/Daemon.cs#L1391-L1400)). Five separate guards exist only
to make that inexact comparison behave:

- a **debounce** (`stableSince`, `AutoPublishDebounceSec`), so a half-saved edit is not built
- a **`.built-from` stamp**, because `dodona.exe`'s mtime spans one project while the question
  spans three — that asymmetry **looped 64 times in an afternoon**
- **`kv.autopublish_last_tried`**, because an in-process guard is reset by the swap it triggers
- **`consecutiveFailures` / `surrendered`**, after 16 failed publishes buried the one that mattered
- a **30-minute dirty-tree nag**, because published-but-uncommitted nearly lost a feature

`git rev-parse main` against the SHA the running build was made from is **exact**: no clock, no
filesystem, no partial-write window, no project asymmetry. A commit is atomic and already
quiet, so the debounce is unnecessary. The SHA *is* the guard, so `lastTried` is unnecessary.

| item | what |
|---|---|
| P2.3 | The watcher compares `git rev-parse <main>` against the built SHA. On difference it builds from `git worktree add --detach <sha>` — its own tree, never the live one — then swaps through the existing M4 path. |
| P2.4 | **Delete** `Ver.NewestSource`, `Ver.WriteBuiltFrom`, `Ver.ImageBuiltFrom`, the `.built-from` file, `AutoPublishDebounceSec`, `kv.autopublish_last_tried`, `lastMax`, `stableSince`, `dirtySince`, `dirtyAnnounced`, and the 30-minute dirty nag. **Keep** `consecutiveFailures`/`surrendered` — a broken `main` must not rebuild forever. |
| P2.5 | `dodona publish --from <ref\|worktree>` for a deliberate trial. Stamped with non-main provenance; `status` reads `trial: <branch>@<sha>`; the next commit to `main` replaces it. |
| P2.6 | `status` and `version --json` report `build=<sha>`. Bisectable, revertible, comparable to `git log`. |

**Becomes impossible:** cross-session commit carry; `obj/` contention between agents and the
publisher; **publishing uncommitted work at all**; the entire mtime-comparison bug class
(the 64-iteration loop, the stamp workaround, published-not-committed).

**Verify (<30 s):**
```powershell
dodona status                     # build=<sha>, and `git log <sha>` knows it
# edit a source file, do not commit  -> nothing publishes, ever
# commit to main                     -> swap lands within one poll
git worktree list                 # one entry per live session + one for the publisher
```

**Cost / breaks:** Worktrees cost disk and a `dotnet restore` each. Trialling work in progress
is no longer automatic — it becomes `publish --from`, which is the decision recorded in §5 D-1
and is one line to flip. If `main` breaks, the app breaks: the guard already exists (publish
probes `version --json` before promoting, and the shortcut moves only after a daemon accepts
the build), and with `dev gate` required before commit this is **safer** than today.

---

## Phase 3 — Processes can die on their own  *(I3, I4, I6)*

**Effort: ~1 day. P3.1 → P3.2 → P3.3/P3.4/P3.5 is one causal unit, in that order, by one agent.**

| item | what |
|---|---|
| P3.1 | Lane liveness from `Instance.LivePipes()` ([Instance.cs:127](../src/Dodona/Instance.cs#L127)) in `ps` and `stop-all --lanes`. Shim-info is demoted to a pid lookup for killing — never the count. Today: 11 live lane pipes, 7 records, 4 agents nothing can see or stop. |
| P3.2 | The shim **exits when its child exits**, once the buffer is drained. The flag is already computed at [DodonaShim/Program.cs:71](../src/DodonaShim/Program.cs#L71) and then ignored. |
| P3.3 | **Lane lease.** No daemon contact for N minutes → the shim exits itself. This names the condition that un-sticks it rather than a person (CLAUDE.md §0.1), and closes the orphan class even when a daemon is killed with `-Force`. |
| P3.4 | Reconcile asks the OS before writing `utility_lane_reaped … "shim gone"` ([Daemon.cs:300](../src/Dodona/Daemon.cs#L300)). Delete `attempts: 1` ([Daemon.cs:257-258](../src/Dodona/Daemon.cs#L257-L258)): pipe absent → zero attempts (**faster** than today, which is what the 35-second reconcile hang actually wanted); pipe present → be patient, and if it will not converse, send `##shutdown` rather than abandoning it. |
| P3.5 | `EnsureBrainAsync`/`EnsureRouterAsync` ([Daemon.cs:1864-1901](../src/Dodona/Daemon.cs#L1864-L1901)) never spawn a replacement while the predecessor's pipe is live. Adoption failure must not be a spawn trigger. |
| P3.6 | `stop-all` gets `publish`'s scoping: `--workspace <name>…` / `--all` = every live workspace **in the registry**. It currently stops every unregistered `dodona-*-ctl` pipe on the machine ([Program.cs:528-536](../src/Dodona/Program.cs#L528-L536)), which includes another agent's `DODONA_HOME`-isolated suite daemons. |
| P3.7 | Checks in `m0` and `m4` for P3.1–P3.5, each `dev prove`d against `cd53389` **before** being believed. |

**Becomes impossible:** an orphan; an unkillable agent; a count that lies in either direction;
a cleanup that reaches someone else's work.

**Verify (<30 s):**
```powershell
$pipes = @([IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -match 'dodona-.*-lane\d+$' }).Count
$ps    = (dodona ps --json | ConvertFrom-Json | % { $_.lanes } | measure -Sum).Sum
"$pipes / $ps"                                   # must be equal — today 11 / 7
dodona stop-all --lanes
@([IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -match 'lane\d+$' }).Count   # must be 0
```

**Cost / breaks:** P3.2 means `lane-respawn` can no longer reattach to a shim whose agent
crashed — correct, since there is nothing to reattach to, but it changes `unreachable` handling
for work lanes and needs a check. P3.4 makes reconcile faster, not slower.

---

## Phase 4 — Verification costs seconds  *(I7)*

**Effort: ~1–2 days.** Order within the phase matters: de-sleep before parallel, and fix the
by-name queries before parallel.

Measured baseline: **5 min 16 s** for all 11 suites (343 checks, 0 failed), of which
**214.2 s (68 %) is fixed `Start-Sleep`**. CLAUDE.md §1's recorded "twenty minutes, of which
only 3.6 is sleep, so the rest is inherent and cannot be optimised away" is wrong by ~4× and
backwards in its conclusion — corrected in P5.4.

| item | what |
|---|---|
| P4.1 | `Wait-Until` (condition + deadline) in `_workspace.ps1`; convert the fixed sleeps. The pattern already exists twice in the tree: `m4:175-179` and `ui-use:519-527`. Targets in order: `ui-use` (88.7 s sleep of 103.7 s total — 22 s of it in two adjacent lines at `:274`/`:278`), `brain` (44.0 s), `compression` (16.6 s), `m0` (13.3 s), `m4` (17.8 s), `m3` (15.7 s). |
| P4.2 | Replace the three machine-wide by-**name** process queries — [m4:222](../tests/m4-acceptance.ps1#L222), [ui-use:523](../tests/ui-use-acceptance.ps1#L523), [ui-use:548](../tests/ui-use-acceptance.ps1#L548) — with resolution by pid/path. Same rule as CLAUDE.md §4. **These are the only known blocker to running suites concurrently.** |
| P4.3 | Parallel runner in `dev.ps1`. PS 5.1 has no `ForEach-Object -Parallel`, so: `Start-Process powershell -PassThru` per suite + `Wait-Process`, per-suite log files — already the shape of `Run-Suite`. `m4` runs alone until P2.3 moves its internal build out of the shared `obj/`. |
| P4.4 | `Run-Suite` **fails** on a missing tally line. Today `m0` prints six named checks and no `N checks, M failed`, so `dev.ps1` reports `no tally line` and **cannot detect an `m0` failure at all**. |
| P4.5 | `tests/Dodona.Tests` in the existing solution + `dev test unit`. Move the pure logic's verification there: `Claims` (53 lines of algebra with no I/O), `Instance.Canonical`, `Policy`, `Repos.ForClaims`, the routing-tier decision. (`Ver.NewestSource` is *deleted* by P2.4, so it is not on this list.) |

**Why parallelism is already safe:** `Use-IsolatedDodonaHome` gives each suite a GUID temp home
(registry, stores, shim-info, neutral cwd); `Instance.Scoped()`
([Instance.cs:94](../src/Dodona/Instance.cs#L94)) hashes `DODONA_HOME` into the concierge and
shell ids — added *because* a leaked concierge from `ui-use` once answered the concierge
suite's questions and failed 21 checks; roots are GUID temp dirs; all four UI launches in
`ui-use` carry `--test-window` (off-screen, no activation, no taskbar). P4.2 is what is left.

**Projected:**

| step | full gate | iterate (one suite) |
|---|---|---|
| today | 5 m 16 s | 8–104 s |
| after P4.1 | ~100–140 s | ~5–30 s |
| after P4.3 | **~35–45 s** | unchanged |
| after P4.5 | ~40 s | **<1 s** for pure logic |

**Becomes impossible:** skipping verification because it is expensive. This is the only durable
fix for "believing a green check" — make checking cheaper than guessing.

**Verify (<30 s):** `Measure-Command { powershell -File tools\dev.ps1 suites }` matches what
CLAUDE.md claims; `dotnet test -c Release` under 5 s covering `Claims.Covers`; delete `m0`'s
last check and confirm `dev test m0` exits non-zero.

**Cost / breaks:** Condition-waits can hang if the condition never arrives — hence *deadline*,
per CLAUDE.md §0.1. Parallel output interleaves; per-suite log files solve it. Some logic must
be extracted from `Daemon.cs` to be unit-reachable, which is real work and the reason P4.5 is
last.

---

## Phase 5 — Prose shrinks to what code cannot hold  *(I8)*

**Effort: ~half a day. Depends on P0.2, Phase 4, P5.1.**

| item | what |
|---|---|
| P5.1 | Repo lint (sub-second; belongs in P4.5's project): **(i)** no `.md` or `.ps1` may contain a control byte outside tab/CR/LF; **(ii)** every `tests\*.ps1` path named in any `.md` must exist on disk. (i) catches the literal `0x08` in `CLAUDE.md:253` and `SKILL.md:43` that makes `tests\brain-acceptance.ps1` unrunnable in both. |
| P5.2 | Delete the duplicated suite lists from CLAUDE.md §3 and SKILL.md §2; point both at `dev help` / `dev suites`. The list exists in three places and only the executed one is correct. |
| P5.3 | `/ship` becomes a thin wrapper over `dev ship` plus the commit step. CLAUDE.md §1 forbids `dotnet build`; `/ship` step 1 still mandates it — a contradiction created by `cd53389`, which changed the delivery path without touching the skill, breaking CLAUDE.md §5.1's own rule. |
| P5.4 | Correct CLAUDE.md §1's timing figure, and delete §5.2's lane-cwd claim — written by `ba555b5` at 18:11:55 and falsified by `f9aaf25` at 19:13:39, still asserted today. |
| P5.5 | Delete this document. Its content now lives in code, deletions and checks. |

**Becomes impossible:** a command in CLAUDE.md that does not run; two suite lists that disagree.

---

## 2. The standing gate — `dev gate`

One verb on the existing door, run before every commit. It **asserts** the invariants rather
than describing them. Every check is `dev prove`d against a build that lacks its fix before it
is believed — a check that has not been seen red is worth nothing, and that is the one thing
`cd53389` got exactly right.

| assertion | invariant | today |
|---|---|---|
| `dev check` → "in the build output: nothing" | I1 | 3 pids |
| `git status --porcelain` empty after a full suite run | I5 | 48 dirty |
| lane pipes == `ps` lanes | I3 | 11 vs 7 |
| two agents `dev build` concurrently, both succeed | I2 | **earned 2a** — but weaker than it reads: two concurrent builds of one *shared* tree were measured and both succeeded, so this row is regression protection for worktree builds, not proof of the fix |
| `dev suites` green while the live app runs, app untouched | I1, I2 | **earned 2a** — asserts the live app's pids survive the suites; prints `n/a` rather than a green line when no app is running |
| `Measure-Command { dev suites }` under 60 s | I7 | 5 m 16 s |
| repo lint clean | I8 | 2 corrupt lines |
| `dodona status` build SHA is a commit `git log` knows | I2 | **earned 2b** — asks the INSTALLED build and demands `git cat-file -t` resolves it to a commit; prints `n/a` when nothing is installed, or when the installed image carries no provenance (a `dev build` or `--exe` publish, which is a real state and not a failure) |

---

## 3. Ordering constraints

**Must not be concurrent:**

- **P3.1 → P3.2 → P3.3/P3.4/P3.5**, in that order, by one agent. P3.2 without P3.1 makes `ps`
  under-report further (shims vanish before their records do); P3.4 without P3.2 leaves
  `##shutdown` as the only exit for a shim the daemon has just decided to keep.
- **P1.1 and P1.2** — the window between them makes every suite red.
- **P4.2 before P4.3** — by-name process queries break under parallelism.
- **P4.1/P4.4 not concurrent with P3.7** — both edit the same suites' assertions.
- **P0.3 before Phase 1, and before any `dev prove` verdict is believed** — until the artifact
  churn is gone, no diff-based guard can distinguish a real change from noise.
- **P0.2 before any two agents work concurrently (before P2.1)** — while `dev build` calls
  `stop-all`, two agents following CLAUDE.md §1 destroy each other's daemons.
- **P2.3 after P2.1** — the publisher needs its own tree.

**Safe in parallel with anything:** P0.4, P4.5, P5.1.

---

## 4. What the end state looks like

Four actors, four build outputs, zero overlap:

| actor | builds in | runs from |
|---|---|---|
| the live app | — | `%LOCALAPPDATA%\Dodona\bin\<stamp>` *(already true)* |
| each suite run | — | its own `$DODONA_HOME\bin` *(P1.1)* |
| each agent session | its own worktree | its own worktree's bin *(P2.1)* |
| the publisher | a detached worktree of `main` | *(P2.3)* |

Two agents can build, test and publish simultaneously while you use the app, and none of them
can see each other. The only contended resource left is `main`, which merge tokens already
serialize.

---

## 5. Decisions taken — do not re-propose these

Recorded with the reason, in the style of `docs/LANE-LIFECYCLE.md` §2.

- **D-1. Automatic publishing follows `main` only; trials are explicit.** *(operator, 2026-08-18)*
  A trial is `dodona publish --from <ref|worktree>`, stamped with non-main provenance so
  `status` says what you are running. Rejected alternative: keep auto-publishing whatever is on
  disk — reason: it requires five compensating guards for one inexact comparison, it lets any
  session's uncommitted broken edit reach the operator's app, and it caused the 64-iteration
  loop. *One line to flip if you would rather the watcher follow a named branch instead of
  `main`; nothing else in this plan changes.*

- **D-2. Suites do not share daemons.** Rejected — reason: `DODONA_HOME` isolation plus
  `Instance.Scoped()` is precisely what makes parallel runs possible; sharing makes suites
  order-dependent and reintroduces the leaked-concierge failure (21 checks failing under one
  shell and passing under another). The cost to remove is *process startup*, and the way to
  remove startup is to **not need a process** (P4.5), never to share one.

- **D-3. No new tool.** `tools/dev.ps1` stays the one door; the only additions are `dev gate`
  and `dev test unit`, both verbs on the existing door. Reason: CLAUDE.md §0.3 — reaching for a
  new tool is this project's second reflex after reaching for a document, and both failed on
  2026-08-18.

- **D-4. `ClearBlockers`'s `stop-all` is deleted, not narrowed.** Reason: after P1.1 it has
  nothing to clear. It is simultaneously too broad (it stops every registered daemon, the
  concierge, and every unregistered ctl pipe on the machine) and too narrow (it only fires when
  a process *named* `dodona` is in the build output — the three `DodonaShim` blockers `dev
  check` finds trigger nothing).

- **D-5. `dev prove` stays; its guard is fixed.** Reason: the idea is right and it is the best
  thing `cd53389` shipped. Its only precondition — `git status --porcelain -- src tests`
  non-empty — is permanently satisfied by the tracked artifacts, which is why it declared a
  sound check `VACUOUS`. After P0.3, require `src/` specifically to differ, and add a third
  outcome: *"passes against HEAD because HEAD already contains the change"*, distinct from
  *"vacuous"*.

- **D-7. Per-session trees are enforced AT THE COMMIT, not at the edit.** *(operator + session,
  2026-08-18, revised 2026-08-19)*
  P2.1 as written said CLAUDE.md §0 would name worktrees "the only supported way to run two
  sessions". That is a documentation line, the shape **D-6** forbids — and the failure it guards
  already happened: `f9aaf25` carried another lane's `lanes.cwd` migration and `Ver.cs` edits
  into an unrelated routing fix, because two sessions shared one checkout. So P2.1 is superseded
  by enforcement: **a `pre-commit` hook refuses any commit whose worktree is the main checkout.**
  `.githooks/pre-commit` is the tracked, reviewable source and `tools/dev.ps1` copies it into
  `.git/hooks/` on every run, since `.git` is never cloned. `dev worktree <name>` makes compliance five seconds, and
  `DODONA_ALLOW_MAIN_TREE=1` is the deliberate override — an enforcement with no escape is a new
  way to be stuck (CLAUDE.md §0.1).

  **The first attempt had a second layer and it was wrong, which is recorded here so it is not
  re-proposed.** A `PreToolUse` hook refused Edit/Write into the shared checkout, and a sibling
  refused `git add -A`. Measured: **255 ms per edit** and 255 ms per **shell command** (136 ms of
  which is just PowerShell starting) — a permanent tax on every operation for rules that should
  fire almost never. It also could not see a heredoc, `sed` or a redirect, so it was never a
  guarantee. Decisive argument: **once each session has its own tree, a broad `git add` can only
  stage that session's own files** — the `f9aaf25` mechanism is prevented by the separation, not
  by intercepting edits. One lock, at the boundary where a mistake becomes permanent, for 40 ms
  once per commit.

  **`core.hooksPath` was tried instead of the copy, and measured failing** — recorded because it
  is the obvious-looking improvement and will be proposed again. Pointing git at the tracked
  `.githooks/` makes the enforcing file reviewable and versioned instead of a copy inside `.git`,
  which is a real gain. But a commit from the shared checkout then **succeeded**: a tracked hooks
  directory exists only on branches and commits that carry it, so it silently disappears on every
  other branch and on every historical commit checked out while bisecting — which this phase's
  provenance work exists to enable. The copy in `.git/hooks` is branch-independent, and that is
  the property the lock depends on. The cost is real and accepted: the file doing the enforcing
  lives where nobody diffs it, and a pre-existing foreign hook can only be warned about. `dev
  gate` compares the deployed copy byte for byte against the tracked source and asserts
  `core.hooksPath` is unset, because "a file exists" passed through both failures.

  **A hook that must be reliable should be a compiled subcommand, not a script.** Recorded as
  the direction for the claim gate (design §6), which is load-bearing and cannot be deleted: a
  syntax error in a generated `.ps1` is a silent runtime no-op, where the same mistake in
  `dodona.exe` is a loud build failure. It is also one process instead of two — the claim gate
  currently spawns PowerShell, which then spawns `dodona claim-check` anyway.

  **The scope, stated rather than papered over:** this binds anything that reaches a commit,
  agent or human, but it does not stop an edit before then. The edit is recoverable; the commit
  is what put another lane's work into history.

- **D-6. A documented warning is not a fix.** Reason: CLAUDE.md §3.1 documented "a daemon
  outlives its window and the operator believes the machine is idle", and the identical
  incident then recurred, to the same person, from the same cause. Every item in this plan is
  code, a deletion, or a check; where something can only be prose, it is said so explicitly.

---

## 6. What this does not buy

Stated so nobody mistakes the plan for a guarantee.

- **An ad-hoc script spawning real Claude lanes outside `DODONA_HOME`.** That is what produced
  the `mlroot` and `freeze-repro` orphans — `tests/_workspace.ps1`'s discipline protects the
  suites and nothing else. P3.3's lease bounds the damage to N minutes instead of forever, but
  does not prevent the spawn. Preventing it needs a hook refusing `lane-start` outside a test
  home; that should be designed, not bolted on.
- **A broken `main` still breaks the app.** The guards exist (publish probes `version --json`
  before promoting; the shortcut moves only after a daemon accepts) and `dev gate` stands
  before every commit — net safer than today, but not immune.
- **New failure classes will appear.** The loop is the same each time: find the shared thing
  that nobody owns, and give it an owner. That, not this document, is the durable part.

---

## Effort summary

| phase | effort | unlocks |
|---|---|---|
| 0 — stop the bleeding | 30 min | a readable diff; agents stop killing each other's daemons |
| 1 — nothing runs from the build output | half a day | **a build can never be blocked** |
| 2 — own tree per actor; `main` publishes | 1–1.5 days | concurrency; provenance is a commit |
| 3 — processes can die | 1 day | no orphans; no lying counts |
| 4 — verification costs seconds | 1–2 days | gate ~40 s; iterate <1 s |
| 5 — prose shrinks | half a day | no unrunnable command |

**~4–5 days.** Phase 1 alone removes the failure that cost the hour. Every phase is
independently shippable and independently verifiable.
