# Orchestrator design review — findings and the way forward

Review of [ORCHESTRATOR-DESIGN.md](ORCHESTRATOR-DESIGN.md), 2026-08-17. Produced by four
independent review lenses (concurrency/durability, latency/cost, product/UX, build strategy),
three Claude Code capability verifications, and timing measurements on this machine.

**Verdict: the architecture is right. Nothing here says "rethink."** Every blocker below is a
*specification gap* — a mechanism the doc relies on but never defines — not a wrong decision.
The keystone ideas were independently endorsed by all four lenses: single-writer daemon over
SQLite/WAL with check-and-insert in one transaction; code-hands-permission-agents-do-work;
leases not locks; git-as-truth with reconcile-on-start; panes as views over store state;
lane/ticket/agent lifetime split; act-announce-undo; contention surfaced at plan time.

---

## 0. Measured and verified facts

Timings on this machine (claude 2.1.233, Windows 11, PowerShell `Measure-Command`):

| Operation | Wall time |
|---|---|
| `claude --version`, cold | 1.18s |
| `claude --version`, warm | 0.18s |
| `claude -p "…" --model haiku`, one-shot | **3.6–3.8s** |

So a one-shot CLI call can never meet a 300ms budget — the warm *process spawn alone* eats
most of it. This kills one implementation strategy, not the design (see §2 below).

Verified against Claude Code documentation:

- **`--resume <session-id>` continues the same session id** (use `--fork-session` to fork).
  Full history, model, permission mode restored. Sessions are JSONL on disk under
  `~/.claude/projects/<cwd-slug>/` and survive reboot.
  ⚠ **Default retention is 30 days** (`cleanupPeriodDays`) — set it high, or long-lived
  lanes lose their resume path. ⚠ Session storage is **keyed to the cwd** — worktree
  removal interacts with session files; decide removal timing relative to retention.
- **Hooks can inject mid-turn.** PreToolUse, PostToolUse, UserPromptSubmit and Stop can all
  return `additionalContext` (≤10,000 chars) that lands inside the running turn. §5's
  injection mechanism is confirmed viable as designed.
- **`--model haiku|sonnet|opus` works per headless invocation.** Per-invocation reasoning
  effort is *not* clearly documented for `-p` — verify `--effort` in the week-1 spikes.
- **Headless `-p` runs on subscription auth and draws from the same usage pool** as
  everything else. Concurrent-session limits are not officially documented; treat all
  third-party numbers as stale and calibrate empirically (§2.6).
- **Long-lived stream-json sessions**: docs are thin, and one verification agent claimed
  each `-p` invocation is single-turn — but the docs' own line about a message sent
  mid-turn "staying queued and running as its own turn" is only possible in a
  multi-message session, and the Agent SDK is built on exactly this. Treat as
  near-certain, confirm in spike 1. The stream-json wire schema is **not** formally
  published — a .NET driver is written against observed behavior, so pin the CLI version
  per Dodona release and add a protocol smoke test to the build.
- Two processes resuming one session id **interleave into one transcript** — not
  corruption, but never do it; the store must treat session ids as single-owner.

---

## 1. Blockers — specify before building

### 1.1 Claims are declared but never enforced
Nothing stops an agent writing outside its claim; every downstream guarantee (safe
parallelism, branch-from-main decision, conflict-free merges) silently degrades to
advisory. **Fix, two code-only layers:** (a) a PreToolUse hook in every worktree gating
Edit/Write against the lane's current claim — a violation denies with "request a claim
extension," and the extension re-runs the intersection, which is the natural escalation
trigger; (b) a merge-time backstop — at token request the daemon diffs the branch against
its merge base and refuses the token if any touched path is outside the claim. Layer (b)
catches what hooks can't see (Bash writes).

### 1.2 The agent↔daemon interface does not exist in the doc
Agents declare claims, request tokens, release them — *how*? This is the load-bearing
contract of the system. **Fix: MCP, not a shelled-out CLI.** A ~300-line C# stdio proxy
(official `ModelContextProtocol` NuGet) that connects to the daemon's per-instance named
pipe, exposing typed tools: `claim_declare`, `claim_extend`, `merge_token_request`,
`merge_token_release`, `land_ticket`, `status_update`, `ticket_info`. Passed via
`--mcp-config` inline JSON at spawn. Typed schemas the model can't malform; tools visible
in context so the protocol is discoverable, not memorized; auto-approvable via an
`mcp__dodona__*` allowlist; reconnects across daemon hot-swaps exactly like the shim.
The dispatcher uses the same mechanism with a wider toolset (`lane_create`,
`ticket_create`, `agent_kill`). Keep a small human-facing `dodona` CLI on the same pipe
for your own debugging; agents never shell out to it.

### 1.3 The pane-event log is not in the store
§13's whole update story rests on "a pane is a view over store state," but §2's store
inventory has no message log — the swap queue is *drained*, not retained. Every UI
restart produces six amnesiac panes, violating the doc's own no-lost-state rule, in the
self-hosting phase where the UI restarts daily. **Fix:** add
`pane_events(lane_id, seq, ts, kind ∈ {user_input, agent_compressed, announcement,
receipt, system}, body, decision_id?, raw_ref?)`. A pane is a replay of the last N rows.
`raw_ref` points at session-id + message-uuid so "raw one keystroke away" also survives
restart. Draining the swap queue *moves* rows into pane_events, never deletes.

### 1.4 "One keystroke reverses it" has no undo record
§11 removes the ask-you path on the strength of an undo mechanism that is never
specified — and for some decisions the inverse is genuinely non-obvious (undoing
fold-into-lane after the agent committed mixed edits). **Fix:** every automatic decision
writes a decision row `{id, ts, kind, lane, ticket, params, inverse_op, snapshot,
validity}` — snapshot captures branch SHAs and session ids at decision time. Announcements
carry the decision id; undo executes `inverse_op`. Define the closed set of inverses
(retarget → recall-if-unconsumed else structured retraction + redeliver; fold-in → kill,
reset branch to snapshot, spawn lane with extracted brief; new-lane → kill lane, deliver
brief to target; restart → resume old session, reset worktree). Undo may legitimately be
"kill + reset + reseed, seconds" rather than perfect rewind — *say so*. The `validity`
predicate (e.g. "until ticket merges") makes undo refuse with a reason instead of doing
something surprising. An undo writes its own decision row: redo for free.

---

## 2. Design amendments — majors, grouped

### 2.1 Latency: take models off the critical path instead of making them faster
The measured 3.6s one-shot call breaks §4's tier-1 budget as an implementation, and even
warm Haiku turns are ~1–2s, not 300ms. But the requirement is *responsiveness*, and the
design already contains its own answer — apply "undo beats accuracy" one level up:

- **The deterministic ack (<100ms, code) is the felt latency for every input.** Write it
  into §5 as the hard guarantee: *no model is ever between a keystroke and visible
  feedback.* Models refine what code already did.
- **Route optimistically.** Deliver to the focused lane immediately; the classifier runs
  behind as an async second opinion and issues a visible retarget + undo when it
  disagrees. Router latency stops mattering.
- **Persistent warm utility sessions**, not one-shot spawns: at daemon start, spawn a
  long-lived Haiku stream-json session behind the same shim pattern as lanes — router,
  cleaner, and compressor turns each land on an already-warm session (~1s). Blue/green
  recycle at ~100 turns: warm the replacement with a no-op turn before cutover.
- **Merge the router and the input cleaner** into one Haiku turn returning
  `{intent, target, confidence, cleaned_text}` — they read the same text; this halves
  utility volume and removes a serial hop.
- Revised honest budgets: ack <100ms · warm tier-1 ~1s · tier-2 ~2–4s · adjudication
  30s–3min (it's an agentic codebase read — pre-seed it with both claims, the decision-log
  excerpt, and the overlapping paths so it rules on evidence rather than re-discovering
  it) · first token of a real answer 1–2s, with a thinking indicator so the gap reads as
  alive.

### 2.2 Compression: most of it is not a judgment — derive it in code
Every-message Haiku compression at 6 lanes is ~200–700 calls/hour from the same
subscription pool the lanes need, and most of those calls contain no judgment — violating
§1's own rule. stream-json already carries structured `tool_use` events, so presence
lines ("editing Water.cs", "running tests", "idle") are computable with **zero** model
calls. Reserve Haiku for turn-final summaries, BLOCKED/needs-you messages, and
announcements — a 5–10× volume cut. Run 2–3 pooled compressor sessions, never one (a
single compressor accumulating six lanes' chatter is exactly the unbounded serialization
point §3 forbids the dispatcher to be).

### 2.3 Merge discipline: verify before the token, fence the land
Held-across-verify serializes all merges behind full builds (hours of queue at 6 lanes);
released-before-land lands stale verifies. Neither is stated. **Fix:** rebase onto current
main and verify *before* requesting the token; the grant records the main SHA the verify
ran against; unchanged → land in seconds; moved → re-rebase, re-verify (build-only when
the intervening landings had disjoint claims). Leases get **heartbeat renewal** (renew
every N, expiry 3N) and **fencing**: the final ref update of main is executed by the
daemon itself, checking lease owner + generation inside the same store transaction that
records the land — an expired holder physically cannot land, and this sidesteps git's
hazards updating a branch checked out in your own primary worktree. `merge: on-approval`
tickets don't enter the FIFO until approved. Recovery is mechanical: ancestry check says
landed-or-not; every cleanup step idempotent.

### 2.4 Claim algebra: define it, or someone implements the impossible version
Glob-vs-glob intersection is regular-language emptiness, not a set op, and expanding
against the current tree is blind to files that don't exist yet — a core case. **Fix:**
restrict path claims to literal paths, directory-subtree prefixes, and declared-new
literal paths — intersection becomes equality/prefix checks, microsecond-cheap, and new
files are covered by their subtree. Renames must claim both old and new paths. Claims are
released in the same transaction that marks the ticket landed or abandoned.
**Semantic collisions** (WaterFlowController vs FluidFlowManager in disjoint files — the
doc's headline failure, invisible to extensional claims): add a one-sentence `intent`
field to every claim (§11's Haiku pass already produces it); after the code intersection
passes, one cheap model comparison against active tickets' intents; above a threshold →
adjudicator. Consistent with §1: concept overlap *is* a judgment.

### 2.5 Lifecycle contradiction: overlap must not share a worktree
§11 says overlap "continues that branch and worktree," but a branch *is* a ticket and
"ticket lands → worktree removed" — if A lands while B works in A's worktree, the rug is
pulled. **Fix:** overlap means B queues behind A *in the same lane* with its **own**
worktree branched from A's head (or from main once A lands). Claim refinement (the
agent's first act) mandatorily re-runs the intersection: within provisional → proceed;
overlaps in-flight → delete the just-created worktree (seconds, by the doc's own
economics) and re-branch per the rule; ambiguous → adjudicator.

### 2.6 Quota is the real budget and nothing watches it
Six concurrent lanes ≈ 6 model-hours per wall-clock hour, from the same pool as the
dispatcher, router, and compressor — an Opus-default fleet can exhaust a weekly cap in
under a day, and an intra-day burst can lock *everything* out mid-turn. **Fix: a quota
governor in the daemon.** stream-json carries per-message usage fields — meter per
lane/model into the store; set model per ticket type (Sonnet default, Haiku/low for
mechanical `merge: auto` work, Opus only for design-tier tickets — §9's lever made
policy); as the window fills, admit fewer lanes and queue non-urgent tickets rather than
letting everything stall at the cap; show a usage presence line in the dispatcher pane.
Calibrate from measured telemetry, not published numbers.

### 2.7 The dispatcher's context grows all day with no policy
It's the one component whose responsiveness the design most protects, and in-session
auto-compaction will fire at an unpredictable moment. **Fix:** make the doc's own claim
literal — everything the dispatcher needs lives in the registry, so the dispatcher
session is disposable like the agents. Blue/green recycle at ~50–100 turns from a
registry-generated brief, swapped at an idle moment so the pane never blanks.

### 2.8 Single-writer vs "queue in the store" during a swap — a real contradiction
While the daemon is down there is no writer, so inbound messages sit in pipe buffers and
die with a shim. **Fix:** narrow the single-writer rule to the *state* tables; each shim
is the sole writer of its own append-only inbox table (disjoint writers on WAL with
busy_timeout is fine; per-lane ordering free from rowid). The daemon remains the sole
mutator of claims/tickets/tokens/lanes.

### 2.9 Permission policy: unspecified means either stalled or unbounded
A headless session with no policy blocks forever at its first Edit;
`--dangerously-skip-permissions` is six autonomous agents with your whole machine. And
the merge token is advisory at the git layer — any agent can push. **Fix:** worktree
creation deploys `.claude/settings.json`: acceptEdits; Read/Edit/Write scoped to the
worktree; Bash allowlist for `dotnet build/test` and branch-local git; **deny `git push`
and merges into main** — landing happens only through the daemon's `land_ticket` tool,
which checks the token: enforced, not advisory. Residual prompts route via
`--permission-prompt-tool` to a daemon tool — auto-approve in-worktree, anything else
flips the lane to `waiting on you`.

### 2.10 "Verify" is undefined, and `merge: auto` is unsafe until it isn't
**Fix:** `dodona.json` at project root: verify steps (for MassWorks:
`dotnet build MassWorks.sln -c Release -warnaserror` then `dotnet test`), timeouts, the
definition of *land* (ff-only merge to main, push optional), and per-lane run commands +
ports for §10. `merge: auto` requires a green verify run inside the token window,
post-rebase. The **daemon** (code) runs the post-merge verify on main; red auto-creates a
blocked ticket routed per §11, and a red verify while holding the token auto-releases it.

### 2.11 Windows realities
- **Get out of OneDrive/Documents.** OneDrive touching a live WAL or `.git` is a known
  corruption vector; Defender taxes every hot operation. Project roots and worktrees at
  short paths (`C:\src`, `C:\w\mw\<ticket>` — also solves MAX_PATH, plus
  `core.longpaths=true`), Defender exclusions for source root, worktree root, store dir.
- **Worktree removal will routinely fail** while MSBuild nodes, VBCSCompiler (lingers
  ~10 min), testhost, or the running game hold locks — and §10 *tells you* to have the
  game running. The daemon owns launched instances: record pid at launch; on land, close
  gracefully with an announcement ("SKYBOX build closed to land ticket") or lazy-prune on
  exit; `dotnet build-server shutdown`; retry with backoff; on persistent failure surface
  the lock holder — never fail silently. Stamp launched windows with lane name + colour;
  `run skybox` focuses an existing instance rather than spawning a second.
- **Build semaphore**: 2–3 concurrent verifies, or six MSBuilds starve the machine.
- **Instance identity by path string is unsound** (case, 8.3 names, junctions): derive the
  id from the *canonicalized* final path AND hold a named mutex keyed to it so a second
  daemon on the same repo refuses to start. Five lines; take both.
- **Reattach by pid is unreliable** (aggressive pid reuse): the shim's named pipe is the
  daemon's sole reattach handle; the shim disambiguates its child by pid + start time.
- **A running exe can't be overwritten**: version the daemon install per build; the
  supervisor role disappears via *successor handoff* (old daemon spawns new binary, new
  one signals ready on a control pipe, old exits, new adopts shim pipes) plus
  start-on-demand (any client that can't connect to the daemon pipe launches it — which
  also honors "the registry is a store, never a service").
- Run the daemon's SQLite connection at `synchronous=FULL` — at this write rate the cost
  is nothing, and it removes the power-loss window where a lost claim row silently
  re-enables collisions.

### 2.12 Attention model: three consistency fixes
- **Badges need the toast rule's taxonomy** or they're blind in days: progress lines never
  badge (presence already shows `working…`); announcements count-badge; blocked-on-you
  gets a categorically distinct signal — glyph + border highlight (border, not fill:
  colour-means-lane survives).
- **A pinned decision feed** in the dispatcher column: every announcement appears in its
  lane *and* the feed; unacknowledged ones persist until acked/undone/expired; each row is
  an undo target. This is where "missing it costs a wrong build" stops being true, and
  it gives the undo model a home after lines scroll.
- **The merge-approval affordance** — the one intentionally gated action — needs UI:
  presence `waiting on you: merge`, blocked signal, one-key approve/deny in the feed.

### 2.13 Speech and briefs
- **Defer voice to its own milestone and say so** — routing is input-agnostic, so this
  costs nothing now. Milestone 1 is text; Win+H dictation into the input box is a
  zero-code stand-in that exercises the cleanup pass on real dictation. Design the input
  boundary now: router consumes `{text, modality, focused_lane, ts}`, and voice input
  down-weights the focus prior (you're often looking at the game while talking).
- **The brief needs a correction loop**: the ticket announcement shows the distilled brief
  *verbatim* and is undoable; raw dictation is stored beside the cleaned brief (both you
  and the agent can consult it when the cleaning reads oddly); small edits inject
  `brief revised: <diff>`, large drift kills and reseeds — already priced as cheap. Feed
  the cleaner the lane titles and recent ticket nouns so mishearings correct toward known
  vocabulary instead of being normalized into something plausible-but-wrong.
- **Retarget undo must know consumed-vs-unconsumed** (§4's promise races §5's injection —
  you can't unread an instruction): unconsumed → true recall; consumed → structured
  retraction + redeliver. Optionally a ~2s delivery grace below a confidence threshold.

### 2.14 Remaining minors, one line each
Overlay-maximize a pane (Esc restores; grid never reflows) — raw transcripts need
somewhere to render. Trayed lanes are **dormant** (no agent, no worktree until promoted;
promotion is an announced, undoable decision). Dispatcher pane: pick the **right column**
(vertical history, squarer panes, natural home for feed + tray). Scratch-file injection
gets a real protocol: per-lane instruction table in the store, hook fetches unseen-since-
cursor via the shim, cursor advance is the ack. Observability from day one, since schema
is the unretrofittable piece: append-only `events` table, `routing_decisions`
(input, tier, confidence, target, undone-flag — **the undo keystroke is free labeled data
for tuning §4's threshold**), tee raw stream-json to `logs/<ticket>/`, retention tied to
ticket land.

---

## 3. Stack

**C#/.NET 8+ end-to-end.** A Go/Rust daemon is a second language for zero payoff on local
I/O-bound plumbing; Electron/Tauri is packaging weight for what §13 insists is a dumb
disposable view.

| Piece | Choice |
|---|---|
| Daemon | Generic Host console app, normal user process (never a Windows Service — session 0 + auth lives in your profile) |
| Shim | dependency-free console app, self-contained/AOT publish, so "essentially never changes" stays true; version the pipe protocol anyway |
| Pipes | `System.IO.Pipes`, length-prefixed JSON frames, names derived from canonical project root |
| Store | `Microsoft.Data.Sqlite`, plain SQL, `PRAGMA user_version` migrations (EF Core fights the hot-swap discipline); daemon holds the sole write connection; UI reads via read-only WAL connection, writes via the pipe |
| Agent interface | `ModelContextProtocol` NuGet stdio proxy → daemon pipe |
| UI | WPF (mature, native toasts, no MSIX friction; Markdig → FlowDocument for markdown). If streaming-markdown fidelity disappoints, host the grid in one WebView2 later — cheap precisely because the UI is disposable |

---

## 4. The way forward

### Week 1 — four spikes, each a day or less, before any architecture is poured

1. **Resume durability**: multi-turn stream-json task → kill claude.exe mid-turn →
   respawn `--resume`, same cwd → context intact? Repeat across a reboot. Confirm
   long-lived multi-message sessions while you're in there, and document the
   cwd↔session-storage coupling (decide worktree-removal timing vs retention).
2. **The shim**: real ~100–200 lines — spawn claude with redirected stdio, one duplex
   `NamedPipeServerStream`, no inherited handles, detached. Connect from process A, kill
   A, connect from B: zero message loss, shim outlives both.
3. **Mid-turn injection**: PostToolUse/PreToolUse `additionalContext` into a live turn —
   does the model integrate it or re-plan? (Mechanism verified in docs; behavior needs
   eyes.) If it disappoints, inject at turn boundary via stream-json and accept the
   latency.
4. **Quota + warm latency**: 6 concurrent sessions doing real MassWorks work for an hour —
   throttling behavior, burn rate, warm-session turn latency for Haiku. Also verify
   `--effort` under `-p`. This calibrates the governor and the §4 budgets with your
   numbers.

### Milestones

- **M0 — walking skeleton** (~wk 1–2): shim; minimal daemon (store with `user_version`,
  spawns one lane via shim, messages as rows); console client. One ticket → worktree →
  agent → hand merge. **Acceptance test: kill and restart the daemon mid-agent-turn; the
  session must not notice.** The three unretrofittable pieces — shim, queue-in-store,
  schema versioning + idempotency keys — all live here.
- **M1 — coordination**: claims + intersection + hook gate; merge-token lease + fenced
  land; MCP tool surface; `dodona.json` verify config. Two lanes in parallel on
  MassWorks, `merge: on-approval` only.
- **M2 — the conversation**: dispatcher session; tier-0 prefix routing; optimistic
  delivery + async tier-1 (warm Haiku, merged router+cleaner); code-derived presence;
  selective compression.
- **M3 — the UI**: WPF grid as a dumb view over the store; pane_events replay; decision
  feed; badges/presence/toasts; overlay maximize.
- **M4 — self-hosting**: hot-swap publish flow end-to-end, successor handoff, versioned
  binaries. **Do not dogfood Dodona-on-Dodona until M4's swap test passes** — until then
  build it in plain Claude Code sessions.
- **M5 — judgment & polish**: adjudicator (pre-seeded), tier-2, semantic-intent claim
  check, `merge: auto`, quota governor UI, run-per-lane.
- **M6 — voice**: global push-to-talk, streaming STT (local whisper vs Azure/Deepgram),
  vocabulary biasing via the cleanup pass.

### Status — M0–M4 done, and two things the plan did not name

M0–M4 all pass, each with a model-free acceptance suite (111 checks across six suites;
fake agents, so the whole thing runs free and races are reproducible). **The M4 swap test
passes, so the prohibition is lifted and Dodona-on-Dodona is allowed.**

Two additions came out of using it rather than planning it:

- **An app shell.** Dodona was a CLI harness — set an env var, run a daemon at a path.
  It is now an application: launch it, pick a project, go. `publish` emits daemon, shim
  and UI into one folder and the UI summons its own daemon. Instances share nothing, so a
  second project is a second window, and that *is* multi-project.
- **Workspaces.** A project root holds either itself as a repository or several
  underneath. Identity stays one store, one daemon, one grid, one dispatcher; the **merge
  token became per repository**, so `engine` and `tools` land in parallel while two
  tickets in `engine` still serialize. A ticket belongs to exactly one repository —
  landing fast-forwards one main, and two fast-forwards cannot be atomic — and its
  repository is inferred from its claim paths, which are workspace-relative and therefore
  already say which. Sequencing a change that spans repositories is two tickets and a
  judgement call, which makes it M5's adjudicator problem, not the merge queue's.

Git is now required **where it is used**, not at the door: a project opens and runs lanes
with no repository at all, and `repo-init` creates one when a ticket first needs a branch.

### Carried into M5 — recorded from use, not yet built

- **A short unique label per project and per repository.** With lanes spread across
  repositories and across project windows, the lane name alone no longer says where work
  will land. **Algorithm: the shortest path suffix that is unique within the current
  set** — the deepest folder segment normally (`masswork`, `dodona`), extended leftward a
  segment at a time and joined with `/` only where that collides (`client/src` vs
  `proj/src`). Recompute when the set changes; the same rule serves both levels, the open
  projects across windows and the repositories inside one workspace. **Placement is
  deliberately open** — a per-pane subtitle is the obvious guess, but grouping the grid by
  repository, or a separate colour band, may read better at a glance than six more
  strings. Note that colour already means the lane (§8), so a repository colour must be a
  distinct visual channel, not a recolouring of the pane. Decide it against a real
  six-lane workspace, not in the abstract.
- **A quota indicator in the UI: percent of the rolling 5-hour window consumed.** §2.6
  makes the quota governor load-bearing rather than polish, and its precondition is that
  the number is *visible* — a fleet that dies at 4pm with no warning is the failure being
  prevented. **The sourcing question is settled, and better than hoped: the CLI pushes it
  down the wire unasked.** Observed in a live lane's stream-json:

  ```json
  {"type":"rate_limit_event","rate_limit_info":{
     "status":"allowed_warning","resetsAt":1787004000,
     "rateLimitType":"five_hour","utilization":0.97,
     "isUsingOverage":false,"surpassedThreshold":0.9}}
  ```

  So no estimation, no header scraping, no self-metering, and no "labelled as an
  estimate" caveat — it is the authoritative number, from the same session the work runs
  in. Better still, **the data is already in the store**: the shim forwards every wire
  line and `LaneRuntime` files anything it does not recognise as `kind='wire'` with the
  raw JSON, so historical utilisation is already sitting in `pane_events` in every project
  that has ever run a lane. Building the indicator is therefore: recognise the event type
  in `LaneRuntime`, keep the latest reading (utilisation, `resetsAt`, `status`,
  `isUsingOverage`) in `kv`, and render it. Two notes for whoever does it — the reading
  arrives only when a lane takes a turn, so it must be shown with its timestamp rather
  than implied to be live; and `status`/`surpassedThreshold` already encode the escalation
  the governor needs, so the UI should follow the CLI's own thresholds rather than
  inventing colour bands.
- **The dispatcher's own session — the one that decides.** This is the largest carried
  item and it is worth stating plainly, because a stopgap now stands in its place.
  Typing a sentence at an empty project used to answer with an error telling the operator
  to go run a command; today the daemon starts a lane by itself, names it **in code** from
  the longest substantial word, gives it no ticket and no claims, delivers the sentence
  and announces the whole thing with an undo. That is the right *shape* — act, announce,
  allow undo (§11) — with the judgement removed. What is missing is exactly the judgement:
  whether the sentence deserves a **ticket** with claims, what those claims should be,
  which existing lane it really belongs to, and a name a human would have chosen. Those
  are dispatcher decisions, and the dispatcher is a session that does not exist yet.
  The plumbing is not the hard part — the warm utility session (`router-start`) and
  `AskAsync` already do request/response against a live model, and ticket creation,
  claims and the gate are all built, so wiring a proposal into them is on the order of a
  hundred lines. The hard part is that a bad claim proposal produces a gated agent that
  cannot do its job, so it needs the repository's shape as context and an easy correction
  path. **Follow §4's own pattern when building it**: keep the instant code-derived lane
  as tier 0, and let the dispatcher's opinion arrive behind it as a visible, undoable
  correction — renaming a lane or promoting it to a ticket after the fact, rather than
  making the operator wait on a model before anything happens.
- **Older carried items** *(status 2026-08-18)*: the selective-compression pool — **DONE**
  (schema v7; warm pool, fixed schema, every failure leaves the full text standing);
  settings-merge for tracked `.claude/` — **DONE** by construction (the gate now deploys
  to `settings.local.json`, which Claude merges over the repo's own settings, so the
  tracked file is never touched and the repo's hooks keep running); the short unique
  label — **DONE** for panes (repo tag, multi-repo workspaces only); for windows it was
  the shortest-unique-suffix trick, later deleted with the folder picker (2026-08-18,
  WORKSPACES-CONCIERGE.md §6.1: windows title by workspace NAME now); the quota indicator —
  **DONE** (rate_limit_event off the wire into kv, aged honestly, amber past the CLI's own
  threshold); lane lifecycle §3–§5 — **DONE** (land retires the agent, lanes go dormant,
  `lane-respawn`/wake resumes the session, badge deferral, liveness clock).

  Still genuinely carried: **the dispatcher session** (the big one — needs the operator's
  input on prompt and policy, and real-model quota to develop against), and
  **`publish --all` against multiple live instances** — deliberately untested, because
  `--all` enumerates every ctl pipe on the machine and a test would therefore hot-swap
  the operator's live instances; it needs an instance-scoping story before it can have a
  suite.

### A testing lesson worth keeping

Every suite through M4 asserted on `dodona ui dump` and screenshots, and all of them
passed while the **first thing a person did with the UI hit a dead end**. Dumps prove the
UI reports correctly; they cannot prove it is usable, because they never exercise the path
a person takes. `tests/ui-use-acceptance.ps1` now drives the real window through UI
Automation — focus the box, set the text, press Enter — and asserts what the operator
actually gets, including that the status line never tells a GUI user to run a CLI command.
**Any new interactive affordance needs a check in that file, not only a dump assertion.**

### One honest cost note

The doc's premise "runs on the Claude Code subscription because it *is* the CLI" holds,
but the subscription window is the system's true scarce resource — more binding than any
latency number in the doc. The quota governor (§2.6) isn't polish; it's what keeps a
six-lane fleet from being dead by Tuesday. Design tickets small, default lanes to Sonnet,
and spend Opus/Fable where judgment compounds — the same §9 discipline, applied to the
platform's own budget.
