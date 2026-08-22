---
name: check-authoring
description: Rules for writing or editing an acceptance check in tests/*-acceptance.ps1. Use whenever you add a check, change what a check asserts, add a suite, or are about to decide that a check passing means your change works. Covers what may be asserted on, how to query the store, and the proof a new check needs before it is worth anything.
---

# Writing a check that can actually fail

Every rule here is one this project already wrote down somewhere and then broke anyway,
which is why it is here — at the moment you are writing the check — instead of only in
CLAUDE.md.

## 1. A check is worth nothing until it has been seen RED

```powershell
dev prove m0:check_a m0:check_b brain:check_c     # many checks, one run per suite
dev prove m0 check_a                             # one check
```

Three verdicts, and they are not interchangeable:

- **PROVEN** — it failed against HEAD. Now it has teeth.
- **VACUOUS** — it passes against HEAD, so your change is not what makes it pass.
  **Rewrite it.** Do not shrug, do not "note it and move on".
- **MISSING** — it never ran. Usually a typo, or a check sitting behind an earlier
  failure in the same suite.

`dev prove` returns VACUOUS by design for a check about MACHINE state rather than code
state, and for repairing a stale TEST — it judges code. When you hit that, say which one
you hit and demonstrate the claim directly instead. Do not work around it.

## 2. Assert on PROCESSES and STORE ROWS. Never on an instantaneous pipe read

A lane pipe **blinks out of the namespace** for a few milliseconds while its shim swaps
server instances (measured: 8 of 192 reads over 1.5 s, `src/Dodona/LaneLiveness.cs`). So
`-not (Test-DodonaPipe $p)` does not mean "gone", it means "gone OR mid-reconnect" — and
inside a `Wait-Until` polling for twenty seconds it will eventually catch the gap and call
a live agent stopped. That is a **false green**, which costs exactly as much as a false red.

```powershell
Wait-Until { Test-DodonaPipeGone $pipe }                       # yes: two absences, 150ms apart
Wait-Until { -not (Get-Process -Id $shimPid -EA SilentlyContinue) }   # better: processes do not blink
Wait-Until { -not (Test-DodonaPipe $pipe) }                    # NO
```

Asserting on a pipe's absence is only safe once you have already proved the process is
gone. Phase 3 made this mistake four times *after* discovering it and writing it up.

## 3. Query the store with `Invoke-StoreSql`, never a local copy

```powershell
function Rows([string]$sql) { Invoke-StoreSql $storeDb $sql }
```

It throws on a sqlite error. A hand-rolled version pipes python's stdout and lets stderr
go nowhere, so a query naming a column that does not exist returns **empty** — and an
empty string cast to int is `0`, and `-eq 0` is a passing assertion. A Phase 3 check
written against `lane` instead of `lane_id` therefore passed against every build ever
made. **A check that passes because its query is broken is indistinguishable from one that
works.**

Column names are worth one look: the events table keys on `lane_id`, not `lane`.

## 4. Waits are conditions with deadlines, never sleeps

`Wait-Until { <condition> } <timeoutMs> '<what>'`. Never add a `Start-Sleep` — the few
that survive are real durations (a fake agent's own turn length, a lease) and say so in a
comment. A wait with no deadline is CLAUDE.md §0.1's standing directive violated in a new
costume.

Do not shorten a deadline to make a proof faster. A check that merely passes SLOWLY would
then read as PROVEN — a fake RED, which is worse than a fake green.

## 5. Every suite must print its tally

`<N> checks, <M> failed`. `tools/dev.ps1` reads that line to decide whether the suite ran
at all; m0 had no tally for its entire life, so a red m0 was indistinguishable from a green
one.

## 6. A check that matches a PRODUCT STRING is coupled to it, and `-notmatch` hides that

An assertion that greps for a message the product prints is a dependency on that message. A
POSITIVE match announces itself the moment the string changes — the check goes red and somebody
looks. A **negative** one does not: `-notmatch 'gate fail-open'` passes when the string changes,
passes when the string is deleted, and passes when the feature it was about is removed. It cannot
distinguish *"the bad thing did not happen"* from *"the bad thing can no longer be spelled"*.

Measured, 2026-08-22 (slice `S-GATE`): a `src/` commit renamed the marker
`gate fail-open:` to `gate could not check:`, correctly, because every path that reached it went
on to DENY and the log had been lying for months. That commit found and updated the two positive
matches in the suites. It could not see the third check, whose second arm was
`-notmatch 'gate fail-open'` — nothing emits that string any more, so the arm was **green and
blind**, and `m1` stayed at 128/0 throughout. It surfaced only because `dev prove --with` came
back **VACUOUS** on that exact check.

So:

- **Prefer a positive assertion.** Assert what the output IS, not what it is not.
- If a negative is genuinely the property (*"it must not claim a fail-open"*), **pin it to a
  string the product still emits** — assert the line is present AND says the right thing — so a
  rename breaks the check instead of silencing it.
- When you rename a product string, `grep` the suites for **both** `-match` and `-notmatch`. The
  second kind will not fail to tell you.
- The general form: **an assertion that cannot fail after an unrelated edit is not passing, it is
  absent.** This is the same family as a scan whose regex matches nothing and a query that returns
  empty — six incidents in one session, every one found by comparing two readings rather than by
  re-reading the code.

## 7. Name the incident in a comment

Not decoration. The next person reads it while debugging at speed, and "why is this check
here" is the question they cannot answer from the assertion alone.
