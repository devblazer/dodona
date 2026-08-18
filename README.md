# Dodona

A multi-agent orchestration platform: issue tasks by voice at conversational speed, have
several Claude Code agents work them in parallel on one project, and never have two
agents build competing versions of the same thing.

## Running it

Dodona is an application you launch, not a command you type at a project. Build the
one-folder install, then run `DodonaUi.exe` from it — it asks which project to open,
starts that project's daemon itself, and needs no environment variables:

```powershell
dotnet build Dodona.sln -c Release
# publish all three executables into one folder, and put Dodona on the desktop
.\src\Dodona\bin\Release\net8.0\dodona.exe publish --project . --all --shortcut
```

That installs to `%LOCALAPPDATA%\Dodona\bin\<stamp>\` and creates **Dodona** on the
desktop. Each publish lands in a fresh versioned folder — Windows locks the image of a
running exe, so in-place replacement is impossible — and once the shortcut exists, every
later publish re-points it at the newest build automatically. Publishing into an
overridden `DODONA_BIN_ROOT` (as the acceptance tests do) never touches it.

The picker lists recent projects with live status (`running` / `idle` / `new`), and
**Browse…** adds one — any git repository will do; tickets are branches and lanes are
worktrees, so git is required. Opening a second project opens a second window with its
own daemon: instances share nothing (§14), so that *is* multi-project support. `Ctrl+P`
from a grid opens another.

Direct and forensic entry points, for shortcuts and for debugging sessions:

```powershell
DodonaUi.exe --root C:\src\myproject          # skip the picker (starts the daemon if needed)
DodonaUi.exe --root <copied-store> --attach   # look only: never summon a daemon
DodonaUi.exe --pose full                      # a seeded visual state, no store at all
DodonaUi.exe --shot out.png                   # self-render this window and exit
```

Everything the UI does is also a CLI command against the same daemon — see
[DEBUGGING.md](DEBUGGING.md) for the full verb list, the store schema, and how to read a
store with nothing running.

## The documents

The design doc deliberately lives **outside** this repo (it governs a system that
rewrites itself — no branch, merge or reset may touch it):

- [docs/ORCHESTRATOR-DESIGN.md](docs/ORCHESTRATOR-DESIGN.md) — the design decisions (the authority)
- [docs/ORCHESTRATOR-REVIEW.md](docs/ORCHESTRATOR-REVIEW.md) — the review: rationale, evidence, measurements
- [docs/LANE-LIFECYCLE.md](docs/LANE-LIFECYCLE.md) — lane closing and attention: decided, rejected, still open

These are copies kept in-repo so agents can actually read them — a lane works from
`.dodona\wt\t<N>`, where the old `..\MassWorks\` path resolves to nothing. The masters
still live outside the repo; see [docs/README.md](docs/README.md).

## Status

Week-1 spikes (design doc §16). Each spike de-risks a load-bearing assumption before any
architecture is poured around it:

| Spike | Assumption | Status |
|---|---|---|
| 1 — `spikes/spike1.ps1` | resume durability + long-lived stream-json sessions | **PASS ×4** + one finding — `spikes/SPIKE-1-resume.md` |
| 2 — `spikes/spike2/spike2.ps1` | detached C# shim survives daemon death, zero message loss | **PASS ×6** — `spikes/SPIKE-2-shim.md`; shim is real code: `src/DodonaShim/` |
| 3 — `spikes/spike3/spike3.ps1` | mid-turn `additionalContext` injection behavior | **PASS** with trust framing (~335ms pickup); undeclared channel is refused — `spikes/SPIKE-3-injection.md` |
| 4 | six concurrent sessions: quota burn + warm-turn latency | pending |

## M0 — walking skeleton: **DONE, acceptance green**

`src/Dodona` (daemon + console client, one binary, two roles), `src/DodonaShim`,
`src/DodonaFakeAgent`, store schema v1 (see [DEBUGGING.md](DEBUGGING.md)).

Acceptance test — *kill the daemon mid-agent-turn; the session must not notice* —
passes: [tests/m0-acceptance.ps1](tests/m0-acceptance.ps1). Daemon killed ~1s into a 6s
turn; the result landed exactly once after restart (seq dedupe); the same agent answered
daemon #2. Zero model calls (fake agent, design §17).

Two bugs the test caught, both now load-bearing knowledge: pipe teardown must be
guarded (the closing side flushes into a dead pipe → WerFault park), and a duplex pipe
handle read and written concurrently needs `PipeOptions.Asynchronous` or the pending
read blocks the write forever.

## M1 — claims, gate, fenced merge token: **DONE, acceptance 29/29**

Claim algebra (`path:|new:|subtree:|symbol:`, §6) with plan-time conflict refusal;
PreToolUse claim gate deployed per worktree (fails open, logged); tickets with
branch+worktree lifecycle; FIFO merge-token lease with expiry reclaim; daemon-executed
ff-only land; claims released in the land transaction; post-land verify from
`dodona.json`. Acceptance: [tests/m1-acceptance.ps1](tests/m1-acceptance.ps1) — a
scripted git fixture playing the agents' role, zero model calls.

Bug the test caught: gate files deployed into worktrees were committable by `git add -A`
and landed on main, colliding with every other ticket's gate on rebase — deployment
files now live in `.git/info/exclude`, invisible to git by construction.

## M2 — routing, backstop, real lanes: **core DONE (10/10 + 11/11 live)**

Schema v3. Delivered: merge-time claim backstop (a branch touching outside its claim is
refused the token, `claim-extend` clears it); code-derived presence from tool_use wire
events; tier-0 prefix routing; optimistic focus delivery with the warm haiku classifier
as an async second opinion — visible retarget receipt, `routing_decisions` rows; and the
**first real claude lanes**: `dodona ticket-agent` spawns claude in a gated ticket
worktree with the `[DISPATCHER]`-channel system prompt (spike 3) and acceptEdits.

Live smoke ([tests/m2-live.ps1](tests/m2-live.ps1), ~4 haiku turns): a real agent edited
its claimed file (10.6s turn incl. cold start), was **denied by the gate** on an
out-of-claim write and stopped and said so; the router retargeted a misrouted input with
a receipt in the wrong pane (first classify 5.4s — cold session; steady-state is spike-4
data).

**Selective compression (§5) now landed** (schema v7): a pane shows the short readable
form of a turn — mid-turn narration never reaches the grid, turn-finals are always kept
and always shortened by a warm 2–3 session Haiku pool answering in a fixed schema, and
already-short results skip the model entirely. `compressed` is a second column, so `body`
stays the agent's words and the overlay stays the raw truth. Every failure leaves the
operator reading the full text. See [DEBUGGING.md](DEBUGGING.md).

Deliberately carried to M3 (with the UI they serve): dispatcher session,
retraction-on-consumed-retarget, settings-merge for repos with their own tracked
`.claude/`. Spike 4 (quota calibration) still parked, by choice.
