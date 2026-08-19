# Voice into the dispatcher box — a proposal

Status: **proposal, nothing built.** Written 2026-08-20 on the operator's ask: *"add text to
speech to the app. Toggle listening on and off. Send will still need an enter."*

**A note on the name, because it changes what gets built.** "Text to speech" is the machine
talking; what the rest of the sentence describes — *listening*, a toggle, Enter still sending —
is the machine **hearing**: speech-to-text, dictation into the box that already exists. This
proposal builds that. Actual TTS (Dodona reading a pane aloud) is a different feature with a
different governing rule, and §9 says what it would take and why it is not folded in here.

Read `ORCHESTRATOR-DESIGN.md` §8 (attention) and CLAUDE.md §3/§3.1 first — this adds an
affordance a person touches, which is the exact category with a house rule attached: **if you
add something a person can click, add the verb in the same commit.**

## 1. What the box is today (read from the code, not remembered)

The dispatcher box is one `TextBox` at [MainWindow.xaml:394](../src/DodonaUi/MainWindow.xaml#L394)
— `AcceptsReturn="True"`, wrapping, auto-growing to 200px, draggable past that by the grip above
it. Everything that reaches it goes through four methods:

| method | what it is | who calls it |
|---|---|---|
| [`Input_PreviewKeyDown`:559](../src/DodonaUi/MainWindow.xaml.cs#L559) | the keyboard | a real keystroke only |
| [`InputKey(shift)`:572](../src/DodonaUi/MainWindow.xaml.cs#L572) | what Enter *means* | the keystroke, and `ui key` |
| [`ComposeInput(text)`:588](../src/DodonaUi/MainWindow.xaml.cs#L588) | types characters, **never sends** | `ui compose` |
| [`SubmitInput()`:674](../src/DodonaUi/MainWindow.xaml.cs#L674) | **the only path to the daemon** | `InputKey(false)`, `ui type` |

That table is the whole safety argument for this feature, and it is a happy accident of the
multiline work: **the codebase already separates "put text in the box" from "send it".**
Dictation needs the first and must never be able to reach the second. It is not a rule to
remember — it is a method it does not call.

Two more facts the design leans on:

- `ui dump` already testifies about the box ([:290](../src/DodonaUi/MainWindow.xaml.cs#L290)):
  `text`, `lines`, `height`, `fit`, `sized`, `remembered`, `hint`. Adding a `listen` key is the
  established way to make a new state checkable headlessly.
- The window remembers exactly one thing, in `ui.json`
  ([UiSettings.cs](../src/DodonaUi/UiSettings.cs)) — a file and not the store, because the shell
  spans workspaces and boot-to-zero has no store to read. A second preference belongs there, and
  the file's own rule ("a preference, never data: every read failure is a silent default") applies
  unchanged.

## 2. The ask, restated as invariants

Three from the operator, verbatim in force:

1. **A toggle.** Listening is a state you turn on and leave on, not a button you hold.
2. **Speech becomes text in the box.** It is composed, not sent.
3. **Enter still sends**, and nothing else does.

Four the house rules add, non-negotiable and each one already paid for:

4. **No modal, ever** (D-L4). A microphone-permission dialog would be permanently untestable —
   the same reason `PickerWindow` and `StartLaneWindow` have no coverage at all. A test window is
   forbidden from producing a modal, so a modal is a blind spot by construction.
5. **A verb for every affordance** (§3.1). The five lane actions shipped a defect that went two
   builds unseen because nothing headless could reach them. A mic button with no verb would be
   the third instance of that rule.
6. **The suites stay model-free and device-free** (§0.1: quota is the scarce resource; §4: never
   touch what the operator is using). No suite may open a real microphone — a check that grabs
   the mic while the operator is in a call is CLAUDE.md §4's incident in a new costume.
7. **Never stuck, never quietly stale** (§0.1). A toggle that reads "listening" while the engine
   is dead is precisely the silent degrade that cost two days on the routing ladder.

## 3. The shape: a pure half and a device half

The same split `Ask.cs` and `PaneProgress.cs` already use, and for the same reason — the half
worth testing must not need a device.

**`src/Dodona/Dictation.cs` — pure, linked into `DodonaUi.csproj`.** No mic, no WPF, no store:

- the state machine: `Off → Starting → Listening → (Error)`, and every legal transition
- `Splice(text, caret, selection, heard)` → the exact `(insert, newCaret)` a recognised phrase
  produces, including spacing and sentence capitalisation
- the spoken-punctuation table, and the **inert word list** (§4)
- `ShouldDrop(resultEpoch, submitEpoch)` — the submit race, §4
- `Describe(state, reason)` → the one sentence the indicator and `ui dump` both show, so the
  screen and the dump cannot disagree about what is happening

All of it lands in `dev test unit`: ~1 second, no daemon, no window, no microphone. That is where
the bulk of the checks go, and it is the reason this feature is cheap to verify at all.

**`src/DodonaUi/Recognizer.cs` — the device half, behind a seam.**

```csharp
interface IRecognizer : IDisposable
{
    event Action<Heard> Heard;          // partial or final, with an epoch stamp
    event Action<string> Failed;        // one reason string, never an exception to the caller
    void Start();                       // never throws; failure arrives as Failed
    void Stop();
}
```

Two implementations, and **one landing site**: `MainWindow.OnHeard(Heard)`. The real engine and
the fake both raise the same event into the same method, which is the `ui type` reasoning applied
one layer down — a fake that fed a parallel path would prove nothing about the real one, exactly
as `DodonaFakeAgent` stands in for `claude` without inventing a second lane runtime.

`OnHeard` is ~15 lines and calls `ComposeInput`. It does not call `SubmitInput`, and a unit check
plus a `ui-use` check both say so.

## 4. The rules, all pure, all unit-testable

**Splice at the caret, not at the end.** The operator may type and speak in the same sentence;
dictation is another way of typing, so it lands where typing lands and replaces a selection the
way a typed character does — which is what `ComposeInput` already implements.

**Spacing and capitalisation.** A leading space unless the box is empty or the caret follows an
open bracket or whitespace; capitalise after `.`, `?`, `!` or at the start. Small, mechanical,
and wrong in a way that is instantly visible and instantly fixable — a string function, not a
model call.

**Spoken punctuation, and the words that must do nothing.**

| said | inserted |
|---|---|
| "comma" / "full stop" / "period" / "question mark" | `,` `.` `.` `?` |
| "new line" / "next line" | a newline — through `InputKey(shift: true)`, the same method Shift+Enter uses |
| "new paragraph" | two newlines, same path |
| **"enter" / "send" / "submit" / "go"** | **the literal words, and nothing else happens** |

That last row is the operator's constraint turned into a table. It is not a comment asking
future code to behave: `OnHeard` has no reference to `SubmitInput`, and the words are ordinary
text. **This is the check most worth proving red** — delete the guard, say "enter", and the
message must not go.

**Partials never enter `InputBox.Text`.** A live recogniser emits unsettled hypotheses that
rewrite themselves. If those land in the box, `ui dump`'s `text` becomes non-deterministic and
every existing input check goes intermittent. Partials render as grey ghost text beside the hint
(`InputHint`'s slot, which is already an overlay on the box) and appear in `dump.listen.partial`.
Only a **final** result is spliced.

**The submit race, which is real and easy to miss.** Speech recognition is asynchronous: the
operator can finish a sentence, press Enter, and *then* the recogniser delivers the tail of what
they said — into a box that has just been cleared, as the opening of the next message. So every
result carries the submit epoch it was recognised under, `SubmitInput` bumps the epoch, and a
final result from a stale epoch is **dropped and logged**, never spliced. Pure function, unit
check, and the one bug in this feature a person would find baffling in the wild.

## 5. The affordance and its verbs

**On screen**: a mic glyph in the grip strip above the box — the strip exists, is hit-testable,
and is already where the box's controls live. Three states, and the third is the point:

- **off** — outline glyph, no colour. The hint reads as it does today.
- **listening** — filled glyph, lane-accent colour, and the hint gains "· listening".
- **error** — filled glyph in the blocked colour, and the hint carries the reason in words
  ("no microphone", "speech is switched off in Windows settings"). **A toggle that is on and
  deaf must never look like a toggle that is on.**

**Verbs, in the same commit** (§3.1), each landing in the method the click lands in:

```powershell
dodona ui listen <on|off|toggle>     # the mic button, focus-free — same method Mic_Click calls
dodona ui heard "<text>" [--partial] # a recognition result, through the real OnHeard splice
```

`ui heard` is the fake recogniser's mouth, and it is deliberately not gated behind a test flag:
it lands in exactly the code a real utterance lands in, so a check drives the affordance instead
of a rehearsal of it. `ui dump` gains one key:

```json
"listen": { "state": "listening", "engine": "sapi", "device": "Headset (Realtek)",
            "partial": "run the suites for", "error": null, "remembered": true }
```

Plus a `listening` pose so a screenshot check can see the indicator, and two lines in
`Program.cs`'s `ui` help block ([:1225](../src/Dodona/Program.cs#L1225)).

**Persistence: the toggle is remembered**, in `ui.json` beside `inputHeight`. The counter-argument
is real and worth stating — an app that arms a microphone at launch without asking that time is a
surprise, and surprises about microphones are the expensive kind. It loses to two things: a toggle
that resets itself is a button, which is not what was asked for; and **publish hot-swaps this
window** (§2), so an unremembered toggle would silently go deaf mid-sentence on every swap, which
is the "quietly outdated" failure the standing directive names. The escape hatch is hard and
machine-level: `DODONA_UI_MIC=off` refuses to construct a real recogniser at all, and the suites
set it.

## 6. Which engine — and the one that must not be chosen

| option | offline | download | cost | quality on *this* vocabulary |
|---|---|---|---|---|
| **`System.Speech`** (SAPI, `System.Speech` 8.0.0, in-box engine) | yes | none | free | poor-to-fair; a 2000s engine that will hear "work tree" as "work three" |
| **`Windows.Media.SpeechRecognition`** (WinRT) | yes, with a language pack | pack | free | better, but needs the TFM raised to `net8.0-windows10.0.19041.0` and is quirky for unpackaged apps |
| **Whisper.net** (whisper.cpp) | yes | ~150 MB model, native libs per RID | free, CPU | good, including technical words |
| cloud STT (Azure, Deepgram, …) | **no** | — | per-minute, plus a key | best |

**Cloud is rejected outright** (D-V5), for three reasons that each stand alone: it needs a key and
a network on a machine whose whole design assumption is local work; §0.1 makes recurring spend the
thing you do not add casually; and it would ship the operator's continuously-open microphone to a
third party, which is not a trade a dictation toggle gets to make on someone's behalf.

**Recommendation: settle the engine in a SPIKE, before either engine is shipped** (revised
2026-08-20, see §6.2). The first version of this section said "ship SAPI behind the seam, then
measure, then maybe swap" — and in the same breath said SAPI is "probably not good enough". That
is a phase planned in the expectation it will fail, which is the workaround-instead-of-fix pattern
CLAUDE.md §0.3 forbids, and it would have spent Phase B's UI work twice.

The measurement does not need a shipped phase. It needs an afternoon:

> **Spike 4 — can a local engine hear Dodona?** `spikes/SPIKE-4-dictation.md`, in the established
> shape: twenty recorded sentences of real operator speech ("run the suites", "publish from the
> worktree", "the ff-only merge failed", "collapse lane three"), transcribed by **SAPI** and by
> **Whisper.net small**, both with and without term biasing, scored on word error rate over the
> *technical* words only — the ones that carry the instruction. Verdict table, same as spikes 1–3.

Two things make that spike decisive rather than academic. First, §6.2: Claude Code ships an
18-word `keyterms` list containing *worktree*, which is direct evidence that raw recognition of
this vocabulary is the failure mode, and that biasing is the fix. SAPI takes a lexicon, Whisper
takes an initial prompt — so **both arms must be measured biased**, or the spike rejects an engine
that was never given the words. Second, it produces the number this proposal is otherwise missing.

**The latency budget, derived rather than asked for** (§0.1: the operator states goals, not metric
specs — "make it feel instant" is the requirement). §6.2 hands over what a shipped product
considers instant: **300 ms of silence ends a phrase, 1000 ms ends an utterance.** So the budget
is *text appears within ~300 ms of you stopping speaking*. SAPI streams and meets it trivially.
Whisper does not stream — it transcribes a chunk — so it must be driven by voice-activity
detection on ~300 ms of trailing silence, and the spike must report **latency alongside accuracy**.
An engine that is accurate and takes two seconds fails the actual requirement.

### 6.1 The honest baseline: Windows already has Win+H

Windows 11's voice typing types into whatever text box has focus, with no code at all. If it is
sufficient, this proposal should not be built. What it does not do:

- it dictates into the **foreground** window, so glancing at a browser mid-thought sends the rest
  of your sentence to the browser — the failure Dodona's whole focus-free verb surface exists to
  avoid;
- it is armed per session, per box, by a keystroke — not a state the app remembers;
- nothing about it is visible to `ui dump`, so no check can ever assert on it.

That is a genuine but modest gap, and it is worth the operator weighing before Phase B is built.
Phase A (§11) is worth building either way, because it is where the rules and the tests live.

### 6.2 What Claude Code's own VS Code extension does (read from the installed bundle)

Worth knowing before choosing, because it is a shipped answer to nearly this problem. Read out
of `anthropic.claude-code-2.1.138-win32-x64` on 2026-08-20, not remembered:

- **Recognition is cloud, streaming**: a WebSocket to
  `wss://api.anthropic.com/api/ws/speech_to_text/voice_stream`, carrying
  `encoding=linear16, sample_rate=16000, channels=1, endpointing_ms=300, utterance_end_ms=1000,
  use_conversation_engine=true, stt_provider=deepgram-nova3`. Deepgram Nova-3 behind an
  Anthropic proxy — the user's existing auth pays for it, and there is no third-party key in
  the bundle.
- **Capture is native, with a fallback ladder**: a per-platform `audio-capture.node` addon,
  falling back to `rec` (sox) or `arecord` located with `where`/`which`, and if neither exists an
  error naming **both** causes in one sentence. The webview does no recognition at all — it sets
  `supportsSpeechRecognition = false` and only receives start/interim/stop events.
- **A hardcoded vocabulary boost**, sent as repeated `keyterms`: *VS Code, IDE, webview,
  IntelliSense, MCP, symlink, grep, regex, localhost, codebase, TypeScript, JSON, OAuth, webhook,
  gRPC, dotfiles, subagent, **worktree***. Somebody hit the same word §6 predicts and fixed it the
  cheap way.
- **Interim text renders inline in the composer**, as a span styled secondary-colour and italic,
  replaced when the phrase finalises.

Three things follow for this proposal:

1. **It does not overturn D-V5, but it sharpens the reason.** Claude Code can go cloud because it
   *already holds an authenticated channel to its own API* and the user's subscription covers the
   traffic. Dodona holds no such channel: cloud STT here means a new vendor key, new recurring
   spend, and a network dependency on a system whose design assumption is local work. The
   rejection stands; the honest wording is "no existing authenticated pipe", not "cloud is wrong".
2. **The keyterms trick belongs in §6's measurement.** Word error rate must be measured *with* a
   Dodona term list — *lane, worktree, daemon, shim, concierge, ff-only, backstop* — not raw.
   Whisper.net biases through its initial prompt and SAPI through a lexicon, so both can take it.
   A measurement without it would reject an engine that was never given the words.
3. **It is a real argument against D-V6**, and the counter-argument is a WPF fact rather than a
   principle: a `TextBox` cannot style a run, so inline italic interims mean a `RichTextBox` or a
   caret-aligned overlay, and either one changes what `ui dump`'s `input.text` even means. The
   middle path is to keep `input.text` as *committed* text only and carry the unsettled tail in
   `listen.partial` — which is D-V6 as written — while rendering that tail inline rather than
   beside the hint. Whether the render is worth a `RichTextBox` is the one open UI question here.

## 7. How it fails, and how it refuses to get stuck

Every one of these is the standing directive applied literally — name the thing that un-sticks it,
and it is never a person:

- **No microphone, or the Bluetooth headset walks out of range mid-turn.** `Failed("no
  microphone")` → state `error`, indicator loud, box still types normally. The recogniser retries
  on device arrival; it does not sit in `Starting` forever, because `Starting` has a deadline and
  the deadline lands in `error` with a reason.
- **Speech is switched off in Windows privacy settings.** One announcement naming the setting, in
  the hint line and the feed — **not a dialog** (§2.4), and not a link to a control panel that may
  not exist on that build.
- **The engine throws or hangs.** It is behind the seam and on its own thread; `OnHeard` is the
  only thing that touches the UI thread. A dead recogniser can make the box deaf. It must never be
  able to make the box unusable — the box is the one thing you would use to report the problem,
  which is the same argument `UiSettings` already makes about a corrupt `ui.json`.
- **A publish hot-swap arrives mid-utterance.** The successor reads `ui.json`, re-arms, and the
  in-flight partial is discarded (it was never in `Text`). If the successor cannot arm, it says so
  in the indicator rather than presenting as off.
- **A test window.** `--test-window` plus `DODONA_UI_MIC=off` (set in `tests/_workspace.ps1`)
  constructs the fake recogniser only. No suite ever opens the operator's microphone, and a suite
  that could would be a §4-class incident waiting for a bad afternoon.

## 8. Verification — and what has to be seen RED

Nothing here is worth anything until `dev prove` has seen it fail. Use the multi-check form —
grouped by suite, one run per suite (§1) — not eleven runs to read eleven lines:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 prove `
  unit:dictation_never_submits unit:spoken_enter_is_inert unit:stale_epoch_result_dropped `
  ui-use:listening_toggle_persists ui-use:heard_text_lands_in_box_unsent
```

| suite | check | the defect that must make it red |
|---|---|---|
| `unit` | `dictation_never_submits` | point `OnHeard` at `SubmitInput` |
| `unit` | `spoken_enter_is_inert` | add "enter" to the punctuation table |
| `unit` | `spoken_new_line_inserts_one` | drop the newline mapping |
| `unit` | `splice_lands_at_caret` | append at end instead |
| `unit` | `stale_epoch_result_dropped` | ignore the epoch |
| `unit` | `error_state_is_not_listening` | let `Describe` report `error` as listening |
| `ui-use` | `heard_text_lands_in_box_unsent` | as above, at the window level: `ui heard "hello"` then dump — `input.text` is `hello` and `feed` gained nothing |
| `ui-use` | `spoken_send_words_do_not_submit` | `ui heard "send"` must leave the feed untouched |
| `ui-use` | `enter_still_sends_after_dictation` | the operator's actual constraint, end to end |
| `ui-use` | `listening_toggle_persists` | close the window, reopen, `dump.listen.state` is still `listening` |
| `ui-use` | `partial_is_not_in_input_text` | splice partials |
| `ui-use` | `no_modal_when_the_mic_fails` | the window must still answer `ui dump` with the mic forced to fail |

`ui-use` is the suite that matters here for the reason its own header gives: dumps prove the UI
*reports* correctly while the first thing a person tries is a dead end. It is also the suite that
went intermittently red at concurrency 5 and is already a 70-second monolith (§1) — these checks
add to that, and splitting `ui-use` remains the unfinished business it was before this proposal.

## 9. Text-to-speech proper — what the title literally asked for

Reading Dodona's output aloud is a real feature and a genuinely different one. Sketched, not
proposed:

- **The rule is §8, not §5.** Audio is the loudest notification a computer has. The existing toast
  rule already says: only when the app lacks focus **and** a lane is blocked on you, and *never*
  for progress. Speech would have to obey that more strictly, not less — an app that narrates
  every `result` row is unusable within a minute.
- **`System.Speech.Synthesis`** is in the same in-box package the dictation half would take, so
  the marginal dependency is zero.
- **What it would actually be worth**: one spoken sentence when a lane goes blocked-on-you while
  you are in another window — "Dodona: lane 3 needs you" — and nothing else. That is a small
  feature with a real payoff, and it does not need any of the machinery above.

It is left out because it shares no code with dictation, obeys a different rule, and folding two
features into one proposal is how the smaller one ships untested.

## 10. Explicitly not in this proposal

- **Voice commands.** "Stop lane three", "focus the router" — a spoken control surface over an
  orchestrator is a different and much riskier thing than dictation, and the operator asked for
  the box, not a command channel. The inert-word list is the boundary, and it is enforced.
- **A wake word.** Always-listening-for-a-trigger is a second recogniser running permanently.
- **Dictating into a lane pane, or per-lane microphones.** One box is the front door
  (WORKSPACES-CONCIERGE §4).
- **Listening while the window is closed.** The window is disposable (§13); a microphone that
  outlives it would be the one piece of Dodona state that is neither in the store nor visible.
- **Transcribing agent output**, or any speech that costs quota.

## 11. Cost, in phases

| phase | what | size | verified by |
|---|---|---|---|
| **A** | `Dictation.cs`, the seam, the fake recogniser, `ui listen` / `ui heard`, `dump.listen`, the `listening` pose, all twelve checks | ~350 lines, most of it pure | `dev test unit` (~1 s) + `dev test ui-use` |
| **Spike 4** | SAPI vs Whisper.net on twenty real sentences, both biased, WER on technical words **and** latency | an afternoon, throwaway | its own verdict table (§6) |
| **B** | **the engine the spike chose**, the mic glyph and its three states, `ui.json` persistence, `DODONA_UI_MIC` | ~150 lines (SAPI) or ~250 + model bootstrap (Whisper) | manual: talk to it |

Phase A and the spike are **independent and can run in either order or at once** — the spike needs
no Dodona code at all, and Phase A needs no microphone to exist. That is the property worth
having twice over: by the time a real engine is wired in, "speech cannot send a message" is
already enforced by code that has been seen to fail, and the engine was chosen on a number.

There is no Phase C. The old plan's third phase was "swap the engine we expect to regret", which
the spike exists to make unnecessary.

## 12. Decisions

- **D-V1 — dictation composes, never submits.** Not a convention: `OnHeard` does not reference
  `SubmitInput`, and three checks say so. The operator's constraint, in code.
- **D-V2 — the toggle is remembered.** A toggle that resets is a button; and publish hot-swaps the
  window, so an unremembered toggle would go silently deaf on every swap. `DODONA_UI_MIC=off` is
  the hard override.
- **D-V3 — no modal for anything, including a mic failure.** D-L4's reasoning unchanged: a modal a
  test window cannot produce is a permanent blind spot.
- **D-V4 — the suites use a fake recogniser and never open a microphone.** Same role
  `DodonaFakeAgent` plays, same landing site as the real one.
- **D-V5 — local engines only; cloud STT rejected.** A new vendor key, new recurring spend, and a
  continuously-open microphone streaming off-machine. Claude Code's own extension chose the
  opposite (§6.2) and the difference is the reason, not the taste: it already holds an
  authenticated channel to its own API, and Dodona holds none.
- **D-V6 — partials never enter `InputBox.Text`.** They would make every existing input check
  non-deterministic. The `listen.partial` field carries them instead. Whether that tail is
  *rendered* inline (the Claude Code treatment, §6.2) or beside the hint is left open — it costs a
  `RichTextBox`, and it does not change what a check reads.
- **D-V7 — text-to-speech is a separate proposal** (§9), governed by §8's attention rule.
- **D-V8 — the engine is chosen by a spike, not by shipping one and regretting it** (§6, revised
  2026-08-20). Both arms measured *with* term biasing, scored on the technical words, and reported
  with latency against a ~300 ms budget derived from §6.2. The seam stays regardless — it exists
  so the suites can run without a microphone, which is a testing requirement, not a hedge about
  engines.

### Decided during the unattended build, 2026-08-20 night

Each of these was a fork hit with the operator asleep. The rule applied was CLAUDE.md §0.1's
*act, announce, allow undo*: take the reversible option, write down why, keep going.

- **D-V9 — spoken punctuation is WHOLE-UTTERANCE, never substituted inside a phrase.** "comma"
  said by itself is a comma; "the grace period" is three words of text. Inline substitution demos
  better — "run the suites full stop" ending in a full stop — and it silently mangles ordinary
  English the moment someone says "a comma separated list". A dictation box that edits your words
  where you did not ask is worse than one that types "period", because the second is visible and
  the first is not. This is the reversible half: word-level substitution can be added later behind
  its own checks and nothing here has to be undone first. `Dictation.Punctuation` carries the
  reasoning.

- **D-V10 — the lint that blocked the gate was a WORKING-COPY artefact, not repo content, so
  there was nothing to commit.** The night's step 0 was "normalize `ORCHESTRATOR-DESIGN.md` to
  CRLF and commit it alone". Measured first, and the premise did not hold: the committed blob is
  **pure LF** (0 CR bytes) and `core.autocrlf=true`, so a fresh checkout of that file is uniformly
  CRLF — verified in the new `voice` worktree, where `dev lint` was **clean on arrival**. The 869
  CRLF / 108 bare LF mix existed only in the shared checkout's working copy, left by some earlier
  patch script, and `git diff` could not see it because git normalises what it is handed.

  So the fix was to renormalize that working copy (`rm` + `git checkout --`, content verified
  byte-identical modulo CR), after which `dev lint` is clean there too. **A commit was impossible,
  not skipped**: there was no diff to record. Worth knowing for next time — this lint reads the
  WORKING COPY on purpose (its own comment says so: a mixed file is what makes the next patch
  script pick the wrong newline), so it can be red in one tree and green in another, and "red on
  main" is not necessarily a property of main.

- **D-V11 — the six unit checks were proved by break-and-revert, because `dev prove` refuses the
  `unit` suite.** Not a shortcut taken; the tool aborts on `dev prove unit:...` and explains why:
  a unit test compiles against the code it tests, so a HEAD without `Dictation` cannot run these
  at all — there is no red to see, only a compile error. Its own prescribed substitute was
  followed once per check: break the function on purpose, `dev build`, `dev test unit`, read the
  failure, revert. All six went red naming their own check, and the red each one printed is
  recorded in the comment above it in `DictationTests.cs`.

  One trap inside the trap, worth the line: the first driver ran `dev test unit` **without**
  building, hit P1.5's stale-build refusal on all six, and reported **VACUOUS** — a fake verdict
  about a run that never happened, which is the believed-a-green-check disease one level up. The
  driver now treats "STALE BUILD" and a missing tally as `DID-NOT-RUN`, distinct from vacuous.
