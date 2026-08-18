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
    [ValidateSet('check', 'build', 'test', 'suites', 'prove', 'ship', 'help')]
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
function ReportBlockers {
    $b = Blockers
    if ($b.Count -eq 0) { Say "blockers: none"; return }

    Say "in the build output: $($b.Count) process(es) -- may or may not block, the build decides"
    foreach ($p in $b) { Say "  pid $($p.Id)  $($p.ProcessName)  $($p.Path)" }
}

# ---------------------------------------------------------------- verbs

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
    Say "  ship                     build + suites + publish."
    Say ""
    Say "Every run logs to .dodona\dev-logs. A blocked run stops on line one, not minute forty."
}

switch ($Verb) {
    'check' { Do-Check }
    'build' { Do-Build }
    'test' { Do-Test }
    'suites' { Do-Suites }
    'prove' { Do-Prove }
    'ship' { Do-Ship }
    'help' { Do-Help }
}
