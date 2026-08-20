# Debugging Dodona from outside

Written for a Claude Code session told "go look at what happened." Everything below is
readable with **nothing running** — the store is SQLite in WAL mode and the daemon never
holds exclusive locks. You need no tool but a SQLite reader (or the `dodona` CLI if a
daemon is up).

## Where things live

Everything is scoped to a **workspace** — a named, durable session group, no longer a
project root (`docs/WORKSPACES-CONCIERGE.md` §1). The `<instance>` in every pipe name below
is the workspace's generated id slug, e.g. `personal-3f9a`, and **`--root <path>` still
works**: it resolves to whichever workspace owns that path.

**Start here, always:**

```
dodona where --root <path>          # or --workspace <name|id|alias>
dodona where --workspace work --json
dodona workspaces                   # every workspace, its members, * = daemon running
```

That prints the store, the workspace directory and the pipe names. It exists because a
store's location is derived from a generated id, so it is no longer something you can work
out by looking at a folder.

| Thing | Location |
|---|---|
| The store (all state, all history) | `%LOCALAPPDATA%\Dodona\workspaces\<id>\store.db` |
| Shim identity per lane | `%LOCALAPPDATA%\Dodona\workspaces\<id>\shim-lane<N>.json` → `{shimPid, childPid, pipeName}` |
| Why a wrapper exited | `%LOCALAPPDATA%\Dodona\workspaces\<id>\shim-exits.log` — one line per shim: `up`, then `exiting -- <reason>` and `gone`. Reasons are `##shutdown from a client`, `child <pid> exited and all N buffered line(s) were delivered`, `lease expired`, or `CRASHED: <type>`. **Read this first when a lane vanished.** It is a file and not stderr because a shim inherits its DAEMON's stderr handle, and every interesting exit happens after that daemon is gone — so the one reason worth having was the one that could never be captured. |
| The workspace registry (names, ids, aliases, members) | `%LOCALAPPDATA%\Dodona\concierge\registry.db` |
| Ticket worktrees — **still beside the repo** | `<member>\.dodona\wt\t<N>` |
| Control pipe (daemon alive only) | `dodona-<instance>-ctl`, instance = the workspace id slug |
| Lane pipes (shim alive) | `dodona-<instance>-lane<N>` |
| UI pipe (UI alive only) | `dodona-<instance>-ui` |
| Handoff pipe (during a swap only) | `dodona-<instance>-handoff` |
| Published builds | `%LOCALAPPDATA%\Dodona\bin\<stamp>` (or `$env:DODONA_BIN_ROOT`) |
| What a build was built FROM | `<build dir>\.built-from` — newest source timestamp at publish time; absent for `publish --exe`, which falls back to the exe's mtime |
| Agent session files (Claude Code's own) | `$env:CLAUDE_CONFIG_DIR\projects\<cwd-slug>\<session-id>.jsonl` |

`$env:DODONA_HOME` relocates every `%LOCALAPPDATA%\Dodona` row above (bar published
builds, which have `DODONA_BIN_ROOT`). Every acceptance suite sets it, so a suite never
touches the registry the operator is using.

**The registry is readable with nothing running**, same as a store:

```sql
SELECT id, name FROM workspaces;
SELECT workspace_id, path, is_git FROM members ORDER BY workspace_id, id;
SELECT ts, kind, workspace_id, detail FROM events ORDER BY id;   -- attach_refused lives here
```

`members` carries a partial unique index on `key WHERE is_git = 1`: **a git repo belongs to
at most one workspace.** That index is what replaced path-hash identity as the structural
guarantee against two merge tokens over one main. If you ever see one repo path under two
workspace ids, that is the incident — not a display bug.

## "Is anything running?"

```
dodona ps [--json]        # every daemon, window and lane on this machine, named
dodona stop-all [--lanes] # stop the daemons; --lanes takes the agents down too
```

**A daemon deliberately outlives its UI window.** That is the design, not a leak: the window
is the disposable half (§13) and agents survive behind their shims, which is the whole reason
a swap or a crash costs nothing. But it means **closing the app does NOT mean nothing is
running**, and until `dodona ps` existed there was no way to find out short of Task Manager —
which cannot tell a suite's processes from your own.

`ps` names anything unregistered separately (a pre-workspace instance, or one belonging to
another `DODONA_HOME`). Those are never `publish --all` targets, so seeing one here is the
only way to know it is there.

## Utility lanes are reaped; work lanes are not

A brain, router or compressor whose shim is gone is marked `dead` at the next daemon start
(`utility_lane_reaped`). They are fungible infrastructure with no thread — nothing resumes
one and nobody reads its transcript. An unreachable **work** lane is deliberately left alone:
that one is a problem to notice, and `lane-respawn` can bring its session back
(`docs/LANE-LIFECYCLE.md` §1).

This exists because of a real incident. `EnsureBrainAsync` spawns a brain when `_brainLo` is
unset, `_brainLo` resets to -1 in every new process, and reconcile re-adopted *compressor*
lanes but never the brain — so every daemon start spawned a fresh BRAIN lane while the
previous one sat connected and unreachable. Found on the operator's own instance: **14 BRAIN
lanes, one per start** across a morning of auto-publish swaps. No quota was burned (§2's
"turns cost quota, existing does not"), but it grew without bound, and the dead pipes made
reconcile — which runs BEFORE the control pipe server — take **35 seconds during which the
daemon answered nothing and refused `stop-daemon` because it had not started listening**.
Now: utility lanes get one connect attempt rather than three, and the reaper clears the rows.
Measured on a copy of that store: 15.9s on the healing start, 1.2s every start after.

## Model and effort (§9)

Every lane is its own `claude -p` process and inherits **nothing** from anyone's
interactive session — before this existed, no effort level was passed at all. Now:

- **Defaults** (in `dodona.json`, falling back to built-ins): `model` `opus`, `effort`
  `high`; the router runs `routerModel` `haiku` / `routerEffort` `low`. An empty string
  for an effort means "don't pass the flag".
- **A policy table** picks per prompt when the daemon starts a lane for typed input:
  first matching rule wins, else the default. The built-in table is three rules
  (mechanical→haiku/low, tests→sonnet/medium, design-tier→opus/max); a project can
  replace it with a `policy` array of `{when, model, effort, why}` regex rules.
- **The operator overrides anywhere**: `@opus @max <text>` at the front of any prompt.
  Tokens are stripped before the agent sees the sentence.
- `dodona policy` prints the table; `dodona policy <text>` prints what that sentence
  would run as, and why. Every choice is an event (`policy_choice`) and is announced in
  the pane it started.
- **Model and effort are fixed at process start.** An override aimed at a lane that is
  already running is answered with exactly that, not silently ignored. `lane-start`,
  `ticket-agent` and `router-start` accept `--model`/`--effort`.

## Workspaces and repositories

A **workspace** is a named, durable session group (`docs/WORKSPACES-CONCIERGE.md` §1). It
anchors identity — one store, one daemon, one grid, one dispatcher — and holds **members**:
folders attached to it, each either a repository itself or a folder with repositories under
it. `dodona repos` lists the repositories; `dodona workspaces` lists the members.

```
dodona workspace-create --name work --member C:\repos\engine --member C:\repos\tools
dodona workspace-attach --member C:\repos\thing [--bulk]      # --bulk expands a folder
dodona workspace-move --member C:\repos\thing --workspace personal
dodona workspace-rename <NAME> | workspace-alias <name> | workspace-forget
```

A repository is named by its workspace-relative path. **With one member that is exactly what
it always was** — `.` when the member is itself the repository (the ordinary single-repo
project, where nothing mentions repos at all), or `engine`/`tools` for repos underneath it.
Only a second member introduces a `<member-leaf>/` prefix, because only then is `engine`
ambiguous.

**A git repo belongs to at most one workspace at a time.** Attaching one that is already
owned is refused loudly and told to use `workspace-move`; bare folders are exempt (no merge
token exists to split). Enforced by a partial unique index, by the attach-time check, and
again at `ticket-create` — the last one catches a bare folder that became a repo after it
was attached to two workspaces. Look for `attach_refused` in the registry's events and
`ticket_repo_not_exclusive` in a workspace store.

- **A ticket belongs to exactly one repository.** Landing fast-forwards one branch onto
  one main, and two fast-forwards cannot be made atomic, so a change spanning
  repositories is two tickets. `ticket-create` refuses a cross-repo claim set and says so.
- **The repository is inferred from the claims**, which are workspace-relative paths and
  therefore already say (`subtree:engine/src` → `engine`). `--repo <name>` overrides;
  symbol-only claims in a multi-repo workspace must.
- **The merge token is per repository**, so `engine` and `tools` land in parallel while
  two tickets in `engine` still serialize exactly as before.
- **Claims stay workspace-relative everywhere.** The gate resolves an agent's write
  inside its worktree and prefixes the repo name back on before matching; the merge-time
  backstop does the same to git's repo-relative diff output. For `.` the prefix is empty,
  which is why single-repo behaviour is bit-for-bit unchanged.
- **`dodona.json` is per repository**, falling back to the workspace's — so one repo can
  be on `main` with one set of verify steps while another is on `master` with different
  ones. **A LANE'S CONFIG IS ITS PROJECT'S** since LOCATIONS-PLAN Phase 2: `permissionMode`
  and `allowedTools` come from `Config.For(<the lane's project>)`, not from the workspace's
  first project, so a repo deliberately kept on a leash keeps it. The `lane_config` event
  records which project configured each lane and what mode it got — that was previously
  unanswerable from outside the process, because the mode lands in a claude argv nothing reads
  back. Note `Config.For` picks a WHOLE FILE and does not merge two, so a project config that
  sets only `permissionMode` gets the built-ins for `agent` and `model`, not the workspace's.
- **A lane needs no repository at all**: an agent can work in a folder that has never seen
  git. Only tickets need one.
- **A lane opens in ONE project, and it is chosen, not inherited.** `dodona lane-start
  --project <path>` names it; the path must be a registered project or inside one (it resolves
  UP to the project) and anything else is refused rather than substituted. With no `--project`
  it is the workspace's first project, which is what every spawn site did before Phase 2 — so a
  one-project workspace is unchanged. `lanes.cwd` is the record, `dodona status` and `ui dump`
  report it (Phase 1), and the fake agent's `cwd` directive is how a check sees where the
  *process* is rather than where the row says it should be.
  **Typed input still goes to the first project** — choosing a project from a sentence is
  Phase 3's four rungs, and `Daemon.SpawnForAsync` is the one line that changes.
- **A detached project does not leave live lanes behind.** `workspace-detach` and
  `workspace-move` now tell a *running* daemon (`project-gone`, never summoning one — a
  registry edit must not start four haiku processes), which stops the agents in that project
  and marks the lanes `unreachable`. The rows and transcripts stay. `lane-respawn` then
  **refuses** to put a fresh agent back into a folder this workspace no longer owns, and names
  `lane-respawn <lane> --project <project>` as the way to re-home it. A ticket lane cannot be
  re-homed: its folder is its worktree and its gate is deployed there.

## The concierge (WORKSPACES-CONCIERGE.md §2)

One per machine, and it answers exactly one question: **which workspace**. It holds no
lanes, no claims and no merge tokens, and **no workspace daemon ever reads its store** —
that cap is what keeps it from becoming the serialization point §12 designed out.

| Thing | Location |
|---|---|
| Its store | `%LOCALAPPDATA%\Dodona\concierge\store.db` |
| Its config (models, effort, `searchRoots`) | `%LOCALAPPDATA%\Dodona\concierge\concierge.json` |
| Its ctl pipe | `dodona-concierge-ctl` |
| Its two model tiers | `dodona-concierge-tier1` (cheap), `dodona-concierge-tier2` (expensive) |
| Tier shim pids | `%LOCALAPPDATA%\Dodona\concierge\shim-tier<N>.json` |

```
dodona concierge                      # run it (start-on-demand does this for you)
dodona concierge-status               # tiers, the fence, every workspace, open questions
dodona concierge-resolve <text>       # walk the ladder and print the verdict as JSON
dodona concierge-feed                 # the merged-feed spine: the system's voice at group scope
dodona concierge-questions            # rung-4 questions still waiting
dodona concierge-answer <id> <name|new:NAME>    # answer one, and TEACH an alias
dodona concierge-review <text> --workspace-id <id>   # the review-behind net, by hand
dodona concierge-stop
```

**The ladder, cheapest first.** Rungs 0, 1 and 1b are code and cost nothing; the steady
state never leaves them:

| rung | what | reached when |
|---|---|---|
| `path` | an explicit path in the prompt | the operator said where — never searches |
| `registry` | exact workspace name or alias | the ordinary case |
| `only` | one workspace exists | nothing to disambiguate |
| `fuzzy` | cheap tier matched confidently | a mangled or loose name |
| `discovery` | expensive tier found a folder in the fence | an unknown name with signal |
| `ask` | nobody was sure | a question row + a feed line; the answer becomes an alias |

**The fence** is the parent directory of every registered member, plus configured
`searchRoots`, minus drive roots. It is the single narrow exception to "management brains
never run tools" — one capability, enumerating candidates inside it. **It never widens
itself**: a rung-3 miss falls to rung 4 rather than looking further.

Reading it with nothing running:

```sql
SELECT ts, rung, confidence, workspace_id, created, latency_ms, input FROM resolutions ORDER BY id;
SELECT id, state, input, candidates, answer FROM questions ORDER BY id;   -- rung 4
-- ...and in a WORKSPACE store, `kind` says what answering DOES: `repo-init`, `route`, or
-- `land` (R6 — subject is the ticket id, and answering `yes` is the operator's approval; it
-- is the only path to `TicketApprove` besides `dodona approve`).
SELECT id, ts, acked, body FROM feed ORDER BY id;                          -- the merged spine
SELECT ts, kind, detail FROM events ORDER BY id;
```

Event kinds worth knowing: `resolved` (every verdict, with its rung), `group_clarification`
(rung 4 asked), `question_answered` (with the alias it taught), `discovery_miss` (the fence
had candidates and none matched), `review_behind` / `group_misroute` (§2.3 caught a
wrong-workspace delivery), `tier_timeout` / `tier_unparseable` (a rung had no opinion, and
the ladder moved on rather than stalling).

**A group-misroute is never retracted.** You cannot unsay a sentence to an agent, so the
review-behind reports where it went, where it belonged, and the command to resend — and says
plainly that it was already delivered. If you see one of these in the feed, the work is in
the wrong workspace and only you can move it.

## Selective compression (§5)

A pane shows the **short readable** form of what happened; the store keeps everything.

- **Mid-turn `agent_line` narration never reaches the grid.** Not filtered by a model —
  by construction: an agent ends its turn when it needs you, so anything that needs you
  IS a `result`. That half stands.
- **The other half of that argument was wrong, and cost the operator four minutes of
  blank screen (2026-08-19).** It used to end "and what it is doing meanwhile is already
  the presence line". Presence is ONE COLUMN that every event overwrites, so it holds
  the newest tool and nothing else: a real turn ran 111 wire lines and 18 tool calls
  with two sentences on screen, 93 of those lines being `thinking_tokens`, and a
  tool-only assistant event wrote no row at all — so sixteen of the eighteen steps left
  no trace anywhere. The operator's words were "staring at a blank screen hoping
  something's happening".
- **So there is a third kind now: `progress`, decided in code, three tiers**
  (`src/Dodona/PaneProgress.cs`, which carries the measurement):
  - **noise → no row.** `BashOutput`/`KillShell` polling its own shell;
    `thinking_tokens`, which moves presence to `thinking…` and writes nothing.
  - **step → one line, FOLDED at render.** Reads, greps, globs, ls, web fetches, todo
    updates. A run of the same verb becomes `read 6 files: a.cs, b.cs, c.cs +3`, with
    identical subjects collapsing, so re-reading one file cannot inflate a count.
  - **act → its own line, always.** An edit, a write, a command (`$ dotnet build …` —
    the command, not the agent's description of it), a subagent, and **a failed
    `tool_result`** (`! bash failed: Exit code 2 …`), which is new territory: a
    tool_result arrives as a `user` event and used to land in an unread raw row, so a
    build that would not compile was invisible until the turn ended.
  - **an UNKNOWN tool is an `act`**, deliberately. Claude Code ships new tools, and a
    classifier defaulting to silence would restore this exact blind spot with no error
    anywhere — the dead-routing-ladder failure (CLAUDE.md §3) in one file's clothing.
  Folding happens at READ, never at write: the store keeps one row per tool call, so
  `UNIQUE(lane_id, seq)` still makes shim redelivery exactly-once, the overlay still
  shows every step, and the fold stays a pure function with no migration behind it.
  It costs **zero model calls** — `compression` asserts that no `progress` row ever
  acquires a `compressed` value, because that is the whole argument for showing them.
  Between them these still buy the 5–10× volume cut §2.2 asks for.
- **Turn-finals are always kept and always shortened.** `result` rows go to a warm
  compressor, which must answer in a fixed schema — `{headline ≤90 chars, needs_you,
  options[]}` — so it cannot ramble. `needs_you` renders `BLOCKED — <headline>` with an
  `options:` line under it.
- **Already-short results (≤120 chars, single line) skip the compressor entirely.** There
  is no judgement to buy there, and §2.2's whole point is not to spend calls where there
  is none.
- **`compressed` is a second column, never an overwrite.** `body` stays the agent's own
  words, `raw` stays the wire line. The pane reads `COALESCE(compressed, body)`; the
  overlay reads `body` and filters no kinds at all — raw one keystroke away, literally.
- **The pool is 2–3 sessions, round-robin, one lock each.** One session accumulating six
  lanes' turn-finals would be the serialization point §3 forbids the dispatcher to be.

```
dodona compressor-start [--count 2] [--model haiku] [--effort low] [--child <exe>]
```

`dodona.json` keys: `compressorModel`, `compressorEffort`, `compressors`.

**Every failure path leaves the operator reading the agent's own words**, because the row
is written and on screen before compression is even attempted. No pool warm, a timeout, a
non-JSON reply, an empty headline — all of them simply leave `compressed` NULL. Look for
`compressed` (with latency and before→after sizes), `compressor_timeout` and
`compressor_failed` in `events`. Compression never blocks the wire pump and never delays
a pane.

Deliberately **not** compressed: announcements. The design lists them, but every
announcement Dodona writes today is a code-authored one-liner that is already in a fixed
shape — spending a model call on it would buy exactly the no-judgement volume §2.2 says
to refuse, and would put `undo: dodona lane-stop 3` at the mercy of a paraphrase.

Also deliberately unchanged: **attention**. `needs_you` is rendered as text only. It does
not badge, toast, or set presence — blocked-on-you is code-derived from ticket state
today, and handing a small model the badge is a policy decision `docs/LANE-LIFECYCLE.md`
§4 has not taken.

## The schema (v10 — `PRAGMA user_version`)

*(This heading said v7 while the store was on v8. It is `Ver.Schema` in
`src/Dodona/Ver.cs` and the highest `PRAGMA user_version` in `Store.Migrate`; those two
are the authority and they must agree.)*

- **`lanes`** — `id, title, state (alive|unreachable|dead), pipe_name, session_id,
  presence, role, cwd, project, created_ts`. The session_id is the resume handle; the pipe
  is the reattach handle.
  **Two location columns, and the distinction is load-bearing (v10, P5.1):** `cwd` (v8) is
  where the lane's process RUNS; `project` is which project it is FOR. For a work lane they
  are the same folder, and `project` is a cache of what `Projects.Of(cwd)` derives. For a
  MANAGEMENT lane they are deliberately different: a brain runs in `Paths.NeutralDir` on
  purpose (a manager inside a project loads that project's `CLAUDE.md` and skills, i.e. a
  judgement agent that can end up running `/ship`), so its cwd can never say which project
  it serves.
  **`project` is a REGISTRATION, and it is what decides whether a brain lives.** A brain is
  valid iff a row says it should exist for (role, project) — operator, 2026-08-19: *"If it's
  not tracked, it's not valid. Why must you do some weird kill to count?"* Reconcile reaps a
  management lane whose `project` is no longer a project of the workspace, and a second
  claimant on one (role, project) slot; it reaps nothing for being the Nth brain.
  Compressors are exempt from the duplicate rule — a pool is *meant* to have several per
  project.
  Empty means "not resolved yet": every pre-v10 row is, and `Store.StampLaneProjects` fills
  them in at daemon start (the store knows nothing about workspace membership, so the daemon
  resolves and the store writes — the same split as v9's `#unresolved:` repo paths). A work
  lane is stamped from its cwd; a MANAGEMENT lane is stamped to the workspace's first project
  and that is **announced as an assumption** (`lane_project_assumed`), because before v10
  there was one brain per workspace and no row records which project it was for. Anything
  unresolvable is reported (`lane_project_unresolved`), never guessed.
  **`RegistrationKey` never answers "" for a manager**, and that matters more than it looks:
  an empty registration reads as *unregistered*, which the reaper acts on — so a stamp that
  failed quietly (a locked store, a store copied out of a suite) would kill a healthy brain.
  And the reaper only ever runs on a membership list the registry actually gave us
  (`TrustedProjects`): `Members()` degrades to the first project alone when the registry
  cannot be opened, and fed to a reaper that reads as *every project but the first is gone*.
- **`pane_events`** — everything a pane would show, in order: `lane_id, ts, kind, body,
  seq, raw, acked, compressed`. `compressed` is the short readable rendering (§5), NULL
  when the row was not eligible or the compressor never answered; `body` is never
  rewritten. `kind ∈ user_input | agent_line | progress | result | system | wire |
  announcement | error`. `progress` is one mid-turn step, code-derived (§5); it is the
  one kind the pane FOLDS rather than renders row-for-row. `seq` is the shim's delivery sequence (NULL for locally-generated
  rows); `UNIQUE(lane_id, seq)` is what makes shim redelivery exactly-once. `raw` is the
  untouched wire line — the raw truth when `body`'s extraction looks wrong. `acked`
  applies to announcements only (the decision feed persists until acked — acked rows
  grey out, never disappear).
- **`tickets`** — `id, lane_id, title, branch (ticket/<id>), worktree, state
  (open|landed|abandoned), merge_mode (on-approval|auto), approved, repo, repo_path,
  created_ts, landed_ts`. A branch is a thing that lands; that is what a ticket is.
  **Two repository columns, and the distinction is load-bearing (v9, P0.1):** `repo` is
  the workspace-relative DISPLAY name (`.` for the workspace root itself), frozen at
  creation because the ticket's `claims` rows were written relative to it — an open
  ticket's claim namespace must not move underneath it. `repo_path` is the IDENTITY: the
  canonical path (`Repos.Key`, i.e. `Instance.Canonical`), which is what every operation
  on a ticket's repository resolves through (`Daemon.RepoOf`). Empty only on a pre-v9 row
  whose name resolved to nothing at migration time — those are announced, never dropped.
  The reason: `repo` is recomputed by `Repos.Discover` on every call, by a rule that
  changes with project count, so the same repository is `.` in a one-project workspace and
  `proj` once a second project is attached.
- **`claims`** — `ticket_id, kind (path|newfile|subtree|symbol), value`. Deleted in the
  same transaction that marks the ticket landed — a lingering claim row for a landed
  ticket is a bug. Values are normalized: forward slashes, lowercase, no leading slash.
- **`merge_token`** — one row **per repository**, keyed by **`repo_path` COLLATE NOCASE**
  (v9): `repo` (display only, refreshed on read), `holder_ticket, generation, granted_ts,
  expires_ts, main_sha`. Rows appear on first use. Expired holders are reclaimed at the
  next request; `generation` increments per grant. `main_sha` is what that repository's
  main was when the grant happened.
  **It was keyed by `repo` until v9, and that was a live double-fast-forward bug**: the
  display name changes with project count, so one repository could hold a `.` row and a
  `proj` row at once and two tickets could each be told `granted` over the same `main`.
  Binary collation was a second route to the same thing (`Engine` vs `engine` after a
  folder rename), which is why every key column is `COLLATE NOCASE`.
  A key of the form `#unresolved:<name>` is a v6-era row whose name no longer resolves to
  any repository in the workspace. It is deliberately unmergeable and cannot be granted;
  `repo_path_unresolved` in `events` names it.
- **`token_queue`** — FIFO of tickets waiting, scoped by `repo_path` (same identity as the
  token, v9; `repo` is retained for display): the head of `engine`'s queue is independent
  of `tools`'.
- **`routing_decisions`** — every routed input (§4/§5): `ts, input, tier, target_lane,
  delivered_lane, confidence, retargeted, undone`. `tier` is the VERDICT that decided it:
  `prefix` | `generic` | `addendum` | `new-task` | `first` | `focus` | `ask` | `answered`. A row with
  `tier='ask'` and a NULL `delivered_lane` is the one to look for — the sentence was HELD and
  the operator asked, because guessing between "new work" and "continues something" is the one
  routing mistake that cannot be undone. **`tier='answered'` with `confidence='operator'` is the
  SAME sentence arriving, later, at the lane an answer created (P3.A): a project hold writes two
  rows for one sentence and both are true — it was asked about, and it was then delivered.**
  `undone` is reserved for the UI's undo keystroke —
  free labeled data for tuning the confidence threshold.
- **`kv`** — small state: `focused_lane`, `dispatcher_lane` (`autopublish_last_tried` was
  DELETED in Phase 2b — the drift watcher compares two SHAs now and needs no remembered
  state. An old store may still carry the row; nothing reads it.)
- **`lanes`** additionally carries `presence` (derived by code from tool_use wire
  events — never a model) and `role ∈ work | router | compressor | dispatcher`. Only
  `work` lanes take a grid slot, receive routed input, or have their turn-finals
  compressed — a compressor asked to summarise its own summary would never stop. The `dispatcher`
  lane (title `DODONA`) holds no agent and takes no grid slot — it is where the system
  speaks in its own voice, and reconcile skips it.
- **`swaps`** — every proposed hot swap (§13/§14): `exe, build, schema_version,
  shim_protocol, blocker, mode (ask|now|when-it-lands|hold), state (pending|armed|held|
  swapped|failed|superseded)`. A `blocker` is why a swap could not be seamless; `armed`
  means the daemon will swap itself the instant that blocker clears. At most one row is
  live at a time — a newer proposal supersedes a parked one.
- **`events`** — the causal chain: `ts, kind, lane_id, detail`. Every daemon action
  writes here. Lane kinds: `daemon_start`, `reconcile_done`, `shim_spawned`,
  `lane_connected`, `lane_unreachable`, `lane_pipe_lost`, `say`, `daemon_stop`.
  `reconcile_done`'s detail is `connected=<n> brains=[<leaf>=<lane>,…] brains-hi=[…]
  compressors=<n>`. **The key is `brains=`, plural, and the rename is deliberate (v10, P5.6):**
  it was `brain=<lane>`, and the check that read it regexed `brain=\d+` — which a per-project
  `brain=3,7` would have kept matching while asserting nothing about either brain. Renaming
  the key makes the old pattern impossible to match, so a check that would have degraded into
  a green proving nothing goes red and has to be rewritten. `dodona status` answers the same
  question per lane, as `scope=<project>` — printed only for a management lane in a workspace
  with more than one project, because with one project there is exactly one answer and a
  one-project workspace must not start printing a new field (`Projects.ScopeField`, on the
  `unit` loop).
  Project kinds (LOCATIONS-PLAN Phase 2): `lane_config` (which project's dodona.json
  configured this lane, and the permission mode it got — `source=argv` when read back out of
  the real claude argv, `source=config` when the child takes no claude flags),
  `lane_project_detached` (its project left the workspace, so the agent was stopped),
  `lane_respawn_refused` (a respawn into a folder no project owns — trap T4),
  `shim_spawn_refused` (**the system prompt named a different folder than the process would
  start in** — trap T1, enforced in `AttachShimAsync`; this can only fire on a code defect,
  never on configuration, so seeing it means a spawn site diverged from its prompt again).
  Ticket/merge kinds: `ticket_created`, `claim_conflict`, `claim_extended`,
  `ticket_approved`, `token_granted`, `token_queued`, `token_refused_unapproved`,
  `token_released`, `token_expired_reclaimed`, `claim_backstop_refused`, `landed`,
  `land_refused`, `land_inconsistent`, `verify_green`, `verify_red`, `worktree_pruned`,
  the R1/R2 land-flow kinds (`land_merged_main`, `land_no_worktree`, `land_conflict`,
  `land_merge_abort_failed`, `land_not_ff_under_token`, `land_drop_check`,
  `land_drop_check_moot` — **the drop check ran and had nothing to examine, which is a
  different thing from a check that failed to run** — and `land_silent_drop`),
  the R3.5 asynchronous-land kinds (`land_started` — the pipe accepted it and let go;
  `land_finished` / `land_refused_async` — the outcome, written where a caller's reply used to
  be; `land_threw` — an exception that would once have become `error:` on that reply and would
  now vanish silently; `land_interrupted` — a daemon stopped mid-land, which was impossible
  while the land held the pipe, and which loses nothing because the trunk moves only in the
  last step),
  the R4 completion-record kinds (`completion_record` — **the whole record, as JSON, in the
  detail after `ticket <id> `**: branch, worktree, `git diff --stat <main>...<branch>`, the
  drop check, the verify result AS RECORDED and never run here (D-R15), and the agent's own
  end-of-turn report. `dodona ticket-record <ticket>` prints the newest one and starts nothing.
  `completion_record_unchanged` — D-R13's gate fired, so the worktree had not moved since the
  last record and a chatty lane produced one record instead of one per turn; a record kind with
  no skip kind anywhere beside it means the gate never fired. `completion_record_impossible` —
  no worktree, no repository, or git could not read the tree, so there is deliberately no
  record: the `land_drop_check_moot` distinction, one phase along.
  `completion_record_failed` — it threw, which is the one shape that would otherwise be a
  turn that quietly produced nothing),
  the R5 manager-review kinds, all carrying `ticket <id> ` first so `LastTicketEvent` and
  `CountTicketEvents` can find them (`manager_review` — **the review itself, as JSON, written for
  EVERY verdict including `ok`**, because D-R11 says the write-up is the point and the verdict is
  not: `verdict` (`ok|send-back`), `asked` (what the model actually said, so a reply of `approve`
  is visible as the no-op it is), `confidence`, `tier` (`lo|hi` — the escalation is a field of the
  one row, not an event of its own), `round`, `bound`, `note` for the operator and `message` for
  the agent. `manager_sent_back` — a send-back that REACHED THE WIRE, and the only kind the bound
  counts: three of them and the ticket goes to the operator (D-R12/D-R16). `manager_bound_reached`
  — that moment, written once and carrying the history; there is deliberately no fourth model
  call. `manager_review_skipped` — it could not run and says which: `DODONA_NO_AUTOSTART=1` (the
  review is the daemon starting a judgement agent on its own initiative, D-R17), or no brain for
  that project. `manager_review_failed` — the tier did not answer in 25 s, the reply would not
  parse, it threw, or the verdict was `send-back` with nothing to say.
  `manager_send_back_undelivered` — the lane never came back, so the whole message is in the
  event AND announced with the two commands that deliver it by hand; it counts as no round,
  because nothing was said),
  the R8 kinds (`manager_sent_back_mechanical` — a DELIVERED send-back that spends NO round,
  because R4's record said `verify: red` at the moment of the review (D-R24). Its own kind rather
  than a flag, so the bound's counter and R6's ask both stay right without learning anything new
  (D-R25); it carries `verifyWhen`, the verify event's timestamp, which is what limits the
  exemption to ONE per red result (D-R26) — the record keeps saying `red` until another land
  runs, so the word alone would be an unbounded loop. `not-run` is NOT red and earns nothing.
  `manager_details_granted` — the review asked to read specific files (D-R23) and this is what it
  got: `tier`, `granted`, `refused` with the reason for each, and `why` in the reviewer's own
  words. Written even when everything was refused, because "does this hatch get used for
  judgement or out of habit" has to be answerable by counting rows. The `manager_review` row
  carries the same five facts as fields: `verify`, `exempt`, `details`, `detailsWhy`,
  `detailsRefused` — and its `round` is the count AFTER the send-back, so an exempt one leaves it
  unchanged),
  the R6 approval-ask kinds (`question_opened` with `kind=land subject=<ticket>` — the ask that
  puts the manager's write-up in front of the operator, raised by the RECORD existing and never
  by the review having answered, so `no review has run` is a normal thing for it to say
  (D-R19); `land_ask_failed` — raising or refreshing it threw, which is worth an event because a
  ticket with no ask is one the operator can only approve from the command line),
  `worktree_prune_failed`, `ticket_git_failed`, `claim_extend_refused` (a `claim-extend`
  whose claims were outside the ticket's own repository), `token_refused_no_repo` (the
  ticket's repository has left the workspace — the token is refused rather than aimed at
  the first project). Repo-identity kinds (v9): `store_backed_up` /
  `store_backup_failed` (a migration copied the store first, and where to), plus
  `repo_path_stamped`, `merge_token_merged` (**two token rows over one `main` were folded
  into one — that is the incident, on disk**) and `repo_path_unresolved`. Routing kinds: `classified` (with
  latency, kind and reason), `classified_escalated` (the expensive tier was asked),
  `routed_addendum` (with its `direct`/`tweak` reason), `routed_new_task`,
  `routing_clarification` (held and asked — **nothing was delivered**),
  `routed_retarget`, `classifier_timeout`, `classifier_failed`, `route_undone`,
  `router_started` / `router_failed` (the classifier the daemon warms for itself), and
  **`routing_unrouted`** — the fallback saying out loud that it has no classifier and is
  sending everything to the focused lane. If you see that row, routing is OFF: the ladder
  is not choosing lanes, and every `routing_decisions` row will read
  `tier=focus confidence=no-classifier`. That was the live state for two days
  (CLAUDE.md §3) because the classifier was looked up by a role nothing ever created.
  **Project-routing kinds (LOCATIONS-PLAN Phase 3) — which project a typed sentence opened a
  lane in:** `project_chosen` (`rung=named|live|answered how=leaf|alias|spoken|sole-live|classified|operator`,
  plus the project path — **never written for a one-project workspace**, where there is no
  decision to record and the whole phase must be invisible), `classified_project` (the cheap
  tier was asked, because several projects held live lanes — this is the one row that costs
  quota, so its absence is how the free rungs are checked), `project_unclassified` (there was
  no classifier, or it would not choose: the sentence is HELD rather than aimed at the first
  project), `project_unknown` (**held and asked — nothing was delivered, and no lane row
  exists**; the detail carries every project it could have meant), and
  `project_gone_at_spawn` (a rung's answer stopped being a project between the ladder's read
  and the spawn — trap T4 arriving on the typed-input path). A `routing_decisions` row with
  `tier=ask confidence=no-project` is the project hold seen from the other side.
  **The hold also opens a `questions` row with `kind='route'` (P3.A), and `question_opened` /
  `question_answered` carry its id.** Answering delivers `questions.subject` — the held sentence,
  kept whole for exactly that — so a store where `project_unknown` exists and no `route` question
  does is the two-day gap this closed: rung 4 asked and nothing could render the question.
  `question_answer_refused` means the answer named a project the workspace no longer has, and the
  row was deliberately LEFT OPEN so the sentence is not lost. Compression kinds: `compressed` (with latency and before→after sizes),
  `compressor_timeout`, `compressor_failed`. Lifecycle kinds: `lane_stopped`,
  `lane_dormant` (its ticket landed — the agent was retired, the lane keeps the thread),
  `lane_respawned` (a fresh agent resumed the recorded session).
  Auto-publish kinds: `autopublish_watching` (detail names the branch tracked, the commit
  running, and the baseline), `autopublish_started`, `autopublish_failed` (the live app is now
  BEHIND main — fix the build), `autopublish_surrendered` (three consecutive failures: it has
  STOPPED trying, and says so once), `autopublish_misconfigured`, `autopublish_error`, and
  **`autopublish_no_provenance`** — new in Phase 2b: this build cannot say what commit it came
  from (a `dev build` image, or `publish --exe <prebuilt>`), so it is NOT watching rather than
  guessing. That row plus an announcement is the whole symptom; publishing once from a git
  checkout arms it.

  `autopublish_started`'s detail reads `main at <sha>, this build baselined <sha>` — two
  commits, not two timestamps. **`autopublish_dirty_tree` is GONE**, with the 30-minute nag
  that raised it: uncommitted work can no longer reach the app, so there is nothing to warn
  about. If you are reading an OLD store, `autopublish_started` rows detailed
  `sources <t> > built-from <t>`, and identical timestamps in consecutive rows are the
  64-iteration loop of 2026-08-18 — the failure the SHA comparison replaced.
  Lifecycle: `utility_lane_reaped` (a brain/router/compressor whose shim is gone — written only
  after the OS has been asked, both ways; see `LaneLiveness`).
  `utility_lane_stubborn` (its pipe or shim process is STILL LIVE, so it was NOT reaped: marking
  the row dead would drop the last reference capable of stopping that process, which is exactly
  how 14 orphaned BRAIN lanes were manufactured, one per daemon start).
  `utility_predecessor_live` (a spawn was REFUSED because the predecessor is still running;
  routing degrades loudly for that one call and retries on the next — never a second orphan).
  The refusal is scoped to **(role, project)** and the poke is bounded to three attempts per
  lane per daemon (v10, P5.4): it used to be role-only over a `HashSet` that was never
  cleared, so one wedged brain refused to let a brain be created for *every* project, forever,
  and a shim that would have accepted a second `##shutdown` was never asked again.
  `brain_unregistered` (a management lane was stopped because its registration is not valid —
  its project left the workspace, another lane already holds that (role, project) slot, or the
  workspace was forgotten). **`brain_surplus_retired` is GONE**, with the count-and-kill loop
  that wrote it: it kept ONE lane id per role and shut every other alive lane of that role
  down as "a duplicate left by a fixed leak", which with a brain per project killed N-1
  healthy sessions on every daemon start — including every auto-publish swap — and announced
  it as a repair. An old store may still hold those rows; they are history, not behaviour.
  `brain_cap_reached` (`maxBrains` in `dodona.json`, default 6: a new project gets no judgement
  agent rather than an existing one being evicted — a cap that made room by shutting a brain
  down would be the count-and-kill loop growing back somewhere else).
  `lane_projects_stamped` / `lane_project_assumed` / `lane_project_unresolved` (the v10
  migration's own report, per the `lanes.project` notes above).
  `reap_skipped_untrusted_projects` (the registry did not answer, so nothing was reaped for
  being unregistered this start — see `TrustedProjects`).
  Swap kinds: `swap_blocked`, `swap_armed`, `swap_held`, `swap_spawned`,
  `swap_forced`, `swap_refused`, `swap_failed`, `daemon_handoff`, `binary_gc`,
  `binary_gc_skipped`. **If a state change happened with no event row naming why, that
  is a bug — report it as one.**

## The claim gate

Each ticket worktree carries a generated `dodona-gate.ps1` + `.claude/settings.json`
(PreToolUse on Edit|Write|MultiEdit|NotebookEdit). The gate asks the daemon
(`dodona claim-check`) and **fails open** with a line in `<worktree>\.dodona-bypass.log`
when the daemon is unreachable — the merge-time backstop catches what slips. A non-empty
bypass log is worth reading. Gate files are registered in `<root>\.git\info\exclude`
so agents can never commit them.

## Worked queries

What happened in this store, chronologically:
```sql
SELECT ts, kind, lane_id, detail FROM events ORDER BY id;
```

What lane 1's pane would show:
```sql
SELECT ts, kind, body FROM pane_events WHERE lane_id = 1 ORDER BY id;
```

Did anything get lost across a daemon death? (gaps in seq per lane):
```sql
SELECT lane_id, seq FROM pane_events WHERE seq IS NOT NULL ORDER BY lane_id, seq;
-- seqs must be contiguous per lane per shim lifetime
```

Was the daemon restarted, and did reconcile find everyone?
```sql
SELECT ts, kind, detail FROM events WHERE kind IN ('daemon_start','reconcile_done','lane_unreachable') ORDER BY id;
```

## Reading it live

```
dodona status --root <root>            # or --workspace <name|id>
dodona tail <lane> [n] --root <root>
dodona where --json                   # which store am I even reading
```

If the CLI reports the daemon isn't running, read the store directly — that is the
point of the design. A lane whose shim still runs (check the pids in
`%LOCALAPPDATA%\Dodona\workspaces\<id>\shim-lane<N>.json` — `dodona where` prints that
directory) is buffering; whatever it holds arrives when the next daemon connects, deduped
by seq.

## Hot swap (§13/§14)

`dodona version [--json]` tells you what a binary is: `build` (identity),
`schema` (the store shape it expects) and `shimProtocol` (the wire live shims speak).
The last two decide whether a swap can be seamless.

```
dodona publish [--project <dir>] [--all]   # build into a fresh versioned dir, then swap
dodona swap <dodona.exe> [--mode now]      # swap to an existing build
dodona swap-answer now | when-it-lands | hold
dodona swaps                               # every proposal + what is running now
```

The handoff: old daemon spawns the new binary with `--successor`, which connects to the
handoff pipe and says `ready`; the old daemon records `daemon_handoff`, replies `go
<pid>`, and exits; the successor waits for that pid to die, takes the mutex, opens the
store, reconciles, and adopts the shim pipes. **An agent mid-turn never notices** — it is
talking to Anthropic, and its shim buffered whatever arrived.

Diagnosing a swap that misbehaved, in order:

```sql
SELECT * FROM swaps ORDER BY id;                     -- what was proposed, and its verdict
SELECT ts, kind, detail FROM events
 WHERE kind LIKE 'swap%' OR kind IN ('daemon_handoff','daemon_start','daemon_stop')
 ORDER BY id;                                        -- the handoff, step by step
```

- `swap_refused` — the candidate was not a usable binary, or was a schema downgrade.
  Nothing happened; the running daemon is untouched.
- `swap_blocked` then no `daemon_handoff` — it is waiting on an answer. `swaps` shows
  `pending` (asking), `armed` (will fire when the blocker clears) or `held`.
- `swap_failed` — the successor never signalled ready. **The old daemon stays up**; this
  is not an outage. Read the note column for why.
- `daemon_handoff` with no following `daemon_start` — the successor died between `go`
  and startup. No daemon is running, but nothing is lost: the shims are buffering, and
  the next client command starts a daemon (start-on-demand) which drains them.

Start-on-demand means *any* client command revives a dead daemon. Set
`DODONA_NO_AUTOSTART=1` when you want the honest "daemon not running" instead — the
acceptance tests all do, so they own daemon lifetime.

## The shell: one window over N workspaces (WORKSPACES-CONCIERGE.md §6)

```
DodonaUi.exe --shell [--test-window]     # over every AWAKE workspace; boots to zero
DodonaUi.exe --root <path>               # over one workspace, as before
dodona ui dump --shell                   # add --shell to any ui verb to address that window
dodona ui workspace <name|id> --shell    # give a band the grid (the path a click takes)
dodona ui pose bands|merged-feed|boot-zero --shell
```

The focused workspace holds the full 3×2 grid; every other awake workspace is a **band** —
one row of lane chips with attention badges. Clicking a band swaps which workspace holds the
grid, and that is *all* it does: a band is a view, never an eviction. The six-slot cap,
`focused_lane` and the dispatcher lane stay per-workspace concepts inside each store.

`ui dump` grew a workspace dimension — `workspace`, `workspaceName`, `bootToZero`, `bands`,
and a `workspace` plus `concierge` key on every feed row. Everything that was there before
kept its shape: what the UI testifies to must not change because it gained a dimension.

**Boot-to-zero** (`bootToZero: true`) is a window with no workspace awake — a real state,
not an error. The grid is an invitation, the feed still shows the concierge, and the input
box still works: it routes through the concierge, which wakes or creates a workspace.

**The merged feed is a union** across workspaces plus the concierge, newest first. Ack goes
back to the store the row came from — ids are only unique within one store, so a single ack
path would clear an unrelated row that happened to share a number.

## The UI can testify (§17)

`DodonaUi.exe --root <root> [--pose <name>]` is a dumb view over the store: read-only
WAL reads, all writes via the daemon's control pipe. It answers on its own pipe
(`dodona-<instance>-ui`) — the daemon does not need to be running:

```
dodona ui dump --root <root>                     # panes/badges/presence/feed/toasts as JSON
dodona ui screenshot [--pane WATER] --out <png>  # self-rendered, always 1600x900 full-window
dodona ui pose <full|badges|blocked|feed|empty-slot|tray|overlay|long>  # deterministic fixtures
dodona ui pose live                              # resume store polling
dodona ui overlay <PANE|off> | dodona ui close
dodona ui compose "<text>"                       # type into the dispatcher box, NO send
dodona ui key <enter|shift+enter>                # Enter sends; Shift+Enter is a new line
dodona ui input-resize <dy|reset>                # the resize grip, without a mouse
```

The dispatcher box is **multiline**: it **opens at three lines** (a measured `MinHeight` —
`MinLines` is ignored once `TextWrapping` is on, which `ui dump` caught as `fit=28`),
auto-grows to 200px as lines arrive, the grip above it drags further (capped at 60% of the
window — the feed absorbs it, so the window itself never moves), and double-clicking the grip
refits it. **The dragged size is remembered** in `<DODONA_HOME>\ui.json`
(`{"inputHeight": 173.5}`) — a file rather than a store row because the shell spans
workspaces and boots to zero with no store at all; it therefore survives a restart AND a
publish hot-swap, and a double-click forgets it. Delete the file to reset by hand.

`ui dump` reports all of it under `input`: `text`, `lines` (LOGICAL lines, not wrapped rows),
`height`, `fit` (the default), `sized` (the operator overruled the auto-fit), `remembered`
(what is on disk), `hint` (the placeholder is showing). A newline reaches the agent intact
because `Say` serializes the whole message to ONE json line — the shim's stdin protocol is
line-delimited and would otherwise have cut the prompt in half.

Most "does the UI show X" questions are text questions — ask `ui dump` and read JSON.
Screenshots are for layout and visual judgment only. `pose long` is the one to reach for
when judging legibility: it is the only fixture with more transcript than a pane has
pixels, and lines longer than a pane is wide.

A pane row's `kind` is rendered as colour — `you>` blue, `agent>` violet, `✓` green, `·`
amber, `wire`/`system` dim mono. A `progress` step has no speaker prefix at all: two
spaces, dim, mono, subordinate to the turn it sits under — a step is not a voice. In the decision feed every row is an announcement, so
colour there means **lane**: the title is tinted with the lane's slot colour (same one as
its pane chip). Rows from the **dispatcher lane are the system's own voice** and get the
oak's trunk colour with a *round* chip — Dodona is not a seventh lane, and shape says so
where a hue could be mistaken for one. A grey square chip means a work lane with no grid
slot (it is in the tray).

`overlayLines` is the maximized pane's own transcript — unfolded, unfiltered,
uncompressed. The overlay has promised "raw, one keystroke away" (§12) since M3 while
dumping only its TITLE, so nothing could check the promise; a claim no verb can reach is
where the next defect lives (CLAUDE.md §3.1). `null` when nothing is maximized.

`ui dump` is unaffected by any of this: `lines` stays an array of `prefix + body` strings,
because what the UI testifies to must not change shape because it got prettier.

**The UI hot-swaps too, separately from the daemon.** Windows locks a running image, so
a published UI used to sit on disk while the operator kept looking at the old window —
a swapped daemon behind a stale window is indistinguishable from nothing having happened.
`dodona publish` now swaps the daemon and *then* refreshes every live UI, so a UI change
shows up without closing anything.

The UI handoff is much cheaper than the daemon's: a UI owns no lanes, no agents and no
store writes — its only exclusive resource is its pipe. So the protocol is just:

```
dodona ui update <DodonaUi.exe>     # what publish sends; also usable by hand
```

1. the incumbent opens `dodona-<instance>-uihandoff` and spawns `<exe> --root <root> --successor`
2. the successor builds its window, then says `ready <build>` on that pipe — "ready" means
   the new build actually runs, so a binary that cannot open a window never takes over
3. the incumbent replies and exits, releasing `dodona-<instance>-ui`
4. the successor's pipe loop (`--successor` ⇒ retry for 15s instead of "already running")
   binds it and takes over

Same safety rule as the daemon: **if the successor never answers, the existing window
stays** and the verb reports why. Failure modes: `error: successor never connected`
(new binary died before showing a window — nothing changed), or the successor exiting
with the "Another Dodona UI is already running" box (the incumbent did not exit; its
pipe was never freed). A dump reflects the live view
model, so after mutating the store give the 250ms poller a beat (~500ms) before asking.
`dodona ack <pane_event_id>` clears a feed row's badge; `dodona undo-route <decision_id>`
marks the routing row undone and sends a `[DISPATCHER]` retraction to the lane that
consumed the misroute.
