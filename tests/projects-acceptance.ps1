# Projects acceptance: WHICH PROJECT a lane opens in, and which one a typed sentence means
# (docs/LOCATIONS-PLAN.md phases 1, 2 and 3). Fake agents and scripted git only -- zero model calls.
#
# SPLIT OUT OF `workspace-acceptance.ps1` 2026-08-22, at the fixture boundary it already had.
# That suite was 1,654 lines and 30,794 tokens -- the largest test file in the repo and the one
# issue #23 names as the first candidate, because an agent asked to change it spends 30k tokens
# just opening it. Everything here stands up its OWN two-project workspace
# (`New-TwoProjectWorkspace`) and never touched that suite's `$root`, its daemon or its store, so
# the seam was real rather than a line count: the moved block referenced none of `$root`, `$ws`,
# `$storeDb`, `$wsDir` or `$daemon`.
#
# WHAT THE SPLIT IS **NOT** FOR, said here because the obvious reason is the wrong one: it does not
# make the gate faster. Both halves were already in the 3-wide wave, and the wave is SUM-BOUND
# (sum/3 = 177s against a longest suite of 90s), so splitting can only add a second fixture to the
# sum -- measured at ~4s, about +1s of wall clock. The win is the file size and nothing else, and
# the measurement is in the commit that made it. See issue #2 for the trap in the other direction:
# four SOLO pieces cost 134.3s against one 88.8s monolith, and ui-use's split paid only because it
# left SoloSuites.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the operator's own
# workspaces (CLAUDE.md 5, and 4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'proj'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes out of
# the build output, so a leaked daemon can never hold the file the compiler must overwrite.
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this suite owns daemon lifetime; start-on-demand must not join in
$out = Join-Path $PSScriptRoot 'projects-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$results = [ordered]@{}
$errFile = Join-Path $out 'stderr.tmp'
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }

# Every daemon this suite starts, so the `finally` stops exactly what it started -- resolved from
# this suite's own record and never by process name (CLAUDE.md 4).
$extraDaemons = New-Object System.Collections.ArrayList
# Three-line wrappers over tests\_workspace.ps1, where the bodies and their reasons live (the
# ctl-pipe/mutex race in particular). Wrapping rather than re-parameterising is what let every
# call site in the moved block stay exactly as it was.
function DodonaBare([string[]]$a) { Invoke-DodonaBare $dodona $errFile $a }
function StartDaemonFor([string]$wsId) { Start-WorkspaceDaemon $dodona $wsId $out $extraDaemons }
function StopDaemonFor([string]$wsId) { Stop-WorkspaceDaemon $dodona $errFile $wsId }

try {
    # ================== TWO PROJECTS WITH LIVE LANES (LOCATIONS-PLAN P1.1, P1.2) ==================
    # THE FIXTURE PHASES 2, 3 AND 5 ARE BUILT ON, and the first checks in the tree that read
    # `lanes.cwd` at all.
    #
    # What was missing, from the coverage audit that produced docs/LOCATIONS-PLAN.md: exactly TWO
    # checks anywhere asserted a lane's working directory (m3:183-187), both by parsing the
    # `shim_spawned` event DETAIL STRING -- the only observable surface for a lane's project in
    # the whole product. `status` did not say. `ui dump` did not say. And the `pair` section above
    # is the only two-project fixture with a daemon in the tree: it makes a ticket and stops, so
    # no suite had ever started a LANE in a workspace with more than one project.
    #
    # So a lane opening in the wrong project was invisible to a check AND to the operator, which
    # is why Phase 1 blocks Phases 2 to 5.
    #
    # HOW A LANE GETS INTO THE SECOND PROJECT TODAY: a ticket. `lane-start` passes `_primary`
    # (Daemon.cs:501) and so does the typed-input path, so a PLAIN lane can only land in the
    # first project until Phase 2 moves those spawn sites -- and that is exactly the asymmetry
    # these checks pin. A ticket lane already runs outside the first project, because its worktree
    # is created beside the repo that owns it (Paths.Worktrees, asserted just above).
    $tp = New-TwoProjectWorkspace $dodona 'twoproj'
    StartDaemonFor $tp.Id | Out-Null
    function Tp([string[]]$a) { DodonaBare ($a + @('--workspace', $tp.Id)) }
    function TpRows([string]$sql) { Invoke-StoreSql $tp.Store $sql }

    $ls2 = Tp @("lane-start", "--title", "PLAIN", "--child", $fake)
    if ($ls2 -notmatch 'lane (\d+)') { throw "lane-start failed in the two-project workspace: $ls2" }
    $plainLane = $Matches[1]
    Tp @("ticket-create", "--title", "BETAWORK", "--claim", "subtree:$($tp.BLeaf)/src") | Out-Null
    $ta2 = Tp @("ticket-agent", "1", "--child", $fake)
    if ($ta2 -notmatch 'lane (\d+)') { throw "ticket-agent failed in the two-project workspace: $ta2" }
    $betaLane = $Matches[1]
    # A management lane too, with the FAKE agent: `router-start --child` exists for suites for
    # exactly this reason (a real one is `claude -p`, i.e. quota). It belongs in the neutral
    # directory and NOT in either project -- a router or brain inside a project loads that
    # project's CLAUDE.md and skills, i.e. a classifier that can run /ship (commit 19dad3d).
    $rs = Tp @("router-start", "--child", $fake)
    if ($rs -notmatch 'lane (\d+)') { throw "router-start failed in the two-project workspace: $rs" }
    $routerLane = $Matches[1]

    # ---- the store records where each lane runs, and they are NOT the same place ----
    # The first check in this repo to read `lanes.cwd`. It would go red if lane-start and
    # ticket-agent both resolved to `_primary`, which is the single most likely way Phase 2
    # breaks: one spawn site moved, the other not.
    $plainCwd = (TpRows "SELECT cwd FROM lanes WHERE id=$plainLane").Trim()
    $betaCwd = (TpRows "SELECT cwd FROM lanes WHERE id=$betaLane").Trim()
    $routerCwd = (TpRows "SELECT cwd FROM lanes WHERE id=$routerLane").Trim()
    Check 'a_plain_lane_records_the_project_it_opened_in' `
        ($plainCwd -eq $tp.A) "cwd='$plainCwd' first_project='$($tp.A)'"
    Check 'a_ticket_lane_records_a_directory_inside_its_own_project' `
        ($betaCwd.StartsWith($tp.B, [StringComparison]::OrdinalIgnoreCase) -and
         -not $betaCwd.StartsWith($tp.A, [StringComparison]::OrdinalIgnoreCase)) "cwd='$betaCwd' B='$($tp.B)'"
    Check 'two_lanes_in_one_workspace_run_in_different_projects' `
        ($plainCwd.Length -gt 0 -and $betaCwd.Length -gt 0 -and
         -not $betaCwd.StartsWith($plainCwd, [StringComparison]::OrdinalIgnoreCase)) "plain='$plainCwd' ticket='$betaCwd'"

    # ---- and `dodona status` SAYS SO: a person can read which project a lane is in (P1.2) ----
    $tpSt = Tp @("status")
    $plainProj = Get-StatusProject $tpSt $plainLane
    $betaProj = Get-StatusProject $tpSt $betaLane
    $routerProj = Get-StatusProject $tpSt $routerLane
    Check 'status_names_the_project_of_a_plain_lane' ($plainProj -eq $tp.A) "project='$plainProj' want='$($tp.A)'"
    # THE PROJECT, NOT THE WORKTREE. A ticket lane's cwd is `<project>\.dodona\wt\tN`, and the
    # question a person is asking is "which project", so the ancestor is the answer and the
    # worktree path is what `lanes.cwd` is for.
    Check 'status_names_a_ticket_lanes_project_not_its_worktree' ($betaProj -eq $tp.B) "project='$betaProj' want='$($tp.B)'"
    Check 'status_does_not_report_two_projects_as_one' ($plainProj -ne $betaProj) "plain='$plainProj' ticket='$betaProj'"
    # A management lane is where it BELONGS, so there is nothing to say about it -- and the
    # omission is per ROLE, so a brain that ended up inside a project would still be named.
    Check 'a_management_lane_is_not_reported_against_a_project' `
        ($routerProj -eq '' -and $routerCwd.Length -gt 0 -and
         -not $routerCwd.StartsWith($tp.A, [StringComparison]::OrdinalIgnoreCase) -and
         -not $routerCwd.StartsWith($tp.B, [StringComparison]::OrdinalIgnoreCase)) "project='$routerProj' cwd='$routerCwd'"

    # ---- and the WINDOW says so, in the slot a person looks at (P1.2) ----
    # --test-window: off-screen, never activated, never in the taskbar. A test window that steals
    # the operator's keyboard mid-work was a priority complaint (CLAUDE.md 3).
    $tpUi = Start-Process "$bin\DodonaUi.exe" -ArgumentList "--workspace", $tp.Id, "--test-window" -PassThru
    [void]$extraDaemons.Add($tpUi)
    function TpDump() { try { (Tp @('ui', 'dump')) | ConvertFrom-Json } catch { $null } }
    Wait-Until { @((TpDump).slots | Where-Object { -not $_.empty }).Count -ge 2 } 30000 'the two-project window answers with both lanes' | Out-Null
    $tpD = TpDump
    $plainSlot = @($tpD.slots | Where-Object { -not $_.empty -and $_.title -eq 'PLAIN' })
    $betaSlot = @($tpD.slots | Where-Object { -not $_.empty -and $_.title -eq 'BETAWORK' })
    Check 'the_window_shows_both_projects_lanes' ($plainSlot.Count -eq 1 -and $betaSlot.Count -eq 1) `
        "titles=$(($tpD.slots | Where-Object { -not $_.empty }).title -join ',')"
    Check 'a_pane_names_the_project_its_lane_is_in' `
        ($plainSlot.Count -eq 1 -and $plainSlot[0].project -eq $tp.A) "project='$($plainSlot[0].project)' want='$($tp.A)'"
    Check 'a_ticket_panes_project_is_its_project_not_its_worktree' `
        ($betaSlot.Count -eq 1 -and $betaSlot[0].project -eq $tp.B) "project='$($betaSlot[0].project)' want='$($tp.B)'"
    # The daemon and the window must not be able to disagree: both call Projects.Field over the
    # same three inputs, and this is the check that notices if one of them stops.
    Check 'the_window_and_status_agree_about_a_lanes_project' `
        ($plainSlot[0].project -eq $plainProj -and $betaSlot[0].project -eq $betaProj) `
        "ui='$($plainSlot[0].project)','$($betaSlot[0].project)' status='$plainProj','$betaProj'"

    Tp @("ui", "close") | Out-Null

    # =====================================================================================
    # PHASE 2: A LANE OPENS IN A PROJECT (docs/LOCATIONS-PLAN.md Phase 2)
    #
    # Everything above this line is Phase 1 -- the ability to SEE which project a lane is in.
    # Everything below is the ability to CHOOSE it, and every check here was red against the
    # commit that landed Phase 1.
    #
    # WHY THESE LIVE HERE AND NOT IN m3. m3 has the only two checks in the tree that assert a
    # lane's working directory, and Phase 1 established by experiment that it CANNOT catch this
    # phase: reversing the cwd rungs left m3 31/31 green (a normally-spawned ticket lane's two
    # rungs name the same folder), and m3 covers the ticket-lane RESPAWN path but never the
    # SPAWN path at all. A green m3 is therefore not evidence about a spawn site. Two projects
    # in one workspace is the only fixture that can tell "the project it was given" from "the
    # first project", which is why P1.1 built it.
    # =====================================================================================

    # ---- P2.1/P2.2: a lane opens in the project it was given ----
    $lsB = Tp @("lane-start", "--title", "INB", "--project", $tp.B, "--child", $fake)
    if ($lsB -notmatch 'lane (\d+)') { throw "lane-start --project failed in the two-project workspace: $lsB" }
    $bLane = $Matches[1]
    Check 'a_lane_opens_in_the_project_it_was_given' `
        ((TpRows "SELECT cwd FROM lanes WHERE id=$bLane").Trim() -eq $tp.B) `
        "cwd='$((TpRows "SELECT cwd FROM lanes WHERE id=$bLane").Trim())' want='$($tp.B)'"
    # THE ROW IS NOT THE POINT: THE PROCESS HAS TO BE THERE. `lanes.cwd` is written BEFORE
    # Process.Start, so a recorded path only proves what the daemon INTENDED -- and "the lane
    # looks placed while the process is somewhere else" is exactly this phase's failure mode
    # (trap T1). The fake agent's `cwd` directive answers with its own Environment.CurrentDirectory,
    # so this is the OS's answer about the agent at the far end of the chain: daemon sets the
    # shim's WorkingDirectory, shim hands its cwd to the child, child reports it back.
    Tp @("say", "$bLane", "cwd") | Out-Null
    # No `$` anchor: PS -match is single-line, so `$` would demand the path be the last thing in
    # the whole tail. The path itself is unique to this project, which is the assertion.
    Wait-Until { (Tp @("tail", "$bLane", "10")) -match [regex]::Escape($tp.B) } 20000 'the project-B agent reports its own cwd' | Out-Null
    Check 'the_agent_process_really_runs_in_that_project' `
        ((Tp @("tail", "$bLane", "5")) -match [regex]::Escape($tp.B)) `
        "tail=$((Tp @("tail", "$bLane", "5")) -replace '\s+', ' ') want='$($tp.B)'"

    # A folder INSIDE a project resolves up to the project. A lane opens in a project, not in
    # whichever subdirectory a caller happened to name, so `lanes.cwd` stays in the operator's
    # units and `Projects.Field` keeps answering with a project path.
    $lsSub = Tp @("lane-start", "--title", "INSUB", "--project", (Join-Path $tp.B 'src'), "--child", $fake)
    if ($lsSub -notmatch 'lane (\d+)') { throw "lane-start --project <subdir> failed: $lsSub" }
    $subLane = $Matches[1]
    Check 'a_folder_inside_a_project_opens_in_that_project' `
        ((TpRows "SELECT cwd FROM lanes WHERE id=$subLane").Trim() -eq $tp.B) `
        "cwd='$((TpRows "SELECT cwd FROM lanes WHERE id=$subLane").Trim())' want='$($tp.B)'"

    # ---- P2.1: a folder no project owns is REFUSED, and leaves no row behind ----
    # The negative half, and the one that matters: a plain lane is completely ungated (trap T7 --
    # GateHook returns 0 with no --ticket), so an agent started in a folder no workspace owns is
    # an unbounded agent in a tree nothing here is tracking. `brain:220`
    # `held_input_invents_no_lane` is the same shape of assertion for the routing side.
    $outsider = Join-Path (Use-SuiteTemp) ("dodona-outsider-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$outsider\src" | Out-Null
    $lanesBefore = [int](TpRows "SELECT COUNT(*) FROM lanes").Trim()
    $refused = Tp @("lane-start", "--title", "OUTSIDE", "--project", $outsider, "--child", $fake)
    $lanesAfter = [int](TpRows "SELECT COUNT(*) FROM lanes").Trim()
    # Captured native stderr is WRAPPED to the console width, so a newline can land mid-sentence
    # (CLAUDE.md 0.2 -- it produced a false red once). Collapse before matching.
    Check 'a_lane_in_a_folder_no_project_owns_is_refused' `
        ($DODONA_EXIT -eq 1 -and (($refused -replace '\s+', ' ') -match 'is in no project of workspace')) `
        "exit=$DODONA_EXIT out=$($refused -replace '\s+', ' ')"
    Check 'a_refused_lane_leaves_no_row_behind' ($lanesAfter -eq $lanesBefore) "lanes went $lanesBefore -> $lanesAfter"

    # ---- P2.3 / trap T2: a lane's permissions come from ITS project's dodona.json ----
    # A repo deliberately kept on a leash loses it, otherwise. `permissionMode` plus
    # `allowedTools` is the ONLY thing CLAUDE.md 7 lets a project ask for, and `Config.For` --
    # which has existed since multi-repo landed -- had never once been used to configure a lane.
    #
    # Project A has no dodona.json, so it gets the built-in default (bypassPermissions). Project
    # B asks for acceptEdits. One daemon, one command each, two answers.
    #
    # `agent` is restated in B's file on purpose: Config.For picks a WHOLE FILE, it does not
    # merge two, so a project config that named only permissionMode would send this lane's agent
    # back to the built-in default of the real `claude` -- i.e. quota, from a suite.
    Set-Content "$($tp.B)\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0; permissionMode = 'acceptEdits' } | ConvertTo-Json)
    $lsPa = Tp @("lane-start", "--title", "LEASHA", "--project", $tp.A, "--child", $fake)
    if ($lsPa -notmatch 'lane (\d+)') { throw "lane-start in A failed: $lsPa" }
    $paLane = $Matches[1]
    $lsPb = Tp @("lane-start", "--title", "LEASHB", "--project", $tp.B, "--child", $fake)
    if ($lsPb -notmatch 'lane (\d+)') { throw "lane-start in B failed: $lsPb" }
    $pbLane = $Matches[1]
    $cfgA = (TpRows "SELECT detail FROM events WHERE kind='lane_config' AND lane_id=$paLane").Trim()
    $cfgB = (TpRows "SELECT detail FROM events WHERE kind='lane_config' AND lane_id=$pbLane").Trim()
    Check 'a_lanes_permission_mode_comes_from_its_own_project' `
        ($cfgB -match 'permissionMode=acceptEdits') "B: $cfgB"
    Check 'a_lane_does_not_inherit_another_projects_permission_mode' `
        ($cfgA -match 'permissionMode=bypassPermissions' -and $cfgA -notmatch 'acceptEdits') "A: $cfgA"
    Check 'two_lanes_in_one_workspace_are_configured_by_different_projects' `
        ($cfgA.Length -gt 0 -and $cfgB.Length -gt 0 -and $cfgA -ne $cfgB) "A: $cfgA  B: $cfgB"

    # ---- P2.5 / trap T6: the claim gate resolves a write in the ticket's OWN project ----
    # The two bases were the worktree and THE FIRST PROJECT, so a write anywhere in a second
    # project resolved to neither and the gate denied it -- while the agent was writing inside a
    # repository this workspace owns and its ticket claims. Broken before this phase; Phase 2 is
    # what makes it normal, so the latent hole starts firing. Ticket 1 lives in project B and
    # claims subtree:<bLeaf>/src.
    $ccIn = Tp @("claim-check", "1", (Join-Path $tp.B 'src\main.cs'))
    Check 'claim_check_covers_a_write_in_the_tickets_own_project' `
        ((($ccIn -replace '\s+', ' ') -match "covered: $([regex]::Escape($tp.BLeaf))/src/main\.cs")) `
        ($ccIn -replace '\s+', ' ')
    # ...and still denies one no project owns. A base list that widened until everything resolved
    # would pass the check above and be a hole, so this is the other half of the same assertion.
    $ccOut = Tp @("claim-check", "1", (Join-Path $outsider 'src\x.cs'))
    Check 'claim_check_still_denies_a_write_no_project_owns' `
        ($DODONA_EXIT -eq 1 -and (($ccOut -replace '\s+', ' ') -match 'outside the worktree and every project')) `
        "exit=$DODONA_EXIT out=$($ccOut -replace '\s+', ' ')"

    # ---- P2.4 / trap T5: repo-status and repo-init act on the project they were given ----
    $rsB = Tp @("repo-status", "--project", $tp.B) | ConvertFrom-Json
    Check 'repo_status_reports_the_project_it_was_given' ($rsB.root -eq $tp.B) "root='$($rsB.root)' want='$($tp.B)'"

    # repo-init needs a project that is NOT a repo, so attach a third. A bare folder is exempt
    # from repo exclusivity (no merge token exists to split), which is why this is attachable.
    $projC = Join-Path (Use-SuiteTemp) ("dodona-projc-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$projC\src" | Out-Null
    Set-Content "$projC\src\x.cs" "// project c"
    DodonaBare @("workspace-attach", "--member", $projC, "--workspace", $tp.Id) | Out-Null
    $ri = Tp @("repo-init", "--project", $projC, "--adopt")
    Check 'repo_init_initialises_the_project_it_was_given' `
        ((($ri -replace '\s+', ' ') -match [regex]::Escape($projC)) -and (Test-Path "$projC\.git")) `
        ($ri -replace '\s+', ' ')
    # ...AND SAYS NOTHING ABOUT ANOTHER ONE. This is the harm signature, not a tidiness check:
    # against the unfixed build the command was aimed at the FIRST project regardless, which is
    # already a repository with commits, so the red printed `error: <project A> is already a git
    # repository with commits` -- project A's path, in an answer about project C, to an agent
    # that has never seen project A.
    Check 'repo_init_does_not_answer_about_another_project' `
        (($ri -replace '\s+', ' ') -notmatch [regex]::Escape($tp.A)) ($ri -replace '\s+', ' ')

    # ---- P2.6 / trap T4: a detached project does not leave live lanes behind ----
    # `workspace-detach` and `workspace-move` are registry edits made in the CLI and they touched
    # no lane row at all, while respawn's only test was Directory.Exists -- which PASSES, because
    # the folder is still there; it just belongs to another workspace now. So an ungated agent
    # (T7) kept working in someone else's repository, and a respawn would have started a fresh
    # one there.
    $lsC = Tp @("lane-start", "--title", "GAMMA", "--project", $projC, "--child", $fake)
    if ($lsC -notmatch 'lane (\d+)') { throw "lane-start in project C failed: $lsC" }
    $cLane = $Matches[1]
    Tp @("say", "$cLane", "say gamma up") | Out-Null
    Wait-Until { (Tp @("tail", "$cLane", "10")) -match 'gamma up' } 20000 'the project-C lane answers' | Out-Null
    $cShimPid = [int]((Get-Content "$($tp.Dir)\shim-lane$cLane.json" -Raw | ConvertFrom-Json).shimPid)
    DodonaBare @("workspace-detach", "--member", $projC, "--workspace", $tp.Id) | Out-Null
    # PROCESSES, NOT PIPES. A lane pipe blinks out of the namespace for milliseconds while its
    # shim swaps server instances (8 of 192 reads over 1.5 s), so polling for a pipe's absence
    # eventually catches the gap and calls a live agent stopped -- a false green
    # (.claude/skills/check-authoring 2). A pid does not blink.
    Wait-Until { -not (Get-Process -Id $cShimPid -ErrorAction SilentlyContinue) } 20000 'the detached project''s shim exits' | Out-Null
    Check 'detaching_a_project_stops_the_lanes_that_were_in_it' `
        (-not (Get-Process -Id $cShimPid -ErrorAction SilentlyContinue)) "shim pid $cShimPid is still alive"
    Check 'a_detached_projects_lane_records_why_it_stopped' `
        ((TpRows "SELECT detail FROM events WHERE kind='lane_project_detached' AND lane_id=$cLane").Trim() -match 'project=') `
        (TpRows "SELECT detail FROM events WHERE kind='lane_project_detached' AND lane_id=$cLane")
    # The lane ROW survives -- nothing here deletes a transcript (§12) -- but it must not be
    # respawned back into a folder this workspace no longer owns.
    $rr = Tp @("lane-respawn", "$cLane")
    Check 'a_lane_is_not_respawned_into_a_project_that_left' `
        ($DODONA_EXIT -eq 1 -and (($rr -replace '\s+', ' ') -match 'belongs to no project of workspace')) `
        "exit=$DODONA_EXIT out=$($rr -replace '\s+', ' ')"
    # And the refusal names something that un-sticks it, rather than parking (CLAUDE.md 0.1).
    $rh = Tp @("lane-respawn", "$cLane", "--project", $tp.B)
    Check 'a_stranded_lane_can_be_re_homed_to_a_project_that_is_still_here' `
        (($rh -match "lane $cLane") -and (TpRows "SELECT cwd FROM lanes WHERE id=$cLane").Trim() -eq $tp.B) `
        "out=$($rh -replace '\s+', ' ') cwd='$((TpRows "SELECT cwd FROM lanes WHERE id=$cLane").Trim())'"


    # =====================================================================================
    # PHASE 3: WHICH PROJECT A TYPED SENTENCE MEANS (docs/LOCATIONS-PLAN.md Phase 3)
    #
    # Phase 2 gave `lane-start` a `--project` and refused anything a project does not own.
    # It deliberately left ONE site passing the first project: the typed-input path, because
    # choosing a project from a sentence is a ladder rather than an argument. This is that
    # ladder, and these are its rungs at the spawn site.
    #
    #   only    one project              free, and byte-for-byte the old answer
    #   named   the sentence says where  code: leaf, taught handle, or the leaf said as words
    #   live    a project has a lane in  code when exactly one does; the cheap tier when several
    #   ask     nothing to go on         HOLD the sentence -- no lane, nothing delivered
    #
    # ---- FIRST, THE PROPERTY THE WHOLE PLAN RESTS ON: ONE PROJECT WRITES NOTHING NEW -----
    # Phase 3 left this undone and said so: the one-project workspace is identical BY
    # CONSTRUCTION (`ProjectLadder.Decide` answers `only` before the liveness read, the registry
    # read and any model, and `project_chosen` is not written) and identical BY THE ELEVEN SUITES
    # that run one-project workspaces staying green -- but NOTHING COUNTED THE EVENTS, so a future
    # rung inserted AHEAD of the `only` short-circuit would not be caught by name. The operator's
    # own machine is a one-project workspace; this is the check for it.
    #
    # A WHITELIST, NOT A COUNT, and that is the point: any event kind this path did not write
    # before -- including one that does not exist yet -- appears in the detail string by name. A
    # plain total would go red for a reason nobody could read.
    #
    # THE WINDOW IS DETERMINISTIC ON PURPOSE. `brain=false` and `compressors=0`, so nothing
    # fires-and-forgets into the events table behind the input (BrainReview would spawn a
    # manager whose own events land whenever they land), and DODONA_NO_AUTOSTART means no
    # warm-up. No live lanes either, so the input takes RouteInput's first-sentence path --
    # code only, no classifier, nothing to time out.
    #
    # WHAT THIS DELIBERATELY DOES NOT SEE: `ProjectPaths()` is one registry read per typed
    # sentence that a one-project workspace did not pay before this phase. It writes no event and
    # prints nothing (it degrades to the first project if the registry will not open), so it is
    # invisible here and stays recorded in the code comment that admits it, rather than being
    # glossed as byte-for-byte.
    $oneProj = Join-Path (Use-SuiteTemp) ("dodona-one-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$oneProj\src" | Out-Null
    Set-Content "$oneProj\src\only.cs" "// the only project"
    Set-Content "$oneProj\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0; brain = $false } | ConvertTo-Json)
    DodonaBare @("workspace-create", "--name", "onlyone", "--member", $oneProj) | Out-Null
    # ConvertFrom-Json emits a JSON ARRAY as ONE pipeline item in PS 5.1, so it lands in a
    # variable before anything filters it (CLAUDE.md 0.2).
    $oneAll = (DodonaBare @("workspaces", "--json")) | ConvertFrom-Json
    $oneId = (@($oneAll) | Where-Object { $_.name -eq 'onlyone' } | Select-Object -First 1).id
    $oneW = (& $dodona where --workspace $oneId --json) | Out-String | ConvertFrom-Json
    StartDaemonFor $oneId | Out-Null
    function One([string[]]$a) { DodonaBare ($a + @('--workspace', $oneId)) }
    function OneRows([string]$sql) { Invoke-StoreSql $oneW.store $sql }
    # THE BASELINE MUST NOT BE TAKEN WHILE THE DAEMON IS STILL WRITING ITS STARTUP ROWS, and it
    # was. Measured 2026-08-21 on an IDLE machine at 0974e53: this check went red with
    # `daemon_start, reconcile_done, repo_path_unresolved, lane_project_unresolved,
    # lane_projects_stamped` all landing after the baseline -- the daemon's own startup, sampled
    # as though a typed sentence had written it. `StartDaemonFor` waits for the ctl pipe, which
    # the daemon opens AFTER `reconcile_done` (Daemon.cs:958 vs :912), so ordinarily that is
    # enough; but `Wait-Until` returns $false on timeout rather than throwing, so a slow start
    # silently drops through here and the next read is early.
    #
    # Waiting on the LAST startup row is the fix, not widening $oneAllowed below -- that list is a
    # statement about the operator's machine and its comment says so.
    $oneStarted = Wait-Until { ([int](OneRows "SELECT COUNT(*) FROM events WHERE kind='reconcile_done'").Trim()) -ge 1 } `
        30000 'the one-project daemon to finish starting before the baseline is taken'
    $oneMax = [int](OneRows "SELECT COALESCE(MAX(id),0) FROM events").Trim()
    One @("input", "make the header quite a lot taller") | Out-Null
    Wait-Until { ([int](OneRows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim()) -eq 1 } `
        30000 'the one-project workspace opens its lane' | Out-Null
    # Wait for the TURN TO FINISH, not just for the lane row: reading the event kinds while the
    # turn is still in flight would measure a moment earlier than the one this check is about.
    # The store, not `tail`, because this is a claim about rows and `tail` renders them.
    Wait-Until { ([int](OneRows "SELECT COUNT(*) FROM pane_events WHERE kind='result'").Trim()) -ge 1 } `
        30000 'the only project''s lane finishes its turn' | Out-Null
    $oneKinds = @((OneRows "SELECT DISTINCT kind FROM events WHERE id > $oneMax ORDER BY kind") -split "`r?`n" |
                  ForEach-Object { $_.Trim() } | Where-Object { $_ })
    # Every kind a one-project typed sentence wrote before the projects work existed: a lane being
    # born (`shim_spawned`, `lane_started`, `lane_connected`, `lane_config`), the policy that chose
    # its model, the fact that it was auto-created, and the sentence being said to it. Not one of
    # them is about WHICH project, because with one project there is nothing to be about.
    #
    # ADDING TO THIS LIST IS A DECISION ABOUT THE OPERATOR'S OWN MACHINE, not a test fix. If a rung
    # ever lands ahead of the `only` short-circuit, its event shows up in the detail below by name
    # and this line is where somebody has to justify it.
    $oneAllowed = @('lane_auto_created', 'lane_config', 'lane_connected', 'lane_started',
                    'policy_choice', 'say', 'shim_spawned')
    $oneUnexpected = @($oneKinds | Where-Object { $oneAllowed -notcontains $_ })
    # `-ge 1` FIRST, deliberately: an empty set contains no unexpected kind either, so without it
    # this passes against a window that never opened -- the vacuous shape `dev prove` exists for.
    # `daemon-started=` is in the detail because it is the first thing to read when this goes red:
    # False means the baseline was taken early and the kinds below are a startup, not a sentence.
    Check 'a_one_project_workspace_writes_no_project_ladder_event' `
        ($oneKinds.Count -ge 1 -and $oneUnexpected.Count -eq 0) `
        "daemon-started=$oneStarted kinds=[$($oneKinds -join ',')] outside the allowed set=[$($oneUnexpected -join ',')]"
    # ...and it asks nothing. One project is one answer, so there is no question to open -- the
    # `questions` table must be untouched, which is also what `ui-use`'s
    # `a_one_project_workspace_is_never_asked_anything` asserts from the window's side.
    Check 'a_one_project_workspace_opens_no_question' `
        ((OneRows "SELECT COUNT(*) FROM questions").Trim() -eq '0') `
        ((OneRows "SELECT id, kind, state, subject FROM questions") -replace '\s+', ' ')
    Stop-WorkspaceShims $oneW.dir
    StopDaemonFor $oneId

    # A FRESH WORKSPACE, not the one above, and that is deliberate. The Phase 2 section leaves
    # live lanes in both projects and a detached third, so `only`, `named` and the single-live
    # rung could not be told apart in it -- and a rung test that cannot fail for the right
    # reason is the class of check this repo keeps paying for.
    $p3 = New-TwoProjectWorkspace $dodona 'ladder'
    # `agent` in the FIRST project's dodona.json, because Config is loaded once from _primary
    # and the operator-path check below restarts this daemon with autostart ON. Without it the
    # warm-up would start real `claude -p --model haiku` processes from an acceptance suite
    # (CLAUDE.md 0.1: quota is the scarce resource; 3.2: a wake costs four of them).
    Set-Content "$($p3.A)\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
    StartDaemonFor $p3.Id | Out-Null
    function P3([string[]]$a) { DodonaBare ($a + @('--workspace', $p3.Id)) }
    function P3Rows([string]$sql) { Invoke-StoreSql $p3.Store $sql }
    function P3Work() { [int](P3Rows "SELECT COUNT(*) FROM lanes WHERE role='work'").Trim() }
    function P3Classified() { [int](P3Rows "SELECT COUNT(*) FROM events WHERE kind='classified_project'").Trim() }
    function P3Chosen() { (P3Rows "SELECT detail FROM events WHERE kind='project_chosen' ORDER BY id DESC LIMIT 1").Trim() }
    function P3NewestWorkLane() { (P3Rows "SELECT id FROM lanes WHERE role='work' ORDER BY id DESC LIMIT 1").Trim() }
    # A LANE ID IS NOT ALWAYS THERE, AND A MISSING ONE USED TO TAKE THE WHOLE SUITE DOWN.
    # An empty `$lane` makes the SQL `... WHERE id=`, python raises OperationalError("incomplete
    # input"), `Invoke-StoreSql` throws, and the throw tears straight out of the try block: NO
    # TALLY LINE -- which `dev.ps1` counts as a failed suite and which reports nothing about the
    # checks that did run -- and **24 shims left alive** for the wrapper to reap, because
    # `Stop-WorkspaceShims` is in the part of the script that never ran. Measured in a full `dev
    # gate` wave on 2026-08-21, from a rung whose lane had not appeared yet.
    #
    # Six call sites hand this the raw result of `P3NewestWorkLane`; exactly one of them guarded
    # it, with `-1`. A guard five call sites can forget is not a guard, so it lives here, and it
    # returns empty rather than throwing: the check that asked then fails on its own terms and
    # prints what it actually saw, which is a better diagnosis than a stack trace (0.1, and the
    # same reason `Wait-Until` returns $false instead of throwing).
    function P3Cwd([string]$lane) {
        if ($lane -notmatch '^-?\d+$') { return '' }
        (P3Rows "SELECT cwd FROM lanes WHERE id=$lane").Trim()
    }
    P3 @("router-start", "--child", $fake) | Out-Null
    Wait-Until { (P3Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim() -eq '1' } `
        25000 'the ladder workspace has a warm classifier' | Out-Null

    # ---- rung 4: NOTHING TO GO ON, SO HOLD. Two projects, no live lane, no name in the ----
    # sentence. Before this phase the answer was "the first project, instantly and silently",
    # which is an agent editing a repository nobody pointed it at -- and unlike a wrong LANE
    # that is not undone by one `lane-stop`, because the agent has already read the wrong tree.
    # So the sentence is held, exactly as the lane ladder's own top rung holds one.
    $held4Before = P3Work
    $held4 = P3 @("input", "make the header quite a lot taller")
    Check 'a_typed_sentence_with_no_project_to_infer_is_held' `
        ((($held4 -replace '\s+', ' ') -match 'held: not sure which project')) ($held4 -replace '\s+', ' ')
    # THE NEGATIVE HALF, and the one that matters: holding must invent nothing. A rung that
    # asked and spawned anyway would look identical in the reply text.
    Check 'a_held_sentence_invents_no_lane' ((P3Work) -eq $held4Before) "before=$held4Before after=$(P3Work)"
    Check 'the_project_hold_is_recorded_as_asked' `
        ((P3Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 1") -match 'ask\|no-project') `
        (P3Rows "SELECT tier, confidence FROM routing_decisions ORDER BY id DESC LIMIT 1")
    # And it names what un-sticks it (CLAUDE.md 0.1: a wait names a condition, never a person).
    $held4Ev = (P3Rows "SELECT detail FROM events WHERE kind='project_unknown' ORDER BY id DESC LIMIT 1").Trim()
    Check 'the_project_hold_offers_every_project_it_knows' `
        (($held4Ev -match [regex]::Escape($p3.ALeaf)) -and ($held4Ev -match [regex]::Escape($p3.BLeaf))) $held4Ev
    # THE ANNOUNCEMENT LAGS THE EVENT ABOVE, so this waits for it and then asserts on ONE
    # CAPTURED VALUE. It used to run the same `ORDER BY id DESC LIMIT 1` twice -- once for the
    # condition and once for the detail -- which is two reads of a table that is still moving:
    # the condition saw the PREVIOUS rung's hold (still the newest row matching `%which
    # project%`, and it does not mention `lane-start`), and the detail printed the row that had
    # landed in the milliseconds between them.
    #
    # Seen once in a full `dev gate`, 2026-08-21, while the product was answering correctly --
    # and it is the worst shape a red can have: a FAILURE WHOSE OWN DETAIL CONTAINS THE STRING
    # IT SAYS IS MISSING. That sends the next reader hunting through the product for a bug that
    # is in the check, which is CLAUDE.md 0.2's false-red trap arriving from a new direction and
    # costs exactly as much as a false green. Never assert on a query and then print a second
    # one: capture, then assert and report the same value.
    $held4Say = ''
    Wait-Until {
        $script:held4Say = (P3Rows "SELECT body FROM pane_events WHERE body LIKE '%which project%' ORDER BY id DESC LIMIT 1")
        $script:held4Say -match 'lane-start'
    } 20000 'the project hold reaching the pane' | Out-Null
    Check 'the_project_hold_says_how_to_answer_it' ($held4Say -match 'lane-start') ($held4Say -replace '\s+', ' ')

    # ---- P3.A: THE `ask` RUNG NOW ASKS SOMEBODY ------------------------------------------
    # THE GAP THIS CLOSES. Phase 3 built this rung and Phase 4 built the overlay that renders a
    # `questions` row, and NOTHING CONNECTED THEM for two days: the hold wrote a
    # `routing_decisions` row at tier `ask`, a `project_unknown` event and an announcement, and
    # the operator's window never showed a routing question at all. Every check above this line
    # passed the whole time. "Ask" asked nobody.
    #
    # THE ROW IS IN THE WORKSPACE STORE, and that is D-L11 rather than convenience: a workspace
    # daemon may never read the concierge's store (§2), and every suite -- plus any machine whose
    # concierge is asleep -- runs daemons without a concierge at all. Scope is WHICH STORE the row
    # is in, which is why no scope column was needed anywhere. The query below is against this
    # workspace's own store, so it is also the assertion that no concierge was involved.
    #
    # The `questions` table has existed since Phase 4, so this can fail against HEAD without
    # taking the suite down with it -- unlike a check naming a column a migration adds, which
    # `Invoke-StoreSql` correctly turns into a throw and `dev prove` then reports as MISSING.
    Wait-Until { ([int](P3Rows "SELECT COUNT(*) FROM questions WHERE kind='route' AND state='open'").Trim()) -ge 1 } `
        20000 'the hold opens a route question' | Out-Null
    $q4 = (P3Rows "SELECT id FROM questions WHERE kind='route' AND state='open' ORDER BY id LIMIT 1").Trim()
    Check 'the_project_hold_opens_a_question_row' ($q4 -match '^\d+$') `
        "questions=$((P3Rows 'SELECT id, kind, state, subject FROM questions') -replace '\s+', ' ')"
    # '-1' when there is no row, so every query below stays syntactically valid: a missing id must
    # make the checks that follow FAIL, never make `Invoke-StoreSql` throw and report them MISSING.
    $q4id = if ($q4 -match '^\d+$') { $q4 } else { '-1' }
    # THE HELD SENTENCE, VERBATIM. `subject` is the only place it exists between the hold and the
    # answer, and answering DELIVERS it -- so a truncated or reworded subject would silently
    # deliver something the operator never typed. The rendered `input` column is the one that
    # gets shortened, because that one is read rather than replayed.
    Check 'the_question_carries_the_held_sentence_whole' `
        ((P3Rows "SELECT subject FROM questions WHERE id=$q4id").Trim() -eq 'make the header quite a lot taller') `
        ((P3Rows "SELECT input, subject FROM questions WHERE id=$q4id") -replace '\s+', ' ')
    # NAMES, NOT PATHS (CLAUDE.md §3.1, operator directive: no folder UI, ever). A routing question
    # names projects; it does not offer somewhere to navigate. `ui-use`'s
    # `the_ask_offers_no_filesystem_navigation` asserts the same property on the rendered choices;
    # this asserts it where the daemon writes it, which is the only place it can be guaranteed.
    $q4blob = (P3Rows "SELECT candidates FROM questions WHERE id=$q4id").Trim()
    $q4parsed = $null
    try { $q4parsed = $q4blob | ConvertFrom-Json } catch { }
    $q4vals = @(@($q4parsed).id | Where-Object { $_ })
    Check 'the_question_offers_every_project_by_name' `
        (($q4vals -contains $p3.ALeaf) -and ($q4vals -contains $p3.BLeaf)) "ids=[$($q4vals -join ',')] blob=$q4blob"
    Check 'the_question_offers_no_filesystem_navigation' `
        ($q4vals.Count -ge 2 -and @($q4vals | Where-Object { $_ -match '[\\/]' -or $_ -match '^[A-Za-z]:' }).Count -eq 0) `
        "ids=[$($q4vals -join ',')]"
    # AND STILL NO LANE. The question exists; the work does not. Answering is what creates it
    # (asserted at the end of this section), which is what keeps `held_input_invents_no_lane`'s
    # guarantee true one level down -- an ask that pre-created a lane "ready to receive" would
    # have put an agent in a folder nobody chose, which is the whole error this rung avoids.
    Check 'opening_a_question_still_invents_no_lane' ((P3Work) -eq $held4Before) "before=$held4Before after=$(P3Work)"
    # A near-miss answer is REFUSED and the question STAYS OPEN. Asking exists because guessing was
    # wrong, so the one moment the operator actually told us the truth is the worst possible moment
    # to start inferring -- and a refusal that closed the row would lose the held sentence for good,
    # because `QuestionAnswer` is guarded on `state='open'` and there is no re-opening it.
    $bogus4 = P3 @("answer", $q4id, "atlantis")
    Check 'a_route_answer_naming_nothing_offered_is_refused' `
        ((($bogus4 -replace '\s+', ' ') -match 'not one of the answers') -and
         (P3Rows "SELECT state FROM questions WHERE id=$q4id").Trim() -eq 'open') `
        "out=$($bogus4 -replace '\s+', ' ') state='$((P3Rows "SELECT state FROM questions WHERE id=$q4id").Trim())'"

    # ---- rung 3: THE SENTENCE NAMES A PROJECT. Code, free, and no model is asked ----------
    P3 @("input", "tidy up the changelog in $($p3.BLeaf)") | Out-Null
    Wait-Until { ((P3Work) -eq $held4Before + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'the named project gets a lane, and its cwd lands' | Out-Null
    $nLane = P3NewestWorkLane
    Check 'a_typed_sentence_naming_a_project_opens_a_lane_there' `
        ((P3Cwd $nLane) -eq $p3.B) "cwd='$(P3Cwd $nLane)' want='$($p3.B)'"
    Check 'the_named_rung_records_which_evidence_answered' (($nLane) -and (P3Chosen) -match 'rung=named how=leaf') (P3Chosen)
    # FREE MEANS FREE. `classified_project` is written from inside ClassifyProjectAsync and
    # nowhere else, so this is the check that notices if a name ever starts costing a call.
    #
    # A REGRESSION CHECK, AND `dev prove` SAYS VACUOUS FOR IT BY CONSTRUCTION -- reported as such
    # rather than dressed up as proof: HEAD writes no `classified_project` row at all, so "the
    # count is zero" cannot fail against it. It is kept for the same reason
    # `an_explicit_root_beats_the_inherited_env` is (Phase 0c): it pins a property no code state
    # can currently redden, so a later change that starts spending a model call on a rung the
    # operator was promised for free cannot pass quietly. Its provable sibling is
    # `a_named_project_is_not_overruled_by_a_busy_one`, which asserts the same freeness alongside
    # a destination HEAD gets wrong.
    Check 'naming_a_project_costs_no_model' ((P3Classified) -eq 0) "classified_project events=$(P3Classified)"

    # ---- rung 2, the free half: ONE project holds a live lane, so there is nothing to ----
    # choose between and no model is asked. This is the operator's rung 2 in its common shape.
    $soleBefore = P3Work
    P3 @("input", "routekind:new-task shorten the footer as well") | Out-Null
    Wait-Until { ((P3Work) -eq $soleBefore + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'the new task joins the live project, and its cwd lands' | Out-Null
    $sLane = P3NewestWorkLane
    Check 'a_new_task_joins_the_only_project_with_a_live_lane' `
        ((P3Cwd $sLane) -eq $p3.B) "cwd='$(P3Cwd $sLane)' want='$($p3.B)'"
    Check 'the_live_rung_records_that_it_needed_no_model' ((P3Chosen) -match 'rung=live how=sole-live') (P3Chosen)
    Check 'one_live_project_costs_no_model_either' ((P3Classified) -eq 0) "classified_project events=$(P3Classified)"

    # ---- rung 2 proper: SEVERAL projects are live, so the cheap tier chooses --------------
    # routeproject:N is an INDEX, not a name, and that is the same lesson `cxpick:N` carries in
    # the concierge suite: a project NAME written into the sentence is matched IN CODE by the
    # `named` rung before any model is asked, so this check written with a name would pass at
    # rung 3 having never reached the tier -- proving the opposite of what it claims.
    P3 @("lane-start", "--title", "ALPHAWORK", "--project", $p3.A, "--child", $fake) | Out-Null
    Wait-Until { (P3Rows "SELECT COUNT(*) FROM lanes WHERE title='ALPHAWORK' AND state='alive'").Trim() -eq '1' } `
        25000 'a lane is live in the first project too' | Out-Null
    #
    # AND IT MUST NAME THE SECOND PROJECT (index 2), not the first. `dev prove` said VACUOUS for
    # the first draft of this check, which asserted project A: A is `_primary`, so it is what the
    # OLD code answered for every typed sentence -- an assertion no build can fail. Every check in
    # this section that names a project therefore names B. Worth knowing before writing another.
    $classBefore = P3Work
    P3 @("input", "routekind:new-task routeproject:2 add the missing footnote") | Out-Null
    Wait-Until { ((P3Work) -eq $classBefore + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'the cheap tier chooses a project and a lane opens there, and its cwd lands' | Out-Null
    $cLane2 = P3NewestWorkLane
    Check 'several_live_projects_reach_the_cheap_tier' ((P3Classified) -ge 1) "classified_project events=$(P3Classified)"
    # The lane COUNT is in the assertion, not only in the wait: a classifier that answered `none`
    # holds the sentence, and `$cLane2` would then be the PREVIOUS lane -- which is also in B, so
    # a cwd-only check would go green on the held case.
    Check 'the_lane_opens_in_the_project_the_classifier_chose' `
        (((P3Work) -eq $classBefore + 1) -and ((P3Cwd $cLane2) -eq $p3.B)) `
        "lanes=$classBefore->$(P3Work) cwd='$(P3Cwd $cLane2)' want='$($p3.B)'"
    Check 'the_classified_rung_records_that_a_model_answered' ((P3Chosen) -match 'rung=live how=classified') (P3Chosen)

    # ...and a classifier that will not choose HOLDS, rather than falling back to the first
    # project. This is the fallback that would be invisible: it compiles, it never errors, and
    # it is wrong in exactly the case nobody watches.
    $unsureBefore = P3Work
    $unsure = P3 @("input", "routekind:new-task and now for something completely different")
    Check 'a_classifier_that_will_not_choose_holds_the_sentence' `
        ((($unsure -replace '\s+', ' ') -match 'held: not sure which project')) ($unsure -replace '\s+', ' ')
    Check 'an_unchosen_project_invents_no_lane' ((P3Work) -eq $unsureBefore) "before=$unsureBefore after=$(P3Work)"
    Check 'a_classifier_that_would_not_choose_says_so_in_the_chain' `
        ((P3Rows "SELECT detail FROM events WHERE kind='project_unclassified' ORDER BY id DESC LIMIT 1") -match 'would not choose') `
        (P3Rows "SELECT detail FROM events WHERE kind='project_unclassified' ORDER BY id DESC LIMIT 1")

    # ---- THE MEMORY (D-L5): a spoken handle per project, in `aliases`, not a new table ----
    # `members` was already every project ever attached; what was missing is what the operator
    # CALLS one. So `aliases` grew one nullable `member_key` (registry schema 2) instead of a
    # parallel `places` table -- fewer owned things is this project's whole failure mode.
    # Taught for the SECOND project, for the reason recorded above: A is `_primary`, so a check
    # asserting A cannot fail against a build that always answered A.
    $pa = DodonaBare @("project-alias", "lantern", "--member", $p3.B, "--workspace", $p3.Id)
    Check 'a_project_can_be_taught_a_spoken_handle' ($pa -match 'project aliased') $pa
    $regDb = (DodonaBare @("where", "--workspace", $p3.Id, "--json") | ConvertFrom-Json).registry
    # PRAGMA-GUARDED, and that is not defensive padding -- `Invoke-StoreSql` THROWS on a sqlite
    # error, so naming a column that a pre-schema-2 registry does not have killed the whole
    # suite mid-run: `dev prove` reported all twelve of this section's checks as MISSING and no
    # tally line at all, which reads as "the suite crashed" rather than "the schema is not there
    # yet". A check must be able to FAIL against HEAD; it must not be able to take the suite
    # down with it.
    $aliasCols = (Invoke-StoreSql $regDb "SELECT name FROM pragma_table_info('aliases')").Trim()
    $aliasRow = if ($aliasCols -match 'member_key') {
        (Invoke-StoreSql $regDb "SELECT alias, member_key FROM aliases WHERE alias='lantern'").Trim()
    } else { "(aliases has no member_key column: registry schema is pre-2) cols=$($aliasCols -replace '\s+', ',')" }
    Check 'a_project_handle_is_stored_against_the_project_not_only_the_workspace' `
        ($aliasRow -match [regex]::Escape($p3.B.ToLowerInvariant())) "row='$aliasRow' B='$($p3.B.ToLowerInvariant())'"
    # THE POINT OF THE MEMORY: the handle now answers for free, and it answers even though
    # BOTH projects hold live lanes -- a name beats the classifier, the same rule the concierge
    # applies one level up (explicit information never triggers a search).
    $taughtBefore = P3Work
    $taughtClassified = P3Classified
    P3 @("input", "the lantern needs a new bulb") | Out-Null
    Wait-Until { ((P3Work) -eq $taughtBefore + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'the taught handle opens a lane in its project, and its cwd lands' | Out-Null
    $tLane = P3NewestWorkLane
    Check 'a_taught_handle_opens_a_lane_in_its_project' `
        (((P3Work) -eq $taughtBefore + 1) -and ((P3Cwd $tLane) -eq $p3.B)) `
        "lanes=$taughtBefore->$(P3Work) cwd='$(P3Cwd $tLane)' want='$($p3.B)'"
    Check 'the_alias_rung_records_that_evidence' ((P3Chosen) -match 'rung=named how=alias') (P3Chosen)
    # BOTH halves, because the free-ness alone is unprovable: HEAD writes no `classified_project`
    # row at all, so "the count did not move" passes against it having tested nothing (dev prove
    # said VACUOUS). The claim is one thing -- a name wins over a busy project, AND costs nothing
    # -- so it is one check over both facts.
    Check 'a_named_project_is_not_overruled_by_a_busy_one' `
        (((P3Cwd $tLane) -eq $p3.B) -and ((P3Classified) -eq $taughtClassified)) `
        "cwd='$(P3Cwd $tLane)' want='$($p3.B)' classified_project went $taughtClassified -> $(P3Classified)"
    # A handle for a folder no project owns is refused, not remembered. An alias pointing at a
    # place no lane may open is a memory of somewhere the spawn site will refuse later, and
    # "later" here means after a sentence has already resolved to it.
    $badAlias = DodonaBare @("project-alias", "nowhere", "--member", (Join-Path $p3.A 'src'), "--workspace", $p3.Id)
    Check 'a_handle_for_a_folder_that_is_not_a_project_is_refused' `
        (($badAlias -replace '\s+', ' ') -match 'is not a project of') ($badAlias -replace '\s+', ' ')

    # ---- THE OPERATOR'S OWN PATH: autostart ON, nothing pre-built by this test -----------
    # The rule this exists for cost two days (CLAUDE.md 3): the routing ladder was fully
    # covered and fully green while being DEAD in production, because the suite stood up its
    # own classifier by hand and the real daemon never created one. Everything above this line
    # ran against a router THIS TEST started with `router-start --child`. So: stop the
    # classifier, stop the daemon, clear DODONA_NO_AUTOSTART, and let the daemon build its own
    # warm-up -- then type a sentence and demand the project ladder actually decided.
    $p3Router = (P3Rows "SELECT id FROM lanes WHERE role='router' AND state='alive' ORDER BY id DESC LIMIT 1").Trim()
    if ($p3Router) { P3 @("lane-stop", $p3Router) | Out-Null }
    StopDaemonFor $p3.Id
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue    # as the operator has it
    $p3auto = Start-Process $dodona -ArgumentList "daemon", "--workspace", $p3.Id -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon-p3-auto.out" -RedirectStandardError "$out\daemon-p3-auto.err"
    [void]$extraDaemons.Add($p3auto)
    Wait-Daemon $p3.CtlPipe | Out-Null
    Wait-Until { (P3Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim() -eq '1' } `
        30000 'autostart builds its own classifier' | Out-Null
    Check 'autostart_builds_the_classifier_the_ladder_will_use' `
        ((P3Rows "SELECT COUNT(*) FROM lanes WHERE role='router' AND state='alive'").Trim() -eq '1') `
        (P3Rows "SELECT id, title, role, state FROM lanes WHERE role='router'")
    $opBefore = P3Work
    $opChosen = [int](P3Rows "SELECT COUNT(*) FROM events WHERE kind='project_chosen'").Trim()
    # The SECOND project again: naming the first would be an assertion no build can fail.
    P3 @("input", "routekind:new-task $($p3.BLeaf) needs a changelog entry of its own") | Out-Null
    Wait-Until { ((P3Work) -eq $opBefore + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'the operator-shaped sentence opens a lane, and its cwd lands' | Out-Null
    $opLane = P3NewestWorkLane
    Check 'the_project_ladder_is_live_on_the_path_the_operator_uses' `
        (((P3Work) -eq $opBefore + 1) -and ((P3Cwd $opLane) -eq $p3.B)) `
        "lanes=$opBefore->$(P3Work) cwd='$(P3Cwd $opLane)' want='$($p3.B)'"
    Check 'a_ladder_decision_is_recorded_on_that_path_too' `
        (([int](P3Rows "SELECT COUNT(*) FROM events WHERE kind='project_chosen'").Trim()) -gt $opChosen) `
        "project_chosen went $opChosen -> $([int](P3Rows "SELECT COUNT(*) FROM events WHERE kind='project_chosen'").Trim())"
    $env:DODONA_NO_AUTOSTART = "1"

    # ---- P3.A part 2: ANSWERING THE QUESTION IS WHAT CREATES THE LANE -------------------
    # Deliberately at the END of this section rather than beside the question it answers: this
    # ADDS a live lane, and the rungs above depend on which projects hold live lanes -- a check
    # that quietly changed the fixture for the eight checks after it would be the concierge
    # suite's `$names[1]` trap in a new place.
    #
    # `$q4` was opened by the rung-4 hold at the top of this section and has been open ever since,
    # which is itself the point: a question is a ROW, so it outlives the daemon restart the
    # operator-path block just did. A pending question that evaporated would make asking worse
    # than guessing.
    #
    # THE SECOND PROJECT, never the first. A is `_primary`, so "the lane landed in A" is an
    # assertion no build can fail -- the rule four VACUOUS verdicts taught this section.
    $ansBefore = P3Work
    $ans4 = P3 @("answer", $q4id, $p3.BLeaf)
    Wait-Until { ((P3Work) -eq $ansBefore + 1) -and ((P3Cwd (P3NewestWorkLane)) -eq $p3.B) } 30000 'answering the route question opens a lane, and its cwd lands' | Out-Null
    $aLane = P3NewestWorkLane
    $aLaneId = if ($aLane -match '^\d+$') { $aLane } else { '-1' }
    # THE LANE COUNT IS PART OF THE ASSERTION, not only of the wait: this section keeps choosing
    # B, so on a build that delivered nothing "the newest work lane" is the PREVIOUS one -- which
    # is also in B, and a cwd-only check would go green on the failure it exists to catch.
    Check 'answering_the_project_question_opens_the_lane_there' `
        (((P3Work) -eq $ansBefore + 1) -and ((P3Cwd $aLaneId) -eq $p3.B)) `
        "out=$($ans4 -replace '\s+', ' ') lanes=$ansBefore->$(P3Work) cwd='$(P3Cwd $aLaneId)' want='$($p3.B)'"
    # ...AND THE HELD SENTENCE ITSELF ARRIVES. A lane existing is not the claim: delivering the
    # words the operator typed twenty checks ago is, and `questions.subject` is the only place
    # they were kept. A route answer that opened an empty lane would pass the check above.
    Check 'the_held_sentence_itself_reaches_the_new_lane' `
        (([int](P3Rows "SELECT COUNT(*) FROM pane_events WHERE lane_id=$aLaneId AND kind='user_input' AND body LIKE '%quite a lot taller%'").Trim()) -ge 1) `
        ((P3Rows "SELECT kind, body FROM pane_events WHERE lane_id=$aLaneId") -replace '\s+', ' ')
    # Every rung that places a lane records which evidence decided. "The operator said so" is
    # evidence like any other, and without this row the ONE rung a person actually answered would
    # be the only rung with nothing saying why the lane is where it is.
    #
    # WAITED FOR, THEN CAPTURED ONCE. This asserted on `(P3Chosen)` and printed a SECOND
    # `(P3Chosen)` -- 84c0002's lesson, and it went red in a full wave on 2026-08-21 with a detail
    # that CONTAINED the string the condition had just failed to find. The row is written on the
    # daemon's side of the answer and the wait above is satisfied by the LANE appearing, so under
    # load the two queries straddled it: the condition read the previous rung's row, and the
    # detail -- one python process start later -- read the new one. A check whose FAIL text
    # disproves the FAIL is the worst kind of red there is.
    $chosen = ''
    Wait-Until {
        $script:chosen = P3Chosen
        $script:chosen -match 'rung=answered'
    } 20000 'the answered rung recording which evidence decided' | Out-Null
    Check 'the_answered_rung_records_that_the_operator_decided' `
        ($chosen -match 'rung=answered how=operator') $chosen
    # Two routing rows for one sentence, and both are true: it WAS asked about (tier `ask`, no
    # lane), and it WAS then delivered (tier `answered`, to the lane the answer created).
    # Captured once for the same reason, and with the columns the detail needs, so the assertion
    # and the report are one reading of one row.
    $lastRoute = (P3Rows "SELECT tier, confidence, delivered_lane FROM routing_decisions ORDER BY id DESC LIMIT 1").Trim()
    Check 'the_answered_delivery_joins_the_routing_chain' `
        ($lastRoute -match 'answered\|operator') $lastRoute
    $q4row = ((P3Rows "SELECT state, answer FROM questions WHERE id=$q4id") -replace '\s+', ' ').Trim()
    Check 'answering_closes_the_question_row' `
        ($q4row -match "answered\|$([regex]::Escape($p3.BLeaf))") $q4row

    Stop-WorkspaceShims $p3.Dir
    DodonaBare @("stop-daemon", "--workspace", $p3.Id) | Out-Null

    Stop-WorkspaceShims $tp.Dir
    DodonaBare @("stop-daemon", "--workspace", $tp.Id) | Out-Null

    # ---- forget removes the registry rows and keeps every transcript (§12) ----
    $forgotten = DodonaBare @("workspace-forget", "--workspace", $twin)
    # (moved to unit:Dodona.Tests.RegistryIdentityTests -- forget_removes_the_registry_row,
    #  forget_keeps_the_store_directory -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # ---- P2.7: FORGETTING A LIVE WORKSPACE MUST NOT LEAVE AGENTS BEHIND ------------------
    # Phase 2 wired `workspace-detach` and `workspace-move` to `project-gone` and DEFERRED this
    # one deliberately (LOCATIONS-PLAN P2.7, handed to Phase 5): `Registry.Forget` deletes every
    # `members` row in one transaction, so forgetting a live workspace stranded an agent in a
    # folder the registry no longer records -- and it orphans the DAEMON too, which is why it
    # belongs with Phase 5's reaping rather than bolted onto detach.
    #
    # An orphaned daemon is not merely untidy. `publish --all` resolves swap targets by id from
    # the registry, so a daemon whose workspace is forgotten can never be hot-swapped again: it
    # is an un-updatable process holding agents nothing lists. Stopping it is reversible -- the
    # store directory is kept (the check above), so re-creating the workspace over the same
    # folder wakes it with every transcript intact.
    $fgProj = Join-Path (Use-SuiteTemp) ("dodona-forget-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$fgProj\src" | Out-Null
    Set-Content "$fgProj\src\main.cs" "// forget me"
    # agent=$fake, or a lane in this workspace would be a real `claude -p` (CLAUDE.md 2.6).
    Set-Content "$fgProj\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
    DodonaBare @("workspace-create", "--name", "forgetme", "--member", $fgProj) | Out-Null
    $fgAll = (DodonaBare @("workspaces", "--json")) | ConvertFrom-Json
    $fgId = (@($fgAll) | Where-Object { $_.name -eq 'forgetme' } | Select-Object -First 1).id
    $fgW = (& $dodona where --workspace $fgId --json) | Out-String | ConvertFrom-Json
    $fgDaemon = StartDaemonFor $fgId
    $fgLs = DodonaBare @("lane-start", "--title", "DOOMED", "--child", $fake, "--workspace", $fgId)
    if ($fgLs -notmatch 'lane (\d+)') { throw "lane-start in the workspace about to be forgotten failed: $fgLs" }
    $fgLane = $Matches[1]
    DodonaBare @("say", "$fgLane", "say doomed up", "--workspace", $fgId) | Out-Null
    Wait-Until { (DodonaBare @("tail", "$fgLane", "10", "--workspace", $fgId)) -match 'doomed up' } 20000 'the doomed lane answers' | Out-Null
    $fgShimPid = [int]((Get-Content "$($fgW.dir)\shim-lane$fgLane.json" -Raw | ConvertFrom-Json).shimPid)
    $fgForget = DodonaBare @("workspace-forget", "--workspace", $fgId)
    # PROCESSES, NOT PIPES (.claude/skills/check-authoring 2). A lane pipe blinks out of the
    # namespace for milliseconds while its shim swaps server instances, so a pipe's absence
    # means "gone OR mid-reconnect" and a Wait-Until would eventually catch the gap and call a
    # live agent stopped. A pid does not blink.
    Wait-Until { -not (Get-Process -Id $fgShimPid -ErrorAction SilentlyContinue) } 25000 "the forgotten workspace's shim exits" | Out-Null
    Check 'forgetting_a_workspace_stops_its_agents' `
        (-not (Get-Process -Id $fgShimPid -ErrorAction SilentlyContinue)) `
        "shim pid $fgShimPid for lane $fgLane is still alive; forget said: $($fgForget -replace '\s+', ' ')"
    Wait-Until { $fgDaemon.HasExited } 25000 "the forgotten workspace's daemon exits" | Out-Null
    Check 'forgetting_a_workspace_stops_its_orphaned_daemon' `
        ($fgDaemon.HasExited) "daemon pid $($fgDaemon.Id) is still running for a workspace the registry no longer knows"
    # ...and nothing was deleted. Forget is an undo for a workspace made by accident and must
    # never be able to mean "delete six lanes of history" (§12).
    Check 'a_forgotten_workspaces_transcripts_survive' `
        ((Test-Path (Join-Path $dodonaHome "workspaces\$fgId")) -and
         (Invoke-StoreSql $fgW.store "SELECT COUNT(*) FROM lanes WHERE id=$fgLane").Trim() -eq '1') `
        "store=$(Test-Path (Join-Path $dodonaHome "workspaces\$fgId")) lane rows=$(Invoke-StoreSql $fgW.store "SELECT COUNT(*) FROM lanes WHERE id=$fgLane")"
}
finally {
    foreach ($p in @($extraDaemons)) { if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } } }
    # Scoped cleanup: only THIS suite's processes, resolved from the shim-info files of the
    # workspaces it made. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (CLAUDE.md 4 -- tests collide with nothing, including the instance the
    # operator is using right now).
    Get-ChildItem (Join-Path $dodonaHome 'workspaces') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ChildItem "$($_.FullName)\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
            $si = Get-Content $_.FullName | ConvertFrom-Json
            foreach ($pid2 in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $pid2 -Force -ErrorAction Stop } catch { } }
        }
    }
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived it.
    # It reports; it never kills -- a check that killed what it found would hide the leak it
    # exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- PROJECTS ACCEPTANCE (which project a lane opens in, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
