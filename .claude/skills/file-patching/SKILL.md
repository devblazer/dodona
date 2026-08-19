---
name: file-patching
description: Rules for editing a tracked file in this repo with a script, heredoc, sed, or any generated patch rather than by hand. Use whenever you are about to write a Python or shell script that rewrites a source file, feed content through a bash heredoc, or write a file with an encoding argument. Covers backslash collapse, BOM and line endings, parse-checking, and reading your own diff.
---

# Patching a file without silently damaging it

Four traps. Every one of them is recorded in CLAUDE.md §0.2, had been read, and was hit
anyway — because it was read at session start and needed forty minutes later. That is what
this file is for.

## 1. Never write `\\` through a bash heredoc — it collapses to `\`

A heredoc is shell-processed even when quoted in ways you expect to be safe. Anything with
Windows paths, regexes, or escape sequences arrives mangled, and the failure is silent when
the mangled version still parses.

**Write the patch script to a file with a tool that does no shell processing, then run it.**
Phase 3 hit this three times in one session: twice mangling a script, once turning the
escape sequences in a document's prose into real control bytes.

## 2. Emit bare LF. Never derive the newline from the working tree

`core.autocrlf=true` here: git stores LF and checks out CRLF. So a script that reads the
working-tree bytes to decide its newline sees **CRLF**, and replacing LF with CRLF on
already-CRLF text produces CR-CR-LF. The extra CR survives git's normalisation, and a
105-line insert becomes a 1214-line phantom rewrite of the whole file. Measured: 700 CRs
against 638 LFs.

```python
io.open(p, 'w', encoding='utf-8', newline='\n').write(s)   # yes
```

**`sed -i` rewrites the WHOLE file's line endings, not the line you matched.** Git Bash's sed
reads CRLF text, strips the CR, and writes bare LF everywhere — so a one-character `s///` on a
CRLF working copy shows up as every line changed, and `git diff` starts warning *"LF will be
replaced by CRLF the next time Git touches it"*. The committed bytes are the same (git normalises
to LF), which is exactly why this is easy to wave through, but `dev gate`'s P7.5 assertion is
looking at the working copy. Hit while correcting a single number in a plan file, 2026-08-19,
Phase 0b. Rule 5 below is what caught it: `git diff --stat` said 610 lines, `git diff -w --stat`
said the same, and neither matched the one line that was meant to change.

## 3. Match the BOM. Do not let the writer choose it

`utf-8-sig` **adds** a BOM. This repo is mixed on purpose — `src/Dodona/Program.cs` has one,
`Daemon.cs` does not — so preserve what is there rather than normalising:

```python
raw = io.open(p, 'rb').read()
enc = 'utf-8-sig' if raw[:3] == b'\xef\xbb\xbf' else 'utf-8'
```

Phase 3 added a BOM to seven files this way and it was caught only because a human read the
diff. **Non-ASCII in a BOM-less `.ps1` is read as ANSI and matches nothing** (§0.2), so a
BOM change can silently break a pattern that worked yesterday.

`dev gate` asserts both this and rule 2 now (P7.5) — but the gate is a backstop, not a
substitute for getting it right.

## 4. Parse-check every `.ps1` you write, before believing anything about it

```powershell
$e = $null
[System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$e)
if ($e.Count) { $e[0].Message }
```

**A `.ps1` that fails to PARSE never reaches its `finally`** — everything it started leaks,
and a generated gate script that cannot parse denies nothing while looking installed.

The likeliest cause is a **backtick**: it is PowerShell's escape character, so a trailing
one inside a double-quoted string eats the closing quote and unbalances every brace after
it. `"see `git worktree list`"` is a parse error, not a sentence. Use single quotes, or say
it in words.

## 5. Read your own diff before you believe the edit

```powershell
git diff --stat            # and compare against:
git diff -w --stat         # whitespace-blind
```

**A diff stat that looks too big for the change is the tell.** If the two disagree wildly,
you rewrote line endings. If a file you barely touched shows one changed line at the top,
you moved its BOM. Both of Phase 3's encoding incidents were caught this way and by nothing
else.

Anchor replacements on something unique, and assert the match count is exactly 1 — a
replacement that silently matched zero times leaves the file untouched while the script
reports success.
