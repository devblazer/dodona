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
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$out = Join-Path $PSScriptRoot 'm4-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# Published builds go to a scratch bin root, not the machine-wide one: tests collide with
# nothing (§17), and the GC check gets a directory it owns.
$binRoot = Join-Path $env:TEMP ("dodona-bin-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$env:DODONA_BIN_ROOT = $binRoot

$root = Join-Path $env:TEMP ("dodona-m4-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
    (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('''$q'''): print('|'.join(str(x) for x in r))
") | Out-String
}

$d1 = $null
$shimInfo = $null
$token = "SURVIVED-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
try {
    # ================= the swap test: mid-turn handoff =================
    $d1 = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\d1.out" -RedirectStandardError "$out\d1.err"
    Start-Sleep -Milliseconds 800

    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $lane = $Matches[1]
    $shimInfo = Get-Content "$wsDir\shim-lane$lane.json" | ConvertFrom-Json
    # Read THIS lane's session, not the last one printed: the dispatcher lane the swap
    # creates has no session, and a greedy match would happily take its dash.
    function LaneSession { ((Dodona @("status")) -split "`r?`n" | Where-Object { $_ -match "^lane $lane\b" }) -replace '.*session=(\S+).*', '$1' }
    $sessionBefore = LaneSession

    # a 6-second turn: the swap happens squarely inside it
    Dodona @("say", "$lane", "sleep:6 then say $token") | Out-Null
    Start-Sleep -Milliseconds 1000

    $publish = Dodona @("publish", "--project", $repo)
    Set-Content "$out\publish.txt" $publish
    Check 'publish_builds_versioned_dir' (@(Get-ChildItem -Directory $binRoot -ErrorAction SilentlyContinue).Count -eq 1) $publish
    Check 'publish_hands_off' ($publish -match 'handed off to build') $publish

    # daemon #1 must be GONE (it exited after the handoff) and #2 must own the pipe
    Start-Sleep -Seconds 3
    Check 'old_daemon_exited' ($d1.HasExited) "pid $($d1.Id) still alive"
    $status = Dodona @("status")
    Set-Content "$out\status-after-swap.txt" $status
    $newPid = if ($status -match 'daemon pid=(\d+)') { [int]$Matches[1] } else { -1 }
    Check 'new_daemon_serves' ($newPid -gt 0 -and $newPid -ne $d1.Id) $status
    Check 'new_daemon_is_published_build' ($status -match [regex]::Escape($binRoot)) $status

    # the agent never noticed: shim and child are the SAME processes as before
    Check 'shim_survived_swap' ([bool](Get-Process -Id $shimInfo.shimPid -ErrorAction SilentlyContinue)) ''
    Check 'agent_survived_swap' ([bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)) ''

    # the turn that was in flight during the swap: landed, exactly once
    Start-Sleep -Seconds 5
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
    Start-Sleep -Seconds 2
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

    # ================= blocked swap: three answers, and "when it lands" =================
    Dodona @("ticket-create", "--title", "MERGE", "--claim", "path:src/a.cs") | Out-Null
    Dodona @("approve", "1") | Out-Null
    $grant = Dodona @("token-request", "1", "--lease", "300")
    if ($grant -notmatch 'granted') { throw "token-request failed: $grant" }

    $publish2 = Dodona @("publish", "--project", $repo)
    Set-Content "$out\publish2.txt" $publish2
    Check 'midmerge_blocks_swap' ($publish2 -match 'MERGE is mid-merge' -and $publish2 -match 'swap-answer now \| when-it-lands \| hold') $publish2
    Check 'blocker_announced_with_three_answers' ((Sql "SELECT body FROM pane_events WHERE kind='announcement' ORDER BY id") -match 'update ready.*mid-merge.*swap now / when it lands / hold') ''
    Check 'daemon_did_not_swap_while_blocked' ((Dodona @("status")) -match "daemon pid=$newPid") ''

    $held = Dodona @("swap-answer", "hold")
    Check 'hold_parks_the_swap' ($held -match 'parked' -and (Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'held') $held

    $armed = Dodona @("swap-answer", "when-it-lands")
    Check 'when_it_lands_arms' ($armed -match 'armed' -and (Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'armed') $armed
    Check 'armed_but_not_yet_swapped' ((Dodona @("status")) -match "daemon pid=$newPid") ''

    # defer to a CONDITION, not a timer: release the token and it must swap itself
    Dodona @("token-release", "1") | Out-Null
    $swapped = $false
    foreach ($i in 1..15) {
        Start-Sleep -Seconds 1
        $s = Dodona @("status")
        if ($s -match 'daemon pid=(\d+)' -and [int]$Matches[1] -ne $newPid) { $newPid2 = [int]$Matches[1]; $swapped = $true; break }
    }
    Check 'armed_swap_fires_when_blocker_clears' $swapped "still pid $newPid after 15s"
    Check 'armed_swap_recorded' ((Sql "SELECT state FROM swaps ORDER BY id DESC LIMIT 1") -match 'swapped') ''
    Check 'agent_survived_second_swap' ([bool](Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)) ''

    # ================= old build directories are collected =================
    $dirs = @(Get-ChildItem -Directory $binRoot | Where-Object { $_.Name -ne 'bogus' })
    Check 'old_builds_gcd' ($dirs.Count -eq 1) "dirs=$($dirs.Name -join ',')"

    # ================= start-on-demand: the daemon is summoned, not served =================
    Dodona @("stop-daemon") | Out-Null
    Start-Sleep -Seconds 2
    $revived = Dodona @("status")
    Check 'autostart_summons_daemon' ($revived -match 'daemon pid=(\d+)' -and [int]$Matches[1] -ne $newPid2) $revived
    Check 'autostart_reconnects_lane' ($revived -match 'SKY' -and $revived -match 'connected=True') $revived
    $rt2 = "AFTERAUTOSTART-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    Dodona @("say", "$lane", "say $rt2") | Out-Null
    Start-Sleep -Seconds 2
    Check 'agent_answers_after_autostart' ((Dodona @("tail", "$lane", "10")) -match [regex]::Escape($rt2)) ''

    $env:DODONA_NO_AUTOSTART = "1"
    Dodona @("stop-daemon") | Out-Null
    Start-Sleep -Seconds 1
    $noauto = Dodona @("status")
    Check 'autostart_can_be_disabled' ($DODONA_EXIT -ne 0 -and $noauto -match 'daemon not running') $noauto
}
finally {
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($shimInfo) { foreach ($p in @($shimInfo.shimPid, $shimInfo.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } } }
    Get-CimInstance Win32_Process -Filter "Name='dodona.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$root*" } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
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
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M4 ACCEPTANCE (hot swap, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
