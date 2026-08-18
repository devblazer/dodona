# M0 acceptance test (design §16): kill the daemon mid-agent-turn; the session must not
# notice. Runs entirely on the fake agent — zero model calls, deterministic, free.
#
# Timeline:  t=0   daemon #1 up, lane SKY started (fake agent), slow 6s turn begins
#            t≈1s  daemon #1 MURDERED (turn in flight)
#            t≈1-8s  no daemon exists; the turn completes into the shim's buffer
#            t≈8s  daemon #2 starts: reconcile → reconnect → drain (deduped by seq)
#            then  a fresh message through daemon #2 — the SAME agent must answer.

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
    Start-Sleep -Milliseconds 800

    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $lane = $Matches[1]
    Dodona @("say", "$lane", "sleep:6 then say $token") | Out-Null

    # ---- murder daemon #1 mid-turn ----
    Start-Sleep -Milliseconds 1000
    Stop-Process -Id $d1.Id -Force
    $results['daemon1_killed_mid_turn'] = "pid $($d1.Id), ~1s into a 6s turn"

    $shimInfo = Get-Content "$wsDir\shim-lane$lane.json" | ConvertFrom-Json
    Start-Sleep -Milliseconds 500
    $results['shim_alive_no_daemon']  = if (Get-Process -Id $shimInfo.shimPid  -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }
    $results['agent_alive_no_daemon'] = if (Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }

    # ---- the turn completes with NO daemon in existence ----
    Start-Sleep -Seconds 7

    # ---- daemon #2: reconcile, reconnect, drain ----
    $d2 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d2.out" -RedirectStandardError "$out\d2.err"
    Start-Sleep -Seconds 2

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
    Start-Sleep -Seconds 2
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
if ($failed.Count) { exit 1 } else { exit 0 }
