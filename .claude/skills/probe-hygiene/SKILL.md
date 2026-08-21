---
name: probe-hygiene
description: Rules for writing an ad-hoc script that starts a Dodona daemon, shim, agent, or UI outside the acceptance suites — a one-off probe, a repro, or a measurement. Use before launching any Dodona binary by hand. Covers isolating DODONA_HOME, where binaries must come from, which commands are safe to observe with, and not disturbing work that is already running.
---

# Probing without making a mess you then misdiagnose

The suites are disciplined because `tests/_workspace.ps1` makes them so. **An ad-hoc script
inherits none of that**, and this is where the damage comes from.

## 1. Isolate `DODONA_HOME`, always

```powershell
. "$repo\tests\_workspace.ps1"
$null = Use-IsolatedDodonaHome 'myprobe'
```

Without it a probe writes into the operator's real workspace registry, and a probe of a
*refusal* path can refuse one of their real repos. `%LOCALAPPDATA%\Dodona\` is their live
state, not scratch space.

## 2. Binaries come from `Use-TestBinaries`, never from `src\*\bin`

```powershell
$bin = Use-TestBinaries $repo      # a copy, in your own DODONA_HOME
$dodona = "$bin\dodona.exe"; $env:DODONA_SHIM = "$bin\DodonaShim.exe"
```

A process launched from `src\<project>\bin\Release` **holds the file the compiler must
overwrite**, and the next build fails with ten screens of MSB3026 that read as "your code is
broken". CLAUDE.md §2 forbids that invocation for exactly this reason.

Phase 3's own session did it anyway: a probe started a shim from `src\DodonaShim\bin\Release`,
blocked the next build, and made one suite run against **stale binaries** whose results were
briefly believed. The daemon refuses to autostart from a source-tree build output
(`Ver.IsSourceTreeBuildOutput`); the shim does not, because it is deliberately dependency-free
(§13). So this rule is the only thing standing there.

## 3. A `dodona` verb has TWO ways of not being read-only, and it needs both

**It can SUMMON a daemon.** A summoned daemon runs its warm-up: the router, the brain and the
compressor pool, each a real `claude -p --model haiku` lane. That is **two** lanes in a folder
with no `dodona.json` and **four** in this repo — not the "five" this section used to claim,
which was copied from a code comment that had never been counted. It happened on 2026-08-19:
a session ran `status` twice as a health check, then spent two hours misdiagnosing its own
leaked agents as "machine contention".

**It can ADOPT a folder.** Different property, and this section used to promise only the first
one. On 2026-08-21 a session ran `dodona where --root <the operator's other project>` — listed
right here as safe — and registered that folder as a workspace. With a legacy `.dodona\store.db`
in it, the same call moves the store out and writes a file in.

Adoption is closed now: **a typed `--root` names a path, it does not adopt one**, and the
refusal names what to do instead. It takes `--adopt` to create. Summoning is still a
hand-maintained list, so it is still on you.

Safe to observe with — these neither start a daemon nor adopt a folder:

```powershell
dodona version [--json]   # what a binary is. The ONLY verb that writes nothing at all
dodona status             # reports ASLEEP; no longer summons
dodona where [--json]     # ids, paths, pipe names, whether a daemon is LIVE
dodona ps                 # what is actually running — but it DELETES stale shim-lane<N>.json
dodona land-status <n>    # a land in flight or its outcome
dodona ticket-record <n>  # the completion record
```

**Everything else summons — including verbs that read like questions**: `swaps`, `policy`,
`repos`, `tail`, `repo-status`, `claim-check`, `token-status`, `questions`, `tickets`, and every
`concierge-*` verb. `repos` and `token-status` also write a row. That is issue #13; until it
lands, assume a verb summons unless it is in the block above.

**And the rule that outlives all of these lists: `cat` is always safe, a `dodona` verb is not.**
If you are pointing one at a path the operator did not hand you, read what it does first.

## 4. No machine-wide mutation while a verification is in flight

`dodona stop-all --lanes --orphans` **works now** — it never did before Phase 3, because
`orphans` was missing from the argument parser's flag list, so the escape hatch the "LEFT
ALONE" message tells you to use did nothing. Now that it functions, its blast radius includes
**another session's suite run**: Phase 3 ran it mid-proof and killed one of that proof's lanes,
then had to re-run the proof because the verdict was no longer cleanly attributable.

Check first (`dodona ps`, or look for `dev` output in flight), and prefer a scoped stop.

## 5. Never kill by process name

```powershell
Get-Process DodonaShim | Stop-Process -Force    # NEVER
```

This murdered the operator's live session mid-trial. Resolve pids **by path** with
`Get-ProcessesUnder`/`Stop-ProcessesUnder`, or from that workspace's own `shim-lane*.json`.
A path a probe created is an identity; a process name is a category, and the operator's work
is in it.

## 6. Clean up in a `finally`, then check you actually did

```powershell
finally {
    Stop-ProcessesUnder $bin
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
}
```

Then confirm: zero processes under your probe's paths, and zero lane pipes you created. A
`.ps1` that fails to parse never reaches `finally` at all, so parse-check it first (see the
`file-patching` skill).
