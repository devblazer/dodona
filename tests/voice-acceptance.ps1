# VOICE acceptance: dictation at the WINDOW (docs/VOICE-INPUT-PLAN.md Phase A).
#
# The operator's constraint, verbatim: "Send will still need an enter." The unit suite proves
# the algebra in a second; these prove it where the operator actually meets it -- CLAUDE.md 3:
# dumps prove the UI reports correctly while the first thing a person tries is still a dead end.
#
# WHY ITS OWN SUITE RATHER THAN ui-use, where the proposal put it. Measured, not preferred:
# with these checks inside ui-use that suite ran 113.1s against a ~70s baseline, crossing the
# ~90s line at which the plan says to split. ui-use is already a monolith whose failures cascade
# and which went intermittently red at concurrency 5; three more window restarts inside it is
# the wrong place for them. Recorded as D-V12.
#
# NO MICROPHONE IS EVER OPENED. tests\_workspace.ps1 sets DODONA_UI_MIC=off for every suite, so
# Recognizers.Create refuses to construct a real recogniser at all and the window gets the fake
# (D-V4). A check that grabbed the mic while the operator was in a call would be CLAUDE.md 4's
# incident in a new costume.
#
# `ui heard` is the fake's mouth, and it lands in MainWindow.OnHeard -- the SAME method the real
# engine's event raises into. That is the `ui type` reasoning one layer down: a check must drive
# the affordance a person touches, not a rehearsal of it.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree, and DODONA_UI_MIC=off, for this run only.
$dodonaHome = Use-IsolatedDodonaHome 'voice'
# Binaries are a COPY in this run's own DODONA_HOME -- nothing here executes out of src\...\bin,
# so a leaked process can never hold the file the compiler must overwrite.
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'voice-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-voice-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\app.cs" "// app"
Set-Content "$root\.gitignore" ".dodona/"
# The fake agent stands in for claude wherever the daemon starts one: Enter really sends, and a
# real model call for a test message would burn the scarce resource (CLAUDE.md 0.1).
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
# "the window is not answering yet" as a VALUE rather than an exception, so Wait-Until can poll it.
function DumpOrNull() { try { Dump } catch { $null } }
# The box's text, with "there is no window" folded to empty -- every check below reads it, and a
# null property access inside a Wait-Until scriptblock is a silent false rather than a diagnosis.
function BoxText() { $d = DumpOrNull; if ($null -eq $d) { '' } else { "$($d.input.text)" } }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

# This suite's OWN window, never someone else's: a -like "Dodona*" lookup also matches the
# operator's editor window, and the first version of a grip check in ui-use found THAT one.
# Cached, because FindAll on RootElement walks every top-level window on the desktop.
$script:uiWin = $null
function UiWindow([string]$nameLike = "Dodona*$(Split-Path $root -Leaf)") {
    if ($null -ne $script:uiWin) {
        try { $null = $script:uiWin.Current.Name; return $script:uiWin } catch { $script:uiWin = $null }
    }
    $all = $AE::RootElement.FindAll('Children',
        (New-Object System.Windows.Automation.PropertyCondition $AE::ControlTypeProperty, ([System.Windows.Automation.ControlType]::Window)))
    foreach ($w in $all) { if ($w.Current.Name -like $nameLike) { $script:uiWin = $w; return $w } }
    return $null
}
function Reset-UiWindow { $script:uiWin = $null }
function ByName($win, [string]$name) {
    if (-not $win) { return $null }
    $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name))
}

$daemon = $null
$uiProc = $null
try {
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the UI window answers ui dump' | Out-Null

    # ---- the toggle exists, and it is a TOGGLE -------------------------------------------
    $d = DumpOrNull
    Check 'dictation_starts_off' `
        ($null -ne $d -and $d.listen.state -eq 'off' -and "$($d.listen.says)".Length -eq 0) `
        "state=[$($d.listen.state)] says=[$($d.listen.says)]"

    $listenReply = (Dodona @('ui', 'listen', 'on')) | Out-String
    Wait-Until { (DumpOrNull).listen.state -eq 'listening' } 20000 'the toggle reports listening' | Out-Null
    $d = DumpOrNull
    Check 'the_listen_verb_reaches_the_window_at_all' `
        ($listenReply -notmatch 'unknown ui verb' -and $null -ne $d -and $d.listen.state -eq 'listening') `
        "reply=[$($listenReply.Trim())] state=[$($d.listen.state)]"

    # No suite may ever open the operator's microphone (D-V4). If this ever reads 'sapi', the
    # override has stopped working and every run of every suite is holding a capture device.
    Check 'the_suite_never_opens_a_real_microphone' `
        ($null -ne $d -and $d.listen.engine -eq 'fake') "engine=[$($d.listen.engine)]"

    # ---- a recognised phrase lands in the box, and NOTHING is sent -------------------------
    # THE ASSERTION IS "THE BOX WAS NOT CLEARED", not "the feed did not grow". SubmitInput
    # clears the box, so a box still holding the words is exact proof no submit happened. A feed
    # COUNT would be flaky -- the feed is a union and can grow for reasons unrelated to
    # dictation -- so the feed is checked by MATCHING this suite's own sentence instead.
    Dodona @('ui', 'heard', 'hello from dictation') | Out-Null
    Wait-Until { (BoxText) -match 'from dictation' } 20000 'the heard phrase reaches the box' | Out-Null
    $d = DumpOrNull
    $feedRows = @($d.feed)
    $echoed = @($feedRows | Where-Object { "$($_.body)" -match 'from dictation' }).Count
    Check 'heard_text_lands_in_box_unsent' `
        ($null -ne $d -and $d.input.text -match 'from dictation' -and $echoed -eq 0) `
        "text=[$($d.input.text)] feedRowsMatching=$echoed"

    # A recogniser hands back lower case mid-sentence; an empty box starts a sentence.
    Check 'dictation_capitalises_the_start_of_the_box' `
        ($null -ne $d -and $d.input.text -cmatch '^Hello from dictation') "text=[$($d.input.text)]"

    # ---- THE CHECK THIS FEATURE EXISTS FOR -------------------------------------------------
    # Speaking "enter" must not send. Nor "send", "submit" or "go". They are ordinary text and
    # they appear as ordinary text -- not because they are on a block list that could be
    # forgotten, but because DictationAct has no member that means send, so there is nowhere
    # else for them to go (src\Dodona\Dictation.cs, class note). Delete the guard, say "enter",
    # and the message must still not move.
    foreach ($word in @('enter', 'send', 'submit', 'go')) { Dodona @('ui', 'heard', $word) | Out-Null }
    Wait-Until { (BoxText) -match 'go\s*$' } 20000 'all four spoken send-words reached the box' | Out-Null
    $d = DumpOrNull
    $feedRows = @($d.feed)
    $echoed = @($feedRows | Where-Object { "$($_.body)" -match 'from dictation' }).Count
    Check 'spoken_send_words_do_not_submit' `
        ($null -ne $d -and $d.input.text -match 'from dictation' `
         -and $d.input.text -match 'enter' -and $d.input.text -match 'send' `
         -and $d.input.text -match 'submit' -and $d.input.text -match 'go' -and $echoed -eq 0) `
        "text=[$($d.input.text)] feedRowsMatching=$echoed"

    # ---- "new line" is the one spoken control that IS allowed -------------------------------
    # Through InputKey(shift: true), the same method Shift+Enter uses.
    $linesBefore = (DumpOrNull).input.lines
    Dodona @('ui', 'heard', 'new line') | Out-Null
    Wait-Until { (DumpOrNull).input.lines -gt $linesBefore } 20000 'the spoken newline reaches the box' | Out-Null
    $d = DumpOrNull
    Check 'spoken_new_line_inserts_one_at_the_window' `
        ($null -ne $d -and $d.input.lines -eq ($linesBefore + 1)) `
        "lines=$($d.input.lines) before=$linesBefore"

    # ---- an unsettled hypothesis never enters the box (D-V6) --------------------------------
    # In the box it would make input.text non-deterministic and turn every existing input check
    # in ui-use intermittent.
    $beforePartial = BoxText
    Dodona @('ui', 'heard', 'this tail is unsettled', '--partial') | Out-Null
    Wait-Until { (DumpOrNull).listen.partial -match 'unsettled' } 20000 'the partial reaches the indicator' | Out-Null
    $d = DumpOrNull
    Check 'partial_is_not_in_input_text' `
        ($null -ne $d -and $d.input.text -notmatch 'unsettled' -and $d.listen.partial -match 'unsettled' `
         -and $d.input.text -eq $beforePartial) `
        "text=[$($d.input.text)] partial=[$($d.listen.partial)]"

    # ---- the submit race, end to end (section 4) --------------------------------------------
    # A result recognised under an older epoch is the tail of a sentence already sent. Spliced,
    # it would open the NEXT message with the end of the last one -- the one bug here a person
    # would find baffling in the wild.
    $beforeStale = BoxText
    $droppedBefore = (DumpOrNull).listen.dropped
    Dodona @('ui', 'heard', 'tail of the last message', '--epoch', '-1') | Out-Null
    Wait-Until { (DumpOrNull).listen.dropped -gt $droppedBefore } 20000 'the stale result is counted as dropped' | Out-Null
    $d = DumpOrNull
    Check 'stale_epoch_result_dropped_at_the_window' `
        ($null -ne $d -and $d.input.text -notmatch 'tail of the last message' `
         -and $d.input.text -eq $beforeStale -and $d.listen.dropped -eq ($droppedBefore + 1)) `
        "text=[$($d.input.text)] dropped=$($d.listen.dropped) before=$droppedBefore"

    # ---- and Enter STILL SENDS, which is the other half of the operator's sentence ----------
    $sentText = BoxText
    Dodona @('ui', 'key', 'enter') | Out-Null
    Wait-Until { (BoxText).Trim().Length -eq 0 } 20000 'Enter cleared the box' | Out-Null
    $d = DumpOrNull
    Check 'enter_still_sends_after_dictation' `
        ($null -ne $d -and "$($d.input.text)".Trim().Length -eq 0 -and $sentText -match 'from dictation') `
        "textAfter=[$($d.input.text)] sent=[$sentText]"

    # ---- the mic is a real BUTTON, not only a verb -------------------------------------------
    # CLAUDE.md 3.1: an affordance no verb can reach is where the next defect will live -- and
    # the inverse holds too. The five lane actions had a verb and no coverage of the CLICK, and
    # the defect that broke every one of those clicks shipped twice. This invokes the button
    # through UIA, which is what a mouse does.
    $micBtn = $null
    Wait-Until { $null -ne ($script:micBtn = ByName (UiWindow) 'microphone') } 20000 'the mic button appears in the UIA tree' | Out-Null
    Check 'the_mic_is_a_real_button' ($null -ne $micBtn) 'no element named "microphone" in the UIA tree'
    # OUTSIDE the if, deliberately. Written inside it this check read MISSING against HEAD --
    # the button does not exist there, so the check never ran, and `dev prove` is explicit that
    # a check which never ran is not proven, it is unproven. A guard that skips the assertion
    # when its subject is absent is a check that cannot fail for the one reason it exists.
    $stateBefore = (DumpOrNull).listen.state
    if ($micBtn) {
        ($micBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Wait-Until { (DumpOrNull).listen.state -ne $stateBefore } 20000 'clicking the mic toggles it' | Out-Null
    }
    $d = DumpOrNull
    Check 'clicking_the_mic_toggles_listening' `
        ($null -ne $micBtn -and $null -ne $d -and $d.listen.state -ne $stateBefore) `
        "button=$($null -ne $micBtn) before=[$stateBefore] after=[$($d.listen.state)]"
    # Back on, for the persistence check below.
    if ((DumpOrNull).listen.state -ne 'listening') { Dodona @('ui', 'listen', 'on') | Out-Null }
    Wait-Until { (DumpOrNull).listen.state -eq 'listening' } 20000 'the toggle is back on' | Out-Null

    # ---- the toggle is REMEMBERED (D-V2) -----------------------------------------------------
    # A toggle that resets itself is a button, which is not what was asked for; and publish
    # hot-swaps this window, so an unremembered toggle would go silently deaf on every swap --
    # the "quietly outdated" failure the standing directive names. It lives in ui.json under
    # DODONA_HOME, beside the box's remembered height.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before reopening' | Out-Null
    Reset-UiWindow
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the reopened window answers' | Out-Null
    $d = DumpOrNull
    Check 'listening_toggle_persists' `
        ($null -ne $d -and $d.listen.state -eq 'listening' -and $d.listen.remembered -eq $true) `
        "state=[$($d.listen.state)] remembered=[$($d.listen.remembered)]"

    # ---- a failed microphone must not produce a MODAL (D-V3) ---------------------------------
    # A test window is forbidden from producing one, so a modal would be permanently untestable
    # -- which is exactly why PickerWindow and StartLaneWindow have no coverage at all. THE
    # PROOF THAT NO MODAL APPEARED IS THAT THE WINDOW STILL ANSWERS: a MessageBox blocks the
    # dispatcher, so `ui dump` would never return and this check would time out rather than
    # merely fail. DODONA_UI_MIC=fail is what makes the error state reachable without unplugging
    # a device.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the failing-mic window' | Out-Null
    Reset-UiWindow
    $micWas = $env:DODONA_UI_MIC
    $env:DODONA_UI_MIC = 'fail'
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    $answered = Wait-Until { $null -ne (DumpOrNull) } 30000 'the window answers with the mic forced to fail'
    $env:DODONA_UI_MIC = $micWas          # every later process is back to "open nothing"
    $d = DumpOrNull
    # A MODAL IS DETECTED BY COUNTING TOP-LEVEL WINDOWS, and the first version of this check was
    # wrong in a way worth recording, because it is the exact disease this repo keeps paying for.
    #
    # It asserted "the window still ANSWERS `ui dump`", on the stated reasoning that a MessageBox
    # blocks the dispatcher so the dump would never return. That reasoning is FALSE, and it was
    # written down as fact. Measured 2026-08-20 by putting a real MessageBox on the failure path:
    # the suite still answered and the check still passed. Win32 modal dialogs run their own
    # NESTED MESSAGE PUMP, so the dispatcher keeps processing while the dialog is up -- which is
    # precisely why a modal is invisible to every assertion this suite makes about content.
    #
    # So it counts WINDOWS instead. A MessageBox is a second top-level window owned by the UI
    # process; the main window is the only one there should ever be. That is a direct observation
    # of the thing D-V3 forbids, rather than an inference from a symptom that does not occur.
    #
    # `dev prove` reports this one VACUOUS, and that is structural rather than a weak assertion:
    # HEAD has no dictation at all, so it shows no modal either and the absence holds trivially.
    # A check about the ABSENCE of something cannot be failed by a build that never had it. It
    # was therefore demonstrated the way CLAUDE.md 0.3 prescribes -- MessageBox.Show put on the
    # failure path in Recognizers.Create on purpose -- and it went RED, reading:
    #
    #   no_modal_when_the_mic_fails: FAIL answered=True topLevelWindows=2 names=[ | Dodona - ...]
    #
    # Note `answered=True` in that red. The modal was up and the window answered anyway. That is
    # the measurement that killed the first version of this check, and it is why the window count
    # is the assertion.
    $topLevel = @()
    if ($uiProc) {
        $topLevel = @($AE::RootElement.FindAll('Children',
            (New-Object System.Windows.Automation.PropertyCondition $AE::ProcessIdProperty, $uiProc.Id)))
    }
    Check 'no_modal_when_the_mic_fails' `
        ($answered -and $null -ne $d -and $topLevel.Count -eq 1) `
        ("answered=$answered topLevelWindows=$($topLevel.Count) " +
         "names=[$(@($topLevel | ForEach-Object { $_.Current.Name }) -join ' | ')]")

    # ON AND DEAF MUST NEVER READ AS ON. This is the failure this feature is most likely to
    # ship: a toggle that says "listening" while the engine is dead, which is precisely the
    # silent degrade that cost two days on the routing ladder (CLAUDE.md 3).
    Check 'a_failed_mic_reads_as_error_not_listening' `
        ($null -ne $d -and $d.listen.state -eq 'error' `
         -and "$($d.listen.says)" -notmatch 'listening' -and "$($d.listen.error)".Trim().Length -gt 0) `
        "state=[$($d.listen.state)] says=[$($d.listen.says)] error=[$($d.listen.error)]"

    # The reason reaches the operator in WORDS, in the indicator -- not an error code, and not
    # a link to a control panel that may not exist on that build (section 7).
    Check 'a_failed_mic_says_why_in_words' `
        ($null -ne $d -and "$($d.listen.says)" -match 'microphone') "says=[$($d.listen.says)]"

    # ---- the pose, so a screenshot can see the indicator --------------------------------------
    $poseReply = (Dodona @('ui', 'pose', 'listening')) | Out-String
    $d = DumpOrNull
    Check 'the_listening_pose_exists' `
        ($poseReply -notmatch 'unknown pose' -and $null -ne $d -and $d.listen.state -eq 'listening' `
         -and "$($d.listen.partial)".Length -gt 0) `
        "reply=[$($poseReply.Trim())] state=[$($d.listen.state)] partial=[$($d.listen.partial)]"

    # Leave the toggle OFF, so this suite does not hand the next run a ui.json that arms a
    # microphone. DODONA_UI_MIC=off makes that harmless anyway, and both belts are cheap.
    Dodona @('ui', 'pose', 'live') | Out-Null
    Dodona @('ui', 'listen', 'off') | Out-Null
}
finally {
    # 'Continue' FOR THE WHOLE CLEANUP: under 'Stop', ANY native command writing ANY line to
    # stderr raises NativeCommandError and aborts (CLAUDE.md 0.2), which would throw out of this
    # finally, skip the tally, and make dev.ps1 report the whole suite as `no tally line` --
    # counted as nothing. That silently discarded 59 of ui-use's checks on 2026-08-19.
    $ErrorActionPreference = 'Continue'
    foreach ($proc in $uiProc, $daemon) {
        if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    }
    # Scoped cleanup, from THIS workspace's own shim-info files. Killing by process NAME once
    # murdered the operator's live session's shim and UI mid-dogfood (CLAUDE.md 4).
    if ($wsDir) { Stop-WorkspaceShims $wsDir }
    if ($storeDb) { Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue }
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_UI_MIC -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? Last, so it reports only what
    # survived the cleanup above. It reports; it never kills -- a check that killed what it
    # found would hide the leak it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- VOICE ACCEPTANCE (dictation at the window, no microphone, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
