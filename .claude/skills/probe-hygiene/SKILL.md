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

## 3. `dodona status` is NOT read-only — it SUMMONS a daemon

A summoned daemon runs its warm-up and spawns the router, brain and compressor pool: **five
real `claude -p --model haiku` processes** on a machine the operator believed was idle. It
happened on 2026-08-19, and the session then spent two hours misdiagnosing its own five leaked
agents as "machine contention".

Safe to observe with — none of these start anything:

```powershell
dodona where [--json]     # ids, paths, pipe names, whether a daemon is LIVE
dodona version [--json]   # what a binary is, including its commit
dodona ps                 # what is actually running, machine-wide
```

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
