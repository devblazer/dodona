# Work isolation — deciding that a sentence is load-bearing, and ending what it starts

Status: **plan, not built.** Written 2026-08-20 from the operator's brief, after tracing the
ticket path in code. The authority for this work. It extends `WORKSPACES-CONCIERGE.md` §5/§5.1
(the routing ladder) and consumes `LANE-LIFECYCLE.md` §3 (how a lane ends) rather than
re-deciding it. `M5-DELIVERY-PLAN.md` owns a *foreign* project's ceremony; this owns Dodona's
own.

## 0. The one-line statement

The operator drives this by voice, which means text in the box, which means `RouteInput`. That
ladder answers **which lane**. It does not answer **is this load-bearing work, and does it need
a checkout of its own** — and nothing starts or finishes the ticket process from where the
operator is sitting. So the isolation this system was built to provide is reachable only by
typing a CLI command in a terminal, which by voice is not reachable at all.

## 1. What exists today, from the code

- `RouteInput` (`src/Dodona/Daemon.cs`, `RouteInput`) has four verdicts —
  `generic | addendum | new-task | unclear`. `new-task` calls `SpawnForAsync`, which opens a
  **plain lane in the project directory**. No rung can answer "isolate this".
- The brain can only *suggest* a ticket, as a pane announcement carrying a command string
  (`brain_suggested_ticket`). Nothing acts on it.
- The only UI path to a ticket is clicking an empty grid slot → `StartLaneWindow`, a modal with
  no coverage, which D-L4 already ruled out as the shape for asking anything.
- **A plain lane on a one-project workspace cannot commit.** Its cwd is the shared checkout and
  `.githooks/pre-commit` refuses commits from there. So today's default destination for
  load-bearing work is a tree that cannot deliver it. The lane prompt tells the agent to stop
  and ask for a ticket, which is honest, and by voice is a dead end.
- The ticket spine itself is sound and covered by `m1`: branch `ticket/<id>`, worktree, claim
  gate, merge token, backstop, ff-only land, prune. What is missing is both ends of it.
- **`ticket-agent` has never run with a real `claude` in any of the thirteen suites** — only the
  fake agent (`m3`, `workspace`), plus `tests/m2-live.ps1`, which is not one of them.

Measured while writing this, because the plan turns on it: `git worktree add` on this repo is
**sub-second** (0.72 s for the whole `dev worktree` verb, ~136 ms of that merely starting
PowerShell). The expensive part of a fresh ticket is the **cold build** in a tree with no
`bin`/`obj` — which is the agent's first task, not the box's wait. That distinction is what
makes D-3 affordable.

## 2. Principles this is held to

- **Responsive first.** The box already pays ~1 s for the classifier and the operator accepted
  that price once. This feature is allowed **zero additional model calls** on the input path.
- **Derive in code what is not really a judgement** (§2.2). Conflict, dirtiness, whether a path
  is already claimed: all facts. Only "will this change the repository" is judgement.
- **Act, announce, allow undo** (§11). No dialog, no modal, no blocking question.
- **Never hung, halted, stuck, or outdated** (§0.1). Every wait names the thing that un-sticks
  it, and no rung may end in "go and type a command in a terminal".
- **The error asymmetry is still the design** — but isolation changes the numbers, which is D-9.

## 3. Decisions

**D-1. The answer is not a fifth verdict. It is a flag on `new-task`.** The classifier's reply
grows one field — `{"kind":…,"target":…,"isolate":true|false,"confidence":…,"reason":…}` — and
`isolate` is read only when `kind` is `new-task`. One decision point, one round trip, no extra
latency and no extra quota. A separate "is this ticket-worthy" call is rejected in §5.

**D-2. `isolate` defaults false and needs positive evidence.** A question, a status request, a
read, an explanation — none of them get a branch. The classifier is asked for it as *will this
change files in the repository*, which is the honest discriminator; not length, not imperative
mood (§5.1 already rejected length, on the operator's own correction).

**D-3. The verdict is synchronous; materialisation is not.** The lane row and its pane appear
the instant the verdict lands, presence `preparing…`, and the sentence is delivered when the
agent attaches. `LaneCreate` already precedes `AttachShimAsync`, so this shape is expressible
today. The operator's sentence is never held waiting on git or on a build.

**D-4. Claims are elastic while the ticket is scoping.** A ticket needs claims up front; a
sentence spoken out loud cannot supply paths; and a frozen wrong claim **strands the agent
mid-work**, which is the worst outcome in this area. So while a ticket is `scoping`, an
out-of-claim write **auto-extends the claim and announces it**, and the gate refuses only what
another **open ticket already claims**. The claim freezes at the first `token-request`.

This deliberately changes what layer 1 is *for*: from *bounding the agent to a prediction* to
*preventing collision with another ticket*. Three things make it the right trade — the gate only
matches `Edit|Write|MultiEdit|NotebookEdit` and never saw a `sed` or a heredoc anyway; it fails
open by design; and the invariant that actually matters (no two tickets over one path, no two
merge tokens over one main) is untouched and still refuses. The review surface moves to the
backstop at `token-request`, where the diff is a fact rather than a guess: *this ticket touched
X, Y and Z — approve?*

**D-5. Verify runs BEFORE the merge, in the worktree.** Today `LandOp` merges, commits the
fence, and *then* runs verify — so a red verify has already advanced main. Because the merge is
`--ff-only`, main's post-merge tree is byte-identical to the branch tip, which makes verifying
the worktree **exactly equivalent** and strictly safer. A red verify refuses the land and says
so. This is a bug fix independent of everything else here.

**D-6. Verify must go through `tools/dev.ps1`.** `dodona.json`'s verify is a bare
`dotnet build Dodona.sln -c Release -warnaserror`, run via `cmd /c`. A locked output file makes
that report `Build FAILED` with ten screens of MSB3026 — the exact false diagnosis §1 mandates
the wrapper to prevent, arriving at the moment nobody is watching. It becomes `dev gate`, which
is also the thing CLAUDE.md §1 requires before a merge to main and which no ticket has ever run.

**D-7. Approval is rendered, not hunted.** `token-request` under `on-approval` already flips
presence to `waiting on you: merge` and announces `dodona approve N` — a GUI telling you to go
and type. It becomes an **`open` row in `questions`**, so the ask overlay renders it live,
`ui dump`'s `ask` key shows it headless, and one answer path serves both (D-L4). Voice composes
the word and the operator presses Enter: the standing dictation constraint preserved, not worked
around.

**D-8. Auto-land stays opt-in per project.** A ref advance is the one irreversible act in this
system, so responsiveness is bought by making the approval *arrive*, never by deleting it.
`"landing": "auto-when-green"` exists for a project that wants it; this repo's default stays
`on-approval`.

**D-9. `lane-stop` on an isolated lane abandons its ticket.** The ladder's optimism rests on a
wrong new lane being free. A wrong *ticket* costs a branch, a worktree, a gate and a claim
namespace — so the undo line must be true: stopping the lane prunes the worktree, deletes the
branch and releases the claims, announced. Without this, D-1 is a fifth verdict whose mistakes
accumulate.

**D-10. The agent is told the tail, and its two traps are enforced rather than written down.**
`TicketSystemPrompt` names commit → `token-request` → `land`; it currently names none of them,
so an agent follows `/ship` instead. And `/ship` step 4 from a ticket worktree runs
`dodona publish --project .`, which builds **that worktree** and hot-swaps the operator's live
app to an unfinished branch. So: `publish` refuses a `--project` resolving inside `.dodona/wt/`
unless `--from` is explicit, and `dev worktree` refuses to run from inside a ticket worktree
(CLAUDE.md §0.0 tells every session to make one with no carve-out, and obeying it there puts the
agent on a fresh branch off main, outside its own claim).

**D-11. How the lane ends is already decided.** `LANE-LIFECYCLE.md` §3: landed + no further
ticket → the one judgement call, asked once at completion, with the code-checkable preconditions
listed there. This plan wires into that and re-decides none of it.

## 4. Phases

| | what | proof |
|---|---|---|
| **P1** | `isolate` on the verdict; `SpawnForAsync` gains an isolated path creating ticket, worktree, gate and agent in one act. Refusal (claim conflict, no repo) falls back to a plain lane and announces — never a dead end. | `unit`: the verdict parses, `isolate` defaults false, a non-`new-task` kind ignores it. `m2`: an isolating sentence produces a ticket row and a lane whose cwd is the worktree. |
| **P2** | Async materialisation: pane immediately, presence `preparing…`, sentence delivered on attach. | `m2`: the routing row exists before the shim does. `m3`: the pane renders `preparing…` and then the text. |
| **P3** | Elastic claims — auto-extend while `scoping`, announce each widening, freeze at `token-request`, still refuse another ticket's paths. | `unit`: the algebra (covers / conflicts / widen). `m1`: a scoping write outside the claim is allowed and announced; the same write against a second ticket's claim is refused. |
| **P4** | D-5 + D-6: verify in the worktree before the merge; `dodona.json` verify becomes `dev gate`. | `m1`: a red verify leaves main's sha unchanged. This phase must be `dev prove`-d red first — it is the one most likely to look green against the old order. |
| **P5** | Approval as a question row; overlay + `ui answer`; `dodona approve` stays as the CLI form. | `ui-use`: the ask offers the approval and answering it grants the token, at a live window. |
| **P6** | D-10: the prompt tail, the `publish` refusal, the `dev worktree` refusal. | `publish`: `publish --project <a ticket worktree>` refuses and names `--from`. |
| **P7** | D-11: wire landing into `LANE-LIFECYCLE.md` §3's completion question. | `m1` + `m3`: after a land the lane is dormant, the question is asked once, and the preconditions gate it. |

P1–P2 are what make the feature exist; P3 is what makes it usable by voice; P4 is a bug fix
worth landing on its own; P5–P7 are what stop it holding the operator up at the end.

## 5. Rejected — do not re-propose

- **A separate ticket-worthiness model call.** Two round trips on the input path, double the
  latency the operator agreed to once, and quota is the scarce resource. It rides on the
  existing verdict or it does not happen.
- **Brain-proposed claims, frozen at creation.** The stranding case: the agent needs one file
  nobody predicted, the gate denies, and by voice there is no way to extend. D-4 exists because
  of this.
- **A whole-repo `path:.` claim instead.** It bounds nothing and makes every other ticket a
  conflict, destroying the one invariant claims exist for.
- **Auto-landing by default.** See D-8; a ref advance has no undo.
- **A "create a ticket?" dialog.** §3.1 and D-L4: no modals — a test window is forbidden from
  producing one, so it would be permanently untestable, which is exactly why `StartLaneWindow`
  has no coverage today.
- **Promoting a plain lane to a ticket after it has edited the shared checkout.** The edits are
  in the wrong tree and there is no safe way to move them: `git stash` is repo-global (§5.2), so
  two lanes stashing interleave one stack and `pop` takes the other lane's work. Decide
  isolation at spawn, or not at all.
- **Deciding isolation from sentence length or mood.** §5.1 rejected length on the operator's
  own correction; mood is the same mistake holding a grammar book.

## 6. Traps this will hit

- **`ticket-create` refuses on claim conflict**, so an auto-isolated ticket can fail at the
  moment of spawn. P1's fallback is not a nicety; without it a spoken sentence vanishes.
- **`land` demands main checked out in the shared checkout.** True while §0.0 keeps the operator
  there, but it must announce rather than fail quietly.
- **ff-only refuses when main moved under an open ticket**, and nothing rebases. Under D-8 this
  is where a "never stuck" wait belongs: arm the land behind a rebase, announce it, do not park.
- **A ticket worktree lives at `<member>/.dodona/wt/t<N>`, inside the repo.** Anything walking
  the tree must keep skipping `.dodona` — `Git.FindRepos` and the repo lint already do.

## 7. Open questions

- Does an isolating verdict need a name for the ticket, or is the lane title enough? The title
  is already brain-chosen and reviewed, so a second name is probably noise.
- Should `isolate` be suppressed while the same project already has N open tickets? Cheap to
  count in code, and unclear whether that is a real limit or an invented one. Measure first.
