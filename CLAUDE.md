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
- **`-shl` on `[byte]` stays a byte** and overflows to 0 — cast `[int]` first.
- **Commit messages** with quotes/dashes: `git commit -F <file>`, never inline `-m`.
- **`.Count` on a one-element pipeline** is `$null` — wrap in `@(...)`.
- **`ConvertFrom-Json` emits a JSON ARRAY as ONE pipeline item**, so `... | ConvertFrom-Json
  | Where-Object {...}` filters the array object, not its elements — and `$_.name -eq 'x'`
  on an array returns the matching *elements*, which is truthy, so **every row passes**.
  Land it in a variable first (`$all = ... | ConvertFrom-Json`, then `@($all) | Where…`).
  This turned three acceptance checks into silent no-ops before it was noticed.
- **A `.ps1` that fails to PARSE never reaches `finally`** — everything it started leaks.
- **WPF**: implicit usings omit `System.IO`; with `AcceptsReturn` the TextBox class
  handler eats Enter before instance `KeyDown` (use `PreviewKeyDown`);
  `RenderTargetBitmap` renders in the element's own coordinate space (capture the Window,
  not a margined child).
- **Redirected child stdio defaults to the OEM codepage** — set UTF-8 explicitly or em
  dashes become `ΓÇö`.
- **`Microsoft.Data.Sqlite`**: `INSERT …; SELECT last_insert_rowid();` in one command
  returns nothing without `NextResult()` — use a separate command.

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

## 2. Completed work gets published — and the daemon now enforces this itself

**This rule is no longer only yours to remember.** With `"autoPublish": true` in
`dodona.json` (on, for this repo), the daemon watches the source tree and publishes +
swaps itself when the sources are newer than the image it runs. It waits for the tree to
go quiet first, a failed build changes nothing and is announced loudly, and a 30-minute
dirty tree gets one announcement — because edited-not-built, built-not-published and
published-not-committed each blocked the operator once in a single day, and an instruction
in this file is advisory while a watcher is not (the claim-gate reasoning, §6).

What that leaves for YOU: still build before reporting (rule 1 — the watcher publishing
your broken edit produces a loud failure with your name on it), still commit (the watcher
nags, it does not commit for you), and still run publish yourself when the operator asks
or when you want the swap *now* rather than a debounce later.

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

Blocked swaps **arm themselves** instead of asking (`swap-answer now` forces, `hold`
parks — holding is opt-in, waiting never is), and a schema-migrating swap **backs up the
store and proceeds** (announced with the restore path; only downgrades refuse). §14 of
the design doc records the revision.

## 3. Verify with the suites, not by looking

Eleven model-free suites, all fake agents, all free. Run the ones your change touches; run
all of them before publishing something structural:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\m0-acceptance.ps1   # daemon death
powershell ... tests\m1-acceptance.ps1        # claims, gate, merge token
powershell ... tests\m2-acceptance.ps1        # routing, backstop, presence
powershell ... tests\m3-acceptance.ps1        # the UI as a view over the store
powershell ... tests\m4-acceptance.ps1        # hot swap
powershell ... tests\workspace-acceptance.ps1 # workspaces: identity, repo-exclusivity, multi-repo
powershell ... tests\ui-use-acceptance.ps1    # the UI driven like a person
powershell ... tests\compression-acceptance.ps1  # selective compression (§5)
powershell ... testsrain-acceptance.ps1     # the dispatcher brain and its routing ladder
powershell ... tests\concierge-acceptance.ps1 # the group-scope ladder, the fence, the review-behind
powershell ... tests\publish-acceptance.ps1   # publish targeting: --all spares foreign instances
```

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
```

`ui dump` gained an `input` key (`text`, `lines`, `height`, `sized`, `hint`) — `lines` is
LOGICAL lines, not wrapped rows. Note §0.2's WPF trap is load-bearing here: with
`AcceptsReturn` the TextBox class handler eats Enter before an instance `KeyDown`, so the
handler is `PreviewKeyDown` and `tests/ui-use-acceptance.ps1` now proves Enter still sends.

Poses are deterministic fixtures (`full`, `badges`, `blocked`, `feed`, `collapsed`,
`tray`, `overlay`, `long`, `two`, `twelve`, `bands`, `merged-feed`, `boot-zero`). `--pose` needs a `--root`
or `--shell`; a bare launch (no `--root`, no `--workspace`) is the shell.

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

**A daemon outlives its window.** Closing the app does not stop anything — that is the design
(the window is disposable, agents survive behind their shims). `dodona ps` is how you find out
what is actually running, and `dodona stop-all [--lanes]` is how you stop it. This cost the
operator a surprise: they closed the window, believed the machine was idle, and a daemon plus
seventeen shims had been up for hours.

**Routing waits now.** A distinct task gets its own lane (WORKSPACES-CONCIERGE.md §5.1), so
input is no longer delivered optimistically to the focused lane: a wrong continuation cannot be
undone, and correcting it is exactly what is impossible. Only `LANE: text` and unmistakable
generics ("stop", "no", "try again") are decided in code and stay instant. On double uncertainty
the sentence is HELD and you are asked — nothing is delivered. Without a warm brain, behaviour
is unchanged (focused lane), which is why the suites are unaffected.

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

## 5.1 Delivery is a skill

`/ship` (`.claude/skills/ship/SKILL.md`) is the complete build → suites → commit →
publish → verify-the-swap path. Use it rather than improvising the sequence; when the
delivery process changes, change the skill in the same commit.

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
