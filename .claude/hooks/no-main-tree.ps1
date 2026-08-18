# Dodona enforcement layer 1: an AGENT may not write into the shared checkout.
#
# WHY THIS IS CODE AND NOT A SENTENCE. RECOVERY-PHASES P2.1 as written said CLAUDE.md would
# name worktrees "the only supported way to run two sessions". That is a documented warning,
# which is the exact shape D-6 forbids -- and the failure it guards has already happened:
# f9aaf25 committed another lane's lanes.cwd migration and Ver.cs changes along with its own
# work, because two sessions shared one checkout and `git add` cannot tell whose edit is
# whose. A warning would not have stopped it. This does. (Decision D-7.)
#
# THE TEST IS STATELESS, deliberately: git leaves `.git` as a FILE in a worktree and a
# DIRECTORY in the main checkout (CLAUDE.md 0.2 / 5.2). So walk up from the target path to
# the nearest `.git` and look at what it is. No registry, no lock file, no session table --
# nothing that can go stale, disagree with reality, or jam and leave an agent stuck.
#
# Walking up from the TARGET (not from the session's cwd) is what makes a nested worktree
# work: .claude/worktrees/phase2 lives inside the main checkout, but the nearest .git above
# a file in it is the worktree's .git FILE, so it is allowed.
#
# LAYER 1 OF TWO. This one binds the Edit/Write tools; .git/hooks/pre-commit is layer 2 and
# catches anything that slips past (a shell heredoc, sed, a redirect) -- the same
# gate-plus-backstop doctrine as the claim gate and the merge-time diff (design 6).
#
# Everything here is ASCII on purpose (CLAUDE.md 0.2: non-ASCII literals in a BOM-less .ps1
# are read as ANSI and match nothing).

$in = [Console]::In.ReadToEnd()

# Fail OPEN, always. A hook that cannot parse its input must not be able to brick every
# write in the repo -- layer 2 still stands behind it.
try { $j = $in | ConvertFrom-Json } catch { exit 0 }

# The one deliberate override (RECOVERY-PHASES D-7 item 4). Sometimes the main checkout IS
# the right place -- installing hooks, a release commit, a one-line fix the operator is
# watching. This must be a choice you make, never a wall you hit: an enforcement with no
# escape is a new way to be stuck, which is the standing directive in a fresh costume
# (CLAUDE.md 0.1).
if ($env:DODONA_ALLOW_MAIN_TREE -eq '1') { exit 0 }

$fp = $j.tool_input.file_path
if (-not $fp) { exit 0 }

# The file may not exist yet (Write creates it), so resolve the PARENT and never the file.
try {
    if (-not [System.IO.Path]::IsPathRooted($fp)) { $fp = Join-Path (Get-Location).Path $fp }
    $dir = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($fp))
}
catch { exit 0 }

# Nearest enclosing .git wins. FILE -> worktree -> allowed. DIRECTORY -> main checkout ->
# refused. Neither -> not a git tree at all, none of our business.
$cur = $dir
$mainRoot = $null
while ($cur) {
    $g = Join-Path $cur '.git'
    if (Test-Path -LiteralPath $g) {
        if (Test-Path -LiteralPath $g -PathType Container) { $mainRoot = $cur }
        break
    }
    $parent = [System.IO.Path]::GetDirectoryName($cur)
    if (-not $parent -or $parent -eq $cur) { break }
    $cur = $parent
}
if (-not $mainRoot) { exit 0 }

$name = 'w' + (Get-Date -Format 'MMdd-HHmm')

# NO BACKTICKS ANYWHERE IN THIS HERE-STRING. In a @" " @ block the backtick is PowerShell's
# escape character, so a line ENDING in one escapes the newline -- which swallows the
# terminator and the whole file stops parsing. That happened here: the first version quoted
# a variable name as ` + '`$env:VAR`' + ` and the hook died with "The string is missing the
# terminator", exit 1, denying nothing. A hook that does not parse is not weak enforcement,
# it is ABSENT enforcement that still looks installed -- which is why Install-Hooks in
# tools/dev.ps1 now parse-checks every hook script and the gate asserts layer 1 still bites.
# (CLAUDE.md 0.2: a .ps1 that fails to PARSE never reaches anything.)
$reason = @"
refused: $fp is in the SHARED CHECKOUT ($mainRoot), which no agent session may write to.

Two sessions in one checkout cannot tell whose edit is whose. That is not hypothetical:
commit f9aaf25 carried another lane's lanes.cwd migration into an unrelated fix, because
both edits sat in the same working tree and git add had no way to know.

Work in your own tree instead -- five seconds:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 worktree $name
then re-run this edit against the path it prints.

If the shared checkout is genuinely the right place (installing hooks, a release commit),
say so deliberately: set DODONA_ALLOW_MAIN_TREE=1 in your environment.
"@

@{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } |
    ConvertTo-Json -Compress -Depth 5
exit 0
