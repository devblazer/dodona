# Dodona enforcement (RECOVERY-PHASES P2.2): no `git add -A`, `git add .`, `git commit -a`.
#
# THE INCIDENT. `git add -A` is how a live SQLite database got committed into this repo, and
# it is half the mechanism of f9aaf25 -- which carried another lane's lanes.cwd migration and
# Ver.cs edits into an unrelated routing fix. Broad staging cannot tell your change from
# whatever else is in the tree, so it commits both and the commit message describes one.
# P0.4 already deleted `git add -A` from the /ship skill; this stops it being retyped.
#
# There is no override, and that is not a wall (CLAUDE.md 0.1): the substitute is named in
# the refusal and is always available. Explicit pathspecs are not a restriction on what you
# can commit -- they are the same commit, spelled so a reviewer can see what is in it.
#
# ASCII only (CLAUDE.md 0.2).

$in = [Console]::In.ReadToEnd()
try { $j = $in | ConvertFrom-Json } catch { exit 0 }     # fail open; layer 2 stands behind

$cmd = $j.tool_input.command
if (-not $cmd) { exit 0 }

# Single-dash clusters only for `commit -a`: the lookbehind is what keeps `--amend` out of
# it (`-[a-z]*a[a-z]*` happily matches "-amend" otherwise -- caught while writing this).
# `git -C <path> add -A` is the same command with a detour, so the git GLOBAL options are
# part of the prefix. Found by testing: without this, one -C and the ban is off.
$gitp = 'git(?:\s+(?:-C\s+\S+|-c\s+\S+|--no-pager|--git-dir=\S+|--work-tree=\S+))*\s+'

$bad = $null
if ($cmd -match ($gitp + 'add\s+(?:\S+\s+)*(?:-A|--all)(?:\s|$)')) { $bad = 'git add -A / --all' }
elseif ($cmd -match ($gitp + 'add\s+(?:\S+\s+)*\.(?:\s|$)')) { $bad = 'git add .' }
elseif ($cmd -match ($gitp + 'commit\s+(?:\S+\s+)*(?:--all(?:\s|$)|(?<![-\w])-[a-zA-Z]*a[a-zA-Z]*(?:\s|$))')) { $bad = 'git commit -a' }

if (-not $bad) { exit 0 }

$reason = @"
refused: $bad stages everything in the tree, including work that is not yours.

That is not hypothetical. It is how a live SQLite store was once committed into this repo,
and it is half of f9aaf25 -- which carried another lane's lanes.cwd migration into an
unrelated routing fix, because both edits sat in one working tree.

Name what you are committing:
    git add -- src/Dodona/Daemon.cs docs/RECOVERY-PHASES.md
    git commit -F <message-file>

A forgotten new file surfacing at review is the point, not a cost (RECOVERY-PHASES P0.4).
"@

@{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } |
    ConvertTo-Json -Compress -Depth 5
exit 0
