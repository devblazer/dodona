# Spike 1 — resume durability + long-lived stream-json sessions

Run 2026-08-17 on this machine (claude 2.1.233, Windows 11, model haiku).
Script: [spike1.ps1](spike1.ps1). Raw wire samples: `spike1-output/wire.jsonl`.

## Verdicts

| # | Assumption (design doc §) | Verdict |
|---|---|---|
| A | One `claude -p --input-format stream-json --output-format stream-json` invocation accepts **multiple user messages** over stdin (§2, §4 — warm utility sessions) | **PASS** — turn 2 in the same invocation recalled the fact from turn 1 |
| B | A **hard kill mid-turn** does not corrupt the session (§13) | **PASS** — no corruption; resume worked cleanly |
| C | `--resume <id>` restores **full context headlessly** after the kill (§13) | **PASS** — resumed one-shot returned the planted fact verbatim |
| D | Resume **continues the same session id** — no fork (§13) | **PASS** — resume turn appended to the same JSONL; one distinct sessionId in the file |

## The finding that wasn't on the list

**A hard kill loses the in-flight user message entirely.** Turn 3 was sent ~1.5s before
the kill; after resume, that prompt appears **nowhere** in the session file. The CLI
persists completed turns, not in-flight ones — "a few seconds of thinking" is not the
only loss; *the instruction itself* is gone.

Design consequence (validates §5/§12 as necessary, not just tidy): delivery is
**store-first** — an instruction becomes a row before it is injected, the hook's cursor
advance is the ack, and a resumed lane gets its **unacked rows redelivered** by the
daemon. Never hand a message to a claude process as its only copy.

## Operational facts for the daemon

- Session files live under `$env:CLAUDE_CONFIG_DIR\projects\<cwd-slug>\<session-id>.jsonl`
  (this machine sets `CLAUDE_CONFIG_DIR=C:\Users\devbl\.claude-work`; default is
  `~\.claude`). Plain JSONL on disk → reboot survival is a file-existence fact.
- The slug is derived from the **cwd of the claude process** — the worktree. Worktree
  removal and `cleanupPeriodDays` both interact with resume; the registry's session-id
  row is only as good as the file behind it.
- Spike-observed wire shape: NDJSON; `session_id` arrives in the early `system` event;
  each turn terminates with a `type:"result"` event carrying the final text. Samples in
  `spike1-output/wire.jsonl` are the seed of the .NET driver's protocol reference
  (schema is unpublished — pin the CLI version, §2).

## What this de-risks

§13's disposable-agent model stands on measured ground: kill → respawn → `--resume`,
same id, full context, no fork. §2/§4's persistent warm utility sessions (router,
compressor) are viable as designed. Spike 2 (the shim) can proceed on these facts.
