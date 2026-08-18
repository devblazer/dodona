---
name: ship
description: Verify, commit, and publish Dodona work — the complete delivery path. Use whenever a change is finished, the operator asks to ship/publish/commit, or before reporting any work as done. Runs the acceptance suites, commits with an incident-honest message, publishes (hot-swapping live daemons and UI), and verifies the swap actually landed.
---

# Shipping Dodona work

The full delivery path. Every step exists because skipping it once cost the operator real
time — the incident is named where that matters. Do the steps in order; do not report
"done" from any earlier point.

## 1. Build

```powershell
dotnet build Dodona.sln -c Release
```

If it fails, that is the **headline** of your report, not a footnote. An edit that has
not compiled is a claim, not a change.

If a running process locks a binary, find the holder before killing anything, and kill by
**pid / exe path / instance id — never by process name** (a name-based kill once murdered
the operator's live session mid-trial):

```powershell
Get-CimInstance Win32_Process -Filter "Name='dodona.exe'" | Select ProcessId, CommandLine
```

## 2. Run the acceptance suites

All model-free (fake agents). Run the ones your change touches; run all eleven before
shipping anything structural:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m0-acceptance.ps1          # daemon death is a non-event
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m1-acceptance.ps1          # claims, gate, merge token
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m2-acceptance.ps1          # routing, backstop, presence
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m3-acceptance.ps1          # UI as a view; land/dormant/wake
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m4-acceptance.ps1          # hot swap
powershell -NoProfile -ExecutionPolicy Bypass -File tests\workspace-acceptance.ps1   # workspace identity + repo-exclusivity + multi-repo
powershell -NoProfile -ExecutionPolicy Bypass -File tests\ui-use-acceptance.ps1      # UI driven like a person
powershell -NoProfile -ExecutionPolicy Bypass -File tests\compression-acceptance.ps1 # selective compression
powershell -NoProfile -ExecutionPolicy Bypass -File testsrain-acceptance.ps1       # the dispatcher brain + routing ladder
powershell -NoProfile -ExecutionPolicy Bypass -File tests\concierge-acceptance.ps1   # group-scope ladder, fence, review-behind
powershell -NoProfile -ExecutionPolicy Bypass -File tests\publish-acceptance.ps1     # publish targeting; --all spares foreign instances
```

Exit code 0 each, or fix before proceeding. UI affordances need a check in `ui-use`
(driven via UI Automation), not only a `ui dump` assertion — dumps prove the UI *reports*
correctly while the first thing a person tries can still be a dead end.

If a fresh failure is in the **test**, fix the test and say so; known test traps are in
CLAUDE.md ("Windows & PS 5.1 traps").

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
.\src\Dodona\bin\Release\net8.0\dodona.exe publish --project . --all
```

This builds into a fresh versioned dir, hot-swaps every running daemon without
interrupting agents mid-turn (M4), updates any live UI, and re-points the desktop
shortcut. The drift watcher (`autoPublish` in dodona.json) would eventually do this on
its own; shipping means *now*, not after a debounce.

## 5. Verify the swap landed

```powershell
$env:DODONA_NO_AUTOSTART = "1"
.\src\Dodona\bin\Release\net8.0\dodona.exe status --root .
$env:DODONA_NO_AUTOSTART = $null
```

The reported `build=` must be the one just published. Only now is the work "done".

## Blocked?

A parked swap announces its three answers (`swap-answer now | when-it-lands | hold`).
A failed publish leaves the old daemon running — nothing is lost; say what failed.
