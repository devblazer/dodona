# UI ASK acceptance: the window ASKS things, and it is not a dialog (docs/LOCATIONS-PLAN.md
# Phase 4, decision D-L4). One component, two render modes, one answer path -- proved over all
# three question kinds that real code paths produce: `repo-init` (ticket-create in a folder
# with no git repo), `land` (a finished ticket asking to be merged, REVIEW-AND-MERGE-PLAN R6),
# and the ROUTER's own held sentence (LOCATIONS-PLAN P3.A).
#
# `PickerWindow` and `StartLaneWindow` have ZERO coverage between them for exactly one reason:
# they are dialogs, and a test window is forbidden from producing a modal at all. So the ask is
# an OVERLAY plus a `ui dump` key, and these checks are what make asking observable.
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
$dodonaHome = Use-IsolatedDodonaHome 'uiask'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'ui-ask-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-uiask-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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

# Two workspaces, each with its own window: $ws3 is the project with no git repo (the
# `repo-init` and `land` questions), $tp4 is the two-project one (the router's held sentence).
# Tracked so the scoped cleanup in `finally` can stop every one of them by pid (CLAUDE.md §4).
$daemon3 = $null
$askUi = $null
$ws3 = $null
$daemon4 = $null
$routeUi = $null
$tp4 = $null
try {
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    # =====================================================================================
    # THE ASK: ONE COMPONENT, TWO RENDER MODES, ONE ANSWER PATH
    # (docs/LOCATIONS-PLAN.md Phase 4 — P4.1/P4.2/P4.3/P4.5, decision D-L4)
    #
    # Driven the way a person drives it, because that is what this suite is for. `PickerWindow`
    # and `StartLaneWindow` have ZERO coverage between them for exactly one reason: they are
    # dialogs, `ui dump` had no field for a dialog, and a test window is forbidden from producing
    # a modal at all (MainWindow.IsTestWindow — it renders off-screen so it cannot steal the
    # keyboard, and a MessageBox would block until somebody clicked, which in an automated
    # capture is forever). So the ask is an OVERLAY plus a `ui dump` key, and these checks are
    # what make asking observable for the first time.
    #
    # The question used here is P4.5's: a project with no git repo. That one is produced by a
    # real code path with no model call and no Phase 3 -- `ticket-create` refusing for want of a
    # repository -- which is why it is the fixture. The ROUTER's rung-4 "we do not know the
    # project, so ask" is Phase 3's to produce; every assertion below is on the QUESTION ROW and
    # the ANSWER PATH rather than on which rung opened it, so it holds for that one too.
    #
    # ONE project in this workspace, so rule 2 still holds: the overlay appears because a row
    # says a question is open, never because the workspace has more than one of anything.
    # =====================================================================================
    $root3 = Join-Path (Use-SuiteTemp) ("dodona-uiask-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$root3\src" | Out-Null
    Set-Content "$root3\src\thing.cs" "// thing"
    Set-Content "$root3\dodona.json" (@{ main = 'main'; agent = $fake } | ConvertTo-Json)
    # NO `git init`. That is the whole fixture: a project Dodona can run lanes in and cannot cut
    # a ticket from (Repos.ForClaims refuses), which is the state P4.5 turns into a question.
    $ws3 = Get-WorkspacePaths $dodona $root3
    $daemon3 = Start-Process $dodona -ArgumentList "daemon", "--workspace", $ws3.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon3.out" -RedirectStandardError "$out\daemon3.err"
    Wait-Daemon $ws3.CtlPipe | Out-Null

    function Ask3([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        (& $dodona ($a + @('--workspace', $ws3.Id))) | Out-String
    }
    function AskDumpOrNull() { try { (& $dodona ui dump --workspace $ws3.Id) | Out-String | ConvertFrom-Json } catch { $null } }
    # TOLERANT ON PURPOSE, and only here. `Invoke-StoreSql` throws on a sqlite error, which is
    # right everywhere else — a query naming a column that does not exist must not come back
    # empty and be mistaken for a zero (check-authoring §3). But `dev prove` runs THIS section
    # against a HEAD whose store has no `questions` table at all, and a throw would abort the
    # suite and report every check below as MISSING instead of PROVEN. So a missing table reads
    # as "nothing is being asked", which is the honest answer, and **every assertion below fails
    # on an empty result** — none of them is an `-eq 0` that an empty string could satisfy. The
    # waits carry deadlines and return false, so the check that follows fails on its own terms.
    # TRIMMED, because `Invoke-StoreSql` ends in `| Out-String` and therefore returns
    # "repo-init`r`n" — and `-eq 'repo-init'` on that is false. Five checks here failed on their
    # first run for exactly that and nothing else, which is a false RED and costs the same as a
    # false green: the product was correct and the detail string proved it ("answered|yes" while
    # the assertion said the state was not `answered`).
    function Q3([string]$sql) { try { "$(Invoke-StoreSql $ws3.Store $sql)".Trim() } catch { '' } }
    # `git` in a folder that is not a repo writes "fatal: not a git repository" to STDERR, and
    # under this suite's `$ErrorActionPreference = 'Stop'` a native command's stderr line becomes
    # a NativeCommandError that ABORTS THE SUITE (CLAUDE.md §0.2). That is not hypothetical: the
    # first `dev prove` run of this section reached the last-but-four check, died there, printed no
    # tally, and reported all 23 perfectly good checks as MISSING rather than PROVEN — the same
    # failure the header of this file describes happening inside `finally`. 'Continue' is scoped
    # to the function, which is the pattern `Dodona` and `Ask3` above already use.
    function Git3([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        (& git -C $root3 @a 2>$null) | Out-String
    }

    $askUi = Start-Process $ui -ArgumentList "--workspace", $ws3.Id, "--test-window" -PassThru
    Wait-Until { $null -ne (AskDumpOrNull) } 30000 'the ask window answers ui dump' | Out-Null

    # ---- a refusal becomes a QUESTION, not an instruction (P4.5) --------------------------
    # Before this, ticket-create printed "(lanes work without git; only tickets need a
    # repository)" and left the operator to go and type `dodona repo-init` -- a GUI telling a
    # person to use the CLI, the same original sin as "undo: dodona lane-stop 3" printed in the
    # feed at somebody who has a mouse.
    $tc = Ask3 @('ticket-create', '--title', 'polish the thing', '--claim', 'path:src/thing.cs')
    Check 'ticket_create_without_a_repo_still_refuses' ($tc -match 'error') $tc.Trim()
    Wait-Until { (Q3 "SELECT COUNT(*) FROM questions WHERE state='open'") -ge 1 } 20000 'a question row is open' | Out-Null
    $qid = (Q3 "SELECT id FROM questions WHERE state='open' ORDER BY id LIMIT 1")
    Check 'the_refusal_opens_a_question_row' ("$qid" -match '^\d+$') "questions rows: $(Q3 'SELECT id, state, kind FROM questions')"
    Check 'the_question_is_about_this_project' `
        ((Q3 "SELECT kind FROM questions WHERE id=$qid") -eq 'repo-init') (Q3 "SELECT kind, subject FROM questions WHERE id=$qid")
    # A row, so it survives the window closing -- the argument ConciergeStore's class note makes
    # for questions being rows at all: a pending question that evaporated would make asking worse
    # than guessing.
    $qlist = Ask3 @('questions')
    Check 'the_question_is_listed_by_the_cli_too' ($qlist -match 'no git repo') $qlist.Trim()

    # ---- RENDER MODE 1: the in-window overlay, reported through `ui dump` (P4.2) ----------
    Wait-Until { ($script:a3 = AskDumpOrNull) -and $script:a3.ask -and "$($script:a3.ask.id)" -eq "$qid" } 20000 'the window renders the question' | Out-Null
    $a3 = AskDumpOrNull
    Check 'the_window_renders_the_open_question' ($null -ne $a3.ask -and "$($a3.ask.id)" -eq "$qid") ($a3.ask | ConvertTo-Json -Compress)
    Check 'the_ask_is_on_screen' ($a3.ask.shown -eq $true) ($a3.ask | ConvertTo-Json -Compress)
    Check 'the_ask_carries_the_question_text' ($a3.ask.question -match 'no git repo') $a3.ask.question
    $vals = @($a3.ask.choices).value
    Check 'the_ask_carries_its_choices' (($vals -contains 'yes') -and ($vals -contains 'no')) ($vals -join ',')
    # No FOLDER anywhere in it (CLAUDE.md §3.1, operator directive, and this phase's rule 1). The
    # choices are things the system already knows; if a path or a drive letter ever appears in a
    # choice VALUE, the overlay has started becoming the file picker that was deliberately deleted.
    # `-ge 1` first, deliberately: an EMPTY choice list contains no paths either, so without it
    # this check stays green against a build with no ask at all -- which is the vacuous shape
    # `dev prove` exists to catch, and it caught exactly this one.
    Check 'the_ask_offers_no_filesystem_navigation' `
        ($vals.Count -ge 1 -and @($vals | Where-Object { $_ -match '[\\/]' -or $_ -match '^[A-Za-z]:' }).Count -eq 0) ($vals -join ',')

    # ---- IT IS NOT A MODAL, and that is checkable ------------------------------------------
    # A MessageBox would block the dispatcher thread until somebody clicked it, so the ui pipe
    # would stop answering -- which is precisely why a modal ask is PERMANENTLY untestable and
    # why D-L4 rejected one. The window answering a dump WHILE a question is up is the proof
    # that it did not become one. The grid is still there behind it, too: a question is laid
    # over work that keeps running, not a machine that has stopped.
    # Three things at once, because "not a modal" needs all three and any one alone is VACUOUS:
    # the pipe still ANSWERS (a MessageBox blocks the dispatcher thread until somebody clicks,
    # which in an automated capture is forever), the question is ON SCREEN at the same moment,
    # and the WORK IS STILL THERE BEHIND IT -- same window, same input box, same feed. A dialog
    # would satisfy none of the last two.
    $live = AskDumpOrNull
    Check 'the_ask_is_not_a_modal' `
        ($null -ne $live -and $null -ne $live.ask -and $live.ask.shown -eq $true -and $null -ne $live.input -and $live.window.h -gt 0) `
        ($(if ($null -eq $live) { 'the ui pipe stopped answering while a question was up' } else { "ask=$($null -ne $live.ask) h=$($live.window.h)" }))
    (& $dodona ui screenshot --out "$out\ask-live.png" --workspace $ws3.Id) | Out-Null
    Check 'the_ask_renders_to_a_screenshot' (Test-Path "$out\ask-live.png") ''

    # ---- THE AFFORDANCE A PERSON CLICKS carries the value the verb takes (P4.3) -----------
    # This is the mechanical link between the pixels and the answer path: the BUTTON in the live
    # window is named `ask:<value>`, and `ui answer <value>` takes that same value into the same
    # method (MainWindow.AnswerAsk). Without this, "one answer path" would be a claim about code
    # nobody had looked at from the outside -- the exact shape of failure ui-use exists for.
    Reset-UiWindow
    $askWin = UiWindow "Dodona*$(Split-Path $root3 -Leaf)"
    Check 'the_ask_window_is_findable' ($null -ne $askWin) "no window titled Dodona*$(Split-Path $root3 -Leaf)"
    Check 'a_choice_is_a_real_button_a_person_can_click' ($null -ne (ByName $askWin 'ask:yes')) `
        'no automation element named ask:yes -- the choices are not clickable affordances'
    Check 'the_button_value_is_the_verb_value' `
        (($null -ne (ByName $askWin 'ask:yes')) -and ($vals -contains 'yes')) ($vals -join ',')

    # ---- ESCAPE PUTS IT DOWN, and loses nothing --------------------------------------------
    # An overlay that cannot be put down is a modal in all but name, and §0.1's standing
    # directive ("never hung, halted, stuck") applies to the operator's screen too. Dismissing is
    # VIEW state: the row stays open, `dodona questions` still lists it, and it is still
    # answerable. `ui key escape` lands in Window_KeyDown's own body, not a copy of it.
    $esc = (& $dodona ui key escape --workspace $ws3.Id) | Out-String
    Wait-Until { ($script:a3 = AskDumpOrNull) -and $script:a3.ask -and $script:a3.ask.shown -eq $false } 20000 'escape puts the ask down' | Out-Null
    $a3 = AskDumpOrNull
    Check 'escape_puts_the_ask_down' ($a3.ask.shown -eq $false -and $a3.ask.dismissed -eq $true) "esc='$($esc.Trim())' ask=$($a3.ask | ConvertTo-Json -Compress)"
    Check 'putting_it_down_does_not_answer_it' ((Q3 "SELECT state FROM questions WHERE id=$qid") -eq 'open') (Q3 "SELECT state FROM questions WHERE id=$qid")

    # ---- RENDER MODE 2 uses THE SAME ANSWER PATH (P4.3) ------------------------------------
    # `ui answer` goes through MainWindow.AnswerAsk -- the method the button's Click handler
    # calls -- which sends the daemon the same `answer` command `dodona answer` sends. One path,
    # so there is no second implementation to leave untested. And the effect is the real one:
    # `git init` plus the first commit, run by the SAME RepoInitOp the `repo-init` command runs.
    $ans = (& $dodona ui answer yes --workspace $ws3.Id) | Out-String
    Check 'the_ui_answer_verb_reaches_the_ask' ($ans -match 'answered') $ans.Trim()
    Wait-Until { (Q3 "SELECT state FROM questions WHERE id=$qid") -eq 'answered' } 30000 'the question is answered in the store' | Out-Null
    Check 'answering_records_the_answer_on_the_row' ((Q3 "SELECT state FROM questions WHERE id=$qid") -eq 'answered') (Q3 "SELECT state, answer FROM questions WHERE id=$qid")
    Wait-Until { Test-Path "$root3\.git" } 30000 'the project became a git repo' | Out-Null
    Check 'answering_yes_actually_creates_the_repo' (Test-Path "$root3\.git") "no .git in $root3"
    Check 'the_repo_has_the_first_commit' ((Git3 @('rev-parse', 'HEAD')) -match '[0-9a-f]{40}') (Git3 @('rev-parse', 'HEAD'))
    Check 'answering_is_announced_in_the_feed' ((Ask3 @('tail', '1')) -match 'git repository ready' -or (Q3 "SELECT COUNT(*) FROM pane_events WHERE body LIKE '%git repository ready%'") -ge 1) `
        (Q3 "SELECT COUNT(*) FROM pane_events WHERE body LIKE '%git repository ready%'")

    # ---- THE OVERLAY GOES AWAY BECAUSE THE ROW CLOSED, not because it was told ------------
    # m3 doctrine: the UI owns nothing and a view is a replay of rows. Answering through ANY
    # surface -- this verb, a click, `dodona answer`, another window -- closes the row, and every
    # window over this workspace stops asking within one 250ms tick.
    Wait-Until { ($script:a3 = AskDumpOrNull) -and $null -eq $script:a3.ask } 20000 'the overlay follows the row closing' | Out-Null
    Check 'the_overlay_closes_when_the_row_closes' ($null -eq (AskDumpOrNull).ask) ((AskDumpOrNull).ask | ConvertTo-Json -Compress)

    # ---- answering twice is refused, the same guard the concierge's questions have ---------
    # ONE invocation, landed in a variable. The first draft called `answer` twice -- once for the
    # condition and once for the detail -- and `answer` MUTATES: under a deliberate break the
    # first call succeeded, so the assertion saw no error while the detail printed "already
    # answered". A check whose detail is not the value it asserted on is a check that lies about
    # why it failed.
    $twice = Ask3 @('answer', "$qid", 'yes')
    Check 'answering_the_same_question_twice_is_refused' ($twice -match 'error') $twice.Trim()
    # A choice the question does not offer is REFUSED, never guessed. Asking exists because
    # guessing was wrong; a fuzzy answer would put the guess back at the one moment the operator
    # had actually told us the truth.
    $bogus = Ask3 @('answer', "$qid", 'maybe')
    Check 'an_answer_the_question_never_offered_is_refused' ($bogus -match 'error') $bogus.Trim()

    # ---- and the ticket now works, which is what the operator was trying to do -------------
    $tc2 = Ask3 @('ticket-create', '--title', 'polish the thing', '--claim', 'path:src/thing.cs')
    Check 'the_ticket_the_question_was_blocking_now_works' ($tc2 -notmatch '^error' -and $tc2 -match 'ticket') $tc2.Trim()

    # =====================================================================================
    # R6 / D-R11: THE APPROVAL ASK -- the write-up in front of the operator, and the yes
    # (docs/REVIEW-AND-MERGE-PLAN.md R6, absorbing docs/WORK-ISOLATION-PLAN.md D-7 and P5)
    #
    # R4 assembles a PR-shaped record when a ticket turn ends with the worktree moved, R5's
    # manager reads it and writes a note FOR THE OPERATOR -- and until this phase that note went
    # into the store and was read by nothing. This is where it reaches a person: the same
    # `questions` row, the same overlay, the same one answer path (D-L4), and answering it is the
    # operator's approval, which is the one thing a merge cannot happen without.
    #
    # THE FIXTURE REUSES THE DAEMON AND THE WINDOW ALREADY STANDING. `ui-use` is a SoloSuites
    # member, so every second here lands on the gate's wall clock directly (I7), and window
    # creation is the expensive part. By this line $root3 is a git repo (the question above made
    # it one) and the ticket that refusal was blocking exists -- so all this section adds is one
    # commit, one fake agent and one turn.
    #
    # NO REVIEW RUNS HERE, AND THAT IS THE POINT OF TESTING IT HERE. This suite sets
    # DODONA_NO_AUTOSTART=1, so R5's manager honours it and writes `manager_review_skipped`
    # (D-R17) -- which is exactly the case the ask must survive. If the ask only appeared once a
    # note existed, approving a merge would be gated on a model having answered, and a machine
    # with judgement switched off could never merge anything. The WITH-note case is proved in
    # `brain`, which already stands up a real (fake-backed) manager and is not a solo suite.
    # =====================================================================================
    $tid3 = if ($tc2 -match 'ticket (\d+) ') { $Matches[1] } else { 0 }
    # Read the worktree off the ROW rather than composing `.dodona\wt\t<N>` by hand: never
    # reconstruct a Dodona path (CLAUDE.md §5), and this suite has no business knowing the layout.
    $wt3 = (Q3 "SELECT worktree FROM tickets WHERE id=$tid3")
    function Wt3([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        (& git -C $wt3 @a 2>$null) | Out-String
    }
    Set-Content "$wt3\src\thing.cs" "// thing, polished"
    Wt3 @('add', '-A') | Out-Null
    Wt3 @('-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-q', '-m', 'polish') | Out-Null
    $tl3 = Ask3 @('ticket-agent', "$tid3")
    $tlid3 = if ($tl3 -match 'lane (\d+)') { $Matches[1] } else { 0 }
    # The fake agent's `say <text>` is a turn that ENDS, which is the trigger: R4's record exists
    # exactly when the worktree moved, and R6 hangs the question off the record existing.
    $said3 = Ask3 @('say', "$tlid3", 'say polished the mask; one file, and I have not run the tests')
    Wait-Until { (Q3 "SELECT COUNT(*) FROM questions WHERE kind='land' AND state='open'") -ge 1 } `
        40000 'the approval ask' | Out-Null
    $lq = (Q3 "SELECT id FROM questions WHERE kind='land' AND state='open' ORDER BY id DESC LIMIT 1")
    # A QUESTION ABOUT A TICKET, NOT ABOUT A PROJECT, and the subject is what says so: a ticket
    # number, which the system already knows, and never a folder. The two kinds must not be
    # confusable -- `a_one_project_workspace_is_never_asked_anything` above pins that a workspace
    # with one project is asked nothing, and this must not be a way around it.
    Check 'a_finished_ticket_asks_the_operator_to_approve_the_merge' `
        ("$lq" -match '^\d+$' -and (Q3 "SELECT subject FROM questions WHERE id=$lq") -eq "$tid3") `
        "ticket=[$($tc2.Trim())] lane=[$($tl3.Trim())] say=[$($said3.Trim())] rows=$(Q3 'SELECT id, kind, state, subject FROM questions')"
    # THE ONE MOST LIKELY TO BE GOT WRONG. Four ordinary things leave a ticket with no
    # `manager_review` row -- autostart off (D-R17), `"brain": false`, a cheap tier that timed
    # out, the send-back bound spent (D-R18) -- and if the ask waited for one, judgement being
    # switched off would make a merge un-approvable. Asserted in the shape that cannot be
    # satisfied by accident: the review provably did NOT run, and the question is there anyway.
    Check 'the_approval_ask_does_not_wait_for_a_review' `
        ("$lq" -match '^\d+$' -and
         (Q3 "SELECT COUNT(*) FROM events WHERE kind='manager_review_skipped'") -ge 1 -and
         (Q3 "SELECT COUNT(*) FROM events WHERE kind='manager_review'") -eq '0') `
        "q=$lq skipped=$(Q3 "SELECT COUNT(*) FROM events WHERE kind='manager_review_skipped'") reviews=$(Q3 "SELECT COUNT(*) FROM events WHERE kind='manager_review'")"
    # ...and it falls back to what CODE knows rather than an empty box: what changed, the verify
    # state (`not-run` is the normal value -- D-R15: the verify that gates is the land's own), and
    # why there is no note. A blank where a reason belongs is the failure `verify: not-run` and
    # `drop: moot` were both written to avoid.
    $lqText = (Q3 "SELECT input FROM questions WHERE id=$lq")
    Check 'the_approval_ask_says_what_code_knows_when_no_review_ran' `
        ($lqText -match 'ready to merge' -and $lqText -match '1 file' -and $lqText -match 'verify not-run' -and
         $lqText -match 'no review ran' -and $lqText -match 'Approve the merge') $lqText

    # ---- RENDER MODE 1 again, for the kind this phase adds ---------------------------------
    Wait-Until { ($script:a6 = AskDumpOrNull) -and $script:a6.ask -and "$($script:a6.ask.id)" -eq "$lq" } `
        20000 'the window renders the approval ask' | Out-Null
    $a6 = AskDumpOrNull
    Check 'the_approval_ask_renders_at_a_live_window' `
        ($null -ne $a6 -and $null -ne $a6.ask -and "$($a6.ask.id)" -eq "$lq" -and $a6.ask.shown -eq $true -and
         $a6.ask.question -match 'Approve the merge') `
        ($(if ($null -eq $a6) { 'the ui pipe did not answer' } else { $a6.ask | ConvertTo-Json -Compress }))
    # §3.1 again, on the kind whose subject is a thing that LIVES ON DISK: a ticket has a
    # worktree and a repository path hanging off it, and neither may reach a choice. `-ge 1`
    # first for the same reason the repo question's copy of this has it: an empty choice list
    # contains no paths either, and would keep this green against a build that never asks.
    $vals6 = @($a6.ask.choices).value
    Check 'the_approval_ask_offers_no_filesystem_navigation' `
        ($vals6.Count -ge 1 -and @($vals6 | Where-Object { $_ -match '[\\/]' -or $_ -match '^[A-Za-z]:' }).Count -eq 0) `
        ($vals6 -join ',')
    # Kept for the check AFTER the answer, and that is the whole reason it exists: "the overlay is
    # gone" is true of a build that never put one up, which is what `dev prove` called VACUOUS on
    # the first draft of it. Paired with this, the same check can only pass on a build that both
    # asked and stopped asking.
    $wasUp = ($null -ne $a6 -and $null -ne $a6.ask -and "$($a6.ask.id)" -eq "$lq" -and $a6.ask.shown -eq $true)

    # ---- THE YES, AND IT IS THE ONE THING A MERGE CANNOT HAPPEN WITHOUT (D-R10) ------------
    # §8's proof for this phase, in one check: the token is REFUSED before the answer and GRANTED
    # after it, through `dodona ui answer` -- the same method a button click lands in, sending the
    # same daemon command `dodona approve` sends. Both halves in one assertion on purpose: the
    # "granted" half alone would be green against a build where the ticket was already approved,
    # which is the vacuous shape `dev prove` exists to catch.
    $tokBefore = Ask3 @('token-request', "$tid3")
    $ansL = (& $dodona ui answer yes --workspace $ws3.Id) | Out-String
    Wait-Until { (Q3 "SELECT approved FROM tickets WHERE id=$tid3") -eq '1' } 25000 'the ticket is approved' | Out-Null
    $tokAfter = Ask3 @('token-request', "$tid3")
    Check 'answering_the_approval_ask_grants_the_merge_token' `
        ($tokBefore -match 'not approved' -and $ansL -match 'answered' -and $tokAfter -match 'granted') `
        "before=[$($tokBefore.Trim())] answer=[$($ansL.Trim())] after=[$($tokAfter.Trim())]"
    # It is the OPERATOR'S approval, recorded as one: same `ticket_approved` event `dodona
    # approve` writes, because both go through one `ApproveTicket`. R5 deliberately has no path
    # from a review to that method, and R6 must not become one by the back door.
    Check 'the_answer_is_recorded_as_the_operators_own_approval' `
        ((Q3 "SELECT COUNT(*) FROM events WHERE kind='ticket_approved'") -ge 1 -and
         (Q3 "SELECT state FROM questions WHERE id=$lq") -eq 'answered') `
        "events=$(Q3 "SELECT COUNT(*) FROM events WHERE kind='ticket_approved'") row=$(Q3 "SELECT state, answer FROM questions WHERE id=$lq")"
    # ...and then it STOPS ASKING -- the overlay goes because the ROW closed (m3 doctrine), and no
    # second question takes its place: `AskToLand` returns early on an approved ticket, so the
    # next turn cannot re-ask a decision already made. Three things in one assertion because the
    # last two alone are true of a build that never asks at all -- which is exactly what
    # `dev prove` called VACUOUS on the first draft of this check, and the reason `$wasUp` is
    # captured above rather than re-derived here.
    Wait-Until { ($script:a6 = AskDumpOrNull) -and $null -eq $script:a6.ask } 20000 'the approval overlay follows the row closing' | Out-Null
    Check 'an_approved_ticket_is_never_asked_about_again' `
        ($wasUp -and $null -eq (AskDumpOrNull).ask -and
         (Q3 "SELECT COUNT(*) FROM questions WHERE kind='land' AND state='open' AND subject='$tid3'") -eq '0') `
        "wasUp=$wasUp ask=$((AskDumpOrNull).ask | ConvertTo-Json -Compress) open=$(Q3 "SELECT COUNT(*) FROM questions WHERE kind='land' AND state='open' AND subject='$tid3'")"

    (& $dodona ui close --workspace $ws3.Id) | Out-Null
    Wait-Until { $null -eq (AskDumpOrNull) } 20000 'the ask window is gone' | Out-Null
    (& $dodona stop-daemon --workspace $ws3.Id) | Out-Null

    # =====================================================================================
    # THE ROUTER'S OWN QUESTION, RENDERED AND ANSWERED (LOCATIONS-PLAN P3.A)
    #
    # WHAT PHASE 4 COULD NOT TEST, AND SAID SO. The section above uses P4.5's repo question
    # because it is produced by a real code path; the ROUTER's rung 4 -- "we do not know which
    # project, so ask" -- wrote a `routing_decisions` row, an event and an announcement and
    # opened NO QUESTION ROW, so the overlay never saw it. Phase 4 deliberately did not write a
    # fixture faking one: a green over a row no code path produces proves nothing.
    #
    # It produces one now, so this is the whole loop through the real product: an ambiguous
    # sentence is HELD, the window shows the question, `ui answer` answers it through
    # MainWindow.AnswerAsk -- THE ONE ANSWER PATH (D-L4, P4.3), the same method the button's
    # Click handler calls -- and the held sentence lands in a lane in the project that was
    # picked. Nothing here fakes a row, and nothing here calls a daemon command the button
    # could not have sent.
    #
    # TWO PROJECTS, NO NAME IN THE SENTENCE, NO LIVE LANE: that is `no-name-no-live-lane`, the
    # rung that holds. It costs no model -- `ProjectLadder.Decide` reaches it in code -- so this
    # section starts no classifier and burns no quota (CLAUDE.md §2.6).
    $tp4 = New-TwoProjectWorkspace $dodona 'uiroute'
    foreach ($p in @($tp4.A, $tp4.B)) {
        Set-Content "$p\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0; brain = $false } | ConvertTo-Json)
    }
    $daemon4 = Start-Process $dodona -ArgumentList "daemon", "--workspace", $tp4.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon4.out" -RedirectStandardError "$out\daemon4.err"
    Wait-Daemon $tp4.CtlPipe | Out-Null
    function Rt([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        (& $dodona ($a + @('--workspace', $tp4.Id))) | Out-String
    }
    function RtDumpOrNull() { try { (& $dodona ui dump --workspace $tp4.Id) | Out-String | ConvertFrom-Json } catch { $null } }
    # TRIMMED, and tolerant, for the two reasons the `Q3` helper above records: Invoke-StoreSql
    # ends in `| Out-String` so every answer arrives with a trailing newline, and a store this
    # section reads before the table exists must fail the CHECK rather than abort the suite.
    function Q4([string]$sql) { try { "$(Invoke-StoreSql $tp4.Store $sql)".Trim() } catch { '' } }
    $routeUi = Start-Process $ui -ArgumentList "--workspace", $tp4.Id, "--test-window" -PassThru
    Wait-Until { $null -ne (RtDumpOrNull) } 30000 'the two-project window answers ui dump' | Out-Null

    # ---- the sentence is held, and the WINDOW is what says so ----------------------------
    $rtHeld = Rt @('input', 'make the header quite a lot taller')
    Check 'an_ambiguous_sentence_is_held_rather_than_placed' (($rtHeld -replace '\s+', ' ') -match 'held: not sure which project') `
        ($rtHeld -replace '\s+', ' ')
    Wait-Until { ($script:r4 = RtDumpOrNull) -and $script:r4.ask -and $script:r4.ask.shown -eq $true } `
        25000 'the window renders the routing question' | Out-Null
    $r4 = RtDumpOrNull
    # THE GAP, STATED AS AN ASSERTION: before this, `ask` was null here forever while the store
    # carried a `routing_decisions` row at tier `ask`. The rung asked nobody.
    Check 'the_routing_question_reaches_the_operators_window' `
        ($null -ne $r4 -and $null -ne $r4.ask -and $r4.ask.shown -eq $true) `
        ($(if ($null -eq $r4) { 'the ui pipe did not answer' } else { $r4.ask | ConvertTo-Json -Compress }))
    Check 'the_routing_question_asks_which_project' ("$($r4.ask.question)" -match 'Which project') "$($r4.ask.question)"
    $r4vals = @($r4.ask.choices).value
    Check 'the_routing_question_offers_the_projects' `
        (($r4vals -contains $tp4.ALeaf) -and ($r4vals -contains $tp4.BLeaf)) ($r4vals -join ',')
    # A routing question names PROJECTS, never paths to navigate (CLAUDE.md §3.1). Same assertion
    # `the_ask_offers_no_filesystem_navigation` makes about the repo question, on the source that
    # actually enumerates folders -- which is the one that could regress into a file picker.
    Check 'the_routing_question_offers_no_filesystem_navigation' `
        ($r4vals.Count -ge 2 -and @($r4vals | Where-Object { $_ -match '[\\/]' -or $_ -match '^[A-Za-z]:' }).Count -eq 0) `
        ($r4vals -join ',')
    # No lane yet, and this is the invariant P3.A had to preserve: the question exists, the work
    # does not. `brain:held_input_invents_no_lane` says it for the lane ladder; this says it for
    # the project ladder, from the outside, with a window open on it.
    Check 'a_rendered_routing_question_still_invents_no_lane' ((Q4 "SELECT COUNT(*) FROM lanes WHERE role='work'") -eq '0') `
        ((Q4 "SELECT id, title, role, cwd FROM lanes") -replace '\s+', ' ')
    # A real button, named for the value the verb takes -- the mechanical link between the pixels
    # and the answer path (P4.3). The repo question's buttons are `ask:yes`/`ask:no`; a routing
    # question's are named for the projects, and if the choices were not clickable affordances the
    # overlay would be a status line telling a person to go and type.
    Reset-UiWindow
    # The title is `Dodona — <workspace name>`, and the pattern is deliberately NOT `Dodona*`:
    # earlier sections leave other Dodona windows around, and matching one of those would look
    # like this check failing when it had never found the right window.
    $rtWin = UiWindow "Dodona*$($tp4.Name)*"
    Check 'a_project_choice_is_a_real_button_a_person_can_click' `
        ($null -ne $rtWin -and $null -ne (ByName $rtWin "ask:$($tp4.BLeaf)")) `
        "no automation element named ask:$($tp4.BLeaf) -- the projects are not clickable"

    # ---- ONE ANSWER PATH: `ui answer` is the button's own method ---------------------------
    # This verb calls MainWindow.AnswerAsk, which is what AskChoice_Click calls, which sends the
    # same `{cmd:"answer", id, answer}` the CLI's `dodona answer` sends. So a green here is a
    # statement about the code the BUTTON runs, not about a second headless path beside it.
    # THE SECOND project, never the first: A is `_primary`, which is what every spawn site
    # answered before the projects work, so asserting A is an assertion no build can fail.
    $rtAns = (& $dodona ui answer $tp4.BLeaf --workspace $tp4.Id) | Out-String
    Check 'the_ui_answer_verb_reaches_a_routing_question' ($rtAns -match 'answered') $rtAns.Trim()
    Wait-Until { (Q4 "SELECT COUNT(*) FROM lanes WHERE role='work'") -eq '1' } 30000 'answering opens the lane' | Out-Null
    Check 'answering_in_the_window_delivers_the_held_sentence_to_the_chosen_project' `
        ((Q4 "SELECT COUNT(*) FROM lanes WHERE role='work'") -eq '1' -and
         (Q4 "SELECT cwd FROM lanes WHERE role='work' ORDER BY id DESC LIMIT 1") -eq $tp4.B) `
        "lanes=$((Q4 'SELECT id, title, cwd FROM lanes WHERE role=''work''') -replace '\s+', ' ') want='$($tp4.B)'"
    Wait-Until { (Q4 "SELECT COUNT(*) FROM pane_events WHERE kind='user_input' AND body LIKE '%quite a lot taller%'") -ge '1' } `
        25000 'the held sentence reaches the lane it created' | Out-Null
    Check 'the_words_the_operator_typed_are_what_the_agent_receives' `
        ((Q4 "SELECT COUNT(*) FROM pane_events WHERE kind='user_input' AND body LIKE '%quite a lot taller%'") -eq '1') `
        ((Q4 "SELECT lane_id, kind, body FROM pane_events WHERE kind='user_input'") -replace '\s+', ' ')
    # The overlay goes away because the ROW closed, not because it was told to (m3 doctrine).
    Wait-Until { ($script:r4 = RtDumpOrNull) -and $null -eq $script:r4.ask } 20000 'the routing overlay follows its row' | Out-Null
    Check 'the_routing_overlay_closes_when_its_row_closes' ($null -eq (RtDumpOrNull).ask) `
        ((RtDumpOrNull).ask | ConvertTo-Json -Compress)

    (& $dodona ui close --workspace $tp4.Id) | Out-Null
    Wait-Until { $null -eq (RtDumpOrNull) } 20000 'the routing window is gone' | Out-Null
    (& $dodona stop-daemon --workspace $tp4.Id) | Out-Null
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
Write-Output "---- UI ASK ACCEPTANCE (the overlay that asks, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
