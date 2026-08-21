---
name: ship
description: Verify, commit, and publish Dodona work — the complete delivery path. Use whenever a change is finished, the operator asks to ship/publish/commit, or before reporting any work as done. Runs the acceptance suites, commits with an incident-honest message, publishes (hot-swapping live daemons and UI), and verifies the swap actually landed.
---

# Shipping Dodona work

The full delivery path. Every step exists because skipping it once cost the operator real
time — the incident is named where that matters. Do the steps in order; do not report
"done" from any earlier point.

## 1. Build — through the wrapper, never `dotnet build`

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 build
```

This step said `dotnet build Dodona.sln -c Release` until 2026-08-19, which CLAUDE.md §1
explicitly forbids — a contradiction `cd53389` created by changing the delivery path without
touching this skill, breaking §5.1's own rule that the two move together (P5.3). Whichever of
the two you followed, you were violating the other.

**Why the wrapper is mandatory rather than tidier:** a locked output file makes `dotnet build`
report `Build FAILED` with ten screens of MSB3026 retries, which reads as "your code is broken"
when it means "a daemon you cannot see is holding a file". `dev build` names the pid and the one
command that frees it, on line one. It stops nothing on your behalf — clearing a holder is
always your explicit call.

If it fails, that is the **headline** of your report, not a footnote. An edit that has not
compiled is a claim, not a change.

If you do need to find a holder yourself, resolve it by **pid / exe path / instance id — never
by process name** (a name-based kill once murdered the operator's live session mid-trial):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 check   # names any holder
dodona ps                                                                  # what is running, machine-wide
```

## 2. Run the acceptance suites

**Through `dev`, never by invoking a `.ps1` directly.** That is not a style preference: the
wrapper is what makes a suite which crashed, hung or printed no tally a FAILURE instead of a
blank line, and it is what runs them five at a time. Invoking the scripts by hand gives back
exactly the blindness this step exists to remove — `ui-use` (since split into the four `ui-*`
suites) spent its whole life dying in its own `finally` and reporting nothing, and nobody saw
it, because the tally was never required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test unit        # ~1 s, while you edit
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test m3 brain    # the ones you touched
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 gate             # ONCE, before you merge
```

**Run the suites your change touches — the full set belongs at the merge, not at every edit.**
`dev test` takes any combination and runs them concurrently; CLAUDE.md §1 has the
change-to-suite table. This is now the operator's **standing directive** rather than an
efficiency argument (CLAUDE.md §0.1, 2026-08-20): the heavy suites are not authorized as a
default anywhere, and `dev gate` at every edit is how verification turned back into a thing to
skip.

`dev gate` is the real step here — it runs every suite AND asserts the **ten** invariants
(nothing left in the build output, the run dirtied nothing, no wrapper or agent process that
outlived it, the commit guard deployed and unoverridden, the live build's commit resolvable in
`git log`, the run inside its time budget, no changed file quietly altering its BOM or line
endings, …). **Measured 2026-08-20: ~271 s all in**, against the I7 budget of 300 s. This step
said "~95 s all in / about 70 s of suites" until then, and a number that stale gets quoted —
it is what made `dev gate` sound like a thing to reach for casually. Re-measure rather than
trust either figure; `dev gate` prints its own timings.

Exit code 0, or fix before proceeding. UI affordances need a check in one of the four UI
suites — `ui-grid` (the box, the panes, the tiles), `ui-shell` (N workspaces in one window),
`ui-ask` (the overlay that asks) or `ui-wake` (a window over a sleeping workspace), driven via
UI Automation — not only a `ui dump` assertion — dumps prove the UI *reports* correctly while the
first thing a person tries can still be a dead end.

If a fresh failure is in the **test**, fix the test and say so; known test traps are in
CLAUDE.md ("Windows & PS 5.1 traps"). If it is a TIMING failure, do not add a `Start-Sleep`:
wait for the condition the check asserts (`Wait-Until` in `tests/_workspace.ps1`).

## 3. Commit

```powershell
git status --short                # FIRST: read the tree. Decide what is yours before staging anything.
git add -- <path> [<path>...]     # explicit pathspecs are still the habit; see below
git status --short                # confirm: staged = exactly your paths, nothing else
git commit -F <message-file>      # -F, not -m: inline messages with quotes break PS 5.1
```

**THE BAN ON `git add -A` HERE WAS STALE, AND SAYING SO IS SAFER THAN LEAVING IT.** This step
used to forbid it outright on the grounds that *"two sessions share this checkout, so staging
the whole tree stages the other session's uncommitted work"* — the `f9aaf25` incident, whose own
message admits it: *"Carries M5.1's lanes.cwd migration and its Ver.Schema bump to 8, which were
in the working tree from another lane."* **That premise no longer holds.** Every session works in
a worktree of its own (CLAUDE.md §0.0), so a broad add can only sweep up files this session put
there, and the sharing that made it dangerous is prevented structurally.

What survives is the habit, for a different and smaller reason: **read the tree before you
stage.** A broad add still quietly commits a scratch file, a stray `.log`, or a plan document you
never meant to track — and review that follows the irreversible act is not a control. Explicit
pathspecs remain the default here because they make you look. A rule kept after its reason
expired is worse than no rule, though: it teaches that the rules are decoration.

A file you did not put there is not yours to commit, even when it looks finished. Say so in
your report and leave it in the tree — an orphan needs a decision, not a drive-by.

The message says what changed, **why**, and names any incident or gotcha discovered —
commit messages are this project's history of record (there is no other memory).

## 4. Land the work where this project's process says it lands

**Two questions, and the second is not optional because the first was answered.** You are in a
worktree on a branch (CLAUDE.md §0.0), so *something* has to move the work onto the trunk, and
publish follows `main` — a commit sitting on your own branch reaches the operator's app never.

1. **Does this project already have a delivery process of its own?** Its `CLAUDE.md`, its own
   skills, a `"delivery": "pr"` in `dodona.json` (CLAUDE.md §5.2, `docs/M5-DELIVERY-PLAN.md`).
   Branch off `develop`, a naming convention, push, open a PR, a human reviews, the forge
   merges. **If it does, that process governs and you follow it.** It is not a choice between
   that and the steps below.
2. **Whichever answer you got, the worktree is still yours to deal with.** A PR flow does not
   absolve you of a branch left checked out in `.claude\worktrees\<name>`, and a local merge
   does not absolve you of the push the project expects.

The local ff-only merge below is the **fallback for a project that has no process of its own**,
and the reason this step mentions worktrees at all. It is Dodona's own ceremony; do not apply it
to a repo that told you otherwise.

```powershell
# from the SHARED checkout, which is where main lives (section 0.0)
git -C <shared-checkout> merge --ff-only <your-branch>
```

If `--ff-only` refuses, main moved under you: rebase your branch and re-run `dev gate` before
merging. Do not merge with a merge commit to get past it, and do not park — announce what you
are rebasing onto (§0.1: name the thing that un-sticks it).

When the branch has landed, the worktree has done its job: `git worktree remove` it, or say in
your report that you left it and why. A tree nobody removed is the next session's confusion
about which of two checkouts is current.

## 5. Publish

```powershell
# Resolve the INSTALLED binary: newest build that is actually COMPLETE.
# `dodona.dll` is the test, and it is not decoration -- a versioned directory holding only
# `dodona.exe` exists on this machine twice over (2026-08-20 and 2026-08-21). A publish that
# raced another publish left one, and `Select-Object -Last 1` then resolves to a stamp whose
# .dll is missing: "The application to execute does not exist". This snippet said newest-by-name
# until an agent following it hit exactly that and spent a diagnosis on it.
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'dodona.dll') } |
    Sort-Object Name | Select-Object -Last 1).FullName 'dodona.exe'
& $dodona publish --project . --all
```

Run the **installed** binary, never `.\src\Dodona\bin\Release\net8.0\dodona.exe` — which is
what this step said until 2026-08-18, and is how a daemon ends up holding the compiler's own
output file. The binary now refuses to autostart from a source-tree build output. If nothing
is installed yet, `powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 ship`
bootstraps it.

This builds into a fresh versioned dir, hot-swaps every running daemon without
interrupting agents mid-turn (M4), updates any live UI, and re-points the desktop
shortcut. The drift watcher (`autoPublish` in dodona.json) would eventually do this on
its own; shipping means *now*, not after a debounce.

## 6. Verify the swap landed

```powershell
$env:DODONA_NO_AUTOSTART = "1"
# Resolve the INSTALLED binary: newest build that is actually COMPLETE.
# `dodona.dll` is the test, and it is not decoration -- a versioned directory holding only
# `dodona.exe` exists on this machine twice over (2026-08-20 and 2026-08-21). A publish that
# raced another publish left one, and `Select-Object -Last 1` then resolves to a stamp whose
# .dll is missing: "The application to execute does not exist". This snippet said newest-by-name
# until an agent following it hit exactly that and spent a diagnosis on it.
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'dodona.dll') } |
    Sort-Object Name | Select-Object -Last 1).FullName 'dodona.exe'
& $dodona status --root .
$env:DODONA_NO_AUTOSTART = $null
```

`$dodona` is re-resolved here rather than reused from step 4: publish has just created a NEW
versioned directory, so the newest one is now the build you are verifying.

The reported `build=` must be the one just published. Only now is the work "done".

## Blocked?

A parked swap announces its three answers (`swap-answer now | when-it-lands | hold`).
A failed publish leaves the old daemon running — nothing is lost; say what failed.
