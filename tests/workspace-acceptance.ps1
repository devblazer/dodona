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
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$fake = "$repo\src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$repo\src\DodonaShim\bin\Release\net8.0\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'workspace-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

# A workspace: not a repository itself, holding three that are, plus a docs folder that
# belongs to no repository at all.
$root = Join-Path $env:TEMP ("dodona-ws-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
    $daemon = Start-Process $dodona -ArgumentList "daemon", "--root", $root -PassThru -NoNewWindow `
        -RedirectStandardOutput "$out\daemon.out" -RedirectStandardError "$out\daemon.err"
    Start-Sleep -Milliseconds 800

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
    Start-Sleep -Milliseconds 700
    Check 'lanes_are_workspace_wide' ((Dodona @("tail", "$lane", "5")) -match 'lanes span the workspace') ''

    # ---- one store, one causal chain for the whole workspace ----
    $ev = (python -c "
import sqlite3
db = sqlite3.connect(r'$root\.dodona\store.db')
for r in db.execute('''SELECT detail FROM events WHERE kind='ticket_created' ORDER BY id'''): print(r[0])
") | Out-String
    Check 'one_causal_chain_names_repos' ($ev -match 'repo engine' -and $ev -match 'repo tools') $ev

    Dodona @("stop-daemon") | Out-Null
}
finally {
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    foreach ($n in 'DodonaShim', 'DodonaFakeAgent') { try { Get-Process $n -ErrorAction Stop | Stop-Process -Force } catch { } }
    Copy-Item "$root\.dodona\store.db" "$out\store.db" -ErrorAction SilentlyContinue
    Remove-Item env:DODONA_NO_AUTOSTART -ErrorAction SilentlyContinue
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- WORKSPACE ACCEPTANCE (multi-repo, model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
