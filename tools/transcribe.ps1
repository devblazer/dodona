# Push an audio FILE through the real dictation path and print what came back.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\transcribe.ps1 `
#       -Wav tests\assets\recordings\vocab.m4a [-Seconds <n>]
#
# THE ONE MEASUREMENT NO SUITE CAN MAKE. Recognition quality needs speech, so it cannot live in
# an acceptance suite (and must not: D-E5 forbids a suite opening a socket on the operator's
# credential). It lives here, beside the recordings it scores, because a measurement whose tool
# is thrown away is a measurement nobody repeats -- and repeating it is the entire point:
# tests\assets\recordings\README.md carries the ground truth and the 17/19 baseline, so the next
# engine change is scored against the same speech rather than a fresh recording session.
#
# It drives the SHIPPED path via DODONA_STT_WAV -- same resampler, same 20 ms frames, same
# socket, same keyterms, same OnHeard splice -- for the reason section 5 gives about `ui heard`:
# a harness with its own socket would be measuring a rehearsal. It opens a real socket on the
# operator's credential, deliberately, and NO microphone at all.
#
# probe-hygiene: isolated DODONA_HOME, binaries from Use-TestBinaries, --test-window throughout,
# nothing machine-wide touched.
param(
    [Parameter(Mandatory = $true)][string]$Wav,
    [int]$Seconds = 0,
    # Re-run the keyterm A/B. Measured inert three ways (D-E18/D-E21/D-E22); kept because that
    # is the kind of finding that gets re-litigated unless the experiment is one flag away.
    [switch]$NoKeyterms
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$repo\tests\_workspace.ps1"

if (-not (Test-Path $Wav)) { Write-Output "no such file: $Wav"; exit 2 }
$Wav = (Resolve-Path $Wav).Path

$null = Use-IsolatedDodonaHome 'transcribe'
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = '1'

# The REAL engine, the REAL endpoint, the REAL credential, and the file as the source.
Remove-Item env:DODONA_UI_MIC -ErrorAction SilentlyContinue
Remove-Item env:DODONA_STT_TOKEN -ErrorAction SilentlyContinue
Remove-Item env:DODONA_STT_ENDPOINT -ErrorAction SilentlyContinue
# Use-IsolatedDodonaHome sets this to keep SUITES off the operator's credential (D-E15). This
# tool is the one thing that is SUPPOSED to authenticate, so it is cleared here and nowhere else.
Remove-Item env:DODONA_STT_NO_CLI_AUTH -ErrorAction SilentlyContinue
if ($NoKeyterms) { $env:DODONA_STT_NO_KEYTERMS = '1' } else { Remove-Item env:DODONA_STT_NO_KEYTERMS -ErrorAction SilentlyContinue }
$env:DODONA_STT_WAV = $Wav

# How long to listen: the file's own duration plus slack for the last phrase to settle. The
# server endpoints far more lazily than endpointing_ms=300 suggests (D-E19), so the slack is
# generous on purpose.
if ($Seconds -le 0) {
    Add-Type -AssemblyName PresentationCore
    try {
        $p = New-Object System.Windows.Media.MediaPlayer
        $p.Open([uri]$Wav)
        Start-Sleep -Milliseconds 700          # MediaPlayer resolves duration asynchronously
        $dur = $p.NaturalDuration
        $Seconds = if ($dur.HasTimeSpan) { [int]$dur.TimeSpan.TotalSeconds + 9 } else { 45 }
        $p.Close()
    } catch { $Seconds = 45 }
}

$out = Join-Path $PSScriptRoot '..\.dodona\transcribe'
New-Item -ItemType Directory -Force $out | Out-Null
$root = Join-Path (Use-SuiteTemp) ("dodona-tr-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\app.cs" '// app'
Set-Content "$root\.gitignore" '.dodona/'
Set-Content "$root\dodona.json" (@{ main = 'main'; agent = "$bin\DodonaFakeAgent.exe" } | ConvertTo-Json)
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

function DumpOrNull { $ErrorActionPreference = 'Continue'; try { (& $dodona ui dump --root $root) | ConvertFrom-Json } catch { $null } }

$procs = @()
try {
    $daemon = Start-Process $dodona -ArgumentList 'daemon', '--root', $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    $procs += $daemon
    $ws = Get-WorkspacePaths $dodona $root
    Wait-Daemon $ws.CtlPipe | Out-Null

    $uiProc = Start-Process $ui -ArgumentList '--root', $root, '--test-window' -PassThru
    $procs += $uiProc
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the window answers' | Out-Null

    Write-Output "file      : $Wav"
    Write-Output ("keyterms  : {0}" -f $(if ($NoKeyterms) { 'OFF (A/B)' } else { 'on' }))
    Write-Output "listening for $Seconds s ..."
    & $dodona ui listen on --root $root | Out-Null

    $settled = Wait-Until { (DumpOrNull).listen.state -in @('listening', 'error') } 25000 'the engine settles'
    $d = DumpOrNull
    Write-Output ("state     : {0}  engine={1}  error=[{2}]" -f $d.listen.state, $d.listen.engine, $d.listen.error)
    if (-not $settled -or $d.listen.state -ne 'listening') {
        Write-Output 'not listening -- nothing to transcribe.'
        return
    }

    # Every distinct hypothesis, so the interim stream is visible and not only the settled text.
    # This is also where a latency problem shows itself.
    $seen = New-Object System.Collections.Generic.List[string]
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $dd = DumpOrNull
        if ($null -ne $dd) {
            $p = "$($dd.listen.partial)"
            if ($p.Length -gt 0 -and ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $p)) { $seen.Add($p) | Out-Null }
        }
        Start-Sleep -Milliseconds 250       # a poll interval, not a wait on a condition
    }

    # Toggle OFF before reading the final box: it is what a person does, and it is what FLUSHES
    # the phrase still being held (D-E19 -- there is nearly always one).
    & $dodona ui listen off --root $root | Out-Null
    Start-Sleep -Milliseconds 1500
    $box = "$((DumpOrNull).input.text)"

    Write-Output ''
    Write-Output '---- interim stream (last 5 distinct hypotheses) ----'
    if ($seen.Count -eq 0) { Write-Output '(none -- the engine never produced a hypothesis)' }
    else { $seen | Select-Object -Last 5 | ForEach-Object { Write-Output "  ~ $_" } }

    Write-Output ''
    Write-Output '---- THE TRANSCRIPT (settled, after the flush) ----'
    if ($box.Length -eq 0) { Write-Output '(empty)' } else { Write-Output $box }
    Write-Output ''
    Write-Output 'Score it against tests\assets\recordings\README.md -- baseline is 17/19 technical words.'
    Set-Content "$out\transcript.txt" $box -Encoding utf8
}
finally {
    $ErrorActionPreference = 'Continue'
    foreach ($p in $procs) { if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } } }
    Stop-ProcessesUnder $bin
    Remove-Item env:DODONA_STT_WAV, env:DODONA_STT_NO_KEYTERMS -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART, env:DODONA_HOME -ErrorAction SilentlyContinue
}
