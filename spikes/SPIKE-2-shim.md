# Spike 2 — the shim survives daemon death with zero message loss

Run 2026-08-17. Script: [spike2/spike2.ps1](spike2/spike2.ps1). The shim under test is
**real product code**: [src/DodonaShim](../src/DodonaShim/Program.cs) (~140 lines,
dependency-free).

## Sequence

"Daemon A" spawns the shim (which spawns claude, haiku, stream-json), plants a fact,
fires a slow turn — and is **killed 0.9s later**, mid-turn. The shim and claude must
outlive their own launcher. "Daemon B" — a process the shim has never met — connects to
the pipe, receives everything produced while nobody was listening, and asks for the fact.

## Verdicts

| Check (design doc §13) | Result |
|---|---|
| Shim outlives the daemon that launched it | **PASS** |
| claude outlives the dead daemon | **PASS** |
| Zero message loss across the gap | **PASS** — A saw seqs 0–17, B saw 18–38, contiguous |
| Turn-2 result produced with *no client attached* reaches B | **PASS** |
| Context intact across the whole ordeal (fact recall via B) | **PASS** — `CINNABAR-77` |
| Shim exits cleanly on `##shutdown` | **PASS** |

Delivery is at-least-once with seq tags; this run had **zero duplicate deliveries** —
the delivered-cursor (advance only after a successful pipe write) behaved exactly as
designed.

## What the shim does (the contract M0 builds on)

- Owns the child's stdio; no job objects, no inherited handles — lifetimes are
  independent by construction.
- Buffers every child stdout line in order; on (re)connect, greets with `!hello
  shim=<pid> child=<pid> delivered=<n> buffered=<n>` then replays everything undelivered.
- Client lines forward verbatim to child stdin; `##shutdown` kills the child tree and
  exits.
- Spike protocol is `seq<TAB>line` newline-framed; the real protocol will be
  length-prefixed frames + the store-backed inbox (§12), but the survival and
  zero-loss mechanics are proven here.

## Notes for M0

- One pipe client at a time (`maxNumberOfServerInstances: 1`) is correct — the daemon is
  the only client; a second connector queues on `Connect`.
- Teardown etiquette matters: after `##shutdown` the shim closes first; clients must
  guard their disposal (found the hard way — a `StreamWriter` flush-on-dispose against a
  closed pipe throws).
- PS 5.1 orchestration: run native exes via `Start-Process`, never `&` with stderr
  redirect under `ErrorActionPreference=Stop`.
