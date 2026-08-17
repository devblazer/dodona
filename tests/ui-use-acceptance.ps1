# UI USE acceptance: drive the real UI the way a person does — focus the dispatcher box,
# type a sentence, press Enter — and assert what the operator actually gets.
#
# This test exists because everything else asserted on `ui dump` and screenshots, which
# proved the UI could *report* correctly while the first thing a person tried produced a
# dead end: on a fresh project there were no lanes, so typing answered a sentence with an
# error telling them to go run a command. Dumps cannot catch that; only using it can.
#
# Driven through UI Automation (WPF exposes it natively), and the agent is the fake one
# via dodona.json's "agent" key — zero model calls.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$ui = "$repo\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'ui-use-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-uiuse-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\app.cs" "// app"
Set-Content "$root\.gitignore" ".dodona/"
# the fake agent stands in for claude wherever the daemon starts an agent on its own
Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake } | ConvertTo-Json)
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    (& $dodona ($a + @('--root', $root))) | Out-String
}
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function Dump() { Dodona @('ui', 'dump') | ConvertFrom-Json }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]

# Match the window by prefix, never by its full title: the title contains an em dash, and
# a literal one in this file is read as ANSI by PS 5.1 and breaks the parser.
function TypeInDispatcher([string]$text) {
    $all = $AE::RootElement.FindAll('Children',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Window)))
    $win = $null
    foreach ($w in $all) { if ($w.Current.Name -like 'Dodona*' -and $w.Current.Name.Length -gt 'Dodona'.Length) { $win = $w; break } }
    if (-not $win) { throw "no Dodona grid window found" }
    $box = $win.FindFirst('Descendants',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Edit)))
    if (-not $box) { throw "no input box in the window" }

    # Set the text through UIA and send only the keypress. Typing the whole sentence with
    # SendKeys drops characters (or the entire line) whenever the window loses activation
    # between calls — the first message would land and later ones vanish. The box clearing
    # is the proof that Enter was actually received.
    $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    foreach ($attempt in 1..5) {
        $win.SetFocus()
        Start-Sleep -Milliseconds 200
        $box.SetFocus()
        Start-Sleep -Milliseconds 200
        $vp.SetValue($text)
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Start-Sleep -Milliseconds 500
        if ($vp.Current.Value -eq '') { return }
    }
    throw "the dispatcher box never accepted Enter (still holds '$($vp.Current.Value)')"
}

$daemon = $null
$uiProc = $null
try {
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800

    $uiProc = Start-Process $ui -ArgumentList "--root", $root -PassThru
    Start-Sleep -Seconds 3

    # the state a person actually starts in: nothing running, nothing to click
    $d = Dump
    Check 'starts_empty' ((@($d.slots | Where-Object { -not $_.empty }).Count) -eq 0) ''

    # ---- THE TEST: type a sentence into an empty project ----
    TypeInDispatcher "say add a settings dialog to the app"
    Start-Sleep -Seconds 3
    $d = Dump

    Check 'typing_does_not_error' ($d.status -notmatch '^error') $d.status
    Check 'typing_never_tells_you_to_use_the_cli' ($d.status -notmatch 'dodona \w' -and $d.status -notmatch '<LANE>') $d.status

    $lanes = @($d.slots | Where-Object { -not $_.empty })
    Check 'a_lane_now_exists' ($lanes.Count -eq 1) "$($lanes.Count) lanes"
    Check 'lane_named_from_the_text' ($lanes[0].title -eq 'SETTINGS') $lanes[0].title
    Check 'lane_is_focused' ($lanes[0].focused -eq $true) ''
    Check 'the_message_was_delivered' ((($lanes[0].lines) -join '|') -match 'add a settings dialog') (($lanes[0].lines) -join '|')
    Check 'the_agent_answered' ((($lanes[0].lines) -join '|') -match 'add a settings dialog to the app') ''

    # what it did is announced, and the announcement carries its undo
    $announced = @($d.feed | Where-Object { $_.lane -eq 'SETTINGS' -and $_.body -match 'started this lane' })
    Check 'creation_is_announced' ($announced.Count -ge 1) ($d.feed | ConvertTo-Json -Compress)
    Check 'announcement_offers_undo' ($announced[0].body -match 'undo: dodona lane-stop \d+') $announced[0].body

    # ---- a second sentence goes to the lane that exists, not a second lane ----
    TypeInDispatcher "say and make it resizable"
    Start-Sleep -Seconds 2
    $d = Dump
    $lanes = @($d.slots | Where-Object { -not $_.empty })
    Check 'second_sentence_reuses_the_lane' ($lanes.Count -eq 1) "$($lanes.Count) lanes"
    Check 'second_message_delivered' ((($lanes[0].lines) -join '|') -match 'make it resizable') ''

    # ---- the undo actually works ----
    if ($announced[0].body -match 'lane-stop (\d+)') { $laneId = $Matches[1] }
    Dodona @("lane-stop", $laneId) | Out-Null
    Start-Sleep -Seconds 2
    $d = Dump
    Check 'undo_stops_the_lane' ((@($d.slots | Where-Object { -not $_.empty }).Count) -eq 0) ($d.slots | ConvertTo-Json -Compress)
    $rows = Dodona @("tail", $laneId, "20")
    Check 'undo_keeps_the_transcript' ($rows -match 'add a settings dialog') ''

    # ---- and typing again after the undo still just works ----
    TypeInDispatcher "say start over with the toolbar"
    Start-Sleep -Seconds 3
    $d = Dump
    $lanes = @($d.slots | Where-Object { -not $_.empty })
    Check 'typing_after_undo_starts_a_fresh_lane' ($lanes.Count -eq 1 -and $lanes[0].title -eq 'TOOLBAR') (($lanes | ConvertTo-Json -Compress))

    Dodona @("ui", "screenshot", "--out", "$out\after-typing.png") | Out-Null
    Dodona @("ui", "close") | Out-Null
    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    foreach ($n in 'DodonaShim', 'DodonaFakeAgent', 'DodonaUi') { try { Get-Process $n -ErrorAction Stop | Stop-Process -Force } catch { } }
    Copy-Item "$root\.dodona\store.db" "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- UI USE ACCEPTANCE (driven like a person, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
