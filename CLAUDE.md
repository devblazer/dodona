# Working in Dodona

Dodona orchestrates Claude Code agents. You are probably one, working in a lane. Every rule here
cost real time to learn and carries its reason — a rule with no reason looks arbitrary and gets
overridden. **Written compressed on purpose (§8): terse, tables, one line per fact.**

## 0. The repo is the only memory

**Do not rely on session memory or recalled context** — it loads for some sessions and not others,
and a lane in a worktree sees none of it. Operator's rule: *skills + CLAUDE.md, or tooling that
enforces, is the only reliable way.* Knowledge worth keeping goes in exactly one of:

| where | why |
|---|---|
| **enforcement in code** | strongest — the claim gate, merge backstop and drift watcher exist because instructions get skipped and code does not |
| **this file** | rules + traps needed before touching anything |
| **`.claude/skills/`** | invocable workflows (§5.1) |
| **`docs/`, `DEBUGGING.md`** | design authority, decisions *and rejections*, schema |
| **commit messages** | the incident history of record — write so a stranger can reconstruct what and why |

Learn something load-bearing mid-task → put it in one of those **in the same commit as the work**.
Anywhere else is a lesson the next session re-learns expensively.

### 0.0 Work in a tree of your own — git refuses commits from the shared one

**The shared checkout is a source of truth, not a workspace.**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 worktree <name>
# -> .claude\worktrees\<name>, own bin and obj. cd there and work.
```

Why: `f9aaf25` committed another lane's migration along with its own fix — both sat in one tree, and
`git add` cannot tell whose edit is whose.

- **A `pre-commit` hook aborts any commit made *from* the shared checkout.** Git runs it, so heredoc,
  `sed` and shell redirect are all caught — they all reach a commit eventually.
  `DODONA_ALLOW_MAIN_TREE=1` lifts it for a deliberate exception (a release commit).
- **`.githooks/pre-commit` is the tracked source; `tools/dev.ps1` copies it into `.git/hooks/` on
  every run** — `.git` is never cloned, and an install step someone must remember is not enforcement.
  Edit the tracked file, never the copy.
- **Do not switch to `core.hooksPath`.** Tried; a shared-checkout commit then succeeded. A tracked
  hooks dir only exists on branches carrying it, so it vanishes on every other branch and on every
  historical commit you check out while bisecting. Branch-independence is the property that matters.
- **Do not reintroduce a per-edit `PreToolUse` hook for this.** The deleted one cost **255 ms per
  edit** (136 ms just starting PowerShell) and its `git add -A` sibling cost the same on **every
  shell command** — a permanent tax for rules that should fire ~never — and neither could see a
  shell redirect. What they guarded is now structural: with one tree per session, a broad `git add`
  can only sweep that session's own files.

**The limit:** this binds anything reaching a **commit**, not a stray edit before then. Deliberate —
the edit is recoverable; the commit is what carried someone else's work into history. `dev gate`
asserts the hook is present *and* wired, because an enforcement that quietly stops enforcing is the
failure this project keeps paying for (§3's dead routing ladder; and this lock's own first version,
which spent part of its session unable to parse, denying nothing while looking installed).

### 0.1 How the operator works

- **Goals, not metric specs.** "Make it feel instant" is the requirement; deriving budgets is your
  job. Do not ask them to quantify.
- **Quota is the scarce resource:** suites stay model-free, real-model runs are rare and deliberate,
  router/compressor stay cheap, no subagent swarms where one session will do.

- **THE HEAVY SUITES ARE NOT AUTHORIZED AS A DEFAULT — ANYWHERE** (2026-08-20): *"I don't have the
  bandwidth to run those heavy handed test suites all the time. Run it sparsely only when you
  actually absolutely have to. And then when you do run it, don't run everything. Run modules that
  matter."* Corrects a plan that made `dev gate` the verify step on every land (`REVIEW-AND-MERGE-
  PLAN` D-6/§11).
  - **A full run needs a reason that names itself.** "Before merging to main" is the one standing
    case (§1) — once per merge, not per phase or edit.
  - **Automated verification never gets the full set.** A land's `verify`, a watcher, a hook: run the
    modules that matter and say which. `dodona.json`'s `//verify` block carries the reasoning.
  - **Do not propose widening it back** because a subset might miss something. It might; that is the
    trade, made twice, knowingly.

- **Speed beats thoroughness where they conflict — a decision, not a preference** (2026-08-21):
  *"We don't wanna hold agents up forever, because your costs are gonna balloon dramatically… A
  person that's serious about reviews can ask for it themselves… Move fast and break things."* Said
  of the manager's review (D-R23) and it generalises: **anything automatic that reads more, waits
  longer or checks harder spends the scarce resource to buy thoroughness nobody asked for.** Depth
  is available on request. So anything automatic defaults to the cheap pass with a way to ask for
  more, and **a missed catch is the accepted cost** — the operator approves every irreversible step
  anyway. Do not propose widening an automatic reader because it might miss something. It will.

- **AN OUTDATED OR UNNEEDED TEST IS A DEFECT, AND FINDING IT IS PART OF THE JOB** (2026-08-22):
  *"…make sure none of them are outdated or no longer needed… there might be a bunch of shit there
  that's either completely wrong or just no longer even needed"*, alongside *"I fix any errors along
  the way"* — fix what you find en route rather than routing around it.

  **This does NOT repeal `TEST-ARCHITECTURE-PLAN`'s no-coverage-lost rule.** It opens one narrow
  evidenced hole, as enforcement (that plan §5.4.1, D-T32): a check may be dropped only as an
  **`obsolete`** row in `tests/ledger/moves/<slice>.tsv`, and `dev ledger` refuses one without
  evidence in exactly four forms — **subject is gone** (cite the commit or the file no longer holding
  the symbol) · **assertion cannot fail** (`dev prove --with` returns VACUOUS under a real defect in
  the thing the check names) · **contradicts current behaviour** · **exact duplicate of a NAMED
  survivor that still runs**. *"It looks redundant" / "it seems old" / "we probably don't need it"*
  are refused by name, in those words, by the tool.

  Every such row is reported, never silent: it carries what would be LOST if the judgement is wrong,
  is named in the commit message and to the operator, is counted on its own line by `dev ledger
  --verdict` (never folded into `moved`/`stays` — it is the only number meaning coverage went down),
  and `git revert` puts the check back. **Forbidden:** porting a dead assertion faithfully down a
  layer to stay inside the rule — that spends a seam and a mutant preserving something untrue.

- **Act, announce, allow undo** (§11): make the routine call, say what you did, keep it reversible.
  Blocking questions are for genuinely unsafe forks only.
- **Feedback like "that's bullshit" is a decision** — record it (`LANE-LIFECYCLE` §2 style: *with the
  reason*) so it is never re-proposed.

- **`..\MassWorks\` IS THE OPERATOR'S; YOU DO NOT WORK IN IT** (2026-08-21): *"you're not allowed to
  mess with the MassWorks project unless it's to investigate something… I will use Dodona for
  MassWorks… But you're not doing anything there yourself."* No `dodona` command pointed at it, no
  workspace registered, no lane opened, no file written, no repo initialised, no branch touched —
  **unless they just asked you to look at something specific, and then only that looking.** Two
  drivers over one project is the same hazard class as two workspaces over one repo (§5). *Reading a
  master doc there is the one permitted act (§6).*

  *Incident:* a session answering *"is Dodona running anything in MassWorks?"* ran `dodona where
  --root <MassWorks>` — listed in §3.2 under **commands that observe** — and it **registered
  MassWorks as a workspace**. **The general trap outlives the case: "does not summon a daemon" and
  "does not write" are DIFFERENT PROPERTIES**, and §3.2 only ever promised the first while reading as
  though it promised both. Closed in code (issue #12); summoning is not (issue #13).

- **Never hung, halted, stuck, or outdated** (2026-08-18). Anything that parks behind a question,
  waits on a human who did not opt into waiting, or goes quietly stale is a bug, not a safety
  feature. Pattern: make it reversible (back up, log, announce the undo), then act. Updates arm
  themselves; migrations back up and proceed; a process dying at startup leaves a line in
  `<DODONA_HOME>\logs\daemon-start.log`. **When you add a wait, name the thing that un-sticks it — a
  condition, never a person.**

### 0.2 Windows & PS 5.1 traps (each cost a debugging round)

| trap | consequence / fix |
|---|---|
| **Non-ASCII literals in `.ps1`** (`✓ — ⚠`) | read as ANSI in BOM-less files, match nothing → build from `[char]0x2713` / `[char]0x2014` |
| **Native stderr** | capturable only with `$ErrorActionPreference='Continue'` + `2> file`. `Stop` throws NativeCommandError; `SilentlyContinue` eats the record |
| **Captured native stderr is WRAPPED to console width** | a newline lands mid-sentence, so a regex spanning a space matches today and fails when a path grows. Collapse first: `($out -replace '\s+',' ') -match '...'`. Turned a repo-exclusivity check red while the product refused correctly — a **false red**, which costs as much as a false green |
| **`-shl` on `[byte]` stays a byte** | overflows to 0 → cast `[int]` first |
| **Commit messages with quotes/dashes** | `git commit -F <file>`, never inline `-m` |
| **`.Count` on a one-element pipeline is `$null`** | wrap in `@(...)` |
| **`$pid` is read-only automatic** | `foreach ($pid in ...)` throws, loop body never runs → name it `$procId` |
| **`ConvertFrom-Json` emits a JSON ARRAY as ONE pipeline item** | so `\| Where-Object {…}` filters the array object, and `$_.name -eq 'x'` on an array returns matching *elements* → truthy → **every row passes**. Land it in a variable first. Silently no-opped three checks |
| **A `.ps1` that fails to PARSE never reaches `finally`** | everything it started leaks |
| **`function f([string]$x)` SILENTLY SWALLOWS extra args into `$args`** | `$(Rows "SELECT …" -replace '\s+',' ')` runs the query, never the `-replace`, and errors nowhere. Parenthesise: `$((Rows "…") -replace …)` |
| **A `Store` migration that THROWS kills the daemon in its constructor** | before the control pipe exists → `Wait-Daemon` times out and every check in the section goes red pointing at the wrong thing. A fixture restoring an older shape must drop the columns of **every** later version, keyed on the column existing — never on a version number, since the same suite runs under `dev prove` against a build without it |
| **WPF** | implicit usings omit `System.IO`; with `AcceptsReturn` the TextBox class handler eats Enter before instance `KeyDown` (use `PreviewKeyDown`); `RenderTargetBitmap` renders in the element's own coordinate space (capture the Window, not a margined child) |
| **Redirected child stdio defaults to the OEM codepage** | set UTF-8 or em dashes become `ΓÇö` |
| **`Microsoft.Data.Sqlite`** | `INSERT …; SELECT last_insert_rowid();` in one command returns nothing without `NextResult()` → use a separate command |
| **`python` resolves to the Store app-exec alias first** (`WindowsApps\python.exe`) | a start can fail with **no stdout, no stderr and no error record** — indistinguishable from a query returning nothing. Seen once mid-wave after ~20 good calls; throwing on it killed `brain` entire (38 checks unrun, gate read `NO TALLY LINE`). `Invoke-StoreSql` retries once and **prints** either way |

**A NAMED PIPE'S NAME BLINKS OUT while its server swaps instances**, so **a single instantaneous read
is not a liveness test**. Measured: **8 of 192 reads over 1.5 s saw no pipe** while the shim was
alive and instantly connectable — and the gap is *synchronised* (every shim disconnects when its
daemon exits; the next reconcile runs milliseconds later), which is what makes it dangerous. Lane
liveness is therefore the UNION of two OS answers — the pipe, or a recorded shim pid that is alive
(`LaneLiveness`) — and `stop-all` picks targets BEFORE stopping any daemon, because an attached pipe
is a steady one. Nearly shipped as "reconcile declares every lane dead on restart".

### 0.3 Problems are not allowed to exist (operator directive, 2026-08-18)

**A snag is not something to work around. It is something to eliminate.** Removing it becomes the
task, and it ends **in the session you hit it** in exactly one of: **enforcement in code**
(strongest), **a tool** making the wrong thing impossible or the right thing instant, or **a rule
here** — only when neither of the above can hold it.

Forbidden, each having happened in one afternoon and cost an hour on a fifteen-minute change:

- **Nursing a broken environment instead of fixing it.** Four daemons held the compiler's output, so
  a second copy of the tree was built in temp and everything ran twice. The fix was one command that
  clears the holder, and a `ps` that stops lying about what is running.
- **Working around the same snag twice.** Once is information; twice means you chose the workaround.
- **Reporting a snag as if it were the work's fault.** "Build FAILED" that means "an invisible daemon
  holds a file" sends the next reader hunting through their own code.
- **Believing a green check.** A new check is worth nothing until seen red against the code it
  catches; `dev prove` makes this mechanical rather than remembered.
- **Documenting instead of fixing.** §3.1 already recorded "a daemon outlives its window"; it
  happened again anyway, to the same person, from the same cause. **A written warning is not a fix.**
  If the honest answer is "the next session will read this and be careful", the answer is wrong.

Operator's phrasing, kept because it is the standard: *problems are not allowed to exist.*

## 1. Everything mechanical goes through `tools/dev.ps1`

**Do not call `dotnet build`, a suite, or `publish` directly.**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 <verb>
```

| verb | for |
|---|---|
| `check` | Can this tree build? What is in the way? Seconds. **Run before starting, not after** |
| `build` | Only *real* compile errors reach you; a locked output is named, never mistaken for one |
| `test <suite>...` | Named suites, concurrently (`--sequential` for one at a time). **IT DOES NOT BUILD** — run `dev build` first or you test the PREVIOUS binary |
| `test unit` | Pure logic, no daemon/store/window. ~2 s; run it while you edit |
| `suites` | All seventeen, **three at a time** |
| `prove <suite> <check>` | Demands a new check FAILS against HEAD. PROVEN / VACUOUS (rewrite it) / MISSING (it never ran) |
| `prove <suite>:<check> ...` | Same for MANY: grouped by suite, **one run per suite**. **Default to this form** — Phase 3 ran m0 eleven times to read eleven lines of one run's output, 46 min for what is 40 s |
| `lint` | Control bytes, dangling `tests\*.ps1` refs in docs, mixed line endings, all of `dev ledger`'s static side, the double ledger's rung 1, every wire command declared. ~1.5–2.5 s. **Tracked AND untracked** (issue #15) — `.gitignore` is the filter, and the verdict line states its scope |
| `gate` | The pre-commit gate: the suites plus **ten asserted invariants**. `dev gate <suite>` runs the same machinery over less (~20 s), saying PARTIAL on every line |
| `ship` | build + suites + publish |
| `worktree <name>` | a tree of your own (§0.0) |

The ten gate invariants: nothing left running in the build output · a suite run that dirtied nothing
· **no wrapper or agent process that outlived the run** (I3) · the commit guard deployed and
unoverridden · the live build's commit resolvable in `git log` · the run inside its time budget (I7)
· **no changed file quietly altering its BOM or line endings** (P7.5 — happened three times, twice
unnoticed until someone read a diff) · `dev lint` asserted (which is why the count stays at ten
rather than growing an eleventh row). Asserting all of `RECOVERY-PHASES` §2 is **not** the same as
proving the system works — the verdict line says "on the 10 assertions above, and only those" on
purpose.

**It is a script, not a `dodona` subcommand, deliberately:** a tool whose job is to fix a blocked or
broken build cannot itself require a build.

**Why mandatory rather than convenient:** raw commands hand you the wrong diagnosis. `dotnet build`
reports a locked output as `Build FAILED` with ten screens of MSB3026 retries — reads as "your code
is broken", means "a daemon you cannot see holds a file". The wrapper names the pid and the freeing
command on line one. It stops nothing on your behalf (`stop-all` is machine-wide, so clearing a
holder is always your explicit call). Every run logs to `.dodona\dev-logs`.

**An edit that has not been built is a claim, not a change** — the most expensive mistake in this
project's history, twice: a lane reported a rewritten input box as done having never compiled it
(and the code held a real Enter-handling bug one build would have caught); and 28 minutes of UI work
sat unbuilt and unpublished while the operator restarted the app wondering why it looked the same.
**If you cannot build — permission denied, an uncleared lock, anything — say so as the headline of
your reply**, not a footnote, and name the exact command.

### Iterate fast, gate slow — and "slow" is ~2 minutes, not twenty

*"Ban any test that takes longer than a second or two. Twenty minutes of test is ridiculous."*

This section once claimed twenty minutes and that the time was inherent. Measured 2026-08-19: eleven
suites took **5 min 20 s**, of which **214 s — 68 % — was fixed `Start-Sleep`**. A `Start-Sleep
-Seconds 3` is a guess about the slowest machine that ever ran it, paid on every machine since,
while the condition it waits for is written one line below. **That wrong number is why verification
became a thing to skip rather than fix — the root of every believed-a-green-check incident in §0.3.
A measurement you did not take is not a measurement.**

| | before | after |
|---|---|---|
| full run | 5 min 20 s, and could **hang forever** | **~116 s** (was 54–72 s: concurrency 5→3, plus ~105 earned checks). Gate budget raised **120 → 180 s** against that — earned coverage growth must not present as a gate failure |
| sequential | ~320 s | ~200 s |
| fixed `Start-Sleep` | 214 s | ~4 s |
| narrowest useful check | ~7 s (a daemon must start) | **~2 s** (`dev test unit`) |

Three changes did it, in `tools/dev.ps1` and `tests/_workspace.ps1`:

- **`Wait-Until` (condition + deadline) replaced the sleeps.** Every wait names what un-sticks it
  (§0.1); a condition-wait with no deadline would be that directive in a new costume, so timing out
  is a normal return that prints one line and lets the following check fail on its own terms.
- **Suites run THREE at a time**, each its own process with its own `DODONA_HOME`. At five and at
  twelve, `ui-use` went intermittently red (61 s green, then 149 s and 119 s red on a quiet machine)
  and its failures cascade, so two missed interactions become six red checks. The contention is
  windows and process starts, not CPU (22 cores, never bound); **root cause not established**.
  `DODONA_TEST_CONCURRENCY` overrides; `--sequential` is the debugging escape hatch.
- **`dev test unit`** runs the pure logic in ~2 s. **Do not hand-maintain the check count here — the
  suite prints it every run.** This row carried a stale figure three times (54 → 88 → 189, all wrong
  when read), which is §1's own failure inside the section about it.
- **`dev test`, `dev suites` and `dev gate` REFUSE a stale build output** (P1.5). None compile, and
  every suite copies binaries out of `src\*\bin\Release`, so an edited-but-unbuilt tree verified
  green: a line deleted from `Daemon.cs`, `dev test m0` reporting *26 checks, 0 failed*, and
  `dev build` + the same command reporting 1 failed — **a false green from the tool itself**. Costs
  ~30 ms and compares **each project against its own assembly** (tree-wide is the question
  auto-publish looped 64 times on, §2). `dev prove` is exempt: it builds its own baseline.

#### RUN THE SUITES YOUR CHANGE TOUCHES. THE FULL SET IS FOR MERGING.

**The default, not an optimisation.** Reaching for `dev suites`/`dev gate` on every edit is how
verification became something to skip; 80 s spent twenty times is worse than 80 s spent once.

| changed | run |
|---|---|
| a pure function (claims, policy, repo resolution, paths, routing verdicts) | `dev test unit` (~2 s) |
| anything pure in `src\DodonaUi`, or any test double anywhere | `dev test ui-unit` (~4.5 s) + `dev lint` |
| daemon lifetime, reconnect, drain | `dev test m0` |
| the write gate, merge token, land flow, completion record | `dev test m1` |
| a lane's system prompt or per-turn briefing | `dev test unit m1` |
| routing, presence, the recorded branch touch | `dev test m2` |
| the UI as a view over the store | `dev test m3` |
| publish, hot swap, provenance | `dev test m4 publish` |
| workspaces, members, repo exclusivity | `dev test workspace` |
| box, panes, tiles — anything clicked or typed in ONE workspace | `dev test ui-grid` |
| one window over N workspaces: bands, merged feed, boot-to-zero | `dev test ui-shell` |
| the overlay that ASKS | `dev test ui-ask` |
| a window over a SLEEPING workspace, the five lane-tile actions | `dev test ui-wake` |
| dictation | `dev test voice` (~13 s, opens no microphone) |
| compression | `dev test compression` |
| the dispatcher brain, routing ladder, manager review | `dev test brain` |
| the concierge, the fence | `dev test concierge` |
| `tools/dev.ps1` itself | `dev gate m0` (~20 s) |

- **Iterating:** `dev test unit`, then the one or two suites your change touches. Anything starting a
  daemon has a ~7 s floor — the honest target.
- **Before you MERGE to main:** `dev gate`, once. The only moment the full set is required; skipping
  it is how a stale test survives for months (two did).
- **Three consecutive failed verification attempts:** stop and report. Do not grind.
- **A suite you did not touch goes red → suspect the machine before the code.** `dev gate` prints the
  leaked-process count first: with strays alive a full run went 87 s → 300 s and reddened thirteen
  checks in suites nobody had edited.

**A suite that does not print `<N> checks, <M> failed` is a FAILURE, not a shrug.** `m0` never
printed a tally, so `dev.ps1` could not detect an m0 failure at all; `ui-use` died inside its own
`finally` on a stray stderr line (§0.2) — 74 checks computed, discarded, reported as `no tally line`,
counted as nothing. Both looked green and both were blind.

**And `dev suites` could hang forever — very probably what was killed three times as "too slow".**
Reading a child's stdout through a pipe ends when the last handle to the write end closes, not when
the child exits, and every process a suite spawns inherits that handle; `publish-acceptance` leaks
four shims whose only exit is a message from a daemon already gone. Measured: eight minutes of
waiting after the results had printed, and it would never have ended. Suite output goes to **files**
now, and every suite has a deadline.

## 2. Completed work gets published — and the daemon enforces this itself

With `"autoPublish": true` (on for this repo) the daemon compares **`git rev-parse main` against the
commit the running build was made from**; when they differ it builds *that commit* — in a detached
worktree of its own, never your tree — and swaps itself to it. A failed build changes nothing and is
announced loudly. This exists because edited-not-built, built-not-published and published-not-
committed each blocked the operator once in a single day, and **an instruction in this file is
advisory while a watcher is not**.

**Only `main` publishes itself** (D-1) — uncommitted work cannot reach the app at all, which is the
point: any session's half-finished edit used to be able to. Trial deliberately with `dodona publish
--from <ref|worktree>`; it is stamped `trial: <branch>@<sha>` and **the next commit to `main`
replaces it**, because the trial build carries the `main` SHA it was cut against.

That leaves for YOU: still build before reporting (§1 — the watcher publishing your broken edit
produces a loud failure with your name on it), still commit (it nags, it does not commit), and still
run publish yourself when asked or when you want the swap *now* rather than at the next 15 s poll.

**Finishing work — or being asked to publish — means running publish.** Not "mention that it could
be", not leaving it built-but-installed-nowhere: the operator sees Dodona through the installed app,
and work that never reaches it does not exist from where they sit.

```powershell
# Resolve the INSTALLED binary: newest build that is actually COMPLETE.
# `dodona.dll` is the test, not decoration -- a versioned directory holding only `dodona.exe` exists
# on this machine twice over (a publish that raced another). Newest-by-name then resolves to a stamp
# whose .dll is missing: "The application to execute does not exist".
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'dodona.dll') } |
    Sort-Object Name | Select-Object -Last 1).FullName 'dodona.exe'
& $dodona publish --project . --all
```

**Never `.\src\Dodona\bin\Release\net8.0\dodona.exe`** — what this file said until 2026-08-18, and
how a daemon ends up holding the compiler's own output: the instruction *caused* the failure it warns
about elsewhere. The binary now refuses to autostart from a source-tree build output
(`Ver.IsSourceTreeBuildOutput`); `%LOCALAPPDATA%\Dodona\bin\` and a suite's `$DODONA_HOME\bin` are
allowed — a build output is the problem, not the word "bin".

Nothing installed yet? `dev.ps1 ship` bootstraps it — the single exception, because `publish` is a
transient CLI that exits, not a daemon that outlives your window. It builds all three executables
into a fresh versioned directory, hands off **without interrupting a live agent mid-turn** (M4), and
re-points the shortcut (which launches `DodonaUi.exe --shell`). Safe to run while the operator is
working — the whole point of the hot swap. A running UI window hot-swaps too, through `ui update`
after the daemons; it hands off to the new build or stays if the successor never answers.

`--all` means **every live workspace in the registry, plus the concierge** — resolved by id, never by
scraping `dodona-*-ctl` pipes off the OS, so a live daemon belonging to no registered workspace is
never a swap target (which is what made `publish-acceptance.ps1` possible). Narrower: `--workspace
<name>...`, `--concierge`; with neither, the workspace owning `--project`.

**PUBLISH REPORTS OUTCOMES, NOT INTENTIONS, AND READING ITS OUTPUT IS PART OF PUBLISHING** (issue
#9). It used to announce a swap *before* the call and say nothing after, so a target that refused,
errored or ignored it was never named — and `accepted` needs only ONE target to succeed for the
shortcut to move. That is how the concierge ran a **two-day-old build** across many publishes: it
had no `swap` command and no `default:` branch, and **silence at the wire is indistinguishable from
success**. Every target now prints a verdict — *took this build* / *armed* / *DID NOT SWAP* /
**ANSWERED NOTHING** — and **publish exits non-zero** when anyone did not take it. The
answered-nothing test generalises: it catches the next silent no-op whatever causes it. **After any
publish, read the per-target lines** — a green-looking publish with one `ANSWERED NOTHING` is a
process still running the old code.

**Publishing does not commit.** Commit too, or the work is published and then lost on the next
checkout.

Publish **verifies before it promotes**: the new binary must answer `version --json`, and the
shortcut is repointed only after a daemon accepted the build. Fourteen consecutive auto-publishes of
a broken tree once each repointed the shortcut at a binary whose daemon died on startup — the front
door itself rotted, and every project open froze against it.

**Auto-publish asks an exact question — two SHAs — and the five guards that made an inexact one
behave are deleted.** It once looped 64 times in an afternoon because "is any source newer than the
running image?" is not answerable by a filesystem: the newest source spanned **all three** projects
while the image was **one**, leaving a condition that could never be satisfied. So:

- The commit is compiled **into the binary** (`InformationalVersion` → `Ver.Commit`) — no stamp file
  to lose, and what is running is checkable against `git log` and bisectable.
- **A build with no provenance refuses to watch, out loud.** `dev build` images and `publish --exe
  <prebuilt>` compiled nothing, so they know no commit — and the old code *degraded to the mtime
  compare* exactly there, the bug wearing a fallback. Any `publish` from a git checkout arms it.
- **Kept:** surrender after three consecutive failures; a broken `main` must not rebuild forever.

Two stamp traps: the **dotnet CLI splits a `-p:k=v` value on commas**, so a comma-separated stamp
arrives truncated with no error anywhere → the separator is `~`, which git forbids in a ref name. And
the SDK appends its own `.<SourceRevisionId>` unless
`IncludeSourceRevisionInInformationalVersion=false` → `Ver` accepts only its own `c=` marker.

Blocked swaps **arm themselves** instead of asking (`swap-answer now` forces, `hold` parks — holding
is opt-in, waiting never is), and a schema-migrating swap **backs up the store and proceeds**
(announced with the restore path; only downgrades refuse). Design §14 records the revision.

## 3. Verify with the suites, not by looking

Seventeen model-free suites, all fake agents, all free. **Run them through `dev test`, never by
invoking the `.ps1` directly** — the wrapper is what makes a suite that crashed, hung or never
reported a FAILURE rather than a blank line (P4.4).

| suite | covers |
|---|---|
| `unit` | pure logic: claim algebra, policy table, repo resolution, canonical paths, code-only routing decisions; double ledger rung 2 over `Dodona` |
| `ui-unit` | the same one-second loop for the WINDOW's assembly, which `unit` cannot load at all (net8.0 vs net8.0-windows): double ledger rung 2 over `DodonaUi`, and `RecognizerContract` — one body run against `FakeRecognizer` and against the real `DeepgramRecognizer` at a closed loopback port. No window, socket or microphone |
| `m0` | daemon death mid-turn; a wrapper outliving its agent, a lane with no shim record, the lease, reconcile asking the OS |
| `m1` | the write gate (layer 1), the merge token, the land (merge main in, verify, fast-forward, dropped-nothing, all **off the control pipe**); the completion record — one per worktree change, not per turn, from a lane the daemon ADOPTED; the lane briefing — reaches the AGENT never the operator's feed, differs by lane kind, rebuilt when layer 2 promotes a lane |
| `m2` | routing, presence, what a branch touched (recorded, not judged) |
| `m3` | the UI as a view over the store |
| `m4` | hot swap (runs a REAL build — the slow one) |
| `workspace` | identity, repo-exclusivity, multi-repo |
| `ui-grid` | the UI driven like a person: the box and how it grows, panes, model/effort policy, attention badges, close/collapse/expand |
| `ui-shell` | one window over N workspaces (§3.1): boot-to-zero, the bare launch, a band, clicking a band, the feed as a labelled union, a live shell hot-swapping |
| `ui-ask` | the window ASKS, and it is not a dialog: one component, two render modes, one answer path, over all three question kinds real code produces |
| `ui-wake` | the WINDOW OVER A SLEEPING WORKSPACE — the mirror of "a daemon outlives its window" — plus the five lane-tile actions, which had no `ui` verb at all until added there |
| `compression` | selective compression (§5) |
| `brain` | the dispatcher brain, its routing ladder, the no-second-brain guard, and the manager reviewing finished work: may send back, bounded at three, **can never approve**. May ask to READ a named changed file — once per review, refused if not in the record's own list — and a send-back on a `verify: red` record spends no round, decided from the RECORD never from the reviewer's stated reason |
| `concierge` | the group-scope ladder, the fence, the review-behind |
| `publish` | targeting: `--all` spares foreign instances; a swapped concierge reports the NEW build; a target answering nothing is NAMED and fails the publish (fixture: a bare named-pipe server on a registered ctl pipe — what a silent no-op looks like at the wire) |
| `voice` | dictation: speech composes and can never send |

**Per-suite durations are deliberately not listed (P5.2).** They drifted — two wrong by more than
double — and a table nobody can trust is worse than none, because it still gets quoted. `dev suites`
and `dev test` print the real ones on the machine you are using.

**Three suites run ALONE, and taking one off that list will look like it works.** `SoloSuites` in
`tools/dev.ps1` carries each reason:

| suite | why solo |
|---|---|
| `unit` | it compiles — `dotnet test` builds Dodona into `src\Dodona\bin`, where every other suite copies its binaries from. Two compilers, one directory |
| `ui-unit` | the same one project along (`src\DodonaUi\bin`, source for all four `ui-*` and `voice`). ~4.5 s of serialized wall clock on a 258–312 s run |
| `voice` | a window suite whose failures CASCADE — measured **70.5 s red on three checks** in a wave (the mic toggle did not take within its 20 s wait) and **40.3 s green** alone minutes later, same tree. Solo means nothing ran beside it, so the crowd is not concurrent suites — it is what a full wave leaves behind (windows, process starts, the pipe namespace) still settling. Not root-caused |

**Treat a red window suite inside a wave as a machine reading until it reproduces alone, and do not
raise a budget to cover it.**

Two removals, with their measurements, live in `tools/dev.ps1` beside `SoloSuites`; the transferable
parts: **`ui-use` was split into the four `ui-*`** (issue #2) and **a split only pays if the pieces
run concurrently** — four solo pieces are four fixture setups where there was one, and sequentially
cost **134.3 s against the monolith's 88.8 s**, so if one ever has to go solo, do not move the other
three with it. **`m1` rejoined the wave** (issue #4) on seven consecutive gates at 135 checks, 0
failed; its old intermittent is **still not explained**, only unseen — read `.dodona-bypass.log`
first if it returns.

**Do not "tidy up" `SoloSuites` because a suite looks fast enough to parallelise, and do not add to it
because one looks risky.** Both directions are a measurement; m1's took four full gate runs. A gate
red one run in four for an undiagnosed reason costs more than either number, because it teaches
people to re-run instead of read — the same disease as a gate that is always green.

**A m1 failure once looked flaky and was not: the claim gate was failing OPEN on every run.** Stdin
reached `GateHook` with a **UTF-8 BOM** (PS 5.1 writes them by default, §0.2, and `Console.In` hands
U+FEFF back as an ordinary character), `JsonDocument.Parse` refused it, and the fail-open branch
allowed the write **without ever asking the claim algebra**. Two lessons worth more than the fix:
**a fail-open path must be read as a red flag whenever a suite goes red near it**, because the
failing check is the only thing between it and silence; and a loud diagnostic beats a silent
`return 0`, which hides the next one exactly as long.

**Waits are conditions, not sleeps.** `Wait-Until { <condition> } <timeoutMs> '<what>'` in
`tests/_workspace.ps1`; `Wait-Daemon` is the common case. Do not add a `Start-Sleep` — the four
survivors are each a real duration (a fake agent's own turn length) and say so in a comment.
`Wait-Until` always has a deadline (§0.1) and returns `$false` rather than throwing, so the check
that follows fails on its own terms and prints the real value it saw — a better diagnosis than a
wait's idea of what went wrong.

**A suite that builds the thing it tests proves nothing about the wiring.** The routing ladder was
fully covered and green while **dead in production for two days**: the suite stood up its own
`role='router'` classifier and proved the ladder against it, but nothing in the daemon ever created
one, so every sentence the operator typed fell to `no-classifier` — 14 routed inputs, **zero**
`classified` events, while `dodona status` printed `router: model=haiku` for a lane that had never
existed. Two rules, which generalize past routing:

- **Ensure at the point of use, never look up.** A lookup that misses is indistinguishable from one
  that was never going to hit. After `EnsureRouterAsync`, "no classifier" can only mean *switched off
  in config* or *the spawn failed*, and both say so out loud.
- **Every suite must exercise at least one path the way the OPERATOR runs it** — autostart on,
  nothing pre-built by the test. Verified by reintroducing the defect: those checks go red with the
  live store's exact signature, `focus|no-classifier`.

**A silent degrade is a bug** (§0.1's "quietly stale"): the only evidence of two dead days was a
status-line suffix nobody reads. The fallback now announces itself once per daemon and writes a
`routing_unrouted` event.

### The UI, driven headlessly

The four `ui-*` suites matter most for UI work: dumps and screenshots prove the UI *reports*
correctly while the first thing a person tries is still a dead end. **Add an interactive affordance →
add a check in whichever suite owns that surface**, not only a dump assertion.

```powershell
# visual work: capture, do not describe pixels
.\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe --root <project> --pose long --shot out.png
```

**Every UI you launch for a test gets `--test-window`**: off-screen, never activates, never enters
the taskbar — dumps, screenshots, poses and UIA still work. Test windows stealing the operator's
keyboard mid-work was a priority complaint. **`SendKeys` is banned** for the same reason (it needs
focus) — use the focus-free verbs, each landing in the method the mouse or keyboard lands in:

```powershell
dodona ui type "<text>"             # submits, through the same path as Enter
dodona ui compose "<text>"          # type WITHOUT sending
dodona ui key shift+enter | enter   # the keystroke, through the real PreviewKeyDown path
dodona ui input-resize <dy|reset>   # the grip: +px taller, reset = fit the text
dodona ui lane <focus|stop|respawn|collapse|expand> <n>
dodona ui listen <on|off|toggle>    # the mic button, in the method Mic_Click calls
dodona ui heard "<text>" [--partial] [--epoch <n>]   # a recognition result, through the real splice
dodona ui workspace <name>          # give a band the grid, without stealing focus
dodona ui answer <choice>           # the ask, in the method a button click lands in
```

**The box is multiline**: Enter sends, **Shift+Enter is a new line**, it grows as lines arrive, and
the grip drags it taller (the feed gives up the pixels, so the window never moves). It opens at three
lines and remembers the size you last dragged to, in `<DODONA_HOME>\ui.json` — a file and not the
store, because the shell window spans workspaces and booted to zero has no store to read (§5). It is
a preference, so **every read failure falls back to the default silently**: a corrupt `ui.json` must
never stop the window opening, since the box is what you would use to say so.

Two WPF facts this cost, both in code comments now: `MinLines`/`MaxLines` are **ignored** once
`TextWrapping` is on, and §0.2's Enter trap is load-bearing here — the handler must be
`PreviewKeyDown`. `ui dump` keys the box under `input` (`lines` is LOGICAL lines, not wrapped rows).
Poses are deterministic fixtures (`ui --pose <name>`, needing `--root` or `--shell`); a bare launch
is the shell.

### Dictation

**The box LISTENS, and speech can never send** (`VOICE-INPUT-PLAN` Phase A). Operator's constraint:
*"Send will still need an enter."* Not a rule anyone has to keep — `Dictation.DictationAct` has no
member meaning send, so the decision layer cannot ask for a submit, and `MainWindow.OnHeard` calls
`ComposeInput` and `InputKey` and nothing else. "enter", "send", "submit", "go" are ordinary text
because there is nowhere else for them to go.

The toggle is remembered in `ui.json`; the mic glyph has three states (off / listening / **error**,
because on-and-deaf must never look like on); `ui dump`'s `listen` key carries `state`, `engine`,
`says`, `partial`, `error`, `dropped`, `remembered`. **`DODONA_UI_MIC=off` refuses to construct a
real recogniser at all** and `tests/_workspace.ps1` sets it for every suite — **no suite can ever
open the operator's microphone**, which would be §4's incident in a new costume. `=fail` forces the
error state, otherwise unreachable without unplugging something.

**The engine is Deepgram** (`VOICE-ENGINE-PLAN`). SAPI shipped first, produced **gibberish** when the
operator spoke to it, and the seam was used for what it was built for. **Status: working at the
current build, unmeasured** — the operator, 2026-08-21: *"I did test the diction. It worked fine."*
One gap, named precisely so nobody re-opens the settled part: **there is no word-error rate** (nobody
has scored the engine against `tests\assets\recordings`), so that is a person's judgement and not a
number — enough to stop treating dictation as unproven, not enough to claim one engine beats another.
**No suite can close that gap and none may try** (the microphone rule above), so it lives in prose by
necessity — precisely the case §0 says to write down.

### 3.1 No folder UI, ever (operator directive, 2026-08-18)

The app's only user-facing identity is the **workspace name**. Locations are the router's business.
The old folder picker (recents, Browse…, repo statuses) and the header dropdown that reopened it
were leftovers from before the workspace redesign, removed on the operator's direction.
`PickerWindow` is now the **workspace switcher** — names + awake/asleep only; picking wakes the
workspace and hands it the grid through `FocusWorkspace`, never a second window. **Do not
reintroduce a folder list, a Browse dialog, or a filesystem path in window chrome.** `repo-init`
stays a daemon command reached by typing. (`WORKSPACES-CONCIERGE` §6.1.)

**The grid divides itself:** no slot cap, panes shrink as lanes arrive, nothing scrolls, and the
operator collapses tiles to chips. A collapsed chip still carries its badge and blocked glyph — an
active-but-invisible lane stays forbidden.

**The window is one shell over N workspaces** (`--shell`): the focused workspace holds the grid,
every other awake one is a band of lane chips, the feed is a union with a workspace chip per row.
Address it with `--shell` on any `ui` verb. `--shell` with nothing awake is **boot-to-zero**, a real
state: just feed and input, and typing is how you leave it.

**THE WINDOW ASKS THINGS, AND IT IS NOT A DIALOG** (`LOCATIONS-PLAN` D-L4). A question is an `open`
row in a `questions` table, and the ask is *rendering that row*: an in-window overlay live, `ui
dump`'s `ask` key headless, **one answer path** shared by both. `Esc` puts it down without answering;
the row stays open and the feed still carries it.

- **Never make it a modal.** A test window is forbidden from producing one, so a modal ask would be
  permanently untestable — which is why `PickerWindow` and `StartLaneWindow` have no coverage at all,
  and why D-L4 rejected one.
- **Never let it become a folder picker.** The choices are names the system already knows;
  `ui-ask:the_ask_offers_no_filesystem_navigation` goes red if a path appears in one.
- **With one project there is nothing to ask, so no overlay may appear** — the operator's own machine
  is a one-project workspace, and two `ui-grid` checks pin it.
- **A FINISHED TICKET ASKS TO BE MERGED** — the only merge approval besides `dodona approve` (R6).
  The ask carries what the manager wrote (*"4 files, verify not-run; the manager sent this back,
  round 2 of 3: it changed the schema and its report does not mention it"*) and `yes` is your
  approval. Two rules travel with it. It **never waits for the review** — no manager, `"brain":
  false`, a timeout or a spent bound all render as words over facts code knows, because an ask
  appearing only once a model had answered would make a merge un-approvable on a machine with
  judgement switched off. And **nothing that is not a person may answer it** — no timeout, no
  default, and above all no "the manager said ok, so approve it": the manager may block and may never
  bless (D-R10), and there is deliberately no path from a review to `TicketApprove`.

**A daemon outlives its window.** Closing the app stops nothing — the window is disposable, agents
survive behind their shims. `dodona ps` finds what is running; `dodona stop-all [--lanes]` stops it.
This cost the operator a surprise: they closed the window, believed the machine idle, and a daemon
plus seventeen shims had been up for hours.

**But nothing outlives its REASON.** A shim exits when its agent dies and the buffer is handed over;
if no daemon connects for `DODONA_SHIM_LEASE_SEC` (30 min default) it exits anyway and takes the
agent with it. Both say why, in `<DODONA_HOME>\workspaces\<id>\shim-exits.log`, because stderr
belongs to a daemon usually already gone. So a deliberate `stop-all` (daemons only, lanes keep
running on purpose) costs you those agents after the lease: the lane ROW survives and `lane-respawn`
resumes the session — bounded and recoverable, where an immortal process was neither.

**And the WINDOW outlives its daemon — the half that was broken.** After a `stop-all`, crash or
reboot, the window is up and nothing is running; the store reader is read-only, so every lane still
renders as `alive` and the app looks healthy. It was not: `MainWindow.Send` did not start a sleeping
daemon, so the first thing a person did was answered *"daemon not running"*. Two of the three write
paths already ensured first; the third, carrying the most traffic, did not. **Start-on-demand now
lives inside `DaemonClient.Send`**, so no call site can forget it — the same correction the routing
ladder needed (§3: *ensure at the point of use*).

It survived because none of the five lane actions had a `ui` verb at all: **unreachable, not merely
untested.** Second instance of the general rule: **an affordance no verb can reach is where the next
defect will live**, because it is the one place a suite is physically unable to look. If you add
something a person can click, add the verb in the same commit.

**Routing waits, and so does project choice** (`WORKSPACES-CONCIERGE` §5.1, `LOCATIONS-PLAN` Phase
3). Input is not delivered optimistically to the focused lane, and a new lane is not aimed at the
first project: only `LANE: text`, unmistakable generics ("stop", "no", "try again") and a sentence
that NAMES a project are decided in code and stay instant. **On genuine uncertainty the sentence is
HELD and you are asked — nothing is delivered and nothing is spawned**, because a wrong continuation
cannot be undone and a lane opened in the wrong project is an agent reading the wrong repository,
which no `lane-stop` undoes. Without a warm brain, behaviour is unchanged (focused lane), which is
why the suites are unaffected; a one-project workspace is byte-for-byte unchanged.

### 3.2 Commands that observe, and commands that ACT

Start-on-demand means a client command summons the workspace daemon if it is not up (§2), and a
summoned daemon runs its warm-up: router, brain and the compressor pool, **each a real `claude -p
--model haiku` process** — measured, **four** on a real wake with the default config.

*Incident (2026-08-19):* a session ran `status` twice while verifying a publish, on a machine the
operator believed idle, and left one daemon and five haiku lanes — then two hours later diagnosed
its own leaked shims as "machine contention" and moved a suite out of the parallel wave on that
evidence: **a wrong conclusion drawn from self-inflicted noise**. Quota was not burned (`inputs=0
results=0` — existing costs nothing, only a TURN costs quota) but it was still a machine the operator
did not ask for. **A command whose name promises a reading must not change what it reads** — `status`
is on the no-summon list, held by three `m0` checks, each proved red.

**TWO PROPERTIES, AND THIS SECTION USED TO PROMISE ONLY ONE** (issue #12). The list below was headed
*"None of these start anything"* — true, and read by everyone as *nothing happens*; §0.1's MassWorks
incident is what that cost. **Adoption is now closed at the one place that creates:** a typed
`--root` NAMES a path and no longer adopts one.

```powershell
dodona <command> --root <path>           # addresses the workspace owning <path>; REFUSES if none does
dodona <command> --root <path> --adopt   # ...and creates one. The suites pass this; you rarely should
```

The refusal names `workspace-create` and `--adopt`, so nothing is stuck. `PathSource` carries the
reasoning; `workspace:a_named_root_creates_no_workspace_for_a_daemon_command` asserts it through
`status` on purpose — through the resolution layer, not through `where`. `concierge-resolve` needs
the same `--adopt`, because rung 0 attaches any absolute path it finds in a sentence.

**THE SUMMON HALF IS NO LONGER A LIST SOMEBODY MAINTAINS — issue #13 closed it.** It was a literal
`cmd is "stop-daemon" or "status" or "land-status" or "ticket-record"` in `Client`: right about those
four names from the day it was written, silent about the other sixty. So `policy` — whose own handler
comment reads *"Inspectable without spawning anything"* — started four model agents to print a static
config table, and a bare `publish` against a sleeping workspace woke a daemon **on the old build**
purely to have something to hand a swap to.

**`src\Dodona\DaemonSurface.cs` declares it, per WIRE COMMAND, for both dispatchers.** One rule:
**a command that only REPORTS starts nothing; a command that CHANGES or DELIVERS summons.** Not
read-vs-write — `repos` and `token-status` materialise a `merge_token` row and are readings by name;
`ack` flips one bit and is an act.

- **Keyed on the wire command, not the verb you typed.** They differ: `land`'s poll sends
  `land-status`, `lane-expand` sends `lane-collapse`, `publish` sends `swap`. That gap was half the
  bug, and it deleted `Client`'s `neverSummon` parameter.
- **Completeness is enforced, not remembered:** `dev lint` reconciles the table against every `case`
  label in `Daemon.Commands.cs` and `Concierge.cs`, both directions. **A new command cannot be added
  without answering the question** — which is the property the literal never had. Both directions
  proved red.
- **Fails closed:** an undeclared command starts nothing and says so, naming the two files.
- **Moved with it:** `tail` and `tickets` no longer summon (§3.2 used to list them as deliberate;
  the buffering argument is `say`/`input`'s, where you are DELIVERING). Every `concierge-*` reading
  the same. `swap`/`swap-answer`/`stop-daemon` too — nothing to stop or swap if nothing is running.

**None of these start a daemon, and none adopt a folder** — read `DaemonSurface` for the full set:

| command | |
|---|---|
| `version [--json]` | what a binary is, including its commit. Writes NOTHING, on any path |
| `where [--json]` · `ps` | ids, paths, pipes, what is running. `ps` **REAPS** stale `shim-lane<N>.json` |
| `status` | reports ASLEEP rather than waking the workspace |
| `policy` · `swaps` · `tickets` · `questions` · `tail` · `repos` · `token-status` · `repo-status` · `claim-check` | readings |
| `land-status` · `ticket-record` | polled by `LandCli` and by a manager (R5) |
| `concierge-status` · `concierge-feed` · `concierge-questions` · `concierge-resolve` | the concierge's readings |

`version` is the only one writing nothing whatsoever: every other verb opens the registry, and
**constructing it creates `<DODONA_HOME>\concierge\registry.db`** if absent — inside Dodona's own
territory, never in a project, but it is a write. `repos` and `token-status` also INSERT a
`merge_token` row through a method called `TokenRead` — only ever against a daemon already up, since
they no longer start one.

**These summon deliberately** (waking the daemon is what the caller wants, and the shims have been
buffering): `say`, `input`, `lane-start`, `lane-respawn`, `ticket-create`, `land`, `route`, and the
rest of the acts. Expect the four warm-up processes.

**PROVOKE A BEHAVIOUR WITH A COMMAND THAT MEANS IT.** `m4`'s three autostart checks used `status`
because it happened to summon; when `status` stopped, they were re-pointed at `tickets` — chosen the
same way — and #13 broke them again. One of the two had by then become vacuous while still green.
A check that acts uses `focus`; the reading it happens to be adjacent to is not the point.

**THE CONTROL PIPE IS SERIAL, SO A SLOW HANDLER FREEZES THE WHOLE DAEMON.** One
`NamedPipeServerStream`, `HandleAsync` awaited inline — while any command is handled, that daemon
answers *nothing*: no UI, no lane input, no `say`, no other repository's work. The land ran there for
months unnoticed because the operator's `verify` was fast enough to look like latency rather than a
freeze; the full `dev gate` would have held it **4.6 minutes** (D-R14, fixed by R3.5 — `LandBegin`
answers in 142 ms and the rest runs on its own task). **A command that can take longer than about a
second goes on its own task and reports through an announcement plus a `*-status` command, and you
write the check that makes a real concurrent call during it.** Reasoning about which thread runs what
is what kept this invisible.

When you must run a real command against a live workspace — `publish` in particular, which §2
requires — **say so in your report**: it is an action on the operator's machine, not a measurement of
it. Anything you only want to *observe* belongs in an isolated `$env:DODONA_HOME` (§5).

## 4. Never kill processes by name

```powershell
Get-Process DodonaShim | Stop-Process -Force    # NEVER
```

This murdered the operator's live session — their shim, their agent, their open window — in the
middle of a trial. Machine-wide kills do not know which instance is a test and which is the human's
work. Resolve pids from the specific workspace's `shim-lane*.json` instead (`dodona where` prints the
directory). Tests collide with nothing (§17), *including the instance the operator is using now*.

**A pid is the FALLBACK, not the first move.** `stop-all --lanes` asks each shim to go over its own
pipe (`##shutdown`), which needs no bookkeeping, kills the child TREE rather than orphaning it, and
lets the shim exit cleanly — which is what made an agent whose `shim-lane*.json` was never written or
already reaped stoppable (four such were running on 2026-08-18, three out of the compiler's own
output directory, unreachable by any `dodona` command). The pid sweep remains, for a shim too wedged
to converse.

## 5. Dodona's own state is never repo content, and lives outside the repo

Identity is a **workspace**, not a project root (`WORKSPACES-CONCIERGE` §1): a named, durable session
group over N member folders. So state left the project tree:

| what | where |
|---|---|
| the store, its WAL twins, `shim-lane<N>.json` | `%LOCALAPPDATA%\Dodona\workspaces\<id>\` |
| workspace names, ids, aliases, members | `%LOCALAPPDATA%\Dodona\concierge\registry.db` |
| ticket worktrees | `<member>\.dodona\wt\t<N>` — the one deliberate exception: volume- and path-sensitive, and moving them buys nothing |
| all of it, relocated | `$env:DODONA_HOME` |

**Every suite must set `DODONA_HOME`** (`tests/_workspace.ps1` does) or a test run litters the
operator's real workspace list — and a test of the repo-exclusivity *refusal* could refuse one of
their real repos.

`.dodona/` is git-ignored and must stay so — a `git add -A` once committed a live SQLite database
into this repo. Deployed gate files live in `.git/info/exclude` for the same reason.

**Never reconstruct a store path by hand.** `dodona where [--json]` answers it; the suites ask
instead of guessing, which is what let the store move without rewriting eight of them.

The invariant path-hash identity used to give for free is now enforcement in code: **a git repo
belongs to at most one workspace at a time** — a partial unique index, a loud attach-time refusal
carrying the `workspace-move` command, and a third check at `ticket-create` for the
bare-folder-that-later-became-a-repo case. Two workspaces over one repo is two merge tokens over one
main, the race this system exists to prevent. **If `workspace-acceptance.ps1`'s exclusivity checks go
red, that is a correctness incident, not a flaky test.**

### 5.1 Delivery is a skill — and so are the three traps that keep recurring

`.claude/skills/` carries what CLAUDE.md cannot: a rule arriving **at the moment of the action**
rather than at session start. Not theoretical — §0.2's heredoc-backslash trap was written down, had
been read, and was violated three times in one session anyway, not from disagreement but because
forty minutes had passed.

| skill | fires when |
|---|---|
| `check-authoring` | writing or editing an acceptance check — what may be asserted on (processes and store rows, never an instantaneous pipe read), `Invoke-StoreSql`, and that a check is worth nothing until `dev prove` has seen it red |
| `file-patching` | rewriting a tracked file with a script — backslash collapse, BOM, bare LF, parse-checking, reading your own diff against `git diff -w` |
| `probe-hygiene` | launching a daemon, shim or agent by hand — isolated `DODONA_HOME`, binaries from `Use-TestBinaries`, `status` is not read-only, no machine-wide mutation while a verification is in flight |
| `ship` | the complete build → suites → commit → publish → verify-the-swap path. Use it rather than improvising the sequence; when delivery changes, change the skill in the same commit |
| `ticket` | writing an issue on the tracker (§6) |

**If one turns out to be skipped as reliably as a section of this file was, promote its contents to
enforcement and delete the skill. Do not write a fourth trap skill (D-6)** — a *trap* skill being one
that fires a warning at the moment of a dangerous edit. `ship` and `ticket` are **workflow** skills,
the named job you invoke. Saying which of the two kinds a new skill is, in the commit that adds it,
is the check on this — otherwise D-6 erodes one reasonable exception at a time.

### 5.2 A project's git process may not be Dodona's — and worktrees have sharp edges

Some repos own their ticket lifecycle in their own CLAUDE.md and skills: branch off `develop`, push,
open a PR, a human reviews, the forge merges. Dodona's §7 assumes the opposite (an ff-only local
merge it performs itself), so this is a per-repo mode: `"delivery": "pr" | "local-merge"` in that
repo's `dodona.json`. **`docs/M5-DELIVERY-PLAN.md` is the authority** (design §7.1/§7.2 superseded on
two points there); read it before touching any of this.

**In a `"delivery": "pr"` repo Dodona never merges, never grants a merge token, never deletes a
branch and raises no approval question** (R7, D-R28) — it supplies the worktree and gets out of the
way, and the forge's merge button is the human gate. Upstream is untouched: the completion record is
still assembled and readable (`dodona ticket-record <ticket>` IS the PR description, D-R29), and the
manager still reviews and can still send work back. Three things:

- **An unrecognised value reads as `pr`, not `local-merge`** (D-R31) — only the absent key and the
  exact word `local-merge` permit merging, because a typo that refuses a land is recoverable and one
  that advances a ref is not.
- **Dodona does not touch a forge at all** — no push, no PR, no observer; that is the project's
  ceremony and M5.5's remaining work.
- **The recorded branch can go stale**: Dodona still names `ticket/N`, so a lane cutting its own
  branch the project's way leaves the record pointing at the old one. A gap rather than a hazard only
  because nothing destructive reads it in pr mode.

**This is not either/or with `/ship`** — that skill's landing step is the fallback for a project with
no process of its own. Where a project *does* define one, that process governs, and the worktree
still has to be dealt with either way. (Operator's correction: an agent reading either half alone
will skip the other.)

| worktree trap | why |
|---|---|
| **Directory name and branch name are unrelated** | `t7` can hold `feature/ABC-123`. Nothing outside Dodona sees the directory — never rename a worktree to match a branch; short paths are a Windows `MAX_PATH` margin once `node_modules` sits under it |
| **`git stash` is repo-global** | one shared ref in the common dir. Two lanes stashing interleave one stack and `pop` takes the other lane's work. **Commit WIP to your own branch instead — always, everywhere** |
| **`.git` in a worktree is a FILE** | anything doing `test -d .git` or reading `.git/HEAD` by hand breaks |
| **`checkout <existing-branch>` inside a worktree is the silent killer** | it fails loudly if that branch is checked out elsewhere and **SUCCEEDS if it is not** — the worktree wanders off its branch while Dodona's recorded branch goes stale. Defence: a **branch lock**, a `git worktree add --no-checkout` sentinel per shared branch — costs no disk, makes git refuse. Never make this depend on the operator being on main |
| **Cutting a NEW branch is fine** | `checkout -b` / `switch -c` is the PR flow. The rule is "no checking out branches that already exist", never "no branch but main" |

**A lane carries its own working directory** — `lanes.cwd`, the schema-8 migration at
[Store.cs:214](src/Dodona/Store.cs#L214), written by `Store.LaneCwd`, read in `Store.LanesAll`.
`AttachShimAsync` records it for **every** spawn and uses it as the shim's `WorkingDirectory`;
`RespawnLaneAsync` prefers it, falling back to `_primary` only for a lane older than the column.

**A lane opens in a project it is given**: `SpawnAgentLaneAsync(title, project, …)` has no default,
`lane-start --project <path>` chooses one, a folder no project owns is refused, and the project picks
the lane's `permissionMode`/`allowedTools` (`Config.For`) and is written into its system prompt from
the same single parameter. A typed sentence chooses too (§3.1's ladder).

**Still true and still the danger:** with one project — which is what the operator has — every lane
opens in their live tree, so a plain lane is in a SHARED checkout and **must never check out a
branch**. Giving each lane a tree of its own is unbuilt, and is a spawn-site change rather than a
schema one.

## 6. Where things are written down

| | |
|---|---|
| `docs/ORCHESTRATOR-DESIGN.md` | **the authority.** Every `§n` in this codebase (§8 attention, §11 lifecycle, §17 testing) points here |
| `docs/ORCHESTRATOR-REVIEW.md` | the milestone plan, the measurements, what is carried |
| `docs/WORK-ISOLATION-PLAN.md` | no agent writes into a project outside a worktree, and the refused write promotes itself into one. Layers 1 and 2 BUILT |
| `docs/REVIEW-AND-MERGE-PLAN.md` | how ticket work is reviewed and lands, and a manager that can send back but **never approve**. Supersedes declared-claims-as-a-lock entirely |
| `docs/LANE-LIFECYCLE.md` | decisions taken and **ideas already rejected** about closing lanes and the attention model. Read before proposing either |
| `docs/TEST-ARCHITECTURE-PLAN.md` | the test migration, the ledger, the no-coverage-lost rule and its one evidenced exception (§0.1) |
| `DEBUGGING.md` | schema, every event kind, the pipes, how to read a store with nothing running. **Start here when something looks wrong** |
| **`github.com/devblazer/dodona/issues`** | the tracker, and it is **PUBLIC**. Outstanding work lives there rather than in a plan's unbuilt sections; issues go on `gh project` **2**, owner `devblazer`. Write them with the `ticket` skill |

**The repo is public as of 2026-08-21** — a change of kind, not degree: this file, every plan, and
the operator's quoted words are world-readable. That does not change what to write; it changes what to
check before writing a credential, a path off this machine, or somebody's name.

The docs in `docs/` are copies; the masters live in `..\MassWorks\`, copied in because a lane works
from `<root>\.dodona\wt\t<N>` where that path resolves to nothing. **Reading a master there is the
ONLY thing you may do in that directory** (§0.1).

## 7. Permissions: a lane cannot ask

The operator's own session has a permission-prompt tool wired to a dialog, so an unapproved command
becomes a question. **A lane has no such channel** — headless `-p` denies outright and the agent
strands mid-task: it edits fine, then cannot build what it edited. So lanes default to
`bypassPermissions`, matching what the operator's IDE grants in auto mode.

**That does not loosen Dodona's guarantees**, measured not assumed: a PreToolUse hook still fires
under `bypassPermissions`. The write gate *is* a PreToolUse hook, so **no agent can write into a
project outside a worktree**, and it fails CLOSED — an unreadable argument, unparseable stdin, a path
it cannot find, or a daemon that does not answer all deny. **The safety model never rested on
Claude's permission prompt.**

**What that gate no longer asks is whether the write is inside the ticket's CLAIM** (D-R5, R3). Four
refusals went with it: the claim question in the hook, `ticket-create` refusing a second ticket over a
claimed path, the `token-request` backstop refusing a branch that touched outside its claim, and
`claim-extend`'s. **Do not describe a ticket lane as bounded to its claim, and do not reintroduce any
of them.** The operator's decision: two agents about to work on the same file is *"often the case,
very often the case, and if that is problematic it's the manager's job to say something about it."*
Files are not the unit of work. The ticket lane's system prompt claimed that boundary to every ticket
agent on every turn for a day after R3 removed it — how long a false sentence survives when nothing
reads it back — and `unit`'s `A_ticket_lane_is_not_told_it_is_bounded_by_its_claim` reads it back
now. Claims survive as an annotation and a derived signal (what a branch actually touched is `git
diff`, recorded at `token-request` for a reviewer); the guarantee that remains is the tree, which is
the one that was doing the work.

**`GateHook` has no fail-open path left, and that is a check rather than a sentence.** This section
and the function's own header comment once both claimed otherwise, so somebody enumerated every
`return` (issue #4): with a lane argument present, **every** exit denies except the one positively
placing the write inside a worktree — the unparseable-stdin branch calls `GateAllowedUnchecked` *for
the trace only* and then returns `GateDeny`, which reads like an allow at a glance and is not one.
**The enumeration found a real hole neither document knew about**: `--lane 5 --ticket abc` (readable
lane, unreadable ticket) returned an unchecked ALLOW and never asked the tree question — unreached
because `DeployGate` only ever writes a numeric `--ticket`, which is precisely why it sat there
unread. **The lesson, at its own expense: a property claimed in prose is not enforcement.** Assert it
by enumerating every `return`, and leave a check behind
(`m1:the_gate_still_checks_the_tree_when_the_ticket_argument_is_unreadable`).

A second question added to that hook **must not reintroduce a fail-open.** The claim question failed
open and was only tolerable because the tree question ran first and refused on doubt.

A project wanting a leash sets `"permissionMode": "acceptEdits"` plus an `allowedTools` list — leakier
than it looks: `PowerShell(dotnet build:*)` still loses to `dotnet build ... | Select-Object`, because
a pipeline counts as multiple operations. **If a command is denied, that is the environment, not you
being wrong** — report it as the headline (§1) and name the exact command.

## 8. Write it compressed

**All new documentation, skills, doc comments and code comments are written compressed by default:
terse notation, tables, one line per fact — not verbose English.** Agents infer fine from shorthand,
and the verbose register was written for a human reader who was never the primary audience.

The reason is a measurement (issue #23): this file is loaded in full into **every session and every
subagent, and re-sent on every turn**. At 24,600 tokens it put the floor near 40,000 before anyone
read a line of code, and the largest source file costs 113,000 tokens to open — so an agent asked to
change it starts near the edge of what it can hold and gets worse the longer it works. Prose is the
part of that cost buying nothing.

**Compress the prose, keep the REASONS.** Every rule here survives because it names the incident that
produced it. An agent reading *"never kill processes by name"* with no reason will find a case where
it seems fine and do it; the same rule with *"this killed the operator's live session mid-trial"*
holds. **Cutting the story to a clause is right; cutting it to nothing is how the rule gets
overridden six weeks later.** Comments in this codebase explain *why*, and often name the incident
that caused the line — keep that habit, compressed. The next person to read it is debugging at speed.
