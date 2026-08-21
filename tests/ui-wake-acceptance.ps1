# UI WAKE acceptance: THE WINDOW OVER A SLEEPING WORKSPACE -- the state the operator's machine
# is in every morning, and the mirror of CLAUDE.md §3.1's "a daemon outlives its window".
# Closing the app stops nothing; a reboot with the shortcut relaunched leaves the opposite --
# a window up with nothing running behind it, rendering every lane as `alive` because the store
# reader is read-only, and looking perfectly healthy while it is not.
#
# Also the five lane-tile actions, which had no `ui` verb at all until they were added here:
# unreachable, not merely untested, which is exactly where the defect above lived.
#
# Driven through UI Automation (WPF exposes it natively), and the agent is the fake one
# via dodona.json's "agent" key -- zero model calls.
#
# ONE OF FOUR. `ui-use` was a 1221-line, 130-check, 88.8-second monolith that had to run
# ALONE (SoloSuites in tools/dev.ps1), so it set the pace for the whole gate -- and because it
# was one unbroken chain its failures CASCADED: two missed interactions became six red checks,
# "one problem arriving looking like six" (issue #2). It is now `ui-grid`, `ui-shell`, `ui-ask`
# and `ui-wake`, cut at the four fixture boundaries it already had -- each one already stood up
# its own daemon and its own window.
#
# THE FIXTURE HEADER BELOW IS DUPLICATED IN ALL FOUR, ON PURPOSE. Every suite in this repo is
# self-contained -- each defines its own `Check`, and voice-acceptance.ps1 already carries its
# own verbatim copy of UiWindow/ByName. Hoisting them into tests/_workspace.ps1 would make
# every non-window suite pay for UIAutomation's Add-Type, and a suite you cannot read end to
# end is the thing this split was for. If one of these helpers ever has to change, it changes
# in five files, and that is the price that was chosen.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'uiwake'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'ui-wake-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-uiwake-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
# The same dump, but "the window is not answering yet" is a VALUE rather than an exception --
# which is what lets a Wait-Until poll it. Waiting for a window to come up is the single most
# common fixed sleep in this suite, and every one of them was a guess.
function DumpOrNull() { try { Dump } catch { $null } }
function ShellDumpOrNull() { try { (& $dodona ui dump --shell) | Out-String | ConvertFrom-Json } catch { $null } }

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
# CACHED, and that is a measured decision, not a micro-optimisation. FindAll on RootElement
# enumerates every top-level window ON THE DESKTOP and then reads .Current.Name on each --
# a cross-process UIA round trip per window. It was called once per section when every wait
# was a fixed sleep; it is now called from inside Wait-Until polls, and with the suites
# running concurrently there are several test windows on the desktop to walk past. The
# element handle stays valid for the life of the window, so it is looked up once and dropped
# when the window closes (Reset-UiWindow, called where the window is reopened).
$script:uiWin = $null
function UiWindow([string]$nameLike = "Dodona*$(Split-Path $root -Leaf)") {
    if ($null -ne $script:uiWin) {
        # Still there? Touching .Current on a dead element throws, which IS the answer.
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
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    # ---- THE WINDOW OVER A SLEEPING WORKSPACE (2026-08-19) -------------------------------
    # THE STATE THE OPERATOR'S MACHINE IS IN EVERY MORNING, and no check had ever visited it.
    # A daemon outlives its window and closing the app stops nothing (CLAUDE.md 3.1) -- so the
    # mirror of that is equally normal: the WINDOW outlives the daemon, after a `stop-all`, a
    # crash, or a reboot with the shortcut relaunched. The store reader is read-only, so the
    # window goes on rendering every lane as `alive` and looks perfectly healthy.
    #
    # It was not healthy. Two of the three write paths started the daemon first (AnswerAsk, and
    # the input box's concierge branch); MainWindow.Send -- which carries EVERY lane click and
    # the one-workspace branch of the input box -- did not. Measured before the fix: typing into
    # a window with one sleeping workspace returned `status: daemon not running`, tiles intact.
    # The first thing a person does, answered with a sentence about our internals.
    #
    # It is in THIS suite because it is a use failure, not a reporting one: every dump was
    # correct throughout (CLAUDE.md 3, "dumps prove the UI reports correctly while the first
    # thing a person tries is a dead end").
    #
    # ITS OWN WINDOW, deliberately. The first draft reused the primary window from 900 lines
    # earlier and asserted it had survived; that made a check about THIS defect fail for
    # unrelated reasons and turned two others VACUOUS, because a window that is already gone
    # reports an empty status and an empty status matches nothing.
    Dodona @('ui', 'close') | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 20000 'any primary window is gone' | Out-Null
    Reset-UiWindow
    # Launching the UI is itself start-on-demand (App.OnStartup calls Ensure), so this is also
    # how the daemon comes back for the section.
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the sleeping-workspace window answers' | Out-Null
    Wait-Daemon $ws.CtlPipe | Out-Null

    # A lane that is NOT already collapsed -- an earlier section collapsed one and left it that
    # way, so `collapse` on that one would assert nothing. Two traps paid for in these lines:
    # collapsedLanes carries OBJECTS with a .lane, not bare ints (`-contains` against the object
    # silently never matches), and Invoke-StoreSql returns EVERY row as ONE newline-joined
    # string, so `| ForEach-Object { [int]$_ }` casts "8`n7`n6" and throws.
    # THIS SECTION MAKES ITS OWN LANE, and that is the fourth draft. The tile check needs a lane
    # that is ALIVE and not already collapsed, and every attempt to find one among the suite's
    # existing lanes failed differently -- each time in a way that said nothing:
    #   - collapsedLanes carries OBJECTS with a .lane, not bare ints, so `-contains` against the
    #     object silently never matches;
    #   - a DEAD lane is not rendered at all -- `ui lane collapse 8` worked, the daemon replied
    #     "collapsed lane 8", and the grid showed nothing, because GAMMA had already exited;
    #   - the chip an earlier section makes is EXPANDED again at its own tidy-up (line ~505), so
    #     looking for one here found nothing, the verb was then called with no lane, and a
    #     missing argument fails on STDERR -- which `Dodona` does not capture, so the check's
    #     detail was an empty string. A blank detail is the worst possible diagnosis;
    #   - and picking the newest still-alive lane passed alone and went RED inside `dev gate`,
    #     because under a full concurrent wave the older lanes had already gone. A check whose
    #     subject depends on what nine hundred lines above it left behind is a check that will
    #     eventually fail for a reason that has nothing to do with what it tests.
    Dodona @('lane-start', '--title', 'SLEEPER', '--child', $fake) | Out-Null
    $sleepLane = [int](@((Invoke-StoreSql $storeDb "SELECT id FROM lanes WHERE title='SLEEPER' ORDER BY id DESC LIMIT 1") `
        -split "`r?`n" | Where-Object { $_.Trim() })[0])
    Wait-Until { @((DumpOrNull).slots | Where-Object { $_.lane -eq $sleepLane }).Count -eq 1 } `
        20000 'the sleeper lane has a tile' | Out-Null
    Check 'the_sleeping_section_has_its_own_live_lane' ($sleepLane -gt 0) "sleepLane=$sleepLane"

    # Graceful, because that is what `stop-all` does to the operator's machine -- not a kill.
    # Through the suite's own helper, NEVER `& $dodona ... 2>$null`: under $ErrorActionPreference
    # 'Stop' a native command's stderr line becomes NativeCommandError and throws OUT of the try
    # (CLAUDE.md 0.2), and a redirect does not save you. The first draft did exactly that and
    # every check below it read MISSING instead of red.
    Dodona @('stop-daemon') | Out-Null
    Wait-Until { Test-DodonaPipeGone $ws.CtlPipe } 20000 'the daemon is down' | Out-Null
    # VACUOUS against HEAD, and knowingly kept -- `dev prove` says PASS because this behaviour was
    # already correct. It is the precondition every check below depends on (a window that died
    # would report an empty status, and an empty status matches nothing, which is how two of them
    # were VACUOUS in an earlier draft), and it is the untested half of CLAUDE.md 3.1: the daemon
    # outliving its window is written down and covered, the window outliving its daemon was
    # neither. Stated here rather than quietly left in, per check-authoring 1.
    Check 'the_window_outlives_its_daemon' ($null -ne (DumpOrNull)) 'the window went with it'

    # Typing. The whole test.
    Dodona @('ui', 'type', 'the daemon is asleep and I typed anyway') | Out-Null
    # WAKING is the claim, so waking is what is waited for and asserted. Delivery of the sentence
    # to an agent is NOT asserted here, and that is deliberate: LaneRuntime.Say throws unless the
    # lane's shim has finished reconnecting to the newly started daemon, so a delivery assertion
    # would be a race dressed as a check -- measured, it timed out at 40 s while the status line
    # already read "-> ALPHA (focus, no classifier warm)", i.e. the daemon was up and had routed
    # the sentence. The reconnect window is real and worth its own work; it is not this defect.
    $woke = Wait-Daemon $ws.CtlPipe 40000
    $sd = DumpOrNull
    Check 'typing_at_a_sleeping_workspace_never_says_daemon_not_running' `
        ($null -ne $sd -and "$($sd.status)" -notmatch 'daemon not running') "status=[$($sd.status)]"
    Check 'typing_wakes_a_sleeping_workspace' ([bool]$woke -and "$($sd.status)" -notmatch '^error') `
        "woke=$woke status=[$($sd.status)]"

    # ---- AND THE TILE. The five lane actions had NO `ui` verb until this commit, so nothing
    # could reach the click handlers at all -- unreachable, not merely untested, which is
    # exactly where the defect above lived. `ui lane` lands in MainWindow.LaneAction, the same
    # method a click lands in (D-L4: only pixels may diverge).
    Dodona @('stop-daemon') | Out-Null
    Wait-Until { Test-DodonaPipeGone $ws.CtlPipe } 20000 'the woken daemon is down again' | Out-Null
    $la = (Dodona @('ui', 'lane', 'collapse', "$sleepLane")) | Out-String
    Check 'the_lane_verb_reaches_the_window_at_all' `
        ($la -notmatch 'unknown ui verb' -and $la -match "collapse $sleepLane") "lane=$sleepLane reply=[$($la.Trim())]"
    Wait-Until { @((DumpOrNull).collapsedLanes | ForEach-Object { [int]$_.lane }) -contains $sleepLane } `
        40000 'the collapse lands in the store and reaches the grid' | Out-Null
    $sd2 = DumpOrNull
    Check 'a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts' `
        ($null -ne $sd2 -and @($sd2.collapsedLanes | ForEach-Object { [int]$_.lane }) -contains $sleepLane) `
        "lane=$sleepLane collapsed=[$(@($sd2.collapsedLanes | ForEach-Object { $_.lane }) -join ',')] status=$($sd2.status)"
    Check 'a_lane_click_never_says_daemon_not_running' `
        ($null -ne $sd2 -and "$($sd2.status)" -notmatch 'daemon not running') "status=[$($sd2.status)]"
    # The daemon these checks woke is a pid no variable holds; the finally stops by pid only.
    Dodona @('stop-daemon') | Out-Null
}
finally {
    # 'Continue' FOR THE WHOLE CLEANUP, and this is not a style choice -- it is the fix for a
    # suite that had stopped reporting. Under 'Stop', ANY native command writing ANY line to
    # stderr raises NativeCommandError and aborts (CLAUDE.md §0.2), and `concierge-stop` says
    # "concierge not running" on stderr whenever the shell's input box did not happen to start
    # one. That threw OUT of this finally, so Assert-NoBuildOutputProcesses never ran, the
    # tally after the try/finally never printed, and tools\dev.ps1 -- which only grepped for
    # ': FAIL' and a tally -- reported the whole suite as `no tally line` and counted it as
    # nothing. 59 checks were computed and thrown away, silently, on 2026-08-19's baseline run.
    # A cleanup block must never be abortable by a diagnostic line.
    $ErrorActionPreference = 'Continue'
    foreach ($proc in $uiProc, $shellUi, $bareUi, $askUi, $routeUi, $daemon, $daemon2, $daemon3, $daemon4) {
        if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    }
    # The swap successor is a NEW pid the variables above never saw — resolve it by the
    # exe path unique to this run's copied build, never by bare process name (CLAUDE.md §4).
    if ($uiCopy) {
        Stop-ProcessesUnder $uiCopy
        Remove-Item $uiCopy -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    # Both workspaces' shims, each resolved from its OWN workspace directory.
    Stop-WorkspaceShims $wsDir
    if ($ws2) { Stop-WorkspaceShims $ws2.Dir }
    if ($ws3) { Stop-WorkspaceShims $ws3.Dir }
    if ($tp4) { Stop-WorkspaceShims $tp4.Dir }
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
Write-Output "---- UI WAKE ACCEPTANCE (a window over a sleeping workspace, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
