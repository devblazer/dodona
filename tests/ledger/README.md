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
