# M1 acceptance (design §16): claims + hook gate + fenced merge token + verify config.
# Runs on a scripted git fixture — zero model calls. The test performs the git work a
# real agent would (commit, rebase), and asserts on every §6/§7 behavior:
#   plan-time conflict detection, disjoint parallelism, hook-gate allow/deny,
#   on-approval gating, token FIFO serialization, lease expiry fencing,
#   ff-only land discipline, claim release on land, post-land verify.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'm1'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
#
# This suite deliberately sets NO $env:DODONA_SHIM, and that is the point rather than an
# omission: a shim spawned without it falls back to AppContext.BaseDirectory\DodonaShim.exe,
# which now resolves to the fresh copy beside dodona.exe. Under the old paths it resolved to
# src\Dodona\bin's orphan copy, which no ProjectReference maintains and which was measured 18
# hours stale on 2026-08-18. So m1 is also the check that the flat layout works.
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$env:DODONA_NO_AUTOSTART = "1"   # this test owns daemon lifetime; start-on-demand (M4) must not join in
$out = Join-Path $PSScriptRoot 'm1-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# ---- git fixture ----
$root = Join-Path (Use-SuiteTemp) ("dodona-m1-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
    # Where this workspace keeps its state. Not `<root>\.dodona` any more: a workspace
    # is named rather than located, so the suite asks the binary (see tests/_workspace.ps1).
    $ws = Get-WorkspacePaths $dodona $root
    $storeDb = $ws.Store
    $wsDir = $ws.Dir

    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Wait-Daemon $ws.CtlPipe | Out-Null

    # ---- 1. ticket WATER claims src/water ----
    $t1 = Dodona @("ticket-create", "--title", "WATER", "--claim", "subtree:src/water")
    Check 'ticket1_created' ($t1 -match 'ticket 1 branch ticket/1') $t1
    Check 'worktree1_exists' (Test-Path "$root\.dodona\wt\t1\src\water\sim.cs")
    # settings.LOCAL.json: merged over any tracked project settings.json, so a repo with
    # its own .claude/ keeps its hooks and never sees a dirty tracked file in the worktree
    # The gate is dodona.exe's own `gate-hook` subcommand now, not a generated .ps1: a script
    # that fails to parse denies NOTHING while still looking installed, and the same mistake in
    # C# cannot be shipped because it does not compile. So what must exist is the registration,
    # and what must NOT exist is a stale script that nobody is running any more.
    Check 'gate_deployed' ((Test-Path "$root\.dodona\wt\t1\.claude\settings.local.json") -and
        (-not (Test-Path "$root\.dodona\wt\t1\dodona-gate.ps1")))
    # and a repo's own tracked settings must be untouched by gate deployment
    Check 'tracked_settings_untouched' (-not (Test-Path "$root\.dodona\wt\t1\.claude\settings.json") -or
        ((git -C "$root\.dodona\wt\t1" status --porcelain ".claude/settings.json" | Out-String).Trim() -eq ''))

    # ---- 2. overlapping claim refused at plan time (§6) ----
    $t2bad = Dodona @("ticket-create", "--title", "WATER2", "--claim", "path:src/water/sim.cs")
    Check 'overlap_refused_at_plan_time' ($DODONA_EXIT -eq 1 -and $t2bad -match 'conflict: .*ticket 1') $t2bad

    # ---- 3. disjoint claim runs in parallel ----
    $t2 = Dodona @("ticket-create", "--title", "SKY", "--claim", "subtree:src/sky")
    Check 'disjoint_parallel' ($t2 -match 'ticket (\d+) branch') $t2
    if ($t2 -match 'ticket (\d+) ') { $t2id = $Matches[1] } else { $t2id = 0 }

    # ---- claim-extend must not report success for a claim it silently dropped (P0.5) ----
    # `specs.Select(Claims.Parse).Where(p => p is not null)` threw away every spec it could not
    # parse and then printed "extended ticket 1" with exit 0. So an agent that wrote
    # `--claim src/water/new.cs` (no `path:`) was told its claim had been widened, wrote the file,
    # and hit the gate -- or worse, was allowed to believe the claim covered work it did not.
    # All specs unparseable meant an empty list, an insert of nothing, and a success message.
    # ticket-create has always named a bad spec and refused; this now does too.
    $badExtend = Dodona @("claim-extend", "1", "--claim", "src/water/new.cs")
    Check 'claim_extend_refuses_a_spec_it_cannot_parse' `
        ($DODONA_EXIT -ne 0 -and $badExtend -match 'bad claim spec' -and $badExtend -notmatch 'extended ticket') $badExtend
    # and one good spec among bad ones does not smuggle the bad ones through as a success
    $mixedExtend = Dodona @("claim-extend", "1", "--claim", "path:src/water/ok.cs", "--claim", "nonsense")
    Check 'claim_extend_refuses_the_whole_batch_on_one_bad_spec' `
        ($DODONA_EXIT -ne 0 -and $mixedExtend -notmatch 'extended ticket') $mixedExtend

    # ---- 4. hook gate: allow inside claim, deny outside (§6 layer 1) ----
    $wt1 = "$root\.dodona\wt\t1"
    $inJson  = @{ tool_name = 'Write'; tool_input = @{ file_path = "$wt1\src\water\sim.cs" } } | ConvertTo-Json -Compress
    $outJson = @{ tool_name = 'Write'; tool_input = @{ file_path = "$wt1\src\sky\box.cs" } }   | ConvertTo-Json -Compress
    # Drive THE COMMAND CLAUDE CODE WOULD ACTUALLY RUN, read out of settings.local.json, rather
    # than a hard-coded path. This test used to invoke dodona-gate.ps1 directly, and when that
    # script stopped being generated the deny check went red while the ALLOW check went GREEN --
    # because a missing script makes PowerShell print a banner, and a banner contains no "deny".
    # A check that passes when the thing under test is absent is worth nothing (CLAUDE.md 0.3),
    # so it now exercises whatever is wired, whatever that turns out to be.
    $hookCmd = ((Get-Content "$wt1\.claude\settings.local.json" -Raw | ConvertFrom-Json).hooks.PreToolUse[0].hooks[0].command)
    Check 'gate_registration_names_a_command' ([bool]$hookCmd) "settings.local.json: $hookCmd"
    # STDERR IS CAPTURED, AND THE DENY IS A CONDITION-WAIT. Both because of what this check did
    # when the suites started running concurrently: it went red with an EMPTY detail, which said
    # only that the gate had not denied, never why. Worse, the ALLOW check passes on empty
    # output -- so a gate producing nothing at all reads as half green, the exact trap the
    # comment above already warns about.
    #
    # What the empty output actually meant, once it could be seen: `dodona gate-hook` asks the
    # daemon (`claim-check` over the ctl pipe) and DELIBERATELY FAILS OPEN on anything that is
    # not a clean allow/deny -- src/Dodona/Program.cs GateHook, "Anything else (no daemon, a
    # pipe error) is a fail-open, logged where the merge backstop's reader will look for it".
    # With eleven suites on the machine the pipe sometimes did not answer in time, so the gate
    # allowed the write and said nothing. That is the DESIGN (§6 layer 2, the merge-time diff
    # backstop, is what catches it), not a regression -- but it means this check has to
    # distinguish three outcomes, not two: denied, allowed, and could-not-ask.
    #
    # So: retry with a deadline, the same idiom as everything else in these suites, and if it
    # never denies, quote the bypass log the hook leaves behind. A retry is honest here because
    # the gate is deterministic once the daemon answers -- what is being waited for is the
    # daemon being reachable, not a different verdict.
    $ErrorActionPreference = 'Continue'
    $gerr = Join-Path $out 'gate.err'
    $allow = ($inJson | & cmd /c $hookCmd 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
    Check 'gate_allows_inside_claim' ($allow.Trim() -eq '') "expected silence, got: $allow"

    $deny = ''
    Wait-Until {
        $script:deny = ($outJson | & cmd /c $hookCmd 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
        $script:deny -match '"permissionDecision":"deny"'
    } 20000 'the claim gate denies a write outside the claim' | Out-Null
    $bypass = Join-Path $wt1 '.dodona-bypass.log'
    $bypassed = if (Test-Path $bypass) { (Get-Content $bypass -Raw).Trim() } else { '' }
    Check 'gate_denies_outside_claim' ($deny -match '"permissionDecision":"deny"') `
        "hook=$hookCmd output=[$($deny.Trim())] bypass-log=[$bypassed]"
    # ---- A FAIL-OPEN MUST LEAVE A TRACE, whatever caused it (operator decision 2026-08-19) ----
    # The gate had four paths that allowed a write and said nothing: no ticket, unreadable stdin,
    # unparseable stdin, and no file_path. Silence is the reason `gate_denies_outside_claim`
    # could go red under load with an empty detail and three wrong diagnoses -- there was
    # nothing written down to read. Every one of them now records what it saw.
    #
    # Driven with input the gate CANNOT interpret, which is the closest reproducible stand-in for
    # whatever happens under heavy load, and the case that used to vanish completely.
    $bypassLog = Join-Path $wt1 '.dodona-bypass.log'
    Remove-Item $bypassLog -ErrorAction SilentlyContinue
    $garbage = 'this is not json at all'
    $g = ($garbage | & cmd /c $hookCmd 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
    $gFlat = ($g -replace '\s+', ' ')
    # Whitespace collapsed before matching: captured native stderr is WRAPPED to the console
    # width, so a phrase can be split mid-sentence (CLAUDE.md 0.2).
    Check 'unparseable_input_is_allowed_but_recorded' ($gFlat -match 'gate fail-open' -and $gFlat -match 'unparseable') $gFlat
    Check 'the_fail_open_says_how_much_it_got' ($gFlat -match '\d+ bytes') $gFlat
    $logged = if (Test-Path $bypassLog) { (Get-Content $bypassLog -Raw) } else { '' }
    Check 'the_fail_open_reaches_the_backstops_log' (($logged -replace '\s+', ' ') -match 'fail-open.*unparseable') "log=[$logged]"
    # ...and it still ALLOWED the write, because layer 1 failing closed would strand a lane that
    # has no way to ask a human for permission (CLAUDE.md 7). The backstop is layer 2.
    Check 'a_fail_open_does_not_block_the_write' ($gFlat -notmatch 'permissionDecision') $gFlat
    Remove-Item $bypassLog -ErrorAction SilentlyContinue

    # A fail-open is not by itself a gate failure -- layer 2, the merge-time diff backstop,
    # exists for it. But it must never be SILENT, because then the only evidence is a file
    # nobody reads (CLAUDE.md §3: a silent degrade is a bug).
    #
    # THIS CHECK WAS BACKWARDS ON ITS FIRST ATTEMPT and passed during the exact event it was
    # written to catch: it read `$bypassed -eq ''` as "no fail-open happened", when an empty
    # bypass log is precisely what a SILENT fail-open leaves behind. It printed PASS in the
    # same run where the gate allowed a write outside the claim and said nothing at all.
    # The three outcomes are: denied (good), allowed-and-logged (fail-open, but visible), and
    # allowed-with-nothing-anywhere (the silent one, which is what this must catch).
    Check 'gate_never_failed_open_silently' (($deny -match 'deny') -or ($bypassed -ne '')) `
        "the gate allowed a write outside ticket 1's claim and left NO trace: output=[$($deny.Trim())] bypass-log=[$bypassed]"

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
db = sqlite3.connect(r'$storeDb')
print('\n'.join(k for (k,) in db.execute('SELECT kind FROM events ORDER BY id')))
") | Out-String
    foreach ($k in 'ticket_created','claim_conflict','token_refused_unapproved','token_granted','token_queued','landed','verify_green','token_expired_reclaimed','worktree_pruned') {
        Check "event_$k" ([bool]($events -match $k))
    }

    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- M1 ACCEPTANCE ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
