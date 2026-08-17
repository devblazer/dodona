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
| Agent session files (Claude Code's own) | `$env:CLAUDE_CONFIG_DIR\projects\<cwd-slug>\<session-id>.jsonl` |

## The schema (v4 — `PRAGMA user_version`)

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
  (open|landed|abandoned), merge_mode (on-approval|auto), approved, created_ts,
  landed_ts`. A branch is a thing that lands; that is what a ticket is.
- **`claims`** — `ticket_id, kind (path|newfile|subtree|symbol), value`. Deleted in the
  same transaction that marks the ticket landed — a lingering claim row for a landed
  ticket is a bug. Values are normalized: forward slashes, lowercase, no leading slash.
- **`merge_token`** — one row, id 1: `holder_ticket, generation, granted_ts, expires_ts,
  main_sha`. Expired holders are reclaimed at the next request; `generation` increments
  per grant. `main_sha` is what main was when the grant happened.
- **`token_queue`** — FIFO of tickets waiting for the token.
- **`routing_decisions`** — every routed input (§4): `ts, input, tier
  (prefix|focus|classifier), target_lane, delivered_lane, confidence, retargeted,
  undone`. `undone` is reserved for the UI's undo keystroke — free labeled data for
  tuning the confidence threshold.
- **`kv`** — small state: `focused_lane`.
- **`lanes`** additionally carries `presence` (derived by code from tool_use wire
  events — never a model) and `role ∈ work | router | dispatcher`.
- **`events`** — the causal chain: `ts, kind, lane_id, detail`. Every daemon action
  writes here. Lane kinds: `daemon_start`, `reconcile_done`, `shim_spawned`,
  `lane_connected`, `lane_unreachable`, `lane_pipe_lost`, `say`, `daemon_stop`.
  Ticket/merge kinds: `ticket_created`, `claim_conflict`, `claim_extended`,
  `ticket_approved`, `token_granted`, `token_queued`, `token_refused_unapproved`,
  `token_released`, `token_expired_reclaimed`, `claim_backstop_refused`, `landed`,
  `land_refused`, `land_inconsistent`, `verify_green`, `verify_red`, `worktree_pruned`,
  `worktree_prune_failed`, `ticket_git_failed`. Routing kinds: `classified` (with
  latency), `routed_retarget`, `classifier_timeout`, `classifier_failed`,
  `route_undone`. **If a state change happened with no event row naming why, that is a
  bug — report it as one.**

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

## The UI can testify (§17)

`DodonaUi.exe --root <root> [--pose <name>]` is a dumb view over the store: read-only
WAL reads, all writes via the daemon's control pipe. It answers on its own pipe
(`dodona-<instance>-ui`) — the daemon does not need to be running:

```
dodona ui dump --root <root>                     # panes/badges/presence/feed/toasts as JSON
dodona ui screenshot [--pane WATER] --out <png>  # self-rendered, always 1600x900 full-window
dodona ui pose <full|badges|blocked|feed|empty-slot|tray|overlay>   # deterministic fixtures
dodona ui pose live                              # resume store polling
dodona ui overlay <PANE|off> | dodona ui close
```

Most "does the UI show X" questions are text questions — ask `ui dump` and read JSON.
Screenshots are for layout and visual judgment only. A dump reflects the live view
model, so after mutating the store give the 250ms poller a beat (~500ms) before asking.
`dodona ack <pane_event_id>` clears a feed row's badge; `dodona undo-route <decision_id>`
marks the routing row undone and sends a `[DISPATCHER]` retraction to the lane that
consumed the misroute.
