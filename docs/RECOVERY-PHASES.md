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

## Phase 3 — Processes can die on their own  *(I3, I4, I6)* — **DONE 2026-08-19**

**Shipped. P3.1 → P3.2 → P3.3/P3.4/P3.5 in that order, one agent, as required.** What follows is
what was built and — more usefully — the four things this plan had wrong, each of which would
have shipped a silent regression if the checks had been trusted instead of proved.

| item | what shipped |
|---|---|
| P3.1 | Lane liveness comes from the OS: `Instance.LiveLanes()` reads the pipe namespace, `LaneLiveness.Live()` crosses it with live shim PROCESSES. `ps` counts lanes that way (workspaces, the concierge — which used to print `-` for its own two judgement tiers — and unregistered instances), and now prints a row for **live lanes belonging to no daemon and no workspace**: the four-agents-nothing-can-see case. `stop-all --lanes` stops a lane over its own pipe with `##shutdown`, so a lane whose `shim-lane*.json` was never written or has been reaped is reachable at last; shim-info is demoted to a pid sweep for whatever the pipe could not reach. |
| P3.2 | The shim exits when its child exits, once the buffer is DRAINED (`delivered >= buffer.Count`) — drained, not merely dead, or the daemon would lose the last turn m0 exists to protect. It also deletes its own `shim-lane*.json`, so the record dies with the process; nothing in the tree deleted one on any exit path before, which is why "24 lanes" was every lane a workspace had ever spawned. |
| P3.3 | The lease: no client connected for `DODONA_SHIM_LEASE_SEC` (default 1800) and the shim exits, taking the child with it. Closes the orphan class P3.2 cannot — a live agent whose daemon was killed with `-Force` and never came back. Names the condition that un-sticks it, never a person (CLAUDE.md §0.1). |
| P3.4 | Reconcile asks the OS before declaring a lane dead. `attempts: 1` for utility lanes is gone: **no pipe and no live shim → zero attempts** (faster than the one 500 ms knock this plan wanted to remove), **alive → be patient whatever the role**, and if it will not converse it is sent `##shutdown` rather than abandoned. Abandoning was the manufacturing step for an immortal orphan: the row was marked dead, which dropped the only reference that could stop the process, and a replacement started 160 ms later. |
| P3.5 | `EnsureBrainAsync`/`EnsureRouterAsync` never spawn beside a live predecessor. A lane whose pipe or shim process is still there is told to go, once (`_shutdownAsked` — the guard is on the path of every routed sentence, so asking twice is latency paid forever); if it will not go, the spawn is REFUSED and announced. The reap loop no longer writes `utility_lane_reaped … "shim gone"` over a live pipe either — that lie is what let the guard be bypassed. **Extended past what this row asked for:** the COMPRESSOR pool has the same hole — its arithmetic counts a member only if it is adopted, so a live-but-unreachable one is invisible and gets a replacement started beside it. Same bug, third costume; the guard closes the class rather than the two call sites the plan happened to list. |
| P3.6 | **Was already done before this phase started, by 69e8003.** `stop-all` is registry-scoped and NAMES daemons the registry does not own instead of stopping them. This row was stale; `--lanes` now follows the same rule, so a lane belonging to no registered workspace is named, and `--orphans` is how you mean it. |
| P3.7 | 15 new checks: 11 in `m0` (P3.1–P3.4, taking it from 8 checks to 19) and 4 in `brain` (P3.5, 41 to 45 — `brain` owns router/brain lifetime and already stops and restarts the classifier, so the incident's own shape lives there; m4 would have paid for a real build to assert nothing about hot swap). Every one `dev prove`d red against 69e8003 before being believed. |

**Becomes impossible:** an orphan; an unkillable agent; a count that lies in either direction; a
cleanup that reaches someone else's work.

### What this plan got wrong

**1. "A lane pipe in the namespace" is NOT a sound liveness test, and this phase nearly shipped
on it.** P3.1/P3.4 as written say to read `Instance.LivePipes()` and believe it. Measured: **a
shim's pipe name blinks OUT of the namespace between clients** — the serve loop disposes its
`NamedPipeServerStream` and constructs the next one, and in that gap the name is simply not
there. Probed directly: 8 of 192 reads over 1.5 s saw no pipe while the shim was alive and
instantly connectable. The window is not rare, it is *synchronised* — every shim in a workspace
disconnects the instant its daemon exits, and the next daemon's reconcile runs milliseconds
later. A single read there declared four to seven live lanes "gone" per restart and orphaned
every agent in the workspace. It was caught only because `brain-acceptance` noticed a restart
had stopped adopting anything, and because the shim was made to say why it exits. So liveness is
the UNION of two OS answers (pipe, or a recorded shim pid that is alive) — see `LaneLiveness`,
which carries the measurement — and `stop-all` captures its lane targets BEFORE it stops any
daemon, because that is the one moment every pipe is steady. **Do not narrow this back to one
answer.**

**2. ~~`publish-acceptance` does not leak four `DodonaShim` processes, and never has.~~ THIS
CORRECTION WAS ITSELF WRONG, and it is left here struck through rather than deleted, because the
way it was wrong is the more useful lesson.** The plan said that suite leaks four wrappers on every
successful run. This session grepped it for `lane-start`, found none, and concluded it "starts no
lanes at all, so it cannot leak a wrapper" — then wrote that into the plan, into
`tests/_workspace.ps1`, into `tools/dev.ps1` and into two commit messages, and used it to argue
that `reaped 0` was the wrong acceptance signal for Phase 3.

It does start lanes. Its `apnoprov` section clears `DODONA_NO_AUTOSTART` **deliberately** — so that
one daemon runs the way the operator runs it — and an autostarting daemon's warm-up creates the
router, brain and compressor pool. Those are shims. They then outlive the daemon exactly as designed,
with a 30-minute lease that has not expired by the time the runner looks. **So the plan's original
claim was substantially right, and `reaped 4` was a true report about a real orphan.**

Caught only because Phase 5 made the reaper NAME what it reaps: the log said `DodonaShim`, not
`dodona`, and the daemon-mid-exit story died on the spot. A grep for one spawn verb is not a survey
of what a suite starts — autostart spawns without ever naming a command. The fix is in the suite,
where it belongs: it now stops the lanes its own daemon spawned (P6.2), and `publish` reaps 0 across
three consecutive runs.

**3. `dev gate`'s own leak counter could not see the leak.** `LeakedTestProcesses` matched
`$env:TEMP\dodona-*`, which was every suite temp directory until the per-suite sandbox landed
as `$env:TEMP\dsb-<6hex>` (short, for MAX_PATH). Everything a suite makes now nests inside that,
so the counter matched almost nothing and the gate opened by reporting "0 leaked" on a machine
that had them. Fixed to match both.

**4. The verify snippet in this row was itself unsound.** It compared an instantaneous lane-pipe
count against `dodona ps` — the exact read that blinks. Both sides can be wrong at once, and a
flaky verification is how a real regression gets re-run instead of read. Replaced below.

**Verify:**
```powershell
# Nothing Dodona started may outlive the run. Asserted by `dev gate` as I3, so this is the
# by-hand form: PROCESSES (which do not blink), scoped by PATH, never by name (CLAUDE.md 4).
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 gate     # I3 row must say PASS

# And the counts must agree. `dodona ps` is the union of both OS answers; so is this.
dodona ps                      # LANES column, per workspace
dodona stop-all --lanes        # then it must report what it stopped, and ps must go to zero
```

### Verified against REAL models, 2026-08-19

Everything above was proved with the fake agent. The operator then authorised spending quota to
validate real-world behaviour, so it was. One workspace at a time, isolated `DODONA_HOME`, a
scratch repo — never their own — and the PUBLISHED build.

| what | result |
|---|---|
| a workspace wake | **four real `claude` processes** warm: brain, router, two compressors, roles correct in the store |
| a sentence typed and submitted with Enter | went through the real `PreviewKeyDown` path; box cleared |
| first sentence, nothing live | `tier=first confidence=only` — spawned a lane on opus/high with an undo line. **Correct and by design**: with no live work lane there is nothing to disambiguate, so the classifier is not asked. Do not read `classified=0` here as the old dead-router bug; that one is `tier=focus confidence=no-classifier`, and `routing_unrouted` was 0 |
| second sentence, a lane live | **the real classifier decided**: `kind=addendum target=HEIGHT confidence=high` in 5071 ms, reason *"Refine mask to use curvature alongside height"*, delivered to that lane. This is the ladder that was DEAD IN PRODUCTION for two days, now proved live with a real model rather than a fixture |
| a real agent doing real work | edited its file and reported accurately |
| **the claim gate, under a real agent** | told to write one file inside its claim and one outside, it reported `src/foam.cs: SUCCEEDED` and `docs/notes.md: DENIED`, and disk agreed exactly. `.dodona-bypass.log` absent, so the gate never failed open. **The safety model (§6 layer 1) holds against a real `claude -p` running bypassPermissions**, which had only ever been asserted from a measurement, never demonstrated |
| P3.2 against a real agent | killed only the agent by recorded pid: the shim exited itself, **after draining all 14 buffered lines**. The fake agent emits one to three, so the DRAIN half of P3.2 had never been under load |
| cleanup, every run | 0 processes under any Dodona path, 0 lane pipes |

**Still not covered, and these are the ones most likely to surface next:** two real work lanes
running concurrently; `approve` → `land` → merge with real work in the ticket (the gate and the
worktree were checked, the merge token path was not); a long session, so the 30-minute lease and
a hot swap mid-turn are still only exercised at 2 seconds and with fakes.

**Two probe bugs worth recording, because both wasted a run.** An instruction to the real agent
embedded `"` inside a native-command argument and PowerShell 5.1 mangled it — the agent received
a truncated sentence and sensibly asked for the rest, so that run said nothing about the gate.
And a cleanup check counted `claude`/`node` BY NAME machine-wide, reporting 18 survivors that were
the session's own tooling; by PATH it was 0. Both are the traps this repo already documents,
committed by the probe whose own skill forbids them.

**Cost / breaks:** `lane-respawn` can no longer reattach to a shim whose agent crashed — correct,
there is nothing to reattach to, and m0 covers the respawn path. A `stop-all` that leaves lanes
running now loses those agents after the lease (30 min) rather than never; the lane ROW survives
and `lane-respawn` resumes the session, so the loss is bounded and recoverable, where an
immortal process is neither.

**Still open, and deliberately not done here:** make the lane pipe stop blinking at all, by
keeping a listening server instance alive across the swap. That is the root fix and it would
retire the union — but `maxNumberOfServerInstances: 2` lets a second client connect while the
first is still being pumped, and `LaneRuntime.ConnectAndPumpAsync` then waits on a `!hello` with
no timeout, which is a hang. Fixing the hang first is the prerequisite; see Phase 6.

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

**LANDED 2026-08-19.** What the plan got wrong, recorded because the next phase inherits it:

- **The baseline was not green.** P4's "343 checks, 0 failed" was never true on this machine.
  Re-measured at `892b548`: `m0` printed no tally at all (so `dev.ps1` could not detect an m0
  failure), `ui-use` crashed in its own `finally` on a `concierge-stop` stderr line and
  reported **nothing** while `dev.ps1` counted it as fine, and `m4` (2) and `publish` (2) were
  genuinely red. Five of the twelve rows were lying or blank.
- **`dev suites` could hang forever, and probably did.** PowerShell's `& powershell ... 2>&1`
  reads stdout through a pipe that closes when the last INHERITED handle closes, not when the
  child exits; `publish-acceptance` leaks four `DodonaShim` processes and a shim's only exit is
  a message from a daemon that is already gone. Measured: eight minutes of waiting after the
  suite had printed its results. This is the most likely thing that was killed three times as
  "too slow" in the Phase 2 session.
- **P4.2's premise did not hold.** All three by-name queries already narrowed by path or by a
  GUID root, so they were not the concurrency blocker the plan names. They are converted anyway
  (`Get-ProcessesUnder`/`Stop-ProcessesUnder`, which refuse an empty directory) because
  `-like "*$root*"` is one empty variable away from matching every process on the machine.
- **P4.3's "m4 must run alone" is not required.** `publish` passes
  `-p:BaseOutputPath=<temp>\` per project, so its bin output never touches `src\...\bin`;
  only `obj\` stays in the tree, and `obj\` is contended by another COMPILE, which is `unit`
  and nothing else. m4 runs in the parallel wave and that is worth 28 s.
- **All twelve at once is worse than five at a time.** On 22 cores, never CPU-bound: `ui-use`
  went 42.5 s → 70.6 s and went intermittently RED. The contention is windows, UIA and
  process starts. Cap is 5, `DODONA_TEST_CONCURRENCY` overrides.
- **The three that were red are now FIXED** (2026-08-19, follow-up commit). Two were stale
  TESTS that could never pass, so the behaviour each protected had been silently unverified:
  `m4 gate_points_at_running_build` read `dodona-gate.ps1`, which DeployGate now deletes as
  stale (m1's `gate_deployed` asserts its absence), and `publish
  no_provenance_daemon_refuses_to_guess` called `dodona feed`, which is not a command, and
  grepped for "NOT watching", wording replaced when the watcher was changed to arm itself.
  The third, `publish default_target_is_the_owning_workspace`, was a REAL product bug:
  `Git.Sha` threw on a folder that is not a repository, six lines above the `haveProvenance`
  test and the "provenance: NONE" message written for exactly that case, so `publish --exe` on
  a plain folder died with an unhandled exception. `Git.ShaOrEmpty` fixes it; `dev prove`
  confirms the crash reproduces against HEAD.

- **`dev prove` had the SAME HANG as `dev suites`, fixed in one place and missed in the other.**
  It still read a suite through `& powershell ... 2>&1`, so proving anything against
  publish-acceptance hung forever on the leaked shims' inherited pipe handle -- measured, 24
  minutes on a suite that had finished, and being killed skipped its `finally`, leaving a
  throwaway worktree of HEAD registered in `git worktree list`. Both call sites now go through
  `Start-Suite`/`Complete-Suite`, so there is one code path, one verdict and one deadline.
  CLAUDE.md 0.3's "working around the same snag twice" applied to the fix itself.

- **THE SUITES' OWN LEAKS CHANGE THE ANSWER, and the gate now says so.** Accumulated leaked
  shims are not idle: measured with 78 of them alive, the full run took **300 s instead of
  87 s**, `m3` crashed outright with no tally, `brain` went red on nine timing checks, `ui-use`
  on two, `m4` on two -- and I7's failure text blamed a returning `Start-Sleep`, which sent the
  reader at entirely the wrong file. `dev gate` now counts them by PATH before the run and
  names them first in the I7 failure. Stopping them is P3's; not misdiagnosing them is not.
  Clean with `Get-Process | Where-Object { $_.Path -like "$env:TEMP\dodona-*" } | Stop-Process -Force`.

- **The I7 budget is 120 s, and it was 90 s for one commit, which was too tight.** Clean-machine
  green runs measured 54.6, 69.7, 74.4, 76.9 and **87.0 s** -- three seconds inside a 90 s line.
  A threshold just above the worst observation is a coin flip, not a budget.

- **Still red, and out of this phase's scope:** nothing. All twelve suites are green -- 416
  checks, 0 failed, 87 s on a clean machine. What remains open is not a red check: the suites
  leak shims (P3), and the claim gate has three paths that allow a write and leave no trace
  (`GateHook`'s silent `return 0`s), which is a safety-model decision for the operator.

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

## Phase 6 — What is left after Phase 3  *(operator directive, 2026-08-19; rewritten 2026-08-19)*

**This phase was written during Phase 4 and its first three questions were already Phase 3's
job — P3.2, P3.4 and P3.1, named in that plan since it was written.** The Phase 4 author did not
notice, so two phases described one fix, and the duplication is recorded here rather than
quietly deleted because "write a new phase" is this project's reflex and the reflex is the bug
(D-3, D-6). Those questions are answered: read Phase 3.

The operator's words stand and are what earned Phase 3 its I3 gate row: *"the fact that you have
strays is very alarming."* The alarming part was never the memory — it was what strays implied
about whether Dodona knows what it is running.

**Question 4 — should the suites still need a reaper? YES. Do not delete it.**
A shim can no longer outlive its agent, so the sandbox is no longer the only thing standing
between a suite and an immortal process. It is still load-bearing for two cases the shim cannot
reach: a `.ps1` that fails to PARSE never runs its `finally` at all (CLAUDE.md §0.2), and the
suites deliberately kill daemons with `-Force`, which reaches no cleanup either. The leak counter
stays for the same reason it was built: a silent leak is how this went unnoticed for two days.

**What actually remains, and it is small:**

| item | what | why it is not Phase 3 |
|---|---|---|
| P6.1 | Stop the lane pipe blinking: keep a listening server instance across the swap, so the pipe namespace alone is a sound liveness test and `LaneLiveness`' second answer can retire. **Prerequisite:** `LaneRuntime.ConnectAndPumpAsync` awaits `!hello` with no timeout, so a client that connects to a not-yet-pumped instance would hang the daemon's reconcile — that must be bounded first. | Phase 3 shipped the union instead, because a hang is worse than a race and the standing directive forbids adding one (CLAUDE.md §0.1). |
| P6.2 | **DONE, and the diagnosis in this row was wrong before it was fixed.** It said the suites' `stop-daemon` is fire-and-forget so the reaper catches daemons mid-exit — a race, not an orphan. That was inference, never observed: every process the reaper has ever named was a `DodonaShim`. The real cause is that `publish-acceptance`'s `apnoprov` section clears `DODONA_NO_AUTOSTART` on purpose, and that daemon's warm-up spawns utility lanes whose shims then outlive it **by design**, with a lease of 30 minutes. Genuine orphans, correctly reported. Fixed where it belongs — the suite stops the lanes its own daemon spawned. Verified: `publish` reaps 0 on three consecutive runs. A settle-wait was written first, against the wrong theory, and **reverted** rather than left in: it would have hidden nothing and cost 2 s x every suite for a guess. | **DONE** |
| P6.3 | An ad-hoc script spawning real Claude lanes outside `DODONA_HOME` (§6). The lease now bounds the damage to 30 minutes instead of forever, which is what produced the `mlroot` and `freeze-repro` orphans; preventing the spawn needs a hook refusing `lane-start` outside a test home. Design it, do not bolt it on. | Carried from §6 unchanged. Phase 3 bounded it; it did not prevent it. |

---
## Phase 7 — The tooling that would have made Phase 3 cheap  *(operator, 2026-08-19)*

**Written from the retrospective on Phase 3, which took ~102 minutes and hit fourteen problems in

a programme whose thesis is that problems are not allowed to exist.** That is not an argument

against the thesis; it is the evidence for it. The breakdown, because the shape matters more than

the count:

| what happened | how many | caught by |

|---|---|---|

| violated a rule that is written down and had been read | 3 | an assertion, a blocked build, hindsight |

| repeated a lesson the same session had just authored | 5 | `dev prove` x2, a red suite run, diff review |

| slipped where nothing was watching | 2 | manual diff review only |
| ...and once more while writing this phase up | 1 | `git show -w` on the commit that wrote P7.5 |

| genuine discoveries the plan did not know | 4 | a pre-existing red check; running the command |

**The finding that sets this phase's direction.** The session discovered that a lane pipe blinks

out of the namespace, wrote it into `LaneLiveness`' class comment, added it to CLAUDE.md §0.2 and

put it in the commit message — and then violated it **four more times, while writing those very

documents**. That is D-6 (*"a documented warning is not a fix"*) demonstrated on its own author,

inside one session, about a lesson twenty minutes old.

**But "prose does not bind" is the wrong conclusion, and the operator corrected it.** CLAUDE.md

§0.2 already contained the heredoc-backslash trap, and it had been read — at session start, forty

minutes before the moment it applied. The rule was not absent; it was *not present at the point of

use*. That is a retrieval failure, not a persuasion failure, and it has a mechanism: **a skill

gated on a trigger fires when the action happens**, which is why §0 names "skills + CLAUDE.md, or

tooling that enforces" as the reliable set rather than code alone. So this phase has two halves,

and the skills half is not a consolation prize.

### Half one — make the wrong thing impossible

| item | what | status |

|---|---|---|

| P7.1 | **`Invoke-StoreSql` fails loudly** (`tests/_workspace.ps1`). Three suites grew a local `Rows` that piped python's stdout and let stderr go nowhere, so a bad column name returned EMPTY — and `[int]''` is `0`, and `-eq 0` is a passing assertion. A Phase 3 check written against `lane` instead of `lane_id` therefore passed against every build ever made and would have passed forever; `dev prove` caught it only by luck of the draw. **A check that passes because its query is broken is indistinguishable from one that works.** | **DONE.** m0 and brain delegate to it; demonstrated throwing on the exact bad query |

| P7.2 | **`Test-DodonaPipeGone`** (two absences, 150 ms apart). `-not (Test-DodonaPipe $p)` is not a test for "gone", it is a test for "gone OR mid-reconnect" — and inside a 20-second `Wait-Until` it will eventually catch the blink and call a live agent stopped. The rule could not be remembered four times over, so it stopped being a rule. | **DONE.** m0 asserts with it |

| P7.3 | **Migrate `compression-acceptance.ps1`'s local `Rows`** to `Invoke-StoreSql`, and delete the pattern. Left alone in P7.1 to keep that change contained. | todo, ~5 min |

| P7.4 | **One kept worktree per commit in `dev prove`**, instead of a GUID-named tree built cold and deleted on every invocation. **DONE -- and the justification this row first carried was WRONG, which matters more than the item does.** It claimed ~19 minutes of the Phase 3 session went on "rebuilding an identical tree". Measured afterwards: a cold `dotnet build` of this solution in a fresh worktree is **~2 s**, and it does produce all four project outputs (checked, not assumed). So nineteen proofs spent roughly **40 seconds** building -- not 19 minutes. That figure was inferred from iteration wall-clock and never measured, which is the exact mistake CLAUDE.md section 1 records against itself: *a measurement you did not take is not a measurement*. Value as shipped, measured: cold proof 23.8 s, reused proof 20.1 s -- **~3.7 s, about 15 %**. Worth keeping, because it also removes the `worktree add`/`remove` churn and the leftover-registration failure mode a killed proof used to leave behind. It is not the lever. | **DONE**, honestly small |
| P7.4b | **DONE, and it was not parallelism.** Measuring P7.4 properly said the cost was the failing suite waiting out its deadlines, so the plan was to run proofs concurrently. The real answer is smaller and much better: **a suite run prints every check it ran**, so judging eleven m0 checks needs ONE m0 run, not eleven. The Phase 3 session ran m0 eleven separate times to read eleven lines that appear together in any single run of it. `dev prove` now takes `suite:check` pairs, groups them, runs each suite once (solo suites still solo, the wave still capped), and reports three verdicts rather than two -- PROVEN / VACUOUS / **MISSING**, because "never ran" is a typo or an earlier failure in the same suite and must not read as "does not test your change". **Measured: the session's fifteen checks, which cost 46 minutes and nineteen suite runs, are 40 SECONDS and two suite runs.** Deadlines were deliberately NOT shortened: a check that merely passes slowly would then read as PROVEN, a fake red, worse than a fake green. | **DONE**, ~70x |
| P7.5 | **Encoding invariance in `dev gate`**: for every file differing from HEAD, its BOM presence must match HEAD's blob, and a diff must not be mostly whitespace churn. This has now bitten THREE times, twice inside the commits that wrote this row, which is the strongest evidence for it that could exist: (a) Phase 3 silently added a BOM to seven files; (b) the Phase 7 commit silently rewrote this file's line endings, turning a 105-line insert into a 1214-line phantom rewrite; (c) the prose of this very row was written with literal control bytes in it instead of the escape sequences it meant to quote. **The mechanism, stated precisely, because the first version of this row got it wrong:** `core.autocrlf=true` on this machine, so git stores LF and checks out CRLF. A patch script that derives its newline from the WORKING TREE therefore sees CRLF, and replacing LF with CRLF on already-CRLF text yields CR-CR-LF -- the extra CR survives git's normalisation on the way in. Measured: 700 CRs against 638 LFs. **The rule for scripts: emit bare LF unconditionally and let autocrlf do its job; never sniff the tree.** By hand, `git show -w --stat` against the plain `--stat` is how you spot it, and a diff stat that looks too big for its change is the tell. BOM must be compared byte-exact against `git cat-file blob` (plumbing, so no smudge filters); PowerShell's `>` re-encodes and cannot be used for it. Folds into P5.1's lint project, which already bans control bytes in `.md` -- and would have caught (c). | todo |
| P7.6 | **REJECTED — do not re-propose.** The idea was for `DodonaShim` to refuse to start from a source-tree build output, mirroring `Ver.IsSourceTreeBuildOutput` on the daemon, because a Phase 3 probe launched a shim from `src\DodonaShim\bin\Release`, blocked the next build, and made one suite run against stale binaries. Reason for rejection, read off the project file itself: *"The shim is the one piece that must essentially never change (design §13). Dependency-free on purpose."* Adding the guard means either a `ProjectReference` to Dodona — which breaks that property outright — or a duplicated copy of the rule inside the most stability-critical component in the tree, and duplicated rules are precisely what P7.1 and P7.3 spent this phase deleting. Meanwhile the daemon already refuses the spawn, and `dev check` plus the gate's I1 catch anything that does get in. What is actually left is an ad-hoc probe doing it by hand, which is a habit, not a product path — so it belongs in P7.9 (`probe-hygiene`), where it now is, and where it states that the shim is the one binary with no guard of its own. | **rejected**, reason recorded |


### Half two — put the rule where the action is

**DONE — all three written, `.claude/skills/{check-authoring,file-patching,probe-hygiene}/SKILL.md`.**
Skills whose descriptions make them load when the trigger appears rather

than being read once at session start. Each carries rules that already exist somewhere and were

still violated, because "somewhere" was not "here, now".

| item | skill | fires when | carries |

|---|---|---|---|

| P7.7 **DONE** | `check-authoring` | adding or editing an acceptance check | assert on PROCESSES and STORE ROWS, never on an instantaneous pipe read (P7.2); every new check gets `dev prove`d and a VACUOUS verdict is a rewrite, not a shrug; a check whose query can error must use `Invoke-StoreSql`; name the incident in a comment |

| P7.8 **DONE** | `file-patching` | editing a tracked file with a script or heredoc | never `\\` through a bash heredoc — it collapses to `\` (§0.2); preserve BOM and line endings, do not let the writer choose them (P7.5); parse-check every `.ps1` you write; review the diff before believing the edit |

| P7.9 **DONE** | `probe-hygiene` | writing an ad-hoc script that starts a daemon, shim or agent | isolate `$DODONA_HOME`; get binaries from `Use-TestBinaries`, never from `src\*\bin` (§2, P7.6); no machine-wide mutation while a verification is in flight — `stop-all --lanes --orphans` newly WORKS (it never did before Phase 3) and its blast radius includes another session's suites |

**Becomes impossible:** a check that passes because its query failed; a liveness assertion that

a blink can satisfy; a silent encoding change; a shim started from the compiler's output.

**Becomes present at the moment it is needed:** the three rule sets above.

**What this phase does NOT claim.** P7.7–P7.9 are prose with a better delivery mechanism, and D-6

still applies to them: if a gated skill turns out to be skipped as reliably as a section of

CLAUDE.md was, the honest response is to promote its contents to half one and delete the skill,

not to write a fourth skill. Measure it — the signal is whether the same class of mistake recurs

in a later phase's retrospective.

---

## Phase 5 — Prose shrinks to what code cannot hold  *(I8)* — **DONE 2026-08-19**

**Shipped, and `dev lint` earns the last unasserted row in section 2.** Two of the five items were
already done by earlier phases without anyone noticing they were on this list, and one is
deliberately NOT done — see P5.5.

The lint was worth writing for a reason the row did not anticipate: **it found live defects, not
cosmetics.** Its stated targets (a `0x08` in CLAUDE.md and SKILL.md) were long gone; what it
actually caught, on its first run, was two `0x07` BEL bytes inside string literals in
`tests/publish-acceptance.ps1` — `"$out\ap-noprov.out"` with its `\a` eaten as an escape by
whatever wrote the line. The suite has been green throughout, writing its daemon diagnostics to a
filename containing a control character: present, correct-looking, and impossible to find.

| item | what |
|---|---|
| P5.1 | **DONE.** `dev lint` — sub-second, tracked files only (`git ls-files` is the scope, so `bin\`, `obj\` and other sessions' worktrees are excluded by construction rather than by a pattern somebody maintains). THREE rules, and the third was not in the plan: **(i)** no control byte outside tab/CR/LF — found the two BEL bytes above; **(ii)** every `tests\*.ps1` named in a `.md` must exist, with `(planned)` on the line as the exemption, because a plan describing an unwritten suite is correct prose and a lint that fires on correct prose gets switched off (one hit: `docs/M5-DELIVERY-PLAN.md`, now marked); **(iii)** **no MIXED line endings in a working copy.** That third rule exists because writing the first two exposed CLAUDE.md sitting at 758 CRLF against 20 bare LF — twenty lines Phase 7 had inserted with a forced `\n`. Git normalises that away on commit, so nothing wrong was ever stored and P7.5 passed *correctly*; but a mixed working file is exactly what makes the next patch script sniff the wrong newline, which is how Phase 7 produced a 1214-line phantom diff. It was invisible to every check that existed. | **DONE**, earns I8 |
| P5.2 | **DONE, and the duplication was smaller than claimed.** The row said the suite list lives in three places; measured, `ship/SKILL.md` had none, so it was two — CLAUDE.md and the code. What was actually rotting was the **timings column**: two of its twelve entries were wrong by more than double before Phase 7 corrected them. The column is deleted and points at `dev suites`, which measures on the machine you are using. The suite-to-coverage mapping stays, because that is judgement no command can print. | **DONE** |
| P5.3 | **DONE.** `/ship` step 1 mandated `dotnet build Dodona.sln -c Release`, which CLAUDE.md §1 forbids — so whichever of the two you followed, you were violating the other. Now `dev build`, with the reason inline: a locked output makes `dotnet build` report `Build FAILED` under ten screens of MSB3026, which reads as "your code is broken" when it means "an invisible daemon holds a file". | **DONE** |
| P5.4 | **Already done, by other phases.** CLAUDE.md §1's timing figure was corrected in Phase 7 (93 s at 69e8003, 100 s with Phase 3's checks, against the 54–72 s the table claimed); §5.2's lane-cwd denial was corrected on 2026-08-18 and now carries the correction. Verified both, rather than assumed. | **stale row** |
| P5.5 | **NOT DONE — deliberately, and this is the operator's call to overrule.** The row says to delete this document. It cannot be deleted as it stands, because it is now the only place holding things nothing else does: the pipe-blink measurement, why P7.6 was **rejected** and must not be re-proposed, why proof deadlines must not be shortened, and the four wrong claims Phase 3's plan made about itself. Deleting it destroys exactly the rejected-ideas record that §5 exists to preserve — the same argument D-6 makes about warnings. **What would have to happen first:** the decisions and rejections move to a `docs/DECISIONS.md` (or into CLAUDE.md §0), the schema and event-kind facts move to `DEBUGGING.md`, and only the completed how-to rows are deleted. That is a prose migration with no verification value, so it is proposed rather than performed. | **deferred, with a reason** |

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
| no wrapper or agent process survives a suite run | I3 | **earned 3** — asserted off the process table, scoped by path, with a 3 s settle for a `finally` still killing pids. NOT "lane pipes == `ps` lanes" as this row used to read: both sides of that comparison come from a read that blinks (Phase 3, "what this plan got wrong" #1), so it was a flaky assertion about a real invariant |
| two agents `dev build` concurrently, both succeed | I2 | **earned 2a** — but weaker than it reads: two concurrent builds of one *shared* tree were measured and both succeeded, so this row is regression protection for worktree builds, not proof of the fix |
| `dev suites` green while the live app runs, app untouched | I1, I2 | **earned 2a** — asserts the live app's pids survive the suites; prints `n/a` rather than a green line when no app is running |
| `dev suites` wall clock inside its budget | I7 | **earned 4** — the runner measures and prints its own wall clock and the gate asserts it. Budget is **90 s**, not the 35–45 s P4.3 projected: measured across six full runs the same code gave 53.7 s and 71.7 s, and a 60 s line would be red on half of green runs. `ui-use` is the long pole (42.5 s alone, up to 69 s in a parallel wave) and is really four suites wearing one name |
| repo lint clean | I8 | **earned 5** — `dev lint`, asserted by the gate as its tenth row. Three rules: control bytes, dangling `tests\*.ps1` references in docs (`(planned)` exempts), and mixed line endings in a working copy. Found two live BEL bytes in a green suite and one mixed file that no other check could see |
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
