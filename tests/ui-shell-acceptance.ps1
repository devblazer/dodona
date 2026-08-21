# UI SHELL acceptance: one window over N workspaces (docs/WORKSPACES-CONCIERGE.md §4, §6) --
# boot-to-zero, the bare launch that IS the shell, a second awake workspace becoming a band,
# clicking a band to hand it the grid, the feed as a union labelled per workspace, and a live
# shell window hot-swapping on `ui update`.
#
# Driven the way a person drives it -- open the shell, look at what is on screen, click a band
# -- because that is the whole reason these suites exist: dumps and screenshots proved the UI
# could REPORT correctly while the first thing a person tried was a dead end.
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
$dodonaHome = Use-IsolatedDodonaHome 'uishell'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'ui-shell-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-uishell-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
# The shell section runs a SECOND workspace daemon and two more UIs (the bare launch and the
# shell itself); all are tracked so the scoped cleanup in `finally` can stop them by pid
# (CLAUDE.md §4). $uiCopy is the copied build the hot-swap check hands off to.
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

    # ---- PRIMED: this workspace must already have SAID something --------------------------
    # THE ONE THING THIS SUITE NEEDS THAT THE MONOLITH GOT FOR FREE. `merged_feed_spans_both_
    # workspaces` below asserts the union carries a labelled row from EACH workspace, and the
    # feed is `pane_events WHERE kind='announcement'` (DodonaUi/StoreReader.Feed) -- so a
    # workspace nothing has ever spoken in contributes nothing to label, and the check would
    # go red against a product that is working correctly. In the monolith this workspace had
    # been typed at for four hundred lines before the shell section ever reached it.
    #
    # TYPED INPUT, NOT lane-start, for exactly the reason the second workspace below uses it:
    # input makes a lane AND announces; lane-start announces nothing, which is what once left
    # the union with only one workspace's rows in it.
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon-prime.out" -RedirectStandardError "$out\daemon-prime.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # TRIMMED, for the reason every other store helper in these suites records: Invoke-StoreSql
    # ends in `| Out-String`, so every answer arrives with a trailing newline and `-eq '0'` on
    # one is false. Parenthesised at every call site too -- a plain `function Q([string]$sql)`
    # silently swallows extra arguments into $args (CLAUDE.md §0.2).
    function Q([string]$sql) { "$(Invoke-StoreSql $storeDb $sql)".Trim() }
    Dodona @('input', 'say sort out the FIRST thing') | Out-Null
    Wait-Until { [int](Q "SELECT COUNT(*) FROM pane_events WHERE kind='announcement'") -ge 1 } `
        30000 'the first workspace has announced something for the union to label' | Out-Null
    # Stopped, because the monolith stopped every lane at its own tidy-up before this section
    # began, and boot-to-zero below should mean what it says over a workspace with nothing
    # running in it. Split on newlines first: Invoke-StoreSql returns EVERY row as ONE
    # newline-joined string.
    foreach ($l in @((Q "SELECT id FROM lanes WHERE state='alive'") -split "`r?`n" | Where-Object { $_.Trim() })) {
        Dodona @('lane-stop', $l.Trim()) | Out-Null
    }
    Wait-Until { [int](Q "SELECT COUNT(*) FROM lanes WHERE state='alive'") -eq 0 } 20000 'the primed lane is stopped' | Out-Null


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
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the first daemon is down' | Out-Null

    # ---- a BARE launch (no --root, no --workspace, no --shell) IS the shell ----------------
    # The folder picker that used to answer a bare launch was removed 2026-08-18 on the
    # operator's direction (WORKSPACES-CONCIERGE.md §6.1): a workspace is named, not
    # located, so there is no folder question to ask on the way in. If this check fails,
    # a bare launch opened something that does not answer the shell pipe — which for a
    # person means the front door regressed to a dialog.
    $bareUi = Start-Process $ui -ArgumentList "--test-window" -PassThru
    Wait-Until { $null -ne ($script:bz = ShellDumpOrNull) } 30000 'the bare launch answers the SHELL pipe' | Out-Null
    Check 'bare_launch_is_the_shell_booted_to_zero' ($null -ne $bz -and $bz.bootToZero -eq $true) `
        ($(if ($null -eq $bz) { 'shell pipe did not answer' } else { $bz | ConvertTo-Json -Compress -Depth 3 }))
    # ---- P4.4: THE ASK IS UNREACHABLE FROM A BARE LAUNCH -----------------------------------
    # The check above exists because the FOLDER PICKER used to answer a bare launch: a dialog in
    # the front door, asking where you wanted to work, contradicting the entire workspace design
    # (WORKSPACES-CONCIERGE.md §6.1). Phase 4 adds an overlay that asks things, so it has to be
    # held to the same line: a question appears ONLY in response to something the operator typed.
    # Nothing has been typed at this point and no question row exists anywhere, so a bare launch
    # must open a window that asks nothing. If this goes red, the front door has regressed to a
    # dialog by a new route -- one Dodona itself opened rather than one it inherited.
    Check 'a_bare_launch_is_never_asked_anything' ($null -ne $bz -and $null -eq $bz.ask) `
        ($bz.ask | ConvertTo-Json -Compress)
    (& $dodona ui close --shell) | Out-Null
    Wait-Until { $null -eq (ShellDumpOrNull) } 20000 'the bare shell window is gone' | Out-Null
    if ($bareUi -and -not $bareUi.HasExited) { try { Stop-Process -Id $bareUi.Id -Force } catch { } }

    $shellUi = Start-Process $ui -ArgumentList "--shell", "--test-window" -PassThru
    function ShellDump() { (& $dodona ui dump --shell) | Out-String | ConvertFrom-Json }
    Wait-Until { $null -ne ($script:z = ShellDumpOrNull) } 30000 'the shell window answers' | Out-Null
    Check 'shell_boots_to_zero_with_nothing_awake' ($z.bootToZero -eq $true) ($z | ConvertTo-Json -Compress -Depth 3)
    Check 'boot_to_zero_shows_no_bands' ((@($z.bands).Count) -eq 0) ''
    Check 'boot_to_zero_still_has_an_input_box' ($z.window.h -gt 0 -and $z.workspaceName -eq '') $z.workspaceName

    # ---- a workspace waking up appears, without the operator doing anything ---------------
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon-back.out" -RedirectStandardError "$out\daemon-back.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # The shell noticing is a separate event from the daemon being up -- wait for the SHELL's
    # view, which is what the check is about.
    Wait-Until { ($script:z = ShellDumpOrNull) -and $script:z.workspaceName -eq $ws.Name } 25000 'the waking workspace takes the grid' | Out-Null
    Check 'a_waking_workspace_takes_the_grid' ($z.bootToZero -eq $false -and $z.workspaceName -eq $ws.Name) `
        "bootToZero=$($z.bootToZero) ws=$($z.workspaceName)"

    # ---- a SECOND awake workspace becomes a band (§6, shape B) ---------------------------
    $root2 = Join-Path (Use-SuiteTemp) ("dodona-uiuse2-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$root2\src" | Out-Null
    Set-Content "$root2\src\other.cs" "// other"
    Set-Content "$root2\dodona.json" (@{ main = 'main'; agent = $fake } | ConvertTo-Json)
    git -C $root2 init -b main -q
    git -C $root2 add -A
    git -C $root2 -c user.email=t@t -c user.name=t commit -q -m init
    $ws2 = Get-WorkspacePaths $dodona $root2
    $daemon2 = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws2.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon2.out" -RedirectStandardError "$out\daemon2.err"
    Wait-Daemon $ws2.CtlPipe | Out-Null
    # Typed input, not lane-start: it makes a lane (so the band has a chip) AND announces
    # (so the merged feed has a row from this workspace to label). lane-start announces
    # nothing, which left the union with only one workspace's rows in it.
    (& $dodona input "say sort out the OTHER thing" --workspace $ws2.Id) | Out-Null
    Wait-Until {
        $script:z = ShellDumpOrNull
        $b = @($script:z.bands) | Where-Object { $_.name -eq $ws2.Name }
        $b -and (@($b.lanes).title) -contains 'OTHER'
    } 30000 'the second workspace appears as a band carrying its lane chip' | Out-Null
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
    Wait-Until { ($script:z = ShellDumpOrNull) -and $script:z.workspaceName -eq $ws2.Name } 20000 'the band click swaps the grid' | Out-Null
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
    $uiCopy = Join-Path (Use-SuiteTemp) ("dodona-uiswap-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Copy-Item (Split-Path $ui) $uiCopy -Recurse
    $upd = (& $dodona ui update "$uiCopy\DodonaUi.exe" --shell) | Out-String
    Check 'shell_ui_update_hands_off' ($upd -match 'updated:') $upd.Trim()
    # Found by the PATH this run owns, never by the name DodonaUi.exe (CLAUDE.md §4, and
    # P4.2). The old query enumerated every DodonaUi.exe on the machine -- the operator's own
    # window included -- and narrowed afterwards; this can only ever see the copied build,
    # which is the only thing the check is about. It also un-hand-rolls the deadline loop:
    # [Environment]::TickCount wraps every 24.9 days and the arithmetic above overflows with
    # it, so the loop could run for approximately ever. Wait-Until uses a Stopwatch.
    $successor = $null
    $z = $null
    Wait-Until {
        if ($null -eq $script:successor) { $script:successor = @(Get-ProcessesUnder $uiCopy)[0] }
        $script:z = ShellDumpOrNull
        $script:successor -and $script:z
    } 20000 'the successor UI is up from the copied build and answers the shell pipe' | Out-Null
    Check 'shell_successor_is_the_new_binary' ($null -ne $successor) "no DodonaUi.exe running from $uiCopy" 
    Check 'shell_survives_the_swap' ($null -ne $z -and (@($ws.Name, $ws2.Name) -contains $z.workspaceName)) `
        ($(if ($null -eq $z) { 'shell pipe did not answer after the swap' } else { $z.workspaceName }))

    # --shell, not the Dodona helper: that one appends --root and would address the
    # single-workspace window's pipe, which is already closed.
    (& $dodona ui screenshot --out "$out\shell-bands.png" --shell) | Out-Null
    (& $dodona ui close --shell) | Out-Null
    Wait-Until { $null -eq (ShellDumpOrNull) } 20000 'the shell window is gone' | Out-Null
    Dodona @("stop-daemon") | Out-Null
    (& $dodona stop-daemon --workspace $ws2.Id) | Out-Null
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
Write-Output "---- UI SHELL ACCEPTANCE (one window over N workspaces, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
