# Work isolation — deciding that a sentence is load-bearing, and ending what it starts

Status: **plan, not built.** Written 2026-08-20 from the operator's brief, after tracing the
ticket path in code. **Revised the same day**, after the operator reframed it: the point of
tickets is the manager *coordinating* lanes so they do not step on each other or make bad
decisions for the collective — and the first draft of this plan made a model's guess the thing
standing between a lane and the shared checkout. That was backwards, and §3 now says so.

The authority for this work. It extends `WORKSPACES-CONCIERGE.md` §5/§5.1 (the routing ladder)
and consumes `LANE-LIFECYCLE.md` §3 (how a lane ends) rather than re-deciding it.
`M5-DELIVERY-PLAN.md` owns a *foreign* project's ceremony; this owns Dodona's own.

## 0. The one-line statement

The operator drives this by voice, which means text in the box, which means `RouteInput`. That
ladder answers **which lane**. It does not answer **is this load-bearing work, does it need a
checkout of its own, and does it collide with what another lane is already doing** — and nothing
starts or finishes the ticket process from where the operator is sitting. So the isolation this
system was built to provide is reachable only by typing a CLI command in a terminal, which by
voice is not reachable at all.

## 1. What exists today, from the code

- `RouteInput` (`src/Dodona/Daemon.cs`) has four verdicts —
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
- **The claim gate exists only inside ticket worktrees.** A plain lane has no `PreToolUse` hook
  at all, so nothing in code stops it writing anywhere in the operator's live tree. That is the
  hole the operator named.
- **The router's fact sheet does not say what any lane OWNS** (`FactSheet`): it carries titles,
  busy/idle, the last readable line, and whether the input refers back. Not claims, not
  worktrees, not who holds the merge token. So the classifier cannot reason about collision even
  in principle.
- The ticket spine itself is sound and covered by `m1`: branch `ticket/<id>`, worktree, claim
  gate, merge token, backstop, ff-only land, prune. What is missing is both ends of it.
- **`ticket-agent` has never run with a real `claude` in any of the thirteen suites** — only the
  fake agent (`m3`, `workspace`), plus `tests/m2-live.ps1`, which is not one of them.

Measured while writing this, because the async decision turns on it: `git worktree add` on this
repo is **sub-second** (0.72 s for the whole `dev worktree` verb, ~136 ms of that merely starting
PowerShell). The expensive part of a fresh ticket is the **cold build** in a tree with no
`bin`/`obj` — the agent's first task, not the box's wait.

## 2. Principles this is held to

- **Enforcement in code, never a prompt** (§0). A guarantee that depends on a model answering
  correctly is not a guarantee. Prompts remove friction; they never provide safety.
- **Responsive first.** The box already pays ~1 s for the classifier and the operator accepted
  that price once. This feature is allowed **zero additional model calls** on the input path.
- **Derive in code what is not really a judgement** (§2.2). Who owns which path, who holds the
  token, whether a lane is in a worktree: all facts.
- **Deny with a rewrite, never a wall** (`M5-DELIVERY-PLAN.md` §1). A refusal that names its
  substitute keeps the work moving; a bare refusal strands the agent.
- **Act, announce, allow undo** (§11). No dialog, no modal, no blocking question.
- **Never hung, halted, stuck, or outdated** (§0.1). Every wait names the thing that un-sticks
  it, and no rung may end in "go and type a command in a terminal".

## 3. The shape: three layers, and the model is the OUTER one

The first draft of this plan put the classifier's `isolate` verdict in front of the shared
checkout — get it right and the lane is isolated, get it wrong and an agent does real work in
the operator's live tree. **That is a prompt providing safety, which §0 forbids**, and it fails
in the case that matters most: a lane that starts as a lookup ("why is the pane blank") and
becomes work three sentences later. No spawn-time decision, however good, can catch that.

Inverted, the layers are:

**Layer 1 — code, unconditional. No agent writes into a project outside a worktree.** The claim
gate is deployed to **every** lane, not only ticket lanes. A write whose path resolves inside a
project but outside a worktree is refused. This is the same shape as the pre-commit hook that
already refuses commits from the shared checkout, one step earlier, and it fires whether the
router guessed right, guessed wrong, or was never consulted.

**Layer 2 — the refusal is a promotion, not a wall.** On that first refused write the daemon
creates the ticket, materialises the worktree, deploys the gate, and **respawns the lane into
the worktree resuming its session** (`RespawnLaneAsync` already takes a working directory, and
`--resume` rebuilds context — measured in spike 1). Nothing has been written yet, so there is
nothing to move: the agent comes back with its context in a tree it is allowed to work in, and
retries. Announced, with the undo.

**Layer 3 — the classifier's `isolate` flag is an OPTIMISATION.** When the router can already
tell the sentence is load-bearing, the lane is born in a worktree and layer 2 never fires. Get
it wrong in either direction and layer 1 catches it: a lookup that turns into work is promoted
on its first write, and a ticket created for what turned out to be a lookup is abandoned by
D-9's undo. **The model's judgement buys latency, never correctness.**

Read-only work needs no worktree and gets none. That is not a carve-out — it falls out of the
design, because **the write attempt is the first moment isolation is knowable for certain**.

## 4. Coordination: what the manager decides, and what code decides

The operator's frame: the manager exists to stop lanes screwing each other over. Splitting that
honestly:

**Facts, settled in code — no model involved.**

| collision | already handled by | gap |
|---|---|---|
| two lanes editing one file | claim conflict at `ticket-create` | the refusal does not say *who* holds it |
| two lanes merging at once | the merge token, one per repo | none |
| two lanes building in one tree | a worktree per ticket has its own `bin`/`obj` | plain lanes share the live tree — layer 1 closes it |
| work in the shared checkout | nothing today | layer 1 |
| a lane whose repo left the workspace | refusals at token, land, claim-extend | none |

**Judgement, and the only place a model belongs.** Is this sentence the same work another lane
is already doing? Does this change undercut what another lane is mid-way through? Those need
the transcript and cannot be derived. They are asked of the classifier **in the call that is
already being made**, which is what keeps the latency budget at zero.

**So the fact sheet grows** (P3): per lane — whether it is isolated and where, what paths it
claims, whether it holds the merge token, whether it is mid-turn. All store reads, all free.
The classifier cannot reason about collision today because it is not told anything to reason
with; this is the cheapest half of the coordination work and it should land first.

**D-12. The operator's words are passed through verbatim; facts are attached alongside.** The
router decides *where*, never *what*. Rewriting or summarising the operator into an agent is a
game of telephone whose errors are unrecoverable — you cannot unsay a sentence, and a
paraphrase is a sentence. What the agent receives is the operator's own text plus a derived
context block: which lane it is, which tree it owns, which paths another lane holds. Facts, not
interpretation.

**D-13. A refusal names the holder.** "Denied: `src/Dodona/Daemon.cs` is claimed by lane WATER
(ticket 4)" is actionable — the agent can say so, and the operator can retarget. "Denied: outside
your claim" sends the reader hunting. Same rule as §0.3's "name the real cause".

## 5. Decisions

**D-1. Ticket-worthiness is a flag on the existing `new-task` verdict**, not a fifth verdict and
not a second model call: `{"kind":…,"target":…,"isolate":true|false,"confidence":…,"reason":…}`,
read only when `kind` is `new-task`. Per §3 it is an optimisation, not the mechanism.

**D-2. `isolate` defaults false and needs positive evidence.** The classifier is asked *will
this change files in the repository* — not length, not imperative mood (§5.1 rejected length on
the operator's own correction). A wrong `false` is now cheap, because layer 1 catches it.

**D-3. The verdict is synchronous; materialisation is not.** Lane row and pane appear the
instant the verdict lands, presence `preparing…`, and the sentence is delivered when the agent
attaches. `LaneCreate` already precedes `AttachShimAsync`. The operator never waits on git or a
build.

**D-4. Claims are elastic while the ticket is scoping.** A sentence spoken out loud cannot supply
claim paths, and a frozen wrong claim strands the agent mid-work. So while a ticket is
`scoping`, an out-of-claim write **auto-extends the claim and announces it**; the gate refuses
only what another **open ticket** claims (naming it, per D-13). The claim freezes at the first
`token-request`.

This changes what the claim gate is *for*: from bounding an agent to a prediction, to preventing
collision between lanes — which is the operator's own framing of why tickets exist. It is
defensible because the gate only matches `Edit|Write|MultiEdit|NotebookEdit`, never saw a `sed`
or a heredoc, and fails open by design; the invariant that matters (no two tickets over one path,
no two merge tokens over one main) is untouched and still refuses. Review moves to the backstop
at `token-request`, where the diff is a fact rather than a guess: *this touched X, Y and Z —
approve?*

**D-5. Verify runs BEFORE the merge, in the worktree.** Today `LandOp` merges, commits the
fence, and *then* verifies — so a red verify has already advanced main. Because the merge is
`--ff-only`, main's post-merge tree is byte-identical to the branch tip, which makes verifying
the worktree **exactly equivalent** and strictly safer. A red verify refuses the land. A bug fix
independent of everything else here.

**D-6. Verify must go through `tools/dev.ps1`.** `dodona.json`'s verify is a bare
`dotnet build Dodona.sln -c Release -warnaserror` via `cmd /c`. A locked output file makes that
report `Build FAILED` with ten screens of MSB3026 — the exact false diagnosis §1 mandates the
wrapper to prevent, arriving when nobody is watching. It becomes `dev gate`, which CLAUDE.md §1
requires before a merge to main and which no ticket has ever run.

**D-7. Approval is rendered, not hunted.** `token-request` under `on-approval` announces
`dodona approve N` — a GUI telling you to go and type. It becomes an **`open` row in
`questions`**, so the ask overlay renders it live, `ui dump`'s `ask` key shows it headless, and
one answer path serves both (D-L4). Voice composes the word, the operator presses Enter: the
dictation constraint preserved, not worked around.

**D-8. Auto-land stays opt-in per project.** A ref advance is the one irreversible act, so
responsiveness is bought by making approval *arrive*, never by deleting it.
`"landing": "auto-when-green"` exists for a project that wants it; this repo stays
`on-approval`.

**D-9. `lane-stop` on an isolated lane abandons its ticket** — prune the worktree, delete the
branch, release the claims, announce. The ladder's optimism rests on a wrong lane being free, so
the undo line has to be true.

**D-10. The agent is told the tail, and its two traps are enforced rather than written down.**
`TicketSystemPrompt` names commit → `token-request` → `land`; it names none of them today, so an
agent follows `/ship` instead. And `/ship` step 4 from a ticket worktree runs
`dodona publish --project .`, which builds **that worktree** and hot-swaps the operator's live
app to an unfinished branch. So: `publish` refuses a `--project` inside `.dodona/wt/` unless
`--from` is explicit, and `dev worktree` refuses to run from inside a ticket worktree (CLAUDE.md
§0.0 tells every session to make one with no carve-out, and obeying it there puts the agent on a
fresh branch off main, outside its own claim).

**D-11. How the lane ends is already decided.** `LANE-LIFECYCLE.md` §3: landed + no further
ticket → the one judgement call, asked once at completion, with the code-checkable preconditions
listed there. Wire into it; re-decide none of it.

(D-12 and D-13 are in §4.)

## 6. Phases

| | what | proof |
|---|---|---|
| **P1** | **Layer 1**: deploy the gate to every lane; refuse a write inside a project but outside a worktree, naming the holder (D-13). | `m1`: a plain lane's write into the shared checkout is refused. Must be `dev prove`-d red — today there is no gate there at all. |
| **P2** | **Layer 2**: promotion on that refusal — ticket, worktree, gate, respawn-with-resume, announce, undo. | `m2`: a plain lane that attempts a write ends up in a worktree with its session resumed and nothing written to the live tree. |
| **P3** | The fact sheet grows (§4): what each lane owns, claims, token, mid-turn. No new call. | `unit`: the fact sheet renders ownership for isolated and plain lanes. `m2`: a colliding sentence is routed to the owning lane rather than a new one. |
| **P4** | **Layer 3**: `isolate` on the verdict; isolated spawn; fallback to a plain lane when `ticket-create` refuses. | `unit`: verdict parses, `isolate` defaults false, ignored unless `new-task`. `m2`: an isolating sentence is born in a worktree, skipping promotion. |
| **P5** | D-4: elastic claims — auto-extend while scoping, announce, freeze at `token-request`, still refuse another ticket's paths. | `unit`: the algebra. `m1`: a scoping widen is allowed and announced; a second ticket's path is refused. |
| **P6** | D-5 + D-6: verify in the worktree before the merge; `dodona.json` verify becomes `dev gate`. | `m1`: a red verify leaves main's sha unchanged. `dev prove` first — most likely phase to look green against the old order. |
| **P7** | D-7: approval as a question row; overlay + `ui answer`. | `ui-use`: the ask offers the approval and answering it grants the token, at a live window. |
| **P8** | D-10: the prompt tail, the `publish` refusal, the `dev worktree` refusal. | `publish`: `publish --project <a ticket worktree>` refuses and names `--from`. |
| **P9** | D-11: wire landing into `LANE-LIFECYCLE.md` §3's completion question. | `m1` + `m3`: after a land the lane is dormant, the question is asked once, preconditions gate it. |

**P1–P2 are the safety and come first.** They make the operator's named failure — real work
started outside a worktree — structurally impossible, and they do it without any of the model
work. P3–P4 are the responsiveness on top. P6 is a bug fix landable on its own.

## 7. Rejected — do not re-propose

- **A separate ticket-worthiness model call.** Two round trips on the input path, double the
  latency the operator agreed to once, and quota is the scarce resource. It rides on the existing
  verdict or it does not happen.
- **Making the classifier's verdict the thing that keeps agents out of the shared checkout.**
  This plan's own first draft. A prompt providing safety (§0), and blind to the lookup-turns-
  into-work case, which is the common one.
- **Brain-proposed claims, frozen at creation.** The stranding case: the agent needs one file
  nobody predicted, the gate denies, and by voice there is no way to extend.
- **A whole-repo `path:.` claim instead.** Bounds nothing, and makes every other ticket a
  conflict — destroying the one invariant claims exist for.
- **Promotion AFTER a lane has edited the shared checkout.** Still rejected: the edits are in the
  wrong tree and there is no safe way to move them, because `git stash` is repo-global (§5.2) and
  two lanes stashing interleave one stack. Layer 2 promotes on the *attempt*, before anything is
  written, which is a different act with none of that exposure — the distinction is the whole
  reason layer 1 sits at the write and not at the commit.
- **Auto-landing by default.** See D-8; a ref advance has no undo.
- **A "create a ticket?" dialog.** §3.1 and D-L4: no modals — a test window is forbidden from
  producing one, so it would be permanently untestable, which is why `StartLaneWindow` has no
  coverage today.
- **The router rewriting or summarising the operator.** See D-12; a paraphrase is a sentence, and
  a wrong one cannot be unsaid.
- **Deciding isolation from sentence length or mood.** §5.1 rejected length on the operator's own
  correction; mood is the same mistake holding a grammar book.

## 8. Traps this will hit

- **A gate on every lane is a `PreToolUse` hook on every lane, and hooks cost.** The two that
  were deleted cost 255 ms per edit, 136 ms of it merely starting PowerShell (§0.0). This one is
  `dodona.exe gate-hook` — one process, not a script shelling out to a binary — but it must be
  measured before P1 is called done, not assumed.
- **`GateHook` fails open, and layer 1 makes that load-bearing.** Today a fail-open gate means a
  ticket lane's write slips past to the backstop. Under layer 1 it means a write into the live
  tree. Every fail-open path there needs re-reading against that (§3's BOM incident is what
  fail-open already cost once).
- **`ticket-create` refuses on claim conflict**, so promotion can fail at the moment it is
  needed. It must degrade to a refused write with a named holder, never to a silent allow.
- **`lane-respawn` refuses to re-home a ticket lane** — deliberately. Layer 2 goes the other way
  (plain → ticket), which that refusal does not cover, so it needs its own path and its own
  check.
- **`land` demands main checked out in the shared checkout.** True while §0.0 keeps the operator
  there, but it must announce rather than fail quietly.
- **ff-only refuses when main moved under an open ticket**, and nothing rebases. Under D-8 this
  is where a "never stuck" wait belongs: arm the land behind a rebase, announce, do not park.
- **A ticket worktree lives at `<member>/.dodona/wt/t<N>`, inside the repo.** Anything walking
  the tree must keep skipping `.dodona` — `Git.FindRepos` and the repo lint already do.

## 9. Open questions

- Does an isolating verdict need a name for the ticket, or is the lane title enough? The title
  is already brain-chosen and reviewed, so a second name is probably noise.
- Should `isolate` be suppressed while the same project already has N open tickets? Cheap to
  count in code, and unclear whether that is a real limit or an invented one. Measure first.
- Layer 1 refuses writes inside a project outside a worktree. What about a write **outside every
  project** — a scratch file in `%TEMP%`, a note in the operator's home? Today `claim-check`
  refuses it for ticket lanes. Refusing it for every lane may be right, or may break ordinary
  work in ways only use will show.
