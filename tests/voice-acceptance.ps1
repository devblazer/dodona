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
# The box MINUS the unsettled dictation tail (D-E17). The tail lives in input.text now, so any
# check about what a person has FINISHED saying has to be able to subtract it -- which is exactly
# the determinism D-V6 was protecting, kept without pushing the words out of the box.
function CommittedText() {
    $d = DumpOrNull
    if ($null -eq $d) { return '' }
    $t = "$($d.input.text)"
    $at = [int]$d.input.pendingAt
    $len = [int]$d.input.pending
    if ($at -ge 0 -and $len -gt 0 -and ($at + $len) -le $t.Length) { $t.Remove($at, $len) } else { $t }
}

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

    # ---- the unsettled tail SHOWS IN THE BOX, at the caret (D-E17 reverses D-V6) -------------
    # The operator's words after using it: "while I'm recording, the word should appear in the text
    # box from wherever the cursor was left. Currently it's kinda showing above the text box, which
    # was weird." D-V6 had kept partials out of input.text to protect check determinism -- a
    # testability argument spent against the thing the feature is FOR, which is the wrong trade.
    #
    # Determinism is kept by TRACKING the tail instead: dump.input.pending/pendingAt say which part
    # of the box is unsettled, so CommittedText() can subtract it. This check pins both halves --
    # the tail is visible in the box, AND it is subtractable.
    $committedBefore = CommittedText
    Dodona @('ui', 'heard', 'this tail is unsettled', '--partial') | Out-Null
    Wait-Until { (DumpOrNull).input.text -match 'unsettled' } 20000 'the partial reaches the box' | Out-Null
    $d = DumpOrNull
    Check 'the_unsettled_tail_shows_in_the_box_at_the_caret' `
        ($null -ne $d -and $d.input.text -match 'unsettled' -and $d.listen.partial -match 'unsettled' `
         -and [int]$d.input.pending -gt 0 -and (CommittedText) -eq $committedBefore) `
        "text=[$($d.input.text)] pending=$($d.input.pending) committed=[$(CommittedText)]"

    # A REWRITING hypothesis must REPLACE the tail, never append to it. This is the failure that
    # would look almost right: every interim spliced in turn, giving "this tail is unsettledthis
    # tail is unsettled now" in the box.
    Dodona @('ui', 'heard', 'this tail is unsettled now', '--partial') | Out-Null
    Wait-Until { (DumpOrNull).input.text -match 'unsettled now' } 20000 'the rewritten partial lands' | Out-Null
    $d = DumpOrNull
    $tailCopies = ([regex]::Matches("$($d.input.text)", [regex]::Escape('this tail is unsettled'), 'IgnoreCase')).Count
    Check 'a_rewritten_partial_replaces_the_tail_instead_of_appending' `
        ($null -ne $d -and $tailCopies -eq 1 -and (CommittedText) -eq $committedBefore) `
        "text=[$($d.input.text)] copies=$tailCopies committed=[$(CommittedText)]"

    # ---- the submit race, end to end (section 4) --------------------------------------------
    # A result recognised under an older epoch is the tail of a sentence already sent. Spliced,
    # it would open the NEXT message with the end of the last one -- the one bug here a person
    # would find baffling in the wild.
    #
    # Compared on COMMITTED text (D-E17): there is a live unsettled tail in the box from the two
    # checks above, and a stale FINAL clears it, so raw input.text legitimately changes while the
    # property under test -- nothing was committed -- holds.
    $beforeStale = CommittedText
    $droppedBefore = (DumpOrNull).listen.dropped
    Dodona @('ui', 'heard', 'tail of the last message', '--epoch', '-1') | Out-Null
    Wait-Until { (DumpOrNull).listen.dropped -gt $droppedBefore } 20000 'the stale result is counted as dropped' | Out-Null
    $d = DumpOrNull
    Check 'stale_epoch_result_dropped_at_the_window' `
        ($null -ne $d -and $d.input.text -notmatch 'tail of the last message' `
         -and (CommittedText) -eq $beforeStale -and $d.listen.dropped -eq ($droppedBefore + 1)) `
        "committed=[$(CommittedText)] before=[$beforeStale] dropped=$($d.listen.dropped)"

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

    # ══ THE CLOUD ENGINE (docs/VOICE-ENGINE-PLAN.md E6) ═══════════════════════════════════════
    #
    # Everything above this line predates the engine and passes unchanged, which is the property
    # that made swapping the engine safe at all: the suites drive `ui heard` into the real
    # OnHeard splice, so they never needed a recogniser.
    #
    # These four are new in kind, because the engine is now a NETWORK CALL ON THE OPERATOR'S
    # CREDENTIALS. A check that opened one would be CLAUDE.md 4's incident with a bill attached.

    # ---- MIC OFF MUST OPEN NO SOCKET, and the ORDER is the assertion (D-E5) -------------------
    # The most important new check in the feature. DODONA_UI_MIC=off has to short-circuit BEFORE
    # any connect -- not "connect, then do not listen", which would pass any check that looked
    # only at the reported state. So this reads two things: the engine the window actually built,
    # and the TCP connections the UI process owns.
    #
    # Proved RED by moving the MicDisabled early-return in Recognizers.Create to AFTER the
    # DeepgramRecognizer construction, which gave: engine=[deepgram] sockets=0.
    #
    # ON THE SOCKET HALF, STATED PLAINLY: with the env var cleared by _workspace.ps1 and no token
    # file in this run's isolated DODONA_HOME, SpeechAuth has nothing to present, so the shipped
    # code returns before a socket exists and the count is structurally 0. That half is therefore
    # VACUOUS BY CONSTRUCTION here, exactly as no_modal_when_the_mic_fails is, and it was
    # demonstrated the way CLAUDE.md 0.3 prescribes instead: with the reorder above AND
    # DODONA_STT_TOKEN set to a junk string, the connect ran and it read sockets=1. It stays in
    # the assertion because it is the thing that will still be true when a token DOES exist on
    # this machine, which is when the ordering starts to matter for real.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the mic-off window' | Out-Null
    Reset-UiWindow
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the mic-off window answers' | Out-Null
    Dodona @('ui', 'listen', 'on') | Out-Null
    Wait-Until { (DumpOrNull).listen.state -eq 'listening' } 20000 'the fake reports listening' | Out-Null
    $d = DumpOrNull
    # A direct observation, not an inference. Remote port 443 owned by the UI process: this window
    # reads SQLite and talks to a named pipe, so it has no other reason to hold one.
    $sockets = @(Get-NetTCPConnection -OwningProcess $uiProc.Id -ErrorAction SilentlyContinue |
                 Where-Object { $_.RemotePort -eq 443 })
    Check 'mic_off_opens_no_socket' `
        ($null -ne $d -and $d.listen.engine -eq 'fake' -and $sockets.Count -eq 0) `
        "engine=[$($d.listen.engine)] sockets=$($sockets.Count)"

    # ---- A DEAD NETWORK READS AS ERROR, NEVER AS LISTENING -----------------------------------
    # On and deaf must never look like on (section 5) -- and with a socket engine this is the
    # likely everyday failure rather than an exotic one.
    #
    # THE REAL ENGINE IS CONSTRUCTED HERE, deliberately, and it still reaches no network: the
    # endpoint is pointed at ws://127.0.0.1:1, which the loopback stack refuses instantly. So the
    # genuine socket failure path runs -- connect, refusal, classify, Failed, error state -- with
    # no egress, no credential of the operator's, and nothing to pay for. That is what makes this
    # a real test of the shipped code rather than of the fake.
    #
    # The junk token is required only to get past SpeechAuth; it is never sent anywhere but to a
    # closed port on this machine.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the dead-network window' | Out-Null
    Reset-UiWindow
    $micWas = $env:DODONA_UI_MIC
    Remove-Item env:DODONA_UI_MIC -ErrorAction SilentlyContinue   # let the REAL engine be built
    $env:DODONA_STT_ENDPOINT = 'ws://127.0.0.1:1/dead'
    $env:DODONA_STT_TOKEN = 'not-a-real-token-and-it-goes-to-a-closed-local-port'
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the dead-network window answers' | Out-Null
    Dodona @('ui', 'listen', 'on') | Out-Null
    $reachedError = Wait-Until { (DumpOrNull).listen.state -eq 'error' } 30000 'the dead network lands in error'
    $d = DumpOrNull
    Check 'a_dead_network_reads_as_error_not_listening' `
        ($reachedError -and $null -ne $d -and $d.listen.state -eq 'error' `
         -and $d.listen.engine -eq 'deepgram' `
         -and "$($d.listen.says)" -notmatch 'listening' -and "$($d.listen.error)".Trim().Length -gt 0) `
        "state=[$($d.listen.state)] engine=[$($d.listen.engine)] says=[$($d.listen.says)] error=[$($d.listen.error)]"

    # ---- A SUITE CANNOT AUTHENTICATE, EVEN HOLDING THE REAL ENGINE (D-E5, second lock) -------
    # THE CHECK THAT MATTERS MOST AFTER mic_off_opens_no_socket, and it exists because route 1
    # BROKE the property it restores. SpeechAuth reads the credential the claude CLI already holds,
    # at %USERPROFILE%\.claude\.credentials.json -- a path DODONA_HOME does not relocate. So when
    # that route landed, "a suite has no credential" silently stopped being true and the operator's
    # live token became readable from inside every test run.
    #
    # _workspace.ps1 sets DODONA_STT_NO_CLI_AUTH for every suite. This arms the REAL engine with no
    # endpoint override and no token override -- the exact configuration that authenticated for
    # real when it was measured by hand -- and demands it lands in error saying it has no
    # credential. Anything else means a suite can stream the operator's microphone on their bill.
    #
    # Proved RED by deleting the NoCliAuthVar early-return from ClaudeCliRoute:
    #   FAIL state=[listening] engine=[deepgram] error=[]  -- a test run, authenticated.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the no-auth window' | Out-Null
    Reset-UiWindow
    $micWas2 = $env:DODONA_UI_MIC
    Remove-Item env:DODONA_UI_MIC -ErrorAction SilentlyContinue      # the REAL engine
    Remove-Item env:DODONA_STT_TOKEN -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_STT_ENDPOINT -ErrorAction SilentlyContinue
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the no-auth window answers' | Out-Null
    Dodona @('ui', 'listen', 'on') | Out-Null
    $settled = Wait-Until { (DumpOrNull).listen.state -in @('listening', 'error') } 25000 'the engine settles'
    $d = DumpOrNull
    $noAuthSockets = @(Get-NetTCPConnection -OwningProcess $uiProc.Id -ErrorAction SilentlyContinue |
                       Where-Object { $_.RemotePort -eq 443 })
    $env:DODONA_UI_MIC = $micWas2
    Check 'a_suite_cannot_authenticate_even_with_the_real_engine' `
        ($settled -and $null -ne $d -and $d.listen.state -eq 'error' `
         -and "$($d.listen.error)" -match 'no speech credential' -and $noAuthSockets.Count -eq 0) `
        ("state=[$($d.listen.state)] engine=[$($d.listen.engine)] " +
         "error=[$($d.listen.error)] sockets=$($noAuthSockets.Count)")

    # ---- STARTING HAS A DEADLINE -------------------------------------------------------------
    # The genuinely new state to get wrong (section 6). SAPI's Start() was synchronous, so
    # `Starting` could not linger and Phase A got away without a deadline; a socket connect can
    # linger, and spike E1 found worse -- this endpoint's UPGRADE SUCCEEDS WITH NO CREDENTIAL and
    # refuses one frame later, so "Start() returned" no longer means "we are hearing".
    #
    # DODONA_UI_MIC=hang is a recogniser that never becomes ready and never fails, which is the
    # only way to reach this without a black-hole address and a long wait. It is the same
    # reasoning that earned `fail` its place in production code (D-V15): the error state was
    # otherwise unreachable without unplugging something.
    #
    # Proved RED by deleting the ArmStartingDeadline call from ArmMic: state=[starting].
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the hanging-start window' | Out-Null
    Reset-UiWindow
    Remove-Item env:DODONA_STT_ENDPOINT -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_STT_TOKEN -ErrorAction SilentlyContinue
    $env:DODONA_UI_MIC = 'hang'
    $env:DODONA_STT_CONNECT_MS = '600'     # so the deadline is ~2.6s, not ~10s
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the hanging-start window answers' | Out-Null
    Dodona @('ui', 'listen', 'on') | Out-Null
    # It must be in `starting` first, or this check would pass for a window that never tried.
    $sawStarting = Wait-Until { (DumpOrNull).listen.state -eq 'starting' } 10000 'the hung engine reports starting'
    $leftStarting = Wait-Until { (DumpOrNull).listen.state -eq 'error' } 20000 'the deadline lands in error'
    $d = DumpOrNull
    $env:DODONA_UI_MIC = $micWas
    Remove-Item env:DODONA_STT_CONNECT_MS -ErrorAction SilentlyContinue
    Check 'starting_has_a_deadline' `
        ($sawStarting -and $leftStarting -and $null -ne $d -and $d.listen.state -eq 'error' `
         -and "$($d.listen.says)" -notmatch 'listening' -and "$($d.listen.error)".Trim().Length -gt 0) `
        "sawStarting=$sawStarting state=[$($d.listen.state)] says=[$($d.listen.says)] error=[$($d.listen.error)]"

    # ---- AN INTERIM NEVER ENTERS THE BOX, against a REWRITING stream (D-V6) ------------------
    # partial_is_not_in_input_text above sends one partial. A real engine sends a SEQUENCE that
    # rewrites itself -- "run", "run the", "run the suites" -- and the failure mode this pins is
    # the one that would look almost right: every interim spliced, giving
    # "runrun therun the suites" in the box. Only the settled text may land, once.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'the window is gone before the interim-stream window' | Out-Null
    Reset-UiWindow
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the interim-stream window answers' | Out-Null
    Dodona @('ui', 'listen', 'on') | Out-Null
    $beforeStream = BoxText
    foreach ($hyp in 'publish', 'publish from', 'publish from the worktree') {
        Dodona @('ui', 'heard', $hyp, '--partial') | Out-Null
    }
    Wait-Until { (DumpOrNull).listen.partial -eq 'publish from the worktree' } 20000 'the last interim is held' | Out-Null
    $dMid = DumpOrNull
    # Now the settled text, the way TranscriptEndpoint promotes it.
    Dodona @('ui', 'heard', 'publish from the worktree') | Out-Null
    Wait-Until { (BoxText) -match 'publish from the worktree' } 20000 'the settled text lands' | Out-Null
    $d = DumpOrNull
    $box = "$($d.input.text)"
    # Exactly once: three interims plus a final must not leave four copies, or any prefix of one.
    # IGNORECASE, and the first version of this check was wrong for the lack of it -- the box read
    # "Publish from the worktree" because Dictation.Splice capitalises the start of an empty box
    # (section 4), so a case-sensitive count found 0 and the check failed while the behaviour was
    # correct. A false red costs exactly as much as a false green (CLAUDE.md 0.2).
    $occurrences = ([regex]::Matches($box, [regex]::Escape('publish from the worktree'),
                                     'IgnoreCase')).Count
    # THE VALUABLE HALF SURVIVES D-E17 UNCHANGED: a rewriting stream must leave exactly ONE copy.
    # What changed is the mid-stream expectation -- the tail is now IN the box (visible, pending>0)
    # rather than absent from it, and the settled text replaces it rather than joining it. The
    # failure this pins is the same one either way: "publishpublish frompublish from the worktree".
    Check 'an_interim_stream_leaves_exactly_one_copy_in_the_box' `
        ($null -ne $dMid -and "$($dMid.input.text)" -match 'publish from the worktree' `
         -and [int]$dMid.input.pending -gt 0 `
         -and $occurrences -eq 1 -and $box -notmatch 'publishpublish' `
         -and [int]$d.input.pending -eq 0 -and "$($d.listen.partial)".Length -eq 0) `
        ("midBox=[$($dMid.input.text)] midPending=$($dMid.input.pending) box=[$box] " +
         "occurrences=$occurrences pendingAfter=$($d.input.pending)")

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
    # The cloud engine's three overrides, unset unconditionally: a suite that died mid-check with
    # DODONA_STT_ENDPOINT still set would hand the next process in this shell a recogniser aimed
    # at a closed port, which reads as "dictation is broken" a long way from here.
    Remove-Item env:DODONA_STT_ENDPOINT -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_STT_TOKEN -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_STT_CONNECT_MS -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_STT_NO_CLI_AUTH -ErrorAction SilentlyContinue
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
