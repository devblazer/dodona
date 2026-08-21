---
name: ticket
description: Write or update a Dodona issue on GitHub — the tracker at github.com/devblazer/dodona, on the operator's project board. Use whenever you file an issue, add findings to one, close one, or are asked to "make a ticket" for something. Covers the two-layer format (plain English for a person, folded detail for an agent), the label taxonomy, and the shell traps that mangle issue bodies.
---

# Writing a Dodona ticket

**A ticket has two readers and they want opposite things.** The operator wants to know, in a
sentence, what is wrong and whether it matters — without reading code, at a glance, possibly on a
phone. An agent picking the ticket up wants file paths, measurements, the failing string, and the
decision that is already made so it does not re-litigate it.

Writing for one reader loses the other. So every ticket is **layered**: plain English on top,
technical detail folded away underneath. Both are required. The top half is not a summary of the
bottom half — it is the same thing said to a different person.

This is not a new convention. It is the shape the operator's other repo already uses; match it.

## Where things are

| | |
|---|---|
| repository | `devblazer/dodona` (public) |
| project board | `gh project` **2**, owner `devblazer`, titled *Dodona* — **this project's own board.** One board per project, the way `MassWorks` (board 1) is that project's. Dodona work does not go on another project's board |
| new items land in | Status **Todo** |

```powershell
gh issue create --repo devblazer/dodona --title "<title>" --body-file <file> --label "kind/x,prio/y,area/z"
gh project item-add 2 --owner devblazer --url <issue-url>
```

**Add it to the board in the same breath as creating it.** An issue that is not on the board is
invisible where the operator actually looks, which is the tracker equivalent of built-but-not-
published (CLAUDE.md §2).

## The format

```markdown
**What's wrong:** One or two sentences. No jargon, no file paths, no identifiers.

**Why it matters:** The consequence, in terms of what someone would notice or lose.

**<A third bold lead-in, when there is one>:** scope, what it is *not*, new evidence,
what already works, the constraint that makes it hard. Use the words that fit; these are
not fixed headings.

<details><summary>Detail</summary>

Everything an agent needs. Paths, line numbers, exact strings, measurements with dates,
the commands to reproduce, decisions already taken and why, what has been ruled out.

</details>
```

**The title is a sentence about the problem, not a label for it.** *"m1 is kept out of the
parallel run for a reason the code contradicts"* — not *"Investigate m1 SoloSuites"*. A person
scanning the board should be able to skip the ticket entirely on the strength of its title.

### The layman test, stated as a test

Read the part above `<details>` out loud to someone who does not know this codebase. If they
cannot say back **what is broken** and **why anyone should care**, it is not done. Specifically,
above the fold:

- **No identifiers.** Not `GateHook`, not `I7`, not `PromptDirMismatch`, not `D-R28`. Say *the
  write gate*, *the time limit on the test run*, *the check that catches a prompt naming the wrong
  folder*, *the decision that made pull-request mode refuse*.
- **No file paths and no line numbers.** They are facts about where, not about what.
- **Name the consequence, not the mechanism.** *"one problem arrives looking like six"* beats
  *"failures cascade through dependent assertions"*.
- **Numbers are fine, and usually help** — *"takes 292 seconds against a 300 second limit"* is
  plain English. It is the *names* that lock a reader out, not the measurements.

### What belongs below the fold

Everything the top half deliberately left out, and in particular the things a fresh agent would
otherwise get wrong:

- **Measurements with their date and commit.** A number with no date rots silently and then gets
  quoted (CLAUDE.md §1 has the incident: a stale duration is why verification became a thing to
  skip).
- **What has already been ruled out**, so nobody re-runs a disproved hypothesis.
- **Decisions already taken**, with the reason — this repo records rejected ideas *with the
  reason* precisely so they are not re-proposed.
- **The constraint that makes it hard**, if there is one. "No suite may open the microphone" is
  the whole shape of that ticket; leaving it out invites an agent to write the suite that is
  forbidden.
- **Scope honesty.** If a fix is two lines, say so. If it is a day, say so.

## Comments carry the same two layers

Findings, progress and outcomes go in **comments**, not by editing the body — the body is what the
ticket *is*, the comments are what happened to it. A comment follows the same rule:

```markdown
**Where this got to:** plain English, one or two sentences.

<details><summary>Detail</summary>

What was measured, what the red said verbatim, which commit.

</details>
```

**A comment that closes a ticket says what was done and what it cost**, in the same voice a commit
message would. The tracker and the commit log are two views of one history; do not let them
disagree.

## Labels — all three, every time

One `kind/`, one `prio/`, one or more `area/`. A ticket with no labels is a ticket that cannot be
filtered, which on a board this size means a ticket that is not read.

| kind | |
|---|---|
| `kind/bug` | something is wrong |
| `kind/feature` | new behaviour |
| `kind/debt` | cleanup, refactor, a shortcut to pay back |
| `kind/docs` | CLAUDE.md, plans, comments |
| `kind/investigation` | measure or understand before deciding |

| prio | |
|---|---|
| `prio/p0` | broken now, blocks other work |
| `prio/p1` | next |
| `prio/p2` | soon |
| `prio/p3` | someday |

| area | |
|---|---|
| `area/suites` | the acceptance suites, `dev.ps1`, the gate and its budget |
| `area/daemon` | lifetime, reconcile, the control pipe, shims and lanes |
| `area/delivery` | tickets, the merge token, the land, PR mode, publish |
| `area/ui` | the window, the grid, the input box, dictation |
| `area/brain` | routing, the manager review, the concierge, compression |
| `area/agent` | what a lane agent is told: prompts, the briefing, the write gate |

`kind/investigation` is not a softer `kind/bug`. Use it when the next step is genuinely *find out*,
and say in the ticket what a finished investigation looks like — otherwise it is a ticket that can
never be closed.

## Traps, each of which has bitten something here

- **`--body-file`, never `--body` or `-m`.** Issue bodies contain backticks, quotes, `$`, `%` and
  em dashes. PS 5.1 and `cmd` will mangle at least one of those, and the damage lands in a
  published issue rather than in an error (CLAUDE.md §0.2 — this is the same reason commit
  messages go through `git commit -F`).
- **Write the body file with LF and UTF-8, no BOM.** A BOM at the top of a markdown file renders
  as a stray character in the first heading on GitHub.
- **Read before you write.** `gh issue view <n> --repo devblazer/dodona` first. Appending a comment
  that repeats what the body already says is noise, and noise is how a board stops being read.
- **Do not close a ticket you have not verified closed.** Same standard as reporting work done: a
  ticket closed on the strength of an edit is CLAUDE.md §1's *an edit that has not been built is a
  claim, not a change*, in a second costume.
- **Link, do not duplicate.** If the detail already lives in `docs/`, cite the plan and the
  decision number rather than pasting it — the plan is the authority and a copy will drift from it.

## Why this is a skill and not a section of CLAUDE.md

CLAUDE.md §5.1 says **do not write a fourth one**, and means it — but it means a fourth *trap*
skill beside `check-authoring`, `file-patching` and `probe-hygiene`, which exist to fire a warning
at the moment of a dangerous edit. This is a **workflow** skill, the sibling of `ship`: a thing you
invoke when you are doing a named job. If it ever turns out to be skipped as reliably as a section
of CLAUDE.md was, the answer is the same as for the others — promote it to enforcement, or delete
it. It is not a licence to add more.
