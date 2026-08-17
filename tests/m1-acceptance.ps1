# M1 acceptance (design §16): claims + hook gate + fenced merge token + verify config.
# Runs on a scripted git fixture — zero model calls. The test performs the git work a
# real agent would (commit, rebase), and asserts on every §6/§7 behavior:
#   plan-time conflict detection, disjoint parallelism, hook-gate allow/deny,
#   on-approval gating, token FIFO serialization, lease expiry fencing,
#   ff-only land discipline, claim release on land, post-land verify.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$out = Join-Path $PSScriptRoot 'm1-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# ---- git fixture ----
$root = Join-Path $env:TEMP ("dodona-m1-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src\water", "$root\src\sky" | Out-Null
Set-Content "$root\src\water\sim.cs" "// water sim"
Set-Content "$root\src\sky\box.cs" "// skybox"
Set-Content "$root\README.md" "fixture"
Set-Content "$root\.gitignore" ".dodona/"
Set-Content "$root\dodona.json" '{ "main": "main", "verify": ["echo verify-ok"] }'
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $global:DODONA_EXIT = 0; $o = (& $dodona ($a + @('--root', $root))) | Out-String; $global:DODONA_EXIT = $LASTEXITCODE; $o.Trim() }
function Check([string]$name, [bool]$cond, [string]$detail = '') {
    $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() }
}

$daemon = $null
try {
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800

    # ---- 1. ticket WATER claims src/water ----
    $t1 = Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water")
    Check 'ticket1_created' ($t1 -match 'ticket 1 branch ticket/1') $t1
    Check 'worktree1_exists' (Test-Path "$root\.dodona\wt\t1\src\water\sim.cs")
    Check 'gate_deployed' ((Test-Path "$root\.dodona\wt\t1\.claude\settings.json") -and (Test-Path "$root\.dodona\wt\t1\dodona-gate.ps1"))

    # ---- 2. overlapping claim refused at plan time (§6) ----
    $t2bad = Dodona @("ticket-create", "--title", "WATER2", "--claim", "path:src/water/sim.cs")
    Check 'overlap_refused_at_plan_time' ($DODONA_EXIT -eq 1 -and $t2bad -match 'conflict: .*ticket 1') $t2bad

    # ---- 3. disjoint claim runs in parallel ----
    $t2 = Dodona @("ticket-create", "--title", "SKY", "--claim", "subtree:src/sky")
    Check 'disjoint_parallel' ($t2 -match 'ticket (\d+) branch') $t2
    if ($t2 -match 'ticket (\d+) ') { $t2id = $Matches[1] } else { $t2id = 0 }

    # ---- 4. hook gate: allow inside claim, deny outside (§6 layer 1) ----
    $wt1 = "$root\.dodona\wt\t1"
    $inJson  = @{ tool_name = 'Write'; tool_input = @{ file_path = "$wt1\src\water\sim.cs" } } | ConvertTo-Json -Compress
    $outJson = @{ tool_name = 'Write'; tool_input = @{ file_path = "$wt1\src\sky\box.cs" } }   | ConvertTo-Json -Compress
    $allow = $inJson  | powershell -NoProfile -ExecutionPolicy Bypass -File "$wt1\dodona-gate.ps1" | Out-String
    $deny  = $outJson | powershell -NoProfile -ExecutionPolicy Bypass -File "$wt1\dodona-gate.ps1" | Out-String
    Check 'gate_allows_inside_claim' (-not ($allow -match 'deny'))
    Check 'gate_denies_outside_claim' ($deny -match '"permissionDecision":"deny"') $deny

    # ---- 5. agent work: commit in wt1 (the test IS the agent at the git layer) ----
    Set-Content "$wt1\src\water\sim.cs" "// water sim v2"
    git -C $wt1 add -A
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "water v2"

    # ---- 6. on-approval gates the token (§7) ----
    $req = Dodona @("token-request", "1")
    Check 'unapproved_token_refused' ($DODONA_EXIT -eq 1 -and $req -match 'not approved') $req

    Dodona @("approve", "1") | Out-Null
    $req = Dodona @("token-request", "1")
    Check 'approved_token_granted' ($req -match 'granted ticket 1') $req

    # ---- 7. second ticket queues behind the holder (FIFO serialization) ----
    Dodona @("approve", "$t2id") | Out-Null
    $req2 = Dodona @("token-request", "$t2id")
    Check 'second_ticket_queued' ($req2 -match 'queued') $req2

    # ---- 8. land ticket 1: daemon executes ff-only; claims released; verify runs ----
    $land1 = Dodona @("land", "1")
    Check 'ticket1_landed' ($land1 -match 'landed ticket 1') $land1
    Check 'verify_ran_green' ($land1 -match 'verify green') $land1
    $mainTip = git -C $root log -1 --format=%s
    Check 'main_advanced' ($mainTip -eq 'water v2') $mainTip
    Check 'worktree1_pruned' (-not (Test-Path $wt1))

    # ---- 9. released claim is claimable again ----
    $t3 = Dodona @("ticket-create", "--title", "WATER-NEXT", "--claim", "subtree:src/water")
    Check 'released_claim_reclaimable' ($t3 -match 'ticket \d+ branch') $t3

    # ---- 10. queued ticket now gets the token; stale branch must rebase (§7) ----
    $req2 = Dodona @("token-request", "$t2id")
    Check 'queued_ticket_now_granted' ($req2 -match "granted ticket $t2id") $req2
    $wt2 = "$root\.dodona\wt\t$t2id"
    Set-Content "$wt2\src\sky\box.cs" "// skybox v2"
    git -C $wt2 add -A
    git -C $wt2 -c user.email=t@t -c user.name=t commit -q -m "sky v2"
    $landStale = Dodona @("land", "$t2id")
    Check 'stale_branch_refused_ff_only' ($DODONA_EXIT -eq 1 -and $landStale -match 'not fast-forward') $landStale
    git -C $wt2 -c user.email=t@t -c user.name=t rebase -q main | Out-Null
    $land2 = Dodona @("land", "$t2id")
    Check 'rebased_branch_lands' ($land2 -match "landed ticket $t2id") $land2

    # ---- 11. lease expiry fences a dead holder (§7/§12) ----
    $t4 = Dodona @("ticket-create", "--title", "EXPIRY", "--claim", "path:README.md")
    if ($t4 -match 'ticket (\d+) ') { $t4id = $Matches[1] }
    Dodona @("approve", "$t4id") | Out-Null
    Dodona @("token-request", "$t4id", "--lease", "1") | Out-Null
    Start-Sleep -Seconds 2
    $wt4 = "$root\.dodona\wt\t$t4id"
    Set-Content "$wt4\README.md" "fixture v2"
    git -C $wt4 add -A
    git -C $wt4 -c user.email=t@t -c user.name=t commit -q -m "readme v2"
    git -C $wt4 -c user.email=t@t -c user.name=t rebase -q main | Out-Null
    $landExpired = Dodona @("land", "$t4id")
    Check 'expired_lease_cannot_land' ($DODONA_EXIT -eq 1 -and $landExpired -match 'expired') $landExpired
    Dodona @("token-request", "$t4id") | Out-Null      # expired holder reclaimed, re-granted
    $landRetry = Dodona @("land", "$t4id")
    Check 'regrant_after_expiry_lands' ($landRetry -match "landed ticket $t4id") $landRetry

    # ---- 12. the causal chain is in the store (§12) ----
    $events = (python -c "
import sqlite3
db = sqlite3.connect(r'$root\.dodona\store.db')
print('\n'.join(k for (k,) in db.execute('SELECT kind FROM events ORDER BY id')))
") | Out-String
    foreach ($k in 'ticket_created','claim_conflict','token_refused_unapproved','token_granted','token_queued','landed','verify_green','token_expired_reclaimed','worktree_pruned') {
        Check "event_$k" ([bool]($events -match $k))
    }

    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    Copy-Item "$root\.dodona\store.db" "$out\store.db" -ErrorAction SilentlyContinue
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M1 ACCEPTANCE ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
