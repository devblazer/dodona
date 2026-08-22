# dev.ps1 -- the ONLY door for mechanical work in this repo (CLAUDE.md sections 0.3 and 1).
#
# Why this is a SCRIPT and not a `dodona dev` subcommand: a tool whose job is to fix a
# broken or blocked build cannot itself require a build. This file works from a clean
# checkout, with no compiled output, on a tree that will not compile.
#
# Why it exists at all: on 2026-08-18 a fifteen-minute change took an hour. Not one minute
# of that was the change. It was (a) four daemons running from src\Dodona\bin holding the
# compiler's own output file, invisible because a daemon outlives its window (section 3.1's
# documented incident, repeating -- the doc did not prevent it, which is the whole argument
# for code over instructions), and (b) a new acceptance check that PASSED against the
# unfixed binary, so it looked like verification and was worth nothing.
#
# Everything here is ASCII on purpose (section 0.2: non-ASCII literals in a BOM-less .ps1
# are read as ANSI and match nothing).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('check', 'build', 'test', 'suites', 'prove', 'gate', 'lint', 'ledger', 'ship', 'worktree', 'help')]
    [string]$Verb,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

# 'Continue', not 'Stop', and CLAUDE.md section 0.2 says why: with 'Stop', a native
# executable writing ANY line to stderr throws NativeCommandError and kills the script. Every
# suite here announces things on stderr, and dotnet/git/dodona all do. This script therefore
# checks $LASTEXITCODE explicitly at every native call and aborts deliberately, rather than
# letting a stray stderr line masquerade as a failure. (It caught this script itself on its
# first run: `dev test m3` died on a workspace-created notice.)
$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$dodona = "$repo\src\Dodona\bin\Release\net8.0\dodona.exe"
$logDir = "$repo\.dodona\dev-logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = "$logDir\dev-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$Verb.log"

function Say([string]$m) { Write-Host $m; Add-Content -Path $log -Value $m -Encoding utf8 }

# One line, at the top, then stop. Never forty minutes in (that is the whole point).
function Abort([string]$why, [string]$fix) {
    Say ""
    Say "BLOCKED: $why"
    if ($fix) { Say "FIX:     $fix" }
    Say "log:     $log"
    exit 2
}

function AllSuites {
    # 'unit' first: it is the cheapest thing that can fail, so a full run finds a broken
    # claim algebra in under a second instead of four minutes in.
    #
    # 'ui-unit' JOINED THIS LIST AT W4, and W3 left the decision here on purpose: it was created
    # with one trivial fact in it, and *"widening the default gate is W4's call to make when it
    # has something to assert"*. It now has. Rung 2 of the double ledger over the DodonaUi
    # assembly lives ONLY there -- tests\Dodona.Tests is net8.0 and cannot load a net8.0-windows
    # assembly at all -- and so does RecognizerContract, which runs the real DeepgramRecognizer
    # against a closed loopback port. Leaving it out would mean the half of the mechanism that
    # covers two of this repo's three doubles ran only when somebody typed the command, which is
    # the routing ladder's failure in a new costume.
    #
    # THE COST, MEASURED 2026-08-22 ON THIS MACHINE: 4.4-4.5 s warm, and it is SOLO (it compiles
    # DodonaUi, and every window suite copies its binaries out of that directory), so it is 4.5 s
    # of SERIALIZED wall clock added to a full run that measured 258-312 s against a 300 s budget.
    # That is ~1.5 %, it is model-free, it opens no window and no microphone, and it is not a
    # candidate for widening back out: the budget pressure is issue #1, not this.
    'unit', 'ui-unit', 'm0', 'm1', 'm2', 'm3', 'm4', 'workspace', 'voice', 'compression', 'brain', 'concierge', 'publish',
    # `ui-use` was one 1221-line, 130-check, 88.8 s suite that had to run alone. Split at its
    # four fixture boundaries 2026-08-21 (issue #2); see SoloSuites below for the measurement.
    'ui-grid', 'ui-shell', 'ui-ask', 'ui-wake'
}

# ---------------------------------------------------------------- blockers

# Any process running out of this repo's build output will hold files the compiler must
# overwrite. Found by PATH, never by name: killing by name once murdered the operator's
# live session (CLAUDE.md section 4), and a name cannot tell their work from a test's.
function Blockers {
    $out = "$repo\src\"
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $p = $null
            try { $p = $_.Path } catch { }
            $p -and $p.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) -and
            ($p -match '\\bin\\')
        })
}

# Report what is running from the build output. It reports; it does NOT stop anything, and
# must not.
#
# It used to run `dodona stop-all` whenever a daemon was among them. That was a machine-wide
# daemon kill wearing the word "clear": StopAll stops every registered workspace daemon, the
# concierge, AND every unregistered dodona-*-ctl pipe on the machine
# (src/Dodona/Program.cs:528-536). DODONA_HOME scopes the registry but NOT the OS pipe
# namespace, so another agent's DODONA_HOME-isolated suite daemons died with it -- fired by
# the command CLAUDE.md section 1 tells every agent to run BEFORE STARTING WORK. `publish
# --all` was deliberately narrowed to registry scope for exactly this reason, and
# tests/publish-acceptance.ps1 proves it; stop-all has no suite at all. Narrowing it here
# would only relocate the blast radius, so the call is deleted rather than scoped.
#
# Nothing is lost by not clearing. A process running from the build output only blocks a
# build if MSBuild actually needs to overwrite the file it holds -- three shims holding
# DodonaShim.exe did not stop a build that only had to replace dodona.exe. THE BUILD IS THE
# ORACLE: if it fails on a lock, Do-Build names the exact holder and the one command that
# frees it. Predicting a block and killing 24 agents to be safe is how you murder someone's
# live session (CLAUDE.md section 4).
# The INSTALLED app's processes -- the operator's live daemons, shims and window. Resolved by
# PATH under %LOCALAPPDATA%\Dodona\bin, never by name: a name cannot tell the operator's live
# work from a test's, and killing by name once murdered their session (CLAUDE.md section 4).
# This only ever READS; nothing here stops anything.
function LiveApp {
    $binRoot = Join-Path $env:LOCALAPPDATA 'Dodona\bin'
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $p = $null
            try { $p = $_.Path } catch { }
            $p -and $p.StartsWith($binRoot, [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Name = $_.ProcessName; Path = $_.Path } })
}

# Test processes left behind by an earlier suite run, found by PATH under %TEMP%\dodona-*.
# These are never the operator's: their app lives under %LOCALAPPDATA%\Dodona\bin (see
# LiveApp), and a suite's DODONA_HOME is always a GUID temp directory.
#
# Why this is counted at all: they are not idle. Each one holds a pipe server, a fake agent
# child, and the runner's own redirect files, and enough of them CHANGE THE ANSWER a timing
# assertion gives. Measured 2026-08-19: with 78 of them alive the full suite run took 300 s
# instead of 75 s, m3 crashed outright, and brain went red on nine timing checks -- and I7's
# failure text blamed a returning Start-Sleep, sending the reader at the wrong file entirely.
# A number here is what stops that misdiagnosis. Stopping them is Phase 3's job (a shim
# should exit when its child does); this only ever REPORTS.
# Phase 3 SHIPPED that, so a wrapper can no longer outlive its agent -- and Do-Gate now asserts
# it (I3) instead of merely printing a number. This still only reports, because what it reports
# BEFORE a run is a machine another session dirtied, which is not this run's failure to own.
# BOTH PREFIXES. This matched only `$env:TEMP\dodona-*`, which was every suite temp directory
# until the per-suite sandbox arrived and became `$env:TEMP\dsb-<6hex>` -- deliberately short,
# for MAX_PATH. Everything a suite makes now nests inside THAT, so from the day the sandbox
# landed this function matched almost nothing, and `dev gate` opened by reporting "leaked test
# processes before: 0" on a machine that had them. A counter that cannot see the thing it
# counts is worse than no counter: it is a green light.
function LeakedTestProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $p = $null
            try { $p = $_.Path } catch { }
            $p -and (($p -like "$env:TEMP\dodona-*") -or ($p -like "$env:TEMP\dsb-*"))
        })
}

# WHAT ELSE IS ON THE MACHINE THAT MOVES THE CLOCK -- and neither of these is a Dodona process,
# which is exactly why nothing reported them (issue #1, issue #25).
#
# `leaked test processes before` was the right instinct aimed at too narrow a set: it counts what a
# SUITE leaves and says nothing about what this repo's own tooling leaves. Measured 2026-08-22,
# five consecutive `dev gate` runs at ONE commit with `live app before: 0` and no leaked test
# processes every time: 268.6s, 296.2s, 303.3s, 346.3s -- monotonic, not a spread -- and 289.5s
# after clearing the two things below. That is a 78 s drift nothing in the preamble could see,
# on a budget whose whole headroom is about 40 s.
#
#   1. `dotnet build` reuses MSBuild nodes, so they OUTLIVE the build. Thirteen were up holding
#      2.9 GB, two of them 22 hours old. This repo builds constantly -- `dev build`, `dev prove`'s
#      baseline, m4's real build inside every gate, every publish. `dotnet build-server shutdown`
#      is the polite way to retire them; it is NOT run automatically here, because it is a
#      machine-wide action and CLAUDE.md 1 is explicit that this script stops nothing on your
#      behalf.
#   2. `%TEMP%` is where every suite creates its sandbox, and it had 32,861 directories in it.
#      Creating, listing and searching there gets slower as that number grows, and it is paid by
#      every suite in the run.
#
# THE COST IS ABOUT 200 ms on a 270 s run, and it is the number that explains the run when it goes
# long. CLAUDE.md 0.1 says not to widen an automatic reader to buy thoroughness nobody asked for;
# the case for this one is that a budget nobody can reproduce is worse than no budget.
function MachineNoise {
    $servers = @(Get-Process -Name 'dotnet', 'MSBuild', 'VBCSCompiler' -ErrorAction SilentlyContinue | Where-Object {
            # The operator's IDE runs OmniSharp on `dotnet` too, and it is THEIRS -- never counted
            # here and never suggested for shutdown. `build-server shutdown` leaves it alone as
            # well, which is how the two were told apart in the first place.
            $c = $null
            try { $c = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue).CommandLine } catch { }
            -not ($c -and $c -match 'OmniSharp|languageserver')
        })
    $mb = if ($servers.Count) { [math]::Round((($servers | Measure-Object WorkingSet64 -Sum).Sum) / 1MB) } else { 0 }
    $tempDirs = 0
    try { $tempDirs = @([IO.Directory]::EnumerateDirectories($env:TEMP)).Count } catch { }
    [pscustomobject]@{ Servers = $servers.Count; ServerMB = $mb; TempDirs = $tempDirs }
}

# The wrappers and agents specifically -- what I3 is about. A `dodona` daemon still winding down
# from `stop-daemon` when we look is a RACE, not an orphan, and lumping the two together is what
# made "publish-acceptance leaks four DodonaShim every run" a fact everybody repeated: publish-
# acceptance starts no lanes at all, so it has never leaked a wrapper.
function LeakedAgentProcesses {
    @(LeakedTestProcesses | Where-Object { $_.ProcessName -in @('DodonaShim', 'DodonaFakeAgent', 'claude') })
}

# ---------------------------------------------------------------- the repo lint (I8, P5.1)

# WHAT THE LINT LOOKS AT -- TRACKED **AND** UNTRACKED (issue #15).
#
# This was `git ls-files` alone, which lists only what git already knows about. So a file you had
# written but not staged -- the normal state at the exact moment CLAUDE.md 1 tells you to run
# `dev lint`, "directly after any scripted edit" -- was invisible to every rule, and the verdict
# line still said `clean`. It was not wrong about what it looked at; it was silent about what it
# skipped, and silence reads as approval.
#
# It put a broken reference on main TWICE IN ELEVEN MINUTES on 2026-08-21, by the same person,
# minutes after they had written the explanation of the mistake into a commit message -- which is
# why this is a change to the tool and not a note in the docs. The second instance is the one that
# matters: `dev lint` had by then taken on the double ledger's rung 1, so an unanchored test double
# could be written, linted ("every double anchored"), and committed -- and the file becomes tracked
# AT the commit, so the check never runs again. The one moment the guarantee is needed was the one
# moment it was blind.
#
# `--cached --others --exclude-standard` is the whole fix: the rules are pure functions over file
# CONTENT and nothing about them needs the file to be tracked, and `.gitignore` is already the
# right filter -- bin\, obj\, .dodona\, tests\*-output\ and other sessions' worktrees are excluded
# by construction rather than by a pattern somebody has to maintain. Checking them beats refusing
# while they exist, which was the other option this ticket offered.
#
# AND THE VERDICT LINE STATES ITS SCOPE, for the same reason `dev gate` says "on the 10 assertions
# above, and only those": an unqualified `clean` is a claim about the repo, and this is a claim
# about a file set.
function Lint-Files([string[]]$patterns) {
    $tracked = @(& git -C $repo ls-files --cached --exclude-standard -- $patterns 2>$null)   # lint-files-ok
    $untracked = @(& git -C $repo ls-files --others --exclude-standard -- $patterns 2>$null) # lint-files-ok
    [pscustomobject]@{
        Tracked   = $tracked
        Untracked = $untracked
        All       = @($tracked) + @($untracked)
    }
}

# How many files the last Repo-Lint actually opened, for the verdict line. A script variable
# rather than a second return value because `Repo-Lint` returns problems to two call sites and a
# shape change there is a change to what a green means.
$script:LintScope = ''

# Two questions about the PROSE, both sub-second.
#
# (i) NO CONTROL BYTES outside tab/CR/LF. This is not tidiness. The rule was written because a
#     literal 0x08 in CLAUDE.md and in SKILL.md made the `tests\brain-acceptance.ps1` path in
#     both of them unrunnable -- copy the line, and it does not work. Those two are long gone,
#     and the lint immediately found a LIVE one they did not know about: two 0x07 (BEL) bytes
#     inside string literals at tests\publish-acceptance.ps1:207, where `$out\ap-noprov.out` had
#     its `\a` eaten as an escape by whatever wrote it. The suite is green, so for who knows how
#     long it has been writing its diagnostics to a filename containing a control character --
#     which is to say, somewhere nobody would ever look for them.
#
# (ii) EVERY tests\*.ps1 NAMED IN A .md MUST EXIST. A command in the docs that cannot run is the
#      thing this whole phase is named after. `(planned)` on the same line exempts it, because a
#      PLAN describing a suite nobody has written yet is correct prose, and a lint that fires on
#      correct prose is a lint somebody switches off.
# ---- (iii) EVERY WIRE COMMAND DECLARES WHETHER IT IS WORTH STARTING A DAEMON (issue #13)
#
# `src\Dodona\DaemonSurface.cs` decides, per wire command, whether a client that cannot connect
# should SUMMON one. Summoning a workspace daemon runs its warm-up: four real `claude -p --model
# haiku` processes in this repo. Before #13 the decision was four literals in `Client`, right
# about those four names and silent about the other sixty -- so `dodona policy` started four
# model agents to print a static config table.
#
# A longer hand-maintained list is the same bug with more entries, so what makes this different
# is HERE and not there: the table has to stay exactly equal to the two dispatchers' `case`
# labels, in both directions. Add a command and forget to declare it, and this goes red before
# the commit; delete one and leave a row behind, and it goes red too. The compiler cannot ask
# the question -- a `case` label is a string -- so the lint asks it.
#
# The outer switch is discriminated by INDENTATION (12 spaces): `swap-answer` has a nested
# `switch (answer)` whose cases sit at 20. A refactor that reindents would silently harvest
# nothing, which is a check degrading into a green -- so an empty harvest is itself a problem.
function Surface-Static {
    $problems = @()
    $surfaceFile = Join-Path $repo 'src\Dodona\DaemonSurface.cs'
    if (-not (Test-Path $surfaceFile)) {
        return @{ Problems = @("src\Dodona\DaemonSurface.cs is missing -- it is what declares which commands may start a daemon (issue #13)") }
    }
    $surface = Get-Content $surfaceFile -Raw

    # The two tables, read as text: from `Dictionary<string, Summon> <Name> = new(...)` to the
    # `};` that closes it. Text and not reflection for the same reason the double ledger's rung 1
    # is a text scan: it cannot miss an assembly, and it needs no build to run.
    function Declared([string]$name) {
        $m = [regex]::Match($surface, "(?s)Dictionary<string,\s*Summon>\s+$name\s*=\s*new\(.*?\n\s*\};")
        if (-not $m.Success) { return $null }
        @([regex]::Matches($m.Value, '\["([^"]+)"\]') | ForEach-Object { $_.Groups[1].Value })
    }
    function CaseLabels([string]$rel) {
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { return $null }
        @(Get-Content $full | ForEach-Object {
                $m = [regex]::Match($_, '^ {12}case "([^"]+)":')
                if ($m.Success) { $m.Groups[1].Value }
            })
    }

    foreach ($pair in @(
            @{ Table = 'Ws'; File = 'src\Dodona\Daemon.Commands.cs'; What = 'the workspace daemon' },
            @{ Table = 'Cx'; File = 'src\Dodona\Concierge.cs';       What = 'the concierge' })) {
        $declared = Declared $pair.Table
        $labels = CaseLabels $pair.File
        if ($null -eq $declared) {
            $problems += "DaemonSurface.$($pair.Table) could not be read -- the table that decides whether $($pair.What)'s commands may start a daemon (issue #13)"
            continue
        }
        if ($null -eq $labels) { $problems += "$($pair.File) is missing -- Surface-Static cannot check $($pair.What)"; continue }
        if (@($labels).Count -eq 0) {
            $problems += "$($pair.File): no `case` labels found at the outer switch -- Surface-Static harvests on 12-space indentation, so a reindent turns this check into a green that proves nothing (issue #13)"
            continue
        }
        foreach ($c in @($labels)) {
            if (@($declared) -notcontains $c) {
                $problems += "$($pair.File) handles `"$c`" and DaemonSurface.$($pair.Table) does not declare it -- say whether answering it is worth STARTING $($pair.What) (issue #13)"
            }
        }
        foreach ($d in @($declared)) {
            if (@($labels) -notcontains $d) {
                $problems += "DaemonSurface.$($pair.Table) declares `"$d`" and $($pair.File) handles no such command -- a stale row (issue #13)"
            }
        }
    }
    @{ Problems = $problems }
}

function Repo-Lint {
    $problems = @()
    $scope = Lint-Files @('*.md', '*.ps1')
    $files = @($scope.All)
    $script:LintScope = "$(@($scope.Tracked).Count) tracked + $(@($scope.Untracked).Count) untracked"
    foreach ($rel in $files) {
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { continue }          # staged-deleted, still listed
        $bytes = [System.IO.File]::ReadAllBytes($full)
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            $b = $bytes[$i]
            if ($b -lt 0x20 -and $b -ne 0x09 -and $b -ne 0x0A -and $b -ne 0x0D) {
                # the LINE, so the report is actionable rather than an offset nobody can find
                $line = 1
                for ($k = 0; $k -lt $i; $k++) { if ($bytes[$k] -eq 0x0A) { $line++ } }
                $problems += ("{0}:{1} control byte 0x{2:x2} -- only tab, CR and LF are allowed" -f $rel, $line, $b)
                break                                     # one per file is enough to send someone
            }
        }

        # ...and NO MIXED LINE ENDINGS in the working copy. This one is not about git: with
        # core.autocrlf on, git normalises whatever it is handed, so a half-CRLF file commits
        # clean and the gate's P7.5 row passes -- correctly, because nothing wrong was stored.
        # It still matters, because a MIXED file is precisely what makes the next patch script
        # misbehave: `if CRLF in bytes` is true for a file that is mostly LF, so the script picks
        # the wrong newline and double-converts. That is how Phase 7 turned a 105-line insert into
        # a 1214-line phantom rewrite, and this lint found CLAUDE.md sitting at 758 CRLF against 20
        # bare LF -- twenty lines Phase 7 had inserted, invisible to every check that existed.
        $crlf = 0; $bare = 0
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            if ($bytes[$i] -eq 0x0A) {
                if ($i -gt 0 -and $bytes[$i - 1] -eq 0x0D) { $crlf++ } else { $bare++ }
            }
        }
        if ($crlf -gt 0 -and $bare -gt 0) {
            $problems += ("{0} has MIXED line endings ({1} CRLF, {2} bare LF) -- pick one; a patch script that sniffs this file will pick the wrong newline" -f $rel, $crlf, $bare)
        }
    }
    foreach ($rel in @($files | Where-Object { $_ -like '*.md' })) {
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { continue }
        $n = 0
        foreach ($text in @(Get-Content $full -ErrorAction SilentlyContinue)) {
            $n++
            if ($text -match '\(planned\)') { continue }
            foreach ($m in [regex]::Matches($text, 'tests[\\/]([A-Za-z0-9_.\-]+\.ps1)')) {
                $namedFile = $m.Groups[1].Value
                if (-not (Test-Path (Join-Path $repo (Join-Path 'tests' $namedFile)))) {
                    $problems += ("{0}:{1} names tests\{2}, which does not exist -- write it, fix the name, or mark the line (planned)" -f $rel, $n, $namedFile)
                }
            }
        }
    }

    # ============================ THE FOLD (D-T23, plan 3.4 and review finding 15) ============
    #
    # `dev ledger`'s STATIC rungs and the double ledger's rung 1 are asserted HERE, inside I8,
    # and deliberately not as gate assertions of their own. The gate's count STAYS AT TEN.
    #
    # Why that matters rather than being bookkeeping: the plan's first draft said the count
    # stayed at ten, then made `dev ledger` an eleventh assertion, then conceded in its own risk
    # list that it had -- three statements and no reconciliation. A lint row is the correct home
    # anyway. I8 is already one of the ten, already asserted by `gate`, and is by definition a
    # sub-second static parse of tracked files, which is exactly what these rungs are.
    #
    # W2 could not do this fold: it would have made baseline.tsv's absence a GATE FAILURE, and
    # only a green gate can capture a baseline. The census exists now (964 rows), so it lands.
    #
    # THE COST, said out loud: `dev lint` is no longer sub-second. It is ~1-2 s, because
    # Ledger-Static AST-parses fifteen suite files and Doubles-Static reads every tracked .cs
    # under src\ and tests\. That is still the cheapest verification in this repo by two orders
    # of magnitude, and it is the one that catches a check name colliding with another suite's.
    $problems += @((Ledger-Static).Problems)
    $problems += @((Doubles-Static).Problems)
    $problems += @((Surface-Static).Problems)

    # ...AND NOBODY ENUMERATES BEHIND `Lint-Files`'S BACK (issue #15).
    #
    # The fix for #15 was to make one function decide what the lint looks at. What can undo it is
    # a THIRD site reaching for `git ls-files` directly -- which is how the two that existed came
    # to disagree with the verdict line in the first place, and neither author did anything
    # unreasonable: `ls-files` is the obvious way to ask git for the repo's files, and its silence
    # about untracked ones is not written on it.
    #
    # So the rule is asserted rather than remembered, in the file it is about. `Lint-Files` itself
    # is the one exemption, by line: it is the definition, and the two calls there are what the
    # rest of the tooling routes through. Cheap enough to belong in a sub-second lint -- two files,
    # read once.
    foreach ($rel in @('tools\dev.ps1', 'tools\dev.ledger.ps1')) {
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { continue }
        $n = 0
        foreach ($text in @(Get-Content $full -ErrorAction SilentlyContinue)) {
            $n++
            # The INVOCATION shape, not the word: this rule's own message names the command.
            if ($text -notmatch 'git\s+(?:-C\s+\S+\s+)?ls-files') { continue }
            if ($text -match '^\s*#') { continue }                 # prose about the rule
            if ($text -match 'lint-files-ok') { continue }          # the marker, spelled out below
            $problems += ("{0}:{1} calls `git ls-files` directly -- that lists TRACKED files only, which is what made `dev lint` report clean over work it had never opened (issue #15). Go through Lint-Files." -f $rel, $n)   # lint-files-ok: this line NAMES the command it forbids
        }
    }
    return $problems
}

function ReportBlockers {
    $b = Blockers
    if ($b.Count -eq 0) { Say "blockers: none"; return }

    Say "in the build output: $($b.Count) process(es) -- may or may not block, the build decides"
    foreach ($p in $b) { Say "  pid $($p.Id)  $($p.ProcessName)  $($p.Path)" }
}

# ---------------------------------------------------------------- the commit guard

# The MAIN checkout, whichever tree is asking. $repo is $PSScriptRoot's parent, which inside a
# worktree is the WORKTREE -- so anything that must be repo-wide (where worktrees live, what the
# shared checkout IS) has to come from the git COMMON dir instead.
function MainCheckout {
    $common = (& git -C $repo rev-parse --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $common) { return $null }
    if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $repo $common }
    return (Split-Path -Parent ([System.IO.Path]::GetFullPath($common)))
}

# Deploy .githooks\pre-commit into the repository's real hooks directory, so committing from the
# shared checkout is refused (D-7).
#
# Runs on EVERY dev invocation, on purpose. .git\hooks is not versioned, so a tracked hook file
# is inert until something copies it, and an install step a person has to remember is not
# enforcement (CLAUDE.md 0.3, D-6). Same move DeployGate makes with .git\info\exclude.
#
# WHY A COPY AND NOT core.hooksPath, WHICH WAS TRIED AND MEASURED FAILING. Pointing git at the
# tracked directory is git's own mechanism and looks obviously better -- the hook is then a
# reviewable versioned file rather than a copy inside .git. It was implemented, and then a commit
# from the shared checkout SUCCEEDED against it. The reason is the whole objection to it: the
# hooks directory is TRACKED, so it exists only on branches and commits that carry it. Any other
# branch, and every historical commit checked out while bisecting -- which this phase's
# commit-provenance work exists to make possible -- silently has no hook at all.
#
# So .githooks\pre-commit stays the tracked, reviewable SOURCE and this deploys it into the
# common .git\hooks, which is branch-independent. Any leftover core.hooksPath is cleared, because
# git honours that setting and would ignore the copy.
#
# Quiet when already correct: a line on every `dev check` would be noise, and noise is how a real
# line gets skipped.
function Install-Hooks {
    $src = "$repo\.githooks\pre-commit"
    if (-not (Test-Path $src)) { return }        # a checkout that predates the hook

    $hp = (& git -C $repo config --get core.hooksPath 2>$null)
    if ($LASTEXITCODE -eq 0 -and $hp) {
        & git -C $repo config --unset core.hooksPath 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
        Say "cleared core.hooksPath ('$hp') -- it vanishes on any branch without that directory"
    }

    # A worktree's hooks live in the COMMON dir, shared with the main checkout, which is exactly
    # what we want: one install covers every session's worktree.
    $common = (& git -C $repo rev-parse --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $common) { return }
    if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $repo $common }
    $dir = Join-Path $common 'hooks'
    $dst = Join-Path $dir 'pre-commit'

    $marker = 'Dodona enforcement'
    $want = [System.IO.File]::ReadAllText($src)
    if (Test-Path $dst) {
        $have = [System.IO.File]::ReadAllText($dst)
        if ($have -eq $want) { return }                                  # already current
        if ($have -notmatch [regex]::Escape($marker)) {
            # Never clobber somebody else's hook silently, and say it EVERY run: an unenforced
            # lock nobody mentions is the silent degrade CLAUDE.md section 3 calls a bug.
            Say "WARNING: $dst exists and is not Dodona's -- commits from the shared checkout are NOT refused."
            Say "         Merge .githooks\pre-commit into it by hand, or move it aside."
            return
        }
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
    # LF and no BOM: git runs this through sh, which chokes on CRLF and on a BOM.
    $w = New-Object System.IO.StreamWriter($dst, $false, (New-Object System.Text.UTF8Encoding($false)))
    $w.NewLine = "`n"
    $w.Write($want.Replace("`r`n", "`n"))
    $w.Close()
    Say "deployed the commit guard: $dst"
}

# ---------------------------------------------------------------- verbs

# worktree: a tree of your own, which is where all work belongs (D-7).
#
# A verb on the existing door rather than a new script or a dodona subcommand (D-3), and it
# is deliberately thin: `git worktree add` plus the cd line. It holds no state, no registry
# and no session table -- there is nothing here to go stale, which is the same reason both
# enforcement layers test the shape of `.git` instead of consulting a list.
#
# Layer 1's refusal names this exact command, so meeting the refusal costs five seconds
# rather than a detour into git's manual.
function Do-Worktree {
    if (-not $Rest -or $Rest.Count -eq 0) {
        Abort "worktree needs a name" "dev worktree phase2   (becomes .claude\worktrees\phase2)"
    }
    $name = $Rest[0]
    if ($name -notmatch '^[A-Za-z0-9._-]+$') {
        Abort "'$name' is not a usable worktree name" "letters, digits, dot, dash, underscore"
    }
    Say "== worktree: $name =="

    # Worktrees are always SIBLINGS under the main checkout, never nested inside whichever
    # tree you happen to be standing in. $repo is $PSScriptRoot's parent, which in a worktree
    # is the WORKTREE -- so using it here built .claude\worktrees\phase2\.claude\worktrees\x
    # the first time this verb ran. Nesting costs the Windows MAX_PATH margin that CLAUDE.md
    # 5.2 keeps worktree names short to protect, and hides one session's tree inside
    # another's. The git COMMON dir is always the main checkout's .git, whichever tree asks.
    $main = MainCheckout
    if (-not $main) { Abort "not a git repository: $repo" "run this inside the Dodona checkout" }

    $dir = "$main\.claude\worktrees\$name"
    if (Test-Path $dir) {
        # Already there is a fine answer, not an error: the point is to end up in a tree of
        # your own, and you are. Never halt on a state that is already correct (CLAUDE.md 0.1).
        Say "already exists -- use it:"
        Say ""
        Say "  cd $dir"
        Say ""
        Say "log: $log"
        return
    }

    # A branch per worktree, named after it. `git worktree add <path>` does this by default;
    # naming it explicitly is what makes the failure legible when the branch already exists.
    $existing = @(& git -C $main branch --list $name)
    $out = if ($existing.Count -gt 0) {
        Say "branch '$name' already exists -- checking it out into the new tree"
        & git -C $main worktree add $dir $name 2>&1
    }
    else {
        & git -C $main worktree add -b $name $dir 2>&1
    }
    $code = $LASTEXITCODE
    Add-Content -Path $log -Value $out -Encoding utf8
    if ($code -ne 0) {
        $out | Select-Object -Last 6 | ForEach-Object { Say "  $_" }
        Abort "git worktree add failed" "see $log"
    }
    $out | ForEach-Object { Say "  $_" }

    Say ""
    Say "your tree is ready. Work there, not in the shared checkout:"
    Say ""
    Say "  cd $dir"
    Say ""
    Say "It has its own bin and obj, so your builds cannot collide with another session's (I2)."
    Say "log: $log"
}


function Do-Check {
    Say "== check =="
    Say "repo: $repo"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Abort "dotnet is not on PATH" "install the .NET 8 SDK" }
    Say "dotnet: $(dotnet --version)"
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Abort "git is not on PATH" "install git" }

    if (Test-Path $dodona) {
        # A daemon running from the build output is the root cause, not a nuisance. Name it.
        $fromOut = @(Blockers | Where-Object { $_.ProcessName -eq 'dodona' })
        if ($fromOut.Count -gt 0) {
            Say "NOTE: a daemon is running from src\...\bin. That is what blocks builds, and it"
            Say "      is invisible after the window closes. Nothing here stops it for you --"
            Say "      dev build NAMES the holder; stopping it is your explicit call."
        }
    }
    $b = Blockers
    if ($b.Count -eq 0) { Say "in the build output: nothing" }
    else {
        Say "in the build output: $($b.Count) process(es) -- may or may not block, the build decides"
        foreach ($p in $b) { Say "  pid $($p.Id)  $($p.ProcessName)  $(Split-Path -Leaf $p.Path)" }
    }
    $dirty = @(git -C $repo status --porcelain)
    Say "working tree: $(if ($dirty.Count -eq 0) { 'clean' } else { "$($dirty.Count) change(s)" })"
    Say ""
    Say "verdict: run `dev build` -- it names any real holder exactly, and stops nothing itself."
    Say "log: $log"
}

function Do-Build {
    Say "== build =="
    ReportBlockers
    Say "building Dodona.sln -c Release ..."
    $out = & dotnet build "$repo\Dodona.sln" -c Release 2>&1
    Add-Content -Path $log -Value $out -Encoding utf8
    $errors = @($out | Select-String -Pattern ': error ' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique)
    if ($LASTEXITCODE -ne 0) {
        Say ""
        # A lock failure is not a build failure, and must never be reported as one: an hour
        # went on 2026-08-18 because "Build FAILED" was really "four invisible daemons".
        # MSB3021/MSB3027 name the locked FILE; turn that into named pids and one command.
        $locked = @($out | Select-String -Pattern 'MSB302[17]' | ForEach-Object { $_.Line })
        if ($locked.Count -gt 0) {
            $files = @($locked | ForEach-Object { if ($_ -match "to \`"(?<f>[^\`"]+)\`"") { Split-Path -Leaf $Matches.f } } | Select-Object -Unique)
            $holders = @(Blockers | Where-Object { $files -contains (Split-Path -Leaf $_.Path) })
            Say "NOT A CODE PROBLEM -- the compiler could not overwrite: $($files -join ', ')"
            foreach ($p in $holders) { Say "  held by pid $($p.Id)  $($p.ProcessName)  $($p.Path)" }
            $fix = if (@($holders | Where-Object { $_.ProcessName -like 'DodonaShim*' }).Count -gt 0) {
                "these are LANE AGENTS, which outlive their daemon on purpose. `dodona stop-all --lanes` takes them down -- confirm with the operator first, it stops real work."
            }
            else {
                "run `dodona ps` to see what owns them, then stop it deliberately."
            }
            Abort "build output is locked by $($holders.Count) running process(es)" $fix
        }
        if ($errors.Count -gt 0) {
            Say "BUILD FAILED -- $($errors.Count) compile error(s):"
            $errors | Select-Object -First 20 | ForEach-Object { Say "  $_" }
        }
        else {
            Say "BUILD FAILED for a non-compile reason. Last lines:"
            $out | Select-Object -Last 15 | ForEach-Object { Say "  $_" }
        }
        Say "log: $log"
        exit 1
    }
    $warn = @($out | Select-String -Pattern ': warning ' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique)
    Say "BUILD OK ($($warn.Count) warning(s))"
    if ($warn.Count -gt 0) { $warn | Select-Object -First 10 | ForEach-Object { Say "  $_" } }
    Say "log: $log"
}

# Run one suite and report what it ACTUALLY did. Three things here are load-bearing, and
# every one of them was learned the expensive way on 2026-08-19.
#
# 1. THE OUTPUT GOES TO FILES, NOT TO THIS SCRIPT'S PIPE. `$o = & powershell ... 2>&1` reads
#    the child's stdout through a pipe, and PowerShell does not stop reading when the CHILD
#    exits -- it stops when the last handle to the write end closes. Every process the suite
#    spawns inherits that handle. tests\publish-acceptance.ps1 leaks four DodonaShim
#    processes; measured, `dev suites` sat waiting on them for EIGHT MINUTES after the suite
#    had finished and printed its tally, and would have waited forever, because a shim's only
#    exit is a message from a daemon that is already gone. That is the standing directive's
#    "never hung" violated by the verification tool itself -- and it is very probably what
#    the operator killed three times in the Phase 2 session as "too slow". A file redirect
#    has no such handle: WaitForExit returns when the suite process exits, and an orphan can
#    scribble into the file forever without holding anything.
#
# 2. THERE IS A DEADLINE. A suite that genuinely wedges must end the run, not the day.
#
# 3. A SUITE THAT DID NOT REPORT IS A FAILURE, not a shrug (P4.4). This used to print the
#    words "no tally line" into a results column and count it as nothing, so a suite that
#    never reported was indistinguishable from a green one. It was hiding TWO suites on this
#    very run: m0 has never printed a tally at all, and ui-use died in its own `finally`
#    (a NativeCommandError from `concierge-stop` under $ErrorActionPreference='Stop'), so its
#    74 checks were computed, discarded, and reported as fine. Whatever else a suite does, it
#    must say how many checks it ran and how many failed -- and be believed only then.
#
# Split into START and COMPLETE so the sequential and the parallel runner are the SAME code
# reaching the SAME verdict. A second copy of "did this suite pass?" is a second answer, and
# the one that gets read would be whichever happened to run.
# $file overrides where the suite is read from. `dev prove` runs a suite out of a throwaway
# worktree of HEAD, and it MUST come through here rather than invoking powershell itself --
# see the note on the pipe below, which prove learned the hard way after Run-Suite was fixed
# and prove was not.
function Start-Suite([string]$name, [string]$file = '') {
    $f = if ($file) { $file } else { "$repo\tests\$name-acceptance.ps1" }
    if (-not (Test-Path $f)) { Abort "no suite '$name'" "one of: $((AllSuites) -join ', ')" }
    $so = "$log.$name.out"; $se = "$log.$name.err"
    Remove-Item $so, $se -Force -ErrorAction SilentlyContinue

    # ONE SANDBOX PER SUITE RUN, created here and handed to the child, so that when the child
    # is finished this runner can clean up EXACTLY what that child started -- see
    # Complete-Suite, and tests\_workspace.ps1 Use-SuiteTemp for why this is a path and never
    # a process name.
    #
    # NOT Start-Process -Environment (PowerShell 7.4+, and this repo is 5.1): the variable is
    # set on the parent immediately before the child is created, which is enough because a
    # child snapshots the environment at creation. Concurrent starts are therefore still
    # correct -- set, start, set, start -- and the parent's value is put back afterwards so
    # nothing else in this process sees a stale one.
    # SHORT on purpose: `dsb-<6hex>`, not `dodona-suite-<name>-<8hex>`. Everything a suite makes
    # now nests one level deeper, and the first version added ~24 characters to every path
    # inside it. Windows MAX_PATH is a real margin here (CLAUDE.md 5.2), and it bit immediately
    # in a subtler way: a longer path pushed a word of `dodona.exe`'s stderr onto a wrapped
    # line and broke a regex in workspace-acceptance that had matched for months. The suite name
    # goes in a marker file instead, where it costs no path length.
    $sandbox = Join-Path $env:TEMP ("dsb-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $sandbox | Out-Null
    Set-Content -Path (Join-Path $sandbox '_suite.txt') -Value $name -Encoding ascii
    $savedSandbox = $env:DODONA_TEST_SANDBOX
    $env:DODONA_TEST_SANDBOX = $sandbox
    try {
        $p = Start-Process -FilePath 'powershell' `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $f) `
            -RedirectStandardOutput $so -RedirectStandardError $se -NoNewWindow -PassThru
    }
    finally {
        if ($null -eq $savedSandbox) { Remove-Item env:DODONA_TEST_SANDBOX -ErrorAction SilentlyContinue }
        else { $env:DODONA_TEST_SANDBOX = $savedSandbox }
    }
    # Touch .Handle BEFORE waiting, or .ExitCode reads back EMPTY afterwards -- the trap the
    # I2 row in Do-Gate carries a comment about, which once reported two successful builds as
    # a failure. It has to happen at start, not after the wait.
    $null = $p.Handle
    [pscustomobject]@{ Name = $name; Proc = $p; Out = $so; Err = $se; Sandbox = $sandbox; Sw = [System.Diagnostics.Stopwatch]::StartNew() }
}

function Complete-Suite($h, [int]$timeoutSec = 420) {
    $timedOut = -not $h.Proc.WaitForExit($timeoutSec * 1000)
    if ($timedOut) { try { $h.Proc.Kill() } catch { } ; $null = $h.Proc.WaitForExit(5000) }
    $h.Sw.Stop()
    # THE PROCESS'S OWN LIFETIME, not this stopwatch's. Complete-Suite is called in START
    # order, so under the parallel runner a suite that finished in 12s is not "stopped" until
    # every handle before it has been waited on -- and the first parallel run printed
    # compression, brain, concierge and publish as 46.8s each, which is ui-use's time, not
    # theirs. A table that misreports which suite is slow sends the next de-sleeping session
    # at the wrong file.
    $elapsed = $h.Sw.Elapsed
    try { if (-not $timedOut) { $elapsed = $h.Proc.ExitTime - $h.Proc.StartTime } } catch { }
    $code = try { $h.Proc.ExitCode } catch { $null }

    $o = @()
    foreach ($file in @($h.Out, $h.Err)) { if (Test-Path $file) { $o += @(Get-Content $file -ErrorAction SilentlyContinue) } }
    Add-Content -Path $log -Value "===== $($h.Name) =====" -Encoding utf8
    Add-Content -Path $log -Value $o -Encoding utf8
    Remove-Item $h.Out, $h.Err -Force -ErrorAction SilentlyContinue

    # THE SUITE CLEANS UP AFTER ITSELF, whatever it did or did not manage to do in its own
    # `finally`. A .ps1 that fails to PARSE never reaches `finally` at all (CLAUDE.md 0.2), and
    # publish-acceptance leaks four shims on every successful run -- so leaving this to the
    # suites means it does not happen. Leaked shims are not idle: 78 of them turned an 87 s run
    # into 300 s, crashed m3 and reddened nine of brain's timing checks.
    #
    # SCOPED TO THE DIRECTORY THIS RUNNER MADE FOR THIS CHILD. Not by process name, and not
    # "everything under %TEMP%": a concurrent session's suites live in their own sandboxes and
    # must survive untouched, which is the same reason `stop-all` was deleted out of `dev build`
    # (P0.2). Nothing outside this one directory can be reached from here.
    # NO SETTLE WAIT HERE, and that is a correction. A previous version of this paused up to 2 s
    # before counting, on the theory that `stop-daemon` returns before the process is gone and the
    # reaper was catching daemons mid-exit. That theory was never observed: every process this
    # reaper has ever NAMED was a DodonaShim, and the one publish-acceptance leaves is a real
    # orphan -- its `apnoprov` section runs a daemon with autostart CLEARED on purpose, whose
    # warm-up spawns utility lanes, and those shims correctly outlive the daemon (that is the
    # design) with a 30-minute lease that has not expired. The suite cleans them up itself now.
    # A wait would have hidden nothing and cost 2 s x every suite for a guess.
    $leaked = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $pp = $null
            try { $pp = $_.Path } catch { }
            $pp -and $pp.StartsWith($h.Sandbox, [StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($lp in $leaked) { try { Stop-Process -Id $lp.Id -Force -ErrorAction Stop } catch { } }
    if ($leaked.Count -gt 0) {
        # NAMED, not just counted -- and naming it is what corrected the record. A bare integer let
        # two contradictory stories stand: the plan said publish-acceptance leaks four DodonaShim
        # every run, and a later session "corrected" that to a `dodona` daemon caught mid-exit,
        # having grepped for `lane-start`, found none, and concluded the suite starts no lanes.
        # It does: its `apnoprov` section clears DODONA_NO_AUTOSTART deliberately, and that
        # daemon's warm-up spawns the utility lanes. The plan was right and the correction was
        # wrong. The name in the log is the only reason anyone can tell.
        Add-Content -Path $log -Value "$($h.Name): reaped $($leaked.Count) leaked process(es) from its sandbox" -Encoding utf8
        foreach ($lp in $leaked) {
            $lpath = '?'
            try { $lpath = $lp.Path } catch { }
            Add-Content -Path $log -Value "    leaked: $($lp.ProcessName) pid $($lp.Id) -- $lpath" -Encoding utf8
        }
    }
    Remove-Item $h.Sandbox -Recurse -Force -ErrorAction SilentlyContinue

    $fails = @($o | Select-String -Pattern ': FAIL' | ForEach-Object { $_.Line.Trim() })
    $tally = ($o | Select-String -Pattern '^\d+ checks,' | Select-Object -Last 1)
    $reaped = $leaked.Count

    # Structural faults: the suite did not run, did not finish, or did not report. Counted
    # like failed checks by every caller, because that is what they are.
    $problems = @()
    if ($timedOut) { $problems += "TIMED OUT after ${timeoutSec}s -- killed" }
    if (-not $tally) { $problems += "NO TALLY: the suite never reported '<N> checks, <M> failed' (exit $code) -- it crashed, or it does not print one" }
    elseif ($code -ne 0 -and $fails.Count -eq 0) { $problems += "reported clean but exited $code -- something failed after the tally" }

    # A SUITE THAT THREW AND THEN REPORTED CLEAN. Measured 2026-08-19, and it is the worst shape
    # a false green comes in:
    #
    #   ForEach-Object : Cannot convert value "8`n7`n6" to type "System.Int32"
    #   ...
    #   ui-use: 115 checks, 0 failed
    #
    # An unhandled error tore out of ui-use's try block, SIX checks after it never ran, the
    # `finally` still wrote the tally over whatever had been computed so far, and exit was 0.
    # Every existing structural fault missed it: there WAS a tally, the exit code WAS 0, and no
    # line said FAIL. The suite reported a clean run of 115 checks and it was a clean run of 115
    # checks -- out of 121. Nothing anywhere knows how many a suite should have run, so the count
    # cannot be the guard.
    #
    # The error record itself is the guard. `FullyQualifiedErrorId` appears in PowerShell's error
    # format and nowhere else; a suite that emits one has hit something it did not handle,
    # whether that is a bad cast or the NativeCommandError trap of CLAUDE.md 0.2 -- and both of
    # those have now produced a believed green in this repo. The suites are expected to be silent
    # of them: a diagnostic a suite MEANS to print goes through Check with a detail string.
    #
    # This is deliberately about the RUNNER rather than the twelve suites: a rule each suite must
    # remember is a rule eleven of them will eventually not (D-6, and 0.3's "documenting instead
    # of fixing").
    #
    # FALSE-RED RISK, measured rather than assumed: across a full `dev gate` the eleven GREEN
    # suites emitted none of these, so a suite that is behaving does not trip it. The only suite
    # that did was m1, which was already red on both HEAD and this branch for its own reason.
    # If a suite ever needs to capture a native command's stderr deliberately, it must do so
    # under $ErrorActionPreference='Continue' (CLAUDE.md 0.2) -- which produces no error record,
    # so the rule and the correct technique agree.
    $threw = @($o | Select-String -Pattern 'FullyQualifiedErrorId' | ForEach-Object { $_.Line.Trim() })
    if ($threw.Count -gt 0) {
        $problems += "UNHANDLED ERROR: the suite emitted $($threw.Count) PowerShell error record(s). If one tore out of its try block, the checks after it never ran and the tally counts only what did. First: $($threw[0])"
    }

    [pscustomobject]@{
        Name     = $h.Name
        Fails    = $fails
        Problems = $problems
        Seconds  = [math]::Round($elapsed.TotalSeconds, 1)
        Exit     = $code
        Tally    = if ($tally) { $tally.Line.Trim() } else { 'NO TALLY LINE' }
        Reaped   = $reaped
        # The raw lines, for the one caller that needs a SPECIFIC check rather than a verdict:
        # `dev prove` has to find "<check>: PASS|FAIL" and judge that one line.
        Output   = $o
    }
}

function Run-Suite([string]$name, [int]$timeoutSec = 420) {
    if ((UnitSuites) -contains $name) { return Run-Unit -Name $name }
    Complete-Suite (Start-Suite $name) $timeoutSec
}

# What must NOT share the machine, named with the reason and checked against the code -- never
# "to be safe", because every second of caution here is paid on every gate forever.
#
# THE RULE IS: only one thing may COMPILE at a time. A compile writes src\<proj>\obj\ and
# src\<proj>\bin\, and every suite copies its binaries out of src\...\bin at startup
# (tests\_workspace.ps1 Use-TestBinaries). Exactly one entry in the set compiles:
#
#   unit  is `dotnet test`, which builds Dodona.Tests AND its ProjectReference to Dodona --
#         straight into src\Dodona\bin. So it runs alone.
#
#   m1    CAME OFF THIS LIST on 2026-08-21 (issue #4), MEASURED. Keeping the history because
#         the reason it was here is half still true.
#
#         It was here because: alone it was green 3/3 in 8-9 s, and beside m4's real build it
#         failed `gate_denies_outside_claim` 3 times in 4 and took 30 s, because
#         `dodona gate-hook` returned EMPTY -- no deny, and no `.dodona-bypass.log` either --
#         for longer than the check's 20 s retry. Three hypotheses were tested and all three
#         were wrong: not the fail-open-on-pipe-error path (no log), not PowerShell failing to
#         deliver stdin to `cmd /c` (probed 60 times under load, 60/60 delivered).
#
#         THE PART THAT IS NOW FALSE: that comment reasoned "empty output with no bypass log
#         can only be one of GateHook's three SILENT `return 0` paths (unreadable stdin,
#         unparseable stdin, or no file_path)". All three of those DENY now, out loud, and R3's
#         byte-count diagnostic records what they saw. The failure signature this entry was
#         written about is therefore no longer producible: `gate-hook` cannot go quiet and
#         allow. Issue #4 also found and fixed the one unchecked allow that was still in there
#         (an unreadable `--ticket` beside a readable `--lane`), so the safety-model question
#         this comment ended on -- should layer 1 fail closed -- is answered: it does.
#
#         THE PART THAT IS STILL TRUE: the intermittent was never root-caused. It has simply
#         not been seen since, which is not the same thing.
#
#         MEASURED BEFORE REMOVING IT, SEVEN consecutive `dev gate` runs with m1 in the wave:
#         135 checks / 0 failed EVERY time, at 39.9, 40.1, 38.1, 47.8, 49.3, 48.9 and 51.2 s.
#         Solo it is 48 s serialized in FRONT of the wave, so removing it buys ~40 s.
#
#         READ THE REST OF THAT MEASUREMENT HONESTLY: three of those seven gates went red, and
#         not once on m1. Run 3 on `m3:approve_unblocks_lane` -- a `Wait-Until` narrower than
#         the three things the check then asserted, so a dump landed between the unblock and its
#         receipt; a real bug, fixed in the same commit. Run 4 on `workspace` and `m2`, run 6 on
#         `m2:an_extended_claim_leaves_nothing_undeclared` (empty detail: the store row was read
#         before it was written) -- every one of them green alone minutes later. That is issue
#         #3's picture and #3's rule: read a red inside a wave as a machine reading until it
#         reproduces alone. m2's is the same shape as m3's and is NOT fixed here; it wants its
#         own ticket rather than a drive-by.
#
#         AND THE WALL CLOCK IS THE REAL FINDING (issue #1). Across those seven runs it moved
#         258 -> 312 s against a 300 s budget, on one machine, on one commit, purely with how
#         busy the machine was: ui-use alone ranged 94.7-118.3 s and brain 72.8-92.6 s. I7 was
#         breached once, at 312.5 s. Putting m1 back on this list would add ~40 s to every one
#         of those numbers, which is the argument FOR this change and not against it. Do not
#         raise the budget to cover the spread; that is #1's whole subject. Solo it costs 48 s serialized in front of the wave -- the old "~8 s" in this
#         comment and in CLAUDE.md had gone stale by 6x as m1 grew to 135 checks, and a stale
#         number is what CLAUDE.md 1 has a whole section about. If it reddens in a wave again,
#         put it back HERE WITH THE NEW MEASUREMENT and read `.dodona-bypass.log` first.
#
#         IT REDDENED, ONCE IN FIVE, AND IT IS STILL NOT ON THIS LIST -- the measurement, and
#         the reason for departing from the line directly above it, both recorded here because
#         a decision taken silently is one the next session re-argues. 2026-08-21, the ui-use
#         split (issue #2), five consecutive `dev gate` runs on a clean machine:
#
#             m1 in the wave   0 failed / RED on 2 / 0 / 0 / 0 failed, at 46.7, 70.4, 44.5,
#                              43.8 and 44.4 s. Alone, minutes later: 135 checks, 0 failed,
#                              38.6 s.
#             the two reds     `say_answers_during_a_land` (error: lane 2 not connected) and
#                              `a_promoted_lane_is_re_briefed_as_a_ticket_lane` (detail []),
#                              both downstream of a wait that ran out under load. Neither is
#                              an assertion that is wrong; both are the machine reading.
#
#         WHY NOT MOVE IT ANYWAY. Because it is not m1's turn -- it is issue #3's, and treating
#         one instance of a general phenomenon as an m1 fact is exactly the wrong conclusion
#         CLAUDE.md 3.2 records somebody drawing from their own leaked shims. In the SAME five
#         runs `m4` produced NO TALLY LINE once (below) and was green alone; CLAUDE.md's own
#         entry above records three of seven gates red at the previous commit, never on m1.
#         Moving m1 here costs ~44 s serialized, which is the entire wall-clock win of the
#         split, to buy nothing that would have made either of those two runs green.
#
#         WHAT WOULD CHANGE THIS: m1 red in a wave TWICE in five, or red on a wave where
#         nothing else was. Then it is m1, and it comes back here with that measurement.
#
# m4 PRODUCED NO TALLY LINE ONCE IN THOSE FIVE GATES, AND IS NOT ON THIS LIST EITHER. Run 4,
#         2026-08-21: exit 2, stdout completely EMPTY, 25.3 s, and five processes left alive in
#         its sandbox (a `dodona` daemon, a DodonaFakeAgent and three DodonaShim) -- so it died
#         somewhere its `finally` never reached, which is why the cleanup never ran. Alone,
#         minutes later: 43 checks, 0 failed, 27.2 s; and green in the four other gates at 39.2,
#         30.8, 33.8 and 32.4 s.
#
#         WORTH KNOWING WHICHEVER WAY #3 GOES: this is a DIFFERENT signature from a red check.
#         The suite vanished rather than failed, and the only reason anybody knows is P4.4 --
#         `Run-Suite` treating a missing tally as a FAILURE. Before P4.4 this run would have
#         been a silent green. m4 is the one suite in the wave that runs a REAL build, and the
#         wave got denser when ui-use became four suites, so it is a plausible new pressure and
#         is recorded as such rather than as a diagnosis. It has not been reproduced.
#
# m4 IS DELIBERATELY NOT ON THIS LIST, and RECOVERY-PHASES P4.3 says it should be ("its
# internal publish builds the tree's own obj/"). That is half right, and the half it gets
# wrong is the half that matters: publish passes -p:BaseOutputPath=<temp>\ per project
# (src/Dodona/Program.cs, the `publish` branch, with the comment "Only bin is redirected: obj
# must stay put"), so the BIN output goes to a scratch directory and never to src\...\bin.
# Only obj\ stays in the tree -- and obj\ is contended by another COMPILE, which is `unit` and
# nothing else. Measured 2026-08-19: m4 inside the parallel wave is green, and it takes 28 s
# off the wall clock, which is the difference between a 77 s gate and a 49 s one.
#
# Everything else is isolated by construction, which is what makes P4.3 possible at all:
# Use-IsolatedDodonaHome gives each suite a GUID temp DODONA_HOME (registry, stores,
# shim-info, neutral cwd); Instance.Scoped() hashes that home into the concierge and shell
# ids, so two suites cannot collide on a pipe; every root is a GUID temp directory; and every
# UI launch carries --test-window, so it renders off-screen and never takes focus.
#
# ui-use IS NOT ON THIS LIST BECAUSE ui-use NO LONGER EXISTS. Split 2026-08-21 (issue #2)
#         into `ui-grid`, `ui-shell`, `ui-ask` and `ui-wake`, at the four fixture boundaries
#         it already had -- each section already stood up its own daemon and its own window.
#         ALL FOUR RUN IN THE WAVE, and that is a measurement, not a preference.
#
#         WHY IT WAS HERE: it joined on 2026-08-19 after Phase 2 made `workspace` heavier. In
#         one wave the gate gave ui-use 177.6s with 4 FAILED, and 64.4s with 0 failed alone,
#         on a machine with nothing leaked. The four reds were grid-tile counts and a
#         close-button interaction -- missed UI interactions -- and its failures CASCADE: two
#         missed interactions become six red checks, one problem arriving looking like six.
#         Solo isolated the one sensitive suite instead of slowing all twelve.
#
#         WHAT THE SPLIT MEASURED. Three full `dev gate` runs, clean machine (`live app
#         before: 0`, no leaked test processes), 2026-08-21, all sixteen suites:
#
#             whole gate   216.4s / 232.6s / 232.9s   vs 257.4s and 272.2s with the monolith
#             ui-grid       59.8 /  48.3 /  59.6      vs ui-use 87.5-105.7s, 18s of variance
#             ui-ask        30.2 /  31.0 /  29.1         on a completely idle machine
#             ui-shell      20.1 /  18.5 /  21.8
#             ui-wake       22.0 /  21.4 /  21.9
#
#         Zero red checks in any of the four across those three runs. That is the thing that
#         had to be true before they could stay in the wave, and it is what to re-measure
#         before moving any of them.
#
#         PUTTING THE FOUR PIECES HERE INSTEAD WOULD BE SLOWER THAN THE MONOLITH WAS, and it
#         is the trap this whole change turns on: four solo pieces are four fixture setups
#         where there was one, and each one copies the whole build into its own DODONA_HOME.
#         Measured directly rather than argued -- `dev test ui-grid ui-shell ui-ask ui-wake
#         --sequential`: 62.5 + 18.7 + 31.4 + 21.7 = 134.3s, against 88.8s for ui-use alone.
#         Splitting a suite only pays if the pieces run CONCURRENTLY. If one of them ever has
#         to come back here, it costs far more than its share of the monolith did, and the
#         other three should stay in the wave rather than follow it out of sympathy.
# WHAT A PROCESS START COSTS ON THIS MACHINE -- measured 2026-08-22, idle, at 5151640, and the
# reason it lives beside SoloSuites is that every argument on this list is about process and
# window creation rather than CPU (22 cores, never bound). Numbers, so the next person argues
# from one instead of from a hunch. Probe: `dodona version --json`, the one verb that writes
# nothing on any path, launched from copied binaries in an isolated DODONA_HOME.
#
#     WARM  same path, x30            median  77 ms
#     COLD  fresh path each time, x15 median 299 ms      <- +222 ms, 3.9x, ZERO concurrency
#     CONC  warm paths, k=1  x20      median  82 ms
#           k=3                       median  92 ms      (+12 %)
#           k=6                       median 111 ms      (+35 %)
#           k=12                      median 129 ms      (+56 %)
#
# TWO SEPARATE EFFECTS, and conflating them is how this gets misread. The 222 ms is a FIRST-TOUCH
# tax on a binary the machine has never executed from that path -- Defender real-time protection
# and behaviour monitoring are both on here -- and it is paid on an idle machine with nothing else
# running, so it is not contention at all. Every suite copies the four build outputs into a fresh
# `$DODONA_HOME\bin` (`Use-TestBinaries`), so every suite pays it once per run per executable: real,
# but tens of seconds across a full gate, not ninety. The concurrency curve is the contention, it
# is SUB-LINEAR, and +47 ms on an 80 ms operation does not by itself blow a 20-second wait.
#
# SO NEITHER NUMBER EXPLAINS ISSUE #3 ON ITS OWN, and that is the finding. What they do is rule
# out "process starts get catastrophically slower in a crowd" as the whole story, and put the
# remaining suspicion on what a wave LEAVES BEHIND rather than on what runs beside it -- which is
# where the `voice`-alone datapoint already pointed. `Wait-Until`'s grace re-check (see
# tests\_workspace.ps1) is what the next sighting needs to carry: slow, or stuck.
function SoloSuites { , @('unit', 'ui-unit', 'voice') }

# THE TEST PROJECTS, addressed by a suite name like everything else. Two of them since W3.0:
# `unit` is tests\Dodona.Tests (net8.0) and `ui-unit` is tests\Dodona.Ui.Tests
# (net8.0-windows). The second exists because a net8.0 test assembly CANNOT LOAD DodonaUi at
# all -- and FakeRecognizer, Poses and RecordingTransport all live in src\DodonaUi, so without
# it the double ledger's reflection rung covers one of the four double-bearing assemblies and
# `dev prove --with` cannot redden a UI-side test at all (plan W3.0, findings 1 and 16).
#
# `ui-unit` is DELIBERATELY NOT IN AllSuites. It is reachable by name (`dev test ui-unit`) and
# by a `ui-unit:` proof pair; putting it in the default set is what W4 does when it has
# something to say. Widening the gate is not a side effect anybody asked for (CLAUDE.md 0.1).
# Both are SOLO for SoloSuites' own stated reason: `dotnet test` compiles into src\...\bin,
# and every acceptance suite copies its binaries out of there.
function UnitSuites { , @('unit', 'ui-unit') }

function UnitProject([string]$name) {
    switch ($name) {
        'unit' { 'tests\Dodona.Tests\Dodona.Tests.csproj' }
        'ui-unit' { 'tests\Dodona.Ui.Tests\Dodona.Ui.Tests.csproj' }
        default { '' }
    }
}

# `voice` joined this list on 2026-08-20, MEASURED rather than assumed. It went into the wave
# first, on the reasoning that m3 is also a window suite and runs there. The gate then failed on
# `brain:the_brain_cap_refuses_a_new_project` and `m2:unrouted_fallback_is_announced` -- two
# suites the dictation change does not touch -- while every one of the ten assertions passed and
# the leaked-process count was zero before the run. Both went green when run alone (brain 63/0,
# m2 14/0), so it was contention, not code.
#
# That is the same failure mode this file already records for ui-use at concurrency 5: the
# contention is windows and process starts, not CPU. `voice` starts a daemon and FOUR windows --
# one, then three more for the reopen-persistence and forced-failure checks -- which is a lot of
# window creation to add to a wave of three. It costs ~15s serialized and buys a gate that means
# something. If someone later splits its window restarts, try it in the wave again and put the
# measurement in the commit.

# Longest first. With a concurrency cap, start order decides the wall clock: begin the 45
# second suite last and it finishes 45 seconds after everything else already has. This list is
# a SCHEDULING HINT and nothing else -- an unknown name just sorts to the end, and the set that
# runs is decided entirely by the caller.
#
# Measured 2026-08-19, each suite alone: ui-use 42.5, m4 28.4, publish ~30, brain 23.4,
# m3 16.6, workspace 13.8, compression 11.7, concierge 11.1, m1 7.7, m2 7.7, m0 7.0.
function SuiteOrderHint {
    , @('brain', 'workspace', 'ui-grid', 'ui-ask', 'publish', 'm4', 'voice', 'm1', 'ui-shell', 'ui-wake',
        'm3', 'compression', 'concierge', 'm2', 'm0')
}

# HOW MANY AT ONCE. THREE. The number is measured, and it was 5 until 5 was shown to be wrong.
#
# The contention is not CPU -- this is a 22-core machine that never came close to bound. Each
# suite starts a daemon, one to four shims, a WPF window and a python process per store query,
# and the WPF/UIA side serializes on the desktop.
#
# ALL ELEVEN AT ONCE was tried first and is worse in both directions: ui-use went from 42.5s
# alone to 70.6s and went RED (`second_sentence_reuses_the_lane` saw two lanes where there must
# be one).
#
# FIVE looked right for several runs and then was not. ui-use in a five-wide wave measured
# 61.4s GREEN, then 149.1s and 118.6s RED, on a quiet machine with no strays and nothing of the
# operator's running. Its failures cascade, which is what makes it expensive: the input box does
# not grow, or the close button does not stop a lane, and then every tile count after that is
# off by one and three collapse checks time out. Six red checks from two missed interactions.
#
# WHAT IT IS NOT, each ruled out by measurement rather than argument:
#   * not publish-acceptance's leaked shims -- `ui-use publish` together: green, 62.5s
#   * not contention with the other window/UIA suites -- `ui-use m3 brain compression`: green,
#     64.4s
#   * not m4's real three-project build -- `ui-use m4`: green, 67.6s
#   * not the machine being dirty -- the red runs had 0 strays and 0 live app processes
# Pairwise it never reproduces. It needs the full rolling wave, where ui-use spends its whole
# 60-70s life beside a CHANGING set of four companions, about nine suites over its lifetime.
#
# THREE is green and repeatable: two consecutive full runs at 93.1s and 93.3s, all twelve
# suites, 0 failed. It costs ~13s against a five-wide run that WORKS, and buys back the runs
# that do not. A gate that is red one time in three for reasons unrelated to the change is one
# people learn to re-run instead of read, which is the same disease as a gate that is always
# green -- so the slower honest number wins.
#
# THE ROOT CAUSE IS STILL NOT ESTABLISHED, and this comment says so rather than implying
# otherwise. What was known: ui-use was reliable with two companions and unreliable with four,
# and it was the only suite in the set driving a real window through UI Automation for over a
# minute.
#
# THE MONOLITH IS GONE (2026-08-21, issue #2) and THREE IS STILL THE NUMBER. It really was four
# suites wearing one name, and it is now four suites: ui-grid, ui-shell, ui-ask, ui-wake. Three
# full gates with all four in the wave were green in all four, 216.4-232.9s against 257.4-272.2s
# before -- so the split bought wall clock and stopped one 89-second suite setting the pace.
# What it did NOT do is explain the contention: in one of those three runs `m1` went red on two
# checks inside the wave and green alone minutes later (135 checks, 0 failed, 38.6s), which is
# the same signature one suite along. Do not read the split as a fix for that; it is issue #3,
# it is still open, and a denser wave is if anything more likely to provoke it.
#
# DODONA_TEST_CONCURRENCY overrides it, for a machine unlike this one -- and 1 is the same
# thing as `dev suites --sequential`.
function SuiteConcurrency {
    $v = $env:DODONA_TEST_CONCURRENCY
    if ($v -and [int]::TryParse($v, [ref]$null) -and [int]$v -ge 1) { return [int]$v }
    3
}

# A SHORT, STABLE KEY FOR ONE WORKING TREE. Six hex of SHA-256 over the canonical lowercased
# path: stable across runs (so a cache keyed on it is reused), distinct per worktree (so two lanes
# cannot collide), and SHORT because the things keyed on it live under %TEMP% and a Windows
# MAX_PATH margin is not theoretical in this repo (CLAUDE.md 5.2).
function TreeKey([string]$path) {
    $full = ([System.IO.Path]::GetFullPath($path)).TrimEnd('\').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($full)) }
    finally { $sha.Dispose() }
    -join @($bytes[0..2] | ForEach-Object { $_.ToString('x2') })
}

# ---------------------------------------------------------------- is the build the code?

# THE SUITES RUN src\*\bin\Release, NOT YOUR EDIT -- and until 2026-08-19 nothing checked.
# Every suite copies the four binaries out of the build output into its own DODONA_HOME
# (Use-TestBinaries, tests\_workspace.ps1) and NONE of `test`, `suites` or `gate` ever compiled
# anything. So an edited-but-unbuilt tree was verified by running the PREVIOUS binary, and
# reported green. That is a false-green GENERATOR: it does not fail once, it lies every time.
#
# Measured (P1.5, found while proving Phase 0c): the one `psi.Environment` line that tells a lane
# agent its workspace was deleted from Daemon.cs and `dev test m0` reported "26 checks, 0 failed"
# -- a clean green against a defect that was present in the source. `dev build` then the same
# command reported 1 failed. `dev suites` had the identical hole. And `dev gate` was the WORST of
# the three, because this project treats it as the merge authority: its two builds (I2) run in
# detached worktrees of HEAD under %TEMP%, which is not a build of the tree being gated at all,
# and its suite phase read the same stale bin\Release as everything else.
#
# IT REFUSES; IT DOES NOT BUILD. `dev test unit` is one second by the operator's explicit
# requirement (CLAUDE.md 1: "ban any test that takes longer than a second or two") and a ~6 s
# incremental build in front of every invocation would end the one verification loop that is fast
# enough to use while editing -- and a loop nobody uses is how verifying became a thing to skip.
# The refusal costs ~30 ms and names the one command that clears it.
#
# PER PROJECT, and that is the load-bearing part of the design. "Is any source newer than the
# output?" asked across the whole tree is EXACTLY the question auto-publish asked for 64
# consecutive rebuilds in one afternoon (CLAUDE.md 2): the newest source spanned all four
# projects while the image was ONE of them, so editing src\DodonaUi\MainWindow.xaml.cs left a
# condition that could never be satisfied. Here each project is compared against its OWN
# assembly, which MSBuild rewrites whenever that project's own sources change -- so a DodonaUi
# edit can never accuse Dodona of being stale, and the refusal cannot get stuck on.
#
# WHAT COUNTS AS A SOURCE: what the compiler reads. .cs, .csproj, .xaml, .resx, plus the two
# shared root files whose mtime is folded into every project because a change to either rebuilds
# all four. Deliberately NOT tests\*.ps1 -- a suite script is not compiled into anything, and a
# refusal that fired every time somebody edited a check would be worked around within the hour,
# which leaves you worse off than having no refusal at all (CLAUDE.md 0.3).
function StaleProjects {
    $projects = @(
        @{ Name = 'Dodona'; Out = 'src\Dodona\bin\Release\net8.0\Dodona.dll' }
        @{ Name = 'DodonaShim'; Out = 'src\DodonaShim\bin\Release\net8.0\DodonaShim.dll' }
        @{ Name = 'DodonaFakeAgent'; Out = 'src\DodonaFakeAgent\bin\Release\net8.0\DodonaFakeAgent.dll' }
        @{ Name = 'DodonaUi'; Out = 'src\DodonaUi\bin\Release\net8.0-windows\DodonaUi.dll' }
    )
    # The two files all four projects compile against. Compared as part of every project's source
    # set rather than once on their own, because either one changing rebuilds everything.
    $shared = [datetime]::MinValue
    foreach ($f in @('Dodona.sln', 'Directory.Build.props')) {
        $sp = Join-Path $repo $f
        if (Test-Path $sp) {
            $st = [System.IO.File]::GetLastWriteTimeUtc($sp)
            if ($st -gt $shared) { $shared = $st }
        }
    }

    # .NET enumeration, not Get-ChildItem -Recurse: measured 25 ms warm for all four projects,
    # against a one-second budget it must not eat.
    $stale = @()
    foreach ($proj in $projects) {
        $srcRoot = Join-Path $repo "src\$($proj.Name)"
        if (-not (Test-Path $srcRoot)) { continue }
        $newest = $shared
        $newestFile = ''
        foreach ($pat in @('*.cs', '*.csproj', '*.xaml', '*.resx')) {
            foreach ($f in [System.IO.Directory]::EnumerateFiles($srcRoot, $pat, 'AllDirectories')) {
                # obj\ holds generated .cs the build itself writes (the XAML .g.cs, AssemblyInfo),
                # and bin\ IS the output. Comparing either against the output compares a build to
                # itself, and would go stale-forever or never, depending on write order.
                if ($f -match '\\(bin|obj)\\') { continue }
                $t = [System.IO.File]::GetLastWriteTimeUtc($f)
                if ($t -gt $newest) { $newest = $t; $newestFile = $f }
            }
        }
        $outFile = Join-Path $repo $proj.Out
        if (-not (Test-Path $outFile)) {
            $stale += [pscustomobject]@{ Name = $proj.Name; Why = 'has never been built'; Source = '' }
            continue
        }
        if ([System.IO.File]::GetLastWriteTimeUtc($outFile) -lt $newest) {
            $rel = if ($newestFile) { $newestFile.Substring($repo.Length + 1) } else { 'Dodona.sln or Directory.Build.props' }
            $stale += [pscustomobject]@{ Name = $proj.Name; Why = 'its output is older than its source'; Source = $rel }
        }
    }
    # Plain return plus @() at the call site -- the Blockers/LiveApp idiom in this file. `, $stale`
    # would make @(StaleProjects).Count report 1 for an EMPTY result (CLAUDE.md 0.2), i.e. a
    # permanent refusal on a freshly built tree, which is the failure mode this must not have.
    return $stale
}

# Called from Run-Suites, so `test`, `suites` and `gate` cannot disagree about it -- one place,
# one verdict, the same rule Report-Suites follows. NOT called from `prove`: prove builds its own
# baseline in its own worktree, and it is the one verb that was already honest about this.
function Assert-FreshBuild {
    $stale = @(StaleProjects)
    if ($stale.Count -eq 0) { return }
    Say ""
    Say "STALE BUILD -- the suites would test the PREVIOUS binary, not your edit:"
    foreach ($sp in $stale) {
        if ($sp.Source) { Say "  $($sp.Name)  $($sp.Why)   newest source: $($sp.Source)" }
        else { Say "  $($sp.Name)  $($sp.Why)" }
    }
    Say "An edit that has not been built is a claim, not a change (CLAUDE.md 1). This refuses"
    Say "rather than building for you, because dev test unit is a one-second loop and a build in"
    Say "front of it would end that."
    Abort "the build output is older than the sources it would be tested against" "dev build -- powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 build   (then run this again)"
}

# Run a set of suites: the solo ones alone and first, the rest up to SuiteConcurrency at once.
#
# PS 5.1 has no ForEach-Object -Parallel and no Start-ThreadJob, so this is Start-Process per
# suite plus WaitForExit -- which is what Start-Suite already was. Not runspaces: a suite is a
# .ps1 that wants its own $env:DODONA_HOME, and a process is the only isolation boundary that
# actually gives it one.
#
# NOT Start-Process -Environment: that parameter is PowerShell 7.4+ and this repo is 5.1.
# Nothing here needs it -- each suite sets its own DODONA_HOME as its first act.
function Run-Suites([string[]]$names, [switch]$Sequential) {
    # BEFORE ANYTHING STARTS. P1.5: no verb below this line ever compiled, so a stale bin\Release
    # was tested and reported green. Here rather than in each of test/suites/gate so there is one
    # answer to "would this run test my edit?".
    Assert-FreshBuild
    $results = @()
    if ($Sequential) {
        foreach ($n in $names) { $results += Run-Suite $n }
        return $results
    }

    $solo = @($names | Where-Object { (SoloSuites) -contains $_ })
    foreach ($n in $solo) { $results += Run-Suite $n }

    $hint = SuiteOrderHint
    $rest = @($names | Where-Object { (SoloSuites) -notcontains $_ } |
        Sort-Object { $i = [array]::IndexOf($hint, $_); if ($i -lt 0) { 999 } else { $i } })
    if ($rest.Count -eq 0) { return $results }

    $cap = SuiteConcurrency
    $started = @()
    $next = 0
    while ($next -lt $rest.Count) {
        # Free a slot before taking one. HasExited is asked of the OS, so a suite that finished
        # early releases its slot immediately rather than at some poll boundary.
        while ((@($started | Where-Object { -not $_.Proc.HasExited }).Count) -ge $cap) { Start-Sleep -Milliseconds 150 }
        $started += Start-Suite $rest[$next]
        $next++
    }
    # Completed in START order, not completion order, so the printed table is stable from run
    # to run. Each row's duration is the process's own lifetime (see Complete-Suite), so
    # waiting on a slow handle first does not inflate the ones behind it.
    foreach ($h in $started) { $results += Complete-Suite $h }
    return $results
}

# The ONE place a suite result is printed and counted, so `test`, `suites` and `gate` cannot
# disagree about whether a run passed.
function Report-Suites($results, [switch]$Wide) {
    $bad = 0
    foreach ($r in $results) {
        # The reaped count is printed, never swallowed. A runner that quietly tidies up a leak
        # is a runner that hides it, and the leak is a real defect (P3): a shim should exit when
        # its child does. This keeps it visible while stopping it from poisoning the next run.
        $reap = if ($r.Reaped) { "  (reaped $($r.Reaped) leaked)" } else { '' }
        if ($Wide) { Say "$($r.Name.PadRight(12)) $($r.Tally.PadRight(24))  $($r.Seconds)s$reap" }
        else { Say "$($r.Name): $($r.Tally)  [$($r.Seconds)s]$reap" }
        foreach ($f in $r.Fails) { Say "  $f"; $bad++ }
        foreach ($x in $r.Problems) { Say "  $x"; $bad++ }
    }
    $bad
}

# The pure-logic tests (P4.5). A SUITE NAME LIKE ANY OTHER -- `dev test unit` -- so there is
# one door for verification and nobody has to know that this one happens to be dotnet test.
#
# What it is for: the parts of Dodona that are just functions -- the claim algebra, the policy
# table, repo resolution, path canonicalization, the two routing decisions made in code -- were
# reachable only through a daemon, which is a five-to-eight second floor per attempt. They are
# now reachable in under a second, which is the difference between checking and guessing while
# you edit them. It does NOT replace an acceptance suite: nothing here proves behaviour through
# the real binaries, and that is what the eleven suites are for.
#
# --nologo and minimal verbosity because the interesting output is the tally, and a `dev test`
# that prints twenty lines of MSBuild banner in front of it teaches people to skip reading it.
function Run-Unit {
    param(
        # WHICH TREE. Every caller but one wants this repo. `dev prove --with` wants its
        # throwaway worktree of HEAD -- reason 3 of Do-Prove's own refusal is that Run-Unit
        # tested the WORKING tree, which would be the change measured against itself, and
        # -Root is the whole of the answer to it.
        [string]$Root = '',
        # WHICH PROJECT, under the name the runner reports it as. See UnitSuites.
        [string]$Name = 'unit',
        [string]$Project = ''
    )
    if (-not $Root) { $Root = $repo }
    if (-not $Project) { $Project = (UnitProject $Name) }
    if (-not $Project) { Abort "no unit project called '$Name'" "one of: $((UnitSuites) -join ', ')" }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # STILL tests\unit-output, and the file is named for the project rather than for the
    # directory. dotnet test's default is tests\<project>\TestResults, which is a TRACKED
    # path, and the gate asserts a suite run dirtied nothing; tests\*-output\ is already
    # gitignored and is where all sixteen other suites write.
    $trxDir = "$Root\tests\unit-output"
    New-Item -ItemType Directory -Force $trxDir | Out-Null
    Remove-Item "$trxDir\$Name.trx" -Force -ErrorAction SilentlyContinue
    # THE TRX IS THE UNIT SUITE'S CENSUS. `dev ledger` needs per-test verdicts, and the
    # scraped "Passed: N" line is the CASE count -- a [Theory] with 8 [InlineData] rows is
    # one METHOD and eight cases, and the ledger is keyed on the method (plan 1.2/5.2).
    # --results-directory is not decoration: the default is tests\Dodona.Tests\TestResults,
    # which is a TRACKED path, and the gate asserts a suite run dirtied nothing. Every other
    # suite writes to tests\<name>-output\, which .gitignore already covers.
    $o = & dotnet test (Join-Path $Root $Project) -c Release --nologo -v q --logger "trx;LogFileName=$Name.trx" --results-directory $trxDir 2>&1
    $code = $LASTEXITCODE
    $sw.Stop()
    Add-Content -Path $log -Value "===== $Name =====" -Encoding utf8
    Add-Content -Path $log -Value $o -Encoding utf8

    # dotnet test's own summary line, turned into the same "<N> checks, <M> failed" shape every
    # other suite prints -- so Report-Suites, the gate and a human all read one format.
    $sum = ($o | Select-String -Pattern 'Passed!|Failed!|error|Passed:\s+\d+' | Select-Object -Last 1)
    $passed = 0; $failed = 0
    foreach ($line in $o) {
        if ($line -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
        if ($line -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
    }
    # THE FAILING TEST NAMES, from the line xunit actually prints: "...TestName [FAIL]".
    # And a count that DISAGREES with the tally is itself a fault. The first version of this
    # function parsed the counts, printed "54 checks, 1 failed", and returned EXIT 0 anyway,
    # because nothing was ever added to Fails -- which is precisely the P4.4 bug this phase
    # exists to remove, reintroduced inside the code that removes it. Caught by breaking
    # Claims.Covers on purpose and watching a red suite report success. The tally is the
    # authority now: if it says a test failed, this reports a failure whether or not the name
    # was matched.
    $fails = @($o | Select-String -Pattern '\[FAIL\]' | ForEach-Object { $_.Line.Trim() } | Select-Object -First 20)
    $problems = @()
    if (($passed + $failed) -eq 0) { $problems += "NO TALLY: dotnet test reported no test counts (exit $code) -- $($sum)" }
    if ($failed -gt 0 -and $fails.Count -eq 0) { $problems += "$failed test(s) failed but no [FAIL] line was matched -- see $log" }
    if ($failed -eq 0 -and $code -ne 0) { $problems += "dotnet test reported 0 failures but exited $code -- see $log" }

    [pscustomobject]@{
        Name     = $Name
        Fails    = $fails
        Problems = $problems
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Exit     = $code
        Tally    = "$($passed + $failed) checks, $failed failed"
        # The per-test census. `dev prove` judges ONE named method out of this, because a unit
        # run prints no line for a test that passed -- so there is nothing to grep, and
        # scraping [FAIL] lines could only ever see the reds. That cannot tell VACUOUS (it
        # passed) from MISSING (it never ran), and those two mean opposite things.
        Trx      = "$trxDir\$Name.trx"
    }
}

function Do-Test {
    # `--sequential` runs them one at a time. It exists because a parallel run interleaves
    # nothing useful when you are debugging ONE suite, and because a runner has to be able to
    # answer "is this failure mine, or is it the concurrency?" without editing the runner.
    $seq = @($Rest | Where-Object { $_ -eq '--sequential' }).Count -gt 0
    $names = @($Rest | Where-Object { $_ -ne '--sequential' })
    if ($names.Count -eq 0) { Abort "which suite?" "dev test m3   (one of: $((AllSuites) -join ', '))" }
    Say "== test: $($names -join ', ') =="
    $bad = Report-Suites (Run-Suites $names -Sequential:$seq)
    Say "log: $log"
    if ($bad -gt 0) { exit 1 }
}

function Do-Suites {
    $seq = @($Rest | Where-Object { $_ -eq '--sequential' }).Count -gt 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Say $(if ($seq) { "== suites: all (sequential) ==" } else { "== suites: all ($((SoloSuites) -join ', ') alone, then $((AllSuites).Count - (SoloSuites).Count) more, $(SuiteConcurrency) at a time) ==" })
    $bad = Report-Suites (Run-Suites (AllSuites) -Sequential:$seq) -Wide
    $sw.Stop()
    Say ""
    # The wall-clock number is printed BY THE RUNNER, not measured by whoever remembers to
    # wrap it in Measure-Command. I7 is an assertion about this number (see Do-Gate), and an
    # invariant nobody can read off the output is one nobody checks.
    Say ("total: {0:N1}s wall clock" -f $sw.Elapsed.TotalSeconds)
    Say $(if ($bad -eq 0) { "ALL SUITES GREEN" } else { "$bad FAILED CHECK(S)" })
    Say "log: $log"
    if ($bad -gt 0) { exit 1 }
}

# A MUTANT: a checked-in defect, and the checks it is supposed to redden (plan W3 delta 6).
#
# It is a FILE, and it is tracked, because the mutant IS the evidence. A `moved` ledger row
# says an old acceptance check and a new unit test assert the same thing; the only proof of
# that anybody can review is a single defect that reddened BOTH (plan 5.3, the paired red).
# An inline expression would leave the reviewer with two green ticks and a promise.
#
# The header is four kinds of line and nothing else:
#
#   # defect:        one sentence of prose, printed when the patch is applied
#   # expects-red:   <suite>:<check>   -- one per line, and BOTH languages belong here
#   # expects-green: <suite>:<check>   -- the over-broad-mutant detector
#
# expects-green is not decoration. A mutant that reddens half the suite proves nothing about
# the one check it was aimed at, and "it went red" is exactly as convincing either way -- the
# believed-a-green-check disease with the sign flipped. Naming a neighbour that must SURVIVE
# is what makes the red mean something.
function Read-Mutant([string]$path) {
    $defect = @(); $red = @(); $green = @()
    foreach ($line in @(Get-Content $path -ErrorAction SilentlyContinue)) {
        $t = "$line".Trim()
        # The header stops at the diff. Anything after it is the patch body, where a context
        # line copied out of a comment could otherwise read as a directive.
        if ($t -like 'diff --git *' -or $t -like '--- *' -or $t -like 'index *') { break }
        if ($t -match '^#\s*defect:\s*(.+)$') { $defect += $Matches[1].Trim() }
        elseif ($t -match '^#\s*expects-red:\s*(.+)$') { $red += $Matches[1].Trim() }
        elseif ($t -match '^#\s*expects-green:\s*(.+)$') { $green += $Matches[1].Trim() }
    }
    if ($red.Count -eq 0) {
        Abort "the mutant names no check to redden: $path" "add at least one  # expects-red: <suite>:<check>  line above the diff (plan W3 delta 6)"
    }
    [pscustomobject]@{ Path = $path; Defect = ($defect -join ' '); ExpectsRed = $red; ExpectsGreen = $green }
}

# ONE verdict about ONE named check, whichever kind of run produced it.
#
# An acceptance suite prints `<check>: PASS|FAIL` and this greps it. A unit project prints no
# line at all for a test that passed, so there is nothing to grep -- it writes a TRX instead,
# and this reads per-test outcomes out of that. The distinction the TRX buys is the one that
# matters: PASS and ABSENT are indistinguishable in console output, and they mean opposite
# things (VACUOUS = your change is not what makes it fail; MISSING = it never ran).
function Prove-Judge($run, [string]$suite, [string]$check) {
    if ($null -eq $run) { return [pscustomobject]@{ State = 'MISSING'; Text = "$suite $check" } }
    if (@($run.PSObject.Properties.Name) -contains 'Trx') {
        if (-not (Test-Path $run.Trx)) { return [pscustomobject]@{ State = 'MISSING'; Text = "$suite $check -- no TRX at $($run.Trx)" } }
        $xml = [xml](Get-Content $run.Trx -Raw)
        $rows = @(); $bad = 0
        foreach ($r in @($xml.TestRun.Results.UnitTestResult)) {
            if ($null -eq $r) { continue }
            $name = "$($r.testName)"
            # A [Theory] row's testName is the method's FQN with a parenthesised argument list
            # appended; a [Fact]'s is the bare FQN. Splitting at the FIRST '(' is safe because a
            # C# method name cannot contain one, so no argument value can be mistaken for part
            # of the name however it is spelled. Both forms are recorded VERBATIM, off this
            # machine's own TRX, in tests\ledger\README.md.
            $method = ($name -replace '\(.*$', '')
            if ($method -ne $check -and -not $method.EndsWith(".$check", [System.StringComparison]::Ordinal)) { continue }
            $rows += $name
            if ("$($r.outcome)" -ne 'Passed') { $bad++ }
        }
        if ($rows.Count -eq 0) { return [pscustomobject]@{ State = 'MISSING'; Text = "$suite $check" } }
        # A THEORY IS ONE CHECK MADE OF N ROWS, and one red row reddens the method. The count is
        # printed because "1 of 8 rows" and "8 of 8" are different findings about a mutant.
        $shape = if ($rows.Count -eq 1) { '1 case' } else { "$($rows.Count) rows" }
        if ($bad -gt 0) { return [pscustomobject]@{ State = 'FAIL'; Text = "$($check): FAIL -- $bad of $shape not passed" } }
        return [pscustomobject]@{ State = 'PASS'; Text = "$($check): PASS -- $shape" }
    }
    $line = @($run.Output | Select-String -Pattern ([regex]::Escape($check) + ':') | Select-Object -First 1)
    if ($line.Count -eq 0) { return [pscustomobject]@{ State = 'MISSING'; Text = "$suite $check" } }
    $txt = $line[0].Line.Trim()
    if ($txt -match ': FAIL') { return [pscustomobject]@{ State = 'FAIL'; Text = $txt } }
    return [pscustomobject]@{ State = 'PASS'; Text = $txt }
}

# prove: the mechanism for the second half of the 2026-08-18 lesson. A new acceptance check
# is worth nothing until it has been SEEN RED against the code it is supposed to catch. This
# builds HEAD (i.e. the tree WITHOUT your uncommitted fix) in a throwaway git worktree, runs
# the named suite there, and demands that the named check FAILS. If it passes, the check is
# vacuous -- exactly the trap that made a bad respawn fix look verified.
function Do-Prove {
    # TWO FORMS, and the second one is P7.4b -- the change that actually made proving cheap:
    #
    #   dev prove m0 shim_exits_when_its_agent_dies              one check  (unchanged)
    #   dev prove m0:check_a m0:check_b brain:check_c            many, ONE run per suite
    #
    # The insight is embarrassingly simple and it cost 46 minutes to not have: a suite run
    # prints EVERY check it ran, so judging eleven m0 checks needs ONE m0 run, not eleven. The
    # session that shipped Phase 3 ran m0 eleven separate times to read eleven lines that all
    # appear together in any single run of it. Fifteen proofs, nineteen suite runs, 46 minutes --
    # for what is two suite runs of work.
    #
    # This is why P7.4 (the build cache) was the wrong thing to reach for first, and why its
    # justification had to be measured before it could be corrected: the build was never the
    # cost. The cost was running the same failing suite over and over, each time waiting out the
    # same deadlines, to read a different line of the same output.
    # A THIRD FORM, and it is what makes a pure MOVE provable at all (plan W3):
    #
    #   dev prove --with tests\mutants\s-wire-01.patch m2:tier0_prefix_routes unit:<FQN>
    #
    # The default form judges a check against HEAD, which works while the check is NEW: HEAD
    # lacks your fix, so a check with teeth fails there. A MOVE contains no fix -- it is the
    # same assertion one layer down -- so HEAD passes it and every moved check comes back
    # VACUOUS by construction. The instrument for a move is therefore a DEFECT rather than an
    # absence: break the function on purpose and demand that the old acceptance check and the
    # new unit test go red on the SAME break.
    #
    # With no check named, the patch's own `# expects-red:` lines are the list -- so the pair
    # that has to stay in step (the defect, and what it must redden) lives in one reviewable
    # file instead of in a command somebody retypes.
    $with = ''
    $argv = @()
    $args0 = @($Rest)
    for ($i = 0; $i -lt $args0.Count; $i++) {
        if ($args0[$i] -eq '--with') {
            if ($i + 1 -ge $args0.Count) { Abort "--with needs a patch file" "dev prove --with tests\mutants\<slice>-NN.patch [<suite>:<check> ...]" }
            $with = $args0[$i + 1]
            $i++
            continue
        }
        $argv += $args0[$i]
    }

    $mutant = $null
    if ($with) {
        $wp = if ([System.IO.Path]::IsPathRooted($with)) { $with } else { Join-Path $repo $with }
        if (-not (Test-Path $wp)) {
            # NAME THE REAL CAUSE. A bash/git-bash caller typing the documented
            # `--with tests\mutants\s-wire-01.patch` has its backslashes eaten before dev.ps1
            # ever runs, and the argument arrives as `testsmutantss-wire-01.patch`. The refusal
            # was correct and unreadable; every slice after the pilot types this command.
            $hint = if ($with -notmatch '[\\/]' -and $with -match 'mutants') {
                "that path has NO separator in it -- a POSIX shell ate the backslashes. Use forward slashes: dev prove --with tests/mutants/<slice>-NN.patch"
            } else {
                "mutants are CHECKED IN at tests\mutants\<slice>-NN.patch (plan W3 delta 6)"
            }
            Abort "no patch at $wp" $hint
        }
        $mutant = Read-Mutant $wp
        if ($argv.Count -eq 0) { $argv = @($mutant.ExpectsRed) }
    }

    if ($argv.Count -lt 1) { Abort "need a suite and a check name" "dev prove m3 respawned_ticket_lane_returns_to_its_worktree  --  or  dev prove m0:check_a m0:check_b  --  or  dev prove --with <patch>" }
    $pairs = @()
    if (@($argv | Where-Object { $_ -match ':' }).Count -gt 0) {
        foreach ($a in $argv) {
            if ($a -notmatch '^([^:]+):(.+)$') { Abort "cannot read '$a' as suite:check" "mixing the one-check and many-check forms is not supported; use m0:check_a m0:check_b" }
            $pairs += [pscustomobject]@{ Suite = $Matches[1]; Check = $Matches[2] }
        }
    }
    else {
        if ($argv.Count -lt 2) { Abort "need a suite and a check name" "dev prove m3 respawned_ticket_lane_returns_to_its_worktree" }
        $pairs += [pscustomobject]@{ Suite = $argv[0]; Check = $argv[1] }
    }
    # The controls. These ride the same runs and are judged the other way up -- see Read-Mutant.
    $greens = @()
    if ($mutant) {
        foreach ($g in @($mutant.ExpectsGreen)) {
            if ($g -notmatch '^([^:]+):(.+)$') { Abort "cannot read expects-green '$g' as suite:check" "in $($mutant.Path)" }
            $greens += [pscustomobject]@{ Suite = $Matches[1]; Check = $Matches[2] }
        }
    }
    $suiteNames = @(@($pairs) + @($greens) | ForEach-Object { $_.Suite } | Sort-Object -Unique)
    foreach ($n in $suiteNames) {
        if ((UnitSuites) -notcontains $n -and -not (Test-Path "$repo\tests\$n-acceptance.ps1")) { Abort "no suite '$n'" "one of: $((AllSuites) -join ', '), ui-unit" }
    }
    # `unit` CANNOT BE PROVED HERE, and saying so is the whole fix (2026-08-19). It looked
    # supported -- CLAUDE.md's table says `prove <suite> <check>` and the loop above lets the
    # name through -- and it was broken three separate ways, each giving a WRONG answer rather
    # than no answer:
    #
    #   1. the HEAD build compiled tests\Dodona.Tests, so a unit test naming a symbol this
    #      change ADDS fails to compile and prove aborted with "HEAD does not build" -- which
    #      reads as "your baseline is broken" and, worse, took every ACCEPTANCE check in the same
    #      run down with it. That one is a real bug and is fixed below: an acceptance proof has no
    #      business compiling the unit-test project.
    #   2. Start-Suite would then Abort "no suite 'unit'" -- there is no tests\unit-acceptance.ps1.
    #   3. and if it had got past both, Run-Unit builds and tests $repo -- the WORKING TREE --
    #      so the verdict would have been the change measured against itself. A silently wrong
    #      proof is worse than no proof; it is the believed-a-green-check disease with a
    #      certificate.
    #
    # THE HONEST LIMIT: a NEW pure function cannot be failed by a HEAD that does not contain it.
    # There is nothing to compile the test against. So the substitute is the one CLAUDE.md 0.3
    # already prescribes for machine-state checks -- break the thing on purpose and watch the
    # check go red -- and it is a stronger demonstration for a pure refactor anyway, because it
    # pins the exact behaviour rather than the symbol's absence.
    #
    # ALL THREE OF THOSE ARE ANSWERED BY `--with`, and that is why the refusal is now
    # conditional rather than absolute (plan W3 delta 5):
    #   1. a HEAD that lacks your new symbol -> a MOVE adds no symbol, and the seam commit
    #      lands before the slice that uses it, so the project compiles against HEAD;
    #   2. there is no tests\unit-acceptance.ps1 -> `unit` and `ui-unit` route to Run-Unit,
    #      which is not a .ps1 suite and never was;
    #   3. Run-Unit tested the WORKING tree -> Run-Unit -Root $wt tests the worktree of HEAD
    #      with the mutant applied, which is a different tree from the one holding the fix.
    # What is NOT answered, and so is not offered: proving a unit check against a bare HEAD.
    # There is still no red to see there, only a compile error.
    $unitAsked = @($suiteNames | Where-Object { (UnitSuites) -contains $_ })
    if (-not $with -and $unitAsked.Count -gt 0) {
        Abort "the unit suites cannot be proved against a bare HEAD" (@(
            "A unit test compiles AGAINST the code it tests, so a HEAD without your new symbol",
            "cannot run it at all -- there is no red to see, only a compile error.",
            "",
            "Demonstrate it the other way round, in your own tree:",
            "  1. break the function on purpose (reverse the rung order, drop a guard)",
            "  2. dev test unit      -- the check must go RED, and you must read the failure",
            "  3. revert the break, and record what the red said in the commit message",
            "",
            "Acceptance checks (.ps1 suites) prove normally: dev prove m3:check workspace:check",
            "",
            "Or supply the DEFECT yourself, which is what a MOVE needs (plan W3):",
            "  dev prove --with tests\mutants\<slice>-NN.patch unit:<FQN> <suite>:<old_check>"
        ) -join "`n         ")
    }
    Say $(if ($pairs.Count -eq 1) { "== prove: '$($pairs[0].Check)' must FAIL against HEAD ==" }
          else { "== prove: $($pairs.Count) check(s) across $($suiteNames.Count) suite(s) must FAIL against HEAD ==" })

    # THE PATCH *IS* THE CHANGE, so a clean tree is not an error under --with -- it is the
    # normal state at step B0 of a slice, where the mutant is written before anything moves.
    if ($with) {
        Say "with: $($mutant.Path)"
    }
    else {
        $dirty = @(git -C $repo status --porcelain -- 'src' 'tests')
        if ($dirty.Count -eq 0) { Abort "src and tests are identical to HEAD, so there is no change to prove" "make the fix first, leave it uncommitted, then run prove  --  or supply a defect: dev prove --with <patch>" }
    }

    # ONE WORKTREE PER COMMIT PER LANE, KEPT (P7.4, per-lane by P1.7). This used to make a
    # GUID-named worktree, cold-build
    # the whole solution into it, and delete it again -- every single invocation. Measured on the
    # session that shipped Phase 3: nineteen proofs, all against the SAME commit, ~19 minutes
    # spent rebuilding an identical tree, and 45 % of that session sat inside `dev prove`.
    #
    # The waste is not the point. The INCENTIVE is: an expensive proof is one people batch,
    # defer, or skip, and skipped proving is the root of every believed-a-green-check incident in
    # CLAUDE.md 0.3. Making the tool cheap is the only durable way to make it used, which is the
    # same argument that took the suites from twenty minutes to ninety seconds (Phase 4).
    #
    # THERE IS NO CACHE MARKER AND NO STALENESS QUESTION, deliberately. The worktree is named for
    # the commit, so its src\ can only ever be that commit; `dotnet build` still runs every time
    # and MSBuild decides what to redo. A build stamp we maintained ourselves would be a second
    # source of truth about what is built, which is exactly the mistake auto-publish made with
    # `.built-from` (CLAUDE.md 2). Incremental is ~6 s against ~60 s cold.
    # PER LANE AS WELL AS PER COMMIT (P1.7, 2026-08-19). This was keyed on the commit ALONE, so
    # three lanes of one wave sitting at one HEAD shared one worktree, one tests\ directory that
    # each of them cleared and re-copied, and one stderr.tmp. Observed: workspace-acceptance died
    # on its FIRST command with "the process cannot access the file ... stderr.tmp" and prove
    # reported ELEVEN perfectly good checks as MISSING. That is f9aaf25's two-lanes-one-tree
    # failure reappearing inside the tool whose entire job is to verify -- and it presents as a
    # crashed suite rather than as a collision, which is why it cost a lane real time to diagnose.
    #
    # The commit still names the tree's CONTENT (nothing else could); the caller's own path now
    # names the tree's OWNER. Both properties of P7.4 survive: the tree is still reused across
    # proofs at the same commit, and it is still pruned so `git worktree list` says something true.
    $head = (git -C $repo rev-parse HEAD).Trim()
    $proveRoot = Join-Path $env:TEMP 'dodona-prove'
    New-Item -ItemType Directory -Force $proveRoot | Out-Null
    $mine = TreeKey $repo
    $keep = $mine + '-' + $head.Substring(0, 12)
    $wt = Join-Path $proveRoot $keep

    # PRUNE ALL OF MINE, AND ONLY WHAT IS ORPHANED OF ANYBODY ELSE'S.
    #
    # Deleting another lane's tree WHILE IT IS PROVING is the collision this change exists to
    # remove, so a live lane's cache is left alone even though it is not ours. A tree whose owning
    # worktree git no longer lists cannot be in use by anyone, so it goes -- which is what stops
    # this becoming one leaked directory per lane forever, the obvious failure of keying on the
    # caller. Legacy bare-<commit12> directories have no owner prefix at all and are pruned by the
    # same rule, so the old naming cleans itself up on first run.
    #
    # Prove trees are themselves registered worktrees and so appear in `worktree list`; they are
    # excluded, or a prove tree's own key could read as a live owner.
    $liveKeys = @(@(git -C $repo worktree list --porcelain) |
        Where-Object { $_ -like 'worktree *' } |
        ForEach-Object { $_.Substring(9) } |
        Where-Object { -not $_.ToLowerInvariant().StartsWith($proveRoot.ToLowerInvariant()) } |
        ForEach-Object { TreeKey $_ })
    foreach ($old in @(Get-ChildItem $proveRoot -Directory -ErrorAction SilentlyContinue)) {
        if ($old.Name -eq $keep) { continue }
        $owner = if ($old.Name -match '^([0-9a-f]{6})-') { $Matches[1] } else { '' }
        if ($owner -and $owner -ne $mine -and $liveKeys -contains $owner) {
            Say "  left another lane's prove tree alone: $($old.Name)"
            continue
        }
        git -C $repo worktree remove --force $old.FullName 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
        Remove-Item $old.FullName -Recurse -Force -ErrorAction SilentlyContinue
        Say "  pruned a prove tree with no live owner: $($old.Name)"
    }

    $reused = Test-Path (Join-Path $wt 'Dodona.sln')
    if ($reused) { Say "worktree of HEAD (reused): $wt" }
    else {
        Remove-Item $wt -Recurse -Force -ErrorAction SilentlyContinue
        Say "worktree of HEAD (new): $wt"
        git -C $repo worktree add --detach $wt HEAD 2>&1 | ForEach-Object { Say "  $_" }
        if (-not (Test-Path (Join-Path $wt 'Dodona.sln'))) { Abort "could not create a worktree of HEAD at $wt" 'a stale registration may be in the way: git worktree prune, then retry' }
    }
    try {
        # The TEST comes from your working tree (it is the new check); the CODE comes from
        # HEAD (it is the code that must fail it). That is the whole trick.
        #
        # CLEARED FIRST, now that the tree is reused: Copy-Item -Force overwrites but never
        # deletes, so a suite you renamed or removed would linger from an earlier proof and could
        # still be the one that runs. Also drops the previous proof's <suite>-output directories.
        Remove-Item "$wt\tests\*" -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item "$repo\tests\*" "$wt\tests\" -Recurse -Force

        # src\ IS RESTORED FIRST, AND UNCONDITIONALLY. This worktree is a per-commit CACHE
        # (see the pruning block above), so a previous --with run's mutation is still sitting
        # in its src\ -- and a proof judged against yesterday's defect is a WRONG answer, not
        # a missing one. Unconditional because the run that must not inherit one is the run
        # WITHOUT --with: that is the plain `dev prove`, whose whole promise is "this is
        # HEAD". Nothing else in this tree is tracked-and-modified, so it costs milliseconds.
        git -C $wt checkout HEAD -- src 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
        # ...AND THE FILES A MUTANT ADDED, which `checkout` cannot see. `git checkout HEAD -- src`
        # restores tracked content and leaves untracked content exactly where it is, so the first
        # mutant to introduce a NEW file poisoned this cached tree permanently: the next `--with`
        # died on "already exists in working directory", and a plain `dev prove` -- whose whole
        # promise is "this is HEAD" -- would have silently judged against HEAD plus somebody
        # else's new file. That is the wrong answer the paragraph above refuses to accept, one
        # `git` verb further out. Found by s-summon-01, the first mutant here to add one
        # (src\Dodona\DaemonSurface.cs, issue #13).
        git -C $wt clean -fdq -- src 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }

        if ($mutant) {
            # SRC ONLY, and the refusal is the point. A mutant that touched tests\ would be
            # editing the very checks it is supposed to redden -- reason 3 of the unit refusal
            # above in a new costume, a change measured against itself. `git apply --numstat`
            # answers "which files" using git's OWN parser, so this cannot disagree with what
            # `git apply` is about to do; a hand regex over `diff --git` lines could.
            $touched = @(& git -C $wt apply --numstat $mutant.Path 2>&1)
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in $touched) { Say "  $l" }
                Abort "that file does not read as a diff of this tree: $($mutant.Path)" "regenerate it from a tree at this commit: git diff > <patch>"
            }
            $files = @()
            foreach ($l in $touched) {
                $cols = @("$l" -split "`t")
                if ($cols.Count -ge 3) { $files += $cols[2] }
            }
            $outside = @($files | Where-Object { $_ -notmatch '^src[\\/]' })
            if ($outside.Count -gt 0) {
                Abort "the mutant touches $($outside.Count) path(s) outside src\: $($outside -join ', ')" "a mutant is a DEFECT IN THE PRODUCT; a patch that edits tests\ measures a change against itself"
            }
            if ($files.Count -eq 0) { Abort "the mutant changes nothing: $($mutant.Path)" "an empty patch reddens nothing, and every check under it would report VACUOUS" }

            $out = @(& git -C $wt apply --check $mutant.Path 2>&1)
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in $out) { Say "  $l" }
                Abort "the mutant does not apply to HEAD ($($head.Substring(0,12)))" "it was cut against a different commit -- regenerate it from this one"
            }
            $out = @(& git -C $wt apply $mutant.Path 2>&1)
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in $out) { Say "  $l" }
                Abort "git apply --check passed and git apply then failed" "see $log"
            }
            Say "mutant applied to HEAD: $($files -join ', ')"
            if ($mutant.Defect) { Say "  defect: $($mutant.Defect)" }
        }
        # THE FOUR PRODUCT PROJECTS, NOT THE SOLUTION (2026-08-19). `Dodona.sln` includes
        # tests\Dodona.Tests, and an acceptance suite has no use for it: every suite runs the
        # binaries that Use-TestBinaries copies out of src\*\bin\Release, and the unit suite
        # compiles its own project itself. Building it here only ADDED a way to fail -- a unit
        # test in your working tree that names a symbol your change adds does not compile against
        # HEAD, and prove then reported "HEAD does not build" and refused to judge the eleven
        # perfectly provable acceptance checks in the same run. The tests\ directory is copied
        # from the working tree on purpose (that is the trick); compiling part of it against HEAD
        # was never part of it.
        Say $(if ($reused) { "building HEAD (incremental) ..." } else { "building HEAD (cold, first proof at this commit) ..." })
        $projects = @('src\Dodona\Dodona.csproj', 'src\DodonaShim\DodonaShim.csproj',
                      'src\DodonaFakeAgent\DodonaFakeAgent.csproj', 'src\DodonaUi\DodonaUi.csproj')
        # ...PLUS a test project, but only when a proof actually NAMES one. The paragraph above
        # is still right that an ACCEPTANCE proof has no business compiling tests\Dodona.Tests:
        # that is what stopped one unit test naming a new symbol from taking eleven acceptance
        # proofs down with it. A `unit:` or `ui-unit:` pair changes the question -- that project
        # IS the subject, and a compile failure in it is a real "HEAD does not build" rather
        # than collateral. Each is added only for ITS OWN prefix: a UI-only proof must not pay
        # for a net8.0 compile, and a net8.0-only proof must not pay for WPF.
        foreach ($u in $unitAsked) { $projects += (UnitProject $u) }
        foreach ($proj in $projects) {
            $b = & dotnet build (Join-Path $wt $proj) -c Release 2>&1
            Add-Content -Path $log -Value $b -Encoding utf8
            if ($LASTEXITCODE -ne 0) { Abort "HEAD does not build ($proj), so it cannot be used as a baseline" "commit a buildable baseline first; see $log" }
        }

        # THROUGH Start-Suite/Complete-Suite, not `& powershell ... 2>&1`. This line was the
        # SAME HANG that was fixed in Run-Suite and missed here, which is exactly the
        # "working around the same snag twice" CLAUDE.md 0.3 forbids -- and it bit within the
        # hour: `dev prove publish no_provenance_daemon_refuses_to_guess` sat for 24 minutes
        # against a suite that had long since finished, because publish-acceptance leaks four
        # DodonaShim processes and they inherit the write end of that pipe. Worse than the
        # wait: being killed skipped this function's `finally`, so the throwaway worktree of
        # HEAD was left registered in `git worktree list`.
        #
        # One code path now reaches one verdict, and it carries the deadline with it.
        # ONE RUN PER SUITE. Solo suites stay solo (unit compiles into src\...\bin and every
        # other suite copies out of it; m1 is intermittent beside a parallel wave) -- the same
        # rules Run-Suites obeys, for the same reasons, because this worktree has one bin too.
        $runs = @{}
        # -Root $wt is the whole of reason 3's answer: the code under test is HEAD-plus-mutant
        # in the throwaway tree, never the working tree holding the change being proved.
        foreach ($n in $unitAsked) { $runs[$n] = Run-Unit -Root $wt -Name $n }
        foreach ($n in @($suiteNames | Where-Object { (SoloSuites) -contains $_ -and (UnitSuites) -notcontains $_ })) {
            $runs[$n] = Complete-Suite (Start-Suite $n "$wt\tests\$n-acceptance.ps1")
        }
        $wave = @($suiteNames | Where-Object { (SoloSuites) -notcontains $_ -and (UnitSuites) -notcontains $_ })
        $cap = SuiteConcurrency
        $started = @()
        foreach ($n in $wave) {
            while ((@($started | Where-Object { -not $_.Proc.HasExited }).Count) -ge $cap) { Start-Sleep -Milliseconds 150 }
            $started += Start-Suite $n "$wt\tests\$n-acceptance.ps1"
        }
        foreach ($h in $started) { $runs[$h.Name] = Complete-Suite $h }

        foreach ($n in $suiteNames) {
            foreach ($x in $runs[$n].Problems) { Say "  note: $n : $x" }
        }

        # A check that never RAN is not vacuous, it is unproven, and the two must not be
        # conflated: vacuous means "your change is not what makes it fail", missing means "this
        # code path was never reached", and only the second one might be a typo in the name.
        $proven = 0; $vacuous = 0; $missing = 0; $overbroad = 0
        Say ""
        foreach ($pr in $pairs) {
            $v = (Prove-Judge $runs[$pr.Suite] $pr.Suite $pr.Check)
            if ($v.State -eq 'FAIL') { Say "  PROVEN   $($v.Text)"; $proven++ }
            elseif ($v.State -eq 'PASS') { Say "  VACUOUS  $($v.Text)"; $vacuous++ }
            else { Say "  MISSING  $($v.Text) -- never ran against HEAD"; $missing++ }
        }
        # The controls, judged the other way up. A mutant that reddens a check it was not aimed
        # at is over-broad, and every red under it is worth exactly as little as a green would
        # be -- so this fails the proof rather than printing a note somebody scrolls past.
        foreach ($pr in $greens) {
            $v = (Prove-Judge $runs[$pr.Suite] $pr.Suite $pr.Check)
            if ($v.State -eq 'PASS') { Say "  CONTROL  $($v.Text)" }
            elseif ($v.State -eq 'FAIL') { Say "  OVERBROAD $($v.Text) -- declared expects-green, and the mutant reddened it"; $overbroad++ }
            else { Say "  MISSING  $($v.Text) -- declared expects-green, never ran"; $missing++ }
        }

        Say ""
        if ($missing -gt 0) {
            Say "$missing check(s) NEVER RAN. Each must exist in your working tests/ AND be reached on"
            Say "this code path -- a check behind an earlier failure in the same suite never gets there."
        }
        if ($vacuous -gt 0) {
            Say "$vacuous check(s) VACUOUS: they PASS against HEAD, so they do not test your change."
            Say "         Rewrite them before trusting them. (This is the exact trap of 2026-08-18.)"
        }
        if ($overbroad -gt 0) {
            Say "$overbroad check(s) OVER-BROAD: the mutant reddened a check it was not aimed at, so"
            Say "         nothing it reddened is evidence about the check it WAS aimed at. Narrow the defect."
        }
        if ($vacuous -eq 0 -and $missing -eq 0 -and $overbroad -eq 0) {
            $why = if ($with) { "under $(Split-Path -Leaf $mutant.Path)" } else { "without your change" }
            Say $(if ($proven -eq 1) { "PROVEN: the check fails $why, so it has teeth." }
                  else { "PROVEN: all $proven check(s) fail $why, so they have teeth." })
            if ($with -and $greens.Count -gt 0) { Say "        and $($greens.Count) declared control(s) survived it." }
        }
        else { Say "log: $log"; exit 1 }
    }
    finally {
        # THE TREE IS KEPT ON PURPOSE -- it is the cache. It costs one directory per commit, it is
        # pruned at the top of the next run, and `dev prove` is the only thing that reads it.
        # Nothing runs out of it after the suite ends: I1 is asserted over src\...\bin in THIS
        # repo, and a suite copies its binaries into its own DODONA_HOME anyway (P1.1).
        Say "kept for the next proof at this commit, by this tree: $wt"
    }
    Say "log: $log"
}

# gate: the standing pre-commit assertion (RECOVERY-PHASES section 2). It ASSERTS the
# invariants rather than describing them -- a rule that only exists as prose is a rule that
# gets skipped, which is the entire argument of CLAUDE.md 0.3.
#
# It runs the full suites itself, because both assertions below are about what a suite RUN
# leaves behind, and there is nothing to measure without one. That makes gate the slow verb
# on purpose: iterate with `dev test <suite>`, gate once before committing.
#
# IT HOLDS ONLY WHAT IS TRUE TODAY. The table in RECOVERY-PHASES section 2 has eight rows;
# Phase 1 earned two, Phase 2a two more, and Phase 2b the build-SHA row. The rest are printed as "not yet", named
# with the phase that will earn them, because a gate that silently covered a third of its
# table would be exactly the green check nobody has seen fail. When a phase earns a row it
# MOVES from that list into an assertion above -- a gate whose table drifts out of date is
# the same lie it was built to prevent.
function Do-Gate {
    # A named suite (or several) runs the gate's MACHINERY over less than everything. It
    # exists so the gate can be proven without waiting 5.5 minutes -- the same reason
    # `dev test <suite>` exists next to `dev suites`, and the same doctrine: iterate fast,
    # gate slow. It says PARTIAL on every line that could otherwise be misread as a pass,
    # because a gate that reports a full verdict after a third of the work is precisely the
    # green check nobody has seen fail (CLAUDE.md 0.3).
    $suites = if ($Rest -and $Rest.Count -gt 0) { $Rest } else { AllSuites }
    $partial = @($suites).Count -ne (AllSuites).Count
    Say $(if ($partial) { "== gate: PARTIAL ($($suites -join ', ')) -- a SELF-TEST of the gate, NOT a gate ==" } else { "== gate ==" })

    # FIRST, BEFORE THE SNAPSHOTS. Run-Suites asserts this too (one verdict, see Assert-FreshBuild)
    # but the gate is what this project treats as the merge authority, so it refuses a stale tree
    # in the first second rather than after its preamble. gate's own two builds (I2) are of HEAD in
    # detached worktrees under %TEMP% and have never been a build of the tree being gated -- which
    # is precisely how a stale bin\Release got gated green.
    Assert-FreshBuild

    $bad = 0

    # I5 is measured as a DIFFERENCE, and RECOVERY-PHASES' wording ("git status --porcelain
    # is empty after a full suite run") cannot be taken literally: you run gate BEFORE a
    # commit, so the tree always holds the very change you are gating, and a literal
    # emptiness check would fail every honest use. What I5 actually means -- and what
    # P0.3 untracked tests\*-output\ to make measurable -- is that THE SUITE RUN ITSELF adds
    # nothing to the tree. So: snapshot, run, compare.
    $before = @(git -C $repo status --porcelain)
    Say "working tree before: $($before.Count) change(s)"

    # I1/I2's second row is about what the suites do to the OPERATOR'S running app, so the
    # snapshot has to be taken before they run. Pids, because a hot swap replaces the process:
    # if a suite triggers an auto-publish of the live app, the pid changes, and that is exactly
    # what this must catch.
    $appBefore = @(LiveApp)
    $appPids = if ($appBefore.Count) { ' (pids ' + (($appBefore | ForEach-Object { $_.Id }) -join ', ') + ')' } else { '' }
    Say "live app before: $($appBefore.Count) process(es)$appPids"

    # A dirty machine invalidates the timing row and skews every timing-sensitive check, so it
    # is recorded BEFORE the run rather than guessed at afterwards.
    $leakBefore = @(LeakedTestProcesses)
    if ($leakBefore.Count -eq 0) { Say "leaked test processes before: none" }
    else {
        Say "leaked test processes before: $($leakBefore.Count) -- LEFT BY EARLIER SUITE RUNS, and they skew timing"
        Say "  they hold pipes, fake agents and this runner's own files. Stop them by PATH, never by name:"
        # SINGLE quotes. In a double-quoted PowerShell string `$_` expands against whatever
        # pipeline happens to be current, and this line printed `\gate.Path -like \` -- a
        # broken instruction is worse than none, because it is a command someone will paste.
        Say '  Get-Process | Where-Object { $_.Path -like "$env:TEMP\dodona-*" } | Stop-Process -Force'
    }

    # The other half of "a dirty machine invalidates the timing row", and the half that was
    # invisible: see MachineNoise for the five-run measurement that put it here.
    $noise = MachineNoise
    Say "machine before: $($noise.Servers) build server process(es) holding $($noise.ServerMB) MB; TEMP holds $($noise.TempDirs) directories"
    # ONLY the build servers get a warning, and only because it is the half you can DO something
    # about with one command. The TEMP count is reported and never warned on: most of that folder
    # belongs to other programs entirely (9,341 Chrome entries and 2,079 yarn ones when this was
    # measured), so a threshold on it would fire on nearly every run of every machine and teach
    # people to skip the line -- which is the disease CLAUDE.md 3 names about a gate that is always
    # green, arriving from the other direction.
    if ($noise.Servers -ge 8) {
        Say "  THAT SKEWS THE CLOCK, and they are not Dodona processes, so nothing above counts them:"
        Say "  dotnet build-server shutdown     # retires the MSBuild/compiler nodes; leaves your IDE alone"
    }

    Say ""
    Say "-- suites ($(@($suites).Count) of $((AllSuites).Count)) --"
    # Wall clock across the WHOLE set, because that is what I7 is about and what a person
    # actually waits through -- not the sum of the per-suite numbers, which now overlap.
    $suiteSw = [System.Diagnostics.Stopwatch]::StartNew()
    $results = Run-Suites $suites
    $suiteSw.Stop()
    $suiteWall = $suiteSw.Elapsed.TotalSeconds
    $bad += Report-Suites $results -Wide
    Say ("suites wall clock: {0:N1}s" -f $suiteWall)

    Say ""
    Say "-- assertions --"

    # The label follows what ACTUALLY ran. Saying "after a full suite run" under `dev gate m1`
    # would be a small lie in the one place that exists to stop small lies.
    $ran = if ($partial) { "after $($suites -join ', ')" } else { "after a full suite run" }

    # I1: nothing executes from a build output. Asserted AFTER the suites, which is the only
    # moment that proves anything: before Phase 1 this printed three pids at exactly this
    # point, and every one of them would silently block the next build.
    $b = Blockers
    if ($b.Count -eq 0) {
        Say "  PASS  I1  nothing runs from src\...\bin $ran"
    }
    else {
        Say "  FAIL  I1  $($b.Count) process(es) left in the build output:"
        foreach ($p in $b) { Say "          pid $($p.Id)  $($p.ProcessName)  $($p.Path)" }
        $bad++
    }

    # I5: the working tree contains only source.
    $after = @(git -C $repo status --porcelain)
    $added = @(Compare-Object -ReferenceObject $before -DifferenceObject $after |
        Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })
    if ($added.Count -eq 0) {
        Say "  PASS  I5  the run added nothing to git status $ran ($($after.Count) change(s), unchanged)"
    }
    else {
        Say "  FAIL  I5  the run dirtied the tree with $($added.Count) path(s), $ran :"
        foreach ($a in $added) { Say "          $a" }
        $bad++
    }

    # I2: two sessions build at once without touching each other. Two detached worktrees of
    # HEAD, two concurrent `dev build`s, both must succeed -- the same trick `dev prove` uses,
    # for the same reason: a tree of its own is the only honest place to measure this. It runs
    # `dev build` and not `dotnet build` on purpose, so the row tests the door every agent is
    # told to use, Install-Hooks and ReportBlockers included.
    #
    # WHAT THIS ROW IS WORTH, measured rather than assumed. RECOVERY-PHASES section 2 lists it
    # against "today: one kills the other's daemons", which reads as though a shared checkout
    # would fail it. It would not: two concurrent `dotnet build`s against ONE shared tree were
    # measured here and BOTH SUCCEEDED, exit 0, no MSB302x -- MSBuild serializes its own
    # writers happily. The thing that actually blocked builds was never two compilers, it was a
    # DAEMON holding the output (Phase 1, I1) and `dev build` running `stop-all` (deleted in
    # P0.2), neither of which this row can see.
    #
    # So it is regression protection, not proof of the fix: it goes red if a per-session
    # worktree stops being buildable, which is the mechanism 2a hands every agent. It is NOT
    # red against the pre-Phase-2 world, and saying so here is the point -- a row whose teeth
    # are overstated is the green check nobody has seen fail (CLAUDE.md 0.3).
    #
    # It costs two builds. Gate is the slow verb by design (iterate with `dev test <suite>`).
    $wtA = Join-Path $env:TEMP ("dodona-gate-a-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    $wtB = Join-Path $env:TEMP ("dodona-gate-b-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    $madeA = $false; $madeB = $false
    try {
        & git -C $repo worktree add --detach $wtA HEAD 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
        $madeA = ($LASTEXITCODE -eq 0)
        & git -C $repo worktree add --detach $wtB HEAD 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
        $madeB = ($LASTEXITCODE -eq 0)

        if (-not ($madeA -and $madeB)) {
            Say "  FAIL  I2  could not create two worktrees of HEAD to build in (see $log)"
            $bad++
        }
        else {
            $oA = "$log.buildA"; $oB = "$log.buildB"
            $argsA = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$wtA\tools\dev.ps1", 'build')
            $argsB = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$wtB\tools\dev.ps1", 'build')
            $pA = Start-Process -FilePath 'powershell' -ArgumentList $argsA -RedirectStandardOutput $oA -RedirectStandardError "$oA.err" -NoNewWindow -PassThru
            $pB = Start-Process -FilePath 'powershell' -ArgumentList $argsB -RedirectStandardOutput $oB -RedirectStandardError "$oB.err" -NoNewWindow -PassThru
            # Touch .Handle before waiting. Without it, ExitCode reads back EMPTY after
            # WaitForExit on a Start-Process -PassThru object, and the row failed with
            # "A exit , B exit " while both builds had actually succeeded -- a green fact
            # reported as red, which is the same class of lie as the reverse.
            $null = $pA.Handle; $null = $pB.Handle
            $pA.WaitForExit(); $pB.WaitForExit()
            foreach ($f in @($oA, "$oA.err", $oB, "$oB.err")) {
                if (Test-Path $f) {
                    Add-Content -Path $log -Value "===== $f =====" -Encoding utf8
                    Get-Content $f | Add-Content -Path $log -Encoding utf8
                }
            }
            if ($pA.ExitCode -eq 0 -and $pB.ExitCode -eq 0) {
                Say "  PASS  I2  two concurrent dev builds in separate trees both succeeded"
                Say "            (regression protection only -- a shared tree passes this too; see the comment)"
            }
            else {
                Say "  FAIL  I2  concurrent builds collided: A exit $($pA.ExitCode), B exit $($pB.ExitCode)"
                foreach ($f in @($oA, $oB)) {
                    if (Test-Path $f) {
                        Get-Content $f | Select-String -Pattern ': error |MSB302|BLOCKED|BUILD FAILED' | Select-Object -First 4 |
                            ForEach-Object { Say "          $($_.Line.Trim())" }
                    }
                }
                $bad++
            }
            Remove-Item $oA, "$oA.err", $oB, "$oB.err" -Force -ErrorAction SilentlyContinue
        }
    }
    finally {
        if ($madeA) { & git -C $repo worktree remove --force $wtA 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 } }
        if ($madeB) { & git -C $repo worktree remove --force $wtB 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 } }
    }

    # I1/I2: the suites ran green (measured above, in $bad) AND the operator's live app came
    # through them untouched. Same pids, still alive: a suite that reached into the installed
    # app -- by publishing over it, by stop-all, by contending on its files -- changes one of
    # those, and the whole point of Phase 1 plus Phase 2 is that it cannot.
    #
    # With nothing installed and running, this CANNOT be asserted, and it says so rather than
    # printing a green line it did not earn. A pass nobody could have failed is the exact thing
    # `dev prove` exists to stop (CLAUDE.md 0.3).
    if ($appBefore.Count -eq 0) {
        Say "  n/a   I1  no live app was running, so 'the suites leave the app alone' was NOT tested"
        Say "            (open the app and re-run gate to assert this row)"
    }
    else {
        $gone = @(); $survived = 0
        foreach ($a in $appBefore) {
            $now = Get-Process -Id $a.Id -ErrorAction SilentlyContinue
            $samePath = $null
            if ($now) { try { $samePath = $now.Path } catch { } }
            if ($now -and $samePath -and $samePath -eq $a.Path) { $survived++ }
            else { $gone += $a }
        }
        if ($gone.Count -eq 0) {
            Say "  PASS  I1  the live app came through the suites untouched ($survived process(es), same pids)"
        }
        else {
            Say "  FAIL  I1  the suites disturbed $($gone.Count) live app process(es):"
            foreach ($g in $gone) { Say "          pid $($g.Id)  $($g.Name)  $($g.Path)" }
            $bad++
        }
    }

    # ENFORCEMENT IS ALIVE. Not one of RECOVERY-PHASES section 2's eight rows -- it is Phase 2a's
    # own guard against the failure that phase is built on: an enforcement that stops enforcing
    # without saying so. The routing ladder was fully covered, fully green and DEAD IN PRODUCTION
    # for two days (CLAUDE.md section 3). This lock has already failed twice in one session, both
    # times looking installed: once unable to parse (a stray backtick), once pointed at a
    # directory that did not exist on the branch being committed from.
    #
    # So it is asserted BYTE FOR BYTE against the tracked source, and core.hooksPath is asserted
    # ABSENT. "Does a file exist" would have passed happily through both failures.
    $src = "$repo\.githooks\pre-commit"
    $common = (& git -C $repo rev-parse --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $common) {
        if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $repo $common }
        $dst = Join-Path (Join-Path $common 'hooks') 'pre-commit'
    }
    else { $dst = $null }
    $hp = (& git -C $repo config --get core.hooksPath 2>$null)
    $hpSet = ($LASTEXITCODE -eq 0 -and $hp)
    $deployed = $dst -and (Test-Path $dst) -and (Test-Path $src) -and
                ([System.IO.File]::ReadAllText($dst).Replace("`r`n", "`n") -eq [System.IO.File]::ReadAllText($src).Replace("`r`n", "`n"))
    if ($deployed -and -not $hpSet) {
        Say "  PASS  D-7  the commit guard is deployed and current, and nothing overrides it"
    }
    else {
        Say "  FAIL  D-7  commits from the shared checkout are NOT being refused:"
        if (-not $deployed) { Say "          $dst is missing or differs from .githooks\pre-commit" }
        if ($hpSet) { Say "          core.hooksPath is set to '$hp', so git ignores the deployed hook" }
        $bad++
    }

    # I2: what the app reports it is running must be a COMMIT THIS REPO HAS. Before Phase 2b
    # `status` printed a timestamp mapping to nothing, so "which code is live?" had no answer --
    # you could not bisect it, diff it, or check it against git log.
    #
    # `git cat-file -t` is the load-bearing half: it demands the SHA RESOLVES to a commit here.
    # Comparing the value to itself, or matching a hex pattern, would pass for any 40 characters.
    #
    # THIS ROW WAS SILENTLY DELETED ONCE, by an edit whose replacement range ran past it -- the
    # exact "gate whose table drifts out of date" that RECOVERY-PHASES section 2 warns is the same
    # lie the gate exists to prevent. It went unnoticed until a publish reported build=unknown and
    # nothing failed. If you move these rows, count them afterwards.
    $binRoot = Join-Path $env:LOCALAPPDATA 'Dodona\bin'
    $installed = if (Test-Path $binRoot) { @(Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name) } else { @() }
    if ($installed.Count -eq 0) {
        Say "  n/a   I2  nothing is installed in $binRoot, so the live build's commit was NOT checked"
        Say "            (run dev ship, then re-run gate to assert this row)"
    }
    else {
        $exe = Join-Path $installed[-1].FullName 'dodona.exe'
        $vjRaw = & $exe version --json 2>&1 | Out-String
        # ConvertFrom-Json on a single OBJECT is one pipeline item, which is safe -- CLAUDE.md
        # 0.2's trap is arrays, and this is not one.
        $vj = $null
        try { $vj = $vjRaw | ConvertFrom-Json } catch { }
        $sha = if ($vj) { [string]$vj.commit } else { '' }
        if (-not $sha) {
            # A build with no provenance is a REAL state and this row cannot judge it: there is
            # no reported SHA to check. It is called out rather than passed over, because
            # "unknown" is also how a stamping change looks the first time it is published -- the
            # publisher is the OLD binary and cannot stamp the new fields.
            Say "  n/a   I2  the installed build reports no commit, so there is no SHA to check"
            Say "            ($exe -- publish again WITH THAT binary to stamp it)"
        }
        elseif ((& git -C $repo cat-file -t $sha 2>$null) -eq 'commit') {
            Say "  PASS  I2  the installed build's commit is one git log knows: $(& git -C $repo log -1 --format='%h %s' $sha 2>$null)"
        }
        else {
            Say "  FAIL  I2  the installed build reports commit $sha, which this repo does not have"
            Say "            ($exe)"
            $bad++
        }
    }

    # I7: A FULL SUITE RUN FINISHES FAST. Earned by Phase 4; measured here, in the run that
    # just happened, not quoted from a document.
    #
    # THIS IS THE ROW THAT MAKES THE OTHERS AFFORDABLE. The doctrine "iterate fast, gate slow"
    # rested on CLAUDE.md section 1's claim that the suites take twenty minutes and that only
    # 3.6 of those are sleep, "so the rest is inherent and cannot be optimised away". Measured
    # 2026-08-19 that was wrong in both halves: 5 min 20 s, of which 214 s -- 68 % -- was fixed
    # Start-Sleep, and every second of it stood in front of a condition the very next line
    # already asserted. A wrong number in the direction of "not worth fixing" is how verifying
    # became something to skip, which is the root of every believed-green-check incident.
    #
    # It asserts WALL CLOCK for the whole suite set, which is the number a person waits through
    # -- not the sum of the parts, because the parts now run at the same time.
    #
    # THE BUDGET IS 180 s AND RECOVERY-PHASES P4.3 PROJECTED 35-45 s. That projection was not
    # met, and the number here is not quietly rounded to hide it. Measured on this machine, all
    # twelve suites green on a CLEAN machine: 54.6, 69.7, 74.4, 76.9, 87.0 s. The spread is
    # real -- ui-use alone ranges 42.5 s to 72 s, because it makes about a hundred sequential
    # dodona.exe calls and every one of them is slower with four other suites running.
    #
    # It was 90 s for one commit, which was wrong for the reason this comment exists to state:
    # 87.0 s was then observed on a green run, three seconds inside the line. A threshold set
    # just above the worst observation is not a budget, it is a coin flip -- and a gate that
    # goes red for reasons unrelated to the change is one people learn to re-run instead of
    # read, which is the same disease as a gate that is always green.
    #
    # RAISED 120 -> 180 s ON 2026-08-19, and this is a DECISION, not a slipped number. The
    # Locations wave-1 tree (Phases 0, 0c and 1 merged) measured 115.9 s green -- 4.1 s inside the
    # old line -- because those three phases added about NINETY checks: unit 54 -> 88, workspace
    # 56 -> 84, m3 28 -> 32, concierge 39 -> 42. That growth is earned coverage, not a regression,
    # and leaving the budget at 120 would have made the next wave present as A GATE FAILURE rather
    # than as "the suites grew", which is the misleading red this repo treats as costing exactly
    # what a false green costs. 180 s is 1.55x the 115.9 s measurement -- a shade more headroom
    # than the 1.38x that set 120 against 87 -- because the spread is still driven by ui-use,
    # which alone is 67.9 s of that 115.9 s and is four suites wearing one name (CLAUDE.md 3 calls
    # splitting it unfinished business, and it is what would buy the budget back).
    #
    #
    # RAISED 180 -> 260 s ON 2026-08-19, after wave 3 (Phases 3, 4 and 5). Measured 195.1 s
    # with all twelve suites GREEN, 720 checks against 594 -- and the growth is traceable to
    # two suites rather than spread thin: workspace 107 -> 136 checks (Phase 3's project
    # ladder) took it 37.1 -> 67.4 s, and brain 45 -> 62 (Phase 5's per-project managers) took
    # it 38.6 -> 66.3 s.
    #
    # WHY THIS RAISE IS NOT THE ONE REFUSED EARLIER IN THE SAME WAVE. Two hours before this,
    # the same gate failed I7 at 244.3 s and the budget was deliberately LEFT ALONE, because
    # four ui-use checks were red and every suite had inflated by half: the overrun was a
    # SYMPTOM, and raising the line would have recorded a 50% slowdown as normal. Isolating
    # ui-use fixed both and the run came back to 160.3 s inside the old budget. Here nothing
    # is red and no suite is inflated -- the run is slower because there is more of it. That
    # is the whole test for whether this number may move: FIX A RED, RAISE FOR GROWTH, and
    # never the other way round.
    #
    # 260 s is 1.33x the 195.1 s measurement -- tighter than the 1.55x that set 180, because
    # ui-use no longer runs beside anything (it is in SoloSuites now) and that was what made
    # the spread wide. The long pole is now THREE monoliths, not one: ui-use 73.1, workspace
    # 67.4, brain 66.3 -- 207 of the 195 s wall clock between them, which is only possible
    # because two of them overlap. Splitting any of the three is what buys the budget back,
    # and it is still the standing unfinished business CLAUDE.md 3 names.
    # It still goes red the moment a fixed sleep creeps back in, and it is still 1.8x better than
    # the 320 s this took sequentially.
    #
    # A DIRTY MACHINE BREAKS IT ANYWAY, which is why the leak count is printed above: with 78
    # shims left by earlier runs the same code took 300 s, m3 crashed and brain went red on
    # nine timing checks. The way to earn 45 s is to stop ui-use being the long pole (it is
    # four suites wearing one name) and to stop the suites leaking; neither is this phase.
    #
    # RAISED 260 -> 300 s ON 2026-08-20, for work-isolation P1 and P2 (layer 1's write gate on
    # every lane, and promotion on the refused write). Measured twice, all thirteen suites GREEN:
    # 243.9 s at P1 and 250.6 s at P2, against 902 checks where the 260 s line was set at 720. The
    # growth is traceable rather than spread thin -- m1 51 checks (a ticket agent and a plain lane
    # it never used to start), m2 14 -> 24 (the whole promotion path, a real `git worktree add` and
    # a prune), m4 40 -> 42, unit 274 -> 278 -- and the run is slower because there is more of it.
    #
    # It passes the test the paragraph above sets: NOTHING WAS RED and no suite is inflated. 250.6 s
    # against a 260 s line is 96% of the budget, so the next phase would have presented earned
    # coverage as a gate failure -- which is the exact mistake the 180 -> 260 raise was made to
    # avoid. 300 s is 1.20x the measurement, tighter than the 1.33x that set 260, because two
    # consecutive full runs came in 6.7 s apart (243.9 and 250.6) rather than spread wide.
    #
    # The long pole is unchanged and so is the way to buy the budget back: ui-use, workspace and
    # brain are three monoliths at ~100, ~60 and ~56 s, and splitting any of them is worth more than
    # any number here.
    #
    # PARTIAL runs cannot judge it: three suites finishing quickly says nothing about twelve.
    # It says so rather than passing a row it did not earn (CLAUDE.md 0.3).
    if ($partial) {
        Say "  n/a   I7  only $(@($suites).Count) of $((AllSuites).Count) suites ran in $([math]::Round($suiteWall, 1))s, so the budget was NOT tested"
    }
    elseif ($suiteWall -lt 300) {
        Say ("  PASS  I7  the full suite run finished in {0:N1}s, inside the 300s budget (was 320s sequential)" -f $suiteWall)
    }
    else {
        Say ("  FAIL  I7  the full suite run took {0:N1}s, over the 300s budget" -f $suiteWall)
        Say ("            slowest: " + ((($results | Sort-Object Seconds -Descending | Select-Object -First 3 |
                    ForEach-Object { "$($_.Name) $($_.Seconds)s" }) -join ', ')))
        # TWO causes, and the machine one is listed FIRST because it is the one that actually
        # happened: a 300 s run was diagnosed as "a Start-Sleep came back" when it was 78
        # leaked shims from earlier runs. A wrong hint is worse than no hint.
        $leakNow = @(LeakedTestProcesses)
        if ($leakNow.Count -gt 0) {
            Say "            $($leakNow.Count) leaked test process(es) are on this machine -- that is very likely the cause."
            Say "            Stop them by PATH and re-run before believing this row."
        }
        else {
            Say "            the machine is clean, so this is the suites themselves: a fixed"
            Say "            Start-Sleep has probably come back -- grep tests\ for it (P4.1)"
        }
        $bad++
    }

    # I3: NOTHING DODONA STARTED SURVIVED THE RUN. Phase 3's invariant, asserted where it can be
    # measured independently of the product's own counting code -- straight off the process table,
    # scoped by PATH to the directories this runner made (CLAUDE.md 4: never by name alone).
    #
    # The suites deliberately murder daemons with -Force, so every wrapper here had to end itself:
    # by seeing its child exit, or by its lease running out. Before Phase 3 a shim's only exit was
    # a message from a daemon that was usually already dead, and 78 of them accumulated in one
    # session -- which turned an 87 s run into 300 s, crashed m3 and reddened nine of brain's
    # timing checks. A verification tool whose answer depends on how many corpses are on the
    # machine is not a verification tool.
    #
    # A short settle wait, because a `finally` that has just called Stop-Process on eight pids may
    # still have one of them exiting. It is a condition with a deadline, never a sleep.
    $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
    while ((@(LeakedAgentProcesses)).Count -gt 0 -and $sw3.ElapsedMilliseconds -lt 3000) { Start-Sleep -Milliseconds 200 }
    $survivors = @(LeakedAgentProcesses)
    if ($partial) {
        Say "  PARTIAL I3 $($survivors.Count) wrapper/agent process(es) survived -- only $($suites -join ', ') ran"
        if ($survivors.Count -gt 0) { $bad++ }
    }
    elseif ($survivors.Count -eq 0) {
        Say "  PASS  I3  no wrapper or agent process survived the suites"
    }
    else {
        Say "  FAIL  I3  $($survivors.Count) wrapper/agent process(es) outlived the run:"
        foreach ($sv in $survivors) { Say "          pid $($sv.Id)  $($sv.ProcessName)  $($sv.Path)" }
        Say "          Stop them BY PATH, never by name, and read tests\m0-acceptance.ps1's"
        Say "          Phase 3 section -- one of P3.2 (child exited) or P3.3 (lease) has regressed."
        $bad++
    }

    # P7.5: NO SILENT ENCODING CHANGE. Two questions, because the damage has arrived two
    # different ways and each is invisible to the other check.
    #
    # (a) BOM. Phase 3 added one to seven files -- a patch script written with utf-8-sig -- and it
    #     was caught only because a human happened to read the diff. Compared byte-exact against
    #     `git cat-file blob`, which is PLUMBING and so runs no smudge filter, captured through
    #     cmd's redirect because PowerShell's `>` re-encodes and would invent the difference.
    #
    # (b) LINE ENDINGS. The Phase 7 commit rewrote a whole file's endings and turned a 105-line
    #     insert into a 1214-line phantom diff. core.autocrlf=true here, so git stores LF and
    #     checks out CRLF; a script that derives its newline from the WORKING TREE sees CRLF and
    #     double-converts, and the extra CR survives normalisation. There is no BOM involved, so
    #     (a) cannot see it -- but the churn is enormous and a whitespace-blind diff does not
    #     share it. Ratio, not equality: real edits differ a little, a rewrite differs 20x.
    $encBad = @()
    foreach ($row in @(git -C $repo status --porcelain)) {
        if ($row.Length -lt 4) { continue }
        $code = $row.Substring(0, 2)
        # untracked and deleted have no HEAD blob to compare against
        if ($code.Contains('?') -or $code.Contains('D')) { continue }
        $rel = $row.Substring(3).Trim('"')
        if ($rel -match ' -> ') { $rel = ($rel -split ' -> ')[-1].Trim('"') }
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { continue }
        $tmp = Join-Path $env:TEMP ("dodona-headblob-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".bin")
        cmd /c "git -C ""$repo"" cat-file blob ""HEAD:$rel"" > ""$tmp"" 2>nul" | Out-Null
        if (-not (Test-Path $tmp)) { continue }
        try {
            $hb = [System.IO.File]::ReadAllBytes($tmp)
            $wb = [System.IO.File]::ReadAllBytes($full)
            $hBom = ($hb.Length -ge 3 -and $hb[0] -eq 0xEF -and $hb[1] -eq 0xBB -and $hb[2] -eq 0xBF)
            $wBom = ($wb.Length -ge 3 -and $wb[0] -eq 0xEF -and $wb[1] -eq 0xBB -and $wb[2] -eq 0xBF)
            if ($hBom -ne $wBom) { $encBad += "$rel -- BOM was $(if ($hBom) { 'REMOVED' } else { 'ADDED' }) by this change" }
        }
        catch { }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }

    $plainChurn = 0; $blindChurn = 0
    foreach ($n in @(git -C $repo diff --numstat HEAD)) {
        $f2 = $n -split "`t"
        if ($f2.Count -ge 2 -and $f2[0] -ne '-') { $plainChurn += [int]$f2[0] + [int]$f2[1] }
    }
    foreach ($n in @(git -C $repo diff -w --numstat HEAD)) {
        $f2 = $n -split "`t"
        if ($f2.Count -ge 2 -and $f2[0] -ne '-') { $blindChurn += [int]$f2[0] + [int]$f2[1] }
    }
    if ($plainChurn -gt 40 -and $blindChurn -gt 0 -and $plainChurn -gt (3 * $blindChurn)) {
        $encBad += "$plainChurn changed line(s), but only $blindChurn survive a whitespace-blind diff -- that is a line-ending rewrite, not an edit"
    }

    if ($encBad.Count -eq 0) {
        Say "  PASS  P7.5 no changed file altered its BOM, and no diff is mostly whitespace churn"
    }
    else {
        Say "  FAIL  P7.5 a change is altering file ENCODING, not just content:"
        foreach ($e in $encBad) { Say "          $e" }
        Say "          Scripts must emit bare LF and let core.autocrlf do its job -- never derive the"
        Say "          newline from the working tree, and never write with utf-8-sig unless HEAD has a BOM."
        Say "          Confirm by hand with: git show -w --stat  against the plain --stat."
        $bad++
    }

    # I8: the prose does not lie about itself. Last of RECOVERY-PHASES section 2's rows.
    #
    # AND, SINCE W4, THE LEDGER RUNGS TOO -- folded in rather than added beside (D-T23). The
    # assertion count stays at TEN on purpose: the ledger's static rungs and the double ledger's
    # rung 1 are static parses of tracked files, which is what I8 already is.
    $lint = @(Repo-Lint)
    if ($lint.Count -eq 0) {
        Say "  PASS  I8  repo lint clean over $script:LintScope file(s): no control bytes, every named test path real, every ledger row resolves, every double anchored, every wire command declared"
    }
    else {
        Say "  FAIL  I8  repo lint found $($lint.Count) problem(s):"
        foreach ($l in $lint) { Say "          $l" }
        $bad++
    }

    Say ""
    # READINGS, OUTSIDE THE ASSERTION LIST AND SAID TO BE (plan 3.4, D-T13). A count that
    # reddens on a date fails for a non-defect, reddens every historical commit under bisect,
    # and teaches people to re-run instead of read. These are numbers to LOOK at.
    Say "-- readings (not assertions) --"
    foreach ($r in @((Doubles-Static).Readings)) { Say "  $r" }
    Say ""
    # No "not covered yet" list any more: RECOVERY-PHASES section 2's rows are all asserted above.
    # That is NOT the same as "the gate proves the system works" -- it proves these ten things, and
    # the verdict below says so on purpose. A gate whose scope drifts out of its own description is
    # the lie it exists to prevent (the I2 provenance row was silently deleted once).
    if ($partial) {
        Say $(if ($bad -eq 0) { "GATE SELF-TEST PASSED -- machinery works. THIS IS NOT A GATE: only $($suites -join ', ') ran." }
              else { "GATE SELF-TEST FAILED -- $bad problem(s)" })
    }
    else {
        Say $(if ($bad -eq 0) { "GATE PASSED -- on the 10 assertions above, and only those." } else { "GATE FAILED -- $bad problem(s)" })
    }
    Say "log: $log"
    if ($bad -gt 0) { exit 1 }
}

function Do-Ship {
    Do-Build
    Do-Suites
    Say "== publish =="
    ReportBlockers
    $o = & $dodona publish --project $repo --all 2>&1
    Add-Content -Path $log -Value $o -Encoding utf8
    $o | Select-Object -Last 12 | ForEach-Object { Say "  $_" }
    Say ""
    Say "PUBLISHED. Publishing does NOT commit -- commit the work or it is lost on the next checkout."
    Say "log: $log"
}

# ---------------------------------------------------------------- the check-name ledger
#
# Split out of this file (issue #23): dev.ps1 was 3,636 lines / 57,254 tokens, and the ledger
# is 1,404 of them -- a self-contained subsystem with its own scanner, rungs and reports.
# Dot-sourced rather than a module, so every function lands in THIS scope and the script
# variables above ($repo, $log, $dodona) resolve exactly as they did when it was inline.
#
# It must stay dot-sourced BEFORE the dispatch below: Repo-Lint calls Ledger-Static, and
# `dev lint` is one of the verbs.
. "$PSScriptRoot\dev.ledger.ps1"

switch ($Verb) {
    'check' { Do-Check }
    'build' { Do-Build }
    'test' { Do-Test }
    'suites' { Do-Suites }
    'prove' { Do-Prove }
    'lint' {
        Say "== lint =="
        $l = @(Repo-Lint)
        # Readings, NOT assertions (plan 3.4): a number nobody has to keep inside a bound.
        foreach ($r in @((Ledger-Static).Readings) + @((Doubles-Static).Readings)) { Say "  note: $r" }
        if ($l.Count -eq 0) { Say "clean over $script:LintScope file(s): no control bytes, every named test path real, every ledger row resolves, every double anchored, every wire command declared" }
        else { foreach ($x in $l) { Say "  $x" }; Say ""; Say "$($l.Count) problem(s)"; exit 1 }
    }
    'gate' { Do-Gate }
    'ledger' { Do-Ledger }
    'ship' { Do-Ship }
    'worktree' { Do-Worktree }
    'help' { Do-Help }
}
