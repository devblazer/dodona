# M0 acceptance test (design §16): kill the daemon mid-agent-turn; the session must not
# notice. Runs entirely on the fake agent — zero model calls, deterministic, free.
#
# Timeline:  t=0     daemon #1 up, lane SKY started (fake agent), slow 3s turn begins
#            as soon as the agent is running: daemon #1 MURDERED (turn in flight)
#            t≈0.3-3.8s  no daemon exists; the turn completes into the shim's buffer
#            t≈3.8s  daemon #2 starts: reconcile → reconnect → drain (deduped by seq)
#            then    a fresh message through daemon #2 — the SAME agent must answer.
#
# Every step above waits for the CONDITION it names (the pipe is up, the agent process
# exists, the drained result appears), not for a duration -- see Wait-Until in
# tests/_workspace.ps1. The single remaining Start-Sleep is the fake agent's own turn
# length, which by design nothing outside the shim can observe while no daemon exists.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm0'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in

$root = Join-Path $env:TEMP ("dodona-m0-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $root | Out-Null
$out = Join-Path $PSScriptRoot 'm0-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -ErrorAction SilentlyContinue

$results = [ordered]@{}
$ws = Get-WorkspacePaths $dodona $root
$storeDb = $ws.Store
$wsDir = $ws.Dir
$token = "SURVIVED-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
function Dodona([string[]]$a) { (& $dodona ($a + @('--root', $root))) | Out-String }

$shimInfo = $null
$d2 = $null
try {
    # ---- daemon #1, lane, slow turn ----
    $d1 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d1.out" -RedirectStandardError "$out\d1.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $lane = $Matches[1]
    Dodona @("say", "$lane", "sleep:3 then say $token") | Out-Null

    # ---- murder daemon #1 mid-turn ----
    # WAIT FOR THE TURN TO BE IN FLIGHT, do not guess at it. The shim-info file is written
    # when the shim is spawned and the agent is running once its childPid is alive; killing
    # before either exists would test nothing (there would be no orphan to survive), and a
    # fixed second was simultaneously too long here and too short on a loaded machine.
    Wait-Until { Test-Path "$wsDir\shim-lane$lane.json" } 15000 "shim-info for lane $lane" | Out-Null
    $shimInfo = Get-Content "$wsDir\shim-lane$lane.json" | ConvertFrom-Json
    Wait-Until { [bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue) } 15000 'the agent process is up' | Out-Null
    Stop-Process -Id $d1.Id -Force
    $results['daemon1_killed_mid_turn'] = if ($shimInfo) { 'PASS' } else { 'FAIL no shim-info' }
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 10000 'daemon #1 pipe is gone' | Out-Null
    $results['shim_alive_no_daemon']  = if (Get-Process -Id $shimInfo.shimPid  -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }
    $results['agent_alive_no_daemon'] = if (Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }

    # ---- the turn completes with NO daemon in existence ----
    # Nothing outside the shim can observe the buffered result while no daemon exists -- that
    # is the point of the test -- so this one wait is genuinely a duration, and it is the
    # fake agent's own 3s turn plus a margin rather than a number nobody can derive.
    Start-Sleep -Milliseconds 3800

    # ---- daemon #2: reconcile, reconnect, drain ----
    $d2 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d2.out" -RedirectStandardError "$out\d2.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # Reconnect-and-drain is what daemon #2 must do; wait for the DRAINED RESULT, which is
    # the thing the next three checks are about.
    # The token is in the PROMPT as well ("sleep:3 then say <token>"), so a bare -match is
    # satisfied by the echoed input. The wait has to be the predicate the checks use: a RESULT
    # line carrying the token. m4 shipped this bug for one run and it read as a product failure.
    Wait-Until {
        @((Dodona @("tail", "$lane", "50")) -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' }).Count -ge 1
    } 20000 'the orphaned result drains' | Out-Null

    $tail = Dodona @("tail", "$lane", "50")
    Set-Content "$out\tail.txt" $tail
    $hits = @(($tail -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' })
    $results['orphaned_result_landed'] = if ($hits.Count -ge 1) { 'PASS' } else { 'FAIL' }
    $results['landed_exactly_once']    = if ($hits.Count -eq 1) { 'PASS' } else { "FAIL (count=$($hits.Count))" }

    $status = Dodona @("status")
    $results['session_id_recorded'] = if ($status -match 'session=fake-') { 'PASS' } else { 'FAIL' }

    # ---- the session must not notice: same agent answers through daemon #2 ----
    $roundtrip = "ROUNDTRIP-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    Dodona @("say", "$lane", "say $roundtrip") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match [regex]::Escape($roundtrip) } 20000 'the same agent answers through daemon #2' | Out-Null
    $tail2 = Dodona @("tail", "$lane", "10")
    $results['same_agent_answers_daemon2'] = if ($tail2 -match [regex]::Escape($roundtrip)) { 'PASS' } else { 'FAIL' }

    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($shimInfo) {
        foreach ($p in @($shimInfo.shimPid, $shimInfo.childPid)) {
            try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { }
        }
    }
    if ($d2 -and -not $d2.HasExited) { try { Stop-Process -Id $d2.Id -Force } catch { } }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M0 ACCEPTANCE ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
# The tally is not decoration: tools\dev.ps1 reads THIS line to decide whether the suite ran,
# and until 2026-08-19 m0 was the one suite that never printed it -- so a red m0 was
# indistinguishable from a green one for the entire life of the suite. dev.ps1 now treats a
# missing tally as a failure, which is the half of the fix that cannot be forgotten again.
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
