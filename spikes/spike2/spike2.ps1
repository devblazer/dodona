# Spike 2 — the shim survives daemon death with zero message loss (design doc §13, §16)
# Sequence: daemon A spawns shim → plants fact → fires slow turn → A is MURDERED mid-turn
#           → shim + claude must survive → daemon B connects to a shim it never spawned
#           → receives the backlog produced while nobody listened → recalls the fact.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $PSScriptRoot 'spike2-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -ErrorAction SilentlyContinue

$cmd = Get-Command claude -ErrorAction SilentlyContinue
$claudeExe = if ($cmd) { $cmd.Source } else { "$env:USERPROFILE\.local\bin\claude.exe" }

dotnet build "$repo\src\DodonaShim\DodonaShim.csproj" -c Release -v q -nologo | Out-Null
dotnet build "$PSScriptRoot\Spike2Client\Spike2Client.csproj" -c Release -v q -nologo | Out-Null
$shim   = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$client = "$PSScriptRoot\Spike2Client\bin\Release\net8.0\Spike2Client.exe"

$pipeName = "dodona-spike2-$PID"
$marker = "$out\markerA.txt"
$env:DODONA_SHIM_INFO = "$out\shim-info.json"
$results = [ordered]@{}

$shimInfo = $null
try {
    # ---- daemon A: spawns the shim itself (launcher-death independence) ----
    $pa = Start-Process $client -ArgumentList "A", $pipeName, $shim, $claudeExe, $marker `
        -RedirectStandardOutput "$out\clientA.out" -RedirectStandardError "$out\clientA.err" `
        -WorkingDirectory $repo -PassThru -NoNewWindow

    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (-not (Test-Path $marker)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "timeout waiting for daemon A to send turn 2" }
        if ($pa.HasExited) { throw "daemon A exited early (exit $($pa.ExitCode)) - see clientA.err" }
        Start-Sleep -Milliseconds 200
    }

    Start-Sleep -Milliseconds 900          # let turn 2 get in flight
    Stop-Process -Id $pa.Id -Force         # ---- daemon A dies mid-turn ----
    $results['A_killed'] = "daemon A (pid $($pa.Id)) killed ~0.9s after sending turn 2"

    $shimInfo = Get-Content "$out\shim-info.json" | ConvertFrom-Json
    Start-Sleep -Milliseconds 300
    $shimAlive  = $null -ne (Get-Process -Id $shimInfo.shimPid  -ErrorAction SilentlyContinue)
    $childAlive = $null -ne (Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)
    $results['shim_survived_daemon_death']   = if ($shimAlive)  { 'PASS' } else { 'FAIL' }
    $results['claude_survived_daemon_death'] = if ($childAlive) { 'PASS' } else { 'FAIL' }
    if (-not $shimAlive) { throw "shim died with daemon A - the whole point failed" }

    # ---- daemon B: a process the shim has never met ----
    # Start-Process, not `&`: PS 5.1 wraps native stderr in ErrorRecords and would
    # throw on harmless noise under ErrorActionPreference=Stop.
    $pb = Start-Process $client -ArgumentList "B", $pipeName `
        -RedirectStandardOutput "$out\clientB.out" -RedirectStandardError "$out\clientB.err" `
        -WorkingDirectory $repo -PassThru -NoNewWindow -Wait
    $results['B_exit_code'] = $pb.ExitCode

    # shim should exit after ##shutdown
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-Process -Id $shimInfo.shimPid -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    $results['shim_exited_on_shutdown'] = if (Get-Process -Id $shimInfo.shimPid -ErrorAction SilentlyContinue) { 'FAIL' } else { 'PASS' }
}
finally {
    if ($shimInfo) {
        foreach ($id in @($shimInfo.shimPid, $shimInfo.childPid)) {
            try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { }
        }
    }
}

# ---- verdicts from the transcripts ----
$aOut = Get-Content "$out\clientA.out" -ErrorAction SilentlyContinue
$bOut = Get-Content "$out\clientB.out" -ErrorAction SilentlyContinue

$aSeqs = @($aOut | Select-String '^RECV (\d+)$' | ForEach-Object { [long]$_.Matches[0].Groups[1].Value })
$bSeqs = @($bOut | Select-String '^RECV (\d+)$' | ForEach-Object { [long]$_.Matches[0].Groups[1].Value })
$union = @($aSeqs + $bSeqs | Sort-Object -Unique)
$max = ($union | Measure-Object -Maximum).Maximum
$gapFree = ($union.Count -eq ($max + 1))
$overlap = @($aSeqs | Where-Object { $bSeqs -contains $_ })

$results['A_seqs'] = "$($aSeqs.Count) lines, max seq $((($aSeqs | Measure-Object -Maximum).Maximum))"
$results['B_seqs'] = "$($bSeqs.Count) lines, first seq $($bSeqs | Select-Object -First 1), max seq $max"
$results['zero_loss_no_seq_gaps'] = if ($gapFree) { 'PASS' } else { "FAIL (union $($union.Count) of $($max+1))" }
$results['redelivered_overlap'] = "$($overlap.Count) line(s) delivered to both (at-least-once, deduped by seq)"
$results['B_received_turn2_result'] = if ($bOut -match 'B-GOT-TURN2-RESULT true') { 'PASS (produced while no daemon attached)' } else { 'FAIL' }
$results['B_fact_recall'] = if ($bOut -match 'B-FACT PASS') { 'PASS' } else { 'FAIL' }
$results['B_turn3'] = ($bOut | Select-String '^B-TURN3 (.*)$' | ForEach-Object { $_.Matches[0].Groups[1].Value })

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- RESULTS ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
