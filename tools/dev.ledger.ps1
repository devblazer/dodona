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

# Every type declaration in C# under src\ and tests\, with the attributes attached to it.
#
# TRACKED **AND** UNTRACKED (issue #15), through the same `Lint-Files` as the rest of Repo-Lint.
# This said "tracked, which is issue #15 in miniature -- add before you lint", and it was not in
# miniature: rung 1 is a static text scan precisely so that "a text scan cannot miss an assembly"
# (plan 3.2), and the file list it scanned was undermining that. Demonstrated on the ticket -- a
# new fake class read `clean: ... every double anchored` while untracked and was correctly refused
# the moment it was staged, with nothing about the file changed in between. So the ORDINARY
# sequence shipped the thing the mechanism exists to prevent: write the fake, lint, see clean,
# commit -- and the file becomes tracked AT the commit, after the last chance to look at it.
function Doubles-TypeSites {
    $sites = @()
    $decl = '^(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file|new|unsafe|readonly|ref)\s+)*' +
            '(class|struct|interface|record(?:\s+(?:class|struct))?)\s+([A-Za-z_][A-Za-z0-9_]*)'
    foreach ($rel in @((Lint-Files @('src/*.cs', 'tests/*.cs')).All)) {
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
    # Hoisted out of the baseline block: the moves rung keys names too, and it has to key them
    # the SAME way (Ledger-Key), which needs this set. Four sites key a name now -- the capture,
    # the integrity rung, the live rung and the moves rung -- and the last one used to key on
    # the bare check while the baseline keyed harness rows suite+check. That disagreement is
    # what made ONE slice able to claim a fifteen-row name.
    $harness = Ledger-HarnessNames $sites

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
                # THE HARNESS ROW IS DISPOSED OF GLOBALLY, AND NO SLICE OWNS A SHARE OF IT.
                # tests\_workspace.ps1 writes one name into EVERY suite's results, so that one
                # name is fifteen baseline rows. `claimed` keyed the BARE name, so the first
                # slice to write the row would have disposed of all fifteen and locked the other
                # fourteen suites out of a name that is not theirs to give away -- two wave-1
                # slices spotted it and left the row alone rather than take that bet. The
                # decision the plan already made (5.4: stays / harness-hygiene, not
                # deduplicable) is applied by the tool itself, on --verdict's own line.
                if ($harness.ContainsKey($r.old_check)) {
                    $hrows = if ($baseline.Present) { @($baseline.Rows | Where-Object { $_.check -eq $r.old_check }).Count } else { 0 }
                    $out.Problems += "$at claims '$($r.old_check)', which is a HARNESS row -- tests\_workspace.ps1 writes it into EVERY suite's results, so it is $hrows baseline row(s) under one name. It is disposed of GLOBALLY (plan 5.4: stays / harness-hygiene, not deduplicable) and dev ledger accounts for it itself -- see the 'harness (global)' line in --verdict. A per-slice row claims the bare name and LOCKS OUT every other suite's row, so there is no such thing as one slice's share of it"
                    continue
                }
                # The SAME key the baseline is keyed on (Ledger-Key), so the two cannot
                # disagree: a suite check keys bare, a unit method keys unit/<name>, which is
                # what lets a suite check and its unit descendant be disposed of separately.
                $claimKey = Ledger-Key $r.old_suite $r.old_check $harness
                if ($claimed.ContainsKey($claimKey)) {
                    $out.Problems += "$at claims '$claimKey', already claimed by $($claimed[$claimKey]) -- a name is disposed of exactly once"
                }
                $claimed[$claimKey] = $at
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
                # AN IN-SUITE RENAME -- the third destination form, and the gap it closes is a
                # real one: `renamed` was hard-wired to a unit:/ui-unit: destination, so a check
                # that must change its NAME while staying in its acceptance suite had no legal
                # row at all, and the slice that hit that left the work undone rather than write
                # an illegal one. (The case: three m1 checks named after a fail-open path issue
                # #4 established does not exist and a merge backstop D-R5 retired, whose
                # ASSERTIONS are still sound -- so they must be renamed in place, never deleted
                # and never moved.) The form is suite:<suite>:<new_check>, the same shape
                # `merged` and `obsolete / duplicate-of` already use to name a live check, and
                # it is what makes both names recorded: old_check keeps resolving against the
                # FROZEN baseline, so a rename can never read as a removal.
                $inSuiteRename = ($r.disposition -eq 'renamed' -and $r.destination -like 'suite:*')
                if ($inSuiteRename) {
                    $parts = @($r.destination -split ':', 3)
                    if ($parts.Count -ne 3 -or $parts[1] -eq '' -or $parts[2] -eq '') {
                        $out.Problems += "$at destination '$($r.destination)' must be 'suite:<suite>:<new_check>' for an IN-SUITE rename -- the suite the check still runs in, and the name it runs under now"
                        continue
                    }
                    $newSuite = $parts[1]; $newCheck = $parts[2]
                    # The reachability rung, pointed at the NEW name. Same authority the
                    # kept/stays rung uses on the old one: a rename whose new name no suite
                    # registers is a deletion with a row over it.
                    $reg = @($sites | Where-Object { -not $_.Dynamic -and $_.Check -eq $newCheck })
                    if ($reg.Count -eq 0) {
                        $out.Problems += "$at is an IN-SUITE rename to '$newCheck', which no suite registers -- the new name must EXIST, or the rename is a deletion with a row over it (the reachability rung, D-T6)"
                    }
                    elseif (@($reg | Where-Object { $_.Suite -eq $newSuite }).Count -eq 0) {
                        $out.Problems += "$at says the rename lands in suite '$newSuite', but '$newCheck' is registered in $(($reg | Select-Object -ExpandProperty Suite -Unique) -join ', ') -- an IN-SUITE rename changes the NAME and not the suite"
                    }
                    # THE MIRROR, and it is the same one `obsolete` has: the row is written in
                    # the commit that PERFORMS the rename. An old name a suite still registers
                    # is a rename declared and not done -- and then both names run, which the
                    # census, keyed on the check name, cannot hold.
                    if ($stillThere) {
                        $out.Problems += "$at is an IN-SUITE rename of '$($r.old_check)', which a suite STILL registers -- the row is written in the commit that performs the rename, so this one was declared and never done. Both names would run and baseline.tsv, keyed on the CHECK NAME, can hold only one"
                    }
                    continue
                }
                if (@('moved', 'renamed') -contains $r.disposition) {
                    $parts = @($r.destination -split ':', 2)
                    if ($parts.Count -ne 2 -or @('unit', 'ui-unit') -notcontains $parts[0]) {
                        $third = if ($r.disposition -eq 'renamed') { ", or 'suite:<suite>:<new_check>' for an IN-SUITE rename, which changes the name and not the layer" } else { '' }
                        $out.Problems += "$at destination '$($r.destination)' must be 'unit:<FQN>' or 'ui-unit:<FQN>' -- two prefixes because dev prove --with has to know which project to build$third"
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
        # AN IN-SUITE RENAME'S NEW NAME IS DECLARED BY ITS MOVES ROW, not by added.tsv. It is
        # not growth: the frozen baseline row is the OLD name and the new one runs in its place,
        # so counting it as `added (declared)` would report a rename as coverage going up, in
        # the one block whose whole job is arithmetic nobody can fudge. baseline.tsv is frozen,
        # so the new name cannot go there either -- the moves row is the only place both names
        # exist side by side, which is why it carries both.
        foreach ($m in @($static.Moves | Where-Object { $_.disposition -eq 'renamed' -and $_.destination -like 'suite:*' })) {
            $p = @($m.destination -split ':', 3)
            if ($p.Count -eq 3) { $declared[(Ledger-Key $p[1] $p[2] $harness)] = $m }
        }
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
                # The two renames read differently on purpose: one changed layer, one did not.
                'renamed' {
                    if ($r.destination -like 'suite:*') { "RENAMED IN SUITE -> $(@($r.destination -split ':', 3)[2])" }
                    else { "RENAMED DOWN -> $($r.destination)" }
                }
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
    # The two renames are counted apart, because they are different events: one took the name
    # DOWN A LAYER (there is a C# method behind it), the other changed a name inside its own
    # acceptance suite (the check is still there, running, under the new name). A single
    # `renamed` count would hide which of the two happened, and this block is the one place the
    # arithmetic is read.
    $renamedRows = @($moves | Where-Object { $_.disposition -eq 'renamed' })
    $renInSuite = @($renamedRows | Where-Object { $_.destination -like 'suite:*' }).Count
    $renDown = $renamedRows.Count - $renInSuite
    # ORDINAL, and Ledger-Key -- the fourth site that keys a name, and the one that used to
    # disagree with the other three (a bare, case-insensitive @{}). It matters twice over: a
    # unit row and a suite row of the same name are two disposals, and `A_x` is not `a_x`.
    $harness = Ledger-HarnessNames $static.Sites
    $accounted = Ledger-NewSet
    foreach ($r in $moves) { $accounted[(Ledger-Key $r.old_suite $r.old_check $harness)] = $true }
    $unaccounted = 0; $harnessRows = 0
    if ($b.Present) {
        # HARNESS ROWS ARE ACCOUNTED FOR HERE and never in a moves file. One name written by
        # tests\_workspace.ps1 into every suite's results is one baseline row per suite, so no
        # slice can own it: the plan already disposed of it (5.4, stays / harness-hygiene, not
        # deduplicable) and this line is that disposition being reported rather than re-argued.
        $harnessRows = @($b.Rows | Where-Object { $harness.ContainsKey($_.check) }).Count
        $unaccounted = @($b.Rows | Where-Object {
                -not $harness.ContainsKey($_.check) -and -not $accounted.ContainsKey((Ledger-Key $_.suite $_.check $harness))
            }).Count
    }

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
    Say ("  renamed to unit     {0}   (the name changed on the way down; the method exists)" -f $renDown)
    Say ("  renamed in suite    {0}   (the name changed, the LAYER did not; both names recorded)" -f $renInSuite)
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
    Say ("  harness (global)     {0}   <- one baseline row per suite, written by tests\_workspace.ps1;" -f $harnessRows)
    Say  "                            disposed of HERE and never in a moves file (plan 5.4:"
    Say  "                            stays / harness-hygiene, not deduplicable)"
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

