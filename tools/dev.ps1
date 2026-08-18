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
    [ValidateSet('check', 'build', 'test', 'suites', 'prove', 'gate', 'ship', 'worktree', 'help')]
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
    'm0', 'm1', 'm2', 'm3', 'm4', 'workspace', 'ui-use', 'compression', 'brain', 'concierge', 'publish'
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

function ReportBlockers {
    $b = Blockers
    if ($b.Count -eq 0) { Say "blockers: none"; return }

    Say "in the build output: $($b.Count) process(es) -- may or may not block, the build decides"
    foreach ($p in $b) { Say "  pid $($p.Id)  $($p.ProcessName)  $($p.Path)" }
}

# ---------------------------------------------------------------- enforcement layer 2

# Install .claude/hooks/pre-commit into the repository's real hooks directory.
#
# Runs on EVERY dev invocation, on purpose. .git/hooks is not versioned, so a tracked hook
# file is inert until something copies it -- and an install step a person has to remember is
# not enforcement, it is a documented warning wearing a filename (CLAUDE.md 0.3, D-6). This
# is the same move DeployGate makes with .git/info/exclude: deploy it, do not ask.
#
# PER-REPOSITORY, never `git config --global core.hooksPath`. All 11 suites build temp repos
# and run `git commit -m init` inside them, and every one of those is a main checkout -- a
# global install would refuse all of them and turn the whole suite red. Checked before
# writing this.
#
# Quiet when already correct: a line on every `dev check` would be noise, and noise is how a
# real line gets skipped.
# The MAIN checkout, whichever tree is asking. $repo is $PSScriptRoot's parent, which inside
# a worktree is the WORKTREE -- so anything that must be repo-wide (where worktrees live, what
# the shared checkout IS) has to come from the git COMMON dir instead.
function MainCheckout {
    $common = (& git -C $repo rev-parse --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $common) { return $null }
    if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $repo $common }
    return (Split-Path -Parent ([System.IO.Path]::GetFullPath($common)))
}

# Every hook script must PARSE. This is not a nicety: a .ps1 that fails to parse runs nothing,
# emits a parse error, and exits non-zero -- which for a PreToolUse hook means it denies
# NOTHING while still sitting there looking installed. It happened while writing layer 1: one
# line of the refusal text ended in a backtick, PowerShell's escape character, which swallowed
# the here-string terminator. The hook was dead and the only symptom was a parse error nobody
# was reading (CLAUDE.md 0.2, and section 3's rule that a silent degrade is a bug).
function Test-HookParses([string]$file) {
    $e = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$e)
    if ($e -and $e.Count -gt 0) { return $e[0].Message }
    return $null
}

function Install-Hooks {
    $marker = 'Dodona enforcement layer 2'
    $src = "$repo\.claude\hooks\pre-commit"

    # Layer 1's scripts are registered in .claude/settings.json and run by the harness, not by
    # this script -- so this is the only place that ever looks at them. Check them here.
    foreach ($h in @(Get-ChildItem "$repo\.claude\hooks\*.ps1" -ErrorAction SilentlyContinue)) {
        $err = Test-HookParses $h.FullName
        if ($err) {
            Say "WARNING: $($h.Name) DOES NOT PARSE -- it is enforcing nothing: $err"
        }
    }

    if (-not (Test-Path $src)) { return }        # a checkout that predates the hook

    # A worktree's hooks live in the COMMON dir, shared with the main checkout -- which is
    # exactly what we want: one install covers every session's worktree. If the repo sets
    # core.hooksPath, .git\hooks is ignored by git, so installing there would be a silent
    # no-op. Follow the setting instead of quietly doing nothing.
    $hooksPath = (& git -C $repo config --get core.hooksPath 2>$null)
    if ($LASTEXITCODE -eq 0 -and $hooksPath) {
        $dir = if ([System.IO.Path]::IsPathRooted($hooksPath)) { $hooksPath } else { Join-Path $repo $hooksPath }
    }
    else {
        $common = (& git -C $repo rev-parse --git-common-dir 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not $common) { return }
        if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $repo $common }
        $dir = Join-Path $common 'hooks'
    }

    $dst = Join-Path $dir 'pre-commit'
    $want = [System.IO.File]::ReadAllText($src)
    if (Test-Path $dst) {
        $have = [System.IO.File]::ReadAllText($dst)
        if ($have -eq $want) { return }                                  # already current
        if ($have -notmatch [regex]::Escape($marker)) {
            # Never clobber somebody else's hook silently. Say it every run: an unenforced
            # layer 2 that nobody mentions is the silent degrade CLAUDE.md 3 calls a bug.
            Say "WARNING: $dst exists and is not Dodona's -- layer 2 is NOT installed."
            Say "         Merge .claude\hooks\pre-commit into it by hand, or move it aside."
            return
        }
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
    # LF and no BOM: git runs this through sh, which chokes on CRLF and on a BOM.
    $w = New-Object System.IO.StreamWriter($dst, $false, (New-Object System.Text.UTF8Encoding($false)))
    $w.NewLine = "`n"
    $w.Write($want.Replace("`r`n", "`n"))
    $w.Close()
    Say "installed enforcement layer 2: $dst"
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

function Run-Suite([string]$name) {
    $f = "$repo\tests\$name-acceptance.ps1"
    if (-not (Test-Path $f)) { Abort "no suite '$name'" "one of: $((AllSuites) -join ', ')" }
    $o = & powershell -NoProfile -ExecutionPolicy Bypass -File $f 2>&1
    Add-Content -Path $log -Value "===== $name =====" -Encoding utf8
    Add-Content -Path $log -Value $o -Encoding utf8
    $fails = @($o | Select-String -Pattern ': FAIL' | ForEach-Object { $_.Line.Trim() })
    $tally = ($o | Select-String -Pattern '^\d+ checks,' | Select-Object -Last 1)
    [pscustomobject]@{ Name = $name; Fails = $fails; Tally = if ($tally) { $tally.Line.Trim() } else { 'no tally line' } }
}

function Do-Test {
    if (-not $Rest -or $Rest.Count -eq 0) { Abort "which suite?" "dev test m3   (one of: $((AllSuites) -join ', '))" }
    Say "== test: $($Rest -join ', ') =="
    $bad = 0
    foreach ($n in $Rest) {
        $r = Run-Suite $n
        Say "$($r.Name): $($r.Tally)"
        foreach ($f in $r.Fails) { Say "  $f"; $bad++ }
    }
    Say "log: $log"
    if ($bad -gt 0) { exit 1 }
}

function Do-Suites {
    Say "== suites: all =="
    $bad = 0
    foreach ($n in AllSuites) {
        $r = Run-Suite $n
        Say "$($r.Name.PadRight(12)) $($r.Tally)"
        foreach ($f in $r.Fails) { Say "  $f"; $bad++ }
    }
    Say ""
    Say $(if ($bad -eq 0) { "ALL SUITES GREEN" } else { "$bad FAILED CHECK(S)" })
    Say "log: $log"
    if ($bad -gt 0) { exit 1 }
}

# prove: the mechanism for the second half of the 2026-08-18 lesson. A new acceptance check
# is worth nothing until it has been SEEN RED against the code it is supposed to catch. This
# builds HEAD (i.e. the tree WITHOUT your uncommitted fix) in a throwaway git worktree, runs
# the named suite there, and demands that the named check FAILS. If it passes, the check is
# vacuous -- exactly the trap that made a bad respawn fix look verified.
function Do-Prove {
    if (-not $Rest -or $Rest.Count -lt 2) { Abort "need a suite and a check name" "dev prove m3 respawned_ticket_lane_returns_to_its_worktree" }
    $suite = $Rest[0]; $check = $Rest[1]
    Say "== prove: '$check' must FAIL against HEAD =="

    $dirty = @(git -C $repo status --porcelain -- 'src' 'tests')
    if ($dirty.Count -eq 0) { Abort "src and tests are identical to HEAD, so there is no change to prove" "make the fix first, leave it uncommitted, then run prove" }

    $wt = Join-Path $env:TEMP ("dodona-prove-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Say "worktree of HEAD: $wt"
    git -C $repo worktree add --detach $wt HEAD 2>&1 | ForEach-Object { Say "  $_" }
    try {
        # The TEST comes from your working tree (it is the new check); the CODE comes from
        # HEAD (it is the code that must fail it). That is the whole trick.
        Copy-Item "$repo\tests\*" "$wt\tests\" -Recurse -Force
        Say "building HEAD ..."
        $b = & dotnet build "$wt\Dodona.sln" -c Release 2>&1
        Add-Content -Path $log -Value $b -Encoding utf8
        if ($LASTEXITCODE -ne 0) { Abort "HEAD does not build, so it cannot be used as a baseline" "commit a buildable baseline first; see $log" }

        $o = & powershell -NoProfile -ExecutionPolicy Bypass -File "$wt\tests\$suite-acceptance.ps1" 2>&1
        Add-Content -Path $log -Value $o -Encoding utf8
        $line = @($o | Select-String -Pattern ([regex]::Escape($check) + ':') | Select-Object -First 1)
        if ($line.Count -eq 0) {
            Abort "check '$check' never ran against HEAD" "the check must exist in your working tests/ AND be reached on this code path; see $log"
        }
        $txt = $line[0].Line.Trim()
        Say "against HEAD: $txt"
        if ($txt -match ': FAIL') {
            Say ""
            Say "PROVEN: the check fails without your change, so it has teeth."
        }
        else {
            Say ""
            Say "VACUOUS: the check PASSES against HEAD, so it does not test your change."
            Say "         Rewrite it before trusting it. (This is the exact trap of 2026-08-18.)"
            Say "log: $log"
            exit 1
        }
    }
    finally {
        git -C $repo worktree remove --force $wt 2>&1 | ForEach-Object { Say "  $_" }
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
# Phase 1 earned two and Phase 2a earns two more. The rest are printed as "not yet", named
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

    Say ""
    Say "-- suites ($(@($suites).Count) of $((AllSuites).Count)) --"
    foreach ($n in $suites) {
        $r = Run-Suite $n
        Say "$($n.PadRight(12)) $($r.Tally)"
        foreach ($f in $r.Fails) { Say "  $f"; $bad++ }
    }

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

    # ENFORCEMENT IS ALIVE. Not one of RECOVERY-PHASES section 2's eight rows -- it is Phase
    # 2a's own guard against the failure that phase is built on: an enforcement that stops
    # enforcing without saying so. The routing ladder was fully covered, fully green and DEAD
    # IN PRODUCTION for two days (CLAUDE.md section 3), and layer 1 spent part of this session
    # in exactly that state: a backtick swallowed a here-string terminator, the hook stopped
    # parsing, and it denied nothing while still being registered. So the gate asks the hook
    # the real question, through the real code path, and demands both answers.
    $hook = "$repo\.claude\hooks\no-main-tree.ps1"
    $main = MainCheckout
    if (-not (Test-Path $hook) -or -not $main) {
        Say "  FAIL  D-7 layer 1 hook not found ($hook)"
        $bad++
    }
    else {
        $perr = Test-HookParses $hook
        if ($perr) {
            Say "  FAIL  D-7 layer 1 does not parse, so it denies nothing: $perr"
            $bad++
        }
        else {
            # A real worktree is needed for the counter-case, because the test walks up to the
            # nearest .git and a made-up path would walk up into the main checkout and be
            # refused -- which would look like a pass for the wrong reason. --no-checkout keeps
            # it instant: the .git FILE is all this needs.
            $wtL = Join-Path $env:TEMP ("dodona-gate-l1-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
            $madeL = $false
            try {
                & git -C $repo worktree add --detach --no-checkout $wtL HEAD 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 }
                $madeL = ($LASTEXITCODE -eq 0)

                function Ask-Hook([string]$path) {
                    $payload = @{ tool_name = 'Edit'; tool_input = @{ file_path = $path } } | ConvertTo-Json -Compress -Depth 5
                    return ($payload | & powershell -NoProfile -ExecutionPolicy Bypass -File $hook 2>&1 | Out-String)
                }

                $denied = Ask-Hook (Join-Path $main 'src\Dodona\Ver.cs')
                $allowed = if ($madeL) { Ask-Hook (Join-Path $wtL 'src\Dodona\Ver.cs') } else { 'NO WORKTREE' }

                $okDeny = $denied -match '"permissionDecision"\s*:\s*"deny"'
                $okAllow = $madeL -and ($allowed.Trim().Length -eq 0)
                if ($okDeny -and $okAllow) {
                    Say "  PASS  D-7  layer 1 refuses a write to the shared checkout, allows one in a worktree"
                }
                else {
                    Say "  FAIL  D-7  layer 1 is not enforcing:"
                    if (-not $okDeny) { Say "          shared checkout was NOT denied; hook said: $($denied.Trim())" }
                    if (-not $okAllow) { Say "          worktree was NOT allowed; hook said: $($allowed.Trim())" }
                    $bad++
                }
            }
            finally {
                if ($madeL) { & git -C $repo worktree remove --force $wtL 2>&1 | ForEach-Object { Add-Content -Path $log -Value $_ -Encoding utf8 } }
            }
        }
    }

    # Layer 2 is installed and is the file this repo ships. Install-Hooks writes it on every
    # dev run, so this asserts that the write actually happened -- and catches the one case it
    # deliberately refuses to touch: somebody else's pre-commit hook already sitting there.
    $l2 = if (MainCheckout) { Join-Path (MainCheckout) '.git\hooks\pre-commit' } else { $null }
    $l2src = "$repo\.claude\hooks\pre-commit"
    if ($l2 -and (Test-Path $l2) -and (Test-Path $l2src) -and
        ([System.IO.File]::ReadAllText($l2).Replace("`r`n", "`n") -eq [System.IO.File]::ReadAllText($l2src).Replace("`r`n", "`n"))) {
        Say "  PASS  D-7  layer 2 (git pre-commit) is installed and current"
    }
    else {
        Say "  FAIL  D-7  layer 2 is not installed or differs from .claude\hooks\pre-commit"
        $bad++
    }

    Say ""
    Say "-- not covered yet (RECOVERY-PHASES section 2), so this gate does NOT mean these hold --"
    Say "  not yet -- phase 2b  dodona status build SHA is a commit that git log knows        (I2)"
    Say "  not yet -- phase 3   live lane pipes == the lane count dodona ps reports           (I3)"
    Say "  not yet -- phase 4   a full suite run finishes under 60 s                          (I7)"
    Say "  not yet -- phase 5   repo lint clean: no control bytes, every named test path real (I8)"

    Say ""
    if ($partial) {
        Say $(if ($bad -eq 0) { "GATE SELF-TEST PASSED -- machinery works. THIS IS NOT A GATE: only $($suites -join ', ') ran." }
              else { "GATE SELF-TEST FAILED -- $bad problem(s)" })
    }
    else {
        Say $(if ($bad -eq 0) { "GATE PASSED -- on the 6 assertions above, and only those." } else { "GATE FAILED -- $bad problem(s)" })
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
    'gate' { Do-Gate }
    'ship' { Do-Ship }
    'worktree' { Do-Worktree }
    'help' { Do-Help }
}
