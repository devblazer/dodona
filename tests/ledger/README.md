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
