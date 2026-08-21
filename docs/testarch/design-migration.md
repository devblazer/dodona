# THE MIGRATION MECHANICS

How ~1050 checks are rearranged into a handful of wire tests plus a large pure-logic layer,
without losing one, in a way a subagent can execute and a reviewer can verify with one command.

This document is the procedure. It makes no claim about WHICH checks move -- that is the six
survey tables at `scratchpad/testarch/survey-*.md`, which are the authority on classification.
This is the machinery that carries them across, and the proof they arrived.

Everything below is written so an implementing agent needs no further decision from a human.
Where a judgement was made, the rejected alternative is recorded WITH ITS REASON (CLAUDE.md 0.1),
so it is never re-proposed.

---

## 0. THE ONE-SENTENCE SHAPE

Three checked-in text files (`baseline.tsv`, `wires.tsv`, `moves/<slice>.tsv`), one new verb
(`dev ledger`), one extension to an existing verb (`dev prove --with`), and a fixed six-step
slice ritual whose exit condition is that both the OLD check and the NEW check have been seen
RED under the same injected defect.

The ledger is the artefact. `dev ledger` is the command a reviewer runs. `dev prove --with` is
the answer to "a refactor is VACUOUS by construction".

---

## 1. THE LEDGER

### 1.1 Why the ui-use precedent does not generalise, and what survives of it

`3b235ab` (the ui-use split) proved faithfulness like this, in its own words:

> THE PROOF THAT A SPLIT IS FAITHFUL IS THE CHECK-NAME LIST, not `dev prove`. 130 names went
> in and 130 identical names came out -- ZERO lost, ZERO added, ZERO renamed, diffed against
> the monolith's own results.json from a green run at ec28986.

**The idea generalises. The mechanism does not.** Three reasons, each fatal on its own:

1. **The names change.** A `.ps1` check is `status_does_not_summon_a_daemon` in a hashtable; a
   C# test is `Dodona.Tests.NoSummonTests.status_does_not_summon_a_daemon` in a TRX. A sorted
   set-diff of two lists that are not in the same namespace answers nothing. Identity has to be
   *declared* and then *verified*, not assumed.
2. **The layer changes, so an equal name is no longer an equal assertion.** ui-use moved bodies
   as BYTES -- "every original line 114-1171 is carried by exactly one new suite, verified
   line-for-line". Here the body is rewritten. A name that survives into a unit test can be
   green while asserting something strictly weaker, and a name list cannot see that. This is
   the whole risk of the job and it needs a different instrument (section 3).
3. **The old side evaporates.** `tests/*-output/` is gitignored (`.gitignore:19`), so
   `results.json` is a run artefact, not a record. ui-use could diff against "the monolith's own
   results.json" because the monolith had just run. Six waves into this job there is nothing to
   diff against unless the baseline was *frozen and checked in* at the start.

What survives, and is adopted wholesale: **the check-name list is the proof of faithfulness, and
`dev prove` saying VACUOUS on a pure move is CORRECT rather than skipped, and must be said out
loud.**

### 1.2 The three files

All live in `tests/ledger/`. They are **TSV, ASCII-only, CRLF, no BOM**.

- TSV not JSON, deliberately: `ConvertFrom-Json` emits a JSON array as ONE pipeline item
  (CLAUDE.md 0.2), a trap that has already turned three acceptance checks into silent no-ops in
  this repo. ``Import-Csv -Delimiter "`t"`` yields rows as objects with no such edge. Check names
  contain no tabs (they are `[A-Za-z0-9_.]+`), so the delimiter is unambiguous.
- ASCII-only and asserted by the tool, because Repo-Lint's known gap P1.8 is exactly non-ASCII
  in a BOM-less file read by PS 5.1 -- an em dash in a ledger row would match nothing and drop a
  row silently. `dev ledger` refuses a non-ASCII byte in its own files and names the line.
- The reader strips a leading U+FEFF defensively before parsing. That is the GateHook incident
  (CLAUDE.md 3): `Console.In` and `Get-Content` both hand a BOM back as an ordinary character,
  and PS 5.1 writes them by default.

#### `tests/ledger/baseline.tsv` -- the frozen census

```
# baseline captured at <sha> on <date> by: dev ledger --capture
# 1050 names. THIS FILE ONLY EVER GROWS BY CAPTURE. It is never edited by hand.
suite	check
m0	status_does_not_summon_a_daemon
m0	session_id_recorded
compression	blocked_uses_the_fixed_schema
unit	Dodona.Tests.TreesTests.A_write_in_the_shared_checkout_is_the_refused_case
...
```

Captured by `dev ledger --capture` from a **green** full run: every suite's
`tests/<suite>-output/results.json` keys, plus every executed test name from the unit run's TRX.
Runtime capture, not a static parse of the sources, for the same reason `dev.ps1`'s tally is the
authority: a name that exists in the source but never ran counts as nothing, and the repo has
already been bitten by both halves (m0 never printed a tally in its life; ui-use reported
"115 checks, 0 failed" that were 115 of 121).

**The integrity property that makes the whole ledger worth anything:** `dev ledger` compares
`tests/ledger/baseline.tsv` against `git show HEAD:tests/ledger/baseline.tsv` and **refuses any
run in which a baseline row was removed or altered**. Only appends by `--capture` are legal, and
`--capture` refuses to write unless the run it read was green. Without this, the verdict is
green-able by deleting a row, which is the one edit nobody would notice in a 1050-line file.

#### `tests/ledger/wires.tsv` -- the wire register

One row per DISTINCT wire in the whole repo. Built before any check is deleted (section 4).

```
wire_id	owner_suite	owner_check	what_it_proves	why_real_machinery
W-SPAWN	m0	daemon1_killed_mid_turn	lane-start makes a live shim+agent pair with a record on disk	two real processes; every other check treats it as a fixture
W-GATEHOOK	m1	gate_denies_a_ticket_lane_writing_its_claim_in_the_shared_checkout	a PreToolUse subprocess fed JSON on stdin denies a shared-checkout write	separate process, real stdin, exit protocol
```

`owner_suite`/`owner_check` name the ONE check that survives to prove that wire. Every other
integration check that rides it points at `wire_id` and is dispositioned `merged`.

#### `tests/ledger/moves/<slice>.tsv` -- one file per slice

**One file per slice is the entire answer to subagent collision on the ledger.** Two subagents
never edit the same file, so there is no merge conflict, and `dev ledger` reads the directory.

```
old_suite	old_check	disposition	destination	wire	mutation	red_old	red_new	note
m0	session_id_recorded	moved	unit:Dodona.Tests.ShimWireTests.session_id_recorded		tests/mutants/wire-01.patch	session_id_recorded: FAIL got= want=fake-	ShimWireTests.session_id_recorded [FAIL] Assert.Equal() Actual: (null)
m0	daemon1_killed_mid_turn	kept			W-SPAWN				it is W-SPAWN's owner
m0	shim_record_dies_with_the_shim	merged	suite:m0:shim_exits_when_its_agent_dies	W-SHIMEXIT				downstream consequence of the same exit
m2	a_pr_repo_still_promotes_a_refused_write	vacuous-guard	unit:Dodona.Tests.DeliveryTests.a_pr_repo_still_promotes_a_refused_write					vacuous vs HEAD by construction and the suite says so; kept and LABELLED (R7 precedent)
m4	published_build_carries_its_commit	stays		W-STAMP				process-fact: the value only exists after MSBuild stamps a real assembly
```

Columns, and what the tool enforces about each:

| column | rule enforced by `dev ledger` |
|---|---|
| `old_suite`,`old_check` | must appear in `baseline.tsv`; must appear exactly ONCE across all `moves/*.tsv` |
| `disposition` | one of `moved` `kept` `merged` `stays` `vacuous-guard` `renamed` -- a closed vocabulary, so it cannot become a shrug |
| `destination` | `moved`/`renamed`/`vacuous-guard`-that-moved: `unit:<FQN>`; `merged`: `suite:<suite>:<check>`, and that check must itself be a `kept` row or a `wires.tsv` owner |
| `wire` | REQUIRED for `kept`, `merged`, `stays`; must exist in `wires.tsv` |
| `mutation` | REQUIRED for `moved`; the patch file must exist under `tests/mutants/` |
| `red_old`,`red_new` | REQUIRED for `moved`; both non-empty. The literal observed failure lines (section 3) |
| `note` | REQUIRED for `stays`, `vacuous-guard`, `renamed`, and for any `merged` whose survivor is in a different suite. For `stays` it must BEGIN with a word from the closed reason vocabulary (section 11) |

**The last-segment rule, and it is what makes the mapping self-verifying:** for
`disposition=moved`, the final dotted segment of `destination` MUST equal `old_check` character
for character. So `m0:session_id_recorded` becomes a C# method literally named
`session_id_recorded`. `dev ledger` asserts this, so a typo in the ledger cannot silently orphan
a name -- the tool catches it without having to trust the ledger's author.

This breaks the existing `Sentence_case_with_underscores` habit in `tests/Dodona.Tests`. Accepted:
the method name becomes self-documenting ("this is the old m0 check of that name"), and the
alternative is worse.

- **Rejected: free renaming with the ledger as the only link.** Loses because the ledger then
  becomes the sole witness of the correspondence, and one mistyped row silently drops a name with
  nothing able to detect it. `renamed` survives as an escape hatch for the rare case where the old
  name is actively wrong -- survey-daemon names three (`m0:landed_exactly_once` does not prove what
  its name says; `compression:overlay_keeps_midturn_and_full_text` never reads the overlay;
  `m0:daemon1_killed_mid_turn` asserts nothing about the kill) -- and a `renamed` row REQUIRES a
  `note` saying why the old name was wrong.

#### `tests/ledger/added.tsv` -- growth, declared

Names that exist in a live run and in no baseline row. Every one is declared here with a one-line
reason, so coverage GROWTH is visible arithmetic rather than noise that could hide a loss. This is
what let `3b235ab` say "Total check rows went 130 -> 133 ... Repo total 847 -> 850".

### 1.3 `dev ledger` -- the one command

A verb on `tools/dev.ps1`, not a new tool (RECOVERY-PHASES D-3: dev.ps1 is the one door).

```
dev ledger                  # STATIC. sub-second. the gate row.
dev ledger --live           # additionally consumes the artefacts of a just-finished run
dev ledger --slice <name>   # that slice's rows and their proof state
dev ledger --capture        # freeze/extend baseline.tsv from a GREEN full run
dev ledger --verdict        # the block in section 10
dev ledger --origin <check> # git log -S over tests/: which commit deleted it, and its body
```

**Static mode** asserts, in this order, stopping at the first class of failure:

1. Encoding: every `tests/ledger/*.tsv` is ASCII-only, CRLF, no BOM.
2. Baseline integrity: `baseline.tsv` has not shrunk or been altered vs `HEAD`.
3. Accounting: every baseline name appears in exactly one `moves/*.tsv` row, OR is still live in
   its suite (no row = still live is the DEFAULT, so an untouched suite needs no rows at all).
4. Every column rule from the table above, including the last-segment rule.
5. Reachability: for `moved`, the named C# method exists -- a source scan of
   `tests/**/*.cs` for `void <name>(` with a `[Fact]`/`[Theory]` within the preceding 6 lines.
   For `kept`/`merged` survivors, the named check name still appears in the named
   `tests/<suite>-acceptance.ps1`.
6. Wire register: every `wire` value resolves; every `wires.tsv` owner is a live check.
7. Prints the verdict block.

**Live mode** adds: every name the ledger says is LIVE actually appears in the corresponding
`results.json` / TRX from the most recent run, and every name in those artefacts is accounted for
by `baseline.tsv` or `added.tsv`.

Live mode catches the two silent-loss shapes the runner explicitly cannot catch today:
`dev.ps1:584` records "Nothing anywhere knows how many a suite should have run, so the count
cannot be the guard" (the run that reported a clean 115 of 121), and prior art records that
`$results` is a hashtable, so a **duplicate check name silently overwrites and the tally drops by
one, with nothing detecting it**. The ledger is a name-set oracle, not a count, so both are caught.

**This is not a contradiction of `dev.ps1:584`, and the difference matters.** That comment says
the COUNT cannot be the guard *for the runner*, and it is right: the runner has no oracle. The
ledger IS an oracle; it compares names rather than counts; it is a static file the suites do not
generate; and it exists for a different job. The `FullyQualifiedErrorId` scan stays exactly as it
is -- it catches a different failure (an error that tore out of the try) and it catches it in
suites that have no ledger rows at all.

**Where it runs.** `dev ledger` (static) becomes one gate row, alongside I8 Repo-Lint, which is
already a sub-second static gate row -- same class, same cost, same once-per-merge moment. It is
NOT added to `dev test`, and NOT added to `dodona.json`'s `//verify` block: CLAUDE.md 0.1's
standing directive is that an automatic reader is not widened. `--live` is run by a slice owner
and by a reviewer, by hand.

---

## 2. THE MUTATION PATCHES

`tests/mutants/<slice>-NN.patch` -- a unified diff against `src/` ONLY, checked in.

A mutant is a NAMED DEFECT: reverse a rung order, drop a guard, return the wrong branch, delete a
`Store.PaneCompressed` call. One patch may carry several mutations as long as each targets a
DISJOINT production function and each is annotated in the header. A batch of disjoint mutations
costs ONE build instead of N, and it is strictly stronger than one-at-a-time: it also detects an
over-broad test (one that reddens on a mutation it should not care about).

Header, required, parsed by `dev prove --with`:

```
# mutant: wire-01
# expects-red: unit:Dodona.Tests.ShimWireTests.session_id_recorded
# expects-red: m0:session_id_recorded
# expects-green: unit:Dodona.Tests.ShimWireTests.thinking_tokens_write_no_row
# defect: LaneRuntime.HandleShimLine no longer calls sink.LaneSession on system/init
```

`expects-green` is optional and is the over-broad detector: if a test that should not care about
this defect goes red, the tool says so and the slice is not done.

**The patch is checked in.** That is the difference between "someone once saw it red" and "anyone
can see it red again in forty seconds". It is also what a reviewer runs.

- **Rejected: keep mutations out of the repo and only record the red text in a comment.** That is
  today's practice for the unit suite (`DictationTests`/`SpeechStreamTests` carry their literal red
  text) and it is fine for one check at a time. Over ~450 moves it makes every claim unverifiable
  except by trusting 450 comments, which is the "a property claimed in prose is not enforcement"
  failure CLAUDE.md 3 records at its own expense. The comments stay too; the patch makes them
  re-runnable.
- **Rejected: an automated mutation-testing framework (Stryker.NET or similar).** New package, new
  network dependency at verification time, and `tests/Dodona.Tests.csproj` is version-pinned to the
  machine's package cache ON PURPOSE -- "adding a verification step that needs the network is
  adding a way for verification to be unavailable exactly when it is wanted". A hand-written patch
  is also the better artefact: it names ONE defect a person chose, which is what makes the red
  readable.

---

## 3. `dev prove --with` -- THE ANSWER TO "VACUOUS BY CONSTRUCTION"

This is the section the plan turns on. State the problem exactly:

- `dev prove` builds HEAD (the code WITHOUT your change), copies your working `tests/` over it,
  runs the suite, and demands the named check FAILS. It judges PRODUCT code.
- A test migration changes NO product behaviour, so `dev prove` on a moved check says **VACUOUS**,
  correctly. `3b235ab` already had to say exactly that out loud about all four ui suites.
- Worse: a NEW unit test asserting EXISTING behaviour is vacuous *by definition*. HEAD contains the
  behaviour, so HEAD passes it. There is no red to see.
- And `dev prove` REFUSES the unit suite outright (`dev.ps1:1178-1196`) for three recorded reasons,
  each of which gives a WRONG answer rather than no answer.

This repo's culture rests on a check having been seen red. So the substitute has to produce a red,
mechanically, for both a `.ps1` check and a C# test, on a tree where the product is correct.

**The substitute: prove against HEAD-PLUS-A-NAMED-DEFECT.**

```
dev prove --with tests/mutants/wire-01.patch m0:session_id_recorded
dev prove --with tests/mutants/wire-01.patch unit:Dodona.Tests.ShimWireTests.session_id_recorded
```

Semantics: build HEAD in the cached prove worktree, **apply the patch to `src/`**, copy the
working tree's `tests/` over it, run the named suites, demand every named check is RED. Identical
verdict vocabulary (PROVEN / VACUOUS / MISSING) and identical exit codes.

`dev prove` already contains almost all of this. The delta, concretely, against `tools/dev.ps1`:

1. Parse `--with <path>` out of `$Rest` before the suite:check loop (dev.ps1:1144).
2. The dirty-tree guard at dev.ps1:1196 (`src and tests are identical to HEAD, so there is no
   change to prove`) is SKIPPED when `--with` is given: the patch *is* the change, and at step B0
   of a slice the working tree is legitimately clean.
3. After `Copy-Item "$repo\tests\*" "$wt\tests\"` (dev.ps1:1279): `git -C $wt apply --check` then
   `git -C $wt apply`. Abort loudly if it does not apply, and abort if it touches anything outside
   `src/` -- a mutant that edited `tests/` would be proving the test against itself, which is
   reason 3 of the existing unit refusal in a new costume.
4. `$projects` (dev.ps1:1290) gains `tests\Dodona.Tests\Dodona.Tests.csproj` **only when a `unit:`
   pair is present**. The existing comment explaining why it is normally excluded stays and gains
   one sentence: with `--with`, the unit project is the subject, and every new symbol it names
   belongs to the SEAM COMMIT, which section 5's ordering guarantees is already in HEAD.
5. The `unit` refusal at dev.ps1:1216 stays for the bare form and gains one line pointing at
   `--with`. Its three reasons are each answered: (1) a new symbol will not compile against HEAD --
   answered by the seam commit landing first; (2) there is no `tests\unit-acceptance.ps1` --
   answered by routing `unit` to `Run-Unit` rather than `Start-Suite`; (3) `Run-Unit` tests `$repo`,
   i.e. the change measured against itself -- answered by `Run-Unit -Root $wt`, a one-parameter
   change.
6. `Run-Unit` gains `--logger "trx;LogFileName=unit.trx"` and a results directory, so per-test
   verdicts are readable (the ledger needs this anyway). Keep the existing `[FAIL]` grep and the
   tally-is-authority rule exactly as they are: dev.ps1:1071 records that the first version of that
   function printed "54 checks, 1 failed" and returned exit 0, and nothing here may make that
   possible again.

**Unverified detail the implementer must settle in the pilot, and it is the only one:** I did not
run `dotnet test`, so I have not seen this machine's TRX `testName` format. The plan assumes
`Namespace.Class.Method` for a `[Fact]` and `Namespace.Class.Method(arg: v)` for a `[Theory]` row,
normalised by cutting at the first `(` and de-duplicating. **Slice 0 verifies this before anything
else and records the observed format in `tests/ledger/README.md`.** If it differs, the fallbacks in
order are: `dotnet test --list-tests` (which loses on its own merits -- it lists tests that EXIST,
including a `[Fact(Skip=)]`, where the whole point is what RAN), then parsing the console
`[FAIL]`/`[PASS]` lines at `-v n`. Do not proceed past slice 0 with an unverified name format.

**What this does NOT do, said plainly:** it does not prove the new unit test asserts *everything*
the old check asserted. It proves both fail on the same named defect. A check that asserted three
things and now asserts one still passes this. That gap is closed by the agent reading the old body
-- which is why `dev ledger --origin <check>` exists (it runs `git log -S` over `tests/` and prints
the deleting commit and the original lines), and why the six survey tables, not this document,
decide what each check was really asserting.

---

## 4. STEP ZERO: THE WIRE REGISTER (before any check is deleted)

The counting rule -- one integration test per wire -- needs a list of wires spanning the whole
repo, because the six surveys each counted wires within their own group and they overlap. Named
cross-group duplicates the surveys already found:

- the gate-hook subprocess wire: owned by m1, re-proved in m2 (`the_write_is_still_refused`)
- the live-window render wire: owned by ui-grid/m3, re-proved in compression and workspace
- the real-git-mutation wire (worktree add / ff merge / repo-init): four separate wire rows across
  m1, m2, m3 and workspace; survey-identity says outright they are "three distinct production
  paths but ONE kind of machinery"
- the child-agent-spawn wire: m0, m1, m3, workspace and brain each claim a version of it
- `no_process_left_in_the_build_output`: 15 rows, harness hygiene, ONE per suite by construction
  and NOT deduplicable

So: **the first deliverable of the whole job is `tests/ledger/wires.tsv`, merged by hand from the
six survey `wires` arrays, cross-group duplicates collapsed, one owner named per row.** It is a
reading exercise over files that already exist -- no code, no build, no suite run -- and it is what
makes every later `merged` row legal.

**Do not pick a target number for the surviving integration count. Derive it.** The target is
`rows(wires.tsv) + one harness row per surviving suite`. The six surveys list 109 wire rows before
cross-group dedup; after collapsing the duplicates above the honest expectation is well below that,
but I did not do the merge and will not invent the number. `dev ledger` prints the derived target
and the stop condition compares against it.

---

## 5. THE SLICE: TWO COMMITS, SIX STEPS

A slice is the unit of work, of parallelism, and of rollback. It is exactly two commits and it
never leaves the tree in a state where coverage is unaccounted for.

### Commit A -- the SEAM commit. Production code only.

Touches `src/` only. No test file changes, no ledger changes. Makes the thing reachable:
`private` -> `internal`, an extracted `static` decision function, or an injected `Func<string,bool>`
probe with a convenience overload binding the real one (the `Trees.Locate` pattern at
`Trees.cs:44` + `:77`, where production keeps exactly ONE path).

Verification of commit A, and it is the only verification it can have:

- `dev build`
- `dev test unit <every suite the slice owns>` -- ALL GREEN. **That is the behaviour-preservation
  proof**: every existing check still runs and still passes over the refactored code.
- `dev ledger` -- unchanged and green (no name has moved).
- `dev prove` is NOT run, and its absence is stated in the commit message: a seam commit is
  behaviour-neutral by intent, so there is nothing for prove to judge. Saying it out loud is
  check-authoring 1 and the `3b235ab` precedent.

### Commit B -- the MOVE commit. Tests and ledger only.

Six steps, in this order. The order is load-bearing: **the old check must be seen red BEFORE it is
deleted**, because after deletion there is nothing left to see.

**B0. Write the mutant, and redden the OLD checks with it.**

```
dev prove --with tests/mutants/<slice>-01.patch m0:session_id_recorded m0:a_missing_shim_is_named_not_guessed
```

Every named old check must come back PROVEN. Record each red line verbatim -- it becomes `red_old`.

If an old check will NOT go red under any mutation you can write against the function it is
supposedly about, **stop and classify it**, do not force it:

- it is vacuous by construction. The repo has several, deliberately -- `m2:a_pr_repo_still_promotes_a_refused_write`,
  `workspace:naming_a_project_costs_no_model`, `voice:mic_off_opens_no_socket`'s socket half,
  `ui-shell:the_window_outlives_its_daemon`. Disposition `vacuous-guard`: keep it, LABEL it, say so
  in the note. R7's precedent is exact: 18 checks shipped, 14 seen red, the 4 vacuous ones kept and
  labelled.
- or it asserts something other than what its name says, in which case fix the aim in a SEPARATE
  commit before moving it, or move it as `renamed` with the note.

**B1. Write the new tests.** One C# method per old check, named exactly the old check name.
Variations within a check become `[Theory]` rows on that method. Never fold two old names into one
method -- if two old names truly assert the same thing, one of them is a `merged` row.

**B2. Redden the NEW tests with the SAME mutant.**

```
dev prove --with tests/mutants/<slice>-01.patch unit:Dodona.Tests.ShimWireTests.session_id_recorded ...
```

All PROVEN, and no `expects-green` method red. Record each red line -- it becomes `red_new`.
**This is the paired red, and it is the plan's core claim: the new check catches the defect the old
check caught.**

**B3. Delete the old checks** from the `.ps1`, and delete any fixture setup they were the only
consumer of. Leave the wire's owner check and its fixture standing.

**B4. Write the ledger rows** into `tests/ledger/moves/<slice>.tsv`.

**B5. Verify and commit.**

```
dev build
dev test unit <the suites this slice owns>      # green
dev ledger --live                               # accounting closed; everything declared-live ran
dev lint                                        # after any scripted edit
```

The commit message states, in the house style: what moved, the mutant and both reds, that
`dev prove` without `--with` is VACUOUS on this commit and why that is correct, and the arithmetic
(N names in, N out, M merged into named survivors).

### Where the work happens

Every slice runs in its own worktree: `dev worktree <slice>` -> `.claude\worktrees\<slice>`, with
its own `bin` and `obj`. This is CLAUDE.md 0.0 and it is enforced -- the `pre-commit` hook aborts
any commit made from the shared checkout, so an agent that forgets is stopped at the moment that
matters rather than reminded in advance.

---

## 6. ORDER OF WORK

### The pilot: `S-WIRE` -- the shim wire parser. One agent. Nothing else runs.

**`LaneRuntime.HandleShimLine` (LaneRuntime.cs:95) via the existing `ILaneSink` (LaneSink.cs:22).**

Why this one, and not the bigger prizes:

1. **It is the cheapest seam in the tree: one keyword** (`private void` -> `internal void`). No new
   project, no WPF, no `Program.cs` refactor -- nothing that cannot be undone with `git revert`.
2. **Its double cannot drift, by construction rather than by promise.** `ILaneSink` is a 6-method
   interface with TWO PRODUCTION implementers (`Store.cs:12`, `ConciergeStore.cs:35`). A recording
   test sink is a third implementer, so adding a method to the interface breaks its compilation.
   That is constraint 2 satisfied by the compiler, which is the standard everything else must meet.
3. **It forces the plan's hardest problem to be solved first, on its smallest instance.** The named
   drift risk in the whole tree is `DodonaFakeAgent`: 545 lines hand-writing claude's stream-json
   shape, with NO shared constant, NO schema and NO contract test, duplicated by hand against its
   real consumer `LaneRuntime.cs:99-230`. If the real wire changes, every suite stays green. That
   is the routing-ladder failure one layer over. `S-WIRE` is exactly the slice that must build the
   mechanism against it (section 7) -- and if that mechanism cannot be built, the plan should die
   here, cheaply, having deleted nothing.
4. **The material is already in the repo and costs no quota.** `spikes/spike1-output/wire.jsonl` is
   a TRACKED, REAL, 20-line `claude` stream-json transcript. I read all 20 lines: it carries
   `system/init`, `rate_limit_event`, `system/thinking_tokens` (x11), `assistant` with a `thinking`
   content item and with a `text` content item, and `result/success` (x2), and its init line carries
   `claude_code_version`. It has a UTF-8 BOM -- which is a feature, because that is the GateHook
   defect in fixture form.
5. **It exercises every mechanism the plan invents**: ledger rows in five dispositions, a mutant
   with `expects-red` on both sides, `dev prove --with` over both a `.ps1` and a `unit:` pair, the
   TRX name format, `dev ledger --live`, a `wires.tsv` row, a `merged` row (m0's shim exit is one
   exit observed three ways) and a `stays` row.
6. **It is small.** Roughly the seven m0 "one stream-json line in, one row out" checks that
   survey-daemon calls "the single largest movable block in the group", plus the compression content
   checks. Concrete seed set, with `survey-daemon.md` as the authority: `m0:session_id_recorded`,
   `m0:a_missing_shim_is_named_not_guessed`, `m0:a_failed_spawn_is_recorded`,
   `m0:a_failed_spawn_leaves_no_lane_claiming_alive`, `compression:midturn_narration_is_still_a_row`,
   `compression:progress_rows_are_written`, `compression:raw_body_is_never_overwritten`,
   `compression:blocked_uses_the_fixed_schema`.

**The pilot is not done until `dev ledger` is a gate row and the whole ritual has run end to end
once.** Only then does anything fan out.

Rejected first slices, with reasons:

- **`ui-shell` / `Shell.Build()`** -- nine checks over two temp SQLite files, and the surveys call
  it "the single most reusable seam here". Loses because it needs a whole new `net8.0-windows` test
  project (S6) with its own `InternalsVisibleTo`: new infrastructure being debugged at the same
  moment as new procedure. Two unknowns at once is how a pilot stops proving anything.
- **`m1` / `GateHook`** -- the largest cluster of expensive-because-unreachable checks. Loses
  because S11 is a real refactor of top-level statements in `Program.cs`, and because it is the
  write gate: CLAUDE.md 3 and issue #4 record TWO occasions where prose about `GateHook`'s return
  paths was wrong. That work deserves a proven procedure, not one being invented around it.
- **`workspace`/`concierge`** -- 163 movable, the largest single win. Loses because most of those
  verdicts are Store- or Registry-backed and the unit project has no Store fixture at all; "move
  down a layer" there is a fixture project, not a copy-paste. Correct as wave 2; wrong as the thing
  that proves the procedure.
- **The wire register alone as the pilot.** Loses because a register with no slice executed against
  it proves nothing about whether the ritual is runnable.

### After the pilot: the slice DAG

Each slice declares the SUITES it owns (exclusively, for its duration) and the `src/` FILES it
touches. Two slices may run concurrently only if BOTH sets are disjoint.

| slice | seam(s) | suites owned | src touched |
|---|---|---|---|
| `S-WIRE` (pilot) | S1 `HandleShimLine` internal | m0, compression | LaneRuntime.cs |
| `S-POLLER` | S4 `Poller.Liveness`/`QuotaLine` internal, S7 `IStoreView` | ui-grid | Poller.cs, StoreReader.cs |
| `S-PUBLISH` | extract publish target resolution + verdict fold from Program.cs:1275-1364; `Ver.Parse` | publish, m4 | Program.cs (publish paths), Ver.cs |
| `S-STORE` | S2 `:memory:` guard; temp-file Store fixture in the unit project | m2 | Store.cs |
| `S-IDENTITY` | S5 `Registry(string path)`, S10 injected fs probes | workspace, concierge | Workspaces.cs, Repos.cs, Fence.cs, Git.cs |
| `S-UIVM` | S6 new `Dodona.Ui.Tests` (net8.0-windows), S9 `Dump()` -> `MainVm.DumpObject()` | ui-shell, m3 | DodonaUi/*.cs, DodonaUi.csproj |
| `S-UIWIRE` | S8 transport delegate | ui-wake, ui-ask | MainWindow.xaml.cs, DaemonClient.cs |
| `S-GATE` | S11 `GateHook`/`ParseArgs` out of Program.cs | m1 | Program.cs |
| `S-ASK` | `LandAskText` parameterised; `Ask.*` round-trip already exists | brain, voice | Daemon.cs (ask text), Dictation |
| `S-DAEMONCMD` | S3 `Daemon` ctor + `HandleAsync` internal | -- spans every suite -- | Daemon.cs |
| `S-SHIM` | S12 shim buffer/lease into a class | m0 (second pass) | DodonaShim/Program.cs |

Two scheduling rules on top of disjointness:

- **`S-DAEMONCMD` runs ALONE.** `HandleAsync` is 45 `case` labels reaching every suite; it cannot
  declare a disjoint suite set. Schedule it as a wave of one, after `S-GATE` (which shares
  `Program.cs`).
- **At most one slice per wave may own a WINDOW suite** (`m3`, `compression`, `ui-grid`,
  `ui-shell`, `ui-ask`, `ui-wake`, `voice`). Issue #3: window suites redden under machine
  contention and are green alone minutes later; the root cause is NOT established; and `voice`
  showed the signature while running SOLO, so the crowd is not concurrent suites but what a wave
  leaves behind it. Two agents verifying window suites at once manufactures exactly that reading,
  and a false red costs as much as a false green.

A workable wave schedule under those rules:

```
wave 0   S-WIRE                              (alone; pilot)
wave 1   S-POLLER | S-PUBLISH | S-STORE      (S-POLLER is the wave's one window suite)
wave 2   S-IDENTITY | S-UIVM                 (S-UIVM is the window suite)
wave 3   S-GATE | S-ASK                      (S-ASK owns voice: the window suite)
wave 4   S-UIWIRE | S-SHIM
wave 5   S-DAEMONCMD                         (alone)
```

- **Rejected: a machine-wide lock in `dev test` that serialises window suites.** It would make
  `dev test` wait on another process, which is CLAUDE.md 0.1's "never hung" directive in a new
  costume; it would tax the operator's own runs forever for the duration of one migration; and
  issue #3 is unrooted, so the lock might not even be drawn round the right boundary. Scheduling is
  the cheaper instrument and it costs the shared tooling nothing.

---

## 7. THE DOUBLES, AND WHAT KEEPS EACH ONE HONEST

Constraint 2 is the one the plan lives or dies on. A promise in a document is not a mechanism.
Five rules, each with its enforcement named:

**R1. Never fake `Store`. Never fake `Registry`.** Use the real class over a temp file (or
`:memory:` after S2's one-line guard, where `StoreReader` is not involved). The reason, from
`seams.md`, is not negotiable: `Store`'s correctness for the merge token IS the transaction --
`TokenRequest`, `TokenRenew` and `LandCommit` re-check holder and lease INSIDE the transaction that
lands the ticket. An in-memory double reimplements that as sequential field writes and PASSES while
deleting the property. `Registry`'s repo-exclusivity is a partial UNIQUE index its own class comment
calls "the real arbiter"; a `HashSet` fake is a DIFFERENT enforcement mechanism passing a test
written about the index, and CLAUDE.md 5 says a red exclusivity check is a correctness incident, not
a flaky test. **Also: do not extract `IStore`** -- ~70 members, and an interface erases the
transaction boundaries that ARE the property.

**R2. Never fake anything that mutates a git ref.** `Git.Run` is `Process.Start` with no interface.
`SilentDrops` exists to detect a merge that SUCCEEDED and lost work; a canned-stdout fake proves the
parser and nothing about the operation. Use a real scripted temp repo -- cheap, deterministic, and
already the suites' technique. Precedent for refusing the mock: `REVIEW-AND-MERGE-PLAN:546`,
"mocking a forge into the daemon would be testing the mock".

**R3. A narrow interface at a real boundary is allowed only when it has TWO PRODUCTION
implementers, or is an injected-probe overload where production binds the real one.** The two
sanctioned shapes are already in the tree: `ILaneSink` (Store and ConciergeStore both ship) and
`Trees.Locate(path, projects, dirExists, fileExists)` with the convenience overload at `Trees.cs:77`
that production is the sole caller of. Neither can rot, because the real path is the only path
production takes. Copy these two; do not invent a third style.

**R4. A stand-in must never be able to look like the real thing.** `IRecognizer`'s fake reports
`Engine = "none"`, not `"fake"`, when it replaced a real engine that failed
(`Recognizer.cs:87-90`), so a missing engine can never read as installed in a dump. Every new
double reports its own identity in whatever the test observes.

**R5. THE WIRE CORPUS -- the mechanism `DodonaFakeAgent` has never had.**

Create `tests/assets/wire/`:

- `recorded/` -- bytes captured from a real `claude` run. **Seed: `spikes/spike1-output/wire.jsonl`,
  already tracked, already real, costs no quota.** Each file gets a sidecar
  `<name>.provenance.txt` carrying the date and the `claude_code_version` from its own `system/init`
  line.
- `authored/` -- hand-written lines for shapes no recording covers. **The seed corpus does NOT cover
  `tool_use`, `user`/`tool_result`, `is_error`, `permission_denied`, or a non-success `result`
  subtype** -- I checked all 20 lines. Every authored line carries a sidecar reason.

Four assertions in `unit`, and together they are the mechanism:

1. `HandleShimLine` classifies EVERY line in both directories to a non-`unknown` outcome, and the
   outcome set equals a checked-in expectations file. The parser cannot silently stop handling a
   shape that really occurs.
2. `DodonaFakeAgent`'s emitter is lifted into an `internal static Wire` class the test project
   references -- one emitter, two callers (the fake's `Main`, and the test). Every shape the fake
   can emit must be shape-equal (same `type`, `subtype`, content-item types, required keys) to some
   corpus line. **The fake cannot invent a shape claude never sent.**
3. A COVERAGE assertion that PRINTS which fake-emittable shapes exist only in `authored/` and not in
   `recorded/`. The blind spot is named on every run rather than hidden. This is R4 applied to a
   corpus.
4. The corpus reader strips a leading BOM, and a test asserts it does -- the seed file has one.

Re-capture is a DELIBERATE, HUMAN-RUN act, never automatic: `dev wire-recapture` runs one real
`claude -p`, diffs the shape set against the corpus, and FAILS if a shape appears that the corpus
does not know. It is quota spend, so it sits on no automatic path and no suite may call it
(CLAUDE.md 0.1). Its existence is what turns "the corpus is stale" from a silent degrade into a
question with a cheap answer.

- **Rejected: a shared constants file or schema that both `LaneRuntime` and `DodonaFakeAgent`
  compile against.** Already considered and rejected as D-B7 (`LANE-BRIEFING-PLAN:240`, repeated
  verbatim as a comment at `DodonaFakeAgent/Program.cs:94-101`). It also would not help: a shared
  constant keeps the two halves agreeing with EACH OTHER while both drift away from what `claude`
  actually emits, which is precisely the failure. The corpus is evidence from outside the repo; a
  shared constant is the repo agreeing with itself.

---

## 8. VERIFICATION AT EACH STEP -- the table an agent works from

| step | command | pass condition |
|---|---|---|
| slice start | `dev worktree <slice>`, then `dev check` | tree builds; nothing running in the build output |
| commit A (seam) | `dev build` | compiles |
| | `dev test unit <owned suites>` | ALL GREEN. this IS the behaviour-preservation proof |
| | `dev ledger` | green, unchanged |
| | -- | `dev prove` deliberately NOT run; say so in the commit message |
| B0 old red | `dev prove --with <patch> <suite>:<old> ...` | every one PROVEN; record `red_old` verbatim |
| B2 new red | `dev prove --with <patch> unit:<FQN> ...` | every one PROVEN; no `expects-green` red; record `red_new` |
| B5 close | `dev build` | compiles |
| | `dev test unit <owned suites>` | green |
| | `dev ledger --live` | accounting closed for this slice; nothing declared-live failed to run |
| | `dev lint` | no control bytes, no mixed line endings |
| before merge to main | `dev gate` | once, and only here (CLAUDE.md 0.1: the heavy set has one standing reason) |

Three consecutive failed verification attempts on a slice: STOP and report; do not grind
(CLAUDE.md 1). If a suite the slice did not touch goes red, suspect the machine first and re-run it
ALONE before believing it (issue #3).

---

## 9. ROLLBACK

Reversibility is structural, not procedural:

- **A slice is exactly two commits and both are independently `git revert`-able.** Commit A touches
  `src/` only and is behaviour-neutral; commit B touches `tests/` only. Reverting B restores the
  deleted `.ps1` checks, removes the new C# tests, and removes that slice's `moves/<slice>.tsv` --
  after which `dev ledger` is green again, because **a baseline name with no move row DEFAULTS to
  "still live in its suite"**. The accounting closes itself on a revert. That is the property to
  preserve if anything about these file formats ever changes.
- **Slices are per-worktree**, so an abandoned slice is `git worktree remove` plus a deleted branch.
  Nothing else in the repo knows it existed.
- **Nothing is ever lost from history.** `dev ledger --origin <check>` runs
  `git log -S"<check>" -- tests/` and prints the commit that deleted it and its original body. The
  sha is DERIVED, never stored in the ledger: a stored sha is a second source of truth that rots,
  which is the `.built-from` mistake CLAUDE.md 2 records.
- **The mutants are checked in**, so a rollback decision six months later can re-run the exact proof
  that justified the move.
- **Rollback trigger, stated so it is not a judgement call:** three failed verification attempts on
  a slice, OR a `dev ledger --live` failure that cannot be explained by the ledger's own contents.
  Revert commit B; KEEP commit A (the seam is independently good and its suites are green); write a
  `stays` row with `note=no-seam-yet ...` naming the seam and the reason.

---

## 10. THE STOP CONDITION

`dev ledger --verdict` prints exactly this block, and the job is finished when it says so:

```
LEDGER
  baseline           1050 names, frozen at <sha>
  live in suite       NNN
  moved to unit       NNN   (each with a mutant and two recorded reds)
  merged into         NNN   (each naming a LIVE survivor and a wire)
  stays               NNN   by reason: process-fact N, git-ref-mutation N, real-window N,
                            timing N, absence-of-process N, wire-shape N, harness-hygiene N
  stays (no-seam-yet)   N   <- MUST BE 0, or every one carries an issue number
  vacuous-guard         N   (kept and labelled, by decision)
  unaccounted           0   <- MUST BE 0
  added (declared)      N
INTEGRATION CHECKS
  wires.tsv rows       NN
  harness rows         NN   (one per surviving suite; not deduplicable)
  live integration     NN   target NN   <- MUST BE <= target
VERDICT: on the accounting above, and only that.
```

The last line deliberately echoes the phrasing `dev gate` already uses ("on the 10 assertions
above, and only those"): the ledger proves every name is accounted for and every move was seen red.
It does not prove the system works. Nothing in this job may claim otherwise.

**Finished** requires ALL of: `unaccounted = 0`; `stays (no-seam-yet) = 0` or every such row carries
an open issue number on the tracker; every `moved` row has non-empty `red_old` AND `red_new`;
`live integration <= target`; and `dev gate` green once, on main, at the end.

**80% and quietly abandoned is detectable, and that is the point.** `unaccounted` is non-zero for
every un-migrated name, so the number never rounds to done. Each slice's state is a separate file in
`tests/ledger/moves/`, so the verdict says which slices exist and which do not. And a slice cannot
be left half done: commit B is atomic, so a slice that never reached it leaves only a green,
behaviour-neutral seam commit.

---

## 11. A CHECK THAT CANNOT MOVE DOWN

The answer is never "delete it". It is a four-rung ladder, walked in order:

1. **Is there a cheap seam?** Consult `seams.md`'s S1-S12 ledger. If yes, the seam belongs in this
   slice's commit A and the check moves. Most "cannot move" answers are really "has no seam yet".
2. **Is it a WIRE?** Then it stays, `disposition=stays`, and it MUST point at a `wires.tsv` row. If
   that wire already has an owner, this check is `merged` into the owner and the owner survives.
3. **Is it a downstream consequence of a wire someone else owns?** `merged`, naming the survivor.
   Survey examples: `m0:shim_record_dies_with_the_shim` and `m0:and_the_lane_pipes_are_gone` are the
   same shim exit observed three ways; m4's fourteen handoff checks are one handoff.
4. **None of the above** -> `disposition=stays`, `wire` pointing at a row you ADD to `wires.tsv`,
   and a `note` beginning with a word from the CLOSED reason vocabulary: `process-fact`,
   `git-ref-mutation`, `real-window`, `timing`, `absence-of-process`, `wire-shape`,
   `harness-hygiene`, `no-seam-yet`. A closed vocabulary is what stops "cannot move" becoming a
   shrug: `dev ledger` refuses a reason outside the list, and counts `no-seam-yet` separately in the
   verdict so it cannot hide among the legitimate ones.

Three special cases that already exist and must not be mishandled:

- **`no_process_left_in_the_build_output` (15 rows).** Written by `Assert-NoBuildOutputProcesses` in
  every suite's `finally` (`_workspace.ps1:353`); it reports what THIS suite's process left behind.
  It is `stays / harness-hygiene`, one per surviving suite, NOT deduplicable however the suites are
  reorganised. If suites merge, the row count falls with the suite count and the ledger arithmetic
  records that -- it is not a loss.
- **Vacuous-by-construction guards.** The repo keeps and LABELS them (R7's precedent: 18 checks, 14
  seen red, 4 vacuous kept and labelled). Three of them are pure predicates
  (`m2:a_pr_repo_still_promotes_a_refused_write` and its two siblings) and CAN move down -- a pure
  predicate test guards identically, cannot flake, and removes three live-daemon fixtures. The rest
  keep the `vacuous-guard` disposition wherever they live.
- **A check pinning something that should CHANGE.** `INVESTIGATION 4.8`: "A suite that proves
  stop-all is machine-wide would ENSHRINE the behaviour." Preserving a name is not endorsing its
  assertion. If a slice finds one, it is moved FAITHFULLY and a ticket is filed; it is never quietly
  re-aimed inside a migration commit, because a migration commit that also changes what is asserted
  is unreviewable.

---

## 12. RISKS, NAMED

1. **The paired red proves co-sensitivity, not equivalence.** A new unit test that asserts one of the
   three things the old check asserted still passes B2. The mitigation is human: the survey tables
   say what each check really asserted, and `dev ledger --origin` puts the original body one command
   away. This is the residual risk of the whole job and it cannot be mechanised away.
2. **The TRX name format is unverified on this machine.** I did not run `dotnet test`. Slice 0
   settles it before anything else; the fallbacks are named in section 3.
3. **`S-DAEMONCMD` cannot declare a disjoint suite set** and must run alone, which serialises the
   tail of the schedule.
4. **The wire register is a hand merge of six survey files.** If it is wrong, every `merged` row
   built on it is wrong. It is cheap to redo and it is checked in, so it is reviewable -- but it is
   the plan's single point of judgement.
5. **Concurrent worktrees still contend** on windows, process starts and the `\\.\pipe\` namespace
   (issue #3, unrooted). One-window-suite-per-wave is a mitigation, not a cure, and a red inside a
   wave must be re-run alone before it is believed.
6. **`dev ledger` as a gate row adds an eleventh assertion.** It is a sub-second static parse in the
   same class as I8 Repo-Lint, and it is NOT added to `dev test` or to `dodona.json`'s `//verify`.
   If the migration ends and the ledger stops earning its place, the row and the files go together
   in one commit -- and until then, the row is what stops the ledger drifting into fiction, which is
   the failure mode of every unchecked manifest.
7. **A plan document that names a `tests\*.ps1` which does not exist turns `dev lint` (I8) red**
   unless the line is marked `(planned)`. Any new suite this plan proposes must be written that way
   in the `docs/` copy.
8. **`git apply` of a checked-in mutant will rot** as `src/` moves. Accepted: a mutant that no
   longer applies makes `dev prove --with` abort loudly rather than pass, which is the correct
   failure direction, and re-cutting a one-hunk patch is minutes. Do not add a refresh mechanism;
   that is a second source of truth about the defect.

---

## 13. WHAT THE IMPLEMENTING AGENT DOES FIRST -- literal order

1. `dev worktree ledger0`; `cd .claude\worktrees\ledger0`; `dev check`.
2. Write `docs/TEST-ARCHITECTURE-PLAN.md` (this document, adapted; CRLF, no BOM, `(planned)` on any
   unwritten suite name).
3. Build `tests/ledger/wires.tsv` by merging the six survey `wires` arrays and collapsing the
   cross-group duplicates named in section 4. One owner per row.
4. Implement `dev ledger` (static, `--capture`, `--live`, `--slice`, `--verdict`, `--origin`) and
   `Run-Unit`'s TRX logger. **Verify the TRX `testName` format here and record it in
   `tests/ledger/README.md`.**
5. `dev gate` once, green, on a clean machine. `dev ledger --capture` from that run -> the frozen
   `baseline.tsv`. Commit: tooling + wires + baseline. No check has moved yet.
6. Implement `dev prove --with` (the six deltas in section 3). Verify it by proving ONE existing
   check red under ONE mutant, before writing any new test.
7. Add the `dev ledger` gate row.
8. Run slice `S-WIRE` end to end, alone: commit A, then B0-B5.
9. Only then fan out to wave 1.
