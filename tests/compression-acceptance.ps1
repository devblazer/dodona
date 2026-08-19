# Selective compression acceptance (§5): a pane says the short readable thing, the raw
# text stays one keystroke away, and mid-turn narration never reaches the grid at all.
# Fake agents on both sides — the pool runs DodonaFakeAgent with DODONA_LANE_ROLE set, so
# this suite costs zero model calls like every other one.
#
# The load-bearing claim is the FALLBACK, not the happy path: compression is an
# improvement to a row that was already complete and already on screen, so every failure
# of it must leave the operator reading the agent's own words. Two checks below unplug
# the compressor deliberately for exactly that reason.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\_workspace.ps1"
# Isolated workspace registry + store tree for this suite: never touch the
# operator's own workspaces (§17, and CLAUDE.md §4's reasoning one level up).
$dodonaHome = Use-IsolatedDodonaHome 'compress'
# The binaries under test are a COPY, in this run's own DODONA_HOME -- nothing here executes
# out of src\...\bin, so a leaked daemon can never hold the file the compiler must overwrite
# (docs/INVESTIGATION-2026-08-18.md RC3; tests/_workspace.ps1 Use-TestBinaries has the why).
$bin = Use-TestBinaries $repo
$dodona = "$bin\dodona.exe"
$ui = "$bin\DodonaUi.exe"
$fake = "$bin\DodonaFakeAgent.exe"
$env:DODONA_SHIM = "$bin\DodonaShim.exe"
$env:DODONA_NO_AUTOSTART = "1"
$out = Join-Path $PSScriptRoot 'compression-output'
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item "$out\*" -Force -Recurse -ErrorAction SilentlyContinue

$root = Join-Path (Use-SuiteTemp) ("dodona-cmp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$root\src" | Out-Null
Set-Content "$root\src\a.cs" "// a"
Set-Content "$root\.gitignore" ".dodona/"
git -C $root init -b main -q
git -C $root add -A
git -C $root -c user.email=t@t -c user.name=t commit -q -m init

$results = [ordered]@{}
function Dodona([string[]]$a) { $o = (& $dodona ($a + @('--root', $root))) | Out-String; $o.Trim() }
function Dump() { Dodona @('ui', 'dump') | ConvertFrom-Json }
# "not answering yet" as a VALUE rather than an exception, so a Wait-Until can poll it.
function DumpOrNull() { try { Dump } catch { $null } }
function Check([string]$name, [bool]$cond, [string]$detail = '') { $results[$name] = if ($cond) { 'PASS' } else { "FAIL $detail".Trim() } }
# The last of the three local copies (P7.3). Delegates to the shared helper, which now carries
# BOTH of this copy's hard-won properties -- the query travels by environment variable so no
# amount of quoting in the SQL can collide with python's, and both ends of the pipe are pinned to
# UTF-8 (the em-dash incident that made `blocked_uses_the_fixed_schema` shell-dependent) -- and
# adds the one this copy lacked: it THROWS on a sqlite error instead of returning an empty string
# that casts to 0 and passes whatever assertion it is put in front of.
function Rows([string]$sql) { Invoke-StoreSql $storeDb $sql }

# A turn-final long enough to be worth compressing: the daemon deliberately skips
# anything already short, so a 40-character result must NOT spend a model call.
$long = "the shoreline foam looked wrong at grazing angles because the mask came from wave height alone, so every crest above the threshold merged into one flat white band; it now uses height times curvature and only breaking crests foam"

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
    Wait-Daemon $ws.CtlPipe | Out-Null

    # Assert the store migrated to whatever THIS binary declares, never a frozen literal.
    # The old form was `-match '7'`, which went red the moment Ver.Schema became 8 (M5.1's
    # lanes.cwd column) even though the migration was perfectly correct -- and, worse, a
    # regex match on a bare digit would have passed for 17 or 71 too. Reading the number the
    # binary itself reports makes this check verify the thing it is named after: that the
    # store the daemon opened is at the shape the daemon expects.
    $declaredSchema = ((& $dodona version --json | ConvertFrom-Json).schema)
    Check 'store_migrated_to_declared_schema' `
        ((Rows "PRAGMA user_version").Trim() -eq "$declaredSchema") `
        "store=$(Rows 'PRAGMA user_version') binary=$declaredSchema"

    # ---- a work lane with NO compressor pool: the full text must still arrive ----
    # lane-start names the lane itself; on an empty store the first work lane is 1.
    Dodona @("lane-start", "--child", $fake) | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role='work' AND state='alive'").Trim() -eq '1' } 25000 'the work lane is alive' | Out-Null
    Dodona @("say", "1", "say $long") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM pane_events WHERE kind='result'").Trim() -ne '0' } 25000 'the turn result arrives' | Out-Null

    $uncompressed = Rows "SELECT compressed IS NULL, substr(body,1,40) FROM pane_events WHERE kind='result' ORDER BY id DESC LIMIT 1"
    Check 'no_pool_leaves_the_row_uncompressed' ($uncompressed -match '^1\|') $uncompressed

    $uiProc = Start-Process $ui -ArgumentList "--root", $root, "--test-window" -PassThru
    Wait-Until { (@((DumpOrNull).slots | Where-Object { -not $_.empty }).Count) -ge 1 } 30000 'the window answers with the lane' | Out-Null
    $pane = (Dump).slots | Where-Object { -not $_.empty }
    Check 'no_pool_still_shows_the_agents_words' (($pane.lines -join '|') -match 'only breaking crests foam') ($pane.lines -join '|')

    # ---- mid-turn narration never reaches the grid, but is never lost either ----
    Check 'midturn_narration_is_not_in_the_pane' (($pane.lines -join '|') -notmatch 'working on:') ($pane.lines -join '|')
    $stored = Rows "SELECT COUNT(*) FROM pane_events WHERE kind='agent_line' AND body LIKE 'working on:%'"
    Check 'midturn_narration_is_still_a_row' ([int]($stored.Trim()) -ge 1) $stored

    # ---- now warm the pool and take another turn ----
    Dodona @("compressor-start", "--child", $fake, "--count", "2") | Out-Null
    Wait-Until { (Rows "SELECT COUNT(*) FROM lanes WHERE role='compressor' AND state='alive'").Trim() -eq '2' } 25000 'the compressor pool is warm' | Out-Null
    $pool = Rows "SELECT COUNT(*) FROM lanes WHERE role='compressor' AND state='alive'"
    Check 'pool_is_two_sessions_not_one' ([int]($pool.Trim()) -eq 2) $pool
    Check 'pool_takes_no_grid_slot' ((@((Dump).slots | Where-Object { -not $_.empty })).Count -eq 1) ''

    Dodona @("say", "1", "say $long") | Out-Null
    Wait-Until { (Rows "SELECT compressed IS NOT NULL FROM pane_events WHERE kind='result' AND lane_id=1 ORDER BY id DESC LIMIT 1") -match '^1' } 30000 'the turn final is compressed' | Out-Null

    # lane_id=1 everywhere: the compressor's OWN reply is also a result row (in its own
    # lane), and "latest result in the store" is usually that one, not the work lane's.
    $row = Rows "SELECT compressed IS NOT NULL, length(compressed) < length(body) FROM pane_events WHERE kind='result' AND lane_id=1 ORDER BY id DESC LIMIT 1"
    Check 'turn_final_gets_compressed' ($row -match '^1\|1') $row
    Check 'compression_is_recorded_as_an_event' ([int]((Rows "SELECT COUNT(*) FROM events WHERE kind='compressed'").Trim()) -ge 1) ''


    $body = Rows "SELECT length(body) FROM pane_events WHERE kind='result' AND lane_id=1 ORDER BY id DESC LIMIT 1"
    Check 'raw_body_is_never_overwritten' ([int]($body.Trim()) -eq $long.Length) "$body vs $($long.Length)"

    Wait-Until {
        $script:pane = @((DumpOrNull).slots | Where-Object { -not $_.empty })
        ($script:pane.lines -join '|') -match 'the shoreline foam looked wrong at grazing angles'
    } 25000 'the pane renders the short version' | Out-Null
    Check 'pane_shows_the_short_version' (($pane.lines -join '|') -match 'the shoreline foam looked wrong at grazing angles') ($pane.lines -join '|')
    # The FIRST turn ran with no pool, so its full text legitimately stands (that is the
    # §5 floor, asserted above). The precise claim: the LAST result line — the turn that
    # ran with a warm pool — is the short rendering, not the paragraph.
    # [char]0x2713 = ✓ : non-ASCII literals in a BOM-less .ps1 are read as ANSI by PS 5.1
    # and match nothing (the em-dash lesson, third occurrence).
    $tick = [string][char]0x2713
    $lastResult = @($pane.lines | Where-Object { $_.StartsWith($tick) }) | Select-Object -Last 1
    Check 'compressed_turn_hides_its_long_tail' ($null -ne $lastResult -and $lastResult -notmatch 'only breaking crests foam') "last result: $lastResult"

    # the overlay is the raw truth: unfiltered kinds, uncompressed bodies (§12)
    $laneTitle = ((Dump).slots | Where-Object { -not $_.empty }).title
    Dodona @("ui", "overlay", $laneTitle) | Out-Null
    Wait-Until { (DumpOrNull).overlay -eq $laneTitle } 25000 'the overlay opens on the lane' | Out-Null
    $ovd = Dump
    Check 'overlay_selected' ($ovd.overlay -eq $laneTitle) "$($ovd.overlay)"
    Check 'overlay_keeps_midturn_and_full_text' ((Rows "SELECT COUNT(*) FROM pane_events WHERE kind='agent_line'").Trim() -ne '0') ''
    Dodona @("ui", "overlay", "off") | Out-Null

    # ---- already-short turn-finals must not buy a model call (§2.2) ----
    $before = [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='compressed'").Trim())
    # A NEGATIVE: nothing must happen. There is no arrival to wait for, so this waits for the
    # short result to LAND (which is observable) and then asserts no compression followed it.
    $shortBefore = [int]((Rows "SELECT COUNT(*) FROM pane_events WHERE kind='result' AND lane_id=1").Trim())
    Dodona @("say", "1", "say done: fixed") | Out-Null
    Wait-Until { ([int]((Rows "SELECT COUNT(*) FROM pane_events WHERE kind='result' AND lane_id=1").Trim())) -gt $shortBefore } 25000 'the short result lands' | Out-Null
    $after = [int]((Rows "SELECT COUNT(*) FROM events WHERE kind='compressed'").Trim())
    Check 'short_results_skip_the_compressor' ($after -eq $before) "before=$before after=$after"

    # ---- needs_you renders the fixed BLOCKED schema (§5) ----
    # Long enough to clear the 120-char skip: an already-short result never buys a model
    # call, and the first draft of this text was 118 characters — silently skipped.
    Dodona @("say", "1", "say BLOCKED I need you to choose a name for the water-in-frame visibility rule before I can continue writing the shader, the sim hooks and their golden-image tests") | Out-Null
    Wait-Until { (Rows "SELECT compressed FROM pane_events WHERE kind='result' AND lane_id=1 ORDER BY id DESC LIMIT 1") -match 'BLOCKED' } 30000 'the blocked result is compressed to the fixed schema' | Out-Null
    $blocked = Rows "SELECT compressed FROM pane_events WHERE kind='result' AND lane_id=1 ORDER BY id DESC LIMIT 1"
    $emdash = [string][char]0x2014
    Check 'blocked_uses_the_fixed_schema' ($blocked -match "BLOCKED $emdash" -and $blocked -match 'options:') $blocked

    # ---- mid-turn progress rows are FREE, and must stay free ----
    #
    # Quota is the scarce resource (CLAUDE.md 0.1), and the whole argument for showing
    # mid-turn steps again is that deciding what they say costs no model call at all: a tier
    # and a phrase, in code, from the tool event (PaneProgress.cs). The compressor pool is
    # warm right here, so this is the moment that claim is checkable -- a progress row that
    # ever picked up a `compressed` value would mean every tool call any agent makes now
    # spends haiku turns, which is the opposite of the 5-10x cut 2.2 asks for.
    Dodona @("say", "1", "tool:Read:src/a.cs tool:Read:src/b.cs tool:Edit:src/c.cs say steps done") | Out-Null
    Wait-Until { [int]((Rows "SELECT COUNT(*) FROM pane_events WHERE kind='progress'").Trim()) -ge 3 } 25000 'the progress rows land' | Out-Null
    $steps = Rows "SELECT COUNT(*) FROM pane_events WHERE kind='progress'"
    $paid = Rows "SELECT COUNT(*) FROM pane_events WHERE kind='progress' AND compressed IS NOT NULL"
    Check 'progress_rows_are_written' ([int]($steps.Trim()) -ge 3) "progress rows: $steps"
    # Both halves in one assertion, on purpose: "none of them were compressed" is
    # trivially true of a store with no progress rows in it, so a check written as that
    # alone would pass against the build this feature does not exist in -- VACUOUS, and
    # a guard that cannot fail is decoration (CLAUDE.md 0.3).
    Check 'progress_rows_never_reach_a_compressor' ([int]($steps.Trim()) -ge 3 -and [int]($paid.Trim()) -eq 0) "progress rows: $steps, of which compressed: $paid"

    Dodona @("ui", "screenshot", "--out", "$out\compressed.png") | Out-Null
    Dodona @("ui", "close") | Out-Null
    Wait-Until { $null -eq (DumpOrNull) } 25000 'the window is gone' | Out-Null
    Dodona @("stop-daemon") | Out-Null
}
finally {
    Remove-Item env:DODONA_HOME -ErrorAction SilentlyContinue
    if ($uiProc -and -not $uiProc.HasExited) { try { Stop-Process -Id $uiProc.Id -Force } catch { } }
    if ($daemon -and -not $daemon.HasExited) { try { Stop-Process -Id $daemon.Id -Force } catch { } }
    # Only this test's processes, resolved from its own shim-info files (CLAUDE.md §4).
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
    Copy-Item $storeDb "$out\store.db" -ErrorAction SilentlyContinue
    # Did this suite leak a process into the build output? (RECOVERY-PHASES P1.3) Last in the
    # finally, so the suite's own cleanup has already run and this reports only what survived
    # it. It reports; it never kills -- a check that killed what it found would hide the leak
    # it exists to expose.
    Assert-NoBuildOutputProcesses $repo $results
}

$results | ConvertTo-Json | Set-Content "$out\results.json" -Encoding utf8
Write-Output "---- COMPRESSION ACCEPTANCE (model-free) ----"
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
$failed = @($results.GetEnumerator() | Where-Object { "$($_.Value)" -like 'FAIL*' })
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
