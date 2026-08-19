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
# THE HISTORY OF THIS COMMENT IS THE LESSON. It first said publish-acceptance leaves four
# DodonaShim behind every run -- true. A later session "corrected" that to "it starts no lanes at
# all, so it cannot leak a wrapper", having grepped the suite for `lane-start` and found none, and
# propagated the correction into four files. Wrong: that suite clears DODONA_NO_AUTOSTART for its
# `apnoprov` section on purpose, and an autostarting daemon's WARM-UP spawns the router, brain and
# compressor pool without any command naming a lane. Those shims then outlive the daemon by design,
# on a 30-minute lease. Real orphans, correctly reported.
#
# A grep for one spawn verb is not a survey of what a suite starts. The suite now stops the lanes
# its own daemon spawned, and publish reaps 0 (RECOVERY-PHASES Phase 3, "what this plan got wrong"
# item 2, which is struck through rather than deleted for this reason).
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
    Assert-IsolatedRegistry $dest
    $dest
}

# ---------------------------------------------------------------- is the registry really ours?

# THE REGISTRY MUST BE UNDER DODONA_HOME, AND THIS REFUSES TO RUN THE SUITE IF IT IS NOT.
#
# `registry.db` is the one machine-wide table in the product: every workspace name, id, alias
# and project row, for the whole machine. DODONA_HOME exists so a suite can create workspaces,
# migrate stores and exercise the repo-exclusivity REFUSAL without touching the registry the
# operator is using right now (CLAUDE.md 5) -- and the only thing that made that true was
# Paths.Registry deriving from Paths.Home. Nothing anywhere checked it.
#
# IT IS CHECKED BEFORE THE SUITE CREATES ANYTHING, because a check at the end reports a mess
# that has already been made. Measured on 2026-08-19, by breaking Paths.Registry on purpose to
# prove the P1.4 acceptance check red: the concierge suite promptly wrote THREE workspaces --
# `harbour`, `lighthouse` (plus the alias `rotation`) and `work` -- into the operator's REAL
# registry under %LOCALAPPDATA%\Dodona\concierge\, and two unrelated checks went red because the
# group-scope ladder was resolving against the operator's real workspaces instead of the
# fixture's. Undoing it took three `workspace-forget` calls against live state.
#
# Called from Use-TestBinaries so all twelve suites get it with no per-suite edit: every one of
# them calls that immediately after Use-IsolatedDodonaHome, and a guard that has to be added to
# twelve files is a guard that will be missing from the thirteenth.
#
# HOW IT ASKS: `workspaces --json` opens the Registry, which CREATES registry.db when it is not
# there -- so the file appearing under DODONA_HOME is evidence of where the BINARY resolved it,
# not of what this script believes. That command starts no daemon (a registry read plus a pipe
# enumeration), which is what makes it safe to run before the suite owns anything.
#
# It reconstructs `concierge\registry.db` by hand, which CLAUDE.md 5 otherwise forbids. That is
# deliberate and it is the point: this assertion IS the layout, so asking the binary where it put
# things would be asking the suspect to vouch for itself.
function Assert-IsolatedRegistry([string]$bin) {
    if (-not $env:DODONA_HOME) { throw "Assert-IsolatedRegistry: DODONA_HOME is not set -- call Use-IsolatedDodonaHome first" }
    $ErrorActionPreference = 'Continue'
    & "$bin\dodona.exe" workspaces --json 2>&1 | Out-Null
    $want = Join-Path $env:DODONA_HOME 'concierge\registry.db'
    if (Test-Path $want) { return }
    throw ("REFUSING TO RUN: the registry is not under DODONA_HOME, so this suite would write " +
           "into the machine-wide one. expected '$want'; DODONA_HOME='$env:DODONA_HOME'. " +
           "Paths.Registry must derive from Paths.Home (CLAUDE.md 5): a suite that creates " +
           "workspaces outside it litters the operator's real workspace list, and a test of the " +
           "repo-exclusivity refusal could refuse one of their real repos.")
}

# ---------------------------------------------------------------- TWO projects, live lanes

# A workspace with TWO projects (two `members` rows -- docs/GLOSSARY.md: a project is one
# folder), both git repos, ready for a daemon and live lanes.
#
# WHY THIS IS SHARED PLUMBING RATHER THAN A LOCAL FIXTURE (docs/LOCATIONS-PLAN.md P1.1). Every
# phase of that plan is about which project a lane opens in, and before this the tree could not
# express the question: the ONLY two-project fixture with a daemon anywhere in the suites
# (workspace-acceptance's `pair`) made a ticket and stopped, so NO suite had ever started a plain
# lane, or a brain, in a workspace with more than one project. Phases 2, 3 and 5 all need one, and
# three copies of it would drift.
#
# WHAT IT DELIBERATELY DOES NOT DO: start the daemon. Every suite starts daemons its own way
# (its own output redirection, its own $extraDaemons list to reap in `finally`), and a helper
# that spawned a process the caller does not know about is a leak waiting for a `finally` that
# was never told.
#
# The returned paths come back FROM THE REGISTRY, not from the strings we made the directories
# with -- the registry stores them canonicalized (Instance.Canonical resolves 8.3 names,
# junctions and casing), and `_primary` and `lanes.cwd` are both canonical. Comparing a check's
# idea of a path against a canonical one is a false red waiting to happen on the first machine
# whose TEMP is a junction.
#
# Members are ordered as attached, and Members[0] IS the workspace's `Primary`
# (Workspaces.cs:26) -- which is what every spawn site that has not yet been moved passes as a
# lane's working directory. So .A is "where a plain lane lands today" and .B is "the project a
# plain lane cannot reach yet", and that asymmetry is the whole point of the fixture.
function New-TwoProjectWorkspace([string]$dodona, [string]$name) {
    if (-not $env:DODONA_HOME) { throw "New-TwoProjectWorkspace: call Use-IsolatedDodonaHome first -- this CREATES a workspace and would litter the operator's registry (CLAUDE.md 5)" }
    $ErrorActionPreference = 'Continue'
    $made = @()
    foreach ($tag in 'a', 'b') {
        $d = Join-Path (Use-SuiteTemp) ("dodona-proj$tag-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force "$d\src" | Out-Null
        Set-Content "$d\src\main.cs" "// project $tag"
        Set-Content "$d\.gitignore" ".dodona/"
        git -C $d init -b main -q
        git -C $d add -A
        git -C $d -c user.email=t@t -c user.name=t commit -q -m init
        $made += $d
    }
    (& $dodona workspace-create --name $name --member $made[0] --member $made[1]) | Out-Null
    # ConvertFrom-Json emits a JSON ARRAY as ONE pipeline item in PS 5.1, so it lands in a
    # variable BEFORE anything filters it -- filtering in the same pipeline filters the array
    # object and `.name -eq 'x'` on an array returns matching ELEMENTS, which is truthy, so
    # every row passes (CLAUDE.md 0.2; it made three checks silent no-ops once).
    $all = (& $dodona workspaces --json) | ConvertFrom-Json
    $row = @($all) | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $row) { throw "New-TwoProjectWorkspace: '$name' was not created -- $($all | ConvertTo-Json -Compress)" }
    $members = @($row.members).path
    if ($members.Count -ne 2) { throw "New-TwoProjectWorkspace: '$name' has $($members.Count) project(s), not 2" }
    $w = (& $dodona where --workspace $row.id --json) | Out-String | ConvertFrom-Json
    [pscustomobject]@{
        Id      = $row.id
        Name    = $name
        A       = $members[0]                        # the FIRST project == the workspace Primary
        B       = $members[1]
        ALeaf   = (Split-Path -Leaf $members[0])
        BLeaf   = (Split-Path -Leaf $members[1])
        Store   = $w.store
        Dir     = $w.dir
        CtlPipe = $w.ctlPipe
        UiPipe  = $w.uiPipe
    }
}

# The `project=` field `dodona status` prints for one lane, or '' when it printed none. Shared
# because three suites ask it now, and because "none" and "the field was absent" are DIFFERENT
# answers (Projects.Field returns null for "nothing to say" and the literal string `none (cwd=)`
# for a lane whose folder no project owns) -- a check that conflated them would go green on the
# defect it exists to catch.
function Get-StatusProject([string]$statusText, [string]$lane) {
    $line = @($statusText -split "`r?`n" | Where-Object { $_ -match "^lane $lane\b" })
    if ($line.Count -ne 1) { return "<$($line.Count) lines matched 'lane $lane'>" }
    if ($line[0] -match 'project=(.+?)\s*$') { return $Matches[1] }
    return ''
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
    # PIN BOTH ENDS OF THE PIPE TO UTF-8. Carried in from compression-acceptance's local copy
    # (P7.3), where it fixed a real incident and where it was the ONLY copy that had it: a
    # redirected child's stdio defaults to the OEM codepage (CLAUDE.md 0.2) and python's stdout to
    # the ANSI one, so an em dash left the store as U+2014, went out as cp1252 0x97 and came back
    # decoded as cp850 -- and `blocked_uses_the_fixed_schema`, which compares against
    # [char]0x2014, failed in one shell and passed in another. A suite whose verdict depends on
    # which console started it is not a suite. m0 and brain never had this and were one em dash
    # away from the same thing.
    $prevEnc = [Console]::OutputEncoding
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
    $env:PYTHONIOENCODING = 'utf-8'
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
        [Console]::OutputEncoding = $prevEnc
        Remove-Item env:DODONA_TEST_SQL, env:DODONA_TEST_DB, env:PYTHONIOENCODING -ErrorAction SilentlyContinue
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
