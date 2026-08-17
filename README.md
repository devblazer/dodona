# Dodona

A multi-agent orchestration platform: issue tasks by voice at conversational speed, have
several Claude Code agents work them in parallel on one project, and never have two
agents build competing versions of the same thing.

## The documents

The design doc deliberately lives **outside** this repo (it governs a system that
rewrites itself — no branch, merge or reset may touch it):

- `..\MassWorks\ORCHESTRATOR-DESIGN.md` — the design decisions (the authority)
- `..\MassWorks\ORCHESTRATOR-REVIEW.md` — the review: rationale, evidence, measurements

## Status

Week-1 spikes (design doc §16). Each spike de-risks a load-bearing assumption before any
architecture is poured around it:

| Spike | Assumption | Status |
|---|---|---|
| 1 — `spikes/spike1.ps1` | resume durability + long-lived stream-json sessions | **PASS ×4** + one finding — `spikes/SPIKE-1-resume.md` |
| 2 — `spikes/spike2/spike2.ps1` | detached C# shim survives daemon death, zero message loss | **PASS ×6** — `spikes/SPIKE-2-shim.md`; shim is real code: `src/DodonaShim/` |
| 3 — `spikes/spike3/spike3.ps1` | mid-turn `additionalContext` injection behavior | **PASS** with trust framing (~335ms pickup); undeclared channel is refused — `spikes/SPIKE-3-injection.md` |
| 4 | six concurrent sessions: quota burn + warm-turn latency | pending |

Then M0: shim, minimal daemon, one lane, console client, fake agent, `DEBUGGING.md`.
Acceptance test: *kill the daemon mid-agent-turn; the session must not notice.*
