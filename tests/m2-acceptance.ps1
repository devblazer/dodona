# M2 acceptance, model-free half: merge-time claim backstop (§6 layer 2), code-derived
# presence (§5), tier-0 prefix routing + focus routing with routing_decisions rows (§4).
# Fake agents only — zero model calls.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm2'
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in
$out = Join-Path $PSScriptRoot 'm2-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path $env:TEMP ("dodona-m2-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
    Start-Sleep -Milliseconds 800

    # ---- backstop: branch touching outside its claim cannot get the token ----
    Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water") | Out-Null
    $wt1 = "$root\.dodona\wt\t1"
    Set-Content "$wt1\src\water\sim.cs" "// water v2"
    Set-Content "$wt1\src\sky\box.cs" "// SNEAKY out-of-claim edit"
    git -C $wt1 add -A
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "water + sneaky sky"
    Dodona @("approve", "1") | Out-Null
    $req = Dodona @("token-request", "1")
    Check 'backstop_refuses_outside_claim' ($DODONA_EXIT -eq 1 -and $req -match 'outside ticket 1' -and $req -match 'src/sky/box.cs') $req

    # extend the claim -> backstop satisfied
    Dodona @("claim-extend", "1", "--claim", "path:src/sky/box.cs") | Out-Null
    $req = Dodona @("token-request", "1")
    Check 'backstop_passes_after_extend' ($req -match 'granted ticket 1') $req
    Dodona @("token-release", "1") | Out-Null

    # ---- presence derived from tool events, in code ----
    $ls = Dodona @("lane-start", "--title", "SKY", "--child", $fake)
    if ($ls -match 'lane (\d+)') { $sky = $Matches[1] } else { throw "lane-start failed: $ls" }
    Dodona @("say", "$sky", "tool:Write:src/sky/box.cs sleep:2 then say presence-done") | Out-Null
    Start-Sleep -Milliseconds 900
    $status = Dodona @("status")
    Check 'presence_shows_tool' ($status -match 'presence=write: box.cs') $status
    Start-Sleep -Seconds 2
    $status = Dodona @("status")
    Check 'presence_idle_after_result' ($status -match 'presence=idle') $status

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
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M2 ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
