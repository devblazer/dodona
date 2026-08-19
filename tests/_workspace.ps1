# Shared test plumbing for workspaces (docs/WORKSPACES-CONCIERGE.md §1).
#
# Two things every suite now needs, and both exist for reasons the suites already care
# about (§17: tests collide with nothing, including the instance the operator is using
# right now):
#
#   1. AN ISOLATED REGISTRY. Workspace identity lives in a machine-global registry under
#      %LOCALAPPDATA%\Dodona. A suite that created workspaces there would litter the
#      operator's own workspace list, and a suite that tested the repo-exclusivity REFUSAL
#      could refuse one of their real repos. DODONA_HOME redirects the whole tree —
#      registry, workspace stores, shim-info files, the neutral cwd — into a temp folder
#      the suite owns and deletes.
#
#   2. WHERE THE STORE IS. It is no longer `<root>\.dodona\store.db`: a workspace is named
#      rather than located, so its store lives under the workspace id. Suites ask the
#      binary instead of reconstructing the path — `dodona where --json` exists for exactly
#      this, and it means a future relocation breaks nothing here.

# ---------------------------------------------------------------- one directory per suite run

# EVERY temporary thing a suite makes lives under here: its DODONA_HOME, its fake project
# roots, its scratch bin roots. One directory per suite run, and the RUNNER owns it.
#
# Why it exists (2026-08-19). Leaked processes are not idle: measured with 78 alive, a full run
# took 300 s instead of 87 s, m3 crashed outright and brain went red on nine timing checks. So
# the runner has to be able to clean up after itself.
#
# This comment used to say publish-acceptance leaves four DodonaShim behind every run. IT DOES
# NOT, and never did: that suite starts no lanes at all, so it cannot leak a wrapper. Measured
# at 69e8003 -- 0 reaped alone, 1 in a parallel wave, and the 1 is a `dodona` DAEMON still
# winding down from `stop-daemon` when the reaper looks. A race, not an orphan. The claim was
# repeated in three places and sent Phase 3's session hunting for the wrong bug; the runner
# names what it reaped now, so the two can never be confused again (RECOVERY-PHASES Phase 3,
# "what this plan got wrong").
#
# A shim can no longer outlive its agent (Phase 3), so this is no longer the only thing
# standing between a suite and an immortal process -- but it stays: a .ps1 that fails to PARSE
# never reaches its `finally` at all, and the suites kill daemons with -Force on purpose,
# which reaches no cleanup either.
#
# AND THAT IS WHY THE PATH MATTERS RATHER THAN THE PROCESS NAME. Cleaning "every DodonaShim
# under %TEMP%" would kill a CONCURRENT session's suite run stone dead -- this repo already
# deleted `stop-all` out of `dev build` for exactly that blast radius (P0.2), and killing by
# name once murdered the operator's live session (CLAUDE.md section 4). A directory the runner
# created for one child process is an identity nothing else can be inside.
#
# DODONA_TEST_SANDBOX is how tools\dev.ps1 hands it in. A suite run BY HAND still works: it
# makes its own, and cleans nothing, which is the same behaviour as before.
function Use-SuiteTemp {
    if ($script:DodonaSuiteTemp) { return $script:DodonaSuiteTemp }
    $dir = if ($env:DODONA_TEST_SANDBOX) { $env:DODONA_TEST_SANDBOX }
           else { Join-Path $env:TEMP ("dodona-suite-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $script:DodonaSuiteTemp = $dir
    return $dir
}

function Use-IsolatedDodonaHome([string]$tag) {
    # Inside the suite's sandbox, so the runner can clean up everything this suite started by
    # looking at one directory. See Use-SuiteTemp for why a path and not a process name.
    $dir = Join-Path (Use-SuiteTemp) ("dodona-home-$tag-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:DODONA_HOME = $dir
    $dir
}

# Resolve (creating or migrating if need be) the workspace that owns $root, and report
# where its state lives. Returns @{ Id; Name; Dir; Store; CtlPipe }.
function Get-WorkspacePaths([string]$dodona, [string]$root) {
    # Native stderr under $ErrorActionPreference='Stop' throws NativeCommandError even on a
    # clean exit (CLAUDE.md §0.2), and `where` deliberately narrates a first-time workspace
    # creation on stderr. Continue here, and read stdout only.
    $ErrorActionPreference = 'Continue'
    $json = (& $dodona where --root $root --json) | Out-String
    if (-not $json.Trim()) { throw "dodona where --root $root --json produced nothing" }
    $w = $json | ConvertFrom-Json
    [pscustomobject]@{
        Id      = $w.id
        Name    = $w.name
        Dir     = $w.dir
        Store   = $w.store
        CtlPipe = $w.ctlPipe
    }
}

# CLAUDE.md §4: never kill by process NAME — that murdered the operator's live session
# once. Resolve pids from THIS workspace's own shim-info files, which now live in the
# workspace directory rather than under the project root.
function Stop-WorkspaceShims([string]$wsDir) {
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
}

# ---------------------------------------------------------------- the binaries under test

# Copy the build outputs into $DODONA_HOME\bin and hand back that directory. Call it right
# after Use-IsolatedDodonaHome; point $dodona / $ui / $fake / $env:DODONA_SHIM at what it
# returns, and NOTHING in this suite ever executes out of src\...\bin again.
#
# Why this exists (docs/INVESTIGATION-2026-08-18.md RC3). src\<proj>\bin\Release is both the
# file MSBuild must overwrite AND where every suite launched its daemons, shims and agents
# from. A daemon deliberately outlives the window that started it, so a leaked one blocks the
# next build INVISIBLY: on 2026-08-18 four of them turned a fifteen-minute change into an
# hour, reported the whole time as "Build FAILED" -- which sent the reader hunting through
# their own code. A leak now leaks into a temp directory the suite already owns and deletes.
#
# Publish solved this for itself long ago by building into a private scratch bin; only the
# tests were left behind. This is the same move.
#
# ONE FLAT DIRECTORY, the way a published build lays them out, and that is not tidiness:
#
#  - m1 sets $dodona and nothing else, so a shim it spawns falls back to
#    AppContext.BaseDirectory\DodonaShim.exe (src/Dodona/Daemon.cs). Under the old paths that
#    resolved to src\Dodona\bin\Release\net8.0\DodonaShim.exe -- an orphan of an earlier
#    design that no ProjectReference maintains and that was measured 18 HOURS STALE on
#    2026-08-18, while three unkillable shims were running from it. Landing all four binaries
#    together fixes that whole class, rather than papering it over with a DODONA_SHIM line.
#  - the UI finds dodona.exe beside itself and the daemon finds DodonaShim.exe beside itself,
#    with no environment variable anywhere -- which is what makes a directory an application
#    instead of a build output.
#
# ORDER MATTERS: Dodona first, DodonaShim second. src\Dodona\bin also contains a stale
# DodonaShim.exe (the orphan above); copying the real shim project afterwards overwrites it.
function Use-TestBinaries([string]$repo) {
    if (-not $env:DODONA_HOME) { throw "Use-TestBinaries: call Use-IsolatedDodonaHome first" }
    $dest = Join-Path $env:DODONA_HOME 'bin'
    New-Item -ItemType Directory -Force $dest | Out-Null

    # DodonaUi's TFM is net8.0-windows, not net8.0. Spelled out rather than globbed: a glob
    # that silently matches nothing is how a suite ends up testing a binary that is not there.
    $sources = @(
        "$repo\src\Dodona\bin\Release\net8.0",
        "$repo\src\DodonaShim\bin\Release\net8.0",
        "$repo\src\DodonaFakeAgent\bin\Release\net8.0",
        "$repo\src\DodonaUi\bin\Release\net8.0-windows"
    )
    foreach ($src in $sources) {
        if (-not (Test-Path $src)) {
            throw "Use-TestBinaries: $src does not exist -- run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 build"
        }
        # File-by-file, not `Copy-Item $src\* $dest -Recurse`: copying a directory INTO an
        # existing directory of the same name (runtimes\, here, which three of the four
        # outputs have) nests it rather than merging it in PS 5.1.
        foreach ($f in @(Get-ChildItem $src -Recurse -File)) {
            $target = Join-Path $dest $f.FullName.Substring($src.Length + 1)
            $dir = Split-Path -Parent $target
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
            Copy-Item $f.FullName $target -Force
        }
    }
    $dest
}

# ---------------------------------------------------------------- did this suite leak?

# One assertion, called from every suite's finally: nothing may still be running out of
# $repo\src\*\bin. It REPORTS -- it never stops anything. The leaking suite's own cleanup
# owns the killing, and a check that quietly killed what it found would hide the very leak it
# exists to expose (and would be a kill this suite cannot prove belongs to it).
#
# Records a normal check in $results, so it lands in the suite's own tally and its FAIL line
# is picked up by tools\dev.ps1 like any other.
#
# ITS LIMIT, stated rather than papered over: a .ps1 that fails to PARSE never reaches
# `finally` (CLAUDE.md 0.2), so this cannot be the only guard -- `dev check` stays the
# backstop, and `dev gate` asserts it after a full run. It also cannot ATTRIBUTE: it sees the
# whole machine, so a daemon someone else left in this repo's build output fails whichever
# suite finishes first. That is still the right alarm; the pid and full path below say whose.
#
# The grace window is a condition-wait, not a sleep: Stop-Process returns before the process
# is actually gone, and a cleanup still in flight is not a leak. It exits the moment the
# leaks list is empty, so the common case costs one enumeration.
function Assert-NoBuildOutputProcesses([string]$repo, $results, [int]$graceMs = 4000) {
    $prefix = Join-Path $repo 'src'
    if (-not $prefix.EndsWith('\')) { $prefix += '\' }
    $deadline = [Environment]::TickCount + $graceMs
    while ($true) {
        # By PATH, never by name (CLAUDE.md 4): a name cannot tell a test's process from the
        # operator's live session, and killing by one once murdered theirs mid-trial.
        $leaks = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
                $path = $null
                try { $path = $_.Path } catch { }
                if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and $path -match '\\bin\\') {
                    "pid $($_.Id) $($_.ProcessName) $path"
                }
            })
        if ($leaks.Count -eq 0 -or [Environment]::TickCount -ge $deadline) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($leaks.Count -eq 0) {
        $results['no_process_left_in_the_build_output'] = 'PASS'
        return
    }
    $results['no_process_left_in_the_build_output'] = "FAIL $($leaks -join ' ; ')"
    Write-Output "LEAKED INTO THE BUILD OUTPUT -- these block the next build, invisibly:"
    foreach ($l in $leaks) { Write-Output "  $l" }
}

# ---------------------------------------------------------------- waiting on a CONDITION

# A wait is a CONDITION plus a DEADLINE, never a duration (CLAUDE.md §0.1: nothing may be
# hung, halted or stuck -- every wait names the thing that un-sticks it, and a condition-wait
# with no deadline is that standing directive violated in a new costume).
#
# WHY THIS EXISTS. Measured on 2026-08-19: the eleven suites took 5 min 20 s, of which
# 214 s -- 68 % -- was fixed `Start-Sleep`. Almost none of it was real waiting: a
# `Start-Sleep -Seconds 3` in front of a check is a guess about the slowest machine that
# ever ran it, paid in full on every machine since. The condition is already written down
# one line below, in the check itself; this just waits for THAT instead of for a clock.
# CLAUDE.md §1 recorded the opposite conclusion as measured fact ("the rest is inherent and
# cannot be optimised away"), which is how twenty-minute verification became something to
# skip rather than something to fix.
#
# ON TIMEOUT IT RETURNS $false AND SAYS SO -- it does not throw. The check that follows then
# fails on its own terms and prints the real value it saw, which is a far better diagnosis
# than a wait's own idea of what went wrong. So the idiom is: wait for the condition, then
# assert it.
#
# GETTING A VALUE BACK OUT. $Condition runs in a child scope, so a plain `$d = Dump` inside
# it is discarded. Assign to $script: and the suite body (which is script scope) sees it:
#
#     Wait-Until { $script:d = Dump; @($script:d.slots).Count -eq 3 } 8000 'three tiles' | Out-Null
#     Check 'grid_grows_to_the_number_of_lanes' ((@($d.slots).Count) -eq 3) "tiles=$(@($d.slots).Count)"
#
# Stopwatch, not [Environment]::TickCount, and that is not fussiness: TickCount is a signed
# 32-bit millisecond counter that wraps every 24.9 days, so `TickCount + $timeout` can
# overflow to a negative deadline -- which either times out instantly or, on the other side
# of the wrap, waits approximately forever. The second one is the hang this function exists
# to make impossible.
function Wait-Until {
    param(
        [Parameter(Mandatory = $true, Position = 0)][scriptblock]$Condition,
        [Parameter(Position = 1)][int]$TimeoutMs = 15000,
        [Parameter(Position = 2)][string]$What = '',
        # 250 ms, not the 120 ms this started at. Each poll of a UI condition SPAWNS
        # dodona.exe, and each poll of a UIA condition walks the desktop's window tree; with
        # eleven suites polling at once that cost is paid eleven times over and it showed --
        # ui-use went from 42.5 s alone to 69.3 s in the parallel wave, on a 22-core machine
        # that was nowhere near CPU-bound. A quarter-second of latency per condition is
        # invisible against what it replaced (a flat 2 to 3 second sleep).
        [int]$PollMs = 250
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # BACKOFF, not a flat interval, because a poll is not free: every UI condition here spawns
    # dodona.exe and every UIA one walks a window tree cross-process. Most conditions are true
    # within a few hundred milliseconds, so the first polls are quick; the ones that are really
    # waiting on a 14-second agent turn back off to $PollMs and stop paying for the answer
    # sixty times. Measured: a flat 120ms was costing ui-use more in process starts than the
    # sleeps it replaced were costing in idling.
    $wait = 60
    while ($true) {
        $ok = $false
        # A condition that throws is a condition that is not true YET -- a dump against a
        # window still starting, a store row that does not exist. Throwing out of the wait
        # would turn "not ready" into a suite crash.
        try { $ok = [bool](& $Condition) } catch { $ok = $false }
        if ($ok) { return $true }
        if ($sw.ElapsedMilliseconds -ge $TimeoutMs) {
            $desc = if ($What) { $What } else { ($Condition.ToString() -replace '\s+', ' ').Trim() }
            # STDERR, never Write-Output: this function returns a bool, and a Write-Output
            # here lands IN that return value -- `$ok = Wait-Until {...}` would come back as
            # a two-element array whose [bool] cast is always $true. A wait that reported
            # success on timeout would be the exact green-check-nobody-has-seen-fail this
            # phase exists to remove.
            [Console]::Error.WriteLine(("WAIT TIMEOUT after {0:N1}s: {1}" -f ($sw.Elapsed.TotalSeconds), $desc))
            return $false
        }
        Start-Sleep -Milliseconds $wait
        if ($wait -lt $PollMs) { $wait = [Math]::Min($PollMs, [int]($wait * 1.6)) }
    }
}

# Is a named pipe on the machine right now? Asked of the OS, which is the authority --
# Instance.LivePipes() in the daemon does exactly this, and for the same reason: a file we
# wrote is a record of what we intended, not of what is running (INVESTIGATION-2026-08-18
# RC2). The pipe namespace is flat, so this is one enumeration.
function Test-DodonaPipe([string]$name) {
    # An empty name matches an empty leaf in the enumeration and comes back TRUE, so a
    # wait on a pipe name nobody set would satisfy itself instantly. Refuse it.
    if (-not $name) { return $false }
    # [IO.Path]::GetFileName, NOT Split-Path -Leaf. Measured 2026-08-19: Split-Path returns
    # an EMPTY STRING for a pipe path, so the list came back as 962 empty strings, every
    # lookup missed, and Wait-Daemon sat out its full 20 s timeout twice in m0 while the
    # daemon had been answering the whole time -- a condition-wait made slower than the
    # sleep it replaced. Instance.LivePipes() in the daemon uses Path.GetFileName; so does
    # this now, which is the general lesson: match the code that already answers this.
    try { return @([System.IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -contains $name }
    catch { return $false }
}

# Wait for a daemon to be ANSWERING, not for a number of milliseconds. Every suite used to
# open with `Start-Sleep -Milliseconds 800` after starting a daemon; measured, the pipe is up
# in about 250 ms, and on a slow machine 800 ms was never enough anyway -- a fixed sleep is
# simultaneously too long and too short, which is the whole case against it.
function Wait-Daemon([string]$ctlPipe, [int]$TimeoutMs = 20000) {
    Wait-Until { Test-DodonaPipe $ctlPipe } $TimeoutMs "daemon pipe $ctlPipe"
}

# ---------------------------------------------------------------- asking the store, LOUDLY

# Run one SQL statement against a suite's store and return its rows as text.
#
# THIS FAILS LOUDLY, and that is the entire point of it existing (Phase 7, P7.1). Three suites
# grew their own copy of this that piped python's stdout and let stderr go wherever stderr went.
# So a query naming a column that does not exist produced an EMPTY result -- and `[int]''` is 0,
# and `-eq 0` is a passing assertion. Phase 3 shipped a check written against `lane` instead of
# `lane_id` which therefore passed against every build ever made, and would have passed forever:
# it was caught only because `dev prove` happened to include it in the proved set.
#
# A check that passes because its query is broken is indistinguishable from a check that works.
# That is the same disease as a green check nobody has seen red (CLAUDE.md 0.3), one layer down.
function Invoke-StoreSql([string]$db, [string]$sql) {
    if (-not $db) { throw 'Invoke-StoreSql: no store path' }
    # Native stderr is capturable ONLY with Continue + `2> file` (CLAUDE.md 0.2): under the
    # suites' `Stop`, python writing one warning line would throw NativeCommandError, and under
    # SilentlyContinue the record is eaten -- which is how this was invisible in the first place.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $errFile = Join-Path ([System.IO.Path]::GetTempPath()) ("dodona-sql-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".err")
    $env:DODONA_TEST_SQL = $sql
    $env:DODONA_TEST_DB = $db
    try {
        $out = (python -c "
import sqlite3, os
db = sqlite3.connect(os.environ['DODONA_TEST_DB'])
for r in db.execute(os.environ['DODONA_TEST_SQL']): print('|'.join('' if x is None else str(x) for x in r))
" 2> $errFile) | Out-String
        $err = ''
        if (Test-Path $errFile) { $err = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) }
        # Collapse first: captured native stderr is WRAPPED to the console width, so a newline
        # lands mid-sentence and any regex spanning a space breaks when a path gets longer.
        if ($err) { $err = ($err -replace '\s+', ' ').Trim() }
        if ($err) { throw "store query FAILED: $err  --  sql: $sql" }
        return $out
    }
    finally {
        $ErrorActionPreference = $prev
        Remove-Item env:DODONA_TEST_SQL, env:DODONA_TEST_DB -ErrorAction SilentlyContinue
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- pipes that BLINK

# Is this pipe REALLY gone? Two absences, 150 ms apart.
#
# A lane pipe blinks out of the namespace for a few milliseconds while its shim disposes one
# NamedPipeServerStream and constructs the next (src/Dodona/LaneLiveness.cs carries the
# measurement: 8 of 192 reads over 1.5 s saw nothing while the shim was alive and instantly
# connectable). So `-not (Test-DodonaPipe $p)` is not a test for "gone" -- it is a test for
# "gone OR mid-reconnect", and inside a Wait-Until that polls for twenty seconds it will
# eventually catch the gap and call a live agent stopped.
#
# Phase 3's own session made that mistake FOUR times, after discovering the blink and writing it
# up. Hence a function: the rule could not be remembered, so it is not a rule any more.
# ASSERT WITH THIS, never with a bare Test-DodonaPipe. The Start-Sleep is a real duration -- the
# blink window itself -- which is the only kind this repo allows (CLAUDE.md 3).
function Test-DodonaPipeGone([string]$name, [int]$SettleMs = 150) {
    if (-not $name) { return $false }
    if (Test-DodonaPipe $name) { return $false }
    Start-Sleep -Milliseconds $SettleMs
    return (-not (Test-DodonaPipe $name))
}

# ---------------------------------------------------------------- processes, BY PATH

# Every process running out of a given directory, resolved by EXECUTABLE PATH -- never by
# process name, ever (CLAUDE.md §4: a by-name query once murdered the operator's live
# session's shim and window mid-dogfood, because a name cannot tell their work from a test's).
#
# Get-Process is enumerated once and filtered on .Path, the same shape tools\dev.ps1's
# Blockers and LiveApp use. A path a suite owns -- a GUID temp directory -- is an identity;
# "DodonaUi.exe" is a category, and the operator's own window is in it.
#
# $dir is REQUIRED and must be non-empty: `-like "$dir*"` with an empty $dir matches every
# process on the machine, which is the by-name failure wearing a path's clothes. That is a
# throw rather than an empty result, because silently matching nothing and silently matching
# everything look identical at the call site.
function Get-ProcessesUnder([string]$dir) {
    if (-not $dir) { throw "Get-ProcessesUnder: refusing an empty directory -- it would match every process on the machine" }
    if (-not $dir.EndsWith('\')) { $dir += '\' }
    @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            if ($path -and $path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) {
                [pscustomobject]@{ Id = $_.Id; Name = $_.ProcessName; Path = $path }
            }
        })
}

# Stop everything running out of $dir. Same rule, same reason -- and it can only ever reach
# processes whose image lives in a directory the caller named.
function Stop-ProcessesUnder([string]$dir) {
    foreach ($p in (Get-ProcessesUnder $dir)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }
}
