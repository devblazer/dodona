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
    # THREE-LINE WRAPPERS OVER `tests\_workspace.ps1`, which is where the bodies and their reasons
    # live now (the ctl-pipe/mutex race in particular). They moved when this suite was split
    # 2026-08-22 and `projects-acceptance.ps1` needed the same three; wrapping rather than
    # re-parameterising is what let ~80 call sites here stay exactly as they were.
    function DodonaBare([string[]]$a) { Invoke-DodonaBare $dodona $errFile $a }
    function StartDaemonFor([string]$wsId) { Start-WorkspaceDaemon $dodona $wsId $out $extraDaemons }
    function StopDaemonFor([string]$wsId) { Stop-WorkspaceDaemon $dodona $errFile $wsId }
    function RestartDaemonFor([string]$wsId) { StopDaemonFor $wsId; StartDaemonFor $wsId }

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
