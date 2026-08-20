# Mid-turn progress in the pane — a three-tier proposal

Status: **BUILT — `b3b791d`, 2026-08-20** ("A pane that went silent for four minutes now says
what the agent is doing"). Written 2026-08-19 as a proposal, on the operator's report that Dodona
"filters out agent mid progress responses a bit too aggressively", with the ask: drop
meaningless nonsense, mega-condense medium-value steps, show high-value steps abbreviated.

**AND IT SPENT A DAY UNTRACKED, STILL SAYING "nothing built" — which is the incident worth more
than the design below.** The code shipped; this file, the only record of *why it is shaped that
way and what was rejected*, sat untracked in the shared checkout. So it was invisible to every
worktree (CLAUDE.md §0.0: a lane sees only what is committed), it made every `publish` stamp the
build `dirty=1`, and anyone who did find it would have read "proposal, nothing built" about
shipped code and concluded the tiers were still to do. CLAUDE.md §0 is exactly this: *a lesson
that lives anywhere else is a lesson the next session re-learns the expensive way.* Committed on
the operator's direction, 2026-08-20, while landing R4.

**Read §8 before proposing anything here** — it is the rejection list, and the one most likely to
be re-proposed is a model pass over mid-turn narration (refused on quota, 20–50× the compressor's
traffic).

**One drift to know, because the names below are not the names in the code.** The shipped
`ProgressTier` (`src/Dodona/PaneProgress.cs`) is `Noise` / `Step` / `Act`, where §2's table says
`nonsense` / `mechanical` / `signal`. Same three tiers, same rules, different words; `Step`
additionally FOLDS consecutive same-verb rows at render time, which §2's "coalesced into one
rolling line" describes but does not name. **The code is the authority for the names**; this
document is the authority for the reasoning. The tier table below has deliberately not been
rewritten to match — editing a design record after the fact to look prescient is how the reason
for a decision gets quietly replaced by the decision.

Read `ORCHESTRATOR-DESIGN.md` §5 (compression at the boundary) and §8 (attention) first —
this revises the first and must not violate the second.

## 1. What the filter actually does today (read from the code, not remembered)

Two paths, and the aggression is entirely in the second one.

**Write** — `LaneRuntime.HandleShimLine` ([LaneRuntime.cs:96](../src/Dodona/LaneRuntime.cs#L96))
turns each wire line into a `pane_events` row: `user_input`, `agent_line` (assistant prose),
`result` (turn-final), `system` (real init only), `error` (permission denied), `wire`
(anything unparsed). One exception, and it matters below: an assistant event carrying **only**
`tool_use` writes **no row at all** — it updates the mutable `lanes.presence` column and
returns.

**Read** — `StoreReader.Tail` ([StoreReader.cs:193](../src/DodonaUi/StoreReader.cs#L193))
selects `kind IN ('user_input','result','announcement','error')` once the store is schema >= 7.
`agent_line` is dropped wholesale. The overlay (`all: true`) filters nothing.

So between "you said X" and "the turn ended", a tile carries **nothing** except the presence
line — one tool name, the latest only, plus the elapsed clock `Poller.Liveness` adds.

Three specific consequences:

- **The filter's granularity is a whole event class.** "Let me check that file" and "the build
  fails: MSB3026, a daemon is holding the output" are the same `kind`, so they are discarded by
  the same rule. There is no mechanism that could keep the second one.
- **Tool activity has no history.** Presence is one mutable string. A glance after four minutes
  cannot say what happened during them — not because it is filtered, because it was never
  written down.
- **The justification does not hold for long turns.** §5 argues narration is safe to drop
  because "an agent ends its turn when it needs you, so anything that needs you IS a result".
  True of a well-behaved short turn. This project's own agents run ten-minute turns, and for
  ten minutes the pane is indistinguishable from a dead one.

The complaint is therefore about the **live** pane, not the transcript. That distinction is
what §4's collapse rule turns into a design.

## 2. The proposal: a value tier, stamped in code, at write time

Replace the binary kind filter with a **tier** on the row:

| tier | name | in the pane |
|---|---|---|
| 0 | nonsense | never — row still exists, overlay still shows it |
| 1 | mechanical | never on its own; **coalesced** into one rolling line per run |
| 2 | signal | shown, abbreviated: first sentence, <=110 chars, prefixed with a chevron |

**Zero model calls.** All of it is string work on text the daemon already has. This is not an
optimisation, it is the constraint: quota is the scarce resource (CLAUDE.md §0.1), and a model
call per narration line would be 20–50x today's compressor traffic, which is one call per
turn-final over 120 characters.

### Why write time and not read time

Read-time classification would let a rule change take effect over all history, and the
per-poll cost is negligible. It is still wrong here, for one decisive reason: **the tier
depends on context the read path cannot see** — whether a tool call followed this text, whether
it restates the immediately preceding `user_input`, how many rows have passed since the last
`result`. `Tail` sees `(kind, body)`. `HandleShimLine` sees the turn.

Two things fall out of it for free: the tier is **queryable**, so a suite asserts on the store
rather than on pixels; and a mis-tier is **cosmetic and reversible**, because `body` is never
touched — the same property that makes compression safe
([Store.cs:220](../src/Dodona/Store.cs#L220)).

## 3. The classifier

Precedence is the whole safety argument, so it is stated first: **test tier 2, then tier 0,
otherwise tier 1.** A line that looks like filler but contains a trouble word is signal. The
classifier can only err toward showing.

**Tier 2 — signal.** Each of these is a *fact in the text*, not a judgment:

- trouble: `fail(ed|ing|ure)`, `error`, `cannot`, `refus`, `denied`, `blocked`, `timeout`,
  `wedged`, `broken`, `wrong`, `unexpected`, `exception`, `MSB\d+`, `CS\d{4}`
- verdict or measurement: `\d+ checks?, \d+ failed`, `PROVEN|VACUOUS|MISSING`, a duration, a
  diff stat, a pid
- finding or decision: `because`, `the cause`, `root cause`, `turns out`, `so the fix`,
  `instead of`, `rather than`, `note that`
- surprise: `actually`, `however`, `does not exist`, `neither`
- **a question addressed to the operator** mid-turn — ends in `?`. Rare, and today it is
  dropped, which is the worst single case in the current behaviour.
- `kind='error'` (permission denied) — already loud, unchanged.

**Tier 0 — nonsense.** Only on a conjunction, never on a prefix alone:

- empty or whitespace
- a filler opener *and* under ~60 chars *and* no tier-2 signal: ok / okay / got it / perfect /
  great / right / sure / alright / now / let me / I'll / let's / looking at / checking /
  reading / first let me
- a restatement of the preceding `user_input` (token-set containment — no model)
- `wire` rows that are pure protocol: thinking deltas, tool-result echoes, stream framing

**Tier 1 — mechanical.** Everything else, plus the one new row type:

- **tool activity becomes a row.** `kind='tool'`, tier 1, body `read: Store.cs`, `bash: dotnet
  build`, written *alongside* the presence update instead of the current write-nothing return.
  This is the missing history from §1.
- procedural narration ("I'll add the column, then wire Tail to it") — first sentence, <=70 chars
- a near-duplicate of the previous tier-1 line in the same run folds into `xN`

**Rendering tier 1 is where "mega condense" lives.** A contiguous run of tier-1 rows since the
last tier->=2 row renders as exactly **one** line:

```
· read 6 files, grep x3, bash: dotnet build, edit: StoreReader.cs
```

## 4. Two rules that stop this becoming a log again

The pane was filtered for a reason. Both guards below are structural, not advice.

- **Per-turn budget.** Between a `user_input` and its `result`, at most **3** tier-2 lines and
  **1** coalesced tier-1 line. Past three, the oldest is replaced by `+N more` — the overlay
  has all of them. §8 forbids scrolling, and a chatty agent must not be able to make a tile
  scroll.
- **Collapse on result.** When the turn-final lands, the turn's mid-progress lines collapse to
  the single coalesced condensed line. A finished turn reads: your input, one summary line, the
  compressed headline — i.e. **today's volume**. The extra lines exist only while the turn is
  live, which is exactly when the operator wants them, and §5's 5–10x cut stays true of the
  transcript.
  *Alternative considered:* keep tier-2 lines forever. Rejected as the default — it multiplies
  history by the number of turns, which is the volume the filter was installed against. It is
  one constant, so it can be flipped if the operator disagrees after using it.

The numbers 3 / 1 / 70 / 110 are **placeholders to be calibrated in §6, not guesses to ship**.

## 5. Where it lands

| file | change |
|---|---|
| `src/Dodona/PaneTier.cs` *(new)* | the classifier as a **pure function**: `Tier(kind, body, prevUserInput, toolFollowed)`. Pure, so it belongs to `dev test unit` — ~1 s, no daemon. |
| `src/Dodona/Store.cs` | schema **11**: `ALTER TABLE pane_events ADD COLUMN tier INTEGER;`. NULL = untiered, so the read path renders such a store exactly as today — the same rule already used for pre-compression stores. |
| `src/Dodona/LaneRuntime.cs` | stamp the tier; write a `tool` row for tool-only assistant events instead of returning with nothing stored. |
| `src/DodonaUi/StoreReader.cs` | select by tier, coalesce tier-1 runs, apply the budget and the collapse rule. `all: true` keeps filtering nothing. |
| `src/DodonaUi/Vm.cs` | prefixes and brushes for the two new row shapes. |
| `src/Dodona/Daemon.cs` (`ui dump`) | pane lines carry their tier, so the suites can assert headlessly. An affordance no verb can reach is where the next defect lives (CLAUDE.md §3.1). |

## 6. Verification, and the measurement that comes first

**Before the UI half is written**: run the classifier offline over the `agent_line` rows of a
real store and report the tier histogram plus the exact lines it would show. Cheap, and §1's
rule is that a measurement not taken is not a measurement. Calibrate the placeholders from
that, then build.

- `dev test unit` — the classifier table: one case per tier-0 class, per tier-1 class, per
  tier-2 signal group, **plus the precedence case** (filler opener containing "failed" -> 2)
  and the restatement case.
- `dev test compression` — new checks for the coalesced line, the budget, and the collapse.
- `dev prove compression:<check> unit:<check> ...` — the grouped form, one run per suite.

**One existing green check is deliberately inverted, and pretending otherwise would be the
dishonest part of this proposal.** `compression-acceptance.ps1`'s
`midturn_narration_is_not_in_the_pane` asserts that `working on:` is absent from the pane —
that is the behaviour being changed. It splits in two: filler stays absent, and a narration
line carrying a trouble word must be **present**. `midturn_narration_is_still_a_row` and
`overlay_keeps_midturn_and_full_text` are unaffected and stay as they are.

## 7. How it fails

- **The keyword lists rot.** Mitigated by shape rather than diligence: one table in one pure
  function with a unit case per entry, and precedence means an unlisted signal degrades to
  tier 1 — condensed and counted, never invisible.
- **A chatty agent trips tier 2 constantly.** That is what the budget of 3 is for.
- **It becomes a log again.** The collapse-on-result rule is the structural answer; if the tile
  still reads as a log after calibration, the budget is the dial, not the tier system.

## 8. Explicitly not in this proposal

- **A model pass over mid-turn narration.** Rejected on quota (§0.1): one call per narration
  line is 20–50x today's traffic, and the coalesced code line already answers "what is it
  doing". If a tier-1 run ever needs summarising, that is a second phase with its own
  measurement.
- **Attention-sized panes.** Already rejected in design §8 and not reopened here.
- **Anything that makes progress badge.** §8: progress never badges. Tiers change what a pane
  *reads like*, never what demands attention.
