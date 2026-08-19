# M0 acceptance test (design §16): kill the daemon mid-agent-turn; the session must not
# notice. Runs entirely on the fake agent — zero model calls, deterministic, free.
#
# Timeline:  t=0     daemon #1 up, lane SKY started (fake agent), slow 3s turn begins
#            as soon as the agent is running: daemon #1 MURDERED (turn in flight)
#            t≈0.3-3.8s  no daemon exists; the turn completes into the shim's buffer
#            t≈3.8s  daemon #2 starts: reconcile → reconnect → drain (deduped by seq)
#            then    a fresh message through daemon #2 — the SAME agent must answer.
#
# Every step above waits for the CONDITION it names (the pipe is up, the agent process
# exists, the drained result appears), not for a duration -- see Wait-Until in
# tests/_workspace.ps1. The single remaining Start-Sleep is the fake agent's own turn
# length, which by design nothing outside the shim can observe while no daemon exists.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm0'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in

$root = Join-Path (Use-SuiteTemp) ("dodona-m0-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $root | Out-Null
$out = Join-Path $PSScriptRoot 'm0-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -ErrorAction SilentlyContinue

$results = [ordered]@{}
$ws = Get-WorkspacePaths $dodona $root
$storeDb = $ws.Store
$wsDir = $ws.Dir
$token = "SURVIVED-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
function Dodona([string[]]$a) { (& $dodona ($a + @('--root', $root))) | Out-String }
# Reading the store directly, the same shape brain-acceptance uses: the Phase 3 checks below
# are about what reconcile WROTE, and an event detail is the only place that is recorded.
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
# Live lane pipes for THIS workspace, straight off the OS -- the same question
# Instance.LiveLanes() answers in the product, asked independently here so a check cannot
# be satisfied by the code it is testing.
function LanePipes {
    @([System.IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { [System.IO.Path]::GetFileName($_) } |
        Where-Object { $_ -match "^dodona-$([regex]::Escape($ws.Id))-lane\d+$" })
}

$shimInfo = $null
$d2 = $null
$extraDaemons = @()          # the Phase 3 section starts more; the finally must reach them all
try {
    # ---- daemon #1, lane, slow turn ----
    $d1 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d1.out" -RedirectStandardError "$out\d1.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $lane = $Matches[1]
    Dodona @("say", "$lane", "sleep:3 then say $token") | Out-Null

    # ---- murder daemon #1 mid-turn ----
    # WAIT FOR THE TURN TO BE IN FLIGHT, do not guess at it. The shim-info file is written
    # when the shim is spawned and the agent is running once its childPid is alive; killing
    # before either exists would test nothing (there would be no orphan to survive), and a
    # fixed second was simultaneously too long here and too short on a loaded machine.
    Wait-Until { Test-Path "$wsDir\shim-lane$lane.json" } 15000 "shim-info for lane $lane" | Out-Null
    $shimInfo = Get-Content "$wsDir\shim-lane$lane.json" | ConvertFrom-Json
    Wait-Until { [bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue) } 15000 'the agent process is up' | Out-Null
    Stop-Process -Id $d1.Id -Force
    $results['daemon1_killed_mid_turn'] = if ($shimInfo) { 'PASS' } else { 'FAIL no shim-info' }
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 10000 'daemon #1 pipe is gone' | Out-Null
    $results['shim_alive_no_daemon']  = if (Get-Process -Id $shimInfo.shimPid  -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }
    $results['agent_alive_no_daemon'] = if (Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue) { 'PASS' } else { 'FAIL' }

    # ---- the turn completes with NO daemon in existence ----
    # Nothing outside the shim can observe the buffered result while no daemon exists -- that
    # is the point of the test -- so this one wait is genuinely a duration, and it is the
    # fake agent's own 3s turn plus a margin rather than a number nobody can derive.
    Start-Sleep -Milliseconds 3800

    # ---- daemon #2: reconcile, reconnect, drain ----
    $d2 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d2.out" -RedirectStandardError "$out\d2.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    # Reconnect-and-drain is what daemon #2 must do; wait for the DRAINED RESULT, which is
    # the thing the next three checks are about.
    # The token is in the PROMPT as well ("sleep:3 then say <token>"), so a bare -match is
    # satisfied by the echoed input. The wait has to be the predicate the checks use: a RESULT
    # line carrying the token. m4 shipped this bug for one run and it read as a product failure.
    Wait-Until {
        @((Dodona @("tail", "$lane", "50")) -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' }).Count -ge 1
    } 20000 'the orphaned result drains' | Out-Null

    $tail = Dodona @("tail", "$lane", "50")
    Set-Content "$out\tail.txt" $tail
    $hits = @(($tail -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' })
    $results['orphaned_result_landed'] = if ($hits.Count -ge 1) { 'PASS' } else { 'FAIL' }
    $results['landed_exactly_once']    = if ($hits.Count -eq 1) { 'PASS' } else { "FAIL (count=$($hits.Count))" }

    $status = Dodona @("status")
    $results['session_id_recorded'] = if ($status -match 'session=fake-') { 'PASS' } else { 'FAIL' }

    # ---- the session must not notice: same agent answers through daemon #2 ----
    $roundtrip = "ROUNDTRIP-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    Dodona @("say", "$lane", "say $roundtrip") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match [regex]::Escape($roundtrip) } 20000 'the same agent answers through daemon #2' | Out-Null
    $tail2 = Dodona @("tail", "$lane", "10")
    $results['same_agent_answers_daemon2'] = if ($tail2 -match [regex]::Escape($roundtrip)) { 'PASS' } else { 'FAIL' }

    # =====================================================================================
    # PHASE 3 -- nothing Dodona starts is allowed to outlive its reason (I3, I4, I6).
    #
    # Everything above this line is about an agent SURVIVING a dead daemon, which is the
    # design (§13). Everything below is the other half, which was missing entirely: an agent
    # whose own reason to exist is gone must die by itself. Measured on the operator's machine
    # 2026-08-18 (docs/INVESTIGATION-2026-08-18.md RC2): eleven live lane pipes, ten of them
    # agents nobody could reach, three of those running out of the compiler's own output
    # directory -- which is how they blocked every build invisibly for eighteen hours, while
    # `dodona ps` said seven lanes and `stop-all --lanes` could reach none of the four.

    # ---- P3.2: the shim exits when its AGENT dies, once the buffer is drained -----------
    # The flag was computed at DodonaShim/Program.cs and then never read; the serve loop only
    # ever ended on `##shutdown` from a client. So a shim whose agent had died ran forever,
    # still answering `!hello`, and was re-adopted and routed to as though it held an agent.
    $ls2 = Dodona @("lane-start", "--title", "ORPHAN", "--child", $fake)
    if ($ls2 -notmatch 'lane (\d+)') { throw "lane-start ORPHAN failed: $ls2" }
    $oLane = $Matches[1]
    $oPipe = "dodona-$($ws.Id)-lane$oLane"
    Wait-Until { Test-Path "$wsDir\shim-lane$oLane.json" } 15000 "shim-info for lane $oLane" | Out-Null
    $oInfo = Get-Content "$wsDir\shim-lane$oLane.json" | ConvertFrom-Json
    Wait-Until { Test-DodonaPipe $oPipe } 15000 "lane $oLane pipe is up" | Out-Null
    # THE AGENT ONLY, resolved by recorded pid -- never by process name (CLAUDE.md §4: a
    # by-name kill once murdered the operator's live session mid-trial). The shim is left
    # strictly alone, so anything that happens to it now, it did to itself.
    Stop-Process -Id $oInfo.childPid -Force
    $results['shim_exits_when_its_agent_dies'] =
        if (Wait-Until { -not (Get-Process -Id $oInfo.shimPid -ErrorAction SilentlyContinue) } 15000 'the shim exits itself') { 'PASS' } else { 'FAIL' }
    # Safe to assert on the namespace HERE, and only because the check above already proved the
    # shim process exited: after that an absent pipe cannot be the between-clients blink.
    $results['dead_lane_pipe_leaves_the_namespace'] =
        if (Wait-Until { -not (Test-DodonaPipe $oPipe) } 15000 'the lane pipe is gone') { 'PASS' } else { 'FAIL' }
    # Nothing in the tree used to delete a shim-info file on ANY exit path, so the set was
    # monotonic for the life of a workspace: that is why "24 lanes" was every lane the
    # workspace had ever spawned rather than eighteen leftovers.
    $results['shim_record_dies_with_the_shim'] =
        if (Wait-Until { -not (Test-Path "$wsDir\shim-lane$oLane.json") } 10000 'the shim-info record is gone') { 'PASS' } else { 'FAIL' }

    # ---- P3.1: liveness is the OS, not a file -------------------------------------------
    # Delete the record and keep the agent. `ps` counted lanes from `shim-lane*.json` and
    # `stop-all --lanes` iterated the same files, so an agent with no record was invisible AND
    # unstoppable by every dodona command -- four of them were, on the operator's machine.
    $ls3 = Dodona @("lane-start", "--title", "NORECORD", "--child", $fake)
    if ($ls3 -notmatch 'lane (\d+)') { throw "lane-start NORECORD failed: $ls3" }
    $nLane = $Matches[1]
    $nPipe = "dodona-$($ws.Id)-lane$nLane"
    Wait-Until { Test-Path "$wsDir\shim-lane$nLane.json" } 15000 "shim-info for lane $nLane" | Out-Null
    $nInfo = Get-Content "$wsDir\shim-lane$nLane.json" | ConvertFrom-Json
    Wait-Until { Test-DodonaPipe $nPipe } 15000 "lane $nLane pipe is up" | Out-Null
    # BEFORE and AFTER, not '>= 1'. The first version of this check summed every lane `ps`
    # reported and demanded at least one -- which OTHER live lanes in this suite satisfy, so it
    # passed against HEAD and `dev prove` called it VACUOUS. A check that cannot fail is the
    # exact trap of 2026-08-18. Deleting one record must not change the count by one.
    function PsLanes {
        # ConvertFrom-Json emits a JSON ARRAY as ONE pipeline item (CLAUDE.md §0.2), so land it
        # in a variable before touching it -- filtering the array object instead of its elements
        # is how three acceptance checks became silent no-ops once already.
        $j = Dodona @('ps', '--json')
        $line = @(($j -split "`r?`n") | Where-Object { $_.Trim().StartsWith('[') }) | Select-Object -Last 1
        if (-not $line) { return 0 }
        $rows = $line | ConvertFrom-Json
        [int]((@($rows) | ForEach-Object { $_.lanes } | Measure-Object -Sum).Sum)
    }
    $psWithRecord = PsLanes
    Remove-Item "$wsDir\shim-lane$nLane.json" -Force
    $psWithout = PsLanes
    $results['ps_counts_a_lane_whose_record_is_gone'] =
        if ($psWithout -eq $psWithRecord -and $psWithRecord -ge 1) { 'PASS' }
        else { "FAIL (ps said $psWithRecord lanes with the record, $psWithout without it; pipe live=$(Test-DodonaPipe $nPipe))" }
    # ...and it can still be STOPPED, which is the half that mattered on the day: the shim's
    # own pipe is a door that needs no bookkeeping, and `##shutdown` takes the child tree too.
    Dodona @('stop-all', '--lanes') | Out-Null
    # THE SHIM PROCESS, not its pipe name. Asserting on the namespace here made this check pass
    # against HEAD -- `dev prove` called it VACUOUS -- because `stop-all` stops the daemon first,
    # every shim disconnects at that instant, and a disconnecting shim's pipe name blinks out
    # while it swaps server instances. A 20-second poll for absence will eventually land in one
    # of those gaps and call an untouched agent 'stopped'. Third time this phase made the same
    # mistake; PROCESSES do not blink.
    $results['stopall_stops_a_lane_whose_record_is_gone'] =
        if (Wait-Until { -not (Get-Process -Id $nInfo.shimPid -ErrorAction SilentlyContinue) } 20000 'the recordless lane is stopped') { 'PASS' }
        else { "FAIL (shim $($nInfo.shimPid) still alive)" }
    $results['and_its_agent_dies_with_it'] =
        if (Wait-Until { -not (Get-Process -Id $nInfo.childPid -ErrorAction SilentlyContinue) } 15000 'its agent is gone') { 'PASS' } else { 'FAIL' }
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 15000 'stop-all took the daemon down too' | Out-Null

    # ---- P3.3: the LEASE, and P3.4: reconcile asks the OS -------------------------------
    # One setup serves both. A short lease on six lanes, then the daemon is killed with
    # -Force so no `##shutdown` can ever reach them: only the lease can end these shims, and
    # that is the orphan class P3.2 alone cannot close (a live agent with nobody left to
    # deliver to). What it leaves behind is six dead pipe names for the NEXT daemon to
    # reconcile, which is exactly the state P3.4 is about.
    $env:DODONA_SHIM_LEASE_SEC = '2'      # a real duration, and the shortest one worth testing
    $d3 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d3.out" -RedirectStandardError "$out\d3.err"
    $extraDaemons += $d3
    Wait-Daemon $ws.CtlPipe | Out-Null
    for ($i = 0; $i -lt 6; $i++) { Dodona @('lane-start', '--title', "LEASE$i", '--child', $fake) | Out-Null }
    Wait-Until { (LanePipes).Count -ge 6 } 25000 'six lane pipes are up' | Out-Null
    # BOTH pids per lane, from this workspace's own records (CLAUDE.md 4, never by name). The
    # checks below are written against PROCESSES rather than pipe names on purpose: a lane pipe
    # blinks out of the namespace between clients (measured, see LaneLiveness), so "the pipe is
    # gone" can be true for a few milliseconds while the shim is perfectly alive -- and a check
    # that can pass early is a false green, which costs exactly as much as a false red.
    $leaseShims = @(); $leaseKids = @()
    foreach ($f in Get-ChildItem "$wsDir\shim-lane*.json") {
        $si = Get-Content $f | ConvertFrom-Json
        $leaseShims += $si.shimPid; $leaseKids += $si.childPid
    }
    Stop-Process -Id $d3.Id -Force
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 15000 'daemon #3 murdered' | Out-Null
    # NOTHING kills these shims. If they go, they went by themselves.
    $results['lease_expires_when_no_daemon_ever_returns'] =
        if (Wait-Until { @($leaseShims | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -eq 0 } 25000 'every leased shim exits on its own') { 'PASS' }
        else { "FAIL ($(@($leaseShims | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count) shim(s) still alive of $($leaseShims.Count))" }
    $results['the_lease_takes_the_agents_too'] =
        if (Wait-Until { @($leaseKids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -eq 0 } 20000 'their agents are gone too') { 'PASS' } else { 'FAIL' }
    # ...and only now is the namespace a safe thing to assert on: the processes are provably gone,
    # so an absent pipe cannot be a blink.
    $results['and_the_lane_pipes_are_gone'] =
        if (Wait-Until { (LanePipes).Count -eq 0 } 15000 'the lane pipes are gone') { 'PASS' } else { "FAIL ($((LanePipes).Count) still there)" }
    Remove-Item env:DODONA_SHIM_LEASE_SEC -ErrorAction SilentlyContinue

    # P3.4. `attempts = patient ? ... : 1` asserted that a utility lane whose pipe does not
    # answer in one 500 ms knock "is simply gone" -- an assertion nothing ever checked, and it
    # was false: lane 20 on the operator's store was declared unreachable, reaped as "shim
    # gone" and replaced 160 ms later while its shim, child and pipe were all still alive.
    # A pipe that is not in the namespace belongs to a process that does not exist, so the
    # right number of attempts is ZERO -- faster than one knock, which is what the 35-second
    # reconcile hang actually wanted. Seven alive rows with dead pipes are waiting here.
    # THE DEAD PIPES HAVE TO BE DEAD IN BOTH WORLDS, or this measures nothing. Relying on the
    # lease to have removed them made the check VACUOUS against HEAD: HEAD has no lease, so
    # those shims were still ALIVE there, reconcile adopted them in milliseconds and the budget
    # was never approached. The condition being timed is 'rows that are alive with pipes that are
    # not', so the suite has to create it itself.
    Stop-WorkspaceShims $wsDir
    Wait-Until { (LanePipes).Count -eq 0 } 20000 'no lane pipe survives, in either build' | Out-Null
    $aliveRows = $null
    try { $aliveRows = (Rows "SELECT COUNT(*) FROM lanes WHERE state='alive' AND role<>'dispatcher'").Trim() } catch { }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $d4 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d4.out" -RedirectStandardError "$out\d4.err"
    $extraDaemons += $d4
    $up4 = Wait-Daemon $ws.CtlPipe
    $sw.Stop()
    $reconcileMs = [math]::Round($sw.Elapsed.TotalMilliseconds)
    # Reconcile runs BEFORE the control pipe server, so time-to-answerable IS time-to-reconcile.
    # 4000 ms is generous by design: measured 233 ms after the fix, and ~800 ms PER LANE before
    # it (one 500 ms connect plus a 300 ms delay, x3 attempts for a work lane).
    $results['reconcile_does_not_knock_on_pipes_that_are_gone'] =
        if ($up4 -and $reconcileMs -lt 4000) { 'PASS' }
        else { "FAIL (answerable after ${reconcileMs}ms with $aliveRows alive row(s) and no pipes, up=$up4)" }
    $unreachDetail = (Rows "SELECT detail FROM events WHERE kind='lane_unreachable' ORDER BY id DESC LIMIT 1").Trim()
    $results['reconcile_says_it_asked_the_os'] =
        if ($unreachDetail -match 'no pipe and no live shim process') { 'PASS' } else { "FAIL ($unreachDetail)" }
    # A daemon that abandons a lane it could still reach is how an immortal shim is made, so
    # the honest message matters: "did not answer in N attempt(s)" says nothing about whether
    # the process is there, and that ambiguity is the bug.

    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($shimInfo) {
        foreach ($p in @($shimInfo.shimPid, $shimInfo.childPid)) {
            try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { }
        }
    }
    Remove-Item env:DODONA_SHIM_LEASE_SEC -ErrorAction SilentlyContinue
    foreach ($p in @($d2) + $extraDaemons) {
        if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } }
    }
    # Whatever the Phase 3 section left, by PATH -- it owns every lane it started and several
    # of them are deliberately killed in ways that skip a graceful exit.
    Stop-WorkspaceShims $wsDir
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # ...and the shim exit log beside it. A suite's DODONA_HOME is deleted with the run, so
    # without this the one record of WHY a wrapper process ended dies with it -- and "it was
    # simply gone" is the diagnosis this whole phase exists to stop accepting.
    Copy-Item "$wsDir\shim-exits.log" "$out\shim-exits.log" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M0 ACCEPTANCE ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
# The tally is not decoration: tools\dev.ps1 reads THIS line to decide whether the suite ran,
# and until 2026-08-19 m0 was the one suite that never printed it -- so a red m0 was
# indistinguishable from a green one for the entire life of the suite. dev.ps1 now treats a
# missing tally as a failure, which is the half of the fix that cannot be forgotten again.
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
