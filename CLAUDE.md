# Working in Dodona

Dodona orchestrates Claude Code agents. You are probably one of them, working in a lane.
These are the house rules — short, and every one of them exists because breaking it cost
real time.

## 0. The repo is the only memory

**Do not rely on session memory, recalled context, or anything that lives outside this
repository.** Those load for some sessions and not others; a lane in a worktree sees none
of it. The operator's standing rule: *skills + CLAUDE.md, or tooling that enforces, is
the only reliable way.* So knowledge worth keeping goes in exactly one of:

- **enforcement in code** — the strongest form (the claim gate, the merge backstop, the
  drift watcher exist because instructions get skipped and code does not)
- **this file** — rules and traps every agent must know before touching anything
- **`.claude/skills/`** — invocable workflows (`/ship` is the delivery path)
- **`docs/` and `DEBUGGING.md`** — design authority, decisions and rejections, schema
- **commit messages** — the incident history of record; write them so a stranger can
  reconstruct what happened and why

If you learn something load-bearing mid-task, put it in one of those places *in the same
commit as the work*. A lesson that lives anywhere else is a lesson the next session
re-learns the expensive way.

### 0.0 Work in a tree of your own — and git refuses commits from the shared one

**The shared checkout is a source of truth, not a workspace.** Every session works in its own
git worktree:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 worktree <name>
# -> .claude\worktrees\<name>, its own bin and obj, cd there and work
```

Why, in one line: `f9aaf25` committed another lane's `lanes.cwd` migration along with its own
routing fix, because both sat in one working tree and `git add` cannot tell whose edit is whose.

**One lock, at the moment that matters.** A `pre-commit` hook aborts any commit made *from* the
shared checkout. Git runs it itself, so no tool choice evades it — a heredoc, `sed` and a shell
redirect are all caught, because they all have to reach a commit eventually.
`DODONA_ALLOW_MAIN_TREE=1` lifts it for a deliberate exception, such as a release commit.

`.githooks/pre-commit` is the tracked, reviewable source; **`tools/dev.ps1` copies it into
`.git/hooks/` on every run**, because `.git` is never cloned and an install step someone must
remember is not enforcement. Never edit the copy — edit `.githooks/pre-commit` and let the next
`dev` command deploy it. **Do not switch this to `core.hooksPath`**: it was tried, and a commit
from the shared checkout then succeeded, because a tracked hooks directory only exists on
branches that carry it — so it silently vanishes on every other branch and on every historical
commit you check out while bisecting. The copy is branch-independent, which is the property that
matters.

**There were two locks, and the second one was deleted — measured, not argued.** A `PreToolUse`
hook also refused Edit/Write into the shared checkout. It cost **255 ms on every edit** (136 ms
of that is merely starting PowerShell), and a sibling hook banning `git add -A` cost the same on
**every shell command**: a permanent tax on all work, for rules that should fire approximately
never. It could not see a shell redirect either, so it was never a guarantee. And what it
guarded is now structural — once each session has its own tree, a broad `git add` can only
sweep up that session's own files, so the `f9aaf25` mechanism is prevented by the separation
itself. Do not reintroduce a per-edit hook for this; the commit-time refusal is the boundary.

**The limit, stated plainly:** this binds anything that reaches a **commit**, which is every
agent and every human alike — but it does not stop a stray edit before then. That is deliberate:
the edit is recoverable, the commit is what carried someone else's work into history. `dev gate`
asserts the hook is both present and wired, because an enforcement that quietly stops enforcing
is the failure this project keeps paying for (§3's dead routing ladder, and the first version of
this very lock, which spent part of the session it was written in unable to parse — denying
nothing while looking installed).

## 0.1 How the operator works (previously unwritten)

- They state **goals, not metric specs** — "make it feel instant" is the requirement;
  deriving budgets is your job. Do not ask them to quantify.
- **Quota is the scarce resource** (§2.6): suites stay model-free, real-model runs are
  rare and deliberate, the router/compressor stay on cheap models, and you never spawn
  subagent swarms when one focused session will do.
- **Act, announce, allow undo** (§11) applies to you too: make the routine call, say what
  you did, keep it reversible. Blocking questions are for genuinely unsafe forks only.
- Feedback like "that's bullshit" about a proposal is a decision — record it (rejected
  ideas live in `docs/LANE-LIFECYCLE.md` §2 style: *with the reason*) so it is never
  re-proposed.
- **Never hung, halted, stuck, or outdated** (standing directive, 2026-08-18). Anything
  that parks behind a question, waits on a human who did not opt into waiting, or goes
  quietly stale is a bug, not a safety feature. The pattern is always the same: make the
  action reversible (back up, log, announce the undo), then act. Updates arm themselves;
  migrations back up and proceed; deployed artifacts re-deploy on adoption; a process
  that dies at startup leaves a line in `<DODONA_HOME>\logs\daemon-start.log`. When you
  add a wait, name the thing that un-sticks it — a condition, never a person.

## 0.2 Windows & PS 5.1 traps (each cost a debugging round)

- **Non-ASCII literals in `.ps1` files** (`✓ — ⚠`) are read as ANSI in BOM-less files and
  match nothing — build patterns from `[char]0x2713` / `[char]0x2014`.
- **Native stderr**: capturable only with `$ErrorActionPreference='Continue'` + `2> file`
  (`Stop` throws NativeCommandError; `SilentlyContinue` eats the record).
- **Captured native stderr is WRAPPED to the console width**, with a newline inserted
  mid-sentence. So a regex spanning a space can match today and fail tomorrow because a path
  got longer and moved the wrap. Collapse first: `($out -replace '\s+', ' ') -match '...'`.
  This turned `workspace-acceptance`'s repo-exclusivity check red while the product was
  refusing correctly — a **false red**, which costs exactly as much as a false green.
- **`-shl` on `[byte]` stays a byte** and overflows to 0 — cast `[int]` first.
- **Commit messages** with quotes/dashes: `git commit -F <file>`, never inline `-m`.
- **`.Count` on a one-element pipeline** is `$null` — wrap in `@(...)`.
- **`$pid` is a read-only automatic variable** — `foreach ($pid in ...)` throws
  `VariableNotWritable` and the loop body never runs. Name it `$procId`.
- **`ConvertFrom-Json` emits a JSON ARRAY as ONE pipeline item**, so `... | ConvertFrom-Json
  | Where-Object {...}` filters the array object, not its elements — and `$_.name -eq 'x'`
  on an array returns the matching *elements*, which is truthy, so **every row passes**.
  Land it in a variable first (`$all = ... | ConvertFrom-Json`, then `@($all) | Where…`).
  This turned three acceptance checks into silent no-ops before it was noticed.
- **A `.ps1` that fails to PARSE never reaches `finally`** — everything it started leaks.
- **A plain `function f([string]$x)` SILENTLY SWALLOWS extra arguments into `$args`.** So
  `$(Rows "SELECT …" -replace '\s+', ' ')` parses as `Rows` with four arguments: the query runs,
  the `-replace` never happens, and there is no error anywhere. Written inside a check's detail
  string it produces a wrapped, unreadable diagnosis at exactly the moment you need to read one;
  written anywhere that consumes the value it is a wrong answer. Parenthesise the call:
  `$((Rows "SELECT …") -replace '\s+', ' ')`.
- **A `Store` migration that THROWS kills the daemon in its constructor**, before the control
  pipe exists — so `Wait-Daemon` times out and every check in that section goes red pointing at
  whatever it was really testing. Cost a debugging round in Phase 5: a fixture that stands a
  store back up in an older shape (`PRAGMA user_version = 8`) must drop the columns of **every**
  later version, or that version's `ADD COLUMN` fails with "duplicate column". Key such a drop on
  the column existing, never on a version number — the same suite runs under `dev prove` against
  a build that does not have the column at all.
- **WPF**: implicit usings omit `System.IO`; with `AcceptsReturn` the TextBox class
  handler eats Enter before instance `KeyDown` (use `PreviewKeyDown`);
  `RenderTargetBitmap` renders in the element's own coordinate space (capture the Window,
  not a margined child).
- **Redirected child stdio defaults to the OEM codepage** — set UTF-8 explicitly or em
  dashes become `ΓÇö`.
- **`Microsoft.Data.Sqlite`**: `INSERT …; SELECT last_insert_rowid();` in one command
  returns nothing without `NextResult()` — use a separate command.
- **A NAMED PIPE'S NAME BLINKS OUT while its server swaps instances.** `\\.\pipe\` is a
  directory and enumerating it is the right way to ask what is live (`Instance.LivePipes`) —
  but a server that disposes one `NamedPipeServerStream` and constructs the next is, for a few
  milliseconds, not in the namespace at all. Measured: **8 of 192 reads over 1.5 s saw no pipe**
  while the shim was alive and instantly connectable. And the gap is *synchronised*, which is
  what makes it dangerous: every shim in a workspace disconnects the instant its daemon exits,
  and the next daemon's reconcile runs milliseconds later. **A single instantaneous read is not
  a liveness test.** Lane liveness is therefore the UNION of two OS answers — the pipe, or a
  recorded shim pid that is alive (`LaneLiveness`, which carries the measurement) — and
  `stop-all` picks its targets BEFORE stopping any daemon, because an attached pipe is a steady
  one. This nearly shipped as "reconcile declares every lane dead on restart".

## 0.3 Problems are not allowed to exist (operator directive, 2026-08-18)

**A snag is not something to work around. It is something to eliminate.** When you hit one,
the task stops being the task — removing the snag becomes the task, and it ends in exactly
one of three places, *in the session you hit it*:

- **enforcement in code** (strongest — the claim gate, the merge backstop, `ps` counting live
  pids instead of leftover files)
- **a tool** that makes the wrong thing impossible or the right thing instant (`tools/dev.ps1`)
- **a rule in this file**, but only when neither of the above can hold it

What is forbidden, explicitly, because every one of these happened in a single afternoon and
cost an hour on a fifteen-minute change:

- **Nursing a broken environment instead of fixing it.** Four daemons held the compiler's
  output file, so a whole second copy of the tree was built in a temp directory and every
  build and every test ran twice. The copy was the workaround; the fix was one command that
  clears the holder and a `ps` that stops lying about what is running.
- **Working around the same snag twice.** Hitting it once is information. Hitting it twice
  means you chose the workaround over the fix. Stop and fix it.
- **Reporting a snag as if it were the work's fault.** "Build FAILED" that really means "an
  invisible daemon holds a file" sends the next reader hunting through their own code. Name
  the real cause, or the tool must name it for you.
- **Believing a green check.** A new check is worth nothing until it has been *seen red*
  against the code it is meant to catch. One passed against the unfixed binary and looked
  like proof. `dev prove` exists so this is mechanical rather than remembered.
- **Documenting instead of fixing.** §3.1 already recorded "a daemon outlives its window,
  and the operator believed the machine was idle". It happened again anyway, to the same
  person, from the same cause. **A written warning is not a fix.** If the honest answer is
  "the next session will read this and be careful", the answer is wrong.

The operator's phrasing, kept verbatim because it is the standard: *problems are not allowed
to exist.*

## 1. Everything mechanical goes through `tools/dev.ps1`

**Do not call `dotnet build`, a suite, or `publish` directly. Use the wrapper.**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 <verb>
```

| verb | what it is for |
|---|---|
| `check` | Can this tree build? What is in the way? Seconds. **Run it before starting work, not after.** |
| `build` | Builds. Only *real* compile errors reach you; a locked output is named, never mistaken for one. |
| `test <suite>...` | One or more named suites, run concurrently. `--sequential` for one at a time. **IT DOES NOT BUILD — run `dev build` first or you are testing the PREVIOUS binary.** Suites copy their binaries out of `src\...\bin\Release\...` (`Use-TestBinaries`), and only `prove`, `gate` and `suites` compile. Measured 2026-08-19: a defect was deleted from `Daemon.cs`, `dev test m0` said **26 checks, 0 failed**, and `dev build` + the same command said 1 failed. That is a false green from the tool itself — `LOCATIONS-PLAN.md` P1.5 carries the fix. |
| `test unit` | The pure logic — no daemon, no store, no window. **~1 second**; run it while you edit. |
| `suites` | All thirteen, **three at a time** (69e8003 lowered it from five; `ui-use` went intermittently red at five). Measured on this machine 2026-08-19: **93 s** at 69e8003 and **100 s** with Phase 3's fifteen extra checks, not the 54–72 s this row claimed — the range predates both the concurrency change and Phase 3's eleven extra m0 checks. Still a gate before committing rather than the twenty-minute event the table claimed before that. |
| `prove <suite> <check>` | Demands a new check FAILS against HEAD. Run it before believing any new check. Three verdicts: PROVEN, VACUOUS (it passes against HEAD — rewrite it), MISSING (it never ran). |
| `prove <suite>:<check> ...` | The same, for MANY checks: grouped by suite and **one run per suite**, because a suite run prints every check it ran. Phase 3 ran m0 eleven times to read eleven lines of one run's output — 46 minutes for what is 40 seconds. Reach for this form by default. |
| `lint` | The repo lint (I8): control bytes, dangling `tests\*.ps1` references in docs, mixed line endings. Sub-second, tracked files only. Asserted by `gate`; run it directly after any scripted edit. |
| `gate` | The pre-commit gate: runs the suites, then **asserts** ten invariants — nothing left running in the build output, a suite run that dirtied nothing, **no wrapper or agent process that outlived the run** (I3), the commit guard deployed and unoverridden, the live build’s commit resolvable in `git log`, the full run inside its time budget (I7), and **no changed file quietly altering its BOM or line endings** (P7.5 — that has happened three times and twice went unnoticed until someone read a diff). Every row of `RECOVERY-PHASES` §2 is asserted now — which is NOT the same as proving the system works, and the verdict line says "on the 10 assertions above, and only those" on purpose. `dev gate <suite>` runs the same machinery over less, in ~20 s, and says PARTIAL on every line. |
| `ship` | build + suites + publish. |
| `worktree <name>` | a tree of your own under `.claude\worktrees\`. All work goes in one (§0.0). |

It is a **script, not a `dodona` subcommand**, and deliberately so: a tool whose job is to fix
a blocked or broken build cannot itself require a build. It runs on a tree that will not
compile.

Why the wrapper is mandatory rather than convenient: the raw commands hand you the wrong
diagnosis. `dotnet build` reports a locked output file as `Build FAILED` with ten screens of
MSB3026 retries, which reads as "your code is broken" when it means "a daemon you cannot see
is holding a file". The wrapper names the pid and the one command that frees it — on line
one, not at minute forty. It stops nothing on your behalf: `stop-all` is machine-wide, so
clearing a holder is always your explicit call (Phase 0, 2026-08-18). Every run logs to
`.dodona\dev-logs`.

**An edit that has not been built is a claim, not a change.** That has not softened; the
wrapper is how you honour it. This was the single most expensive mistake in this project's
history — twice:

- A lane rewrote the dispatcher input box, was denied permission to build, and reported the
  change as in. The operator restarted the app and saw nothing, because nothing had ever been
  compiled. Worse, the code contained a real bug (WPF's TextBox class handler eats Enter
  before an instance `KeyDown` runs) that one build-and-test would have caught.
- Twenty-eight minutes of UI work sat in the working tree unbuilt and unpublished while the
  operator restarted the app repeatedly wondering why it looked the same.

If you cannot build — permission denied, a lock the wrapper could not clear, anything — **say
so as the headline of your reply**, not as a footnote, and name the exact command.

### Iterate fast, gate slow — and "slow" is now 54 seconds, not twenty minutes

*"Ban any test that takes longer than a second or two. Twenty minutes of test is ridiculous."*

**This section used to say the suites take twenty minutes, that only 3.6 minutes of that is
`Start-Sleep`, and therefore "the rest is inherent and cannot be optimised away". Every part
of that was wrong, and it was stated as measured.** Measured properly on 2026-08-19 the
eleven suites took **5 min 20 s**, of which **214 s — 68 % — was fixed `Start-Sleep`**: not
inherent at all, and almost none of it real waiting. A `Start-Sleep -Seconds 3` in front of a
check is a guess about the slowest machine that ever ran it, paid in full on every machine
since, while the condition it is waiting for is already written down one line below in the
check itself.

That wrong number was not harmless. It is the reason verification became a thing to skip
rather than a thing to fix — and skipped verification is the root of every believed-a-green-
check incident in §0.3. **A measurement you did not take is not a measurement.**

Where it stands now, all measured on this machine:

| | before | after |
|---|---|---|
| full run, all suites | 5 min 20 s (and it could **hang forever**, see below) | **54–72 s** at the time, **115.9 s** today — concurrency dropped 5→3 (69e8003), Phase 3 added fifteen checks, and the Locations wave-1 phases added ~90 more; the gate's budget was raised **120 s → 180 s** against that 115.9 s measurement, because earned coverage growth must not present as a gate failure |
| the same run, sequential | ~320 s | ~200 s |
| fixed `Start-Sleep` across the suites | 214 s | ~4 s |
| the narrowest useful check | ~7 s (a daemon must start) | **~1 s** (`dev test unit`) |

Three changes did it, and all three are in `tools/dev.ps1` and `tests/_workspace.ps1`:

- **`Wait-Until` (a condition plus a deadline) replaced the sleeps.** Every wait now names the
  thing that un-sticks it, per §0.1's standing directive — a condition-wait with no deadline
  would be that directive violated in a new costume, so timing out is a normal return that
  prints one line and lets the following check fail on its own terms.
- **Suites run THREE at a time**, each in its own process with its own `DODONA_HOME`. Not all
  twelve, and not five: at both of those `ui-use` went intermittently red — 61 s green, then
  149 s and 119 s red on a quiet machine — and its failures cascade, so two missed interactions
  become six red checks. Three is green and repeatable (93.1 s and 93.3 s, all twelve, twice).
  The contention is windows and process starts, not CPU (22 cores, never bound), and the root
  cause is NOT established: ruled out by measurement are the leaked shims, the other window
  suites, and m4's build — it needs the full rolling wave to reproduce. `ui-use` being a
  70-second monolith is the real problem; splitting it is unfinished business.
  `DODONA_TEST_CONCURRENCY` overrides; `dev suites --sequential` is the debugging escape hatch.
- **`dev test unit`** runs the pure logic — the claim algebra, the policy table, repo
  resolution, path canonicalization, the two routing decisions made in code, the progress
  tiers and their fold — with no daemon, no store and no window. **230 checks** in
  **1.9–2.3 s warm**, and ~5.6 s on the first run after a build. Corrected twice now: this
  row said "54 checks in about a second", then **88**, and the real number at
  `d43dffb` was **189** — nobody had re-counted, which is the same failure §1 has a whole
  section about, in the section about it. Do not hand-maintain this figure: the suite
  prints it on every run. Still the "one or two seconds" the operator asked for; it does
  not and cannot replace an acceptance suite.
- **`dev test`, `dev suites` and `dev gate` REFUSE a stale build output** rather than testing the
  previous binary (P1.5). None of them ever compiled, and every suite copies its binaries out of
  `src\*\bin\Release`, so an edited-but-unbuilt tree was verified green — measured: a deleted
  line in `Daemon.cs`, `dev test m0` reporting *26 checks, 0 failed*. The refusal costs ~30 ms,
  compares **each project against its own assembly** (tree-wide is the question auto-publish
  looped 64 times on, §2), and names `dev build`. `dev prove` is exempt: it builds its own
  baseline.

### RUN THE SUITES YOUR CHANGE TOUCHES. THE FULL SET IS FOR MERGING.

**This is the default, not an optimisation.** `dev test <suite> [<suite>...]` takes any
combination and runs them concurrently, and `dev test unit` is a second. Reaching for
`dev suites` or `dev gate` on every edit is how verification became something to skip, and 80
seconds spent twenty times is worse than 80 seconds spent once at the end.

| what you changed | run |
|---|---|
| anything that is a pure function (claims, policy, repo resolution, paths, routing verdicts) | `dev test unit` — ~1 s, no daemon |
| daemon lifetime, reconnect, drain | `dev test m0` |
| claims, the gate, the merge token | `dev test m1` |
| routing, presence, the backstop | `dev test m2` |
| the UI as a view over the store | `dev test m3` |
| publish, hot swap, provenance | `dev test m4 publish` |
| workspaces, members, repo exclusivity | `dev test workspace` |
| anything a person clicks or types | `dev test ui-use` |
| dictation: the box, the mic toggle, spoken words | `dev test voice` — ~13 s, opens no microphone |
| compression | `dev test compression` |
| the dispatcher brain, the routing ladder | `dev test brain` |
| the concierge, the fence | `dev test concierge` |
| `tools/dev.ps1` itself | `dev gate m0` — the gate's machinery over one suite, ~20 s |

- **While iterating**: `dev test unit` for anything that is a function (~1 s), then the one or
  two suites your change actually touches. Anything that must start a daemon has a ~7 second
  floor, and that is the honest target.
- **Before you MERGE to main**: `dev gate`, once — all thirteen plus the ten assertions, about
  80 s of suites and ~15 s of its own two builds. This is the only moment the full set is
  required, and skipping it is how a stale test survives for months (two did).
- **Three consecutive failed verification attempts**: stop and report. Do not grind.
- **If a suite you did not touch goes red, suspect the machine before the code.** `dev gate`
  prints the leaked-process count first for exactly this reason: with strays alive a full run
  went from 87 s to 300 s and reddened thirteen checks in suites nobody had edited.

**A suite that does not print `<N> checks, <M> failed` is now a FAILURE, not a shrug.** This
is not bookkeeping. `m0` had never printed a tally in its life, so `dev.ps1` could not detect
an m0 failure at all; and `ui-use` was dying inside its own `finally` on a stray stderr line
(§0.2's `NativeCommandError` trap) — 74 checks computed, discarded, and reported as `no tally
line`, which counted as nothing. Both were green-looking and both were blind.

**And `dev suites` could hang forever, which is very probably what was killed three times as
"too slow".** Reading a child's stdout through a pipe does not end when the child exits — it
ends when the last handle to the write end closes, and every process a suite spawns inherits
that handle. `publish-acceptance` leaks four `DodonaShim` processes, and a shim's only exit is
a message from a daemon that is already gone. Measured: eight minutes of waiting after the
suite had finished and printed its results, and it would never have ended. Suite output goes
to **files** now, and every suite has a deadline.

## 2. Completed work gets published — and the daemon now enforces this itself

**This rule is no longer only yours to remember.** With `"autoPublish": true` in
`dodona.json` (on, for this repo), the daemon compares **`git rev-parse main` against the
commit the running build was made from**, and when they differ it builds *that commit* — in a
detached worktree of its own, never your tree — and swaps itself to it. A failed build changes
nothing and is announced loudly. This exists because edited-not-built, built-not-published and
published-not-committed each blocked the operator once in a single day, and an instruction in
this file is advisory while a watcher is not (the claim-gate reasoning, §6).

**Only `main` publishes itself** (decision D-1, Phase 2b). Uncommitted work cannot reach the
app at all now — which is the point: any session's half-finished edit used to be able to. To
trial something deliberately:

```powershell
dodona publish --from <ref|worktree>     # stamped as a TRIAL; status says so
```

A trial says what it is (`trial: <branch>@<sha>`) and **the next commit to `main` replaces
it**, because the trial build carries the `main` SHA it was cut against. Nothing to remember,
nothing to reset.

What that leaves for YOU: still build before reporting (rule 1 — the watcher publishing
your broken edit produces a loud failure with your name on it), still commit (the watcher
nags, it does not commit for you), and still run publish yourself when the operator asks
or when you want the swap *now* rather than at the next 15-second poll.

**Finishing a piece of work — or being asked to publish — means running publish.** Not
"mention that it could be published", not leaving it built-but-installed-nowhere. The
operator sees Dodona through the installed app; work that never reaches it does not exist
from where they are sitting.

```powershell
# Resolve the INSTALLED binary the way Ver.BinRoot does, newest build wins.
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
    Sort-Object Name | Select-Object -Last 1).FullName 'dodona.exe'
& $dodona publish --project . --all
```

**Never `.\src\Dodona\bin\Release\net8.0\dodona.exe`**, which is what this file said until
2026-08-18. That invocation is how a daemon ends up holding the compiler's own output: the
instruction *caused* the failure it warns about elsewhere, and any `dodona` command run that
way could autostart one. The binary now refuses to autostart from a source-tree build output
and names `publish` instead (`Ver.IsSourceTreeBuildOutput`) — `%LOCALAPPDATA%\Dodona\bin\` and
a suite's `$DODONA_HOME\bin` are deliberately allowed, because a build output is the problem,
not the word "bin".

Nothing installed yet on this machine? `powershell -NoProfile -ExecutionPolicy Bypass -File
tools\dev.ps1 ship` bootstraps it. That one runs `publish` from the build output on purpose
and it is the single exception: `publish` is a transient CLI that exits, not a daemon that
outlives your window.

That builds all three executables into a fresh versioned directory, hands off to any
running daemon **without interrupting a live agent mid-turn** (M4), and re-points the
desktop shortcut. Safe to run while the operator is working — that is the whole point of
the hot swap, verified by `tests/m4-acceptance.ps1`. Work in progress gets published too
when the operator wants to trial it; that is what a trial is.

`--all` now means **every live workspace in the registry, plus the concierge** — resolved by
id, never by scraping every `dodona-*-ctl` pipe off the OS. A live daemon belonging to no
registered workspace is never a swap target, which is what finally made
`tests/publish-acceptance.ps1` possible. Narrower: `--workspace <name>...` and `--concierge`;
with neither, the workspace that owns `--project`.

One thing publish cannot do, so say it plainly when it applies:

- **Publishing does not commit.** Commit the work too, or it will be published and then
  lost on the next checkout.

(A running UI window DOES hot-swap now — publish refreshes live windows through
`ui update` after the daemons, and the window hands off to the new build or stays if the
successor never answers. An older revision of this file claimed otherwise.)

Publish also **verifies before it promotes**: the new binary must answer
`version --json`, and the desktop shortcut is repointed only after a daemon accepted the
build (or after the probe, when nothing was running). This exists because fourteen
consecutive auto-publishes of a broken tree once each repointed the shortcut at a binary
whose daemon died on startup — the front door itself rotted, and every project open froze
against it (2026-08-18). The shortcut launches `DodonaUi.exe --shell` — the workspace
shell. (The folder picker no longer exists at all, see §3.1.)

**Auto-publish asks an exact question now, and the five guards that made an inexact one
behave are deleted.** It once looped 64 times in an afternoon — 72 daemon restarts, a full
three-project build every ~65 seconds, four consecutive swaps reporting the byte-identical
`sources 15:56:19 > image 15:55:55`. The cause was that "is any source newer than the running
image?" is not answerable by a filesystem: the newest source spanned **all three** projects
while the image was **one** of them, so editing `src\DodonaUi\MainWindow.xaml.cs` left a
condition that could never be satisfied — and the guard that should have stopped it was an
in-process local, reset by the very swap it triggered.

It compares **two SHAs**. That is the whole mechanism, and it needs no guards:

- The commit is compiled **into the binary** (`InformationalVersion`, read by `Ver.Commit`), so
  a build always knows what it came from and there is no stamp file to lose. `dodona status`
  and `version --json` report it, so what is running is checkable against `git log` and
  bisectable.
- **A build with no provenance refuses to watch, out loud.** `dev build` images and
  `publish --exe <prebuilt>` compiled nothing, so they know no commit — and the old code
  *degraded to the mtime compare* in exactly that case, which is the bug wearing a fallback.
  One announcement, then it stops; any `publish` from a git checkout arms it.
- **Gone:** `Ver.NewestSource`, `Ver.WriteBuiltFrom`, `Ver.ImageBuiltFrom`, the `.built-from`
  file, `autoPublishDebounceSec`, `kv.autopublish_last_tried`, and the 30-minute dirty-tree
  nag. A commit is atomic and already quiet, so there is nothing to debounce; the SHA is its
  own guard; and uncommitted work can no longer reach the app, so nagging about it answers a
  question nobody can ask. **Kept:** surrender after three consecutive failures — a broken
  `main` must not rebuild forever, and that was never about mtimes.

Two traps this cost, both worth knowing before you touch the stamp: the **dotnet CLI splits a
`-p:k=v` value on commas**, so a comma-separated stamp silently arrives truncated at the first
comma (trial detection and the baseline both vanished, with no error anywhere) — the separator
is `~`, which git forbids in a ref name. And the SDK appends its own `.<SourceRevisionId>`
unless `IncludeSourceRevisionInInformationalVersion=false`; it also writes a bare SHA there by
itself, so `Ver` accepts only its own `c=` marker.

Blocked swaps **arm themselves** instead of asking (`swap-answer now` forces, `hold`
parks — holding is opt-in, waiting never is), and a schema-migrating swap **backs up the
store and proceeds** (announced with the restore path; only downgrades refuse). §14 of
the design doc records the revision.

## 3. Verify with the suites, not by looking

Thirteen model-free suites, all fake agents, all free. **Run them through `dev test`, never by
invoking the `.ps1` directly** — the wrapper is what enforces that a suite which crashed, hung
or never reported is a FAILURE rather than a blank line (P4.4), and it is what runs them five
at a time:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test m3 brain
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test unit      # ~1s, no daemon
```

| suite | what it covers |
|---|---|
| `unit` | the pure logic: claim algebra, policy table, repo resolution, canonical paths, the code-only routing decisions |
| `m0` | daemon death mid-turn, and Phase 3's whole invariant: a wrapper that outlives its agent, a lane with no shim record, the lease, reconcile asking the OS |
| `m1` | claims, the gate, the merge token |
| `m2` | routing, the backstop, presence |
| `m3` | the UI as a view over the store |
| `m4` | hot swap (runs a REAL build — the slow one) |
| `workspace` | identity, repo-exclusivity, multi-repo |
| `ui-use` | the UI driven like a person |
| `compression` | selective compression (§5) |
| `brain` | the dispatcher brain, its routing ladder, and the no-second-brain-beside-a-live-one guard |
| `concierge` | the group-scope ladder, the fence, the review-behind |
| `publish` | publish targeting: `--all` spares foreign instances |
| `voice` | dictation: speech composes and can never send (docs/VOICE-INPUT-PLAN.md Phase A) |

**The per-suite durations that were a column here are gone (P5.2).** They drifted: two were
wrong by more than double before Phase 7 corrected them, and a table nobody can trust is worse
than no table, because it still gets quoted. `dev suites` and `dev test` print the real ones on
every run, measured on the machine you are actually using. What stays is the mapping from a
suite to what it covers, which is judgement no command can print.

**Two suites run ALONE, and taking either out of that list will look like it works.**
`SoloSuites` in `tools/dev.ps1` is the list, and each entry carries its reason in a comment:

- **`unit`** compiles (`dotnet test` builds Dodona into `src\Dodona\bin`), and every other
  suite copies its binaries out of there at startup. Two compilers, one directory.
- **`m1`** is intermittent beside a parallel wave — measured, green 5/5 alone in 8–9 s, and
  `gate_denies_outside_claim` failed 3 of 4 times when it ran next to `m4`'s real build,
  because `dodona gate-hook` returned EMPTY for longer than the check's 20 s retry.
  **That cause is still not known.** It is not the daemon being slow (that path writes
  `.dodona-bypass.log`, and the log was empty), and it is not PowerShell failing to deliver
  stdin to `cmd /c` (probed 60× under load, 60/60 delivered).

  **A DIFFERENT m1 failure was found and fixed on 2026-08-19, and it was not flaky at
  all — the claim gate was failing OPEN on every run.** Both gate checks were red on
  `main` at `d43dffb`, alone and in the wave, and `dev prove` confirmed it: stdin reached
  `GateHook` with a **UTF-8 BOM** (`Ã¯Â»Â¿{"tool_input":…` in the bypass log),
  `JsonDocument.Parse` refused it, and the fail-open branch allowed the write **without
  ever asking the claim algebra**. `Console.In` hands a leading U+FEFF back as an ordinary
  character, and PS 5.1 writes BOMs by default (§0.2), so this was reachable by any
  producer piping a file in. `GateHook` strips it now. Two lessons worth more than the
  fix: a **fail-open** path must be read as a red flag whenever a suite goes red near it,
  because the failing check is the only thing standing between that and silence; and the
  loud diagnostic added in P4 (byte count + prefix in the bypass log) is what turned a
  third round of guessing into one look — a *silent* `return 0` would have hidden this
  exactly as long as it hid the other one.

  m1 stays SOLO: the deterministic bug is gone, the intermittent EMPTY-stdin one is not
  explained, and it is still one of `GateHook`'s remaining fail-open paths.

So: **do not "tidy up" `SoloSuites` because a suite looks fast enough to parallelise.** m1 alone
costs 8 seconds. A gate that is red one run in four for a reason nobody has diagnosed costs far
more, because it teaches people to re-run instead of read — which is the same disease as a gate
that is always green. If you want m1 back in the wave, first find out why the hook goes quiet,
and put the answer in the commit.

**Waits are conditions, not sleeps.** `Wait-Until { <condition> } <timeoutMs> '<what>'` in
`tests/_workspace.ps1` is how every one of them is written now; `Wait-Daemon` is the common
case. Do not add a `Start-Sleep` — the four that survive are each a real duration (a fake
agent's own turn length) and say so in a comment. A wait with no deadline is §0.1's standing
directive violated in a new costume, so `Wait-Until` always has one and returns `$false`
rather than throwing: the check that follows then fails on its own terms and prints the real
value it saw, which is a better diagnosis than a wait's own idea of what went wrong.

**A suite that builds the thing it tests proves nothing about the wiring.** The routing
ladder — four verdicts, the escalation chain, the held-input rung — was fully covered and
fully green while being **dead in production for two days**. `RouteInput` looked its
classifier up by `role='router'`; nothing in the daemon ever created one (the warm-up and
`brain-start` both make `brain`), and the sole producer of `router` was a manual command
whose only caller in the entire tree was `brain-acceptance.ps1` itself. The suite stood up
its own classifier, proved the ladder against it, and passed — while every sentence the
operator typed fell to `no-classifier` and went to whatever lane was focused. Measured on
their store: 14 routed inputs, all `tier=focus confidence=no-classifier`, **zero**
`classified` events, **zero** router lanes ever created, while `dodona status` cheerfully
printed `router: model=haiku` for a lane that had never existed.

Two rules came out of it, and they generalize past routing:

- **Ensure at the point of use, never look up.** A lookup that misses is indistinguishable
  from one that was never going to hit. `EnsureRouterAsync` creates the classifier where
  routing needs it, the way `EnsureBrainAsync` already did — after which "no classifier"
  can only mean *switched off in config* or *the spawn failed*, and both say so out loud.
- **Every suite must exercise at least one path the way the OPERATOR runs it** — autostart
  on, nothing pre-built by the test. `brain-acceptance` now stops the classifier, restarts
  the daemon with `DODONA_NO_AUTOSTART` cleared, and demands that a typed sentence produce
  a `classified` event. Verified by reintroducing the defect: those checks go red with the
  detail `focus|no-classifier`, the live store's exact signature.

And **a silent degrade is a bug** (§0.1's standing directive covers "quietly stale"): the
only evidence of two dead days was a status-line suffix nobody reads. The fallback now
announces itself once per daemon and writes a `routing_unrouted` event.

`ui-use` is the one that matters most for UI work: dumps and screenshots prove the UI
*reports* correctly while the first thing a person tries is still a dead end. If you add
an interactive affordance, add a check there — not only a dump assertion.

For visual work, use the capture loop rather than describing pixels:

```powershell
.\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe --root <project> --pose long --shot out.png
```

**Every UI you launch for a test or a check gets `--test-window`.** It renders off-screen,
never activates, and never enters the taskbar — dumps, screenshots, poses and UIA all
still work. Test windows popping up and stealing the operator's keyboard mid-work was a
priority complaint; `SendKeys` is banned for the same reason (it needs focus) — drive
input with `dodona ui type "<text>"`, which submits through the same code path as Enter.

**The box is multiline**: Enter sends, **Shift+Enter is a new line**, it grows itself as
lines arrive, and the grip above it drags it taller (the feed gives up the pixels, so the
window never moves). Three more focus-free verbs exist for the same reason `type` does —
each lands in the method the mouse or keyboard lands in:

```powershell
dodona ui compose "<text>"          # type WITHOUT sending — `type` always submits
dodona ui key shift+enter | enter   # the keystroke, through the real PreviewKeyDown path
dodona ui input-resize <dy|reset>   # the grip: +px taller, reset = fit the text
dodona ui lane <focus|stop|respawn|collapse|expand> <n>   # a tile's five actions
dodona ui listen <on|off|toggle>    # the mic button, in the method Mic_Click calls
dodona ui heard "<text>" [--partial] [--epoch <n>]   # a recognition result, through the real splice
```

**The box LISTENS now, and speech can never send** (docs/VOICE-INPUT-PLAN.md Phase A). The
operator's constraint, verbatim: *"Send will still need an enter."* That is not a rule anyone has
to keep — `Dictation.DictationAct` has no member that means send, so the decision layer cannot
ask for a submit, and `MainWindow.OnHeard` calls `ComposeInput` and `InputKey` and nothing else.
The words "enter", "send", "submit" and "go" are ordinary text because there is nowhere else for
them to go. `voice:spoken_send_words_do_not_submit` says so at a live window.

The toggle is remembered in `ui.json` beside the box height, the mic glyph in the grip strip has
three states (off / listening / **error**, because on-and-deaf must never look like on), and
`ui dump` gained a `listen` key (`state`, `engine`, `says`, `partial`, `error`, `dropped`,
`remembered`). **`DODONA_UI_MIC=off` refuses to construct a real recogniser at all**, and
`tests/_workspace.ps1` sets it for every suite — so no suite can ever open the operator's
microphone, which would be §4's incident in a new costume. `=fail` forces the error state, which
is otherwise unreachable without unplugging something.

**The engine is wired but UNVERIFIED.** `SapiRecognizer` (System.Speech, one PackageReference)
compiles and is behind the seam; nothing has confirmed it *hears* anything, because that needs a
voice. Which engine actually ships is D-V8's spike, which has not been run. Do not describe
dictation as working end to end until someone has spoken to it.

`ui dump` gained an `input` key (`text`, `lines`, `height`, `fit`, `sized`, `remembered`,
`hint`) — `lines` is LOGICAL lines, not wrapped rows; `fit` is the default height and
`remembered` is what is on disk. The box **opens at three lines** and **remembers the size
you last dragged it to**, in `<DODONA_HOME>\ui.json` — a file and not the store, because the
shell window spans workspaces and booted to zero has no store to read (§5). It is a
preference, so every read failure falls back to the default silently: a corrupt `ui.json`
must never be able to stop the window opening, since the box is the one thing you would use
to say so. A double-click on the grip forgets it.

Two WPF facts this cost, both now in code comments: `MinLines`/`MaxLines` are **ignored**
once `TextWrapping` is on (the default height is a measured `MinHeight`, and `ui dump`'s
`fit=28` is what caught it), and §0.2's trap is load-bearing here — with `AcceptsReturn` the
TextBox class handler eats Enter before an instance `KeyDown`, so the handler is
`PreviewKeyDown` and `tests/ui-use-acceptance.ps1` now proves Enter still sends.

Poses are deterministic fixtures (`full`, `badges`, `blocked`, `feed`, `collapsed`,
`tray`, `overlay`, `long`, `two`, `twelve`, `bands`, `merged-feed`, `boot-zero`, `ask`,
`ask-group`). `--pose` needs a `--root`
or `--shell`; a bare launch (no `--root`, no `--workspace`) is the shell.

**The window ASKS things now, and it is not a dialog** (docs/LOCATIONS-PLAN.md Phase 4, D-L4).
A question is an `open` row in a `questions` table — the concierge's, or a workspace's — and the
ask is *rendering that row*: an in-window overlay live, `ui dump`'s `ask` key headless, and **one
answer path** shared by both (`dodona ui answer <choice>` lands in the same method a button click
lands in, and sends the same daemon command `dodona answer` / `dodona concierge-answer` sends).
`Esc` puts it down without answering; the row stays open and the feed still carries it.

- **Never make it a modal.** A test window is forbidden from producing one, so a modal ask would
  be permanently untestable — which is why `PickerWindow` and `StartLaneWindow` have no coverage
  at all, and why D-L4 rejected one.
- **Never let it become a folder picker** (§3.1). The choices are names the system already knows;
  `ui-use:the_ask_offers_no_filesystem_navigation` goes red if a path appears in one.
- **With one project there is nothing to ask, so no overlay may appear.** The operator's own
  machine is a one-project workspace, and two `ui-use` checks pin it.

## 3.1 No folder UI, ever (operator directive, 2026-08-18)

The app's only user-facing identity is the **workspace name**. Locations are the router's
business: the concierge attaches folders as work arrives, and a workspace daemon manages
every lane in its member locations. The old folder picker (recents, Browse…, repo
statuses) and the header dropdown that reopened it survived the workspace redesign as
leftovers and were removed on the operator's direction. `PickerWindow` is now the
**workspace switcher** — names + awake/asleep only; picking wakes the workspace and hands
it the grid through `FocusWorkspace`, never a second window. Do not reintroduce a folder
list, a Browse dialog, or a filesystem path in window chrome. `repo-init` stays a daemon
command reached by typing. (Decision recorded in WORKSPACES-CONCIERGE.md §6.1.)

**The grid divides itself** (§8 revised): no slot cap, panes shrink as lanes arrive, nothing
scrolls, and the operator collapses tiles to chips (`dodona lane-collapse|lane-expand <lane>`).
A collapsed chip still carries its badge and blocked glyph — an active-but-invisible lane stays
forbidden.

**The window is now one shell over N workspaces** (`DodonaUi.exe --shell`): the focused
workspace holds the grid, every other awake one is a band of lane chips, and the feed is a
union with a workspace chip per row. Address that window with `--shell` on any `ui` verb,
and give a band the grid with `dodona ui workspace <name>` — the same code path a click
takes, without stealing focus. `DodonaUi.exe --shell` with nothing awake is **boot-to-zero**,
a real state: just feed and input, and typing is how you leave it.

**And the WINDOW outlives its daemon — which is the half that was broken.** After a `stop-all`,
a crash, or a reboot with the shortcut relaunched, the window is up and nothing is running. The
store reader is read-only, so every lane still renders as `alive` and the app looks healthy. It
was not: `MainWindow.Send` — every lane click, plus the one-workspace branch of the input box —
did not start a sleeping daemon, so the first thing a person did was answered with the literal
words *"daemon not running"*. Two of the three write paths already ensured first; the third,
carrying the most traffic, did not. **Start-on-demand now lives inside `DaemonClient.Send`**, so
it is not a rule a call site can forget — the same correction the routing ladder needed (§3:
*ensure at the point of use, never look up*).

It survived because none of the five lane actions had a `ui` verb at all: **unreachable, not
merely untested.** They have one now (`ui lane`, above), landing in the same `LaneAction` a click
lands in. The general rule this is the second instance of: **an affordance no verb can reach is
where the next defect will live**, because it is the one place a suite is physically unable to
look. If you add something a person can click, add the verb in the same commit.

**A daemon outlives its window.** Closing the app does not stop anything — that is the design
(the window is disposable, agents survive behind their shims). `dodona ps` is how you find out
what is actually running, and `dodona stop-all [--lanes]` is how you stop it. This cost the
operator a surprise: they closed the window, believed the machine was idle, and a daemon plus
seventeen shims had been up for hours.

**But nothing outlives its REASON any more** (Phase 3). A shim exits when its agent dies and the
buffer has been handed over; and if no daemon connects for `DODONA_SHIM_LEASE_SEC` — 30 minutes
by default — it exits anyway and takes the agent with it. Both say why, in
`<DODONA_HOME>\workspaces\<id>\shim-exits.log`, because stderr belongs to a daemon that is
usually already gone. So a deliberate `stop-all` (daemons only, lanes keep running on purpose)
costs you those agents after the lease: the lane ROW survives and `dodona lane-respawn <lane>`
resumes the session, which is bounded and recoverable where an immortal process was neither.

**Routing waits now.** A distinct task gets its own lane (WORKSPACES-CONCIERGE.md §5.1), so
input is no longer delivered optimistically to the focused lane: a wrong continuation cannot be
undone, and correcting it is exactly what is impossible. Only `LANE: text` and unmistakable
generics ("stop", "no", "try again") are decided in code and stay instant. On double uncertainty
the sentence is HELD and you are asked — nothing is delivered. Without a warm brain, behaviour
is unchanged (focused lane), which is why the suites are unaffected.

**And a new lane now chooses its PROJECT the same way** (Phase 3): one project answers itself for
free, a sentence naming a project is decided in code (folder name, a handle taught with `dodona
project-alias <name> --member <path>`, or the folder name said as words — `project zed` for
`project-zed`), otherwise the project that already holds a live lane; several live projects cost
one cheap call. When none of that answers, **the sentence is held and nothing is spawned** — a
lane opened in the wrong project is an agent reading the wrong repository, which no `lane-stop`
undoes. `project_chosen` records which rung and which evidence; a one-project workspace writes
none of it and is byte-for-byte unchanged.

## 3.2 Commands that observe, and commands that ACT

**`dodona status` used to summon a daemon. It does not any more, and that is enforced in code**
rather than warned about here — this section is the warning that failed, kept because the
incident is why the enforcement exists.

Start-on-demand means a client command summons the workspace daemon if it is not up (§2), and a
summoned daemon runs its warm-up: router, brain and the compressor pool, **each a real
`claude -p --model haiku` process**. Measured on a real wake with the default config: **four**
of them. So a "quick health check" used to start four model-backed agents on a machine the
operator believed was idle.

That happened on 2026-08-19, to a session that had just finished writing the tooling to stop
exactly this class of thing. The operator had nothing running; the agent ran `status` twice while
verifying a publish, and left behind one daemon and five haiku lanes. Two hours later it
diagnosed its own leaked shims as "machine contention" and moved a suite out of the parallel wave
on that evidence — a wrong conclusion drawn from self-inflicted noise.

**Quota was not burned, and the reason is worth knowing**: `inputs=0 results=0` on every lane.
Existing costs nothing; only a TURN costs quota (LANE-LIFECYCLE §2). It was recoverable. It was
still a machine the operator did not ask for.

A command whose name promises a reading must not change what it reads. So `status` is now on the
no-summon list beside `stop-daemon`: against a sleeping workspace it says so and starts nothing.
Three checks in `m0` hold it, each proved red — against the old build they read *"a daemon
appeared: status started one"* and *"lanes went 10 -> 11"*.

None of these start anything:

```powershell
dodona status                # now safe: reports ASLEEP rather than waking the workspace
dodona where [--json]        # ids, paths, pipe names, and whether a daemon is LIVE
dodona version [--json]      # what a binary is, including its commit
dodona ps                    # what is actually running, machine-wide
```

**These still summon, deliberately** — bringing the daemon back is what the caller wants, and the
shims have been buffering the whole time: `say`, `tail`, `input`, `lane-start`, `tickets`, and the
rest. Reach for one of those when you mean to wake a workspace, and expect the four warm-up
processes that come with it.

And when you must run a real command against a live workspace — `publish` in particular, which
§2 requires — say so in your report, because it is an action on the operator's machine and not a
measurement of it. Anything you only want to *observe* belongs in an isolated `$env:DODONA_HOME`
(§5), the same rule the suites already follow.

## 4. Never kill processes by name

```powershell
Get-Process DodonaShim | Stop-Process -Force    # NEVER
```

This murdered the operator's live session — their shim, their agent, and their open
window — in the middle of a trial. Machine-wide kills do not know which instance is a
test and which is the human's work. Resolve pids from the specific workspace's
`shim-lane*.json` instead — `dodona where` prints the directory they live in (they moved
out of `<root>/.dodona/` when workspaces landed; see §5). Tests collide with nothing (§17),
*including the instance the operator is using right now*.

**A pid is now the FALLBACK, not the first move.** `stop-all --lanes` asks each shim to go over
its own pipe (`##shutdown`), which needs no bookkeeping at all, kills the child TREE rather than
orphaning it, and lets the shim exit cleanly. That is what finally made an agent whose
`shim-lane*.json` was never written — or had already been reaped — stoppable: four such were
running on 2026-08-18, three of them out of the compiler's own output directory, and no `dodona`
command could reach any of them. The pid sweep remains, for a shim too wedged to converse.

## 5. Dodona's own state is never repo content — and now lives outside the repo entirely

Identity is a **workspace**, not a project root (`docs/WORKSPACES-CONCIERGE.md` §1): a
named, durable session group over N member folders. So state left the project tree:

- **`%LOCALAPPDATA%\Dodona\workspaces\<id>\`** — the store, its WAL twins, `shim-lane<N>.json`
- **`%LOCALAPPDATA%\Dodona\concierge\registry.db`** — workspace names, ids, aliases, members
- **`<member>\.dodona\wt\t<N>`** — ticket worktrees, the one deliberate exception: they are
  volume- and path-sensitive, and moving them buys nothing
- **`$env:DODONA_HOME`** relocates all of it. **Every suite must set it** (`tests/_workspace.ps1`
  does) or a test run litters the operator's real workspace list — and a test of the
  repo-exclusivity *refusal* could refuse one of their real repos.

`.dodona/` is still git-ignored and must stay that way — a `git add -A` once committed a
live SQLite database into this repo. Deployed gate files live in `.git/info/exclude` for the
same reason.

**Never reconstruct a store path by hand.** `dodona where [--json]` answers it; the suites
ask instead of guessing, which is what let the store move without rewriting eight of them.

The invariant that path-hash identity used to give for free is now enforcement in code
(§0's strongest form): **a git repo belongs to at most one workspace at a time** — a partial
unique index, a loud attach-time refusal carrying the `workspace-move` command, and a third
check at `ticket-create` for the bare-folder-that-later-became-a-repo case. Two workspaces
over one repo is two merge tokens over one main, which is the race this system exists to
prevent. If `tests/workspace-acceptance.ps1`'s exclusivity checks ever go red, that is a
correctness incident, not a flaky test.

## 5.1 Delivery is a skill — and so are the three traps that keep recurring

`.claude/skills/` carries what CLAUDE.md cannot: a rule that arrives **at the moment of the
action** rather than at session start. That distinction is not theoretical. §0.2's
heredoc-backslash trap was written down, had been read, and was violated three times in one
session anyway — not from disagreement but because forty minutes had passed. Alongside `/ship`:

- **`check-authoring`** — writing or editing an acceptance check. What may be asserted on
  (processes and store rows, never an instantaneous pipe read), `Invoke-StoreSql`, and the fact
  that a check is worth nothing until `dev prove` has seen it red.
- **`file-patching`** — rewriting a tracked file with a script. Backslash collapse, BOM, bare
  LF, parse-checking, and reading your own diff stat against `git diff -w`.
- **`probe-hygiene`** — launching a daemon, shim or agent by hand. Isolated `DODONA_HOME`,
  binaries from `Use-TestBinaries`, `status` is not read-only, and no machine-wide mutation
  while a verification is in flight.

**If one of these turns out to be skipped as reliably as a section of this file was, promote its
contents to enforcement and delete the skill.** Do not write a fourth one (D-6).

## 5.1.1 Delivery itself

`/ship` (`.claude/skills/ship/SKILL.md`) is the complete build → suites → commit →
publish → verify-the-swap path. Use it rather than improvising the sequence; when the
delivery process changes, change the skill in the same commit.

## 5.2 A project's git process may not be Dodona's — and worktrees have sharp edges

Some repos own their ticket lifecycle in their own CLAUDE.md and skills: branch off
`develop` with a naming convention, push, open a PR, a human reviews, the forge merges.
Dodona's §7 assumes the opposite (it performs an ff-only local merge itself), so this is a
per-repo mode — `"delivery": "pr" | "local-merge"` in that repo's `dodona.json`. The plan for
all of it is `docs/M5-DELIVERY-PLAN.md` (the authority; design §7.1/§7.2 are superseded on two
points there). Read it before touching any of this; the traps below are the short version, and
each one is a way to lose someone's work silently.

**And this is not either/or with `/ship`.** That skill's landing step is the fallback for a
project with no process of its own — it exists so a session can cope with a worktree at all,
and with a repo that never defined one. Where a project *does* define one, that process
governs, and the worktree still has to be dealt with either way. Recorded on the operator's
correction, 2026-08-20: an agent reading either half alone will skip the other.

- **A worktree's directory name and its branch name are unrelated.** `t7` can hold
  `feature/ABC-123`. Nothing outside Dodona sees the directory, so never rename a worktree
  to match a branch — short paths are a Windows `MAX_PATH` margin once an enterprise repo's
  `node_modules` sits under it.
- **`git stash` is repo-global**, one shared ref in the common dir. Two lanes stashing
  interleave one stack and `pop` takes the other lane's work. Commit WIP to your own branch
  instead — always, everywhere in this codebase.
- **`.git` in a worktree is a FILE**, not a directory. Anything doing `test -d .git` or
  reading `.git/HEAD` by hand breaks.
- **`checkout <existing-branch>` inside a worktree is the silent killer**: it fails loudly
  if that branch is checked out elsewhere, but SUCCEEDS if it is not — and the worktree then
  wanders off its branch while Dodona's recorded branch goes stale. The defence is a
  **branch lock**: a `git worktree add --no-checkout` sentinel per shared branch, which
  costs no disk and makes git refuse. Never make this depend on the operator being on main.
- Cutting a NEW branch (`checkout -b`, `switch -c`) is fine and necessary — that is the PR
  flow. The rule is "no checking out branches that already exist", never "no branch but
  main".
- **A lane DOES carry its own working directory, and half of this bullet used to deny it.**
  Corrected 2026-08-18 after reading the code (RECOVERY-PHASES P5.4 flagged the staleness;
  this settles it). `lanes.cwd` exists — the `ALTER TABLE lanes ADD COLUMN cwd` migration at
  [Store.cs:214](src/Dodona/Store.cs#L214) under schema 8, written by `Store.LaneCwd`, read
  back in `Store.LanesAll`. `AttachShimAsync` records it for **every** spawn and uses it as
  the shim's `WorkingDirectory`, and `RespawnLaneAsync` prefers the recorded value, falling
  back to `_primary` only for a lane older than the column. Landed by `f9aaf25` — which is
  itself the cross-session carry that D-7 now prevents.
  **Corrected again 2026-08-19 (LOCATIONS-PLAN Phase 2).** This used to end "`SpawnAgentLaneAsync`
  passes `_primary`, and so does the plain `lane-start` path, so a fresh agent lane *does* run in
  the operator's live tree". Half of that is now wrong: **a lane opens in a project it is given**
  — `SpawnAgentLaneAsync(title, project, …)` has no default, `dodona lane-start --project <path>`
  chooses one, a folder no project owns is refused, and the project picks the lane's
  `permissionMode`/`allowedTools` (`Config.For`) and is written into its system prompt from the
  same single parameter. **A typed sentence now chooses too** (Phase 3, 2026-08-19): the project
  comes from a ladder — one project answers itself, a sentence that NAMES a project (its folder
  name, a handle taught with `dodona project-alias`, or the folder name said as words) is decided
  in code for free, otherwise the project holding a live lane, and on genuine uncertainty the
  sentence is **HELD and you are asked** rather than aimed at the first project.
  What is **still true and still the danger**: with one project — which is what the operator has —
  every lane still opens in their live tree, so a plain lane is in a SHARED checkout and **must
  still never check out a branch**. Giving each lane a tree of its own is still unbuilt, and is
  still a spawn-site change rather than a schema one.

## 6. Where things are written down

- `docs/ORCHESTRATOR-DESIGN.md` — **the authority.** Every `§n` in this codebase (§8
  attention, §11 lifecycle, §17 testing) points here.
- `docs/ORCHESTRATOR-REVIEW.md` — the milestone plan, the measurements, what is carried.
- `docs/WORK-ISOLATION-PLAN.md` — work isolation: no agent writes into a project outside a
  worktree, and the refused write promotes itself into one. Layers 1 and 2 are BUILT.
- `docs/REVIEW-AND-MERGE-PLAN.md` — how a ticket's work is reviewed and lands: the ordinary
  bring-main-in-then-fast-forward flow, and a manager that reviews the work and can send it
  back but **never approve**. Supersedes the declared-claims-as-a-lock idea entirely.
- `docs/LANE-LIFECYCLE.md` — decisions already taken and **ideas already rejected** about
  closing lanes and the attention model. Read before proposing either.
- `DEBUGGING.md` — the schema, every event kind, the pipes, and how to read a store with
  nothing running. Start here when something looks wrong.

The design docs are copies; the masters live in `..\MassWorks\`. They were copied in
because a lane works from `<root>\.dodona\wt\t<N>`, where the old `..\MassWorks\` path
resolves to nothing — see `docs/README.md`.

## 7. Permissions: a lane cannot ask

The operator's own session has a permission-prompt tool wired to a dialog, so an
unapproved command becomes a question. **A lane has no such channel** — headless `-p`
denies outright, and the agent strands mid-task: it edits fine, then cannot build what it
edited. So lanes default to `bypassPermissions`, matching what the operator's IDE grants
in auto mode.

**That does not loosen Dodona's guarantees**, and it is measured, not assumed: a PreToolUse
hook still fires under `bypassPermissions`. The claim gate *is* a PreToolUse hook, so a
ticket lane is still bounded to its claim, and the merge-time diff backstop still refuses
anything that slips. The safety model never rested on Claude's permission prompt — it
rests on the gate (§6 layer 1) and the fence (§6 layer 2).

A project that wants a leash sets `"permissionMode": "acceptEdits"` in `dodona.json` plus
an `allowedTools` list. Be aware that list is leakier than it looks: `PowerShell(dotnet
build:*)` still loses to `dotnet build ... | Select-Object`, because a pipeline counts as
multiple operations. If a command is denied, that is the environment, not you being
wrong — report it as the headline (rule 1) and name the exact command.

Comments in this codebase explain *why*, and often name the incident that caused the
line. Keep that habit: the next person to read it is debugging at speed.
