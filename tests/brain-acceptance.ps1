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

$root = Join-Path (Use-SuiteTemp) ("dodona-brain-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
# Delegates to the shared helper, which THROWS on a sqlite error instead of returning nothing
# (tests/_workspace.ps1 Invoke-StoreSql has the incident -- a check in THIS suite queried `lane`
# instead of `lane_id` and passed against every build because of it).
function Rows([string]$sql) { Invoke-StoreSql $storeDb $sql }
# The same query, but a sqlite error becomes the ANSWER instead of an exception. For the checks
# that read `lanes.project`, which does not exist before schema 10: `Invoke-StoreSql` throws
# (correctly -- a query naming a missing column used to return empty, parse as 0, and pass), and a
# throw here would abort the suite inside its own try, so `dev prove` would report every later
# check as MISSING rather than as a verdict. A red saying `no such column: project` is the honest
# answer against an older build; a suite that never printed a tally is not an answer at all.
function RowsTry([string]$sql) { try { Rows $sql } catch { "<sql error: $($_.Exception.Message)>" } }

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
    # PER PROJECT, NOT PER WORKSPACE (LOCATIONS-PLAN P5.6). This asserted COUNT(*) = 1 over the
    # whole store, which is a workspace-wide statement about a thing that is now per project: with
    # two projects it would go red for entirely correct behaviour, or -- worse -- be "fixed" by
    # relaxing it to >= 1, at which point it asserts nothing about duplication at all. This
    # workspace has ONE project, so the value it checks is unchanged; what changed is the QUESTION,
    # which is now "one brain for THIS project" and stays meaningful with N.
    $oneBrain = "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive' AND project='$root'"
    Wait-Until { (RowsTry $oneBrain).Trim() -eq '1' } 25000 'the brain lane for this project is alive' | Out-Null
    Check 'brain_is_warm_and_off_grid' ((RowsTry $oneBrain).Trim() -eq '1') `
        (RowsTry "SELECT id, role, state, project FROM lanes WHERE role LIKE 'brain%'")

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
    #
    # `brains=`, NOT `brain=` (P5.6). This pattern was `brain=\d+`, and with a brain per project
    # the detail would have become `brain=3,7` -- which that pattern STILL MATCHES, while asserting
    # nothing about either brain. A check that degrades into a green proving nothing is the exact
    # failure this plan keeps hitting, so the event key was renamed to make the old pattern
    # impossible to match: it goes red and must be rewritten rather than quietly passing.
    $reconcileDetail = "SELECT detail FROM events WHERE kind='reconcile_done' ORDER BY id DESC LIMIT 1"
    $thisLeaf = [regex]::Escape((Split-Path -Leaf $root))
    Wait-Until { (Rows $reconcileDetail) -match "brains=\[$thisLeaf=\d+" } 25000 'the restarted daemon has reconciled' | Out-Null

    $brainAfter = (Rows "SELECT id FROM lanes WHERE role='brain' AND state='alive' ORDER BY id").Trim()
    Check 'restart_adopts_the_brain_it_already_had' ($brainAfter -eq $brainBefore) "before=[$brainBefore] after=[$brainAfter]"
    Check 'restart_does_not_leak_a_second_brain' `
        ((RowsTry $oneBrain).Trim() -eq '1') `
        (RowsTry "SELECT id, title, role, state, project FROM lanes WHERE role LIKE 'brain%'")
    # ...and asking for one again reuses it rather than making another.
    Dodona @("brain-start") | Out-Null
    Wait-Until { (RowsTry $oneBrain).Trim() -eq '1' } 25000 'brain-start after a restart reuses the brain' | Out-Null
    Check 'brain_start_after_restart_reuses_it' `
        ((RowsTry $oneBrain).Trim() -eq '1') `
        (RowsTry "SELECT id, role, state, project FROM lanes WHERE role LIKE 'brain%'")
    # The adoption is in the causal chain, not just in memory -- and it names WHICH PROJECT'S brain
    # it adopted, which is the fact `brain=\d+` could not carry and `brain=3,7` would have hidden.
    Check 'reconcile_records_which_brain_it_adopted' `
        ((Rows $reconcileDetail) -match "brains=\[$thisLeaf=$brainAfter\b") `
        (Rows $reconcileDetail)

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

    # =====================================================================================
    # R5 -- THE MANAGER READS THE WORK, MAY SEND IT BACK, AND CANNOT APPROVE
    # (docs/REVIEW-AND-MERGE-PLAN.md D-R9 / D-R10 / D-R12)
    #
    # This is the empty chair R3 left. R3 retired the file reservations on the operator's own
    # reasoning -- "if that is problematic in some way, it's the manager's job to say something
    # about it" -- and that manager did not exist: `claim_overlap` and `branch_touched` were
    # recorded and NOTHING read either. R4 assembled the record; this reads it and can send the
    # work back.
    #
    # AUTOSTART STAYS CLEARED FOR THIS WHOLE SECTION, and that is what makes it the operator's
    # own path rather than a fixture: the review is the daemon starting a judgement agent on its
    # own initiative, so it honours DODONA_NO_AUTOSTART exactly as the warm-up, the drift watcher
    # and the classifier's on-demand ensure do (`m1` asserts the other side of that guard). It is
    # still free -- $root's dodona.json points `agent` at the fake, so the MANAGER is the fake
    # agent, answering `mgrverdict:` directives that ride in on the work agent's own report. That
    # is the only channel that could carry them: the completion record quotes the report back
    # verbatim, which is precisely the thing D-R8 says the manager had never once been shown.
    $tk = Dodona @("ticket-create", "--title", "FOAMSHIP", "--claim", "subtree:src")
    $mtid = if ($tk -match 'ticket (\d+) branch') { $Matches[1] } else { 0 }
    $mwt = "$root\.dodona\wt\t$mtid"
    $ml = Dodona @("ticket-agent", "$mtid", "--child", $fake)
    $mlane = if ($ml -match 'lane (\d+)') { $Matches[1] } else { 0 }
    Wait-Until { (Rows "SELECT state FROM lanes WHERE id=$mlane").Trim() -eq 'alive' } 25000 'the ticket agent is alive' | Out-Null

    # ONE TURN OF THE LOOP. The worktree has to MOVE or D-R13's digest gate lets no record
    # through and there is nothing for the manager to read -- which is also the property that
    # stops a send-back from reviewing itself: `Say` starts a turn, that turn changes no files,
    # so it produces no record and therefore no second review. The loop terminates on a fact
    # rather than on a model choosing to stop. Two arguments, two parameters, called
    # positionally: a plain PowerShell function SILENTLY SWALLOWS extras into $args (CLAUDE.md
    # 0.2), so a third would vanish without an error anywhere.
    function Turn([string]$directives, [string]$file) {
        Set-Content "$mwt\src\$file" "// $file"
        git -C $mwt add -A | Out-Null
        git -C $mwt -c user.email=t@t -c user.name=t commit -q -m $file | Out-Null
        Dodona @("say", "$mlane", "say $directives finished a pass on $file") | Out-Null
    }

    # ---- round 1: the record reaches the manager, and the send-back reaches the lane ----
    Turn 'mgrverdict:send-back mgrmsg:SCHEMA-UNMENTIONED' 'b.cs'
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_review'").Trim()) -ge 1 } 40000 'the first manager review' | Out-Null
    $rev1 = Rows "SELECT detail FROM events WHERE kind='manager_review' ORDER BY id DESC LIMIT 1"
    # The fixture's own state rides in this check's DETAIL rather than being a check of its
    # own: `dev prove` called an assertion on it VACUOUS, correctly -- ticket-create and its
    # worktree already work at HEAD, so a check there has no teeth and only implies it has
    # some. R4 deleted one on the same verdict rather than keep it. What is left is the R5
    # claim, with everything a red would need to diagnose it.
    Check 'a_completed_ticket_turn_reaches_the_manager' `
        ($rev1 -match "ticket $mtid " -and $rev1 -match '"verdict":"send-back"' -and
         $rev1 -match '"tier":"lo"' -and $rev1 -match '"round":1') `
        "review=[$rev1] ticket=[$tk] lane=[$ml] wt=$mwt"
    # A send-back is INPUT, on the same path a typed sentence takes (D-R9), so the agent keeps
    # its warm context and simply carries on. It therefore lands in the pane as `user_input`,
    # where the operator sees their own sentences -- the `[manager review ...]` prefix is what
    # tells the two apart, and there is deliberately no announcement, because a machine handling
    # its own round is not a person being needed.
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim()) -ge 1 } 25000 'the first send-back' | Out-Null
    $sb1 = Rows "SELECT body FROM pane_events WHERE lane_id=$mlane AND kind='user_input' AND body LIKE '%manager review%' ORDER BY id DESC LIMIT 1"
    Check 'a_send_back_reaches_the_lane_as_input' `
        ($sb1 -match 'manager review, round 1 of 3' -and $sb1 -match 'SCHEMA-UNMENTIONED' -and
         (Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim() -eq '1') `
        "pane=[$sb1] sent_back=$((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim())"

    # ---- R6 / D-R11: THE WRITE-UP IS THE POINT, so it reaches the OPERATOR ----------------
    #
    # `manager_review` carries a `note` written for the person deciding whether to merge, and
    # until R6 that note went into the store and was read by NOTHING. The approval ask is where
    # it lands: one `questions` row per ticket, refreshed in place as the review comes back.
    #
    # THIS SUITE OWNS THE WITH-NOTE HALF, and `ui-use` owns the at-a-live-window half. The split
    # is a budget decision, recorded as D-R19: a real review needs autostart cleared and a
    # manager agent, which this section already stands up, while `ui-use` is a `SoloSuites`
    # member whose every second lands on the gate's wall clock directly (I7). So: the note
    # reaches the row here; the row reaches the screen and the yes grants the token there.
    #
    # The token in the assertion is the ROUND'S OWN (`mgrmsg:` rides into the fake's `note`), so
    # this cannot pass on some other review's write-up.
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM questions WHERE kind='land' AND state='open'").Trim()) -ge 1 } `
        25000 'the approval ask' | Out-Null
    $lq = (RowsTry "SELECT id FROM questions WHERE kind='land' AND state='open' ORDER BY id DESC LIMIT 1").Trim()
    # A row id or nothing usable -- never an empty string spliced into the SQL below, which would
    # make `WHERE id=` a sqlite syntax error, throw inside this suite's own try, and report every
    # later check as MISSING instead of as a verdict (the shape `dev prove` cannot read).
    if ($lq -notmatch '^\d+$') { $lq = '-1' }
    Wait-Until { (RowsTry "SELECT input FROM questions WHERE id=$lq") -match 'SCHEMA-UNMENTIONED' } `
        25000 "the manager's note in the ask" | Out-Null
    $lqText = "$(RowsTry "SELECT input FROM questions WHERE id=$lq")"
    Check 'the_managers_write_up_reaches_the_operators_approval_ask' `
        ((RowsTry "SELECT subject FROM questions WHERE id=$lq").Trim() -eq "$mtid" -and
         $lqText -match 'SCHEMA-UNMENTIONED' -and $lqText -match 'sent this back, round 1 of 3' -and
         $lqText -match 'Approve the merge') `
        "q=$lq text=[$($lqText -replace '\s+', ' ')] rows=$((RowsTry "SELECT id, kind, state, subject FROM questions") -replace '\s+', ' ')"

    # ---- D-R10: IT MAY BLOCK, IT MAY NOT BLESS -- the load-bearing rule of the whole plan ----
    #
    # The fake manager is asked for `approve` ON PURPOSE, a verdict the schema does not offer,
    # because the failure being pinned is a future edit that wires a review to `TicketApprove`.
    # Rejection is free and reversible -- worst case it costs a round. Approval advances a ref
    # that has no undo, so a model as the sole gate on it is WORK-ISOLATION-PLAN 2's *a prompt
    # providing safety*, whatever the model is called. Three things must all still hold: the
    # ticket is unapproved, no `ticket_approved` event exists, and `token-request` still refuses.
    # And it burns no round -- `approve` is not `send-back`, so it is read as no objection.
    Turn 'mgrverdict:approve' 'c.cs'
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_review'").Trim()) -ge 2 } 40000 'the review of the approve round' | Out-Null
    $tokAfter = Dodona @("token-request", "$mtid")
    Check 'a_manager_approval_grants_nothing' `
        ((Rows "SELECT approved FROM tickets WHERE id=$mtid").Trim() -eq '0' -and
         [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='ticket_approved'").Trim()) -eq 0 -and
         $tokAfter -match 'not approved' -and
         (Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim() -eq '1') `
        "token-request=[$tokAfter] approved=$((Rows "SELECT approved FROM tickets WHERE id=$mtid").Trim()) sent_back=$((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim())"
    # ...AND IT DID NOT ANSWER THE ASK EITHER (R6). This is the same rule at the surface R6 adds,
    # and it is a new way to break it: the approval question is a row with an `answer` column, so
    # "the manager said ok, so approve it" would be one `QuestionAnswer` call away. Nothing that
    # is not a person may answer a question of kind `land` -- no verdict, no timeout, no default.
    # The manager has just been asked for `approve` on purpose, and the question is still open.
    Check 'a_manager_verdict_never_answers_the_approval_ask' `
        ((RowsTry "SELECT state FROM questions WHERE id=$lq").Trim() -eq 'open' -and
         [int]((Rows "SELECT COUNT(*) FROM questions WHERE kind='land' AND state='answered'").Trim()) -eq 0) `
        ((Rows "SELECT id, state, answer, subject FROM questions WHERE kind='land'") -replace '\s+', ' ')

    # ---- round 2: the cheap tier is unsure, so the SAME question goes to the expensive one ----
    # D-R12 bounds the READING as well as the loop: the diffstat plus the agent's report on the
    # cheap tier, and more only when that tier says it is unsure. `mgrlow` forces that path.
    Turn 'mgrverdict:send-back mgrmsg:STILL-UNMENTIONED mgrlow' 'd.cs'
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim()) -ge 2 } 40000 'the second send-back' | Out-Null
    $rev2 = Rows "SELECT detail FROM events WHERE kind='manager_review' ORDER BY id DESC LIMIT 1"
    Check 'the_review_escalates_to_the_expensive_tier_when_the_cheap_one_is_unsure' `
        ($rev2 -match '"tier":"hi"' -and $rev2 -match '"verdict":"send-back"') $rev2

    # ---- THE BOUND IS COUNTED IN THE STORE, so a publish cannot reset it --------------------
    #
    # A daemon restarts on every publish and every hot swap. An in-memory counter would start
    # again from zero right here, the next three rounds would all be sent back, and it would look
    # perfectly green in a single-daemon test while being wrong on the operator's machine -- 3's
    # dead routing ladder in a third costume, and the same reason R4 reads its previous digest
    # back out of its own event. So the restart happens BETWEEN round two and round three.
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down between rounds two and three' | Out-Null
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon5.out" -RedirectStandardError "$out\daemon5.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # ROUND 3 IS SPELLED OUT RATHER THAN USING `Turn`, because the adoption wait has to be the
    # round's OWN `say` and not a turn beside it. Two things this fixture got wrong on its first
    # run, both worth keeping written down:
    #
    #  * Adoption is waited for by ASKING the lane, never by reading `state` -- the row still
    #    says `alive` from before the restart, so a state check returns instantly and proves
    #    nothing (m1 carries the same reasoning for the same fixture).
    #  * The change is COMMITTED BEFORE the lane is asked anything. A separate `say` to test
    #    adoption ended its turn while `e.cs` sat uncommitted in the worktree, so the digest had
    #    genuinely moved and R4 wrote a record for it -- the product answering correctly while
    #    the fixture had asked the wrong question. Commit first and every turn from here sees
    #    one tree.
    Set-Content "$mwt\src\e.cs" "// e.cs"
    git -C $mwt add -A | Out-Null
    git -C $mwt -c user.email=t@t -c user.name=t commit -q -m 'e.cs' | Out-Null
    $adopted = ''
    Wait-Until {
        $script:adopted = Dodona @("say", "$mlane", "say mgrverdict:send-back mgrmsg:THIRD-TIME finished a pass on e.cs")
        $script:adopted -match "lane $mlane"
    } 30000 'the adopted ticket lane accepts input again' | Out-Null
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim()) -ge 3 } 40000 'the third send-back' | Out-Null

    # ---- the fourth objection does not happen: it goes to the operator, with the history ----
    # An infinitely polite loop is CLAUDE.md 0.1's "never stuck" in a costume where everyone is
    # being reasonable (D-R12). At the bound there is NO model call either -- a fourth review
    # would spend quota on a note nothing can act on -- and the operator is told ONCE, which is
    # why the announcement is gated on its own event rather than on a flag.
    Turn 'mgrverdict:send-back mgrmsg:FOURTH-TIME' 'f.cs'
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='manager_bound_reached'").Trim()) -ge 1 } 40000 'the send-back bound' | Out-Null
    $bnd = Rows "SELECT detail FROM events WHERE kind='manager_bound_reached' ORDER BY id DESC LIMIT 1"
    $sbCount = (Rows "SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'").Trim()
    $revAll = Rows "SELECT detail FROM events WHERE kind='manager_review' ORDER BY id"
    # NO FOURTH MODEL CALL, asserted on the ROUND rather than on a total count of reviews. A
    # total is not a claim about the bound: any turn that moves the worktree earns a record and
    # therefore a review, so the total is a fact about how chatty the fixture was, and pinning it
    # makes the check fail for reasons that have nothing to do with D-R12. `round` is derived
    # from the send-back count in the store, so a review at round 4 is exactly and only the
    # fourth objection this check says does not happen. (A racing record cannot forge one
    # either: its report is the agent's echo, which carries no `mgrverdict:`, so it reads `ok`.)
    Check 'three_send_backs_is_the_bound_and_it_survives_a_daemon_restart' `
        ($sbCount -eq '3' -and $revAll -notmatch '"round":4' -and $bnd -match "ticket $mtid") `
        "sent_back=$sbCount bound=[$bnd] reviews=[$revAll]"
    $ann = Rows "SELECT body FROM pane_events WHERE lane_id=$mlane AND kind='announcement' AND body LIKE '%the bound%' ORDER BY id DESC LIMIT 1"
    Check 'the_bound_hands_the_operator_the_history_instead_of_a_fourth_round' `
        ($ann -match 'sent back 3 times' -and $ann -match 'SCHEMA-UNMENTIONED' -and
         $ann -match 'STILL-UNMENTIONED' -and $ann -match 'THIRD-TIME') $ann
    # AND THE ASK SAYS SO (R6). At the bound the operator is being handed a DIFFERENT question
    # from an ordinary approval -- three rounds of objection are spent and it is theirs to judge
    # regardless -- so an overlay that looked identical to a clean ticket's would be the honest
    # part of D-R18 lost at the last surface. One question still, refreshed in place.
    Wait-Until { (Rows "SELECT input FROM questions WHERE kind='land' AND state='open' AND subject='$mtid'") -match 'the bound' } `
        25000 'the bound reaching the ask' | Out-Null
    $lqBound = "$(Rows "SELECT input FROM questions WHERE kind='land' AND state='open' AND subject='$mtid'")"
    Check 'the_ask_says_the_bound_was_reached_rather_than_looking_like_an_ordinary_approval' `
        ($lqBound -match 'sent this back 3 times' -and $lqBound -match 'the bound' -and
         $lqBound -match 'yours to judge' -and
         [int]((Rows "SELECT COUNT(*) FROM questions WHERE kind='land' AND subject='$mtid'").Trim()) -eq 1) `
        "text=[$($lqBound -replace '\s+', ' ')] rows=$((Rows "SELECT id, state, subject FROM questions WHERE kind='land'") -replace '\s+', ' ')"
    # =====================================================================================
    # R8 -- THE REVIEWER MAY ASK FOR DETAILS, AND A MECHANICAL OBJECTION IS NOT A STRIKE
    # (docs/REVIEW-AND-MERGE-PLAN.md D-R23 / D-R24, plus D-R25 / D-R26 / D-R27 decided there)
    #
    # Both amendments are the operator's, decided 2026-08-21, and they are one decision seen
    # from two sides: the manager must not become a thing that HOLDS AGENTS UP. A FRESH TICKET,
    # because the one above deliberately ends at the bound and no further review will ever run
    # on it (D-R18: at three there is no fourth model call).
    $tk8 = Dodona @("ticket-create", "--title", "FOAMSHIP-R8", "--claim", "subtree:src")
    $t8 = if ($tk8 -match 'ticket (\d+) branch') { $Matches[1] } else { 0 }
    $wt8 = "$root\.dodona\wt\t$t8"
    $l8 = Dodona @("ticket-agent", "$t8", "--child", $fake)
    $lane8 = if ($l8 -match 'lane (\d+)') { $Matches[1] } else { 0 }
    Wait-Until { (Rows "SELECT state FROM lanes WHERE id=$lane8").Trim() -eq 'alive' } 25000 'the R8 ticket agent is alive' | Out-Null

    # PER-TICKET COUNTS, NEVER THE GLOBAL ONES. The ticket above ends at three send-backs on
    # purpose, so a bare `SELECT COUNT(*) FROM events WHERE kind='manager_sent_back'` down here
    # would be counting THAT ticket's history and would pass or fail for reasons nothing below
    # is about. This is `Store.AboutTicket`'s own boundary written in SQL: the trailing space is
    # what stops ticket 1 from matching ticket 12. One parameter, called with one argument -- a
    # plain PowerShell function SILENTLY SWALLOWS extras into $args (CLAUDE.md 0.2).
    function T8([string]$kind) { [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='$kind' AND detail LIKE 'ticket $t8 %'").Trim()) }
    function Turn8([string]$directives, [string]$file) {
        Set-Content "$wt8\src\$file" "// $file"
        git -C $wt8 add -A | Out-Null
        git -C $wt8 -c user.email=t@t -c user.name=t commit -q -m $file | Out-Null
        Dodona @("say", "$lane8", "say $directives finished a pass on $file") | Out-Null
    }

    # ---- D-R24, AND THIS IS THE CHECK MOST WORTH WRITING FIRST --------------------------
    #
    # R5's objection to the whole exemption was never overturned -- *an exemption the model
    # classifies is a bound the model can talk its way out of* -- it is what FIXES the
    # implementation. Code decides, and it decides from R4's record. So the fake manager is made
    # to say, in as many words, that its objection was mechanical, while the record says
    # `verify: not-run`. It must earn exactly nothing by saying so.
    #
    # AND `not-run` IS NOT RED, which is the load-bearing half. `not-run` is the NORMAL value --
    # no verify has run for most tickets, by design (D-R15) -- so an implementation that read it
    # as red would exempt every send-back there has ever been and the bound would stop existing
    # while looking perfectly present. This one check pins both, because the record here says
    # exactly `not-run` and the objection claims exactly the opposite.
    Turn8 'mgrverdict:send-back mgrmsg:THE-TESTS-ARE-RED-HONESTLY' 'r1.cs'
    Wait-Until { (T8 'manager_sent_back') -ge 1 } 40000 'the R8 ticket first send-back' | Out-Null
    $r8rev1 = Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    Check 'a_manager_claiming_its_objection_was_mechanical_earns_no_exemption' `
        ((T8 'manager_sent_back') -eq 1 -and (T8 'manager_sent_back_mechanical') -eq 0 -and
         $r8rev1 -match '"verify":"not-run"' -and $r8rev1 -match '"exempt":false') `
        "review=[$r8rev1] counted=$(T8 'manager_sent_back') exempt=$(T8 'manager_sent_back_mechanical') ticket=[$tk8]"

    # ---- the code fact itself: a REAL red verify, written by the land ---------------------
    #
    # `LandFlow` is the only producer of `verify_red` in the tree and there must not be a second
    # one here either: a fixture that inserted the event by hand would be proving this suite's
    # idea of the record rather than the land's (D-R15 -- the record REPORTS the verify result
    # and never runs one). `Config.For` re-reads dodona.json per call, which is what makes the
    # swap below work, and it is restored immediately so no later section inherits it.
    Dodona @("approve", "$t8") | Out-Null
    Dodona @("token-request", "$t8") | Out-Null
    Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0; verify = @('exit 3') } | ConvertTo-Json)
    $r8land = Dodona @("land", "$t8")
    Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
    Wait-Until { (T8 'verify_red') -ge 1 } 30000 'the land writing a red verify for the R8 ticket' | Out-Null

    # ---- D-R24 / D-R25: THE EXEMPTION KEYS ON THE RECORD, AND IT IS ITS OWN EVENT KIND ----
    #
    # A strike is a judgement the agent has to answer; red tests are a fact it can already see,
    # so spending one of three chances on them means an agent gets fewer REAL rounds for having
    # had a bad build at the wrong moment. The exempt send-back is `manager_sent_back_mechanical`
    # rather than a flag inside the JSON (D-R25): the bound counts `manager_sent_back`, and R6's
    # approval ask counts it AGAIN, independently, to word "you are at the bound". A separate
    # kind keeps both readers right with no new logic in either, where a flag would have had to
    # be learned by every counter in the tree and would have been wrong in whichever one was
    # missed -- and a bound that quietly stops bounding is this project's most repeated failure.
    Turn8 'mgrverdict:send-back mgrmsg:REALLY-MECHANICAL' 'r2.cs'
    Wait-Until { (T8 'manager_sent_back_mechanical') -ge 1 } 40000 'the exempt send-back' | Out-Null
    $r8rev2 = Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    $r8pane = Rows "SELECT body FROM pane_events WHERE lane_id=$lane8 AND kind='user_input' AND body LIKE '%manager review%' ORDER BY id DESC LIMIT 1"
    Check 'a_send_back_on_a_red_verify_spends_none_of_the_three_rounds' `
        ((T8 'manager_sent_back') -eq 1 -and (T8 'manager_sent_back_mechanical') -eq 1 -and
         $r8rev2 -match '"verify":"red"' -and $r8rev2 -match '"exempt":true' -and
         $r8pane -match 'REALLY-MECHANICAL' -and $r8pane -match 'not one of your 3 rounds') `
        "counted=$(T8 'manager_sent_back') exempt=$(T8 'manager_sent_back_mechanical') review=[$r8rev2] pane=[$r8pane] land=[$r8land]"

    # ---- D-R26: ONE EXEMPTION PER VERIFY RESULT, NOT AN ENDLESS SUPPLY -------------------
    #
    # `verify_red` is written by the land and never re-run (D-R15), so the record goes on saying
    # `red` for as long as no further land happens -- and an exemption keyed only on that word
    # would be an unbounded send-back loop for free, which is D-R12's failure mode restored by
    # the very fix written to honour it (CLAUDE.md 0.1: never stuck). The FIRST mechanical
    # objection carries information the agent can act on. A second one about the SAME red
    # carries none, so it counts, and three of those reach the bound and the operator like any
    # other judgement. The identity of a red is the verify event's own timestamp, out of the
    # record -- not the word `red`, which never changes.
    Turn8 'mgrverdict:send-back mgrmsg:SAME-RED-AGAIN' 'r3.cs'
    Wait-Until { (T8 'manager_sent_back') -ge 2 } 40000 'the repeated mechanical objection' | Out-Null
    $r8rev3 = Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    Check 'the_second_objection_on_the_same_red_verify_is_not_exempt' `
        ((T8 'manager_sent_back') -eq 2 -and (T8 'manager_sent_back_mechanical') -eq 1 -and
         $r8rev3 -match '"verify":"red"' -and $r8rev3 -match '"exempt":false') `
        "counted=$(T8 'manager_sent_back') exempt=$(T8 'manager_sent_back_mechanical') review=[$r8rev3]"

    # ---- D-R23: NAMED AND NARROW, BOUNDED AND ONCE, RECORDED -----------------------------
    #
    # The default does not change and is now PERMANENT: the diffstat, the changed-file names and
    # the agent's own report, never the diff content. What is added is the reviewer being able
    # to say "I need to see this one file before I can judge it". Each of the three properties
    # below is a way that escape hatch could quietly become the expensive reviewer arriving
    # through the door marked cheap -- which is the thing the operator said no to, at any price,
    # because the cost is paid in agents held up.
    #
    # THE TOKEN LIVES INSIDE THE FILE AND NOWHERE ELSE IN THE QUESTION: the diffstat is names
    # and counts, the changed list is names, and the report is the agent's echo of the `say`
    # below. So a note carrying `read:GRANTED-D1` can only have come from the CONTENT being
    # handed over. A check that asserted the file's NAME appeared would pass against a build
    # that granted nothing at all -- the name is already in the question, every time.
    # From here the verdicts are `ok`: two rounds are spent and a third would hit the bound,
    # which would end the reviews this section still needs.
    Set-Content "$wt8\src\d1.cs" "// detailtoken:GRANTED-D1"
    Set-Content "$wt8\src\d2.cs" "// detailtoken:GRANTED-D2"
    git -C $wt8 add -A | Out-Null
    git -C $wt8 -c user.email=t@t -c user.name=t commit -q -m 'd1+d2' | Out-Null
    Dodona @("say", "$lane8", "say mgrverdict:ok mgrneed:src/d1.cs mgrneed2:src/d2.cs finished a pass") | Out-Null
    Wait-Until { (T8 'manager_details_granted') -ge 1 } 40000 'the details round' | Out-Null
    Wait-Until {
        (Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1") -match 'read:GRANTED-D1'
    } 25000 'the review that read the file it asked for' | Out-Null
    $r8det = Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    $r8gr = Rows "SELECT detail FROM events WHERE kind='manager_details_granted' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    Check 'a_review_that_asks_for_a_named_file_is_given_that_file_and_reads_it' `
        ($r8det -match '"details":\["src/d1\.cs"\]' -and $r8det -match 'read:GRANTED-D1' -and
         $r8det -match '"detailsWhy":"[^"]+"' -and $r8gr -match 'src/d1\.cs') `
        "review=[$r8det] granted=[$r8gr]"
    # ...AND ONLY ONCE. `mgrneed2:` is what the fake asks for ON the details pass, so a second
    # round would put `src/d2.cs` in that same list and hand the reviewer a file it never
    # earned. Ask, read, ask again is D-R12's send-back loop one level down, and it is the
    # failure this bound exists for -- untimed, it would look exactly like a slow model.
    Check 'the_details_round_happens_once_and_a_second_request_is_not_read' `
        ($r8det -notmatch 'd2\.cs' -and $r8det -notmatch 'GRANTED-D2' -and (T8 'manager_details_granted') -eq 1) `
        "review=[$r8det] granted_events=$(T8 'manager_details_granted')"

    # ...AND NARROW. A request that can widen to everything IS the full diff read with extra
    # steps, so what may be named is the record's own changed list -- enforced in `GrantDetails`
    # in code, never by the prompt asking nicely (a prompt is not a boundary,
    # WORK-ISOLATION-PLAN 2). `*` is refused for exactly the reason `../../secrets` and another
    # repository's file would be: none of them is a file this change touched. And the round is
    # still SPENT by the asking, or a reviewer could retry after every refusal and have its
    # unbounded loop back for the price of one bad path.
    Set-Content "$wt8\src\d4.cs" "// d4"
    git -C $wt8 add -A | Out-Null
    git -C $wt8 -c user.email=t@t -c user.name=t commit -q -m 'd4' | Out-Null
    Dodona @("say", "$lane8", "say mgrverdict:ok mgrneed:* finished a pass on d4") | Out-Null
    Wait-Until { (T8 'manager_details_granted') -ge 2 } 40000 'the refused details request' | Out-Null
    $r8ref = Rows "SELECT detail FROM events WHERE kind='manager_review' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1"
    Check 'a_details_request_that_is_not_a_named_changed_file_is_refused_and_recorded' `
        ($r8ref -match '"details":\[\]' -and $r8ref -match 'not one of the files this change touched' -and
         $r8ref -notmatch 'detailtoken') `
        "review=[$r8ref] granted=[$(Rows "SELECT detail FROM events WHERE kind='manager_details_granted' AND detail LIKE 'ticket $t8 %' ORDER BY id DESC LIMIT 1")]"

    # Hand the rest of the suite the machine it expects back: the ticket agents go, and autostart
    # goes back off so the sections below own their own lifetimes again.
    Dodona @("lane-stop", "$lane8") | Out-Null
    Wait-Until { (Rows "SELECT state FROM lanes WHERE id=$lane8").Trim() -ne 'alive' } 25000 'the R8 ticket agent is stopped' | Out-Null
    Dodona @("lane-stop", "$mlane") | Out-Null
    Wait-Until { (Rows "SELECT state FROM lanes WHERE id=$mlane").Trim() -ne 'alive' } 25000 'the ticket agent is stopped' | Out-Null
    $env:DODONA_NO_AUTOSTART = "1"

    # ---- P3.5: ADOPTION FAILURE IS NOT A SPAWN TRIGGER ----------------------------------
    # The two checks above prove the daemon reuses a brain it CAN adopt. This one is the case
    # that actually leaked: a predecessor that is unmistakably alive -- its pipe is in the OS
    # namespace -- but which this daemon cannot adopt. "I could not adopt it" and "it is not
    # there" were the same branch, and only the second justifies a spawn.
    #
    # Measured on the operator's own instance: 14 BRAIN lanes, one per daemon start across a
    # morning of auto-publish swaps, each an idle `claude -p` nobody could reach. Reconcile
    # declared the predecessor unreachable after one 500 ms knock, wrote `utility_lane_reaped
    # ... "shim gone"` over a process that was still running, and the warm-up then started a
    # replacement beside it -- 160 ms later, in the store rows.
    #
    # HOW THE FAILURE IS FORCED: a shim serves ONE client at a time, so a test holding the
    # brain's pipe makes the next daemon's connect fail while the process is provably alive.
    # That is the exact shape of the incident, and it needs no timing luck to reproduce.
    # NAMED, NOT `LIMIT 1` (P5.6). With one brain per workspace `LIMIT 1` WAS the only brain; with
    # one per project it picks an ARBITRARY row, and every check in this section -- the wedge, the
    # refusal, the never-called-gone -- would then be about whichever one sqlite happened to return
    # first. The whole block would become a green proving nothing, which costs more than a red. So
    # the brain is selected by the project it is FOR.
    $brainLane = (RowsTry "SELECT id FROM lanes WHERE role='brain' AND state='alive' AND project='$root' ORDER BY id LIMIT 1").Trim()
    # A lane id or nothing usable -- never an error string spliced into the SQL below, which
    # would throw and take the tally down with it.
    if ($brainLane -notmatch '^\d+$') { $brainLane = '-1' }
    $brainPipe = "dodona-$($ws.Id)-lane$brainLane"
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down' | Out-Null
    $hold = New-Object System.IO.Pipes.NamedPipeClientStream('.', $brainPipe,
        [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        # A FAILED CONNECT IS A FAILED CHECK, NOT A DEAD SUITE. `$hold.Connect` throws, and the
        # `finally` below disposes but does not swallow -- so a probe that could not reach the pipe
        # aborted the whole suite inside its own try, no tally was printed, and `dev prove`
        # reported every check in the file as MISSING rather than as a verdict. Found 2026-08-19
        # proving Phase 5: against a build with no `lanes.project` the query above cannot name a
        # brain, `$brainLane` is `-1`, and the pipe name is nonsense. The honest answer is a red
        # precondition; twenty MISSING verdicts is not an answer at all.
        $holdHello = $null
        try {
            $hold.Connect(10000)
            $holdReader = New-Object System.IO.StreamReader($hold)
            $holdHello = $holdReader.ReadLine()  # drain the greeting; now the shim is ours
        }
        catch { $holdHello = "<could not hold pipe '$brainPipe': $($_.Exception.Message)>" }
        # A PRECONDITION, not a claim about the fix -- `dev prove` reports it VACUOUS and that is
        # correct, because HEAD has a live predecessor here too. It is kept deliberately: it is
        # what stops the next check passing for the wrong reason. If the hold ever fails to
        # connect, `no_second_brain_beside_a_live_one` would go green against no predecessor at
        # all, and nothing else in the suite would notice.
        Check 'the_predecessor_brain_is_provably_alive' ($holdHello -match '^!hello') $holdHello

        # COUNT THE SPAWNS, not the pipes. Counting live lane pipes here was this check's first
        # form and it was WRONG for a reason worth keeping: a lane pipe blinks out of the
        # namespace while the shim swaps server instances, and every shim in a workspace
        # disconnects the instant its daemon stops -- which is exactly when this measurement is
        # taken. It read 4 live pipes where 7 shims were running, so the "before" was too low and
        # the check failed while the product was behaving perfectly. A `shim_spawned` row cannot
        # blink: a second brain is a spawn, and a spawn is written down.
        $spawnsBefore = [int](Rows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'").Trim()
        $brainRowsBefore = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='brain'").Trim()
        $daemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws.Id -PassThru -NoNewWindow `
            -RedirectStandardOutput "$out\daemon5.out" -RedirectStandardError "$out\daemon5.err"
        Wait-Daemon $ws.CtlPipe | Out-Null
        Dodona @("brain-start") | Out-Null       # force the decision rather than waiting on a warm-up
        $spawnsAfter = [int](Rows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'").Trim()
        $brainRowsAfter = [int](Rows "SELECT COUNT(*) FROM lanes WHERE role='brain'").Trim()
        Check 'no_second_brain_beside_a_live_one' `
            (($spawnsAfter -eq $spawnsBefore) -and ($brainRowsAfter -eq $brainRowsBefore)) `
            "shim_spawned before=$spawnsBefore after=$spawnsAfter; brain rows before=$brainRowsBefore after=$brainRowsAfter"
        # ...and it is in the causal chain, not merely absent. A silent degrade is a bug
        # (CLAUDE.md 0.1): the only evidence of two dead routing days was a status-line suffix.
        Check 'the_refusal_is_recorded' `
            (([int](Rows "SELECT COUNT(*) FROM events WHERE kind='utility_predecessor_live'").Trim()) -ge 1) `
            (Rows "SELECT kind, detail FROM events WHERE kind IN ('utility_predecessor_live','utility_lane_stubborn','utility_lane_reaped') ORDER BY id DESC LIMIT 4")
        # And reconcile must NOT have called a live process gone. That one line is what dropped
        # the only reference capable of stopping it.
        # `lane_id`, NOT `lane`. The first version of this check used a column that does not
        # exist, so sqlite errored, Rows returned nothing, the count parsed as 0 and the check
        # PASSED -- against everything, forever. `dev prove` called it vacuous and that is why.
        # A check written against the wrong column is indistinguishable from a check that works.
        Check 'a_live_pipe_is_never_called_shim_gone' `
            (([int](Rows "SELECT COUNT(*) FROM events WHERE kind='utility_lane_reaped' AND lane_id=$brainLane").Trim()) -eq 0) `
            (Rows "SELECT kind, lane_id, detail FROM events WHERE lane_id=$brainLane ORDER BY id DESC LIMIT 5")
    }
    finally { try { $hold.Dispose() } catch { } }

    # ---- THE ONE-PROJECT CASE MUST NOT START REPORTING A NEW FIELD ---------------------
    # Every phase of docs/LOCATIONS-PLAN.md leaves the one-project case byte-for-byte
    # identical -- the property the whole workspace migration rested on, and the one the plan
    # says it is most likely to break. The operator's own machine is a one-project workspace,
    # so `scope=` (which names the project a management lane is scoped to) must be absent
    # here: with one project there is exactly one answer and the field would be noise.
    #
    # VACUOUS BY CONSTRUCTION, and kept anyway (the shape Phase 0c's
    # `an_explicit_root_beats_the_inherited_env` established): HEAD prints no `scope=` either,
    # so no code state makes this go red. It exists so a later change cannot start printing
    # one here quietly.
    Check 'a_one_project_workspace_says_nothing_about_scope' `
        ((Dodona @("status")) -notmatch 'scope=') `
        (((Dodona @("status")) -split "`r?`n" | Where-Object { $_ -match '^lane ' }) -join ' | ')

    Dodona @("stop-daemon") | Out-Null

    # =====================================================================================
    # A BRAIN PER PROJECT (docs/LOCATIONS-PLAN.md Phase 5, decision D-L8)
    #
    # THE BUG THIS SECTION EXISTS FOR. Reconcile kept ONE `keep` lane id per utility role and
    # shut every other alive lane of that role down as "a duplicate left by a fixed leak" --
    # so with two projects it killed a healthy brain on every daemon start, including every
    # auto-publish swap, and ANNOUNCED IT AS A REPAIR. A count cannot tell "two brains because
    # two projects" from "two brains because of a bug".
    #
    # The operator's correction (2026-08-19): *"You just use a global system to keep track of
    # that stuff. If it's not tracked, it's not valid. Why must you do some weird kill to
    # count?"* So validity is a REGISTRATION -- `lanes.project` (schema v10) keyed by
    # (role, project) -- and "surplus" stops being a count.
    #
    # WHY THIS FIXTURE IS THE ONLY ONE THAT CAN SEE ANY OF IT: one project has exactly one
    # brain per role, so every check above this line passes with the defect fully present.
    # Two projects in one workspace is the whole test.
    #
    # MODEL-FREE, and it takes a deliberate step to stay that way: `brain-start` has no
    # `--child`, it uses the agent from its project's dodona.json. New-TwoProjectWorkspace
    # writes no config at all, so without the two files below both brains would be a real
    # `claude -p` -- i.e. quota, from a suite (CLAUDE.md 2.6).
    # =====================================================================================
    $p5Daemon = $null
    $p5Dirs = New-Object System.Collections.ArrayList
    $tp = New-TwoProjectWorkspace $dodona 'p5brains'
    [void]$p5Dirs.Add($tp.Dir)
    foreach ($p in @($tp.A, $tp.B)) {
        Set-Content "$p\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
    }
    function Tp([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        $ef = Join-Path $out 'p5.stderr.tmp'
        Remove-Item $ef -ErrorAction SilentlyContinue
        $o = (& $dodona ($a + @('--workspace', $tp.Id)) 2> $ef) | Out-String
        $global:DODONA_EXIT = $LASTEXITCODE
        $e = if (Test-Path $ef) { (Get-Content $ef -Raw) } else { '' }
        ("$o`n$e").Trim()
    }
    function TpRows([string]$sql) { Invoke-StoreSql $tp.Store $sql }
    # `lanes.project` DOES NOT EXIST BEFORE v10, and Invoke-StoreSql throws on a sqlite error
    # (correctly -- a query naming a missing column used to return empty, parse as 0 and pass).
    # A throw here would abort the suite inside its own try and report every later check as
    # MISSING rather than as a verdict, so the error is turned into the check's DETAIL string
    # instead of being swallowed: a red that says `no such column: project` is exactly the red
    # this section wants against HEAD.
    function TpTry([string]$sql) { try { TpRows $sql } catch { "<sql error: $($_.Exception.Message)>" } }
    # A count that may have come back as an error string, WITHOUT the two traps around it.
    # `[int]'<sql error: ...>'` throws (and would abort the suite, reporting every later check as
    # MISSING); `[int]''` is 0, and `-eq 0` is a passing assertion, which is how a check written
    # against a column that does not exist passed against every build ever made
    # (.claude/skills/check-authoring 3). -1 can never equal a real count, so an unreadable
    # answer FAILS and the error text still reaches the check's detail string.
    function AsInt([string]$s) { $n = 0; if ([int]::TryParse((("$s") -replace '\s', ''), [ref]$n)) { $n } else { -1 } }
    function Flat([string]$s) { (("$s") -replace '\s+', ' ') }
    # A lane id out of a list that may be shorter than expected. '-1' keeps the SQL that follows
    # syntactically valid, so a missing brain fails a check instead of throwing inside it.
    function Nth($a, [int]$i) { if ($a -and @($a).Count -gt $i) { @($a)[$i] } else { '-1' } }
    function StartP5Daemon([string]$tag) {
        $p = Start-Process $dodona -ArgumentList "daemon", "--workspace", $tp.Id -PassThru -NoNewWindow `
            -RedirectStandardOutput "$out\$tag.out" -RedirectStandardError "$out\$tag.err"
        Wait-Daemon $tp.CtlPipe | Out-Null
        $p
    }

    try {
        $p5Daemon = StartP5Daemon 'p5daemon1'

        # ---- two projects, two brains, and each knows which project it is for -------------
        $bA = Tp @("brain-start", "--project", $tp.A)
        $bB = Tp @("brain-start", "--project", $tp.B)
        Wait-Until { (AsInt (TpRows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'")) -ge 2 } `
            25000 'a brain exists for each of the two projects' | Out-Null
        $brainIds = @((TpRows "SELECT id FROM lanes WHERE role='brain' AND state='alive' ORDER BY id").Trim() -split "`r?`n" | Where-Object { $_ })
        Check 'two_projects_get_two_distinct_brains' `
            ($brainIds.Count -eq 2 -and $brainIds[0] -ne $brainIds[1]) `
            "brain lanes=[$($brainIds -join ',')] A=$(Flat $bA) B=$(Flat $bB)"
        # THE REGISTRATION ITSELF. Without a row saying which (role, project) a brain is for,
        # "surplus" can only ever be a count -- which is the thing being deleted.
        $scoped = (TpTry "SELECT group_concat(project) FROM (SELECT project FROM lanes WHERE role='brain' AND state='alive' ORDER BY id)").Trim()
        Check 'a_brains_project_is_recorded_on_its_lane_row' `
            ($scoped -match [regex]::Escape($tp.A) -and $scoped -match [regex]::Escape($tp.B)) `
            "project column=[$scoped] A='$($tp.A)' B='$($tp.B)'"
        # ...and a PERSON can read it. A brain lane's cwd is neutral (P5.8 -- a brain inside a
        # project loads that project's CLAUDE.md and skills, i.e. a manager that can run
        # /ship), so `project=` says nothing about it by design and `scope=` is the new fact.
        $p5St = Tp @("status")
        $scopeA = Get-StatusScope $p5St (Nth $brainIds 0)
        $scopeB = Get-StatusScope $p5St (Nth $brainIds 1)
        Check 'status_names_the_project_a_brain_is_scoped_to' `
            (($scopeA -eq $tp.A -or $scopeA -eq $tp.B) -and ($scopeB -eq $tp.A -or $scopeB -eq $tp.B) -and $scopeA -ne $scopeB) `
            "lane $(Nth $brainIds 0) scope='$scopeA'  lane $(Nth $brainIds 1) scope='$scopeB'"

        # ---- THE ESCALATION ASKS THE FOCUSED LANE'S OWN PROJECT'S MANAGER ----------------
        # Phase 5 handed this to Phase 3 as prose, deliberately unnumbered. `EnsureBrainAsync` and
        # `AskBrainHiAsync` take a project, and `BrainReview` already resolves the reviewed lane's
        # own registration -- but the lane ladder's escalation inside `RouteInput` still passed the
        # DEFAULT, which is the workspace's first project, while the fact sheet it sends describes
        # the FOCUSED lane and its siblings. So project B's lanes were reasoned about by project
        # A's manager: the cross-project confusion the projects work removed everywhere else.
        #
        # HOW THIS IS FORCED WITHOUT A MODEL: `routekind:unclear` makes the fake classifier answer
        # unclear, which is the one verdict that escalates to the expensive tier -- and the
        # expensive tier is created on demand, so the brain-hi lane that appears is the one this
        # call site asked for, and its `project` column is the answer.
        #
        # PROJECT B, NEVER A. A is `_primary`, so a check asserting A cannot fail against the
        # build that always answered A -- four VACUOUS verdicts taught this plan that rule.
        Tp @("router-start", "--child", $fake) | Out-Null
        Wait-Until { (AsInt (TpRows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'")) -eq 1 } `
            25000 'the two-project workspace has a classifier' | Out-Null
        $escLane = Tp @("lane-start", "--title", "BETAWORK", "--project", $tp.B, "--child", $fake)
        if ($escLane -notmatch 'lane (\d+)') { throw "lane-start in project B failed: $escLane" }
        $escLaneId = $Matches[1]
        Tp @("say", "$escLaneId", "say beta up") | Out-Null
        Wait-Until { (Tp @("tail", "$escLaneId", "10")) -match 'beta up' } 20000 'the project-B lane answers' | Out-Null
        Tp @("input", "routekind:unclear hikind:new-task say something ambiguous") | Out-Null
        Wait-Until { (AsInt (TpTry "SELECT COUNT(*) FROM lanes WHERE role='brain-hi' AND state='alive'")) -ge 1 } `
            30000 'the escalation creates an expensive-tier manager' | Out-Null
        # EVERY brain-hi row, not the newest one. The newest would be the wrong question to ask:
        # the escalated verdict then SPAWNS a lane, whose `BrainReview` escalates in turn and
        # already passed the right project -- so `ORDER BY id DESC LIMIT 1` could read that second
        # session and go green against a build where the first one was still scoped to A. The claim
        # is that NO expensive-tier manager was ever created for project A over project B's work.
        $hiScopes = (TpTry "SELECT group_concat(project) FROM (SELECT project FROM lanes WHERE role='brain-hi' ORDER BY id)").Trim()
        Check 'the_escalation_asks_the_focused_lanes_own_projects_manager' `
            (($hiScopes -match [regex]::Escape($tp.B)) -and ($hiScopes -notmatch [regex]::Escape($tp.A))) `
            "brain-hi projects=[$hiScopes] want B='$($tp.B)' and never A='$($tp.A)'"
        # Tidy up before the restart section reads brain counts: the expensive tier, the classifier
        # and the work lanes are this check's fixture, not the next one's.
        foreach ($role in 'brain-hi', 'router', 'work') {
            foreach ($lid in @((TpTry "SELECT id FROM lanes WHERE role='$role' AND state='alive'").Trim() -split "`r?`n" |
                               Where-Object { $_ -match '^\d+$' })) {
                Tp @("lane-stop", $lid) | Out-Null
            }
        }
        Wait-Until { (AsInt (TpTry "SELECT COUNT(*) FROM lanes WHERE state='alive' AND role IN ('brain-hi','router','work')")) -eq 0 } `
            25000 'the escalation fixture is cleared' | Out-Null

        # ---- A RESTART ADOPTS BOTH AND RETIRES NEITHER -----------------------------------
        # The exact failure: `_brainLo` is one scalar, so the adoption loop overwrote it and
        # the surplus loop shut the other one down. Two projects, one daemon restart, one
        # healthy session killed and reported as healing.
        $beforeRestart = @($brainIds)
        Tp @("stop-daemon") | Out-Null
        Wait-Until { -not (Test-DodonaPipe $tp.CtlPipe) } 20000 'the two-project daemon is down' | Out-Null
        $p5Daemon = StartP5Daemon 'p5daemon2'
        # Being up is not being RECONCILED, and the reap runs inside reconcile -- so wait for the
        # second `reconcile_done` row before reading what survived, or this measures the moment
        # before the decision it is about.
        Wait-Until { (AsInt (TpRows "SELECT COUNT(*) FROM events WHERE kind='reconcile_done'")) -ge 2 } 25000 'the second reconcile is recorded' | Out-Null
        $afterRestart = @((TpRows "SELECT id FROM lanes WHERE role='brain' AND state='alive' ORDER BY id").Trim() -split "`r?`n" | Where-Object { $_ })
        # `-eq 2` IS PART OF THE ASSERTION, not a tidy-up. `before -eq after` alone was VACUOUS:
        # against a build with one brain per WORKSPACE both lists are `[1]`, they match, and the
        # check passes while asserting nothing about adoption at all. `dev prove` said so, which is
        # the whole reason it exists (2026-08-19).
        Check 'restart_adopts_a_brain_for_every_project' `
            ($afterRestart.Count -eq 2 -and ($afterRestart -join ',') -eq ($beforeRestart -join ',')) `
            "before=[$($beforeRestart -join ',')] after=[$($afterRestart -join ',')] rows=$(Flat (TpTry "SELECT id, state, project FROM lanes WHERE role LIKE 'brain%'"))"
        # The harm signature, because it is what the operator would have SEEN: an announcement
        # claiming a repair while a healthy session was shut down. Paired with "both are still
        # alive" for the same VACUOUS reason as above -- a build that never makes two brains never
        # makes a surplus either, so zero surplus rows on its own is true for the wrong reason.
        Check 'no_healthy_brain_is_retired_as_a_surplus' `
            ((TpRows "SELECT COUNT(*) FROM events WHERE kind='brain_surplus_retired'").Trim() -eq '0' -and
             (TpRows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim() -eq '2') `
            "surplus rows=$(Flat (TpRows "SELECT lane_id, detail FROM events WHERE kind='brain_surplus_retired'")) alive=$((TpRows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'").Trim())"
        # P5.6: `reconcile_done` regexed `brain=\d+`, so `brain=3,7` would keep matching while
        # asserting nothing at all. The key is `brains=` now, plural, so the old pattern cannot
        # match by accident -- a degraded check is forced to be rewritten instead of going quiet.
        $rd = (TpRows "SELECT detail FROM events WHERE kind='reconcile_done' ORDER BY id DESC LIMIT 1").Trim()
        Check 'reconcile_lists_a_brain_per_project' `
            ($rd -match "brains=\[[^\]]*$([regex]::Escape($tp.ALeaf))=$(Nth $beforeRestart 0)" -and
             $rd -match "brains=\[[^\]]*$([regex]::Escape($tp.BLeaf))=$(Nth $beforeRestart 1)") `
            "reconcile_done=$rd"

        # ---- P5.5: A BRAIN WHOSE PROJECT LEFT IS REAPED, AND ONLY THAT ONE ---------------
        # A lifecycle event that did not exist: `workspace-detach` reaches lanes whose CWD is
        # in the departing project (Phase 2's project-gone), and a brain's cwd is NEUTRAL -- so
        # a brain for a project that is gone was invisible to every existing path and is the
        # obvious source of the next leak.
        $bBrain = @((TpTry "SELECT id FROM lanes WHERE role='brain' AND state='alive' AND project='$($tp.B)'").Trim() -split "`r?`n" | Where-Object { $_ -match '^\d+$' })
        $bBrainLane = if ($bBrain.Count -eq 1) { $bBrain[0] } else { Nth $afterRestart 1 }
        $bShimFile = "$($tp.Dir)\shim-lane$bBrainLane.json"
        $bShimPid = if (Test-Path $bShimFile) { [int]((Get-Content $bShimFile -Raw | ConvertFrom-Json).shimPid) } else { -1 }
        $aBrainLane = Nth $afterRestart 0
        & $dodona workspace-detach --member $tp.B --workspace $tp.Id | Out-Null
        Wait-Until { (TpTry "SELECT state FROM lanes WHERE id=$bBrainLane").Trim() -eq 'dead' } 25000 "the detached project's brain is reaped" | Out-Null
        Check 'a_detached_projects_brain_is_reaped' `
            ((TpTry "SELECT state FROM lanes WHERE id=$bBrainLane").Trim() -eq 'dead') `
            "lane $bBrainLane state='$((TpTry "SELECT state FROM lanes WHERE id=$bBrainLane").Trim())' rows=$(Flat (TpTry "SELECT id, state, project FROM lanes WHERE role='brain'"))"
        # THE ROW IS NOT THE POINT: the two OS processes have to be gone. PROCESSES, NOT PIPES
        # -- a lane pipe blinks out of the namespace while its shim swaps server instances
        # (8 of 192 reads over 1.5 s), so polling for a pipe's absence eventually catches the
        # gap and calls a live agent stopped, which is a false green.
        Wait-Until { $bShimPid -le 0 -or -not (Get-Process -Id $bShimPid -ErrorAction SilentlyContinue) } `
            20000 "the reaped brain's shim exits" | Out-Null
        Check 'a_detached_projects_brain_shim_really_exits' `
            ($bShimPid -gt 0 -and -not (Get-Process -Id $bShimPid -ErrorAction SilentlyContinue)) `
            "shim pid $bShimPid for lane $bBrainLane"
        Check 'the_reaping_says_which_registration_went_stale' `
            ((TpRows "SELECT detail FROM events WHERE kind='brain_unregistered' AND lane_id=$bBrainLane").Trim() -match [regex]::Escape($tp.B)) `
            (TpRows "SELECT kind, lane_id, detail FROM events WHERE lane_id=$bBrainLane ORDER BY id DESC LIMIT 4")
        # THE OTHER HALF, and the one this whole phase is about: the brain of a project that
        # STAYED is untouched. A reaper that widened until everything resolved would pass the
        # check above and be the very bug being removed. VACUOUS by construction -- HEAD reaps
        # nothing here, so no code state makes it red -- and kept because it is the only line
        # that would catch a reaper going too wide.
        Check 'a_brain_in_a_project_that_stayed_is_untouched' `
            ((TpRows "SELECT state FROM lanes WHERE id=$aBrainLane").Trim() -eq 'alive') `
            "lane $aBrainLane state='$((TpRows "SELECT state FROM lanes WHERE id=$aBrainLane").Trim())'"

        # ---- P5.4: A WEDGED BRAIN IN ONE PROJECT MUST NOT BLOCK ANOTHER ------------------
        # `ClearOfLivePredecessorsAsync` filtered by ROLE only, and `_shutdownAsked` is a
        # HashSet that is never cleared -- so one brain that will not let go of its pipe
        # refused to let a brain be created for EVERY project, for the life of the daemon.
        #
        # HOW THE WEDGE IS FORCED, with no timing luck: a shim serves ONE client at a time, so
        # a test holding project A's brain pipe makes the next daemon's connect fail while the
        # process is provably alive. That is the exact shape of the leak incident.
        $projC = Join-Path (Use-SuiteTemp) ("dodona-p5c-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force "$projC\src" | Out-Null
        Set-Content "$projC\src\c.cs" "// project c"
        Set-Content "$projC\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
        & $dodona workspace-attach --member $projC --workspace $tp.Id | Out-Null
        $aPipe = "dodona-$($tp.Id)-lane$aBrainLane"
        Tp @("stop-daemon") | Out-Null
        Wait-Until { -not (Test-DodonaPipe $tp.CtlPipe) } 20000 'the two-project daemon is down again' | Out-Null
        $wedge = New-Object System.IO.Pipes.NamedPipeClientStream('.', $aPipe,
            [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        try {
            # Non-fatal for the reason recorded above: a probe that cannot connect must fail its
            # own precondition check, never abort the suite and turn every verdict into MISSING.
            $wedgeHello = $null
            try {
                $wedge.Connect(10000)
                $wedgeReader = New-Object System.IO.StreamReader($wedge)
                $wedgeHello = $wedgeReader.ReadLine()   # drain the greeting; the shim is ours now
            }
            catch { $wedgeHello = "<could not hold pipe '$aPipe': $($_.Exception.Message)>" }
            # A PRECONDITION, not a claim about the fix. HEAD wedges here too, so `dev prove`
            # calls it VACUOUS and that is correct -- it is kept because without it the check
            # below could go green against no wedge at all and nothing would notice.
            Check 'the_wedged_brain_is_provably_alive' ($wedgeHello -match '^!hello') $wedgeHello
            $p5Daemon = StartP5Daemon 'p5daemon3'
            $spawnsBefore = AsInt (TpRows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'")
            $cOut = Tp @("brain-start", "--project", $projC)
            $cBrains = "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive' AND project='$projC'"
            Wait-Until { (AsInt (TpTry $cBrains)) -ge 1 } 25000 "project C gets a brain despite project A's wedged one" | Out-Null
            Check 'a_wedged_brain_in_one_project_does_not_block_another' `
                ((AsInt (TpTry $cBrains)) -ge 1 -and
                 (AsInt (TpRows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'")) -gt $spawnsBefore) `
                "out=$(Flat $cOut) rows=$(Flat (TpTry "SELECT id, state, project FROM lanes WHERE role='brain'"))"
            # ...and the wedged one was NOT called gone. Writing "shim gone" over a live pipe is
            # what dropped the only reference capable of stopping 14 leaked brains.
            Check 'the_wedged_brain_was_never_called_gone' `
                ((AsInt (TpRows "SELECT COUNT(*) FROM events WHERE kind='utility_lane_reaped' AND lane_id=$aBrainLane")) -eq 0 -and
                 (AsInt (TpRows "SELECT COUNT(*) FROM events WHERE kind='brain_unregistered' AND lane_id=$aBrainLane")) -eq 0) `
                (Flat (TpRows "SELECT kind, lane_id, detail FROM events WHERE lane_id=$aBrainLane ORDER BY id DESC LIMIT 5"))
        }
        finally { try { $wedge.Dispose() } catch { } }
    }
    finally {
        if ($p5Daemon -and -not $p5Daemon.HasExited) { try { Stop-Process -Id $p5Daemon.Id -Force } catch { } }
    }

    # =====================================================================================
    # P5.7 -- A CAP ON CONCURRENT BRAINS, WHICH REFUSES RATHER THAN EVICTING
    #
    # Measured before this phase: each lane is two OS processes, and today's steady state is
    # 4 lanes / 8 processes. Ten projects would be 13 / 26, peaking at 23 / 46 with every
    # `brain-hi` warm -- TEN OF THEM OPUS. Quota is the scarce resource (CLAUDE.md 2.6), so a
    # brain per project needs a ceiling.
    #
    # IT MUST REFUSE, NEVER EVICT. Making room by shutting an existing brain down is the
    # count-and-kill loop growing back in a new place, and a cap that evicts is worse than no
    # cap: it would kill a session that is mid-question to serve one that is not.
    # =====================================================================================
    $capDaemon = $null
    $cap = New-TwoProjectWorkspace $dodona 'p5cap'
    [void]$p5Dirs.Add($cap.Dir)
    Set-Content "$($cap.A)\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0; maxBrains = 1 } | ConvertTo-Json)
    Set-Content "$($cap.B)\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
    function Cap([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        $o = (& $dodona ($a + @('--workspace', $cap.Id))) | Out-String
        $o.Trim()
    }
    function CapRows([string]$sql) { Invoke-StoreSql $cap.Store $sql }
    function CapTry([string]$sql) { try { CapRows $sql } catch { "<sql error: $($_.Exception.Message)>" } }
    try {
        $capDaemon = Start-Process $dodona -ArgumentList "daemon", "--workspace", $cap.Id -PassThru -NoNewWindow `
            -RedirectStandardOutput "$out\p5cap.out" -RedirectStandardError "$out\p5cap.err"
        Wait-Daemon $cap.CtlPipe | Out-Null
        Cap @("brain-start", "--project", $cap.A) | Out-Null
        Wait-Until { (AsInt (CapRows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'")) -eq 1 } `
            25000 "the capped workspace's first brain is alive" | Out-Null
        $capFirst = (CapRows "SELECT id FROM lanes WHERE role='brain' AND state='alive'").Trim()
        $capSpawns = AsInt (CapRows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'")
        $capOut = Cap @("brain-start", "--project", $cap.B)
        # THE ANSWER HAS TO SAY THE CAP DID IT. Counting brains alone is not enough and the
        # reason is worth keeping: HEAD has exactly one brain per WORKSPACE, so "one brain, no
        # new shim" is true there for a completely different reason and the check would pass
        # against the defect (VACUOUS -- the 2026-08-18 trap). The refusal naming `maxBrains` is
        # the only part of this that cannot be true by accident.
        Check 'the_brain_cap_refuses_a_new_project' `
            ((AsInt (CapRows "SELECT COUNT(*) FROM lanes WHERE role='brain' AND state='alive'")) -eq 1 -and
             (AsInt (CapRows "SELECT COUNT(*) FROM events WHERE kind='shim_spawned'")) -eq $capSpawns -and
             $capOut -match 'maxBrains') `
            "out=$(Flat $capOut) rows=$(Flat (CapTry "SELECT id, state, project FROM lanes WHERE role='brain'"))"
        # A NEGATIVE GUARD, and VACUOUS by construction: HEAD evicts nothing here either,
        # because it never gets as far as having two brains to choose between. It is kept for
        # the reason Phase 0c kept `an_explicit_root_beats_the_inherited_env` -- a later cap
        # that made room by shutting a brain down would be the count-and-kill loop growing back,
        # and this is the only line in the tree that would notice.
        Check 'the_brain_cap_never_evicts_an_existing_brain' `
            ((CapRows "SELECT state FROM lanes WHERE id=$capFirst").Trim() -eq 'alive') `
            "lane $capFirst state='$((CapRows "SELECT state FROM lanes WHERE id=$capFirst").Trim())'"
        # A silent degrade is a bug (CLAUDE.md 0.1 and 3's dead routing ladder): a project with
        # no judgement must say so, and name the setting that lifts it.
        Check 'the_brain_cap_refusal_names_the_setting_that_lifts_it' `
            ((CapRows "SELECT detail FROM events WHERE kind='brain_cap_reached' ORDER BY id DESC LIMIT 1") -match 'maxBrains') `
            (CapRows "SELECT kind, detail FROM events WHERE kind='brain_cap_reached' ORDER BY id DESC LIMIT 2")
        Cap @("stop-daemon") | Out-Null
    }
    finally {
        if ($capDaemon -and -not $capDaemon.HasExited) { try { Stop-Process -Id $capDaemon.Id -Force } catch { } }
        foreach ($d in @($p5Dirs)) { Stop-WorkspaceShims $d }
    }
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
    # ...and the shim exit log beside it. A suite's DODONA_HOME is deleted with the run, so
    # without this the one record of WHY a wrapper process ended dies with it -- and "it was
    # simply gone" is the diagnosis this whole phase exists to stop accepting.
    Copy-Item "$wsDir\shim-exits.log" "$out\shim-exits.log" -ErrorAction SilentlyContinue
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
