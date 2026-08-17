# Debugging Dodona from outside

Written for a Claude Code session told "go look at what happened." Everything below is
readable with **nothing running** — the store is SQLite in WAL mode and the daemon never
holds exclusive locks. You need no tool but a SQLite reader (or the `dodona` CLI if a
daemon is up).

## Where things live

Everything is scoped to a **project root** (the `--root` the daemon was started with):

| Thing | Location |
|---|---|
| The store (all state, all history) | `<root>\.dodona\store.db` |
| Shim identity per lane | `<root>\.dodona\shim-lane<N>.json` → `{shimPid, childPid, pipeName}` |
| Control pipe (daemon alive only) | `dodona-<instance>-ctl`, instance = first 8 hex of SHA256(lowercased canonical root) |
| Lane pipes (shim alive) | `dodona-<instance>-lane<N>` |
| UI pipe (UI alive only) | `dodona-<instance>-ui` |
| Handoff pipe (during a swap only) | `dodona-<instance>-handoff` |
| Published builds | `%LOCALAPPDATA%\Dodona\bin\<stamp>` (or `$env:DODONA_BIN_ROOT`) |
| Agent session files (Claude Code's own) | `$env:CLAUDE_CONFIG_DIR\projects\<cwd-slug>\<session-id>.jsonl` |

## Model and effort (§9)

Every lane is its own `claude -p` process and inherits **nothing** from anyone's
interactive session — before this existed, no effort level was passed at all. Now:

- **Defaults** (in `dodona.json`, falling back to built-ins): `model` `opus`, `effort`
  `high`; the router runs `routerModel` `haiku` / `routerEffort` `low`. An empty string
  for an effort means "don't pass the flag".
- **A policy table** picks per prompt when the daemon starts a lane for typed input:
  first matching rule wins, else the default. The built-in table is three rules
  (mechanical→haiku/low, tests→sonnet/medium, design-tier→opus/max); a project can
  replace it with a `policy` array of `{when, model, effort, why}` regex rules.
- **The operator overrides anywhere**: `@opus @max <text>` at the front of any prompt.
  Tokens are stripped before the agent sees the sentence.
- `dodona policy` prints the table; `dodona policy <text>` prints what that sentence
  would run as, and why. Every choice is an event (`policy_choice`) and is announced in
  the pane it started.
- **Model and effort are fixed at process start.** An override aimed at a lane that is
  already running is answered with exactly that, not silently ignored. `lane-start`,
  `ticket-agent` and `router-start` accept `--model`/`--effort`.

## Workspaces and repositories

A project root is a **workspace**: it anchors identity (one store, one daemon, one grid,
one dispatcher) and holds either itself as a repository or several underneath it.
`dodona repos` lists them. A repository is named by its workspace-relative path, or `.`
when the workspace root is itself the repository — the ordinary single-repo project,
where nothing ever mentions repos at all.

- **A ticket belongs to exactly one repository.** Landing fast-forwards one branch onto
  one main, and two fast-forwards cannot be made atomic, so a change spanning
  repositories is two tickets. `ticket-create` refuses a cross-repo claim set and says so.
- **The repository is inferred from the claims**, which are workspace-relative paths and
  therefore already say (`subtree:engine/src` → `engine`). `--repo <name>` overrides;
  symbol-only claims in a multi-repo workspace must.
- **The merge token is per repository**, so `engine` and `tools` land in parallel while
  two tickets in `engine` still serialize exactly as before.
- **Claims stay workspace-relative everywhere.** The gate resolves an agent's write
  inside its worktree and prefixes the repo name back on before matching; the merge-time
  backstop does the same to git's repo-relative diff output. For `.` the prefix is empty,
  which is why single-repo behaviour is bit-for-bit unchanged.
- **`dodona.json` is per repository**, falling back to the workspace's — so one repo can
  be on `main` with one set of verify steps while another is on `master` with different
  ones.
- **Lanes are workspace-wide** and need no repository at all: an agent can work in a
  folder that has never seen git. Only tickets need one.

## The schema (v6 — `PRAGMA user_version`)

- **`lanes`** — `id, title, state (alive|unreachable|dead), pipe_name, session_id,
  created_ts`. The session_id is the resume handle; the pipe is the reattach handle.
- **`pane_events`** — everything a pane would show, in order: `lane_id, ts, kind, body,
  seq, raw, acked`. `kind ∈ user_input | agent_line | result | system | wire |
  announcement`. `seq` is the shim's delivery sequence (NULL for locally-generated
  rows); `UNIQUE(lane_id, seq)` is what makes shim redelivery exactly-once. `raw` is the
  untouched wire line — the raw truth when `body`'s extraction looks wrong. `acked`
  applies to announcements only (the decision feed persists until acked — acked rows
  grey out, never disappear).
- **`tickets`** — `id, lane_id, title, branch (ticket/<id>), worktree, state
  (open|landed|abandoned), merge_mode (on-approval|auto), approved, repo, created_ts,
  landed_ts`. A branch is a thing that lands; that is what a ticket is. `repo` is the
  workspace-relative repository name, `.` for the workspace root itself.
- **`claims`** — `ticket_id, kind (path|newfile|subtree|symbol), value`. Deleted in the
  same transaction that marks the ticket landed — a lingering claim row for a landed
  ticket is a bug. Values are normalized: forward slashes, lowercase, no leading slash.
- **`merge_token`** — one row **per repository**, keyed by `repo`: `holder_ticket,
  generation, granted_ts, expires_ts, main_sha`. Rows appear on first use. Expired
  holders are reclaimed at the next request; `generation` increments per grant.
  `main_sha` is what that repository's main was when the grant happened.
- **`token_queue`** — FIFO of tickets waiting, `repo`-scoped: the head of `engine`'s
  queue is independent of `tools`'.
- **`routing_decisions`** — every routed input (§4): `ts, input, tier
  (prefix|focus|classifier), target_lane, delivered_lane, confidence, retargeted,
  undone`. `undone` is reserved for the UI's undo keystroke — free labeled data for
  tuning the confidence threshold.
- **`kv`** — small state: `focused_lane`, `dispatcher_lane`.
- **`lanes`** additionally carries `presence` (derived by code from tool_use wire
  events — never a model) and `role ∈ work | router | dispatcher`. The `dispatcher`
  lane (title `DODONA`) holds no agent and takes no grid slot — it is where the system
  speaks in its own voice, and reconcile skips it.
- **`swaps`** — every proposed hot swap (§13/§14): `exe, build, schema_version,
  shim_protocol, blocker, mode (ask|now|when-it-lands|hold), state (pending|armed|held|
  swapped|failed|superseded)`. A `blocker` is why a swap could not be seamless; `armed`
  means the daemon will swap itself the instant that blocker clears. At most one row is
  live at a time — a newer proposal supersedes a parked one.
- **`events`** — the causal chain: `ts, kind, lane_id, detail`. Every daemon action
  writes here. Lane kinds: `daemon_start`, `reconcile_done`, `shim_spawned`,
  `lane_connected`, `lane_unreachable`, `lane_pipe_lost`, `say`, `daemon_stop`.
  Ticket/merge kinds: `ticket_created`, `claim_conflict`, `claim_extended`,
  `ticket_approved`, `token_granted`, `token_queued`, `token_refused_unapproved`,
  `token_released`, `token_expired_reclaimed`, `claim_backstop_refused`, `landed`,
  `land_refused`, `land_inconsistent`, `verify_green`, `verify_red`, `worktree_pruned`,
  `worktree_prune_failed`, `ticket_git_failed`. Routing kinds: `classified` (with
  latency), `routed_retarget`, `classifier_timeout`, `classifier_failed`,
  `route_undone`. Swap kinds: `swap_blocked`, `swap_armed`, `swap_held`, `swap_spawned`,
  `swap_forced`, `swap_refused`, `swap_failed`, `daemon_handoff`, `binary_gc`,
  `binary_gc_skipped`. **If a state change happened with no event row naming why, that
  is a bug — report it as one.**

## The claim gate

Each ticket worktree carries a generated `dodona-gate.ps1` + `.claude/settings.json`
(PreToolUse on Edit|Write|MultiEdit|NotebookEdit). The gate asks the daemon
(`dodona claim-check`) and **fails open** with a line in `<worktree>\.dodona-bypass.log`
when the daemon is unreachable — the merge-time backstop catches what slips. A non-empty
bypass log is worth reading. Gate files are registered in `<root>\.git\info\exclude`
so agents can never commit them.

## Worked queries

What happened in this store, chronologically:
```sql
SELECT ts, kind, lane_id, detail FROM events ORDER BY id;
```

What lane 1's pane would show:
```sql
SELECT ts, kind, body FROM pane_events WHERE lane_id = 1 ORDER BY id;
```

Did anything get lost across a daemon death? (gaps in seq per lane):
```sql
SELECT lane_id, seq FROM pane_events WHERE seq IS NOT NULL ORDER BY lane_id, seq;
-- seqs must be contiguous per lane per shim lifetime
```

Was the daemon restarted, and did reconcile find everyone?
```sql
SELECT ts, kind, detail FROM events WHERE kind IN ('daemon_start','reconcile_done','lane_unreachable') ORDER BY id;
```

## Reading it live

```
dodona status --root <root>
dodona tail <lane> [n] --root <root>
```

If the CLI reports the daemon isn't running, read the store directly — that is the
point of the design. A lane whose shim still runs (check the pids in
`shim-lane<N>.json`) is buffering; whatever it holds arrives when the next daemon
connects, deduped by seq.

## Hot swap (§13/§14)

`dodona version [--json]` tells you what a binary is: `build` (identity),
`schema` (the store shape it expects) and `shimProtocol` (the wire live shims speak).
The last two decide whether a swap can be seamless.

```
dodona publish [--project <dir>] [--all]   # build into a fresh versioned dir, then swap
dodona swap <dodona.exe> [--mode now]      # swap to an existing build
dodona swap-answer now | when-it-lands | hold
dodona swaps                               # every proposal + what is running now
```

The handoff: old daemon spawns the new binary with `--successor`, which connects to the
handoff pipe and says `ready`; the old daemon records `daemon_handoff`, replies `go
<pid>`, and exits; the successor waits for that pid to die, takes the mutex, opens the
store, reconciles, and adopts the shim pipes. **An agent mid-turn never notices** — it is
talking to Anthropic, and its shim buffered whatever arrived.

Diagnosing a swap that misbehaved, in order:

```sql
SELECT * FROM swaps ORDER BY id;                     -- what was proposed, and its verdict
SELECT ts, kind, detail FROM events
 WHERE kind LIKE 'swap%' OR kind IN ('daemon_handoff','daemon_start','daemon_stop')
 ORDER BY id;                                        -- the handoff, step by step
```

- `swap_refused` — the candidate was not a usable binary, or was a schema downgrade.
  Nothing happened; the running daemon is untouched.
- `swap_blocked` then no `daemon_handoff` — it is waiting on an answer. `swaps` shows
  `pending` (asking), `armed` (will fire when the blocker clears) or `held`.
- `swap_failed` — the successor never signalled ready. **The old daemon stays up**; this
  is not an outage. Read the note column for why.
- `daemon_handoff` with no following `daemon_start` — the successor died between `go`
  and startup. No daemon is running, but nothing is lost: the shims are buffering, and
  the next client command starts a daemon (start-on-demand) which drains them.

Start-on-demand means *any* client command revives a dead daemon. Set
`DODONA_NO_AUTOSTART=1` when you want the honest "daemon not running" instead — the
acceptance tests all do, so they own daemon lifetime.

## The UI can testify (§17)

`DodonaUi.exe --root <root> [--pose <name>]` is a dumb view over the store: read-only
WAL reads, all writes via the daemon's control pipe. It answers on its own pipe
(`dodona-<instance>-ui`) — the daemon does not need to be running:

```
dodona ui dump --root <root>                     # panes/badges/presence/feed/toasts as JSON
dodona ui screenshot [--pane WATER] --out <png>  # self-rendered, always 1600x900 full-window
dodona ui pose <full|badges|blocked|feed|empty-slot|tray|overlay|long>  # deterministic fixtures
dodona ui pose live                              # resume store polling
dodona ui overlay <PANE|off> | dodona ui close
```

Most "does the UI show X" questions are text questions — ask `ui dump` and read JSON.
Screenshots are for layout and visual judgment only. `pose long` is the one to reach for
when judging legibility: it is the only fixture with more transcript than a pane has
pixels, and lines longer than a pane is wide.

A pane row's `kind` is rendered as colour — `you>` blue, `agent>` violet, `✓` green, `·`
amber, `wire`/`system` dim mono. In the decision feed every row is an announcement, so
colour there means **lane**: the title is tinted with the lane's slot colour (same one as
its pane chip). Rows from the **dispatcher lane are the system's own voice** and get the
oak's trunk colour with a *round* chip — Dodona is not a seventh lane, and shape says so
where a hue could be mistaken for one. A grey square chip means a work lane with no grid
slot (it is in the tray).

`ui dump` is unaffected by any of this: `lines` stays an array of `prefix + body` strings,
because what the UI testifies to must not change shape because it got prettier.

**The UI hot-swaps too, separately from the daemon.** Windows locks a running image, so
a published UI used to sit on disk while the operator kept looking at the old window —
a swapped daemon behind a stale window is indistinguishable from nothing having happened.
`dodona publish` now swaps the daemon and *then* refreshes every live UI, so a UI change
shows up without closing anything.

The UI handoff is much cheaper than the daemon's: a UI owns no lanes, no agents and no
store writes — its only exclusive resource is its pipe. So the protocol is just:

```
dodona ui update <DodonaUi.exe>     # what publish sends; also usable by hand
```

1. the incumbent opens `dodona-<instance>-uihandoff` and spawns `<exe> --root <root> --successor`
2. the successor builds its window, then says `ready <build>` on that pipe — "ready" means
   the new build actually runs, so a binary that cannot open a window never takes over
3. the incumbent replies and exits, releasing `dodona-<instance>-ui`
4. the successor's pipe loop (`--successor` ⇒ retry for 15s instead of "already running")
   binds it and takes over

Same safety rule as the daemon: **if the successor never answers, the existing window
stays** and the verb reports why. Failure modes: `error: successor never connected`
(new binary died before showing a window — nothing changed), or the successor exiting
with the "Another Dodona UI is already running" box (the incumbent did not exit; its
pipe was never freed). A dump reflects the live view
model, so after mutating the store give the 250ms poller a beat (~500ms) before asking.
`dodona ack <pane_event_id>` clears a feed row's badge; `dodona undo-route <decision_id>`
marks the routing row undone and sends a `[DISPATCHER]` retraction to the lane that
consumed the misroute.
