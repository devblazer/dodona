# Dictation recordings — the operator's voice, and the only way to score the engine

Six recordings of real operator speech, made 2026-08-20 on the Logitech G733 headset
(48 kHz mono m4a, no noise processing). They exist because **recognition quality is the one
property no suite can check**: it needs speech, and an unattended session has none. That is why
`docs/VOICE-ENGINE-PLAN.md` §7 says *"what no suite can verify is whether it hears"* and why no
word-error-rate number was reported until these existed.

**Committed on purpose, audio and all.** A recording without its script is unscoreable, so the
ground truth below is the load-bearing half of this directory — and keeping both in the repo is
what turns "does it hear?" from a ceremony the operator has to perform into a **repeatable
measurement**. The next engine change is scored against the same speech instead of a fresh
recording session, which is the only way a regression is detectable at all.

## Running them

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\transcribe.ps1 -Wav tests\assets\recordings\vocab.m4a
```

It drives the **shipped** path — same resampler, same 20 ms frames, same socket, same
`OnHeard` splice — via `DODONA_STT_WAV`, so what it measures is what the operator gets. It
opens a real socket on the operator's credential and no microphone at all.

`DODONA_STT_NO_KEYTERMS=1` re-runs the keyterm A/B. `DODONA_STT_TRACE=<file>` logs every frame,
which is how the endpoint laziness in D-E19 was found.

## Ground truth

**`vocab.m4a`** — 41 s, eight sentences with ~1.5 s pauses. The technical vocabulary, and the
homophone traps `VOICE-INPUT-PLAN.md` §6 predicted.

1. Publish from the worktree and run the suites.
2. The ff-only merge failed, so the backstop refused the diff.
3. Collapse lane three and respawn the router.
4. The daemon outlived its window, but the shim still holds a thirty minute lease.
5. Prove the check red before you trust the gate.
6. Read the WAL with SQLite from PowerShell.
7. The concierge is blocked on you and the compressor is idle.
8. Stamp the epoch at the start of the utterance, not at delivery.

**`punct-alone.m4a`** — each word alone, ~2 s apart: *comma, full stop, period, question mark,
new line, new paragraph*. Retired as a feature (D-E24); kept because it is the evidence.

**`punct-inline.m4a`** — the same words inside phrases, which must stay literal (D-V9's
protective half, and it holds):

1. I need a comma separated list of lanes.
2. The grace period is thirty minutes.
3. Put a new line between the two blocks.

**`inert.m4a`** — *enter, send, submit, go* each alone, then "Press enter to send the message."
**The operator's hard constraint.** All five must appear as ordinary text and nothing may be
submitted.

**`long.m4a`** — one unbroken breath, 3 s trailing silence:

> When the socket dies in the middle of an utterance, the held interim has to be flushed as
> final, because losing the tail silently is worse than a visible reconnect.

**`abrumpt.m4a`** — recording stops on the last syllable, no trailing silence, deliberately:

> The gate passed on ten assertions and only those.

*(The filename's typo is the operator's and is left alone: renaming a committed fixture breaks
every reference to it for the sake of a letter.)*

## The baseline, so a regression is visible

Measured 2026-08-20 at `c41c96e`, Deepgram Nova-3 via Anthropic's endpoint, with
`SpeechStream.Vocabulary` applied:

| | result |
|---|---|
| technical words | **17 of 19 correct** |
| still wrong | `WAL → "wall"`, `diff → "death"` |
| ordinary prose | essentially verbatim; `long.m4a` had one word wrong ("tail" → "trail") |
| `inert.m4a` | all five landed as text, nothing submitted |
| keyterms on vs off | **byte-for-byte identical** (D-E18/D-E21/D-E22 — the mechanism is inert) |

**The two remaining errors are deliberate, not outstanding work.** `wall` and `death` are
plausible English, and D-E23's bar is that a repair may only fire where the mistaken form is
*not* something the operator might have said — otherwise dictation silently corrupts real
sentences, which D-V9 established is worse than a visibly wrong word.

If a future run scores below 17/19, something regressed. If it scores above it without a change
to `SpeechStream.Vocabulary`, the endpoint changed underneath us — which is D-E1's accepted term,
not a defect.
