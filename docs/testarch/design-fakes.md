# THE DOUBLE LEDGER — standardised test doubles for Dodona, and the mechanism that makes a drifted one fail loudly

Scope: the mock/shim architecture and its anti-drift enforcement. It does not decide the
per-check migration plan (that is the sibling survey's output); it decides **what a double is
allowed to be, where it lives, what anchors it to reality, and what goes red when it stops
being anchored.**

Everything below is read off the code or off the survey reports. The two things I ran myself
are marked MEASURED. I have invented no timings.

---

## 0. THE ONE-PARAGRAPH ANSWER

No single mechanism wins, and a plan that names one is wrong, because **drift is three
different failures wearing one word** (§1). The design is a **Double Ledger**: one
`[Double(...)]` attribute that every test double must carry, and one ~120-line reflection test
in the `unit` suite that enumerates the population and refuses any double that is not anchored
to its real counterpart by one of three admissible anchors — a **shared interface with two
production implementers**, a **contract test whose body runs against the real thing too**, or a
**corpus of real-world bytes**. The attribute additionally names the **integration wire the
double does not replace**, and the ledger mechanically asserts that wire's check name still
exists in a tracked suite. Add an unanchored fake, delete the wire a fake stood beside, or grow
the fake a shape reality has never emitted, and `dev test unit` goes red in about ten
milliseconds with the offending type named. Nobody has to remember anything.

The one drift class no mechanism can catch automatically is named honestly in §12 and given a
verb (`dev corpus-record`) and a **reading, not an assertion**, on the gate's verdict block.

---

## 1. DRIFT IS THREE FAILURES, AND ONE THING THAT IS NOT DRIFT AT ALL

Naming them separately is what makes the mechanism choice decidable. Conflating them is why
"contract tests" reads like a complete answer and is not.

| axis | the failure | what can catch it |
|---|---|---|
| **D1 — shape drift** | the interface grew a member, or the double no longer satisfies it | the **compiler**, if the double implements the same interface production does; a **reflection assertion** for negative shape claims ("no member may mean send") |
| **D2 — behaviour drift** | the double satisfies the interface and *decides differently* from the real implementation | a **contract test** — one test body, two subjects — and only where the real subject is cheap, deterministic and in-process |
| **D3 — world drift** | the double is faithful to yesterday's reality and reality moved: `claude` changes its stream-json, Deepgram changes its frames, git changes its output | **nothing automatic.** Only a recorded corpus of real bytes plus a deliberate re-record. Everything else is a lie about coverage |

And the fourth thing, which the repo is scarred by and which is **not drift**:

> **D0 — the self-fulfilling lookup.** The routing ladder was fully covered and fully green
> while dead in production for two days, because `brain-acceptance.ps1` stood up its own
> `role='router'` classifier and then looked one up. The fake was *perfectly faithful*. The test
> was asking a question that could not come back false.

D0 is not fixed by any anti-drift mechanism, and a plan that files it under "drift" will build
the wrong machine. It is fixed by two rules this design carries as hard constraints:

- **ONE LANDING SITE.** A double must be installed by replacing the exact field, delegate,
  argument or executable that production reads — never by feeding a parallel path.
  `IRecognizer`'s doc comment states this verbatim (`src/DodonaUi/Recognizer.cs:21`): *"a fake
  that fed a parallel path would prove nothing about the real one."*
- **EVERY DOUBLE NAMES A WIRE IT DOES NOT REPLACE**, and the ledger checks that wire still
  exists (§3.4). This is constraint 3 turned from a promise into a red build.

---

## 2. MECHANISM EVALUATION

### 2.1 Contract tests / verified fakes — **STRONGEST ANCHOR, CANNOT BE THE MECHANISM**

One shared abstract test body, two concrete subclasses, one supplying the real implementation
and one the fake. xunit does this for free by inheritance; no package, no generator.

**Where it fits here, and fits well:** `ILaneSink`. The real implementation is a `Store` over a
temp SQLite file — cheap, in-process, deterministic. And it has a property a naive fake will
absolutely delete: `Store.PaneEventId` (Store.cs:1017) is `INSERT OR IGNORE` on
`UNIQUE(lane_id, seq)` and **returns 0 when the row was a duplicate seq** (Store.cs:1030, and
the doc comment at :1013 says so). A recording sink written the obvious way — append to a list,
return `list.Count` — returns a positive id for a redelivered line, which silently deletes the
exactly-once guarantee that `m0`'s whole daemon-death section exists to protect. A contract test
catches that on the first run. This is the single best argument in the document for contract
tests and it should be the flagship.

**Why it cannot be *the* mechanism, given the real implementations named in the brief:**

- **A SQLite store.** Contract-testable, and the contract's verdict is *"delete the fake"* —
  because for the merge token and the land (`TokenRequest` :1408, `TokenRenew` :1489,
  `TokenRelease` :1507, `LandCommit` :1525) the property **is** the transaction: `LandCommit`
  re-checks holder identity and lease expiry *inside* the transaction that lands the ticket,
  frees the claims and withdraws the `land` question, in one multi-statement command
  (Store.cs:1541-1560). A double reimplements that as sequential field writes and passes a
  contract test written in sequential steps. The contract only bites if it asserts atomicity
  under interleaving, which is not a test body you can write once and run against both. So the
  right answer for Store is **no double at all**, and the contract test's value there is that it
  *forces you to notice that*.
- **A named-pipe server.** There is nothing to contract-test: the correct move is to delete the
  transport from the test rather than fake it. `Daemon.HandleAsync(string req, StreamWriter w)`
  (Daemon.cs:981) is the entire 45-case command surface and its only dependencies are a JSON
  string and a `StreamWriter`; a `StreamWriter` over a `MemoryStream` **is not a double** — it is
  the real handler with nine lines of pipe server (Daemon.cs:957-975) not present. No fake, no
  drift, no anchor needed. That is strictly better than any verified fake.
- **A child process (`claude`).** This is the killer. The real counterpart **costs quota, is
  nondeterministic, and §0.1 forbids suites making model calls**. A contract test that cannot run
  against the real subject is not a contract test; it is one test body run once. And this is
  precisely the highest-drift-risk double in the tree (`DodonaFakeAgent`, 545 lines hand-writing
  `claude`'s wire shape, with nothing keeping it in sync — the seam survey §1.11 names it).

**Verdict:** admitted as **anchor kind `Contract`**, mandatory wherever the real implementation
is cheap and in-process. Rejected as the mechanism, because the one double that most needs an
anchor structurally cannot have this one.

### 2.2 Reflection assertions (the `Dictation_never_submits` style) — **THE MECHANISM'S SPINE**

`tests/Dodona.Tests/DictationTests.cs:35` walks `Enum.GetNames<DictationAct>()` and demands no
member means send; `:202` walks `Enum.GetValues<ListenState>()` and demands every state reaches
`Off`. The prior-art survey confirms these are **the only two in the tree** — a grep for
`Enum.GetNames|Enum.GetValues|Reflection|GetMethod` across `tests/Dodona.Tests` returns nothing
else. `docs/VOICE-ENGINE-PLAN.md:88` calls breaking one *"breaking the operator's one hard
constraint, not finding an inconvenience."*

Strengths that decide it: it is enforcement in code (§0's strongest form), it costs
milliseconds, it needs no package, it cannot flake, it runs in the suite the operator already
runs while editing, and **it is the only candidate that can police the population of doubles
itself** rather than one double at a time. A contract test proves *this* fake is honest; a
reflection assertion proves *no dishonest fake was added*, which is the property that survives
a session boundary.

Its limit, stated plainly: reflection sees only types this repository declares. It cannot know
what `claude` emits. So it cannot touch D3.

**Verdict: this is the mechanism.** Everything else is an anchor it enforces.

### 2.3 Source generators / analyzers — **REJECTED**

Loses on three counts, any one of which is sufficient:

1. **Cost against value.** Everything an analyzer could enforce here (a type must carry an
   attribute; an attribute's target must satisfy a predicate) a ~120-line reflection test
   enforces at ~10 ms with zero build cost. The analyzer buys an earlier red squiggle and
   nothing else.
2. **The offline-pinning decision.** `tests/Dodona.Tests/Dodona.Tests.csproj` is version-pinned
   to the machine's package cache *on purpose*: *"adding a verification step that needs the
   network is adding a way for verification to be unavailable exactly when it is wanted."* A
   Roslyn analyzer is a new package reference on the critical path of every build.
3. **`tools/dev.ps1` must run on a tree that will not compile** (CLAUDE.md §1: *"a tool whose job
   is to fix a blocked or broken build cannot itself require a build"*). An analyzer that
   participates in compilation adds a way for `dev check` to fail for a reason that is not the
   user's code.

### 2.4 Colocation — fake and real in the same file/project — **KEPT AS A RULE, REJECTED AS THE MECHANISM**

The tempting reading is that `ILaneSink` is safe because its two implementations are visible
together. That is not why. **It is safe because both implementers ship** — `Store` (Store.cs:12)
and `ConciergeStore` (ConciergeStore.cs:35) are production code, so the interface cannot be
changed without breaking a running system. Visibility in review is a convention, and
`RECOVERY-PHASES` D-6 and CLAUDE.md §0.3 both refuse conventions as answers: *"a documented
warning is not a fix"*, *"every item is code, a deletion, or a check."*

**Kept as a layout rule** (§5): a double lives next to its contract, in `tests/Dodona.Tests/
Doubles/`, one file per double, and the contract that anchors it is one directory over. Free,
helps a reviewer, enforces nothing, and is never cited as a reason a double is safe.

### 2.5 Golden / approval files of the wire protocol — **THE ONLY THING THAT REACHES D3**

A corpus of real bytes replayed through the real parser is the only candidate that can catch
"reality moved". And **the seed already exists and is already committed**:
`spikes/spike1-output/wire.jsonl` — 20 real `claude` stream-json lines from spike 1.

MEASURED (I parsed the file): 20 lines — 1 `system/init`, 11 `system/thinking_tokens`, 4
`assistant`, 2 `result` (`subtype: success`), 1 `rate_limit_event`, and **line 1 carries a UTF-8
BOM** (`json.load` refused it until decoded as `utf-8-sig`). Two findings from it that a plan
author must not miss:

- The two `result` lines do **not** put `"type"` first in key order (the first 400 characters are
  `is_error`, `duration_api_ms`, `num_turns`, …). Any parse that is not a real JSON parse dies on
  real traffic. `LaneRuntime` uses `JsonDocument`, so it is fine — but this is the shape of the
  hazard a corpus catches and a hand-written fake never will, because the fake writes its own
  key order.
- Real `assistant` messages carry `content[].type == "thinking"` blocks (lines 9 and 17), which
  the hand-written fake never emits at all, and which arrive as a *separate* message sharing the
  same `message.id` as the text block that follows it.

**The honest weakness, which must be designed around rather than hidden:** a golden file is a
*recording*, and a recording goes stale silently — which is CLAUDE.md §0.1's "quietly stale"
in a new costume. A corpus with no re-record path is a mechanism that decays into a museum.
§6.4 gives it a verb, a manifest with provenance, and a gate **reading**.

**Verdict:** admitted as **anchor kind `Corpus`**, mandatory for any double standing in for
something outside this repository.

### 2.6 Scoreboard

| candidate | D0 | D1 | D2 | D3 | cost | verdict |
|---|---|---|---|---|---|---|
| contract tests | no | partly | **yes** | no | low where the real thing is in-process | anchor kind `Contract` |
| reflection assertions | partly (can check the landing site exists) | **yes** | no | no | ~10 ms, zero deps | **THE MECHANISM** |
| generator / analyzer | no | yes | no | no | new package, build-path risk | rejected (§2.3) |
| colocation | no | no | no | no | free | layout rule only |
| golden corpus | no | no | partly | **yes** | one deliberate model call per re-record | anchor kind `Corpus` |

---

## 3. THE DOUBLE LEDGER (the mechanism, in code)

### 3.1 The attribute

`tests/Dodona.Tests/Doubles/DoubleAttribute.cs`:

```csharp
enum Anchor
{
    /// The double implements an interface that has TWO OR MORE implementers in the
    /// PRODUCTION assemblies. The interface cannot change without breaking a shipping
    /// implementation, so the double is dragged along by the compiler. (ILaneSink.)
    Interface,

    /// A `*Contract` abstract test class supplies its body to both this double and the
    /// real implementation named by `Real`. Behaviour is compared, not asserted about.
    Contract,

    /// The double stands in for something OUTSIDE this repository. Its faithfulness is
    /// anchored to a corpus of real bytes under tests\assets\wire\real\.
    Corpus,

    /// The double is installed by assigning the SAME static delegate production reads, so
    /// there is exactly one landing site. The weakest anchor: use it only when the real
    /// counterpart cannot be run in-process at all, and NEVER without a Wire.
    Landing,
}

[AttributeUsage(AttributeTargets.Class)]
sealed class DoubleAttribute : Attribute
{
    public DoubleAttribute(Anchor anchor, Type real) { Anchor = anchor; Real = real; }
    public Anchor Anchor { get; }
    /// The real counterpart, as a TYPE — never a string. A string name is itself a
    /// drift-prone hand copy; a typeof() stops compiling when the real thing is renamed.
    public Type Real { get; }
    /// `<suite>:<check_name>` — the integration check that still crosses the real thing
    /// that this double stands in for. Required. Verified to EXIST by the ledger (3.4).
    public string Wire { get; init; } = "";
    /// Corpus anchor only: the key in tests\assets\wire\MANIFEST.json.
    public string Corpus { get; init; } = "";
}
```

`Real` is a `Type`, not a string, on purpose: a string is a hand-copied name and hand-copied
names are the thing this document exists to stop.

### 3.2 The ledger test

`tests/Dodona.Tests/Doubles/DoubleLedgerTests.cs`, three `[Fact]`s, all in `unit`:

**`Every_double_is_anchored_to_its_real_counterpart`**

Enumerate `Assembly.GetExecutingAssembly().GetTypes()`. A type is *a double* if it carries
`[Double]` **or** its name starts with `Fake`/`Recording`. For each:

- it must carry `[Double]` (a `Fake*` with no attribute is the failure this test exists for);
- `Anchor.Interface` → find the interfaces the double and `Real` share; at least one such
  interface must have **≥ 2 implementers among the production assemblies**
  (`typeof(Store).Assembly`, and `typeof(MainVm).Assembly` once a UI test project exists).
  Reflection over the real assembly, not a list;
- `Anchor.Contract` → there must exist an abstract type whose name ends in `Contract`, carrying
  ≥ 1 `[Fact]`, with ≥ 2 concrete subclasses in this assembly, one of whose `Subject` property
  type is the double and one of whose is `Real`;
- `Anchor.Corpus` → `Corpus` must be non-empty and must be a key in
  `tests/assets/wire/MANIFEST.json`'s `anchored` object, naming a file that exists and is
  non-empty;
- `Anchor.Landing` → `Real` must declare a `static` field or property whose declared type
  accepts this double, and `Wire` must be non-empty.

**`No_double_is_named_Stub_or_Mock`**

A naming refusal with teeth: any type in the test assembly whose name starts with `Stub` or
`Mock` fails. Not stylistic. A `Stub` in the usual sense is *a thing that returns a canned value
so the code under test proceeds* — which is exactly what a `role='router'` lane that the suite
created and the suite then looked up was. The two permitted words carry their meaning:
**`Recording*`** records what production did to it and asserts nothing on its own;
**`Fake*`** stands in for something that would cost money, a device, or a process.

**`Every_double_names_a_wire_that_still_exists`** — §3.4.

### 3.3 Repo-root discovery from a test

The ledger reads tracked files. Do not use `AppContext.BaseDirectory` (it is `bin/Release/net8.0`
and moves when `BaseOutputPath` is redirected — see §9.2) and do not use an environment
variable. Use the compiler:

```csharp
static string RepoRoot([CallerFilePath] string here = "") =>
    // <repo>\tests\Dodona.Tests\Doubles\Paths.cs -> up four
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
```

`[CallerFilePath]` is baked at compile time, is exact, and survives any output redirection.

### 3.4 `Wire` — the mechanism against constraint-3 erosion

This is the highest-value idea in the design and it is nearly free.

Every double names, as a string, the integration check that still crosses the real thing:
`Wire = "m0:orphaned_result_landed"`. The ledger builds the **check-name ledger** — every check
name in the repository — and asserts each `Wire` is in it. Delete or rename the wire that the
double stands beside, and `dev test unit` names the double and the missing check.

The check-name ledger is a shared helper, `tests/Dodona.Tests/CheckLedger.cs`, and it must
handle every registration form the repo actually uses (the prior-art census found four):

| form | where | example |
|---|---|---|
| `Check '<name>'` / `Check "<name>"` | 14 suites (a local `function Check` in each) | `brain:39`, `m1:44`, `m3:40` |
| `$results['<name>'] = ` | `m0` only — it has no `Check` helper, 26 inline assignments | `m0:36` |
| loop-generated | two sites, enumerate-proof | `concierge-acceptance.ps1:307` `Check "resolution_recorded_$rung"`, `m1-acceptance.ps1:1167` `Check "event_$k"` |
| harness-generated | `Assert-NoBuildOutputProcesses`, `tests/_workspace.ps1:353` | one `no_process_left_in_the_build_output` per suite |
| **new: C# method names** | `[Fact]`/`[Theory]` methods in this assembly | `a_finished_turn_produces_a_completion_record` |

The two loop-generated families are matched by prefix (`resolution_recorded_*`, `event_*`) and
carry a comment saying so; the harness-generated one is synthesised per suite file.

**And the ledger asserts uniqueness, which is a free catch nothing does today.** Every suite
keys `$results` by check name, so a **duplicate name silently overwrites and the tally silently
drops by one** — and `tools/dev.ps1:584` records the decision that the runner has *no
expected-count oracle*, so nothing anywhere can notice. `Every_check_name_is_unique` closes that
for the price of a `HashSet`, and it is what makes constraint 1's *N-names-in / N-names-out*
proof mechanical rather than a promise.

### 3.5 The escape hatch, and why it is the same shape everywhere

Every refusal in this design has exactly one escape: **a tracked list entry carrying a reason
string, enumerated by the same reflection test.** `MANIFEST.json`'s `unwitnessed[]` (§6.3),
`PoseCoverage.Exempt` (§4.12), the loop-generated name prefixes above. This is the repo's own
habit — *rejected ideas live with their reasons* — turned into enforcement: the exception is
still red until somebody writes down why, in a file a reviewer reads, in the same commit.

---

## 4. THE BOUNDARY TABLE — every boundary, and its verdict

`Wire` values below are the survivors the wire-counting survey already named; an implementing
agent uses the survey's `bestSingleCheck` for each.

| # | boundary | double? | anchor | notes |
|---|---|---|---|---|
| 1 | **`ILaneSink`** (Store, ConciergeStore) | **`RecordingLaneSink`** | `Interface` **+** `Contract` | the flagship (§4.1) |
| 2 | **`Store`** (SQLite) | **NO** — the real `Store` over a temp path is the fixture | — | the properties are the transactions (§4.2) |
| 3 | **`Registry`** | **NO** | — | the partial `UNIQUE(members.key) WHERE is_git` index is the arbiter; a `HashSet` is a different mechanism (§4.3) |
| 4 | **daemon control pipe** | **NO — deleted, not faked** | — | `Daemon.HandleAsync(req, StreamWriter over MemoryStream)` is the real handler minus nine lines of pipe (§4.4) |
| 5 | **`DaemonClient.Send`** (UI → daemon) | **`RecordingTransport`** | `Landing` | one static delegate, five call sites; wire kept (§4.5) |
| 6 | **shim ↔ daemon wire, daemon half** | **NO double for the parser** — `HandleShimLine` made `internal`, fed the corpus | `Corpus` | (§6) |
| 7 | **shim ↔ daemon wire, shim half** | **NO** | — | `delivered` advances only after `WriteLine` succeeds (DodonaShim/Program.cs:240-243); the property is a real broken pipe |
| 8 | **the agent (`claude`)** | **`DodonaFakeAgent`, unchanged** | `Corpus` | stays a real process on the real shim; anchored by §6 (§4.6) |
| 9 | **the clock** | **NO INTERFACE** — time is a parameter with a defaulted overload | `Interface` n/a | (§7) |
| 10 | **process launcher** | **NO** | — | the spawn site's invariants *are* the OS call (§4.7) |
| 11 | **git** | **NO — a real temp repo fixture** | — | `SilentDrops` exists to catch a merge that *succeeded* and lost work (§4.8) |
| 12 | **filesystem predicates** | **injected `Func<string,bool>`** | `Interface` n/a — it is an *argument* | the `Trees.Locate` pattern; production has exactly one path (§4.9) |
| 13 | **`IRecognizer`** | **`FakeRecognizer` (exists)** | `Interface` | keep; close the named `Ready` hole (§4.10) |
| 14 | **`GateHook` stdin** | **`TextReader` parameter after S11** | — | keep one real-bytes-into-the-real-exe wire, forever (§4.11) |
| 15 | **`Poses` → `Snapshot`** | **`Poses` is already a double of the live store** | `Interface` (it builds the real `Snapshot` record) | add the completeness assertion (§4.12) |
| 16 | **the forge (`gh`)** | **NO** | — | `REVIEW-AND-MERGE-PLAN:546`: *"mocking a forge into the daemon would be testing the mock"* |
| 17 | **`\\.\pipe\` namespace / lane liveness** | **NO** | — | a fake namespace has no blink, and the blink is why `LaneLiveness` is a union (§4.13) |

### 4.1 `RecordingLaneSink` — the flagship double

`tests/Dodona.Tests/Doubles/RecordingLaneSink.cs`

```csharp
[Double(Anchor.Interface, typeof(Store), Wire = "m0:orphaned_result_landed")]
sealed class RecordingLaneSink : ILaneSink { … }
```

Anchored twice on purpose:

- **`Interface`** — `ILaneSink` (LaneSink.cs:22) is six methods with **two production
  implementers**. It cannot rot: a seventh method breaks `Store` and `ConciergeStore` first.
  Seam cost to use it: `LaneRuntime.HandleShimLine` (LaneRuntime.cs:95) `private` → `internal`.
  **One keyword.**
- **`Contract`** — `Contracts/LaneSinkContract.cs`, abstract, `[Fact]`s asserting the semantics
  `LaneRuntime` actually depends on, run against **both** `Store` (real, temp file) and
  `RecordingLaneSink`:
  - `PaneEventId` returns **> 0** for a fresh `(laneId, seq)` and **0** for a repeat
    (Store.cs:1030, doc at :1013) — the exactly-once property;
  - `PaneEvent` is `PaneEventId(...) > 0` (Store.cs:1010-1011);
  - a **null `seq`** never collides with anything, however many are written — this is the
    derived progress row (LaneRuntime.cs, the `seq stays NULL` comment) and a fake that keyed
    dedup on the tuple naively would wrongly drop the second progress row of a turn;
  - `LaneSession` / `LanePresence` / `KvSet` are last-write-wins and readable back.

  The contract is what stops the obvious three-line recording sink — append, return
  `list.Count` — from returning a positive id for a redelivered line and silently deleting the
  guarantee `m0`'s daemon-death section exists to protect.

Concrete win it unlocks, already flagged by the survey: `m0:landed_exactly_once` **does not prove
what its name says** — the fixture never creates a redelivery, so daemon 2 replays once. The
only mechanism that could double it is the `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)`. That is
now a two-insert contract case, free and strictly stronger, and the m0 wire keeps its name for
what it *does* prove (a replacement daemon drains the orphaned buffer).

### 4.2 `Store` — no double, a fixture

`tests/Dodona.Tests/Fixtures/StoreFixture.cs`. A real `Store` over a real path. Two flavours,
and the choice is **MEASURED, not stylistic**:

- **`:memory:`** for pure-`Store` questions (migration ladder, token FIFO, claim conflicts,
  question upsert). Blocked today by exactly one line: `Store.cs:33` does
  `Directory.CreateDirectory(Path.GetDirectoryName(path)!)`, and MEASURED,
  `Path.GetDirectoryName(":memory:")` returns `""` (not null) so `Directory.CreateDirectory("")`
  throws `ArgumentException: Path cannot be the empty string or all whitespace`. Fix:
  `if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);`
- **a temp FILE** for anything spanning `Store` **and** `StoreReader`. `StoreReader.Open()`
  (StoreReader.cs:30) does `if (!File.Exists(_path)) return false;` and then opens a *second*
  `Mode=ReadOnly` connection — a private `:memory:` db is invisible across connections, so the
  writer/reader pair (the whole m3 doctrine) **cannot** run over `:memory:`.

Fixture path: `%TEMP%\dodona-tests\<guid>\`, deleted on `Dispose`. Never `DODONA_HOME` (§9.4).

The v8-shaped-store fixture for migration content keeps the trap CLAUDE.md §0.2 already paid
for: it must drop the columns of **every later version**, keyed on **the column existing**, never
on a version number — because the same fixture runs under `dev prove` against a build that does
not have the column at all.

### 4.3 `Registry` — no double, and a two-line seam

`Registry()` (Workspaces.cs:65) takes no path and always uses `Paths.Registry`. Add a
`Registry(string path)` overload with the parameterless one delegating. Two lines, and it is the
single change that lets two isolated registries exist in one process. **No fake ever**:
repo-exclusivity is enforced by a partial `UNIQUE` index which the class comment calls *"the
real arbiter"*, and CLAUDE.md §5 says a red exclusivity check is a correctness incident, not a
flaky test. A fake enforcing uniqueness in a `HashSet` is a *different enforcement mechanism*
passing a test written about the index.

### 4.4 The daemon control pipe — deleted, not faked

`Daemon(string primary, string wsId, string wsName, string ctlPipe, Store store)` (Daemon.cs:528)
`private` → `internal`, and `HandleAsync` (Daemon.cs:981) `private` → `internal`. **Two
keywords.** `DaemonFixture` then constructs a daemon over a `StoreFixture` and drives all 45
command shapes through `HandleAsync(req, new StreamWriter(new MemoryStream()))` with no pipe, no
`Global\dodona-<id>` mutex, and no `RunAsync` loop.

This is the design's preferred move wherever it is available and it is worth stating as a rule:
**a transport you can leave out beats a transport you fake.** There is no double, so there is
nothing to anchor and nothing to drift.

What it does **not** cover, and what therefore keeps its integration check: that the *pipe
server* calls `HandleAsync` at all, that the control pipe is **serial** so a slow handler freezes
the daemon (`m1:say_answers_during_a_land` — a real concurrent call *during* a running land),
and start-on-demand.

### 4.5 `RecordingTransport` — the weakest anchor, and the rules that make it tolerable

`DaemonClient` is a `static class` (DaemonClient.cs:13) with `public static string Send`
(:160), called directly from `MainWindow.Send` (:1244), `SendConcierge` (:1256) and `AnswerAsk`
(:1201), which also calls `DaemonClient.Ensure`/`EnsureConcierge` (:1199-1200) — those
`Process.Start` a daemon, so a transport seam that misses them starts one.

Seam: `internal static Func<string, object, string> Transport = DaemonClient.Send;` plus an
`Ensure` delegate; route all five sites through them. Then `RecordingTransport` answers *which
pipe, which command shape, which id* for `LaneAction`, `AnswerAsk`, `FocusWorkspace` and
`SubmitInput`'s concierge-vs-daemon branch.

`Anchor.Landing` is deliberately the weakest kind and comes with two non-negotiable rules:

- **`Wire` is mandatory** — here, `ui-wake:a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts`,
  because the 2026-08-19 incident was a *call site that forgot to ensure*, answering the first
  thing a person did with the literal words "daemon not running". Faking the transport removes
  the daemon whose absence is the whole test.
- **one landing site** — the recording delegate must be the *same field* production reads. A
  test that called `RecordingTransport` directly would be D0 in a new costume.

### 4.6 `DodonaFakeAgent` — unchanged, and finally anchored

Do **not** replace it with an in-process agent double. Its virtue is that it goes through the
**real shim, the real named pipe and the real `LaneRuntime`** — it is not a parallel path — and
an in-process double would delete all three and turn every m0/m1 check into a claim about
nothing.

Do **not** touch its default behaviour, which is deliberately un-helpful (router defaults to
`unclear`, both concierge tiers default to `none`/`low`, and `src/DodonaFakeAgent/Program.cs:51,69`
say why: *"a fake agent that guessed would hide the one path most worth testing"*). That is
already the right rule and it generalises to every new double in this plan: **a double's default
must be the answer that makes the interesting path run, never the quiet successful one.**
`LANE-LIFECYCLE` rejected "default an unclassified tool to silence" for the same reason — *"it
reads as tidy and is the same defect wearing a fallback."*

What changes is only that it becomes **anchored** (§6), and that it gains one argv:
`--wire-sample <path|->`, which writes exactly one instance of every shape it can emit and
exits. It stays a project that references nothing.

### 4.7 The process launcher — no double, a *value*

`AttachShimAsync` (Daemon.cs:3998) holds two invariants only a real spawn can show:
`Projects.PromptDirMismatch` (:4013) compares the system prompt's *stated* directory against the
real `ProcessStartInfo.WorkingDirectory`, and `DeployGate` (:4062/:6581) writes the settings
file — the comment at :4064 says outright that gating only real-claude lanes would make the
deployment invisible to every suite.

So: extract the **plan**, not a launcher. `Daemon.ClaudeArgs` (:2846) is already `internal
static`; widen the idea to a `SpawnPlan(string exe, string[] args, string cwd, IReadOnlyDictionary<string,string> env)`
record computed by a pure function and *passed* to the real `Process.Start`. Unit-test the plan;
keep the wires that assert the OS honoured it
(`m0:a_lane_agent_is_told_its_workspace`, `workspace:the_agent_process_really_runs_in_that_project`).

### 4.8 git — no double, a real temp repo

`Git.Run` (Git.cs:9) is `static Process.Start` with no interface, and every git operation goes
through it including `MainMergeOnBranch` (Daemon.cs:6093) and `SilentDrops` (:6129). **Never
fake anything that mutates a ref.** A canned stdout tests the parser and proves nothing about
the operation, and `SilentDrops` exists *specifically* to detect a merge that **succeeded** and
lost work.

`Fixtures/GitRepoFixture.cs`: `git init`, a pinned identity (`-c user.name=… -c user.email=…`
so it never reads the operator's global config), deterministic commits. Shared per xunit
collection and reset between tests (`git reset --hard` + `git clean -fd` is far cheaper than a
fresh `init`), because a per-test repo is many process starts and the `unit` budget is 1-2 s.

What **is** safely unit-testable is the **parsing** of git output: split `SilentDrops` and
`MainMergeOnBranch` into `(runGit) → decision` shape with the string-parsing half separated, the
way `Trees.Locate` already is.

### 4.9 The filesystem — injected predicates, which are arguments, not doubles

`Trees.Locate(fullPath, projects, Func<string,bool> dirExists, Func<string,bool> fileExists)`
(Trees.cs:44) with a convenience overload binding the real `Directory.Exists`/`File.Exists`
(Trees.cs:77). `TreesTests.cs:24` passes array lookups. **Production has exactly one path**,
which is the strongest anti-drift property available anywhere in this document — there is no
second implementation to drift.

Extend to the five sites the seam survey names: `Repos.Under` (Repos.cs:81), `Fence.Enumerate`
(Fence.cs:67), `Fence.Roots` (Fence.cs:41), `Git.FindRepos` (Git.cs:77), `LaneLiveness.Records`
(LaneLiveness.cs:50). ~5 lines each.

Test-side helper `Doubles/Fs.cs` builds the predicate pairs from a path list. It carries **no**
`[Double]` attribute and the ledger must not demand one: an argument is not a double. The ledger
recognises it by it being a static helper class with no instance state; simpler, state the rule
as: **`[Double]` is required only of types that stand in the place of a named production type.**

### 4.10 `IRecognizer` — keep, and close the one named hole

`FakeRecognizer` (Recognizer.cs:73) is the best double in the tree and the standard everything
else is held to: two implementations, ONE landing site (`MainWindow.OnHeard`), and — the detail
worth copying — at Recognizer.cs:87-90 it reports `Engine = "none"`, **not `"fake"`**, when it
stands in because the *real* engine failed, so `ui dump` can never make a missing engine look
installed.

Its **named semantic hole**, admitted in its own comment: `Start()` raises `Ready`
**synchronously** (Recognizer.cs:98-107) while the real socket does not — and the comment says
the sync raise exists to keep the existing checks byte-for-byte unchanged. So the fake is
deliberately easier than the real thing *exactly where the real thing is hardest* (the interface
doc at :33-46 spends fourteen lines on why "Start() returned" and "we are hearing" are two
different facts), and nothing covers the difference.

**Fix in production code, matching D-V15's own reasoning** (*"a test-only path would prove nothing
about the real one"*): add `DODONA_UI_MIC=async` — `Ready` raised on a thread-pool callback after
a short delay — and run at least one `voice` check under it. Marked **optional / measured**: it
changes `voice`'s timing, and `voice` is the suite with the live flake signature (issue #3), so
it is implemented only if it measures green 5/5 alone and 3/3 in a wave. If it does not, the
hole gets a `[Double]`-adjacent written exemption rather than a comment, per §3.5.

### 4.11 `GateHook` — the one place prose has been wrong twice

`Program.cs` is top-level statements: `GateHook()` (:890), `GateAsk()` (:875), `ParseArgs()`
(:1692) are **private members of the compiler-generated `Program` class**, and
`InternalsVisibleTo` does not reach private members. So today every one of `GateHook`'s ~8
return paths is provable only by running the real exe.

Seam S11 (a real refactor, not a keyword): move `GateHook` + `ParseArgs` into a real `static
class` in `src/Dodona/`, parameterised on `TextReader`/`TextWriter` and an arg dictionary
instead of the closed-over `opts`, with the daemon round trip as an injected
`Func<object,(int,string)>`. The injected func is an *argument* (§4.9), not a double.

**And one integration wire stays forever, whatever the refactor buys**: real bytes, **including a
UTF-8 BOM**, piped into the real `dodona.exe`. `Console.In` hands a leading U+FEFF back as an
ordinary character; PS 5.1 writes BOMs by default; and that combination made the claim gate
**fail open on every run** while looking green. CLAUDE.md §7 and issue #4 have each asserted this
function's properties in prose and been wrong. This double's `Wire` is
`m1:the_gate_still_checks_the_tree_when_the_ticket_argument_is_unreadable`.

### 4.12 `Poses` — a double that already exists, with a false green on its record

A pose returns a real `Snapshot` (Vm.cs:85), the same record `Poller.cs:152` builds from the live
store, so the **positional** members are compile-enforced across all 15 `new Snapshot(...)` sites.
Everything added since is an `init` property with a default (`Ask`, `Quota`, `Bands`,
`FocusedWorkspace`, `FocusedWorkspaceName`, Vm.cs:91-102) — so a pose that omits new state
compiles clean, and `Poses.cs:71-76` records the incident verbatim: *"they posed a state that
cannot occur — panes present, no workspace — which is how a blank grid once rendered behind a
passing test."* It was fixed with a wrapper, not a mechanism, and `Poses.Names` is a
hand-maintained `string[16]`.

**Mechanism:** `Poses_cover_every_snapshot_member` — reflect over `Snapshot`'s properties and
demand each takes **at least two distinct values across the pose set**. "Non-default somewhere"
is too weak (a member whose meaningful value *is* the default passes trivially); two distinct
values means somebody deliberately posed it both ways. Escape hatch per §3.5: a `PoseCoverage.
Exempt` array of `(member, reason)`.

### 4.13 Lane liveness — never fake the pipe namespace

MEASURED and recorded in CLAUDE.md §0.2: **8 of 192 reads over 1.5 s saw no pipe** while the shim
was alive and instantly connectable, because a server that disposes one `NamedPipeServerStream`
and constructs the next is briefly not in the namespace. `LaneLiveness.Live` is a *union* of two
OS answers precisely because of that blink. **A fake namespace has no blink**, so it cannot
reproduce the bug the union exists to prevent. The union algebra is pure and moves down; the
namespace stays real, in one wire.

---

## 5. FILE LAYOUT AND NAMING

```
tests/Dodona.Tests/                       one project, xunit, InternalsVisibleTo already granted
  CheckLedger.cs                          every check name in the repo, all five forms (3.4)
  Doubles/
    DoubleAttribute.cs                    the attribute + Anchor enum
    DoubleLedgerTests.cs                  the three enforcement facts (3.2)
    RecordingLaneSink.cs                  [Double(Interface, typeof(Store))]
    RecordingTransport.cs                 [Double(Landing,   typeof(DaemonClient))]
    Fs.cs                                 injected predicate builders (no attribute: an argument)
  Contracts/
    LaneSinkContract.cs                   abstract, the [Fact]s
    StoreLaneSinkTests.cs                 : LaneSinkContract   Subject => real Store
    RecordingLaneSinkTests.cs             : LaneSinkContract   Subject => the double
  Fixtures/
    StoreFixture.cs  RegistryFixture.cs  GitRepoFixture.cs  DaemonFixture.cs
  Wire/
    WireCorpusTests.cs                    real bytes -> HandleShimLine -> expectation
    FakeShapeTests.cs                     fake-sample ⊆ corpus ∪ declared unwitnessed
tests/assets/wire/
  MANIFEST.json
  real/spike1.jsonl                       seeded from spikes/spike1-output/wire.jsonl, BOM kept
  real/<yyyy-mm-dd>-<model>.jsonl         written by `dev corpus-record`
  expected/<name>.expected.json           the classification the parser must produce
  fake-sample.jsonl                       written by `dev wire-sample`, compared by lint I9
```

**Naming, and it is enforced (§3.2):**

- **`Recording*`** — records what production did to it, asserts nothing itself.
- **`Fake*`** — stands in for something that would cost money, a device, or a process.
- **`*Fixture`** — owns *real* machinery over a temp path; is not a double and carries no
  attribute.
- **`*Contract`** — abstract, holds the `[Fact]`s, has ≥ 2 concrete subclasses.
- **`Stub*` / `Mock*`** — **forbidden by a failing test.** A stub returning a canned value so the
  code proceeds is the D0 shape.
- Test method names are the **exact `snake_case` check name**, because the name is what
  constraint 1's proof is made of and the ledger reads both languages. The existing xunit tests
  already write names this way.

---

## 6. THE WIRE CORPUS — the `Corpus` anchor, in detail

This is the part that was *owed* (the seam survey: *"if the plan introduces more fakes, this is
the one place a drift mechanism is owed"*), and it is the part that reaches D3.

### 6.1 What it anchors

Two doubles and one parser, in one mechanism:

- `DodonaFakeAgent` — hand-writes `claude`'s stream-json shape, 545 lines, with **nothing** today
  keeping it in sync with what `claude` emits. If the real wire changes, every suite stays green.
- `LaneRuntime.HandleShimLine` — hand-parses the same shape. Fake and parser are two hand copies
  of one undocumented schema (`docs/ORCHESTRATOR-DESIGN.md:41`: *"the stream-json wire schema is
  not formally published"*), maintained in different projects, checked against each other by
  nothing.

### 6.2 The two tests

**`WireCorpusTests.every_recorded_line_classifies_as_recorded`** — for each `real/*.jsonl`, frame
each line as `<seq>\t<line>` (the shim's own format, DodonaShim/Program.cs:240), push it through
the real `HandleShimLine` into a `RecordingLaneSink`, and compare the resulting
(kind, presence, session, kv, row-or-no-row) tuple sequence against the checked-in
`expected/<name>.expected.json`. Change the parser → red, with the line number. Record a corpus
containing a shape the parser mishandles → red.

The seeded corpus is `spikes/spike1-output/wire.jsonl`, and **its BOM is kept deliberately** —
it is the BOM incident's own artefact, and a test asset that reproduces a real-world encoding
hazard is worth more than a tidy one.

**`FakeShapeTests.every_shape_the_fake_emits_has_a_real_witness`** — read the tracked
`fake-sample.jsonl` and, for every distinct shape in it, require a witness in `real/*.jsonl`
**unless** the shape appears in `MANIFEST.json`'s `unwitnessed[]` with a `reason`. Adding an
emitted shape to the fake without either recording one from reality or writing down why you
cannot is red.

"Shape" is the discriminating tuple the parser actually switches on:
`(type, subtype, content-block-type)` — e.g. `("system","init",null)`,
`("system","permission_denied",null)`, `("system","thinking_tokens",null)`,
`("assistant",null,"text")`, `("assistant",null,"tool_use")`, `("assistant",null,"thinking")`,
`("user",null,"tool_result")`, `("result","success",null)`, `("rate_limit_event",null,null)`.
Put the tuple type in `src/Dodona/WireShape.cs` (pure, no dependencies) so the parser, the
corpus test and the expectation files all key off one declaration.

### 6.3 Day one is a debt, not a wall — say so

MEASURED from the seed corpus: it contains **`system/init`, `system/thinking_tokens`,
`assistant/thinking`, `assistant/text`, `result/success`, `rate_limit_event` — and nothing
else.** No `tool_use`, no `user`/`tool_result`, no `permission_denied`. So `unwitnessed[]`
starts **large**, and on day one this mechanism mostly records a debt in a tracked file rather
than catching anything.

That is the honest state and it must be written into `MANIFEST.json` rather than glossed. It
becomes enforcement as the corpus grows, and it is already enforcement for the *next* shape
somebody adds. The way to shrink the debt fast is §6.4's recording being **directed** — one
session that uses a tool, fails a tool, and trips a permission denial covers most of the list in
a single real-model run.

### 6.4 `dev corpus-record` — the verb, and the staleness answer

`tools/dev.ps1 corpus-record` (a **verb on the one tool**, per `RECOVERY-PHASES` D-3, not a new
tool). It runs **one real `claude -p --input-format stream-json --output-format stream-json`
session**, driven by a scripted prompt that deliberately exercises the shape list, tees the raw
wire to `tests/assets/wire/real/<date>-<model>.jsonl`, and updates `MANIFEST.json` with
`recordedAt`, `cliVersion` (from `claude --version`), `model`, and the shapes witnessed.

- **It costs quota**, so it is exactly the kind of run §0.1 sanctions as *rare and deliberate*.
  It is never run by a suite, never by `gate`, never by a watcher. It prints what it is about to
  spend before it spends it.
- **It never runs in `dev suites`, `dev gate` or `dev test`.** A suite that made a model call
  would be the standing directive broken; the corpus test reads a file.

**Staleness, and the deliberate refusal to make it an assertion.** A time-bomb test that reddens
when the corpus passes N days old is rejected: it fails for a non-defect, on a date, it reddens
every historical commit you check out while bisecting, and a gate that is red for a reason that
is not the change teaches people to re-run instead of read — the same disease CLAUDE.md §3
diagnoses in a gate that is always green. Instead:

- **`dev lint` (I8) fails** only on a *malformed or absent* provenance: `MANIFEST.json` missing,
  unparseable, or missing `recordedAt`/`cliVersion`. That is a defect, not a date.
- **`dev gate` prints a reading**: `corpus: recorded <date> (<N> days), claude <ver>` in the
  verdict block, explicitly **outside** the assertion list — the verdict line says *"on the 10
  assertions above, and only those"* on purpose and this design does not make it eleven. A
  reading in the one place that is read once per merge is the honest weight for a fact nobody can
  turn into a defect.

### 6.5 `dev wire-sample` and lint row I9

`DodonaFakeAgent.exe --wire-sample -` prints one instance of every shape it can emit. `dev
wire-sample` writes it to `tests/assets/wire/fake-sample.jsonl`; **lint row I9** regenerates and
compares, and goes red naming `dev wire-sample` when they differ. If the binary is not built,
I9 reports `SKIPPED (not built)` — lint must keep running on a tree that will not compile.

This is what makes `FakeShapeTests` un-forgettable: edit the fake to emit a new shape and forget
to re-sample, and lint (sub-second, asserted by `gate`) is red before the fake's new shape ever
reaches a suite.

---

## 7. THE CLOCK

### 7.1 No `IClock`. Time is a parameter.

The house pattern is already decided by `Trees.Locate` and by `Poller.Liveness(presence, state,
lastSeen, DateTime now)` (Poller.cs:65) — **which already takes `now`.** Injecting a value with a
defaulted overload binding the real source keeps production on exactly one path and creates
nothing to drift. An `IClock` interface would be a *second* style, would need its own
`[Double]`, its own anchor, and would buy nothing the parameter does not.

**Rule: time enters as `DateTime nowUtc`, with a defaulted overload binding `DateTime.UtcNow`.**

The one place a whole object is cheaper than threading a parameter is `Store`, which stamps
timestamps internally at four sites: `Now()` (Store.cs:720), `Expired(TokenRow)` (:1404), and the
two `$exp` lease writes (:1462, :1499). Give `Store` an optional constructor parameter
`Func<DateTime>? clock = null` defaulting to `() => DateTime.UtcNow` and use it at those four
sites. One field, one defaulted parameter, one production path.

### 7.2 What that removes

- `m1:expired_lease_cannot_land` is the suite's **only real `Start-Sleep -Seconds 2`**. With the
  store clock it becomes a fixture test at `t0 + 2h` — instant, deterministic, and the sleep is
  deleted rather than shortened. (`check-authoring` forbids *shortening a deadline* to make a
  proof faster, because a check that merely passes slowly then reads as PROVEN. Deleting the
  wait by removing the need for elapsed time is a different act and is the one that is allowed.)
- `Poller.Liveness`'s bucketed elapsed clock, `quiet Nm`, and the busy predicate — already
  parameterised, needs two `internal` keywords to be reachable.
- `Poller.QuotaLine` (:159) — the 5-hour window and its `as of Nm ago` age.

### 7.3 What it does NOT remove — and this is the honest part

**A controllable clock does not touch the measured flakiness.** Issue #3's reds are
`Wait-Until` deadlines expiring because a real WPF window, a real daemon or a real process did
not respond inside 20 s **under machine load**. That is wall-clock latency of real machinery, not
test-controlled time. No clock makes a real window paint faster, and a fake clock inside the test
process cannot advance the real process it is waiting on.

The measurement that settles it is already recorded — four gates, busy → clean:

| suite | busy | clean |
|---|---|---|
| ui-use | 120.9 s | 87.5 s |
| brain | 95.4 | 79.8 |
| workspace | 87.5 | 74.1 |
| m1 | 57.5 | 46.7 |
| voice | 49.9 | 39.7 |
| m3 | 24.8 | 19.0 |
| m2 | 18.1 | 13.9 |
| concierge | 17.6 | 13.8 |
| **unit** | **5.3** | **5.4** |

`unit` starts no daemon, no shim and no window, and **it is the only one that did not move.**
Every suite that spawns processes or opens windows got 15-28 % faster idle. So the only lever on
the surviving integration flake is **fewer integration tests** — which is the wire-counting rule,
i.e. this whole job, and which issue #1 already sanctions by name (*"what remains is either
cheaper suites or #3's root cause"*).

Say it in the plan in exactly these terms, because the tempting sentence — "a controllable clock
removes the timing flake" — is false and would buy the wrong work.

---

## 8. LANGUAGE, AND THE HONEST COST OF THE REWRITE

### 8.1 The new tests are C# xUnit. Reasons, in order.

1. **The door is already open.** `src/Dodona/Dodona.csproj:23-28` grants
   `InternalsVisibleTo("Dodona.Tests")`. Every internal type — `Store`, `Daemon`, `LaneRuntime`,
   `Registry`, `Instance`, `Repos`, `Projects`, `Trees`, `Fence`, `Git`, `Ver`, `Paths` — is
   reachable today with no plumbing.
2. **The seams are C# types.** A PowerShell test reaching `HandleShimLine` would have to go
   through a compiled binary and a process, which is the exact cost this job removes.
3. **One tally contract already exists.** `Run-Unit` (dev.ps1:1054) scrapes `Passed:`/`Failed:`
   off `dotnet test` and prints the same `<N> checks, <M> failed` shape every `.ps1` prints, so
   `dev test`, `dev suites`, `dev gate` and a human all read one format. Nothing new is owed.
4. **No new packages.** Hand-written doubles only: `Moq`/`NSubstitute`/`FluentAssertions` are
   refused by the csproj's stated offline-pinning decision.

### 8.2 Moving a check from `.ps1` to `.cs` is a REWRITE. What actually survives.

Only **the name**. A `.ps1` check asserts on a store row read back through SQL, produced as a
side effect of a real binary; the `.cs` equivalent asserts on a return value or an in-process
row. The fixture changes, the assertion changes, the failure text changes. Budget it as writing
a new test with a name handed to you, not as a move.

Three specific costs an implementing agent will otherwise discover the expensive way:

- **`dev prove` REFUSES the unit suite** (dev.ps1:1178-1196) for three recorded reasons, each of
  which gives a *wrong* answer rather than no answer. So **every moved check loses `dev prove`**
  and owes the prescribed substitute: break → `dev build` → `dev test unit` → read the red →
  revert → **record the literal red text in the check's XML doc comment**, which is what
  `DictationTests`/`SpeechStreamTests` already do. Two traps travel with it: a driver that skips
  `dev build` hits P1.5's stale-build refusal and reports VACUOUS on everything, and *DID NOT RUN
  is a distinct verdict from VACUOUS*.
- **Do it per FUNCTION, not per check.** Breaking `Claims.Covers` reddens every check that reads
  it in one run, so one break and one `dev test unit` yields a whole batch of literal red lines.
  This is `dev prove <suite>:<check>`'s own insight (fifteen proofs in two suite runs instead of
  nineteen) applied to the unit layer. At the survey's 465 movable checks, that is on the order
  of **tens of breaks, not hundreds** — and it is still the single largest line item in the job.
- **Every moved assertion uses the message overload.** `Assert.True(cond, $"...")`, never bare.
  A bare `Assert.True() Failure` with no detail is issue #11's family (`FAIL []`) in a new
  costume, and `dev test unit`'s failure output is all a reader gets.

### 8.3 What stays PowerShell, permanently

Every surviving integration wire. They drive compiled binaries, real pipes, real processes and
real windows; that is what they are for, and `tests/_workspace.ps1`'s twelve functions are a
decade of hard-won Windows knowledge (`Test-DodonaPipe` using `[IO.Path]::GetFileName` rather
than `Split-Path -Leaf`, which returned empty for pipe paths and produced two false 20 s
timeouts in m0; `Wait-Until` writing its timeout notice to **stderr** because `Write-Output`
would land in the return value and make every timeout read `$true`). None of that moves.

---

## 9. WIRING INTO `tools/dev.ps1`

### 9.1 New verbs (verbs, not tools — `RECOVERY-PHASES` D-3)

| verb | what it does | cost |
|---|---|---|
| `dev names` | prints every check name in the repository, sorted, all five forms — the mechanical form of constraint 1's N-in/N-out proof (`dev names > before.txt` … `> after.txt` … `diff`) | sub-second, tracked files only |
| `dev wire-sample` | regenerates `tests/assets/wire/fake-sample.jsonl` from the built fake | one process start |
| `dev corpus-record` | **one real model call**, deliberate, operator-authorised; records a real transcript and updates the manifest (§6.4) | quota |

### 9.2 The `unit` suite's solo status is a MEASUREMENT, not a decision to take here

`unit` is solo for a **structural** reason, not flakiness: `dotnet test` builds Dodona straight
into `src\Dodona\bin`, which every other suite copies out of via `Use-TestBinaries`. Two
compilers, one directory.

There are two known escapes and the prior art records that **neither is rejected anywhere**:
`--no-build`, or `-p:BaseOutputPath=<temp>\` (which is exactly why `m4` is not solo despite
running a real build). Redirecting `BaseOutputPath` would let `unit` join the wave and would give
back the seconds it is currently serialized in front of it.

**Do not edit `SoloSuites` on that argument.** Its own comment is explicit: *"do not 'tidy up'
`SoloSuites` because a suite looks fast enough to parallelise, and do not add to it because a
suite looks risky. Both directions are a measurement."* The instruction to the implementing
agent is therefore: implement `BaseOutputPath` redirection, run **five consecutive `dev gate`s**,
and only then edit the list **with the measurement written beside it**. If it is not green 5/5,
leave the list alone and record that too.

### 9.3 The `unit` time budget, and the split that is gated on a number

The operator's requirement is explicit and `LOCATIONS-PLAN` P1.5 restates it: **`dev test unit`
is 1-2 s**, and every second added is serialized in front of the wave while it stays solo. This
design adds fixtures that touch disk (`StoreFixture`) and processes (`GitRepoFixture`).

**I have not measured either, and I will not guess.** The instruction is a measurement gate:

1. Land the pure-logic and `RecordingLaneSink` work first (no disk, no process). Measure.
2. Land `StoreFixture` (temp SQLite). Measure.
3. Land `GitRepoFixture` (shared per collection, `reset --hard` between tests). Measure.
4. **If total `dev test unit` exceeds ~4 s**, split into two suite names over the **same
   project** using xunit traits — `dev test unit` runs `--filter Category!=Fixture`, `dev test
   fixtures` runs `--filter Category=Fixture` — and add `fixtures` to `AllSuites` and
   `SuiteOrderHint`. One project, one compile, two doors.
5. Record whichever branch was taken, with the number, in `tools/dev.ps1` beside `AllSuites`.

### 9.4 Isolation rules the new layer must obey

- **Never set `DODONA_HOME` from an in-process test.** `Instance.ConciergeId` (Instance.cs:84) and
  `Instance.ShellId` (:91) are `static { get; } = Scoped(...)` — frozen at first touch — so two
  homes in one process is impossible today. Fixtures take **explicit paths**
  (`Store(path)`, `Registry(path)` after §4.3, `ConciergeStore(path)` which already does).
- **Never touch `Instance.AllPipes()`** (Instance.cs:171) from a unit test: it enumerates the
  machine-global `\\.\pipe\` directory, shared with the operator's live session.
- **Never construct more than one WPF window per process** if a UI test project is ever added:
  `Application.Current` is a per-process singleton and `MainWindow.TestWindow` is `static`.
- `Assert-NoBuildOutputProcesses` stays **per suite**, last in each `finally`
  (`tests/_workspace.ps1:353`). It is harness hygiene, one row per suite process, and cannot be
  deduplicated across suites however they are reorganised.

### 9.5 The skill question

`check-authoring` gets a new section: what an anchor is, which anchor a new double needs, and
that `Wire` is mandatory. **Do not write a fourth trap skill** — CLAUDE.md §5.1 D-6 forbids it by
name, and a "how to write a fake" skill is squarely a trap skill. If it ever turns out the
section is skipped as reliably as §7 was, D-6's own instruction applies: promote it to
enforcement and delete the prose. Most of it already is enforcement here.

---

## 10. THE FIVE CONSTRAINTS, CHECKED AGAINST THIS DESIGN

1. **No coverage lost.** Nothing here deletes a check. Doubles are *added alongside*; the wire
   checks the survey identified as survivors keep their names, and §3.4's `Wire` field makes
   deleting one of them a **red build** rather than a silent choice. `dev names` gives the
   N-in/N-out proof a command, and `Every_check_name_is_unique` closes the duplicate-name hole
   that could have made the proof lie.
2. **A fake that drifts fails loudly.** §3, and the three anchors of §2. The mechanism is a
   reflection test, not a convention; the escape hatch is a tracked reason string, not a comment.
3. **Every behaviour keeps an operator-path wire.** Enforced, not promised: `Wire` is a required
   attribute argument and its check must exist. `publish:no_provenance_daemon_refuses_to_guess`,
   `brain:typed_input_reaches_the_classifier_autostart_made` and
   `workspace:the_project_ladder_is_live_on_the_path_the_operator_uses` — the three checks that
   run with autostart cleared and nothing pre-built — are named as `Wire` values and cannot be
   removed silently.
4. **Windows / PS 5.1.** The only new `.ps1` work is three `dev` verbs and one lint row. They
   obey the file's own traps: `@(...)` around one-element pipelines, `ConvertFrom-Json` landed in
   a variable before filtering, `$procId` not `$pid`, native stderr collapsed with
   `-replace '\s+',' '` before matching, `.ps1` written **UTF-8 BOM + CRLF** and parse-checked
   (a `.ps1` that fails to parse never reaches `finally`), `.cs`/`.md` CRLF no BOM, and no
   non-ASCII literal in any BOM-less `.ps1` (lint I8 does not catch that — P1.8).
5. **Operator standing directives.** No suite gains a model call or a microphone:
   `DODONA_UI_MIC=off` and the three locks in `Use-IsolatedDodonaHome` stay untouched, and
   `dev corpus-record` is a hand-run verb that no suite, watcher or gate may invoke. Nothing
   automatic is widened. The gate gains a **reading**, not an eleventh assertion. The I7 budget
   is not raised — *"FIX A RED, RAISE FOR GROWTH, and never the other way round"* (dev.ps1:1699).

---

## 11. REJECTED, WITH REASONS (so they are never re-proposed)

| rejected | reason |
|---|---|
| **`IStore` + an in-memory store** | ~70 public members, and the properties worth testing are the transaction boundaries an interface erases. `LandCommit` (Store.cs:1525) re-checks holder and lease *inside* the tx that lands the ticket; a double reimplements that as sequential writes and proves itself. |
| **Contract tests as *the* mechanism** | The highest-drift-risk double (`DodonaFakeAgent`) has a real counterpart that costs quota and is nondeterministic, so its contract body can never run against the real subject. Kept as one anchor of three. |
| **A Roslyn analyzer or source generator** | Enforces nothing a ~120-line reflection test does not, adds a package to a deliberately offline-pinned project, and puts a dependency on the compile path that `dev check` must survive without. |
| **Colocation as the anti-drift mechanism** | It is a convention. `ILaneSink` is safe because **both implementers ship**, not because they are near each other. Kept as a layout rule only. |
| **A test that reddens when the corpus is older than N days** | Fails for a non-defect, on a date; reddens historical commits under bisect; teaches re-running instead of reading. Replaced by a lint failure on *malformed provenance* and a gate **reading** of the age. |
| **An in-process double for the agent** | Would delete the real shim, the real named pipe and `LaneRuntime`'s entire reason to exist, turning every m0/m1 check into a claim about nothing. `DodonaFakeAgent` stays a real process. |
| **Faking `Registry` uniqueness in a `HashSet`** | A different enforcement mechanism passing a test written about a partial UNIQUE index. CLAUDE.md §5: a red exclusivity check is a correctness incident. |
| **Faking `Git.Run` with canned stdout** | Proves the parser, not the operation. `SilentDrops` exists to catch a merge that **succeeded** and lost work — a canned success is the exact input that cannot catch it. |
| **A shared or pooled daemon fixture across suites** | `RECOVERY-PHASES` D-2: *"the way to remove startup is to NOT NEED A PROCESS (P4.5), never to share one."* |
| **A fourth trap skill ("how to write a fake")** | CLAUDE.md §5.1 D-6 forbids a fourth trap skill by name. Extend `check-authoring`. |
| **`Moq` / `NSubstitute` / `FluentAssertions`** | The test csproj is version-pinned to the machine's package cache on purpose: *"adding a verification step that needs the network is adding a way for verification to be unavailable exactly when it is wanted."* |
| **Editing `SoloSuites` on the strength of `BaseOutputPath` reasoning** | The list's own comment: both directions are a measurement. Implement, measure five gates, then edit **with the number**. |
| **Transliterating `.ps1` checks into `.cs`** | Impossible: the assertion, the fixture and the failure text all change. Only the name survives. Budget a rewrite. |
| **An `IClock` interface** | A second style beside the house's injected-value pattern, and it would need its own double and its own anchor. `Poller.Liveness` already takes `now`; `Trees.Locate` already shows the shape. |
| **Two `DODONA_HOME`s in one test process** | `Instance.ConciergeId`/`ShellId` freeze at first touch (Instance.cs:84/:91). Fixtures take explicit paths instead. |
| **Quoting DESIGN §17 (*"a test asserts on events and decisions, not on internals"*) to block this** | It must be argued rather than ignored, and the counter is already in-tree: `Daemon.ClaudeArgs` and `Projects.PromptDirMismatch` were made `internal static` *precisely because* **no acceptance suite can see them** (the fake agent takes no claude flags, so `IsClaude` is false for it). §17's *"the view is dumb, so tests inject the message"* is likewise partly superseded by CLAUDE.md §3.1: five lane actions were **unreachable**, not merely untested. Do not cite §17 to replace a UI wire check with an injected message. |

---

## 12. RISKS, AND WHAT THIS CANNOT CATCH

1. **The corpus starts mostly empty, so day one the mechanism records a debt.** MEASURED: the
   seed has no `tool_use`, no `tool_result`, no `permission_denied`. Until `dev corpus-record`
   runs a directed session, `unwitnessed[]` carries most of the shape list. Named in
   `MANIFEST.json`, not glossed.
2. **Nothing catches a shape `claude` starts emitting that we neither emit nor recorded.** That
   is D3 with no recording, and it is unfixable by any automatic mechanism. Its only mitigation
   is that the *parser* has a `catch` that leaves an unparseable line as `kind=wire, body=raw`
   (LaneRuntime.cs) rather than dropping it — so a new shape degrades to a visible raw row rather
   than to silence. Worth one check asserting exactly that.
3. **The ledger is itself a check and is worth nothing until seen red.** Prove it by adding an
   unanchored `FakeThing` and reading the failure; record the literal red in its doc comment.
   Same for `Every_double_names_a_wire_that_still_exists` (rename a wire check) and
   `Every_check_name_is_unique` (duplicate a name).
4. **`Anchor.Landing` is genuinely weak.** `RecordingTransport` proves which message would be
   sent, not that anything carries it. It is tolerable only because `Wire` is mandatory and
   enforced. If a second `Landing` double is ever proposed, the burden is to show why the real
   counterpart cannot run in-process at all.
5. **The `unit` budget.** Fixtures touching disk and git may push `dev test unit` past the
   operator's 1-2 s. Unmeasured. §9.3 is a measurement gate, not a guess.
6. **The proof obligation does not shrink.** ~465 moved checks each owe a recorded red, and
   `dev prove` refuses to help. §8.2's per-function batching is the only lever, and it is a real
   multi-session cost that must be in the plan's estimate rather than discovered.
7. **`DODONA_UI_MIC=async` (§4.10) could add flake to `voice`** — the suite with the live flake
   signature. Gated on 5/5 alone and 3/3 in a wave; abandoned with a written exemption otherwise.
8. **This design does not make the surviving integration tests less flaky.** Only having fewer of
   them does (§7.3), and that is the wire-counting rule's job, not this document's.

---

## 13. IMPLEMENTATION ORDER (each step ends green and independently useful)

| # | step | seam cost | proves itself by |
|---|---|---|---|
| 0 | `CheckLedger.cs` + `dev names` + `Every_check_name_is_unique` | none | duplicate a check name → red. Gives constraint 1 its proof machinery **before** anything moves. |
| 1 | `DoubleAttribute` + `DoubleLedgerTests` (three facts) | none | add an unanchored `FakeThing` → red |
| 2 | `LaneRuntime.HandleShimLine` `private`→`internal`; `RecordingLaneSink`; `LaneSinkContract` over real `Store` + the double | 1 keyword | write the naive `list.Count` sink → the dedup contract case is red |
| 3 | `WireShape` + seed corpus from `spikes/spike1-output/wire.jsonl` + `WireCorpusTests` + `MANIFEST.json` | none | change a `case` in `HandleShimLine` → red at a line number |
| 4 | `--wire-sample`, `dev wire-sample`, lint I9, `FakeShapeTests` | ~30 lines in the fake | add an emitted shape to the fake → lint red, then `FakeShapeTests` red |
| 5 | `Store` `:memory:` guard (Store.cs:33) + `StoreFixture`; `Registry(string path)` overload + `RegistryFixture`; `Store` clock parameter (four sites) | ~10 lines | **measure `dev test unit`** |
| 6 | `Daemon` ctor + `HandleAsync` `internal`; `DaemonFixture` over a `MemoryStream` | 2 keywords | drive a command shape in-process; the pipe wire stays |
| 7 | `Poller.Liveness`/`QuotaLine` `internal static`; injected fs predicates at the five S10 sites | ~30 lines | `TreesTests` pattern |
| 8 | `Poses_cover_every_snapshot_member` + `PoseCoverage.Exempt` (needs the UI test project) | 1 csproj + `InternalsVisibleTo` | add a `Snapshot` member no pose sets → red |
| 9 | `BaseOutputPath` redirection; **five `dev gate`s**; then and only then edit `SoloSuites` **with the number** | 1 line | the measurement |
| 10 | `dev corpus-record`, run once, directed; shrink `unwitnessed[]`; `check-authoring` gains the anchor section **in the same commit** | one deliberate model call | the manifest's shape list |

Steps 0-4 are the mechanism and cost almost no production change (**one keyword** and one argv).
They can land before a single check moves, which is the right order: the ledger must exist
before the population it polices does.

---

*Conventions for whoever turns this into a repo document: `.md` here is CRLF with no BOM; `.ps1`
is UTF-8 BOM + CRLF; any `.md` naming a `tests\*.ps1` that does not exist yet must mark it
`(planned)` or lint row I8 goes red.*
