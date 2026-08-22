# Workspace acceptance: one project root, several repositories under it. One daemon, one
# store, one grid, one dispatcher — and a merge token PER REPOSITORY, so a ticket landing
# in `engine` never queues behind one landing in `tools`.
#
# The hard rule this encodes: a ticket lands by fast-forwarding one repository, and two
# fast-forwards cannot be atomic, so one ticket = one repository. Claims are
# workspace-relative paths, so they already say which one — no new syntax.
#
# Fake agents and scripted git only — zero model calls.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'ws'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'workspace-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# A workspace: not a repository itself, holding three that are, plus a docs folder that
# belongs to no repository at all.
$root = Join-Path (Use-SuiteTemp) ("dodona-ws-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\docs" | Out-Null
Set-Content "$root\docs\notes.md" "# workspace notes"
foreach ($r in 'engine', 'tools') {
    New-Item -ItemType Directory -Force "$root\$r\src" | Out-Null
    Set-Content "$root\$r\src\main.cs" "// $r"
    Set-Content "$root\$r\.gitignore" ".dodona/"
    git -C "$root\$r" init -b main -q
    git -C "$root\$r" add -A
    git -C "$root\$r" -c user.email=t@t -c user.name=t commit -q -m init
}

$results = [ordered]@{}
$errFile = Join-Path $out 'stderr.tmp'
function Dodona([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    Remove-Item $errFile -ErrorAction SilentlyContinue
    $o = (& $dodona ($a + @('--root', $root)) 2> $errFile) | Out-String
    $global:DODONA_EXIT = $LASTEXITCODE
    $e = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
    ("$o`n$e").Trim()
}
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
function Commit([string]$wt, [string]$msg) {
    git -C $wt add -A
    git -C $wt -c user.email=t@t -c user.name=t commit -q -m $msg
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

    # (moved to unit:Dodona.Tests.RepoDiscoveryTests.discovers_repos -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # ---- a ticket infers its repository from its claim paths ----
    $t1 = Dodona @("ticket-create", "--title", "ENGINE", "--claim", "subtree:engine/src")
    $t2 = Dodona @("ticket-create", "--title", "TOOLS", "--claim", "subtree:tools/src")
    # (moved to unit:Dodona.Tests.RepoResolutionTests -- infers_repo_from_claims, second_repo_ticket -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # the worktree is a worktree OF THAT REPO, branched from its own main
    $wt1 = "$root\.dodona\wt\t1"
    $wt2 = "$root\.dodona\wt\t2"
    $origin1 = (git -C $wt1 rev-parse --show-toplevel)
    Check 'worktree_belongs_to_its_repo' ((Test-Path "$wt1\src\main.cs") -and (Get-Content "$wt1\src\main.cs") -eq '// engine') "$origin1"
    Check 'second_worktree_is_other_repo' ((Get-Content "$wt2\src\main.cs") -eq '// tools') ''

    # ---- a ticket spanning repositories is refused, with the reason ----
    $span = Dodona @("ticket-create", "--title", "SPAN", "--claim", "path:engine/src/a.cs", "--claim", "path:tools/src/b.cs")
    # (moved to unit:Dodona.Tests.RepoResolutionTests.cross_repo_ticket_refused -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # ---- a claim in no repository is refused, and says what there is ----
    $homeless = Dodona @("ticket-create", "--title", "DOCS", "--claim", "path:docs/notes.md")
    # (moved to unit:Dodona.Tests.RepoResolutionTests.claim_outside_any_repo_refused -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # ---- the claim gate works in workspace terms (worktree path -> workspace path) ----
    $covered = Dodona @("claim-check", "1", "$wt1\src\main.cs")
    Check 'gate_allows_claimed_path' ($DODONA_EXIT -eq 0 -and $covered -match 'covered: engine/src/main.cs') $covered
    $denied = Dodona @("claim-check", "1", "$root\tools\src\main.cs")
    Check 'gate_denies_other_repo' ($DODONA_EXIT -eq 1 -and $denied -match 'denied') $denied

    # ---- THE POINT: two repositories land in parallel, no queueing ----
    Set-Content "$wt1\src\main.cs" "// engine v2"
    Commit $wt1 "engine work"
    Set-Content "$wt2\src\main.cs" "// tools v2"
    Commit $wt2 "tools work"
    Dodona @("approve", "1") | Out-Null
    Dodona @("approve", "2") | Out-Null
    $g1 = Dodona @("token-request", "1", "--lease", "300")
    $g2 = Dodona @("token-request", "2", "--lease", "300")
    Check 'both_repos_hold_tokens_at_once' ($g1 -match 'granted ticket 1' -and $g2 -match 'granted ticket 2') "$g1 | $g2"
    $ts = Dodona @("token-status")
    Check 'token_status_is_per_repo' (($ts -split "`r?`n" | Where-Object { $_ -match 'holder=[12]\b' }).Count -eq 2) $ts

    # ---- within ONE repository the token still serializes (M1's guarantee intact) ----
    $t3 = Dodona @("ticket-create", "--title", "ENGINE2", "--claim", "path:engine/README.md")
    Dodona @("approve", "3") | Out-Null
    $q = Dodona @("token-request", "3")
    Check 'same_repo_still_serializes' ($q -match 'queued ticket 3') $q

    # ---- naming the repo does not buy a way past claim validation (P0.6) ----
    # `--repo X` skipped Repos.ForClaims ENTIRELY, so this created a ticket in `tools` holding a
    # claim over `engine` -- and then the gate prefixed the claim with `tools/`, the merge
    # backstop diffed `tools`, and the land fast-forwarded `tools`, while the agent edited
    # `engine`. Every one of those disagreed silently. The inference path has always refused a
    # cross-repo claim; naming the repo went straight past the refusal.
    $wrongRepo = Dodona @("ticket-create", "--title", "WRONGREPO", "--repo", "tools", "--claim", "path:engine/src/main.cs")
    # (moved to unit:Dodona.Tests.RepoResolutionTests.named_repo_still_validates_its_claims -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)
    # (the ordinary case -- a claim that IS in the named repo -- is asserted in the `pair`
    # fixture below, where creating a ticket cannot shift the ticket ids these checks hardcode)

    # ---- claim-extend cannot widen a ticket into a different repository (P0.5) ----
    # Store.ClaimExtend takes only a ticket id, and the daemon never looked the repo up -- so an
    # extension could hand ticket 3 (in `engine`) a claim over `tools`, which is the same hole
    # P0.6 leaves in ticket-create, reached from the other side. A ticket lands by
    # fast-forwarding ONE repository; its claims have to stay in that one.
    $xRepo = Dodona @("claim-extend", "3", "--claim", "path:tools/src/main.cs")
    Check 'claim_extend_cannot_cross_repositories' `
        ($DODONA_EXIT -ne 0 -and $xRepo -match 'not in repository engine') $xRepo

    # ---- landing goes to that repository's main, and only that one ----
    $l1 = Dodona @("land", "1")
    Check 'lands_in_its_own_repo' ($l1 -match 'landed ticket 1 on engine/main') $l1
    Check 'engine_main_advanced' ((git -C "$root\engine" show "main:src/main.cs") -eq '// engine v2') ''
    Check 'tools_main_untouched' ((git -C "$root\tools" show "main:src/main.cs") -eq '// tools') ''
    $l2 = Dodona @("land", "2")
    Check 'second_repo_lands_too' ($l2 -match 'landed ticket 2 on tools/main') $l2

    # queued ticket 3 gets engine's token now that ticket 1 released it
    $g3 = Dodona @("token-request", "3")
    Check 'queue_advances_within_repo' ($g3 -match 'granted ticket 3') $g3
    Dodona @("token-release", "3") | Out-Null

    # ---- what the branch touched is RECORDED in workspace terms (D-R5/D-R7, R3) ----
    #
    # RE-AIMED from `backstop_uses_workspace_paths`. The property under test is unchanged and is
    # the reason this check exists: git speaks REPO-relative and claims are WORKSPACE-relative, so
    # a multi-repo workspace must prefix `other/sneaky.cs` up to `engine/other/sneaky.cs` before
    # comparing. Getting that wrong compares two different namespaces and the answer is garbage in
    # whichever direction. Only the observable moved: the token is granted now (the refusal is
    # retired), and the prefixed path appears in the record a reviewer reads instead.
    $t4 = Dodona @("ticket-create", "--title", "SNEAK", "--claim", "path:engine/src/main.cs")
    $wt4 = "$root\.dodona\wt\t4"
    Set-Content "$wt4\src\main.cs" "// engine v3"
    New-Item -ItemType Directory -Force "$wt4\other" | Out-Null
    Set-Content "$wt4\other\sneaky.cs" "// out of claim"
    Commit $wt4 "claimed + sneaky"
    Dodona @("approve", "4") | Out-Null
    $back = Dodona @("token-request", "4")
    Check 'an_out_of_claim_branch_is_no_longer_refused_the_token' ($DODONA_EXIT -eq 0 -and $back -match 'granted ticket 4') $back
    # The third site of issue #10's defect, found by sweeping for it rather than by waiting for it
    # to go red: `branch_touched` is written by the daemon AFTER `token-request` has answered, so
    # an unguarded read returns an empty string under load -- failing the assertion and printing
    # nothing, because the detail IS the value it failed on. The other two are in m2.
    $touchedWs = ''
    $touchedWsOk = Wait-Until {
        $script:touchedWs = ((Invoke-StoreSql $storeDb "SELECT detail FROM events WHERE kind='branch_touched'") -replace '\s+', ' ').Trim()
        $script:touchedWs -ne ''
    } 20000 'the branch_touched row for the out-of-claim branch'
    Check 'the_touch_record_uses_workspace_paths' ($touchedWs -match 'engine/other/sneaky\.cs') `
        "row-arrived=$touchedWsOk detail=[$touchedWs]"
    Dodona @("token-release", "4") | Out-Null

    # ---- CLAIMS ARE PER REPOSITORY: the same claim string, twice, in two repos (Phase 0b) ----
    #
    # THE CHECK THIS PHASE EXISTS FOR, and LOCATIONS-PLAN.md said flatly that it "cannot be
    # written today". A `symbol:` claim carries no path (Claims.Parse leaves it alone), ForClaims
    # skips it, and Overlap compared bare equality -- so a symbol named the whole WORKSPACE, and
    # holding `symbol:Config` in `engine` refused it in `tools`, where it is a different file in
    # a different repository that no agent in `engine` can even reach. Under HEAD the second
    # create prints "conflict: symbol:Config overlaps ... held by ticket 5".
    $sym1 = Dodona @("ticket-create", "--title", "SYM-ENGINE", "--repo", "engine", "--claim", "symbol:Config")
    Check 'a_symbol_claim_can_be_held_in_one_repo' ($sym1 -match 'ticket 5') $sym1
    $sym2 = Dodona @("ticket-create", "--title", "SYM-TOOLS", "--repo", "tools", "--claim", "symbol:Config")
    Check 'the_same_claim_string_is_free_in_a_different_repo' ($DODONA_EXIT -eq 0 -and $sym2 -match 'ticket 6') $sym2
    # ...AND THE DETECTION SURVIVED, which is the half that makes the pair worth anything. A
    # "scoping" that simply stopped comparing symbols would turn the check above green while
    # letting two agents rename one identifier -- the worst outcome available to this phase, and
    # indistinguishable from the fix unless something asserts the detection is still reachable.
    #
    # RE-AIMED from `the_same_claim_string_in_the_SAME_repo_is_still_refused` (R3): the overlap is
    # REPORTED rather than refused now (D-R5), and it is the detection this check was always
    # about. Asserting the refusal would assert a lock the operator retired; asserting the report
    # still fails the instant the scoping stops comparing symbols in one repo.
    $symSame = Dodona @("ticket-create", "--title", "SYM-AGAIN", "--repo", "engine", "--claim", "symbol:Config")
    Check 'the_same_claim_string_in_the_SAME_repo_is_still_detected' `
        ($DODONA_EXIT -eq 0 -and $symSame -match 'overlap:' -and $symSame -match 'ticket 5') $symSame
    # (the PATH half of cross-repo independence needs a repository whose NAME has drifted, so it
    # lives in the repo-identity fixture below -- here `engine/...` and `tools/...` never
    # collided in the first place, and a check that cannot fail is worth nothing)

    # ---- a lane needs no repository: an agent can run with no git involved at all ----
    # RENAMED FROM `lanes_are_workspace_wide` (docs/LOCATIONS-PLAN.md Phase 2). Its stated
    # premise was that lanes belong to the workspace rather than to a place, and this phase
    # contradicts that: a lane now opens in ONE project, chosen at the spawn site, and
    # `lane-start --project` is how. What the assertion below actually tested survives
    # unchanged and is still worth holding -- a lane needs no repository, only tickets do --
    # so the name was the wrong half, not the check.
    $ls = Dodona @("lane-start", "--title", "DOCS", "--child", $fake)
    if ($ls -match 'lane (\d+)') { $lane = $Matches[1] } else { throw "lane-start failed: $ls" }
    Dodona @("say", "$lane", "say lanes span the workspace") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match 'lanes span the workspace' } 20000 'the lane answers' | Out-Null
    Check 'a_lane_needs_no_repository' ((Dodona @("tail", "$lane", "5")) -match 'lanes span the workspace') ''

    # ---- one store, one causal chain for the whole workspace ----
    $ev = (python -c "
import sqlite3
db = sqlite3.connect(r'$storeDb')
for r in db.execute('''SELECT detail FROM events WHERE kind='ticket_created' ORDER BY id'''): print(r[0])
") | Out-String
    Check 'one_causal_chain_names_repos' ($ev -match 'repo engine' -and $ev -match 'repo tools') $ev

    # =====================================================================================
    # WORKSPACE IDENTITY AND REPO EXCLUSIVITY (docs/WORKSPACES-CONCIERGE.md §1/§3)
    #
    # Everything above still passes because a one-member workspace is indistinguishable
    # from the old root-anchored instance — that is the degenerate case the design promises.
    # What follows tests the parts that are NEW, and one of them is load-bearing:
    #
    # Path-derived identity used to make "two merge tokens over one main" structurally
    # impossible: two spellings of a repo hashed to one id, one mutex, one token. Named
    # workspaces delete that, so the invariant moved up a level and became registry law.
    # If these checks ever go red, the guarantee this whole system exists to provide is
    # gone — treat a failure here as a correctness incident, not a test problem.
    # =====================================================================================

    # ---- identity is a generated slug, and the store left the project folder ----
    # (moved to unit:Dodona.Tests.RegistryIdentityTests.identity_is_a_slug_not_a_path_hash -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)
    Check 'store_lives_in_workspace_territory' `
        ($storeDb.StartsWith($dodonaHome) -and (Test-Path $storeDb)) $storeDb
    Check 'store_is_not_under_the_project_root' (-not (Test-Path "$root\.dodona\store.db")) ''
    # ...but worktrees deliberately DID stay beside their repo (§1's stated exception)
    Check 'worktrees_stayed_beside_the_repo' (Test-Path "$root\.dodona\wt\t4") 

    # ---- THE INVARIANT: a repo belongs to at most one workspace ----
    # Fresh fixtures, so the live workspace above is never disturbed.
    $solo = Join-Path (Use-SuiteTemp) ("dodona-solo-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $solo | Out-Null
    Set-Content "$solo\a.txt" "solo"
    git -C $solo init -b main -q
    git -C $solo add -A
    git -C $solo -c user.email=t@t -c user.name=t commit -q -m init
    $shared = Join-Path (Use-SuiteTemp) ("dodona-notes-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $shared | Out-Null

    # Extra daemons for the extra workspaces these checks address. This suite owns daemon
    # lifetime (DODONA_NO_AUTOSTART=1 - start-on-demand must not join in), so they are
    # started explicitly and tracked for the scoped cleanup in `finally`.
    $extraDaemons = New-Object System.Collections.ArrayList
    function StartDaemonFor([string]$wsId) {
        $p = Start-Process $dodona -ArgumentList "daemon", "--workspace", $wsId -PassThru -NoNewWindow `
            -RedirectStandardOutput "$out\daemon-$wsId.out" -RedirectStandardError "$out\daemon-$wsId.err"
        [void]$extraDaemons.Add($p)
        Wait-Daemon (& $dodona where --workspace $wsId --json | Out-String | ConvertFrom-Json).ctlPipe | Out-Null
        $p
    }

    # Stop a daemon and WAIT FOR THE CTL PIPE TO GO. A daemon that has been asked to stop still
    # holds `Global\dodona-<id>` for a moment, and a non-successor start makes exactly ONE attempt
    # at that mutex -- so an immediate restart loses the race, prints "another daemon already
    # owns workspace", and every check after it fails for a reason that has nothing to do with
    # what it was testing. The repo-identity section below restarts three times, and it also
    # rewrites the store file with python between two of them, which cannot be done while a
    # writer holds it.
    function StopDaemonFor([string]$wsId) {
        $ctl = (& $dodona where --workspace $wsId --json | Out-String | ConvertFrom-Json).ctlPipe
        DodonaBare @("stop-daemon", "--workspace", $wsId) | Out-Null
        Wait-Until { -not (Test-DodonaPipe $ctl) } 15000 "the daemon for $wsId is down" | Out-Null
    }
    function RestartDaemonFor([string]$wsId) { StopDaemonFor $wsId; StartDaemonFor $wsId }

    function DodonaBare([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        Remove-Item $errFile -ErrorAction SilentlyContinue
        $o = (& $dodona $a 2> $errFile) | Out-String
        $global:DODONA_EXIT = $LASTEXITCODE
        $e = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
        ("$o`n$e").Trim()
    }

    # $solo resolves into a workspace of its own (named after the folder, sole member)
    $soloWs = Get-WorkspacePaths $dodona $solo
    DodonaBare @("workspace-create", "--name", "rival") | Out-Null
    $steal = DodonaBare @("workspace-attach", "--member", $solo, "--workspace", "rival")
    # WHITESPACE-NORMALISED BEFORE MATCHING, and this is not defensive padding -- it is a trap
    # that fired for real. PowerShell WRAPS a native command's stderr to the console width when
    # it renders the error record, inserting a newline mid-sentence. The phrase this looks for
    # sits immediately after a temp path, so when that path grew by ~24 characters (suites moved
    # into a per-run sandbox) the wrap landed between "already" and "belongs" and a check that
    # had passed for months went red -- while the product was refusing correctly, which is a
    # FALSE RED and every bit as costly as a false green. Never regex across a space in captured
    # native stderr without collapsing it first.
    $stealFlat = ($steal -replace '\s+', ' ')
    # (moved to unit:Dodona.Tests.RegistryExclusivityTests -- repo_in_two_workspaces_refused,
    #  refusal_says_why_two_tokens_is_the_problem, refusal_offers_the_move_affordance -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # A BARE FOLDER is exempt and must stay exempt: there is no merge token to split, and
    # a shared notes folder in two workspaces harms nobody.
    $b1 = DodonaBare @("workspace-attach", "--member", $shared, "--workspace", "rival")
    $b2 = DodonaBare @("workspace-attach", "--member", $shared, "--workspace", $soloWs.Id)
    # (moved to unit:Dodona.Tests.RegistryExclusivityTests.bare_folder_may_be_shared -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # Reassignment is legitimate — that is what the refusal points at — and it is atomic:
    # the repo is never in two workspaces and never in none.
    $moved = DodonaBare @("workspace-move", "--member", $solo, "--workspace", "rival")
    $wsList = DodonaBare @("workspaces", "--json") | ConvertFrom-Json
    $ownersOfSolo = @($wsList | Where-Object { $_.members.path -contains (Resolve-Path $solo).Path })
    # (moved to unit:Dodona.Tests.RegistryIdentityTests.move_reassigns_the_repo -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    $rival = ($wsList | Where-Object { $_.name -eq 'rival' }).id

    # ---- layer 3: the check where a merge token is actually at stake ----
    # The one hole attach-time enforcement cannot cover, BY DESIGN: a bare folder is exempt
    # and may legitimately live in two workspaces — until someone runs `git init` in it.
    # The row was valid when it was written; only a check at the point of use notices the
    # ground moved. Same shape as the diff backstop behind the claim gate (§6).
    #
    # Its own fixture and its own two workspaces: the pair above are now doing other jobs,
    # and a check this load-bearing should not depend on their state.
    $drift = Join-Path (Use-SuiteTemp) ("dodona-drift-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $drift | Out-Null
    DodonaBare @("workspace-create", "--name", "drift-a", "--member", $drift) | Out-Null
    DodonaBare @("workspace-create", "--name", "drift-b", "--member", $drift) | Out-Null
    $driftA = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'drift-a' }).id
    $driftB = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'drift-b' }).id
    # VACUOUS-GUARD (S-IDENTITY, and MEASURED rather than guessed). This asserts only that two
    # workspaces exist. `workspace-create --member <folder>` still CREATES the workspace when the
    # attach is REFUSED, so it passes with the bare-folder exemption deleted underneath it: it was
    # declared expects-red on tests\mutants\s-identity-07.patch, which makes every folder look
    # like a repository, and dev prove answered `VACUOUS ... PASS`. Its NAME promises a membership
    # it never reads. KEPT, because plan 5.4.1's question answers yes -- a `workspace-create` that
    # stopped creating anything would still fail here -- and the membership half now runs as
    # unit:Dodona.Tests.RegistryExclusivityTests.bare_folder_in_two_workspaces_is_allowed.
    Check 'bare_folder_in_two_workspaces_is_allowed' ($null -ne $driftA -and $null -ne $driftB) "a=$driftA b=$driftB"

    # ...and NOW it becomes a repo, behind the registry's back.
    git -C $drift init -b main -q
    Set-Content "$drift\note.md" "# now a repo"
    git -C $drift add -A
    git -C $drift -c user.email=t@t -c user.name=t commit -q -m init

    StartDaemonFor $driftA | Out-Null
    $sneakTicket = DodonaBare @("ticket-create", "--title", "SNEAK2", "--claim", "path:note.md", "--workspace", $driftA)
    Check 'ticket_refused_when_repo_is_not_exclusive' `
        ($DODONA_EXIT -ne 0 -and $sneakTicket -match 'also belongs to workspace') $sneakTicket
    Check 'exclusivity_backstop_offers_the_move' ($sneakTicket -match 'workspace-move --member') $sneakTicket
    # And it is recorded: a refusal with no event row naming why would be a bug (DEBUGGING.md).
    $driftStore = (DodonaBare @("where", "--workspace", $driftA, "--json") | ConvertFrom-Json).store
    $driftEv = (python -c "
import sqlite3
db = sqlite3.connect(r'$driftStore')
for r in db.execute('''SELECT kind FROM events WHERE kind='ticket_repo_not_exclusive' '''): print(r[0])
") | Out-String
    Check 'exclusivity_refusal_is_in_the_causal_chain' ($driftEv -match 'ticket_repo_not_exclusive') $driftEv
    DodonaBare @("stop-daemon", "--workspace", $driftA) | Out-Null

    # ---- renaming re-derives nothing: name is display, id is identity (§1) ----
    $before = Get-WorkspacePaths $dodona $solo
    DodonaBare @("workspace-rename", "renamed-rival", "--workspace", $rival) | Out-Null
    $after = DodonaBare @("where", "--workspace", $rival, "--json") | ConvertFrom-Json
    Check 'rename_keeps_the_id' ($after.id -eq $rival) "$($after.id) vs $rival"
    Check 'rename_keeps_the_store_path' ($after.store -eq $before.Store) "$($after.store)"
    Check 'rename_keeps_the_ctl_pipe' ($after.ctlPipe -eq $before.CtlPipe) "$($after.ctlPipe)"
    Check 'new_name_resolves' ((DodonaBare @("where", "--workspace", "renamed-rival", "--json") | ConvertFrom-Json).id -eq $rival) ''

    # ---- an alias is how rung 4 decays toward rung 1 (§4) ----
    DodonaBare @("workspace-alias", "the-rival", "--workspace", $rival) | Out-Null
    Check 'alias_resolves_to_the_workspace' `
        ((DodonaBare @("where", "--workspace", "the-rival", "--json") | ConvertFrom-Json).id -eq $rival) ''

    # ---- naming a workspace that does not exist is a typo, not an invitation ----
    $typo = DodonaBare @("status", "--workspace", "no-such-workspace")
    Check 'unknown_workspace_name_is_refused' ($DODONA_EXIT -ne 0 -and $typo -match 'no workspace') $typo

    # ---- migration: a pre-workspace instance becomes a workspace named after its root ----
    $legacy = Join-Path (Use-SuiteTemp) ("dodona-legacy-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$legacy\.dodona" | Out-Null
    # A store shaped like the real thing, so the assertion is "this exact file moved".
    $marker = "legacy-store-" + [guid]::NewGuid().ToString('N')
    Set-Content "$legacy\.dodona\store.db" $marker
    Set-Content "$legacy\.dodona\shim-lane7.json" '{"shimPid":0,"childPid":0,"pipeName":"x"}'
    $mig = Get-WorkspacePaths $dodona $legacy
    Check 'migrated_workspace_named_after_its_root' ($mig.Name -eq (Split-Path -Leaf $legacy)) $mig.Name
    Check 'migration_moved_the_store' `
        ((Test-Path $mig.Store) -and (Get-Content $mig.Store -Raw).Trim() -eq $marker -and -not (Test-Path "$legacy\.dodona\store.db")) $mig.Store
    Check 'migration_moved_the_shim_info' `
        ((Test-Path "$($mig.Dir)\shim-lane7.json") -and -not (Test-Path "$legacy\.dodona\shim-lane7.json")) ''
    $migRow = (DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.id -eq $mig.Id }
    Check 'migrated_root_is_the_sole_member' (@($migRow.members).Count -eq 1) "members=$(@($migRow.members).Count)"

    # ---- Dodona does not INVENT a workspace for a folder nobody named -------------------
    # docs/LOCATIONS-PLAN.md Phase 0c / D-L9, operator 2026-08-19: *creating a workspace is a
    # user action.* The incident: an agent inside a lane ran an ordinary `dodona` command; the
    # CLI had no workspace id in its environment, fell back to Environment.CurrentDirectory,
    # and CREATED a workspace named after whatever folder the daemon happened to spawn that
    # process in -- moving a legacy store into workspace territory on the way.
    #
    # THIS SITS DELIBERATELY BESIDE THE MIGRATION SET ABOVE, which must keep passing: `--root
    # <p> --adopt` still creates, and it is the whole invisible-migration mechanism. The
    # distinction being asserted here is provenance, not the path -- the same folder either
    # creates or refuses depending on whether anybody asked for it.
    #
    # THE COMMENT ABOVE USED TO SAY "an EXPLICIT `--root` still creates", and issue #12 is what
    # that cost. A typed `--root` alone no longer creates: naming a path is not adopting it,
    # and `--adopt` is how a caller says it means the second thing. Get-WorkspacePaths passes
    # it; the checks below assert that nothing else gets it for free.
    $stray = Join-Path (Use-SuiteTemp) ("dodona-stray-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$stray\.dodona" | Out-Null
    # Shaped like a real legacy store, so "the store did not move" is an assertion about this
    # exact file rather than about a path that never existed.
    $strayMarker = "stray-store-" + [guid]::NewGuid().ToString('N')
    Set-Content "$stray\.dodona\store.db" $strayMarker

    # LAND THE JSON IN A VARIABLE BEFORE COUNTING IT. `ConvertFrom-Json` emits a JSON ARRAY as
    # ONE pipeline item (CLAUDE.md §0.2), so `@(cmd | ConvertFrom-Json).Count` is 1 however
    # many workspaces exist. The first version of `inherited_cwd_creates_no_workspace` compared
    # 1 to 1 and came back VACUOUS from `dev prove` -- passing against a HEAD that was
    # cheerfully inventing a workspace underneath it. Third time this trap has cost this repo a
    # silent no-op check.
    function WsCount { $all = DodonaBare @("workspaces", "--json") | ConvertFrom-Json; @($all).Count }
    $wsBefore = WsCount

    # An AGENT-SHAPED invocation: no --root, no --workspace, cwd inherited from whoever
    # started the process. Push-Location is how a suite can be somewhere else without
    # spawning a shell -- `& $dodona` inherits PowerShell's own working directory.
    Push-Location $stray
    try { $strayOut = DodonaBare @("tickets") } finally { Pop-Location }
    $strayExit = $DODONA_EXIT
    $wsAfter = WsCount
    # COUNT, not just the message. A refusal that still wrote a registry row would print
    # exactly the same words -- and "a message appeared" is the assertion that lets a phantom
    # workspace ship anyway.
    Check 'inherited_cwd_creates_no_workspace' `
        ($strayExit -ne 0 -and $wsAfter -eq $wsBefore) `
        "exit=$strayExit before=$wsBefore after=$wsAfter :: $strayOut"
    Check 'inherited_cwd_does_not_move_a_legacy_store' `
        ((Test-Path "$stray\.dodona\store.db") -and (Get-Content "$stray\.dodona\store.db" -Raw).Trim() -eq $strayMarker) `
        "still there: $(Test-Path "$stray\.dodona\store.db")"
    # CLAUDE.md §0.1: a refusal must name the thing that un-sticks it. Whitespace-normalised
    # first -- PowerShell WRAPS captured native stderr to the console width, so a phrase
    # spanning a space can match today and fail tomorrow because a path got longer (§0.2).
    Check 'the_refusal_names_the_command_that_makes_a_workspace' `
        ((($strayOut -replace '\s+', ' ') -match 'workspace-create --name') -and
         (($strayOut -replace '\s+', ' ') -match 'user action')) $strayOut

    # ---- DODONA_WORKSPACE: the workspace an agent is already in (P0c.1/P0c.2) ------------
    # The daemon stamps this on every lane it spawns (Daemon.AttachShimAsync), which is what
    # gives an agent's own `dodona` commands an answer that is not a guess. Asserted here from
    # the environment directly, so the resolution ladder is testable without a daemon at all.
    $env:DODONA_WORKSPACE = $mig.Id
    try {
        Push-Location $stray
        try { $envWhere = DodonaBare @("where", "--json") } finally { Pop-Location }
        $envId = try { ($envWhere | ConvertFrom-Json).id } catch { "unparseable: $envWhere" }
        $wsEnv = WsCount
        Check 'env_workspace_is_used_before_any_folder' `
            ($envId -eq $mig.Id -and $wsEnv -eq $wsBefore) `
            "id=$envId want=$($mig.Id) workspaces=$wsEnv/$wsBefore"

        # PRECEDENCE, and this one is a PIN rather than a proof: `--workspace` -> explicit
        # `--root` -> DODONA_WORKSPACE -> the inherited cwd. The plan wrote the middle pair the
        # other way round; an environment variable silently overruling a typed argument is the
        # compiles-clean, acts-on-the-wrong-workspace failure Phase 0c exists to remove, and it
        # would also break every suite the moment one ran inside a lane -- they all pass --root,
        # and their workspaces live in an isolated DODONA_HOME the inherited id knows nothing
        # about. `dev prove` calls this VACUOUS by construction: nothing in the fix makes it
        # pass, it is here so a later reordering cannot pass quietly.
        # $root, not $solo: `workspace-move` above reassigned $solo to `rival`, so $soloWs.Id
        # is deliberately stale by here. $root's ownership never changes in this suite.
        $rootWins = DodonaBare @("where", "--root", $root, "--json")
        $rootWinsId = try { ($rootWins | ConvertFrom-Json).id } catch { "unparseable: $rootWins" }
        Check 'an_explicit_root_beats_the_inherited_env' ($rootWinsId -eq $ws.Id) `
            "id=$rootWinsId want=$($ws.Id)"
    }
    finally { Remove-Item env:DODONA_WORKSPACE -ErrorAction SilentlyContinue }

    # A STALE id must be neither obeyed nor swallowed. It happens for real: the workspace was
    # forgotten, or DODONA_HOME moved, and the lane's environment still carries the old id. A
    # silent degrade is a bug (CLAUDE.md §3's dead routing ladder), and hard-failing on a
    # leftover variable would strand a lane for no reason -- so it says so and carries on down
    # the ladder, which then refuses the unowned cwd on its own terms.
    $env:DODONA_WORKSPACE = 'no-such-workspace-id'
    try {
        Push-Location $stray
        try { $staleOut = DodonaBare @("tickets") } finally { Pop-Location }
        $staleExit = $DODONA_EXIT
    }
    finally { Remove-Item env:DODONA_WORKSPACE -ErrorAction SilentlyContinue }
    $wsStale = WsCount
    Check 'a_stale_env_workspace_is_announced_and_still_creates_nothing' `
        ((($staleOut -replace '\s+', ' ') -match 'names no workspace in this registry') -and
         $staleExit -ne 0 -and $wsStale -eq $wsBefore) `
        "exit=$staleExit workspaces=$wsStale/$wsBefore :: $staleOut"

    # ---- A NAMED `--root` IS NOT AN ADOPTION EITHER (issue #12) --------------------------
    # The sibling of the D-L9 set above, one provenance along. D-L9 split "nobody said this
    # path" from "somebody said it" and let the second create. A typed path can be a SUBJECT OF
    # INQUIRY rather than a DECLARATION OF OWNERSHIP, and nothing distinguished them.
    #
    # THE INCIDENT (2026-08-21, commit 2ef0c54): a session asked "is Dodona running anything in
    # the operator's other project?" and answered it with `dodona where --root <that project>`
    # -- a command CLAUDE.md §3.2 lists under "commands that observe". It started nothing, and
    # it registered that folder as a workspace. Had the folder held a legacy `.dodona\store.db`
    # it would also have MOVED that store out of it and written a file into it.
    #
    # The same fixture shape as the stray set above, and for the same reason: a real-looking
    # legacy store makes "the store did not move" an assertion about this exact file.
    $named = Join-Path (Use-SuiteTemp) ("dodona-named-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$named\.dodona" | Out-Null
    $namedMarker = "named-store-" + [guid]::NewGuid().ToString('N')
    Set-Content "$named\.dodona\store.db" $namedMarker

    $namedBefore = WsCount
    $namedOut = DodonaBare @("where", "--root", $named, "--json")
    $namedExit = $DODONA_EXIT
    # COUNT, not just the message -- the same reasoning as `inherited_cwd_creates_no_workspace`
    # above: a refusal that still wrote a registry row prints exactly the same words.
    Check 'a_named_root_creates_no_workspace' `
        ($namedExit -ne 0 -and (WsCount) -eq $namedBefore) `
        "exit=$namedExit before=$namedBefore after=$(WsCount) :: $namedOut"
    Check 'a_named_root_does_not_move_a_legacy_store' `
        ((Test-Path "$named\.dodona\store.db") -and (Get-Content "$named\.dodona\store.db" -Raw).Trim() -eq $namedMarker) `
        "still there: $(Test-Path "$named\.dodona\store.db")"
    # A refusal must name what un-sticks it (CLAUDE.md §0.1). Whitespace-normalised first:
    # PowerShell WRAPS captured native stderr to the console width, so a phrase spanning a
    # space can match today and fail tomorrow because a path got longer (§0.2).
    Check 'the_named_root_refusal_names_adopt' `
        ((($namedOut -replace '\s+', ' ') -match '--adopt')) $namedOut

    # AND IT IS NOT A PROPERTY OF `where`. The adoption was never per-verb: `Client`'s first
    # statement resolves the workspace, so every verb that talks to a daemon adopted on sight
    # -- including `status`, which was put on the no-summon list to make it safe and then
    # reported the workspace it had just invented as ASLEEP. Asserting it through `status`
    # rather than `where` is what makes this a check about the resolution layer.
    #
    # ITS OWN FRESH DIRECTORY, and that is load-bearing rather than tidiness: against the build
    # this is proved against, the `where` call above ADOPTS $named -- so a `status` reusing it
    # would find an owner, create nothing, and come back VACUOUS while the bug was still live.
    $namedS = Join-Path (Use-SuiteTemp) ("dodona-named-s-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $namedS | Out-Null
    $namedStatusBefore = WsCount
    $namedStatusOut = DodonaBare @("status", "--root", $namedS)
    Check 'a_named_root_creates_no_workspace_for_a_daemon_command' `
        ((WsCount) -eq $namedStatusBefore) `
        "before=$namedStatusBefore after=$(WsCount) :: $namedStatusOut"

    # THE OTHER DIRECTION: `--adopt` must still create, or the migration mechanism goes with
    # the fix. This is the one thing every acceptance suite depends on (Get-WorkspacePaths),
    # and a change that refused it too would fail sixteen suites at startup with no explanation
    # of why.
    #
    # READ ITS PROOF HONESTLY. `dev prove` reports it PROVEN, and that is an ARTIFACT OF
    # ORDERING rather than teeth of its own: against the old build the `where` above already
    # adopted $named, so by here there is nothing left to create and the count does not move.
    # It is a PIN on a property the old build also had. Do not cite it as evidence that
    # adoption works -- `migration_moved_the_store` above is that evidence, and it runs through
    # Get-WorkspacePaths, which is the caller that actually matters.
    $adoptBefore = WsCount
    $adoptOut = DodonaBare @("where", "--root", $named, "--adopt", "--json")
    Check 'an_adopted_root_still_creates' `
        ($DODONA_EXIT -eq 0 -and (WsCount) -eq ($adoptBefore + 1)) `
        "exit=$DODONA_EXIT before=$adoptBefore after=$(WsCount) :: $adoptOut"

    # ---- creating a workspace cannot quietly take an owned repo with it ----
    DodonaBare @("workspace-create", "--name", "twin", "--member", $solo) | Out-Null
    # the workspace IS created; the attach inside it is what gets refused
    # (moved to unit:Dodona.Tests.RegistryExclusivityTests.creating_a_workspace_cannot_steal_an_owned_repo -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)
    $twin = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'twin' }).id

    # ---- multi-member: repo names gain a member prefix only when they must ----
    # Two members, so `.` is no longer an unambiguous name and a member prefix appears.
    # A ONE-member workspace must keep the old names byte-for-byte, which is what every
    # check above this line has already been asserting.
    $twoA = Join-Path (Use-SuiteTemp) ("dodona-mA-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $twoB = Join-Path (Use-SuiteTemp) ("dodona-mB-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    foreach ($r in $twoA, $twoB) {
        New-Item -ItemType Directory -Force "$r\src" | Out-Null
        Set-Content "$r\src\main.cs" "// $r"
        git -C $r init -b main -q
        git -C $r add -A
        git -C $r -c user.email=t@t -c user.name=t commit -q -m init
    }
    DodonaBare @("workspace-create", "--name", "pair", "--member", $twoA, "--member", $twoB) | Out-Null
    $pair = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'pair' }).id
    StartDaemonFor $pair | Out-Null
    $pairRepos = DodonaBare @("repos", "--workspace", $pair)
    Check 'multi_member_repo_names_are_member_prefixed' `
        ($pairRepos -match [regex]::Escape((Split-Path -Leaf $twoA)) -and
         $pairRepos -match [regex]::Escape((Split-Path -Leaf $twoB)) -and
         $pairRepos -notmatch '(?m)^\s*\.\s') $pairRepos
    # A ticket in a multi-member workspace still lands in exactly one repo, and its
    # worktree still sits beside THAT member — not beside the other one, and not in
    # workspace territory (§1's exception).
    $pt = DodonaBare @("ticket-create", "--title", "PAIRED", "--claim", "subtree:$(Split-Path -Leaf $twoA)/src", "--workspace", $pair)
    Check 'multi_member_ticket_names_its_repo' ($pt -match [regex]::Escape((Split-Path -Leaf $twoA))) $pt
    Check 'multi_member_worktree_sits_beside_its_own_member' `
        ((Test-Path "$twoA\.dodona\wt\t1") -and -not (Test-Path "$twoB\.dodona\wt\t1")) $pt
    # ...and naming the repo explicitly works, when the claim really is in it (P0.6's other half)
    $ptNamed = DodonaBare @("ticket-create", "--title", "NAMED", "--repo", (Split-Path -Leaf $twoB),
                            "--claim", "path:$(Split-Path -Leaf $twoB)/src/main.cs", "--workspace", $pair)
    Check 'named_repo_accepts_its_own_claims' ($ptNamed -match [regex]::Escape((Split-Path -Leaf $twoB))) $ptNamed
    DodonaBare @("stop-daemon", "--workspace", $pair) | Out-Null

    # =====================================================================================
    # REPO IDENTITY: A TICKET'S REPOSITORY IS A PATH, NOT A NAME (P0.1/P0.2/P0.3)
    #
    # This was BROKEN IN PRODUCTION, with no changes from us, and it is the double
    # fast-forward this whole system exists to prevent:
    #
    #   Repos.Discover recomputes repo names on every call, and the rule CHANGES WITH
    #   PROJECT COUNT -- one project that is a repo is named "." (Repos.Under's empty
    #   prefix), and attaching a second project renames that same repository to its leaf.
    #   tickets.repo is written once and never updated. So after an attach the pre-existing
    #   ticket asked for merge token "." while a new ticket in the SAME repository asked for
    #   token "<leaf>": two rows over one main, two agents each told "granted", both able to
    #   fast-forward the same branch.
    #
    # A red check in this section is a correctness incident, not a flaky test.
    # =====================================================================================
    $driftA = Join-Path (Use-SuiteTemp) ("dodona-drA-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $driftB = Join-Path (Use-SuiteTemp) ("dodona-drB-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    foreach ($r in $driftA, $driftB) {
        New-Item -ItemType Directory -Force "$r\src\one", "$r\src\two" | Out-Null
        Set-Content "$r\src\one\a.cs" "// one"
        Set-Content "$r\src\two\b.cs" "// two"
        Set-Content "$r\.gitignore" ".dodona/"
        git -C $r init -b main -q
        git -C $r add -A
        git -C $r -c user.email=t@t -c user.name=t commit -q -m init
    }
    # ONE project, and it IS a repository: the degenerate case, named "."
    DodonaBare @("workspace-create", "--name", "drift", "--member", $driftA) | Out-Null
    $drift = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'drift' }).id
    $driftStore = (& $dodona where --workspace $drift --json | Out-String | ConvertFrom-Json).store
    StartDaemonFor $drift | Out-Null
    $dt1 = DodonaBare @("ticket-create", "--title", "DRIFT1", "--claim", "subtree:src/one", "--workspace", $drift)
    # (moved to unit:Dodona.Tests.RepoDiscoveryTests.a_lone_project_that_is_a_repo_is_still_named_dot -- S-IDENTITY, tests/ledger/moves/s-identity.tsv)

    # THE ATTACH. Nothing about the repository changed; only what discovery calls it.
    DodonaBare @("workspace-attach", "--member", $driftB, "--workspace", $drift) | Out-Null
    $leafA = Split-Path -Leaf $driftA
    $driftRepos = DodonaBare @("repos", "--workspace", $drift)
    Check 'attaching_a_second_project_renames_the_first_repository' `
        ($driftRepos -match [regex]::Escape($leafA) -and $driftRepos -notmatch '(?m)^\s*\.\s') $driftRepos

    # A second ticket in THE SAME REPOSITORY, created under its new name. Its claim does not
    # overlap ticket 1's, and must still be allowed: so both tickets are open in one repository,
    # which is exactly the state the merge token exists for.
    $dt2 = DodonaBare @("ticket-create", "--title", "DRIFT2", "--claim", "subtree:$leafA/src/two", "--workspace", $drift)
    Check 'second_ticket_lands_in_the_same_repository_under_its_new_name' ($dt2 -match 'ticket 2') $dt2

    # ---- THE DANGEROUS HALF OF THE RENAME (Phase 0b) ----
    # One directory, two spellings. Ticket 1 holds `subtree:src/one`, written while this
    # repository was still called "." (a lone project that IS a repo gets the empty claim
    # prefix); the attach renamed it, so the same directory is now spelled
    # `<leaf>/src/one`. Store.FindConflicts compared the RAW stored strings, which share no
    # prefix -- so the claim algebra saw two unrelated folders and granted a second ticket a
    # directory an open ticket already held. TWO AGENTS INTO ONE FOLDER, silently, with no
    # merge-token protection between them because the token only serialises the LAND.
    #
    # Nothing in the tree detected this before: the comment above used to say so out loud
    # ("after the rename it CANNOT be seen to ... that half is Phase 0b's problem"). Claims are
    # now reduced to REPO-RELATIVE terms and compared per repository path, so both spellings
    # come out as `src/one`.
    #
    # THE ORDER OF THESE THREE IS LOAD-BEARING and cost a `dev prove` round: under HEAD each of
    # them SUCCEEDS, so the first one leaves behind a claim in the new namespace that refuses
    # the next -- and the second check then went VACUOUS, passing against HEAD for the wrong
    # reason (refused by the ticket the defect had just created, not by ticket 1). So the
    # claim-extend case goes FIRST, and no two of them name paths that contain each other:
    # a.cs and b.cs are siblings, and src/three is elsewhere. Do not reorder or rename these
    # without re-proving all three.

    # The conflict search has TWO callers, and claim-extend is the one that reads the
    # repository off the ticket ROW (Store.TxRepoId) rather than being handed one, so it needs
    # its own check. Widening ticket 2 into the directory ticket 1 holds under the repository's
    # OLD name must be refused. Under HEAD this prints "extended ticket 2".
    # RE-AIMED from `claim_extend_cannot_widen_across_a_rename` (R3): the extension is permitted
    # now -- Store.ClaimExtend carries why that fourth refusal had to go with D-R5's three -- and
    # what this check is for is that the conflict SEARCH still resolves the old name to the same
    # repository. It would go red on a reduction that stopped seeing across the rename, which is
    # the bug it was written for.
    $dt3d = DodonaBare @("claim-extend", "2", "--claim", "path:$leafA/src/one/a.cs", "--workspace", $drift)
    Check 'the_extend_conflict_search_still_sees_across_a_rename' `
        ($DODONA_EXIT -eq 0 -and $dt3d -match 'overlap:' -and $dt3d -match 'ticket 1') $dt3d
    # ...and the same folder cannot be ticketed twice. A path INSIDE the subtree rather than an
    # identical spelling, deliberately: the reduction has to put a path and a subtree into ONE
    # namespace, not merely fold two equal strings together. Under HEAD this prints "ticket 3".
    $dt3 = DodonaBare @("ticket-create", "--title", "DRIFT3", "--claim", "path:$leafA/src/one/b.cs", "--workspace", $drift)
    # RE-AIMED from the refusal to the report (R3, D-R5). The reduction being tested is the same
    # one: a path INSIDE the subtree, not an identical spelling, so the two spellings of the
    # repository must land in ONE namespace before the algebra compares them.
    Check 'one_folder_under_two_names_is_still_one_claim' `
        ($DODONA_EXIT -eq 0 -and $dt3 -match 'overlap:' -and $dt3 -match 'ticket 1') $dt3
    # ...while a directory nobody holds is still free, so the scoping did not simply start
    # refusing everything in the repository. VACUOUS BY CONSTRUCTION -- HEAD permits this too --
    # and kept anyway for the same reason as `an_explicit_root_beats_the_inherited_env` above:
    # no code state makes it red, and it is what would catch a later "narrowing" that refuses
    # the whole repository instead of the claim. Extended rather than ticketed on purpose: this
    # fixture's migration section below reads tickets by id.
    $dt3c = DodonaBare @("claim-extend", "2", "--claim", "subtree:$leafA/src/three", "--workspace", $drift)
    Check 'a_disjoint_directory_in_the_renamed_repository_is_still_free' ($DODONA_EXIT -eq 0 -and $dt3c -match 'extended ticket 2') $dt3c
    DodonaBare @("approve", "1", "--workspace", $drift) | Out-Null
    DodonaBare @("approve", "2", "--workspace", $drift) | Out-Null
    $dg1 = DodonaBare @("token-request", "1", "--lease", "300", "--workspace", $drift)
    $dg2 = DodonaBare @("token-request", "2", "--lease", "300", "--workspace", $drift)
    # THE CHECK THIS SECTION EXISTS FOR. Under the defect both are "granted".
    Check 'one_repository_grants_one_merge_token_after_a_rename' `
        ($dg1 -match 'granted ticket 1' -and $dg2 -match 'queued ticket 2') "$dg1 | $dg2"
    # The same fact read off the store rather than off the CLI: ONE holder, because there is one
    # repository. Counted by holders rather than by rows on purpose -- `repos` materialises a
    # token row per repository, so a bare row count says nothing, while a second HOLDER is the
    # incident itself. Under the defect there are two.
    Check 'two_tickets_in_one_repository_cannot_both_hold_the_token' `
        ([int]((Invoke-StoreSql $driftStore "SELECT COUNT(*) FROM merge_token WHERE holder_ticket IS NOT NULL").Trim()) -eq 1) `
        (Invoke-StoreSql $driftStore "SELECT * FROM merge_token")

    # The drifted ticket must keep ENFORCING across a rename and a daemon restart.
    #
    # THIS USED TO COUNT `gate_redeployed` EVENTS, and that machinery is gone (WORK-ISOLATION-PLAN
    # D-17). The incident it guarded was real: `Repos.ByName(repos, '.')` returned null once the
    # rename happened, reconcile's answer to null was `continue`, so layer 1 silently stopped being
    # refreshed, GcOldBuilds deleted the exe the stale gate invoked, and the gate failed OPEN.
    #
    # Redeployment cannot be the thing that prevents that, because hooks are read once at session
    # start and never re-read (measured 2026-08-20) -- so rewriting the file never reached the live
    # agent it was protecting. The gate now names only the LANE, and the daemon resolves the ticket,
    # its repository and its claims fresh on every write -- so a rename has nothing to desync,
    # because no deployed file caches a repository name any more. What must be asserted is
    # therefore the OUTCOME rather than the maintenance, which is also the stronger claim.
    RestartDaemonFor $drift | Out-Null
    $dDenied = DodonaBare @("claim-check", "1", "$driftA\.dodona\wt\t1\src\three\x.cs", "--workspace", $drift)
    Check 'a_drifted_ticket_still_refuses_a_path_it_does_not_own' `
        ($DODONA_EXIT -ne 0 -and $dDenied -match 'denied') $dDenied
    # and its claims still mean what they meant when they were written: the ticket keeps the
    # name it was born with, so its claim prefix does not move underneath it
    $dCovered = DodonaBare @("claim-check", "1", "$driftA\.dodona\wt\t1\src\one\a.cs", "--workspace", $drift)
    Check 'a_drifted_tickets_claims_still_cover_its_own_files' ($DODONA_EXIT -eq 0 -and $dCovered -match 'covered:') $dCovered

    # ---- the migration itself (P0.3), run on a store that predates it ----
    # A migration can only be tested against a store that predates it, and a suite always builds
    # the newest schema -- so this stands the store back UP in the v8 shape (merge token keyed by
    # NAME, no repo_path anywhere) and then gives it the two rows the defect produced in the
    # field: "<leaf>" from after the attach, and "." from before it. Two names, one repository,
    # two tokens over one main. Then the daemon starts and repairs them.
    #
    # The revert is conditional because this same suite must also run against a build that has no
    # v9 (that is what `dev prove` does): there the store is already v8 and DROP COLUMN repo_path
    # would fail, killing the suite instead of failing the checks.
    StopDaemonFor $drift
    # v10's column has to go too, and finding out why cost a debugging round worth recording.
    # Migrate() runs one `if (v < N)` block per version and each is a single statement batch, so a
    # `PRAGMA user_version = 8` that leaves a LATER version's column in place makes that version's
    # `ALTER TABLE ... ADD COLUMN` fail with "duplicate column" -- inside the Store CONSTRUCTOR.
    # The daemon then dies before it opens its control pipe, StartDaemonFor times out, and all
    # FOUR checks below go red pointing at repo identity, which is not what broke. A fixture that
    # claims to build a v8 store must actually build one.
    #
    # Keyed on the COLUMN, not on the version number, so this survives v11: `dev prove` runs this
    # same suite against a build that has no v10 at all, where the column is legitimately absent.
    if ([int]((Invoke-StoreSql $driftStore "SELECT COUNT(*) FROM pragma_table_info('lanes') WHERE name='project'").Trim()) -ge 1) {
        Invoke-StoreExec $driftStore "ALTER TABLE lanes DROP COLUMN project;"
    }
    if ([int]((Invoke-StoreSql $driftStore "PRAGMA user_version").Trim()) -ge 9) {
        Invoke-StoreExec $driftStore @"
ALTER TABLE tickets DROP COLUMN repo_path;
ALTER TABLE token_queue DROP COLUMN repo_path;
CREATE TABLE merge_token_v8(
    repo TEXT PRIMARY KEY, holder_ticket INTEGER, generation INTEGER NOT NULL DEFAULT 0,
    granted_ts TEXT, expires_ts TEXT, main_sha TEXT);
INSERT INTO merge_token_v8(repo, holder_ticket, generation, granted_ts, expires_ts, main_sha)
    SELECT repo, holder_ticket, generation, granted_ts, expires_ts, main_sha FROM merge_token;
DROP TABLE merge_token;
ALTER TABLE merge_token_v8 RENAME TO merge_token;
PRAGMA user_version = 8;
"@
    }
    # The pre-attach row, with a HIGHER generation than the live one: the fencing counter must
    # not go backwards across a merge, or a stale generation re-authorises a dead grant.
    #
    # OR IGNORE, and the reason is the defect itself: against a build that keys the token on the
    # display NAME, this row already exists -- ticket 1 still says "." so its token-request made
    # one, right beside the "<leaf>" row ticket 2 made for the same repository. Two rows, two
    # holders, one main. Inserting unconditionally would abort the suite on a UNIQUE violation
    # instead of letting the checks below report it.
    Invoke-StoreExec $driftStore "INSERT OR IGNORE INTO merge_token(repo, generation) VALUES ('.', 4);"
    StartDaemonFor $drift | Out-Null
    # A query naming a column this build does not have must FAIL THE CHECK, not kill the suite --
    # everything below here would otherwise never run against an older build, and `dev prove`
    # would report MISSING (unproven) where the honest answer is red. Empty is never a pass: the
    # comparisons below all reject it.
    function DriftSql([string]$sql) { try { Invoke-StoreSql $driftStore $sql } catch { '' } }
    Check 'a_pre_v9_store_is_copied_before_it_is_migrated' `
        ((Test-Path "$driftStore.pre-v8") -and
         (DriftSql "SELECT detail FROM events WHERE kind = 'store_backed_up'") -match 'pre-v8') `
        (DriftSql "SELECT kind, detail FROM events WHERE kind LIKE 'store_back%'")
    Check 'a_pre_v9_ticket_is_stamped_with_its_repository_path' `
        ((DriftSql "SELECT repo_path FROM tickets WHERE id = 1").Trim().Length -gt 0) `
        (DriftSql "SELECT id, repo FROM tickets")
    # THE REPAIR: two names, one repository, one row -- and it says so out loud, because an
    # operator whose store held two tokens over one main needs to know that happened.
    Check 'two_token_rows_over_one_repository_are_merged_and_announced' `
        ([int]((DriftSql "SELECT COUNT(*) FROM merge_token").Trim()) -eq 2 -and
         [int]((DriftSql "SELECT COUNT(*) FROM events WHERE kind = 'merge_token_merged'").Trim()) -ge 1) `
        (DriftSql "SELECT repo, holder_ticket, generation FROM merge_token")
    Check 'the_merged_token_keeps_the_highest_generation' `
        ([int]((DriftSql "SELECT generation FROM merge_token WHERE holder_ticket = 1").Trim()) -ge 4) `
        (DriftSql "SELECT repo, holder_ticket, generation FROM merge_token")
    StopDaemonFor $drift

    # ---- a RECYCLED repo name cannot inherit another repository's ticket (P0.1) ----
    # `leaf~2` is handed out by position: two projects with the same folder leaf make the second
    # one `leaf~2`. Detach it and attach a DIFFERENT project with the same leaf, and the name
    # `leaf~2` now points at a repository the ticket has never been in -- so the old ticket's
    # token, its claim gate and its LAND all silently moved to a stranger's `main`.
    $twinRoot = Join-Path (Use-SuiteTemp) ("dodona-twins-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $twin1 = "$twinRoot\p1\twin"; $twin2 = "$twinRoot\p2\twin"; $twin3 = "$twinRoot\p3\twin"
    foreach ($r in $twin1, $twin2, $twin3) {
        New-Item -ItemType Directory -Force "$r\src" | Out-Null
        Set-Content "$r\src\main.cs" "// $r"
        Set-Content "$r\.gitignore" ".dodona/"
        git -C $r init -b main -q
        git -C $r add -A
        git -C $r -c user.email=t@t -c user.name=t commit -q -m init
    }
    DodonaBare @("workspace-create", "--name", "recycle", "--member", $twin1, "--member", $twin2) | Out-Null
    $recycle = ((DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.name -eq 'recycle' }).id
    StartDaemonFor $recycle | Out-Null
    $rt1 = DodonaBare @("ticket-create", "--title", "RECYCLED", "--claim", "subtree:twin~2/src", "--workspace", $recycle)
    Check 'a_colliding_leaf_gets_the_tilde_name' ($rt1 -match 'repo twin~2') $rt1
    DodonaBare @("workspace-detach", "--member", $twin2, "--workspace", $recycle) | Out-Null
    DodonaBare @("workspace-attach", "--member", $twin3, "--workspace", $recycle) | Out-Null
    $recRepos = DodonaBare @("repos", "--workspace", $recycle)
    Check 'the_tilde_name_is_recycled_onto_the_new_project' `
        ($recRepos -match 'twin~2' -and $recRepos -match [regex]::Escape($twin3)) $recRepos
    DodonaBare @("approve", "1", "--workspace", $recycle) | Out-Null
    $recTok = DodonaBare @("token-request", "1", "--lease", "300", "--workspace", $recycle)
    # Under the defect this is "granted" -- for a repository the ticket was never in, whose main
    # the land would then have tried to fast-forward with a branch from a different history.
    Check 'a_recycled_repo_name_cannot_inherit_another_repos_ticket' `
        ($DODONA_EXIT -ne 0 -and $recTok -match 'no longer in this workspace') $recTok
    StopDaemonFor $recycle
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

    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    foreach ($p in @($extraDaemons)) { if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch { } } }
    # Scoped cleanup: only THIS test's processes, resolved from its own shim-info
    # files. Killing by process NAME once murdered the operator's live session's shim
    # and UI mid-dogfood (17: tests collide with nothing -- including the instance the
    # operator is using right now).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- WORKSPACE ACCEPTANCE (multi-repo, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
