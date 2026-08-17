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

# Match THIS test's window by its project name — never a bare 'Dodona*' prefix, which
# grabs whichever Dodona window enumerates first, including the operator's own live one.
# (It did: the suite once seized the user's open session mid-dogfood.) The title's em
# dash still can't appear literally here — PS 5.1 reads this file as ANSI — so wildcard
# around the project leaf instead.
function TypeInDispatcher([string]$text) {
    $leaf = Split-Path $root -Leaf
    $all = $AE::RootElement.FindAll('Children',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Window)))
    $win = $null
    foreach ($w in $all) { if ($w.Current.Name -like "Dodona*$leaf") { $win = $w; break } }
    if (-not $win) { throw "no Dodona grid window titled for $leaf" }
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

    # ---- model/effort policy (§9): the table decides, the operator overrides ----
    Check 'policy_table_is_inspectable' ((Dodona @("policy")) -match 'design-tier') ''
    Check 'policy_picks_cheap_for_mechanical' ((Dodona @("policy", "fix the spelling in the readme")) -match '^haiku low') ''
    Check 'policy_picks_max_for_design' ((Dodona @("policy", "redesign the schema")) -match '^opus max') ''
    Check 'policy_default_is_opus_high' ((Dodona @("policy", "make the toolbar collapsible")) -match '^opus high') ''

    # An override in a typed prompt must be honoured AND must never reach the agent.
    # Clear the grid first: model and effort are fixed when a process starts, so the
    # policy only gets a say when a lane is BORN — with a lane already live the sentence
    # correctly goes to it instead.
    foreach ($s in @($d.slots | Where-Object { -not $_.empty })) { Dodona @("lane-stop", "$($s.lane)") | Out-Null }
    Start-Sleep -Seconds 2
    TypeInDispatcher "@haiku @low say fix the spelling in the readme"
    Start-Sleep -Seconds 3
    $d = Dump
    $spellLane = @($d.slots | Where-Object { -not $_.empty -and $_.title -eq 'SPELLING' })
    Check 'override_lane_started' ($spellLane.Count -eq 1) (($d.slots | Where-Object { -not $_.empty } | ForEach-Object { $_.title }) -join ',')
    $delivered = ($spellLane[0].lines -join '|')
    Check 'override_tokens_stripped_from_prompt' ($delivered -match 'fix the spelling' -and $delivered -notmatch '@haiku' -and $delivered -notmatch '@low') $delivered
    $choice = (python -c "
import sqlite3
db = sqlite3.connect(r'$root\.dodona\store.db')
r = db.execute('''SELECT detail FROM events WHERE kind='policy_choice' ORDER BY id DESC LIMIT 1''').fetchone()
print(r[0] if r else 'none')
") | Out-String
    Check 'override_recorded_in_causal_chain' ($choice -match 'haiku/low' -and $choice -match 'overridden=True') $choice
    Check 'choice_announced_to_operator' ((@($d.feed | Where-Object { $_.lane -eq 'SPELLING' -and $_.body -match 'haiku/low' })).Count -ge 1) ($d.feed | ConvertTo-Json -Compress)

    Dodona @("ui", "screenshot", "--out", "$out\after-typing.png") | Out-Null
    Dodona @("ui", "close") | Out-Null
    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    Get-ChildItem "$root\.dodona\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item "$root\.dodona\store.db" "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- UI USE ACCEPTANCE (driven like a person, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
