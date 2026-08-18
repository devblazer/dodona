# M3 acceptance: the UI is a dumb view over the store (§8, §17). Fake agents, zero model
# calls. Assertions are `dodona ui dump` JSON (the UI testifies about what it shows),
# store rows, and screenshot pixel dimensions — no human eye required.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm3'
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$ui = "$repo\src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in
$out = Join-Path $PSScriptRoot 'm3-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-m3-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src\water", "$root\src\sky" | Out-Null
Set-Content "$root\src\water\sim.cs" "// water"
Set-Content "$root\src\sky\box.cs" "// sky"
Set-Content "$root\.gitignore" ".dodona/"
# agent = fake: lane-respawn spawns from config, and a suite must never start real claude
Set-Content "$root\dodona.json" (@{ main = 'main'; agent = $fake; compressors = 0 } | ConvertTo-Json)
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $global:DODONA_EXIT = 0; $o = (& $dodona ($a + @('--root', $root))) | Out-String; $global:DODONA_EXIT = $LASTEXITCODE; $o.Trim() }
function Dump() { Dodona @('ui', 'dump') | ConvertFrom-Json }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function PngDims([string]$path) {
    # [int] casts matter: -shl on a [byte] stays a byte and overflows to zero.
    $b = [IO.File]::ReadAllBytes($path)
    $w = ([int]$b[16] -shl 24) + ([int]$b[17] -shl 16) + ([int]$b[18] -shl 8) + [int]$b[19]
    $h = ([int]$b[20] -shl 24) + ([int]$b[21] -shl 16) + ([int]$b[22] -shl 8) + [int]$b[23]
    "$($w)x$($h)"
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
    Start-Sleep -Milliseconds 800

    # Lanes: a ticket-agent lane (WATER, linked to ticket 1 — the fake agent ignores the
    # claude argv, which is exactly the §17 test seam) and a plain lane (SKY).
    Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water") | Out-Null
    $ta = Dodona @("ticket-agent", "1", "--child", $fake)
    if ($ta -notmatch 'lane (\d+)') { throw "ticket-agent failed: $ta" }
    $waterLane = $Matches[1]
    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -notmatch 'lane (\d+)') { throw "lane-start failed: $ls" }
    $skyLane = $Matches[1]
    Dodona @("say", "$waterLane", "say water ready") | Out-Null
    Dodona @("say", "$skyLane", "say sky ready") | Out-Null

    # ---- the UI is a replay of rows: launch AFTER the messages exist ----
    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Start-Sleep -Milliseconds 1800

    $d = Dump
    $water = $d.slots | Where-Object { -not $_.empty -and $_.title -eq 'WATER' }
    $sky = $d.slots | Where-Object { -not $_.empty -and $_.title -eq 'SKY' }
    Check 'panes_replay_store_rows' ($null -ne $water -and $null -ne $sky -and
        ($water.lines -join '|') -match 'water ready' -and ($sky.lines -join '|') -match 'sky ready') ($d.slots | ConvertTo-Json -Compress)
    Check 'fixed_slots_and_colors' ($water.slot -eq 0 -and $sky.slot -eq 1 -and $water.color -ne $sky.color) "water=$($water.slot) sky=$($sky.slot)"
    Check 'presence_idle_after_result' ($water.presence -eq 'idle') $water.presence

    # ---- a click and a pipe message are the same thing: route via daemon, see it in the UI ----
    Dodona @("focus", "$skyLane") | Out-Null
    Dodona @("input", "say focus works") | Out-Null
    Start-Sleep -Milliseconds 900
    $d = Dump
    $sky = $d.slots | Where-Object { $_.title -eq 'SKY' }
    Check 'routed_input_reaches_pane' (($sky.lines -join '|') -match 'you> say focus works') ($sky.lines -join '|')
    Check 'focus_marked_in_ui' ($sky.focused -eq $true -and ($d.slots | Where-Object { $_.title -eq 'WATER' }).focused -ne $true) ''

    # ---- blocked-on-you (§8): refused merge -> border+glyph+badge+presence+feed row ----
    Dodona @("token-request", "1") | Out-Null      # on-approval, unapproved -> refused
    Start-Sleep -Milliseconds 900
    $d = Dump
    $water = $d.slots | Where-Object { $_.title -eq 'WATER' }
    Check 'blocked_state_in_pane' ($water.blocked -eq $true -and $water.presence -eq 'waiting on you: merge' -and $water.badge -eq 1) ($water | ConvertTo-Json -Compress)
    $feedRow = $d.feed | Where-Object { $_.body -match 'waiting on you: merge ticket 1' } | Select-Object -First 1
    Check 'blocked_lands_in_feed' ($null -ne $feedRow -and $feedRow.acked -eq $false) ($d.feed | ConvertTo-Json -Compress)

    # ---- ack clears the badge but the row stays (greyed), never deleted ----
    Dodona @("ack", "$($feedRow.id)") | Out-Null
    Start-Sleep -Milliseconds 900
    $d = Dump
    $water = $d.slots | Where-Object { $_.title -eq 'WATER' }
    $acked = $d.feed | Where-Object { $_.id -eq $feedRow.id }
    Check 'ack_clears_badge_keeps_row' ($water.badge -eq 0 -and $acked.acked -eq $true) ($d.feed | ConvertTo-Json -Compress)

    # ---- approve unblocks: presence back to idle, receipt announced ----
    Dodona @("approve", "1") | Out-Null
    Start-Sleep -Milliseconds 900
    $d = Dump
    $water = $d.slots | Where-Object { $_.title -eq 'WATER' }
    Check 'approve_unblocks_lane' ($water.blocked -eq $false -and $water.presence -eq 'idle' -and
        (@($d.feed | Where-Object { $_.body -match 'approved' }).Count -ge 1)) ($water | ConvertTo-Json -Compress)

    # ---- undo-route (§4): undo is labeled data + a retraction the agent can act on ----
    Dodona @("input", "say oops wrong lane") | Out-Null
    Start-Sleep -Milliseconds 400
    $route = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print(db.execute('SELECT id FROM routing_decisions ORDER BY id DESC LIMIT 1').fetchone()[0])
") | Out-String
    Dodona @("undo-route", $route.Trim()) | Out-Null
    Start-Sleep -Milliseconds 400
    $tail = Dodona @("tail", "$skyLane", "10")
    $undone = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print(db.execute('SELECT undone FROM routing_decisions ORDER BY id DESC LIMIT 1').fetchone()[0])
") | Out-String
    Check 'undo_route_retracts' ($tail -match 'Disregard' -and $undone.Trim() -eq '1') "undone=$($undone.Trim()) tail=$tail"

    # ---- M5.1: a respawned TICKET lane comes back in its WORKTREE ----
    # This is the bug that made the whole M5 plan urgent. RespawnLaneAsync hardcoded
    # `_primary` and always rebuilt the PLAIN-lane system prompt, so a ticket agent that was
    # respawned -- by `lane-respawn`, or by the wake path after a night -- came back running
    # in the operator's live working copy while still being told "your worktree is the
    # current working directory; work only there". A gated agent, resumed, editing main's
    # tree.
    #
    # Two things this check learned the hard way, both of which made the FIRST draft of it
    # pass against the unfixed binary:
    #   1. `lane-respawn` REFUSES a lane whose agent is still connected, so the naive version
    #      respawned nothing and then happily asserted on the ORIGINAL ticket-agent spawn
    #      event -- whose cwd is the worktree either way. Stop the agent first.
    #   2. Asserting only on "the newest shim_spawned row" cannot tell a fresh spawn from an
    #      old one. Capture the row id first and require a NEWER one, or the check cannot fail.
    # Placed BEFORE the land below on purpose: landing prunes the worktree, after which
    # falling back to the primary is the only correct answer, so the open-ticket window is
    # the only place the interesting claim exists at all.
    $spawnsBefore = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
r = db.execute('SELECT MAX(id) FROM events WHERE kind=? AND lane_id=?', ('shim_spawned', $waterLane)).fetchone()
print(0 if r is None or r[0] is None else r[0])
") | Out-String
    Dodona @("lane-stop", "$waterLane") | Out-Null
    Start-Sleep -Milliseconds 900
    Dodona @("lane-respawn", "$waterLane") | Out-Null
    Start-Sleep -Milliseconds 1200
    $spawn = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
r = db.execute('SELECT id, detail FROM events WHERE kind=? AND lane_id=? ORDER BY id DESC LIMIT 1', ('shim_spawned', $waterLane)).fetchone()
print('|' if r is None else str(r[0]) + '|' + r[1])
") | Out-String
    $spawnId = [int]($spawn.Trim() -split '\|', 2)[0]
    $spawnDetail = ($spawn.Trim() -split '\|', 2)[1]
    Check 'respawn_actually_respawned' `
        ($spawnId -gt [int]$spawnsBefore.Trim()) "before=$($spawnsBefore.Trim()) after=$spawnId"
    $wantCwd = "cwd=$root\.dodona\wt\t1"
    Check 'respawned_ticket_lane_returns_to_its_worktree' `
        ($spawnDetail.EndsWith($wantCwd)) "want '$wantCwd' got '$spawnDetail'"
    Check 'respawned_ticket_lane_is_not_in_the_live_tree' `
        (-not $spawnDetail.EndsWith("cwd=$root")) $spawnDetail

    # ---- landing retires the agent, the lane survives, wake resumes it ----
    # (docs/LANE-LIFECYCLE.md §3: the prune deletes the directory the agent stands in, so
    # an agent left running there was the most confusing state the system could be in.)
    $wt1 = "$root\.dodona\wt\t1"
    Set-Content "$wt1\src\water\sim.cs" "// landed work"
    git -C $wt1 add -A
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "water work"
    $shimInfo = Get-Content "$wsDir\shim-lane$waterLane.json" | ConvertFrom-Json
    Dodona @("token-request", "1") | Out-Null
    $land = Dodona @("land", "1")
    Check 'land_succeeds' ($land -match 'landed ticket 1') $land
    Start-Sleep -Seconds 2
    Check 'land_retires_the_agent' (-not (Get-Process -Id $shimInfo.childPid -ErrorAction SilentlyContinue)) "agent pid $($shimInfo.childPid) still alive"
    $d = Dump
    $water = $d.slots | Where-Object { -not $_.empty -and $_.title -eq 'WATER' }
    Check 'lane_survives_as_dormant' ($water.state -eq 'dormant') ($water | ConvertTo-Json -Compress)
    Check 'lane_keeps_its_slot_and_thread' ($water.slot -eq 0 -and (($water.lines) -join '|') -match 'water ready') ''

    # wake: a fresh agent resumes the thread (fake = fresh session; real claude = --resume)
    Dodona @("lane-respawn", "$waterLane") | Out-Null
    Start-Sleep -Milliseconds 900
    Dodona @("say", "$waterLane", "say woke up fine") | Out-Null
    Start-Sleep -Milliseconds 900
    $d = Dump
    $water = $d.slots | Where-Object { -not $_.empty -and $_.title -eq 'WATER' }
    Check 'wake_revives_the_lane' ($water.state -eq 'alive' -and (($water.lines) -join '|') -match 'woke up fine') ($water | ConvertTo-Json -Compress)

    # ---- overlay maximize: verb-driven, grid never reflows underneath ----
    Dodona @("ui", "overlay", "WATER") | Out-Null
    Start-Sleep -Milliseconds 700
    $d = Dump
    Check 'overlay_opens' ($d.overlay -eq 'WATER') $d.overlay
    Dodona @("ui", "overlay", "off") | Out-Null
    $d = Dump
    Check 'overlay_closes' ($null -eq $d.overlay) "$($d.overlay)"

    # ---- screenshots are deterministic pixels (§17): fixed 1600x900, pane-scoped works ----
    Dodona @("ui", "screenshot", "--out", "$out\live.png") | Out-Null
    Check 'screenshot_fixed_size' ((PngDims "$out\live.png") -eq '1600x900') (PngDims "$out\live.png")
    Dodona @("ui", "screenshot", "--pane", "WATER", "--out", "$out\live-water.png") | Out-Null
    Check 'pane_screenshot_writes' ((Test-Path "$out\live-water.png") -and (Get-Item "$out\live-water.png").Length -gt 1000) ''

    # ---- poses (§17): each visual state on demand, deterministic, distinct ----
    $poseHashes = @{}
    # 'collapsed' replaced 'empty-slot': with a grid that divides itself there are no empty
    # placeholders left to pose. 'two' and 'twelve' are new, and are the fixtures that catch a
    # layout which only looks right at six.
    foreach ($pose in 'full', 'badges', 'blocked', 'collapsed', 'tray', 'overlay', 'two', 'twelve') {
        Dodona @("ui", "pose", $pose) | Out-Null
        Dodona @("ui", "screenshot", "--out", "$out\pose-$pose.png") | Out-Null
        $poseHashes[$pose] = (Get-FileHash "$out\pose-$pose.png").Hash
    }
    Check 'poses_render_distinct' (($poseHashes.Values | Select-Object -Unique).Count -eq 8) (($poseHashes.Values | Select-Object -Unique).Count)

    Dodona @("ui", "pose", "blocked") | Out-Null
    $d = Dump
    $posedBlocked = $d.slots | Where-Object { -not $_.empty -and $_.blocked }
    Check 'pose_blocked_testifies' ($d.pose -eq 'blocked' -and $posedBlocked.title -eq 'SKYBOX' -and $d.toasts.Count -eq 1) ($d.toasts | ConvertTo-Json -Compress)

    # pose determinism: same pose, same pixels
    Dodona @("ui", "pose", "full") | Out-Null
    Dodona @("ui", "screenshot", "--out", "$out\pose-full-2.png") | Out-Null
    Check 'pose_screenshot_deterministic' ((Get-FileHash "$out\pose-full-2.png").Hash -eq $poseHashes['full']) ''

    # ---- pose live returns to the store's truth ----
    Dodona @("ui", "pose", "live") | Out-Null
    Start-Sleep -Milliseconds 900
    $d = Dump
    Check 'pose_live_resumes_store' ($null -eq $d.pose -and (($d.slots | Where-Object { -not $_.empty }).title -contains 'WATER')) ($d.pose)

    # ---- clean close, exit-code honest ----
    Dodona @("ui", "close") | Out-Null
    Start-Sleep -Milliseconds 1200
    Check 'ui_close_exits_process' ($uiProc.HasExited) ''

    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
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
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M3 ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
