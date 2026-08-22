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

# The wrappers and agents specifically -- what I3 is about. A `dodona` daemon still winding down
# from `stop-daemon` when we look is a RACE, not an orphan, and lumping the two together is what
# made "publish-acceptance leaks four DodonaShim every run" a fact everybody repeated: publish-
# acceptance starts no lanes at all, so it has never leaked a wrapper.
function LeakedAgentProcesses {
    @(LeakedTestProcesses | Where-Object { $_.ProcessName -in @('DodonaShim', 'DodonaFakeAgent', 'claude') })
}

# ---------------------------------------------------------------- the repo lint (I8, P5.1)

# Two questions about the PROSE, both sub-second, both asked of TRACKED files only -- `git
# ls-files` is the scope, so bin\, obj\, .dodona\ and other sessions' worktrees are excluded by
# construction rather than by a pattern somebody has to maintain.
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
function Repo-Lint {
    $problems = @()
    $files = @(git -C $repo ls-files '*.md' '*.ps1' 2>$null)
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
        Say "  PASS  I8  repo lint clean: no control bytes, every named test path real, every ledger row resolves, every double anchored"
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
# W2 of docs\TEST-ARCHITECTURE-PLAN.md. The accounting that makes "no coverage was lost" a
# fact rather than a promise, for a migration that moves ~560 acceptance checks down into
# pure-logic tests.
#
# WHY IT IS HERE AND NOT IN A NEW TOOL: D-3 (RECOVERY-PHASES) -- dev.ps1 is the one door --
# and D-T6: there is exactly ONE parser of check names in this repo, and it is this one,
# because tools\dev.ps1 must run on a tree that will not compile (CLAUDE.md section 1). The
# C# side reads the tracked TSV artefact and never parses a .ps1. Two enumerators of one
# thing are two hand copies, which is the failure the whole plan exists to prevent.
#
# THE SCANNER USES THE POWERSHELL AST, NOT A REGEX, and that is load-bearing twice over.
# tests\ledger\README.md records the two real cases a text scan gets wrong:
#   (1) _workspace.ps1:409 is a commented-out Check '...' inside a doc comment. A grep
#       reports it as a duplicate registration forever, and a permanent false positive in a
#       gate assertion is how people learn to ignore the assertion -- the same disease as a
#       gate that is always green. The AST has no comment nodes, so it cannot see it at all.
#   (2) m2-acceptance.ps1:330 and :334 are the two arms of ONE if/else writing ONE name. A
#       source-line rule would have to forbid a perfectly correct idiom. The AST can prove
#       the two sites are mutually exclusive, so the idiom stays legal.

function Ledger-Dir { Join-Path $repo 'tests\ledger' }

function Ledger-Sha([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('x2') }) -join '' }
    finally { $sha.Dispose() }
}

# The ledger files are TSV, ASCII-only, CRLF, no BOM, and this asserts all of it (plan
# section 5.2). Not JSON: ConvertFrom-Json emits a JSON ARRAY as one pipeline item
# (CLAUDE.md 0.2) and that trap has already turned three acceptance checks into silent
# no-ops here. ASCII-only, because Repo-Lint's known gap is exactly a non-ASCII byte in a
# BOM-less file read by PS 5.1, and one em dash in a ledger row would match nothing and
# drop that row SILENTLY. The reader strips a leading U+FEFF defensively anyway -- that is
# the GateHook incident, where Console.In handed a BOM back as an ordinary character.
function Ledger-ReadTsv([string]$path, [string[]]$columns) {
    $r = [pscustomobject]@{ Present = $false; Rows = @(); Problems = @(); Path = $path }
    if (-not (Test-Path $path)) { return $r }
    $r.Present = $true
    $rel = $path.Substring($repo.Length).TrimStart('\')
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $r.Problems += "$rel has a UTF-8 BOM -- ledger files are ASCII with no BOM"
        $bytes = if ($bytes.Length -gt 3) { $bytes[3..($bytes.Length - 1)] } else { @() }
    }
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 0x7F) {
            $line = 1; for ($k = 0; $k -lt $i; $k++) { if ($bytes[$k] -eq 0x0A) { $line++ } }
            $r.Problems += ("{0}:{1} non-ASCII byte 0x{2:x2} -- a ledger row read as ANSI matches nothing and drops SILENTLY" -f $rel, $line, $bytes[$i])
            break
        }
    }
    $bare = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 0x0A) { if (-not ($i -gt 0 -and $bytes[$i - 1] -eq 0x0D)) { $bare++ } }
    }
    if ($bare -gt 0) { $r.Problems += "$rel has $bare bare LF line ending(s) -- ledger files are CRLF" }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lines = @($text -split "`r`n|`n")
    $n = 0; $sawHeader = $false; $rows = @()
    foreach ($raw in $lines) {
        $n++
        if ($raw -eq '') { continue }
        if ($raw.StartsWith('#')) { continue }
        $f = @($raw -split "`t")
        if (-not $sawHeader) {
            $sawHeader = $true
            if (($f -join ',') -ne ($columns -join ',')) {
                $r.Problems += "${rel}:${n} header is [$($f -join '|')] -- expected [$($columns -join '|')]"
            }
            continue
        }
        if ($f.Count -ne $columns.Count) {
            $r.Problems += "${rel}:${n} has $($f.Count) tab-separated field(s), expected $($columns.Count) -- [$raw]"
            continue
        }
        $o = New-Object psobject
        for ($c = 0; $c -lt $columns.Count; $c++) { $o | Add-Member -NotePropertyName $columns[$c] -NotePropertyValue $f[$c] }
        $o | Add-Member -NotePropertyName '_line' -NotePropertyValue $n
        $o | Add-Member -NotePropertyName '_file' -NotePropertyValue $rel
        $rows += $o
    }
    if (-not $sawHeader) { $r.Problems += "$rel has no header row" }
    $r.Rows = @($rows)
    return $r
}

function Ledger-WriteTsv([string]$path, [string[]]$columns, $rows) {
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.Append(($columns -join "`t")).Append("`r`n")
    foreach ($row in @($rows)) {
        $vals = @()
        foreach ($c in $columns) { $vals += ("" + $row.$c) }
        $null = $sb.Append(($vals -join "`t")).Append("`r`n")
    }
    # An encoder that emitted a BOM would break the very file this tool refuses a BOM on.
    [System.IO.File]::WriteAllText($path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

# ---- the scanner -------------------------------------------------------------------
#
# One site per place a check name is REGISTERED: Check '<name>' in fourteen suites, and
# m0's inline $results['<name>'] = (m0 has no Check helper and never had one).
# Dynamic == the name is built at runtime (Check "event_$k"), which no static parse can
# enumerate; plan section 5.4 says those are reachable on the --live side only.
function Ledger-ScanChecks {
    $sites = @()
    $files = @()
    foreach ($s in (AllSuites)) {
        if ((UnitSuites) -contains $s) { continue }          # xunit; the TRX is its census
        $f = "$repo\tests\$s-acceptance.ps1"
        if (Test-Path $f) { $files += [pscustomobject]@{ Suite = $s; Path = $f } }
    }
    # The harness writes one row into EVERY suite's $results (Assert-NoBuildOutputProcesses).
    # It is a real check and one of the 750; it is also the one name that is deliberately
    # not unique (plan section 5.4: fifteen rows, "not deduplicable").
    $ws = "$repo\tests\_workspace.ps1"
    if (Test-Path $ws) { $files += [pscustomobject]@{ Suite = '_harness'; Path = $ws } }

    foreach ($entry in $files) {
        $tok = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($entry.Path, [ref]$tok, [ref]$errs)
        if ($errs -and @($errs).Count -gt 0) {
            $sites += [pscustomobject]@{ Suite = $entry.Suite; Check = ''; Line = @($errs)[0].Extent.StartLineNumber
                File = $entry.Path; Dynamic = $false; ParseError = @($errs)[0].Message; Ast = $null }
            continue
        }
        foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            if ($c.GetCommandName() -ne 'Check') { continue }
            if (@($c.CommandElements).Count -lt 2) { continue }
            if (Ledger-InsideCheckHelper $c) { continue }
            $a = $c.CommandElements[1]
            if ($a -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $sites += [pscustomobject]@{ Suite = $entry.Suite; Check = $a.Value; Line = $a.Extent.StartLineNumber
                    File = $entry.Path; Dynamic = $false; ParseError = $null; Ast = $c }
            }
            else {
                $sites += [pscustomobject]@{ Suite = $entry.Suite; Check = $a.Extent.Text; Line = $a.Extent.StartLineNumber
                    File = $entry.Path; Dynamic = $true; ParseError = $null; Ast = $c }
            }
        }
        foreach ($s in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            $l = $s.Left
            if ($l -isnot [System.Management.Automation.Language.IndexExpressionAst]) { continue }
            if ("$($l.Target.Extent.Text)" -ne '$results') { continue }
            if (Ledger-InsideCheckHelper $s) { continue }
            $ix = $l.Index
            if ($ix -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $sites += [pscustomobject]@{ Suite = $entry.Suite; Check = $ix.Value; Line = $s.Extent.StartLineNumber
                    File = $entry.Path; Dynamic = $false; ParseError = $null; Ast = $s }
            }
            else {
                $sites += [pscustomobject]@{ Suite = $entry.Suite; Check = $ix.Extent.Text; Line = $s.Extent.StartLineNumber
                    File = $entry.Path; Dynamic = $true; ParseError = $null; Ast = $s }
            }
        }
    }
    return @($sites)
}

# function Check writes $results[$name] itself. That is the helper, not a registration, and
# counting it would put one phantom dynamic site in every suite.
function Ledger-InsideCheckHelper($node) {
    $p = $node
    while ($null -ne $p) {
        if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $p.Name -eq 'Check') { return $true }
        $p = $p.Parent
    }
    return $false
}

function Ledger-Ancestors($node) {
    $chain = @()
    $p = $node
    while ($null -ne $p) { $chain += $p; $p = $p.Parent }
    return @($chain)
}

# TWO SITES, ONE NAME, IN ONE FILE -- is exactly one of them going to run?
#
# Two shapes are legal and both are in this repo today:
#   if (...) { Check 'x' ... } else { Check 'x' ... }        m2-acceptance.ps1:330 / :334
#   if (...) { $results['x'] = 'PASS'; return }              _workspace.ps1:377 / :380
#   $results['x'] = "FAIL ..."
# The first diverges AT a branching node; the second is sequential, but the earlier arm
# leaves the block. Anything else is a real collision: the second write overwrites the
# first, the tally comes out one lower than the suite believes, and nothing anywhere says
# so. That silent overwrite is the failure this rung exists to catch.
function Ledger-MutuallyExclusive($a, $b) {
    if ($null -eq $a.Ast -or $null -eq $b.Ast) { return $false }
    $ca = Ledger-Ancestors $a.Ast
    $cb = Ledger-Ancestors $b.Ast
    $common = $null; $ia = -1; $ib = -1
    for ($i = 0; $i -lt $ca.Count; $i++) {
        for ($k = 0; $k -lt $cb.Count; $k++) {
            if ([object]::ReferenceEquals($ca[$i], $cb[$k])) { $common = $ca[$i]; $ia = $i; $ib = $k; break }
        }
        if ($null -ne $common) { break }
    }
    if ($null -eq $common) { return $false }

    if ($common -is [System.Management.Automation.Language.IfStatementAst] -or
        $common -is [System.Management.Automation.Language.SwitchStatementAst] -or
        $common -is [System.Management.Automation.Language.TryStatementAst]) { return $true }

    if ($common -is [System.Management.Automation.Language.StatementBlockAst] -or
        $common -is [System.Management.Automation.Language.NamedBlockAst] -or
        $common -is [System.Management.Automation.Language.ScriptBlockAst]) {
        $stmtA = if ($ia -gt 0) { $ca[$ia - 1] } else { $null }
        $stmtB = if ($ib -gt 0) { $cb[$ib - 1] } else { $null }
        if ($a.Line -lt $b.Line) { $firstStmt = $stmtA; $firstAst = $a.Ast }
        else { $firstStmt = $stmtB; $firstAst = $b.Ast }
        if ($null -ne $firstStmt -and $firstStmt -is [System.Management.Automation.Language.IfStatementAst]) {
            return (Ledger-BranchLeaves $firstStmt $firstAst)
        }
    }
    return $false
}

# Does the arm of $ifStmt that contains $node end in return/throw/break/continue?
function Ledger-BranchLeaves($ifStmt, $node) {
    $bodies = @()
    foreach ($clause in $ifStmt.Clauses) { $bodies += $clause.Item2 }
    if ($null -ne $ifStmt.ElseClause) { $bodies += $ifStmt.ElseClause }
    $chain = Ledger-Ancestors $node
    foreach ($body in $bodies) {
        $inside = $false
        foreach ($anc in $chain) { if ([object]::ReferenceEquals($anc, $body)) { $inside = $true; break } }
        if (-not $inside) { continue }
        $stmts = @($body.Statements)
        if ($stmts.Count -eq 0) { return $false }
        $last = $stmts[$stmts.Count - 1]
        return ($last -is [System.Management.Automation.Language.ReturnStatementAst] -or
            $last -is [System.Management.Automation.Language.ThrowStatementAst] -or
            $last -is [System.Management.Automation.Language.BreakStatementAst] -or
            $last -is [System.Management.Automation.Language.ContinueStatementAst])
    }
    return $false
}

# The harness row is written once per suite BY DESIGN (fifteen rows, plan section 5.4,
# "not deduplicable"). It is therefore the one name exempt from repo-wide uniqueness, and
# the exemption is DERIVED -- a name whose registration site is in _workspace.ps1 --
# rather than a hand-maintained list that could go stale.
function Ledger-HarnessNames($sites) {
    $names = Ledger-NewSet
    foreach ($s in @($sites | Where-Object { -not $_.Dynamic -and $_.Suite -eq '_harness' })) { $names[$s.Check] = $true }
    return $names
}

# A dictionary that tells 'A_x' from 'a_x'. PowerShell's @{} does NOT: it is case-insensitive,
# and that silently merged two DELIBERATE PAIRS out of the first census ever captured --
# `a_named_project_is_not_overruled_by_a_busy_one` (workspace-acceptance.ps1:1465) with the C#
# `A_named_...`, and `a_one_project_workspace_says_nothing_about_scope`
# (brain-acceptance.ps1:806) with its C# twin. 962 names were frozen where 964 had run, and
# nothing said so. A census that loses names is the one thing this tool exists to prevent.
function Ledger-NewSet { , (New-Object 'System.Collections.Hashtable' ([System.StringComparer]::Ordinal)) }

# THE KEY, in one place so the capture and the integrity rung can never disagree about it.
#
# Suite checks key on the BARE NAME -- that is deliberate and is what keeps W6's rename of every
# suite free (plan section 5.2). Two exceptions, both namespaced:
#   * harness rows -- one source site in _workspace.ps1 emitted into EVERY suite's results, so
#     the same name legitimately appears once per suite;
#   * unit methods -- a suite check and a unit method may carry the same name ON PURPOSE. That
#     is the plan's end state for a wire (keep one integration check, add unit tests beneath
#     it), and step B1 names the new C# method after the old check verbatim.
function Ledger-Key([string]$suite, [string]$check, $harness) {
    # BOTH unit suites, since W4 put ui-unit in AllSuites. Namespaced for the reason the
    # comment above gives about `unit`, and for a second one that is specific to the pair: the
    # two projects are different assemblies and a method name may legitimately exist in both.
    if ((UnitSuites) -contains $suite) { return "$suite/$check" }
    if ($null -ne $harness -and $harness.ContainsKey($check)) { return "$suite/$check" }
    return $check
}

function Ledger-DupProblems($sites) {
    $problems = @()
    $named = @($sites | Where-Object { -not $_.Dynamic -and $_.Check -ne '' })
    foreach ($g in ($named | Group-Object Check)) {
        if ($g.Count -le 1) { continue }
        $group = @($g.Group)
        $where = ($group | ForEach-Object { "$(Split-Path -Leaf $_.File):$($_.Line)" }) -join ' and '
        # Cross-file first: two suites both writing one name means BOTH survive at runtime
        # and the census -- which is keyed on the check name -- can only hold one of them.
        $files = @($group | Select-Object -ExpandProperty File -Unique)
        if ($files.Count -gt 1) {
            $problems += "duplicate check name '$($g.Name)' is registered by more than one suite -- $where. baseline.tsv is keyed on the CHECK NAME (plan 5.2), so one of the two cannot be represented at all"
            continue
        }
        $ok = $true
        for ($i = 0; $i -lt $group.Count -and $ok; $i++) {
            for ($k = $i + 1; $k -lt $group.Count -and $ok; $k++) {
                if (-not (Ledger-MutuallyExclusive $group[$i] $group[$k])) { $ok = $false }
            }
        }
        if (-not $ok) {
            $problems += "duplicate check name '$($g.Name)' is registered twice in one suite and the sites are NOT mutually exclusive -- $where. The second write overwrites the first: the tally comes out one lower and nothing says so"
        }
    }
    return @($problems)
}

# ---- the C# destinations -----------------------------------------------------------
#
# A moved row's destination names a test METHOD. The last-segment rule (plan 5.2) makes
# the mapping checkable without trusting the row's author: the final dotted segment must
# equal old_check character for character. So "does the destination exist" reduces to "is
# there a method by that name in that project", which a text scan answers on a tree that
# has never been built -- which is the property dev.ps1 exists for.
function Ledger-TestMethods([string]$projectDir) {
    $found = Ledger-NewSet          # C# is case-sensitive; Foo and foo are different methods
    if (-not (Test-Path $projectDir)) { return $found }
    $keywords = @('return', 'new', 'await', 'if', 'while', 'for', 'foreach', 'switch', 'using', 'lock', 'catch', 'throw')
    foreach ($f in (Get-ChildItem $projectDir -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue)) {
        if ($f.FullName -match '\\(bin|obj)\\') { continue }
        $text = [System.IO.File]::ReadAllText($f.FullName)
        foreach ($m in [regex]::Matches($text, '(?m)^[ \t]*(?:\[[^\r\n\]]*\][ \t]*)*(?:(?:public|internal|private|protected|static|async|sealed|override|virtual)[ \t]+)*([A-Za-z_][A-Za-z0-9_<>,\.\[\]\?]*)[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*\(')) {
            if ($keywords -contains $m.Groups[1].Value) { continue }
            $found[$m.Groups[2].Value] = $f.FullName
        }
    }
    return $found
}

# The Wire '<id>' { ... } block W7 introduces, normalised: whitespace collapsed, so
# reindenting is not a rewrite but narrowing an assertion is.
function Ledger-WireBody([string]$wireId) {
    foreach ($f in (Get-ChildItem "$repo\tests" -Filter '*.ps1' -ErrorAction SilentlyContinue)) {
        $tok = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tok, [ref]$errs)
        if ($errs -and @($errs).Count -gt 0) { continue }
        foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            if ($c.GetCommandName() -ne 'Wire') { continue }
            if (@($c.CommandElements).Count -lt 3) { continue }
            $a = $c.CommandElements[1]
            if ($a -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) { continue }
            if ($a.Value -ne $wireId) { continue }
            return (($c.Extent.Text -replace '\s+', ' ').Trim())
        }
    }
    return $null
}

# ---- the static rungs ---------------------------------------------------------------

# ---- the double ledger, RUNG 1: population -------------------------------------------
#
# docs/TEST-ARCHITECTURE-PLAN.md 3.2, and it is the half the first design got fatally wrong.
#
# THE QUESTION IS "WHICH TYPES IN THIS REPOSITORY ARE DOUBLES", AND IT IS ANSWERED BY READING
# THE REPO. The first design asked `Assembly.GetExecutingAssembly().GetTypes()` from
# Dodona.Tests, whose one ProjectReference is src\Dodona -- so its population contained NONE of
# the three doubles that already existed: FakeRecognizer and Poses are in src\DodonaUi (a net8.0
# project cannot load a net8.0-windows one at all) and DodonaFakeAgent is a standalone exe. It
# would have gone green because it was looking at an empty set, which is the routing ladder's
# own failure shape in the mechanism written to prevent it.
#
# A TEXT SCAN CANNOT MISS AN ASSEMBLY, because it never asks the runtime what is loaded. It also
# runs on a tree that will not compile, which is what this script exists for (CLAUDE.md 1) and
# which reflection can never have. The SEMANTIC questions -- how many implementers an interface
# has, whether a contract resolves -- a text scan answers badly, and those are rung 2, in
# tests\Dodona.Tests\Doubles and tests\Dodona.Ui.Tests.

# ---- SEEN RED, EACH ONE, BEFORE ANY OF IT WAS BELIEVED ----------------------------------
#
# CLAUDE.md 0.3: a check is worth nothing until it has been seen red against the code it is meant
# to catch. `dev prove` cannot judge dev.ps1 itself, so each of these was broken by hand against
# this tree and the refusal copied out verbatim. tests\ledger\README.md carries all eight of W4's
# reds together with the rung-2 ones; these are the five this function produced.
#
#   assertion 1  src\Dodona\RED01.cs:3 class 'FakeThingA' is a test double by its NAME and carries
#                no [Double(...)] -- every double declares what keeps it honest (plan 3.2 rung 1,
#                assertion 1)
#                ...and the same refusal from src\DodonaUi\RED02.cs, which is the point: ONE scan,
#                and src\ is inside its population. The first design's was not.
#   assertion 2  src\Dodona\RED04.cs:3 class 'MockThingD' is named Stub*/Mock*, which is refused
#                anywhere in the repo -- name it Fake*/Recording* and anchor it with [Double(...)]
#   assertion 3  src\DodonaUi\Recognizer.cs:93 [Double] on 'FakeRecognizer' names Wire
#                'voice:clicking_the_mic_toggles_listening', which resolves to no
#                tests\ledger\wires.tsv row -- the wire was deleted, renamed, or misspelled
#   assertion 4  src\DodonaShim\RED03.cs:6 declares [Double] on 'FakeThingC' but project
#                'src\DodonaShim' is in NO tests\ledger\double-assemblies.tsv row -- no reflection
#                test loads that assembly, so the anchor would never be checked
#   the issue    src\Dodona\RED08.cs:6 [Double] on 'FakeThingE' declares a KnownDivergence with no
#                Issue -- a divergence is visibility, not a catch, and an untracked gap is one
#                nobody will ever close
#
# THE CONTROL, because a refusal that fires on everything is worth nothing: the same scan over the
# real tree is clean, and prints what it found rather than only what it refused.

# A `[` that is still open across a line break, so a multi-line attribute is read whole.
# String spans are blanked first: Wire = "a[b]" must not count as a bracket.
function Doubles-Balanced([string]$text) {
    $s = [regex]::Replace($text, '"(?:[^"\\]|\\.)*"', '""')
    $open = @([regex]::Matches($s, '\[')).Count
    $close = @([regex]::Matches($s, '\]')).Count
    return ($open -le $close)
}

# Every type declaration in tracked C# under src\ and tests\, with the attributes attached to it.
#
# TRACKED, which is issue #15 in miniature: a file you have written but not `git add`ed is
# invisible here, exactly as it is to the rest of Repo-Lint. Add before you lint.
function Doubles-TypeSites {
    $sites = @()
    $decl = '^(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file|new|unsafe|readonly|ref)\s+)*' +
            '(class|struct|interface|record(?:\s+(?:class|struct))?)\s+([A-Za-z_][A-Za-z0-9_]*)'
    foreach ($rel in @(& git -C $repo ls-files 'src/*.cs' 'tests/*.cs' 2>$null)) {
        $full = Join-Path $repo $rel
        if (-not (Test-Path $full)) { continue }              # staged-deleted, still listed
        $lines = @([System.IO.File]::ReadAllLines($full))
        $block = $false; $attrs = @(); $buf = ''
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $t = "$($lines[$i])".Trim()
            if ($block) { if ($t -match '\*/') { $block = $false }; continue }
            if ($t -eq '') { continue }
            if ($t.StartsWith('//')) { continue }             # covers /// too
            if ($t.StartsWith('/*')) { if ($t -notmatch '\*/') { $block = $true }; continue }
            if ($buf -ne '') {
                $buf = $buf + ' ' + $t
                if (Doubles-Balanced $buf) { $attrs += $buf; $buf = '' }
                continue
            }
            if ($t.StartsWith('[')) {
                if (Doubles-Balanced $t) { $attrs += $t } else { $buf = $t }
                continue
            }
            $m = [regex]::Match($t, $decl)
            if ($m.Success) {
                $sites += [pscustomobject]@{
                    File = ($rel -replace '/', '\'); Line = $i + 1
                    Kind = $m.Groups[1].Value; Name = $m.Groups[2].Value; Attrs = @($attrs)
                }
            }
            # Any other code line ends the run of attributes -- they belonged to whatever it was.
            $attrs = @()
        }
    }
    return $sites
}

# The C# project a file belongs to, as a repo-relative path: the nearest ancestor holding a
# .csproj. This is what rung-1 assertion 4 resolves against double-assemblies.tsv.
function Doubles-Project([string]$rel) {
    $dir = Split-Path -Parent (Join-Path $repo $rel)
    while ($dir -and $dir.Length -gt $repo.Length) {
        if (@(Get-ChildItem -Path $dir -Filter '*.csproj' -File -ErrorAction SilentlyContinue).Count -gt 0) {
            return $dir.Substring($repo.Length).TrimStart('\')
        }
        $dir = Split-Path -Parent $dir
    }
    return ''
}

function Doubles-Field([string]$attr, [string]$name) {
    $m = [regex]::Match($attr, $name + '\s*=\s*"((?:[^"\\]|\\.)*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Doubles-Number([string]$attr, [string]$name) {
    $m = [regex]::Match($attr, $name + '\s*=\s*(-?\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

function Doubles-Static {
    # Cached: Repo-Lint asserts on it and Do-Gate reads it for the reading line, and scanning
    # the tree twice to print one sentence is the kind of tax CLAUDE.md 0.1 is about.
    if ($null -ne $script:_doublesStatic) { return $script:_doublesStatic }

    $out = [pscustomobject]@{ Problems = @(); Readings = @(); Rows = @() }
    $dir = Ledger-Dir
    $anchors = @('Interface', 'Corpus', 'Landing')

    # ---- double-assemblies.tsv: the list a rung-2 reflection test actually loads ----
    $asm = Ledger-ReadTsv (Join-Path $dir 'double-assemblies.tsv') @('project', 'assembly', 'rung2', 'note')
    $out.Problems += @($asm.Problems)
    $known = Ledger-NewSet
    if (-not $asm.Present) {
        $out.Problems += "tests\ledger\double-assemblies.tsv is MISSING -- rung-1 assertion 4 has nothing to resolve a project against, and that is the assertion that closes 'put the fake in a project the ledger does not look at'"
    }
    else {
        foreach ($r in $asm.Rows) {
            $at = "double-assemblies.tsv:$($r._line)"
            if ($known.ContainsKey($r.project)) { $out.Problems += "$at repeats project '$($r.project)'" }
            $known[$r.project] = $r
            if (-not (Test-Path (Join-Path $repo $r.project))) {
                $out.Problems += "$at names project '$($r.project)', which is not a directory in this repo"
            }
            elseif (@(Get-ChildItem -Path (Join-Path $repo $r.project) -Filter '*.csproj' -File -ErrorAction SilentlyContinue).Count -eq 0) {
                $out.Problems += "$at names project '$($r.project)', which holds no .csproj -- assertion 4 resolves a FILE to its nearest .csproj, so a row naming anything else can never be matched"
            }
            if ($r.rung2 -ne 'corpus' -and -not (Test-Path (Join-Path $repo $r.rung2))) {
                $out.Problems += "$at names rung2 '$($r.rung2)', which is neither a directory in this repo nor the word 'corpus' (plan 3.4: a file-based anchor)"
            }
        }
    }

    $wires = Ledger-ReadTsv (Join-Path $dir 'wires.tsv') @('wire_id', 'owner_suite', 'owner_check', 'owner_body_sha', 'what_it_proves', 'why_real_machinery')

    # ---- the four assertions of plan 3.2 ----
    $sites = Doubles-TypeSites
    foreach ($s in $sites) {
        $carries = @($s.Attrs | Where-Object { $_ -match '\[\s*Double\s*\(' })

        # 1. every Fake*/Recording* type carries a [Double(...)].
        if ($s.Name -match '^(Fake|Recording)' -and $carries.Count -eq 0) {
            $out.Problems += "$($s.File):$($s.Line) $($s.Kind) '$($s.Name)' is a test double by its NAME and carries no [Double(...)] -- every double declares what keeps it honest (plan 3.2 rung 1, assertion 1)"
        }

        # 2. nothing is named Stub* or Mock*, anywhere. Not a style rule: those words name a
        #    thing with no anchor at all, and this repo's word for a double that is anchored is
        #    Fake or Recording.
        if ($s.Name -match '^(Stub|Mock)') {
            $out.Problems += "$($s.File):$($s.Line) $($s.Kind) '$($s.Name)' is named Stub*/Mock*, which is refused anywhere in the repo -- name it Fake*/Recording* and anchor it with [Double(...)] (plan 3.2 rung 1, assertion 2)"
        }

        if ($carries.Count -eq 0) { continue }
        $attr = $carries[0]
        $at = "$($s.File):$($s.Line)"

        $anchor = ''
        $am = [regex]::Match($attr, '\[\s*Double\s*\(\s*Anchor\s*\.\s*([A-Za-z]+)')
        if ($am.Success) { $anchor = $am.Groups[1].Value }
        if ($anchors -notcontains $anchor) {
            $out.Problems += "$at [Double] on '$($s.Name)' does not open with a known anchor -- expected Anchor.[$($anchors -join ' ')]"
        }

        $wire = Doubles-Field $attr 'Wire'
        $contract = Doubles-Field $attr 'Contract'
        $divergence = Doubles-Field $attr 'KnownDivergence'
        $issue = Doubles-Number $attr 'Issue'
        $seam = Doubles-Number $attr 'SeamOnlyInterface'

        $out.Rows += [pscustomobject]@{
            File = $s.File; Line = $s.Line; Name = $s.Name; Anchor = $anchor
            Wire = $wire; Contract = $contract; KnownDivergence = $divergence; Issue = $issue; SeamOnlyInterface = $seam
        }

        # 3. Wire resolves to a wires.tsv row. EVERY double names a wire it does not replace
        #    (plan 3.1's second hard rule against the self-fulfilling lookup).
        if ($wire -eq '') {
            $out.Problems += "$at [Double] on '$($s.Name)' names no Wire -- every double names a wire it DOES NOT replace, still proved against the real machinery (plan 3.1)"
        }
        elseif (-not $wires.Present) {
            $out.Problems += "$at [Double] on '$($s.Name)' names Wire '$wire' but tests\ledger\wires.tsv does not exist"
        }
        else {
            $parts = @($wire -split ':', 2)
            if ($parts.Count -ne 2 -or $parts[0] -eq '' -or $parts[1] -eq '') {
                $out.Problems += "$at [Double] on '$($s.Name)' has Wire '$wire', which must be '<suite>:<check>'"
            }
            elseif (@($wires.Rows | Where-Object { $_.owner_suite -eq $parts[0] -and $_.owner_check -eq $parts[1] }).Count -eq 0) {
                $out.Problems += "$at [Double] on '$($s.Name)' names Wire '$wire', which resolves to no tests\ledger\wires.tsv row -- the wire was deleted, renamed, or misspelled"
            }
        }

        # 4. the project declaring it has rung-2 coverage. THIS IS THE ASSERTION THE FIRST DESIGN
        #    COULD NOT HAVE, and the escape it closes is where two of the three doubles live.
        $proj = Doubles-Project $s.File
        if ($proj -eq '') {
            $out.Problems += "$at declares [Double] on '$($s.Name)' but the file is under no .csproj"
        }
        elseif (-not $known.ContainsKey($proj)) {
            $out.Problems += "$at declares [Double] on '$($s.Name)' but project '$proj' is in NO tests\ledger\double-assemblies.tsv row -- no reflection test loads that assembly, so the anchor would never be checked. Give the project a rung-2 row, or move the double (plan 3.2 rung 1, assertion 4)"
        }

        # ---- and the declarations that make a gap named rather than silent ----
        if ($divergence -ne '' -and $issue -le 0) {
            $out.Problems += "$at [Double] on '$($s.Name)' declares a KnownDivergence with no Issue -- a divergence is visibility, not a catch, and an untracked gap is one nobody will ever close (plan 3.2)"
        }
        if ($divergence -eq '' -and $issue -gt 0) {
            $out.Problems += "$at [Double] on '$($s.Name)' sets Issue = $issue with no KnownDivergence to explain -- the issue number belongs to a sentence"
        }
        if ($seam -lt 0 -or ($seam -eq 0 -and $attr -match 'SeamOnlyInterface')) {
            $out.Problems += "$at [Double] on '$($s.Name)' sets SeamOnlyInterface to a value that is not an open issue number -- the shortfall it declares is counted in the gate reading, so it has to be tracked"
        }
        if ($seam -gt 0 -and $anchor -ne 'Interface') {
            $out.Problems += "$at [Double] on '$($s.Name)' sets SeamOnlyInterface on a $anchor anchor -- it declares a shortfall in the Interface implementer count and means nothing anywhere else"
        }
    }

    # ---- the readings, which are NOT assertions (plan 3.4) ----
    $anchored = @($out.Rows).Count
    # Issue -gt 0 as well as a sentence: a divergence with no issue is REFUSED above, and a
    # reading that printed '#0' beside it would be the tool describing a state it just refused.
    $div = @($out.Rows | Where-Object { $_.KnownDivergence -ne '' -and $_.Issue -gt 0 })
    $seams = @($out.Rows | Where-Object { $_.SeamOnlyInterface -gt 0 })
    $corpus = @()
    if ($asm.Present) { $corpus = @($asm.Rows | Where-Object { $_.rung2 -eq 'corpus' }) }
    $divWords = if ($div.Count -gt 0) { " (issues " + (($div | ForEach-Object { '#' + $_.Issue }) -join ' ') + ")" } else { '' }
    $seamWords = if ($seams.Count -gt 0) { " (issues " + (($seams | ForEach-Object { '#' + $_.SeamOnlyInterface }) -join ' ') + ")" } else { '' }
    $out.Readings += "doubles: $anchored anchored by attribute, $($corpus.Count) by corpus; $($div.Count) with a known divergence$divWords; $($seams.Count) on a seam-only interface$seamWords"

    $script:_doublesStatic = $out
    return $out
}

# THE CLOSED DISPOSITION VOCABULARY, IN ONE PLACE, because it was written out THREE times --
# the static rung, --slice and --verdict -- and a disposition added to one of them validates
# and then vanishes from the count. That matters most for the one below that DELETES coverage:
# a number able to hide inside a list somebody forgot to widen is the whole reason 5.5 counts
# no-seam-yet separately in the first place.
function Ledger-Dispositions { @('moved', 'merged', 'kept', 'stays', 'vacuous-guard', 'renamed', 'obsolete') }

# The EVIDENCE vocabulary for `obsolete` -- the narrow exception to "no coverage may be lost"
# opened by the operator's directive of 2026-08-22 (CLAUDE.md 0.1). Every other disposition
# keeps the assertion alive somewhere; this one ends it. So it is the one that may never rest
# on judgement: each word names a FACT a reader can check, and the note has to cite it.
#   subject-gone                  the code, config key, command or behaviour is gone (cite it)
#   cannot-fail                   structurally vacuous -- dev prove --with says VACUOUS under a
#                                 real defect in the very thing the check names
#   contradicts-current-behaviour it passes by asserting the wrong thing (cite where the right
#                                 answer lives)
#   duplicate-of                  an exact duplicate of a NAMED survivor that still runs
# "it looks redundant" is not on this list on purpose.
function Ledger-ObsoleteEvidence { @('subject-gone', 'cannot-fail', 'contradicts-current-behaviour', 'duplicate-of') }

function Ledger-Static {
    $dir = Ledger-Dir
    $out = [pscustomobject]@{ Problems = @(); Readings = @(); Sites = @(); Baseline = $null; Added = $null; Wires = $null; Moves = @() }

    $sites = Ledger-ScanChecks
    $out.Sites = $sites
    foreach ($p in @($sites | Where-Object { $_.ParseError })) {
        $out.Problems += "$(Split-Path -Leaf $p.File):$($p.Line) will not parse -- $($p.ParseError)"
    }
    $out.Problems += (Ledger-DupProblems $sites)

    $baseline = Ledger-ReadTsv (Join-Path $dir 'baseline.tsv') @('check', 'suite', 'cases')
    $added = Ledger-ReadTsv (Join-Path $dir 'added.tsv') @('check', 'suite', 'reason')
    $wires = Ledger-ReadTsv (Join-Path $dir 'wires.tsv') @('wire_id', 'owner_suite', 'owner_check', 'owner_body_sha', 'what_it_proves', 'why_real_machinery')
    $out.Baseline = $baseline; $out.Added = $added; $out.Wires = $wires
    $out.Problems += @($baseline.Problems) + @($added.Problems) + @($wires.Problems)

    if (-not $baseline.Present) {
        $out.Readings += "baseline.tsv: ABSENT -- capture it with 'dev ledger --capture' from a GREEN full run. Every accounting rung that needs it is inert until then"
    }
    if (-not $wires.Present) {
        # W1.2 builds it. A ledger that fell over because a sibling work item has not landed
        # would be an outage, not an assertion.
        $out.Readings += "wires.tsv: ABSENT (W1.2) -- wire resolution is skipped, and any row REQUIRING a wire is refused by name below"
    }

    # THE INTEGRITY PROPERTY. Without it the verdict is green-able by deleting a row, which
    # is the one edit nobody would notice in a 958-line file. Only appends by --capture, and
    # edits to suite and cases, are legal.
    if ($baseline.Present) {
        $head = @(& git -C $repo show 'HEAD:tests/ledger/baseline.tsv' 2>$null)
        if ($LASTEXITCODE -eq 0 -and $head.Count -gt 0) {
            $old = @()
            foreach ($line in $head) {
                if ("$line" -eq '' -or "$line".StartsWith('#')) { continue }
                $f = @("$line" -split "`t")
                if ($f[0] -eq 'check') { continue }
                $old += $f[0]
            }
            # Ordinal: `A_x` and `a_x` are different rows and the baseline now genuinely
            # holds both. A case-insensitive key here would let one be DELETED while the
            # other covers for it -- a removal this rung exists to refuse, passing silently.
            $now = Ledger-NewSet
            foreach ($r in $baseline.Rows) { $now[$r.check] = $true }
            $gone = @($old | Where-Object { -not $now.ContainsKey($_) })
            if ($gone.Count -gt 0) {
                $tail = if ($gone.Count -gt 5) { ' ...' } else { '' }
                $out.Problems += "baseline.tsv REMOVED OR ALTERED $($gone.Count) frozen row(s): $(($gone | Select-Object -First 5) -join ', ')$tail. The baseline is FROZEN -- only appends by --capture, and edits to suite/cases, are legal"
            }
            $out.Readings += "baseline.tsv: $(@($baseline.Rows).Count) rows, $($old.Count) frozen at HEAD"
        }
        else {
            $out.Readings += "baseline.tsv: $(@($baseline.Rows).Count) rows, NOT YET COMMITTED -- the integrity compare has nothing to compare against until it is"
        }
        # THE THIRD SITE THAT KEYS A NAME, and it must use the same key as the other two or the
        # tool contradicts itself: the capture writes a row and this rung then calls it a repeat.
        # That is exactly what happened -- Ledger-Key was applied to the capture and the
        # integrity rung, and this one still built its own case-insensitive key, so the very
        # baseline the fixed capture produced was rejected by the static rung on the next run.
        $seen = Ledger-NewSet
        $harness = Ledger-HarnessNames $sites
        foreach ($r in $baseline.Rows) {
            $key = Ledger-Key $r.suite $r.check $harness
            if ($seen.ContainsKey($key)) { $out.Problems += "baseline.tsv:$($r._line) repeats the key '$key'" }
            $seen[$key] = $true
        }
    }

    # ---- wires.tsv ----
    $wireIds = Ledger-NewSet
    if ($wires.Present) {
        foreach ($w in $wires.Rows) {
            if ($wireIds.ContainsKey($w.wire_id)) { $out.Problems += "wires.tsv:$($w._line) repeats wire id '$($w.wire_id)'" }
            $wireIds[$w.wire_id] = $w
            if ($w.owner_check -eq '') { $out.Problems += "wires.tsv:$($w._line) has no owner_check"; continue }
            $hit = @($sites | Where-Object { -not $_.Dynamic -and $_.Check -eq $w.owner_check })
            if ($hit.Count -eq 0) {
                $out.Problems += "wires.tsv:$($w._line) names owner_check '$($w.owner_check)', which no suite registers -- deleted, renamed, or misspelled"
            }
            elseif ($w.owner_suite -ne '' -and $w.owner_suite -ne '_harness' -and @($hit | Where-Object { $_.Suite -eq $w.owner_suite }).Count -eq 0) {
                $out.Problems += "wires.tsv:$($w._line) says owner_suite '$($w.owner_suite)' but '$($w.owner_check)' is registered in $(($hit | Select-Object -ExpandProperty Suite -Unique) -join ', ')"
            }
            # owner_body_sha is EMPTY until W7 creates the Wire '<id>' { } block it hashes
            # (plan 3.3.1). Set, with no block to hash, is a misconfiguration and not a pass.
            if ($w.owner_body_sha -ne '') {
                $body = Ledger-WireBody $w.wire_id
                if ($null -eq $body) {
                    $out.Problems += "wires.tsv:$($w._line) sets owner_body_sha but no Wire '$($w.wire_id)' block exists to hash (the Wire block is W7)"
                }
                elseif ((Ledger-Sha $body) -ne $w.owner_body_sha) {
                    $out.Problems += "wires.tsv:$($w._line) owner_body_sha does not match the Wire '$($w.wire_id)' block -- it was narrowed or rewritten. Re-state the row so a reviewer sees the diff beside the test diff"
                }
            }
        }
    }

    # ---- moves\<slice>.tsv ----
    $moveCols = @('old_suite', 'old_check', 'disposition', 'destination', 'wire', 'mutation', 'red_old', 'red_new', 'note')
    $dispositions = @(Ledger-Dispositions)
    $reasons = @('process-fact', 'git-ref-mutation', 'real-window', 'timing', 'absence-of-process', 'wire-shape', 'harness-hygiene', 'no-seam-yet')
    $evidence = @(Ledger-ObsoleteEvidence)
    $movesDir = Join-Path $dir 'moves'
    $unitMethods = $null; $uiMethods = $null
    $claimed = Ledger-NewSet
    if (Test-Path $movesDir) {
        foreach ($file in (Get-ChildItem $movesDir -Filter '*.tsv' -ErrorAction SilentlyContinue | Sort-Object Name)) {
            $slice = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
            $t = Ledger-ReadTsv $file.FullName $moveCols
            $out.Problems += @($t.Problems)
            foreach ($r in $t.Rows) { $r | Add-Member -NotePropertyName '_slice' -NotePropertyValue $slice }
            $out.Moves += @($t.Rows)

            foreach ($r in $t.Rows) {
                $at = "moves\$($file.Name):$($r._line)"
                if ($dispositions -notcontains $r.disposition) {
                    $out.Problems += "$at disposition '$($r.disposition)' is outside the closed vocabulary [$($dispositions -join ' ')] -- so it cannot become a shrug (D-T21)"
                    continue
                }
                if ($claimed.ContainsKey($r.old_check)) {
                    $out.Problems += "$at claims '$($r.old_check)', already claimed by $($claimed[$r.old_check]) -- a name is disposed of exactly once"
                }
                $claimed[$r.old_check] = $at
                if ($baseline.Present -and @($baseline.Rows | Where-Object { $_.check -eq $r.old_check }).Count -eq 0) {
                    $out.Problems += "$at names '$($r.old_check)', which is in no baseline.tsv row"
                }
                # REACHABILITY (D-T6): a check deleted from a .ps1 while its row survives.
                # Loop-generated names are the one exception, reachable on --live only.
                $stillThere = @($sites | Where-Object { -not $_.Dynamic -and $_.Check -eq $r.old_check }).Count -gt 0
                if (@('kept', 'stays', 'vacuous-guard') -contains $r.disposition -and -not $stillThere) {
                    $out.Problems += "$at is '$($r.disposition)' but no suite registers '$($r.old_check)' any more"
                }
                # THE MIRROR of the rung above, and it is not symmetry for its own sake. An
                # `obsolete` row is written in the commit that DELETES the check (plan 9.4 B3),
                # so a row whose check still runs is a deletion that never happened: the verdict
                # would report the coverage gone while the check is sitting there green. The two
                # rungs together mean a moves row and the suites can never disagree about whether
                # a name is alive.
                if ($r.disposition -eq 'obsolete' -and $stillThere) {
                    $out.Problems += "$at is 'obsolete' but a suite still registers '$($r.old_check)' -- an obsolete row is written in the commit that DELETES the check, never before it"
                }
                switch ($r.disposition) {
                    'moved' {
                        if ($r.mutation -eq '') { $out.Problems += "$at is 'moved' with no mutation -- a move is proved by a PAIRED RED under one checked-in mutant (D-T5)" }
                        elseif (-not (Test-Path (Join-Path $repo $r.mutation))) { $out.Problems += "$at names mutation '$($r.mutation)', which does not exist" }
                        if ($r.red_old -eq '' -or $r.red_new -eq '') { $out.Problems += "$at is 'moved' without BOTH recorded reds (red_old, red_new) -- the literal observed failure lines" }
                    }
                    'renamed' { if ($r.note -eq '') { $out.Problems += "$at is 'renamed' with no note" } }
                    'stays' {
                        if ($r.note -eq '') { $out.Problems += "$at is 'stays' with no note" }
                        else {
                            $word = @($r.note -split '[ :,]')[0]
                            if ($reasons -notcontains $word) { $out.Problems += "$at note begins '$word', which is outside the closed reason vocabulary [$($reasons -join ' ')]" }
                        }
                    }
                    'vacuous-guard' { if ($r.note -eq '') { $out.Problems += "$at is 'vacuous-guard' with no note" } }
                    # OBSOLETE -- the narrow, EVIDENCED exception to "no coverage may be lost"
                    # (operator directive 2026-08-22). Every refusal below exists because the
                    # alternative is a row reading `obsolete  it looked redundant`, which is
                    # D-T21's shrug wearing the one word that ends in a deletion.
                    'obsolete' {
                        if ($r.note -eq '') {
                            $out.Problems += "$at is 'obsolete' with NO EVIDENCE -- the note must BEGIN with one of [$($evidence -join ' ')], cite it, and carry an  if-wrong: <what is lost if this judgement is wrong>  clause. 'it looks redundant' is not evidence (operator directive 2026-08-22)"
                        }
                        else {
                            $word = @($r.note -split '[ :,]')[0]
                            if ($evidence -notcontains $word) {
                                $out.Problems += "$at note begins '$word', which is outside the closed obsolete EVIDENCE vocabulary [$($evidence -join ' ')] -- obsolete is the one disposition that REDUCES coverage, so it may never rest on judgement"
                            }
                            if ($r.note -notmatch 'if-wrong:\s*\S') {
                                $out.Problems += "$at is 'obsolete' with no  if-wrong:  clause -- every obsolete row states what is LOST if the judgement is wrong, because that sentence is what the operator is shown and what a git revert restores"
                            }
                            switch ($word) {
                                'cannot-fail' {
                                    # The STANDARD FORM of this evidence: dev prove --with returns
                                    # VACUOUS under a real defect in the very thing the check names.
                                    if ($r.mutation -eq '') { $out.Problems += "$at is 'obsolete / cannot-fail' with no mutation -- the standard evidence is dev prove --with <patch> coming back VACUOUS under a real defect in the thing the check names" }
                                    elseif (-not (Test-Path (Join-Path $repo $r.mutation))) { $out.Problems += "$at names mutation '$($r.mutation)', which does not exist" }
                                    if ($r.red_old -eq '') { $out.Problems += "$at is 'obsolete / cannot-fail' with no red_old -- record the literal VACUOUS line dev prove printed, or the evidence is an assertion about a run nobody else can see" }
                                }
                                'duplicate-of' {
                                    # 5.4's own escape clause: "unless the elsewhere is NAMED and
                                    # EXISTS". The survivor is resolved against the live suites by
                                    # the same rung `merged` uses, below.
                                    if ($r.destination -eq '') { $out.Problems += "$at is 'obsolete / duplicate-of' and names no survivor -- a duplicate is obsolete only when the elsewhere is NAMED and EXISTS (plan 5.4); destination must be 'suite:<suite>:<check>'" }
                                }
                                default {
                                    if ($r.note -notmatch '[0-9a-f]{7,40}' -and $r.note -notmatch '[\\/]') {
                                        $out.Problems += "$at is 'obsolete / $word' with no citation in the note -- name the commit that removed the subject, or the file the current behaviour lives in. A citation is what separates this from an opinion"
                                    }
                                }
                            }
                        }
                    }
                }
                if (@('kept', 'merged', 'stays') -contains $r.disposition) {
                    if ($r.wire -eq '') { $out.Problems += "$at is '$($r.disposition)' and names no wire" }
                    elseif (-not $wires.Present) { $out.Problems += "$at names wire '$($r.wire)' but tests\ledger\wires.tsv does not exist yet (W1.2)" }
                    elseif (-not $wireIds.ContainsKey($r.wire)) { $out.Problems += "$at names wire '$($r.wire)', which is in no wires.tsv row" }
                }
                if (@('moved', 'renamed', 'merged') -contains $r.disposition -and $r.destination -eq '') {
                    $out.Problems += "$at is '$($r.disposition)' with no destination"
                    continue
                }
                if (@('moved', 'renamed') -contains $r.disposition) {
                    $parts = @($r.destination -split ':', 2)
                    if ($parts.Count -ne 2 -or @('unit', 'ui-unit') -notcontains $parts[0]) {
                        $out.Problems += "$at destination '$($r.destination)' must be 'unit:<FQN>' or 'ui-unit:<FQN>' -- two prefixes because dev prove --with has to know which project to build"
                        continue
                    }
                    $leaf = @($parts[1] -split '\.')[-1]
                    $leaf = ($leaf -replace '\(.*$', '')          # a TRX theory row is name(arg: 1)
                    if ($r.disposition -eq 'moved' -and $leaf -ne $r.old_check) {
                        $out.Problems += "$at THE LAST-SEGMENT RULE: destination ends '$leaf' but old_check is '$($r.old_check)' -- they must match character for character, so a typo cannot silently orphan a name"
                    }
                    if ($parts[0] -eq 'unit') {
                        if ($null -eq $unitMethods) { $unitMethods = Ledger-TestMethods "$repo\tests\Dodona.Tests" }
                        if (-not $unitMethods.ContainsKey($leaf)) { $out.Problems += "$at destination method '$leaf' does not exist in tests\Dodona.Tests" }
                    }
                    else {
                        if ($null -eq $uiMethods) { $uiMethods = Ledger-TestMethods "$repo\tests\Dodona.Ui.Tests" }
                        if (-not $uiMethods.ContainsKey($leaf)) { $out.Problems += "$at destination method '$leaf' does not exist in tests\Dodona.Ui.Tests (that project is created in W3)" }
                    }
                }
                # `merged` names its survivor -- and so does `obsolete / duplicate-of`, through
                # the SAME rung, because the plan's escape clause is one clause: the elsewhere
                # must be NAMED and must EXIST (5.4). A name no suite registers any more is not
                # an elsewhere, whichever of the two words is written in the disposition column.
                if ($r.disposition -eq 'merged' -or ($r.disposition -eq 'obsolete' -and $r.note -match '^duplicate-of\b')) {
                    if ($r.destination -ne '') {
                        $parts = @($r.destination -split ':', 3)
                        if ($parts.Count -ne 3 -or $parts[0] -ne 'suite') {
                            $out.Problems += "$at destination '$($r.destination)' must be 'suite:<suite>:<check>' for a $($r.disposition) row"
                        }
                        else {
                            $survivor = $parts[2]
                            if (@($sites | Where-Object { -not $_.Dynamic -and $_.Check -eq $survivor }).Count -eq 0) {
                                $out.Problems += "$at names survivor '$survivor', which no suite registers -- the elsewhere must be NAMED and EXIST (plan 5.4)"
                            }
                        }
                    }
                }
            }
        }
    }

    $gen = @($sites | Where-Object { $_.Dynamic })
    if ($gen.Count -gt 0) {
        $where = (($gen | ForEach-Object { "$(Split-Path -Leaf $_.File):$($_.Line)" }) -join ', ')
        $out.Readings += "generated names: $($gen.Count) site(s) build their name at runtime ($where) -- reachable on the --live side only (plan 5.4)"
    }
    $hn = @((Ledger-HarnessNames $sites).Keys)
    if ($hn.Count -gt 0) {
        $out.Readings += "harness rows: $($hn -join ', ') -- written by tests\_workspace.ps1 into EVERY suite's results, so keyed suite+check in baseline.tsv and exempt from repo-wide name uniqueness (plan 5.4: not deduplicable)"
    }
    return $out
}

# ---- the live side -------------------------------------------------------------------
#
# RUNTIME KEYS, NOT SOURCE LINES. tests\ledger\README.md rule 2: uniqueness is a property
# of what a run actually wrote into $results, which is the only authority that agrees with
# dev.ps1's tally-is-authority rule. A name that exists in a file but never ran counts as
# nothing -- this repo has been bitten by both halves of that.
# One TRX, read into an ORDINAL set of fully-qualified method names against their case count.
# A [Theory] row's testName is the method's FQN with a parenthesised argument list appended, and
# stripping at the FIRST '(' is safe because a C# method name cannot contain one -- so no argument
# value can be mistaken for the name however it is spelled (tests\ledger\README.md, W3).
function Ledger-Trx([string]$path) {
    $r = [pscustomobject]@{ Present = $false; Methods = (Ledger-NewSet); Failed = @() }
    if (-not (Test-Path $path)) { return $r }
    $r.Present = $true
    $xml = [xml](Get-Content $path -Raw)
    foreach ($row in @($xml.TestRun.Results.UnitTestResult)) {
        if ($null -eq $row) { continue }
        $name = "$($row.testName)"
        $method = ($name -replace '\(.*$', '')
        if (-not $r.Methods.ContainsKey($method)) { $r.Methods[$method] = 0 }
        $r.Methods[$method] = $r.Methods[$method] + 1
        if ("$($row.outcome)" -ne 'Passed') { $r.Failed += $name }
    }
    return $r
}

function Ledger-Live($static) {
    # Unit is keyed by SUITE now, because there are two xunit projects since W3 and ui-unit
    # joined AllSuites at W4. W3's own note said teaching the census about the second project
    # belonged in "the commit that gives it rows to count", and this is that commit.
    $out = [pscustomobject]@{ Problems = @(); Readings = @(); Suite = @{}; Unit = @{}; Missing = @() }
    foreach ($u in (UnitSuites)) { $out.Unit[$u] = (Ledger-NewSet) }
    foreach ($s in (AllSuites)) {
        if ((UnitSuites) -contains $s) { continue }
        $f = "$repo\tests\$s-output\results.json"
        if (-not (Test-Path $f)) { $out.Missing += $s; continue }
        # ConvertFrom-Json on a JSON OBJECT gives one PSCustomObject; enumerate its
        # properties, never pipe it (CLAUDE.md 0.2 -- an array arrives as ONE pipeline item,
        # a trap that has already turned three acceptance checks into silent no-ops).
        $obj = (Get-Content $f -Raw) | ConvertFrom-Json
        $props = @($obj.PSObject.Properties)
        $keys = @($props | ForEach-Object { $_.Name })
        $fails = @($props | Where-Object { "$($_.Value)" -like 'FAIL*' } | ForEach-Object { $_.Name })
        $out.Suite[$s] = [pscustomobject]@{ Keys = $keys; Fails = $fails; When = (Get-Item $f).LastWriteTime }
    }
    foreach ($u in (UnitSuites)) {
        # Both TRXes land in tests\unit-output\ (Run-Unit passes --results-directory there, and
        # the reason is not cosmetic: dotnet test's default is tests\<project>\TestResults\,
        # which is TRACKED, and dev gate asserts that a suite run dirtied nothing).
        $t = Ledger-Trx "$repo\tests\unit-output\$u.trx"
        if (-not $t.Present) {
            $out.Readings += "$u.trx: ABSENT at tests\unit-output\$u.trx -- run 'dev test $u' (Run-Unit writes it)"
            continue
        }
        $out.Unit[$u] = $t.Methods
        $cases = (@($t.Methods.Values) | Measure-Object -Sum).Sum
        if ($null -eq $cases) { $cases = 0 }
        $out.Readings += "$u.trx: $($t.Methods.Count) methods, $cases executed cases, $($t.Failed.Count) not passed"
        if ($t.Failed.Count -gt 0) { $out.Readings += "$u.trx: NOT PASSED -- $(($t.Failed | Select-Object -First 5) -join ', ')" }
    }
    if ($out.Missing.Count -gt 0) {
        $out.Readings += "no results.json for: $($out.Missing -join ', ') -- those suites did not run"
    }

    # Cross-suite uniqueness at RUNTIME. The harness row is written into every suite's
    # $results on purpose and is the one derived exemption.
    $harness = Ledger-HarnessNames $static.Sites
    $where = Ledger-NewSet
    foreach ($s in @($out.Suite.Keys)) {
        foreach ($k in $out.Suite[$s].Keys) {
            if ($harness.ContainsKey($k)) { continue }
            if ($where.ContainsKey($k)) {
                $out.Problems += "check name '$k' was written by TWO suites in this run ($($where[$k]) and $s) -- baseline.tsv is keyed on the check name and can hold only one"
            }
            else { $where[$k] = $s }
        }
    }

    if ($static.Baseline.Present) {
        $base = Ledger-NewSet
        foreach ($r in $static.Baseline.Rows) { $base[(Ledger-Key $r.suite $r.check $harness)] = $r }
        $declared = Ledger-NewSet
        if ($static.Added.Present) { foreach ($r in $static.Added.Rows) { $declared[(Ledger-Key $r.suite $r.check $harness)] = $r } }
        $undeclared = @()
        foreach ($s in @($out.Suite.Keys)) {
            foreach ($k in $out.Suite[$s].Keys) {
                $key = Ledger-Key $s $k $harness
                if ($base.ContainsKey($key) -or $declared.ContainsKey($key)) { continue }
                $undeclared += "${s}:$k"
            }
        }
        foreach ($u in (UnitSuites)) {
            foreach ($m in @($out.Unit[$u].Keys)) {
                $leaf = @($m -split '\.')[-1]
                $key = Ledger-Key $u $leaf $harness
                if ($base.ContainsKey($key) -or $declared.ContainsKey($key)) { continue }
                $undeclared += "${u}:$m"
            }
        }
        if ($undeclared.Count -gt 0) {
            $tail = if ($undeclared.Count -gt 8) { ' ...' } else { '' }
            $out.Problems += "$($undeclared.Count) name(s) ran and are in neither baseline.tsv nor added.tsv: $(($undeclared | Select-Object -First 8) -join ', ')$tail. Growth is DECLARED, so a loss cannot hide inside it"
        }
    }
    return $out
}

# ---- --capture ------------------------------------------------------------------------

function Ledger-Capture($static) {
    $live = Ledger-Live $static
    $expected = @((AllSuites) | Where-Object { (UnitSuites) -notcontains $_ })
    if ($live.Missing.Count -gt 0) {
        Abort "capture needs a FULL run: no results.json for $($live.Missing -join ', ')" "powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 gate"
    }
    foreach ($u in (UnitSuites)) {
        if ($live.Unit[$u].Count -eq 0) {
            Abort "capture needs the $u TRX: tests\unit-output\$u.trx is absent or empty" "powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 test $u"
        }
    }
    $red = @()
    foreach ($s in $expected) { if ($live.Suite[$s].Fails.Count -gt 0) { $red += "${s}: $($live.Suite[$s].Fails -join ', ')" } }
    if ($red.Count -gt 0) {
        # A census taken from a red run freezes names whose meaning nobody has established.
        foreach ($r in $red) { Say "  RED  $r" }
        Abort "capture refuses a run with failures -- the baseline is frozen from a GREEN full run, only" "fix the failures, run the gate again, then dev ledger --capture"
    }

    $rows = @()
    $existing = Ledger-NewSet
    $harness = Ledger-HarnessNames $static.Sites
    if ($static.Baseline.Present) {
        foreach ($r in $static.Baseline.Rows) {
            $existing[(Ledger-Key $r.suite $r.check $harness)] = $true
            $rows += [pscustomobject]@{ check = $r.check; suite = $r.suite; cases = $r.cases }
        }
    }
    $new = 0
    foreach ($s in $expected) {
        foreach ($k in @($live.Suite[$s].Keys | Sort-Object)) {
            $key = Ledger-Key $s $k $harness
            if ($existing.ContainsKey($key)) { continue }
            $existing[$key] = $true
            $rows += [pscustomobject]@{ check = $k; suite = $s; cases = '1' }
            $new++
        }
    }
    # A unit leaf that two FQNs share is a REAL collapse and must be reported, not skipped in
    # silence: `A_trailing_separator_is_not_a_different_folder` exists in both
    # InstanceCanonicalTests and ProjectResolutionTests, and the census can hold one row.
    foreach ($u in (UnitSuites)) {
        $seenLeaf = Ledger-NewSet          # per PROJECT: two assemblies may hold one leaf name
        foreach ($m in @($live.Unit[$u].Keys | Sort-Object)) {
            $leaf = @($m -split '\.')[-1]
            if ($seenLeaf.ContainsKey($leaf)) {
                Say "  NOTE two $u methods share the leaf name '$leaf' ($($seenLeaf[$leaf]) and $m) -- the census holds one row; rename one to count both"
                continue
            }
            $seenLeaf[$leaf] = $m
            $key = Ledger-Key $u $leaf $harness
            if ($existing.ContainsKey($key)) { continue }
            $existing[$key] = $true
            $rows += [pscustomobject]@{ check = $leaf; suite = $u; cases = "$($live.Unit[$u][$m])" }
            $new++
        }
    }
    New-Item -ItemType Directory -Force (Ledger-Dir) | Out-Null
    Ledger-WriteTsv (Join-Path (Ledger-Dir) 'baseline.tsv') @('check', 'suite', 'cases') @($rows | Sort-Object suite, check)
    $cases = (@($rows | ForEach-Object { [int]("0" + $_.cases) }) | Measure-Object -Sum).Sum
    Say "captured $(@($rows).Count) name(s) into tests\ledger\baseline.tsv ($new new)"
    Say "  suite checks:   $(@($rows | Where-Object { (UnitSuites) -notcontains $_.suite }).Count)"
    foreach ($u in (UnitSuites)) { Say "  $u methods:$(' ' * [Math]::Max(1, 8 - $u.Length))$(@($rows | Where-Object { $_.suite -eq $u }).Count)" }
    Say "  executed cases: $cases"
    Say ""
    Say "COMMIT IT. A baseline that is not in git has nothing to be frozen against, and the"
    Say "integrity rung says so on every run until it is."
}

# ---- --slice / --verdict / --origin ---------------------------------------------------

function Ledger-Slice($static, [string]$name) {
    $rows = @($static.Moves | Where-Object { $_._slice -eq $name })
    if ($rows.Count -eq 0) {
        $known = @($static.Moves | Select-Object -ExpandProperty _slice -Unique)
        if ($known.Count -gt 0) { Say "no slice '$name' -- known: $($known -join ', ')" }
        else { Say "no slice '$name' -- tests\ledger\moves\ is empty or absent" }
        return
    }
    Say "slice $name -- $($rows.Count) row(s)"
    foreach ($d in @(Ledger-Dispositions)) {
        $these = @($rows | Where-Object { $_.disposition -eq $d })
        if ($these.Count -eq 0) { continue }
        Say ""
        Say "  $d ($($these.Count))"
        foreach ($r in $these) {
            $proof = switch ($d) {
                'moved' { if ($r.red_old -and $r.red_new -and $r.mutation) { "PAIRED RED under $($r.mutation)" } else { 'NOT PROVED' } }
                'merged' { "into $($r.destination) on $($r.wire)" }
                'obsolete' { "COVERAGE REMOVED -- $(@($r.note -split '[ :,]')[0])" }
                default { if ($r.wire) { "on $($r.wire)" } else { $r.note } }
            }
            Say ("    {0,-58} {1}" -f "$($r.old_suite):$($r.old_check)", $proof)
        }
    }
}

function Ledger-Verdict($static) {
    $b = $static.Baseline
    $moves = @($static.Moves)
    $sites = @($static.Sites | Where-Object { -not $_.Dynamic -and $_.Check -ne '' })
    $liveNames = @($sites | Select-Object -ExpandProperty Check -Unique)
    $by = @{}
    foreach ($d in @(Ledger-Dispositions)) { $by[$d] = @($moves | Where-Object { $_.disposition -eq $d }).Count }
    $accounted = @{}
    foreach ($r in $moves) { $accounted[$r.old_check] = $true }
    $unaccounted = 0
    if ($b.Present) { $unaccounted = @($b.Rows | Where-Object { -not $accounted.ContainsKey($_.check) }).Count }

    $reasonCounts = @{}
    foreach ($r in @($moves | Where-Object { $_.disposition -eq 'stays' })) {
        $w = @($r.note -split '[ :,]')[0]
        if (-not $reasonCounts.ContainsKey($w)) { $reasonCounts[$w] = 0 }
        $reasonCounts[$w] = $reasonCounts[$w] + 1
    }
    $noSeam = if ($reasonCounts.ContainsKey('no-seam-yet')) { $reasonCounts['no-seam-yet'] } else { 0 }

    # OBSOLETE GETS ITS OWN LINE AND ITS OWN BREAKDOWN. It is the only disposition that ends an
    # assertion rather than relocating it, so folding it into `moved` or `stays` would make the
    # one number that means "coverage went down" invisible inside a number that means the
    # opposite. Same reasoning as no-seam-yet's separate count (D-T21), one step sharper.
    $obsCounts = @{}
    foreach ($r in @($moves | Where-Object { $_.disposition -eq 'obsolete' })) {
        $w = @($r.note -split '[ :,]')[0]
        if (-not $obsCounts.ContainsKey($w)) { $obsCounts[$w] = 0 }
        $obsCounts[$w] = $obsCounts[$w] + 1
    }

    $frozenAt = "$(& git -C $repo log -1 --format=%h -- 'tests/ledger/baseline.tsv' 2>$null)"
    if (-not $frozenAt) { $frozenAt = 'NOT COMMITTED' }
    $baseCount = if ($b.Present) { @($b.Rows).Count } else { 0 }
    $suiteRows = if ($b.Present) { @($b.Rows | Where-Object { $_.suite -ne 'unit' }).Count } else { 0 }
    $unitRows = if ($b.Present) { @($b.Rows | Where-Object { $_.suite -eq 'unit' }).Count } else { 0 }
    $cases = 0
    if ($b.Present) {
        $cases = (@($b.Rows | ForEach-Object { [int]("0" + $_.cases) }) | Measure-Object -Sum).Sum
        if ($null -eq $cases) { $cases = 0 }
    }
    $addedCount = if ($static.Added.Present) { @($static.Added.Rows).Count } else { 0 }

    $wireRows = if ($static.Wires.Present) { @($static.Wires.Rows).Count } else { 0 }
    $wireNote = if ($static.Wires.Present) { '' } else { '   <- wires.tsv does not exist yet (W1.2)' }
    $surviving = @((AllSuites) | Where-Object { $_ -ne 'unit' }).Count
    $target = $wireRows + $surviving

    Say "LEDGER"
    Say ("  baseline            {0} names, frozen at {1}   ({2} suite + {3} unit methods; {4} cases)" -f $baseCount, $frozenAt, $suiteRows, $unitRows, $cases)
    Say ("  live in suite       {0}" -f $liveNames.Count)
    Say ("  moved to unit       {0}   (each with a mutant and two recorded reds)" -f $by['moved'])
    Say ("  merged into         {0}   (each naming a LIVE survivor and a wire)" -f $by['merged'])
    $rs = @()
    foreach ($w in @('process-fact', 'git-ref-mutation', 'real-window', 'timing', 'absence-of-process', 'wire-shape', 'harness-hygiene')) {
        $rs += "$w $(if ($reasonCounts.ContainsKey($w)) { $reasonCounts[$w] } else { 0 })"
    }
    Say ("  stays               {0}   by reason: {1}" -f ($by['stays'] + $by['kept']), ($rs -join ', '))
    Say ("  stays (no-seam-yet)   {0}   <- MUST BE 0, or every one carries an issue number" -f $noSeam)
    Say ("  vacuous-guard         {0}   (kept and labelled, by decision)" -f $by['vacuous-guard'])
    $os = @()
    foreach ($w in @(Ledger-ObsoleteEvidence)) { $os += "$w $(if ($obsCounts.ContainsKey($w)) { $obsCounts[$w] } else { 0 })" }
    Say ("  obsolete              {0}   <- REDUCES COVERAGE, never folded into moved or stays." -f $by['obsolete'])
    Say ("                            by evidence: {0}" -f ($os -join ', '))
    Say ("  unaccounted           {0}   <- MUST BE 0" -f $unaccounted)
    Say ("  added (declared)      {0}" -f $addedCount)
    Say "INTEGRATION CHECKS"
    Say ("  wires.tsv rows       {0}{1}" -f $wireRows, $wireNote)
    Say ("  harness rows         {0}   (one per surviving suite; not deduplicable)" -f $surviving)
    Say ("  live integration     {0}   target {1}   <- MUST BE <= target" -f $liveNames.Count, $target)
    # READ THE REAL NUMBERS. These three lines said "W4 builds the double ledger; there is
    # nothing to read yet" for one commit AFTER W4 built it -- while `dev lint`, on the same
    # tree, printed "doubles: 2 anchored by attribute, 1 by corpus". A verdict block that
    # contradicts the lint beside it teaches people to trust neither, which is the same disease
    # as a gate that is always green. It went stale because W5's commit B is scoped to tests    # and could not reach this file; that is a good rule and this is its cost, paid here.
    $dbl = Doubles-Static
    # The SAME reader and the SAME column list as the lint rung (see Doubles-Static's use of
    # double-assemblies.tsv), so the two readings cannot drift apart. The first draft of this
    # block called a Doubles-Assemblies that does not exist: PowerShell resolved it to nothing,
    # the corpus count came back 0, and the verdict contradicted the lint on the same tree --
    # which is the exact defect this block was rewritten to remove.
    $asmRows = Ledger-ReadTsv (Join-Path (Ledger-Dir) 'double-assemblies.tsv') @('project', 'assembly', 'rung2', 'note')
    $anch = @($dbl.Rows).Count
    $corp = 0
    if ($asmRows.Present) { $corp = @($asmRows.Rows | Where-Object { $_.rung2 -eq 'corpus' }).Count }
    $known = @($dbl.Rows | Where-Object { $_.KnownDivergence -ne '' -and $_.Issue -gt 0 })
    $seamOnly = @($dbl.Rows | Where-Object { $_.SeamOnlyInterface -gt 0 })
    Say "DOUBLES"
    Say ("  anchored              {0}   ({1} by attribute, {2} by corpus)" -f ($anch + $corp), $anch, $corp)
    Say ("  known divergence      {0}{1}   <- each MUST carry an open issue" -f $known.Count,
         $(if ($known.Count -gt 0) { "  " + (($known | ForEach-Object { '#' + $_.Issue }) -join ' ') } else { '' }))
    Say ("  seam-only interface   {0}{1}" -f $seamOnly.Count,
         $(if ($seamOnly.Count -gt 0) { "  " + (($seamOnly | ForEach-Object { '#' + $_.SeamOnlyInterface }) -join ' ') } else { '' }))
    Say "  unwitnessed shapes    -   <- issue #18: the corpus witnesses 6 of the parser's ~10 shapes"
    Say "VERDICT: on the accounting above, and only that."
}

# git log -S over tests\: which commit changed the number of occurrences of a name, and the
# lines it took with it. The paired red proves CO-SENSITIVITY, not equivalence (plan 5.3) --
# a check that asserted three things and now asserts one still passes. Closing that gap
# means READING the old body, so this makes reading it one command rather than archaeology.
function Ledger-Origin([string]$check) {
    Say "== origin: $check =="
    $commits = @(& git -C $repo log --format=%H -S $check -- 'tests/' 2>$null)
    $commits = @($commits | Where-Object { $_ -match '^[0-9a-f]{7,}$' })
    if ($commits.Count -eq 0) { Say "no commit under tests\ ever changed the number of occurrences of '$check'"; return }
    Say "$($commits.Count) commit(s), newest first:"
    foreach ($c in $commits) {
        Say ""
        Say "  $(& git -C $repo log -1 --format='%h %ad %s' --date=short $c 2>$null)"
        $diff = @(& git -C $repo show $c --format='' --unified=0 -S $check -- 'tests/' 2>$null)
        foreach ($line in @($diff | Where-Object { "$_" -match [regex]::Escape($check) })) {
            Say "    $("$line".TrimEnd())"
        }
    }
    Say ""
    Say "The first commit listed is the one that removed it, if it is gone. git show <sha> for the whole body."
}

function Do-Ledger {
    # ValueFromRemainingArguments hands back $null with no arguments, and @($null) has a
    # Count of ONE (CLAUDE.md 0.2's .Count trap in its other direction) -- so a bare
    # `dev ledger` came out as one unknown empty argument.
    $flags = @($Rest | Where-Object { "$_" -ne '' })
    $wantLive = @($flags | Where-Object { $_ -eq '--live' }).Count -gt 0
    $wantCapture = @($flags | Where-Object { $_ -eq '--capture' }).Count -gt 0
    $wantVerdict = @($flags | Where-Object { $_ -eq '--verdict' }).Count -gt 0
    $slice = ''; $origin = ''; $unknown = @()
    for ($i = 0; $i -lt $flags.Count; $i++) {
        $a = $flags[$i]
        if ($a -eq '--live' -or $a -eq '--capture' -or $a -eq '--verdict') { continue }
        if ($a -eq '--slice' -and $i + 1 -lt $flags.Count) { $slice = $flags[$i + 1]; $i++; continue }
        if ($a -eq '--origin' -and $i + 1 -lt $flags.Count) { $origin = $flags[$i + 1]; $i++; continue }
        $unknown += $a
    }
    if ($unknown.Count -gt 0) {
        Abort "unknown argument(s): $($unknown -join ' ')" "dev ledger [--live] [--capture] [--slice <name>] [--verdict] [--origin <check>]"
    }

    if ($origin) { Ledger-Origin $origin; Say "log: $log"; return }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Say "== ledger =="
    $static = Ledger-Static
    $problems = @($static.Problems)
    $readings = @($static.Readings)

    if ($wantCapture) {
        if ($problems.Count -gt 0) {
            foreach ($p in $problems) { Say "  $p" }
            Abort "$($problems.Count) static problem(s) -- capture will not freeze a census over a repo that fails its own rungs" "fix the problems above, then dev ledger --capture"
        }
        Ledger-Capture $static
        Say "log: $log"
        return
    }

    if ($wantLive) {
        $live = Ledger-Live $static
        $problems += @($live.Problems)
        $readings += @($live.Readings)
    }

    foreach ($r in $readings) { Say "  note: $r" }
    if ($readings.Count -gt 0) { Say "" }

    if ($slice) { Ledger-Slice $static $slice; Say "" }
    if ($wantVerdict) { Ledger-Verdict $static; Say "" }

    $sw.Stop()
    if ($problems.Count -eq 0) {
        if (-not $slice -and -not $wantVerdict) {
            Say "clean: $(@($static.Sites | Where-Object { -not $_.Dynamic }).Count) registration site(s), no duplicate name, every ledger row resolves"
        }
        Say ("{0:N2}s" -f $sw.Elapsed.TotalSeconds)
        Say "log: $log"
    }
    else {
        foreach ($p in $problems) { Say "  $p" }
        Say ""
        Say "$($problems.Count) problem(s)"
        Say "log: $log"
        exit 1
    }
}

function Do-Help {
    Say "dev.ps1 -- the only door for mechanical work here (CLAUDE.md 0.3 / 1)"
    Say ""
    Say "  check                    can this tree build? what is in the way? seconds."
    Say "  build                    build. names any holder of the output; stops nothing itself."
    Say "  test <suite> [...]       run named suite(s). isolated, self-cleaning."
    Say "  suites                   run all $((AllSuites).Count). end of a change, once."
    Say "  prove <suite> <check>    demand a new check FAILS against HEAD. do this BEFORE"
    Say "                           believing any new check."
    Say "  gate [suite...]          the pre-commit gate: suites, then ASSERT the invariants"
    Say "                           Phase 1 earns. Names the six it does not cover yet."
    Say "                           With suite names: a PARTIAL self-test of the gate itself,"
    Say "                           seconds instead of minutes. Never a gate verdict."
    Say "  ledger [--live] [--capture] [--slice <n>] [--verdict] [--origin <check>]"
    Say "                           the check-name ledger: every check accounted for, no"
    Say "                           name lost. STATIC by default -- ~1 s, builds nothing,"
    Say "                           runs on a tree that will not compile."
    Say "  ship                     build + suites + publish."
    Say "  worktree <name>          a tree of your own under .claude\worktrees\. ALL work"
    Say "                           goes in one: the shared checkout refuses agent writes"
    Say "                           and refuses commits (D-7)."
    Say ""
    Say "Every run logs to .dodona\dev-logs. A blocked run stops on line one, not minute forty."
}

# Enforcement layer 2 is installed before ANY verb runs, including `help`. Every path into
# this repo's mechanical work goes through this script, so this is the one place that can
# guarantee the hook exists without anyone remembering to put it there (D-7 item 2).
Install-Hooks

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
        if ($l.Count -eq 0) { Say "clean: no control bytes, every named test path real, every ledger row resolves, every double anchored" }
        else { foreach ($x in $l) { Say "  $x" }; Say ""; Say "$($l.Count) problem(s)"; exit 1 }
    }
    'gate' { Do-Gate }
    'ledger' { Do-Ledger }
    'ship' { Do-Ship }
    'worktree' { Do-Worktree }
    'help' { Do-Help }
}
