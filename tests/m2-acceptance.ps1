# M2 acceptance, model-free half: merge-time claim backstop (§6 layer 2), code-derived
# presence (§5), tier-0 prefix routing + focus routing with routing_decisions rows (§4).
# Fake agents only — zero model calls.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm2'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in
$out = Join-Path $PSScriptRoot 'm2-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-m2-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src\water", "$root\src\sky" | Out-Null
Set-Content "$root\src\water\sim.cs" "// water"
Set-Content "$root\src\sky\box.cs" "// sky"
Set-Content "$root\.gitignore" ".dodona/"
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $global:DODONA_EXIT = 0; $o = (& $dodona ($a + @('--root', $root))) | Out-String; $global:DODONA_EXIT = $LASTEXITCODE; $o.Trim() }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }

$daemon = $null
try {
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    # ---- what the branch touched is RECORDED, not judged (REVIEW-AND-MERGE-PLAN D-R5/D-R7) ----
    #
    # RE-AIMED, NOT DELETED. This block used to assert `backstop_refuses_outside_claim`: a branch
    # touching a path outside its declared claim could not get the merge token. That refusal is
    # retired -- it asked whether reality matched a prediction, and with the prediction gone the
    # question has no content. The operator's decision: two agents about to work on the same file
    # is often the case, and duplicated effort is the manager's to raise, not the store's to lock.
    #
    # It also BLOCKED the merge flow R1 introduced. The diff is taken from the merge base, so once
    # an agent has merged main into its own branch -- D-R3's path, and the only way a silent drop
    # can exist -- the base is main's tip and every file the branch touched reads as out-of-claim.
    #
    # The diff itself is kept and is now the derived ownership signal, so the SAME FIXTURE (a
    # deliberately out-of-claim edit) asserts the new fact: the token is granted, and the touch is
    # on the record where a reviewer can read it, naming the undeclared path specifically.
    Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water") | Out-Null
    $wt1 = "$root\.dodona\wt\t1"
    Set-Content "$wt1\src\water\sim.cs" "// water v2"
    Set-Content "$wt1\src\sky\box.cs" "// out-of-claim edit -- ordinary now, and recorded"
    git -C $wt1 add -A
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "water + sky"
    Dodona @("approve", "1") | Out-Null
    $req = Dodona @("token-request", "1")
    Check 'an_out_of_claim_branch_is_granted_the_token' ($DODONA_EXIT -eq 0 -and $req -match 'granted ticket 1') $req
    # WAIT FOR THE ROW, THEN READ IT ONCE (issue #10). The daemon writes `branch_touched` AFTER
    # `token-request` has already answered, so an unguarded read here comes back empty under load
    # -- which fails the assertion AND leaves the FAIL detail blank, because the detail IS that
    # value. A failure with nothing in it is what teaches people to re-run instead of read, and
    # re-running instead of reading is how a real failure eventually gets waved through as flaky.
    #
    # The wait's own verdict goes in the detail, because a row that NEVER arrives would otherwise
    # still report blank -- the wait would move the diagnosis into its timeout line and leave the
    # check saying nothing, which is the same complaint one step along.
    $touchedFlat = ''
    $touchedOk = Wait-Until {
        $script:touchedFlat = ((Invoke-StoreSql $storeDb "SELECT detail FROM events WHERE kind='branch_touched'") -replace '\s+', ' ').Trim()
        $script:touchedFlat -ne ''
    } 20000 'the branch_touched row for the granted token'
    Check 'the_branch_touch_is_recorded_for_the_reviewer' ($touchedFlat -match 'src/water/sim\.cs') `
        "row-arrived=$touchedOk detail=[$touchedFlat]"
    Check 'the_record_singles_out_the_undeclared_path' `
        ($touchedFlat -match 'undeclared:.*src/sky/box\.cs') "row-arrived=$touchedOk detail=[$touchedFlat]"
    Dodona @("token-release", "1") | Out-Null

    # An extension still works and is still worth having -- it is an annotation now rather than a
    # lock, so after it there is nothing left undeclared to single out.
    Dodona @("claim-extend", "1", "--claim", "path:src/sky/box.cs") | Out-Null
    # How many touch rows existed BEFORE this request, so the wait below can hold out for a NEW
    # one. Waiting merely for "not empty" would be satisfied instantly by the row the checks above
    # just read -- the stale-row trap, which would assert the previous token's answer against this
    # token's question and go red for a reason that has nothing to do with either.
    $touchedBefore = [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='branch_touched'").Trim())
    $req = Dodona @("token-request", "1")
    Check 'the_token_is_granted_after_an_extend_too' ($req -match 'granted ticket 1') $req
    # THE LATEST record only, and it must EXIST. Written first as a bare `-notmatch` over every
    # branch_touched row, `dev prove` called it VACUOUS and was right: against HEAD no such event
    # is written at all, so "does not contain undeclared" passed on an empty string. A negative
    # assertion that is satisfied by absent data is the check-that-cannot-fail trap (CLAUDE.md
    # 0.3), and it is easy to write by accident precisely here, where the new evidence is a row
    # that used not to exist. So: the row is present, names the path, and no longer flags it.
    #
    # AND IT IS WAITED FOR RATHER THAN SAMPLED (issue #10, the ticket this check IS). The wait is
    # on the row COUNT rising, deliberately not on the content this then asserts -- a wait that
    # already checks the assertion is tautological, and would turn a real product failure into a
    # 20-second timeout instead of an immediate red carrying the value it read.
    $touched2 = ''
    $touched2Ok = Wait-Until {
        if ([int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='branch_touched'").Trim()) -le $touchedBefore) { return $false }
        $script:touched2 = ((Invoke-StoreSql $storeDb "SELECT detail FROM events WHERE kind='branch_touched' ORDER BY id DESC LIMIT 1") -replace '\s+', ' ').Trim()
        $true
    } 20000 'the branch_touched row for the extended claim'
    Check 'an_extended_claim_leaves_nothing_undeclared' `
        (($touched2 -match 'src/sky/box\.cs') -and ($touched2 -notmatch 'undeclared:')) `
        "new-row-arrived=$touched2Ok before=$touchedBefore detail=[$touched2]"
    Dodona @("token-release", "1") | Out-Null

    # ---- presence derived from tool events, in code ----
    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -match 'lane (\d+)') { $sky = $Matches[1] } else { throw "lane-start failed: $ls" }
    Dodona @("say", "$sky", "tool:Write:src/sky/box.cs sleep:2 then say presence-done") | Out-Null
    Wait-Until { (Dodona @("status")) -match 'presence=write: box.cs' } 20000 'presence shows the tool in use' | Out-Null
    $status = Dodona @("status")
    Check 'presence_shows_tool' ($status -match 'presence=write: box.cs') $status
    Start-Sleep -Seconds 2
    $status = Dodona @("status")
    Check 'presence_idle_after_result' ($status -match 'presence=idle') $status

    # ---- presence must not LIE while the agent thinks ----
    #
    # A long think floods the wire with `system/thinking_tokens` and nothing else -- 93 of
    # 111 lines in one measured turn on 2026-08-19. Those events used to be dropped whole,
    # which left `presence` reading as the last TOOL the agent had run: a tile that said
    # `bash: ls -la docs/...` through ninety seconds of pure reasoning, with the pane clock
    # beside it ticking (LANE-LIFECYCLE 5) so a stale label looked like a live one. A thought
    # is not a step, so it still earns no pane row -- only the truth about what is happening.
    $progBefore = [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM pane_events WHERE lane_id=$sky AND kind='progress'").Trim())
    Dodona @("say", "$sky", "tool:Read:src/sky/box.cs think:20 sleep:3 then say thinking-done") | Out-Null
    Wait-Until { (Dodona @("status")) -match 'presence=thinking' } 20000 'presence follows the agent into a think' | Out-Null
    $status = Dodona @("status")
    Check 'presence_shows_thinking_not_a_stale_tool' ($status -match 'presence=thinking' -and $status -notmatch 'presence=read: box.cs') $status
    # And the flood is not transcript: twenty thinking events add ZERO rows. A DELTA, not a
    # total -- this lane already ran a tool:Write turn earlier in the suite, so an absolute
    # count would have asserted something true about the SUITE rather than about the product.
    # The +1 is the Read, which is what makes this a measurement instead of a zero.
    $progAfter = [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM pane_events WHERE lane_id=$sky AND kind='progress'").Trim())
    Check 'thinking_writes_no_pane_rows' (($progAfter - $progBefore) -eq 1) "progress rows went $progBefore -> $progAfter (only the Read may count)"

    # ---- tier-0 prefix routing ----
    $r = Dodona @("input", "sky: hello via prefix")
    Check 'tier0_prefix_routes' ($r -match '-> SKY \(tier 0\)') $r
    $tail = Dodona @("tail", "$sky", "10")
    Check 'tier0_message_delivered' ($tail -match 'hello via prefix') $tail

    # ---- focus routing (optimistic delivery; no router running -> no second opinion) ----
    $ls2 = Dodona @("lane-start", "--title", "WATER", "--child", $fake)
    if ($ls2 -match 'lane (\d+)') { $water = $Matches[1] }
    Dodona @("focus", "$water") | Out-Null
    $r = Dodona @("input", "make the waves taller")
    Check 'focus_routes_optimistically' ($r -match '-> WATER \(focus') $r
    $tail = Dodona @("tail", "$water", "10")
    Check 'focus_message_delivered' ($tail -match 'make the waves taller') $tail

    # ---- and falling back SAYS SO. A silent permanent downgrade to "whatever is focused" is
    # how the routing ladder stayed dead for two days on the operator's instance: the only
    # evidence was a status-line suffix, and they typed into it believing lanes were being
    # chosen. Never hung, halted, stuck or outdated (CLAUDE.md 0.1) covers "quietly degraded".
    $unrouted = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('SELECT kind, detail FROM events'): print(r)
") | Out-String
    Check 'unrouted_fallback_is_announced' `
        ([bool]($unrouted -match "routing_unrouted.*(classifier|brain)")) `
        (($unrouted -split "`r?`n" | Where-Object { $_ -match 'routing_unrouted' }) -join ' ')

    # ---- a stale focus is not a dead end: pick a live lane and say so (§11) ----
    # (was: assert an error. Refusing to route a sentence because the focused lane no
    # longer exists is the machine asking permission to do the obvious thing.)
    Dodona @("focus", "999") | Out-Null
    $r = Dodona @("input", "orphan text")
    Check 'stale_focus_falls_back_to_a_live_lane' ($DODONA_EXIT -eq 0 -and $r -match '-> (WATER|SKY)') $r

    # ---- routing_decisions rows recorded ----
    $rows = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('SELECT tier, delivered_lane, retargeted FROM routing_decisions ORDER BY id'): print(r)
") | Out-String
    Check 'routing_rows_recorded' ([bool]($rows -match 'prefix' -and $rows -match 'focus')) $rows

    # ---- LAYER 2: THE REFUSAL IS A PROMOTION, NOT A WALL (WORK-ISOLATION-PLAN P2) ----
    #
    # P1 made the shared checkout refuse writes. On its own that is a locked door with no key: on a
    # one-project workspace every plain lane opens in the shared checkout, so a lane asked to do
    # real work could not do any. Layer 2 is the key -- the first refused write creates the ticket,
    # materialises the worktree, deploys the gate and moves the lane in, resuming its session.
    #
    # NOTHING HAS BEEN WRITTEN YET, which is exactly why layer 1 sits at the write ATTEMPT and not
    # at the commit: edits made first would be in the wrong tree, and there is no safe way to move
    # them -- `git stash` is repo-global, so two lanes stashing interleave one stack (CLAUDE.md 5.2).
    $promoted = Dodona @("lane-start", "--title", "ENGINE", "--child", $fake)
    Check 'promotion_lane_started' ($promoted -match 'lane (\d+)') $promoted
    $pLane = if ($promoted -match 'lane (\d+)') { $Matches[1] } else { 0 }
    # A path NO open ticket claims -- ticket 1 holds src/water and src/sky/box.cs, so src/engine is
    # free. The claim-conflict route is the OTHER case and m1 covers it.
    $engine = "$root\src\engine\e.cs"
    $pHook = "`"$dodona`" gate-hook --lane $pLane --workspace `"$($ws.Id)`""
    $pJson = @{ tool_name = 'Write'; tool_input = @{ file_path = $engine } } | ConvertTo-Json -Compress
    $ErrorActionPreference = 'Continue'
    $perr = Join-Path $out 'promote.err'
    $pOut = ($pJson | & cmd /c $pHook 2> $perr | Out-String) + (Get-Content $perr -Raw -ErrorAction SilentlyContinue)
    $pFlat = ($pOut -replace '\s+', ' ')
    # Captured native stderr is WRAPPED to the console width, so a phrase can be split mid-sentence
    # -- collapse before matching (CLAUDE.md 0.2, which cost a false red once already).
    Check 'the_write_is_still_refused' ($pFlat -match '"permissionDecision":"deny"') $pFlat
    # The denial is a REWRITE, not a wall: it names the ticket and where the same file now lives.
    Check 'the_refusal_names_the_new_ticket_and_the_new_path' `
        ($pFlat -match 'ticket \d+' -and $pFlat -match 'wt[\\/]+t\d+' -and $pFlat -match 'Nothing was written') $pFlat

    # The lane ends up IN the worktree. Asserted from the store rather than from the message: the
    # move is deliberately behind the answer (it respawns the process that is waiting for it), so
    # this is a condition-wait, not a sleep.
    $pWt = ''
    Wait-Until {
        $script:pWt = (Invoke-StoreSql $storeDb "SELECT cwd FROM lanes WHERE id = $pLane").Trim()
        $script:pWt -match 'wt[\\/]+t\d+'
    } 20000 'the promoted lane is respawned into its ticket worktree' | Out-Null
    Check 'the_lane_ends_up_in_a_worktree' ($pWt -match 'wt[\\/]+t\d+') "cwd=$pWt"
    Check 'the_promotion_is_recorded' `
        ([int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='lane_promoted' AND lane_id=$pLane").Trim()) -ge 1) `
        (Invoke-StoreSql $storeDb "SELECT kind, detail FROM events WHERE lane_id=$pLane")
    # THE POINT OF DOING IT AT THE WRITE ATTEMPT. The shared checkout is untouched.
    Check 'nothing_was_written_to_the_shared_checkout' (-not (Test-Path $engine)) "$engine should not exist"
    # The lane is the thread and it survives its agent (section 11): same row, same id, and the
    # ticket now points at it.
    # EVERY QUERY BELOW IS GUARDED ON THE TICKET EXISTING, and that is not defensive style -- it is
    # what makes these checks provable. The first version interpolated an empty id into
    # `WHERE id = `, sqlite refused it, `Invoke-StoreSql` threw, and the whole suite tore out of its
    # try block: against HEAD every check here came back MISSING rather than RED, which is worth
    # nothing (a check that cannot be seen to fail has no teeth). A check must FAIL on the old
    # behaviour, not crash on it.
    $pTicket = (Invoke-StoreSql $storeDb "SELECT id FROM tickets WHERE lane_id = $pLane AND state = 'open'").Trim()
    Check 'the_ticket_is_linked_to_the_same_lane' ($pTicket -match '^\d+$') "ticket=[$pTicket] lane=$pLane"

    # ---- D-9: THE UNDO LINE HAS TO BE TRUE ----
    # Promotion announces `dodona lane-stop <n>` as the undo, so stopping the lane must actually
    # undo it: ticket abandoned, worktree pruned, branch deleted, claims released. An announcement
    # offering an undo that does not undo is worse than offering none.
    $pWtPath = $pWt
    $pBranch = if ($pTicket -match '^\d+$') { (Invoke-StoreSql $storeDb "SELECT branch FROM tickets WHERE id = $pTicket").Trim() } else { '' }
    $stopOut = Dodona @("lane-stop", $pLane)
    $pState = if ($pTicket -match '^\d+$') { (Invoke-StoreSql $storeDb "SELECT state FROM tickets WHERE id = $pTicket").Trim() } else { '' }
    Check 'stopping_a_promoted_lane_abandons_its_ticket' ($pState -eq 'abandoned') "state=[$pState] ticket=[$pTicket] $stopOut"
    $wtGone = ($pWtPath -match 'wt[\\/]+t\d+') -and (-not (Test-Path $pWtPath))
    $branchGone = ($pBranch -ne '') -and ((git -C $root branch --list $pBranch | Out-String).Trim() -eq '')
    Check 'the_undo_prunes_the_worktree_and_the_branch' ($wtGone -and $branchGone) `
        "wt=[$pWtPath] gone=$wtGone branch=[$pBranch] gone=$branchGone"
    # ---- R7 / D-R28: THE OTHER PLACE A BRANCH DIES, AND IN pr MODE IT DOES NOT --------------
    #
    # The land is the first place Dodona deletes a branch, and pr mode makes it unreachable (m1
    # asserts that). THIS is the second and the dangerous one: an abandon undoes DODONA'S ticket,
    # not the project's work, and in a `delivery: pr` repository that branch may already be pushed
    # with a PR open on it. So the worktree still goes -- it is Dodona's, and a checkout of an old
    # commit left behind for ever is what the prune exists to prevent -- and the branch stays.
    #
    # The repository has no dodona.json in this fixture, so writing one IS the flip; Config.For
    # re-reads per call, so no daemon restart is involved. It is removed again afterwards.
    Set-Content "$root\dodona.json" '{ "main": "main", "delivery": "pr" }'
    $prPromoted = Dodona @("lane-start", "--title", "PRENGINE", "--child", $fake)
    $prLane = if ($prPromoted -match 'lane (\d+)') { $Matches[1] } else { 0 }
    $prFile = "$root\src\prengine\p.cs"
    $prHook = "`"$dodona`" gate-hook --lane $prLane --workspace `"$($ws.Id)`""
    $prJson = @{ tool_name = 'Write'; tool_input = @{ file_path = $prFile } } | ConvertTo-Json -Compress
    $ErrorActionPreference = 'Continue'
    $prErr = Join-Path $out 'promote-pr.err'
    ($prJson | & cmd /c $prHook 2> $prErr) | Out-Null
    $ErrorActionPreference = 'Stop'
    $prWt = ''
    Wait-Until {
        $script:prWt = (Invoke-StoreSql $storeDb "SELECT cwd FROM lanes WHERE id = $prLane").Trim()
        $script:prWt -match 'wt[\\/]+t\d+'
    } 20000 'the pr-mode promoted lane is respawned into its ticket worktree' | Out-Null
    $prTicket = (Invoke-StoreSql $storeDb "SELECT id FROM tickets WHERE lane_id = $prLane AND state = 'open'").Trim()
    $prBranch = if ($prTicket -match '^\d+$') { (Invoke-StoreSql $storeDb "SELECT branch FROM tickets WHERE id = $prTicket").Trim() } else { '' }
    # VACUOUS AGAINST HEAD BY CONSTRUCTION, and kept on purpose for the same reason m2's
    # `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is kept: HEAD has no delivery
    # field at all, so nothing about a pr repo can differ there and no code state makes it red
    # today. What it catches is later -- the day somebody reads "Dodona gets out of the way" as
    # permission to take this away too.
    Check 'a_pr_repo_still_promotes_a_refused_write' `
        ($prTicket -match '^\d+$' -and $prBranch -ne '') "ticket=[$prTicket] branch=[$prBranch] cwd=[$prWt]"
    $prStop = Dodona @("lane-stop", $prLane)
    # Captured once, then asserted and reported: 84c0002's lesson about a check whose red named
    # the string the red itself contained.
    $prBranchAfter = if ($prBranch -ne '') { (git -C $root branch --list $prBranch | Out-String).Trim() } else { '' }
    $prWtGone = ($prWt -match 'wt[\\/]+t\d+') -and (-not (Test-Path $prWt))
    Check 'abandoning_a_pr_ticket_keeps_its_branch' ($prBranchAfter -ne '') `
        "branch=[$prBranch] after=[$prBranchAfter] stop=[$prStop]"
    # VACUOUS AGAINST HEAD BY CONSTRUCTION, and kept on purpose for the same reason m2's
    # `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is kept: HEAD has no delivery
    # field at all, so nothing about a pr repo can differ there and no code state makes it red
    # today. What it catches is later -- the day somebody reads "Dodona gets out of the way" as
    # permission to take this away too.
    Check 'abandoning_a_pr_ticket_still_prunes_the_worktree' $prWtGone "wt=[$prWt] gone=$prWtGone"
    # The receipt has to SAY the branch was kept. An undo that silently reports a deletion it did
    # not perform is D-9's announcement lying, one mode over.
    Check 'the_undo_says_the_branch_was_kept_rather_than_reporting_a_deletion' `
        ($prStop -match 'KEPT' -and $prStop -notmatch 'deleted') $prStop
    Remove-Item "$root\dodona.json" -Force -ErrorAction SilentlyContinue

    # ...and a ticket the OPERATOR created is not collateral. Ticket 1 was made by ticket-create,
    # so stopping ITS lane must leave it alone -- section 11's "nothing is deleted" is about their
    # work, and only a PROMOTED ticket is Dodona's to withdraw.
    #
    # VACUOUS BY CONSTRUCTION -- HEAD abandons nothing, so no code state makes this red -- and kept
    # anyway, for the same reason `workspace`'s `a_disjoint_directory_in_the_renamed_repository_is_still_free`
    # is kept: it is what catches this widening later, when someone simplifies the promoted-ticket
    # test out of `lane-stop` and starts deleting the operator's branches on their behalf.
    $t1lane = Dodona @("ticket-agent", "1", "--child", $fake)
    if ($t1lane -match 'lane (\d+)') {
        $t1id = $Matches[1]
        Dodona @("lane-stop", $t1id) | Out-Null
        Check 'stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone' `
            ((Invoke-StoreSql $storeDb "SELECT state FROM tickets WHERE id = 1").Trim() -ne 'abandoned') `
            (Invoke-StoreSql $storeDb "SELECT id, state FROM tickets")
    }
    else { Check 'stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone' $false "ticket-agent 1 failed: $t1lane" }
    $ErrorActionPreference = 'Stop'

    Dodona @("stop-daemon") | Out-Null

}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M2 ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
