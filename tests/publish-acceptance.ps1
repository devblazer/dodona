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
    $r = Join-Path (Use-SuiteTemp) ("dodona-pub-$n-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
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
    $foreignHome = Join-Path (Use-SuiteTemp) ("dodona-foreign-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $foreignHome | Out-Null
    $foreignRoot = Join-Path (Use-SuiteTemp) ("dodona-foreignws-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
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
    $orphan = Join-Path (Use-SuiteTemp) ("dodona-orphan-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force "$orphan\.dodona" | Out-Null
    Set-Content "$orphan\.dodona\store.db" "pretend-store"
    # Make it look like a pre-workspace daemon owns it, by holding the legacy ctl pipe name.
    # --adopt: this fixture MEANS to migrate the orphan, and a bare --root stopped creating in
    # issue #12 (naming a path is not adopting it).
    $legacyPipe = (Dx @('where', '--root', $orphan, '--adopt', '--json'))  # resolves + migrates: no daemon holds it
    Check 'an_unheld_legacy_store_migrates_normally' ($legacyPipe -match '"store"') $legacyPipe

    # ---- THE CONCIERGE ACTUALLY TAKES THE SWAP (issue #9) -------------------------------
    # `all_includes_the_concierge` above asserts that publish SAYS "swapping concierge". It said
    # that for two days while the concierge understood ten commands, `swap` was not one of them,
    # its dispatch had no `default:`, and the command was discarded in silence. Measured on the
    # operator's machine 2026-08-21 at f346b76, right after a publish that printed that line:
    # the workspace daemon on build 20260821-105924, the concierge still on 20260819-212126.
    # So: assert the OUTCOME, not the intention.
    #
    # A distinguishable build with no second compile: Ver.Build is the assembly version plus the
    # mtime of dodona.dll, so a copy of these same binaries with a different mtime IS a different
    # build as far as every part of this system is concerned.
    $bin2 = Join-Path $out 'bin2'
    New-Item -ItemType Directory -Force $bin2 | Out-Null
    Copy-Item "$bin\*" $bin2 -Recurse -Force
    (Get-Item "$bin2\dodona.dll").LastWriteTimeUtc = [datetime]::new(2026, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
    $newBuild = ((& "$bin2\dodona.exe" version --json) | ConvertFrom-Json).build
    $cxStatusBefore = Dx @('concierge-status')
    $cxPidBefore = if ($cxStatusBefore -match 'concierge pid=(\d+)') { [int]$Matches[1] } else { 0 }
    # A fixture assertion, not a product one: if the copy is not a DIFFERENT build then the
    # check below would pass without anything having swapped at all.
    Check 'the_copied_binary_is_a_distinguishable_build' `
        ($cxPidBefore -gt 0 -and $newBuild -like '*20260102030405' -and $cxStatusBefore -notmatch [regex]::Escape($newBuild)) `
        "pid=$cxPidBefore newBuild=$newBuild"

    $pubCx = Dx @('publish', '--exe', "$bin2\dodona.exe", '--workspace', 'alpha', '--concierge', '--mode', 'now')
    # Asserted on the PROCESS and on what the successor says about itself -- never on an
    # instantaneous pipe read, which blinks out while the swap hands the ctl pipe over
    # (check-authoring rule 2). The wait covers everything the checks below assert.
    $cxStatusAfter = ''
    Wait-Until {
        $script:cxStatusAfter = Dx @('concierge-status')
        ($script:cxStatusAfter -match "build=$([regex]::Escape($newBuild))") -and $cx.HasExited
    } 40000 'the concierge swaps to the new build and the old process exits' | Out-Null
    $cxPidAfter = if ($cxStatusAfter -match 'concierge pid=(\d+)') { [int]$Matches[1] } else { 0 }

    Check 'a_swapped_concierge_reports_the_new_build' `
        (($cxStatusAfter -match "build=$([regex]::Escape($newBuild))") -and $cxPidAfter -gt 0 -and $cxPidAfter -ne $cxPidBefore) `
        "want=$newBuild pidBefore=$cxPidBefore pidAfter=$cxPidAfter status=$(($cxStatusAfter -replace '\s+', ' '))"
    # The status line could in principle be told by the same old process; the process could not.
    Check 'the_old_concierge_process_is_gone_after_the_swap' ($cx.HasExited) `
        "pid $cxPidBefore still alive; publish said: $(($pubCx -replace '\s+', ' '))"

    # ---- PUBLISH REPORTS OUTCOMES, AND NAMES A TARGET THAT DID NOT SWAP (issue #9) -------
    # This is the half that stops the NEXT silent no-op. A target that answers nothing at all is
    # exactly what the concierge was, and at the wire it is indistinguishable from success: no
    # `error:` line, so `Client` returns 0. The fixture reproduces that deterministically -- a
    # plain named-pipe server holding a registered workspace's ctl pipe name, which accepts the
    # connection, reads the request and says nothing. It is what an OLDER build on the far end
    # of a partial swap looks like, and no `default:` branch on the receiving side can help.
    $muteRoot = Join-Path (Use-SuiteTemp) ("dodona-pub-mute-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $muteRoot | Out-Null
    Dx @('workspace-create', '--name', 'mute', '--member', $muteRoot) | Out-Null
    $allWs = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    $muteId = (@($allWs) | Where-Object { $_.name -eq 'mute' }).id
    $mutePipe = "dodona-$muteId-ctl"
    $muteScript = Join-Path $out 'mute-server.ps1'
    Set-Content $muteScript @"
`$ErrorActionPreference = 'Stop'
while (`$true) {
    `$s = New-Object System.IO.Pipes.NamedPipeServerStream('$mutePipe', [System.IO.Pipes.PipeDirection]::InOut, 1)
    `$s.WaitForConnection()
    `$r = New-Object System.IO.StreamReader(`$s)
    try { `$r.ReadLine() | Out-Null } catch { }
    `$s.Dispose()
}
"@ -Encoding utf8
    $muteProc = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $muteScript) `
        -PassThru -NoNewWindow -RedirectStandardOutput "$out\mute.out" -RedirectStandardError "$out\mute.err"
    Wait-Until { Test-DodonaPipe $mutePipe } 20000 "the mute fixture holds $mutePipe" | Out-Null
    Check 'the_mute_target_really_is_live' (Test-DodonaPipe $mutePipe) $mutePipe

    $pubMute = Dx @('publish', '--exe', $dodona, '--workspace', 'mute', '--workspace', 'alpha', '--mode', 'now')
    $muteExit = $DODONA_EXIT
    # Collapsed first: captured native output is WRAPPED to the console width, so a regex
    # spanning a space can match today and fail tomorrow because a path got longer (CLAUDE.md
    # 0.2). ASCII only in the pattern for the same family of reasons -- redirected child stdio
    # defaults to the OEM codepage and an em dash arrives mangled.
    $muteFlat = ($pubMute -replace '\s+', ' ')
    Check 'publish_names_a_target_that_did_not_take_the_build' `
        (($muteFlat -match 'mute \([^)]+\): ANSWERED NOTHING') -and ($muteFlat -notmatch 'alpha \([^)]+\): ANSWERED NOTHING')) `
        $muteFlat
    # ...and it FAILS. `accepted` needs only one target to succeed, so alpha taking the build
    # used to make the whole publish look successful no matter what anyone else did.
    Check 'publish_fails_when_a_target_did_not_take_the_build' ($muteExit -ne 0) `
        "exit=$muteExit $muteFlat"

    Dx @('concierge-stop') | Out-Null
    # ---- provenance: only a build we PERFORMED may claim what it was built from ----------
    # Auto-publish asks "has main moved?", and answers by comparing `git rev-parse main` to the
    # commit the running build was made from. It used to ask "am I newer than my sources?" and
    # answer with mtimes: the newest .cs/.xaml/.csproj across ALL THREE projects against the
    # mtime of the ONE binary the daemon runs. Edit src\DodonaUi\MainWindow.xaml.cs, MSBuild
    # correctly skips the up-to-date Dodona project, the publish copy preserves LastWriteTime,
    # and dodona.exe's mtime can NEVER catch up -- so the condition stayed true forever: 64
    # auto-publishes and 72 daemon restarts in one afternoon, a full three-project build every
    # ~65 seconds, four consecutive swaps reporting the byte-identical
    # `sources 15:56:19 > image 15:55:55`.
    #
    # THIS CHECK USED TO ASSERT THE ABSENCE OF A `.built-from` FILE. That file is gone (P2.4 is
    # a deletion), so the old assertion became vacuously true -- it would have passed against
    # any build forever, which is precisely the green check nobody has seen fail. What it was
    # really protecting is asserted directly instead: a `--exe <prebuilt>` publish compiled
    # nothing, so the binary must claim NOTHING about a commit.
    #
    # The binaries under test are a copy of a `dev build` image, which is exactly the
    # unknown-provenance case. Note the .NET SDK writes a bare commit SHA into
    # InformationalVersion by itself, so "the suffix is non-empty" would NOT be a valid test;
    # Ver only accepts its own `c=` marker, and that distinction is what this asserts.
    $vj = Dx @('version', '--json') | ConvertFrom-Json
    Check 'prebuilt_publish_claims_no_provenance' ($vj.commit -eq '' -and -not $vj.trial) "commit=$($vj.commit)"

    # ...and it SAYS SO rather than guessing. The old code degraded to the image's own mtime
    # whenever the stamp was missing -- the loop-prone comparison wearing a fallback, reached
    # every single time by `--exe`. A daemon that cannot tell whether main moved must announce
    # that it is not watching, because a silent degrade is a bug (CLAUDE.md section 3: the
    # routing ladder was green and dead in production for two days, and its only symptom was a
    # status-line suffix nobody reads).
    $apRoot = Join-Path (Use-SuiteTemp) ("dodona-pub-ap-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $apRoot | Out-Null
    New-Item -ItemType Directory -Force "$apRoot\src\Dodona" | Out-Null
    # autoPublish on, and a src\Dodona\Dodona.csproj so the watcher gets PAST its
    # "not a Dodona source tree" guard and reaches the provenance question this asserts.
    Set-Content "$apRoot\src\Dodona\Dodona.csproj" '<Project />' -Encoding utf8
    Set-Content "$apRoot\dodona.json" '{ "main": "main", "autoPublish": true }' -Encoding utf8
    Dx @('workspace-create', 'apnoprov', '--root', $apRoot) | Out-Null
    $apWs = (Dx @('where', '--workspace', 'apnoprov', '--json') | ConvertFrom-Json)
    # DODONA_NO_AUTOSTART is set for this whole suite and the watcher declines under it, so this
    # one daemon runs with it CLEARED -- the operator's own path (CLAUDE.md section 3: every
    # suite must exercise at least one path the way the operator runs it). Cleared on the parent
    # and restored in the finally, because Start-Process -Environment is PowerShell 7.4+ and
    # this repo is 5.1 (section 0.2's family of traps).
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    $apDaemon = Start-Process -FilePath $dodona -ArgumentList @('daemon', '--workspace', 'apnoprov') `
        -PassThru -NoNewWindow -RedirectStandardOutput "$out\ap-noprov.out" -RedirectStandardError "$out\ap-noprov.err"
    try {
        # ASKED OF THE STORE, and asserting what the code ACTUALLY DOES. This check was stale
        # twice over and so had never once run: it invoked `dodona feed`, which is not a command
        # (only `concierge-feed` exists), and it grepped for "NOT watching" -- wording from a
        # design that was deliberately replaced. Parking with auto-publish silently off was
        # judged a violation of the never-stuck directive, so an unstamped build now ARMS
        # itself: it announces that it cannot tell whether main moved, publishes main once, and
        # only surrenders if the result is still unstamped (Daemon.StartDriftWatcher).
        #
        # So the assertion is that it neither guesses nor goes quiet: an
        # `autopublish_no_provenance` event exists, and the operator was told in the feed.
        # Nothing is actually published here -- $apRoot is not a git repository, so the
        # bootstrap's own Git.IsRepo guard stops it.
        $ev = ''
        $ann = ''
        Wait-Until {
            $script:ev = (python -c "
import sqlite3
db = sqlite3.connect(r'$($apWs.store)')
print(db.execute('''SELECT COUNT(*) FROM events WHERE kind='autopublish_no_provenance' ''').fetchone()[0])
") | Out-String
            $script:ann = (python -c "
import sqlite3
db = sqlite3.connect(r'$($apWs.store)')
r = db.execute('''SELECT body FROM pane_events WHERE kind='announcement' AND body LIKE '%commit stamp%' ORDER BY id DESC LIMIT 1''').fetchone()
print(r[0] if r else '')
") | Out-String
            $script:ev.Trim() -ne '0' -and $script:ann -match 'commit stamp'
        } 25000 'the unstamped build records that it has no provenance and says so' | Out-Null
        Check 'no_provenance_daemon_refuses_to_guess' `
            ($ev.Trim() -ne '0' -and $ann -match 'no commit stamp' -and $ann -match 'arm itself') `
            "event_count=$($ev.Trim()) announcement=$($ann.Trim())"
    }
    finally {
        $env:DODONA_NO_AUTOSTART = "1"          # the rest of the suite owns daemon lifetime again
        Dx @('stop-daemon', '--workspace', 'apnoprov') | Out-Null
        if ($apDaemon -and -not $apDaemon.HasExited) { try { Stop-Process -Id $apDaemon.Id -Force } catch { } }
        # AND THE LANES THAT DAEMON SPAWNED. This is the only place in the suite where autostart is
        # on, so it is the only place a warm-up creates utility lanes -- and a lane outlives its
        # daemon BY DESIGN, with a 30-minute lease. Stopping the daemon therefore left real orphans
        # behind, which the runner then reaped and reported: "publish-acceptance leaks four
        # DodonaShim on every successful run" was TRUE, and a later session wrongly explained it
        # away as a daemon caught mid-exit after grepping for `lane-start` and finding none.
        # Cleaning up after the one daemon we deliberately let warm up is the suite's job.
        Stop-WorkspaceShims $apWs.dir
        Remove-Item $apRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($n in 'alpha', 'beta') { Dx @('stop-daemon', '--workspace', $wsIds[$n]) | Out-Null }
}
finally {
    # `concierge-stop` reaches whichever concierge is live -- which after the swap check is the
    # SUCCESSOR, a process this suite never started and therefore has no object for. Autostart is
    # off for this suite, so this can only ever talk to one that is already up; it never summons.
    Dx @('concierge-stop') | Out-Null
    foreach ($p in @($daemons.Values) + @($cx, $foreignDaemon, $muteProc)) {
        if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } }
    }
    # ...and the successor by pid, if it outlived the stop. Resolved from THIS suite's own
    # status output, never by process name (CLAUDE.md 4).
    if ($cxPidAfter -and $cxPidAfter -gt 0) { try { Stop-Process -Id $cxPidAfter -Force -ErrorAction Stop } catch { } }
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
