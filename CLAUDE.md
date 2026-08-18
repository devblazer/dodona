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

## 2. Completed work gets published

**Finishing a piece of work — or being asked to publish — means running publish.** Not
"mention that it could be published", not leaving it built-but-installed-nowhere. The
operator sees Dodona through the installed app; work that never reaches it does not exist
from where they are sitting.

```powershell
.\src\Dodona\bin\Release\net8.0\dodona.exe publish --project . --all
```

That builds all three executables into a fresh versioned directory, hands off to any
running daemon **without interrupting a live agent mid-turn** (M4), and re-points the
desktop shortcut. Safe to run while the operator is working — that is the whole point of
the hot swap, verified by `tests/m4-acceptance.ps1`. Work in progress gets published too
when the operator wants to trial it; that is what a trial is.

Two things publish cannot do, so say them plainly when they apply:

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
powershell ... tests\compression-acceptance.ps1  # selective compression (§5)
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

- `docs/ORCHESTRATOR-DESIGN.md` — **the authority.** Every `§n` in this codebase (§8
  attention, §11 lifecycle, §17 testing) points here.
- `docs/ORCHESTRATOR-REVIEW.md` — the milestone plan, the measurements, what is carried.
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
