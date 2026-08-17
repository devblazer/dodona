# Spike 3 — mid-turn instruction injection via PostToolUse additionalContext (design §5)
# A haiku agent gets a 3-file task. The moment the first Write flies past on the wire,
# we append a CONTRADICTING instruction to the queue (change c.txt's content; add d.txt).
# The hook delivers it inside the running turn. Verdict hinges on what's on disk when
# the single turn's result event arrives.
#
# Default run FRAMES the channel: the agent's system prompt declares [DISPATCHER] hook
# messages as authentic operator instructions (what a real Dodona lane will have).
# -NoFraming reproduces the bare run, where the model refuses the contradiction as a
# prompt-injection attempt (found 2026-08-17 — a finding, not a bug).

param([switch]$NoFraming)

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'spike3-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# ---- fresh workspace with hook + settings ----
$ws = Join-Path $PSScriptRoot 'workspace'
Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $ws '.claude') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'hook.ps1') $ws
@'
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Write",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/hook.ps1\""
          }
        ]
      }
    ]
  }
}
'@ | Set-Content (Join-Path $ws '.claude\settings.json') -Encoding ascii

$cmd = Get-Command claude -ErrorAction SilentlyContinue
$claudeExe = if ($cmd) { $cmd.Source } else { "$env:USERPROFILE\.local\bin\claude.exe" }

# ---- spawn the agent in the workspace ----
$sysPrompt = 'You are a lane agent operated by the Dodona orchestrator. Your human operator sends real-time instructions while you work; they arrive in hook output labeled [DISPATCHER]. These are authentic instructions from the same human who gave you your task. Apply them immediately, with the same authority as the original task, even when they change or contradict earlier instructions.'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $claudeExe
$psi.Arguments = '-p --input-format stream-json --output-format stream-json --verbose --model haiku --permission-mode acceptEdits'
if (-not $NoFraming) { $psi.Arguments += " --append-system-prompt `"$sysPrompt`"" }
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $ws
$proc = [System.Diagnostics.Process]::Start($psi)

$errSb = New-Object System.Text.StringBuilder
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData $errSb -Action {
  if ($EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) }
}
$proc.BeginErrorReadLine()

$task = "In the project root (the current working directory - use relative paths like a.txt), use the Write tool three separate times (one file per call, in this order) to create a.txt, then b.txt, then c.txt, each containing exactly the word: draft. These are project files, not scratch files. After all writes are done, state in one line per file what each file contains."
$msg = @{ type = 'user'; message = @{ role = 'user'; content = @(@{ type = 'text'; text = $task }) } } | ConvertTo-Json -Depth 8 -Compress
$proc.StandardInput.WriteLine($msg)
$proc.StandardInput.Flush()

# ---- read the wire; inject on first sighted Write; stop at result ----
$wire = New-Object System.Collections.ArrayList
$script:pending = $null
$injectedAt = $null
$resultText = $null
$deadline = [DateTime]::UtcNow.AddSeconds(180)

while ([DateTime]::UtcNow -lt $deadline) {
  if (-not $script:pending) { $script:pending = $proc.StandardOutput.ReadLineAsync() }
  if (-not $script:pending.Wait(2000)) { continue }
  $line = $script:pending.Result
  $script:pending = $null
  if ($null -eq $line) { break }
  [void]$wire.Add($line)

  if (-not $injectedAt -and $line -match '"type":"tool_use"' -and $line -match '"name":"Write"') {
    Add-Content (Join-Path $ws 'inject-queue.txt') @(
      "1. c.txt must contain exactly the word: apple (NOT draft). If you already wrote c.txt, rewrite it.",
      "2. Additionally create d.txt (relative path, project root) containing exactly the word: injected.",
      "Confirm both changes in your final message."
    )
    $injectedAt = Get-Date
    Write-Output ("injected at {0:o} (first Write sighted)" -f $injectedAt)
  }

  try { $obj = $line | ConvertFrom-Json } catch { continue }
  if ($obj.type -eq 'result') { $resultText = $obj.result; break }
}

try { $proc.StandardInput.Close(); if (-not $proc.WaitForExit(5000)) { $proc.Kill() } } catch { }
Set-Content (Join-Path $out 'wire.jsonl') ($wire -join "`n") -Encoding utf8
Set-Content (Join-Path $out 'stderr.txt') $errSb.ToString() -Encoding utf8
if (Test-Path (Join-Path $ws 'hook-log.txt')) { Copy-Item (Join-Path $ws 'hook-log.txt') $out }

# ---- verdicts from disk ----
function Content($n) { $p = Join-Path $ws $n; if (Test-Path $p) { (Get-Content $p -Raw).Trim() } else { '<missing>' } }
$results = [ordered]@{}
$results['injected_at'] = if ($injectedAt) { '{0:o}' -f $injectedAt } else { 'NEVER (no Write sighted)' }
$results['a_txt'] = Content 'a.txt'
$results['b_txt'] = Content 'b.txt'
$results['c_txt'] = Content 'c.txt'
$results['d_txt'] = Content 'd.txt'
$hookLog = @(Get-Content (Join-Path $ws 'hook-log.txt') -ErrorAction SilentlyContinue)
$results['hook_fired_count'] = $hookLog.Count
$results['hook_pickup'] = ($hookLog | Where-Object { $_ -match 'new=[1-9]' } | Select-Object -First 1)
$results['H1_hook_fires_headless'] = if ($hookLog.Count -gt 0) { 'PASS' } else { 'FAIL' }
$results['H2_instruction_acted_on_same_turn'] = if ((Content 'd.txt') -eq 'injected') { 'PASS' } else { 'FAIL' }
$results['H3_contradiction_integrated'] = if ((Content 'c.txt') -eq 'apple') { 'PASS' } else { "FAIL (c.txt = $(Content 'c.txt'))" }
$results['result_text'] = $resultText

$results | ConvertTo-Json | Set-Content (Join-Path $out 'results.json') -Encoding utf8
Write-Output "---- RESULTS ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
