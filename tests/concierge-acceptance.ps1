# CONCIERGE acceptance: the machine-global group-scope ladder
# (docs/WORKSPACES-CONCIERGE.md §2 and §4).
#
# One concierge per machine. It answers exactly one question -- WHICH WORKSPACE -- and the
# whole point of this suite is that it answers it at the cheapest rung that can, escalates
# in order, and never quietly guesses when it does not know.
#
# The ladder, and what each check here is guarding:
#   rung 0  an explicit path in the prompt      code, no model  (explicit info never searches)
#   rung 1  exact name or alias                 code, no model  (the steady state; free)
#   rung 1b only one workspace exists           code, no model  (no group question to answer)
#   rung 2  fuzzy                               cheap tier
#   rung 3  ask the operator, and TEACH         a row + a feed line; the answer becomes an alias
#   -----   bounded discovery inside the fence  expensive tier, ONE capability -- BELOW the ask
#           now (D-L3, operator 2026-08-19): never automatic, reached only by answering an open
#           question with `look`. Fence.cs is unchanged and was deliberately not deleted; what
#           moved is who starts it. Going looking is occasionally the right answer and never
#           the right default.
#
# Plus the review-behind net (§2.3), which exists because the per-workspace brain
# structurally CANNOT catch a wrong-workspace delivery: it does not know other workspaces
# exist (§14).
#
# Model-free. Both tiers are the fake agent via the concierge's own config, driven by
# directives in the text (cxws:/cxguess:/cxlow/cxfolder:/cxdisagree:), and rung 3 is
# deterministic because the fence is a fixture directory tree.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated registry AND isolated concierge: this suite starts a real concierge, and a
# concierge is machine-global. Without DODONA_HOME it would be THE operator's concierge,
# resolving their sentences with a fake agent (§17: tests collide with nothing).
$dodonaHome = Use-IsolatedDodonaHome 'cx'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"      # this suite owns concierge and daemon lifetime
$out = Join-Path $PSScriptRoot 'concierge-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# ---- fixtures -----------------------------------------------------------------------
# A fence with something in it. `bay` is the folder rung 3 has to find: it is INSIDE the
# fence (a sibling of a registered member) and belongs to no workspace.
$fenceRoot = Join-Path (Use-SuiteTemp) ("dodona-fence-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
foreach ($n in 'harbour', 'lighthouse', 'bay') {
    New-Item -ItemType Directory -Force "$fenceRoot\$n\src" | Out-Null
    Set-Content "$fenceRoot\$n\src\main.cs" "// $n"
    git -C "$fenceRoot\$n" init -b main -q
    git -C "$fenceRoot\$n" add -A
    git -C "$fenceRoot\$n" -c user.email=t@t -c user.name=t commit -q -m init
}
# Outside the fence entirely: the fence must never reach it, and nothing widens itself (§8).
$outside = Join-Path (Use-SuiteTemp) ("dodona-outside-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$outside\atlantis" | Out-Null

# The concierge's own config, beside its own store -- it belongs to no workspace, so no
# dodona.json can serve it (§2). The fake agent stands in for both tiers.
New-Item -ItemType Directory -Force (Join-Path $dodonaHome 'concierge') | Out-Null
@{ agent = $fake; loModel = 'fake'; loEffort = ''; hiModel = 'fake'; hiEffort = '' } |
    ConvertTo-Json | Set-Content (Join-Path $dodonaHome 'concierge\concierge.json') -Encoding utf8

$results = [ordered]@{}
$errFile = Join-Path $out 'stderr.tmp'
function Dx([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    Remove-Item $errFile -ErrorAction SilentlyContinue
    $o = (& $dodona $a 2> $errFile) | Out-String
    $global:DODONA_EXIT = $LASTEXITCODE
    $e = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
    ("$o`n$e").Trim()
}
# The ladder, walked READ-ONLY. Rung 0 attaches an unowned path found in the sentence, and
# issue #12 made that an opt-in: this verb is documented as printing a verdict as JSON, and a
# query must not be how a folder gets adopted. Resolve-Adopt is the form that means it, and
# the rung-0 checks below use it deliberately -- through `concierge-resolve` rather than
# `route`, because route also wakes a daemon and delivers the sentence, which is a different
# thing to be testing.
function Resolve-Text([string]$text) { (Dx @('concierge-resolve', $text)) | ConvertFrom-Json }
function Resolve-Adopt([string]$text) { (Dx @('concierge-resolve', $text, '--adopt')) | ConvertFrom-Json }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }

$cx = $null
try {
    # ---- rung 1b: ONE workspace, so there is no group-scope question at all -------------
    Dx @('workspace-create', '--name', 'harbour', '--member', "$fenceRoot\harbour") | Out-Null

    $cx = Start-Process $dodona -ArgumentList "concierge" -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\concierge.out" -RedirectStandardError "$out\concierge.err"
    Start-Sleep -Milliseconds 900

    $st = Dx @('concierge-status')
    Check 'concierge_answers_its_pipe' ($st -match 'concierge pid=') $st
    Check 'concierge_store_is_its_own' ($st -match 'concierge\\store.db') $st

    # ---- THE REGISTRY LIVES UNDER DODONA_HOME (LOCATIONS-PLAN P1.4) ----------------------
    # `registry.db` is the ONE machine-wide table in the product: every workspace name, id,
    # alias and project row. Nothing in the tree asserted where it is. This suite already
    # COPIES it out of $dodonaHome in its `finally` and asserts nothing about its location, so a
    # future change that reached for %LOCALAPPDATA% directly -- the way Ver.BinRoot legitimately
    # does one file over -- would pass all twelve suites while writing into the OPERATOR'S REAL
    # REGISTRY. A suite that tests the repo-exclusivity REFUSAL could then refuse one of their
    # real repos (CLAUDE.md 5, and it is the reason DODONA_HOME exists at all).
    #
    # Three parts, because the first two alone would be satisfied by a decoy file:
    #   1. the path the BINARY reports is under $env:DODONA_HOME,
    #   2. a file is really there,
    #   3. and it is the LIVE one -- it holds the workspace this suite just created. Part 3 is
    #      what a stray %LOCALAPPDATA% write cannot fake: the file under DODONA_HOME would exist
    #      and be empty.
    #
    # A REGRESSION check, not a defect check, and `dev prove` says VACUOUS for it by design --
    # Paths.Registry derives from Paths.Home today, so nothing is broken. It was demonstrated
    # red by making Paths.Registry reach for LocalApplicationData directly; see the commit.
    $regPath = (Dx @('where', '--workspace', 'harbour', '--json') | ConvertFrom-Json).registry
    $homePrefix = $env:DODONA_HOME.TrimEnd('\')
    Check 'registry_is_reported_under_dodona_home' `
        ($regPath -and $regPath.StartsWith($homePrefix + '\', [StringComparison]::OrdinalIgnoreCase)) `
        "registry='$regPath' DODONA_HOME='$homePrefix'"
    Check 'registry_file_exists_where_it_is_reported' ($regPath -and (Test-Path $regPath)) "registry='$regPath'"
    $regWs = if ($regPath -and (Test-Path $regPath)) { Invoke-StoreSql $regPath "SELECT name FROM workspaces ORDER BY name" } else { '' }
    Check 'the_registry_under_dodona_home_is_the_live_one' ($regWs -match 'harbour') "workspaces=$(($regWs -replace '\s+', ' ').Trim())"


    $r = Resolve-Text "make the pier longer"
    Check 'sole_workspace_needs_no_model' ($r.rung -eq 'only' -and $r.name -eq 'harbour') ($r | ConvertTo-Json -Compress)

    # ---- rung 1: exact name, in code. THE steady state, and it must cost nothing ---------
    Dx @('workspace-create', '--name', 'lighthouse', '--member', "$fenceRoot\lighthouse") | Out-Null
    $r = Resolve-Text "on lighthouse, repaint the lamp housing"
    Check 'exact_name_resolves_in_code' ($r.rung -eq 'registry' -and $r.name -eq 'lighthouse') ($r | ConvertTo-Json -Compress)
    Check 'exact_name_is_explicit_confidence' ($r.confidence -eq 'explicit') $r.confidence

    # A name must not match inside a longer word: "network" is not "work".
    Dx @('workspace-create', '--name', 'work') | Out-Null
    $r = Resolve-Text "trace the network timeouts cxws:lighthouse"
    Check 'name_does_not_match_inside_a_word' ($r.name -ne 'work') ($r | ConvertTo-Json -Compress)

    # ---- ASKING WHAT THE LADDER WOULD SAY ADOPTS NOTHING (issue #12) --------------------
    # THE INCIDENT (2026-08-21, commit 2ef0c54): a session asked whether Dodona was running
    # anything in the operator's other project and answered it with a command documented as an
    # observation. That folder became a workspace. `concierge-resolve` is the same shape one
    # verb along -- DEBUGGING.md sells it as "walk the ladder and print the verdict as JSON" --
    # and rung 0 attaches any existing absolute path it finds in the sentence.
    #
    # COUNT THE WORKSPACES, not just the verdict: a rung-0 that attached and then reported
    # created=false would print exactly what a refusal prints.
    $peekDir = Join-Path $fenceRoot 'peek'
    New-Item -ItemType Directory -Force $peekDir | Out-Null
    # ConvertFrom-Json emits a JSON ARRAY as ONE pipeline item (CLAUDE.md 0.2), so this must
    # land in a variable before being counted -- @(cmd | ConvertFrom-Json).Count is always 1.
    function CxWsCount { $all = Dx @('workspaces', '--json') | ConvertFrom-Json; @($all).Count }
    $peekBefore = CxWsCount
    $peek = Resolve-Text "clean up the tests in $peekDir"
    Check 'resolving_a_path_without_adopt_creates_no_workspace' `
        ((CxWsCount) -eq $peekBefore -and $peek.created -ne $true) `
        "before=$peekBefore after=$(CxWsCount) :: $($peek | ConvertTo-Json -Compress)"
    # A refusal that leaves you with no next step is a new way to be stuck (CLAUDE.md 0.1).
    Check 'resolving_a_path_without_adopt_says_what_would_happen' `
        (("$($peek.note)" -replace '\s+', ' ') -match 'would adopt') "note=$($peek.note)"

    # ---- rung 0: an explicit path short-circuits everything -----------------------------
    # Explicit information NEVER triggers a search (§4): the operator said where.
    $r = Resolve-Adopt "clean up the tests in $fenceRoot\bay"
    Check 'explicit_path_attaches_outright' ($r.rung -eq 'path' -and $r.created -eq $true) ($r | ConvertTo-Json -Compress)
    Check 'explicit_path_is_announced_with_undo' ((Dx @('concierge-feed')) -match 'workspace-forget') ''
    # ...and a second mention of the same path resolves to what it made, without creating again.
    $r2 = Resolve-Adopt "and $fenceRoot\bay needs a readme"
    Check 'explicit_path_reuses_its_workspace' ($r2.rung -eq 'path' -and $r2.created -eq $false -and $r2.workspace -eq $r.workspace) ($r2 | ConvertTo-Json -Compress)
    Dx @('workspace-forget', '--workspace', $r.workspace) | Out-Null

    # ---- rung 2: fuzzy, on the cheap tier -----------------------------------------------
    # cxpick:N deliberately, not cxws:NAME. Writing "cxws:lighthouse" would put a workspace
    # NAME into the sentence, rung 1 matches names in the sentence in code, and the check
    # would pass at rung 1 having never reached the tier -- a test proving the opposite of
    # what it claims. (Found by this exact check failing with rung=registry.)
    # Land ConvertFrom-Json in a variable before filtering: in PS 5.1 it emits a JSON array
    # as ONE pipeline item, so `| Where-Object` in the same pipeline filters the array itself
    # and `.name -eq 'x'` on an array returns matching ELEMENTS (truthy) — every row passes.
    # This silently produced "harbour-7667 lighthouse-2153" as one id and made the
    # review-behind checks no-ops. Now in CLAUDE.md 0.2.
    $wsAll = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    $names = @($wsAll).name
    $r = Resolve-Text "repaint the lamp housing on the tall thing by the sea cxpick:2"
    Check 'fuzzy_match_on_the_cheap_tier' ($r.rung -eq 'fuzzy' -and $r.name -eq $names[1]) "$($r | ConvertTo-Json -Compress) names=$($names -join ',')"
    Check 'fuzzy_match_is_announced' ((Dx @('concierge-feed')) -match 'fuzzy match') ''

    # A cheap tier that says LOW must not be acted on -- that is the whole point of asking
    # it to state its confidence honestly, and of the operator's rule that low escalates.
    $r = Resolve-Text "do the unnameable thing cxpick:2 cxlow"
    Check 'low_confidence_does_not_act' ($r.rung -ne 'fuzzy') ($r | ConvertTo-Json -Compress)

    # ---- rung 3 IS NO LONGER A RUNG: the discovery fence is DEMOTED BELOW THE ASK -------
    # D-L3 (operator, 2026-08-19). It used to run automatically on the way to a question we
    # were going to ask anyway: a directory walk plus an EXPENSIVE-tier call, spent to avoid
    # asking -- when asking is the cheapest correct rung there is. And searching unbidden is
    # the wrong default: occasionally going to look is exactly right, which is why `Fence.cs`
    # stays and was NOT deleted, but it has to be something the operator asks for.
    #
    # So it is an affordance now: rung 4 asks, the question offers `look`, and `look` runs the
    # fence. `bay` is still the folder it has to find -- inside the fence, owned by nobody.
    $cxStore = Join-Path $dodonaHome 'concierge\store.db'
    function CxDiscoveryEvents() { [int](Invoke-StoreSql $cxStore "SELECT COUNT(*) FROM events WHERE kind LIKE 'discovery%'").Trim() }
    $st = Dx @('concierge-status')
    Check 'fence_is_derived_from_member_parents' ($st -match [regex]::Escape($fenceRoot)) $st

    # THE DEMOTION ITSELF, and the whole decision rests on this check: an unresolvable sentence
    # must reach `ask` WITHOUT the fence having run. `discovery_miss` and
    # `discovery_attach_failed` are written from inside the fence path and nowhere else, so a
    # fence still running on its own would show up here as an extra row -- which is what makes
    # this an assertion rather than a hope.
    $missBefore = CxDiscoveryEvents
    $r = Resolve-Text "start on the bay wall cxguess:bay cxfolder:bay"
    Check 'the_fence_never_runs_unbidden' `
        ($r.rung -eq 'ask' -and (CxDiscoveryEvents) -eq $missBefore) `
        "$($r | ConvertTo-Json -Compress) discovery_events=$missBefore->$(CxDiscoveryEvents)"
    Check 'the_ask_offers_going_to_look' ((Dx @('concierge-feed')) -match 'new:NAME\|look') (Dx @('concierge-feed'))

    # ...and answering `look` runs it, finds `bay`, and attaches it. Same fence, same single
    # capability, same rung name in the resolutions table -- only the trigger moved.
    # The BEFORE reading is load-bearing, not bookkeeping: written as "the question is gone
    # afterwards" this check was VACUOUS -- against the old build the sentence resolved at
    # `discovery` and no question was ever opened, so "not in the open list" passed having
    # tested nothing (dev prove said so; .claude/skills/check-authoring 1).
    $qOpenBefore = Dx @('concierge-questions')
    $look = Dx @('concierge-answer', "$($r.question)", 'look')
    Check 'looking_on_request_finds_a_folder_in_the_fence' `
        (($look -match 'looked: found') -and ($look -match 'bay')) $look
    Check 'discovery_is_announced_with_undo' ((Dx @('concierge-feed')) -match 'inside the search fence') ''
    $wsAll = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    $bayRow = @($wsAll) | Where-Object { "$($_.members.path)" -match 'bay' } | Select-Object -First 1
    Check 'looking_attaches_what_it_found' ($null -ne $bayRow) "workspaces=$(@($wsAll).name -join ',')"
    Check 'looking_closes_the_question_it_answered' `
        (($qOpenBefore -match 'bay wall') -and ((Dx @('concierge-questions')) -notmatch 'bay wall')) `
        "before='$($qOpenBefore -replace '\s+', ' ')' after='$((Dx @('concierge-questions')) -replace '\s+', ' ')'"
    if ($bayRow) { Dx @('workspace-forget', '--workspace', $bayRow.id) | Out-Null }

    # THE FENCE NEVER WIDENS ITSELF (the design's rejection list). `atlantis` exists, is a
    # perfectly good folder, and is outside every member's parent -- so the expensive tier is
    # never even offered it.
    #
    # THIS CHECK USED TO ASSERT ONLY `rung -eq 'ask'`, AND WITH THE FENCE DEMOTED THAT WOULD
    # HAVE GONE GREEN HAVING TESTED NOTHING: every unresolved sentence reaches `ask` now, fence
    # or no fence (LOCATIONS-PLAN Phase 3 names this exact trap by line number). So it asks,
    # then explicitly LOOKS, and demands the look came back empty and the question stayed open.
    # That is a strictly stronger assertion than the old one, because it proves the fence
    # really ran and still did not reach outside itself.
    # "work on atlantis" would match the workspace named `work` at rung 1 and prove nothing.
    $r = Resolve-Text "dig out the sunken city cxguess:atlantis cxfolder:atlantis"
    Check 'an_unresolvable_sentence_still_asks' ($r.rung -eq 'ask') ($r | ConvertTo-Json -Compress)
    $lookOut = Dx @('concierge-answer', "$($r.question)", 'look')
    Check 'fence_never_reaches_outside_itself' `
        (($lookOut -match 'nothing inside the search fence matched') -and ($lookOut -notmatch 'atlantis')) $lookOut
    # A MISS LEAVES THE QUESTION OPEN, and that is the point of an affordance: the operator
    # asked us to look, we looked, we found nothing, and the next move is still theirs.
    # Closing it here would turn "I looked and found nothing" into "answered".
    Check 'a_look_that_found_nothing_leaves_the_question_open' `
        (($lookOut -match 'still open') -and ((Dx @('concierge-questions')) -match 'sunken city')) $lookOut
    $wsAll = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    Check 'outside_folder_was_never_attached' `
        (-not (@($wsAll) | Where-Object { "$($_.members.path)" -match 'atlantis' })) ''

    # ---- rung 4: ask, with candidates -- and TEACH --------------------------------------
    $r = Resolve-Text "sort out the beacon rotation gearbox"
    Check 'group_double_uncertainty_asks_the_operator' ($r.rung -eq 'ask' -and $null -ne $r.question) ($r | ConvertTo-Json -Compress)
    Check 'question_lands_in_the_merged_feed' ((Dx @('concierge-feed')) -match 'not sure which workspace') ''
    Check 'question_offers_candidates_and_new' ((Dx @('concierge-feed')) -match 'or new\?') ''
    $q = Dx @('concierge-questions')
    Check 'question_is_a_row_that_survives' ($q -match 'beacon rotation gearbox') $q

    $ans = Dx @('concierge-answer', "$($r.question)", 'lighthouse')
    Check 'answer_resolves_the_question' ($ans -match 'answered: lighthouse') $ans
    Check 'answer_teaches_an_alias' ($ans -match 'learned: "gearbox"' -or $ans -match 'learned:') $ans

    # ...and THAT is the point: the same sentence now resolves at rung 1, for free.
    $r = Resolve-Text "the beacon rotation gearbox is grinding again"
    Check 'rung_4_decays_to_rung_1' ($r.rung -eq 'registry' -and $r.name -eq 'lighthouse') ($r | ConvertTo-Json -Compress)

    Check 'answering_twice_is_refused' ((Dx @('concierge-answer', "$($r.question)", 'work')) -match 'error') ''

    # ---- the review-behind net (§2.3) ---------------------------------------------------
    # It exists because the per-workspace brain STRUCTURALLY cannot catch a wrong-workspace
    # delivery: it does not know other workspaces exist (§14). So the concierge's cheap tier
    # reviews an optimistic delivery from behind -- the BrainReview pattern one level up.
    $wsAll = (Dx @('workspaces', '--json')) | ConvertFrom-Json
    $lh = (@($wsAll) | Where-Object { $_.name -eq 'lighthouse' }).id
    if (-not $lh) { throw "could not resolve the lighthouse workspace id from: $($wsAll | ConvertTo-Json -Compress)" }

    # Silent on agreement (operator's rule #3): nothing new reaches the feed.
    $before = @(((Dx @('concierge-feed')) -split "`r?`n") | Where-Object { $_.Trim() }).Count
    Dx @('concierge-review', 'tighten the mooring lines', '--workspace-id', $lh) | Out-Null
    Start-Sleep -Seconds 2
    $after = @(((Dx @('concierge-feed')) -split "`r?`n") | Where-Object { $_.Trim() }).Count
    Check 'review_behind_is_silent_when_it_agrees' ($after -eq $before) "before=$before after=$after"

    # Disagreeing, it speaks -- and it must NOT claim to have fixed anything. You cannot
    # unsay a sentence to an agent (the §5 error asymmetry), so all it can honestly do is
    # say where the sentence went, where it thinks it belonged, and how to resend it.
    Dx @('concierge-review', 'grease the winch cxdisagree:harbour', '--workspace-id', $lh) | Out-Null
    Start-Sleep -Seconds 2
    $feed = Dx @('concierge-feed')
    Check 'review_behind_reports_a_group_misroute' ($feed -match 'but it looks like harbour') $feed
    Check 'review_behind_admits_it_cannot_undo' ($feed -match 'already delivered') $feed
    Check 'review_behind_hands_over_the_resend' ($feed -match 'dodona input .* --workspace') $feed

    # ---- the authority cap (§2), asserted rather than assumed ---------------------------
    # Registry + routing + resolution, nothing else. The moment the concierge coordinates
    # work rather than routing sentences it becomes the persistent-coordinator serialization
    # point §12 designed out -- so its store must hold no lanes, no tickets, no claims and
    # no merge token, and it must hold the things that have to survive a window close.
    $tables = (python -c "
import sqlite3
db = sqlite3.connect(r'$dodonaHome\concierge\store.db')
for r in db.execute('SELECT name FROM sqlite_master'): print(r[0])
") | Out-String
    $tableList = @(($tables -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $forbidden = @(@('lanes', 'tickets', 'claims', 'merge_token', 'token_queue') | Where-Object { $tableList -contains $_ })
    Check 'concierge_store_holds_no_work_state' ($forbidden.Count -eq 0) "found: $($forbidden -join ',')"
    Check 'concierge_store_holds_what_must_survive_a_window' `
        (($tableList -contains 'questions') -and ($tableList -contains 'resolutions') -and ($tableList -contains 'feed')) $tables

    # ---- resolutions are recorded: free labeled data for tuning (§4) --------------------
    $rows = (python -c "
import sqlite3
db = sqlite3.connect(r'$dodonaHome\concierge\store.db')
for r in db.execute('SELECT rung FROM resolutions ORDER BY id'): print(r[0])
") | Out-String
    # Split and compare, never a multiline regex: `$` in .NET sits before \n, so against
    # CRLF output "^only$" never matches. (This cost a round; CLAUDE.md 0.2 territory.)
    $rungs = @(($rows -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($rung in 'only', 'registry', 'path', 'fuzzy', 'discovery', 'ask') {
        Check "resolution_recorded_$rung" ($rungs -contains $rung) ($rungs -join ',')
    }

    Dx @('concierge-stop') | Out-Null
    Start-Sleep -Milliseconds 400
    Check 'concierge_stops_gracefully' ($cx.HasExited -or -not (Get-Process -Id $cx.Id -ErrorAction SilentlyContinue)) ''
}
finally {
    if ($cx -and -not $cx.HasExited) { try { Stop-Process -Id $cx.Id -Force } catch { } }
    # Scoped cleanup, never by process name (CLAUDE.md §4): the concierge's tier shims
    # record their pids in its own directory, exactly as lane shims do in a workspace's.
    Get-ChildItem (Join-Path $dodonaHome 'concierge\shim-tier*.json') -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item (Join-Path $dodonaHome 'concierge\store.db') "$out\concierge-store.db" -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $dodonaHome 'concierge\registry.db') "$out\registry.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- CONCIERGE ACCEPTANCE (group-scope ladder, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
