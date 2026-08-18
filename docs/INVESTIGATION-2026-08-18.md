# Investigation — 2026-08-18

Scope: why software work in this repository keeps going wrong. Evidence gathered on the live
machine ~20:00 on 2026-08-18, against HEAD `cd53389`. No source file was modified. (Disclosure:
confirming §RC5 required running the eleven suites, which rewrote the tracked
`tests/*-output/` artifacts — see RC4 for why that is itself a finding.)

---

## 1. Verdict

Every failure below is one shape: **Dodona — and the sessions working on it — decide what is
true by reading a record they wrote, instead of observing the thing the record describes.**
Right now, with no daemon and no window running anywhere on this machine, there are **11 live
lane pipes, 11 `DodonaShim` processes and 10 live `claude.exe` agents**; `dodona ps` can see at
most 7 and `stop-all --lanes` can kill at most those 7, because 4 lost the file that was their
only record — while `Instance.LivePipes()`, already in the repo and already the authority for
daemons, returns all 11. The same shape defeated the tooling built to catch it: 48 of the 49
entries in `git status` are *tracked generated artifacts*, so "what did I change" has no
answer — which is how one session's `Store.cs`/`Ver.cs`/`Daemon.cs` work was swept into
another's commit (`f9aaf25`, whose own message admits it), and how `dev prove` came to print
`VACUOUS` about a check that is sound. The habit extends to the prose: CLAUDE.md §1 states as
*measured* that the suites take twenty minutes of which only 3.6 are sleep "and the rest is
inherent and cannot be optimised away" — measured today they take **5 min 16 s and sleep is
68 % of it**, so the doctrine that verification must be a rare gate rests on a number that is
wrong by 4×, in the direction that discourages verifying. **Nothing here needs a new tool**:
every missing observation is already in the repo, already in the Claude Code harness, or one
`git rm --cached` away.

---

## 2. Root causes

Ranked by the cost of recurrence, not by ease of fixing.

---

### RC1 — Two agent sessions share one checkout, one build output and one `git add -A`, and nothing knows it

**Cause.** There is no isolation boundary between concurrent agent sessions in this repository,
and the designated delivery path stages the whole working tree, so one session's commit
silently carries another's uncommitted source changes.

**Explains:** F, E, G, I, C, and most of A.

**Evidence.**

- `.claude/skills/ship/SKILL.md` step 3, verbatim:
  `git add -A ; git status --short   # review what is staged`.
  Staging everything and then reviewing is not a control — and per RC4 the review is
  unreadable anyway.
- `f9aaf25` (10 files, +480/−100) is titled *"The routing ladder was only ever wired up inside
  its own test"*; its closing paragraph admits the carry:
  > *"Carries M5.1's lanes.cwd migration and its Ver.Schema bump to 8, **which were in the
  > working tree from another lane** and are inseparable from this commit…"*

  The off-topic hunks are all four `Store.cs` changes — `ALTER TABLE lanes ADD COLUMN cwd`
  ([Store.cs:214](../src/Dodona/Store.cs#L214)), `LaneCwd` ([:263](../src/Dodona/Store.cs#L263)),
  `LaneRow.Cwd` ([:362](../src/Dodona/Store.cs#L362)), the `LanesAll` SELECT
  ([:370](../src/Dodona/Store.cs#L370)) — the `Schema = 7 → 8` bump at
  [Ver.cs:23](../src/Dodona/Ver.cs#L23), and the cwd ladders at
  [Daemon.cs:483](../src/Dodona/Daemon.cs#L483) and
  [Daemon.cs:1638](../src/Dodona/Daemon.cs#L1638).
- **The test for that carried fix is still uncommitted.** `git diff tests/m3-acceptance.ps1`
  → +44 lines adding `respawn_actually_respawned`,
  `respawned_ticket_lane_returns_to_its_worktree`, `respawned_ticket_lane_is_not_in_the_live_tree`.
  The fix landed in HEAD without its test; the test is orphaned in a tree neither session owns.
  It is the **only** source change in `git status` today.
- The consequence, in `.dodona/dev-logs/dev-20260818-194154-prove.log`:
  `dev prove m3 respawned_ticket_lane_returns_to_its_worktree` →
  **`VACUOUS: the check PASSES against HEAD`**. The check is not vacuous. It passes because
  the *other session already committed the fix into HEAD*. The tool accused a good check of
  being toothless — the exact wrong lesson.
- Timing corroboration (`git log --stat`): `0ab58d3` (18:25:46, docs-only) and `d81b9f2`
  (18:25:54, UI-only) land **8 seconds apart** with disjoint file sets; `f3f7ae9` (18:11:11,
  UI-only) and `ba555b5` (18:11:55, docs-only) **44 seconds apart**. Both pairs edit
  `CLAUDE.md`; five commits touch it in 93 minutes.
- Isolation that exists and was not used: `.claude/worktrees/never-stuck` is registered
  (`git worktree list`), gitignored since `d6d908e`, used exactly once. It is clean and
  `git rev-list --count main..never-stuck` = **0** — nothing is stranded. Neither concurrent
  session used the mechanism. The harness offers it directly (`EnterWorktree`/`ExitWorktree`,
  the Agent tool's `isolation: "worktree"`); nothing in `CLAUDE.md`, `SKILL.md` or `.claude/`
  mentions it.
- `.claude/` contains **exactly one file**: `skills/ship/SKILL.md`. No `settings.json`, no
  `settings.local.json`, no hooks, no agents. `.git/info/exclude` is the stock template
  (`grep -c 'dodona-gate'` → 0).
- Dodona's own gate does not apply here: `DeployGate`
  ([Daemon.cs:2435](../src/Dodona/Daemon.cs#L2435)) writes
  `<worktree>/.claude/settings.local.json` and is called only from the two ticket paths
  ([Daemon.cs:740](../src/Dodona/Daemon.cs#L740), [:338](../src/Dodona/Daemon.cs#L338)). A
  plain lane gets `_primary` and no gate ([Daemon.cs:1554](../src/Dodona/Daemon.cs#L1554)). The
  only mutual exclusion in the codebase is one-daemon-per-workspace
  ([Daemon.cs:184](../src/Dodona/Daemon.cs#L184)), which says nothing about editors.

**What makes recurrence structurally impossible.**

1. **(a) Already present, unused — one git worktree per concurrent session.**
   `.claude/worktrees/` exists, is ignored, and has a working precedent; the harness creates
   and removes them. Two sessions in separate worktrees cannot cross-stage, cannot share
   `obj/`, and cannot hold each other's build output.
2. **(b) Delete the mechanism — remove `git add -A` from `/ship` step 3**, replacing it with
   explicit pathspecs (`git add -- <paths>`). `git add -A` is the literal mechanism of
   `f9aaf25`; deleting it removes the failure.
3. **(c) A check that fires without anyone remembering — a `PreToolUse` hook in
   `.claude/settings.json` denying `git add -A`, `git add .`, `git commit -a`**, with a message
   naming the substitute. The repo already knows how to write exactly this file: `DeployGate`
   generates one at [Daemon.cs:2457](../src/Dodona/Daemon.cs#L2457) with the matcher
   `Edit|Write|MultiEdit|NotebookEdit`, and CLAUDE.md §7 records the measurement that PreToolUse
   hooks still fire under `bypassPermissions`. The technique is proven in-repo and has never
   been pointed at the primary tree.

**Confirming (<30 s).** In an agent session run `git add -A`: it must be denied with a message
naming the substitute. Then `git worktree list` must show one entry per live session, not one
shared root.

**Cost / what it breaks.** Explicit pathspecs are more typing, and a forgotten new file
surfaces at review rather than at commit. Worktrees cost disk and a `dotnet restore` each;
they also mean a lane's build output is not the operator's — which is the point. No
user-visible behaviour changes.

---

### RC2 — Dodona starts processes that have no owner and no exit condition, and counts them from files instead of from the OS

**Cause.** Every process Dodona spawns is detached by design, its only owner is an in-memory
dictionary entry, and its only exit door is a message from the daemon that just discarded that
entry — so a daemon that wrongly decides a lane is gone creates an immortal orphan and
immediately spawns its replacement.

**Explains:** B, C, D — and the current state of this machine.

**Evidence — the machine, now.**

```
PS> [IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -like '*dodona*' }
dodona pipes: 11   ctl pipes (daemons): 0   ui pipes (windows): 0   lane pipes: 11
live DodonaShim: 11 · live claude.exe children of DodonaShim: 10
```

- Eleven live lane pipes across **four** workspace ids; only two exist in the registry
  (`dodona-05a5`, `freeze-repro-0397`). `mlroot-28f3e4f7-96e0` (3 lanes) and
  `repro-16119c17-3685` (1 lane) belonged to ad-hoc `DODONA_HOME` temp dirs since deleted.
- Shim-info records on disk: **7**. Four live agents have no record at all. `Ps()` counts
  lanes from `LiveShimPids` ([Program.cs:455](../src/Dodona/Program.cs#L455)), which reads
  `shim-lane*.json` files; its "unregistered" branch
  ([Program.cs:404](../src/Dodona/Program.cs#L404)) inspects **`-ctl` pipes only** — lane pipes
  are never enumerated. `stop-all --lanes`
  ([Program.cs:548](../src/Dodona/Program.cs#L548)) iterates registry workspaces ×
  `LiveShimPids`, so it cannot reach them either. **Three of the four run out of
  `src\Dodona\bin\Release\net8.0\DodonaShim.exe` — exactly the class of process that blocks a
  build, and no `dodona` command can stop them.**
- `Instance.LivePipes()` ([Instance.cs:127](../src/Dodona/Instance.cs#L127)) already enumerates
  `\\.\pipe\` and is already the authority for daemons and UIs. Its own comment reads
  *"liveness is read off the OS instead of stored."* Lanes are the one thing left on the stored
  path; `Instance.LanePipe(id, laneId)` ([Instance.cs:110](../src/Dodona/Instance.cs#L110)) is
  the matching name.

**Evidence — how the orphans are made.**

- The shim's only exit is `##shutdown` from a connected client. It detects its child's death
  and does nothing: [DodonaShim/Program.cs:71-75](../src/DodonaShim/Program.cs#L71-L75) sets
  `childExited = true`; the serve loop ([:78](../src/DodonaShim/Program.cs#L78)) is
  `while (!shutdown.IsCancellationRequested)` and `shutdown` is cancelled only at
  [:121](../src/DodonaShim/Program.cs#L121). A shim whose agent died runs forever, still
  answering `!hello`.
- The daemon abandons a utility lane after **one** 500 ms connect attempt —
  [Daemon.cs:257-258](../src/Dodona/Daemon.cs#L257-L258): `var attempts = patient ? … : 1;` —
  then writes `utility_lane_reaped … "shim gone, nothing to resume"`
  ([Daemon.cs:300](../src/Dodona/Daemon.cs#L300)). **It never asks whether the shim is gone.**
- Live proof the assertion is false: the operator's store records at 17:13:55
  `lane_unreachable 20 "reconcile: pipe did not answer in 1 attempt(s)"`, then
  `utility_lane_reaped 20 "shim gone"`, then 160 ms later `shim_spawned 25`.
  `shim-lane20.json` names `shimPid 57480 / childPid 9248`; **both are alive now**, and
  `\\.\pipe\dodona-dodona-05a5-lane20` is in the list above. The daemon declared a live agent
  dead, dropped the only reference that could ever stop it, and started a replacement.
- Nothing deletes a shim-info file on any exit path — not `lane-stop`, not `stop-daemon`, not a
  hot swap, not a crash. The sole deleter in the tree is `ReapShimInfo`
  ([Program.cs:468](../src/Dodona/Program.cs#L468)), reachable only from a human typing
  `dodona ps` ([Program.cs:377](../src/Dodona/Program.cs#L377)). The file set is monotonic for
  the life of a workspace — which is why "24" was not 18 leftovers but *every lane that
  workspace had ever spawned* (`shim_spawned` exists for lanes 2–25; lane 1 is the dispatcher).
- `LiveShimPids` checks `t.Shim` only and ignores `childPid`
  ([Program.cs:456](../src/Dodona/Program.cs#L456)), so a shim outliving a dead agent counts as
  a live lane and will be re-adopted and routed to.
- The concierge's `shim-tier<N>.json` ([Concierge.cs:738](../src/Dodona/Concierge.cs#L738)) has
  no reaper at all.
- **No acceptance suite exercises `ps` or `stop-all`** (`grep -rn 'stop-all' tests/` → nothing;
  `grep '@("ps"' tests/` → nothing). 343 checks across 11 suites; zero on either.

**What makes recurrence structurally impossible.**

1. **(a) Already present, unused — derive lane liveness from `Instance.LivePipes()`**, the
   function that already answers this for daemons and UIs. `ps` then reports 11 instead of 7,
   `stop-all --lanes` can address a lane it has no file for, and an unregistered workspace's
   lanes are named honestly the way an unregistered `-ctl` pipe already is. Shim-info survives
   only as the pid lookup for killing, never as the count.
2. **(b) Change the code so an orphan cannot exist** — three edits:
   - `DodonaShim`: **exit when the child exits**, once the buffer is drained. The flag is
     already computed at [Program.cs:71](../src/DodonaShim/Program.cs#L71).
   - Reconcile: **replace `attempts: 1` with "ask the OS first."** Pipe absent from
     `Instance.LivePipes()` → zero attempts (*faster* than today; this is what the 35-second
     reconcile hang actually wanted). Pipe present → be patient; and if it is present but will
     not converse, send `##shutdown` — the shim's only exit door — rather than abandoning it.
     This turns `utility_lane_reaped "shim gone"` from a guess into a verified statement.
   - `EnsureBrainAsync`/`EnsureRouterAsync`
     ([Daemon.cs:1864-1901](../src/Dodona/Daemon.cs#L1864-L1901)): **never spawn a replacement
     while the predecessor's pipe is live.** Adoption failure must not be a spawn trigger.
3. **(c) Checks that fire without anyone remembering** — in `m0` (daemon death) and `m4` (hot
   swap): after the scenario, the count of `dodona-<wsid>-lane*` pipes must equal the lanes the
   store believes alive, and `stop-all --lanes` must leave zero. Each is one PowerShell line.
   `dev prove` them against `cd53389` before believing them.

**Confirming (<30 s).**

```powershell
$pipes = @([IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -match 'dodona-.*-lane\d+$' }).Count
$ps    = (dodona ps --json | ConvertFrom-Json | % { $_.lanes } | measure -Sum).Sum
"$pipes pipes / $ps reported"      # must be equal — today it is 11 / 7
dodona stop-all --lanes
@([IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -match 'lane\d+$' }).Count   # must be 0
```

**Cost / what it breaks.** A shim exiting with its child means `lane-respawn` can no longer
reattach to a shim whose agent crashed — correct, since there is nothing to reattach to, but it
changes `unreachable` handling for work lanes and needs a check. Pipe-derived liveness is a
directory enumeration per `ps`. The `attempts` change makes reconcile faster, not slower.

**Separate from the fix, do it now:** the 11 shims and 10 agents live on this machine are
unreachable by any Dodona command. Stop them by pid from the process tree (parent =
`DodonaShim`), never by name (CLAUDE.md §4).

---

### RC3 — The build output doubles as the run directory

**Cause.** `src\<proj>\bin\Release\` is both what MSBuild must overwrite and where daemons,
shims and agents are launched from — so any running test, or any autostarted CLI, blocks the
next build, invisibly, because a daemon deliberately outlives its window.

**Explains:** B, C. (This is the mechanical half of the hour in A; RC2 is why the blockers are
never cleaned up.)

**Evidence.**

- All 11 suites launch the daemon from the build output, e.g. `tests/m2-acceptance.ps1:10`
  `$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"`, `:38`
  `Start-Process $dodona -ArgumentList "daemon", …`.
- `Autostart` spawns the daemon from whatever binary ran the CLI —
  [Program.cs:925](../src/Dodona/Program.cs#L925):
  `var exe = Environment.ProcessPath ?? "dodona.exe";`. So
  `.\src\Dodona\bin\Release\net8.0\dodona.exe <anything>` can leave a daemon holding the
  compiler's own output. **CLAUDE.md §2 and `/ship` step 4 both instruct exactly that
  invocation.**
- CLAUDE.md §0.2 records that a `.ps1` failing to *parse* never reaches `finally` — so a suite
  with a syntax error leaks its daemon into the build output permanently.
- The repo already solves this for itself: `publish` compiles into a private scratch bin so it
  never contends with the dev tree (`-p:BaseOutputPath={scratchBin}\` in `Publish()`,
  [Program.cs](../src/Dodona/Program.cs)), with the comment *"a daemon running from bin\Release
  locks its image (Windows), and publish kept dying on that lock whenever a test daemon
  lingered."* Published builds live in `%LOCALAPPDATA%\Dodona\bin\<stamp>`
  ([Ver.cs:32](../src/Dodona/Ver.cs#L32)). Only the tests were left behind.
- `src\Dodona\bin\Release\net8.0\DodonaShim.exe` is dated **01:42:54**, ~18 hours stale.
  `Dodona.csproj` has no `ProjectReference` to `DodonaShim` and no copy target; every suite
  sets `DODONA_SHIM` to the shim project's own output. It is an orphan of an earlier design —
  and it is the image the three unkillable shims are running.
- `tests/m1-acceptance.ps1` is the one suite that does **not** set `DODONA_SHIM`, so a shim it
  spawns falls back to `Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe")`
  ([Daemon.cs:1651](../src/Dodona/Daemon.cs#L1651)) — that stale copy.
- Observed fact C confirmed on disk: a second tree was built at
  `…\claude\c--Users-devbl-…\7888095e-…\scratchpad\tree\`. The directory has since been
  deleted; two of its processes were still running at 20:00.

**What makes recurrence structurally impossible.**

1. **(b) Change existing code — `tests/_workspace.ps1` (already dot-sourced by all 11 suites)
   gains `Use-TestBinaries`**, which copies `src\*\bin\Release\` into `$env:DODONA_HOME\bin`
   once and returns that path; every suite's `$dodona`, `$fake` and `$env:DODONA_SHIM` point
   there. After this **nothing ever executes out of `src\...\bin`**, MSBuild can always
   overwrite, and a leaked test daemon leaks into a temp dir instead of into the compiler's
   path. It fixes `m1`'s missing `DODONA_SHIM` for free, because all three binaries land in one
   directory the way a published build does.
2. **(b) `Autostart`/`Cx` refuse to daemonize when `Environment.ProcessPath` is under a `\bin\`
   directory**, naming `publish` as the substitute. A build output is not an installation.
3. **(a) Already present** — point CLAUDE.md §2 and `/ship` step 4 at the *published* binary
   (or the shortcut target). Documentation, therefore weak alone; items 1 and 2 carry it.
4. **(c)** one line at the end of every suite: no process may be running from
   `$repo\src\*\bin\`. Fails loudly in the suite that leaked it, naming the pid.

**Confirming (<30 s).** `dev suites`, then `powershell -File tools\dev.ps1 check` → must print
`in the build output: nothing`. Today it prints three pids.

**Cost / what it breaks.** One ~10 MB directory copy per suite run, into a home the suite
already creates and deletes. The `Autostart` refusal changes behaviour for anyone deliberately
running a daemon from `bin\Release` — the practice being removed.

---

### RC4 — Generated artifacts are tracked, so `git status` is not a signal

**Cause.** 80 generated test artifacts are committed and rewritten by every suite run, so the
tree is permanently dirty and "what did I change?" has no readable answer.

**Explains:** G — and it is what disarms the guards in RC1 and in `dev prove`.

**Evidence.**

- `git ls-files tests/ | grep -c output/` → **80**: 16 `.out`, 16 `.err`, 15 `.png`, 12 `.json`,
  12 `.db` (live SQLite stores), 5 `.txt`, 4 `.tmp` — 1.94 MB at HEAD (the 12 `.db` alone
  ~870 KB).
- `git status --porcelain | wc -l` → **49**, of which **48** are those artifacts. The one that
  matters — `tests/m3-acceptance.ps1`, RC1's orphaned test — is buried in them. A full suite
  run dirties 76 of the 80 (the other 4 belong to `m2-live`, which is not in `AllSuites`).
- All 11 suites wipe their output directory at start (identical three lines at m0:23-25,
  m1:16-18, m2:15-17, m3:16-18, m4:24-26, workspace:21-23, ui-use:23-25, compression:22-24,
  brain:17-19, concierge:35-37, publish:30-32) and **no suite ever reads a file from
  `tests/*-output/`.** The committed copies are write-only. The `.gitignore` comment claiming
  they are "what you read after a failure" describes the copy on disk from the run you just
  did, not the copy in git.
- History cost: across the last 20 commits touching `tests/`, a mean of **~34 output files per
  commit, ~95 % of every `tests/` commit**. `d4cdf0d` and `9d7af73` are *entirely* regenerated
  output — "31 files changed, 29 insertions(+), 29 deletions(-)", zero source change. PNGs and
  SQLite files do not delta-compress, so this is on the order of tens of MB of pack for zero
  reviewable content.
- `Do-Prove`'s only precondition is
  `$dirty = @(git -C $repo status --porcelain -- 'src' 'tests'); if ($dirty.Count -eq 0) { Abort … }`
  ([tools/dev.ps1](../tools/dev.ps1)). It can **never** fire. That is why `prove` ran happily
  against a working tree containing no `src` change at all and reported `VACUOUS` rather than
  "you have not written the fix."
- CLAUDE.md §5 records that "a `git add -A` once committed a live SQLite database into this
  repo." Twelve of them are still tracked.

**What makes recurrence structurally impossible.**

- **(b) Delete** — `git rm -r --cached tests/*-output/`, add `tests/*-output/` to `.gitignore`.
  There is nothing to weigh: nothing reads them, every suite deletes them first, and they are
  the entire reason `git status` is illegible.
- **(b) Delete** — with the tree clean, tighten `Do-Prove` to require **`src/`** to differ from
  HEAD, and give it a third outcome — *"the check passes against HEAD because HEAD already
  contains the change"* (detectable with `git log -S`) — distinct from *"the check is
  vacuous."* Today those two produce the same misleading verdict.

**Confirming (<30 s).** `dev suites`, then `git status --porcelain` → **empty**. Then
`dev prove m3 <check>` with no `src/` edit → must abort with "no change to prove", not
"VACUOUS".

**Cost / what it breaks.** A reviewer loses screenshot diffs in commits — never a real workflow
here, since the suites regenerate them in seconds, and the price of keeping them is that no one
can see any diff at all.

---

### RC5 — The "twenty-minute suite" is a recorded number, not an observed one, and the doctrine built on it is backwards

**Cause.** CLAUDE.md §1 records a measurement that is wrong by ~4×, concludes that verification
is inherently expensive, and therefore makes verification a rare gate — while the actual cost is
mostly removable sleep, and there is no fast lane because the repo has **no unit test project at
all**.

**Explains:** H, and the pressure behind A, C and E — an agent that believes verification costs
twenty minutes verifies rarely, or believes a green check it has not earned.

**Evidence.**

- CLAUDE.md §1: *"The eleven suites take about twenty minutes… measured: only 3.6 minutes of
  that is fixed `Start-Sleep`, so the rest is inherent and cannot be optimised away."*
- Measured today, `Measure-Command` per suite on a warm machine, all 11 green (343 checks,
  0 failed): **total 5 min 16 s (315.9 s)**. Static `Start-Sleep` sum: **214.2 s** — which I
  independently confirm as 3.6 min, matching the recorded figure exactly. So sleep is **68 %**
  of the runtime, not 18 %. Non-sleep is 101.7 s in total.

  | suite | sleep | measured | % sleep |
  |---|---|---|---|
  | ui-use | 88.7 s | 103.7 s | 86 % |
  | brain | 44.0 s | 58.7 s | 75 % |
  | m4 | 17.8 s | 45.5 s | 39 % |
  | m3 | 15.7 s | 24.2 s | 65 % |
  | compression | 16.6 s | 21.5 s | 77 % |
  | m0 | 13.3 s | 14.9 s | 89 % |
  | workspace | 2.3 s | 12.5 s | 18 % |
  | concierge | 5.3 s | 9.8 s | 54 % |
  | publish | 4.0 s | 9.1 s | 44 % |
  | m1 | 2.8 s | 8.7 s | 32 % |
  | m2 | 3.7 s | 7.3 s | 51 % |

  `Do-Suites` adds nothing of its own, so `dev suites` ≈ 5 min 16 s.
- Two sleeps in `ui-use` alone (`:274` `-Seconds 13`, `:278` `-Seconds 9`) are 22 s. The
  replacement pattern already exists in this tree, twice: `m4:175-179`
  (`foreach 1..15 { sleep 1; if (…) break }`) and `ui-use:519-527` (12 s deadline, breaks on
  condition). Generalising it cuts minutes without weakening a check.
- The largest non-sleep item is `m4:92` `Dodona @("publish", "--project", $repo)` — one real
  three-project compile, ≤27.7 s. It is also the one place a suite contends with this repo's
  real `obj/`, which `tests/publish-acceptance.ps1:171` already documents as a hazard.
- **No unit test project exists.** `Dodona.sln` holds `Dodona`, `DodonaShim`,
  `DodonaFakeAgent`, `DodonaUi`, `Spike2Client`;
  `grep -rl "xunit\|NUnit\|MSTest\|Microsoft.NET.Test.Sdk" --include=*.csproj .` → nothing.
  `Claims.cs` is 53 lines of pure algebra (`Normalize`, `Parse`, `Overlap`, `Covers`) with no
  I/O, and the only way to exercise it today is to start a daemon, init a repo, create a ticket
  and invoke the gate. `Ver.NewestSource`, `Instance.Canonical` and the routing-tier decision
  are in the same position. CLAUDE.md's own *"pure-logic checks are instant"* describes a lane
  that does not exist.
- `m0` prints six named checks but no `N checks, M failed` tally, so `dev.ps1`'s `Run-Suite`
  reports `no tally line` and **cannot detect a failure in `m0` at all** — it counts fails by
  grepping `: FAIL`, and `Do-Suites` would print `m0  no tally line` and exit 0.

**What makes recurrence structurally impossible.**

1. **(b) Replace fixed sleeps with condition-with-deadline**, using the pattern already in
   `m4:175-179` and `ui-use:519-527`. This is deletion of magic numbers, not new machinery, and
   it targets 68 % of the cost. A `Wait-Until` helper belongs in `tests/_workspace.ps1`, which
   all 11 suites already dot-source.
2. **(a) Already installed — `dotnet test`.** Add `tests/Dodona.Tests` to the existing solution
   and move the pure functions' verification there: `Claims`, `Instance.Canonical`,
   `Ver.NewestSource`/`ImageBuiltFrom`, `Policy`, `Repos.ForClaims`, the routing-tier decision.
   Sub-second and model-free; `dev test unit` becomes the iterate-fast lane §1 already promises.
3. **(c)** `Run-Suite` must **fail** on a missing tally line rather than reporting it as prose.
   A suite that cannot report its result is a red suite.
4. **Correct the recorded number in CLAUDE.md §1** — not as the fix, but because leaving a 4×
   error in the file that governs how often anyone verifies is itself RC6.

**Confirming (<30 s).** `Measure-Command { powershell -File tools\dev.ps1 suites }` → the
figure printed must match what CLAUDE.md claims. `dotnet test -c Release` → under 5 s, covering
`Claims.Covers` including the case `m1` currently proves end-to-end.

**Cost / what it breaks.** Condition-based waits can hang if the condition never arrives —
hence *deadline*, and CLAUDE.md §0.1's rule that a wait must name the thing that un-sticks it.
Some logic must be extracted from `Daemon.cs` to be unit-reachable; that is real work and the
reason this is ranked here rather than higher.

---

### RC6 — Prose is the enforcement mechanism, and the prose is stale, self-contradictory and literally corrupt

**Cause.** The standing response to a failure is to write a rule. There are now 3,801 lines of
governance prose against 10,336 lines of C#, nothing executes any of it, and it has begun to be
wrong in ways nothing can notice.

**Explains:** the *recurrence* in A. CLAUDE.md §3.1 documented "a daemon outlives its window and
the operator believes the machine is idle"; the identical incident then happened again, to the
same person, from the same cause. Rule 4's premise, confirmed.

**Evidence.**

- Sizes: `ORCHESTRATOR-DESIGN.md` 977, `WORKSPACES-CONCIERGE.md` 578, `DEBUGGING.md` 548,
  `ORCHESTRATOR-REVIEW.md` 516, `CLAUDE.md` 483, `M5-DELIVERY-PLAN.md` 380,
  `LANE-LIFECYCLE.md` 196, `SKILL.md` 89, `docs/README.md` 34 = **3,801 lines**. Two of today's
  five commits are pure prose: `ba555b5` (+173, 0 code) and `0ab58d3` (+422, 0 code, "plan, not
  built").
- **Corrupt.** `CLAUDE.md:253` and `.claude/skills/ship/SKILL.md:43` each contain a literal
  `0x08` byte where `tests\brain-acceptance.ps1` should be — Python's `\b`, written through an
  editing script. `cat -A` shows `tests^Hrain-acceptance.ps1`. One of the eleven mandated suite
  commands is unrunnable as written, in both documents that carry it, and has been for at least
  two commits. `tools/dev.ps1`'s `AllSuites` holds the same information and is correct —
  because it is executed.
- **Stale.** `CLAUDE.md:443-446` still asserts *"A lane currently has no working directory of
  its own (no `cwd` column; `_primary` is hardcoded in `SpawnAgentLaneAsync` and
  `RespawnLaneAsync`)."* Written by `ba555b5` at **18:11:55**, falsified by `f9aaf25` at
  **19:13:39** — 62 minutes — which added the column
  ([Store.cs:214](../src/Dodona/Store.cs#L214)) and the ladder that replaced `_primary`
  ([Daemon.cs:1638](../src/Dodona/Daemon.cs#L1638)). It is still there.
- **Self-contradictory.** CLAUDE.md §1: *"Do not call `dotnet build`, a suite, or `publish`
  directly. Use the wrapper."* `.claude/skills/ship/SKILL.md` — which CLAUDE.md §5.1 names as
  the canonical delivery path — step 1 is `dotnet build Dodona.sln -c Release`, step 2 is
  eleven raw suite invocations, step 4 a raw `publish`. `cd53389` created the contradiction:
  `git show --stat cd53389` touches `CLAUDE.md`, `Program.cs`, `tools/dev.ps1` and **not**
  `SKILL.md`, breaking CLAUDE.md §5.1's own rule ("when the delivery process changes, change
  the skill in the same commit") in the very commit that changed the delivery process.
- The eleven-suite list is duplicated in three places (CLAUDE.md §3, SKILL.md §2,
  `dev.ps1 AllSuites`). Only the executed one is right.

**What makes recurrence structurally impossible.**

1. **(b) Delete the duplicates.** Remove the suite list and the raw commands from `CLAUDE.md`
   and `SKILL.md`; point both at `dev help` / `dev suites`. A list that exists once, and is
   executed, cannot disagree with itself. Rewrite `/ship` as a thin wrapper over `dev ship`
   plus the commit step, so there is one door in the skill as well as in the prose.
2. **(c) A repo lint** (sub-second; belongs in RC5's unit project): (i) no `.md` or `.ps1` may
   contain a control byte outside tab/CR/LF; (ii) every `tests\*.ps1` path named in any `.md`
   must exist on disk. (i) catches the backspace; (ii) catches the next one.
3. **(b) Stop writing rules that assert code state.** §5.2's "a lane has no cwd column" was a
   fact about `Store.cs`, and facts about code belong in checks — an assertion that went red
   when `f9aaf25` added the column would have forced the update. Where a rule must stay prose,
   it should describe an *invariant to preserve*, never a *state that happens to hold*.

**Confirming (<30 s).**
`grep -c $'\x08' CLAUDE.md .claude/skills/ship/SKILL.md` → 0, and the lint goes red when a
backspace is reintroduced. `grep -n 'dotnet build' .claude/skills/ship/SKILL.md` → nothing.

**Cost / what it breaks.** Agents lose the copy-pasteable suite list from CLAUDE.md and must
run `dev suites`. That is the intent.

---

## 3. Work order

Each item is independently shippable and independently verifiable.

| # | Item | Depends on | Verify |
|---|---|---|---|
| 1 | **Untrack `tests/*-output/`** (`git rm -r --cached`, `.gitignore`). RC4. | — | `dev suites` then `git status --porcelain` is empty |
| 2 | **Delete `git add -A` from `/ship` step 3**; explicit pathspecs. RC1. | — | `grep 'add -A' .claude/skills/ship/SKILL.md` → nothing |
| 3 | **`.claude/settings.json` PreToolUse hook denying `git add -A` / `git add .` / `git commit -a`.** RC1. | 2 | run `git add -A` in a session → denied, message names substitute |
| 4 | **Remove `ClearBlockers`'s `dodona stop-all` from `Do-Build`.** See §4.1. | — | `dev build` while another daemon runs → that daemon survives |
| 5 | **`Use-TestBinaries` in `tests/_workspace.ps1`; all 11 suites run from `$DODONA_HOME\bin`.** RC3. | 1 | `dev suites` then `dev check` → "in the build output: nothing" |
| 6 | **`Autostart`/`Cx` refuse to daemonize from a `\bin\` path**; repoint CLAUDE.md §2 and `/ship` step 4 at the published binary. RC3. | 5 | `.\src\Dodona\bin\Release\net8.0\dodona.exe status` → refusal naming `publish` |
| 7 | **Lane liveness from `Instance.LivePipes()`** in `ps` and `stop-all --lanes`; shim-info demoted to a pid lookup. RC2. | — | pipe count == `ps` count (RC2 snippet) |
| 8 | **Shim exits when its child exits.** RC2. | 7 | kill an agent's `claude.exe`; its `DodonaShim` is gone within a second |
| 9 | **Reconcile asks the OS before declaring "shim gone"**; delete `attempts: 1`; `Ensure*Async` never spawns while the predecessor's pipe is live. RC2. | 7, 8 | restart a daemon 5× → lane-pipe count unchanged |
| 10 | **Checks for 7–9 in `m0`/`m4`**, each `dev prove`d against `cd53389` first. RC2. | 7–9 | `dev prove m4 <check>` prints PROVEN |
| 11 | **`Wait-Until` helper in `_workspace.ps1`; convert the fixed sleeps**, starting with `ui-use:274/:278`, `brain`, `compression`, `m0`. RC5. | — | `Measure-Command { dev suites }` well under 5 min, 343 checks still green |
| 12 | **`Run-Suite` fails on a missing tally line** (today `m0` cannot report a failure). RC5. | — | delete `m0`'s last check → `dev test m0` exits non-zero |
| 13 | **Repo lint: no control bytes; every `tests\*.ps1` path named in a `.md` exists.** RC6. | — | reintroduce the backspace → lint red |
| 14 | **De-duplicate the suite list and raw commands** out of CLAUDE.md §3 and SKILL.md; `/ship` becomes a wrapper over `dev ship`; correct §1's timing figure. RC6. | 4, 11, 13 | `grep 'dotnet build' .claude/skills/ship/SKILL.md` → nothing |
| 15 | **`tests/Dodona.Tests` unit project**; move `Claims`, `Instance.Canonical`, `Ver.NewestSource`, `Policy`, `Repos.ForClaims`, the routing-tier decision; `dev test unit`. RC5. | — | `dotnet test` < 5 s |
| 16 | **Adopt worktree isolation for concurrent sessions** — one `.claude/worktrees/<session>` each; document in CLAUDE.md §0 as the only supported way to run two sessions. RC1. | 1, 5 | `git worktree list` shows one entry per live session |

**Must not be done in parallel:**

- **7, 8 and 9 are one causal unit, in that order, by one agent.** 8 without 7 makes `ps`
  under-report further (shims vanish before their records do); 9 without 8 leaves `##shutdown`
  as the only exit for a shim the daemon has just decided to keep.
- **5 and 6 must not be split across agents.** Between them lies a window where the suites still
  launch from `bin\Release` while `Autostart` refuses to — every suite goes red.
- **1 must land before 5, and before any `dev prove` verdict is believed.** Until the artifact
  churn is gone, no diff-based guard can distinguish a real change from noise.
- **4 must land before any two agents work concurrently (i.e. before 16).** While `dev build`
  calls `stop-all`, two agents following CLAUDE.md §1 destroy each other's daemons.
- **11 and 12 must not run concurrently with 10** — both edit the same suites' assertions.
- 2, 13 and 15 touch nothing else and can go in parallel with anything.

---

## 4. Do not do

**4.1 — Revert: `tools/dev.ps1`'s `ClearBlockers` must stop calling `dodona stop-all`.**
`ClearBlockers` is called unconditionally from `Do-Build` and runs `& $dodona stop-all`.
`StopAll` ([Program.cs:504](../src/Dodona/Program.cs#L504)) stops **every** registered workspace
daemon, **the concierge**, and every live `dodona-*-ctl` pipe matching no registry entry
([Program.cs:528-536](../src/Dodona/Program.cs#L528-L536)) — which includes another agent's
`DODONA_HOME`-isolated suite daemons, because `DODONA_HOME` scopes the registry but not the OS
pipe namespace. So the command CLAUDE.md §1 tells every agent to run *before starting work* is a
machine-wide daemon kill. `publish --all` was deliberately narrowed to registry-scoped for
exactly this reason and `tests/publish-acceptance.ps1` proves it — it stands up a foreign daemon
under a second `DODONA_HOME`, confirms its pipe is live, and asserts
`all_never_swaps_a_workspace_from_another_registry`, `foreign_daemon_survived_untouched`,
`foreign_daemon_still_answers_its_pipe`. `stop-all` has no suite at all. Note also that
`ClearBlockers` only fires the kill when a process **named `dodona`** is in the build output —
the three `DodonaShim` blockers `dev check` finds today trigger nothing — so it is
simultaneously too broad and too narrow. Once item 5 lands it has nothing to clear: delete it,
do not narrow it.

**4.2 — Do not trust `dev prove`'s `VACUOUS` verdict as currently written.**
`.dodona/dev-logs/dev-20260818-194154-prove.log` declares
`respawned_ticket_lane_returns_to_its_worktree` vacuous. The check is sound; it passes because
another session had already committed the fix in `f9aaf25`. Its only precondition is
permanently satisfied by RC4's churn. **Keep `prove`** — the idea is right and it is the best
thing `cd53389` shipped — but fix its guard (items 1 and 4) before any agent rewrites a good
check on its say-so.

**4.3 — Do not "fix" `ps` by reaping harder.** `ReapShimInfo` deletes only records whose pid is
dead, so it is not itself destructive — but it makes `ps` *look* authoritative while remaining
structurally blind to any shim whose record was lost by other means. Four live agents here have
no record (their `DODONA_HOME` was deleted) and are invisible to `ps` and unreachable by
`stop-all --lanes`. More reaping cannot find them; reading `\\.\pipe\` can.

**4.4 — Do not build a second copy of the tree to get around a lock, ever again.** Confirmed on
disk: the copy at `…\7888095e-…\scratchpad\tree\` has been deleted while two of its processes
still run. It doubled every build and every test and produced a stale-binary failure that was
then debugged as a real one. Item 5 removes the reason.

**4.5 — Do not answer this report with a new CLAUDE.md section.** Rule 4, and RC6: §3.1 already
documented the exact incident that recurred. If an item cannot become code, a deletion, or a
check, say so explicitly instead of writing it down.

**4.6 — Do not preserve `.claude/worktrees/never-stuck`, and do not fear deleting it.**
`git rev-list --count main..never-stuck` → **0**; the worktree is clean; its tip `f8d6729` is on
main and 9 commits behind. `git worktree prune` and `git branch -d never-stuck` lose nothing.
The *mechanism* should be reused; the stale registration should not.

**4.7 — Do not treat `docs/M5-DELIVERY-PLAN.md` as work in progress.** 380 lines, marked "plan,
not built", committed in `0ab58d3` alongside 42 more lines of prose and zero code. Nothing in
the work order depends on it. It should not be extended until the items above are done — a
second design document is the failure mode rule 3 names.

**4.8 — Do not add `stop-all` coverage as the response to 4.1.** A suite that proves `stop-all`
is machine-wide would enshrine the behaviour. The narrower `--workspace`/`--all` targeting that
`publish` already has is the shape `stop-all` should take, and *that* is worth a suite.

---

## 5. Unknowns

- **Where the twenty-minute figure came from.** 5 min 16 s is what this machine does now, with
  11 orphan shims already resident. The most likely explanation is CLAUDE.md §2's own runaway
  auto-publish — a full three-project build every ~65 s, 72 daemon restarts — contending for CPU
  and for this repo's `obj/`, which is exactly what `m4`'s internal build needs. No artifact
  proves it. Settled by re-measuring under a live auto-publish daemon. Either way the recorded
  conclusion ("the rest is inherent and cannot be optimised away") is false today.
- **Whether any of the second session's work was lost rather than carried.** `f9aaf25` committed
  what was in the tree at 19:13:39; git cannot show what was pending, overwritten or reverted
  before then. Settled by reading both sessions' transcripts under `~/.claude/projects/`, which
  I did not do.
- **Why lane 20's pipe missed its single 500 ms connect.** Consistent with
  `DodonaShim/Program.cs:114-117` (`conn.Cancel(); server.Dispose(); await Task.WhenAll(...)`
  with `pumpOut` parked in a non-cancellable `newLine.Wait(500)` at
  [:105](../src/DodonaShim/Program.cs#L105)), leaving the next `NamedPipeServerStream`
  uncreated for up to ~500 ms after a handoff — the same window reconcile allows. Not
  instrumented. Item 9 makes the answer unnecessary, but it would tell you whether the handoff
  race also costs adoptions of *work* lanes.
- **Whether the `mlroot-*` and `repro-*` lanes came from a suite or an ad-hoc script.** No
  `mlroot` or `repro-` string exists in `tests/`, `tools/` or `docs/`, and their children are
  real `claude.exe`, so they were almost certainly hand-written debugging runs. That means
  `tests/_workspace.ps1`'s `DODONA_HOME` discipline protects the suites and nothing else — the
  `freeze-repro-0397` workspace, created by such a script, is registered in the operator's
  **real** registry with a live agent from 16:22. Settled by the authoring session's transcript.
- **Whether the two sessions ever lost each other's CLAUDE.md text.** `ba555b5` and `0ab58d3`
  both edit it 14 minutes apart on a linear parent chain; git resolved it by ordering. Whether
  either agent's prose was silently dropped is not visible in the recorded diffs.
- **How the `0x08` byte was introduced.** The editing scripts in the other session's scratchpad
  (`claudemd.py`, `psfix.py`) are the obvious candidate — a Python string containing
  `"tests\brain"` — but I did not read them to confirm. Item 13 makes the answer unnecessary.
- **The exact cost of `m4`'s internal build in isolation**, bounded at ≤27.7 s warm. Measuring
  it directly means running `dotnet build`, which I avoided so as not to disturb the tree.
