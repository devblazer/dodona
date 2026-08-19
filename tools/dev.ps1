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
    # 'unit' first: it is the cheapest thing that can fail, so a full run finds a broken
    # claim algebra in under a second instead of four minutes in.
    'unit', 'm0', 'm1', 'm2', 'm3', 'm4', 'workspace', 'ui-use', 'compression', 'brain', 'concierge', 'publish'
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
function LeakedTestProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $p = $null
            try { $p = $_.Path } catch { }
            $p -and $p -like "$env:TEMP\dodona-*"
        })
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
    $leaked = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $pp = $null
            try { $pp = $_.Path } catch { }
            $pp -and $pp.StartsWith($h.Sandbox, [StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($lp in $leaked) { try { Stop-Process -Id $lp.Id -Force -ErrorAction Stop } catch { } }
    if ($leaked.Count -gt 0) {
        Add-Content -Path $log -Value "$($h.Name): reaped $($leaked.Count) leaked process(es) from its sandbox" -Encoding utf8
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
    if ($name -eq 'unit') { return Run-Unit }
    Complete-Suite (Start-Suite $name) $timeoutSec
}

# What must NOT share the machine, named with the reason and checked against the code — never
# "to be safe", because every second of caution here is paid on every gate forever.
#
# THE RULE IS: only one thing may COMPILE at a time. A compile writes src\<proj>\obj\ and
# src\<proj>\bin\, and every suite copies its binaries out of src\...\bin at startup
# (tests\_workspace.ps1 Use-TestBinaries). Exactly one entry in the set compiles:
#
#   unit  is `dotnet test`, which builds Dodona.Tests AND its ProjectReference to Dodona --
#         straight into src\Dodona\bin. So it runs alone.
#
#   m1    is here for a reason that is MEASURED BUT NOT ROOT-CAUSED, and saying so is the
#         point. Alone it is green 3 runs out of 3 in 8-9 s. Run beside m4's real build it
#         fails `gate_denies_outside_claim` and takes 30 s, because `dodona gate-hook`
#         returns EMPTY -- no deny, and no `.dodona-bypass.log` either -- continuously for
#         more than the 20 s the check now retries for. Empty output with no bypass log can
#         only be one of GateHook's three SILENT `return 0` paths (unreadable stdin,
#         unparseable stdin, or no file_path), so the hook is not reaching the daemon at all;
#         it is not the daemon being slow, which would have written the log. Three hypotheses
#         were tested and all three were wrong: it is not the fail-open-on-pipe-error path
#         (no log), and it is not PowerShell failing to deliver stdin to `cmd /c` (probed 60
#         times under load, 60/60 delivered). Running it alone costs ~8 s of wall clock and
#         makes the gate deterministic, which is worth more than the 8 s. The real question --
#         whether layer 1 should fail CLOSED instead of silently open -- is a safety-model
#         decision for the operator, not something to paper over with a longer retry.
#
# m4 IS DELIBERATELY NOT ON THIS LIST, and RECOVERY-PHASES P4.3 says it should be ("its
# internal publish builds the tree's own obj/"). That is half right, and the half it gets
# wrong is the half that matters: publish passes -p:BaseOutputPath=<temp>\ per project
# (src/Dodona/Program.cs, the `publish` branch, with the comment "Only bin is redirected: obj
# must stay put"), so the BIN output goes to a scratch directory and never to src\...\bin.
# Only obj\ stays in the tree — and obj\ is contended by another COMPILE, which is `unit` and
# nothing else. Measured 2026-08-19: m4 inside the parallel wave is green, and it takes 28 s
# off the wall clock, which is the difference between a 77 s gate and a 49 s one.
#
# Everything else is isolated by construction, which is what makes P4.3 possible at all:
# Use-IsolatedDodonaHome gives each suite a GUID temp DODONA_HOME (registry, stores,
# shim-info, neutral cwd); Instance.Scoped() hashes that home into the concierge and shell
# ids, so two suites cannot collide on a pipe; every root is a GUID temp directory; and every
# UI launch carries --test-window, so it renders off-screen and never takes focus.
function SoloSuites { , @('unit', 'm1') }

# Longest first. With a concurrency cap, start order decides the wall clock: begin the 45
# second suite last and it finishes 45 seconds after everything else already has. This list is
# a SCHEDULING HINT and nothing else -- an unknown name just sorts to the end, and the set that
# runs is decided entirely by the caller.
#
# Measured 2026-08-19, each suite alone: ui-use 42.5, m4 28.4, publish ~30, brain 23.4,
# m3 16.6, workspace 13.8, compression 11.7, concierge 11.1, m1 7.7, m2 7.7, m0 7.0.
function SuiteOrderHint {
    , @('ui-use', 'publish', 'm4', 'brain', 'm3', 'workspace', 'compression', 'concierge', 'm1', 'm2', 'm0')
}

# HOW MANY AT ONCE, and the number is measured rather than "all of them".
#
# All eleven at once was tried first and it is worse in both directions: on a 22-core machine
# that never came close to CPU-bound, ui-use went from 42.5s alone to 70.6s, and it went RED --
# `second_sentence_reuses_the_lane` saw two lanes where there must be one. That is not a slow
# test, it is a test whose timing assumptions stopped holding, and a gate that is occasionally
# red for reasons unrelated to the change is a gate people learn to re-run instead of read.
#
# The contention is not CPU. Each suite starts a daemon, one to four shims, a WPF window and a
# python process per store query, and the WPF/UIA side in particular serializes on the desktop.
# Five is where the wall clock stopped improving here.
#
# DODONA_TEST_CONCURRENCY overrides it, for a machine unlike this one -- and 1 is the same
# thing as `dev suites --sequential`.
function SuiteConcurrency {
    $v = $env:DODONA_TEST_CONCURRENCY
    if ($v -and [int]::TryParse($v, [ref]$null) -and [int]$v -ge 1) { return [int]$v }
    5
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
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $o = & dotnet test "$repo\tests\Dodona.Tests\Dodona.Tests.csproj" -c Release --nologo -v q 2>&1
    $code = $LASTEXITCODE
    $sw.Stop()
    Add-Content -Path $log -Value "===== unit =====" -Encoding utf8
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
        Name     = 'unit'
        Fails    = $fails
        Problems = $problems
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Exit     = $code
        Tally    = "$($passed + $failed) checks, $failed failed"
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
        $r = Complete-Suite (Start-Suite $suite "$wt\tests\$suite-acceptance.ps1")
        $o = $r.Output
        foreach ($x in $r.Problems) { Say "  note: $x" }
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
    # THE BUDGET IS 120 s AND RECOVERY-PHASES P4.3 PROJECTED 35-45 s. That projection was not
    # met, and the number here is not quietly rounded to hide it. Measured on this machine, all
    # twelve suites green on a CLEAN machine: 54.6, 69.7, 74.4, 76.9, 87.0 s. The spread is
    # real -- ui-use alone ranges 42.5 s to 72 s, because it makes about a hundred sequential
    # dodona.exe calls and every one of them is slower with four other suites running.
    #
    # It was 90 s for one commit, which was wrong for the reason this comment exists to state:
    # 87.0 s was then observed on a green run, three seconds inside the line. A threshold set
    # just above the worst observation is not a budget, it is a coin flip -- and a gate that
    # goes red for reasons unrelated to the change is one people learn to re-run instead of
    # read, which is the same disease as a gate that is always green. 120 s sits clear of the
    # spread, is still 2.7x better than the 320 s this took sequentially, and goes red the
    # moment a fixed sleep creeps back in.
    #
    # A DIRTY MACHINE BREAKS IT ANYWAY, which is why the leak count is printed above: with 78
    # shims left by earlier runs the same code took 300 s, m3 crashed and brain went red on
    # nine timing checks. The way to earn 45 s is to stop ui-use being the long pole (it is
    # four suites wearing one name) and to stop the suites leaking; neither is this phase.
    #
    # PARTIAL runs cannot judge it: three suites finishing quickly says nothing about twelve.
    # It says so rather than passing a row it did not earn (CLAUDE.md 0.3).
    if ($partial) {
        Say "  n/a   I7  only $(@($suites).Count) of $((AllSuites).Count) suites ran in $([math]::Round($suiteWall, 1))s, so the budget was NOT tested"
    }
    elseif ($suiteWall -lt 120) {
        Say ("  PASS  I7  the full suite run finished in {0:N1}s, inside the 120s budget (was 320s sequential)" -f $suiteWall)
    }
    else {
        Say ("  FAIL  I7  the full suite run took {0:N1}s, over the 120s budget" -f $suiteWall)
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

    Say ""
    Say "-- not covered yet (RECOVERY-PHASES section 2), so this gate does NOT mean these hold --"
    Say "  not yet -- phase 3   live lane pipes == the lane count dodona ps reports           (I3)"
    Say "  not yet -- phase 5   repo lint clean: no control bytes, every named test path real (I8)"

    Say ""
    if ($partial) {
        Say $(if ($bad -eq 0) { "GATE SELF-TEST PASSED -- machinery works. THIS IS NOT A GATE: only $($suites -join ', ') ran." }
              else { "GATE SELF-TEST FAILED -- $bad problem(s)" })
    }
    else {
        Say $(if ($bad -eq 0) { "GATE PASSED -- on the 7 assertions above, and only those." } else { "GATE FAILED -- $bad problem(s)" })
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
