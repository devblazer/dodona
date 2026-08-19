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
exactly the blindness this step exists to remove — `ui-use` spent its whole life dying in its
own `finally` and reporting nothing, and nobody saw it, because the tally was never required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test unit        # ~1 s, while you edit
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test m3 brain    # the ones you touched
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 gate             # ONCE, before you merge
```

**Run the suites your change touches — the full set belongs at the merge, not at every edit.**
`dev test` takes any combination and runs them concurrently; CLAUDE.md §1 has the
change-to-suite table. `dev gate` is ~95 s all in, which is cheap once and expensive twenty
times, and treating it as the only way to check is how verification turned back into a thing to
skip.

`dev gate` is the real step here — it runs every suite AND asserts the seven invariants
(nothing left in the build output, the run dirtied nothing, two concurrent worktree builds,
the live app untouched, the commit guard deployed, the installed build's SHA resolvable, and
the run inside its time budget). About 70 s of suites plus ~15 s of its own two builds.

Exit code 0, or fix before proceeding. UI affordances need a check in `ui-use` (driven via UI
Automation), not only a `ui dump` assertion — dumps prove the UI *reports* correctly while the
first thing a person tries can still be a dead end.

If a fresh failure is in the **test**, fix the test and say so; known test traps are in
CLAUDE.md ("Windows & PS 5.1 traps"). If it is a TIMING failure, do not add a `Start-Sleep`:
wait for the condition the check asserts (`Wait-Until` in `tests/_workspace.ps1`).

## 3. Commit

```powershell
git status --short                # FIRST: read the tree. Decide what is yours before staging anything.
git add -- <path> [<path>...]     # explicit pathspecs, one at a time. Never `add -A`, `add .` or `commit -a`.
git status --short                # confirm: staged = exactly your paths, nothing else
git commit -F <message-file>      # -F, not -m: inline messages with quotes break PS 5.1
```

**`git add -A` is banned here, and this is not caution — it is the literal mechanism of a
real loss.** Two sessions share this checkout, so staging the whole tree stages the other
session's uncommitted work. `f9aaf25` says so in its own message: *"Carries M5.1's lanes.cwd
migration and its Ver.Schema bump to 8, which were in the working tree from another lane."*
Its author reviewed the staged list *after* staging, which is exactly this line's shape:
review that follows the irreversible act is not a control. Review first, then stage what you
recognise.

A file you did not put there is not yours to commit, even when it looks finished. Say so in
your report and leave it in the tree — an orphan needs a decision, not a drive-by.

The message says what changed, **why**, and names any incident or gotcha discovered —
commit messages are this project's history of record (there is no other memory).

## 4. Publish

```powershell
# Resolve the INSTALLED binary the way Ver.BinRoot does, newest build wins.
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
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

## 5. Verify the swap landed

```powershell
$env:DODONA_NO_AUTOSTART = "1"
# Resolve the INSTALLED binary the way Ver.BinRoot does, newest build wins.
$dodona = Join-Path (Get-ChildItem "$env:LOCALAPPDATA\Dodona\bin" -Directory |
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
