# Dispatcher-brain acceptance (§3's middle rung, model-free): the brain reviews BEHIND
# the instant code path and corrects visibly — silent unless it disagrees (operator rule).
# The fake agent plays both worker and brain (DODONA_LANE_ROLE), driven by directives
# embedded in the operator text, so every judgement here is deterministic.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$ui = "$repo\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'brain-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-brain-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\a.cs" "// a"
Set-Content "$root\.gitignore" ".dodona/"
Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $o = (& $dodona ($a + @('--root', $root))) | Out-String; $o.Trim() }
function Dump() { Dodona @('ui', 'dump') | ConvertFrom-Json }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function Rows([string]$sql) {
    $env:DODONA_TEST_SQL = $sql
    $o = (python -c "
import sqlite3, os
db = sqlite3.connect(r'$root\.dodona\store.db')
for r in db.execute(os.environ['DODONA_TEST_SQL']): print('|'.join('' if x is None else str(x) for x in r))
") | Out-String
    Remove-Item env:DODONA_TEST_SQL -ErrorAction SilentlyContinue
    $o
}

$daemon = $null
$uiProc = $null
try {
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800
    Dodona @("brain-start") | Out-Null
    Start-Sleep -Milliseconds 800
    Check 'brain_is_warm_and_off_grid' ((Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1') ''

    # ---- silent when it agrees: no announcement beyond the standard creation receipt ----
    Dodona @("input", "make the water foam softer") | Out-Null
    Start-Sleep -Seconds 2
    $agreeRows = Rows "SELECT COUNT(*) FROM events WHERE kind='brain_review' AND detail LIKE 'agree=True%'"
    Check 'brain_reviewed_and_agreed' ([int]($agreeRows.Trim()) -ge 1) $agreeRows
    $noise = Rows "SELECT COUNT(*) FROM pane_events WHERE kind='announcement' AND body LIKE '%dispatcher%'"
    Check 'agreement_is_silent' ([int]($noise.Trim()) -eq 0) $noise

    # ---- disagreement: rename applied + receipt with undo; ticket only suggested ----
    # The brain reviews lane BIRTHS; with a live lane the input would just route to it.
    Dodona @("lane-stop", "2") | Out-Null
    Start-Sleep -Milliseconds 600
    Dodona @("input", "brainname:SHORELINE brainticket:FOAM do the shoreline foam work") | Out-Null
    Start-Sleep -Seconds 2
    $lanes = Rows "SELECT id, title FROM lanes WHERE role='work' ORDER BY id"
    Check 'rename_applied' ($lanes -match 'SHORELINE') $lanes
    $renamed = Rows "SELECT COUNT(*) FROM events WHERE kind='brain_renamed'"
    Check 'rename_in_causal_chain' ([int]($renamed.Trim()) -eq 1) ''
    $receipt = Rows "SELECT body, acked FROM pane_events WHERE body LIKE 'renamed to SHORELINE%' LIMIT 1"
    Check 'rename_receipt_carries_undo' ($receipt -match 'undo: dodona lane-rename' -and $receipt -match '\|1') $receipt
    $suggestion = Rows "SELECT body, acked FROM pane_events WHERE body LIKE '%ticket-worthy%' LIMIT 1"
    Check 'ticket_is_suggested_not_created' (($suggestion -match 'ticket-create --title FOAM') -and ($suggestion -match '\|0') -and
        ((Rows "SELECT COUNT(*) FROM tickets").Trim() -eq '0')) $suggestion

    # ---- the operator's undo is real ----
    if ($lanes -match '(\d+)\|SHORELINE') { $laneId = $Matches[1] }
    Dodona @("lane-rename", "$laneId", "FOAMWORK") | Out-Null
    Check 'operator_rename_wins' ((Rows "SELECT title FROM lanes WHERE id=$laneId").Trim() -eq 'FOAMWORK') ''

    # ---- management roles are isolated from project context ----
    $cwds = Rows "SELECT detail FROM events WHERE kind='shim_spawned' ORDER BY id"
    $brainSpawn = ($cwds -split "`r?`n" | Where-Object { $_ -match 'lane3|lane1' }) -join ' '
    Check 'brain_runs_outside_the_project' ((Rows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned' AND detail LIKE '%neutral%'").Trim() -ge '1') $cwds

    # ---- the pulse: a routed message flashes the receiving pane ----
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Start-Sleep -Milliseconds 1800
    Dodona @("say", "$laneId", "say pulse check") | Out-Null
    Start-Sleep -Milliseconds 700
    $d = Dump
    $pane = $d.slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $laneId }
    Check 'pulse_on_arrival' ($pane.pulsing -eq $true) ($pane | ConvertTo-Json -Compress)
    Start-Sleep -Seconds 2
    $pane = (Dump).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $laneId }
    Check 'pulse_fades' ($pane.pulsing -eq $false) ''

    Dodona @("ui", "close") | Out-Null
    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    # Only this test's processes, resolved from its own shim-info files (CLAUDE.md §4).
    Get-ChildItem "$root\.dodona\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item "$root\.dodona\store.db" "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- BRAIN ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
