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
    # THE GATE IS NOT IN THE PROJECT ANY MORE (WORK-ISOLATION-PLAN D-17). It used to be
    # `.claude\settings.local.json` in the worktree plus a block in the repo's shared
    # .git/info/exclude. The operator's challenge killed that and it was correct: a hook in a
    # project's settings file binds EVERYTHING that runs Claude Code in that folder, including
    # their own IDE session, while only the process Dodona started should be gated. It is handed
    # over on the launch line with `--settings` instead, from workspace state.
    #
    # Three hazards went with it, and the first was live rather than theoretical: DeployGate wrote
    # settings.local.json with File.WriteAllText -- a WHOLE-FILE OVERWRITE -- which was safe only
    # because a fresh worktree never had one to destroy. This phase gates the shared checkout too,
    # where that same write would have silently wiped the developer's own allowed-commands list
    # with nothing in git to restore from.
    Check 'gate_writes_nothing_into_the_project' (
        (-not (Test-Path "$root\.dodona\wt\t1\.claude\settings.local.json")) -and
        (-not (Test-Path "$root\.dodona\wt\t1\dodona-gate.ps1")) -and
        (-not (Select-String -Path "$root\.git\info\exclude" -Pattern 'dodona-gate deployment' -Quiet -ErrorAction SilentlyContinue))
    ) "worktree and info/exclude must carry no gate files"
    # and a repo's own tracked settings must be untouched by gate deployment
    Check 'tracked_settings_untouched' (-not (Test-Path "$root\.dodona\wt\t1\.claude\settings.json") -or
        ((git -C "$root\.dodona\wt\t1" status --porcelain ".claude/settings.json" | Out-String).Trim() -eq ''))

    # ---- 2. an overlapping claim is REPORTED at plan time, not refused (D-R5, R3) ----
    #
    # RE-AIMED, NOT DELETED. This asserted `overlap_refused_at_plan_time`: a second ticket over a
    # claimed path could not be created. The operator retired it in their own words -- *"You give
    # the sheriff to agents about to work on the same file. That's often the case, very often the
    # case. And if that is problematic in some way, it's the manager's job to say something about
    # it."* Files are not the unit of work: a feature spans files and features overlap, so the
    # refusal forbade ordinary development. What is worth knowing about an overlap is duplicated
    # EFFORT, which is a judgement, so it is said rather than enforced.
    #
    # Same fixture, same moment. Two things are asserted where one was: the ticket exists, AND
    # the overlap is named with the ticket holding it. A create that quietly succeeded without
    # saying what it overlaps would be this phase's real regression -- the lock removed and the
    # signal removed with it.
    $t2bad = Dodona @("ticket-create", "--title", "WATER2", "--claim", "path:src/water/sim.cs")
    Check 'an_overlapping_ticket_is_created' ($DODONA_EXIT -eq 0 -and $t2bad -match 'ticket \d+ branch') $t2bad
    Check 'the_overlap_is_reported_and_names_the_holder' `
        ($t2bad -match 'overlap:' -and $t2bad -match 'ticket 1') $t2bad
    if ($t2bad -match 'ticket (\d+) ') { $t2badId = $Matches[1] } else { $t2badId = 0 }

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
    #
    # THE GATE IS PER-LANE NOW, so a lane has to exist before there is a gate file to read. The
    # ticket agent is the fake one (no model, this suite is free); `IsClaude` is false for it, so
    # it is handed the FILE and not the `--settings` argument -- which is deliberately how the
    # deployment stays observable in a model-free suite at all.
    $tlane = Dodona @("ticket-agent", "1", "--child", "$bin\DodonaFakeAgent.exe")
    Check 'ticket_agent_started' ($tlane -match 'lane (\d+)') $tlane
    $tlaneId = if ($tlane -match 'lane (\d+)') { $Matches[1] } else { 0 }
    $gateFile = Join-Path $wsDir "gate-lane$tlaneId.json"
    $hookCmd = if (Test-Path $gateFile) { ((Get-Content $gateFile -Raw | ConvertFrom-Json).hooks.PreToolUse[0].hooks[0].command) } else { '' }
    Check 'gate_registration_names_a_command' ([bool]$hookCmd) "gate file: $gateFile cmd=$hookCmd"
    # ONE KEY, AND ONLY ONE. Command-line settings outrank a project's Local and Project settings
    # on any colliding key, so a second key in this file would silently override whatever the
    # project chose -- which is the opposite of the D-17 intent.
    $gateJson = if (Test-Path $gateFile) { Get-Content $gateFile -Raw | ConvertFrom-Json } else { $null }
    Check 'gate_file_carries_only_the_hook' (
        $null -ne $gateJson -and
        (@($gateJson.PSObject.Properties.Name) -join ',') -eq 'hooks' -and
        (@($gateJson.hooks.PSObject.Properties.Name) -join ',') -eq 'PreToolUse'
    ) "keys: $(if ($gateJson) { (@($gateJson.PSObject.Properties.Name) -join ',') } else { 'no file' })"
    # It names the LANE, not only the ticket: a lane's ticket, claims and worktree all change
    # during its life while the lane id does not -- and hooks are read once at session start
    # (measured 2026-08-20), so the file cannot be rewritten under a live agent to keep up.
    Check 'gate_names_the_lane' ($hookCmd -match "--lane $tlaneId") "cmd=$hookCmd"
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
    # RE-AIMED from `gate_allows_inside_claim`: the write is allowed because it is INSIDE THE
    # WORKTREE, which is now the only question this hook asks (D-R5, R3). The claim it happens to
    # sit inside is no longer consulted.
    Check 'gate_allows_a_write_inside_the_worktree' ($allow.Trim() -eq '') "expected silence, got: $allow"

    # RE-AIMED from `gate_denies_outside_claim`, and this is the inversion at the heart of R3:
    # the SAME payload -- a write in the worktree but outside the ticket's declared claim -- was
    # denied and is now allowed. The write is inside the agent's own private checkout, where it
    # harms nobody, and refusing it was refusing an agent permission to do the work it was given.
    # This is the one check in the suite that goes red in BOTH directions: against HEAD because
    # HEAD denies, and against any future change that puts a claim question back into the gate.
    $deny = ''
    Wait-Until {
        $script:deny = ($outJson | & cmd /c $hookCmd 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
        $script:deny.Trim() -eq ''
    } 20000 'the gate allows a write outside the claim but inside the worktree' | Out-Null
    $bypass = Join-Path $wt1 '.dodona-bypass.log'
    $bypassed = if (Test-Path $bypass) { (Get-Content $bypass -Raw).Trim() } else { '' }
    Check 'a_write_outside_the_claim_but_inside_the_worktree_is_allowed' ($deny.Trim() -eq '') `
        "hook=$hookCmd expected silence, got=[$($deny.Trim())] bypass-log=[$bypassed]"
    # ...and it is allowed DELIBERATELY, not by failing open. A gate that allowed it because it
    # could not reach the daemon would look identical from here, so the bypass log must be empty:
    # every fail-open in this hook writes one, and R3 removed the last path that could.
    Check 'the_allow_is_a_decision_not_a_fail_open' ($bypassed -eq '') "bypass-log=[$bypassed]"
    # ---- LAYER 1: THE SHARED CHECKOUT IS NOBODY'S WORKSPACE (WORK-ISOLATION-PLAN P1) ----
    #
    # The operator's named failure: nothing in code stopped an agent doing real work in their live
    # tree. A plain lane had no PreToolUse hook AT ALL -- `GateHook` returned 0 on the first line
    # when there was no --ticket -- so every write anywhere in the project was allowed. And that
    # tree cannot even deliver the work: .githooks/pre-commit refuses commits made from it.
    #
    # Asserted through `dodona gate-hook`, the command Claude Code itself runs, because that is the
    # only surface a model-free suite can reach: `IsClaude` is false for the fake agent, so no
    # claude argv is ever built for one and the `--settings` hand-off cannot be observed here.
    $plain = Dodona @("lane-start", "--title", "PLAIN", "--child", "$bin\DodonaFakeAgent.exe")
    Check 'plain_lane_started' ($plain -match 'lane (\d+)') $plain
    $plainId = if ($plain -match 'lane (\d+)') { $Matches[1] } else { 0 }
    $plainHook = "`"$dodona`" gate-hook --lane $plainId --workspace `"$($ws.Id)`""

    # A write in the SHARED CHECKOUT -- the operator's live tree, where every other lane and the
    # operator are working. This is the one layer 1 refuses.
    $sharedJson = @{ tool_name = 'Write'; tool_input = @{ file_path = "$root\src\water\sim.cs" } } | ConvertTo-Json -Compress
    $sharedOut = ''
    Wait-Until {
        $script:sharedOut = ($sharedJson | & cmd /c $plainHook 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
        $script:sharedOut -match '"permissionDecision":"deny"'
    } 20000 'layer 1 denies a plain lane writing into the shared checkout' | Out-Null
    Check 'gate_denies_a_plain_lane_writing_the_shared_checkout' `
        ($sharedOut -match '"permissionDecision":"deny"') "hook=$plainHook output=[$($sharedOut.Trim())]"
    # D-13: the refusal has to be ACTIONABLE. "Denied: outside your claim" sends the reader hunting
    # (CLAUDE.md 0.3), so it names the tree it refused and WHERE TO WRITE INSTEAD.
    #
    # RE-AIMED from `the_refusal_names_the_shared_checkout_and_the_holder`. It used to require the
    # holding ticket's number in the message, because a promotion that hit a claim conflict
    # degraded to naming the holder. R3 removed that failure mode entirely -- overlap does not
    # refuse, so promotion no longer has a conflict to degrade on -- and the message is now always
    # the promotion: a ticket of the agent's own and the path inside it. Asserting the old wording
    # would be asserting a refusal that can no longer happen.
    Check 'the_refusal_names_the_shared_checkout_and_where_to_write_instead' `
        ($sharedOut -match 'SHARED CHECKOUT' -and $sharedOut -match 'Write this file at' -and $sharedOut -match 'ticket \d+') `
        "output=[$($sharedOut.Trim())]"
    Check 'the_refusal_confirms_nothing_was_written_to_the_shared_tree' `
        ($sharedOut -match 'Nothing was written') "output=[$($sharedOut.Trim())]"

    # AND IT IS NOT A BLANKET DENY -- asserted as a DISCRIMINATION rather than as a bare allow,
    # deliberately. `dev prove` called the bare version VACUOUS and was right to: HEAD allows every
    # write, so no assertion about the allow side alone can ever go red, and a check that cannot
    # fail is worth nothing (CLAUDE.md 0.3). Stated as "denies the one, allows the other" it is red
    # against HEAD on the first half and red against a gate that has become a brick on the second.
    $wtJson = @{ tool_name = 'Write'; tool_input = @{ file_path = "$wt1\src\water\sim.cs" } } | ConvertTo-Json -Compress
    $wtOut = ($wtJson | & cmd /c $plainHook 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
    Check 'gate_tells_the_two_trees_apart_for_a_plain_lane' `
        (($sharedOut -match '"permissionDecision":"deny"') -and ($wtOut.Trim() -eq '')) `
        "shared=[$($sharedOut.Trim())] worktree=[$($wtOut.Trim())] (expected: deny, then silence)"

    # A TICKET LANE IS SUBJECT TO LAYER 1 TOO, and this one is not belt-and-braces: `claim-check`
    # resolves an absolute path through its repository and project rungs, so a ticket agent writing
    # the ABSOLUTE path of a file its claim covers -- in the operator's live checkout instead of its
    # own worktree -- resolved to the same claim-relative string and was ALLOWED. Reachable since
    # multi-repo landed; found by reading the rungs while implementing this phase.
    $tHook = $hookCmd
    $tSharedOut = ''
    Wait-Until {
        $script:tSharedOut = ($sharedJson | & cmd /c $tHook 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
        $script:tSharedOut -match '"permissionDecision":"deny"'
    } 20000 'layer 1 denies a TICKET lane writing its claimed path in the shared checkout' | Out-Null
    Check 'gate_denies_a_ticket_lane_writing_its_claim_in_the_shared_checkout' `
        ($tSharedOut -match '"permissionDecision":"deny"') "hook=$tHook output=[$($tSharedOut.Trim())]"

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
    Check 'unparseable_input_is_recorded' ($gFlat -match 'gate fail-open' -and $gFlat -match 'unparseable') $gFlat
    Check 'the_fail_open_says_how_much_it_got' ($gFlat -match '\d+ bytes') $gFlat
    $logged = if (Test-Path $bypassLog) { (Get-Content $bypassLog -Raw) } else { '' }
    Check 'the_fail_open_reaches_the_backstops_log' (($logged -replace '\s+', ' ') -match 'fail-open.*unparseable') "log=[$logged]"

    # ---- AND THE VERDICT ON UNREADABLE INPUT IS NOW A REFUSAL. A RECORDED RATIONALE, REVERSED ----
    #
    # This check used to be `a_fail_open_does_not_block_the_write`, asserting the opposite, with the
    # reason written beside it: "layer 1 failing closed would strand a lane that has no way to ask a
    # human for permission (CLAUDE.md 7). The backstop is layer 2."
    #
    # That was correct for the gate it was written about. It lived only inside ticket worktrees, so
    # a fail-open let a write slip to the merge-time diff backstop, which catches it before anything
    # can land. WORK-ISOLATION-PLAN section 9 requires exactly this re-reading, because layer 1
    # changes what is behind the fail-open: nothing. A write allowed into the SHARED CHECKOUT is in
    # the operator's live tree, next to their uncommitted work and every other lane's, and no
    # backstop sees it -- it was never going to be merged, it is already there.
    #
    # So the two questions now fail in opposite directions, and the ORDER is what keeps that safe:
    # the TREE question is asked first and refuses when it cannot get an answer; the CLAIM question
    # is asked second and still fails open exactly as before. Because the tree answer has already
    # been obtained, every remaining claim fail-open can only let a write through INSIDE A WORKTREE
    # -- which is the case the original rationale was actually about, and the backstop still covers.
    #
    # A refused write is also not a stranding: it is announced to the agent, recorded, and
    # retryable, and the daemon it needs an answer from is the same one already pumping this lane's
    # output. An allowed one is invisible and permanent. CLAUDE.md 0.3 is largely a list of what
    # invisible costs.
    Check 'unreadable_input_is_refused_not_allowed_into_the_live_tree' `
        ($gFlat -match 'permissionDecision.*deny') $gFlat
    Remove-Item $bypassLog -ErrorAction SilentlyContinue

    # RE-AIMED from `gate_never_failed_open_silently`, and the re-aim is forced rather than
    # chosen. That check read: denied, OR allowed-with-a-trace -- the two acceptable outcomes for
    # a write outside the claim, where the unacceptable third was allowed-and-silent. It had
    # already been backwards once and passed during the exact event it was written to catch, which
    # is why the three outcomes are spelled out above it. R3 makes silence the CORRECT answer for
    # that payload, so the old form cannot be kept without asserting the opposite of the design.
    #
    # The property it was really protecting -- THIS GATE NEVER ALLOWS WHAT IT CANNOT DETERMINE --
    # is what moves here, and R3 makes it stronger rather than weaker: with the claim question
    # gone there is no fail-open path left in `GateHook` at all. Asserted at the one input that
    # can still make the gate unable to answer: a `--lane` argument it cannot parse. That is our
    # own misconfiguration, the case where guessing "allow" would put a write in the operator's
    # live tree, and it must DENY.
    $badLaneHook = "`"$dodona`" gate-hook --lane not-a-number --workspace `"$($ws.Id)`""
    $badLaneOut = ($inJson | & cmd /c $badLaneHook 2> $gerr | Out-String) + (Get-Content $gerr -Raw -ErrorAction SilentlyContinue)
    Check 'the_gate_denies_a_lane_argument_it_cannot_read' `
        ($badLaneOut -match '"permissionDecision":"deny"') "output=[$($badLaneOut.Trim())]"
    # ...and it says whose fault it is, because an agent cannot fix a Dodona misconfiguration and
    # should not spend a turn trying (CLAUDE.md 0.3: name the real cause).
    Check 'the_misconfiguration_refusal_says_it_is_not_the_agents_mistake' `
        ($badLaneOut -match 'misconfiguration') "output=[$($badLaneOut.Trim())]"

    # ---- 5. agent work: commit in wt1 (the test IS the agent at the git layer) ----
    Set-Content "$wt1\src\water\sim.cs" "// water sim v2"
    git -C $wt1 add -A
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "water v2"

    # ---- 5b. R4 / D-R8: completion produces a PR-shaped record ---------------------------
    #
    # The manager has never once been shown a lane agent's output (plan §1: `BrainReview` fires
    # at lane creation, is handed the operator's sentence and the names of live lanes, and never
    # sees a turn). R4 assembles what it will read: the ticket, its branch and worktree, what the
    # branch changed, the drop check, the verify result, and the agent's OWN end-of-turn report.
    # Code only, no opinions, no model call -- so this section is free like the rest of the suite.
    #
    # D-R15 IS WHAT THESE CHECKS PIN MOST. The record is assembled at COMPLETION and the verify
    # result is REPORTED, never run: a record built on the land path would arrive after the
    # approval it exists to inform, and a verify run here would cost a build plus suites per
    # completed turn to answer a different question from the land's (this branch has not had main
    # merged into it). So `verify.state` must read `not-run` WITH a reason, and a blank or absent
    # slot is the failure -- `land_drop_check_moot` is the pattern being copied.
    #
    # The lane is the ticket-1 agent from section 4 and the worktree is the one section 5 just
    # committed into, so this costs one fake-agent turn and a handful of git calls.
    $reportOne = "reworked the water sim mask to height times curvature; one file, and I have not run the tests"
    Dodona @("say", "$tlaneId", "say $reportOne") | Out-Null
    Wait-Until { [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record'").Trim()) -ge 1 } `
        25000 "ticket 1's completion record" | Out-Null
    # Read it through the VERB, not by hand-rolling SQL: an affordance no verb can reach is where
    # the next defect lives (CLAUDE.md §3.1), and R5 will read it exactly this way.
    $rec1Raw = Dodona @("ticket-record", "1")
    $rec1 = try { $rec1Raw | ConvertFrom-Json } catch { $null }
    Check 'a_finished_turn_produces_a_completion_record' `
        ($DODONA_EXIT -eq 0 -and $null -ne $rec1 -and $rec1.ticket -eq 1) "exit=$DODONA_EXIT [$rec1Raw]"
    Check 'the_record_names_the_ticket_its_branch_and_its_worktree' `
        ($null -ne $rec1 -and $rec1.branch -eq 'ticket/1' -and $rec1.worktree -eq $wt1) `
        "branch=$($rec1.branch) worktree=$($rec1.worktree) expected $wt1"
    # `git diff --stat main...ticket/1`. The three-dot form is ALREADY merge-base-relative, which
    # is why §10's merge-base trap is about the drop check and not about this.
    Check 'the_record_carries_what_the_branch_changed' `
        ($null -ne $rec1 -and $rec1.files -eq 1 -and (@($rec1.changed) -contains 'src/water/sim.cs') -and
         $rec1.diffstat -match 'src/water/sim.cs') "files=$($rec1.files) changed=$(@($rec1.changed) -join ',') stat=$($rec1.diffstat)"
    # THE FIELD THAT DID NOT EXIST ANYWHERE BEFORE R4: the agent's own words, verbatim.
    Check 'the_record_carries_the_agents_own_report' `
        ($null -ne $rec1 -and $rec1.report -eq $reportOne) "report=[$($rec1.report)]"
    # D-R15, stated as an assertion: a verify has not run for this ticket, and the record SAYS SO
    # rather than leaving the slot empty for a reader to interpret.
    Check 'the_record_says_the_verify_has_not_run_rather_than_leaving_it_blank' `
        ($null -ne $rec1 -and $rec1.verify.state -eq 'not-run' -and $rec1.verify.detail.Length -gt 20) `
        "state=$($rec1.verify.state) detail=[$($rec1.verify.detail)]"
    # The drop check DOES run at completion -- it is pure git -- and it is MOOT until a land has
    # merged main in, which is a real state and not a check that failed to run.
    Check 'the_drop_check_runs_at_completion_and_says_moot_before_main_is_merged_in' `
        ($null -ne $rec1 -and $rec1.drop.state -eq 'moot') "drop=$($rec1.drop | ConvertTo-Json -Compress)"

    # ---- D-R13: a chatty lane produces ONE record, not one per turn ----------------------
    #
    # A `result` is the end of A TURN, not of the conversation (LANE-LIFECYCLE.md §2 -- "the agent
    # said it was done" is turn-completion and is wrong often enough that it must never on its own
    # end anything). Re-reviewing every turn would burn the machine and the quota, so the gate is
    # the worktree digest. The skip is RECORDED: a gate that quietly swallowed every completion
    # and a trigger that was never wired look identical otherwise, and that is the failure mode
    # this phase was warned about.
    Dodona @("say", "$tlaneId", "say and one more thought about the shoreline, nothing changed on disk") | Out-Null
    Wait-Until { [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record_unchanged'").Trim()) -ge 1 } `
        25000 'the unchanged-worktree skip' | Out-Null
    $recCount = [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record'").Trim())
    Check 'a_chatty_turn_produces_no_second_record' ($recCount -eq 1) "completion_record rows: $recCount"
    $skip = Invoke-StoreSql $storeDb "SELECT detail FROM events WHERE kind='completion_record_unchanged' ORDER BY id DESC LIMIT 1"
    Check 'the_skipped_record_says_why_rather_than_being_silent' `
        ($skip -match 'ticket 1' -and $skip -match 'unchanged') "[$skip]"

    # ...and a turn that DID change the worktree gets its own record.
    Set-Content "$wt1\src\water\foam.cs" "// foam"
    git -C $wt1 add src/water/foam.cs
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "foam"
    Dodona @("say", "$tlaneId", "say added foam.cs") | Out-Null
    Wait-Until { [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record'").Trim()) -ge 2 } `
        25000 'a second record after a real change' | Out-Null
    $rec2 = try { (Dodona @("ticket-record", "1")) | ConvertFrom-Json } catch { $null }
    Check 'a_turn_that_changed_the_worktree_gets_its_own_record' `
        ($null -ne $rec2 -and $rec2.files -eq 2 -and (@($rec2.changed) -contains 'src/water/foam.cs') -and
         $rec2.digest -ne $rec1.digest) "files=$($rec2.files) digest=$($rec2.digest) vs $($rec1.digest)"

    # ---- 5c. AND IT SURVIVES A DAEMON RESTART, because the trigger is wired at ADOPTION ----
    #
    # This is the check that exists because of §3's dead routing ladder. `LaneRuntime.OnResult` is
    # wired in `HookTurnEnd`, which is called from TWO places: the spawn, and reconcile's adoption
    # of every lane that was already alive. A daemon restarts on every publish and every hot swap,
    # so a record wired only at spawn would be fully covered, fully green, and simply stop
    # happening for every lane the operator already had -- which is exactly what routing did for
    # two days. Nothing in the record's own code path can catch that; only a restart can.
    #
    # THE OPERATOR'S PATH, as far as this suite is allowed to go: a fresh daemon process that
    # reconciles from the store, adopts the live shim, and wires the lane itself -- nothing about
    # the trigger is pre-built by the test. Autostart stays OFF here and that is deliberate, not
    # an omission: a summoned daemon runs its warm-up and spawns the router, brain and compressor
    # pool as real `claude -p --model haiku` processes (CLAUDE.md §3.2), which is the one thing a
    # model-free suite may never do. `m0` clears autostart for the checks where summoning IS the
    # subject; here the subject is adoption.
    Dodona @("stop-daemon") | Out-Null
    Wait-Until { -not (Test-DodonaPipe $ws.CtlPipe) } 15000 'the first daemon to go down' | Out-Null
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon2.out" -RedirectStandardError "$out\daemon2.err"
    Wait-Daemon $ws.CtlPipe | Out-Null
    Set-Content "$wt1\src\water\spray.cs" "// spray"
    git -C $wt1 add src/water/spray.cs
    git -C $wt1 -c user.email=t@t -c user.name=t commit -q -m "spray"
    # The `say` is waited for rather than assumed, and its reply is carried into the detail of the
    # check below rather than becoming a check of its own. Two reasons: a lane the successor failed
    # to ADOPT refuses input outright, which is a different failure from a lane adopted without its
    # trigger, and the detail is what tells them apart; and adoption already works at HEAD, so a
    # separate assertion on it is VACUOUS by `dev prove`'s own verdict -- a check with no teeth
    # sitting in the suite implying it has some.
    $sayAfter = ''
    Wait-Until { $script:sayAfter = Dodona @("say", "$tlaneId", "say added spray.cs after the restart"); $global:DODONA_EXIT -eq 0 } `
        30000 'the adopted lane accepts input again' | Out-Null
    Wait-Until { [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record'").Trim()) -ge 3 } `
        30000 'a record from a lane the daemon ADOPTED rather than spawned' | Out-Null
    $rec3 = try { (Dodona @("ticket-record", "1")) | ConvertFrom-Json } catch { $null }
    Check 'an_adopted_lane_still_produces_a_record_after_a_daemon_restart' `
        ($null -ne $rec3 -and $rec3.files -eq 3 -and $rec3.report -match 'after the restart') `
        "files=$($rec3.files) report=[$($rec3.report)] (the say that should have produced it: [$sayAfter])"
    # ...and the D-R13 gate survived the restart too, which it only can because the previous
    # digest is read back out of the store rather than held in memory. An in-memory gate would
    # produce a duplicate record on the first turn after every publish and look correct here.
    Dodona @("say", "$tlaneId", "say still nothing changed on disk") | Out-Null
    Wait-Until { [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record_unchanged'").Trim()) -ge 2 } `
        25000 'the skip after a restart' | Out-Null
    $recCount3 = [int]((Invoke-StoreSql $storeDb "SELECT COUNT(*) FROM events WHERE kind='completion_record'").Trim())
    Check 'the_record_gate_survives_a_restart_rather_than_resetting' ($recCount3 -eq 3) "completion_record rows: $recCount3"

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
    #
    # RE-AIMED, NOT WEAKENED (R4). `main_advanced` compared main's tip SUBJECT to the literal
    # 'water v2', which coupled it to whichever commit section 5 happened to make last -- and
    # section 5b now makes three. The property it was really asserting is D-R2's: after the
    # fast-forward, main's tip IS the branch tip that was verified, byte for byte. That is a sha
    # comparison, which is strictly stronger than a commit message and cannot go stale when the
    # fixture gains a commit. Captured BEFORE the land, because the land prunes the worktree and
    # deletes the branch.
    $t1Tip = (git -C $root rev-parse ticket/1)
    $land1 = Dodona @("land", "1")
    Check 'ticket1_landed' ($land1 -match 'landed ticket 1') $land1
    Check 'verify_ran_green' ($land1 -match 'verify green') $land1
    $mainTip = (git -C $root rev-parse main)
    Check 'main_advanced' ($mainTip -eq $t1Tip) "main=$mainTip ticket/1 was $t1Tip ($(git -C $root log -1 --format=%s))"
    Check 'worktree1_pruned' (-not (Test-Path $wt1))

    # ---- 9. released claim is claimable again ----
    $t3 = Dodona @("ticket-create", "--title", "WATER-NEXT", "--claim", "subtree:src/water")
    Check 'released_claim_reclaimable' ($t3 -match 'ticket \d+ branch') $t3

    # ---- 10. queued ticket now gets the token, and the DAEMON brings main in (R1 / D-R1) ----
    #
    # RE-AIMED, not added: this fixture used to assert `stale_branch_refused_ff_only` and then
    # `rebased_branch_lands` -- with the TEST performing the rebase in between. That was the
    # measurement that condemned the old design: the refusal said "rebase <branch> onto <main>
    # and re-verify first" and NOTHING IN THE TREE PERFORMED THAT REBASE, so the only thing
    # that ever satisfied it was a test pretending to be a developer. The moment and the
    # fixture are unchanged; what is asserted is now the stronger fact, and the two checks
    # below it are the guard that the precondition is still real.
    $req2 = Dodona @("token-request", "$t2id")
    Check 'queued_ticket_now_granted' ($req2 -match "granted ticket $t2id") $req2
    $wt2 = "$root\.dodona\wt\t$t2id"
    Set-Content "$wt2\src\sky\box.cs" "// skybox v2"
    git -C $wt2 add -A
    git -C $wt2 -c user.email=t@t -c user.name=t commit -q -m "sky v2"
    # main is at 'water v2'; ticket 2 was cut from init. Assert the staleness rather than
    # assume it -- a fixture that has quietly stopped being stale would make the land below
    # pass for the wrong reason, which is this project's most-repeated failure.
    # No 2>&1 anywhere in this suite: $ErrorActionPreference is 'Stop', and redirecting a
    # native command's stderr under Stop throws NativeCommandError (CLAUDE.md §0.2).
    # --is-ancestor prints nothing either way; the answer is the exit code.
    git -C $root merge-base --is-ancestor main "ticket/$t2id" | Out-Null
    $staleBefore = ($LASTEXITCODE -ne 0)
    $land2 = Dodona @("land", "$t2id")
    Check 'stale_branch_was_really_stale' $staleBefore "main was already an ancestor of ticket/$t2id, so nothing was tested"
    Check 'stale_branch_lands_with_no_human_rebase' ($DODONA_EXIT -eq 0 -and $land2 -match "landed ticket $t2id") $land2
    Check 'the_land_says_it_merged_main_in' ($land2 -match 'merged main in') $land2
    # The ff-only put main AT the merge commit the daemon made in the worktree -- which is
    # D-R2's property from the other side: what landed is exactly what was verified.
    $mainSubject = git -C $root log -1 --format=%s
    Check 'main_is_now_the_merge_that_was_verified' `
        ($mainSubject -match "merge main into ticket/$t2id before landing") $mainSubject
    Check 'the_branch_work_came_with_it' ((git -C $root log --format=%s | Out-String) -match 'sky v2') $mainSubject

    # EVERY SECTION BELOW IS DECOUPLED FROM THIS ONE ON PURPOSE, and the reason is a measured
    # one rather than tidiness. Written first with 10b sharing section 10's claim and token,
    # `dev prove` returned VACUOUS on the single most important check in this phase: against
    # HEAD ticket 2's land fails, so ticket 2 keeps both the token AND `subtree:src/sky`, every
    # later ticket-create conflicts, and the fixture derails into asserting nothing. A check
    # that can only be judged when the code already works is not a check. So: release the
    # token explicitly, and give each section a claim of its own that nothing else touches.
    Dodona @("token-release", "$t2id") | Out-Null      # a no-op when it already landed

    # ---- 10b. R1: a RED verify leaves main's sha UNCHANGED --------------------------------
    #
    # Under the old order verify ran AFTER `LandCommit`, in the repository that had just
    # changed: a red verify had already shipped and there was nothing left to refuse
    # (WORK-ISOLATION-PLAN D-5). `verify_ran_green` above passed under both orders, which is
    # why this phase is "the one most likely to look green against the old code" -- the check
    # that tells them apart is main's sha, not verify's colour.
    $t5 = Dodona @("ticket-create", "--title", "REDVERIFY", "--claim", "path:src/verify/probe.cs")
    if ($t5 -match 'ticket (\d+) ') { $t5id = $Matches[1] }
    Dodona @("approve", "$t5id") | Out-Null
    Dodona @("token-request", "$t5id") | Out-Null
    $wt5 = "$root\.dodona\wt\t$t5id"
    New-Item -ItemType Directory -Force "$wt5\src\verify" | Out-Null
    Set-Content "$wt5\src\verify\probe.cs" "// probe"
    git -C $wt5 add -A
    git -C $wt5 -c user.email=t@t -c user.name=t commit -q -m "probe"
    Set-Content "$root\dodona.json" '{ "main": "main", "verify": ["exit 3"] }'   # Config.For re-reads per call
    $mainBefore = (git -C $root rev-parse main)
    $landRed = Dodona @("land", "$t5id")
    $mainAfter = (git -C $root rev-parse main)
    Check 'red_verify_refuses_the_land' ($DODONA_EXIT -eq 1 -and $landRed -match 'VERIFY RED') $landRed
    Check 'red_verify_leaves_main_unchanged' ($mainBefore -eq $mainAfter) "before=$mainBefore after=$mainAfter"
    Check 'the_red_verify_refusal_says_main_is_untouched' ($landRed -match 'main unchanged') $landRed
    Set-Content "$root\dodona.json" '{ "main": "main", "verify": ["echo verify-ok"] }'
    $landGreen = Dodona @("land", "$t5id")
    Check 'the_same_ticket_lands_once_verify_is_green' ($landGreen -match "landed ticket $t5id") $landGreen

    # ---- 10c. R1/D-R3: a conflict is refused, NAMED, and the worktree left CLEAN ----------
    #
    # The abort is not tidiness. A half-merged worktree makes every later check lie (plan §10),
    # and the agent that has to resolve this is standing in it.
    $t6 = Dodona @("ticket-create", "--title", "CONFLICT", "--claim", "path:src/clash/pane.cs")
    if ($t6 -match 'ticket (\d+) ') { $t6id = $Matches[1] }
    $wt6 = "$root\.dodona\wt\t$t6id"
    New-Item -ItemType Directory -Force "$wt6\src\clash" | Out-Null
    Set-Content "$wt6\src\clash\pane.cs" "// pane from the ticket"
    git -C $wt6 add -A
    git -C $wt6 -c user.email=t@t -c user.name=t commit -q -m "pane from the ticket"
    # main adds the same path with other content. The fixture is main's other developer here,
    # and add/add is a conflict git reports the same way content divergence is.
    New-Item -ItemType Directory -Force "$root\src\clash" | Out-Null
    Set-Content "$root\src\clash\pane.cs" "// pane from main"
    git -C $root add -A
    git -C $root -c user.email=t@t -c user.name=t commit -q -m "pane from main"
    Dodona @("approve", "$t6id") | Out-Null
    Dodona @("token-request", "$t6id") | Out-Null
    $mainBeforeConflict = (git -C $root rev-parse main)
    $landConflict = Dodona @("land", "$t6id")
    Check 'a_conflicting_merge_refuses_the_land' ($DODONA_EXIT -eq 1 -and $landConflict -match 'conflict') $landConflict
    Check 'the_conflict_refusal_names_the_file' ($landConflict -match 'src/clash/pane\.cs') $landConflict
    Check 'a_conflict_leaves_main_unchanged' ($mainBeforeConflict -eq (git -C $root rev-parse main)) $mainBeforeConflict
    $wt6Status = ((git -C $wt6 status --porcelain) | Out-String).Trim()
    Check 'the_worktree_is_left_clean_not_half_merged' ($wt6Status -eq '') "status=[$wt6Status]"
    # Asked of git rather than of the filesystem: a linked worktree's git dir is
    # `.git\worktrees\<name>`, and a Test-Path against a guessed path is a check that passes
    # vacuously the day the guess is wrong.
    git -C $wt6 rev-parse --verify --quiet MERGE_HEAD | Out-Null
    Check 'no_merge_is_left_in_progress' ($LASTEXITCODE -ne 0) 'MERGE_HEAD survived the abort'
    # D-R3: the agent resolves it, in its own worktree, and the same ticket lands. The daemon's
    # own merge then finds nothing to do -- which is what "the agent already did it" looks like.
    # This merge CONFLICTS on purpose, so it is the one place the suite must relax Stop to
    # capture native stderr at all (§0.2's rule, stated the way that section states it).
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    git -C $wt6 -c user.email=t@t -c user.name=t merge main 2>&1 | Out-Null
    $ErrorActionPreference = $prevEap
    Set-Content "$wt6\src\clash\pane.cs" "// pane resolved by the agent"
    git -C $wt6 add -A
    git -C $wt6 -c user.email=t@t -c user.name=t commit -q -m "merge main, resolved pane"
    $landResolved = Dodona @("land", "$t6id")
    Check 'an_agent_resolved_merge_lands' ($landResolved -match "landed ticket $t6id") $landResolved
    Check 'the_daemon_had_nothing_left_to_merge' ($landResolved -match 'already current with main') $landResolved
    Check 'the_resolution_is_what_landed' `
        ((Get-Content "$root\src\clash\pane.cs" -Raw).Trim() -eq '// pane resolved by the agent') `
        ((Get-Content "$root\src\clash\pane.cs" -Raw).Trim())

    # ---- 10d. an uncommitted worktree is refused, and told what to do INSTEAD of stashing --
    #
    # `git merge` refuses a dirty tree and its complaint does not say what to do. The wrong
    # answer is `git stash`: it is repo-global, one shared ref in the common dir, so two lanes
    # stashing interleave one stack and `pop` takes the other lane's work (CLAUDE.md §5.2).
    $t7 = Dodona @("ticket-create", "--title", "DIRTY", "--claim", "path:src/dirty/note.cs")
    if ($t7 -match 'ticket (\d+) ') { $t7id = $Matches[1] }
    $wt7 = "$root\.dodona\wt\t$t7id"
    Dodona @("approve", "$t7id") | Out-Null
    Dodona @("token-request", "$t7id") | Out-Null
    New-Item -ItemType Directory -Force "$wt7\src\dirty" | Out-Null
    Set-Content "$wt7\src\dirty\note.cs" "// uncommitted"      # never committed
    $landDirty = Dodona @("land", "$t7id")
    Check 'a_dirty_worktree_is_refused' ($DODONA_EXIT -eq 1 -and $landDirty -match 'uncommitted changes') $landDirty
    Check 'the_dirty_refusal_says_commit_and_warns_off_the_stash' `
        ($landDirty -match 'commit' -and $landDirty -match 'stash') $landDirty
    git -C $wt7 add -A
    git -C $wt7 -c user.email=t@t -c user.name=t commit -q -m "box committed"
    $landClean = Dodona @("land", "$t7id")
    Check 'it_lands_once_the_work_is_committed' ($landClean -match "landed ticket $t7id") $landClean

    # ---- 10e. R2/D-R4: the SILENT DROP -- resolving by discarding what main brought in ------
    #
    # The plan's own recipe: land one ticket, then have a second resolve by discarding it. This
    # is the failure a report will not mention, because the tests still pass -- nothing
    # references the discarded code -- and the agent's write-up says "merged main, resolved a
    # conflict", which is true.
    #
    # THE FIXTURE ORDER IS THE WHOLE FIXTURE. The branch must be cut BEFORE main gets the
    # change it is going to drop, or there is nothing to drop.
    #
    # AND MAIN MOVES BY A DIRECT COMMIT HERE, NOT BY LANDING A SECOND TICKET, which is a
    # departure from the plan's suggested recipe with a measured reason. Written that way
    # first, ticket B could not get the merge token at all: `token-request`'s claim backstop
    # runs BEFORE the merge, and once the agent has merged main in itself (D-R3's path, and the
    # only way a drop can exist) `git diff main...branch` is taken from main's tip, so every
    # file the branch touched relative to main reads as outside its claim. Two tickets cannot
    # both claim keep.cs either -- the plan-time overlap refusal forbids it -- so there is no
    # legal shape for the two-ticket version while that backstop lives. R3 deletes it; R2 is
    # not the phase to work around it, and the fixture asserts the same fact either way. The
    # commit-to-main-directly move is one section 10c already uses.
    New-Item -ItemType Directory -Force "$root\src\drop" | Out-Null
    Set-Content "$root\src\drop\keep.cs" "// keep v1"
    git -C $root add -A
    git -C $root -c user.email=t@t -c user.name=t commit -q -m "keep v1"
    $tB = Dodona @("ticket-create", "--title", "DROPPER", "--claim", "subtree:src/drop")
    if ($tB -match 'ticket (\d+) ') { $tBid = $Matches[1] }
    $wtB = "$root\.dodona\wt\t$tBid"
    Check 'the_dropper_ticket_was_created' ([bool]$tBid) $tB
    # main's other developer changes keep.cs, after B was cut.
    Set-Content "$root\src\drop\keep.cs" "// keep v2 -- main's change"
    git -C $root add -A
    git -C $root -c user.email=t@t -c user.name=t commit -q -m "keep v2"
    Check 'main_carries_the_change_that_is_about_to_be_dropped' `
        ((Get-Content "$root\src\drop\keep.cs" -Raw) -match 'keep v2') 'main should hold v2'
    # B does its own work, merges main in ITSELF (D-R3's agent-resolves path), and then quietly
    # puts keep.cs back the way it was. No conflict is involved: keep.cs changed on ONE side, so
    # git merges it cleanly and the revert is a separate, deliberate, invisible edit.
    Set-Content "$wtB\src\drop\other.cs" "// other work"
    git -C $wtB add -A
    git -C $wtB -c user.email=t@t -c user.name=t commit -q -m "other work"
    git -C $wtB -c user.email=t@t -c user.name=t merge main -m "merge main into B" | Out-Null
    Check 'the_merge_brought_mains_change_into_the_branch' `
        ((Get-Content "$wtB\src\drop\keep.cs" -Raw) -match 'keep v2') 'the merge should have delivered v2'
    Set-Content "$wtB\src\drop\keep.cs" "// keep v1"        # the silent drop
    git -C $wtB add -A
    git -C $wtB -c user.email=t@t -c user.name=t commit -q -m "tidy up"
    Dodona @("approve", "$tBid") | Out-Null
    Dodona @("token-request", "$tBid") | Out-Null
    $mainBeforeDrop = (git -C $root rev-parse main)
    $landB = Dodona @("land", "$tBid")
    Check 'a_silent_drop_is_refused' ($DODONA_EXIT -eq 1 -and $landB -match 'reverts') $landB
    Check 'the_drop_refusal_names_the_file' ($landB -match 'src/drop/keep\.cs') $landB
    Check 'a_silent_drop_leaves_main_unchanged' ($mainBeforeDrop -eq (git -C $root rev-parse main)) $mainBeforeDrop
    # And it must be judged against the FORK POINT, not main's tip. Against the tip this branch
    # is indistinguishable from one that never saw main's change -- which is why the check
    # recovers the fork point from the branch's own merge commit (plan §10). If that recovery
    # ever regresses, this event stops being written and the refusal above goes with it.
    # NO ESCAPED DOUBLE QUOTES IN A python -c BLOCK. PowerShell does not treat \" as an escape
    # inside a double-quoted string (its escape is the backtick), so python receives a literal
    # backslash-quote and dies on a syntax error -- and `python -c` failing prints to stderr and
    # yields an EMPTY string, which reads exactly like "the event was never written". Two checks
    # here were red for that reason and not for any reason in the product. Single quotes only,
    # and the filtering happens in PowerShell where quoting is not a hazard.
    $allEvents = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for k, d in db.execute('SELECT kind, detail FROM events ORDER BY id'):
    print((k or '') + ' :: ' + (d or ''))
") | Out-String
    $dropEvents = (($allEvents -split "`n") | Where-Object { $_ -match '^land_silent_drop ::' }) -join "`n"
    Check 'the_drop_check_recorded_the_pre_merge_it_compared_against' ($dropEvents -match 'pre-merge [0-9a-f]{8}') $dropEvents
    Check 'the_drop_check_did_not_quietly_skip' ($dropEvents -match 'src/drop/keep\.cs') $dropEvents
    # The reference point is NOT main's tip and NOT a merge-base. Both are wrong here and the
    # second one was MEASURED wrong: recovering a "fork point" from the branch's merge history
    # resolved to the repository's INIT COMMIT for every ticket, because a ticket branch's
    # ancestry contains main's own merge history. Against init the dropped file did not exist,
    # the pre-image never matched, and the check passed everything while looking armed. So the
    # event names the pre-merge commit, and this check is what keeps it honest.
    $mootEvents = (($allEvents -split "`n") | Where-Object { $_ -match '^land_drop_check ::' }) -join "`n"
    Check 'the_drop_check_reports_what_it_examined_on_a_clean_land' ($mootEvents -match 'drop\(s\) against pre-merge') $mootEvents
    # Re-applying main's version clears it -- the refusal is a correction, not a dead end.
    Set-Content "$wtB\src\drop\keep.cs" "// keep v2 -- main's change"
    git -C $wtB add -A
    git -C $wtB -c user.email=t@t -c user.name=t commit -q -m "restore main's keep.cs"
    $landB2 = Dodona @("land", "$tBid")
    Check 'it_lands_once_mains_change_is_restored' ($landB2 -match "landed ticket $tBid") $landB2
    # NO FALSE POSITIVE: a resolution that combines both sides differs from the fork point and
    # from main, so it must stay quiet. Section 10c already lands exactly such a branch
    # ("pane resolved by the agent"), and its checks would have gone red here if this check
    # flagged ordinary resolutions -- asserted directly so the coupling is not accidental.
    Check 'an_ordinary_resolution_is_not_flagged_as_a_drop' `
        ($landResolved -match "landed ticket $t6id" -and $landResolved -notmatch 'reverts') $landResolved

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

    # ---- 11b. `subtree:/` -- the claim that read "the whole tree" and blocked nobody --------
    #
    # MEASURED against HEAD before it was changed (unit tests written first, ten of them red):
    # `subtree:/` normalizes to the EMPTY string, and every branch of the algebra then answered
    # no. Overlap's `a == b || a.StartsWith(b + "/")` cannot match an empty `a`, and Covers'
    # `relPath.StartsWith(value + "/")` went looking for a leading "/". So an agent could take
    # what reads as an exclusive lock over the entire repository while every other ticket walked
    # straight through it -- enforcement that is switched off while looking armed, which is the
    # exact failure CLAUDE.md 0.3 is about -- and its own gate then denied it every write.
    #
    # Ticket $t3id (WATER-NEXT) holds subtree:src/water, so a whole-tree claim overlaps it.
    #
    # RE-AIMED from `the_whole_tree_claim_conflicts_with_an_open_claim`: the OVERLAP is what this
    # check is really about -- the algebra answering "yes, these intersect" for a value that
    # normalises to the empty string -- and that is still exactly what is asserted. What changed
    # is only what the daemon DOES with the answer: report it (D-R5) instead of refusing. The
    # algebra bug this section was written for would still be caught, because a `subtree:/` that
    # matched nothing would produce no overlap line at all.
    if ($t3 -match 'ticket (\d+) ') { $t3id = $Matches[1] } else { $t3id = 0 }
    $whole = Dodona @("ticket-create", "--title", "WHOLE", "--claim", "subtree:/")
    Check 'the_whole_tree_claim_is_created_and_its_overlap_reported' `
        ($DODONA_EXIT -eq 0 -and $whole -match 'overlap:' -and $whole -match "ticket $t3id") $whole
    # An empty value is the whole tree for a SUBTREE and nonsense everywhere else: `path:/`
    # names no file. HEAD created a ticket holding a claim over nothing and reported success --
    # P0.5's silently-dropped spec reached from the other side. Refused by name now.
    $emptyPath = Dodona @("ticket-create", "--title", "NOWHERE", "--claim", "path:/")
    Check 'an_empty_path_claim_is_refused_rather_than_created' `
        ($DODONA_EXIT -ne 0 -and $emptyPath -match 'bad claim spec') $emptyPath
    # The other half, and the one that makes a whole-tree claim usable rather than only
    # blocking: its holder must be allowed to write anywhere in the tree. Extended onto the open
    # ticket rather than ticketed, because claim-extend excludes the ticket's own claims.
    #
    # RE-AIMED from `the_whole_tree_is_claimable_when_nothing_else_holds_anything`. That name
    # encoded the precondition the old refusal imposed -- it only succeeded when nothing else held
    # anything -- and R3 removes that condition from every direction, including this one
    # (Store.ClaimExtend carries why the fourth refusal had to go with D-R5's three). So the
    # extension now succeeds regardless, and what it overlaps is said rather than enforced.
    $wide = Dodona @("claim-extend", "$t3id", "--claim", "subtree:/")
    Check 'a_wide_extension_succeeds_whatever_else_is_held' `
        ($DODONA_EXIT -eq 0 -and $wide -match "extended ticket $t3id") $wide
    Check 'the_wide_extension_still_names_what_it_overlaps' ($wide -match 'overlap:') $wide
    $wideCovered = Dodona @("claim-check", "$t3id", "$root\.dodona\wt\t$t3id\src\sky\box.cs")
    Check 'the_whole_tree_covers_a_file_no_other_claim_names' `
        ($DODONA_EXIT -eq 0 -and $wideCovered -match 'covered:') $wideCovered

    # ---- 11c. R3.5 / D-R14: the land runs OFF the serial control pipe --------------------
    #
    # The pipe is serial -- one NamedPipeServerStream instance, HandleAsync awaited inline --
    # and the land used to run ON it. So for the whole of a land's verify the daemon answered
    # NOTHING: no UI, no lane input, no `say`, no other repository's land. Measured 2026-08-20:
    # this repo's narrow verify holds it ~20 s and the full `dev gate` would hold it 4.6 minutes.
    #
    # THE ONLY HONEST PROOF IS A REAL CONCURRENT CALL. Reasoning about which thread runs what is
    # what made this defect invisible for as long as it existed, so the verify below genuinely
    # sleeps and the checks genuinely talk to the daemon while it does. `land-status` is read
    # BEFORE and AFTER the probes: "still running afterwards" is what makes the probes evidence
    # rather than a race that happened to land after the verify finished.
    #
    # No commit for the config change, following 10b: Config.For re-reads dodona.json per call
    # and the ticket branch never touches it, so a dirty shared checkout is harmless here.
    Set-Content "$root\dodona.json" '{ "main": "main", "verify": ["ping -n 8 127.0.0.1 > nul", "echo verify-ok"] }'
    $t8 = Dodona @("ticket-create", "--title", "ASYNC", "--claim", "path:src/async/pipe.cs")
    if ($t8 -match 'ticket (\d+) ') { $t8id = $Matches[1] }
    Dodona @("approve", "$t8id") | Out-Null
    Dodona @("token-request", "$t8id") | Out-Null
    $wt8 = "$root\.dodona\wt\t$t8id"
    New-Item -ItemType Directory -Force "$wt8\src\async" | Out-Null
    Set-Content "$wt8\src\async\pipe.cs" "// off the pipe"
    git -C $wt8 add -A
    git -C $wt8 -c user.email=t@t -c user.name=t commit -q -m "off the pipe"
    $t8Tip = (git -C $wt8 rev-parse HEAD)
    $mainBeforeAsync = (git -C $root rev-parse main)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $landAsync = Dodona @("land", "$t8id", "--no-wait")
    $startMs = $sw.ElapsedMilliseconds
    Check 'land_returns_before_its_verify_has_run' `
        ($DODONA_EXIT -eq 0 -and $landAsync -match "landing ticket $t8id" -and $startMs -lt 3000) "${startMs}ms: $landAsync"
    $stateEarly = Dodona @("land-status", "$t8id")
    Check 'land_status_says_running_while_it_runs' ($stateEarly -match 'state=running') $stateEarly

    # THE CHECK THIS PHASE EXISTS FOR. Under the old build this command could not be answered
    # at all until the land finished.
    #
    # MEASURED on this machine 2026-08-20, with `land-status` confirming the land was still
    # running at the time: `land --no-wait` returned in 142 ms while its verify slept ~7 s,
    # `status` answered in 131 ms and `say` in 161 ms (and the fake agent received it). The
    # 3000 ms budgets are a 20x margin for a loaded machine, not a measurement -- the
    # measurement is in this comment, and `state=running` is what makes the number mean
    # anything. Read them by setting a threshold to `-lt 1` and reading the FAIL detail.
    $statusSw = [Diagnostics.Stopwatch]::StartNew()
    $duringStatus = Dodona @("status")
    $statusMs = $statusSw.ElapsedMilliseconds
    $saySw = [Diagnostics.Stopwatch]::StartNew()
    $duringSay = Dodona @("say", "$plainId", "a sentence typed during a land")
    $sayMs = $saySw.ElapsedMilliseconds
    $sayExit = $DODONA_EXIT      # captured HERE: two more Dodona calls follow and each overwrites it
    # A second land of the same ticket, while one is in flight: impossible before (the serial
    # pipe made it impossible), so it is the one new race the split creates. Closed by name.
    $secondLand = Dodona @("land", "$t8id", "--no-wait")
    $secondLandExit = $DODONA_EXIT
    $stateLate = Dodona @("land-status", "$t8id")
    Check 'status_answers_during_a_land' `
        ($statusMs -lt 3000 -and $duringStatus -match 'lanes' -and $stateLate -match 'state=running') "${statusMs}ms late=[$stateLate] $duringStatus"
    Check 'say_answers_during_a_land' `
        ($sayMs -lt 3000 -and $sayExit -eq 0 -and $stateLate -match 'state=running') "${sayMs}ms exit=$sayExit late=[$stateLate] $duringSay"
    Check 'a_second_land_of_the_same_ticket_is_refused_while_one_runs' `
        ($secondLandExit -eq 1 -and $secondLand -match 'already landing') $secondLand

    # ...and the outcome still arrives. Nobody is holding the reply that used to carry it.
    Wait-Until { (Dodona @("land-status", "$t8id")) -match 'state=done' } 60000 "ticket $t8id's land to finish" | Out-Null
    $finalState = Dodona @("land-status", "$t8id")
    Check 'the_outcome_of_an_async_land_reaches_a_later_reader' `
        ($DODONA_EXIT -eq 0 -and $finalState -match 'state=done ok=1' -and $finalState -match "landed ticket $t8id") $finalState
    git -C $root merge-base --is-ancestor $t8Tip main
    $t8IsAncestor = ($LASTEXITCODE -eq 0)
    $mainAfterAsync = (git -C $root rev-parse main)
    Check 'an_async_land_still_advances_main' `
        ($t8IsAncestor -and $mainAfterAsync -ne $mainBeforeAsync) "ancestor=$t8IsAncestor before=$mainBeforeAsync after=$mainAfterAsync tip=$t8Tip"
    # The announcement is how the outcome reaches the operator when no caller is waiting, and
    # a land with no lane would otherwise finish in silence.
    $t8Panes = Invoke-StoreSql $storeDb "SELECT body FROM pane_events WHERE kind = 'announcement'"
    Check 'the_async_land_announces_its_outcome' ($t8Panes -match "landed ticket $t8id") $t8Panes

    # The waiting form, which is what every other check in this suite uses: `dodona land` still
    # prints the outcome and still exits with it, because a land that returned 0 the moment it
    # STARTED would put a fail-open in every script that lands and checks the exit code.
    Set-Content "$root\dodona.json" '{ "main": "main", "verify": ["echo verify-ok"] }'
    $t9 = Dodona @("ticket-create", "--title", "WAITED", "--claim", "path:src/waited/box.cs")
    if ($t9 -match 'ticket (\d+) ') { $t9id = $Matches[1] }
    Dodona @("approve", "$t9id") | Out-Null
    Dodona @("token-request", "$t9id") | Out-Null
    $wt9 = "$root\.dodona\wt\t$t9id"
    New-Item -ItemType Directory -Force "$wt9\src\waited" | Out-Null
    Set-Content "$wt9\src\waited\box.cs" "// waited"
    git -C $wt9 add -A
    git -C $wt9 -c user.email=t@t -c user.name=t commit -q -m "waited"
    $landWaited = Dodona @("land", "$t9id")
    Check 'the_waiting_form_prints_the_start_and_the_outcome' `
        ($DODONA_EXIT -eq 0 -and $landWaited -match "landing ticket $t9id" -and $landWaited -match "landed ticket $t9id") $landWaited

    # ---- 12. the causal chain is in the store (§12) ----
    $events = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
print('\n'.join(k for (k,) in db.execute('SELECT kind FROM events ORDER BY id')))
") | Out-String
    # R1 added three: the daemon merging main in, a conflict it refused to guess at, and a
    # verify that went red BEFORE the ref moved (which under the old order was unreachable --
    # verify_red could only ever be written after main had already advanced).
    # R3.5 added two more: a land that STARTED (which is all the pipe now knows) and one that
    # finished off it. Their absence would mean the land never left the pipe.
    # R4 adds the record and its D-R13 skip. The SKIP is in this list on purpose: a record kind
    # with no skip kind beside it would mean the gate never fired, which is one record per turn.
    foreach ($k in 'ticket_created','claim_overlap','token_refused_unapproved','token_granted','token_queued','landed','verify_green','token_expired_reclaimed','worktree_pruned','land_merged_main','land_conflict','verify_red','land_started','land_finished','completion_record','completion_record_unchanged') {
        Check "event_$k" ([bool]($events -match $k))
    }

    Dodona @("stop-daemon") | Out-Null
}
finally {
    # STOP THE LANES THIS SUITE STARTED. It never had any until layer 1 needed one (P1): the gate
    # checks used to drive `gate-hook` with no lane at all. Two fake agents then leaked on every
    # run for `dev test` to reap, and leaked shims are not cosmetic -- with strays alive a full
    # suite run went from 87 s to 300 s and reddened thirteen checks in suites nobody had edited.
    # Resolved from THIS workspace's own shim-info files, never by process name: a name-based kill
    # once murdered the operator's live session mid-trial (CLAUDE.md 4).
    if ($wsDir) { Stop-WorkspaceShims $wsDir }
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
