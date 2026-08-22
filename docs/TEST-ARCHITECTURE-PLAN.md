# Test architecture — count the wires, not the cases

Status: **PLANNED, nothing built.** Written 2026-08-21 at `2ef0c54` from the operator's brief,
after six per-suite surveys of all 750 acceptance checks, a seam survey of `src/`, and a prior-art
survey of every decision this could contradict. **Revised 2026-08-21 against an adversarial
review** which found the anti-drift centrepiece unable to see two of the three doubles it claimed
to anchor. §10 lists every finding and its disposition; §3 is a redesign rather than a patch.

**The working files are the per-check authority, and until the review they were NOT IN THE REPO.**
`survey-{daemon,delivery,identity,view,window,ask}.md`, `seams.md` (S1–S12), `prior-art.md`,
`design-wires.md`, `design-fakes.md`, `design-migration.md` were written to a **session scratchpad**
— which is the failure CLAUDE.md §0 opens with, committed by the document that cites them on every
page, and §8.4 risk 1 makes those tables the **only** defence against the residual risk of the whole
job. They are now at **`docs/testarch/`** (untracked until this plan is committed; 11 files,
CRLF, no BOM, `dev lint` clean), and D-T26 makes committing them W1.0. Had they stayed where they
were, this plan's authority would not have survived the session boundary and every `merged` row
built on it would be unreviewable.

**Single phase.** The operator asked for one, explicitly. What follows is one phase containing
numbered work items W1–W12 with dependencies between them. There is no Phase 2 to approve later;
there is a **kill switch at W5** that ends the job cheaply if the approach does not hold, having
deleted nothing.

This plan is the authority on **how the test layer is shaped and how work moves between layers**.
It does not supersede `ORCHESTRATOR-DESIGN` §17 — §17 is argued head-on in D-T3 and in REJECTED,
not ignored. It takes `RECOVERY-PHASES` D-2 (suites do not share daemons) and D-3 (no new tool) as
binding, and it takes CLAUDE.md §0.1's standing directives as binding on every automatic reader it
touches.

Decision IDs are prefixed **D-T** because `D-1`…`D-7` are already taken by `RECOVERY-PHASES` and
this plan cites them.

---

## 1. THE PROBLEM

### 1.1 The operator's terms

> Integration tests are *"more expensive"* and *"more final guarantees"* and *"should be run less
> often"* — they are *"supposed to be spot checks."*

What they asked for: **a handful of expensive tests, everything else unit tests, with standardised
mock/shim shims kept in sync with their real counterparts by construction. Then all existing tests
reworked to fit.**

### 1.2 Today's real numbers

| layer | count | what it costs |
|---|---|---|
| pure logic (`unit`, xunit, `dotnet test`) | **~300 cases** | ~10 ms each; starts no daemon, opens no window, cannot flake |
| acceptance (15 `.ps1` suites) | **750 checks** | a real daemon, a real named pipe, a real SQLite store, a real WPF window driven through UI Automation, or a real child process |
| **total** | **1050 executed cases over 958 NAMES** — see the third provenance bullet | |

Provenance, because a number without one is how the "278 checks" line in CLAUDE.md §1 went stale:

- **750** = 713 static registration sites (`Check '<name>'` in 14 suites plus `m0`'s 26 inline
  `$results['<name>'] =` assignments — `m0` has no `Check` helper) + **15** harness rows written by
  `Assert-NoBuildOutputProcesses` (`tests/_workspace.ps1:353`, one per suite) + **22**
  loop-generated names a grep cannot enumerate (16 `event_*` at `m1-acceptance.ps1:1167`, 6
  `resolution_recorded_*` at `concierge-acceptance.ps1:337` — this said `:307` until the review;
  corrected by reading the file). 713 + 15 + 22 = 750, and that agrees check-for-check with the six
  survey tables, which were counted independently by hand.
- **Two names exist TWICE across suites, asserting different things, and a third only looks like
  it.** Counted 2026-08-21 by `sort | uniq -d` over every `Check '<name>'` registration plus `m0`'s
  inline `$results['<name>']`: `presence_idle_after_result` (`m2-acceptance.ps1:133` matches
  `presence=idle` in `status` text; `m3-acceptance.ps1:84` reads `$water.presence` from a window
  dump) and `double_uncertainty_asks_the_operator` (`brain-acceptance.ps1:229`, the router ladder;
  `concierge-acceptance.ps1:270`, the concierge ladder).
  `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is **not** a duplicate:
  `m2-acceptance.ps1:330` and `:334` are the two arms of one `if/else`, so exactly one ever runs.
  That distinction is precisely why the uniqueness rule keys on **runtime `$results` keys** and
  never on static registration sites (§5.2), and why the collision scan is W1 work rather than
  something W6 discovers by dropping a name (§10, finding 12).
- **~300** = `PureLogicTests` 114 `[Fact]` + 25 `[Theory]` over 97 `[InlineData]` = 211 cases,
  `PaneProgressTests` 41, `SpeechStreamTests` 13, `DictationTests` 20, `TreesTests` 15. Parsed from
  attributes, **not run**. CLAUDE.md §1's "278 checks" is stale by roughly 22 — which is the third
  time that figure has been wrong in a file that has a whole section about not hand-maintaining it.
- **300 IS CASES. 208 IS NAMES. The ledger has to care which, and this plan did not.** Re-counted by
  attribute parse 2026-08-21 across the five files: **176 `[Fact]` + 32 `[Theory]` = 208 test
  METHODS**, the 32 theories carrying **124 `[InlineData]` rows** between them — 176 + 124 = **300
  executed cases over 208 named methods**. `Run-Unit` scrapes `Passed:\s+(\d+)`
  (`tools/dev.ps1:1066-1069`), which is the **case** count, so nothing in the repo has ever needed
  the distinction. The ledger does: §9.4's B1 turns each moved check into ONE method whose
  variations are `[Theory]` rows, so a baseline keyed on cases would count every variation a move
  creates as growth and fill `added.tsv` with rows nobody can reason about. **The unit side of
  `baseline.tsv` is keyed on the METHOD**, with the executed case count as a separate,
  non-integrity-bearing column (§5.2). Restated honestly: **958 named checks today (750 + 208),
  over 1050 executed cases** — and the "1050" this document leads with is the second number.

### 1.3 What the 750 cost, measured

Five clean `dev gate` runs at `3b235ab` (issue #1): **212.4 / 216.4 / 218.8 / 232.6 / 232.9 s**,
median **218.8**. Seven gates at `f346b76`, one commit, one machine: 269.7 / 267.8 / 258.2 / 284.8
/ **312.5 (I7 breached)** / 293.5 / 292.3 — a **54.3 s spread**, 21 % of the 300 s budget, from
nothing but how busy the machine was.

And the reds. Issue #3 is open and its root cause is **not established**. Measured 2026-08-21:
`voice` ran **25 checks, 3 failed, 70.5 s** inside a full gate and **25 checks, 0 failed, 40.7 s**
alone minutes later, on the same tree — *while `voice` was in `SoloSuites`*, so nothing ran beside
it. The three reds were all one `Wait-Until` for the mic toggle expiring at 20 s. Ruled out by
measurement and not to be re-run: leaked processes, the other window suites, `m4`'s real build,
CPU (22 cores, never bound), and concurrent suites at all.

### 1.4 The observation that gives the counting rule

Four gates, busy machine → clean machine (issue #3):

| suite | busy | clean | change |
|---|---|---|---|
| ui-use | 120.9 s | 87.5 s | −28 % |
| brain | 95.4 | 79.8 | −16 % |
| workspace | 87.5 | 74.1 | −15 % |
| m1 | 57.5 | 46.7 | −19 % |
| voice | 49.9 | 39.7 | −20 % |
| m3 | 24.8 | 19.0 | −23 % |
| m2 | 18.1 | 13.9 | −23 % |
| concierge | 17.6 | 13.8 | −22 % |
| **unit** | **5.3** | **5.4** | **+2 %** |

Of sixteen suites, the only one that starts no process and opens no window is the only one whose
runtime did not move. Every suite that spawns processes or opens windows ran 15–28 % faster idle.
**The contention is process and window creation, not CPU** — so the only lever on the surviving
integration flake is *fewer integration tests*, which issue #1 already sanctions by name:
*"what remains is either cheaper suites or #3's root cause."* This plan is the cheaper-suites
branch.

### 1.5 The counting rule

> **COUNT THE WIRES, NOT THE CASES.**

An integration test exists to prove **a path is plugged in** — typing reaches an agent, a tile
click reaches the daemon, answering a question lands, the write gate refuses, publish swaps a
running process, a dying daemon loses no messages. Each such wire needs **exactly one** integration
test.

Every **variation** on what flows down that wire — which model the policy picks, what the briefing
says, whether a choice contains a filesystem path, which rung the ladder took — is a **content
question** and belongs in a pure-logic test.

Today the wiring is re-proved once per case. That is why there are 750. The surveys measured the
duplication exactly: **`m4`'s mid-turn handoff is one wire proved by 14 checks**; **`m1`'s land is
one wire ridden by 26**; **one live window is ridden by 23 `m3` checks**; **`m1`'s gate-hook
subprocess is one wire proved by 21**. Nothing is wrong with the assertions. What is wrong is that
each of them built its own fixture.

**The cost of an acceptance suite is fixtures and process starts, not `Check` calls.** That single
sentence is the whole design.

---

## 2. THE TARGET SHAPE

### 2.1 The answer

**52 integration wires, riding 19 fixtures, in 10 acceptance suites plus `unit`**, holding roughly
**195 named acceptance checks** against today's 750. The rest move down. The unit layer goes from
208 methods to roughly 760. **No name is lost**, and §5's ledger makes that arithmetic mechanical
rather than a promise.

49 came from applying a fold rule to 109 candidate wire rows the six surveys produced; 60 were
folded, each with a recorded reason (`design-wires.md` §3). The rule, in the operator's own terms:
**if cutting this wire would redden another kept test, it is not a distinct wire** — it is the
fixture, and it survives as a named *assertion inside* the test it is upstream of.

**Three more were added by the adversarial review, and the fold rule is what admitted them**
(§10, findings 13 and 14): E7 and E8, because m2's routing half had no destination suite at all and
cutting either reddens nothing else; and J3, because CLAUDE.md §3.2's heading is *"THERE ARE TWO
PROPERTIES"* and the first draft folded both into one. 49 + 3 = **52**. That the number moved on
first contact is the honest reading of §8.4 risk 5: the register is a hand merge and is the plan's
single point of judgement.

**The number the operator actually feels is 19, not 52.** 52 is how many paths are asserted; 19 is
how many expensive *setups* run. An extra assertion on a fixture that already exists costs
milliseconds.

### 2.2 The wire list

Suite names change to what they cover (D-T14). `THE check` is the single existing check name that
becomes that test; every other name folded into it survives as a named assertion on the same
fixture.

**`lifetime`** — a lane's processes, and what ends them (was `m0`). Fixtures: F1 ws+lane+kill+restart, F2 lane+short lease, F3 no daemon at all.

| id | the path | THE check |
|---|---|---|
| A1 | A daemon killed `-Force` mid-turn leaves shim and agent alive; a replacement adopts the orphan, drains its buffer, and the result produced while no daemon existed lands **exactly once** | `m0:orphaned_result_landed` |
| A2 | Only the child is killed: the shim exits by itself and its pipe name and `shim-lane<N>.json` go with it | `m0:shim_exits_when_its_agent_dies` |
| A3 | No daemon ever returns: the shim ends its own lease and takes the agent | `m0:the_lease_takes_the_agents_too` |
| A4 | `stop-all --lanes` reaches a shim over its own pipe (`##shutdown`) and the child **tree** dies | `m0:and_its_agent_dies_with_it` |
| A5 | Liveness is asked of the OS: `ps` still counts a lane whose record file was deleted | `m0:ps_counts_a_lane_whose_record_is_gone` |
| A6 | The spawn-site environment reaches the agent two process hops away | `m0:a_lane_agent_is_told_its_workspace` |
| A7 | A command that promises a reading **summons nothing**: no daemon appears, no lane count moves | `m0:status_does_not_summon_a_daemon` |

**`gate`** — work isolation (was `m2`'s isolation half + `m1`'s G1/G3). Fixture F4.

| id | the path | THE check |
|---|---|---|
| B1 | A real `gate-hook` subprocess fed real stdin bytes **including a UTF-8 BOM** reaches the daemon over the ctl pipe, REFUSES a shared-checkout write, and the refusal promotes the lane: real worktree, agent killed and respawned inside it, session resumed, briefing rebuilt as a ticket lane's | `m2:the_lane_ends_up_in_a_worktree` |
| B2 | The undo removes the worktree directory and the branch ref after the agent has let go of the cwd | `m2:the_undo_prunes_the_worktree_and_the_branch` |

**`land`** — the merge (was `m1`'s M/V/D/A + `workspace`'s land). Fixture F5 = a **two-repo** workspace.

| id | the path | THE check |
|---|---|---|
| C1 | A real `git merge main` + `merge --ff-only` advances **exactly one** repo's main carrying the branch's work, prunes the worktree, deletes the branch, retires the agent standing in it; the other repo's ref does not move | `m1:main_is_now_the_merge_that_was_verified` |
| C2 | `verify` runs as a real child in the worktree and its exit code gates the advance **before** it | `m1:red_verify_leaves_main_unchanged` |
| C3 | The land consults `SilentDrops` over the branch's real merge history and refuses a merge that succeeded and lost work | `m1:a_silent_drop_is_refused` |
| C4 | The land runs off the serial control pipe: the daemon still answers while a verify runs | `m1:say_answers_during_a_land` |

**`review`** — a finished ticket (was `m1`'s R\* + `ui-ask`'s land ask + `brain`'s manager). Fixture F6.

| id | the path | THE check |
|---|---|---|
| D1 | A ticket turn ending with the worktree moved fires the per-turn hook at the **spawn** site, produces a completion record, and opens a `land` question row without waiting for a review | `ui-ask:the_approval_ask_does_not_wait_for_a_review` |
| D2 | That record reaches a real manager; its send-back returns to the lane as input on the typed-sentence path; `approve` grants nothing and answers nothing; a named changed file is handed over off disk, once | `brain:a_send_back_reaches_the_lane_as_input` |
| D3 | The send-back bound is counted in the STORE and survives a real daemon restart mid-loop | `brain:three_send_backs_is_the_bound_and_it_survives_a_daemon_restart` |

**`brain`** — utility lanes exist correctly. Fixture F7 = **two-project** workspace, autostart ON.

| id | the path | THE check |
|---|---|---|
| E1 | A daemon started **the way the operator runs it** creates the classifier itself, a typed sentence reaches THAT lane, and the lane opens in the project the classifier chose | `brain:typed_input_reaches_the_classifier_autostart_made` |
| E2 | A restart ADOPTS every live utility lane over its pipe — one per project — spawning none and retiring none | `brain:restart_adopts_a_brain_for_every_project` |
| E3 | A live predecessor whose pipe cannot be connected is never duplicated nor written off as gone, and one project's wedge does not block another | `brain:no_second_brain_beside_a_live_one` |
| E4 | A registry mutation reaps the processes it orphans | `workspace:detaching_a_project_stops_the_lanes_that_were_in_it` |
| E7 | The sentence decided in CODE takes the INSTANT path: a `LANE:` prefix is delivered to that lane with no classifier consulted and nothing held | `m2:tier0_message_delivered` |
| E8 | With no classifier the fallback **announces itself** and writes `routing_unrouted`; it never degrades in silence | `m2:unrouted_fallback_is_announced` |

**`assist`** — what a second agent does behind the fast path. Fixtures F8, F9.

| id | the path | THE check |
|---|---|---|
| E5 | A real brain turn runs behind the instant code path and its rename lands on the lane row afterwards | `brain:rename_applied` |
| E6 | Genuine uncertainty HOLDS the sentence as a `questions` row that outlives a daemon restart; answering spawns the lane in the chosen project and delivers the stored words verbatim | `workspace:the_held_sentence_itself_reaches_the_new_lane` |
| L1 | The compressor pool starts real sessions, a turn-final crosses over a real shim pipe, and the rewrite lands on the row as `compressed` | `compression:turn_final_gets_compressed` |

**`window`** — one workspace, one live window (was `ui-grid` + `ui-ask` + `voice` + `m3`). Fixture F10.

| id | the path | THE check |
|---|---|---|
| F1 | Text typed in the box crosses the UI pipe to the daemon, creates and routes a lane, reaches a real agent running where the daemon put it, and the agent's own answer, tool calls and out-of-band wire fields come back into the pane | `ui-grid:the_newline_survived_to_the_agent` |
| F2 | Enter through the **real `PreviewKeyDown` path** submits and Shift+Enter does not, raised as a routed event on the real TextBox | `ui-grid:enter_still_sends` |
| F3 | A real WPF layout measures the box: opens at three lines, drags taller, and the FEED gives up the pixels | `ui-grid:the_box_opens_at_three_lines` |
| F4 | A real UIA `Invoke()` reaches `MainWindow.LaneAction` and the daemon acts, across all five tile actions | `ui-grid:close_button_stops_the_lane` |
| F6 | Nothing lives in the window's memory: a relaunched process restores collapse from the store and box height + listen toggle from `ui.json` | `ui-grid:collapse_survives_reopening_the_window` |
| F7 | A real window renders deterministic pixels at a fixed size | `m3:screenshot_fixed_size` |
| F9 | The mic affordance: a real UIA invoke and the `listen` verb both land in `SetListening`; a real socket to a closed loopback port reads as **error**, not listening; `Starting` has a deadline that fires | `voice:clicking_the_mic_toggles_listening` |
| F10w | `ui heard` lands in `MainWindow.OnHeard` — the engine's own landing site — and nothing the decision layer can produce submits the box | `voice:spoken_send_words_do_not_submit` |
| G1 | `ui answer` → `AnswerAsk` → the daemon's `answer` → the row closes, the overlay follows it down, and the kind's real effect runs (a real `git init` with a first commit; a merge token actually granted) | `ui-ask:answering_the_approval_ask_grants_the_merge_token` |
| G2 | The overlay's choices exist in the live UIA tree as elements named `ask:<value>` | `ui-ask:a_choice_is_a_real_button_a_person_can_click` |
| G3 | Esc is view state only: overlay down, row still open and answerable | `ui-ask:escape_puts_the_ask_down` |

**`shell`** — one window over N workspaces (was `ui-shell` + `ui-wake`). Fixtures F11, F12.

| id | the path | THE check |
|---|---|---|
| H1 | A bare launch is the SHELL booted to zero, it notices a workspace daemon that came up elsewhere with no operator action, and the `workspace` verb swaps the grid | `ui-shell:a_waking_workspace_takes_the_grid` |
| H2 | A write from the window starts a **sleeping** workspace's daemon on demand on both the box path and the tile path, and never says "daemon not running" | `ui-wake:a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts` |

**`publish`** — the swap (was `m4` + `publish` + `ui-shell`'s hot swap). Fixtures F13–F17.

| id | the path | THE check |
|---|---|---|
| I1 | A real MSBuild publish lays down one stamped dir; daemon A hands off mid-turn to B and exits; shim, agent, session and in-flight turn survive with no loss or double delivery; the new daemon reports the new build and its stamped commit | `m4:landed_exactly_once` |
| I2 | A candidate that cannot answer `version --json` is refused and the system stays up | `m4:daemon_alive_after_bad_swap` |
| I3 | A blocked swap arms itself and fires when the blocker really clears, and not before | `m4:armed_swap_fires_when_blocker_clears` |
| I4 | `GcOldBuilds` retention vetoes the directory a live lane's deployed gate names | `m4:the_gate_exe_of_a_live_lane_survives_the_swap` |
| I5 | `--all` resolves targets from the REGISTRY: a live daemon from another registry is neither named nor touched | `publish:foreign_daemon_survived_untouched` |
| I6 | A target that connects, reads and answers **nothing** is named and publish exits non-zero | `publish:publish_names_a_target_that_did_not_take_the_build` |
| I7 | The concierge takes the swap over its ctl pipe and the new process reports the new build | `publish:a_swapped_concierge_reports_the_new_build` |
| I8 | A live shell **window** hot-swaps to a successor from a different binary path that serves the shell pipe as `--shell` | `ui-shell:shell_successor_is_the_new_binary` |
| I9 | A build with no provenance refuses to watch, out loud, on a daemon started the operator's way | `publish:no_provenance_daemon_refuses_to_guess` |

**`identity`** — what is on disk, and the machine-global process. Fixtures F18, F19.

| id | the path | THE check |
|---|---|---|
| J1 | An older store is copied before it is migrated when a **real daemon** opens it, and the daemon comes up | `workspace:a_pre_v9_store_is_copied_before_it_is_migrated` |
| J2 | A legacy `<root>\.dodona` store is physically relocated on first explicit resolve | `workspace:migration_moved_the_store` |
| J3 | A typed `--root` **names** a path and does not **adopt** one — asserted through `status`, i.e. through the RESOLUTION layer rather than through `where`, because `Client` resolves before any per-verb guard runs | `workspace:a_named_root_creates_no_workspace_for_a_daemon_command` |
| K1 | The concierge is a real machine-global process on its own pipe; it consults a tier agent child and acts on the answer; it stops when told | `concierge:fuzzy_match_on_the_cheap_tier` |
| K2 | The concierge reviews an already-delivered sentence from behind and its verdict lands in the feed later | `concierge:review_behind_reports_a_group_misroute` |

**7 + 2 + 4 + 3 + 6 + 3 + 11 + 2 + 9 + 5 = 52 wires on F1–F19 = 19 fixtures.**

**Two wires carry a note that outlives them, and both were written by the review.**

- **A7 and J3 are two properties, not one, and CLAUDE.md §3.2 says so in capitals.** "Does not
  summon a daemon" and "does not adopt a folder" are different, the folder-adoption incident is what
  proved it, and `workspace:a_named_root_creates_no_workspace_for_a_daemon_command` asserts the
  second **through `status` on purpose** — through the resolution layer, not through `where`, because
  every verb that talks to a daemon adopted on sight and a per-verb list could never have fixed it.
  Folding them would have discarded exactly the property that was the point.
  **And A7's half is still open work**: issue #13 records that the no-summon list is a literal
  `cmd is "stop-daemon" or "status" or …` in `Client`, that ten verbs summon while reading like
  questions, and that two of them (`repos`, `token-status`) **write**. One wire over one verb is a
  weaker instrument than a hand-maintained list deserves, and this plan does not pretend otherwise.
  **When #13 lands and the list becomes enforcement, A7's `owner_check` is re-pointed at whatever
  enforces it, in #13's own commit** — a `wires.tsv` row is a tracked file, so that is a diff
  somebody reviews rather than a thing to remember.
- **A negative rendering claim is a WIRE question, not a content question**, and
  `compression:midturn_narration_is_not_in_the_pane` is the instance that taught it (§10, finding
  11). It reads `$pane.lines` off a live `ui dump` at `compression-acceptance.ps1:96`; the survey
  classified it `unit`, destination `StoreReader.Tail`'s kind filter
  (`src/DodonaUi/StoreReader.cs:216-218`). But *"the filter is correct"* and *"the pane consults the
  filter"* are two facts, and only the first survives a move: change the pane's call to
  `Tail(all: true)` and the unit test stays green while the operator sees duplicated mid-turn
  narration. **That is CLAUDE.md §3's routing ladder in a new costume.** So the SQL projection moves
  down AND one assertion stays on F10's existing window fixture — `moved` plus `merged`, two ledger
  rows, milliseconds on a fixture that already exists. The general rule: **absence can always be
  produced by a renderer that never consulted the thing you are testing**, so an absence asserted
  through a window stays at a window.

### 2.3 The thirteen that are never skipped

If a future session wants a shorter list to hold in its head, this is it — the wires whose loss
ships a defect this repo has **already paid for once**:

`A1` · `A3` · `B1` · `C1` · `C2` · `D1` · `E1` · `E3` · `E8` · `F1` · `F10w` · `I1` · `I6`

**`E8` was added by the review.** The unrouted fallback's *announcement* is the thing CLAUDE.md §3
added after the routing ladder was dead in production for two days and the only evidence was a
status-line suffix nobody reads. A wire list that keeps `E1` (the classifier exists at all) and
drops the announcement keeps the detector and throws away the alarm.

Everything else in the 52 is cheap because it rides a fixture that already exists. These thirteen
stop being cheap the moment they are missing.

### 2.4 Why 52 and not 15

15 is reachable only by deleting one of five distinctions, and each has already cost this repo
something:

| distinction | what deleting it costs | the incident |
|---|---|---|
| a process **died** vs a process **never appeared** | A7 and I9 go, and a "quick health check" starts four haiku lanes again | CLAUDE.md §3.2, 2026-08-19 |
| a **ref moved** vs a ref was **reported** moved | C1/C2/C3 collapse and verify-after becomes indistinguishable from verify-before | D-5; `red_verify_leaves_main_unchanged` is the only check separating them |
| a **window rendered** vs a dump **reported** | F2/F3/F7/G2 go, and the WPF class-handler trap, the measured `fit`, `RenderTargetBitmap`'s coordinate space and the pixels↔value link go with them | CLAUDE.md §0.2; five lane actions were **unreachable**, not merely untested |
| a **socket opened** vs a state **said** listening | the egress observation goes | `voice`'s recorded red `state=[listening] engine=[deepgram]` — a test run authenticating on the operator's bill |
| a **lookup that could never miss** vs a **live path** | E1 goes | CLAUDE.md §3: the routing ladder, fully green, dead in production for two days |

And **why not 80**: the fold rule is ruthless and was applied. Sixteen candidate wires died purely
because cutting them reddens a kept test — including `lane-start spawns a shim+agent`, `the
gate-hook subprocess denies`, `publish builds a versioned dir` and `a live UI reads the store`,
which carried 21 + 23 + 2 + 23 survey checks between them.

### 2.5 Before and after

| | today (measured / parsed / counted) | after (target, derived) |
|---|---|---|
| acceptance suites | 15 + `unit` = 16 | 10 + `unit` = **11** |
| acceptance checks | **750** | **~195** |
| `Start-Process` sites in the suites | **54**, COUNTED | **~19** |
| `Wait-Daemon` sites in the suites | **32**, COUNTED | **~19** |
| unit test methods / executed cases | **208 / 300** | **~760 / ~1100** |
| total named checks | **958** | **958** (§5 makes it exact) |
| full `dev gate` | **median 218.8 s**, spread 212.4–232.9 (five clean runs at `3b235ab`, issue #1) | **115–135 s ESTIMATED, NOT MEASURED** |

**THE FIXTURE ROW WAS INVENTED AND IS NOW COUNTED** (§10, finding 7). It read *"on the order of 200
(one per keep-classified check)"* in the column headed **measured / parsed**, and the parenthetical
was a false premise: a suite builds one workspace and runs many checks against it. Counted
2026-08-21 with `grep -c 'Start-Process'` and `grep -c 'Wait-Daemon'` over `tests/*-acceptance.ps1`:
**54** and **32**. Neither is a census of process starts — a client verb summons a daemon by
autostart and no `Start-Process` line marks it — so 54 is a **floor**, and it is the only figure
here anybody has actually counted. The honest claim is therefore **54 counted spawn sites → ~19
fixtures, about 2.8×**, not the 10× the invented row implied. §1.4's argument is unaffected (the
contention is process and window creation, and `unit` is the only suite that did not move between a
busy and an idle machine), but the multiplier that sells the job is smaller than it looked.

**The wall-clock figure is an estimate, is labelled one everywhere, and does NOT descend from the
fixture ratio.** It is 345 estimated suite-seconds at `SuiteConcurrency = 3` with `unit` serialized
in front, floored by `publish` at ~70 s — arithmetic over per-suite second estimates, which is why
correcting 200 to 54 does not move it. Nobody has run a gate at the new shape because the new shape
does not exist. W10 measures five gates and **replaces this row with the real spread** — P5.2 deleted
the per-suite duration table because *"a table nobody can trust is worse than no table, because it
still gets quoted."*

**I7's budget stays at 300 s** (D-T17).

### 2.6 How many checks actually move

The six surveys classified every one of the 750 by hand: **465 movable, 275 keep, 10 unclear**.
The wire fold pushes roughly a further 95 of the "keep" rows down, because their assertion turned
out to be content riding a wire somebody else owns — so the working expectation is **~560 moved,
~195 kept**.

**Do not treat either number as a target.** The ledger derives it (D-T25): the surviving
integration count is `rows(wires.tsv)` + one harness row per surviving suite, and everything else
is accounted for one row at a time.

**And do not treat 465/275/10 as verified.** The review found one row misclassified in a way no
count can see — `compression:midturn_narration_is_not_in_the_pane`, filed `unit` while asserting on
a live window's rendered lines (§2.2's second note). One misclassification found in the first
adversarial read of six survey files is not a defect rate, but it is not zero either. The
instrument that catches the rest is §9.7 rung 6's audit, and §10 finding 23 raises its sample rate
because of this row.

---

## 3. THE DOUBLES AND THE ANTI-DRIFT MECHANISM

This is the section the plan is judged on, because the repo has the scar: **the routing ladder was
fully covered and fully green while dead in production for two days**, since
`brain-acceptance.ps1` stood up its own `role='router'` classifier and then looked one up.

### 3.1 Drift is three failures — and the scar is a fourth thing that is not drift

| axis | the failure | what can catch it |
|---|---|---|
| **D1 shape** | the interface grew a member; the double no longer satisfies it | the **compiler**, if the double implements the interface production implements; a reflection assertion for negative shape claims |
| **D2 behaviour** | the double satisfies the interface and **decides differently** | a **contract test** — one body, two subjects — only where the real subject is cheap, deterministic and in-process |
| **D3 world** | `claude` / Deepgram / git moved; the double is faithful to yesterday | **nothing automatic.** Only a recorded corpus of real bytes plus a deliberate re-record |
| **D0 self-fulfilling lookup** | the fake was *perfectly faithful*; the test could not come back false | **not drift.** No anti-drift mechanism touches it |

Conflating D0 with drift builds the wrong machine. D0 is answered by two hard rules:

- **ONE LANDING SITE.** A double is installed by replacing the exact field, delegate, argument or
  executable **production reads** — never by feeding a parallel path. `IRecognizer`'s own doc
  comment (`src/DodonaUi/Recognizer.cs:21`) states it: *"a fake that fed a parallel path would prove
  nothing about the real one."*
- **EVERY DOUBLE NAMES A WIRE IT DOES NOT REPLACE**, and that wire's check name is verified to
  still exist **and its body to be unchanged since the row was written** (§3.3.1). This used to read
  *"Constraint 3 turned from a promise into a red build"*; it is not that, and §5.1 says why four
  sections later — a surviving name can be green while asserting strictly less. It is an **anti-rot
  check on the register**: the wire a double stands beside cannot be deleted, renamed, or silently
  narrowed. Constraint 3 itself is kept by §9.7 rung 6, which is human.

### 3.2 The mechanism: the Double Ledger — REDESIGNED, and here is what was wrong with it

**The first version of this section did not work, and the review is what established that.** It is
worth stating the failure precisely before the fix, because the failure is instructive and the fix
is only credible against it.

The mechanism was `Assembly.GetExecutingAssembly().GetTypes()` in one `unit` test, with the naming
refusal scoped to *"any type in the test assembly"*. But:

- **`FakeRecognizer` is at `src/DodonaUi/Recognizer.cs:73`** — a PRODUCTION assembly, `net8.0-windows`;
- **`Poses` is at `src/DodonaUi/Poses.cs:9`** — the same;
- **`DodonaFakeAgent` is a standalone exe** whose own comment says *"this project deliberately
  references nothing"* (`src/DodonaFakeAgent/Program.cs:93`);
- **`tests/Dodona.Tests/Dodona.Tests.csproj` has exactly one `ProjectReference`, `..\..\src\Dodona\Dodona.csproj`** —
  verified by reading it. It cannot load `DodonaUi` (net8.0 against net8.0-windows) and does not
  reference the fake agent.

So the enumeration would have run over a population containing **none of the three doubles that
already exist**, while §3.6 tabulated all three as anchored and §8.2 sold the ledger's independent
value on anchoring them. **The escape from the ledger was `put the fake in src/`, which is where two
of them already were.** That is a mechanism that is green because it is looking at an empty set —
which is the routing ladder's own failure shape, in the section written to prevent it.

Two further defects, each fatal alone:

- **`Anchor.Interface` was self-satisfiable.** The rule was *"≥ 2 implementers in the production
  assemblies"*, and `IRecognizer` has exactly two: `DeepgramRecognizer`
  (`src/DodonaUi/DeepgramRecognizer.cs:48`) and `FakeRecognizer` — **one of the two IS the double**.
  Any future `FakeX : IX` placed in `src/` would anchor itself.
- **`Interface` alone reaches D1 and nothing else**, and §3.1's own table says so. It was admissible
  as a sole anchor while §3.6 anchored `FakeRecognizer` as `Interface` and named its behavioural
  divergence in the same row.

#### The redesign: two rungs, because one instrument cannot answer both questions

**Rung 1 — POPULATION. Static, text, repo-wide, in Repo-Lint (I8).** A parse of every
`src/**/*.cs` and `tests/**/*.cs` for type declarations and `[Double(` attributes. It asserts four
things:

1. every type whose name matches `^(Fake|Recording)` carries a `[Double(...)]`;
2. no type is named `Stub*` or `Mock*`, anywhere;
3. every `[Double(...)]`'s `Wire = "<suite>:<check>"` resolves to a `wires.tsv` row (§3.3 says what
   "resolves" now means);
4. **every project that declares a `[Double]` appears in `tests/ledger/double-assemblies.tsv`**,
   which is the list of assemblies a rung-2 reflection test actually loads.

**Assertion 4 is the one the first design could not have.** It is what closes "put the fake
somewhere the ledger does not look": adding a double to a project with no rung-2 coverage reddens
the lint until either the project is given coverage or the double moves. And a **text scan cannot
miss an assembly**, because it never asks the runtime what is loaded — it reads the repo. It also
runs on a tree that will not compile, which is the property `tools/dev.ps1` exists for (CLAUDE.md
§1) and which reflection can never have.

**Rung 2 — ANCHOR SEMANTICS. Reflection, per assembly, in that assembly's own test project.**
Counting implementers, resolving `typeof(Real)` and checking contract subclass counts are semantic
questions a text scan answers badly. So each entry in `double-assemblies.tsv` names the test project
that loads it:

| assembly | doubles in it today | rung-2 test project |
|---|---|---|
| `Dodona` | `RecordingLaneSink` (new) | `Dodona.Tests` (net8.0, exists) |
| `DodonaUi` | `FakeRecognizer`, `Poses`, `RecordingTransport` (new) | **`Dodona.Ui.Tests`** (net8.0-windows, **created in W3**) |
| `Dodona.Tests` / `Dodona.Ui.Tests` | fixtures and contracts | themselves |
| `DodonaFakeAgent` | — see below | **none, deliberately** |

**`DodonaFakeAgent` carries no attribute and is not in the reflection population, and that is a
decision rather than a gap** (D-T27). It is a *program*, not a type: anchoring a 545-line exe by
reflecting over one of its classes would assert nothing about the wire shapes it emits, which is the
only thing anyone cares about. Its anchor is the **corpus rung** (§3.4) — `MANIFEST.json`,
`dev wire-sample`, lint row I9 and `FakeShapeTests` — which is file-based end to end and therefore
needs no reference in either direction. **`[Double]` is NOT `Compile Include`-linked into
`DodonaFakeAgent.csproj`**, because D-B7 (`LANE-BRIEFING-PLAN:240`, repeated at
`Program.cs:94-101`) rejected sharing source with that project and this plan cites that rejection
approvingly in §7.2. `double-assemblies.tsv` records `DodonaFakeAgent → corpus` as an explicit row,
so rung-1 assertion 4 is satisfied by a declaration somebody wrote, not by an omission nobody
noticed.

**Where the attribute lives.** `src/Dodona/Testing/DoubleAttribute.cs` — the `Dodona` assembly,
`internal`, zero behaviour — and `Compile Include`-linked into `DodonaUi.csproj` beside the twelve
files it already links (`DodonaUi.csproj:42-98`). That is the house's own mechanism for "the UI must
reach the same answer the daemon reaches", used here for the weakest possible thing: an attribute
with no logic in it. It is in `src/` and not in a test project because two of the three existing
doubles are in `src/` and always will be — `FakeRecognizer` is *the implementation the suites use*
and `DODONA_UI_MIC=off` is what stops a suite opening the operator's microphone.

```csharp
[Double(Anchor.Interface, typeof(Store), Wire = "lifetime:orphaned_result_landed",
        Contract = typeof(LaneSinkContract))]
sealed class RecordingLaneSink : ILaneSink { … }
```

`Real` is a **`Type`, never a string** — a string name is itself a hand copy, and hand copies are
what this document exists to stop.

#### The anchors, corrected

- **`Interface`** — the double and `Real` share an interface with **≥ 2 implementers that do NOT
  carry `[Double]`**. The exclusion is the whole fix: the count may not be satisfied by the double
  counting itself. `ILaneSink` (`src/Dodona/LaneSink.cs:22`, six methods) qualifies —
  `Store` (`Store.cs:12`) and `ConciergeStore` (`ConciergeStore.cs:35`) **both ship**, so a seventh
  method breaks a running system before it breaks a test. `IRecognizer` **does not**: strip
  `FakeRecognizer` and one implementer remains. **The assertion prints the surviving implementers by
  name**, so the reader can see whether the second one ships rather than trusting a count.
- **`Interface` IS NEVER A SOLE ANCHOR.** §3.1's own table says shape drift is caught by the
  compiler and behaviour drift only by a contract; an anchor that reaches D1 alone must say so.
  Every `[Double]` needs `Interface` **plus** one of:
  - **`Contract = typeof(<X>Contract)`** — an abstract class holding the `[Fact]`s with ≥ 2 concrete
    subclasses, one supplying the double and one the real thing, covering **every method whose
    behaviour a test depends on**; or
  - **`KnownDivergence = "<one sentence>", Issue = <n>`** — an enumerated, ticketed behavioural gap.
- **`Corpus`** — for anything standing in for something **outside this repository**: real bytes
  under `tests/assets/wire/real/`, replayed through the **real** parser.
- **`Landing`** is a deliberately weakest kind, for `RecordingTransport` over `DaemonClient.Send`:
  it requires the double to be installed by assigning the *same static delegate* production reads,
  and it is **invalid without a `Wire`**.

#### `KnownDivergence` is visibility, not a catch, and the plan must not sell it as one

It does not stop the drift. It makes the drift **named, counted and ticketed**, which is the same
discipline `no-seam-yet` already carries (D-T21) and which the first draft conspicuously did not
apply to doubles. Every `KnownDivergence` is counted in `dev gate`'s ledger reading line
(`doubles: N anchored, M with known divergence`) and requires an open tracker issue; rung 1 refuses
one with no `Issue`, and refuses a `KnownDivergence` added in the same commit as the code that
needed it — the escape being closed is *"write the divergence, ship it, forget it"*.

#### The mechanism is seen RED on the day it lands, against real code, and that is the point

**`FakeRecognizer`'s `Interface` anchor goes red immediately under the corrected rule.** That is not
a snag to work around; it is the mechanism working, and the drift it makes visible is concrete and
needs no invention:

> `DeepgramRecognizer` changes *when* it raises `Ready` — from socket-open to first audio frame, say.
> `IRecognizer`'s shape is unchanged, so the compiler is silent. `FakeRecognizer` still raises
> `Ready` **synchronously inside `Start()`** (`src/DodonaUi/Recognizer.cs:99-111`, and the comment
> there says why: *"Ready IS raised, synchronously, and that is what keeps the 268 existing checks
> byte-for-byte unchanged"*). So every window check stays green, `ui dump` still reports
> `state=listening`, and the live mic sits in `starting` forever — the exact failure the three-state
> glyph exists for.

What is done about it, in two halves, because only one of them is closable:

- **The arrival contract IS testable and becomes a real `Contract`.** `IRecognizer`'s own doc
  comment states it (`Recognizer.cs:44-47`): *"exactly one of `Ready` or `Failed` arrives, exactly
  once, and one of them always does"*. `RecognizerContract` has two subclasses — `FakeRecognizer`,
  and **`DeepgramRecognizer` pointed at a closed loopback port**, which is deterministic, in-process,
  needs no network, costs no quota and opens no microphone. The suites already prove that shape is
  reachable: `voice`'s F9 wire asserts *"a real socket to a closed loopback port reads as **error**,
  not listening"*. So the real subject runs, and D2 is genuinely closed for arrival-count.
- **The TIMING half is not closable and stays a counted `KnownDivergence` with an issue.** No
  in-process subject makes a socket answer late on purpose without becoming a third fake, and a
  third fake would need its own anchor. So: `KnownDivergence = "Start() raises Ready synchronously;
  DeepgramRecognizer raises it only after the socket answers"`, ticketed, counted in the gate line,
  **permanent** — it is not a debt to be worked off, and labelling it as one would be the same
  dishonesty in a smaller font.

**That is the honest state of the centrepiece:** rung 1 closes the population hole completely
(a text scan cannot miss an assembly); the corrected `Interface` count closes self-anchoring
completely; `Contract` closes D2 wherever the real subject can run in-process; and where it cannot,
nothing closes D2 and the plan says so with a number in the gate output. **If that is not enough,
the honest response is fewer doubles, not a stronger-sounding attribute** — which is why D-T11
(delete a transport rather than fake it) is the model §3.6 should follow further than it does.

#### BUILT AT W4, AND TWO THINGS ABOVE WERE NOT BUILDABLE AS WRITTEN

Recorded here rather than only in the work item, because this subsection is what a later slice
reads before adding a double. `tests/ledger/README.md` § *W4* carries both in full, with the
literal reds.

1. **The `Interface` count has no shippable remedy for `FakeRecognizer`, and this section asserts
   one.** It states the rule as ">= 2 non-`[Double]` implementers", says `IRecognizer` does not
   qualify, and §3.6 anchors `FakeRecognizer` as `Interface` regardless — *"RED on day one under
   the corrected rule, deliberately"*. Both remedies offered here (`Contract`, `KnownDivergence`)
   are about BEHAVIOUR and neither changes an implementer count, so as written the mechanism ships
   a permanently failing unit test. Weakening the rule to ">= 1" was rejected: it would bless
   exactly the interface-that-exists-only-as-a-seam case. **Built instead:
   `SeamOnlyInterface = <open issue>`** — a declaration on the model of `no-seam-yet` (D-T21),
   refused by rung 1 without a positive issue number, counted separately in the gate's ledger
   reading. `FakeRecognizer` carries it; issue #17 holds both of its declared gaps.

2. **`Contract` cannot be a `Type`.** A contract class holds `[Fact]`s, so it lives in a test
   project, and two of the three doubles live in production assemblies which cannot reference
   `tests/`. `typeof` would have compiled only for a double in a test project — the population the
   first design already failed on. It is a **string** naming the class, and the hand copy that
   creates is closed by `Interface_is_never_a_sole_anchor`, which resolves the name in the test
   assembly and reddens if it names nothing, names something concrete, or names something with
   fewer than two concrete subclasses. `Real` stays a `Type`, as required.

Also found by running it: the first implementation of the count went **GREEN** over
`FakeRecognizer`, because it and `DeepgramRecognizer` share `IDisposable` as well as `IRecognizer`
and the strongest shared interface won. Candidates are now restricted to interfaces declared in the
assemblies under test.

### 3.3 What goes red, and when

| you did this | what goes red | where |
|---|---|---|
| added a `Fake*`/`Recording*` type with no `[Double]`, **anywhere in the repo** | `dev lint` rung-1 assertion 1, naming the file and the type | `dev lint`, sub-second, **no build needed** |
| named it `Stub*` or `Mock*`, **anywhere in the repo** | rung-1 assertion 2 | `dev lint` |
| put a double in a project no reflection test loads | **rung-1 assertion 4** — the project is not in `double-assemblies.tsv`. *This is the assertion the first design could not have, and the escape it closes is where two of the three existing doubles already live* | `dev lint` |
| anchored `Interface` where the only second implementer IS the double | `Every_Interface_anchor_has_two_shipping_implementers`, **printing the survivors by name** | `unit` / `Dodona.Ui.Tests`, ~10 ms |
| anchored `Interface` with neither a `Contract` nor a `KnownDivergence` | `Interface_is_never_a_sole_anchor` | `unit` / `Dodona.Ui.Tests` |
| wrote a `KnownDivergence` with no `Issue`, or added one in the same commit as the code that needed it | rung-1, and the count in `dev gate`'s ledger reading line moves | `dev lint`, `dev gate` |
| deleted, renamed **or quietly narrowed** the wire check a double stands beside | `Every_double_names_a_wire_that_still_exists` (existence) **and** `dev ledger`'s `owner_body_sha` mismatch (content) — see §3.3.1 | `unit`, `dev lint` |
| grew `ILaneSink` a seventh method | **the compiler** — `Store`, `ConciergeStore` and the recording sink all stop building | `dev build` |
| wrote the naive recording sink (`append; return list.Count`) | `LaneSinkContract`'s dedup case — the real `Store.PaneEventId` (`Store.cs:1017`) is `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)` and **returns 0 for a repeat** (`:1030`) | `unit` |
| changed a `case` in `HandleShimLine` | `WireCorpusTests.every_recorded_line_classifies_as_recorded`, at a line number | `unit` |
| taught `DodonaFakeAgent` a shape reality never sent | lint row **I9** first (`dev wire-sample` out of date), then `FakeShapeTests.every_shape_the_fake_emits_has_a_real_witness` unless it is declared in `MANIFEST.json`'s `unwitnessed[]` **with a reason** | `dev lint`, then `unit` |
| added a `Snapshot` member no pose sets two ways | `Poses_cover_every_snapshot_member` | `unit` (UI test project) |
| duplicated a check name **anywhere in the repo** at RUNTIME | `Every_check_name_is_unique`, over the union of every suite's `$results` keys — never over static `Check '<name>'` sites, because `m2-acceptance.ps1:330`/`:334` legitimately register one name from two arms of an `if/else` | `dev ledger --live` |
| let `MANIFEST.json` lose its provenance | `dev lint` (malformed provenance is a defect; a *date* is not) | `dev lint` |

**Every one of these is a check, and CLAUDE.md §0.3 says a check is worth nothing until it has been
seen red.** W4 and W5 prove each by breaking it on purpose and recording the literal red in the
check's own XML doc comment, because `dev prove` refuses the `unit` suite. **The same standard
applies to the mechanism itself, which is why §3.2 ends by naming the red it produces on day one
against real code that nobody wrote for it** — `FakeRecognizer`'s `Interface` anchor. A mechanism
that went green on first contact with this repo would be the empty-population failure again.

### 3.3.1 What `Wire` actually enforces — the claim, downgraded and then given teeth

**The first draft over-claimed here and §5.1 refutes it four sections later**, which is the
inconsistency the review named (§10, finding 4). D-T10 said `Wire` *"turns Constraint 3 into a red
build"*; what it checked was that a **string still resolves to a check name that exists**. And §5.1
reason 2 says, correctly, that *"an equal name is no longer an equal assertion… a surviving name can
be green while asserting something strictly weaker, and no name list can see that."* Under this
plan's own migration every wire check is rewritten, so `Wire = "gate:the_lane_ends_up_in_a_worktree"`
would resolve happily against a check narrowed to a store-row read. Both sentences cannot be true.

So the claim is downgraded and the mechanism is given the only teeth available:

- **What it is:** an anti-rot check on the register. A double may not stand beside a wire that has
  been deleted or renamed, and a wire's owner may not be **silently** narrowed.
- **What it is not:** proof that the wire still exercises the path the way the operator runs it.
  Nothing mechanical can prove that; §9.7 rung 6 and the surveys are the instrument, and they are
  human.
- **The teeth:** `wires.tsv` gains an **`owner_body_sha`** column — SHA-256 over the owner check's
  normalised body, from `Wire '<id>' {` to its closing brace with whitespace collapsed (the `Wire`
  block is W7, so this column is populated at W7 and empty before it). `dev ledger` recomputes it and
  **refuses a mismatch**, so narrowing a wire's owner forces the editor to re-state the row and a
  reviewer sees a `wires.tsv` diff beside the test diff.
- **The honest limit, said once so nobody mistakes it for more:** a hash is a **speed bump, not a
  proof**. A person weakening a check can re-run `dev ledger --rehash` and the row goes green. What
  it buys is that the weakening cannot happen **silently** — which is the same property, and the
  same limit, as the `no-seam-yet` count.

### 3.4 The corpus — the only thing that reaches D3, and its seed is already committed

`spikes/spike1-output/wire.jsonl` is a **tracked, real, 20-line `claude` stream-json transcript**
and costs no quota. MEASURED (parsed during the design pass): 1 `system/init`, 11
`system/thinking_tokens`, 4 `assistant`, 2 `result/success`, 1 `rate_limit_event` — and **line 1
carries a UTF-8 BOM**, which is kept deliberately, because it is the GateHook incident's own
artefact in fixture form.

Two findings from it that a hand-written fake would never have produced: the two `result` lines do
**not** put `"type"` first in key order (so any parse that is not a real JSON parse dies on real
traffic), and real `assistant` messages carry `content[].type == "thinking"` blocks that the fake
never emits, arriving as a *separate* message sharing the following text block's `message.id`.

**Day one is a debt, and it says so in a tracked file.** The seed has **no `tool_use`, no
`user`/`tool_result`, no `permission_denied`** — so `unwitnessed[]` starts large. That is the honest
state: the mechanism records a debt on day one and is already enforcement for the *next* shape
somebody adds. W11 shrinks it with one **directed** real-model run.

**Staleness is a reading, not an assertion** (D-T13). A test that reddens when the corpus is older
than N days fails for a non-defect, on a date, reddens every historical commit under bisect, and
teaches people to re-run instead of read. Instead: `dev lint` fails on *malformed or absent*
provenance, and `dev gate` prints one **reading outside the assertion list**:

```
corpus:  recorded <date>, claude <ver> — <N> shapes witnessed, <M> unwitnessed
doubles: <N> anchored, <M> with a known divergence (issues #a #b)
```

**And the gate's assertion count stays at TEN** (§10, finding 15). The first draft said the count
stays at ten here, made `dev ledger` an eleventh gate assertion in D-T23, and conceded in §8.4 risk 8
that it had — three statements, no reconciliation. Resolved by folding the ledger's static checks
into **Repo-Lint (I8), which is already one of the ten and already asserted by `gate`**. Nothing is
widened, the refusal is real, and D-T23 is rewritten accordingly. A lint row is the correct home
anyway: it is a sub-second static parse of tracked files, which is exactly what I8 is.

#### `unwitnessed[]` is the escape hatch this plan denied itself everywhere else

Raised by the review (§10, finding 18) and it holds. As first written, `every_shape_the_fake_emits_has_a_real_witness`
was satisfiable by adding the shape to `MANIFEST.json`'s `unwitnessed[]` *"with a reason"* — no closed
vocabulary, no count in a verdict, no issue number, no cap, while D-T21 imposes all of that on
`no-seam-yet`. A mechanism whose failure mode is *"edit a JSON list and be green"* is a convention
wearing enforcement clothes. So an `unwitnessed[]` entry is now `{shape, reason, issue}` where:

- `reason` comes from a **closed vocabulary** — `not-in-seed`, `costs-quota`, `unreachable-offline`;
- `issue` is an open tracker number, and rung-1 lint refuses an entry without one;
- the **count appears in the gate's corpus reading line**, so it cannot hide;
- **a new entry may not land in the same commit as the shape that needed it** — otherwise the
  workflow is "emit a shape, declare it unwitnessed, ship".

**And `dev wire-sample` had a hole of its own: it compared only the shapes the fake emitted DURING
THE SAMPLE RUN.** A shape behind a directive the sample script never exercises never appears in the
dump, so I9 and `FakeShapeTests` both stay green over a shape nobody has ever seen. That is a lookup
that cannot miss — the routing ladder again. **So `--wire-sample` ENUMERATES rather than samples**:
`DodonaFakeAgent` holds its emittable shapes in one `static readonly` table which is also **the thing
it emits from**, and `--wire-sample` prints that table. Ensure at the point of use, never look up
(CLAUDE.md §3).

**Which side of D-B7 `WireShape.cs` falls on, stated so it is not re-argued:** `src/Dodona/WireShape.cs`
is the tuple `LaneRuntime`'s parser switches on, and it is **not** linked or referenced into
`DodonaFakeAgent`. The fake agent keeps its own literal strings; `dev wire-sample` compares the two
**as data, at lint time**. That is evidence rather than a shared constant, which is exactly what D-B7
asked for (*"a shared constant keeps the two halves agreeing with each other while both drift from
what `claude` emits"*) — and §7.2 cites that rejection approvingly, so the plan may not quietly
violate it four sections later.

### 3.5 What is never faked, and why

`seams.md` §3 enumerates nine boundaries, and **every one of them is the subject of a kept wire**:

| never fake | because | the wire that keeps it real |
|---|---|---|
| `Store` | the properties **are** the transactions: `LandCommit` (`Store.cs:1525`) re-checks holder identity and lease expiry *inside* the tx that lands the ticket, frees the claims and withdraws the `land` question, in one multi-statement command | C1 |
| `Registry` | repo-exclusivity is a partial `UNIQUE(members.key) WHERE is_git` index the class comment calls *"the real arbiter"*; a `HashSet` is a **different enforcement mechanism** passing a test written about the index | `identity`'s real-`Registry` fixtures |
| anything that mutates a git ref | `SilentDrops` exists to detect a merge that **succeeded** and lost work; a canned success is the exact input that cannot catch it | C1, C3 |
| `GateHook`'s stdin | `Console.In` hands a leading U+FEFF back as an ordinary character and PS 5.1 writes BOMs by default — that pair made the claim gate **fail open on every run** while looking green | B1, with real BOM bytes |
| the shim's exactly-once replay | the property is a real broken pipe: `delivered` advances only after `WriteLine` succeeds | A1 |
| the `\\.\pipe\` namespace | MEASURED: **8 of 192 reads over 1.5 s saw no pipe** while the shim was alive. **A fake namespace has no blink**, and the blink is why `LaneLiveness` is a union | A5 |
| the process launcher | `Projects.PromptDirMismatch` compares the prompt's *stated* dir against the real `ProcessStartInfo.WorkingDirectory` | A6 |
| the forge | `REVIEW-AND-MERGE-PLAN:546`: *"mocking a forge into the daemon would be testing the mock"* | — |
| WPF itself | with `AcceptsReturn` the TextBox class handler eats Enter before an instance `KeyDown` | F2 |

**And one thing is deleted rather than faked** (D-T11): the daemon control pipe.
`Daemon.HandleAsync(string req, StreamWriter w)` (`Daemon.cs:981`) is the entire 45-case command
surface and its only dependencies are a JSON string and a `StreamWriter`. A `StreamWriter` over a
`MemoryStream` **is not a double** — it is the real handler with nine lines of pipe server
(`Daemon.cs:957-975`) not present. No fake, no anchor, nothing to drift. *A transport you can leave
out beats a transport you fake.*

### 3.6 The doubles that exist

| double | assembly | anchor, as corrected | notes |
|---|---|---|---|
| `RecordingLaneSink` (new) | `Dodona` | `Interface` **+** `Contract = LaneSinkContract` | the flagship, and the only one that satisfies the corrected `Interface` rule outright: strip the double and `Store` **and** `ConciergeStore` both remain, and both ship. Seam cost: `HandleShimLine` `private`→`internal`, one keyword |
| `FakeRecognizer` (exists) | `DodonaUi` | `Interface` **+** `Contract = RecognizerContract` **+** `KnownDivergence` | **RED on day one under the corrected rule**, deliberately: strip the double and `IRecognizer` has ONE implementer. The contract closes the arrival half against `DeepgramRecognizer` at a closed loopback port; the sync-vs-async timing half is a permanent counted divergence with an issue (§3.2) |
| `Poses` (exists) | `DodonaUi` | `Interface` **+** `Contract` | it builds the real `Snapshot`, so the contract subject is the type production builds. Gains `Poses_cover_every_snapshot_member` |
| `RecordingTransport` (new) | `DodonaUi` | `Landing` | weakest; `Wire` mandatory; §8.4 risk 10 stands |
| `DodonaFakeAgent` (exists, **unchanged**) | its own exe | `Corpus`, **declared in `double-assemblies.tsv`, no attribute** | a program, not a type (D-T27). Stays a real process on the real shim and the real `LaneRuntime`; an in-process agent double would delete all three. D-B7 forbids sharing source with it, so the attribute is not linked in and the anchor is entirely file-based |
| injected `Func<string,bool>` fs probes | `Dodona` | **not doubles — arguments** | the `Trees.Locate` pattern (`Trees.cs:44` + the `:77` overload binding the real predicates). Production has exactly **one** path, which is the strongest anti-drift property in the document |

**Read that table as an argument for having fewer doubles.** One of five satisfies the corrected
`Interface` rule on its own merits; one is anchored by files rather than by types; one is
acknowledged weak; and the strongest row is the one that is **not a double at all**. D-T11 (delete a
transport rather than fake it) and the injected-predicate row are the shapes to reach for first, and
§3.6 should get shorter over the life of this job rather than longer.

**A double's default must be the answer that makes the interesting path run, never the quiet
successful one.** `DodonaFakeAgent` already does this deliberately (router defaults `unclear`, both
concierge tiers default `none`/`low`, and `Program.cs:51,69` say why: *"a fake agent that guessed
would hide the one path most worth testing"*). `LANE-LIFECYCLE` rejected "default an unclassified
tool to silence" for the same reason — *"it reads as tidy and is the same defect wearing a
fallback."*

**A stand-in must never be able to look like the real thing.** `FakeRecognizer` reports
`Engine = "none"`, not `"fake"`, when it replaced a real engine that failed (`Recognizer.cs:87-90`),
so `ui dump` can never make a missing engine look installed. Every new double reports its own
identity in whatever the test observes.

### 3.7 The clock

**No `IClock`** (D-T12). Time enters as a `DateTime nowUtc` parameter with a defaulted overload
binding `DateTime.UtcNow` — the house pattern, and `Poller.Liveness` (`Poller.cs:65`) *already takes
`now`*. `Store` gets one optional `Func<DateTime>? clock` constructor parameter used at its four
timestamp sites (`Store.cs:720`, `:1404`, `:1462`, `:1499`).

That **deletes** `m1:expired_lease_cannot_land`'s `Start-Sleep -Seconds 2` — the suite's only real
one — by removing the need for elapsed time rather than shortening a deadline. (`check-authoring`
forbids shortening a deadline to make a proof faster, because a check that merely passes slowly then
reads as PROVEN. Deleting the wait is a different act and is the allowed one.)

**And the honest half, which must not be mis-sold: a controllable clock does not touch the measured
flakiness.** Issue #3's reds are `Wait-Until` deadlines expiring because a real window, daemon or
process did not answer inside 20 s **under load**. No clock makes a real window paint faster. §1.4's
table settles it — `unit` is the only suite that did not move between busy and idle. The only lever
on the surviving integration flake is **fewer of them**.

---

## 4. THE WORK ITEMS

Twelve items, one phase, ordered so **the approach is proved cheaply before anything irreversible**.
W1–W4 delete nothing; between them they add one `internal` attribute with no logic in it, one
`Compile Include` link, and one empty test project. W5 is the kill switch. **Wave 1 inside W8 is a
second decision point** (D-T31), and W6's irreversible rename now sits after it. Nothing fans out
until W5 has run end to end.

Costs are **estimates** in agent-sessions and are labelled as such. No wall-clock measurement was
taken for any of them.

---

### W1 — The wire register

**Changes.** Three things, and the first is not optional.

**W1.0 — COMMIT THE SURVEYS.** `docs/testarch/{survey-daemon,survey-delivery,survey-identity,survey-view,survey-window,survey-ask,seams,prior-art,design-wires,design-fakes,design-migration}.md`,
moved out of a session scratchpad into the repo. They are the per-check authority this plan cites on
every page, §8.4 risk 1 makes them the only defence against the residual risk of the whole job, and
they currently live where CLAUDE.md §0 says knowledge may not live. Nothing else in W1 is worth doing
first.

**W1.1 — THE CROSS-SUITE DUPLICATE SCAN, before anything captures a baseline.** One
`sort | uniq -d` over every `Check '<name>'` plus `m0`'s inline `$results['<name>']`. It has been run
once already (§1.2): two real cross-suite collisions and one false positive. Rename
`m2:presence_idle_after_result` → `presence_idle_after_result_in_status` and
`concierge:double_uncertainty_asks_the_operator` → `group_double_uncertainty_asks_the_operator`, each
with a `renamed` ledger row. **This is W1 work and not W6 work**: `baseline.tsv` is keyed on the check
name (§5.2), so repo-wide uniqueness is a precondition of the ledger existing at all, and a collision
discovered later shows up as a `$results` row silently overwritten and a tally one lower — which is
exactly the failure §8.2 sells `dev ledger` on being able to catch.

**W1.2 — the register.** `tests/ledger/wires.tsv` (new, tracked). One row per distinct wire
repo-wide: `wire_id / owner_suite / owner_check / owner_body_sha / what_it_proves /
why_real_machinery`. `owner_body_sha` is empty until W7 creates the `Wire` block it hashes (§3.3.1).
Built by hand-merging
the six survey `wires` arrays and collapsing the cross-group duplicates the surveys already named:
the gate-hook subprocess wire (m1 owns, m2 re-proves), the live-window render wire (ui-grid/m3 own,
compression + workspace re-prove), the real-git-mutation wire (four rows across m1/m2/m3/workspace —
survey-identity: *"three distinct production paths but ONE kind of machinery"*), the
child-agent-spawn wire (five claimants), and `no_process_left_in_the_build_output` (15 rows, harness
hygiene, **not** deduplicable).

**Files.** `docs/testarch/*.md` (moved, tracked), `tests/ledger/wires.tsv`,
`tests/ledger/README.md`, and two renamed checks in `m2-acceptance.ps1` / `concierge-acceptance.ps1`.

§2.2's **52** rows are the expected output. The surveys list **109** candidate rows before dedup.

**Verification.** `dev lint` (the surveys are now tracked `.md`, so I8's line-ending and control-byte
rules apply to them). Otherwise reviewed by reading: every `owner_check` must exist today (`grep` in
`tests/*.ps1` — all 49 of the first draft's did, checked one by one during the review, and the three
added rows are checked the same way), and every candidate row from the six surveys must be either an
owner or named in a fold reason.

**Cost.** ESTIMATE: one session. A reading exercise over files that exist — no code, no build, no
suite run — plus two renames and a `git mv`.

**Depends on.** Nothing. **This is the first deliverable of the whole job**, and it is what makes
every later `merged` row legal.

---

### W2 — `dev ledger`, and the frozen baseline

**Changes.** A verb on `tools/dev.ps1` (D-3: dev.ps1 is the one door, no new tool):

```
dev ledger                  # STATIC, sub-second; its rungs are folded into Repo-Lint (I8), D-T23
dev ledger --live           # additionally consumes a just-finished run's results.json + TRX
dev ledger --capture        # freeze/extend baseline.tsv from a GREEN full run
dev ledger --slice <name>   # that slice's rows and their proof state
dev ledger --verdict        # the block in §5.5
dev ledger --origin <check> # git log -S over tests/: which commit deleted it, and its body
```

Plus `tests/ledger/baseline.tsv` (frozen, **958 names**: 750 suite checks + 208 unit methods — see
§1.2, the first draft said 1050 and that is the *case* count) and `tests/ledger/added.tsv` (declared
growth). Also `Run-Unit` (`dev.ps1:1054`) gains `--logger "trx;LogFileName=unit.trx"` — the ledger
needs per-test verdicts.

**`baseline.tsv` IS KEYED ON THE CHECK NAME, NOT ON `suite<TAB>check`** (§10, finding 5). As first
written, the frozen file's integrity property (*"refuses any run in which a baseline row was removed
or altered"*) and W6's rename of all fifteen suites were **mutually exclusive**: W6 alters the
`suite` key of all 750 rows, so W6's own verification (*"`dev ledger` green with zero moved/merged
rows"*) was unreachable. Keyed on the name, `suite` becomes an ordinary non-integrity-bearing column
that a rename updates freely, and the barrier can be scheduled on its merits rather than around the
ledger. That is what makes W1.1's uniqueness scan a precondition rather than a nicety, and it is what
lets W6 move to after wave 1 (§10, finding 20). **Keep the existing `[FAIL]` grep and the tally-is-authority rule exactly
as they are**: `dev.ps1:1071` records that the first version of that function printed *"54 checks,
1 failed"* and returned exit 0.

**Files.** `tools/dev.ps1`, `tests/ledger/*.tsv`, `tests/ledger/README.md`.

**Verification.** Prove each assertion by breaking it: remove a `baseline.tsv` row → refused;
duplicate a check name in a suite → refused; point a `moved` destination at a method that does not
exist → refused. Record the literal refusals in `README.md`. Then `dev gate` once, green, and
`dev ledger --capture` from that run.

**Cost.** ESTIMATE: one to two sessions. ~300 lines of PowerShell, plus one green gate.

**Depends on.** W1.

---

### W3 — `dev prove --with <patch>`, and the second test project

**Changes.** The answer to *"a pure test move is VACUOUS by construction"*, plus one piece of
infrastructure that was scheduled six items too late.

**W3.0 — CREATE `tests/Dodona.Ui.Tests` HERE, EMPTY BUT REAL** (net8.0-windows, `UseWPF`,
`InternalsVisibleTo` on `DodonaUi.csproj` — seam S6), with one trivial `[Fact]` in it. Two separate
findings force it before the kill switch (§10, findings 1 and 16):

- **The double ledger's rung 2 cannot see `DodonaUi` without it.** `FakeRecognizer`, `Poses` and
  `RecordingTransport` all live there, and `Dodona.Tests` is net8.0 — it cannot load a
  net8.0-windows assembly at all. Without this project W4's reflection rung covers one of the four
  double-bearing assemblies.
- **`dev prove --with` cannot redden a UI-side test without it.** `$projects` (`dev.ps1:1290`) is a
  hardcoded list of the four *product* projects, and W3's item 4 adds `Dodona.Tests` only. Four of
  the ten slices (`S-POLLER`, `S-UIVM`, `S-UIWIRE`, `S-ASK`) move checks whose destinations are UI
  types, so **falsifier 2 would fire for 40 % of the slices and the pilot could not detect it**,
  because the pilot moves nothing into a UI project.

**This does not contradict §7.3's rejection of `ui-shell` as the pilot** — it is the opposite of it.
That rejection was about debugging **new infrastructure at the same moment as new procedure**;
standing the project up in the tooling phase, empty, with one trivial fact, is how the pilot avoids
carrying two unknowns. W3's verification proves a red in a net8.0-windows test, so falsifier 2 is
fully tested rather than half-tested at the kill switch.

Six concrete deltas to `tools/dev.ps1`:

1. parse `--with <path>` out of `$Rest` before the suite:check loop (`:1144`);
2. skip the dirty-tree guard (`:1196`) when `--with` is given — the patch **is** the change, and at
   step B0 of a slice the tree is legitimately clean;
3. after the tests copy (`:1279`), `git -C $wt apply --check` then apply; **abort if the patch
   touches anything outside `src/`** — a mutant editing `tests/` would be the unit refusal's reason
   3 in a new costume;
4. `$projects` (`:1290`) gains `tests\Dodona.Tests\Dodona.Tests.csproj` and
   `tests\Dodona.Ui.Tests\Dodona.Ui.Tests.csproj` **only when a `unit:` / `ui-unit:` pair is
   present**, each only when its own prefix appears — a UI-only proof must not pay for a net8.0
   compile and vice versa;
5. the `unit` refusal (`:1181`) stays for the bare form and gains one line pointing at `--with`. Its
   three recorded reasons are each answered: a new symbol will not compile against HEAD → answered
   by the seam commit landing first; there is no `tests\unit-acceptance.ps1` (planned) (it does not exist and is not meant to; the name appears here only because `dev.ps1` records it as one of the reasons `dev prove` refuses `unit`) → answered by routing
   `unit` to `Run-Unit`; `Run-Unit` tests the working tree → answered by `Run-Unit -Root $wt`;
6. mutation patches live in `tests/mutants/<slice>-NN.patch`, checked in, with a parsed header:
   `# expects-red:` (one per check, both languages), optional `# expects-green:` (the over-broad-test
   detector), `# defect:` prose.

**Files.** `tools/dev.ps1`, `tests/mutants/` (new).

**THE `[Theory]` PROBLEM, AND IT IS SETTLED HERE RATHER THAN DISCOVERED AT W5** (§10, finding 17).
§9.4's B1 says *"variations become `[Theory]` rows"* and §5.2 says the final dotted segment of
`destination` must equal `old_check` character for character. VSTest's TRX `testName` for a theory
row carries the parameter list appended — `…session_id_recorded(x: 1)` — so **the plan's own B1
instruction breaks its own last-segment rule by design**, for every moved check that acquires a
variation. Two rules, decided here:

- **`destination` resolves against the theory's METHOD identity.** The ledger strips a trailing
  parenthesised argument list before matching, so `session_id_recorded(x: 1)` resolves to
  `session_id_recorded`.
- **Theory rows are attributed to their declaring method and never surface in `added.tsv`.** The
  unit side of `baseline.tsv` is keyed on the method (§1.2) with the case count as a separate
  column, so adding an `[InlineData]` row moves the case count and not the name census — which is
  what "coverage growth is visible arithmetic" should mean.

Whether the appended form is exactly `(x: 1)` is still **unverified on this machine** and is
falsifier 3; what is now certain regardless of format is that the runtime name set is **per row**,
because `Run-Unit`'s `Passed:` scrape is the case count and the parsed census reconciles only if
`[InlineData]` rows count individually (§1.2). So the method-identity rule is correct under either
format and only the stripping regex depends on the answer.

**Verification.** Prove ONE existing check red under ONE mutant **before any new test is written**.
Prove ONE red in a `net8.0-windows` test in `Dodona.Ui.Tests` under a mutant touching
`src/DodonaUi/`. Verify the TRX `testName` format for a `[Fact]` **and for a `[Theory]` row** on this
machine and record both literally in `tests/ledger/README.md`.

**Cost.** ESTIMATE: one to two sessions — W3.0 is a csproj, an `InternalsVisibleTo` and a build.

**Depends on.** W2 (shares the TRX reader).

---

### W4 — The double ledger

**Changes.** The two rungs of §3.2, and one measurement that used to sit three items past the kill
switch.

- **`src/Dodona/Testing/DoubleAttribute.cs`** — the attribute + `Anchor` enum, `internal`, no
  behaviour, `Compile Include`-linked into `DodonaUi.csproj` beside the twelve files it already
  links. **Not** into `DodonaFakeAgent` (D-B7, D-T27).
- **Rung 1, in Repo-Lint** (`tools/dev.ps1`): the four static assertions of §3.2 over `src/**/*.cs`
  and `tests/**/*.cs`, plus `tests/ledger/double-assemblies.tsv`. This is where the population
  question is answered, because a text scan cannot miss an assembly and runs on a tree that will not
  compile.
- **Rung 2, one reflection test class per double-bearing assembly**:
  `tests/Dodona.Tests/Doubles/DoubleLedgerTests.cs` over `typeof(Store).Assembly`, and the same body
  in `tests/Dodona.Ui.Tests/DoubleLedgerTests.cs` over `typeof(MainVm).Assembly` — an explicit
  assembly list, never `GetExecutingAssembly()`. Facts:
  `Every_Interface_anchor_has_two_shipping_implementers` (excluding `[Double]`-carrying types, and
  **printing the survivors by name**), `Interface_is_never_a_sole_anchor`,
  `Every_double_names_a_wire_that_still_exists`.
- **`RecognizerContract`** — the day-one red's answer (§3.2): two subclasses, `FakeRecognizer` and
  `DeepgramRecognizer` against a closed loopback port, asserting *exactly one of `Ready`/`Failed`,
  exactly once*. No network, no microphone, no quota.
- **`CheckLedger.cs`** reads the **tracked TSVs** and never parses a `.ps1`.
- **THROWAWAY `StoreFixture` + `GitRepoFixture`, MEASURED AND THEN DELETED** (§10, finding 19).
  Falsifier 4 — *"`dev test unit` blows the operator's 1–2 s"* — depended on fixtures the first draft
  scheduled in **W8**, three items past the kill switch and after W6 had already rewritten every
  suite file. They are ~50 lines each and delete cleanly, so they are built here, `dev test unit` is
  measured with them present, and they are reverted. **The threshold is restated: warm, after a
  build, as `dev test unit` prints it** — CLAUDE.md §1 gives 1.9–2.3 s warm and ~5.6 s on the first
  run after a build, while §1.4's table shows 5.3 s *inside a gate*, which is the cold figure. The
  first draft's *"if the total exceeds ~4 s, split"* compared against the cold number and had
  therefore already fired before any work began.

**That last clause is the important one and it is D-T6.** Two independent parsers of the same check
names — one in PowerShell for `dev ledger`, one in C# for the `Wire` field — would be two hand
copies of one thing, which is the exact failure this plan exists to prevent. So there is **one**
parser: `dev ledger` (PowerShell, because `tools/dev.ps1` must run on a tree that will not compile),
which validates the sources and maintains the tracked TSVs; and the C# side **reads the artefact**.
The staleness that arrangement could hide — a check deleted from a `.ps1` while its TSV row survives
— is caught by `dev ledger`'s reachability rung, which runs inside Repo-Lint (I8) and so is asserted
by the gate without adding an eleventh assertion (D-T23). Loop-generated names are the one exception
and §5.4 states it: they are reachable on the `--live` side only.

**Files.** `src/Dodona/Testing/DoubleAttribute.cs`, `src/DodonaUi/DodonaUi.csproj` (one
`Compile Include` link), `tests/Dodona.Tests/{CheckLedger.cs, Doubles/*.cs}`,
`tests/Dodona.Ui.Tests/{DoubleLedgerTests.cs, RecognizerContract.cs}`,
`tests/ledger/double-assemblies.tsv`, `tools/dev.ps1` (Repo-Lint rung 1).

**Verification — eight reds, each seen, each recorded literally**, in doc comments and in
`tests/ledger/README.md`, because `dev prove` refuses `unit`:

1. add an unanchored `FakeThing` **in `src/Dodona/`** → rung-1 red naming the file. *(The first
   design could not produce this red at all: `src/` was outside its population.)*
2. add one in `src/DodonaUi/` → the same, from the same scan.
3. add a `[Double]` in `src/DodonaShim/`, which no rung-2 test loads → **assertion 4** red, naming
   the missing `double-assemblies.tsv` row.
4. name something `MockThing` → rung-1 red.
5. anchor a new fake `Interface` where the only other implementer is itself a `[Double]` → rung-2
   red printing the one surviving implementer.
6. anchor `FakeRecognizer` `Interface` with no `Contract` and no `KnownDivergence` → rung-2 red.
   **This one is red against the tree as it stands**, which is the mechanism proving itself against
   code nobody wrote for it.
7. rename a wire check → rung-2 red naming the double.
8. write a `KnownDivergence` with no `Issue` → rung-1 red.

Then: `RecognizerContract` green on both subclasses; `dev test unit` and `dev test ui-unit` timed and
the numbers written down; the throwaway fixtures reverted.

**BUILT 2026-08-22, in one session.** All eight reds seen and recorded verbatim in
`tests/ledger/README.md` and in the checks' own comments; red 6 was red against the untouched
tree, and so was a ninth nobody asked for (the implementer count, which is what forced
`SeamOnlyInterface` — see §3.2). `RecognizerContract` is green on both subjects. Falsifier 4 does
NOT fire: `dev test unit` warm is **1.8–1.9 s** at 304 cases and **2.8 s** with the throwaway
fixtures and 312, i.e. **+0.9 s for eight fixture-bearing cases** — 56 ms per real `Store`, 183 ms
per real git repo — and the fixtures are reverted. Two decisions the plan left to W4 were taken and
are recorded with their measurements: **the fold into Repo-Lint is done and `dev gate` still
reports ten assertions**, and **`ui-unit` joined `AllSuites`** (4.4–4.5 s warm, still solo).
`Poses` is NOT anchored and that is named as a gap rather than left to be discovered.

**Cost.** ESTIMATE: **two sessions**, not one. It was one when the mechanism was a single ~120-line
reflection test; it is now a lint rung, two reflection classes, a contract with a real second
subject, and eight proved reds. Production change is still ~20 lines of attribute with no logic in
it.

**Depends on.** W1, W2, W3 (`Dodona.Ui.Tests` must exist before rung 2 can cover `DodonaUi`).

---

### W5 — THE PILOT SLICE `S-WIRE`. **This is the kill switch.**

**Changes.** The shim wire parser, end to end through the entire ritual, by one agent with nothing
else running.

- Commit A (seam): `LaneRuntime.HandleShimLine` (`LaneRuntime.cs:95`) `private` → `internal`.
  **One keyword.**
- Commit B (move): `RecordingLaneSink` + `Contracts/LaneSinkContract.cs` over the real `Store` and
  the double; `src/Dodona/WireShape.cs` (the `(type, subtype, content-block-type)` tuple the parser
  already switches on, declared once so parser, corpus test and expectation files key off it);
  `tests/assets/wire/` seeded from `spikes/spike1-output/wire.jsonl` **with its BOM**;
  `WireCorpusTests`; `DodonaFakeAgent --wire-sample <path|->`; `dev wire-sample`; **lint row I9**
  (regenerate and compare, `SKIPPED (not built)` when the binary is absent, because lint must run on
  a tree that will not compile); `FakeShapeTests` + `MANIFEST.json`'s `unwitnessed[]`.
- **The moved checks — FOUR, not eight, and the correction matters because eight was the number
  used to argue the ritual is runnable** (§10, finding 6). Three of the first draft's eight
  (`m0:a_missing_shim_is_named_not_guessed`, `m0:a_failed_spawn_is_recorded`,
  `m0:a_failed_spawn_leaves_no_lane_claiming_alive`) assert on `Daemon.AttachShimAsync`'s
  spawn-failure branch — verified at `src/Dodona/Daemon.cs:4113` (`"shim binary not found: {shimExe}"`)
  and `:4120`/`:4127` (`_store.Event("shim_spawn_failed", …)`) — and `survey-daemon.md` blocker 8 says
  so for each: *"the branch is inside a private `async Task` that also calls `Process.Start` — no seam
  for the spawner."* A fourth, `compression:blocked_uses_the_fixed_schema`, is behind blocker 2:
  `Daemon.CompressResult` has no seam and needs a `Compression.Render(headline, needsYou, options)`
  extraction. **None of the four is reachable through `HandleShimLine`, and §3.5 forbids faking the
  process launcher**, so neither could be moved without a second seam in `Daemon.cs` — which is
  exactly what the pilot exists not to need.

  The seed is: `m0:session_id_recorded`, `compression:midturn_narration_is_still_a_row`,
  `compression:progress_rows_are_written` (all three through the one keyword), and
  `compression:raw_body_is_never_overwritten`, which is a pure `Store` question — one insert, one
  `PaneCompressed`, read both columns — needing **no seam at all** because `Store(string path)` is
  public on an internal class and `Dodona.csproj` already grants `InternalsVisibleTo("Dodona.Tests")`.

  **The four that were cut become the pilot's first `no-seam-yet` rows**, each naming the seam it
  would need (`AttachShimAsync`'s spawner; `Compression.Render`). That is not a consolation prize: it
  rehearses the closed reason vocabulary, the separate `no-seam-yet` count and the issue-number rule
  on real rows, which the first draft's seed would never have exercised.

  **And it makes falsifier 4 partly readable at the kill switch**, because
  `raw_body_is_never_overwritten` needs a temp-file `Store` — the first disk-touching unit fixture
  this repo will have. Combined with W4's throwaway measurement, the unit budget is a reading before
  the barrier rather than after it.

**Why this slice and not a bigger prize.** Six reasons, and rejected alternatives are in REJECTED:

1. **The cheapest seam in the tree: one keyword.** Nothing that cannot be undone with `git revert`.
2. **Its double cannot drift, by construction rather than by promise** — `ILaneSink` has two
   production implementers, so a recording sink is a third and the compiler drags it along. That is
   Constraint 2 satisfied by the compiler, which is the standard everything else must meet.
3. **It forces the plan's hardest problem first, on its smallest instance.** The named drift risk in
   the whole tree is `DodonaFakeAgent` — 545 lines hand-writing `claude`'s stream-json, no shared
   constant, no schema, no contract test, hand-duplicated against `LaneRuntime.cs:99-230`, checked
   against it by nothing. **If the corpus mechanism cannot be built here, the plan should die here**,
   cheaply, having deleted nothing.
4. **The material is tracked and costs no quota.**
5. **It exercises every mechanism the plan invents** — `moved`, `merged`, `stays` and
   `stays (no-seam-yet)` on real rows, a mutant with `expects-red` on both sides, `dev prove --with`
   over a `.ps1` and a `unit:` pair, the TRX name format, `dev ledger --live`, a `wires.tsv` row, and
   the unit-budget reading.
6. **It is small — FOUR moved checks and four `no-seam-yet` rows**, corrected from "eight checks",
   which had not been checked against the code (§10, finding 6).

**Verification.** The full ritual of §9.4, plus: the `dev ledger` static row is added to the gate in
this item and seen red once.

**Cost.** ESTIMATE: two to three sessions.

**Depends on.** W2, W3, W4.

**STOP HERE AND REPORT** if any of the four falsifiers in §8.3 fires.

---

### W6 — The suite rename and consolidation (one commit, a barrier)

**Changes.** 15 acceptance suite files become 10, named for what they cover: `lifetime`, `gate`,
`land`, `review`, `brain`, `assist`, `window`, `shell`, `publish`, `identity`. Check **bodies move
as bytes**; no name changes, no assertion changes. Plus `AllSuites` (`dev.ps1:51`),
`SuiteOrderHint`, `SoloSuites`, `dodona.json`'s `//verify`, CLAUDE.md's suite table, `docs/`
references and `.claude/skills/ship`.

**This is the one act in the whole job where `3b235ab`'s mechanism is valid**, and it should use it:
the ui-use split proved faithfulness by diffing sorted check names from a green run — *"130 names
in, 130 identical names out"* — and that works **because bodies moved as bytes, verified
line-for-line**, so an equal name meant an equal assertion. That is exactly true here and nowhere
else in this plan.

**It must land in ONE commit**: I8's dangling-`tests\*.ps1`-reference lint turns red on every stale
doc reference, which is a forcing function rather than an obstacle.

**It is a BARRIER.** It touches every suite file, so no slice may be in flight.

**IT MOVES TO AFTER WAVE 1, and the first draft's placement was wrong twice over** (§10, findings 5
and 20). Wrong once because a `suite<TAB>check` baseline key made the rename and the frozen file
mutually exclusive — fixed by keying on the name (W2). Wrong again because the rename is the single
most irreversible piece of churn in the job — fifteen suite files, `AllSuites` (`dev.ps1:51`),
`SuiteOrderHint`, `SoloSuites`, `dodona.json`'s `//verify`, CLAUDE.md's suite table, every `docs/`
reference and `.claude/skills/ship` — and it was scheduled **before a single bulk slice had been
attempted**, so it would have been paid before anyone learned whether a Store-backed or a UI-backed
slice is tractable. Those are precisely the two unknowns §7.3 deliberately keeps out of the pilot.

The stated reason for the early placement was I8's dangling-reference lint forcing one commit. That
argues for **one commit**, not for **early**. Wave 1's slices declare the old suite names; the rename
lands after wave 1 with one real bulk slice of evidence behind it, and the barrier property is
unchanged.

**Files.** All of `tests/*-acceptance.ps1`, `tools/dev.ps1`, `dodona.json`, `CLAUDE.md`, `docs/*`,
`.claude/skills/ship/SKILL.md`.

**Verification.** `dev gate` green; `dev ledger` green with **zero** moved/merged rows (nothing
moved layer, only files); `dev lint` green; the name-list diff, printed in the commit message.

**Cost.** ESTIMATE: one to two sessions, mostly mechanical churn.

**Depends on.** W5 **and wave 1 of W8**.

---

### W7 — The harness invariants and the `Wire` block

**Changes.** In `tests/_workspace.ps1`:

- **`no_modal`** promoted from `voice:no_modal_when_the_mic_fails` into every suite's `finally` for
  every suite that opens a window. Counting top-level windows owned by the UI pid is **the only
  detector in the repo that works**: measured 2026-08-20, a real `MessageBox.Show` was up and
  `ui-ask:the_ask_is_not_a_modal` **passed**, because a Win32 modal pumps its own nested message loop
  and the dispatcher keeps answering. That check keeps its name and is rewritten onto the counting
  mechanism.
- **`no_socket_from_the_ui_process`** promoted from `voice`'s egress pair. `Get-NetTCPConnection` on
  the real UI pid. Its recorded red was `state=[listening] engine=[deepgram]` — a test run
  authenticating on the operator's own bill. It moves next to `DODONA_UI_MIC=off` where a config
  change cannot disarm it.
- **`Wire '<id>' { ... }`** helper: a `Check` inside a `Wire` block is an integration assertion; a
  `Check` outside one is a lint failure. Repo-Lint gains: every `wires.tsv` id appears in exactly one
  suite, and every `Wire` id in a suite is in the registry.

That last row is what makes *one wire, one integration test* **enforcement** rather than a sentence:
a second fixture for an existing wire cannot be added without editing the registry, which shows in
the diff. It caps **fixtures** and deliberately leaves assertions uncapped.

**Files.** `tests/_workspace.ps1`, `tools/dev.ps1` (Repo-Lint), the 10 suite files.

**Verification.** Put a `MessageBox.Show` on a failure path → `no_modal` red. Add a second `Wire`
block with an id another suite owns → lint red. `dev gate` green.

**Cost.** ESTIMATE: one session.

**Depends on.** W6. (W7 also populates `wires.tsv`'s `owner_body_sha` column, §3.3.1 — the hash has
nothing to hash until the `Wire` block exists.)

---

### W8 — The slices: the bulk of the work

**Changes.** Nine more slices plus one seam-only barrier, each in its own worktree, run in waves.
Every slice declares the **suites it owns exclusively** and an **explicit list of `src/` files** — no
wildcards (see below); two slices may run concurrently only if BOTH sets are disjoint.

**WAVE 0 — `S-DAEMONSEAM`, a seam-only barrier, and it replaces the wave-5 bottleneck.** The review
found that slice boundaries had been drawn from **suite names** while the seams the surveys actually
name cluster in one file (§10, finding 10). Counted 2026-08-21: **57 lines across the six survey
files reference `src/Dodona/Daemon.cs`** as the seam site — that is a line count and not a row count,
and nobody has counted the rows, but the structural fact needs no precision: `Daemon.cs` is the
busiest seam site in the tree and the first draft let exactly two slices touch it, one of which ran
**last and alone**. Every check belonging to another slice's suite but seamed in `Daemon.cs` was
therefore structurally blocked until wave 5 or forced to `no-seam-yet` — while §9.10 makes
`stays (no-seam-yet) = 0` a stop condition. The plan would have deadlocked against itself.

`S-DAEMONSEAM` is **commit A with no commit B**: `src/Dodona/Daemon.cs` only, seams only, no test
moves, no ledger rows. It lands S3 (`Daemon` ctor + `HandleAsync` `internal`) and the extractions the
surveys name — `RouteInput`'s rungs below `LanePrefix`/`IsObviousGeneric` (blocker 3),
`Compression.WorthCompressing` + `Compression.Render` (blocker 2), `AttachShimAsync`'s spawner as an
injected argument (blocker 8), `LandAskText`'s two store reads, `AskForRepo`/`AskWhichProject`, and
the `branch_touched` assembly at `Daemon.cs:1816` (blocker 4). Its whole verification is
`dev build` + every suite green: it changes no behaviour and asserts nothing new.

**And `S-DAEMONCMD` is ABOLISHED.** Its only content was S3 plus "move the command-shape checks",
and it could not declare a disjoint suite set because `HandleAsync` is 45 `case` labels reaching
every suite. With the seam landed in wave 0, **each slice moves its own suite's command-shape
checks**, which is what exclusive suite ownership already means. §8.4 risk 6 dissolves with it.

| slice | seam(s) | suites owned | `src/` files, explicitly |
|---|---|---|---|
| `S-DAEMONSEAM` (wave 0) | S3 + the six survey-named extractions above | **none — no test moves** | `Dodona/Daemon.cs` |
| `S-POLLER` | S4 `Poller.Liveness`/`QuotaLine` internal; S7 `IStoreView` (~15 lines) | `window` | `DodonaUi/Poller.cs`, `DodonaUi/StoreReader.cs` |
| `S-PUBLISH` | publish target resolution + verdict fold out of `Program.cs:1275-1364`; `Ver.Parse` | `publish` | `Dodona/Program.cs` (publish paths), `Dodona/Ver.cs` |
| `S-GATE` | S11 `GateHook` + `ParseArgs` out of `Program.cs`'s top-level statements | **`gate`** | `Dodona/Program.cs` (gate paths) |
| `S-STORE` | S2 `:memory:` guard at `Store.cs:33`; temp-file `StoreFixture`; the `Store` clock parameter | **`land`** | `Dodona/Store.cs` |
| `S-IDENTITY` | S5 `Registry(string path)` overload (2 lines); S10 injected fs probes at five sites | `identity` | `Dodona/Workspaces.cs`, `Dodona/Repos.cs`, `Dodona/Fence.cs`, `Dodona/Git.cs`, `Dodona/LaneLiveness.cs` |
| `S-UIVM` | S9 `Dump()` → `MainVm.DumpObject()` (S6's project already exists, W3.0) | `shell` | `DodonaUi/Vm.cs`, `DodonaUi/Shell.cs`, `DodonaUi/Poses.cs` |
| `S-UIWIRE` | S8 transport delegate (1 field + 5 call sites, **including `Ensure`/`EnsureConcierge`**) | `review` | `DodonaUi/MainWindow.xaml.cs`, `DodonaUi/DaemonClient.cs` |
| `S-ASK` | the pending-tail range out of `MainWindow`; the ask-text seams landed by wave 0 | `assist`, `brain` | `DodonaUi/ConciergeReader.cs` |
| `S-SHIM` | S12 shim buffer/lease/drain into a class over `(readChildLine, writeClient, nowUtc)` | `lifetime` (2nd pass) | `DodonaShim/Program.cs` |

**`S-GATE` and `S-STORE` swapped suites** (§10, finding 10, the small half). As first written,
`S-GATE` created the `GateHook` seam and owned `land`, while the `gate` suite — every `GateHook` deny
check, and B1/B2 — belonged to `S-STORE`; under §9.6 rule 4 the agent with the seam could not move
the checks and the agent with the checks had no seam. The rule that prevents the next instance is
below.

**`DodonaUi/*.cs` was a WILDCARD over four other slices' declared files** and is now written out.
`Poller.cs`, `StoreReader.cs`, `MainWindow.xaml.cs` and `DaemonClient.cs` all live in
`src/DodonaUi/`, so `S-UIVM`'s original declaration covered three other slices' work. **No slice
declaration may contain a wildcard**, because §9.6 rule 4 — *"anything outside that set is a
STOP-and-report"* — cannot be applied to one.

**Where two slices genuinely must touch one file, they are SEQUENCED and named as such, never run in
one wave.** There is exactly one instance: `S-PUBLISH` and `S-GATE` both edit
`src/Dodona/Program.cs`, in disjoint regions (the publish paths; the top-level gate statements).
They are waves 1 and 2, the later one rebases, and §8.4 risk 6 already carried this. That is the
whole exception list — if a second one appears, it is a sign the slice boundaries are drawn wrong
(D-T29) rather than a licence to widen the rule.

Four scheduling rules on top of disjointness:

- **SEAMS DECIDE OWNERSHIP, NOT SUITE NAMES.** A slice may not be handed a check whose seam site
  falls outside its own declared `src/` set. The parent publishes a **seam → slice map** beside the
  suite → slice map and checks every movable survey row against it before wave 1; a row whose seam
  falls elsewhere is either reassigned or declared `no-seam-yet` **up front**, never discovered
  mid-slice.
- **At most one slice per wave may own a WINDOW suite** (`window`, `shell`, `assist`, `brain`).
  Issue #3 is unrooted and `voice` showed the signature **while solo**, so two agents verifying
  window suites at once manufactures exactly that false red — and a false red costs as much as a
  false green.
- **THE `Compile Include` RULE, which is about MERGE order rather than concurrency.**
  `src/DodonaUi/DodonaUi.csproj` has **no `ProjectReference` to Dodona**; it links thirteen Dodona
  source files in (`:42-98`: `Instance`, `Paths`, `Workspaces`, `WorkspaceResolve`, `Repos`,
  `Claims`, `Projects`, `Ask`, `PaneProgress`, `Dictation`, `SpeechStream`, `Pcm16`, `Ver`). So
  `S-IDENTITY`'s `Workspaces.cs`/`Repos.cs` edits **compile into `DodonaUi.exe`**. This is *not* a
  concurrency hazard — each slice has its own worktree with its own `bin` and `obj` (CLAUDE.md §0.0),
  so nothing recompiles under anybody — but it is a **merge-order** one: a window suite verified
  green before a linked-file change merges was verified against a `DodonaUi.exe` that did not contain
  it. **So: when a slice touching any of those thirteen files merges, the parent re-runs `window` and
  `shell` on the merged result**, alone, before accepting the next slice. Cheap, and it is the honest
  residual.
- **Wave 0 is a barrier**, like W6: no slice in flight across it.

A workable schedule:

```
wave 0   S-DAEMONSEAM                        (alone, seam-only, no test moves)
wave 1   S-POLLER | S-PUBLISH | S-STORE      (S-POLLER owns the wave's window suite)
--- W6, the rename barrier, lands HERE, with wave 1's evidence behind it ---
wave 2   S-IDENTITY | S-GATE                 (no window suite; re-run window+shell after S-IDENTITY merges)
wave 3   S-UIVM | S-SHIM                     (S-UIVM is the window suite)
wave 4   S-UIWIRE | S-ASK                    (S-ASK owns brain/assist; S-UIWIRE owns review)
```

Wave 4 breaks the one-window-suite rule as written (`S-ASK` owns `brain` and `assist`, `S-UIWIRE`
owns `review`) — `review`'s F6 fixture is a completion record and a manager, not a window, so the
rule is satisfied in substance. **Say which suites in a wave actually open a window when the wave is
scheduled**, rather than reading it off the suite name.

**Verification.** Per slice, §9.4's ritual; per wave, the parent's acceptance gate in §9.7.

**THE MEASUREMENT GATE ON `dev test unit` — the FIRST reading is taken at W4, before the kill
switch, and this is where it is repeated.** The operator's requirement is 1–2 s (`LOCATIONS-PLAN`
P1.5 restates it) and every second is serialized in front of the wave while `unit` stays solo. The
fixtures that threaten it touch disk (`StoreFixture`) and start processes (`GitRepoFixture`), and the
first draft scheduled both **here**, three items past the kill switch — so falsifier 4 could not fire
at the kill switch at all (§10, finding 19). W4 now builds both as throwaways, measures, and reverts;
W5 gets a second reading for free because `raw_body_is_never_overwritten` needs a real temp-file
`Store`. Here the readings are taken for real: after `StoreFixture` lands, and after `GitRepoFixture`
lands.

**The threshold, restated with its conditions, because as first written it had already fired.**
*"If the total exceeds ~4 s"* was silent on warm-vs-cold, and §1.4's own measured table shows `unit`
at **5.3 s busy / 5.4 s clean inside a gate** — the cold, first-run-after-a-build figure. CLAUDE.md
§1 gives **1.9–2.3 s warm** and ~5.6 s cold. So: **the number is `dev test unit`'s own printed
seconds, WARM, on a second consecutive run after a build. If that exceeds ~4 s**, split into two
suite names over the SAME project using xunit traits — `dev test unit` = `--filter Category!=Fixture`,
`dev test fixtures` = `--filter Category=Fixture` — and add `fixtures` to `AllSuites` and
`SuiteOrderHint`. One project, one compile, two doors. **Record whichever branch was taken, with the
number, beside `AllSuites`.** `GitRepoFixture` is shared per xunit collection and reset with
`git reset --hard` + `git clean -fd` between tests, because a fresh `git init` per test is many
process starts.

**Cost. THE TWO NUMBERS IN THIS PLAN DISAGREE BY 5–9× AND THE FIRST DRAFT ASSERTED BOTH** (§10,
finding 8). W5 is **4 checks in an ESTIMATED two to three sessions** — 1.5–2 checks per session. W8
is **~560 checks in an ESTIMATED 20–40 sessions** — 14–28 checks per session. At the pilot's rate the
bulk is **280–370 sessions**. Nothing reconciled them, and every per-check obligation is *heavier* in
the bulk than in the pilot: D-T4 says each is a rewrite, each needs a mutant that reddens both
languages, two verbatim reds, and a nine-field ledger row.

The 20–40 figure assumes a **7–19× speedup from batching**, and the case for some speedup is real —
a pilot pays every first-use cost of the tooling once, and one mutant is meant to redden ~11 checks
at a time (`break Claims.Covers once and every reader reddens in one run`). The case for *that much*
speedup is not made, and two costs are missing from it entirely:

- **Mutant authoring and the re-cut loop.** ~50 mutants over ~560 checks is ~11 checks per mutant,
  and B2 requires **one** mutant to redden all eleven in C# while triggering no `expects-green`. That
  partition will not hold on the first cut, and re-cutting is unbudgeted.
- **Machine time, which is not agent time.** Each `dev prove --with` builds its own baseline and runs
  a full suite. At this repo's own measured suite times (`workspace` 74–87 s, `brain` 79–95 s) plus
  two builds, ~100 batched prove invocations is **hours of wall clock** appearing nowhere in the
  estimate.

**So the plan states both rates and asks the operator to fund neither yet.** The number that decides
is measured, not argued: **wave 1's actual checks-per-session, reported by the parent after three
slices.** That is a second, cheap decision point after the kill switch, and it is the honest place to
choose between "continue" and "keep the seams and the ledger and stop widening" (§8.3's far-end
falsifier). What is certain: **this is weeks at best, and possibly months**, and W6 now sits after
wave 1 precisely so the irreversible churn is not paid before that number exists.

**Depends on.** W7. (W6 lands *inside* this item, after wave 1.)

---

### W9 — Closing the fixture fold

**Changes.** After the last slice, every `wires.tsv` row has exactly one owner and every folded
candidate is a `merged` row naming a live survivor. Any wire that acquired a second fixture during
the migration is reconciled here, and the five **mis-aimed** checks are fixed while moving, each in
its own recorded row (D-T22):

- `m0:landed_exactly_once` — the fixture never creates a redelivery (daemon 1 died before the result
  line existed), so the check cannot fail. A1's fixture kills the daemon **after** the result line
  exists but before it is acknowledged, which makes the name true. The `UNIQUE(lane_id, seq)` dedup
  itself becomes a two-insert contract case, free and strictly stronger.
- `compression:overlay_keeps_midturn_and_full_text` — never reads the overlay; it counts
  `agent_line` rows. Becomes a `StoreReader.Tail(all: true)` unit test, the function its name is
  about.
- **`compression:midturn_narration_is_not_in_the_pane` — CORRECTED, and it is the sibling of the row
  above rather than the same case** (§10, finding 11). The survey classified it `unit` alongside
  `overlay_keeps_midturn_and_full_text`, but the two are not alike: the overlay one genuinely never
  reads the overlay, while this one reads `$pane.lines` from a live `ui dump`
  (`compression-acceptance.ps1:92,96`) — it IS a window-rendered assertion. Moving it whole would
  leave `StoreReader.Tail`'s filter proved and **nothing asserting the pane consults it**: change the
  call to `Tail(all: true)`, drop the `HasCompressed()` consult, or render from a second source, and
  the unit test stays green while the operator sees duplicated mid-turn narration. So it splits into
  two rows — the SQL projection `moved` beside its sibling, and one assertion `merged` onto F10's
  existing window fixture asserting the compressed pane omits `working on:`. Milliseconds on a
  fixture that already exists. **The general rule is in §2.2: an absence asserted through a window
  stays at a window.**
- `ui-ask:the_ask_is_not_a_modal` — its mechanism was **measured false**. Rewritten onto the window
  count (W7), name kept.
- `ui-grid:the_newline_survived_to_the_agent` — its comment claims "all the way to the agent's
  stdin"; the assertion reads `pane_events`, so it proves UI→daemon only. F1 asserts the **agent's
  own echo**.
- `m1:a_promoted_lane_is_re_briefed_as_a_ticket_lane` — issue #11: it prints `FAIL []` because it
  discards its `Wait-Until` boolean. Capture once, report the same value. **Do not lengthen the 25 s
  deadline.**

**Verification.** `dev ledger --verdict`: `unaccounted = 0`, `live integration <= target`.

**Cost.** ESTIMATE: one to two sessions.

**Depends on.** W8.

---

### W10 — Measure, then decide

**Changes.** Five `dev gate` runs at the new shape on a clean machine, and only then:

- **replace §2.5's estimate row with the measured spread**, and delete the estimate;
- `SoloSuites` — `unit` is solo for a **structural** reason (`dotnet test` compiles Dodona into
  `src\Dodona\bin\Release`, which every suite copies out of via `Use-TestBinaries`), and
  `Dodona.Ui.Tests` makes it worse. Keep it solo **unless the stated trigger fires**: when
  `dev test unit` exceeds **15 s**, switch the *wave's* invocation to `dotnet test --no-build` and
  take `unit` off the list. Edit the list only **with the measurement written beside it**;
- `dodona.json`'s `//verify` becomes `dev build` + `dev test unit gate land`, and **it is NARROWER
  than today's `unit m1 m2` — say so in the block rather than calling it equivalent** (§10, finding
  21). Today's `m1` splits across `gate` (G1/G3), `land` (M/V/D/A) and `review` (R\*); today's `m2`
  splits across `gate` (the isolation half) and `brain` (the routing half). So `unit gate land`
  drops **m1's R\* — the completion record**, which is the artefact the land itself produces and
  which `dodona ticket-record` serves as the PR description in a `"delivery": "pr"` repo — and all of
  m2's routing half.

  Both drops are deliberate and the `//verify` comment must carry the reason, because that is where
  its reasoning lives and it runs **inside the daemon during a land, when nobody is watching**
  (`dodona.json:9-11`): a land does not route, so `brain` buys nothing there; and the completion
  record is produced *after* the merge the verify gates, so `review` cannot protect the ref this
  check exists to protect. **If either reason stops being true, the answer is `unit gate land
  review`, not a sentence about care.** What may not happen is the first draft's version: calling a
  narrowing an equivalence, which is how a check quietly stops checking. **Do not widen it beyond
  that**;
- I7's budget: **unchanged at 300 s.**

**Verification.** Five gate runs, recorded with their commit and date.

**Cost.** ESTIMATE: one session plus ~20 minutes of gate runs.

**Depends on.** W9.

---

### W11 — `dev corpus-record`, run once, directed

**Changes.** A verb on `dev.ps1` that runs **one real `claude -p --input-format stream-json
--output-format stream-json` session**, driven by a scripted prompt that deliberately exercises the
missing shapes (use a tool, fail a tool, trip a permission denial), tees the raw wire to
`tests/assets/wire/real/<date>-<model>.jsonl`, and updates `MANIFEST.json` with `recordedAt`,
`cliVersion`, `model` and the shapes witnessed. Then `unwitnessed[]` shrinks.

**It costs quota**, so it prints what it is about to spend before spending it, it is
operator-authorised, and **no suite, gate or watcher may ever invoke it**.

**Verification.** `unwitnessed[]` is smaller and every remaining entry carries a reason.

**Cost.** ESTIMATE: half a session plus one deliberate model call.

**Depends on.** W5. Can run at any point after it.

---

### W12 — Close out the enforcement and the documentation

**Changes.** CLAUDE.md's suite table rewritten to the 11 suites (the mapping from a suite to what it
covers is *"judgement no command can print"* and is the table's only surviving value);
`check-authoring` gains an **anchor section** — what an anchor is, which one a new double needs, and
that `Wire` is mandatory — **in the same commit as the mechanism**; `.claude/skills/ship` updated;
this plan's Status line updated with what was built and what was measured.

**No fourth trap skill** (D-T24): CLAUDE.md §5.1 D-6 forbids one by name, and a "how to write a
fake" skill is squarely a trap skill. Most of what it would say is enforcement here already.

**Verification.** `dev lint`, `dev gate`, and a read-through.

**Cost.** ESTIMATE: one session.

**Depends on.** W10.

---

## 5. THE LEDGER — how "nothing was lost" is proved

### 5.1 Why the existing precedent does not generalise

`3b235ab` proved the ui-use split faithful by diffing sorted check names from the monolith's
`results.json`: *"130 names in, 130 identical names out — ZERO lost, ZERO added, ZERO renamed."*
**The idea generalises and is adopted wholesale. The mechanism does not**, for three reasons each
fatal alone:

1. **The names change namespace.** `status_does_not_summon_a_daemon` in a hashtable becomes
   `Dodona.Tests.NoSummonTests.status_does_not_summon_a_daemon` in a TRX. A set-diff of two lists
   that are not in the same namespace answers nothing. Identity has to be **declared and then
   verified**, not assumed.
2. **An equal name is no longer an equal assertion.** ui-use moved bodies as **bytes**, verified
   line-for-line. Here the body is rewritten, so a surviving name can be **green while asserting
   something strictly weaker**, and no name list can see that. This is the whole risk of the job and
   it needs a different instrument (§5.3).
3. **The old side evaporates.** `tests/*-output/` is gitignored (`.gitignore:19`), so `results.json`
   is a run artefact, not a record. Six waves in there is nothing left to diff against unless the
   baseline was **frozen and checked in at the start**.

### 5.2 The three files

All in `tests/ledger/`, **TSV, ASCII-only, CRLF, no BOM**, and the tool asserts all three.

- **TSV not JSON**, deliberately: `ConvertFrom-Json` emits a JSON array as ONE pipeline item
  (CLAUDE.md §0.2), a trap that has already turned three acceptance checks into silent no-ops in this
  repo. Check names are `[A-Za-z0-9_.]+`, so a tab is unambiguous.
- **ASCII-only**, because Repo-Lint's known gap P1.8 is exactly non-ASCII in a BOM-less file read by
  PS 5.1 — one em dash in a ledger row would match nothing and drop that row **silently**.
- The reader **strips a leading U+FEFF** defensively. That is the GateHook incident: `Console.In`
  and `Get-Content` both hand a BOM back as an ordinary character.

**`baseline.tsv`** — the frozen census, **958 rows**. Columns: `check<TAB>suite<TAB>cases`, captured
by `dev ledger --capture` from a **green** full run: every suite's `results.json` keys, plus every
unit test **method** from the TRX with its executed case count. Runtime capture, not a static source
parse, for the same reason `dev.ps1`'s tally is the authority — a name that exists but never ran
counts as nothing, and the repo has been bitten by both halves (`m0` never printed a tally in its
life; `ui-use` reported *"115 checks, 0 failed"* that were 115 of 121).

**THE KEY IS `check`. `suite` AND `cases` ARE ORDINARY COLUMNS** (§10, finding 5). Keyed on
`suite<TAB>check`, the frozen file and W6's rename of all fifteen suites were mutually exclusive:
W6 alters the key of every one of the 750 suite rows, and its own stated verification — *"`dev ledger`
green with zero moved/merged rows"* — was unreachable, because the reachability rung would resolve
`m0:orphaned_result_landed` against a file that no longer exists. Two consequences, both load-bearing:

- **Check names must be unique repo-wide**, which W1.1's scan establishes and W1.1's two renames make
  true. It is not true today (§1.2).
- **`cases` is deliberately outside the integrity check.** Adding an `[InlineData]` row to a moved
  check's `[Theory]` moves the case count and not the census, which is what stops §9.4's B1 from
  fighting the last-segment rule (§10, finding 17).

> **The integrity property that makes the whole ledger worth anything:** `dev ledger` compares
> `baseline.tsv` against `git show HEAD:tests/ledger/baseline.tsv` and **refuses any run in which a
> baseline row's `check` was removed or altered**. Only appends by `--capture`, and edits to `suite`
> and `cases`, are legal. Without it, the verdict is green-able by deleting a row — the one edit
> nobody would notice in a 958-line file.

**`wires.tsv`** — W1's register. One row per distinct wire; `owner_check` is the ONE check that
survives to prove it.

**`moves/<slice>.tsv`** — **one file per slice, which is the entire answer to subagent collision on
the ledger.** Two subagents never edit one file, so there is no merge conflict, and `dev ledger`
reads the directory.

| column | rule enforced |
|---|---|
| `old_suite`, `old_check` | must be in `baseline.tsv`; must appear **exactly once** across all `moves/*.tsv` |
| `disposition` | one of `moved` `kept` `merged` `stays` `vacuous-guard` `renamed` — a **closed vocabulary**, so it cannot become a shrug |
| `destination` | `moved`/`renamed`: `unit:<FQN>` for `Dodona.Tests`, `ui-unit:<FQN>` for `Dodona.Ui.Tests` — two prefixes because `dev prove --with` must know which project to build (W3 item 4) and a `net8.0-windows` compile has no business in a net8.0-only proof; `merged`: `suite:<suite>:<check>`, and that check must itself be a `kept` row or a `wires.tsv` owner |
| `wire` | REQUIRED for `kept`, `merged`, `stays`; must resolve in `wires.tsv` |
| `mutation` | REQUIRED for `moved`; the patch must exist under `tests/mutants/` |
| `red_old`, `red_new` | REQUIRED for `moved`; both non-empty, the **literal observed failure lines** |
| `note` | REQUIRED for `stays`, `vacuous-guard`, `renamed`, and for a cross-suite `merged`. For `stays` it must BEGIN with a word from the closed reason vocabulary |

**THE LAST-SEGMENT RULE.** For `disposition=moved`, the final dotted segment of `destination` MUST
equal `old_check` **character for character**, *after stripping a trailing parenthesised argument
list*. `m0:session_id_recorded` becomes a C# method literally named `session_id_recorded`; a TRX row
reading `…session_id_recorded(n: 3)` resolves to it. `dev ledger` asserts it, so a typo cannot
silently orphan a name — the tool verifies the mapping **without trusting the ledger's author**. This
breaks the existing `Sentence_case_with_underscores` habit in `tests/Dodona.Tests`; accepted, because
the method name becomes self-documenting and the alternative is worse.

**The stripping clause is not a detail — without it the rule and §9.4's B1 contradict each other**
(§10, finding 17). B1 says *"variations become `[Theory]` rows"*, and a theory row's executed name is
never the bare method name, so every moved check that acquires a variation would fail to resolve and
each extra row would land in `added.tsv` demanding a hand-written reason. The exact appended form is
verified on this machine at **W3**, before the pilot, and recorded in `tests/ledger/README.md`.

**`added.tsv`** — names that exist in a live run and in no baseline row, each with a one-line reason,
so coverage **growth** is visible arithmetic rather than noise that could hide a loss.

### 5.3 The paired red — the instrument the name list cannot be

`dev prove` judges **product** code, so a pure test move is VACUOUS, correctly — `3b235ab` had to say
exactly that out loud about all four ui suites. Worse, a **new unit test over existing behaviour is
vacuous by definition**: HEAD contains the behaviour, so HEAD passes it.

The substitute is **HEAD plus a named defect**:

```
dev prove --with tests/mutants/wire-01.patch  m0:session_id_recorded
dev prove --with tests/mutants/wire-01.patch  unit:Dodona.Tests.ShimWireTests.session_id_recorded
```

Both must come back PROVEN. **That is the proof of a faithful move: the same named defect reddens
the old check before deletion and the new test after it.** The patch is checked in, so the proof is
re-runnable by anyone in forty seconds rather than attested by a comment.

**What this does NOT do, said plainly:** it proves **co-sensitivity, not equivalence**. A check that
asserted three things and now asserts one still passes. That gap is closed by an agent reading the
old body — which is why `dev ledger --origin <check>` exists (`git log -S` over `tests/`, printing
the deleting commit and the original lines) — and by the six survey tables, which are the authority
on what each check was really asserting. This is the residual risk of the entire job and §8.4 says so
rather than hiding it.

### 5.4 A check that cannot move down — the four-rung ladder

The answer is **never** "delete it, it is covered elsewhere":

1. **Is there a cheap seam?** Consult `seams.md` S1–S12. Most "cannot move" is really "has no seam
   yet"; the seam belongs in this slice's commit A.
2. **Is it a WIRE?** → `stays`, pointing at a `wires.tsv` row. If that wire already has an owner,
   this check is `merged` into the owner.
3. **Is it downstream of a wire someone else owns?** → `merged`, naming the survivor.
4. **None of the above** → `stays`, with a `note` beginning with a word from the **closed** reason
   vocabulary: `process-fact`, `git-ref-mutation`, `real-window`, `timing`, `absence-of-process`,
   `wire-shape`, `harness-hygiene`, `no-seam-yet`. `dev ledger` refuses a reason outside the list and
   **counts `no-seam-yet` separately in the verdict so it cannot hide** among the legitimate ones.

**REACHABILITY AND THE 22 NAMES NO STATIC PARSE CAN PRODUCE** (§10, finding 24). D-T6 leans on
`dev ledger`'s reachability rung to catch *"a check deleted from a `.ps1` while its TSV row
survives"*, and two sites generate their names at runtime: `Check "event_$k"` over a 16-element
`foreach` (`tests/m1-acceptance.ps1:1167`) and `Check "resolution_recorded_$rung"` over six rungs
(`tests/concierge-acceptance.ps1:337`). §1.2 counts them correctly and the first draft never said how
the rung resolves them. It does not: **reachability for a loop-generated name is satisfied by the
`--live` side only, and `dev ledger` static must never claim one as unreachable.** The static rung
maintains a small allow-list keyed on the generating expression's file and line, which `dev lint`
refuses to let go stale (the line must still contain a `Check "` with an interpolation). A run that
does not produce the name is what catches its deletion, which is the same authority `dev.ps1`'s tally
already has.

Three special cases that must not be mishandled:

- **`no_process_left_in_the_build_output` (15 rows)** — written per suite in `finally`, reports what
  **this** suite left behind, **not deduplicable**. `stays / harness-hygiene`. When suites merge the
  row count falls with the suite count and the ledger records that; it is **not** a loss, and it is
  one of exactly two legal shrinks (the other is the two `voice` checks promoted to harness rows in
  W7).
- **Vacuous-by-construction guards.** The repo keeps and **labels** them — R7's precedent: 18 checks
  shipped, 14 seen red, the 4 vacuous ones kept *and labelled*. `dev prove` will keep calling them
  VACUOUS wherever they live; that is expected and is not a licence to delete. Three of them are pure
  predicates and **do** move down, where they guard identically and cannot flake.
- **A check pinning something that should CHANGE.** `INVESTIGATION §4.8`: *"A suite that proves
  stop-all is machine-wide would ENSHRINE the behaviour."* Preserving a name is not endorsing its
  assertion. Such a check is moved **faithfully** and a ticket is filed; it is never quietly re-aimed
  inside a migration commit, because a migration commit that also changes what is asserted is
  unreviewable.

### 5.5 The verdict block, and the stop condition

`dev ledger --verdict` prints exactly this, and the job is finished when it says so:

```
LEDGER
  baseline            958 names, frozen at <sha>   (750 suite + 208 unit methods; 1050 cases)
  live in suite       NNN
  moved to unit       NNN   (each with a mutant and two recorded reds)
  merged into         NNN   (each naming a LIVE survivor and a wire)
  stays               NNN   by reason: process-fact N, git-ref-mutation N, real-window N,
                            timing N, absence-of-process N, wire-shape N, harness-hygiene N
  stays (no-seam-yet)   N   <- MUST BE 0, or every one carries an issue number
  vacuous-guard         N   (kept and labelled, by decision)
  unaccounted           0   <- MUST BE 0
  added (declared)      N
INTEGRATION CHECKS
  wires.tsv rows       NN
  harness rows         NN   (one per surviving suite; not deduplicable)
  live integration     NN   target NN   <- MUST BE <= target
DOUBLES
  anchored              N
  known divergence      N   (issues #a #b)   <- a READING, not an assertion
  unwitnessed shapes    N   (issues #c #d)   <- a READING, not an assertion
VERDICT: on the accounting above, and only that.
```

The two `DOUBLES` counts are readings for the same reason corpus staleness is (D-T13): a number that
must be zero teaches people to make it zero by editing a list. A number that is **printed, sourced to
issue numbers, and refused without one** is what stops a divergence hiding.

The last line deliberately echoes `dev gate`'s *"on the 10 assertions above, and only those"*: the
ledger proves every name is accounted for and every move was seen red. **It does not prove the
system works**, and nothing in this job may claim otherwise.

**80 % and quietly abandoned is detectable, and that is the point.** `unaccounted` is non-zero for
every un-migrated name, so the number never rounds to done; each slice is a separate file so the
verdict says which slices exist; and a slice cannot be left half done, because commit B is atomic.

---

## 6. DECISIONS

**D-T1. Count the wires, not the cases.** An integration test proves a path is plugged in; a
variation on what flows down it is content and belongs in a pure-logic test. *Reason:* §1.4's
measurement — of sixteen suites, the only one that starts no process and opens no window is the only
one whose runtime did not move between a busy and an idle machine (5.3 → 5.4 s), while every other
suite ran 15–28 % faster idle. The contention is process and window creation, so fewer integration
tests is the only lever available.

**D-T2. 52 wires on 19 fixtures, derived by a fold rule rather than chosen as a target.** *Reason:*
109 candidate rows folded to 49 by *"if cutting this wire reddens another kept test, it is not
distinct"*. §2.4 records what deleting each of the five remaining distinctions has already cost this
repo, and three more (E7, E8, J3) were admitted by the same rule under adversarial review (§10). The
number the operator feels is 19 fixtures, not 52 wires.

**D-T3. The new lower layer is C# xUnit in `tests/Dodona.Tests` (plus a new `Dodona.Ui.Tests`), not
more PowerShell.** *Reason:* the door is already open (`src/Dodona/Dodona.csproj:23-28` grants
`InternalsVisibleTo("Dodona.Tests")`); the seams are C# types, and a PowerShell test reaching
`HandleShimLine` would have to go through a compiled binary and a process, which is the exact cost
this job removes; `Run-Unit` already synthesises the `<N> checks, <M> failed` tally every `.ps1`
prints, so nothing new is owed to `dev test`/`suites`/`gate`. **`ORCHESTRATOR-DESIGN` §17's *"a test
asserts on events and decisions, not on internals"* is argued rather than ignored** — and the counter
is already in-tree: `Daemon.ClaudeArgs` (`:2846`) and `Projects.PromptDirMismatch` were made
`internal static` *precisely because* **no acceptance suite can see them** (the fake agent takes no
claude flags, so `IsClaude` is false for it).

**§17's SECOND clause is the one this plan inverts, and the first draft answered only the first**
(§10, finding 22). The full sentence is *"a test asserts on events and decisions, not on internals,
**which means tests survive refactors** and every test failure is already a trace."* D-T3 answered
"not on internals" and left "survive refactors" standing — while D-T4 makes every move a rewrite and
§8.4 risk 9 accepts ~50 checked-in mutants that *"will rot as `src/` moves"* with **no refresh
mechanism**. So the plan converts a refactor-durable assertion surface into a refactor-fragile one,
and the price is:

- **~760 unit methods facing internals.** A rename of `Store.PaneEventId` reddens every test naming
  it. Against that: the compiler names all of them in one build, which an event-shaped assertion
  never does — a `.ps1` asserting on a SQL projection of `pane_events` goes red at *runtime*, in a
  suite, seconds later, with a diagnosis to read. Refactor **fragility with a compiler** is cheaper
  than refactor durability with a 90-second feedback loop, and that is the trade, stated as one.
- **~50 patches that rot.** Accepted in the correct direction (`dev prove --with` aborts loudly on a
  patch that will not apply), and re-cutting a one-hunk patch is minutes — but it is real recurring
  cost and it is not zero.

**And §17's *"the test seam is the shim pipe"* is moved, deliberately.** W5's flagship puts the seam
*inside* it, at `LaneRuntime.HandleShimLine`. That is defensible only because the corpus (§3.4) keeps
the pipe's own byte shapes honest from outside the repo — the seam moves in, the evidence about what
crosses it comes from real recorded traffic rather than from the fake. If the corpus mechanism fails
(falsifier 1), this justification fails with it and the seam should not move.

**D-T4. Moving a check from `.ps1` to `.cs` is a REWRITE. Only the NAME survives.** *Reason:* a
`.ps1` check asserts on a store row read back through SQL as a side effect of a real binary; the
`.cs` equivalent asserts on a return value. Fixture, assertion and failure text all change. Budget
it as writing a new test with a name handed to you. **This is exactly why the name is what the
proof is made of.**

**D-T5. The proof of a faithful move is the PAIRED RED under one checked-in mutant.** *Reason:* the
`3b235ab` set-diff mechanism cannot see a name that survived while asserting less (§5.1), and the
repo's culture rests on a check having been seen red. The mutant is checked in so the proof is
re-runnable rather than attested.

**D-T6. There is exactly ONE parser of check names: `dev ledger` (PowerShell). The C# ledger reads
the tracked TSV artefact and never parses a `.ps1`.** *Reason:* two independent enumerators of one
thing are two hand copies, which is the failure this whole plan exists to prevent. PowerShell owns
it because `tools/dev.ps1` must run on a tree that will not compile (CLAUDE.md §1). The staleness
that arrangement could hide — a check deleted while its TSV row survives — is caught by `dev
ledger`'s reachability rung, which runs inside Repo-Lint (D-T23) — except for the 22
loop-generated names, which are reachable on the `--live` side only (§5.4).

**D-T7. `dev prove --with <patch>` extends the existing verb; the bare form's `unit` refusal
stays.** *Reason:* all three recorded reasons for that refusal give a **wrong** answer rather than
none, and each is answered only in the `--with` case (§4/W3). Without it the proof burden is ~560
break-and-reverts, which will be skipped — the disease CLAUDE.md §0.3 is about.

**D-T8. Every test double carries `[Double(Anchor, typeof(Real), Wire = …)]`, and the population is
enforced by a STATIC REPO-WIDE SCAN, not by reflection over one assembly.** *Reason:* reflection can
only see the assembly a test project loads, and two of the three existing doubles are in
`src/DodonaUi` — a `net8.0-windows` assembly `Dodona.Tests` cannot load at all. The first version of
this decision enumerated an **empty population** while §3.6 tabulated three doubles as anchored. A
text scan of `src/**` and `tests/**` cannot miss an assembly, needs no build, and — with
`double-assemblies.tsv` — refuses a double placed where no reflection rung looks. **Rung 2 exists for
the semantic questions a text scan answers badly** (implementer counts, `typeof(Real)`, contract
subclass counts) and lives in each assembly's own test project.

**D-T9. Anchors: `Interface` (≥ 2 implementers that do NOT carry `[Double]`), `Contract`, `Corpus`,
and `Landing` as a deliberately weakest fourth invalid without a `Wire`. `Interface` IS NEVER A SOLE
ANCHOR.** *Reason, both halves measured against the tree:* `IRecognizer` has exactly two implementers
and **one of them is the double** (`DeepgramRecognizer.cs:48`, `Recognizer.cs:73`), so an unexcluded
count is satisfied by the double counting itself and any future `FakeX : IX` self-anchors. And
`Interface` reaches **D1 only** — §3.1's own table says so — while the first draft anchored
`FakeRecognizer` as `Interface` *and named its behavioural divergence in the same row*. So every
double needs `Interface` **plus** a `Contract` covering each method whose behaviour a test depends
on, or an explicit `KnownDivergence` + `Issue` that is **counted in the gate's reading line**. The
assertion prints the surviving implementers by name, because a count is not a reading.

**D-T10. `Wire` is required, its check name is verified to EXIST, and the wire's owner body is
HASHED. The claim is downgraded to match.** *Reason:* the first draft said this *"turns Constraint 3
into a red build"* while §5.1 argued four sections later that *"a surviving name can be green while
asserting something strictly weaker, and no name list can see that."* Both cannot be true, and §5.1
is the one that is. So: `Wire` is an **anti-rot check on the register**, not enforcement of
Constraint 3; `wires.tsv` carries `owner_body_sha` over the owner's `Wire '<id>'` block so a silent
narrowing reddens the ledger; and the honest limit is stated in §3.3.1 — **a hash is a speed bump,
not a proof.** The instrument for "does this wire still exercise the real path" is §9.7 rung 6, and
it is human.

**D-T11. Delete a transport rather than fake it.** `Daemon.HandleAsync(req, StreamWriter over
MemoryStream)` is the real 45-case handler with nine lines of pipe server absent. *Reason:* no
double, no anchor, nothing to drift — strictly better than any verified fake. What it does **not**
cover keeps its integration check: that the pipe server calls `HandleAsync` at all, that the pipe is
**serial** (`m1:say_answers_during_a_land`), and start-on-demand.

**D-T12. No `IClock`. Time is a `DateTime nowUtc` parameter with a defaulted overload; `Store` gets
one `Func<DateTime>?` constructor parameter.** *Reason:* the house pattern already exists
(`Poller.Liveness` takes `now`; `Trees.Locate` shows the shape) and keeps production on exactly one
path. An interface would be a second style needing its own double and its own anchor. **And it must
not be sold as fixing issue #3** — §3.7.

**D-T13. Corpus staleness is a gate READING, not an assertion; `dev corpus-record` is a hand-run
verb.** *Reason:* a test that reddens on a date fails for a non-defect, reddens historical commits
under bisect, and teaches re-running instead of reading. `dev lint` fails on malformed provenance,
which is a defect. The gate's verdict stays at ten assertions.

**D-T14. Suites are renamed to what they cover, in ONE commit.** *Reason:* CLAUDE.md's own note on
the deleted duration column applies to names too — the suite table's surviving value is *"the mapping
from a suite to what it covers, which is judgement no command can print"* — and `m1` currently means
three unrelated things (the write gate, the merge token, the briefing). One commit because I8's
dangling-reference lint turns red on every stale doc reference, which is a forcing function.

**D-T15. `no_modal` and `no_socket_from_the_ui_process` become harness teardown assertions in
`_workspace.ps1`.** *Reason:* the modal detector that **works** is counting top-level windows —
measured 2026-08-20, a real `MessageBox` was up and `ui-ask:the_ask_is_not_a_modal` passed, because a
Win32 modal pumps its own nested loop. Promoting it applies the working detector to every window
fixture instead of one. The egress observation moves next to `DODONA_UI_MIC=off` so a config change
cannot disarm it.

**D-T16. `unit` stays solo until a trigger stated in advance: `dev test unit` > 15 s → the wave's
invocation becomes `dotnet test --no-build` and `unit` comes off the list, with the measurement
written beside it.** *Reason:* `unit`'s solo status is **structural** (two compilers, one directory),
not flakiness, and `SoloSuites`' own comment says both directions of an edit are a measurement.

**D-T17. I7's budget stays at 300 s.** *Reason:* `dev.ps1:1699` — *FIX A RED, RAISE FOR GROWTH, and
never the other way round.* Issue #1's finding is that the problem is **variance** (54.3 s spread at
one commit on one machine), not the mean, and a budget lowered against a good mean manufactures a red
for the next busy machine.

**D-T18. A slice is two commits (seam, then move) in its own worktree, with one `moves/<slice>.tsv`
per slice.** *Reason:* rollback becomes structural rather than procedural (§9.9), and one file per
slice is the entire answer to subagent collision on the ledger — two agents never edit one file.

**D-T19. At most one window-suite-owning slice per wave.** *Reason:* issue #3 is unrooted and `voice`
showed the cascade signature **while solo**, so the crowd is not concurrent suites but what a wave
leaves behind it. Two agents verifying window suites at once manufactures a false red, which costs as
much as a false green.

**D-T20. The pilot is `S-WIRE`, and it is the kill switch.** *Reason:* one production keyword,
a double the compiler anchors, the plan's hardest problem (`DodonaFakeAgent`'s unanchored wire shape)
on its smallest instance, material already tracked and free, and four moved checks. If the corpus
mechanism cannot be built there, the plan dies having deleted nothing.

**D-T21. "Delete because it is covered elsewhere" is not a disposition.** The four-rung ladder of
§5.4 with a **closed** reason vocabulary, and `no-seam-yet` counted separately in the verdict so it
cannot hide. *Reason:* a closed vocabulary is what stops "cannot move" becoming a shrug.

**D-T22. Five mis-aimed checks are fixed while moving, each in its own recorded row; nothing else is
re-aimed inside a migration commit.** *Reason:* a commit that both moves and changes what is asserted
is unreviewable. The five are named in W9; a check that pins behaviour which should change is moved
faithfully and **ticketed** (`INVESTIGATION §4.8`).

**D-T23. `dev ledger`'s static checks are folded INTO Repo-Lint (I8), which is already one of the
ten gate assertions. The gate's count stays at TEN.** *Reason:* the first draft said in §3.4 that it
*"does not make it eleven"*, made it eleven in this decision, and conceded in §8.4 risk 8 that it had
— three statements, unreconciled (§10, finding 15). Folding into I8 removes the contradiction rather
than arguing it: the ledger's static rungs are a sub-second static parse of tracked files, which is
precisely what I8 already is, so nothing about the gate widens and the refusal is still real.
`dev ledger` remains a verb for humans (`--live`, `--slice`, `--verdict`, `--origin`); it is NOT
added to `dev test`, and NOT to `dodona.json`'s `//verify`. If the ledger stops earning its place
after the migration, the lint rows and the files go together in one commit.

**D-T24. `check-authoring` gains the anchor section, in the same commit as the mechanism. No fourth
trap skill.** *Reason:* CLAUDE.md §5.1 D-6 forbids one by name, and a "how to write a fake" skill is
squarely a trap skill.

**D-T26. The surveys are committed to `docs/testarch/` in W1.0, before anything else in this job.**
*Reason:* they are the per-check authority this plan cites on every page and §8.4 risk 1 makes them
the only defence against the residual risk of the whole job — and they were written to a session
scratchpad, which is the exact failure CLAUDE.md §0 opens with. A plan whose authority evaporates at
the next session boundary cannot be executed by the sequence of sessions it budgets for.

**D-T27. `DodonaFakeAgent` carries no `[Double]` attribute and is anchored entirely by files.**
*Reason:* it is a *program*, not a type; reflecting over one of its classes would assert nothing about
the wire shapes it emits, which is the only thing anyone cares about. Its anchor is the corpus rung —
`MANIFEST.json`, `dev wire-sample`, lint row I9, `FakeShapeTests` — which is file-based end to end and
therefore respects D-B7's *"this project deliberately references nothing"*, cited approvingly in §7.2.
`double-assemblies.tsv` carries `DodonaFakeAgent → corpus` as an explicit row, so the population scan
is satisfied by a declaration somebody wrote rather than by an omission nobody noticed.

**D-T28. `dev wire-sample` ENUMERATES the fake agent's emittable shapes; it does not sample a run.**
*Reason:* a shape behind a directive the sample script never exercises would never appear in the dump,
so I9 and `FakeShapeTests` would both stay green over a shape nobody has ever seen — a lookup that
cannot miss, which is CLAUDE.md §3's routing-ladder failure in a new costume. The fake holds its
shapes in one `static readonly` table which is also **what it emits from**: ensure at the point of
use, never look up.

**D-T29. Slice boundaries are derived from SEAM SITES, not from suite names, and the seam → slice map
is published beside the suite → slice map.** *Reason:* drawn from suite names, `S-GATE` created the
`GateHook` seam while `S-STORE` owned every `GateHook` check, and the busiest seam site in the tree
(`Daemon.cs`) was claimed by one slice scheduled **last and alone** — so a large set of movable checks
was structurally blocked behind it while `stays (no-seam-yet) = 0` is a stop condition. `S-DAEMONSEAM`
lands those seams in wave 0 as commit A with no commit B, and `S-DAEMONCMD` is abolished because with
the seam landed each slice moves its own suite's command-shape checks.

**D-T30. Slice declarations carry no wildcards, and two slices that must touch one file are
SEQUENCED and named, never concurrent.** *Reason:* `DodonaUi/*.cs` was a wildcard over `Poller.cs`,
`StoreReader.cs`, `MainWindow.xaml.cs` and `DaemonClient.cs`, i.e. over three other slices' declared
work, and §9.6 rule 4 makes "anything outside that set" a stop-and-report — a rule that cannot be
applied to a wildcard. The named exception is `Program.cs`, shared by `S-PUBLISH` (wave 1) and
`S-GATE` (wave 2) in disjoint regions; a second such pair would mean the boundaries are drawn wrong
(D-T29) rather than that the rule needs widening. **The separate `Compile Include` hazard is a MERGE-order rule, not a
concurrency one**: `DodonaUi.csproj` links thirteen `src/Dodona` files rather than referencing the
assembly, so a slice editing one of them changes `DodonaUi.exe` — but each slice has its own worktree
with its own `bin` and `obj`, so nothing recompiles under anybody. What is real is that a window suite
verified before such a merge was verified against a binary that did not contain it, so the parent
re-runs `window` and `shell` after any such slice merges.

**D-T31. The bulk's cost is not asserted; it is MEASURED at wave 1 and that is the second decision
point.** *Reason:* the plan's own two figures disagree by 5–9× (W5: 4 checks in 2–3 sessions; W8: 560
in 20–40), the reconciliation is an assumed 7–19× batching speedup that nobody has demonstrated, and
two costs — the mutant re-cut loop and hours of machine time in `dev prove --with` — appear in neither.
Stating both rates and naming wave 1's actual checks-per-session as the deciding number is the only
honest form. W6 moved after wave 1 so the irreversible churn is not paid before that number exists.

**D-T25. Do not pick a target for the surviving integration count. Derive it.** `target =
rows(wires.tsv) + one harness row per surviving suite`, printed by `dev ledger`. *Reason:* the six
surveys list 109 wire rows before cross-group dedup; §2.2's 52 is the expected output of W1's merge,
not an input to it, and a number chosen in advance would be a number somebody hits by deleting.

---

## 7. REJECTED, WITH REASONS

Kept so they are never re-proposed. Items marked **(prior art)** were already rejected elsewhere in
the repo and are repeated here because this job is exactly where they resurface.

### 7.1 About the shape

| rejected | reason |
|---|---|
| **A count of ~15 wires** | Reachable only by deleting one of five distinctions, each of which has already shipped a defect here — §2.4's table. |
| **A count of ~80** | The upstream test was applied ruthlessly: 16 candidate wires die purely because cutting them reddens a kept test, including `lane-start spawns`, `the gate-hook denies`, `publish builds` and `a live UI reads the store` — 21 + 23 + 2 + 23 survey checks between them. |
| **Keeping the milestone suite names** (far less churn) | `m1` means three unrelated things. The suite table's only surviving value is the judgement of what a suite covers; a rename is a one-time cost against a permanent misdirection. |
| **Merging `ui-shell`/`ui-wake` into `window` too** | **(prior art)** Issue #2's split trap: four solo pieces measured **134.3 s sequential against the monolith's 88.8 s**. A split only pays when the pieces run concurrently. `shell` stays separate so the wave keeps its width and `publish` stays the only pace-setter. |
| **Giving `stop-all --lanes` a wire that asserts machine-wide scope** | **(prior art)** `INVESTIGATION §4.8`: a suite that proved it would **enshrine** the behaviour. A4 asserts only that `##shutdown` reaches *this workspace's* shim and takes *its* child tree. |
| **Deleting the checks the suites mark VACUOUS BY CONSTRUCTION** | R7's precedent: 18 checks shipped, 14 seen red, the 4 vacuous ones **kept and labelled**. They guard against a future widening. |
| **Giving `m0:reconcile_does_not_knock_on_pipes_that_are_gone` its own fixture** | It is a wall-clock budget over a real pipe namespace, observable only as elapsed time — issue #3's flake shape if it owns a fixture. One generous-budget assertion on A1's restart instead. |
| **Moving `brain:pulse_on_arrival` / `pulse_fades` to unit** | The pulse is a TRANSIENT that must be caught while it is on and then observed to fade — timing plus a real window. Two assertions on F1's fixture. |

### 7.2 About the doubles

| rejected | reason |
|---|---|
| **`IStore` + an in-memory store** | ~70 public members, and the properties worth testing are the transaction boundaries an interface **erases**. `LandCommit` (`Store.cs:1525`) re-checks holder and lease *inside* the tx that lands the ticket; a double reimplements that as sequential field writes and proves itself. |
| **Contract tests as *the* mechanism** | The highest-drift-risk double (`DodonaFakeAgent`) has a real counterpart that **costs quota and is nondeterministic**, and CLAUDE.md §0.1 forbids suites making model calls. A contract body that can never run against the real subject is one test body run once. Kept as one anchor of three, and it is the strongest where it applies. |
| **A Roslyn analyzer / source generator** | Enforces nothing a ~120-line reflection test does not, at ~10 ms and zero build cost; adds a package to a **deliberately offline-pinned** project; and puts a dependency on the compile path that `dev check` must survive without (*"a tool whose job is to fix a broken build cannot itself require a build"*). |
| **Colocation as the anti-drift mechanism** | It is a convention. `ILaneSink` is safe because **both implementers ship**. Kept as a free layout rule, never cited as a reason a double is safe. |
| **A test that reddens when the corpus is older than N days** | Fails for a non-defect, on a date; reddens historical commits under bisect; teaches re-running instead of reading. Replaced by a lint failure on malformed provenance and a gate reading. |
| **An in-process double for the agent** | Would delete the real shim, the real named pipe and `LaneRuntime`'s entire reason to exist, turning every `m0`/`m1` check into a claim about nothing. |
| **A shared constants file or schema both `LaneRuntime` and `DodonaFakeAgent` compile against** | **(prior art)** Already rejected as D-B7 (`LANE-BRIEFING-PLAN:240`, repeated verbatim at `DodonaFakeAgent/Program.cs:94-101`) — and it would not help: a shared constant keeps the two halves agreeing with **each other** while both drift from what `claude` emits. The corpus is evidence from outside the repo. |
| **Faking `Registry` uniqueness in a `HashSet`** | A different enforcement mechanism passing a test written about a partial UNIQUE index the class comment calls *"the real arbiter"*. CLAUDE.md §5: a red exclusivity check is a correctness incident. |
| **Faking `Git.Run` with canned stdout** | Proves the parser, not the operation. `SilentDrops` exists to catch a merge that **succeeded** and lost work; a canned success is the exact input that cannot catch it. |
| **Mocking a forge into the daemon** | **(prior art)** `REVIEW-AND-MERGE-PLAN:546`: *"would be testing the mock."* |
| **A shared or pooled daemon fixture across suites** | **(prior art)** `RECOVERY-PHASES` D-2: *"the way to remove startup is to NOT NEED A PROCESS (P4.5), never to share one."* |
| **`Moq` / `NSubstitute` / `FluentAssertions`** | **(prior art)** The test csproj is version-pinned to the machine's package cache on purpose: *"adding a verification step that needs the network is adding a way for verification to be unavailable exactly when it is wanted."* Hand-written doubles only. |
| **An `IClock` interface** | A second style beside the house's injected-value pattern, needing its own double and its own anchor, buying nothing a defaulted parameter does not. |
| **`Compile Include`-linking `[Double]` into `DodonaFakeAgent`** | It would satisfy the ledger's population scan by making the fake agent's classes reflectable, and it would violate D-B7 — cited approvingly two rows above — for the sake of an attribute that says nothing about the wire shapes, which are the only thing that fake can drift on. D-T27: the fake agent is a *program*, and its anchor is the corpus. |
| **Anchoring a double by an implementer count that includes the double** | Measured against the tree: `IRecognizer` has exactly two implementers and one of them is `FakeRecognizer`, so the count self-satisfies and any future `FakeX : IX` in `src/` anchors itself. The count excludes `[Double]`-carrying types and prints the survivors by name (D-T9). |
| **`Anchor.Interface` as a SOLE anchor** | It reaches D1 and §3.1's own table says so. The first draft anchored `FakeRecognizer` as `Interface` while naming its behavioural divergence in the same row — an anchor green over a fake that raises `Ready` synchronously where the real socket does not. Every double needs a `Contract` or a ticketed, counted `KnownDivergence` (D-T9). |
| **Two `DODONA_HOME`s in one test process** | `Instance.ConciergeId` (`Instance.cs:84`) and `ShellId` (`:91`) are `static { get; } = Scoped(...)`, frozen at first touch, while `Paths.Home` re-reads per call. Fixtures take **explicit paths** instead. |

### 7.3 About the migration

| rejected | reason |
|---|---|
| **A sorted set-diff of check names, generalising `3b235ab`** | Three fatal reasons in §5.1. The **idea** is adopted wholesale; the mechanism is not — except at W6, where bodies move as bytes and it is exactly right. |
| **Free renaming of moved checks with the ledger as the only link** | One mistyped row silently orphans a name with nothing able to detect it. Replaced by the last-segment rule; `renamed` survives as a noted escape hatch. |
| **JSON for the ledger files** | `ConvertFrom-Json` emits an array as ONE pipeline item — a trap that already made three acceptance checks silent no-ops here. |
| **Recording each new check's red only in a code comment** | Fine for one check; over ~560 moves it makes every claim unverifiable except by trusting 560 comments — *"a property claimed in prose is not enforcement"*, which CLAUDE.md §3 records at its own expense. The comments **stay**; the checked-in mutant makes them re-runnable. |
| **An automated mutation-testing framework (Stryker.NET or similar)** | New package, new network dependency at verification time, against a stated decision. A hand-written patch is also the better artefact: it names ONE defect a person chose, which is what makes the red readable. |
| **Storing the deleting commit's sha in the ledger** | A second source of truth that rots — the `.built-from` mistake. Derived by `dev ledger --origin` instead. |
| **Adding `dev ledger` to `dev test` or `dodona.json`'s `//verify`** | CLAUDE.md §0.1 forbids widening an automatic reader; the `//verify` block's own comment says *"do not widen it back."* |
| **Wiring an expected-count guard into the suite runner** | **(prior art)** `dev.ps1:584` already decided against it with a reason (*nothing knows how many a suite should have run*), and a **count** is the wrong instrument. The ledger compares **names**, from a static file the suites do not generate. The `FullyQualifiedErrorId` scan stays exactly as it is. |
| **A machine-wide lock in `dev test` serialising window suites across worktrees** | Makes `dev test` wait on another process — CLAUDE.md §0.1's *"never hung"* in a new costume — taxes the operator's own runs for the whole migration, and issue #3 is unrooted so the lock may not even be drawn round the right boundary. Scheduling is the cheaper instrument. |
| **`-p:BaseOutputPath=<temp>\` for the unit run** | A separate output tree makes the first run after any code change a cold compile of Dodona + DodonaUi, breaking `dev test unit`'s 1–2 s promise, which is the operator's **explicit** requirement (P1.5). |
| **Editing `SoloSuites` on reasoning rather than measurement** | Its own comment: *"do not 'tidy up' `SoloSuites` because a suite looks fast enough to parallelise, and do not add to it because a suite looks risky. Both directions are a measurement."* |
| **Lowering I7's budget because the mean improves** | D-T17. |
| **`ui-shell`/`Shell.Build()` as the pilot** (nine checks over two temp SQLite files, the surveys' *"single most reusable seam"*) | Needs a whole new `net8.0-windows` test project with its own `InternalsVisibleTo` — new infrastructure debugged at the same moment as new procedure. Two unknowns at once is how a pilot stops proving anything. |
| **`m1`/`GateHook` as the pilot** (the largest cluster of expensive-because-unreachable checks) | S11 is a real refactor of top-level statements in `Program.cs`, and it is **the write gate**: CLAUDE.md §3 and issue #4 record two separate occasions where prose about `GateHook`'s return paths was wrong. That work deserves an already-proven procedure. |
| **`workspace`/`concierge` as the pilot** (163 movable — the largest single win) | Most of those verdicts are Store- or Registry-backed and the unit project has **no Store fixture at all**; "move down a layer" there is a fixture project, not a copy-paste. Correct as a later wave. |
| **The wire register alone as the pilot** | A register with no slice executed against it proves nothing about whether the ritual is runnable. |
| **Transliterating `.ps1` checks into `.cs`** | Impossible. D-T4. |
| **Declaring slice disjointness on ASSEMBLIES rather than on files** (raised in review, finding 9) | The premise does not hold here: the review's mechanism was *"S-IDENTITY's edits recompile S-UIVM's binary under it"*, and they cannot — **every slice runs in its own worktree with its own `bin` and `obj`** (CLAUDE.md §0.0, §9.3), so nothing recompiles under anybody. Declared on assemblies the rule would also forbid almost every pair, since every suite consumes `Dodona.dll` via `Use-TestBinaries`, and the five-wave schedule would collapse to a queue. **The half that IS real is kept**: `DodonaUi.csproj` links thirteen `src/Dodona` files rather than referencing the assembly, so this is a **merge-order** hazard — the parent re-runs `window` and `shell` after any slice touching those files merges (D-T30) — and the wildcard `DodonaUi/*.cs` is written out as an explicit file list. |
| **Moving W6 BEFORE W2's baseline capture** (raised in review, finding 5, as one of two options) | It dissolves the rename-versus-frozen-baseline conflict, and it makes finding 20 worse: the rename is the most irreversible churn in the job and would then be paid before *any* slice had run. Both are solved instead by keying `baseline.tsv` on the **check name** with `suite` as an ordinary column, after which W6 can go anywhere — and it goes **after wave 1**, where one bulk slice of evidence stands behind it. |
| **Adding the `AttachShimAsync` spawner seam to the pilot** (raised in review, finding 6, as one of two options) | It would give W5 a second seam in `Daemon.cs` and end the "one keyword" property that is the pilot's entire claim to being cheap. The three spawn-failure checks are cut from the seed instead, the pilot is stated as **four** checks, and the cut four become its first `no-seam-yet` rows — which rehearses the closed vocabulary on real rows. The spawner seam lands in `S-DAEMONSEAM` (wave 0) where it belongs. |
| **Making `dev ledger` an eleventh gate assertion, or a bare gate READING** (raised in review, finding 15, as two options) | The reading keeps the count at ten but refuses nothing, and the whole value is refusing a removed baseline row. Folded into **Repo-Lint (I8)** instead, which is already one of the ten and already a sub-second static parse of tracked files: the count stays at ten *and* the refusal is real. D-T23. |
| **Quoting `ORCHESTRATOR-DESIGN` §17 to block this** | It must be argued head-on, and D-T3 does. §17's *"the view is dumb, so tests inject the message"* is **partly superseded** by CLAUDE.md §3.1: five lane actions were **unreachable**, not merely untested, and `MainWindow.Send` answered the first thing a person did with the literal words *"daemon not running"*. **Do not cite §17 to replace a UI wire check with an injected message.** |

---

## 8. RISKS, AND WHAT WOULD FALSIFY THIS PLAN

### 8.1 The honest size

**This is weeks at best, and possibly months.** ~560 checks are rewrites (D-T4), not moves. Nine
slices plus a seam barrier plus a pilot, each two commits, plus ~50 checked-in mutation patches, plus
the tooling. The largest single line item is not the writing — it is the **proof obligation**: every
moved check owes a recorded red, `dev prove` refuses `unit` today, and W3 is what makes that
mechanical rather than 560 break-and-reverts that will be skipped. Even with W3, batching is
compulsory: break `Claims.Covers` once and every reader reddens in one run.

**AND THE PLAN'S TWO COST FIGURES DISAGREE BY 5–9×, WHICH THE FIRST DRAFT ASSERTED WITHOUT
RECONCILING** (§10, finding 8):

| | checks | ESTIMATED sessions | rate |
|---|---|---|---|
| W5, the pilot | 4 | 2–3 | 1.5–2 / session |
| W8, the bulk | ~560 | 20–40 | 14–28 / session |
| **the bulk at the pilot's rate** | ~560 | **280–370** | — |

The 20–40 assumes a 7–19× batching speedup. Some speedup is real — a pilot pays every first-use cost
of the tooling once, and one mutant is meant to redden ~11 checks — but that much is not demonstrated,
and two costs sit outside both figures: **the mutant re-cut loop** (one patch must redden eleven
checks in C# while triggering no `expects-green`; that partition will not hold first try) and
**machine time**, which is not agent time (each `dev prove --with` builds a baseline and runs a full
suite; at this repo's measured `workspace` 74–87 s and `brain` 79–95 s, ~100 batched invocations is
hours of wall clock).

**So the plan states both and funds neither.** D-T31: the deciding number is **wave 1's measured
checks-per-session**, reported by the parent after three slices. That is a second cheap decision point
after the kill switch, and W6's rename barrier now sits *after* wave 1 so the irreversible churn is
not paid before the number exists.

A plan that hid this would be worse than one that admits it. The operator's directive is speed over
thoroughness, which is why **W5 exists as a cheap kill switch, wave 1 is a second one, and W1–W4 have
independent value even if the job stops at either**.

### 8.2 What if this is not worth doing — the smallest slice that proves or kills it

**The smallest slice is W1–W5**, and it is deliberately the cheapest thing that can answer the
question: one production keyword (`HandleShimLine` `private` → `internal`), **four** moved checks,
one tracked corpus file that already exists, and the tooling. ESTIMATE: **six to nine sessions** —
raised from five to seven because W3 now stands up the second test project and W4 is two sessions
rather than one (eight proved reds, two reflection classes, a real contract, a lint rung).

**What abandoning after W5 costs, and what it leaves behind.** Revert `S-WIRE`'s commit B and the
accounting closes itself, because a baseline name with no move row defaults to *still live in its
suite*. Keep commit A — one keyword, behaviour-neutral, its suites green. And keep W1–W4, which
have **independent value even if not one further check ever moves**:

- `dev ledger` catches a **duplicate check name silently overwriting a `$results` row and dropping
  the tally by one**, which nothing in the repo detects today and which `dev.ps1:584` records as
  outside the runner's job by decision. That is a free catch nobody has;
- `wires.tsv` is the first written statement of what the 750 checks are actually *for*, and it is
  useful to a reader whatever happens next;
- `dev prove --with` gives the repo a way to redden a check without a product defect, which the
  `unit` suite has never had;
- the double ledger anchors the doubles that already exist — `FakeRecognizer`, `Poses`,
  `RecordingTransport` in `DodonaUi`, and `DodonaFakeAgent` through the corpus rung, whose wire shape
  is unanchored **today**. **This bullet was FALSE as first written and the review is what caught
  it** (§10, finding 1): the mechanism enumerated `Assembly.GetExecutingAssembly().GetTypes()` from a
  net8.0 test project with one `ProjectReference`, so its population contained **none of the three**.
  It is true of the redesign because the population question is now answered by a repo-wide text
  scan, and rung-1 assertion 4 refuses a double in an assembly no reflection rung loads.

So the cheap question is answerable for the price of the tooling, and a "no" ends the job with the
repo strictly better off than it started.

### 8.3 What would falsify it — the four things that end the job at W5

Each is a concrete, observable outcome, not a feeling:

1. **The corpus mechanism cannot be built.** If `DodonaFakeAgent`'s emittable shapes cannot be
   enumerated (`--wire-sample`) or cannot be compared to real recorded bytes, then Constraint 2 is
   unsatisfiable for the largest fake in the tree and the anti-drift claim is a promise. **Stop.**
   Nothing has been deleted: W5's commit A is one keyword and commit B has not landed.
2. **`dev prove --with` cannot redden both languages under one patch.** If a `.ps1` check and a C#
   test cannot be shown red by the same named defect, the paired red does not exist and the
   faithfulness proof collapses to trusting 560 rewrites. **Stop**, and reconsider W3's design before
   anything moves. **This must be proved in BOTH test projects at W3** — `Dodona.Tests` (net8.0) and
   `Dodona.Ui.Tests` (net8.0-windows) — because four of the ten slices move checks whose destinations
   are UI types, and the pilot moves nothing into a UI project. Half-testing this falsifier at the
   kill switch was the first draft's mistake (§10, finding 16).
3. **The TRX `testName` format on this machine is not what W3 assumes** and neither fallback
   (`--list-tests`, console `[FAIL]`) gives a usable per-test identity. The ledger's unit-side
   identity depends on it. **Settle it in W3 and do not proceed past W5 unverified** — and settle it
   for a `[Theory]` ROW as well as a `[Fact]`, because §9.4's B1 turns every variation into one and
   the last-segment rule cannot match an appended argument list. That is a rule collision the first
   draft created by design and filed as a formatting question (§10, finding 17); §5.2's stripping
   clause is the fix and W3 is where the regex is written against a real file.
4. **`dev test unit` blows the operator's 1–2 s.** `StoreFixture` touches disk and `GitRepoFixture`
   starts processes, and **neither has been measured**. **This falsifier could not fire at the kill
   switch as first written** — both fixtures were scheduled in W8, three items past it and after the
   rename barrier (§10, finding 19) — so W4 now builds both as throwaways, measures, and reverts
   them, and W5 gets a second reading free because `raw_body_is_never_overwritten` needs a real
   temp-file `Store`. **The threshold is `dev test unit`'s own printed seconds, WARM, on a second
   consecutive run after a build**: CLAUDE.md §1 gives 1.9–2.3 s warm and ~5.6 s cold, while §1.4's
   in-gate table shows 5.3 s, which is the cold figure — so *"exceeds ~4 s"* with no warm/cold clause
   had already fired before any work began. If the warm total exceeds ~4 s, split into two suite names
   over the **same project** with xunit traits (`dev test unit` = `--filter Category!=Fixture`,
   `dev test fixtures` = `--filter Category=Fixture`) — one project, one compile, two doors — and
   **record the branch taken with its number**. If neither the budget nor the split holds, the lower
   layer is not free and the arithmetic of this plan changes.

And one falsifier at the far end: **if W10's five measured gates are not materially below the
measured median of 218.8 s**, the job bought correctness hygiene but not the speed the operator asked
for. Keep the seams and the ledger (they have independent value); stop widening.

### 8.4 The residual risks, named

1. **The paired red proves co-sensitivity, not equivalence.** A new unit test asserting one of the
   three things the old check asserted still passes B2. **Nothing mechanical closes this.** The
   mitigation is human: the six survey tables say what each check really asserted, and
   `dev ledger --origin` puts the original body one command away. This is the residual risk of the
   entire job. **The audit that mitigates it is now a RATE, not a count** (§10, finding 23): §9.7 rung
   6 said *"two or three of the moved checks"* per slice, which over ~56 moved rows a slice is a ~5 %
   sample of the one thing nothing mechanical can catch — and §8.1's compulsory batching makes the
   shared mutant coarser and the co-sensitivity signal weaker exactly where the sample is thinnest.
   It is **10 %, weighted toward the survey rows marked as asserting more than one thing**, and the
   subagent reports *"this check asserted N things; the new test asserts M"* per moved row so the
   parent can sample on `N > 1` rather than at random. **One misclassification has already been found
   this way**, in the first adversarial read of the surveys:
   `compression:midturn_narration_is_not_in_the_pane`, filed `unit` while asserting on a live
   window's rendered lines (W9).
2. **The corpus starts as a debt.** MEASURED: the seed has no `tool_use`, no `tool_result`, no
   `permission_denied`. Until W11 runs a directed session, `unwitnessed[]` carries most of the shape
   list. It is written into `MANIFEST.json`, not glossed.
3. **Nothing catches a shape `claude` starts emitting that we neither emit nor recorded.** That is
   D3 with no recording and it is unfixable automatically. The only mitigation is that
   `HandleShimLine`'s `catch` leaves an unparseable line as `kind=wire, body=raw` rather than dropping
   it, so a new shape degrades to a **visible raw row** rather than to silence — worth one check
   asserting exactly that.
4. **`window` inherits `voice`'s cascade signature.** Trigger stated in advance: run `window` in the
   wave and measure; if it reddens **twice in five** gates, or reddens in a wave where nothing else
   did, move it to `SoloSuites` **with the new measurement** — and **do not move `shell` with it**.
5. **The wire register is a hand merge of six survey files** and is the plan's single point of
   judgement: every `merged` row is built on it. It is cheap to redo and it is checked in and
   reviewable, but no tool can derive it.
6. **~~`S-DAEMONCMD` cannot declare a disjoint suite set~~ — DISSOLVED** (§10, finding 10). It was
   real: `HandleAsync` is 45 `case` labels reaching every suite, so the slice could not declare a
   disjoint suite set and had to run last and alone — while every check in another slice's suite whose
   seam lives in `Daemon.cs` was blocked behind it, and `stays (no-seam-yet) = 0` is a stop condition.
   `S-DAEMONSEAM` lands those seams in **wave 0** as commit A with no commit B, `S-DAEMONCMD` is
   abolished, and each slice then moves its own suite's command-shape checks. What remains true:
   `S-GATE` and `S-PUBLISH` both touch `Program.cs` and must be sequenced.
7. **Concurrent worktrees still contend** on windows, process starts and the `\\.\pipe\` namespace.
   One-window-suite-per-wave is a mitigation, not a cure. **Every red inside a wave must be re-run
   alone before it is believed**, or the migration will chase machine readings as if they were lost
   coverage.
8. **~~`dev ledger` as a gate row is an eleventh assertion~~ — REMOVED, not argued** (§10, finding
   15). The first draft promised in §3.4 that the count stays at ten, made it eleven in D-T23, and
   conceded here that it had. The static rungs fold into **Repo-Lint (I8)**, already one of the ten
   and already a sub-second static parse of tracked files: nothing widens and the refusal is real. If
   the ledger stops earning its place after the migration, the lint rows and the files go in one
   commit.
9. **Checked-in mutation patches will rot** as `src/` moves. Accepted: a patch that no longer applies
   makes `dev prove --with` **abort loudly** rather than pass, which is the correct failure
   direction, and re-cutting a one-hunk patch is minutes. **No refresh mechanism**, because that
   would be a second source of truth about the defect. This is one half of the refactor-durability
   price §17 warned about; D-T3 now prices the other half (~760 internals-facing unit methods) and
   argues the trade rather than leaving §17's second clause standing unanswered.
10. **`Anchor.Landing` is genuinely weak.** `RecordingTransport` proves which message would be sent,
    not that anything carries it — and the 2026-08-19 incident was a **call site that forgot to
    ensure**. A second `Landing` double carries the burden of showing why the real counterpart cannot
    run in-process at all.
11. **The renaming barrier (W6) cannot be split across commits**, and it touches every doc,
    `AllSuites`, `SuiteOrderHint`, `//verify`, CLAUDE.md's table and `.claude/skills/ship`.
12. **Some of the 750 may be pinning behaviour that should change.** Preserving a name is not
    endorsing its assertion (D-T22).
13. **This plan does not make the surviving integration tests less flaky.** Only having fewer of them
    does. Issue #3's root cause is still unrooted and this job does not claim to root it.
14. **`Anchor.Interface`'s corrected rule reddens `FakeRecognizer` on the day it lands**, and the
    timing half of that divergence is **permanently** unclosable in-process (§3.2). It is a counted,
    ticketed `KnownDivergence` and not a debt to be worked off. Anyone reading the gate's doubles line
    should read it as *"this many places where the fake is easier than the real thing and nothing
    automatic will tell you when that stops being safe."*
15. **The wire register moved on first contact — 49 to 52** — and every one of the three additions
    (E7, E8, J3) was a wire the first draft had simply left with no home or folded against CLAUDE.md's
    own capitalised distinction. Risk 5 says the register is a hand merge and the plan's single point
    of judgement; this is that risk observed rather than predicted. Expect the number to move again.

---

## 9. HOW TO RUN IT

### 9.1 The executing agent's literal first twelve steps

1. `dev worktree ledger0`; `cd .claude\worktrees\ledger0`; `dev check`.
2. **W1.0** — `git mv` the eleven working files into `docs/testarch/` and commit them. **Do this
   before reading further**: everything below cites them as the authority, and they are currently in
   a session scratchpad (D-T26).
3. **W1.1** — the cross-suite duplicate scan (`sort | uniq -d` over `Check '<name>'` plus `m0`'s
   `$results['<name>']`). Rename the two real collisions; record the false positive
   (`m2-acceptance.ps1:330`/`:334`, one name from two arms of an `if/else`) in
   `tests/ledger/README.md`, because it is the reason the uniqueness rule keys on runtime results.
4. **W1.2** — build `tests/ledger/wires.tsv` by merging the six survey `wires` arrays and collapsing
   the cross-group duplicates named in §4/W1. One owner per row. **52 rows expected.**
5. **W2** — implement `dev ledger` (static, `--capture`, `--live`, `--slice`, `--verdict`,
   `--origin`) and `Run-Unit`'s TRX logger. `baseline.tsv` is keyed on the **check name**.
6. `dev gate` once, green, on a clean machine. `dev ledger --capture` from that run → the frozen
   `baseline.tsv`, **958 rows**. Commit: tooling + surveys + wires + baseline. **No check has moved
   yet.**
7. **W3** — stand up `tests/Dodona.Ui.Tests` (empty, one trivial `[Fact]`), then implement
   `dev prove --with`. Verify by proving ONE existing check red under ONE mutant, and ONE red in the
   net8.0-windows project, before writing any new test. **Record the TRX `testName` format for a
   `[Fact]` AND for a `[Theory]` row, literally, in `tests/ledger/README.md`** — falsifier 3.
8. **W4** — the double ledger, both rungs. Prove the **eight** reds of §4/W4 by breaking them, one of
   which is red against the tree as it stands. Build the throwaway `StoreFixture` and
   `GitRepoFixture`, measure `dev test unit` **warm**, write the number down, revert them —
   falsifier 4.
9. Fold the ledger's static rungs into Repo-Lint (I8). **The gate stays at ten assertions.**
10. **W5** — run slice `S-WIRE` end to end, **alone**: commit A, then B0–B5. Four moved checks, four
    `no-seam-yet` rows. Report against §8.3's four falsifiers before doing anything else.
11. Only then **wave 0** (`S-DAEMONSEAM`), then **wave 1**. Report wave 1's measured
    checks-per-session (D-T31) — this is the second decision point.
12. **W6**, the rename barrier, after wave 1. Then waves 2–4.

### 9.2 Subagent decomposition

**One subagent per slice.** Not per check, not per suite: the slice is the unit of ownership, of
parallelism and of rollback, because it is what can declare a disjoint set of suites and `src/`
files.

Never more than one subagent per wave that owns a window suite (D-T19). Never two subagents in one
worktree. The waves are §4/W8's schedule.

### 9.3 Two commits, and where they happen

Every slice runs in **its own worktree**: `dev worktree <slice>` → `.claude\worktrees\<slice>`, its
own `bin` and `obj`. CLAUDE.md §0.0, and it is **enforced rather than remembered**: the `pre-commit`
hook aborts any commit made from the shared checkout, so an agent that forgets is stopped at the
moment that matters. `f9aaf25` is why — one working tree, two lanes, and `git add` cannot tell whose
edit is whose.

- **Commit A — the SEAM.** `src/` only. `private` → `internal`, an extracted `static` decision, or an
  injected probe with a convenience overload binding the real one (`Trees.cs:44` + `:77`, where
  production keeps exactly ONE path). No test changes, no ledger changes.
- **Commit B — the MOVE.** `tests/` and `tests/ledger/` only.
- **`S-DAEMONSEAM` is the one slice with NO commit B** (D-T29). It exists so the seams every other
  slice's checks depend on land once, in one file, before wave 1 — rather than being discovered
  mid-slice against a file another agent is holding. Its verification is `dev build` plus every suite
  green, because it changes no behaviour and asserts nothing new.

### 9.4 The six steps of commit B, and the order is load-bearing

**The old check must be seen red BEFORE it is deleted**, because after deletion there is nothing
left to see.

| step | what | command |
|---|---|---|
| **B0** | write the mutant; redden the OLD checks with it; record `red_old` verbatim | `dev prove --with <patch> <suite>:<old> ...` → all PROVEN |
| **B1** | write the new tests: **one C# method per old check, named exactly the old check name.** Variations become `[Theory]` rows. **Never fold two old names into one method** — if two truly assert the same thing, one is a `merged` row | — |
| **B2** | redden the NEW tests with the **same** mutant; record `red_new`. **This is the paired red** | `dev prove --with <patch> unit:<FQN> ...` → all PROVEN, no `expects-green` red |
| **B3** | delete the old checks, and any fixture they were the only consumer of. Leave the wire's owner and its fixture standing | — |
| **B4** | write the ledger rows into `tests/ledger/moves/<slice>.tsv` | — |
| **B5** | verify and commit | `dev build`; `dev test unit <owned suites>`; `dev ledger --live`; `dev lint` |

**If an old check will NOT redden under any mutation of the function it is supposedly about, STOP
and classify it** — do not force it. It is `vacuous-guard` (keep and LABEL), or it is misnamed (fix
the aim in a separate commit, or `renamed` with a note). Do not proceed by weakening the mutant.

The commit message states, in house style: what moved, the mutant and **both reds**, that
`dev prove` without `--with` is VACUOUS on this commit and why that is correct, and the arithmetic
(N names in, N out, M merged into named survivors).

### 9.5 Verification table

| step | command | pass condition |
|---|---|---|
| slice start | `dev worktree <slice>`; `dev check` | tree builds; nothing running in the build output |
| commit A | `dev build` | compiles |
| | `dev test unit <owned suites>` | **ALL GREEN — this IS the behaviour-preservation proof** |
| | `dev ledger` | green, unchanged |
| | — | `dev prove` deliberately NOT run; **say so in the commit message** |
| B0 | `dev prove --with <patch> <suite>:<old> …` | every one PROVEN; `red_old` recorded verbatim |
| B2 | `dev prove --with <patch> unit:<FQN> …` | every one PROVEN; no `expects-green` red; `red_new` recorded |
| B5 | `dev build`; `dev test unit <owned>`; `dev ledger --live`; `dev lint` | green |
| before merge to main | `dev gate` | **once, and only here** (CLAUDE.md §0.1: the heavy set has one standing reason) |

**Three consecutive failed verification attempts on a slice: STOP and report. Do not grind.** If a
suite the slice did not touch goes red, **suspect the machine first** and re-run it ALONE before
believing it (issue #3).

### 9.6 The per-slice contract

**What the subagent is TOLD** (all of it, explicitly, in the spawn message — a slice brief that
leaves any of these to inference is a slice that will collide):

1. **Slice id** and worktree name (`dev worktree <slice>`), and that CLAUDE.md §0.0 is enforced by a
   hook, not by memory.
2. **The seams it may create**, with `file:line` from `seams.md`, and the rule that commit A is
   `src/` only.
3. **The suites it owns exclusively** for the duration, and that it may not edit any other suite
   file.
4. **The `src/` files it may touch, WRITTEN OUT — no wildcards** (D-T30). Anything outside that set
   is a STOP-and-report, not a judgement call, and a wildcard makes that rule unusable: the first
   draft's `DodonaUi/*.cs` covered three other slices' declared files.
4b. **The SEAM SITES its checks depend on**, from the seam → slice map (D-T29), and the rule that a
   check whose seam falls outside its declared `src/` set is **not its work** — it is either
   reassigned before the slice starts or already declared `no-seam-yet`. Discovering this mid-slice is
   what the map exists to prevent.
5. **The list of old check names** it is responsible for, each with its survey verdict
   (movable / keep / unclear) and the survey file that is the authority on what it really asserts.
6. **The `wires.tsv` rows it may claim as owner**, and the rule that a `merged` row must name a
   survivor that is itself `kept` or a wire owner.
7. **The ritual**: §9.4's six steps, in order, and that the old check is deleted at B3 and **never
   before** B0 has reddened it.
8. **The refusals**: never fake `Store`, `Registry`, a git ref mutation, the pipe namespace, the
   forge, `GateHook`'s stdin, or the agent in-process (§3.5). Never a new package. Never a
   `Stub*`/`Mock*`. Never a second style of double.
9. **The stop rules**: three failed verification attempts → stop and report; a red in a suite it did
   not touch → re-run alone before believing it; an old check that will not redden → classify, do not
   force.
10. **Windows/PS 5.1**: `.ps1` files are UTF-8 **with BOM** and CRLF; `.cs` and `.md` are CRLF **no
    BOM**; the ledger TSVs are ASCII-only. `@(...)` around one-element pipelines; `ConvertFrom-Json`
    landed in a variable before filtering; `$procId` not `$pid`; native stderr collapsed with
    `-replace '\s+', ' '` before matching; parenthesise a call so it does not swallow arguments into
    `$args`; parse-check every `.ps1` (one that fails to parse never reaches `finally`). Note I8's
    known gap P1.8: it does **not** catch non-ASCII in a BOM-less `.ps1`.

**What the subagent RETURNS** (a structured report, not prose):

- commit A sha and commit B sha, and the branch;
- the exact content of `tests/ledger/moves/<slice>.tsv`;
- the mutant path(s) and, for every `moved` row, `red_old` and `red_new` **verbatim**;
- the output of `dev ledger --slice <name>`;
- the tallies from `dev test unit <owned suites>` at B5 (`<N> checks, <M> failed` per suite);
- `git diff --stat` between commit A's parent and commit B, so the parent can see exactly which files
  moved;
- **everything it could NOT move**, each with a closed-vocabulary reason, and each `no-seam-yet` one
  named with the seam it would need;
- anything it found that is mis-aimed, vacuous, or pinning behaviour that should change — reported,
  **not** fixed inside the migration commit.

### 9.7 What the parent verifies before accepting a slice

The parent does **not** take the subagent's word for any of it. In the parent's own worktree:

1. `dev ledger --slice <name>` — green: exactly-once accounting, closed vocabulary, the last-segment
   rule, wire resolution, reachability of every named C# method and every surviving `.ps1` check.
2. **Spot-check the paired red**: pick two `moved` rows at random and re-run
   `dev prove --with <patch> <old>` and `dev prove --with <patch> unit:<FQN>`. If either is not
   PROVEN, the slice is rejected.
3. `git diff --stat` against the declared `src/` set — **any file outside it rejects the slice**.
4. No suite file the slice does not own was edited.
5. `dev build`; `dev test unit <owned suites>` green. **A red in a suite the slice does not own is
   re-run alone before it is believed** (issue #3).
6. **Read the survey rows for 10 % of the moved checks against the new test bodies, weighted toward
   the rows the surveys marked as asserting more than one thing.** This is the only defence against
   §8.4's residual risk and it is not optional. It was *"two or three"* — a ~5 % sample over ~56 moved
   rows a slice, of the one thing nothing mechanical can catch, made thinner still by §8.1's
   compulsory batching (a coarser shared mutant is a weaker co-sensitivity signal). The subagent
   reports **"this check asserted N things; the new test asserts M"** per moved row (§9.6), so the
   sample is drawn on `N > 1` rather than at random.
6b. **After a slice that touched any of the thirteen `src/Dodona` files `DodonaUi.csproj` links
   (`:42-98`) merges, re-run `window` and `shell` on the merged result, alone** (D-T30). A window
   suite verified before that merge was verified against a `DodonaUi.exe` that did not contain the
   change.
7. `dev lint`.

Only then does the slice merge. `dev gate` runs **once**, before merging to main — not per slice, not
per wave (CLAUDE.md §0.1: the heavy set has one standing reason and this is it).

### 9.8 How slices avoid colliding in git

- **A worktree per slice.** `dev worktree <slice>` gives each its own tree, `bin` and `obj`.
- **The `pre-commit` hook aborts any commit made from the shared checkout.** Git runs it itself, so
  no tool choice evades it. `tools/dev.ps1` re-copies `.githooks/pre-commit` into `.git/hooks/` on
  every run, because `.git` is never cloned and an install step somebody must remember is not
  enforcement.
- **One `moves/<slice>.tsv` per slice** — no shared file, no merge conflict on the ledger.
- **Exclusive suite ownership and disjoint `src/` file sets**, declared before the slice starts.
- **One window-suite-owning slice per wave.**
- **Two barriers**: **wave 0** (`S-DAEMONSEAM`, `src/Dodona/Daemon.cs`, seams only, commit A with no
  commit B) and **W6** (the rename, every suite file). No slice may be in flight across either. W6
  now lands **after wave 1** so its irreversible churn is paid with one bulk slice of evidence behind
  it (§10, finding 20).

### 9.9 Rollback

Reversibility is **structural**, not procedural:

- A slice is exactly two independently `git revert`-able commits. Reverting B restores the deleted
  `.ps1` checks, removes the new C# tests and removes that slice's `moves/<slice>.tsv` — after which
  `dev ledger` is green again, **because a baseline name with no move row DEFAULTS to "still live in
  its suite."** The accounting closes itself on a revert. That is the property to preserve if these
  file formats ever change.
- Slices are per-worktree, so an abandoned slice is `git worktree remove` plus a deleted branch.
- Nothing is lost from history: `dev ledger --origin <check>` derives the deleting commit with
  `git log -S`. **The sha is never stored in the ledger** — a stored sha is a second source of truth
  that rots, which is the `.built-from` mistake CLAUDE.md §2 records.
- **Rollback trigger, stated so it is not a judgement call:** three failed verification attempts on a
  slice, OR a `dev ledger --live` failure that cannot be explained by the ledger's own contents.
  Revert commit B; **KEEP commit A** (the seam is independently good and its suites are green); write
  a `stays` row with `note=no-seam-yet …` naming the seam and the reason.

---

### 9.10 The stop condition

`dev ledger --verdict` (§5.5). **Finished** requires ALL of:

- `unaccounted = 0`, against a **958-name** baseline;
- `stays (no-seam-yet) = 0`, or every such row carries an open issue number on the tracker — note
  that W5 deliberately ends with **four** of them (§4/W5), so this is a stop condition for the JOB and
  never for a slice;
- every `moved` row has non-empty `red_old` **and** `red_new`;
- `live integration <= target`, where `target = rows(wires.tsv) + one harness row per surviving
  suite`;
- five measured `dev gate` runs at the new shape, recorded with their commit and date, and §2.5's
  estimate row replaced by them;
- every `[Double]` in the repo anchored, no `Anchor.Interface` standing alone, and every
  `KnownDivergence` and `unwitnessed[]` entry carrying an open issue number (§3.2, §3.4);
- `dev gate` green once, on main, at the end.

**Not finished** is any other state, and it is visible: `unaccounted` never rounds to zero on its
own, and each slice is a separate file in `tests/ledger/moves/`, so the verdict says which slices
exist and which do not.

---

## 10. THE ADVERSARIAL REVIEW, AND WHAT IT CHANGED

Kept in full, because this document's whole argument is that a claim in prose is not enforcement —
and the review's most valuable finding was that **the enforcement centrepiece was reflecting over an
empty set while three doubles sat outside it**. A plan that recorded only its corrected state would
be making the same mistake it was written to prevent. Every claim below was re-verified against the
working tree at `2ef0c54` before acting on it; where the reviewer's number was itself wrong, that is
said too.

| # | finding | disposition |
|---|---|---|
| 1 | **FATAL.** The double ledger enumerated `Assembly.GetExecutingAssembly().GetTypes()` from `Dodona.Tests`, whose one `ProjectReference` is `src/Dodona` — so its population contained none of `FakeRecognizer` (`src/DodonaUi/Recognizer.cs:73`), `Poses` (`src/DodonaUi/Poses.cs:9`) or `DodonaFakeAgent`. §3.6 tabulated all three as anchored. | **CONFIRMED and REDESIGNED.** §3.2 is two rungs now: a repo-wide **static** population scan in Repo-Lint that cannot miss an assembly, plus per-assembly reflection for the semantic questions. The reviewer's fourth assertion is adopted as `double-assemblies.tsv` + rung-1 assertion 4. `Dodona.Ui.Tests` moves to W3. D-T8, D-T26, D-T27. |
| 2 | **FATAL.** `Anchor.Interface`'s "≥ 2 implementers" is self-satisfiable: `IRecognizer`'s two are `DeepgramRecognizer` and `FakeRecognizer` — one of them is the double. | **CONFIRMED** by reading both files. The count now **excludes `[Double]`-carrying types** and the assertion **prints the survivors by name**. D-T9. |
| 3 | **FATAL.** `Interface` alone reaches D1 only, and the plan anchored `FakeRecognizer` as `Interface` while naming its behavioural divergence in the same row. | **CONFIRMED**, including the drift scenario (`Recognizer.cs:99-111` raises `Ready` synchronously; the real socket does not). `Interface` is **never a sole anchor**; every double needs a `Contract` or a ticketed `KnownDivergence` counted in the gate reading. `RecognizerContract` closes the arrival half against `DeepgramRecognizer` at a **closed loopback port** — real subject, no network, no quota; the timing half is permanent and labelled so. §3.2, D-T9. |
| 4 | **FATAL.** `Wire` verifies a name exists while §5.1 argues at length that a surviving name proves nothing. | **CONFIRMED.** Both remedies taken: D-T10's claim is downgraded to an anti-rot check on the register, and `wires.tsv` gains `owner_body_sha` over the `Wire '<id>'` block so a silent narrowing reddens the ledger. §3.3.1 states the limit — a hash is a speed bump, not a proof. |
| 5 | **FATAL.** The frozen baseline (`suite<TAB>check`, refuses altered rows) and W6's rename of all fifteen suites are mutually exclusive. | **CONFIRMED.** `baseline.tsv` is keyed on the **check name**; `suite` and `cases` are ordinary columns. The reviewer's other option — move W6 earlier — is **rejected** (§7.3): it makes finding 20 worse. |
| 6 | **SERIOUS.** Three of the pilot's eight seed checks are unreachable through the pilot's one-keyword seam. | **CONFIRMED and one more found.** `survey-daemon.md` blocker 8 says so for the three (verified at `Daemon.cs:4113`/`:4120`/`:4127`), and blocker 2 says the same for `compression:blocked_uses_the_fixed_schema`, which the reviewer missed. **The pilot is FOUR checks**; the cut four become its first `no-seam-yet` rows. Adding a spawner seam to W5 is **rejected** (§7.3): it ends the one-keyword property that is the pilot's whole claim. |
| 7 | **SERIOUS.** *"expensive fixtures: on the order of 200"* sat in the column headed **measured** and was invented. | **CONFIRMED.** Counted 2026-08-21: **54 `Start-Process` and 32 `Wait-Daemon`** across `tests/*-acceptance.ps1`. §2.5 carries both with the method, notes that neither counts an autostart-summoned daemon (so 54 is a floor), and states the honest multiplier as **~2.8×, not 10×**. The wall-clock estimate is re-stated as arithmetic over suite-seconds, which does not descend from the fixture ratio. |
| 8 | **SERIOUS.** The cost estimate is internally inconsistent by 5–9×, and two costs are missing. | **CONFIRMED.** §8.1 carries both rates in a table, names the assumed 7–19× batching speedup as undemonstrated, adds the mutant re-cut loop and the machine-time cost, and makes **wave 1's measured checks-per-session** the deciding number. D-T31. |
| 9 | **SERIOUS.** Slice disjointness is declared on files while the real coupling is `Compile Include`. | **SPLIT.** The **premise is rejected** (§7.3): every slice runs in its own worktree with its own `bin`/`obj`, so S-IDENTITY's edits cannot recompile S-UIVM's binary under it, and declared on assemblies the rule would forbid nearly every pair. The **real half is adopted**: it is a *merge-order* hazard, so the parent re-runs `window`/`shell` after a slice touching any of the thirteen linked files merges; and `DodonaUi/*.cs` is written out as an explicit list. Wildcards are banned outright, and the one file two slices must share (`Program.cs`, by `S-PUBLISH` and `S-GATE`, in disjoint regions) is sequenced and named. D-T30. |
| 10 | **SERIOUS.** `S-GATE` creates the `GateHook` seam but may not edit the `gate` suite; and a large block of movable checks is seam-blocked behind `Daemon.cs`, claimed only by a slice that runs last and alone. | **CONFIRMED structurally.** The reviewer's "31 rows" is not verified here — counted instead: **57 lines across the six surveys reference `Daemon.cs`**, a line count, not a row count. The remedy does not depend on the precision: `S-GATE` and `S-STORE` **swap suites**; **`S-DAEMONSEAM` becomes wave 0**, seam-only, commit A with no commit B; **`S-DAEMONCMD` is abolished**; and a **seam → slice map** is published and checked before wave 1. D-T29; §8.4 risk 6 dissolves. |
| 11 | **SERIOUS.** `compression:midturn_narration_is_not_in_the_pane` is classified `unit` but reads a live `ui dump`. | **CONFIRMED** at `compression-acceptance.ps1:92,96` against `StoreReader.cs:216-218`. It splits: the SQL projection `moved`, one assertion `merged` onto F10's existing window fixture. The general rule is stated in §2.2 — **an absence asserted through a window stays at a window** — and this is the one survey misclassification found so far, which is why §9.7's audit rate went up. |
| 12 | **SERIOUS.** Cross-suite duplicate check names create a silent-overwrite hazard at W6. | **CONFIRMED, with corrections in both directions.** There are **three** matches, not two: `presence_idle_after_result` and `double_uncertainty_asks_the_operator` are real cross-suite collisions; `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is **not** a duplicate — `m2:330`/`:334` are two arms of one `if/else`. The scan is **W1.1**, before any capture, and the uniqueness rule keys on **runtime `$results` keys**, exactly as the reviewer asked. |
| 13 | **SERIOUS.** m2's routing/presence half has no destination suite. | **CONFIRMED** — m2 has 30 distinct names and §2.2 claimed only its isolation half. The presence checks were never homeless (`survey-daemon.md` blocker 1 moves all seven down through the `ILaneSink` seam); the **routing** ones were. Two wires added: **E7** (the code-decided instant delivery path, `m2:tier0_message_delivered`) and **E8** (`unrouted_fallback_is_announced`, which CLAUDE.md §3 added after the silent degrade — E8 joins §2.3's never-skipped list). The verdict content moves down behind `S-DAEMONSEAM`'s `RouteInput` rung extraction. |
| 14 | **SERIOUS.** A7 folds two properties CLAUDE.md §3.2 capitalises as different, and one of them is open work. | **CONFIRMED.** Split into **A7** (summons nothing) and **J3** (`--root` names, does not adopt — asserted through `status`, i.e. through the resolution layer, per `workspace-acceptance.ps1:559`). §2.2 records issue #13's leaky summon list and says A7's `owner_check` is re-pointed in #13's own commit when the list becomes enforcement. |
| 15 | **SERIOUS.** §3.4, D-T23 and §8.4 risk 8 contradict each other on the gate's assertion count. | **CONFIRMED.** Neither of the reviewer's two options taken: the static rungs fold into **Repo-Lint (I8)**, already one of the ten, so the count stays at ten **and** the refusal is real. §7.3 records why the bare-reading option was rejected. D-T23. |
| 16 | **SERIOUS.** `dev prove --with` has no path for a UI test project, which four of ten slices need. | **CONFIRMED** at `dev.ps1:1290`. `Dodona.Ui.Tests` is created in **W3.0** and `$projects` gains both test projects, each behind its own prefix. W3's verification proves a red in a net8.0-windows test, so falsifier 2 is fully tested at the kill switch. |
| 17 | **SERIOUS.** The last-segment rule collides with `[Theory]`, and falsifier 3 frames it as formatting. | **CONFIRMED, and the arithmetic proves it independently of TRX format**: the parsed census reconciles only if `[InlineData]` rows count individually (176 `[Fact]` + 124 rows = 300 cases over 208 methods), so the runtime name set is per row. `destination` resolves against **method identity** with the argument list stripped; theory rows are attributed to their declaring method and never reach `added.tsv`; the baseline's unit side is keyed on the method with `cases` as a separate column. Settled at W3. |
| 18 | **SERIOUS.** `unwitnessed[]` is the escape hatch the plan denied itself everywhere else; and `dev wire-sample` only sees shapes the sample run exercised. | **CONFIRMED, both halves.** `unwitnessed[]` entries are `{shape, reason ∈ closed vocabulary, issue}`, counted in the gate's corpus reading, and refused in the same commit as the shape that needed them. **`--wire-sample` now ENUMERATES rather than samples** (D-T28). And §3.4 states which side of D-B7 `WireShape.cs` falls on: `src/Dodona` only, never linked into the fake agent, compared **as data** at lint time. |
| 19 | **SERIOUS.** Falsifier 4 cannot fire at the kill switch, and its threshold is inconsistent with the plan's own measurement. | **CONFIRMED.** Throwaway `StoreFixture`/`GitRepoFixture` are built, measured and reverted at **W4**; W5 gets a second reading free because `raw_body_is_never_overwritten` needs a real temp-file `Store`. The threshold is restated as **`dev test unit`'s printed seconds, warm, on a second run after a build** — CLAUDE.md gives 1.9–2.3 s warm against §1.4's cold 5.3 s, so *"exceeds ~4 s"* unqualified had already fired. |
| 20 | **SERIOUS.** W6 sinks irreversible churn before a single bulk slice has been attempted. | **CONFIRMED.** W6 moves to **after wave 1**; wave 1's slices declare the old suite names. Its I8 forcing function argued for one commit, not for early. This is possible only because finding 5's fix removed the baseline coupling. |
| 21 | **MINOR-SERIOUS.** The `//verify` change is called equivalent and is narrower. | **CONFIRMED.** W10 now says plainly that `unit gate land` **drops m1's R\* and m2's routing half**, gives the reason for each in the `//verify` block where its reasoning lives, and states the trigger for widening to `unit gate land review`. |
| 22 | **MINOR.** D-T3 answers §17's rule and not §17's reason. | **CONFIRMED.** D-T3 gains a paragraph pricing refactor durability: ~760 internals-facing unit methods against a compiler that names them all in one build, plus ~50 rotting patches — and it quotes and answers *"the test seam is the shim pipe"*, whose answer depends on the corpus surviving falsifier 1. |
| 23 | **MINOR.** The audit of the residual risk has no stated sample rate. | **CONFIRMED.** §9.7 rung 6 is **10 %, weighted toward survey rows asserting more than one thing**, and the subagent reports "asserted N, now asserts M" per moved row so the sample is drawn on `N > 1`. Finding 11 is the existence proof that this catches things. |
| 24 | **MINOR.** D-T6's single parser cannot enumerate the 22 loop-generated names. | **CONFIRMED** at `m1-acceptance.ps1:1167` and `concierge-acceptance.ps1:337` (the plan said `:307`; corrected). §5.4: reachability for a loop-generated name is satisfied by the **`--live` side only**, and the static rung must never claim one unreachable. |

**Nothing in the review was rejected outright.** One remedy was rejected on its mechanism (finding 9,
worktrees), three suggested options were rejected in favour of a different fix (findings 5, 6, 15),
and each of those four is a row in §7.3 with its reason, so it is not re-proposed. Three of the
reviewer's own numbers did not survive checking and are corrected in place: finding 10's "31 rows"
(unverified; 57 *lines* counted), finding 12's "two duplicates" (three matches, one of which is not a
duplicate), and finding 6's "eight seed checks, really five" (really four).

**What the review says about the plan's method, and it should be read as the finding it is.** The
errors it found were not spread evenly. They clustered exactly where the first draft **asserted
rather than counted**: an invented fixture number in a column headed *measured*; a seed set never
checked against the code; a mechanism whose population was never enumerated; a cost figure asserted
twice at two rates. Every one of those is the failure CLAUDE.md §0.3 names — *"believing a green
check"* — committed by a document about not committing it. The instrument that caught them was
reading the tree, which is the same instrument this plan asks every slice to use, and it is the
argument for §9.7's audit being a rate rather than a gesture.
