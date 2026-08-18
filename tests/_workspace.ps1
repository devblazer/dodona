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

function Use-IsolatedDodonaHome([string]$tag) {
    $dir = Join-Path $env:TEMP ("dodona-home-$tag-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
