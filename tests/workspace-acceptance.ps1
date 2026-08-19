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

    # ---- discovery: the workspace knows its repositories ----
    $repos = Dodona @("repos")
    Check 'discovers_repos' ($repos -match 'engine' -and $repos -match 'tools' -and $repos -notmatch 'docs') $repos

    # ---- a ticket infers its repository from its claim paths ----
    $t1 = Dodona @("ticket-create", "--title", "ENGINE", "--claim", "subtree:engine/src")
    Check 'infers_repo_from_claims' ($t1 -match 'ticket 1 repo engine') $t1
    $t2 = Dodona @("ticket-create", "--title", "TOOLS", "--claim", "subtree:tools/src")
    Check 'second_repo_ticket' ($t2 -match 'ticket 2 repo tools') $t2

    # the worktree is a worktree OF THAT REPO, branched from its own main
    $wt1 = "$root\.dodona\wt\t1"
    $wt2 = "$root\.dodona\wt\t2"
    $origin1 = (git -C $wt1 rev-parse --show-toplevel)
    Check 'worktree_belongs_to_its_repo' ((Test-Path "$wt1\src\main.cs") -and (Get-Content "$wt1\src\main.cs") -eq '// engine') "$origin1"
    Check 'second_worktree_is_other_repo' ((Get-Content "$wt2\src\main.cs") -eq '// tools') ''

    # ---- a ticket spanning repositories is refused, with the reason ----
    $span = Dodona @("ticket-create", "--title", "SPAN", "--claim", "path:engine/src/a.cs", "--claim", "path:tools/src/b.cs")
    Check 'cross_repo_ticket_refused' ($DODONA_EXIT -ne 0 -and $span -match 'span 2 repositories' -and $span -match 'cannot be atomic') $span

    # ---- a claim in no repository is refused, and says what there is ----
    $homeless = Dodona @("ticket-create", "--title", "DOCS", "--claim", "path:docs/notes.md")
    Check 'claim_outside_any_repo_refused' ($DODONA_EXIT -ne 0 -and $homeless -match 'no repository covers' -and $homeless -match 'engine') $homeless

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
    Check 'named_repo_still_validates_its_claims' `
        ($DODONA_EXIT -ne 0 -and $wrongRepo -match 'not in repository tools' -and $wrongRepo -match 'engine') $wrongRepo
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

    # ---- the merge-time backstop compares in workspace terms ----
    $t4 = Dodona @("ticket-create", "--title", "SNEAK", "--claim", "path:engine/src/main.cs")
    $wt4 = "$root\.dodona\wt\t4"
    Set-Content "$wt4\src\main.cs" "// engine v3"
    New-Item -ItemType Directory -Force "$wt4\other" | Out-Null
    Set-Content "$wt4\other\sneaky.cs" "// out of claim"
    Commit $wt4 "claimed + sneaky"
    Dodona @("approve", "4") | Out-Null
    $back = Dodona @("token-request", "4")
    Check 'backstop_uses_workspace_paths' ($DODONA_EXIT -eq 1 -and $back -match 'engine/other/sneaky.cs') $back

    # ---- lanes are workspace-wide: an agent can run with no repository involved ----
    $ls = Dodona @("lane-start", "--title", "DOCS", "--child", $fake)
    if ($ls -match 'lane (\d+)') { $lane = $Matches[1] } else { throw "lane-start failed: $ls" }
    Dodona @("say", "$lane", "say lanes span the workspace") | Out-Null
    Wait-Until { (Dodona @("tail", "$lane", "10")) -match 'lanes span the workspace' } 20000 'the lane answers' | Out-Null
    Check 'lanes_are_workspace_wide' ((Dodona @("tail", "$lane", "5")) -match 'lanes span the workspace') ''

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
    Check 'identity_is_a_slug_not_a_path_hash' `
        ($ws.Id -match '^[a-z0-9-]+-[0-9a-f]{4}$' -and $ws.Id -match 'dodona-ws') "id=$($ws.Id)"
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
    Check 'repo_in_two_workspaces_refused' ($DODONA_EXIT -ne 0 -and $stealFlat -match 'already belongs to workspace') $stealFlat
    Check 'refusal_says_why_two_tokens_is_the_problem' ($steal -match 'two merge tokens over one main') $steal
    Check 'refusal_offers_the_move_affordance' ($steal -match 'dodona workspace-move --member') $steal

    # A BARE FOLDER is exempt and must stay exempt: there is no merge token to split, and
    # a shared notes folder in two workspaces harms nobody.
    $b1 = DodonaBare @("workspace-attach", "--member", $shared, "--workspace", "rival")
    $b2 = DodonaBare @("workspace-attach", "--member", $shared, "--workspace", $soloWs.Id)
    Check 'bare_folder_may_be_shared' ($b1 -notmatch 'error' -and $b2 -notmatch 'error') "$b1 | $b2"

    # Reassignment is legitimate — that is what the refusal points at — and it is atomic:
    # the repo is never in two workspaces and never in none.
    $moved = DodonaBare @("workspace-move", "--member", $solo, "--workspace", "rival")
    $wsList = DodonaBare @("workspaces", "--json") | ConvertFrom-Json
    $ownersOfSolo = @($wsList | Where-Object { $_.members.path -contains (Resolve-Path $solo).Path })
    Check 'move_reassigns_the_repo' ($moved -match 'moved' -and $ownersOfSolo.Count -eq 1 -and $ownersOfSolo[0].name -eq 'rival') `
        "owners=$($ownersOfSolo.Count) $($ownersOfSolo.name -join ',')"

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
    # THIS SITS DELIBERATELY BESIDE THE MIGRATION SET ABOVE, which must keep passing: an
    # EXPLICIT `--root` still creates (that is what Get-WorkspacePaths passes, and it is the
    # whole invisible-migration mechanism). The distinction being asserted here is provenance,
    # not the path -- the same folder either creates or refuses depending on whether anybody
    # asked for it.
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

    # ---- creating a workspace cannot quietly take an owned repo with it ----
    DodonaBare @("workspace-create", "--name", "twin", "--member", $solo) | Out-Null
    # the workspace IS created; the attach inside it is what gets refused
    Check 'creating_a_workspace_cannot_steal_an_owned_repo' ($DODONA_EXIT -ne 0) ''
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
    Check 'a_lone_project_that_is_a_repo_is_still_named_dot' `
        ((Invoke-StoreSql $driftStore "SELECT repo FROM tickets WHERE id = 1").Trim() -eq '.') $dt1

    # THE ATTACH. Nothing about the repository changed; only what discovery calls it.
    DodonaBare @("workspace-attach", "--member", $driftB, "--workspace", $drift) | Out-Null
    $leafA = Split-Path -Leaf $driftA
    $driftRepos = DodonaBare @("repos", "--workspace", $drift)
    Check 'attaching_a_second_project_renames_the_first_repository' `
        ($driftRepos -match [regex]::Escape($leafA) -and $driftRepos -notmatch '(?m)^\s*\.\s') $driftRepos

    # A second ticket in THE SAME REPOSITORY, created under its new name. Its claim does not
    # overlap ticket 1's -- and after the rename it CANNOT be seen to, because "src/one" and
    # "<leaf>/src/two" no longer share a prefix (that half is Phase 0b's problem). So both
    # tickets are open in one repository, which is exactly the state the merge token exists for.
    $dt2 = DodonaBare @("ticket-create", "--title", "DRIFT2", "--claim", "subtree:$leafA/src/two", "--workspace", $drift)
    Check 'second_ticket_lands_in_the_same_repository_under_its_new_name' ($dt2 -match 'ticket 2') $dt2
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

    # The gate must keep being redeployed for the drifted ticket. `Repos.ByName(repos, '.')`
    # returned null once the rename happened, and reconcile's answer to null was `continue` --
    # so enforcement layer 1 silently stopped being refreshed, GcOldBuilds deleted the exe the
    # stale gate invoked, and the gate then failed OPEN. Found live 2026-08-18 for a different
    # reason; the rename is a second route into it.
    RestartDaemonFor $drift | Out-Null
    Check 'a_drifted_ticket_keeps_its_claim_gate_redeployed' `
        ([int]((Invoke-StoreSql $driftStore "SELECT COUNT(*) FROM events WHERE kind = 'gate_redeploy_failed'").Trim()) -eq 0 -and
         [int]((Invoke-StoreSql $driftStore "SELECT COUNT(*) FROM events WHERE kind = 'gate_redeployed'").Trim()) -ge 1) `
        (Invoke-StoreSql $driftStore "SELECT kind, detail FROM events WHERE kind LIKE 'gate_redeploy%'")
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
    Stop-WorkspaceShims $tp.Dir
    DodonaBare @("stop-daemon", "--workspace", $tp.Id) | Out-Null

    # ---- forget removes the registry rows and keeps every transcript (§12) ----
    $forgotten = DodonaBare @("workspace-forget", "--workspace", $twin)
    Check 'forget_removes_the_registry_row' `
        ($forgotten -match 'forgotten' -and -not (@(DodonaBare @("workspaces", "--json") | ConvertFrom-Json) | Where-Object { $_.id -eq $twin })) $forgotten
    Check 'forget_keeps_the_store_directory' (Test-Path (Join-Path $dodonaHome "workspaces\$twin")) ''

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
