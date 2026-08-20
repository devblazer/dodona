# Replacing the dictation engine — Deepgram Nova-3 through Anthropic's pipe

Status: **E1–E6 BUILT 2026-08-20. It compiles, it connects, and it has never heard a word** —
because that needs a voice and the build was unattended. Written after Phase A of
`VOICE-INPUT-PLAN.md` shipped with SAPI behind the seam and the operator tried it.

**What is true, stated so nobody has to infer it:**

- SAPI is **deleted** (D-E6). `DeepgramRecognizer` is the only engine, and there is no fallback to
  a lesser one anywhere (D-E11).
- **Spike E1 did not return a clean 101-and-authenticated, and it did not return a 4xx either.**
  The endpoint is live and the *upgrade* returns 101 with no credential at all; auth is refused one
  frame later. The credential this machine should present is **still unresolved** — reading the
  `claude` CLI's own credential store was refused by the permission classifier, correctly, for the
  third time across two sessions. §3 and D-E7 carry the measurement; `SpeechAuth` carries the two
  routes that need no credential discovery.
- **279 checks green** (`unit` 261, `voice` 22 — of which 11 are new), with **no microphone and no
  network**, plus the full gate. Every new check has been seen red; §7 records each red verbatim.
- **Nobody has spoken to it.** Whether it *hears* is unverified and unverifiable without the
  operator. Do not describe dictation as working until they have.

**Read `VOICE-INPUT-PLAN.md` first.** This document replaces exactly one file of it and assumes
everything else stands. It also supersedes that plan's **D-V5** and closes **D-V8**; §9 records
both with reasons.

## 0. Why this exists, stated plainly

SAPI shipped, the operator spoke to it, and it produced **gibberish** — not wrong words, noise.
Their words: *"It gets a bunch of words wrong, but it doesn't completely make up gibberish"*
about the extension, by contrast.

That is SAPI's signature failure and it was predictable: a constrained 2000s-era recogniser with
no acoustic model worth the name is forced to emit *something* from its language model, so it
emits confident nonsense rather than admitting uncertainty. `VOICE-INPUT-PLAN.md` §6 predicted
the direction ("will hear *work tree* as *work three*") and under-predicted the severity.

**The operator's decision, and it is a decision, not a preference** (D-E1): use the engine the
Claude Code VS Code extension uses. Their reasoning, verbatim: *"If the extension can use it, we
can use it. They have the exact same risk exposure, etcetera. And I'm willing to bet you I'm
more willing to take chances than they are."*

So the risk of depending on an undocumented internal endpoint is **accepted, explicitly, by the
person who owns the machine**. Do not re-litigate it. Do record it if it breaks (§9, D-E1).

## 1. What Phase A already gives you — do not rebuild any of it

This is the part worth reading twice, because the temptation will be to start over.

**One file changes: `src/DodonaUi/SapiRecognizer.cs` is replaced.** Everything below already
exists, is committed, and is covered by 268 checks that must stay green:

| already built | where | why you must not touch it |
|---|---|---|
| `IRecognizer` — the seam | `src/DodonaUi/Recognizer.cs` | The new engine implements this and nothing else moves. |
| `FakeRecognizer` | same file | **Every suite uses this.** It is not a stub; it is what lets dictation be tested with no device and now no network. |
| `Recognizers.Create` | same file | The one place the engine is chosen, and where `DODONA_UI_MIC` is honoured. |
| `Dictation` — the pure half | `src/Dodona/Dictation.cs` | State machine, caret splice, punctuation, the submit-race guard, `Describe`. Engine-independent. |
| `MainWindow.OnHeard` | `MainWindow.xaml.cs` | **The single landing site.** The new engine raises into the same method a real utterance, the fake, and `ui heard` all reach. |
| `ui listen` / `ui heard` | `Program.cs`, `HandleVerb` | Focus-free verbs. `ui heard` still drives the real splice, so most checks need no engine at all. |
| `dump.listen` | `Dump()` | `state`, `engine`, `says`, `partial`, `error`, `dropped`, `remembered`. Add nothing; `engine` just starts reporting a new value. |
| the mic glyph, 3 states | `MainWindow.xaml` + `UpdateListenUi` | Off outline / listening blue / error amber, words in the state's colour. |
| `listening` pose | `Poses.cs` | Deterministic fixture for screenshots. |
| toggle persistence | `UiSettings` | `ui.json`, beside `inputHeight`. |
| the `voice` suite | `tests/voice-acceptance.ps1` | 18 checks, ~13 s, **no microphone and no network**. Registered in `SoloSuites`. |

**The safety property is already enforced and must stay enforced** (`VOICE-INPUT-PLAN.md` D-V1):
`Dictation.DictationAct` has no member meaning send, so no engine — however clever, however
cloud-backed — can cause a message to be submitted. `Dictation_never_submits` asserts that by
reflection. If your work makes that check red, you have broken the operator's one hard
constraint, not found an inconvenience.

## 2. The protocol, read out of the installed bundle

**Read from `anthropic.claude-code-2.1.235-win32-x64/extension.js` on 2026-08-20, not
remembered.** Everything in this section is a quoted fact. Where a name is minified it is given
as-is so you can find it again.

**Endpoint** — `wss://api.anthropic.com/api/ws/speech_to_text/voice_stream`

**Query string** (`URLSearchParams`, exactly these):

```
encoding=linear16
sample_rate=16000
channels=1
endpointing_ms=300
utterance_end_ms=1000
language=en                      # or the configured language
use_conversation_engine=true
stt_provider=deepgram-nova3
forward_interims=typed           # ONLY when typed-interims is on; omitted otherwise
```

**Headers**

```
Authorization: Bearer <token>
x-app: vscode
anthropic-client-platform: claude_code_vscode
x-config-keyterms: <comma-joined list>      # only when non-empty
```

**Keyterms go in a HEADER, not as repeated query parameters.** `VOICE-INPUT-PLAN.md` §6.2 says
query parameters and **is wrong** — corrected here. The normaliser (`lmr`) is worth copying
exactly, because its cap bites silently:

- each term: commas → spaces, non-ASCII-printable stripped, whitespace collapsed, trimmed
- duplicates dropped, empties dropped
- accumulate `term.length + 1` per term and **`break` the moment the total would exceed 1024**

That `break` is a **silent truncation**: terms after the cap are dropped with no error anywhere.
So **order matters — most valuable words first** (§5).

**Frames you send**

- audio: raw PCM as **binary** WebSocket frames. `linear16` means signed 16-bit little-endian
  PCM, 16 kHz, mono, **no WAV header** — a header would be transcribed as noise.
- keepalive: `{"type":"KeepAlive"}` on open and then **every 8000 ms** (`imr`)
- shutdown: `{"type":"CloseStream"}` (`amr`)

**Frames you receive** — JSON, switched on `type`:

| `type` | meaning | payload |
|---|---|---|
| `TranscriptInterim` | unsettled hypothesis | `data` |
| `TranscriptText` | also delivered as **not final** | `data` |
| `TranscriptEndpoint` | **promote the last interim to final** | *(none)* |
| `TranscriptError` | recognition failed | `description` |
| `error` | server error | `message` |

**THE SERVER NEVER SENDS A FINAL TRANSCRIPT, AND THIS IS THE MOST IMPORTANT LINE IN THE
DOCUMENT.** It streams interims; `TranscriptEndpoint` means "that last interim is now settled".
The extension holds the latest interim in a variable and re-emits it as final on endpoint, and
flushes it on socket close.

That maps onto Phase A without changing anything:

```
TranscriptInterim / TranscriptText  ->  Heard(text, Final: false, Epoch: utteranceEpoch)
TranscriptEndpoint                  ->  Heard(lastInterim, Final: true,  Epoch: utteranceEpoch)
```

`Dictation.Decide` already routes non-final to `listen.partial` and never into the box (D-V6),
and already drops anything stamped with a stale epoch. **You are wiring a new mouth onto a jaw
that already works.**

**Errors on the upgrade** are classified: the extension regexes
`/^Unexpected server response: (\d+)/` and treats **4xx as FATAL** (stop, do not retry) and
anything else as transient. 4xx is what an auth failure looks like. Copy that distinction — it
is the difference between "say why and stop" and "retry forever", and §0.1's standing directive
cares about both.

## 3. Two unknowns. Each is a spike, and the first one gates everything

### Spike E1 — AUTH. Do this before writing a single line of the recogniser

**This is the whole risk of the plan and it is unresolved.** What is known is in §2: the header
is `Authorization: Bearer <token>` plus two client headers. What is **not** known is where
Dodona gets that token.

**An honest note on why it is unresolved**, so you do not assume it is trivial: the session that
wrote this plan tried to trace the token's origin in `extension.js` and was **blocked by its own
permission classifier**, three times, for extracting credential-handling code. That was the
right call by the tooling and it is not a puzzle to route around. It means the work is yours, in
a session where the operator can approve the reads.

Routes to try, best first:

1. **Whatever the `claude` CLI already holds.** Dodona spawns `claude -p` constantly, so the
   machine is already authenticated for Claude Code. Find where that credential lives and
   whether it is a bearer token this endpoint accepts. This is the route that needs no new
   secret and no new spend.
2. **The extension's own stored credential.** Same auth, already on the machine.
3. **An explicit token in `dodona.json` or an env var**, set once by the operator. Ugly, but it
   works and it is honest about what is happening.

**The verification, and it is fifteen lines:** open the WebSocket with those headers and report
the HTTP status of the upgrade. Nothing else. Do not capture audio, do not touch the UI.

- **101** — go. The rest of the plan is buildable.
- **401 / 403** — that route is closed. **Stop and report**; do not start building capture. §10's
  fallbacks exist for exactly this and the operator gets to choose between them.

Put the probe in an isolated `DODONA_HOME` per the `probe-hygiene` skill, and **do not point it
at the operator's live workspace**.

### Spike E2 — CAPTURE. Independent of E1, so it can run in parallel

**None of the extension's capture code transfers.** It uses a per-platform
`audio-capture.node` addon with a fallback ladder to `rec` (sox) and `arecord`. Dodona is
.NET/WPF. This half is new work.

What is needed: **16 kHz, mono, signed 16-bit PCM**, delivered in small buffers (the extension
streams continuously; ~20–100 ms per frame is sane).

- **`NAudio`** is the obvious choice — `WasapiCapture` or `WaveInEvent`, plus
  `MediaFoundationResampler` or `WdlResamplingSampleProvider`.
- **The resample is not optional.** Almost every real device captures at 44.1 or 48 kHz. Sending
  48 kHz audio to an endpoint told `sample_rate=16000` produces fast, garbled, confident
  nonsense — *indistinguishable from the SAPI failure this whole document exists to fix.* If the
  first end-to-end test sounds like chipmunks or gibberish, **suspect the sample rate before the
  engine.**
- **Verification is a file, not a guess:** capture three seconds, write a real `.wav` (16 kHz
  mono 16-bit), and **play it**. If it sounds wrong, nothing downstream can be diagnosed.

**Trap:** `NAudio` is not in this machine's package cache. The repo restores from its own
`nuget.config` (which does have nuget.org), so `dotnet restore` from inside the repo works —
but a probe project *outside* the repo will fail with `NU1100` and look like the package does
not exist. That cost a round on 2026-08-20 with `System.Speech`.

## 4. The build, in order

| phase | what | gated by | verified by |
|---|---|---|---|
| **E1** | the auth probe, nothing else | — | a `101` |
| **E2** | capture → 16 kHz mono PCM | — | a `.wav` you listened to |
| **E3** | `DeepgramRecognizer : IRecognizer` — socket, keepalive, the five message types, epoch at utterance start | E1 + E2 | `dev build`, then a person talking |
| **E4** | Dodona's keyterms (§5) | E3 | the same person, saying "worktree" |
| **E5** | failure and never-stuck (§6) | E3 | checks, proved red |
| **E6** | the checks (§7) | E5 | `dev prove` |

**Build after every step.** `dev test` does not build (CLAUDE.md §1) — a stale-build false green
is a documented incident in this repo, and the tool now refuses rather than lying, so respect the
refusal instead of working around it.

## 5. Dodona's keyterms — the thing that makes it good

The extension ships 18 terms and one of them is **`worktree`**. Somebody there hit the same word
`VOICE-INPUT-PLAN.md` §6 predicted and fixed it the cheap way. Do the same with **Dodona's**
vocabulary, not VS Code's.

Proposed, **most valuable first** because the 1024-byte cap truncates silently (§2):

```
lane, worktree, daemon, shim, concierge, dispatcher, backstop, ff-only, claim, ticket,
pane, gate, suite, respawn, hot swap, publish, store, workspace, prove, acceptance,
compressor, brain, router, presence, quota, epoch, splice, WAL, SQLite, PowerShell
```

Keep it in **one place** — a `static readonly string[]` beside the recogniser, not scattered —
and note in a comment that order is significance, not taste. A term list nobody can find is a
term list that goes stale.

## 6. How it fails, and how it refuses to get stuck

§0.1's standing directive applies literally: every wait names the thing that un-sticks it, and it
is never a person.

- **No network.** `Failed("no network")`, state `error`, glyph and words amber, **box still types
  normally**. Retry on the next toggle-on, not in a loop.
- **401 mid-session** (token expired). Fatal per §2's classification: say so in words, stop. Do
  not retry a 4xx — that is how you get a hot loop against someone's auth endpoint.
- **The socket dies mid-utterance.** Flush the held interim as final on close (the extension
  does), then report. Losing the tail silently is worse than a visible reconnect.
- **`Starting` must not be sittable-in.** SAPI's `Start()` was synchronous so `Starting` could
  not linger; **a socket connect is not.** This is a genuinely new state to get wrong. Give it a
  deadline that lands in `error` with a reason — `VOICE-INPUT-PLAN.md` §7 asked for this and
  Phase A got away without it.
- **The mic is on and the operator walks away.** Streaming an open microphone indefinitely is
  the one new *cost* this engine introduces. Consider a silence timeout that drops the socket and
  keeps the toggle armed, reconnecting on speech. **Flag it, measure it, do not guess it.**

## 7. Verification — and the honest limit

**The existing 268 checks must stay green, unchanged.** They use `FakeRecognizer`, so they need
no network and no device. If your change makes them need either, you have broken the property
that makes this feature testable at all.

**New checks, each proved red before you believe it** (`dev prove voice:<check> ...`, one run).

**BUILT AND VERIFIED 2026-08-20.** Eleven checks, not six — the protocol half went to `unit`
where it belongs, because a wire contract behind a socket is a wire contract no check can reach.
The red each one printed is recorded here and in a comment above the check itself.

| suite | check | verdict | the red it printed |
|---|---|---|---|
| `voice` | **`mic_off_opens_no_socket`** | **PROVEN by injection** | `dev prove` says VACUOUS *and is right*: HEAD has no socket engine, so "no socket" holds trivially there. Injected the reorder (MicDisabled moved after construction): `FAIL engine=[deepgram] sockets=0`. With a junk `DODONA_STT_TOKEN` added so the connect could run at all: **`FAIL engine=[deepgram] sockets=1`** — a real socket, from inside a test run, which is the whole thing this check exists to forbid. |
| `voice` | `a_dead_network_reads_as_error_not_listening` | **PROVEN by injection** (D-E13: `dev prove` would have opened the operator's mic) | `Settle()` instead of `Fail()` on connect failure: `FAIL state=[listening] engine=[deepgram] says=[listening] error=[]` — on-and-deaf reading as on, exactly. |
| `voice` | `starting_has_a_deadline` | **PROVEN by injection** (same reason) | deleted the `ArmStartingDeadline` call: `FAIL sawStarting=True state=[starting] says=[starting the microphone] error=[]`. It sat in `Starting`. |
| `voice` | `an_interim_never_enters_the_box` | **PROVEN by injection** | `dev prove` says VACUOUS and is right — HEAD already kept partials out. Spliced partials into the box: `FAIL midBox=[publishpublish frompublish from the worktree] occurrences=2`. |
| `unit` | `Endpoint_promotes_the_last_interim` | PROVEN | endpoint returning null instead of promoting: *Assert.NotNull() Failure* — every finished sentence silently discarded. |
| `unit` | `The_live_auth_refusal_frame_yields_words` | PROVEN | deleted the nested-`error.message` branch: *Not found: Invalid authorization  In value: ""* — the empty reason that would have reached the operator. |
| `unit` | `A_fatal_auth_failure_is_classified_fatal` | PROVEN | removed the `closeCode == 1008` arm: *Expected Fatal, Actual Transient*. |
| `unit` | `Keyterms_stop_at_the_byte_budget_and_keep_the_earliest` | PROVEN | `break` changed to `continue`: *header was 1739 bytes* — the oversized header the server rejects outright. |
| `unit` | `Ordinary_network_death_stays_transient` | (same break as the fatal one) | the other half: a dropped wifi connection must not permanently disarm dictation. |
| `unit` | `TranscriptText_is_not_final`, `A_blank_interim_does_not_erase_the_held_phrase`, `A_dead_socket_flushes_the_held_interim`, `The_five_message_types_parse`, `Keyterms_are_normalised_…`, `Dodonas_own_keyterms_all_survive_the_budget` | covered by the breaks above | |

**`a_fatal_auth_failure_stops_retrying` was NOT written as an acceptance check, deliberately.**
The property is structural rather than behavioural: `DeepgramRecognizer` contains no retry loop at
all — `Fail()` ends the read pump and returns, and re-arming happens only on a deliberate
toggle-on. A check asserting "it did not retry" would be asserting the absence of code that does
not exist, which is unfailable for the one reason it would exist. The *classification* it depends
on is `unit:A_fatal_auth_failure_is_classified_fatal`, which is proved red. Said plainly rather
than shipped as a green that means nothing.

**`mic_off_opens_no_socket` is the one that matters most**, and it is new in kind: with a cloud
engine, a suite that constructs the real recogniser is no longer merely touching a device, it is
**making a network call on the operator's credentials from inside a test run**. `DODONA_UI_MIC=off`
already refuses to construct it (`Recognizers.Create`), and `tests/_workspace.ps1` sets that for
every suite — keep both, and assert the socket specifically.

**What no suite can verify, and say so plainly in the commit:** whether it *hears*. That needs a
voice. The manual acceptance is `VOICE-INPUT-PLAN.md` §6's Spike 4 list, now against a real
engine — twenty sentences of actual operator speech, scored on the technical words, with latency
against the ~300 ms budget `endpointing_ms=300` confirms is the shipped answer. **Do not report a
word-error-rate number you did not measure.** That instruction was given once and honoured; keep
honouring it.

## 8. Cost

Nothing here spends token quota — audio is not tokens. It does consume whatever budget sits
behind that endpoint, on the operator's account, and an open microphone streams continuously.
That is the real reason §6's silence timeout is worth thinking about, and it is a **new class of
cost for this project** (§0.1: quota is the scarce resource, and this is its sibling).

## 9. Decisions

- **D-E1 — use the extension's engine; the risk is accepted by the operator.** An undocumented
  internal endpoint can change or close without notice. The operator's stated position is that
  their risk exposure is identical to the extension's and their tolerance is higher. **If it
  breaks, that is not a surprise and not a defect — it is the accepted term.** Record the failure
  and fall back to §10, do not treat it as an incident.

- **D-E2 — `VOICE-INPUT-PLAN.md` D-V5 is SUPERSEDED, and its stated reason was partly wrong.**
  D-V5 rejected cloud STT because "Dodona holds no existing authenticated pipe". That is false as
  written: **Dodona already runs on the operator's Claude credentials every time it spawns
  `claude -p`.** The remaining true half — a continuously-open microphone streams off-machine —
  is a real trade, and the operator has taken it knowingly. Correcting the reason matters because
  the wrong version would keep getting quoted.

- **D-E3 — auth is spiked before anything is built.** Not sequencing preference: if E1 returns
  401 the entire capture and recogniser layer is wasted work. Phase A already cost a night
  partly because an engine was wired before it was chosen.

- **D-E4 — keyterms are Dodona's vocabulary, ordered by significance**, in one place, with the
  1024-byte silent truncation named in a comment.

- **D-E5 — the fake stays the suites' recogniser and no suite ever opens a socket.** Same force
  as D-V4, one step stronger: a device was a §4-class hazard, a network call on the operator's
  credentials from a test run is that plus a bill.

- **D-E6 — delete `SapiRecognizer.cs` when E3 lands; do not keep it as an offline fallback.** A
  fallback that produces gibberish is worse than an error that says "no network", because
  gibberish looks like the feature working badly rather than not working. The seam means it can
  come back in an afternoon if anyone ever wants it, and git remembers it regardless.

### Decided during the unattended build, 2026-08-20 (E1–E6)

Each of these was a fork hit while building. The rule applied was CLAUDE.md §0.1's *act,
announce, allow undo*: take the reversible option, write down why, keep going.

- **D-E7 — §2'S ERROR CLASSIFICATION CANNOT WORK, AND THE UPGRADE STATUS IS NOT THE AUTH ANSWER.**
  Measured, spike E1, against the live endpoint: the upgrade returns **101 with no
  `Authorization` header at all**, and 101 again for a syntactically valid but wrong bearer. §2's
  prescription — regex the upgrade for `Unexpected server response: (\d+)` and treat 4xx as fatal
  — therefore has nothing to read here. **Auth is refused one frame later:**

  ```
  {"type":"error","error":{"type":"permission_error","message":"Invalid authorization",
   "details":{"error_visibility":"user_facing","error_code":"account_session_invalid"}}}
  then CLOSE 1008 PolicyViolation "Invalid authorization"
  ```

  This mattered more than a documentation fix. A recogniser that trusted the handshake would sit
  in `Listening` at a rejected credential — deaf, silent, and looking healthy, which is §0.3's
  believed-a-green-check one layer down in the protocol, and exactly the failure §5 exists to
  prevent. `SpeechStream.Classify` reads the **frame text and the close code**, keeps the 4xx arm
  for §10's fallback 1 (where auth *is* on the handshake), and keeps 1006/1000 transient so a
  dropped wifi connection does not permanently disarm dictation. **`error_code` is
  `account_session_invalid`, not `invalid_api_key`** — a hint that what it wants is an account
  session credential rather than an `sk-ant-*` API key.

- **D-E8 — §2 IS ALSO WRONG ABOUT WHERE THE ERROR TEXT LIVES.** It documents a top-level
  `message`; the real frame nests it under `error.message`, with the code under
  `error.details.error_code`. Reading only the documented shape gives an error state **with no
  words in it**, which breaks "on and deaf must never look like on" as thoroughly as a wrong
  colour would. Both spellings are accepted.

- **D-E9 — THE RESAMPLER IS MEDIAFOUNDATION'S, NOT OURS. A HAND-ROLLED ONE WAS WRITTEN AND
  REVERTED.** The first cut of `AudioCapture` used a windowed-sinc resampler in pure code, so that
  the rate conversion could be a unit check. The operator flagged the deviation mid-build, and
  they were right: **the whole premise of this document is mimicking the extension because that
  approach is known to work**, and the extension never resamples in its own code — its native
  addon captures at 16 kHz and its `rec`/`arecord` fallbacks are handed `-r 16000`, so a proven
  implementation always does it. Hand-rolled DSP is unproven arithmetic in the one position where
  a bug is **not** a crash and **not** a red check but confident nonsense — indistinguishable from
  the SAPI gibberish this document exists to replace, and it would have been diagnosed as "the
  cloud engine is bad too".

  Testability was bought back without inventing DSP: `Pcm16.DominantHz` **measures** the frequency
  coming out of the real path. Verified 2026-08-20 — a 440 Hz tone in reads 440 Hz out at
  48 kHz×2, 44.1 kHz×2, 48 kHz×1 and 16 kHz×1, where a 3× rate error would read 1320 Hz.

- **D-E10 — `DODONA_UI_MIC` takes `hang`, and `DODONA_STT_ENDPOINT` exists.** Same force as
  D-V15. §6 names a hung start as the genuinely new state to get wrong, and it was otherwise
  unreachable without a black-hole address and a long wait; `hang` is a recogniser that never
  becomes ready. `DODONA_STT_ENDPOINT` lets a suite point the **real** engine at
  `ws://127.0.0.1:1`, where the loopback stack refuses instantly — so the genuine socket failure
  path (connect, refusal, classify, `Failed`, error state) is exercised with **no egress, no
  credential and no cost**, and D-E5 survives intact. It is also §10 fallback 1's URL half, ready
  if that day comes.

- **D-E11 — NO SILENT FALLBACK TO A LESSER ENGINE, ANYWHERE.** The operator's rule, stated
  mid-build and generalising D-E6: *"I also don't want this thing falling back to lighter inferior
  versions. If something is not available the way we need it to be to run the proper thing, then
  it's better to simply tell the user."* Audited against the code: there is no second engine to
  fall back to, no degraded mode, and no retry-with-worse-settings. The one place that was soft
  was the construction-failure path, which returned the fake and let `dump.listen.engine` report
  **`fake`** — reading, on the operator's machine, as though an engine were installed and merely
  quiet. It reports **`none`** now, with the reason in words in the indicator. `fake` remains the
  truth in a suite, which asked for no engine and got none.

- **D-E12 — THE WINDOW OWNS THE `Starting` DEADLINE, not just the engine.** `IRecognizer` gained a
  `Ready` event, because with a socket "Start() returned" and "we are hearing" stopped being the
  same fact (D-E7). `DeepgramRecognizer` has its own connect deadline, and `MainWindow` arms a
  second one anyway: the invariant belongs to the window, so an engine that forgot to raise either
  event cannot leave the indicator reading `starting` forever. Same reasoning that put
  start-on-demand inside `DaemonClient.Send` rather than at each call site (§3.1) — a rule a caller
  can forget is a rule that gets forgotten. `FakeRecognizer` raises `Ready` synchronously, which is
  what keeps all 268 pre-existing checks byte-for-byte unchanged.

- **D-E13 — TWO OF THE SIX CHECKS COULD NOT BE PROVED WITH `dev prove`, FOR A SAFETY REASON, AND
  WERE PROVED BY INJECTION INSTEAD.** `dev prove` runs the new suite against **HEAD's code**, and
  HEAD's `Recognizers.Create` falls through to `SapiRecognizer` for any `DODONA_UI_MIC` value
  except `off` and `fail`. Two of the new checks deliberately unset it to reach the real engine —
  so proving them the normal way **would have opened the operator's microphone twice**, which is
  §4's incident class, and "they are probably not in a call" is exactly the reasoning §4 forbids.
  They were proved by break-and-revert against the new build instead (D-V11's prescribed
  substitute), and every red is recorded in §7's table below.

## 10. If auth closes — the fallbacks, in order

1. **Deepgram directly, with the operator's own key.** *Same engine, same quality*, documented
   and stable, ~$0.26 per hour of audio. Everything in §2 stays true except the URL and the auth
   header; the message shape is Deepgram's own rather than the proxy's normalised one, so §2's
   five message types become Deepgram's `Results` / `is_final` / `speech_final`.
2. **Whisper.net, local.** Free, offline, genuinely good on technical vocabulary, ~150 MB model.
   **It does not stream** — it transcribes a chunk — so it needs voice-activity detection on
   ~300 ms of trailing silence to meet the budget, and latency must be reported alongside
   accuracy. This is the answer if "nothing leaves the machine" ever becomes the requirement.
3. **Nothing.** Ship Phase A with the fake only and the mic glyph in `error` saying why. Honest,
   and better than gibberish.

## 11. Traps that will each cost an hour

- **`--` inside an XML comment in a `.csproj` is a hard MSBuild error** (MSB4025). Cost two
  rounds on 2026-08-20 *in one session*, both times caught instantly by the build naming the
  line. Do not hand-write a double hyphen into a project-file comment.
- **`dev prove` REFUSES the `unit` suite**, and says why: a unit test compiles against the code
  it tests, so a HEAD without your new symbol yields a compile error, not a red. Its prescribed
  substitute is break → `dev build` → `dev test unit` → read the red → revert. Record the red in
  the commit.
- **`dev test` does not build.** A driver that skipped `dev build` read P1.5's stale-build refusal
  as **VACUOUS on all six checks** — a fake verdict about a run that never happened. Treat "did
  not run" as distinct from "vacuous".
- **A check whose subject does not exist on HEAD reads MISSING, not PROVEN**, if it sits behind a
  guard. `clicking_the_mic_toggles_listening` was inside `if ($micBtn)` and never ran. A guard
  that skips the assertion when its subject is absent is a check that cannot fail for the one
  reason it exists.
- **A modal does NOT block `ui dump`.** Win32 modal dialogs run a nested message pump, so the
  dispatcher keeps serving while a `MessageBox` is up — measured. Any "no modal" assertion must
  **count top-level windows**, not infer from responsiveness. (`VOICE-INPUT-PLAN.md` D-V14.)
- **`dump.listen` being right does not mean the screen is right.** Every error-state check was
  green while the words rendered in the same calm blue as "listening", because only the glyph was
  recoloured. Use the `listening` pose and `DODONA_UI_MIC=fail` with `--shot` and *look*.
- **The `voice` suite is in `SoloSuites`.** In the wave it reddened `brain` and `m2` — two suites
  it does not touch — while every gate assertion passed and both were green alone. It starts four
  windows. Do not "tidy" it back into the wave without measuring.
- **Read CLAUDE.md §0.2 in full before writing PowerShell.** Especially `ConvertFrom-Json` on an
  array, `.Count` on a one-element pipeline, `$pid` being read-only, captured native stderr being
  wrapped to console width, and a plain `function` swallowing extra args into `$args`.
- **Write patch scripts to a FILE, never through a shell heredoc or `python -c`.** Backslash
  collapse mangled two scripts on 2026-08-20 — one turned `\r\n` literals into real newlines and
  produced an unterminated string. The `file-patching` skill exists for this and was still
  violated twice in the session that wrote it down.

## 12. What must be true when this is done

- A person talks; the words land in the box; **"enter" is still just a word**, and Enter still
  sends.
- `dump.listen.engine` says `deepgram`, and `error` carries English when it cannot hear.
- 268 existing checks green, untouched, with **no network and no microphone**.
- Six new checks, each seen red.
- A commit that says what the twenty sentences actually sounded like — measured, not hoped.
