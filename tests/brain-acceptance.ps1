# Dispatcher-brain acceptance (§3's middle rung, model-free): the brain reviews BEHIND
# the instant code path and corrects visibly — silent unless it disagrees (operator rule).
# The fake agent plays both worker and brain (DODONA_LANE_ROLE), driven by directives
# embedded in the operator text, so every judgement here is deterministic.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'brain'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'brain-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-brain-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\a.cs" "// a"
Set-Content "$root\.gitignore" ".dodona/"
Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $o = (& $dodona ($a + @('--root', $root))) | Out-String; $o.Trim() }
function Dump() { Dodona @('ui', 'dump') | ConvertFrom-Json }
# "not answering yet" as a VALUE rather than an exception, so a Wait-Until can poll it.
function DumpOrNull() { try { Dump } catch { $null } }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function Rows([string]$sql) {
    $env:DODONA_TEST_SQL = $sql
    $o = (python -c "
import sqlite3, os
db = sqlite3.connect(r'$storeDb')
for r in db.execute(os.environ['DODONA_TEST_SQL']): print('|'.join('' if x is None else str(x) for x in r))
") | Out-String
    Remove-Item env:DODONA_TEST_SQL -ErrorAction SilentlyContinue
    $o
}

$daemon = $null
$uiProc = $null
try {
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    Dodona @("brain-start") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1' } 25000 'the brain lane is alive' | Out-Null
    Check 'brain_is_warm_and_off_grid' ((Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1') ''

    # ---- silent when it agrees: no announcement beyond the standard creation receipt ----
    Dodona @("input", "make the water foam softer") | Out-Null
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='brain_review' AND detail LIKE 'agree=True%'").Trim()) -ge 1 } 25000 'the brain reviews and agrees' | Out-Null
    $agreeRows = Rows "SELECT COUNT(*) FROM events WHERE kind='brain_review' AND detail LIKE 'agree=True%'"
    Check 'brain_reviewed_and_agreed' ([int]($agreeRows.Trim()) -ge 1) $agreeRows
    $noise = Rows "SELECT COUNT(*) FROM pane_events WHERE kind='announcement' AND body LIKE '%dispatcher%'"
    Check 'agreement_is_silent' ([int]($noise.Trim()) -eq 0) $noise

    # ---- disagreement: rename applied + receipt with undo; ticket only suggested ----
    # The brain reviews lane BIRTHS; with a live lane the input would just route to it.
    Dodona @("lane-stop", "2") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role='work' AND state='alive'").Trim() -eq '0' } 25000 'the work lane is stopped' | Out-Null
    Dodona @("input", "brainname:SHORELINE brainticket:FOAM do the shoreline foam work") | Out-Null
    Wait-Until { (Rows "SELECT id, title FROM lanes WHERE role='work' ORDER BY id") -match 'SHORELINE' } 25000 'the brain renames the new lane' | Out-Null
    $lanes = Rows "SELECT id, title FROM lanes WHERE role='work' ORDER BY id"
    Check 'rename_applied' ($lanes -match 'SHORELINE') $lanes
    $renamed = Rows "SELECT COUNT(*) FROM events WHERE kind='brain_renamed'"
    Check 'rename_in_causal_chain' ([int]($renamed.Trim()) -eq 1) ''
    $receipt = Rows "SELECT body, acked FROM pane_events WHERE body LIKE 'renamed to SHORELINE%' LIMIT 1"
    Check 'rename_receipt_carries_undo' ($receipt -match 'undo: dodona lane-rename' -and $receipt -match '\|1') $receipt
    $suggestion = Rows "SELECT body, acked FROM pane_events WHERE body LIKE '%ticket-worthy%' LIMIT 1"
    Check 'ticket_is_suggested_not_created' (($suggestion -match 'ticket-create --title FOAM') -and ($suggestion -match '\|0') -and
        ((Rows "SELECT COUNT(*) FROM tickets").Trim() -eq '0')) $suggestion

    # ---- the operator's undo is real ----
    if ($lanes -match '(\d+)\|SHORELINE') { $laneId = $Matches[1] }
    Dodona @("lane-rename", "$laneId", "FOAMWORK") | Out-Null
    Check 'operator_rename_wins' ((Rows "SELECT title FROM lanes WHERE id=$laneId").Trim() -eq 'FOAMWORK') ''

    # ---- management roles are isolated from project context ----
    $cwds = Rows "SELECT detail FROM events WHERE kind='shim_spawned' ORDER BY id"
    $brainSpawn = ($cwds -split "`r?`n" | Where-Object { $_ -match 'lane3|lane1' }) -join ' '
    Check 'brain_runs_outside_the_project' ((Rows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned' AND detail LIKE '%neutral%'").Trim() -ge '1') $cwds

    # ---- the pulse: a routed message flashes the receiving pane ----
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { $null -ne (DumpOrNull) } 30000 'the UI window answers ui dump' | Out-Null
    Dodona @("say", "$laneId", "say pulse check") | Out-Null
    # The pulse is a TRANSIENT: it must be caught while it is on, so this waits for it to
    # appear rather than sleeping past it -- which is also why it cannot be replaced with a
    # wait for the fade alone.
    Wait-Until {
        $script:pane = @((DumpOrNull).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $laneId })[0]
        $script:pane.pulsing -eq $true
    } 20000 'the receiving pane pulses' | Out-Null
    Check 'pulse_on_arrival' ($pane.pulsing -eq $true) ($pane | ConvertTo-Json -Compress)
    Wait-Until {
        $script:pane = @((DumpOrNull).slots | Where-Object { -not $_.empty } | Where-Object { $_.lane -eq $laneId })[0]
        $script:pane.pulsing -eq $false
    } 20000 'the pulse fades' | Out-Null
    Check 'pulse_fades' ($pane.pulsing -eq $false) ''

    # ---- the escalation ladder end to end (operator's routing tiers + final rung) ----
    Dodona @("router-start", "--child", $fake) | Out-Null
    Dodona @("brain-start", "--hi") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role IN ('router','brain-hi') AND state='alive'").Trim() -eq '2' } 25000 'the classifier and the expensive tier are warm' | Out-Null
    Dodona @("lane-start", "--title", "WATER", "--child", $fake) | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE title='WATER' AND state='alive'").Trim() -eq '1' } 25000 'the WATER lane is alive' | Out-Null
    Dodona @("focus", "$laneId") | Out-Null

    # =====================================================================================
    # LANE GRANULARITY: the four verdicts (docs/WORKSPACES-CONCIERGE.md §5)
    #
    # The operator's rule: a distinct task gets its OWN lane. An existing lane keeps the input
    # only when it clearly continues that thread -- either it is aimed at what the lane is doing
    # now, or it is a small tweak to what it just finished.
    #
    # The error asymmetry is what every check below is really guarding. A wrong new lane costs
    # one `lane-stop`. A wrong continuation cannot be undone at all: the agent has been told, may
    # already be acting, and its context is spoiled. So routing WAITS for a verdict now instead
    # of delivering optimistically -- correcting is precisely what is impossible.
    # =====================================================================================

    # generic → the focused lane, never second-guessed.
    Dodona @("input", "routekind:generic say dont do that") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$laneId AND kind='user_input' AND body LIKE '%dont do that%'").Trim() -eq '1' } 25000 'the generic reaches the focused lane' | Out-Null
    Check 'generic_goes_to_the_focused_lane' `
        ((Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$laneId AND kind='user_input' AND body LIKE '%dont do that%'").Trim() -eq '1') `
        (Rows "SELECT tier, delivered_lane FROM routing_decisions ORDER BY id DESC LIMIT 1")

    # An unmistakable generic is decided in CODE — instant, and it never reaches a model. That
    # is what keeps "stop" fast even though everything else now waits for a verdict.
    Dodona @("input", "stop") | Out-Null
    Wait-Until { (Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 1") -match 'generic\|explicit' } 25000 'the unmistakable generic is decided in code' | Out-Null
    Check 'obvious_generic_needs_no_model' `
        ((Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 1") -match 'generic\|explicit') `
        (Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 1")

    # addendum → the lane it names, with the reason recorded. Two legitimate reasons, both from
    # the operator: `direct` (talking to work in progress) and `tweak` (a small correction to
    # work just finished).
    $waterLane = (Rows "SELECT id FROM lanes WHERE title='WATER'").Trim()
    Dodona @("input", "routekind:addendum routetarget:WATER routereason:direct say make the water red") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$waterLane AND kind='user_input' AND body LIKE '%water red%'").Trim() -eq '1' } 25000 'the addendum reaches the named lane' | Out-Null
    Check 'addendum_goes_to_the_named_lane' `
        ((Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$waterLane AND kind='user_input' AND body LIKE '%water red%'").Trim() -eq '1') ''
    Check 'addendum_records_its_reason' ((Rows "SELECT detail FROM events WHERE kind='routed_addendum' ORDER BY id DESC LIMIT 1") -match 'direct') ''

    Dodona @("input", "routekind:addendum routetarget:WATER routereason:tweak say actually make it darker") | Out-Null
    Wait-Until { (Rows "SELECT detail FROM events WHERE kind='routed_addendum' ORDER BY id DESC LIMIT 1") -match 'tweak' } 25000 'the tweak is recorded as an addendum' | Out-Null
    Check 'tweak_is_an_addendum_too' ((Rows "SELECT detail FROM events WHERE kind='routed_addendum' ORDER BY id DESC LIMIT 1") -match 'tweak') ''

    # new-task → A FRESH LANE, spawned and delivered. The verdict that did not exist before:
    # while any lane was alive, every input used to be a continuation of something.
    $before = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()
    Dodona @("input", "routekind:new-task say build the configuration dialog") | Out-Null
    Wait-Until { ([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()) -eq $before + 1 } 30000 'the new task spawns its own lane' | Out-Null
    $after = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()
    Check 'new_task_spawns_its_own_lane' ($after -eq $before + 1) "before=$before after=$after"
    $newLane = (Rows "SELECT id FROM lanes WHERE role='work' ORDER BY id DESC LIMIT 1").Trim()
    Check 'new_task_lane_gets_the_message' `
        ((Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$newLane AND kind='user_input' AND body LIKE '%configuration dialog%'").Trim() -eq '1') ''
    # The lane is named in code from the longest substantial word. The directive word must
    # not BE the longest one, or the lane gets named after the test harness (it did: ROUTEKIND).
    Check 'new_task_is_named_from_the_text' ((Rows "SELECT title FROM lanes WHERE id=$newLane").Trim() -eq 'CONFIGURATION') `
        (Rows "SELECT title FROM lanes WHERE id=$newLane")
    Check 'new_task_receipt_carries_its_undo' `
        ((Rows "SELECT body FROM pane_events WHERE lane_id=$newLane AND kind='announcement' LIMIT 1") -match "lane-stop $newLane") ''
    Check 'new_task_in_the_causal_chain' ((Rows "SELECT COUNT(*) FROM events WHERE kind='routed_new_task'").Trim() -eq '1') ''
    # ...and it did NOT leak into the lane the operator happened to be looking at. This is the
    # unrecoverable error, so it gets its own check.
    Check 'new_task_never_touched_the_focused_lane' `
        ((Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$laneId AND body LIKE '%configuration dialog%'").Trim() -eq '0') ''

    # unclear + brain-hi sure → the expensive tier decides, and can itself say new-task.
    $before2 = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()
    Dodona @("input", "routekind:unclear hikind:new-task say overhaul the export pipeline") | Out-Null
    Wait-Until {
        (Rows "SELECT COUNT(*) FROM events WHERE kind='classified_escalated'").Trim() -ne '0' -and
        ([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()) -eq $before2 + 1
    } 30000 'the unclear input escalates and the expensive tier spawns a lane' | Out-Null
    Check 'unclear_escalates_to_the_expensive_tier' `
        ((Rows "SELECT COUNT(*) FROM events WHERE kind='classified_escalated'").Trim() -ne '0') ''
    Check 'escalated_new_task_spawns_a_lane' `
        (([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()) -eq $before2 + 1) ''
    Check 'escalated_decision_is_recorded' `
        ((Rows "SELECT confidence FROM routing_decisions ORDER BY id DESC LIMIT 1").Trim() -eq 'escalated') ''

    # ---- DOUBLE UNCERTAINTY: hold the sentence and ask. -----------------------------------
    # This REVERSES the old behaviour, deliberately. §4's ambiguity default was "leave it with
    # the focused lane", written when delivery had already happened and the only question was
    # whether to retarget. Now nothing has been said yet, and delivering to the wrong lane is
    # the one mistake that cannot be taken back -- so the honest move is to hold and ask.
    # Undoing a wait costs nothing; undoing a polluted context costs the lane.
    # WORK lanes only. Asking the router or brain-hi writes a `user_input` row on THEIR lane
    # (that is how AskAsync talks to a warm session), so an unscoped count rises by two even
    # when nothing was delivered to any actual lane.
    $workInputs = "SELECT COUNT(*) FROM pane_events p JOIN lanes l ON l.id=p.lane_id WHERE p.kind='user_input' AND l.role='work'"
    $inputsBefore = [int](Rows $workInputs).Trim()
    $lanesBefore = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()
    Dodona @("input", "routekind:unclear say something entirely cryptic") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM events WHERE kind='routing_clarification'").Trim() -eq '1' } 30000 'double uncertainty holds the sentence and asks' | Out-Null
    $ask = Rows "SELECT body, acked FROM pane_events WHERE body LIKE '%new work or continues%' LIMIT 1"
    Check 'double_uncertainty_asks_the_operator' (($ask -match 'NOT delivered') -and ($ask -match '\|0')) $ask
    Check 'clarification_in_causal_chain' ((Rows "SELECT COUNT(*) FROM events WHERE kind='routing_clarification'").Trim() -eq '1') ''
    # THE POINT: nothing was delivered anywhere, and no lane was invented either.
    Check 'held_input_is_delivered_nowhere' `
        (([int](Rows $workInputs).Trim()) -eq $inputsBefore) `
        (Rows "SELECT p.lane_id, l.role, p.body FROM pane_events p JOIN lanes l ON l.id=p.lane_id WHERE p.body LIKE '%entirely cryptic%'")
    Check 'held_input_invents_no_lane' `
        (([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()) -eq $lanesBefore) ''
    Check 'held_input_is_recorded_as_asked' `
        ((Rows "SELECT tier FROM routing_decisions ORDER BY id DESC LIMIT 1").Trim() -eq 'ask') ''

    Dodona @("ui", "close") | Out-Null
    # ---- a daemon restart must ADOPT the brain, not spawn a second one ------------------
    # The bug this guards: `_brainLo` resets to -1 in every new process, and reconcile
    # re-adopted compressor lanes but never the brain -- so the startup warm-up concluded no
    # brain existed and spawned a fresh lane. Every. Single. Start. Measured on the operator's
    # own instance: 14 BRAIN lanes (lane6..lane19), one per daemon start across a morning of
    # auto-publish swaps, each an idle `claude -p` nobody could reach. No quota burned
    # (LANE-LIFECYCLE 2: turns cost quota, existing does not) but it grows without bound.
    $brainBefore = (Rows "SELECT id FROM lanes WHERE role='brain' AND state='alive' ORDER BY id").Trim()

    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down' | Out-Null
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon2.out" -RedirectStandardError "$out\daemon2.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # Being up is not being RECONCILED. The checks below are about adoption, so wait for the
    # reconcile_done row that records it -- the very thing the last check reads.
    Wait-Until { (Rows "SELECT detail FROM events WHERE kind='reconcile_done' ORDER BY id DESC LIMIT 1") -match 'brain=\d+' } 25000 'the restarted daemon has reconciled' | Out-Null

    $brainAfter = (Rows "SELECT id FROM lanes WHERE role='brain' AND state='alive' ORDER BY id").Trim()
    Check 'restart_adopts_the_brain_it_already_had' ($brainAfter -eq $brainBefore) "before=[$brainBefore] after=[$brainAfter]"
    Check 'restart_does_not_leak_a_second_brain' `
        ((Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1') `
        (Rows "SELECT id, title, role, state FROM lanes WHERE role LIKE 'brain%'")
    # ...and asking for one again reuses it rather than making another.
    Dodona @("brain-start") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1' } 25000 'brain-start after a restart reuses the brain' | Out-Null
    Check 'brain_start_after_restart_reuses_it' `
        ((Rows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '1') `
        (Rows "SELECT id, role, state FROM lanes WHERE role LIKE 'brain%'")
    # The adoption is in the causal chain, not just in memory.
    Check 'reconcile_records_which_brain_it_adopted' `
        ((Rows "SELECT detail FROM events WHERE kind='reconcile_done' ORDER BY id DESC LIMIT 1") -match 'brain=\d+') `
        (Rows "SELECT detail FROM events WHERE kind='reconcile_done' ORDER BY id DESC LIMIT 1")

    # ---- THE WIRING: the role the daemon CREATES must be the role routing USES -----------
    # This is the check that was missing, and its absence cost two days of silently broken
    # routing. RouteInput looked the classifier up by `role='router'`; nothing in the daemon
    # ever created one. The startup warm-up and `brain-start` both make `brain`, and the only
    # producer of `router` was the manual command -- whose only caller in the entire tree was
    # THIS SUITE, five lines above. So the ladder was proven on a wiring the real daemon never
    # took, and every sentence the operator typed fell to `no-classifier` and went to whatever
    # lane happened to be focused. Measured on their store: 14 routed inputs, all
    # `tier=focus confidence=no-classifier`, ZERO `classified` events, ZERO router lanes.
    #
    # So this section deliberately runs the daemon the way the OPERATOR runs it -- autostart
    # ON -- with no classifier present, and demands that one appear and actually decide.
    $routerLane = (Rows "SELECT id FROM lanes WHERE role='router' AND state='alive'").Trim()
    Dodona @("lane-stop", $routerLane) | Out-Null
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down' | Out-Null
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue    # as the operator has it
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon3.out" -RedirectStandardError "$out\daemon3.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    Wait-Until { ([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim()) -eq 1 } 30000 'autostart creates a classifier' | Out-Null

    Check 'autostart_creates_a_classifier' `
        (([int](Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim()) -eq 1) `
        (Rows "SELECT id, title, role, state FROM lanes WHERE role='router'")
    Check 'autostart_records_the_classifier' `
        ((Rows "SELECT detail FROM events WHERE kind='router_started' ORDER BY id DESC LIMIT 1").Trim() -eq 'classifier warm') `
        (Rows "SELECT kind, detail FROM events WHERE kind LIKE 'router%' ORDER BY id DESC LIMIT 3")

    # The decisive one: a real typed sentence must reach the classifier the daemon made for
    # itself. A `classified` event can only be written from inside ClassifyAsync, so this
    # fails the instant the create-side role and the use-side role diverge again.
    $classifiedBefore = [int](Rows "SELECT COUNT(*) FROM events WHERE kind='classified'").Trim()
    Dodona @("input", "routekind:generic routeconf:high say carry on then") | Out-Null
    Wait-Until { ([int](Rows "SELECT COUNT(*) FROM events WHERE kind='classified'").Trim()) -gt $classifiedBefore } 30000 'the typed sentence reaches the classifier' | Out-Null
    Check 'typed_input_reaches_the_classifier_autostart_made' `
        (([int](Rows "SELECT COUNT(*) FROM events WHERE kind='classified'").Trim()) -gt $classifiedBefore) `
        (Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 3")
    Check 'routing_did_not_fall_back_to_focus' `
        ((Rows "SELECT confidence FROM routing_decisions ORDER BY id DESC LIMIT 1").Trim() -ne 'no-classifier') `
        (Rows "SELECT tier, confidence, input FROM routing_decisions ORDER BY id DESC LIMIT 1")

    # ...and the classifier obeys the same no-leak rule the brain does.
    $routerNow = (Rows "SELECT id FROM lanes WHERE role='router' AND state='alive' ORDER BY id").Trim()
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down' | Out-Null
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon4.out" -RedirectStandardError "$out\daemon4.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    Wait-Until { (Rows "SELECT id FROM lanes WHERE role='router' AND state='alive' ORDER BY id").Trim() -eq $routerNow } 30000 'the restart adopts the classifier it already had' | Out-Null
    Check 'restart_adopts_the_classifier_it_already_had' `
        ((Rows "SELECT id FROM lanes WHERE role='router' AND state='alive' ORDER BY id").Trim() -eq $routerNow) `
        (Rows "SELECT id, role, state FROM lanes WHERE role='router'")
    Check 'restart_does_not_leak_a_second_classifier' `
        ((Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim() -eq '1') `
        (Rows "SELECT id, title, role, state FROM lanes WHERE role='router'")
    $env:DODONA_NO_AUTOSTART = "1"

    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    # Only this test's processes, resolved from its own shim-info files (CLAUDE.md §4).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- BRAIN ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
