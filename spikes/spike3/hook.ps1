# Spike 3 hook — the §5 instruction-queue consumer, miniature edition.
# Runs on PostToolUse. Reads inject-queue.txt beyond the cursor; if anything is new,
# returns it as additionalContext (landing inside the running turn) and advances the
# cursor — the cursor advance IS the ack. Logs every firing for the spike report.

$stdin = [Console]::In.ReadToEnd()
$dir = $PSScriptRoot
$q   = Join-Path $dir 'inject-queue.txt'
$cur = Join-Path $dir 'inject-cursor.txt'
$log = Join-Path $dir 'hook-log.txt'

$cursor = 0
if (Test-Path $cur) { $cursor = [int](Get-Content $cur -Raw).Trim() }
$lines = @()
if (Test-Path $q) { $lines = @(Get-Content $q) }
$new = @()
if ($lines.Count -gt $cursor) { $new = @($lines[$cursor..($lines.Count - 1)]) }

$toolName = ''
try { $toolName = ($stdin | ConvertFrom-Json).tool_name } catch { }
Add-Content $log ("{0:o} fired tool={1} cursor={2} new={3}" -f (Get-Date), $toolName, $cursor, $new.Count)

if ($new.Count -gt 0) {
    Set-Content $cur "$($lines.Count)" -Encoding ascii
    $ctx = "[DISPATCHER] Real-time instructions from your operator - apply immediately within your current task:`n" + ($new -join "`n")
    @{ hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = $ctx } } | ConvertTo-Json -Compress
}
exit 0
