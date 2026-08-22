# M4 acceptance (design §13/§14): hot-swap the daemon while an agent is mid-turn and the
# session must not notice. This is the milestone that gates dogfooding Dodona on Dodona —
# self-hosting before the swap works means every daemon iteration kills live sessions.
#
# Timeline:  t=0    daemon #1 (dev build) up, lane SKY started, slow 6s turn begins
#            t≈1s   `dodona publish` — real build into a fresh versioned dir
#            t≈Xs   successor handoff WHILE the turn is in flight
#            then   the orphaned result must land exactly once, same session, same agent,
#                   and a fresh message must round-trip through the NEW daemon.
#
# Also covered: bad-binary refusal (the system must stay up), the three-answer blocker
# flow with "when it lands" firing on the condition, start-on-demand, and old-build GC.
# Fake agents only — zero model calls.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm4'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$out = Join-Path $PSScriptRoot 'm4-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# Published builds go to a scratch bin root, not the machine-wide one: tests collide with
# nothing (§17), and the GC check gets a directory it owns.
$binRoot = Join-Path (Use-SuiteTemp) ("dodona-bin-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$env:DODONA_BIN_ROOT = $binRoot

$root = Join-Path (Use-SuiteTemp) ("dodona-m4-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\a.cs" "// a"
Set-Content "$root\.gitignore" ".dodona/"
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
$ws = Get-WorkspacePaths $dodona $root
$storeDb = $ws.Store
$wsDir = $ws.Dir
# Capturing a native command's stderr in PS 5.1 needs BOTH a file redirect and a relaxed
# ErrorActionPreference in this scope: any stderr line makes 5.1 raise NativeCommandError
# (even on exit code 0), which under `Stop` aborts the whole run. Several checks below
# assert on stderr text — autostart notices, client-side refusals — so it must be read,
# not merged. Exit codes stay authoritative.
$errFile = Join-Path $out 'stderr.tmp'
function Dodona([string[]]$a) {
    # 'Continue', not 'SilentlyContinue': suppressing the record also stops it reaching
    # the redirect file, leaving stderr uncapturable. Continue keeps the file fed.
    $ErrorActionPreference = 'Continue'
    Remove-Item $errFile -ErrorAction SilentlyContinue
    $o = (& $dodona ($a + @('--root', $root)) 2> $errFile) | Out-String
    $global:DODONA_EXIT = $LASTEXITCODE
    $e = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
    ("$o`n$e").Trim()
}
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function Sql([string]$q) {
    # THE QUERY GOES ON ITS OWN LINE INSIDE THE TRIPLE QUOTES, and that is a fix rather than a
    # style choice. Interpolated inline, a query ENDING in a single quote -- which every
    # `WHERE kind=<quoted>` does -- put four quotes together and Python read them as a syntax
    # error: the process printed nothing, this function returned empty, and the caller read that
    # empty string as a legitimate ZERO. Cost a debugging round on 2026-08-21 -- a real retention
    # event sat in the store while the check asking for it reported none -- and the only reason
    # the older queries here work is a trailing space nobody had written down. A newline is
    # nothing to sqlite and cannot collide with a quote.
    (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('''
$q
'''): print('|'.join(str(x) for x in r))
") | Out-String
}

$d1 = $null
$shimInfo = $null
$token = "SURVIVED-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
try {
    # ================= the swap test: mid-turn handoff =================
    $d1 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d1.out" -RedirectStandardError "$out\d1.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $lane = $Matches[1]
    $shimInfo = Get-Content "$wsDir\shim-lane$lane.json" | ConvertFrom-Json
    # Read THIS lane's session, not the last one printed: the dispatcher lane the swap
    # creates has no session, and a greedy match would happily take its dash.
    function LaneSession { ((Dodona @("status")) -split "`r?`n" | Where-Object { $_ -match "^lane $lane\b" }) -replace '.*session=(\S+).*', '$1' }
    # WAIT FOR A SESSION TO EXIST before recording it. An agent reports its session id on its
    # first exchange, so reading immediately after lane-start sometimes catches `-` -- and then
    # `same_session_after_swap` compares "-" against a real id and fails. It was intermittent
    # from the very first baseline run of this phase and read as a hot-swap defect, which it
    # never was: the swap preserves the session perfectly, the test was just asking too early.
    # The check is "the session did not CHANGE", so it needs a session to start with.
    Wait-Until { (LaneSession) -match '^fake-' } 20000 'the agent reports a session id' | Out-Null
    $sessionBefore = LaneSession

    # a 6-second turn: the swap happens squarely inside it. The turn must actually BE in
    # flight before publish is called -- that is the whole test -- so wait for the agent
    # process rather than guessing a second.
    Dodona @("say", "$lane", "sleep:6 then say $token") | Out-Null
    Wait-Until { (Sql "SELECT COUNT(*) FROM pane_events WHERE lane_id=$lane AND kind='user_input'").Trim() -ne '0' } 15000 'the turn has been handed to the agent' | Out-Null

    # A DECOY STANDING IN FOR A CONCURRENT PUBLISH, planted BEFORE the first publish so that no
    # GC pass in this suite can miss it. It is a stamp NEWER than anything this suite will ever
    # build, which is precisely the shape `GcOldBuilds` used to destroy: on the operator's machine,
    # merging to main and then publishing by hand runs TWO publishes (the manual one and
    # `autoPublish`'s watcher, on the same commit, seconds apart), and whichever swapped first
    # deleted the other's brand-new directory out from under it. Measured twice on 2026-08-21.
    #
    # THE FIRST DRAFT PLANTED IT AFTER THE PUBLISH AND WAS WORTHLESS: the swapped-in daemon runs
    # its GC during reconcile, which had already happened, so the decoy survived by never having
    # been a candidate. It passed while asserting nothing -- caught only because the retention
    # EVENT was missing, which is why this check asserts the event and not just the directory.
    $futureDir = Join-Path $binRoot '29991231-235959'
    New-Item -ItemType Directory -Force $futureDir | Out-Null
    Set-Content "$futureDir\dodona.exe" 'not a real binary, and it does not need to be'

    $publish = Dodona @("publish", "--project", $repo)
    Set-Content "$out\publish.txt" $publish
    # Scaffolding excluded (the decoy above): the claim is that PUBLISH made one versioned
    # directory, not that the suite's own fixtures are absent from a folder it plants them in.
    Check 'publish_builds_versioned_dir' `
        (@(Get-ChildItem -Directory $binRoot -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -notin @('bogus', '29991231-235959') }).Count -eq 1) $publish
    Check 'publish_hands_off' ($publish -match 'handed off to build') $publish

    # daemon #1 must be GONE (it exited after the handoff) and #2 must own the pipe
    Wait-Until { $d1.HasExited -and (Dodona @("status")) -match 'daemon pid=(\d+)' } 30000 'daemon #1 exits and a successor answers' | Out-Null
    Check 'old_daemon_exited' ($d1.HasExited) "pid $($d1.Id) still alive"
    $status = Dodona @("status")
    Set-Content "$out\status-after-swap.txt" $status
    $newPid = if ($status -match 'daemon pid=(\d+)') { [int]$Matches[1] } else { -1 }
    Check 'new_daemon_serves' ($newPid -gt 0 -and $newPid -ne $d1.Id) $status

    # A LANE BORN FROM A PUBLISHED BUILD, so the gate-retention path has something to retain.
    # SKY cannot exercise it: the first daemon runs out of DODONA_HOME\bin, which GcOldBuilds never
    # collects, so SKY's gate names a directory that was never a candidate. This lane is spawned by
    # the daemon that just swapped IN, so its gate names a published build directory -- exactly the
    # operator's shape, where a daemon in bin\<stamp1> spawns a lane and a later publish tries to
    # collect stamp1 underneath it. Written after the first version of the retention check passed
    # vacuously for this reason (dirs=1, kept=0).
    $lsk = Dodona @("lane-start", "--title", "GATEKEEP", "--child", $fake)
    if ($lsk -notmatch 'lane (\d+)') { throw "lane-start GATEKEEP failed: $lsk" }
    $keepLane = $Matches[1]
    $keepGate = Join-Path $wsDir "gate-lane$keepLane.json"
    # PARSED, never string-matched: the file is JSON, so the path inside has DOUBLED backslashes and
    # its quotes written as ". The first version of this check regexed the raw text and got
    # `u0022C:UsersdevblAppData...` back -- the same trap the comment further down already warned
    # about for the same file, and the same one the retention code itself walked into.
    $keepExe = ''
    if (Test-Path $keepGate) {
        try {
            $kc = ((Get-Content $keepGate -Raw | ConvertFrom-Json).hooks.PreToolUse[0].hooks[0].command)
            if ($kc -match '^"([^"]+)"') { $keepExe = $Matches[1] }
        } catch { $keepExe = '' }
    }
    Check 'a_lane_from_a_published_build_names_it_in_its_gate' `
        ($keepExe -ne '' -and $keepExe -like "$binRoot*") "exe=$keepExe binRoot=$binRoot"
    Check 'new_daemon_is_published_build' ($status -match [regex]::Escape($binRoot)) $status

    # ---- provenance is a COMMIT (RECOVERY-PHASES P2.6) --------------------------------------
    # This suite already pays for a REAL build, which is the only place the stamp can be
    # observed end to end: publish resolves the commit, passes it to the compiler as
    # InformationalVersion, and the binary reads it back out of its own assembly. Asserted here
    # rather than in publish-acceptance because that suite deliberately publishes a PREBUILT
    # binary and so can only ever assert the negative.
    #
    # `git cat-file -t` is the half that matters: it demands the SHA is a commit this repository
    # actually has, which is what "bisectable" means. A stamp that merely looks like a hex
    # string would pass an equality check against itself and prove nothing -- the build used to
    # report a timestamp mapping to nothing, and the fix is only real if the value resolves.
    $head = (& git -C $repo rev-parse HEAD 2>$null).Trim()
    $pubDir = @(Get-ChildItem -Directory $binRoot | Where-Object { $_.Name -notin @('bogus', '29991231-235959') })[0].FullName
    $vj = & "$pubDir\dodona.exe" version --json 2>$null | ConvertFrom-Json
    Check 'published_build_carries_its_commit' ($vj.commit -eq $head -and $head.Length -eq 40) "commit=$($vj.commit) head=$head"
    Check 'published_commit_is_a_real_commit' `
        ($vj.commit.Length -eq 40 -and (& git -C $repo cat-file -t $vj.commit 2>$null) -eq 'commit') "commit=$($vj.commit)"
    # The tree under test is whatever the session has checked out, so branch and dirtiness are
    # not fixed -- but they must be REPORTED, and a dirty build must never claim to be clean.
    $treeDirty = @(& git -C $repo status --porcelain).Count -gt 0
    Check 'published_build_admits_uncommitted_changes' ($vj.dirty -eq $treeDirty) "dirty=$($vj.dirty) tree=$treeDirty"

    # the agent never noticed: shim and child are the SAME processes as before
    Check 'shim_survived_swap' ([bool](Get-Process -Id $shimInfo.shimPid -ErrorAction SilentlyContinue)) ''
    Check 'agent_survived_swap' ([bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)) ''

    # the turn that was in flight during the swap: landed, exactly once
    Wait-Until {
        @((Dodona @("tail", "$lane", "50")) -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' }).Count -ge 1
    } 30000 'the orphaned turn lands as a RESULT through the new daemon' | Out-Null
    $tail = Dodona @("tail", "$lane", "50")
    Set-Content "$out\tail.txt" $tail
    $hits = @(($tail -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($token) -and $_ -match 'result' })
    Check 'inflight_turn_landed' ($hits.Count -ge 1) "count=$($hits.Count)"
    Check 'landed_exactly_once' ($hits.Count -eq 1) "count=$($hits.Count)"

    # zero loss means contiguous seqs across the handoff
    $seqs = (Sql "SELECT seq FROM pane_events WHERE lane_id = $lane AND seq IS NOT NULL ORDER BY seq") -split "`r?`n" | Where-Object { $_ } | ForEach-Object { [int]$_ }
    $expected = 0..($seqs.Count - 1)
    Check 'seqs_contiguous_across_swap' (-not (Compare-Object $seqs $expected)) "seqs=$($seqs -join ',')"

    # same session, and the same agent answers through the new daemon
    $sessionAfter = LaneSession
    Check 'same_session_after_swap' ($sessionAfter -eq $sessionBefore -and $sessionAfter -match '^fake-') "before=$sessionBefore after=$sessionAfter"
    $rt = "ROUNDTRIP-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    Dodona @("say", "$lane", "say $rt") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match [regex]::Escape($rt) } 25000 'the same agent answers through the new daemon' | Out-Null
    Check 'same_agent_answers_new_daemon' ((Dodona @("tail", "$lane", "10")) -match [regex]::Escape($rt)) ''

    # the handoff is in the causal chain, and the successor announced itself
    $ev = Sql "SELECT kind FROM events WHERE kind IN ('swap_spawned','daemon_handoff','daemon_start') ORDER BY id"
    Check 'handoff_in_causal_chain' ($ev -match 'swap_spawned' -and $ev -match 'daemon_handoff') $ev
    Check 'successor_recorded_predecessor' ((Sql "SELECT detail FROM events WHERE kind='daemon_start' ORDER BY id DESC LIMIT 1") -match "successor_of=$($d1.Id)") ''
    Check 'swap_announced_to_dispatcher' ((Sql "SELECT body FROM pane_events WHERE kind='announcement' ORDER BY id") -match 'swapped to build') ''

    # ================= a bad binary must not take the system down =================
    $badDir = Join-Path $binRoot 'bogus'
    New-Item -ItemType Directory -Force $badDir | Out-Null
    Set-Content "$badDir\dodona.exe" "not a program"
    $bad = Dodona @("swap", "$badDir\dodona.exe")
    Check 'bad_binary_refused' ($DODONA_EXIT -ne 0 -and $bad -match 'version --json') $bad
    Check 'daemon_alive_after_bad_swap' ((Dodona @("status")) -match "daemon pid=$newPid") ''
    $missing = Dodona @("swap", "$binRoot\nope\dodona.exe")
    Check 'missing_binary_refused' ($DODONA_EXIT -ne 0 -and $missing -match 'no such binary') $missing

    # publish itself must refuse a binary that cannot start — and say nothing was promoted
    $badPub = Dodona @("publish", "--exe", "$badDir\dodona.exe")
    Check 'publish_refuses_unrunnable_binary' ($DODONA_EXIT -ne 0 -and $badPub -match 'nothing promoted') $badPub

    # ============ blocked swap ARMS ITSELF: nothing waits on a person (never-stuck) ============
    Dodona @("ticket-create", "--title", "MERGE", "--claim", "path:src/a.cs") | Out-Null
    Dodona @("approve", "1") | Out-Null
    $grant = Dodona @("token-request", "1", "--lease", "300")
    if ($grant -notmatch 'granted') { throw "token-request failed: $grant" }

    $publish2 = Dodona @("publish", "--project", $repo)
    Set-Content "$out\publish2.txt" $publish2
    Check 'midmerge_arms_swap' ($publish2 -match 'MERGE is mid-merge' -and $publish2 -match 'armed' -and $publish2 -match 'swap-answer now \| hold') $publish2
    Check 'blocked_swap_armed_in_store' ((Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'armed') ''
    # WAIT, DO NOT READ INSTANTLY. `publish` returns as soon as the daemon has ARMED the swap --
    # the `swaps` row on the line above is written on that path -- but the announcement is a
    # pane_event the daemon writes on its own task afterwards, so the two are not simultaneous.
    # Read the moment publish returns, this check is a race that a quiet machine wins and a gate
    # wave loses: it went red on 2026-08-22 at 3538c68 in a full wave with `blocked_swap_armed_in
    # _store` green one line above, which is the signature of exactly that gap and NOT of a
    # missing announcement. The assertion is unchanged; only the "when" is.
    Wait-Until { (Sql "SELECT body FROM pane_events WHERE kind='announcement' ORDER BY id") -match 'armed.*lands the instant this clears.*mid-merge' } 20000 'the armed swap is announced with its overrides' | Out-Null
    Check 'armed_announced_with_overrides' ((Sql "SELECT body FROM pane_events WHERE kind='announcement' ORDER BY id") -match 'armed.*lands the instant this clears.*mid-merge') ''
    Check 'daemon_did_not_swap_while_blocked' ((Dodona @("status")) -match "daemon pid=$newPid") ''

    # holding is opt-in — the override must still park it
    $held = Dodona @("swap-answer", "hold")
    Check 'hold_parks_the_swap' ($held -match 'parked' -and (Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'held') $held

    $armed = Dodona @("swap-answer", "when-it-lands")
    Check 'when_it_lands_rearms' ($armed -match 'armed' -and (Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'armed') $armed
    Check 'armed_but_not_yet_swapped' ((Dodona @("status")) -match "daemon pid=$newPid") ''

    # defer to a CONDITION, not a timer: release the token and it must swap itself
    Dodona @("token-release", "1") | Out-Null
    $swapped = Wait-Until {
        $s = Dodona @("status")
        if ($s -match 'daemon pid=(\d+)' -and [int]$Matches[1] -ne $newPid) { $script:newPid2 = [int]$Matches[1]; $true } else { $false }
    } 20000 'the armed swap fires once the merge token clears'
    Check 'armed_swap_fires_when_blocker_clears' $swapped "still pid $newPid after 20s"
    Check 'armed_swap_recorded' ((Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'swapped') ''
    Check 'agent_survived_second_swap' ([bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)) ''

    # ====== THE GATE OF A LIVE LANE STILL WORKS AFTER A HOT SWAP ======
    #
    # THE REQUIREMENT HERE IS THE OPPOSITE OF WHAT IT WAS, and the reversal is a measurement
    # (2026-08-20, WORK-ISOLATION-PLAN D-17). These checks used to demand that after a swap the
    # deployed gate pointed at the NEWLY RUNNING build, maintained by re-deploy-on-adoption --
    # which existed because a gate hard-codes the exe that wrote it and GcOldBuilds deletes old
    # build directories, so a stale gate called a binary scheduled for deletion and silently failed
    # OPEN (found live 2026-08-18).
    #
    # Measured: HOOKS ARE READ ONCE, AT SESSION START, AND NEVER RE-READ. A two-turn stream-json
    # session kept firing its hook on turn 2 after the hook had been REMOVED from the settings file
    # between turns. So rewriting the file never reached the live agent it was meant to protect,
    # and the lanes redeployment appeared to fix were the ones that respawned afterwards.
    #
    # The correct invariant is therefore about the EXE, not the file: an agent launched by an older
    # build holds that build's path for its whole life, so THAT DIRECTORY MUST SURVIVE. The gate
    # file is per-lane and lives in workspace state (never in anybody's repository, D-17), and
    # GcOldBuilds now refuses to collect a directory any live lane's gate names.
    $gateFile = Join-Path $wsDir "gate-lane$lane.json"
    $gateHook = ''
    if (Test-Path $gateFile) {
        try { $gateHook = ((Get-Content $gateFile -Raw | ConvertFrom-Json).hooks.PreToolUse[0].hooks[0].command) } catch { $gateHook = '' }
    }
    # PARSED, not string-matched, and the reason is worth keeping from the previous version of this
    # check: the file is JSON, so the command inside it has its backslashes doubled and its quotes
    # written as \u0022. A raw Contains() against a Windows path with single backslashes can
    # therefore NEVER match, and the check fails while the gate is perfectly well configured -- a
    # false red, which is as damaging as a false green. `dev prove` caught that one.
    Check 'gate_file_survives_the_swap' ($gateHook -match 'gate-hook' -and $gateHook -match "--lane $lane") `
        "gate=$gateFile hook=$gateHook"
    # THE ONE THAT MATTERS. A hook whose command cannot even start is a fail-open that never reaches
    # Dodona's code to be logged -- and under layer 1 that is a write into the operator's live
    # checkout. So the exe this live agent's gate names has to still be on disk after the swap.
    $gateExe = ''
    if ($gateHook -match '^"([^"]+)"') { $gateExe = $Matches[1] }
    Check 'the_gate_exe_of_a_live_lane_survives_the_swap' ($gateExe -ne '' -and (Test-Path $gateExe)) `
        "exe=$gateExe hook=$gateHook"

    # ================= old build directories are collected, EXCEPT the ones still in use =========
    # `binary_gc_kept` is the retention above, observed from the other side: SKY is still alive and
    # its gate names the build it was launched from, so that directory must NOT have been collected
    # while every other old one was. Asserting only "one directory left" would have made the
    # retention look like a GC bug.
    # Both scaffolding directories out: `bogus` is the no-exe swap target, and the future stamp is
    # the concurrent-publish decoy. Neither is a build this suite made.
    $dirs = @(Get-ChildItem -Directory $binRoot | Where-Object { $_.Name -notin @('bogus', '29991231-235959') })
    $kept = [int](Sql "SELECT COUNT(*) FROM events WHERE kind='binary_gc_kept' ").Trim()

    # A PUBLISH IN FLIGHT IS NOT GARBAGE. The decoy planted before the first swap carries a stamp
    # newer than the running build, so it is either a publish still copying or a successor about
    # to take over -- and the GC deleting it is what produced, on the operator's machine:
    #
    #   building Dodona -> ...\bin\20260820-222405        (succeeded, exe verified)
    #   error: no such binary: ...\bin\20260820-222405\dodona.exe
    #
    # ...plus, when a file was locked partway through the delete, a half-deleted corpse that is
    # newest-by-name and has no dodona.dll -- which is what any "resolve the installed binary"
    # snippet then picks. TWO assertions, because either alone is worthless: the decoy SURVIVED,
    # and the GC still ran and still collected something (`old_builds_gcd_except_those_a_live_gate
    # _names` above is the other half). A GC that kept everything would pass a survival check while
    # being a worse bug than the one being fixed.
    # ONE invocation each, landed in variables first: a detail string that recomputes what it
    # asserted on can disagree with the assertion, and nested double quotes inside an interpolated
    # string is its own trap in PS 5.1.
    $decoyThere = Test-Path (Join-Path $futureDir 'dodona.exe')
    $decoyKept = [int](Sql "SELECT COUNT(*) FROM events WHERE kind='binary_gc_kept' AND detail LIKE '%29991231%'").Trim()
    $keptRows = (Sql "SELECT detail FROM events WHERE kind='binary_gc_kept'") -replace '\s+', ' '
    Check 'a_build_directory_newer_than_the_running_one_survives_the_gc' `
        ($decoyThere -and $decoyKept -ge 1) "exists=$decoyThere kept-events=$decoyKept rows=[$keptRows]"
    # Asserted as "retention HAPPENED", not "at most two directories": SKY is alive (proved two
    # checks up) and its gate names the build it was launched from, so a run where nothing was kept
    # means the retention never fired and this check would be agreeing with a collected exe.
    Check 'old_builds_gcd_except_those_a_live_gate_names' `
        ($kept -ge 1 -and $dirs.Count -le 2) "dirs=$($dirs.Name -join ',') kept-events=$kept"
    # And the retained directory is the one GATEKEEP's gate actually needs -- still on disk after
    # two swaps. This is the 2026-08-18 incident stated correctly: not "the gate points at the
    # running build", which a live agent can never be made to do, but "the build the live agent's
    # gate points at is still there".
    Check 'the_retained_build_is_the_one_a_live_gate_needs' `
        ($keepExe -ne '' -and (Test-Path $keepExe)) "exe=$keepExe"

    # ================= start-on-demand: the daemon is summoned, not served =================
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down before autostart is tested' | Out-Null
    # `status` NO LONGER SUMMONS a daemon, so autostart is provoked by a command that does.
    # These three checks are about AUTOSTART; they leaned on `status` merely because it happened
    # to be a summoning command, and that coupling hid what they were really asserting. Two
    # explicit steps -- summon, then ask -- says it properly.
    #
    # Why status changed: it is what people reach for as a health check, and a summoned daemon
    # runs its warm-up, which spawns the router, brain and compressor pool -- four real
    # `claude -p --model haiku` processes. CLAUDE.md 3.2 warned about it in prose and a session
    # did it anyway, twice, against the operator's live workspace.
    #
    # AND IT HAPPENED AGAIN, TO `tickets`, WHICH IS WHY THIS IS `focus` (issue #13). The
    # substitute chosen above was picked the same way the original was -- a command that
    # happened to summon -- and `tickets` is a pure read, so #13 stopped it summoning too and
    # these three checks went red on a correct product. The general trap, worth more than either
    # instance: PROVOKE A BEHAVIOUR WITH A COMMAND THAT MEANS IT. `focus` changes something, so
    # it summons because of what it IS, and no future correction to a reading can reach it.
    Dodona @("focus", "$lane") | Out-Null
    Wait-Daemon $ws.CtlPipe | Out-Null
    $revived = Dodona @("status")
    Check 'autostart_summons_daemon' ($revived -match 'daemon pid=(\d+)' -and [int]$Matches[1] -ne $newPid2) $revived
    Check 'autostart_reconnects_lane' ($revived -match 'SKY' -and $revived -match 'connected=True') $revived
    $rt2 = "AFTERAUTOSTART-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    Dodona @("say", "$lane", "say $rt2") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match [regex]::Escape($rt2) } 25000 'the agent answers after autostart' | Out-Null
    Check 'agent_answers_after_autostart' ((Dodona @("tail", "$lane", "10")) -match [regex]::Escape($rt2)) ''

    $env:DODONA_NO_AUTOSTART = "1"
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 20000 'the daemon is down' | Out-Null
    # A SUMMONING command, for the same reason: a READING would now refuse whether autostart is
    # disabled or not, so asking one here would prove nothing about DODONA_NO_AUTOSTART. It said
    # `status` and used `tickets`, and issue #13 made `tickets` a reading -- so this check
    # silently became the vacuous thing its own comment warns against. `focus` acts, and the
    # refusal it gets is the generic one, which is what only the disabled path produces.
    $noauto = Dodona @("focus", "$lane")
    Check 'autostart_can_be_disabled' ($DODONA_EXIT -ne 0 -and $noauto -match 'daemon not running') $noauto
}
finally {
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($shimInfo) { foreach ($p in @($shimInfo.shimPid, $shimInfo.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } } }
    # By the PATH this run owns, never by the name dodona.exe (CLAUDE.md §4, and P4.2). The
    # old query enumerated every dodona.exe on the machine and narrowed on a COMMAND LINE
    # substring afterwards -- which is one empty $root away from matching, and stopping,
    # the operator's live daemon. $bin and $binRoot are this run's own GUID temp directories,
    # so nothing outside this suite can be in them.
    Stop-ProcessesUnder $bin
    if ($binRoot) { Stop-ProcessesUnder $binRoot }
    if ($d1 -and -not $d1.HasExited) { try { Stop-Process -Id $d1.Id -Force } catch { } }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item $binRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_BIN_ROOT -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M4 ACCEPTANCE (hot swap, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
