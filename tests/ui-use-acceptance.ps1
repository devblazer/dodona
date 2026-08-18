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
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'uiuse'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
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

# `ui type` fills the box and submits through EXACTLY the code path Enter takes
# (MainWindow.SubmitInput) — without keyboard focus. The previous SendKeys version had to
# focus the test window to press Enter, which stole the operator's keyboard every few
# seconds while they worked; the whole suite now runs against an invisible, never-activated
# window (--test-window) and the operator cannot tell it is running.
function TypeInDispatcher([string]$text) {
    $r = Dodona @('ui', 'type', $text)
    if ($r -notmatch 'typed') { throw "ui type refused: $r" }
}

# The window this suite owns, never someone else's. A `-like "Dodona*"` lookup is not
# specific enough: the operator's own editor window is titled "Dodona - ..." too, and the
# first version of the grip check found THAT window (which has no grip) and failed. The
# title's second half is the workspace name, which is this root's leaf.
function UiWindow([string]$nameLike = "Dodona*$(Split-Path $root -Leaf)") {
    $all = $AE::RootElement.FindAll('Children',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Window)))
    foreach ($w in $all) { if ($w.Current.Name -like $nameLike) { return $w } }
    return $null
}
function ByName($win, [string]$name) {
    if (-not $win) { return $null }
    $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name))
}

$daemon = $null
$uiProc = $null
# The multi-workspace shell section starts a SECOND workspace daemon and a second UI; both
# are tracked so the scoped cleanup in `finally` can stop them by pid (CLAUDE.md §4).
$daemon2 = $null
$shellUi = $null
$bareUi = $null
$uiCopy = $null
try {
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800

    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
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

    # ---- the box opens at a usable size, and grows past it -------------------------------
    # A one-line sliver invites one-line prompts. `fit` is the default the XAML floor
    # measures to (three lines); `height` equal to it means the box opened there.
    $d = Dump
    Check 'the_box_opens_at_three_lines' `
        ($d.input.fit -ge 55 -and $d.input.fit -le 85 -and $d.input.height -eq $d.input.fit -and $d.input.sized -eq $false) `
        ($d.input | ConvertTo-Json -Compress)
    Dodona @('ui', 'compose', 'say one') | Out-Null
    1..5 | ForEach-Object {
        Dodona @('ui', 'key', 'shift+enter') | Out-Null
        Dodona @('ui', 'compose', "line $_") | Out-Null
    }
    $d = Dump
    Check 'the_box_grows_past_the_default' ($d.input.lines -eq 6 -and $d.input.height -gt $d.input.fit) ($d.input | ConvertTo-Json -Compress)
    Dodona @('ui', 'key', 'enter') | Out-Null          # send it, emptying the box for the next check
    Start-Sleep -Seconds 2
    Check 'the_box_shrinks_back_to_the_default' ((Dump).input.height -eq $d.input.fit) ((Dump).input | ConvertTo-Json -Compress)

    # ---- the box is MULTILINE: Shift+Enter is a newline, Enter still sends -------------
    # A prompt is often a paragraph, and the old box swallowed the second sentence you tried
    # to write. Driven the way a person drives it: characters, a Shift+Enter, more characters,
    # Enter. `ui compose` + `ui key` land in the same ComposeInput/InputKey the keyboard lands
    # in (MainWindow.Input_PreviewKeyDown), so this covers the real affordance — including the
    # trap that made PreviewKeyDown necessary: with AcceptsReturn the TextBox class handler
    # eats Enter before an instance KeyDown, and the box goes silently deaf (CLAUDE.md §0.2).
    function InputRows() {
        [int]((python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print(db.execute('''SELECT COUNT(*) FROM pane_events WHERE kind='user_input' ''').fetchone()[0])
") | Out-String).Trim()
    }
    $inputsBefore = InputRows

    Dodona @('ui', 'compose', 'say make the toolbar roomier') | Out-Null
    Dodona @('ui', 'key', 'shift+enter') | Out-Null
    Dodona @('ui', 'compose', 'and keep the send key') | Out-Null
    Start-Sleep -Milliseconds 400
    $d = Dump
    Check 'shift_enter_makes_a_second_line' ($d.input.lines -eq 2) ($d.input | ConvertTo-Json -Compress)
    Check 'shift_enter_sends_nothing' ($d.input.text -match 'roomier' -and $d.input.text -match 'send key') ($d.input | ConvertTo-Json -Compress)
    $inputsMid = InputRows
    Check 'shift_enter_delivered_nothing_anywhere' ($inputsMid -eq $inputsBefore) "before=$inputsBefore mid=$inputsMid"

    # the grip is a real affordance, not only a verb
    $grip = ByName (UiWindow) 'resize-input'
    Check 'resize_grip_exists' ($null -ne $grip) ''

    # ...and it resizes. Dragging UP grows the box; the FEED gives up the pixels, so the
    # WINDOW never changes size and nothing is pushed off the bottom of it.
    $hFit = $d.input.height
    $winH = $d.window.h
    Dodona @('ui', 'input-resize', '70') | Out-Null
    Start-Sleep -Milliseconds 300
    $d = Dump
    Check 'the_box_drags_taller' ($d.input.height -ge $hFit + 50 -and $d.input.sized -eq $true) "fit=$hFit dragged=$($d.input.height)"
    Check 'a_taller_box_never_resizes_the_window' ($d.window.h -eq $winH) "was=$winH now=$($d.window.h)"
    Dodona @('ui', 'input-resize', 'reset') | Out-Null
    Start-Sleep -Milliseconds 300
    $d = Dump
    Check 'double_click_refits_the_box' ($d.input.height -le $hFit + 2 -and $d.input.sized -eq $false) "fit=$hFit after=$($d.input.height)"
    Check 'resizing_kept_the_draft' ($d.input.text -match 'roomier') $d.input.text
    # left set on purpose: the window-reopen section below asserts this exact height came back
    Dodona @('ui', 'input-resize', '96') | Out-Null
    Start-Sleep -Milliseconds 300
    $sizedTo = (Dump).input.height

    # Enter sends the WHOLE paragraph, newline intact all the way to the agent's stdin: Say
    # serializes it to ONE json line, so the shim's line protocol cannot split it in half.
    Dodona @('ui', 'key', 'enter') | Out-Null
    Start-Sleep -Seconds 3
    $d = Dump
    Check 'enter_still_sends' ($d.input.text -eq '' -and $d.input.lines -eq 1) ($d.input | ConvertTo-Json -Compress)
    Check 'the_hint_comes_back_when_the_box_empties' ($d.input.hint -eq $true) ($d.input | ConvertTo-Json -Compress)
    # chr(10), never a backslash escape: this string survives a shell, a here-string and a
    # regex on its way here, and every one of those layers has an opinion about backslashes.
    $body = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
r = db.execute('''SELECT body FROM pane_events WHERE kind='user_input' ORDER BY id DESC LIMIT 1''').fetchone()
print(r[0].replace(chr(10), '<NL>') if r else 'none')
") | Out-String
    Check 'the_newline_survived_to_the_agent' ($body -match 'roomier<NL>and keep the send key') $body

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
db = sqlite3.connect(r'$storeDb')
r = db.execute('''SELECT detail FROM events WHERE kind='policy_choice' ORDER BY id DESC LIMIT 1''').fetchone()
print(r[0] if r else 'none')
") | Out-String
    Check 'override_recorded_in_causal_chain' ($choice -match 'haiku/low' -and $choice -match 'overridden=True') $choice
    Check 'choice_announced_to_operator' ((@($d.feed | Where-Object { $_.lane -eq 'SPELLING' -and $_.body -match 'haiku/low' })).Count -ge 1) ($d.feed | ConvertTo-Json -Compress)

    # ---- attention timing (docs/LANE-LIFECYCLE.md §4): a badge that appears while the
    # agent is still working says "something happened", not "you are needed". The rows
    # exist immediately; the COUNT defers until the turn's result. Timing behaviour, so it
    # must be asserted DURING the turn — a dump after everything settles would pass even
    # when the live experience is wrong (that is how the first dead end shipped).
    $d = Dump
    $lane3 = ($d.slots | Where-Object { -not $_.empty } | Select-Object -First 1).lane
    Dodona @("input", "make a note of this") | Out-Null      # a routing decision to undo
    Start-Sleep -Milliseconds 600
    # 14s turn: the elapsed clock deliberately withholds itself for the first 10s (a
    # snappy turn must not flicker digits), so the mid-turn look happens at ~11s in.
    Dodona @("say", "$lane3", "sleep:20 then say slow turn done") | Out-Null
    Start-Sleep -Milliseconds 800                             # presence is now working/sleeping
    $route = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print(db.execute('SELECT id FROM routing_decisions ORDER BY id DESC LIMIT 1').fetchone()[0])
") | Out-String
    Dodona @("undo-route", $route.Trim()) | Out-Null          # writes an unacked announcement mid-turn
    Start-Sleep -Seconds 13                                   # comfortably past the 10s clock threshold
    $mid = (Dump).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $lane3 }
    Check 'badge_defers_while_agent_works' ($mid.badge -eq 0) "badge=$($mid.badge) presence=$($mid.presence)"
    Check 'liveness_shows_a_moving_clock' ($mid.presence -match '\d+s') $mid.presence
    Start-Sleep -Seconds 9                                    # the turn ends; deferred badges flush
    $after = (Dump).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $lane3 }
    Check 'badge_flushes_at_turn_end' ($after.badge -ge 1 -and $after.presence -eq 'idle') "badge=$($after.badge) presence=$($after.presence)"

    # ---- the 5-hour quota line: the CLI's own number, from the wire, no estimation ----
    Dodona @("say", "$lane3", "ratelimit:0.42 say quota reported") | Out-Null
    Start-Sleep -Seconds 2
    $d = Dump
    Check 'quota_line_from_wire' ($d.quota -match '5h window 42%') "quota='$($d.quota)'"

    # ---- the pane's close button is a real button (CLAUDE.md: the feed telling a GUI
    # user to type "dodona lane-stop 3" was this project's original sin) ----
    $win2 = $null
    $all2 = $AE::RootElement.FindAll('Children',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Window)))
    $leaf2 = Split-Path $root -Leaf
    foreach ($w in $all2) { if ($w.Current.Name -like "Dodona*$leaf2") { $win2 = $w; break } }
    $closeBtn = $win2.FindFirst('Descendants',
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'close-lane'))
    Check 'close_button_exists' ($null -ne $closeBtn) ''
    if ($closeBtn) {
        ($closeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
        $left = @((Dump).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $lane3 })
        Check 'close_button_stops_the_lane' ($left.Count -eq 0) (($left | ConvertTo-Json -Compress))
    }

    # =====================================================================================
    # THE SELF-DIVIDING GRID (ORCHESTRATOR-DESIGN §8, revised by the operator 2026-08-18)
    #
    # The six-slot cap is gone: the grid divides itself as lanes arrive, and the operator
    # collapses what they are not dealing with. No scrolling anywhere -- panes shrink, and
    # crowding is the cue to collapse ("i dont want scroll. if needed user will just collapse
    # more").
    #
    # This also closes a hole §8 itself called forbidden. It said an active-but-invisible lane
    # "could be blocked on you with no visible signal; forbidden" -- yet a seventh live lane used
    # to appear only as a NAME in the tray, agent running, badge unseeable. The last check here
    # is the one that matters: a COLLAPSED lane still shows blocked-on-you.
    #
    # These checks must run while the single-workspace window is still UP. The first draft
    # anchored them after `ui close` below, so every one of them ran against a window that was
    # already gone -- which is the same class of mistake this whole suite exists to catch.
    # =====================================================================================

    # Lanes made explicitly, not by typing. Typing three similar sentences produces ONE lane
    # today, because the classifier has no "new task" verdict yet and everything after the
    # first goes to the focused lane — which is the very gap lane granularity closes. This
    # section is about LAYOUT, so it must not depend on routing behaviour that is still to come.
    foreach ($n in 'ALPHA', 'BETA', 'GAMMA') {
        Dodona @("lane-start", "--title", $n, "--child", $fake) | Out-Null
    }
    Start-Sleep -Seconds 2
    $d = Dump
    Check 'grid_grows_to_the_number_of_lanes' ((@($d.slots).Count) -eq 3) "tiles=$(@($d.slots).Count)"
    Check 'grid_has_no_empty_placeholders' ((@($d.slots | Where-Object { $_.empty }).Count) -eq 0) ($d.slots | ConvertTo-Json -Compress)
    Check 'three_lanes_divide_into_two_columns' ($d.columns -eq 2) "columns=$($d.columns)"

    # ---- the collapse control is a real button, and it collapses ---------------------------
    $collapseBtn = ByName (UiWindow) 'collapse-lane'
    Check 'collapse_button_exists' ($null -ne $collapseBtn) ''
    if ($collapseBtn) {
        ($collapseBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
        $d = Dump
        Check 'collapse_takes_it_out_of_the_grid' ((@($d.slots).Count) -eq 2) "tiles=$(@($d.slots).Count)"
        Check 'collapsed_lane_becomes_a_chip' ((@($d.collapsedLanes).Count) -eq 1) ($d.collapsedLanes | ConvertTo-Json -Compress)
    }

    # ---- collapsing is NOT stopping ------------------------------------------------------
    $chipLane = (@((Dump).collapsedLanes) | Select-Object -First 1).lane
    $st = Dodona @("status")
    $chipRow = @(($st -split "`r?`n") | Where-Object { $_ -match "^lane $chipLane\s" })
    Check 'collapse_does_not_stop_the_lane' (($chipRow -join '') -match 'state=alive') ($chipRow -join '')
    Dodona @("say", "$chipLane", "say still listening while collapsed") | Out-Null
    Start-Sleep -Seconds 2
    Check 'a_collapsed_lane_still_works' ((Dodona @("tail", "$chipLane", "5")) -match 'still listening while collapsed') ''

    # ---- it is a store row, so the choice survives the window ----------------------------
    Dodona @("ui", "close") | Out-Null
    Start-Sleep -Seconds 1
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Start-Sleep -Seconds 4
    $d = Dump
    Check 'collapse_survives_reopening_the_window' `
        (((@($d.collapsedLanes).Count) -eq 1) -and ((@($d.collapsedLanes)[0].lane) -eq $chipLane)) `
        ($d.collapsedLanes | ConvertTo-Json -Compress)

    # ...and the size the operator set outlives the WINDOW, not just the drag: it is on disk
    # under DODONA_HOME (ui.json), so it survives a restart and a publish hot-swap alike.
    Check 'the_box_remembers_the_size_i_set' `
        ($d.input.sized -eq $true -and [Math]::Abs($d.input.height - $sizedTo) -le 2) `
        ($d.input | ConvertTo-Json -Compress)
    Check 'the_remembered_size_is_on_disk' ([Math]::Abs($d.input.remembered - $sizedTo) -le 2) "remembered=$($d.input.remembered) set=$sizedTo"
    Dodona @('ui', 'input-resize', 'reset') | Out-Null   # leave the box as the suite found it


    # ---- expanding puts it back ----------------------------------------------------------
    Dodona @("lane-expand", "$chipLane") | Out-Null
    Start-Sleep -Seconds 2
    $d = Dump
    Check 'expanding_returns_it_to_the_grid' `
        (((@($d.slots).Count) -eq 3) -and ((@($d.collapsedLanes).Count) -eq 0)) `
        "tiles=$(@($d.slots).Count) chips=$(@($d.collapsedLanes).Count)"

    # ---- THE ONE THAT MATTERS: a collapsed lane can still say "you are needed" ------------
    # §8 forbids an active-but-invisible lane precisely because it could be blocked on you with
    # no visible signal. Collapsing must therefore never be a way to lose an attention signal —
    # the chip keeps its badge. `undo-route` is the cheapest way to a genuine UNACKED
    # announcement on a chosen lane (the same technique the badge-timing checks above use;
    # a `result` does not badge, by design — progress never badges).
    Dodona @("focus", "$chipLane") | Out-Null
    Dodona @("input", "note this for the collapsed lane") | Out-Null
    Start-Sleep -Seconds 2
    $route = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print(db.execute('SELECT id FROM routing_decisions ORDER BY id DESC LIMIT 1').fetchone()[0])
") | Out-String
    Dodona @("undo-route", $route.Trim()) | Out-Null
    Dodona @("lane-collapse", "$chipLane") | Out-Null
    Start-Sleep -Seconds 3
    $chip = @((Dump).collapsedLanes) | Where-Object { $_.lane -eq $chipLane }
    Check 'a_collapsed_lane_still_shows_its_badge' ($chip.badge -ge 1) ($chip | ConvertTo-Json -Compress)
    Dodona @("ui", "screenshot", "--out", "$out\collapsed-live.png") | Out-Null

    # tidy: expanded and stopped before the shell section takes the window over
    Dodona @("lane-expand", "$chipLane") | Out-Null
    foreach ($t in @((Dump).slots)) { Dodona @("lane-stop", "$($t.lane)") | Out-Null }
    Start-Sleep -Seconds 1

    Dodona @("ui", "screenshot", "--out", "$out\after-typing.png") | Out-Null
    Dodona @("ui", "close") | Out-Null
    Start-Sleep -Milliseconds 600

    # =====================================================================================
    # THE ONE-WINDOW SHELL (docs/WORKSPACES-CONCIERGE.md §4 and §6)
    #
    # Driven the way a person drives it -- open the shell, look at what is on screen, click a
    # band -- because that is the whole reason this suite exists: dumps and screenshots proved
    # the UI could REPORT correctly while the first thing a person tried was a dead end.
    # =====================================================================================

    # ---- boot-to-zero: a window over nothing awake. A REAL state (§4), not an error --------
    # Nothing is awake yet (the first daemon is still up, so stop it for this check to mean
    # what it says).
    Dodona @("stop-daemon") | Out-Null
    Start-Sleep -Seconds 1

    # ---- a BARE launch (no --root, no --workspace, no --shell) IS the shell ----------------
    # The folder picker that used to answer a bare launch was removed 2026-08-18 on the
    # operator's direction (WORKSPACES-CONCIERGE.md §6.1): a workspace is named, not
    # located, so there is no folder question to ask on the way in. If this check fails,
    # a bare launch opened something that does not answer the shell pipe — which for a
    # person means the front door regressed to a dialog.
    $bareUi = Start-Process $ui -ArgumentList "--test-window" -PassThru
    Start-Sleep -Seconds 3
    $bz = $null
    try { $bz = (& $dodona ui dump --shell) | Out-String | ConvertFrom-Json } catch { }
    Check 'bare_launch_is_the_shell_booted_to_zero' ($null -ne $bz -and $bz.bootToZero -eq $true) `
        ($(if ($null -eq $bz) { 'shell pipe did not answer' } else { $bz | ConvertTo-Json -Compress -Depth 3 }))
    (& $dodona ui close --shell) | Out-Null
    Start-Sleep -Milliseconds 600
    if ($bareUi -and -not $bareUi.HasExited) { try { Stop-Process -Id $bareUi.Id -Force } catch { } }

    $shellUi = Start-Process $ui -ArgumentList "--shell", "--test-window" -PassThru
    Start-Sleep -Seconds 3
    function ShellDump() { (& $dodona ui dump --shell) | Out-String | ConvertFrom-Json }
    $z = ShellDump
    Check 'shell_boots_to_zero_with_nothing_awake' ($z.bootToZero -eq $true) ($z | ConvertTo-Json -Compress -Depth 3)
    Check 'boot_to_zero_shows_no_bands' ((@($z.bands).Count) -eq 0) ''
    Check 'boot_to_zero_still_has_an_input_box' ($z.window.h -gt 0 -and $z.workspaceName -eq '') $z.workspaceName

    # ---- a workspace waking up appears, without the operator doing anything ---------------
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon-back.out" -RedirectStandardError "$out\daemon-back.err"
    Start-Sleep -Seconds 2
    $z = ShellDump
    Check 'a_waking_workspace_takes_the_grid' ($z.bootToZero -eq $false -and $z.workspaceName -eq $ws.Name) `
        "bootToZero=$($z.bootToZero) ws=$($z.workspaceName)"

    # ---- a SECOND awake workspace becomes a band (§6, shape B) ---------------------------
    $root2 = Join-Path $env:TEMP ("dodona-uiuse2-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$root2\src" | Out-Null
    Set-Content "$root2\src\other.cs" "// other"
    Set-Content "$root2\dodona.json" (@{ main = 'main'; agent = $fake } | ConvertTo-Json)
    git -C $root2 init -b main -q
    git -C $root2 add -A
    git -C $root2 -c user.email=t@t -c user.name=t commit -q -m init
    $ws2 = Get-WorkspacePaths $dodona $root2
    $daemon2 = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws2.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon2.out" -RedirectStandardError "$out\daemon2.err"
    Start-Sleep -Seconds 2
    # Typed input, not lane-start: it makes a lane (so the band has a chip) AND announces
    # (so the merged feed has a row from this workspace to label). lane-start announces
    # nothing, which left the union with only one workspace's rows in it.
    (& $dodona input "say sort out the OTHER thing" --workspace $ws2.Id) | Out-Null
    Start-Sleep -Seconds 3

    $z = ShellDump
    $band = @($z.bands) | Where-Object { $_.name -eq $ws2.Name }
    Check 'second_awake_workspace_becomes_a_band' ($null -ne $band) ((@($z.bands).name) -join ',')
    Check 'band_carries_its_lane_chips' ((@($band.lanes).title) -contains 'OTHER') (@($band.lanes) | ConvertTo-Json -Compress)
    Check 'focused_workspace_still_holds_the_grid' ($z.workspaceName -eq $ws.Name) $z.workspaceName
    # A band is a VIEW, never an eviction: the banded workspace's lane is still alive in its
    # own store, holding its own slot (LANE-LIFECYCLE §2 stands).
    $otherStatus = (& $dodona status --workspace $ws2.Id) | Out-String
    Check 'band_does_not_evict_or_demote_a_lane' ($otherStatus -match 'OTHER' -and $otherStatus -match 'state=alive') $otherStatus

    # ---- clicking a band swaps which workspace holds the grid -----------------------------
    # `ui workspace` goes through EXACTLY the code path a click takes (FocusWorkspace), the
    # same reasoning as `ui type` -- and without needing focus, which is what stole the
    # operator's keyboard when this suite used SendKeys.
    $swap = (& $dodona ui workspace $ws2.Name --shell) | Out-String
    Start-Sleep -Milliseconds 800
    $z = ShellDump
    Check 'band_click_swaps_the_grid' ($z.workspaceName -eq $ws2.Name) "swap='$($swap.Trim())' now='$($z.workspaceName)'"
    $backBand = @($z.bands) | Where-Object { $_.name -eq $ws.Name }
    Check 'the_previous_workspace_becomes_a_band' ($null -ne $backBand) ((@($z.bands).name) -join ',')
    Check 'the_grid_shows_the_new_workspaces_lanes' `
        ((@($z.slots | Where-Object { -not $_.empty }).title) -contains 'OTHER') `
        ((@($z.slots | Where-Object { -not $_.empty }).title) -join ',')

    # ---- the feed is a UNION across workspaces, with a workspace chip per row (§6) --------
    $wsLabels = @(@($z.feed) | ForEach-Object { $_.workspace } | Where-Object { $_ } | Select-Object -Unique)
    Check 'merged_feed_spans_both_workspaces' ($wsLabels.Count -ge 2) ($wsLabels -join ',')
    Check 'merged_feed_labels_rows_by_workspace' `
        (($wsLabels -contains $ws.Name) -and ($wsLabels -contains $ws2.Name)) ($wsLabels -join ',')

    # ---- a live SHELL window hot-swaps on `ui update` ---------------------------------------
    # This path had ZERO coverage and shipped broken: the incumbent respawned its successor
    # as `--workspace <shell-sentinel>`, which no registry resolves, so the successor died
    # at startup, never signalled ready, and the incumbent silently kept the old build. From
    # where the operator sits that is indistinguishable from "publish never built anything"
    # (2026-08-18). The successor must respawn as what it is: --shell.
    $uiCopy = Join-Path $env:TEMP ("dodona-uiswap-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Copy-Item (Split-Path $ui) $uiCopy -Recurse
    $upd = (& $dodona ui update "$uiCopy\DodonaUi.exe" --shell) | Out-String
    Check 'shell_ui_update_hands_off' ($upd -match 'updated:') $upd.Trim()
    $successor = $null
    $z = $null
    $deadline = [Environment]::TickCount + 12000
    while ([Environment]::TickCount -lt $deadline) {
        Start-Sleep -Milliseconds 600
        if ($null -eq $successor) {
            $successor = Get-CimInstance Win32_Process -Filter "Name='DodonaUi.exe'" |
                Where-Object { $_.ExecutablePath -like "$uiCopy*" } | Select-Object -First 1
        }
        try { $z = (& $dodona ui dump --shell) | Out-String | ConvertFrom-Json } catch { $z = $null }
        if ($successor -and $z) { break }
    }
    Check 'shell_successor_is_the_new_binary' ($null -ne $successor) 'no DodonaUi.exe running from the copied build'
    Check 'shell_survives_the_swap' ($null -ne $z -and (@($ws.Name, $ws2.Name) -contains $z.workspaceName)) `
        ($(if ($null -eq $z) { 'shell pipe did not answer after the swap' } else { $z.workspaceName }))

    # --shell, not the Dodona helper: that one appends --root and would address the
    # single-workspace window's pipe, which is already closed.
    (& $dodona ui screenshot --out "$out\shell-bands.png" --shell) | Out-Null
    (& $dodona ui close --shell) | Out-Null
    Start-Sleep -Milliseconds 600
    Dodona @("stop-daemon") | Out-Null
    (& $dodona stop-daemon --workspace $ws2.Id) | Out-Null
}
finally {
    foreach ($proc in $uiProc, $shellUi, $bareUi, $daemon, $daemon2) {
        if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    }
    # The swap successor is a NEW pid the variables above never saw — resolve it by the
    # exe path unique to this run's copied build, never by bare process name (CLAUDE.md §4).
    if ($uiCopy) {
        Get-CimInstance Win32_Process -Filter "Name='DodonaUi.exe'" |
            Where-Object { $_.ExecutablePath -like "$uiCopy*" } |
            ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
        Remove-Item $uiCopy -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    # Both workspaces' shims, each resolved from its OWN workspace directory.
    Stop-WorkspaceShims $wsDir
    if ($ws2) { Stop-WorkspaceShims $ws2.Dir }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # The shell's input box starts a concierge on demand (§4: boot-to-zero must not be a dead
    # end), so this suite is responsible for stopping it -- a concierge is machine-global, and
    # a leaked one used to answer the NEXT suite's questions from this suite's registry.
    (& $dodona concierge-stop) 2>$null | Out-Null
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- UI USE ACCEPTANCE (driven like a person, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
