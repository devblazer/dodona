# Spike 1 — resume durability + long-lived stream-json sessions (design doc §16, §13, §2)
# Proves/refutes, on this machine:
#   A. one `claude -p --input-format stream-json` invocation accepts MULTIPLE user messages
#   B. a hard kill mid-turn does not corrupt the session
#   C. `--resume <id>` restores full context headlessly after the kill
#   D. resume continues the SAME session id (same file grows; no fork)
# Wire samples are saved to spike1-output\wire.jsonl — the start of the .NET driver's
# protocol reference.

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'spike1-output'
New-Item -ItemType Directory -Force $outDir | Out-Null

$cmd = Get-Command claude -ErrorAction SilentlyContinue
$claudeExe = if ($cmd) { $cmd.Source } else { "$env:USERPROFILE\.local\bin\claude.exe" }
if (-not (Test-Path $claudeExe)) { throw "claude not found" }
Write-Output "claude: $claudeExe"

$results = [ordered]@{}
$magic = "PERSIMMON-42"

# ---------- Phase A: long-lived bidirectional session ----------
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $claudeExe
$psi.Arguments = '-p --input-format stream-json --output-format stream-json --verbose --model haiku'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path -Parent $PSScriptRoot   # repo root = the session's cwd
$proc = [System.Diagnostics.Process]::Start($psi)

$errSb = New-Object System.Text.StringBuilder
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData $errSb -Action {
  if ($EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) }
}
$proc.BeginErrorReadLine()

$wire = New-Object System.Collections.ArrayList
$script:pending = $null

function Send-Turn([string]$text) {
  $msg = @{ type = 'user'; message = @{ role = 'user'; content = @(@{ type = 'text'; text = $text }) } } |
    ConvertTo-Json -Depth 8 -Compress
  $proc.StandardInput.WriteLine($msg)
  $proc.StandardInput.Flush()
}

function Read-UntilResult([int]$timeoutSec) {
  $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
  while ([DateTime]::UtcNow -lt $deadline) {
    if (-not $script:pending) { $script:pending = $proc.StandardOutput.ReadLineAsync() }
    if (-not $script:pending.Wait(2000)) { continue }
    $line = $script:pending.Result
    $script:pending = $null
    if ($null -eq $line) { throw "EOF from claude before a result event" }
    [void]$wire.Add($line)
    try { $obj = $line | ConvertFrom-Json } catch { continue }
    if ($obj.type -eq 'result') { return $obj }
  }
  throw "timeout waiting for result event"
}

# Turn 1 — plant the fact
Send-Turn "Remember this exactly: the magic word is $magic. Acknowledge in five words or fewer."
$r1 = Read-UntilResult 90
$sessionId = ($wire | ForEach-Object { try { ($_ | ConvertFrom-Json) } catch { $null } } |
  Where-Object { $_ -and $_.session_id } | Select-Object -First 1).session_id
$results['session_id'] = $sessionId
$results['turn1_result'] = $r1.result
Write-Output "turn 1 ok, session: $sessionId"

# Turn 2 — SAME process, SAME invocation: multi-message proof
Send-Turn "What is the magic word? Reply with only the word."
$r2 = Read-UntilResult 90
$results['turn2_result'] = $r2.result
$results['A_multi_message_per_invocation'] = if ("$($r2.result)" -match [regex]::Escape($magic)) { 'PASS' } else { 'FAIL' }
Write-Output "turn 2: $($r2.result)  [multi-message: $($results['A_multi_message_per_invocation'])]"

# Turn 3 — send, then HARD KILL mid-turn
Send-Turn "Write a 150-word story about a lighthouse keeper."
Start-Sleep -Milliseconds 1500
$proc.Kill()
$results['B_killed_mid_turn'] = "killed pid $($proc.Id) ~1.5s after sending turn 3"
Write-Output "killed mid-turn-3"

Set-Content -Path (Join-Path $outDir 'wire.jsonl') -Value ($wire -join "`n") -Encoding utf8
Set-Content -Path (Join-Path $outDir 'stderr.txt') -Value $errSb.ToString() -Encoding utf8

# Locate the session file on disk (reboot-survival evidence: it is a plain JSONL file)
Start-Sleep -Milliseconds 500
$configDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { "$env:USERPROFILE\.claude" }
$projRoot = Join-Path $configDir 'projects'
$sessionFile = Get-ChildItem $projRoot -Directory |
  ForEach-Object { Get-ChildItem $_.FullName -Filter "$sessionId.jsonl" -ErrorAction SilentlyContinue } |
  Select-Object -First 1
if ($sessionFile) {
  $results['session_file'] = $sessionFile.FullName
  $sizeBefore = $sessionFile.Length
  $results['session_file_bytes_after_kill'] = $sizeBefore
  $siblingCountBefore = (Get-ChildItem $sessionFile.DirectoryName -Filter *.jsonl).Count
} else {
  $results['session_file'] = 'NOT FOUND'
}

# ---------- Phase C/D: resume after the kill ----------
$resumeOut = & $claudeExe -p --resume $sessionId --model haiku "What was the magic word I told you at the start of this conversation? Reply with only the word." 2>$null
$results['resume_reply'] = "$resumeOut".Trim()
$results['C_context_survives_kill_and_resume'] = if ("$resumeOut" -match [regex]::Escape($magic)) { 'PASS' } else { 'FAIL' }
Write-Output "resume reply: $($results['resume_reply'])  [context: $($results['C_context_survives_kill_and_resume'])]"

if ($sessionFile) {
  $sessionFile.Refresh()
  $siblingCountAfter = (Get-ChildItem $sessionFile.DirectoryName -Filter *.jsonl).Count
  $grew = $sessionFile.Length -gt $sizeBefore
  $newFiles = $siblingCountAfter - $siblingCountBefore
  $results['session_file_bytes_after_resume'] = $sessionFile.Length
  $results['new_session_files_created_by_resume'] = $newFiles
  $results['D_resume_continues_same_session'] = if ($grew -and $newFiles -eq 0) { 'PASS (same file grew, no fork)' }
    elseif ($newFiles -gt 0) { "FORKED ($newFiles new file(s))" } else { 'UNCLEAR' }
  Write-Output "same-session check: $($results['D_resume_continues_same_session'])"
}

$results | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $outDir 'results.json') -Encoding utf8
Write-Output "---- RESULTS ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
