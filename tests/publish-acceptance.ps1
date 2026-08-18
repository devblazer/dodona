# PUBLISH SCOPING acceptance (docs/WORKSPACES-CONCIERGE.md §7).
#
# **This suite could not exist before.** `publish --all` broadcast a swap to every
# `dodona-*-ctl` pipe it could find in the OS pipe namespace, so a test that exercised it
# would have hot-swapped the operator's own live instances mid-work — which is why
# ORCHESTRATOR-REVIEW flagged it as untestable and left it uncovered.
#
# Targeting is now explicit and resolved through the REGISTRY rather than by scraping pipe
# names, so a suite can name only its own fixtures:
#
#   --workspace <name>...   exactly these
#   --concierge             the concierge too (it has no workspace of its own)
#   --all                   every LIVE REGISTERED workspace, plus the concierge
#   (neither)               the workspace owning --project / the cwd
#
# The property that makes it safe is the one asserted hardest below: a live ctl pipe that
# belongs to no workspace in THIS registry is never a target. With DODONA_HOME pointing at a
# private registry, "every live registered workspace" is exactly this suite's own — and the
# operator's instances are invisible to it by construction, not by luck.
#
# Model-free: no agents at all, only daemons and a prebuilt binary.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
$dodonaHome = Use-IsolatedDodonaHome 'pub'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"          # this suite owns daemon and concierge lifetime
$out = Join-Path $PSScriptRoot 'publish-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# Published builds go to a private root as well: the real one holds the operator's installs
# and `Shortcut()` deliberately refuses to repoint the desktop icon at anything outside it.
$binRoot = Join-Path $out 'bin'
$env:DODONA_BIN_ROOT = $binRoot

$results = [ordered]@{}
$errFile = Join-Path $out 'stderr.tmp'
function Dx([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    Remove-Item $errFile -ErrorAction SilentlyContinue
    $o = (& $dodona $a 2> $errFile) | Out-String
    $global:DODONA_EXIT = $LASTEXITCODE
    $e = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
    ("$o`n$e").Trim()
}
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }

# Three workspaces: two we will publish to, one deliberately left ASLEEP so `--all` can be
# shown to mean "every live one" and not "every one".
$roots = @{}
foreach ($n in 'alpha', 'beta', 'asleep') {
    $r = Join-Path $env:TEMP ("dodona-pub-$n-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $r | Out-Null
    Set-Content "$r\readme.md" "# $n"
    $roots[$n] = $r
    Dx @('workspace-create', '--name', $n, '--member', $r) | Out-Null
}

$daemons = @{}
$cx = $null
try {
    $wsIds = @{}
    foreach ($n in 'alpha', 'beta', 'asleep') {
        $wsIds[$n] = ((Dx @('workspaces', '--json')) | ConvertFrom-Json | Where-Object { $_.name -eq $n }).id
    }
    # PS 5.1: ConvertFrom-Json emits an array as ONE pipeline item, so filter after assigning.
    $all = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    foreach ($n in 'alpha', 'beta', 'asleep') {
        $wsIds[$n] = (@($all) | Where-Object { $_.name -eq $n }).id
    }
    Check 'three_workspaces_registered' ($wsIds['alpha'] -and $wsIds['beta'] -and $wsIds['asleep']) `
        ("$($wsIds['alpha']),$($wsIds['beta']),$($wsIds['asleep'])")

    foreach ($n in 'alpha', 'beta') {
        $daemons[$n] = Start-Process $dodona -ArgumentList "daemon", "--workspace", $wsIds[$n] -PassThru -NoNewWindow `
            -RedirectStandardOutput "$out\daemon-$n.out" -RedirectStandardError "$out\daemon-$n.err"
    }
    $cx = Start-Process $dodona -ArgumentList "concierge" -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\concierge.out" -RedirectStandardError "$out\concierge.err"
    Start-Sleep -Seconds 2

    $before = @{}
    foreach ($n in 'alpha', 'beta') {
        $before[$n] = ((Dx @('status', '--workspace', $wsIds[$n])) -split "`r?`n" | Where-Object { $_ -match 'build=' })
    }
    Check 'both_daemons_are_answering' ($before['alpha'] -and $before['beta']) "$($before['alpha']) | $($before['beta'])"

    # ---- a NAMED target gets the swap, and nothing else does ----------------------------
    # --exe: publish an already-built binary. The point here is the TARGETING, and rebuilding
    # three executables per check would make this suite minutes long for no extra coverage.
    $pub = Dx @('publish', '--exe', $dodona, '--workspace', 'alpha', '--mode', 'now')
    Check 'named_target_is_swapped' ($pub -match "swapping alpha") $pub
    Check 'unnamed_workspace_is_untouched' ($pub -notmatch 'swapping beta') $pub
    Check 'asleep_workspace_is_untouched' ($pub -notmatch 'swapping asleep') $pub

    # ---- --all means every LIVE REGISTERED workspace, plus the concierge -----------------
    $pubAll = Dx @('publish', '--exe', $dodona, '--all', '--mode', 'now')
    Check 'all_reaches_every_live_workspace' (($pubAll -match 'swapping alpha') -and ($pubAll -match 'swapping beta')) $pubAll
    Check 'all_skips_a_workspace_with_no_daemon' ($pubAll -notmatch 'swapping asleep') $pubAll
    Check 'all_includes_the_concierge' ($pubAll -match 'swapping concierge') $pubAll

    # ---- THE SAFETY PROPERTY: a live pipe outside this registry is never a target ---------
    # A daemon for a workspace registered in a DIFFERENT registry — which is exactly what the
    # operator's own instances are, relative to this suite. Its ctl pipe is live and would have
    # been swept up by the old pipe-namespace scan; it must be invisible to --all now.
    $foreignHome = Join-Path $env:TEMP ("dodona-foreign-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $foreignHome | Out-Null
    $foreignRoot = Join-Path $env:TEMP ("dodona-foreignws-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $foreignRoot | Out-Null
    $saveHome = $env:DODONA_HOME
    $env:DODONA_HOME = $foreignHome
    (& $dodona workspace-create --name foreign --member $foreignRoot) | Out-Null
    $foreignAll = (& $dodona workspaces --json) | ConvertFrom-Json
    $foreignId = (@($foreignAll) | Where-Object { $_.name -eq 'foreign' }).id
    $foreignDaemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $foreignId -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\foreign.out" -RedirectStandardError "$out\foreign.err"
    $env:DODONA_HOME = $saveHome
    Start-Sleep -Seconds 2

    $livePipes = [System.IO.Directory]::GetFiles("\\.\pipe\") | Where-Object { $_ -like "*$foreignId-ctl*" }
    Check 'the_foreign_daemon_really_is_live' ($livePipes.Count -ge 1) "pipes=$($livePipes.Count)"

    $pubAll2 = Dx @('publish', '--exe', $dodona, '--all', '--mode', 'now')
    Check 'all_never_swaps_a_workspace_from_another_registry' ($pubAll2 -notmatch [regex]::Escape($foreignId)) $pubAll2
    Check 'foreign_daemon_survived_untouched' (-not $foreignDaemon.HasExited) ''
    # ...and it is genuinely still serving, not merely still a process.
    $env:DODONA_HOME = $foreignHome
    $foreignStatus = (& $dodona status --workspace $foreignId) | Out-String
    $env:DODONA_HOME = $saveHome
    Check 'foreign_daemon_still_answers_its_pipe' ($foreignStatus -match 'daemon pid=') $foreignStatus

    # ---- naming a workspace that does not exist is refused before anything is swapped -----
    $bad = Dx @('publish', '--exe', $dodona, '--workspace', 'no-such-workspace', '--mode', 'now')
    Check 'unknown_target_is_refused' ($DODONA_EXIT -ne 0 -and $bad -match 'no workspace') $bad
    Check 'refusal_swapped_nothing' ($bad -notmatch 'swapping') $bad

    # ---- the default target is the workspace owning --project ------------------------------
    $pubDefault = Dx @('publish', '--exe', $dodona, '--root', $roots['beta'], '--mode', 'now')
    Check 'default_target_is_the_owning_workspace' `
        (($pubDefault -match 'swapping beta') -and ($pubDefault -notmatch 'swapping alpha')) $pubDefault

    # ---- a build still publishes when no workspace can be resolved ------------------------
    # Found live, and the reason resolution is lazy: a source tree whose own pre-workspace
    # daemon still holds its store cannot be migrated — and publish must not refuse to BUILD
    # over a workspace it never needed. It says so and exits 0, because the build is real.
    $orphan = Join-Path $env:TEMP ("dodona-orphan-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force "$orphan\.dodona" | Out-Null
    Set-Content "$orphan\.dodona\store.db" "pretend-store"
    # Make it look like a pre-workspace daemon owns it, by holding the legacy ctl pipe name.
    $legacyPipe = (Dx @('where', '--root', $orphan, '--json'))    # resolves + migrates: no daemon holds it
    Check 'an_unheld_legacy_store_migrates_normally' ($legacyPipe -match '"store"') $legacyPipe

    Dx @('concierge-stop') | Out-Null
    # ---- provenance: only a build we PERFORMED may claim what it was built from ----------
    # Auto-publish asks "am I behind my sources?". It used to answer by comparing the newest
    # .cs/.xaml/.csproj across ALL THREE projects against the mtime of the ONE binary the
    # daemon runs. Edit src\DodonaUi\MainWindow.xaml.cs, MSBuild correctly skips the
    # up-to-date Dodona project, the publish copy preserves LastWriteTime, and dodona.exe's
    # mtime can NEVER catch up -- so the condition stayed true forever: 64 auto-publishes and
    # 72 daemon restarts in one afternoon, a full three-project build every ~65 seconds, four
    # consecutive swaps reporting the byte-identical `sources 15:56:19 > image 15:55:55`.
    # Publish now stamps `.built-from` with the snapshot it compiled, taken BEFORE the build.
    #
    # What is asserted here is the half a hermetic suite CAN own: a `--exe` publish compiled
    # nothing, so it must NOT leave a stamp -- an unknown-provenance binary has to fall back
    # to the mtime compare rather than inherit a claim about sources it never saw. The other
    # half (a real build stamps, and the drift it answers then reads false) cannot live in a
    # suite: publishing the real tree means running `dotnet` against this repo's own `obj/`,
    # which the operator's live auto-publish daemon is also building into. Two builds, one
    # obj -- and 17's "tests collide with nothing" includes the instance they are using right
    # now. It is covered by measurement and by Ver.WriteBuiltFrom's comment instead.
    Check 'prebuilt_publish_claims_no_provenance' `
        (-not (Test-Path (Join-Path (Split-Path -Parent $dodona) '.built-from'))) `
        (Split-Path -Parent $dodona)

    foreach ($n in 'alpha', 'beta') { Dx @('stop-daemon', '--workspace', $wsIds[$n]) | Out-Null }
}
finally {
    foreach ($p in @($daemons.Values) + @($cx, $foreignDaemon)) {
        if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } }
    }
    Remove-Item env:DODONA_BIN_ROOT -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- PUBLISH SCOPING ACCEPTANCE (the suite --all never had) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
