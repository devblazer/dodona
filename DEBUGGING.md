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

## The schema (v1 — `PRAGMA user_version`)

- **`lanes`** — `id, title, state (alive|unreachable|dead), pipe_name, session_id,
  created_ts`. The session_id is the resume handle; the pipe is the reattach handle.
- **`pane_events`** — everything a pane would show, in order: `lane_id, ts, kind, body,
  seq, raw`. `kind ∈ user_input | agent_line | result | system | wire`. `seq` is the
  shim's delivery sequence (NULL for locally-generated rows); `UNIQUE(lane_id, seq)` is
  what makes shim redelivery exactly-once. `raw` is the untouched wire line — the raw
  truth when `body`'s extraction looks wrong.
- **`events`** — the causal chain: `ts, kind, lane_id, detail`. Every daemon action
  writes here: `daemon_start`, `reconcile_done`, `shim_spawned`, `lane_connected`,
  `lane_unreachable`, `lane_pipe_lost`, `say`, `daemon_stop`. **If a state change
  happened with no event row naming why, that is a bug — report it as one.**

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
