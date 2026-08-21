# THE WIRE LIST — the integration tests the repo keeps

Deliverable: the definitive, deduplicated list of expensive tests that survive, the suite shape
afterwards, and the mechanism that stops the list growing back.

Everything below is derived from the six survey tables, `seams.md`, `prior-art.md`, and the repo
itself. **Every check name in this document was verified to exist** by grepping
`C:\Users\devbl\Documents\personel\Dodona\tests\*.ps1` (two greps, 57 names, all found). **No
timing in this document is a measurement I took.** The only measured numbers quoted are ones the
repo already recorded, and they are attributed where they appear. The "est. s" column is an
estimate and is labelled as one everywhere it is used.

---

## 0. THE ANSWER IN ONE PARAGRAPH

**49 integration wires, on 11 suite files (10 acceptance + `unit`), holding roughly 190 named
acceptance checks against today's 750.** The other ~560 acceptance names move down a layer, taking
the unit layer from ~300 cases to ~860. Total named checks stay at ~1050 — that is Constraint 1's
proof, and §7 makes it a lint row rather than a promise. The wire list came from 109 candidate wire
rows across the six surveys; **60 of them were folded**, each with a recorded reason in §3, by
applying the operator's own rule: *if this wire were cut, would any other kept test go red? If yes,
it is not a distinct wire.*

**Why 49 and not 15.** 15 is reachable only by deleting the distinction between "a process died",
"a process never appeared", "a ref moved", "a socket opened" and "a window rendered" — five
different machines, each of which has already shipped a defect in this repo. 49 is what is left
after the fold rule stops deleting things. What makes 49 cheap is not the count: it is that 49
wires sit on **19 fixtures**, and an extra assertion on a fixture that already exists costs
milliseconds. The 750 exists because every assertion built its own fixture.

**The number that answers the operator's actual worry** — how many expensive *setups* run — is
**19 fixtures** (a daemon start, a window launch, a real build, a two-project workspace…), against
something on the order of 200 today.

---

## 1. THE FOLD RULE, AS APPLIED

Four tests, applied in this order to each of the 109 candidate wire rows:

1. **Upstream test.** If cutting this wire reddens another kept test, it is not distinct. It
   survives as a *named assertion inside* the test it is upstream of — the name is preserved, the
   fixture is not duplicated. (This is what deleted `lane-start really spawns a shim+agent`,
   `a real UIA tree exists`, `the poll reaches the window`, `publish builds a versioned dir`, and
   `a gate-hook subprocess denies`.)
2. **Same-machine test.** Two rows that need the *same* machinery and the *same* fixture, differing
   only in payload, are one wire. (`tier0_message_delivered` / `focus_message_delivered` /
   `routed_input_reaches_pane` / `midturn_tool_calls_reach_the_pane` are one wire with four
   payloads.)
3. **Cross-suite test.** A wire proved in more than one suite keeps exactly one owner. The owner is
   the suite whose *subject* it is, not the suite that happens to need it as a precondition.
4. **Content test.** If the assertion is a string, a row, a number, a shape, or a decision — it is
   not a wire at all and it leaves the acceptance layer entirely, per the survey verdicts.

**A wire that survives may register more than one named check.** That is not a loophole: the cost
of an acceptance suite is fixtures and process starts, not `Check` calls. §7's lint caps fixtures
(one `Wire` block per registry id) and leaves assertions uncapped, deliberately.

---

## 2. THE 49 WIRES

Columns: **id** · the path it proves · **THE check** (the single name that is the test) · suite
afterwards · fixture it rides · **est. s** (marginal, estimated, not measured).

### `lifetime` — a lane's processes, and what ends them (was `m0`, plus `m2`'s no-summon siblings)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| A1 | A daemon killed with `-Force` mid-turn leaves its shim and its agent alive; a replacement daemon adopts the orphaned shim, drains its buffer, and the result that was produced while no daemon existed lands — exactly once. | `m0:orphaned_result_landed` | F1 ws+lane, kill, restart | 12 |
| A2 | Only the child is killed: the shim exits by itself, and its pipe name and its `shim-lane<N>.json` go with it. | `m0:shim_exits_when_its_agent_dies` | F1 (2nd lane) | 4 |
| A3 | No daemon ever returns: the shim ends its own lease and takes the agent with it. | `m0:the_lease_takes_the_agents_too` | F2 lane, short `DODONA_SHIM_LEASE_SEC` | 8 |
| A4 | `stop-all --lanes` reaches a shim over its own pipe (`##shutdown`) and the child *tree* dies with it. | `m0:and_its_agent_dies_with_it` | F2 (2nd lane) | 4 |
| A5 | Lane liveness is asked of the OS, not of the record files: `ps` still counts a lane whose `shim-lane<N>.json` has been deleted. | `m0:ps_counts_a_lane_whose_record_is_gone` | F1 | 1 |
| A6 | The spawn-site environment reaches the agent two process hops away — the shim does not touch its child's environment. | `m0:a_lane_agent_is_told_its_workspace` | F1 | 1 |
| A7 | A command that promises a reading creates nothing: no daemon process, no lane, no registry row. | `m0:status_does_not_summon_a_daemon` | F3 (no daemon at all) | 3 |

### `gate` — work isolation (was `m2`'s isolation half + `m1`'s G1/G3)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| B1 | A real `gate-hook` subprocess, fed real bytes on stdin (BOM included), reaches the daemon over the ctl pipe, REFUSES a plain lane's write into the shared checkout, and the refusal promotes the lane: a real worktree appears, the agent process is killed and respawned inside it with its session resumed and its briefing rebuilt as a ticket lane's. | `m2:the_lane_ends_up_in_a_worktree` | F4 ws + plain lane + git repo | 15 |
| B2 | The undo removes the worktree directory and the branch ref — after the agent has let go of the cwd, which is the Windows race `WaitLaneProcessesGone` exists for. | `m2:the_undo_prunes_the_worktree_and_the_branch` | F4 | 5 |

### `land` — the merge (was `m1`'s M/V/D/A + `workspace`'s land)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| C1 | The daemon runs a real `git merge main` then `merge --ff-only` and advances **exactly one** repository's main, carrying the branch's work; the worktree is pruned, the branch deleted, the agent standing in the pruned worktree retired, and the other repository's ref does not move. | `m1:main_is_now_the_merge_that_was_verified` | F5 **two-repo** ws + ticket + agent | 12 |
| C2 | The configured `verify` runs as a real child process in the worktree and its exit code gates the ref advance **before** it — a red verify leaves main where it was. | `m1:red_verify_leaves_main_unchanged` | F5 | 5 |
| C3 | The land consults `SilentDrops` over the branch's *real* merge history and refuses a merge that succeeded and lost work. | `m1:a_silent_drop_is_refused` | F5 | 5 |
| C4 | The land runs off the serial control pipe: the daemon still answers other commands while a land's verify is running. | `m1:say_answers_during_a_land` | F5 | 5 |

### `review` — a finished ticket (was `m1`'s R1/R2/R3 + `ui-ask`'s land ask + `brain`'s manager)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| D1 | A ticket turn that ends with the worktree moved crosses the shim wire, fires the per-turn hook at the spawn site, produces a completion record, and opens a `land` question row — without waiting for any review to do it. | `ui-ask:the_approval_ask_does_not_wait_for_a_review` | F6 ws + ticket lane + real commits | 10 |
| D2 | That record reaches a real manager agent and its send-back returns to the working lane as input on the same path a typed sentence takes; a manager `approve` grants nothing and answers nothing; a reviewer that names a changed file is handed that file's content off disk, once. | `brain:a_send_back_reaches_the_lane_as_input` | F6 | 12 |
| D3 | The send-back bound is counted in the STORE, so three is still three after a real daemon restart mid-loop. | `brain:three_send_backs_is_the_bound_and_it_survives_a_daemon_restart` | F6 + restart | 10 |

### `brain` — utility lanes exist correctly (was `brain`'s W16/W17/W18/W19 + `workspace`'s ladder)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| E1 | A daemon started **the way the operator runs it** — autostart on, nothing pre-built by the test — creates the classifier itself, a typed sentence reaches THAT lane, and the lane it places opens in the project the classifier chose. | `brain:typed_input_reaches_the_classifier_autostart_made` | F7 **two-project** ws, autostart ON | 15 |
| E2 | A real daemon dies and restarts, and reconcile ADOPTS every live utility lane over its pipe — one per project — spawning none and retiring none. | `brain:restart_adopts_a_brain_for_every_project` | F7 + restart | 12 |
| E3 | A live predecessor whose pipe cannot be connected is never duplicated and never written off as gone, and one project's wedge does not block another project getting a brain. | `brain:no_second_brain_beside_a_live_one` | F7 + a held client slot | 10 |
| E4 | A registry mutation reaps the live agent processes it orphans: detaching a project stops its lanes and its brain shim, and forgetting a workspace stops its now-orphaned daemon. | `workspace:detaching_a_project_stops_the_lanes_that_were_in_it` | F7 | 8 |

### `assist` — what a second agent does behind the fast path (was `brain`'s W20 + `workspace`'s hold + `compression`)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| E5 | A typed input causes a real brain turn *behind* the instant code path, and its verdict (the rename) lands on the lane row after the fact without gating anything. | `brain:rename_applied` | F8 ws + lane + brain | 8 |
| E6 | Genuine uncertainty HOLDS the sentence: it becomes a `questions` row that outlives a daemon restart, and answering it spawns the lane in the chosen project and delivers the stored words verbatim. | `workspace:the_held_sentence_itself_reaches_the_new_lane` | F8 + restart | 12 |
| L1 | The compressor pool starts real sessions, a turn-final crosses to one over a real shim pipe, and the rewrite lands back on the row as `compressed`. | `compression:turn_final_gets_compressed` | F9 ws + pool + lane | 15 |

### `window` — one workspace, one live window (was `ui-grid` + `ui-ask` + `voice` + `m3`)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| F1 | Text typed in the window's box crosses the UI pipe to the daemon, which creates and routes a lane, delivers the sentence to a real agent running where the daemon put it, and the agent's own answer — its text, its tool calls, its out-of-band wire fields — comes back into the pane. | `ui-grid:the_newline_survived_to_the_agent` | F10 ws + `--test-window` | 12 |
| F2 | Enter through the **real** `PreviewKeyDown` path submits and Shift+Enter does not — the keystroke raised as a routed event on the real TextBox, so WPF's class handler is in the way. | `ui-grid:enter_still_sends` | F10 | 2 |
| F3 | A real WPF layout measures the multiline box: it opens at three lines, drags taller, and the FEED gives up the pixels so the window never moves. | `ui-grid:the_box_opens_at_three_lines` | F10 | 3 |
| F4 | A real UIA `Invoke()` on a rendered control reaches `MainWindow.LaneAction` and the daemon acts — across all five tile actions. | `ui-grid:close_button_stops_the_lane` | F10 | 4 |
| F6 | Nothing lives in the window's memory: a relaunched window process restores collapsed lanes from the store and the box height and listen toggle from `ui.json`. | `ui-grid:collapse_survives_reopening_the_window` | F10 (2nd launch) | 5 |
| F7 | A real window renders deterministic pixels at a fixed size — `RenderTargetBitmap` over the Window, not a margined child. | `m3:screenshot_fixed_size` | F10 | 2 |
| F9 | The mic affordance: a real UIA button invoke and the `ui listen` verb both land in `SetListening`; a real socket to a closed loopback port reads as **error**, not listening; and `Starting` has a deadline that fires. | `voice:clicking_the_mic_toggles_listening` | F10 | 6 |
| F10w | `ui heard` lands in `MainWindow.OnHeard` — the real engine's own landing site — and nothing the decision layer can produce submits the box. | `voice:spoken_send_words_do_not_submit` | F10 | 3 |
| G1 | `dodona ui answer <v>` → `AnswerAsk` → the daemon's `answer` → the row closes, the overlay follows it down, and that question kind's real effect runs (a real `git init` with a first commit; a merge token actually granted). | `ui-ask:answering_the_approval_ask_grants_the_merge_token` | F10 | 8 |
| G2 | The overlay's choices exist in the live UIA tree as elements named `ask:<value>` — the mechanical link between the pixels and the value the verb takes. | `ui-ask:a_choice_is_a_real_button_a_person_can_click` | F10 | 2 |
| G3 | Esc is view state only: the overlay goes down and the question row stays open and answerable. | `ui-ask:escape_puts_the_ask_down` | F10 | 2 |

### `shell` — one window over N workspaces (was `ui-shell` + `ui-wake`)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| H1 | A bare `DodonaUi.exe` launch is the SHELL booted to zero — no dialog, no folder picker — it notices a workspace daemon that came up in another process with no operator action, and the `workspace` verb (the method a band click lands in) swaps the grid. | `ui-shell:a_waking_workspace_takes_the_grid` | F11 two ws + shell window | 12 |
| H2 | A write from the window starts a **sleeping** workspace's daemon on demand — a real process comes into existence — on both the input-box path and the lane-tile path, and never answers "daemon not running". | `ui-wake:a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts` | F12 sleeping ws + window | 8 |

### `publish` — the swap (was `m4` + `publish` + `ui-shell`'s hot swap)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| I1 | A real MSBuild publish lays down one stamped versioned directory; daemon A hands off mid-turn to daemon B from that build and exits; the shim, the agent, the session and the in-flight turn survive with no loss and no double delivery; and the new daemon reports the new build and the commit MSBuild stamped into it. | `m4:landed_exactly_once` | F13 real build + ws + live lane mid-turn | 40 |
| I2 | A candidate binary that cannot answer `version --json` is refused, and the system it was going to replace stays up. | `m4:daemon_alive_after_bad_swap` | F13 | 5 |
| I3 | A blocked swap arms itself and fires when the real blocker clears — and not before. | `m4:armed_swap_fires_when_blocker_clears` | F13 | 6 |
| I4 | `GcOldBuilds` runs during a real reconcile and its retention vetoes the directory a live lane's deployed gate names — because a missing gate exe is a write gate that silently fails open. | `m4:the_gate_exe_of_a_live_lane_survives_the_swap` | F13 (2nd swap) | 6 |
| I5 | `--all` resolves targets from the REGISTRY: a live daemon belonging to another registry is neither named nor touched, and is still answering afterwards. | `publish:foreign_daemon_survived_untouched` | F14 foreign registry + daemon | 6 |
| I6 | Every target reports an outcome: a target that connects, reads and answers **nothing** is NAMED and publish exits non-zero. | `publish:publish_names_a_target_that_did_not_take_the_build` | F15 bare pipe server on a registered ctl pipe | 3 |
| I7 | The concierge takes the swap over its own ctl pipe and the new process reports the new build. | `publish:a_swapped_concierge_reports_the_new_build` | F16 concierge | 5 |
| I8 | A live shell **window** hot-swaps: it spawns a successor from a different binary path, hands off, and the successor serves the shell pipe as `--shell`. | `ui-shell:shell_successor_is_the_new_binary` | F13 + shell window | 8 |
| I9 | A build with no provenance refuses to watch, out loud, on a daemon started the way the operator starts one. | `publish:no_provenance_daemon_refuses_to_guess` | F17 `dev build` image, autostart cleared | 5 |

### `identity` — what is on disk, and the machine-global process (was `workspace`'s store half + `concierge`)

| id | the one-sentence path | THE check | fixture | est. s |
|---|---|---|---|---|
| J1 | An older store on disk is copied before it is migrated when a **real daemon** opens it, and the daemon comes up — a migration that throws would kill it in its constructor, before the control pipe exists. | `workspace:a_pre_v9_store_is_copied_before_it_is_migrated` | F18 v8-shaped store | 8 |
| J2 | A legacy `<root>\.dodona` store is physically relocated into workspace territory on first explicit resolve — the files really leave one directory and arrive in another. | `workspace:migration_moved_the_store` | F18 | 4 |
| K1 | The concierge is a real machine-global process on its own control pipe; it consults a tier agent child and acts on the answer; it stops when told. | `concierge:fuzzy_match_on_the_cheap_tier` | F19 concierge + tier agent | 12 |
| K2 | The concierge reviews an already-delivered sentence from behind and its verdict lands in the feed later. | `concierge:review_behind_reports_a_group_misroute` | F19 | 6 |

**Wire count: 7 + 2 + 4 + 3 + 4 + 3 + 11 + 2 + 9 + 4 = 49. Fixture count: F1–F19 = 19.**

### Harness invariants — mechanisms, not wires

These are written by `tests/_workspace.ps1` in every suite's `finally`, register a named check row
each, and are not counted among the 49:

| name | scope | why it is a mechanism and not a test |
|---|---|---|
| `no_process_left_in_the_build_output` | every suite | already exists (`_workspace.ps1:353/371`); per-suite by construction — it reports what *this* suite's `finally` left behind, so it cannot be deduplicated. |
| `no_modal` | every suite that opens a window | **promoted from `voice:no_modal_when_the_mic_fails`.** Counting top-level windows owned by the UI pid is the only detector in the repo that works: measured 2026-08-20, a real `MessageBox.Show` was up and `ui-ask:the_ask_is_not_a_modal` passed, because a Win32 modal pumps its own message loop and the dispatcher keeps answering. Making it a teardown assertion applies the working detector to every window fixture instead of one. |
| `no_socket_from_the_ui_process` | every suite that opens a window | **promoted from `voice:mic_off_opens_no_socket` + `a_suite_cannot_authenticate_even_with_the_real_engine`.** `Get-NetTCPConnection` on the real UI pid. Its recorded red was `state=[listening] engine=[deepgram]` — a test run authenticating on the operator's own bill. This must never be reachable by a config change, so it moves into the harness beside `DODONA_UI_MIC=off`. |

---

## 3. WHAT WAS FOLDED, AND WHY — the rejected wires

Recorded here in the house style so nobody re-proposes them. 60 candidate wire rows were folded.
Every folded row's *check names survive* — as assertions inside the kept test, or in the unit layer
— per Constraint 1. Only the **fixture** disappears.

### Folded by the upstream test (cutting it reddens a kept test)

| folded candidate | folds into | reason |
|---|---|---|
| `lane-start really produces a live shim+agent with a record on disk` (m0 W14, m1 L, m2 `promotion_lane_started`, m3 W5, workspace's child-agent wire) | every wire that carries an agent | If a lane cannot spawn, A1, B1, C1, D1, E1, F1 and I1 are all red. It is the fixture, not a wire. `m0:daemon1_killed_mid_turn` — which asserts nothing about the kill, only that the record exists — becomes the precondition assertion in A1. |
| `a PreToolUse gate-hook subprocess denies a write` (m1 G1, 21 checks; m2 W11) | **B1** | The promotion cannot happen unless the hook denied. `m2:the_write_is_still_refused` stays as the upstream assertion in B1, and B1's stdin must carry a UTF-8 BOM — that is `seams.md` §3.4's "keep one integration wire that pipes real bytes into the real exe", satisfied without a second fixture. All ~20 verdict/wording branches are content and go to unit behind seam S11. |
| `the UIA tree publishes named controls` (ui-grid W4: `resize_grip_exists`, `close_button_exists`, `collapse_button_exists`; ui-ask `the_ask_window_is_findable`) | **F4 / G2** | If the tree has no named controls, F4 and G2 cannot find them and go red. Preconditions, kept as assertions. |
| `a store mutation made outside the window reaches the live render` (ui-grid W5) | **F1** | F1 reads the pane through `ui dump`, which only changes when the poller refreshes. `undo_stops_the_lane` survives as an assertion in F1. |
| `publish invokes MSBuild and lays down one stamped dir` (m4 W9a) | **I1** | Nothing in `publish` can run without it. |
| `the commit crosses publish → MSBuild → Ver.Commit` (m4 W10, 3 checks) | **I1** | Same build, same swap; asserting the stamp costs nothing extra. The `~`-separator and `IncludeSourceRevisionInInformationalVersion` traps are string-level and unit-testable behind a `Ver.Parse` seam, but the *stamped value* only exists after a real build, so the assertion stays here. |
| `a fresh daemon reattaches to a shim that outlived its predecessor` (m4 W14b) | **A1** | Identical to A1's adoption, reached by a different trigger. |
| `the land kills the agent standing in the pruned worktree` (m3 W6, `land_retires_the_agent`) | **C1** | On Windows the prune *cannot succeed* while the agent's cwd is inside it, so C1's prune assertion already tests the kill. |
| `a live UI process reads a real store and answers ui verbs` (m3 W1, 23 checks) | **F1** | The whole `window` suite is red without it. |
| `an open questions row reaches a live window's overlay` (ui-ask W1, 5 checks) | **G1** | `AnswerAsk` needs the rendered ask to know which row; if the overlay never renders, G1 is red. `the_window_renders_the_open_question` and `the_overlay_closes_when_the_row_closes` survive as assertions in G1. |
| `a bare launch is the shell and serves the shell pipe` (ui-shell W9) | **H1** | H1 cannot address the window otherwise. |
| `the window outlives its daemon` (ui-wake W13) | **H2** | The suite already records it VACUOUS against HEAD and keeps it as H2's precondition. Kept, labelled, not a fixture of its own. |
| `ui close terminates the UI process` (m3 W8) | **F1** teardown | An assertion at teardown, not a wire. |
| `ticket-create runs a real git worktree add` (m1 W1, workspace's worktree wire) | **C1** / **B1** | Both fixtures create one; without it neither test can run. `worktree_belongs_to_its_repo` becomes an assertion in C1 (which is two-repo, so it still proves the worktree sits beside the right repo). |
| `repo-init runs a real git init` (workspace) | **G1** | It is the repo-init ask's real effect and G1 asserts it there. |
| `start-on-demand spawns a real daemon from the CLI` (m4 W14a) | **I1** / **H2** | Every acceptance suite in the repo runs `dodona` verbs against a stopped daemon; the call site that actually broke (2026-08-19) was the window's, which is H2. |

### Folded by the same-machine test (same fixture, different payload)

| folded candidates | folds into | reason |
|---|---|---|
| `m2:tier0_message_delivered`, `m2:focus_message_delivered`, `m0:same_agent_answers_daemon2`, `m3:routed_input_reaches_pane`, `m3:panes_replay_store_rows`, `m3:midturn_tool_calls_reach_the_pane`, `ui-grid:the_agent_answered` / `second_message_delivered` / `quota_line_from_wire` / `a_collapsed_lane_still_works`, `workspace:the_agent_process_really_runs_in_that_project`, `compression:no_pool_still_shows_the_agents_words` | **F1** | One wire — text in, agent out — with different payloads (a newline, a tool call, an out-of-band quota field, a collapsed tile, a project cwd). The window entry is kept as THE test because it is the longest path and the operator's own. **F1 must be strengthened**: `the_newline_survived_to_the_agent`'s comment claims "all the way to the agent's stdin" but its assertion reads `pane_events`, so today it proves UI→daemon only. It becomes the end-to-end test by asserting the *agent's own echo* carries the newline. |
| `m4`'s 14 handoff checks (`publish_hands_off`, `old_daemon_exited`, `new_daemon_serves`, `new_daemon_is_published_build`, `shim_survived_swap`, `agent_survived_swap`, `inflight_turn_landed`, `seqs_contiguous_across_swap`, `same_session_after_swap`, `same_agent_answers_new_daemon`, `handoff_in_causal_chain`, `successor_recorded_predecessor`, `agent_survived_second_swap`) | **I1** | One handoff, fourteen readings. All fourteen names stay as assertions on the one fixture. |
| `m1`'s 26 M-riding checks and `workspace`'s four land checks (`lands_in_its_own_repo`, `engine_main_advanced`, `tools_main_untouched`, `second_repo_lands_too`) | **C1** | Making C1's fixture a **two-repo** workspace absorbs `workspace`'s land wire at zero marginal cost, and turns "exactly one repo advanced" into two assertions on one land instead of a second suite's fixture. |
| `brain`'s 9 W17 checks and 6 W18 checks | **E2 / E3** | Two failure classes (the `_brainLo` scalar at N=2; the wedged predecessor) on one two-project fixture. |
| `voice`'s 9 W10 checks driving `ui heard` | **F10w** | The splice content (`Dictation.Splice`, `Decide`, `ShouldDrop`, `CanTransition`) is pure and answers in ~10 ms; the wire is that `ui heard` lands where the engine lands and that nothing can submit. |
| `m0:dead_lane_pipe_leaves_the_namespace`, `m0:shim_record_dies_with_the_shim` | **A2** | Two OS-observed consequences of one shim exit. **The ordering must be preserved**: assert the PROCESS is gone first, then the namespace — `\\.\pipe\` blinks (8 of 192 reads over 1.5 s saw no pipe while the shim was alive), and `m0-acceptance.ps1:171/241/286` each carry that reasoning today. |
| `m0:lease_expires_when_no_daemon_ever_returns`, `and_the_lane_pipes_are_gone` | **A3** | Same lease expiry, three readings. |
| `voice:listening_toggle_persists`, `ui-grid:the_box_remembers_the_size_i_set` | **F6** | One wire: a *new process* reads back what the old one wrote. Three payloads (collapse state, box height, listen toggle) over one relaunch. |
| `m3:pane_screenshot_writes`, `pose_screenshot_deterministic`, `ui-ask:the_ask_renders_to_a_screenshot` | **F7** | One renderer. `the_ask_renders_to_a_screenshot` is the survey's one UNCLEAR in that group and it asserts only that a file exists — it becomes an assertion here rather than a wire, which resolves it. |
| `publish:both_daemons_are_answering`, `the_foreign_daemon_really_is_live`, `all_never_swaps_a_workspace_from_another_registry`, `foreign_daemon_still_answers_its_pipe` | **I5** | `foreign_daemon_still_answers_its_pipe` is explicitly the weaker half (a *successor* would also answer); the survivor is the one asserting the original pid never died. |
| `publish:the_old_concierge_process_is_gone_after_the_swap`, `the_mute_target_really_is_live` | **I7 / I6** | Preconditions and process halves of the same two facts. |
| `m4:bad_binary_refused`, `publish_refuses_unrunnable_binary` | **I2** | |
| `m4:daemon_did_not_swap_while_blocked`, `armed_but_not_yet_swapped` | **I3** | |
| `m4:old_builds_gcd_except_those_a_live_gate_names`, `the_retained_build_is_the_one_a_live_gate_needs` | **I4** | |
| `ui-shell:shell_ui_update_hands_off`, `shell_survives_the_swap` | **I8** | |
| `ui-wake:typing_wakes_a_sleeping_workspace`, `typing_at_a_sleeping_workspace_never_says_daemon_not_running`, `a_lane_click_never_says_daemon_not_running` | **H2** | Two entry points, one "a daemon appears" fact; both entries kept as assertions on one fixture. |
| `ui-shell:bare_launch_is_the_shell_booted_to_zero`, `band_click_swaps_the_grid` | **H1** | |
| `ui-grid:the_box_grows_past_the_default` / `shrinks_back` / `drags_taller` / `double_click_refits` / `a_taller_box_never_resizes_the_window` | **F3** | One layout pass. `a_taller_box_never_resizes_the_window` carries the better incident but **passes vacuously if nothing renders**, which is why `the_box_opens_at_three_lines` (a measured `fit` pinned to 55..85, `height == fit`, `sized == false`) is the survivor. |
| `ui-grid:collapse_takes_it_out_of_the_grid`, `m3:respawn_actually_respawned` / `wake_revives_the_lane` | **F4** | The five tile actions are one wire (`LaneAction`) with five verbs. |
| `ui-ask:the_ui_answer_verb_reaches_the_ask`, `answering_records_the_answer_on_the_row`, `answering_yes_actually_creates_the_repo`, `the_repo_has_the_first_commit`, `the_answer_is_recorded_as_the_operators_own_approval`, `a_project_choice_is_a_real_button…`, `the_ui_answer_verb_reaches_a_routing_question` | **G1 / G2** | One answer path, three question kinds. Which kinds exist, what they offer and what they refuse is `Ask.*` and goes to unit — where `AskTests` (PureLogicTests.cs:1454) already round-trips the real writers through the real parser. |
| `ui-ask:putting_it_down_does_not_answer_it` | **G3** | The negative half of one keypress. |
| `workspace:the_project_hold_opens_a_question_row`, `answering_the_project_question_opens_the_lane_there`, `ui-ask:the_routing_question_reaches_the_operators_window`, `the_routing_overlay_closes_when_its_row_closes`, `answering_in_the_window_delivers_the_held_sentence_to_the_chosen_project`, `the_words_the_operator_typed_are_what_the_agent_receives`, `brain:new_task_spawns_its_own_lane` / `new_task_lane_gets_the_message` / `escalated_new_task_spawns_a_lane` | **E6** | One wire: hold → row → survives a restart → answer → lane in the chosen project → the stored words arrive. `brain:held_input_invents_no_lane` and `ui-ask:a_rendered_routing_question_still_invents_no_lane` are the same claim from two fixtures and the ui-ask comment says so. |
| `workspace:several_live_projects_reach_the_cheap_tier`, `the_lane_opens_in_the_project_the_classifier_chose`, `autostart_builds_the_classifier_the_ladder_will_use`, `the_project_ladder_is_live_on_the_path_the_operator_uses`, `brain:autostart_creates_a_classifier`, `routing_did_not_fall_back_to_focus` | **E1** | The operator-path guard and the ladder's act-on-the-answer are one fixture once E1's workspace is two-project. |
| `brain:restart_adopts_the_classifier_it_already_had` / `restart_does_not_leak_a_second_classifier` / `restart_adopts_the_brain_it_already_had` / `restart_does_not_leak_a_second_brain` / `brain_start_after_restart_reuses_it` / `two_projects_get_two_distinct_brains` / `no_healthy_brain_is_retired_as_a_surplus` / `brain_is_warm_and_off_grid` | **E2** | |
| `brain:a_detached_projects_brain_is_reaped` / `a_detached_projects_brain_shim_really_exits`, `workspace:forgetting_a_workspace_stops_its_agents` / `forgetting_a_workspace_stops_its_orphaned_daemon` | **E4** | One wire: a registry mutation reaps what it orphaned. Three mutations (detach, forget-lanes, forget-daemon), one fixture. |
| `brain:a_completed_ticket_turn_reaches_the_manager`, `a_manager_approval_grants_nothing`, `a_manager_verdict_never_answers_the_approval_ask`, `a_review_that_asks_for_a_named_file_is_given_that_file_and_reads_it`, `m1:the_manager_review_starts_no_model_agent_when_autostart_is_off` | **D2** | One review loop; the absence proofs and the extra rounds are free on the same fixture. |
| `m1:a_finished_turn_produces_a_completion_record` and its 9 R1 siblings; `m1:an_adopted_lane_still_produces_a_record_after_a_daemon_restart` | **D1** / **A1** | The spawn-site hook is what D1 rides; the *adoption*-site hook is a different call site and rides A1's restart, where the fixture already exists. |
| `brain:pulse_on_arrival` / `pulse_fades` | **F1** | A transient at a live window that must be caught while it is on; F1 already delivers a message to a live window, so the pulse is two assertions there. **Do not move it to unit** — the fade is timing. |
| `concierge:concierge_answers_its_pipe` / `concierge_stops_gracefully` | **K1** | Precondition and teardown of one process. |
| `compression:pool_is_two_sessions_not_one`, `compression_is_recorded_as_an_event`, `overlay_selected`, `overlay_keeps_midturn_and_full_text` | **L1** | `overlay_keeps_midturn_and_full_text` **never reads the overlay** — it counts `agent_line` rows. It becomes a unit test of `StoreReader.Tail(all: true)`, which is the function its name is about. |
| `workspace:migration_moved_the_shim_info` | **J2** | Same move. |
| `workspace:multi_member_worktree_sits_beside_its_own_member` | **C1** | |
| `workspace:the_window_shows_both_projects_lanes` / `the_window_and_status_agree_about_a_lanes_project` | unit (`Dodona.Ui.Tests`) | The survey says the only thing this adds over F1 is a two-project fixture, and the field itself is `Projects.Field`. The real claim — *the UI has not grown its own copy* — is a round-trip through both real sides (the `ui dump` serializer and the `status` renderer must both call `Projects.Field`), which is exactly the `AskTests` pattern and belongs in unit. |
| `workspace:a_drifted_ticket_still_refuses_a_path_it_does_not_own` | **A1** | "Nothing was cached in memory" needs a restart, and A1 is the restart. |
| `m0:reconcile_does_not_knock_on_pipes_that_are_gone` | **A1** | A wall-clock budget over the pipe namespace, observable only as elapsed time. Given its own fixture it is issue #3's flake shape; on A1's restart it is one generous-budget assertion. |
| `workspace:inherited_cwd_creates_no_workspace` | **A7** | Same claim as the no-summon list from a different direction — *a command that must not create anything, created nothing*. This is the MassWorks incident's check (CLAUDE.md §0.1: "does not summon a daemon" and "does not write" are different properties), and putting it beside `status_does_not_summon_a_daemon` is where a reader will look for it. |
| `m0:landed_exactly_once` | **A1** | **And it must be fixed while moving.** Today the fixture never creates a redelivery — daemon #1 died *before* the result line existed, so daemon #2 replays it once and the check cannot fail. A1's fixture is changed to kill the daemon *after* the result line exists but before it is acknowledged, which makes the name true. (The `UNIQUE(lane_id, seq)` dedup itself is a two-insert Store test and is free; both are kept.) The name also exists in `m4` — see §7 on the duplicate-name hazard. |
| `m1:the_briefing_reaches_a_ticket_agent`, `the_briefing_is_never_in_the_operators_feed`, `a_promoted_lane_is_re_briefed_as_a_ticket_lane` | **D1** / **B1** | Two questions, not four: the block is delivered on the agent wire and never on the pane wire (D1's turn), and it is rebuilt at the *respawn* call site (B1's promotion). The block's content is `Briefing.Block` and `PureLogicTests.cs:1101-1156` already asserts it on all four kinds. **Issue #11 applies while moving**: `a_promoted_lane_is_re_briefed_as_a_ticket_lane` prints `FAIL []` because it discards its `Wait-Until` boolean — capture it once and report the same value (issue #10's rule). Do **not** lengthen the 25 s deadline. |

### Folded because the assertion is content (verdicts already in the surveys)

465 checks were classified movable by the surveys and a further ~95 "keep" rows fold here because
their assertion was content riding a wire someone else owns. They are not re-listed; the six survey
tables are the per-check authority and the implementing agent works from them.

### Kept deliberately although a rule could have deleted them

- `m0:status_does_not_summon_a_daemon` and its two siblings **assert a process did not appear**.
  No unit test can make that claim. A7 is the cheapest integration test in the repo and guards
  quota, which is the scarce resource.
- The four checks the suites already mark **VACUOUS BY CONSTRUCTION** (`workspace:an_explicit_root_beats_the_inherited_env`,
  `a_disjoint_directory_in_the_renamed_repository_is_still_free`, `naming_a_project_costs_no_model`,
  `registry_is_reported_under_dodona_home`), plus `m2:a_pr_repo_still_promotes_a_refused_write`,
  `m1:a_pr_repo_still_gets_its_worktree`, `the_briefing_is_never_in_the_operators_feed`,
  `ui-wake:the_window_outlives_its_daemon`, `brain:a_brain_in_a_project_that_stayed_is_untouched`,
  `the_brain_cap_never_evicts_an_existing_brain`, `a_one_project_workspace_says_nothing_about_scope`:
  **kept and labelled**, per R7's precedent (18 checks shipped, 14 seen red, 4 vacuous-by-construction
  kept *and labelled*). `dev prove` will keep calling them VACUOUS; that is expected and is not a
  licence to delete. Most become pure predicate tests, where they guard identically and cannot flake.
- `INVESTIGATION §4.8` warning honoured: A4 asserts that `##shutdown` reaches *this workspace's*
  shim and takes *its* child tree. It does **not** assert that `stop-all` is machine-wide, because
  a check that did would enshrine behaviour the repo may want to change.

---

## 4. HOW MANY? — the number, defended

**49.**

The operator's instinct is right and the fold rule proves it: 109 candidate wires became 49, and
275 "keep"-classified checks became ~190 named assertions on **19 fixtures**. The 750 exists because
each of those 275 built a fixture. Nothing else changed.

**Why not 15.** Getting to 15 requires deleting one of these five distinctions, and each has already
cost this repo something:

| distinction | what it costs to delete | the incident |
|---|---|---|
| a process **died** vs a process **never appeared** | A7 and I9 both go, and a "quick health check" starts four haiku lanes again | CLAUDE.md §3.2, 2026-08-19 |
| a **ref moved** vs a ref was **reported** moved | C1/C2/C3 collapse into one, and verify-after-the-advance becomes indistinguishable from verify-before | D-5, `red_verify_leaves_main_unchanged` is the only check that separates them |
| a **window rendered** vs a dump **reported** | F2, F3, F7 and G2 go, and the class-handler trap, the measured `fit`, the coordinate space and the pixels↔value link all go with them | CLAUDE.md §0.2's WPF traps; five lane actions were *unreachable*, not merely untested |
| a **socket opened** vs a state **said listening** | the egress observation goes, and the recorded red — a test run authenticating on the operator's bill — comes back | `voice`'s recorded red `state=[listening] engine=[deepgram]` |
| a **lookup that could never miss** vs a **live path** | E1 goes, and the routing ladder is fully green and dead in production again | CLAUDE.md §3, two days |

**Why not 80.** Because the upstream test is ruthless and I applied it: 16 candidate wires were
deleted purely by "cutting it reddens a kept test", including the biggest ones — `lane-start
spawns`, `the gate-hook denies`, `publish builds`, `a live UI reads the store`. Those four alone
carried 21 + 23 + 2 + 23 checks in the surveys.

**The tighter sub-answer, if the operator wants one number for "never skip these".** Twelve. These
are the wires whose loss ships a defect this repo has already paid for once:
`A1, A3, B1, C1, C2, D1, E1, E3, F1, F10w, I1, I6`.
Everything else in the 49 is worth keeping and is cheap because it rides a fixture that already
exists; these twelve are the ones that stop being cheap the moment they are missing.

**The honest caveat on the count.** 49 is the number of *wires*. The number an implementing agent
feels is **19 fixtures** and **11 suite processes**. If a future session is tempted to quote "49
integration tests" as though it were 49 daemon starts, it is not; §7's registry records the fixture
each wire rides, so the arithmetic stays checkable.

---

## 5. THE SUITE SHAPE AFTERWARDS

| suite | replaces | wires | est. names | est. s | notes |
|---|---|---|---|---|---|
| `unit` | `unit` (+ everything moved down) | — | ~860 | ~12–15 | two xunit projects now: `Dodona.Tests` and a new `Dodona.Ui.Tests` (net8.0-windows, UseWPF) |
| `lifetime` | `m0` | 7 | ~25 | ~28 | |
| `gate` | `m2`'s isolation half, `m1`'s G1/G3 | 2 | ~12 | ~20 | |
| `land` | `m1`'s M/V/D/A, `workspace`'s land | 4 | ~22 | ~30 | fixture is a **two-repo** workspace |
| `review` | `m1`'s R\*, `ui-ask`'s land ask, `brain`'s manager | 3 | ~16 | ~32 | |
| `brain` | `brain`'s lifecycle, `workspace`'s ladder | 4 | ~18 | ~45 | fixture is a **two-project** workspace, autostart ON |
| `assist` | `brain`'s review-behind, `workspace`'s hold, `compression` | 3 | ~12 | ~35 | |
| `window` | `ui-grid`, `ui-ask`, `voice`, `m3` | 11 | ~35 | ~40 | one workspace, one `--test-window`, several phases |
| `shell` | `ui-shell`, `ui-wake` | 2 | ~10 | ~20 | |
| `publish` | `m4`, `publish`, `ui-shell`'s hot swap | 9 | ~26 | ~70 | the real build; the pace-setter |
| `identity` | `workspace`'s store half, `concierge` | 4 | ~14 | ~20 | |
| **total** | 16 → **11** | **49** | **~1050** | | |

**Wall clock, estimated, not measured.** Suite-seconds ≈ 5 (unit, if it stays solo) + 28 + 20 + 30 +
32 + 45 + 35 + 40 + 20 + 70 + 20 ≈ **345 s of suite work**. At `SuiteConcurrency = 3` with `unit`
serialized in front, that is roughly **115–135 s**, floored by `publish` at ~70 s. Today's measured
figure, from issue #1 at `3b235ab`: five clean gates at 212.4 / 216.4 / 218.8 / 232.6 / 232.9 s,
median **218.8**. **I did not run a gate and this estimate is not a measurement.** The first thing
the implementing agent should do after the shape lands is measure five gates and replace this
paragraph with the real spread.

**I7's budget stays at 300 s.** `dev.ps1:1699` states the procedure: *FIX A RED, RAISE FOR GROWTH,
and never the other way round.* Lowering a budget because the mean improved is how a red is
manufactured for the next busy machine, and issue #1's finding is that the problem is **variance**
(54.3 s spread at one commit on one machine), not the mean. Revisit only after ≥5 measured gate runs
at the new shape, and then only downward if the worst observation leaves a real margin.

### Suite naming — the judgement call

The milestone names (`m0`…`m4`) go. **Alternative considered and rejected:** keep them and only move
checks, which is far less churn — every `dev prove m1:<check>`, every doc reference, `AllSuites`,
`SuiteOrderHint`, `dodona.json`'s `//verify` and CLAUDE.md's suite table all stay put. Rejected
because CLAUDE.md's own note on the deleted duration column applies to names too: the suite table's
surviving value is *"the mapping from a suite to what it covers, which is judgement no command can
print"*, and `m1` currently means three unrelated things (the write gate, the merge token, the
briefing). A rename is a one-time cost against a permanent misdirection. **It must all land in one
commit**, because I8's dangling-`tests\*.ps1`-reference lint turns red on every stale doc reference
— which is a forcing function, not an obstacle.

### Does a much larger `unit` layer change its solo status?

`unit` is solo for a **structural** reason, not a flakiness one (`dev.ps1:661-668`): `dotnet test`
compiles Dodona into `src\Dodona\bin\Release`, which every other suite copies binaries out of via
`Use-TestBinaries`. Two compilers, one directory. Adding `Dodona.Ui.Tests` makes it worse — it also
writes `src\DodonaUi\bin\Release`.

**Today: keep it solo.** At ~5 s the cost of serializing it is ~5 s, and the machinery that would
change (P1.5's stale-build refusal, which compares each project against its own assembly) is exactly
the machinery that caught a false green.

**The trigger, stated in advance** (D-V12's precedent — a change with its condition written down
before it is needed): **when `dev test unit` exceeds 15 s**, switch the *wave's* unit invocation to
`dotnet test --no-build` and take `unit` out of `SoloSuites`. `--no-build` writes nothing into any
product `bin`, so the structural reason evaporates for the wave run while `dev test unit` (the
iterate-fast verb, which must stay 1–2 s warm per P1.5) keeps compiling normally. Prerequisite:
`dev suites` / `dev gate` must build the test projects too, which they do not today. Take five
measured gates before and after; if it does not measurably help, put it back **with the
measurement**, per the `SoloSuites` rule.

The `-p:BaseOutputPath=<temp>\` escape is the other option and is rejected: a separate output tree
means the first run after any code change is a cold compile of Dodona + DodonaUi, which breaks
`dev test unit`'s 1–2 s promise — the operator's explicit requirement.

### `dodona.json`'s `//verify` block

Currently `dev build` + `dev test unit m1 m2` (~17 s against 275.6 s for everything), with the
reasoning carried at the point of use. After the rename the equivalent is **`dev build` + `dev test
unit gate land`**. Update the `//verify` comment in the same commit and keep its reasoning; do not
widen it (CLAUDE.md §0.1, twice).

---

## 6. WHAT MUST NOT BE LOST — the constraint checks

**Constraint 1 (no coverage lost).** Every one of the ~1050 names survives, in one of three places:
a named assertion inside a kept wire test, a named unit test, or a harness-registered row. §7's
census lint is the proof and it is mechanical. **The migration order is fixed and non-negotiable**:
add the unit check → keep the acceptance check → prove the unit check red → *then* delete the
acceptance check, which is a census-file edit and therefore visible in the diff. Never the reverse.

**Constraint 2 (a fake that drifts is worse than no test).** This wire list introduces **no new
fake**. It removes fixtures; it does not substitute doubles. Every kept wire runs the real process,
the real pipe, the real git, the real window. The three drift hazards that already exist —
`DodonaFakeAgent`'s hand-written wire shape, `FakeRecognizer`'s synchronous `Ready`, and `Poses`'
`init`-with-default properties — are untouched by this document and are the mechanism agent's
subject. What this list contributes is the constraint: **nothing in §2 may be re-implemented against
a double.** `seams.md` §3 enumerates the nine boundaries that must never be faked, and every one of
them is the subject of a kept wire: the merge token and land transaction (C1), repo-exclusivity
(identity's unit fixtures over the real partial index), git for anything that mutates a ref (C1,
C3), `GateHook`'s deny paths (B1, with real BOM bytes), the shim's exactly-once replay (A1), lane
liveness (A5), start-on-demand (H2), `AttachShimAsync`'s spawn-site invariants (A6), WPF's own
behaviour (F2).

**Constraint 3 (one operator-path exercise per behaviour).** Preserved and named, so a future
session can check it:

| behaviour | the operator-path wire |
|---|---|
| routing / the classifier | **E1** — autostart on, nothing pre-built. CLAUDE.md §3's named guard. |
| a command that observes | **A7** — no daemon at all, assert none appeared. |
| the write gate | **B1** — a real subprocess, real stdin bytes, real deny. |
| a person typing | **F1 + F2** — a real window, a real keystroke, a real agent. |
| dictation | **F10w** — `voice:spoken_send_words_do_not_submit`, named by CLAUDE.md §3 by name. |
| waking a sleeping workspace | **H2** — the incident where the first thing a person did was answered "daemon not running". |
| the drift watcher | **I9** — a daemon started the operator's way, nothing pre-built. |

**Constraint 4 (Windows / PS 5.1).** Every trap the surveys flagged in the files being rewritten
carries forward verbatim: `m4`'s `Sql()` query-on-its-own-line inside `'''…'''`; gate JSON must be
**parsed**, never string-matched (backslashes are doubled, quotes arrive as `\"`); captured native
stderr collapsed with `-replace '\s+', ' '` before matching; `ConvertFrom-Json` on an array landed in
a variable first; `[int]` before `-shl` in `PngDims`; `@(...)` around one-element pipelines; `$procId`
not `$pid`; parenthesised calls so a function does not swallow arguments into `$args`; `.ps1` files
UTF-8 **with BOM** and CRLF, `.cs`/`.md` CRLF with no BOM. Note I8's known gap (P1.8): the lint does
**not** catch non-ASCII in a BOM-less `.ps1`, and seven tracked files already carry it — a new
BOM-less `.ps1` with an em dash will silently match nothing.

**Constraint 5 (operator standing directives).** No suite makes a model call (`DodonaFakeAgent` via
`dodona.json`'s `"agent"` key) and none can open a microphone (`DODONA_UI_MIC=off`, set by
`_workspace.ps1` for every suite). Both locks move into the harness rather than out of it — the
`no_socket_from_the_ui_process` promotion in §2 makes the second one stronger, not weaker. Nothing
in this plan widens an automatic check; every change narrows one. `dev gate` remains the merge-time
event only.

---

## 7. THE MECHANISM — what stops this growing back to 750

Four lint rows and one harness helper. All in `tools/dev.ps1`'s existing `Repo-Lint` (I8: sub-second,
tracked files only, already asserted by `dev gate`) and `tests/_workspace.ps1`. **No new tool** —
D-3 forbids one; these are rows on the door that already exists.

**M1 — `tests/wires.json`, the wire registry.** One object per wire: `id`, the one-sentence path,
`check` (THE name), `suite`, `fixture`, and `absorbs` (the folded names, with the reason). It is the
tracked, reviewable form of §2 and §3.

**M2 — `Wire '<id>' { ... }` in every acceptance suite.** A new helper in `_workspace.ps1`. `Check`
inside a `Wire` block is an integration assertion; `Check` outside one is a lint failure. Lint row:
*every registry id appears in exactly one suite, and every `Wire` id in a suite is in the registry.*
This is what makes "one wire, one integration test" enforcement rather than a sentence — a second
fixture for an existing wire cannot be added without editing the registry, which shows in the diff.

**M3 — the check-name census.** `tests/check-census.txt`, tracked. The lint enumerates every check
name in the repo — the `Check '<name>'` form, `m0`'s `$results['<name>'] = …` form (it has no `Check`
helper; 26 sites), the two loop-generated families (`concierge-acceptance.ps1:307`
`resolution_recorded_$rung` and `m1-acceptance.ps1:1167` `event_$k`, whitelisted by pattern), and
every xunit `[Fact]`/`[Theory]` method name — and asserts:

- **no duplicate name within a suite.** `$results` is a hashtable keyed by check name in every
  suite: a duplicate silently overwrites and the tally silently drops by one, and **nothing detects
  it today**. `dev.ps1:584` records that the runner has no expected-count oracle *by decision*, so
  this is the only place the property can live. It is a free catch and it exists nowhere yet.
  (Known live instance: `m2:stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` is registered
  at line 330 and again at its `else` at 334.)
- **the census file is a subset of the enumerated set.** Removing a check requires editing the
  census, which is Constraint 1's proof made mechanical and permanent. Cross-suite duplicates are
  allowed and recorded — `landed_exactly_once` legitimately exists in both `m0` and `m4` today, and
  after the move it exists in `lifetime` and `publish`; the census records both, and the lint would
  refuse them inside one suite.

**M4 — `dev prove unit:<check>`, the missing verdict.** 560 checks are moving down and each needs
proving; `dev prove` refuses `unit` today for three recorded reasons (`dev.ps1:1160-1196`). Two of
the three are now wrong for the case that matters:

- reason 1 (the HEAD build compiled the unit-test project) is **already fixed** for acceptance —
  an acceptance proof no longer compiles `tests\Dodona.Tests`;
- reason 2 (no `tests\unit-acceptance.ps1`) is a dispatch detail;  <!-- (planned) no such suite exists; the name is quoted from dev.ps1 -->
- reason 3 (`Run-Unit` builds and tests the **working tree**, so the verdict is the change measured
  against itself) is the real blocker — **and prove already solves exactly this shape**: build HEAD's
  product projects in the prove worktree, copy the working tree's `tests\*` over HEAD's, run there.
  *"The TEST comes from your working tree; the CODE comes from HEAD. That is the whole trick."*

So `dev prove unit:<check>` becomes supported for a check that exercises a symbol HEAD already has
— which is what a **moved** check is, by definition. The honest limit stands and gets its own
verdict rather than a wrong answer: a check naming a symbol this change *adds* cannot compile
against HEAD, and prove reports **UNPROVABLE-NEW-SYMBOL**, distinct from MISSING and VACUOUS, naming
the break-and-revert substitute (D-V11) in its message. Without M4 the plan's proof burden is 560
break-and-reverts, which will be skipped, which is the disease CLAUDE.md §0.3 is about.

**M5 — harness teardown assertions.** `no_modal` and `no_socket_from_the_ui_process` join
`no_process_left_in_the_build_output` in `_workspace.ps1`'s `finally`, applied to every suite that
opens a window. This converts two one-off checks into properties no suite can be written without.
`ui-ask:the_ask_is_not_a_modal` is **rewritten** to the counting mechanism and keeps its name — its
current mechanism (the dispatcher would stop answering) was *measured false* on 2026-08-20 with a
real `MessageBox` on screen and the check green.

---

## 8. RISKS

1. **`window` inherits `voice`'s cascade signature.** `voice` is in `SoloSuites` because its
   failures cascade — measured 70.5 s and three red inside a full gate, 40.3 s and green alone
   minutes later, *while solo*, which is issue #3's open question. Folding it into `window` puts
   that signature into the wave. Mitigation and trigger, stated in advance: run `window` in the wave
   and measure; if it reddens **twice in five** gates, or reddens in a wave where nothing else did,
   move it to `SoloSuites` **with the new measurement** — and **do not move `shell` with it**
   (issue #2's split trap: four solo pieces measured 134.3 s sequential against the monolith's
   88.8 s; a split only pays when the pieces run concurrently).
2. **The migration is where coverage actually gets lost**, not the design. The add→keep→prove→delete
   order in §6 is the whole defence, and it is only as good as M3's census. If the census lands late,
   the window between "acceptance check deleted" and "unit check proved" is unguarded. **Land M3
   first**, before any check moves.
3. **`dev prove` cannot prove the moved checks without M4**, and 560 break-and-reverts will be
   skipped. M4 is not optional infrastructure; it is the plan's proof mechanism.
4. **`Instance.ConciergeId` / `ShellId` freeze on first touch** (Instance.cs:84/:91) from
   `DODONA_HOME`, while `Paths.Home` re-reads it. Any unit fixture touching identity or liveness must
   be serialised in one xunit collection, or it will read another test's home. `Instance.AllPipes()`
   enumerates the machine-global `\\.\pipe\` — shared with the operator's live session — so no unit
   test may assert over it at all.
5. **WPF is one `Application` per process** and `MainWindow.TestWindow` is `static`. `Dodona.Ui.Tests`
   gets at most one window per process, and most of its value is window-free anyway
   (`Poses.Get` → `MainVm.Apply` → assert). Do not plan in-process UI parallelism.
6. **`:memory:` does not give the Store↔StoreReader pair.** Measured: `StoreReader.Open()` requires
   `File.Exists` and opens a second connection; a private in-memory db is invisible across
   connections. Anything spanning both needs a temp file. The one-line `:memory:` guard at
   `Store.cs:33` is still worth taking for pure-`Store` tests.
7. **A wire test can re-grow into a suite.** M2 caps *fixtures*, not assertions, deliberately — but a
   `Wire` block that quietly acquires a second daemon start has evaded the cap in spirit. The
   registry's `fixture` field is the reviewable record; a wire whose fixture changes must edit it.
8. **Renaming ten suite files touches every doc, `AllSuites`, `SuiteOrderHint`, `//verify`, CLAUDE.md's
   table and `.claude/skills/ship`.** I8's dangling-reference lint makes a partial rename loudly red,
   which is the desired behaviour, but it means the rename cannot be split across commits.
9. **Some of the 750 may be pinning behaviour that should change** (`INVESTIGATION §4.8`: a suite
   proving `stop-all` is machine-wide would *enshrine* it). Preserving a name is not endorsing its
   assertion. Where a moved check's assertion is wrong, fix it while moving and record the fix — five
   such are already named in §3 (`m0:landed_exactly_once`, `compression:overlay_keeps_midturn_and_full_text`,
   `ui-ask:the_ask_is_not_a_modal`, `ui-grid:the_newline_survived_to_the_agent`,
   `m1:a_promoted_lane_is_re_briefed_as_a_ticket_lane`).
10. **`ui type` / `ui key` do not synthesize keystrokes** — they call `SubmitInput` / `InputKey`
    directly (MainWindow.xaml.cs:148-165), so **no check in the repo today proves WPF's class-handler
    trap**, and CLAUDE.md's claim that `ui-grid` "proves Enter still sends" is true only of
    `InputKey(false)`. F2 closes that gap by raising a real routed `KeyDown` on the TextBox. This is
    the one wire a careless refactor could plausibly be *blamed* for losing; it is being added, not
    removed, and it must be proved red (raise the event on a window whose handler is `KeyDown`
    instead of `PreviewKeyDown`).
