# The check-name ledger

The accounting that makes "no coverage was lost" a fact rather than a promise, for the job in
`docs/TEST-ARCHITECTURE-PLAN.md`. Everything here is TRACKED and machine-read by `dev ledger`.

| file | what it is |
|---|---|
| `wires.tsv` | one row per distinct integration wire, and the single check that proves it |
| `baseline.tsv` | the frozen name set, captured once from a green `dev gate` |
| `added.tsv` | declared growth: names that appeared after the freeze, with a reason |
| `moves/<slice>.tsv` | per-slice disposition rows: `moved`, `merged`, `renamed`, `no-seam-yet` |

**`baseline.tsv` is keyed on the CHECK NAME, not on `suite<TAB>check`.** The suite is an ordinary
column. That is what lets the suite rename (W6) happen without invalidating the freeze — and it is
what makes repo-wide name uniqueness a precondition of the ledger existing at all, rather than a
tidiness preference.

## W1.1 - the cross-suite duplicate scan

Run before anything captures a baseline, because a collision resolves at runtime as a `$results`
row **silently overwritten** and a tally one lower. Nothing in the repo detects that today;
`dev.ps1:584` records it as outside the runner's job by decision.

The scan is `sort | uniq -d` over every literal `Check '<name>'` plus every literal
`$results['<name>']`.

**THIS SECTION WAS WRONG WHEN FIRST WRITTEN, AND HOW IT WAS WRONG IS THE MOST USEFUL THING IN
IT.** The first version reported "four matches, two real" and declared the scan complete. The
`$results` half of that scan had matched **zero rows** - the pattern was mangled by shell
escaping on the way to `grep`, so it returned nothing, and nothing is indistinguishable from no
duplicates. Both collisions it did find came from the `Check` half alone. A third real collision
was sitting in plain sight and was caught only because the W1.2 agent parsed the same names
independently for `wires.tsv`.

That is CLAUDE.md 0.2's `ConvertFrom-Json` trap and the check-authoring skill's broken-query rule
in one: **a query that silently returns empty passes every test you would think to run on it.**
The defence is to assert the scan finds the things you already know about before trusting it
about the things you do not. The corrected scan is checked in as part of `dev ledger`'s static
rung precisely so it is never hand-run again.

### What the corrected scan finds

| name | verdict | disposition |
|---|---|---|
| `presence_idle_after_result` | **REAL** - `m2-acceptance.ps1:133`, `m3-acceptance.ps1:84` | m2's renamed to `presence_idle_after_result_in_status` |
| `double_uncertainty_asks_the_operator` | **REAL** - `brain-acceptance.ps1:229`, `concierge-acceptance.ps1:270` | concierge's renamed to `group_double_uncertainty_asks_the_operator` |
| `landed_exactly_once` | **REAL** - `m0-acceptance.ps1:107` (inline `$results`), `m4-acceptance.ps1:207` (`Check`) | m0's renamed to `orphaned_result_landed_exactly_once`. Genuinely different properties: m0's is an orphaned result draining after the daemon dies, m4's is an in-flight turn surviving a hot swap. m4 keeps the name because `wires.tsv` makes it an owner |
| `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` | not a duplicate | `m2-acceptance.ps1:330` and `:334` are the two arms of one `if/else`. One name, one runtime row |
| `grid_grows_to_the_number_of_lanes` | not a duplicate | `ui-grid-acceptance.ps1:436` is the check; `_workspace.ps1:409` is a **commented-out example** inside a doc comment |
| `no_process_left_in_the_build_output` | **legitimately non-unique** | written by the shared harness helper at `_workspace.ps1:377`/`:380` - ONE source site emitted into EVERY suite's results at runtime. See below |
| `nam` | scanner artifact | produced by taking a **variable** key, `$results[$name]`, which is the `Check` helper's own implementation and appears 15 times |

### The three rules that fall out, and all three are load-bearing

1. **Literal keys only.** A scanner that accepts `$results[$name]` invents a duplicate named after
   the variable. Mine did.
2. **Skip comment lines.** Otherwise `grid_grows_to_the_number_of_lanes` is a duplicate forever,
   and a permanent false positive in a gate assertion is how people learn to ignore the
   assertion - the same disease as a gate that is always green.
3. **Uniqueness is a property of runtime `$results` KEYS, not of source lines.** That is what
   keeps the `if/else` row legal: both arms write one key and exactly one executes. A source-line
   rule would forbid a correct idiom.

### The one legitimate exception, which `baseline.tsv`'s key must handle

`no_process_left_in_the_build_output` has a single source site in the shared harness and is
emitted into every suite that calls it, so **that one name appears once per suite in a full
run**. The plan names it (S4/W1: *"15 rows, harness hygiene, not deduplicable"*) but the
consequence for a name-keyed baseline is sharper than the plan says: a baseline keyed purely on
the check name cannot represent it at all.

So harness rows are either keyed on `suite`+`check` or carry a flag exempting them from the
uniqueness assertion - W2 decides and records which. Whichever it is, the refusal message must
distinguish **a genuine collision** from **a harness row**, or the first capture reads as a
15-way conflict and the tool looks broken on the day it is introduced.

The renames are `renamed` rows in `moves/`, not `moved` rows: nothing changed layer, and every
assertion is byte-identical.

## W2 -- `dev ledger`

The verb is on `tools/dev.ps1` (D-3: one door; and it must run on a tree that will not
compile, which is why the ledger's single check-name parser is PowerShell and not C# --
D-T6).

```
dev ledger                  # STATIC, ~1.0 s. every rung below, refusing on any failure
dev ledger --live           # additionally consumes a just-finished run's results.json + TRX
dev ledger --capture        # freeze/extend baseline.tsv from a GREEN full run
dev ledger --slice <name>   # that slice's rows and their proof state
dev ledger --verdict        # the block in TEST-ARCHITECTURE-PLAN 5.5
dev ledger --origin <check> # git log -S over tests/: which commit changed it, and its lines
```

`Run-Unit` now passes `--logger "trx;LogFileName=unit.trx"`, **and
`--results-directory tests\unit-output`**. The second half is not decoration: `dotnet
test`'s default is `tests\Dodona.Tests\TestResults\`, which is a **tracked** path, and
`dev gate` asserts that a suite run dirtied nothing. `tests\*-output\` is already
gitignored and is where all fifteen other suites write, so `unit` writing there keeps one
rule instead of two.

### The scanner is the PowerShell AST, not a regex, and that answers all three rules above

The three rules this file records -- skip comment lines, skip variable keys, uniqueness is
a property of runtime keys -- are not three separate guards in the code. The first two fall
out of using `[System.Management.Automation.Language.Parser]` instead of `grep`:

- **comments are not AST nodes**, so `_workspace.ps1:409`'s commented-out `Check '...'` is
  invisible to the scanner rather than suppressed by a rule that could go stale;
- **a variable key is a different AST type**. `$results['x']` is a
  `StringConstantExpressionAst`; `$results[$name]` is not, so it is classified `Dynamic`
  and never becomes a name. The fifteen `$results[$name]` sites are additionally inside a
  `FunctionDefinitionAst` named `Check` -- the helper's own body -- and are dropped for
  that reason too.

The scanner reports **723 registration sites** and **2 dynamic sites**
(`m1-acceptance.ps1:1167`, `concierge-acceptance.ps1:337`), which are exactly the two
loop-generating expressions behind the plan's 22 loop-generated names.

### The if/else idiom stays legal, and it is PROVED legal rather than exempted

Two sites writing one name in one file are refused **unless the AST can show exactly one of
them runs**. Two shapes are recognised, and both are in the repo today:

| shape | where | how it is proved |
|---|---|---|
| `if (...) { Check 'x' } else { Check 'x' }` | `m2-acceptance.ps1:330` / `:334` | the sites' innermost common ancestor is the `IfStatementAst` itself -- they are in different arms |
| `if (...) { $results['x'] = 'PASS'; return }` then `$results['x'] = "FAIL ..."` | `_workspace.ps1:377` / `:380` | the common ancestor is a statement block, and the earlier site's `if` arm ends in `return` |

Neither is on any allow-list. An allow-list keyed on file and line is a thing that goes
stale the first time somebody inserts a line above it.

### THE HARNESS ROW: the decision the plan did not make

`no_process_left_in_the_build_output` has **one** registration site (`_workspace.ps1`) and
appears **once per suite** in a full run's output. A `baseline.tsv` keyed purely on the
check name cannot represent fifteen of them.

**Decided: harness rows key on `suite` + `check`; every other row keys on `check` alone.**
The alternative -- a flag column -- was rejected because it is a fact somebody has to
remember to set, where this one is derived.

**And the exemption is DERIVED, not listed**: a harness name is a name whose registration
site is in `tests/_workspace.ps1`. Nothing is hand-maintained, so nothing can go stale, and
if a second harness row is ever added it is exempt automatically. `dev ledger` prints the
set it derived on every run, so the exemption is visible rather than implicit:

```
  note: harness rows: no_process_left_in_the_build_output -- written by tests\_workspace.ps1
        into EVERY suite's results, so keyed suite+check in baseline.tsv and exempt from
        repo-wide name uniqueness (plan 5.4: not deduplicable)
```

The repo-wide uniqueness refusal skips that set on both rungs, so a first full capture
reports no conflict where there is none -- which is the failure mode the parent session
predicted.

### The rungs, and the literal refusal each one produced when it was BROKEN on purpose

CLAUDE.md 0.3: a check is worth nothing until it has been seen red. `dev prove` cannot
judge `dev.ps1` itself, so each rung was broken by hand against this tree and the refusal
copied out verbatim. Every one of these exits **1** (`--capture` aborts with **2**).

| what was broken | the literal refusal |
|---|---|
| a frozen `baseline.tsv` row REMOVED | `baseline.tsv REMOVED OR ALTERED 1 frozen row(s): status_does_not_summon_a_daemon. The baseline is FROZEN -- only appends by --capture, and edits to suite/cases, are legal` |
| a frozen row's `check` ALTERED | `baseline.tsv REMOVED OR ALTERED 1 frozen row(s): orphaned_result_landed. ...` |
| one name registered TWICE in one suite, sequentially | `duplicate check name 'screenshot_fixed_size' is registered twice in one suite and the sites are NOT mutually exclusive -- m3-acceptance.ps1:327 and m3-acceptance.ps1:328. The second write overwrites the first: the tally comes out one lower and nothing says so` |
| one name registered by TWO suites | `duplicate check name 'enter_still_sends' is registered by more than one suite -- m2-acceptance.ps1:159 and ui-grid-acceptance.ps1:276. baseline.tsv is keyed on the CHECK NAME (plan 5.2), so one of the two cannot be represented at all` |
| a `moved` destination naming a method that does not exist | `moves\s-fixture.tsv:2 THE LAST-SEGMENT RULE: destination ends 'Dictation_never_submits_typo' but old_check is 'Dictation_never_submits' -- they must match character for character, so a typo cannot silently orphan a name` and `moves\s-fixture.tsv:2 destination method 'Dictation_never_submits_typo' does not exist in tests\Dodona.Tests` |
| a `moved` row with no mutant and no reds | `moves\s-fixture.tsv:2 is 'moved' with no mutation -- a move is proved by a PAIRED RED under one checked-in mutant (D-T5)` and `... is 'moved' without BOTH recorded reds (red_old, red_new) -- the literal observed failure lines` |
| `disposition = deleted` | `moves\s-fixture.tsv:2 disposition 'deleted' is outside the closed vocabulary [moved kept merged stays vacuous-guard renamed] -- so it cannot become a shrug (D-T21)` |
| a `stays` reason outside the closed vocabulary | `moves\s-fixture.tsv:2 note begins 'it', which is outside the closed reason vocabulary [process-fact git-ref-mutation real-window timing absence-of-process wire-shape harness-hygiene no-seam-yet]` |
| a `stays`/`kept` row for a check no suite registers | `moves\s-fixture.tsv:2 is 'stays' but no suite registers 'a_check_nobody_registers_any_more' any more` (the reachability rung, D-T6) |
| a row naming a check in no baseline row | `moves\s-fixture.tsv:2 names 'a_check_nobody_registers_any_more', which is in no baseline.tsv row` |
| a non-ASCII byte in a ledger row | `tests\ledger\baseline.tsv:6 non-ASCII byte 0xe2 -- a ledger row read as ANSI matches nothing and drops SILENTLY` |
| a UTF-8 BOM on a ledger file | `tests\ledger\baseline.tsv has a UTF-8 BOM -- ledger files are ASCII with no BOM` |
| bare LF line endings | `tests\ledger\baseline.tsv has 5 bare LF line ending(s) -- ledger files are CRLF` |
| the wrong header | `tests\ledger\baseline.tsv:1 header is [name|suite|cases] -- expected [check|suite|cases]` |
| `--capture` over a repo that fails its own rungs | `BLOCKED: 1 static problem(s) -- capture will not freeze a census over a repo that fails its own rungs` |

Two more refusals exist and were **not** broken by hand, because neither has anything to
break yet and both are guarded on a value that is empty today: `owner_body_sha` set with no
`Wire '<id>' { }` block to hash (that block is W7), and a `wires.tsv` `owner_check` that no
suite registers (`wires.tsv` is W1.2). They are written and they refuse; they are not
proved.

### The controls -- what did NOT go red

A refusal that fires on everything is worthless, so each break was run beside its control:
a committed fixture baseline with nothing wrong (`clean`, exit 0); a `moved` row whose
destination method really does exist (`clean`, exit 0); and, throughout, the two
mutually-exclusive duplicate idioms above, which never appear in any output.

### `landed_exactly_once` -- the rung's first real find

Run against `c242089`, the first thing `dev ledger` did was refuse the tree it was written
in:

```
duplicate check name 'landed_exactly_once' is registered by more than one suite --
m0-acceptance.ps1:107 and m4-acceptance.ps1:207. baseline.tsv is keyed on the CHECK NAME
(plan 5.2), so one of the two cannot be represented at all
```

This is W1.1's third collision, found independently by the AST scanner and by the parent
session's corrected `sort | uniq -d` in the same hour. `8298d72` renames m0's to
`orphaned_result_landed_exactly_once`; the scanner is green at that commit. A mechanism
that had gone green on first contact with this repo would have been the empty-population
failure the plan's 3.2 is about.

### Two figures confirmed against a real run, not parsed

`dev ledger --live` after `dev test unit` reads the TRX and reports **208 methods, 300
executed cases, 0 not passed** -- which agrees exactly with 1.2's corrected attribute
parse (176 `[Fact]` + 32 `[Theory]` = 208 methods, carrying 124 `[InlineData]` rows;
176 + 124 = 300). The plan's own numbers are now measured rather than derived.

**And the TRX theory-row form is settled here rather than at W3** (5.2 scheduled it there).
A `[Theory]` row's `testName` is the method's FQN with a parenthesised argument list
appended:

```
Dodona.Tests.ClaimsTests.Parse_maps_the_four_spellings(spec: "subtree:src/Water", kind: "subtree", value: "src/water")
```

124 of the 300 rows carry one. Stripping at the **first** `(` is safe and is what the tool
does: a C# method name cannot contain one, so no argument value can be mistaken for the
name however it is spelled.

### What is NOT done

- **`baseline.tsv` is absent.** Capturing it needs a green full `dev gate` on an idle
  machine, which W2 deliberately did not run. `dev ledger` says so on every run and every
  rung that needs it is inert until then. `added.tsv` is committed with its header and no
  rows.
- **`wires.tsv` is not read as required, only as available.** W1.2 writes it. Every rung
  that needs it refuses by name in the meantime rather than passing quietly.
- **The static rungs are NOT folded into Repo-Lint (I8) yet**, which D-T23 asks for. They
  cannot be: the fold makes them a gate assertion, and the first thing that assertion would
  do is fail for want of a `baseline.tsv` that only a green gate can produce. It is one
  edit once the census exists, and it belongs in that commit.
- **`--rehash`** (mentioned at 3.3.1) is not implemented. It would rewrite `wires.tsv`,
  which W2 does not own.

## The first capture lost two names, and the tool is what lost them

`dev ledger --capture` keyed every name into ONE PowerShell hashtable. `@{}` is
**case-insensitive**, so a unit method whose name matched a suite check apart from
capitalisation was silently skipped. Two such pairs exist, and both are DELIBERATE -- the same
property proved at both layers, spelled to each language's convention:

| suite check | unit method |
|---|---|
| `brain:a_one_project_workspace_says_nothing_about_scope` (`brain-acceptance.ps1:806`) | `A_one_project_workspace_says_nothing_about_scope` |
| `workspace:a_named_project_is_not_overruled_by_a_busy_one` (`workspace-acceptance.ps1:1465`) | `A_named_project_is_not_overruled_by_a_busy_one` |

Measured on the first real capture: **962 names frozen where 964 had run**, and nothing said so.
A census that loses names is the one thing this tool exists to prevent, so it lost them in its
own first artifact.

**The fix is two things, not one.**

1. `Ledger-NewSet` -- an ordinal dictionary, everywhere the ledger keys a name. `A_x` and `a_x`
   are different names and merging them is never right.
2. `Ledger-Key` -- one function both the capture and the integrity rung call, so they cannot
   disagree. Suite checks key on the **bare name** (that is what keeps W6's suite rename free);
   harness rows key `suite/check`; **unit methods key `unit/check`**. The layer matters because a
   suite check and a unit method may share a name ON PURPOSE -- it is the plan's end state for a
   wire, and step B1 names the new C# method after the old check verbatim.

**A genuine leaf collapse is now announced rather than swallowed.** Two unit methods in different
classes can share a method name -- `A_trailing_separator_is_not_a_different_folder` exists in both
`InstanceCanonicalTests` and `ProjectResolutionTests` -- and the census holds one row for it. The
capture prints a NOTE naming both fully-qualified methods and telling you to rename one to count
both.

### Proved red before it was fixed

A synthetic green run carrying exactly those collisions, captured twice on the same tree:

| | names | unit methods | the four pair rows |
|---|---|---|---|
| HEAD (unfixed) | 34 | 2 | **only the two suite rows survive** |
| fixed | 36 | 4 | all four present, plus the leaf-collapse NOTE |

A full green gate is the honest source for the real freeze, but it takes ~5 minutes and could not
demonstrate this at all -- the drop is silent, so a green gate looks identical either way. The
fixture is what makes the failure visible, and it is the reason this is a fix rather than a
number nobody questioned.

## W3 -- `dev prove --with <patch>`, and `tests/Dodona.Ui.Tests`

`dev prove` judges a check against HEAD, which works while the check is NEW: HEAD lacks your
fix, so a check with teeth fails there. A pure MOVE has no fix in it -- the same assertion, one
layer down -- so HEAD passes it and **every moved check reports VACUOUS by construction**. The
instrument for a move is a DEFECT, not an absence, and `--with` is how you supply one:

```
dev prove --with tests\mutants\<slice>-NN.patch [<suite>:<check> ...]
```

With no check named, the patch's own `# expects-red:` lines are the list, so the defect and the
checks it must redden live in one reviewable file rather than in a command somebody retypes.
`# expects-green:` names a neighbour that must SURVIVE it -- the over-broad-mutant detector, and
it fails the proof, because a mutant that reddens half the suite proves nothing about the one
check it was aimed at.

`unit` and `ui-unit` are now proof targets. Each names a project (`tests\Dodona.Tests`,
`tests\Dodona.Ui.Tests`), each is added to the HEAD build **only when its own prefix appears**
in the pairs, and each runs `Run-Unit -Root $wt` -- against the throwaway worktree of HEAD, never
the working tree. That is reason 3 of `dev prove`'s own unit refusal answered; the refusal itself
stays for the BARE form, where a HEAD without your new symbol still yields a compile error rather
than a red.

### THE TRX `testName` FORMATS, READ OFF THIS MACHINE

Both were parsed out of `tests\unit-output\unit.trx` and `ui-unit.trx` after a green
`dev test unit ui-unit` at `372e18d` + this work item. Quoted verbatim, between pipes so the
bounds are unambiguous:

```
[Fact]    |Dodona.Ui.Tests.RecognizerArrivalTests.The_fake_recogniser_raises_Ready_exactly_once_from_Start|
[Fact]    |Dodona.Tests.SpeechStreamTests.Dodonas_own_keyterms_all_survive_the_budget|
[Theory]  |Dodona.Tests.ClaimsTests.Parse_maps_the_four_spellings(spec: "subtree:src/Water", kind: "subtree", value: "src/water")|
[Theory]  |Dodona.Tests.RoutingInCodeTests.A_colon_with_no_space_is_not_a_target(text: "see http://example.com for the spec")|
```

So a `[Fact]` is the bare fully-qualified name and a `[Theory]` row is that name with a
parenthesised argument list appended -- `name: value`, comma-separated, string values in C#
literal form (quoted, so a value may contain `:`, `/`, `(` and spaces). This CONFIRMS what W2
recorded from the same file; it was scheduled as a W3 finding by the plan (5.2) and settled a
work item early, which is why it is a confirmation rather than a discovery.

**Stripping at the FIRST `(` is safe, and the reason is a language rule rather than a survey of
the data**: a C# method name cannot contain `(`, so nothing after the first one can be part of
the name however an argument is spelled. `Prove-Judge` and `Ledger-Live` both do exactly that
(`-replace '\(.*$', ''`) and both must keep doing the same thing.

**A theory is ONE check made of N rows, and one red row reddens the method.** `dev prove` prints
the shape it saw (`PASS -- 2 rows`) because "1 of 8 rows" and "8 of 8" are different findings
about a mutant.

### The two reds, verbatim

CLAUDE.md 0.3: a check is worth nothing until it has been seen red. That applies to the tool
too, so the tool's own first reds are kept as checked-in mutants under `tests\mutants\`.

**`w3-verify-01.patch`** -- `LanePrefix` requires a double colon, so `WATER: make it darker` is
no longer a tier-0 prefix. Aimed at ONE function reached from two layers, which is the shape a
slice's mutant has to have:

```
  PROVEN   Dodona.Tests.RoutingInCodeTests.A_lane_prefix_names_its_target_and_keeps_the_rest: FAIL -- 1 of 1 case not passed
  PROVEN   Dodona.Tests.RoutingInCodeTests.A_prefixed_paragraph_keeps_its_newlines: FAIL -- 1 of 1 case not passed
  PROVEN   tier0_prefix_routes: FAIL -> SKY (focus, no classifier warm)
  CONTROL  Dodona.Tests.RoutingInCodeTests.A_colon_with_no_space_is_not_a_target: PASS -- 2 rows
  CONTROL  tier0_message_delivered: PASS

PROVEN: all 3 check(s) fail under w3-verify-01.patch, so they have teeth.
        and 2 declared control(s) survived it.
```

That is the paired red the plan's 5.3 asks for, in both languages, under one checked-in defect.

**`w3-verify-02.patch`** -- `FakeRecognizer.Start()` raises `Ready` only in the `hang` case,
i.e. exactly backwards. This is the half nothing else could reach: a check whose subject is a
type in a **net8.0-windows PRODUCTION assembly**. Proved against `e093cd7`, the seam commit:

```
== prove: 'Dodona.Ui.Tests.RecognizerArrivalTests.The_fake_recogniser_raises_Ready_exactly_once_from_Start' must FAIL against HEAD ==
mutant applied to HEAD: src/DodonaUi/Recognizer.cs

  PROVEN   Dodona.Ui.Tests.RecognizerArrivalTests.The_fake_recogniser_raises_Ready_exactly_once_from_Start: FAIL -- 1 of 1 case not passed

PROVEN: the check fails under w3-verify-02.patch, so it has teeth.
```

The defect is chosen to be the drift plan 3.2 argues a compiler cannot see: `IRecognizer`'s shape
is unchanged, so `dev build` is silent, and the only thing that can notice is a check that
actually SUBSCRIBES. It is also the live failure the three-state mic glyph exists for -- a window
that is on and deaf must never look like a window that is on.

### The finding: `m2:tier0_message_delivered` does not test tier 0

`tier0_message_delivered` was listed `expects-red` on the first run of that mutant and came back
**VACUOUS**. It is about the CHECK, not the mutant. With tier 0 dead the sentence still reached
SKY, by the focus fallback -- the same run printed `-> SKY (focus, no classifier warm)` -- so the
body still arrived and the check still passed. It asserts ARRIVAL, and arrival is what the
fallback also produces. Only its sibling `tier0_prefix_routes` asserts the RUNG, by matching
`-> SKY (tier 0)`.

**This reaches past one check.** TEST-ARCHITECTURE-PLAN 2.2 names `m2:tier0_message_delivered` as
the owner of wire **E7**, whose sentence is *"takes the INSTANT path ... with no classifier
consulted and nothing held"*. Under a defect that removed the instant path entirely, E7's owner
stayed green. On the plan's own four-rung ladder (5.4) that is a `vacuous-guard` or a
mis-aimed owner, and W8's E7 slice has to decide which; `tier0_prefix_routes` is the candidate
that actually asserts the rung. **`wires.tsv` is frozen and is NOT edited here** -- this is the
record, and the decision belongs to whoever owns that slice.

It is written down as a CHECK and not only as prose: it is an `expects-green` row on
`w3-verify-01.patch`, so if somebody strengthens that check the proof goes OVER-BROAD and says
so. Prose is not enforcement (0.2's whole lesson, and 3.3.1's).

### The five refusals `--with` adds, and the literal words each one produced

W2's rungs were each broken by hand and the refusal copied out verbatim, because `dev prove`
cannot judge `dev.ps1` itself. The same standard applies here. Every one of these was run
against this tree and exits **2**:

| what was wrong | the literal refusal |
|---|---|
| a mutant touching a path outside `src\` | `the mutant touches 1 path(s) outside src\: tests/ledger/README.md` / `FIX: a mutant is a DEFECT IN THE PRODUCT; a patch that edits tests\ measures a change against itself` |
| a mutant header with no `expects-red` | `the mutant names no check to redden: <path>` / `FIX: add at least one  # expects-red: <suite>:<check>  line above the diff (plan W3 delta 6)` |
| `--with` with nothing after it | `--with needs a patch file` / `FIX: dev prove --with tests\mutants\<slice>-NN.patch [<suite>:<check> ...]` |
| `dev prove unit <check>`, no `--with` | `the unit suites cannot be proved against a bare HEAD` ... `dev prove --with tests\mutants\<slice>-NN.patch unit:<FQN> <suite>:<old_check>` |
| a bare `dev prove` on a clean tree | `src and tests are identical to HEAD, so there is no change to prove` / `FIX: make the fix first, leave it uncommitted, then run prove  --  or supply a defect: dev prove --with <patch>` |

The last two are the OLD refusals, re-read on purpose: delta 5 keeps the bare-form refusal and
delta 2 keeps the dirty-tree guard, and a change that quietly disarmed either would be a proof
tool that stopped proving. Both still fire, and both now name `--with` as the other road.

**Not proved by hand, and named rather than left to be found:** the `git apply --check` failure
path (a patch cut against a different commit) and the OVER-BROAD verdict. The first is git's own
refusal wrapped in a message; the second is `Prove-Judge` reading a declared `expects-green` row
as FAIL, and it is the same code path as a PROVEN row read the other way up. Neither has been
seen red.

### A mutant patch is stored LF and checked out CRLF, and that is FINE -- measured, not assumed

`git add` says `LF will be replaced by CRLF the next time Git touches it` for every file under
`tests\mutants\`. `core.autocrlf=true` here, `git diff` emits a pure-LF patch, and the round trip
through the index hands it back with CRLF everywhere. That looked like a trap worth a
`.gitattributes` rule, so it was measured instead of argued: the LF form and a hand-CRLF-ified
copy of the same patch were both run through `git apply --check` and `git apply` against this
CRLF working tree, and **both applied cleanly and produced a byte-identical result** -- 1 changed
line, `git diff --numstat` `1 1`, and the target file still 6612 CRLF and 0 bare LF afterwards.
Git converts on both sides of the comparison. So there is no `*.patch -text` rule, deliberately:
this repo's `.gitattributes` says in as many words that a normalisation rule introduced casually
is how the line-ending bill gets paid twice, and this one buys nothing.

### What is NOT done

- **`w3-verify-02.patch` could not be proved in the same commit as the seam, and that order is
  forced rather than tidy.** Run first against `372e18d`, it reddened nothing, because a check in
  `tests\Dodona.Ui.Tests` does not COMPILE against a HEAD whose `DodonaUi.csproj` has no
  `InternalsVisibleTo`:

  ```
  RecognizerArrivalTests.cs(27,21): error CS0122: 'FakeRecognizer' is inaccessible due to its protection level
  BLOCKED: HEAD does not build (tests\Dodona.Ui.Tests\Dodona.Ui.Tests.csproj), so it cannot be used as a baseline
  ```

  That is delta 5's reason 1 happening in the flesh, and the plan's own answer to it is *"the seam
  commit landing first"*. **Every slice that introduces a new destination TYPE inherits this**: the
  seam commit lands, then the slice is proved against it. A mutant is judged against HEAD, so
  anything the check needs in order to compile has to already BE at HEAD.
- **`ui-unit` is deliberately NOT in `AllSuites`.** It is reachable as `dev test ui-unit` and as a
  `ui-unit:` proof pair. Putting it in the default set widens `dev gate` and `dev suites`, which
  is W4's call when it has something to assert there.
- **`Ledger-Live` still reads only `unit.trx`.** `tests\Dodona.Ui.Tests`'s single method is in no
  `baseline.tsv` row and is not counted by `dev ledger`; the baseline is frozen, and teaching the
  census about a second project is W4's, in the commit that gives it rows to count.
- **`--rehash` is still not implemented** (unchanged from W2).

## W4 -- the double ledger

Two rungs, because one instrument cannot answer both questions (plan 3.2, redesigned after the
adversarial review found the first design reflecting over an empty set).

| rung | where | question |
|---|---|---|
| 1, POPULATION | `Repo-Lint` in `tools\dev.ps1`, so `dev lint` and I8 | which types in this repo are doubles, and does anything look at them? |
| 2, ANCHOR SEMANTICS | `tests\Dodona.Tests\Doubles\DoubleLedgerTests.cs` and `tests\Dodona.Ui.Tests\DoubleLedgerTests.cs` | does each anchor actually hold? |

The attribute is `src\Dodona\Testing\DoubleAttribute.cs`, `internal`, zero behaviour,
`Compile Include`-linked into `DodonaUi.csproj` and deliberately NOT into `DodonaFakeAgent`
(D-B7, D-T27). `tests\ledger\double-assemblies.tsv` is the list rung-1 assertion 4 resolves a
project against.

### THE EIGHT REDS, EACH SEEN, EACH VERBATIM

`dev prove` refuses the `unit` suites in their bare form, so every one of these was produced by
hand against this tree and the literal refusal copied out. Reds 1-4 and 8 are `dev lint`; 5, 6 and
7 are `dev test unit` / `dev test ui-unit`. All were reverted.

**1 -- an unanchored `Fake*` in `src\Dodona\`.** *(The first design could not produce this red at
all: `src\` was outside its population.)*

```
src\Dodona\RED01.cs:3 class 'FakeThingA' is a test double by its NAME and carries no
[Double(...)] -- every double declares what keeps it honest (plan 3.2 rung 1, assertion 1)
```

**2 -- the same in `src\DodonaUi\`, from the same scan.**

```
src\DodonaUi\RED02.cs:3 class 'FakeThingB' is a test double by its NAME and carries no
[Double(...)] -- every double declares what keeps it honest (plan 3.2 rung 1, assertion 1)
```

**3 -- a `[Double]` in `src\DodonaShim\`, which no rung-2 test loads.** Assertion 4, the one the
first design could not have.

```
src\DodonaShim\RED03.cs:6 declares [Double] on 'FakeThingC' but project 'src\DodonaShim' is in NO
tests\ledger\double-assemblies.tsv row -- no reflection test loads that assembly, so the anchor
would never be checked. Give the project a rung-2 row, or move the double (plan 3.2 rung 1,
assertion 4)
```

**4 -- a type named `Mock*`.**

```
src\Dodona\RED04.cs:3 class 'MockThingD' is named Stub*/Mock*, which is refused anywhere in the
repo -- name it Fake*/Recording* and anchor it with [Double(...)] (plan 3.2 rung 1, assertion 2)
```

**5 -- a new fake anchored `Interface` where the only other implementer is itself a `[Double]`.**
One throwaway interface, one real implementation, two fakes; rung 2, printing the survivor.

```
Interface anchors that do not hold:
Dodona.FakeRed5A [dodona]: Dodona.IRed5 has 1 shipping implementer(s) once every [Double] is
excluded -- Dodona.RealRed5. An Interface anchor claims the compiler catches shape drift, and with
one shipping implementation the interface's shape is whatever the fake finds convenient. Either
point it at an interface production really implements twice, or declare the shortfall:
SeamOnlyInterface = <open issue>.
Dodona.FakeRed5B [dodona]: Dodona.IRed5 has 1 shipping implementer(s) once every [Double] is
excluded -- Dodona.RealRed5. ...
```

**6 -- `FakeRecognizer` anchored `Interface` with no `Contract` and no `KnownDivergence`. RED
AGAINST THE TREE AS IT STANDS**, which is the mechanism proving itself against code nobody wrote
for it. Observed before the attribute was completed, with `Anchor`, `Real` and `Wire` only:

```
Interface anchors with nothing behind them:
DodonaUi.FakeRecognizer [DodonaUi]: anchored Interface alone. Interface reaches SHAPE drift and
nothing else, so every Interface anchor needs Contract = "<X>Contract" (one body, two subjects) or
KnownDivergence = "<one sentence>" with Issue = <open issue>.
```

**6b -- AND A SECOND ONE NOBODY ASKED FOR, from the same untouched tree.** The implementer count
is red for `FakeRecognizer` too, and the plan offers no remedy for it. See *"the rule the plan
could not ship"* below; this is the red that forced `SeamOnlyInterface` to exist.

```
Interface anchors that do not hold:
DodonaUi.FakeRecognizer [DodonaUi]: DodonaUi.IRecognizer has 1 shipping implementer(s) once every
[Double] is excluded -- DodonaUi.DeepgramRecognizer. An Interface anchor claims the compiler
catches shape drift, and with one shipping implementation the interface's shape is whatever the
fake finds convenient. Either point it at an interface production really implements twice, or
declare the shortfall: SeamOnlyInterface = <open issue>.
```

**7 -- a wire check renamed out from under the double.** One edit to `wires.tsv`'s F9 row, both
rungs red, and BOTH are recorded because they catch it from opposite directions -- rung 1 sees a
register row naming a check no suite registers, rung 2 sees a double naming a register row that no
longer exists.

```
wires.tsv:33 names owner_check 'the_mic_button_toggles_listening', which no suite registers --
deleted, renamed, or misspelled
src\DodonaUi\Recognizer.cs:93 [Double] on 'FakeRecognizer' names Wire
'voice:clicking_the_mic_toggles_listening', which resolves to no tests\ledger\wires.tsv row -- the
wire was deleted, renamed, or misspelled
```

```
doubles standing beside a wire that has moved:
DodonaUi.FakeRecognizer [DodonaUi]: names Wire "voice:clicking_the_mic_toggles_listening", which
resolves to no row in tests\ledger\wires.tsv. The wire was deleted, renamed or misspelled, and
this double is now standing beside nothing.
```

`wires.tsv` was restored byte-for-byte (`git diff HEAD --stat` reports nothing).

**8 -- a `KnownDivergence` with no `Issue`.**

```
src\Dodona\RED08.cs:6 [Double] on 'FakeThingE' declares a KnownDivergence with no Issue -- a
divergence is visibility, not a catch, and an untracked gap is one nobody will ever close
(plan 3.2)
```

### The controls -- what did NOT go red

`dev lint` and both unit suites are green with the same mechanism in place over the real tree
(`304 checks, 0 failed` and `7 checks, 0 failed`), which is the control every one of those reds
needs: a refusal that fires on everything is worth nothing. Rung 1's reading line is printed on
every `dev lint` and every `dev gate`, so the anchored count is visible rather than assumed:

```
note: doubles: 1 anchored by attribute, 1 by corpus; 1 with a known divergence (issues #17);
      1 on a seam-only interface (issues #17)
```

### THE RULE THE PLAN COULD NOT SHIP, AND WHAT REPLACED IT

**Plan 3.2 as written does not build, and the failure is not cosmetic.** It states the `Interface`
rule as *">= 2 implementers that do NOT carry `[Double]`"*, says in as many words that
`IRecognizer` does not qualify, and then 3.6 anchors `FakeRecognizer` as `Interface` anyway and
calls it *"RED on day one under the corrected rule, deliberately"*. The two remedies it offers for
that red -- a `Contract` and a `KnownDivergence` -- are both about BEHAVIOUR, and neither one
changes an implementer count. Red 6b above is that contradiction in the flesh.

So as written the mechanism ships a **permanently failing unit test**, which is a gate people learn
to ignore -- the same disease as a gate that is always green, and the one this repo has paid for
most often.

**Rejected: weakening the rule to ">= 1 surviving implementer".** It would bless every interface
that exists only as a test seam, which is exactly the case `IRecognizer` is, so the rule would then
be satisfied by the thing it is meant to detect.

**Taken: the shortfall becomes a DECLARATION, on the model of `no-seam-yet` (D-T21).**
`SeamOnlyInterface = <open issue>` on the `[Double]`. Rung 1 refuses a value that is not a positive
issue number and refuses it on any anchor but `Interface`; rung 2 lets the count fall below two only
when it is set; `dev gate`'s ledger reading counts it separately from `KnownDivergence`. It is not
a debt to be worked off -- `IRecognizer` has one shipping implementation because there is one speech
engine -- and what it buys is that the weakness cannot be silent. Issue **#17** carries both of
`FakeRecognizer`'s declared gaps.

### `Contract` IS A STRING, AND THE PLAN SAID `typeof`

Plan 3.2 writes `Contract = typeof(LaneSinkContract)`. That cannot compile for the doubles this
redesign exists to reach. A contract class holds `[Fact]`s, so it lives in a test project; two of
the three existing doubles live in PRODUCTION assemblies; and `src\` cannot reference `tests\`.
`typeof` would have worked only for a double living in a test project -- the population the first
design already failed on.

So `Contract` is a name, and the hand copy that creates is closed by enforcement rather than by
care: `Interface_is_never_a_sole_anchor` resolves the name inside the test assembly and is RED if
it names nothing, if what it names is not abstract, or if it has fewer than two concrete
subclasses. `Real` stays a `Type`, as the plan requires -- it is in the same assembly, so it can be.

### A HOLE FOUND IN THIS MECHANISM BY RUNNING IT: `IDisposable` self-satisfied the count

First run against the real tree, `Every_Interface_anchor_has_two_shipping_implementers` came back
**GREEN** over `FakeRecognizer`. `FakeRecognizer` and `DeepgramRecognizer` share `IRecognizer` AND
`IDisposable`; the assertion took the shared interface with the most shipping implementers, and
`IDisposable` has dozens. That is review finding 2's failure wearing a different hat -- a count
satisfied by something other than the thing being claimed -- and it was caught only because red 6b
was expected and did not appear.

Fixed by restricting the candidates to interfaces **declared in the assemblies under test**: the
anchor's claim is *"production implements this interface twice"*, and nobody grows `IDisposable`.
`DoubleLedger.SharedInterfaces` carries the measurement.

### A false positive that was the rule being right

`dev lint` refused `FakeRecognizerContract`, the contract subclass that supplies the fake:

```
tests\Dodona.Ui.Tests\RecognizerContract.cs:91 class 'FakeRecognizerContract' is a test double by
its NAME and carries no [Double(...)]
```

Which is correct. `Fake*` is this repo's word for a thing that stands in for something real, and a
class named that with no anchor is the ambiguity assertion 1 exists to remove. The subclasses are
named for the contract now -- `RecognizerContractOverTheFake` and
`RecognizerContractOverDeepgramAtAClosedPort`.

### `RecognizerContract` -- the real subject really runs, and it costs nothing

`tests\Dodona.Ui.Tests\RecognizerContract.cs`: one abstract body asserting `IRecognizer`'s own
sentence -- *exactly one of `Ready` or `Failed` arrives, exactly once* -- with two concrete
subclasses. The real one is `DeepgramRecognizer` pointed at `ws://127.0.0.1:1/`, a closed loopback
port, so the connect is refused by the loopback stack instantly. **No network, no microphone, no
credential, no quota**: `RunAsync` only reaches `_capture.Start()` after the socket is open and it
never opens, and `DODONA_STT_TOKEN` is set to a dummy so `SpeechAuth` never walks to the operator's
real `~\.claude\.credentials.json`. Both subjects pass.

`tests\Dodona.Ui.Tests\AssemblyInfo.cs` disables xunit's parallel collections in that project,
because the endpoint override is three process-wide environment variables and a sibling class
running beside it would see them half-restored.

### FALSIFIER 4, MEASURED (review finding 19)

Throwaway `StoreFixture` (a real `Store` on a temp file) and `GitRepoFixture` (a real `git init`
plus one commit) were built, exercised by eight cases -- five and three -- measured, and reverted.
All figures are `dev test unit`'s own printed seconds on this machine, 2026-08-22, **warm, on a
second run after a build**, which is the threshold the plan restates. Three runs each:

| | run 1 | run 2 | run 3 | cases |
|---|---|---|---|---|
| without the fixtures | 3.4 s | **1.8 s** | **1.9 s** | 304 |
| with them | 3.3 s | **2.8 s** | **2.8 s** | 312 |
| reverted, confirming the baseline returns | 2.2 s | **1.9 s** | **1.9 s** | 304 |

Per case, from the TRX: **56 ms** for a real `Store`, **183 ms** for a real git repository (four
`git` process starts). So the whole delta is **+0.9 s for eight fixture-bearing cases**, and the
arithmetic that matters for W8 is the per-case figure, not the total.

**Falsifier 4 does not fire, and the honest bound is this**: the operator's one-to-two seconds is
blown at roughly **20 more git-repo cases** or **60 more store cases** beyond today's 304. That is
a real ceiling, and it says the answer for a large slice is a *shared* fixture (`IClassFixture`)
rather than a per-case one -- the measurement above is deliberately the per-case worst case.

### WHAT W4 CHANGED BESIDE ITS OWN DELIVERABLES

- **The fold (D-T23) is done.** `dev ledger`'s static rungs and the double ledger's rung 1 are
  asserted inside **Repo-Lint (I8)**, which is already one of the gate's ten. **`dev gate` still
  reports ten assertions.** W2 could not do this: the fold makes them a gate assertion, and the
  first thing that assertion would have done is fail for want of a `baseline.tsv` that only a green
  gate can produce. The census exists now, so it lands.
  The cost, said out loud: **`dev lint` is no longer sub-second** -- ~1.5-2.5 s, because it now
  AST-parses fifteen suite files and reads every tracked `.cs` under `src\` and `tests\`.
- **`ui-unit` joined `AllSuites`.** W3 left that decision to W4 *"when it has something to assert"*,
  and it now has: rung 2 over `DodonaUi` and `RecognizerContract` exist nowhere else, and
  `tests\Dodona.Tests` cannot load a net8.0-windows assembly. Measured **4.4-4.5 s warm**, and it
  stays SOLO (it compiles `DodonaUi`, whose output every window suite copies its binaries from), so
  it is ~4.5 s of serialized wall clock on a full run that measures 258-312 s against a 300 s
  budget. **The budget was not raised**; the pressure there is issue #1.
- **`Ledger-Live` reads BOTH TRXes** and `Ledger-Key` namespaces both unit suites (`unit/<leaf>`,
  `ui-unit/<leaf>`). W3 recorded this as W4's, *"in the commit that gives it rows to count"*.
  `--capture` was generalised the same way but **was not run**: the baseline stays frozen at its
  964 rows and the eleven new names are declared in `added.tsv` instead, which is what that file is
  for.

### WHAT W4 DID NOT DO

- **`Poses` is not anchored.** Plan 3.6 tabulates it as `Interface + Contract`, but it does not
  match the `Fake*`/`Recording*` name rung 1 enforces, no `PosesContract` is in W4's file list, and
  `Poses_cover_every_snapshot_member` is named in 3.3 without being one of W4's eight reds.
  Inventing one here would be W4 quietly doing W8's work. **It is a gap and this is it being
  recorded as one**: today the ledger's population is `FakeRecognizer` plus the corpus row.
- **"A `KnownDivergence` may not land in the same commit as the code that needed it"** (plan 3.2) is
  NOT implemented. It is a statement about a commit's contents, and rung 1 runs against a working
  tree with no commit in view; every cheap approximation of it was a rule that fires on the wrong
  thing. Named here rather than left to be discovered.
- **`owner_body_sha` is still empty** and `--rehash` is still not implemented. Both are W7.
- **`RecordingLaneSink` and `RecordingTransport` do not exist**, so rung 2 over the `Dodona`
  assembly finds nothing today. Three of its four facts are vacuously true there, which would be
  the empty-set failure again if nothing noticed -- two things notice, and both are deliberate:
  `The_assemblies_this_project_loads_are_exactly_the_ones_it_is_declared_to_cover` fails if the
  explicit list ever stops matching the register in EITHER direction, and rung 1 is a text scan
  that cannot miss an assembly.

## The wire owners have not been audited, and every one looked at so far was wrong

`wires.tsv` names, for each of the 52 wires, the single check that would fail most loudly if that
wire were cut. Those owners were carried from the six survey files. **They were not independently
verified against the code**, and three have now been found defective -- all three found by
accident, while doing something else:

| wire | owner | what is wrong |
|---|---|---|
| **E7** | `m2:tier0_message_delivered` | **Under-proves.** It asserts only that the text ARRIVED in the target lane, which the focus fallback also achieves. Run under a mutant that removed the instant path it came back **VACUOUS** -- green with the wire cut. Its sibling `tier0_prefix_routes` asserts the RUNG (`-> SKY (tier 0)`). **Owner re-pointed to it.** Found by W3's mutant. |
| **F1** | `the_newline_survived_to_the_agent` | **Over-claims.** Reads `pane_events`, so it proves UI to daemon only, while its own comment claims "all the way to the agent's stdin". Must be strengthened to assert the agent's own echo before it owns the row. Found while building the register. |
| **F2** | `ui-grid:enter_still_sends` | **Structurally cannot prove it.** `ui key enter` calls `InputKey` directly and never goes through the `PreviewKeyDown` handler, so swapping the handler registration breaks the real app with every suite green. Issue #16. Found while merging the register. |

**Three for three.** Nobody has looked at the other 49.

That is not an argument that the register is bad -- it is the argument for the mechanism the plan
already specifies. An owner is a CLAIM about what a check proves, and this repo's standing rule is
that a claim in prose is not enforcement. The `Wire` block (W7) and `owner_body_sha` exist to make
an owner's body reviewable and its silent narrowing detectable; until they land, every owner is
prose.

**So the rule for anyone moving checks: prove the owner before you rely on it.** A slice's mutant
should redden the wire's owner, and an owner that stays green under a defect in its own wire is a
finding, not a nuisance -- write it down and re-point the row. `dev prove --with` makes that
mechanical, and it is exactly how E7 was caught.
