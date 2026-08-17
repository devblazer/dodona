# Working in Dodona

Dodona orchestrates Claude Code agents. You are probably one of them, working in a lane.
These are the house rules — short, and every one of them exists because breaking it cost
real time.

## 1. Work is not done until it is built

**Never report a change as complete without compiling it.**

```powershell
dotnet build Dodona.sln -c Release
```

An edit that has not been built is a claim, not a change. This has already been the single
most expensive mistake in this project's history — twice:

- A lane rewrote the dispatcher input box, was denied permission to build, and reported
  the change as in. The operator restarted the app and saw nothing, because nothing had
  ever been compiled. Worse, the code contained a real bug (WPF's TextBox class handler
  eats Enter before an instance `KeyDown` runs) that one build-and-test would have caught.
- Twenty-eight minutes of UI work sat in the working tree unbuilt and unpublished while
  the operator restarted the app repeatedly wondering why it looked the same.

If you cannot build — permission denied, a lock, anything — **say so as the headline of
your reply**, not as a footnote. An unbuilt change is not a deliverable.

## 2. Working work gets published and hot-swapped

**When a change builds and its tests pass, publish it.** Do not leave the operator on a
stale binary, and do not make them run a build to see their own feature.

```powershell
.\src\Dodona\bin\Release\net8.0\dodona.exe publish --project . --all
```

That one command builds all three executables into a fresh versioned directory, hands off
to any running daemon **without interrupting a live agent mid-turn** (M4), and re-points
the desktop shortcut at the new build. It is safe to run while the operator is working —
that is the entire point of the hot swap, and it is verified by
`tests/m4-acceptance.ps1`.

Two things it cannot do, so say them plainly when they apply:

- **A running UI window does not hot-swap.** The daemon and lanes survive; the window is
  the disposable half. Tell the operator to relaunch it from the desktop icon.
- **Publishing does not commit.** Commit the work too, or it will be published and then
  lost on the next checkout.

## 3. Verify with the suites, not by looking

Seven model-free suites, all fake agents, all free. Run the ones your change touches; run
all of them before publishing something structural:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m0-acceptance.ps1   # daemon death
powershell ... tests\m1-acceptance.ps1        # claims, gate, merge token
powershell ... tests\m2-acceptance.ps1        # routing, backstop, presence
powershell ... tests\m3-acceptance.ps1        # the UI as a view over the store
powershell ... tests\m4-acceptance.ps1        # hot swap
powershell ... tests\workspace-acceptance.ps1 # multi-repo workspaces
powershell ... tests\ui-use-acceptance.ps1    # the UI driven like a person
```

`ui-use` is the one that matters most for UI work: dumps and screenshots prove the UI
*reports* correctly while the first thing a person tries is still a dead end. If you add
an interactive affordance, add a check there — not only a dump assertion.

For visual work, use the capture loop rather than describing pixels:

```powershell
.\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe --root <project> --pose long --shot out.png
```

Poses are deterministic fixtures (`full`, `badges`, `blocked`, `feed`, `empty-slot`,
`tray`, `overlay`, `long`). `--pose` needs a `--root`; without one you get the picker.

## 4. Never kill processes by name

```powershell
Get-Process DodonaShim | Stop-Process -Force    # NEVER
```

This murdered the operator's live session — their shim, their agent, and their open
window — in the middle of a trial. Machine-wide kills do not know which instance is a
test and which is the human's work. Resolve pids from the specific project root's
`.dodona/shim-lane*.json` instead. Tests collide with nothing (§17), *including the
instance the operator is using right now*.

## 5. Dodona's own state is never repo content

`.dodona/` holds the live store, its WAL twins, and the worktrees. It is git-ignored, and
it must stay that way — a `git add -A` once committed a live SQLite database into this
repo. Deployed gate files live in `.git/info/exclude` for the same reason.

## 6. Where things are written down

- `DEBUGGING.md` — the schema, every event kind, the pipes, and how to read a store with
  nothing running. Start here when something looks wrong.
- `docs/LANE-LIFECYCLE.md` — decisions already taken and **ideas already rejected** about
  closing lanes and the attention model. Read before proposing either.
- `..\MassWorks\ORCHESTRATOR-DESIGN.md` — the authority. Section numbers (§8, §11, §17)
  throughout this codebase point at it.
- `..\MassWorks\ORCHESTRATOR-REVIEW.md` — the milestone plan and what is carried.

Comments in this codebase explain *why*, and often name the incident that caused the
line. Keep that habit: the next person to read it is debugging at speed.
