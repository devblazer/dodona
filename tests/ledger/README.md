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

## W1.1 — the cross-suite duplicate scan

Run before anything captured a baseline, because a collision resolves at runtime as a `$results`
row **silently overwritten** and a tally one lower. Nothing in the repo detects that today;
`dev.ps1:584` records it as outside the runner's job by decision.

The scan is `sort | uniq -d` over every `Check '<name>'` plus `m0`'s inline `$results['<name>']`.

**Four matches. Two are real, and two are not** — the plan predicted three matches and one false
positive, so the second false positive below is a correction to it.

| name | verdict | disposition |
|---|---|---|
| `presence_idle_after_result` | **REAL** — `m2-acceptance.ps1:133` and `m3-acceptance.ps1:84` | m2's renamed to `presence_idle_after_result_in_status` |
| `double_uncertainty_asks_the_operator` | **REAL** — `brain-acceptance.ps1:229` and `concierge-acceptance.ps1:270` | concierge's renamed to `group_double_uncertainty_asks_the_operator` |
| `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` | not a duplicate | `m2-acceptance.ps1:330` and `:334` are the two arms of one `if/else`. One name, one runtime row |
| `grid_grows_to_the_number_of_lanes` | not a duplicate | `ui-grid-acceptance.ps1:436` is the check; `_workspace.ps1:409` is a **commented-out example inside a doc comment** |

**Two rules for `dev ledger`'s static rung fall out of that fourth row, and both are load-bearing:**

1. **Skip comment lines.** A scanner that does not will report `grid_grows_to_the_number_of_lanes`
   as a duplicate forever, and a permanent false positive in a gate assertion is how people learn
   to ignore it — the same disease as a gate that is always green.
2. **Uniqueness is a property of RUNTIME `$results` keys, not of source lines.** That is why the
   `if/else` row above is legal and must stay legal: both arms write the same key and exactly one
   of them executes. A source-line rule would have to forbid a perfectly correct idiom.

The renames are `renamed` rows in `moves/`, not `moved` rows: nothing changed layer, and the
assertion is byte-identical.
