# M2 live smoke — the first REAL claude lanes. Deliberately tiny (~4 haiku turns):
#   1. a real agent in a gated ticket worktree edits a CLAIMED file (gate allows)
#   2. the same agent is told to edit an OUT-OF-CLAIM file (gate must deny; file untouched)
#   3. the warm haiku router retargets a misrouted input from the focused lane (§4)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm2live'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) { $claude = "$env:USERPROFILE\.local\bin\claude.exe" }
$out = Join-Path $PSScriptRoot 'm2-live-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-m2l-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src\water", "$root\src\sky" | Out-Null
Set-Content "$root\src\water\sim.cs" "// water sim"
Set-Content "$root\src\sky\box.cs" "// skybox"
Set-Content "$root\.gitignore" ".dodona/"
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $global:DODONA_EXIT = 0; $o = (& $dodona ($a + @('--root', $root))) | Out-String; $global:DODONA_EXIT = $LASTEXITCODE; $o.Trim() }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function WaitForResult([string]$lane, [string]$pattern, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $t = Dodona @("tail", "$lane", "30")
        if ($t -match $pattern) { return $t }
        Start-Sleep -Seconds 2
    }
    return $null
}

$daemon = $null
try {
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800

    Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water") | Out-Null
    $wt1 = "$root\.dodona\wt\t1"

    # ---- real agent in the gated worktree ----
    $la = Dodona @("ticket-agent", "1", "--model", "haiku", "--child", $claude)
    Check 'real_agent_spawned' ($la -match 'lane (\d+)') $la
    $agentLane = $Matches[1]

    # WaitForResult patterns anchor on the tail row's kind column ('result  ...') so the
    # user_input echo of the instruction can never satisfy the wait.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Dodona @("say", "$agentLane", "Replace the entire content of src/water/sim.cs with exactly these two lines:`n// water sim`n// touched by dodona lane agent`nThen reply exactly: EDIT-DONE") | Out-Null
    $t = WaitForResult $agentLane 'result\s+.*EDIT-DONE' 120
    $sw.Stop()
    Check 'agent_edited_claimed_file' ((Get-Content "$wt1\src\water\sim.cs" -Raw) -match 'touched by dodona lane agent')
    $results['claimed_edit_turn_seconds'] = [math]::Round($sw.Elapsed.TotalSeconds, 1).ToString([Globalization.CultureInfo]::InvariantCulture)

    $skyBefore = Get-Content "$wt1\src\sky\box.cs" -Raw
    Dodona @("say", "$agentLane", "Now append the line '// sneaky' to src/sky/box.cs. If a gate denies you, do NOT work around it - stop and reply exactly: GATE-DENIED") | Out-Null
    $t = WaitForResult $agentLane 'result\s+.*GATE-DENIED' 120
    Check 'gate_denied_real_agent' ((Get-Content "$wt1\src\sky\box.cs" -Raw) -eq $skyBefore) "file was modified"
    Check 'agent_reported_denial' ($null -ne $t) "no GATE-DENIED result within 120s"

    # ---- warm router retargets a misroute ----
    $rs = Dodona @("router-start", "--model", "haiku", "--child", $claude)
    Check 'router_started' ($rs -match 'role router') $rs
    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -match 'lane (\d+)') { $sky = $Matches[1] }
    Dodona @("focus", "$agentLane") | Out-Null

    $r = Dodona @("input", "the skybox clouds are too sparse, make them denser")
    Check 'optimistic_delivery_instant' ($r -match '-> WATER \(focus, classifier running behind\)') $r
    $t = WaitForResult $sky 'skybox clouds' 60
    Check 'classifier_retargeted_to_sky' ($null -ne $t) "SKY never received the retargeted message"
    $tailWater = Dodona @("tail", "$agentLane", "40")
    Check 'receipt_in_wrong_lane' ([bool]($tailWater -match 'retargeted to SKY')) $tailWater

    $meta = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('SELECT tier, retargeted FROM routing_decisions ORDER BY id DESC LIMIT 1'): print(r)
for (d,) in db.execute('SELECT detail FROM events WHERE kind = ?', ('classified',)): print(d)
") | Out-String
    Check 'routing_row_retargeted' ([bool]($meta -match 'classifier.*1' -or $meta -match "'classifier', 1")) $meta
    if ($meta -match '(\d+)ms') { $results['classifier_latency_ms'] = $Matches[1] }

    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    Start-Sleep -Milliseconds 500
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    # shims own the claude processes; killing shims orphans claude children - kill by session file cwd
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M2 LIVE SMOKE ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
