# Survey: the "view" group — m3, m4, publish

Files surveyed, read end to end:

- `C:\Users\devbl\Documents\personel\Dodona\tests\m3-acceptance.ps1` (408 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\m4-acceptance.ps1` (420 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\publish-acceptance.ps1` (370 lines)

## Counting rule used

`grep -c "Check '"` gives **35 / 42 / 24 = 101** explicit registrations. Each suite's
`finally` also calls `Assert-NoBuildOutputProcesses $repo $results`
(`tests\_workspace.ps1:353`), which writes `$results['no_process_left_in_the_build_output']`
— it is counted in the suite's own `"{0} checks, {1} failed"` tally, so it is a check.
**104 checks total**, which matches the task's "about 104".

Verdict totals: **49 unit · 54 integration · 1 unclear**.

---

## m3-acceptance.ps1 — the UI as a view over the store (36 checks)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| `panes_replay_store_rows` | m3 | integration | a window launched AFTER the rows existed answers `ui dump` with both lanes and their text | **W1** the live UI process reads a real store and answers `ui` verbs over `UiPipe` | the strongest formulation of W1: launch-after-the-fact makes it a replay, not a live feed |
| `fixed_slots_and_colors` | m3 | unit | slot index = position in order (0,1) and the two lanes get different palette entries | `DodonaUi.Vm.Apply` slot loop + `PaneView.ColorHex` / `PaneView.Palette` (Vm.cs:210,238) | pure map from an ordered lane list to slot+hex |
| `presence_idle_after_result` | m3 | unit | after a result event the lane's presence column reads `idle` | presence state machine in `Daemon` / `StoreReader.Lanes` (StoreReader.cs:79) | content of one column; the daemon is only the writer |
| `routed_input_reaches_pane` | m3 | integration | `dodona input` (no lane named) reaches the focused lane's agent and the reply row appears | **W3** a client command crosses the ctl pipe, is routed, and lands in a real agent's pane | |
| `focus_marked_in_ui` | m3 | unit | exactly the focused lane carries `focused: true` | `PaneView.From` / `PaneSnap.Focused` binding from `focused_lane` kv | one-of-N marking is pure |
| `midturn_tool_calls_reach_the_pane` | m3 | integration | a tool-only assistant event on the agent's wire becomes a pane row (`read 4 files`) | **W4** a tool call on the agent's stream is turned into a `progress` row and reaches the tile | the suite's own comment says exactly this: tiers/fold are unit, "the two ends are connected at all" is not |
| `a_run_of_steps_folds_to_one_line` | m3 | unit | four reads render as ONE `read 4 files` line and no per-file lines | `PaneProgress.Fold` (PaneProgress.cs:225) + `FoldKey` (:207) | already covered case-by-case in `tests\Dodona.Tests\PaneProgressTests.cs` |
| `acts_and_failures_keep_their_own_lines` | m3 | unit | edit / bash / failure are not averaged into the fold | `PaneProgress.FromTool` (:74), `FromFailedResult` (:178), `FoldKey` (:207) | pure tier table |
| `the_overlay_still_shows_every_step` | m3 | unit | the overlay lists `read a.cs`…`read d.cs` and never `read 4 files` | `StoreReader.Tail(all: true)` (:201) vs the pane's folded rendering | two renderings of one row list; the "store kept raw rows" half rides W4 |
| `blocked_state_in_pane` | m3 | unit | a refused token-request produces `blocked=true`, `presence='waiting on you: merge'`, `badge=1` | token-refusal presence/badge derivation + `StoreReader.Badges` (:104) | three specific strings/numbers = content |
| `blocked_lands_in_feed` | m3 | unit | a feed row exists with that body and `acked=false` | `Store.Feed` write on refusal + `StoreReader.Feed` (:240) | |
| `ack_clears_badge_keeps_row` | m3 | unit | ack sets badge 0 and the row survives (greyed, not deleted) | `StoreReader.Badges` (:104) excludes acked; feed row not deleted | |
| `approve_unblocks_lane` | m3 | unit | approve → `blocked=false`, `presence=idle`, an `approved` feed row | approval state transition in `Daemon` + the feed body text | the widened `Wait-Until` above it (m3:165-170) is a timing fix, not the assertion |
| `undo_route_retracts` | m3 | unit | `routing_decisions.undone=1` and a `Disregard` line in the lane's tail | `Daemon` undo-route handler: mark undone + emit the retraction body | reads the STORE, not the agent — it does not prove the agent received anything |
| `respawn_actually_respawned` | m3 | integration | a NEW `shim_spawned` row id exists for the lane after `lane-respawn` | **W5** `lane-respawn` starts a real shim + child process | guard that makes the two below non-vacuous |
| `respawned_ticket_lane_returns_to_its_worktree` | m3 | unit | the spawn detail's cwd == `<root>\.dodona\wt\t1` | `Daemon.RespawnLaneAsync` (Daemon.cs:3981) cwd resolution: `lanes.cwd` preferred over `_primary` | a value the code CHOOSES; the choice is pure given the lane row |
| `respawned_ticket_lane_is_not_in_the_live_tree` | m3 | unit | the spawn cwd is not `<root>` | same as above, negative form | |
| `one_project_status_names_no_project` | m3 | unit | no `project=` suffix on any `lane N` status line | `Projects.Field` (Projects.cs:107) returning null for a one-project workspace | |
| `one_project_panes_carry_the_project_key` | m3 | unit | the dump JSON has a `project` key on every non-empty slot | `ui dump` serialization shape | shape assertion |
| `one_project_panes_name_no_project` | m3 | unit | that key's value is empty | `Projects.Field` (:107) | |
| `land_succeeds` | m3 | integration | `dodona land 1` reports `landed ticket 1` after a real git commit in a real worktree | **W6** the land performs a real merge/ff and retires the lane | m1 owns this wire too; here it is the precondition for the four checks below |
| `land_retires_the_agent` | m3 | integration | the agent PROCESS with the recorded childPid is gone | **W6** | irreducible: a process died |
| `lane_survives_as_dormant` | m3 | unit | the lane row's state is `dormant` after the retire | lane state transition written by the retire path | |
| `lane_keeps_its_slot_and_thread` | m3 | unit | slot still 0 and the old lines still there | `Vm.Apply` slot loop + rows not deleted | same pure function as `fixed_slots_and_colors` |
| `wake_revives_the_lane` | m3 | integration | after `lane-respawn` the lane is `alive` and a fresh `say` round-trips | **W5** | strongest form of W5 — the respawned process actually answers |
| `overlay_opens` | m3 | unit | `ui overlay WATER` sets `dump.overlay` to `WATER` | overlay mode field in `Vm` / `MainWindow` dump | the verb→window hop is W1, already proved by every dump |
| `overlay_closes` | m3 | unit | `ui overlay off` nulls it | same | |
| `screenshot_fixed_size` | m3 | integration | the PNG is exactly 1600x900 | **W7** a real WPF window renders to a bitmap at a fixed size | `RenderTargetBitmap` in the window's own coordinate space (§0.2) |
| `pane_screenshot_writes` | m3 | integration | a pane-scoped PNG exists and is >1000 bytes | **W7** | weak duplicate of the above |
| `poses_render_distinct` | m3 | unit | eight poses give eight distinct file hashes | `DodonaUi.Poses.Get` (Poses.cs:79) — the eight snapshots are distinct | the pixel hash is a heavyweight proxy for "the fixtures differ"; distinctness is pure |
| `the_two_pose_names_a_project_per_lane` | m3 | unit | the `two` pose has 2 slots with 2 DIFFERENT project tags | `Poses.Get("two")` + `PaneView.From` project passthrough (Vm.cs:274) | catches "one value computed once, painted N times" — pure |
| `pose_blocked_testifies` | m3 | unit | pose name, `SKYBOX` is the blocked slot, exactly one toast | `Poses.Get("blocked")` + `Vm` `_blockedBefore` fresh-transition toast rule (Vm.cs:533,662) | |
| `pose_screenshot_deterministic` | m3 | integration | the same pose renders byte-identical pixels twice | **W7** | determinism of the render path; same wire as `screenshot_fixed_size` |
| `pose_live_resumes_store` | m3 | unit | `ui pose live` nulls the pose and WATER is back | `Vm.Apply`'s `PoseName is null` branch (Vm.cs:560) | pose vs live is a mode switch over one snapshot |
| `ui_close_exits_process` | m3 | integration | `$uiProc.HasExited` after `ui close` | **W8** `ui close` terminates the real UI process | irreducible: a process died |
| `no_process_left_in_the_build_output` | m3 | integration | no live process has a path under `<repo>\src\...\bin\` | **W12** suite-leak hygiene, real OS process enumeration | `_workspace.ps1:353`; one per suite |

m3: **24 unit · 12 integration · 0 unclear = 36**

---

## m4-acceptance.ps1 — hot swap, real build (43 checks)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| `publish_builds_versioned_dir` | m4 | integration | publish produced exactly ONE new versioned directory (fixtures excluded) | **W9a** publish invokes MSBuild for real and lays down a stamped directory | this is the expensive part of the suite |
| `publish_hands_off` | m4 | integration | publish output contains `handed off to build` | **W9b** the mid-turn handoff | duplicate of `old_daemon_exited`/`new_daemon_serves`; the string comes from `HandoffAsync` |
| `old_daemon_exited` | m4 | integration | daemon #1's process object reports HasExited | **W9b** | process death |
| `new_daemon_serves` | m4 | integration | a DIFFERENT pid answers `status` | **W9b** | |
| `a_lane_from_a_published_build_names_it_in_its_gate` | m4 | unit | the lane's `gate-laneN.json` names an exe under `$binRoot` | `Daemon.DeployGate` (Daemon.cs:6581) — gate JSON generated from the running exe path | fixture for `the_retained_build_is_the_one_a_live_gate_needs`; the JSON is a pure render |
| `new_daemon_is_published_build` | m4 | integration | the status line names `$binRoot` | **W9b** | duplicate of `new_daemon_serves` |
| `published_build_carries_its_commit` | m4 | integration | the published exe's `version --json` commit == `git rev-parse HEAD`, 40 chars | **W10** the commit crosses publish → MSBuild `InformationalVersion` → `Ver.Commit` | irreducible: it crosses the compiler. The `~` separator and `IncludeSourceRevisionInInformationalVersion` traps live on this path |
| `published_commit_is_a_real_commit` | m4 | integration | `git cat-file -t <commit>` == `commit` | **W10** | duplicate; the "bisectable" half of the same value |
| `published_build_admits_uncommitted_changes` | m4 | integration | `vj.dirty` equals the real tree dirtiness | **W10** | duplicate; different field of the same stamp |
| `shim_survived_swap` | m4 | integration | the shim pid is still alive after the handoff | **W9b** | process survival |
| `agent_survived_swap` | m4 | integration | the child pid is still alive after the handoff | **W9b** | process survival |
| `inflight_turn_landed` | m4 | integration | the turn in flight during the swap produced a `result` carrying the token | **W9b** | |
| `landed_exactly_once` | m4 | integration | exactly ONE such result — no double delivery | **W9b** | **survivor**: subsumes `inflight_turn_landed` and is the M4 thesis |
| `seqs_contiguous_across_swap` | m4 | integration | `pane_events.seq` for the lane is 0..N-1 with no gaps | **W9b** | zero-loss across a buffer handover between two processes |
| `same_session_after_swap` | m4 | integration | the lane's session id is unchanged and still `fake-*` | **W9b** | |
| `same_agent_answers_new_daemon` | m4 | integration | a fresh `say` round-trips through daemon #2 to the ORIGINAL agent | **W9b** | |
| `handoff_in_causal_chain` | m4 | integration | `swap_spawned` and `daemon_handoff` events both exist | **W9b** | store rows, but only a real handoff writes them; movable only behind a handoff seam |
| `successor_recorded_predecessor` | m4 | integration | the successor's `daemon_start` detail contains `successor_of=<pid1>` | **W9b** | same caveat as above |
| `swap_announced_to_dispatcher` | m4 | unit | an announcement body contains `swapped to build` | `Daemon.Announce` text for a completed swap (Daemon.cs ~2411) | pure string given "the swap completed" |
| `bad_binary_refused` | m4 | integration | `swap <text file>` exits non-zero and the message mentions `version --json` | **W11** a candidate binary is verified by REALLY running `version --json` | the error text is `Probe`'s, produced only by an actual failed launch |
| `daemon_alive_after_bad_swap` | m4 | integration | the daemon pid is unchanged after the refusal | **W11** | **survivor**: "the system must stay up" is the property, and it is a process fact |
| `missing_binary_refused` | m4 | unit | `no such binary: <path>` + non-zero for a path that does not exist | `Daemon.Probe` file-existence branch (Daemon.cs:2250) | pure `File.Exists` → message |
| `publish_refuses_unrunnable_binary` | m4 | integration | `publish --exe <bad>` says `nothing promoted` and exits non-zero | **W11** | publish-side sibling of the same probe (Program.cs:1256-1258) |
| `midmerge_arms_swap` | m4 | unit | output carries `MERGE is mid-merge`, `armed`, `swap-answer now \| hold` | `Daemon.Blockers` (Daemon.cs:2281) + `ConsiderSwapAsync` line construction (:2343-2355) | pure given the token/ticket rows |
| `blocked_swap_armed_in_store` | m4 | unit | `swaps` newest row state == `armed` | `Store.SwapCreate(..., "armed")` (Daemon.cs:2351) | |
| `armed_announced_with_overrides` | m4 | unit | the announcement carries `armed … lands the instant this clears … mid-merge` | `Daemon.Announce` string at Daemon.cs:2353 | pure string |
| `daemon_did_not_swap_while_blocked` | m4 | integration | the daemon pid did not change while a blocker stood | **W12** an armed swap does NOT hand off until its condition clears | |
| `hold_parks_the_swap` | m4 | unit | `swap-answer hold` → `parked` + state `held` | swap-answer state machine (Daemon.cs:2091-2115) | pure state transition |
| `when_it_lands_rearms` | m4 | unit | `swap-answer when-it-lands` → `armed` + state `armed` | same (Daemon.cs:2106) | |
| `armed_but_not_yet_swapped` | m4 | integration | pid still unchanged after re-arming | **W12** | duplicate of `daemon_did_not_swap_while_blocked` |
| `armed_swap_fires_when_blocker_clears` | m4 | integration | releasing the merge token makes a NEW pid appear within 20 s, unprompted | **W12** | **survivor**: a ticker noticed a real condition and a real handoff followed |
| `armed_swap_recorded` | m4 | unit | the swap row's state is `swapped` | `Store.SwapSet(swapId, "now", "swapped", …)` (Daemon.cs:2411) | |
| `agent_survived_second_swap` | m4 | integration | the child pid is alive after the second handoff | **W9b** | duplicate of `agent_survived_swap` |
| `gate_file_survives_the_swap` | m4 | unit | `gate-laneN.json` still parses and names `gate-hook --lane N` | `Daemon.DeployGate` (Daemon.cs:6581) | the JSON content is a pure render; "survives" is only "nobody deleted it" |
| `the_gate_exe_of_a_live_lane_survives_the_swap` | m4 | integration | the exe path inside a LIVE lane's gate is still on disk after two real swaps | **W13** `GcOldBuilds` runs during a real reconcile and its retention vetoes a real directory | **survivor**: a missing exe here is a hook that cannot start = a silent fail-open write gate |
| `a_build_directory_newer_than_the_running_one_survives_the_gc` | m4 | unit | a decoy stamp `29991231-235959` survives and a `binary_gc_kept` event names it | `Daemon.IsStamp` (:2761) + the `CompareOrdinal(dirStamp, myStamp) > 0` rule (:2727) | the DECISION is a pure string compare over two stamps |
| `old_builds_gcd_except_those_a_live_gate_names` | m4 | integration | at least one retention event fired AND ≤2 directories remain | **W13** | the "GC actually ran" half; keep as an assertion inside the survivor |
| `the_retained_build_is_the_one_a_live_gate_needs` | m4 | integration | GATEKEEP's gate exe is still on disk | **W13** | duplicate of `the_gate_exe_of_a_live_lane_survives_the_swap` for a second lane |
| `autostart_summons_daemon` | m4 | integration | after `stop-daemon`, a summoning command brings up a daemon with a NEW pid | **W14a** start-on-demand spawns a real daemon process | note it deliberately uses `tickets`, not `status` (§3.2) |
| `autostart_reconnects_lane` | m4 | integration | the revived daemon's status shows `SKY` and `connected=True` | **W14b** a fresh daemon reattaches to a shim that outlived its predecessor | |
| `agent_answers_after_autostart` | m4 | integration | a `say` after autostart round-trips to the same agent | **W14b** | **survivor**: proves the reattached pipe actually carries a turn |
| `autostart_can_be_disabled` | m4 | unit | with `DODONA_NO_AUTOSTART=1` a summoning command exits non-zero with `daemon not running` | the client's autostart guard (env-var branch in `Program`) | pure branch on an env var |
| `no_process_left_in_the_build_output` | m4 | integration | no live process under `<repo>\src\...\bin\` | **W12(hygiene)** | `_workspace.ps1:353` |

m4: **12 unit · 31 integration · 0 unclear = 43**

---

## publish-acceptance.ps1 — targeting and the answered-nothing verdict (25 checks)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| `three_workspaces_registered` | publish | unit | three names round-trip to three ids in the registry | `Registry.All` / `Workspaces` create+list | fixture guard; content of a CRUD |
| `both_daemons_are_answering` | publish | integration | two daemons answer `status --workspace <id>` with a `build=` line | **W15** publish targeting resolved against real, live daemons | fixture liveness for the whole targeting section |
| `named_target_is_swapped` | publish | unit | output says `swapping alpha` | publish target resolution, `Program.cs:1284-1293` (`--workspace` branch) | selection is pure given (opts, registry rows, liveness map) |
| `unnamed_workspace_is_untouched` | publish | unit | no `swapping beta` | same resolution | |
| `asleep_workspace_is_untouched` | publish | unit | no `swapping asleep` | same resolution | |
| `all_reaches_every_live_workspace` | publish | unit | `--all` names alpha and beta | `Program.cs:1277-1282` (`--all` branch): `reg.All().Where(Instance.IsLive)` | pure given a liveness oracle |
| `all_skips_a_workspace_with_no_daemon` | publish | unit | `--all` does not name asleep | same, liveness=false | |
| `all_includes_the_concierge` | publish | unit | `--all` names `concierge` | same branch, `Instance.ConciergeId` | |
| `the_foreign_daemon_really_is_live` | publish | integration | a `dodona-<foreignId>-ctl` pipe exists in `\\.\pipe\` | **W15** | fixture guard; a real pipe from a daemon in a DIFFERENT registry |
| `all_never_swaps_a_workspace_from_another_registry` | publish | integration | `--all` output never mentions the foreign id | **W15** | the safety property stated as output text |
| `foreign_daemon_survived_untouched` | publish | integration | the foreign daemon process has not exited | **W15** | **survivor**: fails if and only if the foreign daemon was actually swapped |
| `foreign_daemon_still_answers_its_pipe` | publish | integration | the foreign daemon still answers `status` | **W15** | duplicate — and weaker, since a successor would also answer |
| `unknown_target_is_refused` | publish | unit | `no workspace "…"` + exit 2 | `WorkspaceResolve.ByNameOrId` null → `Program.cs:1290` | pure |
| `refusal_swapped_nothing` | publish | unit | no `swapping` line before the refusal | the early `return 2` precedes the loop (Program.cs:1290) | pure control flow |
| `default_target_is_the_owning_workspace` | publish | unit | `--root <beta>` targets beta, not alpha | the default branch, `Program.cs:1297-1310` + member→workspace resolution | pure given the registry |
| `an_unheld_legacy_store_migrates_normally` | publish | **unclear** | `where --root <orphan> --json` output contains `"store"` | `WorkspaceResolve` / `Paths` legacy-store adoption | the check name promises a MIGRATION (a real file move) but asserts only that resolution answered — I cannot tell from the suite which is intended |
| `the_copied_binary_is_a_distinguishable_build` | publish | unit | the mtime-forced copy reports a different `build` than the running concierge | `Ver.Build` / `Ver.Compute` (Ver.cs:41) = assembly version + `dodona.dll` mtime | fixture guard; the identity function is pure given the file |
| `a_swapped_concierge_reports_the_new_build` | publish | integration | after publish, `concierge-status` shows the new build under a NEW pid | **W16** the concierge accepts a `swap` over its ctl pipe and is replaced | **survivor**: this is issue #9's incident — the concierge ran a two-day-old build while publish printed success |
| `the_old_concierge_process_is_gone_after_the_swap` | publish | integration | the started concierge process has exited | **W16** | duplicate; the process half of the same fact |
| `the_mute_target_really_is_live` | publish | integration | the bare named-pipe fixture holds `dodona-<muteId>-ctl` | **W17** | fixture guard: a pipe that accepts, reads and says nothing |
| `publish_names_a_target_that_did_not_take_the_build` | publish | integration | `mute (…): ANSWERED NOTHING` appears, and alpha's line does not | **W17** a target that answers nothing at the wire is detected as a no-op | **survivor**: `Client` returns 0 on silence — that is a WIRE fact, not a string |
| `publish_fails_when_a_target_did_not_take_the_build` | publish | unit | the publish exits non-zero | exit aggregation `worst = Math.Max(worst, code)` + `code = 1` on silence (Program.cs:1341-1364) | pure given a per-target verdict list |
| `prebuilt_publish_claims_no_provenance` | publish | unit | a `dev build` copy reports `commit == ''` and `trial` false | `Ver.Commit` (:160), `Ver.IsTrial` (:177), `Ver.NoProvenance` (:180) | pure read of assembly metadata; the SDK's bare-SHA case is the distinction |
| `no_provenance_daemon_refuses_to_guess` | publish | integration | a daemon started with autostart CLEARED writes `autopublish_no_provenance` and announces `no commit stamp … arm itself` | **W18** the drift watcher starts on a real daemon on the operator's own path and announces rather than degrading silently | this is CLAUDE.md §3's "every suite must exercise one path the way the OPERATOR runs it" — do not weaken it |
| `no_process_left_in_the_build_output` | publish | integration | no live process under `<repo>\src\...\bin\` | **W12(hygiene)** | `_workspace.ps1:353` |

publish: **13 unit · 11 integration · 1 unclear = 25**

---

## The distinct wires (deduplicated across all three suites)

| # | wire | checks riding it | survivor |
|---|---|---|---|
| W1 | a live UI process reads a real store and answers `ui` verbs over `UiPipe` | 23 (every m3 check whose assertion is read out of `ui dump`) | `m3:panes_replay_store_rows` |
| W3 | a client command crosses the ctl pipe, is routed, and lands in a real agent | 4 (m3) + the `say` round-trips in m4 | `m3:routed_input_reaches_pane` |
| W4 | a tool call on the agent's stream becomes a `progress` row and reaches the tile | 4 | `m3:midturn_tool_calls_reach_the_pane` |
| W5 | `lane-respawn` starts a real shim + child that answers | 4 | `m3:wake_revives_the_lane` |
| W6 | the land runs a real git merge and kills the agent standing in the pruned worktree | 2 | `m3:land_retires_the_agent` |
| W7 | a real WPF window renders deterministic pixels at a fixed size | 4 | `m3:screenshot_fixed_size` |
| W8 | `ui close` terminates the real UI process | 1 | `m3:ui_close_exits_process` |
| W9a | publish invokes MSBuild for real and lays down a stamped directory | 2 | `m4:publish_builds_versioned_dir` |
| W9b | the mid-turn handoff: daemon A hands to daemon B, exits, and lane processes + the in-flight turn survive intact | 14 | `m4:landed_exactly_once` |
| W10 | the commit crosses publish → MSBuild `InformationalVersion` → `Ver.Commit` | 3 | `m4:published_build_carries_its_commit` |
| W11 | a candidate binary is verified by really running `version --json`, and a bad one leaves the system up | 3 | `m4:daemon_alive_after_bad_swap` |
| W12 | an armed swap fires on a real condition clearing, and not before | 3 | `m4:armed_swap_fires_when_blocker_clears` |
| W13 | `GcOldBuilds` runs during a real reconcile and its retention vetoes a directory a live lane's gate names | 3 | `m4:the_gate_exe_of_a_live_lane_survives_the_swap` |
| W14a | start-on-demand spawns a real daemon process | 1 | `m4:autostart_summons_daemon` |
| W14b | a fresh daemon reattaches to a shim that outlived its predecessor and carries a turn | 2 | `m4:agent_answers_after_autostart` |
| W15 | publish targets are resolved from the REGISTRY, so a live daemon from another registry is neither named nor touched | 5 | `publish:foreign_daemon_survived_untouched` |
| W16 | the concierge accepts a `swap` over its ctl pipe and is replaced | 2 | `publish:a_swapped_concierge_reports_the_new_build` |
| W17 | a target that connects, reads and answers nothing is detected as a no-op | 2 | `publish:publish_names_a_target_that_did_not_take_the_build` |
| W18 | the drift watcher runs on a daemon started the operator's way and announces missing provenance | 1 | `publish:no_provenance_daemon_refuses_to_guess` |
| W-hyg | no process leaked into `<repo>\src\...\bin\` | 3 (one per suite) | `m4:no_process_left_in_the_build_output` |

**20 distinct wires carry 54 integration checks.** The concentration is W9b (14 checks
re-proving one handoff) and W1 (23 checks riding one window).

---

## Blockers — why a check is hard to move down

1. **`ui dump` is the only seam, and it is a live-process seam.** Every m3 content question is
   asked by launching `DodonaUi.exe --test-window`, driving it with `dodona ui …` and parsing
   `ui dump` JSON. `Vm`, `PaneView`, `Poses`, `StoreReader` and `PaneProgress` are all in-process
   types with no test project referencing `DodonaUi` — `tests\Dodona.Tests\` contains
   `PaneProgressTests.cs`, `PureLogicTests.cs`, `TreesTests.cs`, `DictationTests.cs`,
   `SpeechStreamTests.cs` and references **Dodona only**. Moving the 24 m3 unit rows down needs
   a test project that can reference `DodonaUi` (net8.0-windows) or those types lifted into
   `Dodona`.
2. **`StoreReader` takes a store PATH, not an interface.** `StoreReader(string storePath)`
   (StoreReader.cs:22) opens SQLite itself, so a snapshot test needs a real .db file. A
   `Snapshot`-in / `PaneView`-out seam already almost exists (`PaneView.From(PaneSnap, int)`,
   Vm.cs:274) and is the cheapest place to cut.
3. **`Poses.Get` is `static` and internal to `DodonaUi`.** `poses_render_distinct` and
   `the_two_pose_names_a_project_per_lane` are pure over it, but there is no way to call it
   without the WPF assembly.
4. **Publish target resolution and verdict aggregation are INLINE in one giant method.**
   `Program.cs:1275-1364` builds `targets`, calls `Client` per target and folds verdicts +
   exit code in the same loop, reading `Registry`, `Instance.IsLive` and `WsName()/WsId()`
   directly. Eight publish checks are pure over `(opts, registry rows, liveness) → targets`
   and two more over `(code, reply) → verdict/exit`, but neither exists as a function. This
   extraction is the single highest-value refactor in this group.
5. **`Daemon.Blockers`, `ConsiderSwapAsync`, `DeployGate`, `RespawnLaneAsync` are instance
   members of `Daemon`,** which owns `_store`, `_lanes`, the ctl pipe and the lane runtime.
   Nine m4 unit rows (`midmerge_arms_swap`, `hold_parks_the_swap`, `when_it_lands_rearms`,
   `blocked_swap_armed_in_store`, `armed_swap_recorded`, `armed_announced_with_overrides`,
   `swap_announced_to_dispatcher`, `gate_file_survives_the_swap`,
   `a_lane_from_a_published_build_names_it_in_its_gate`) are pure over inputs those methods
   compute — but the methods cannot be constructed without a daemon. `Daemon.IsStamp`
   (:2761) is already `static` and is the pattern the rest should follow.
6. **`Ver.Build`, `Ver.Commit` and friends are static properties over the RUNNING assembly's
   own metadata** (Ver.cs:41,160). `prebuilt_publish_claims_no_provenance` and
   `the_copied_binary_is_a_distinguishable_build` cannot be asked of an arbitrary value without
   a `Ver.Parse(informationalVersion)` seam. Note the two live traps that make this worth
   testing at all: the dotnet CLI splitting `-p:k=v` on commas, and the SDK appending its own
   `.<SourceRevisionId>` — both are string-level and both are unit-testable behind such a seam.
7. **`published_build_carries_its_commit` genuinely cannot move.** The value only exists after
   MSBuild has stamped a real assembly. Same for `handoff_in_causal_chain` and
   `successor_recorded_predecessor` unless a fake-successor seam is built for `HandoffAsync`.
8. **The three `no_process_left_in_the_build_output` rows are per-suite by construction** —
   they assert about the process table at the end of THAT suite's `finally`. They cannot be
   merged into one without a runner-level assertion (`dev gate`'s I3 already asserts a
   near-identical property, so this may already be a duplicate of tooling).
9. **Windows/PS 5.1 hazards already paid for in these files, which any rewrite must keep:**
   `Sql()` in m4 puts the query on its own line inside `'''…'''` (m4:66-82) because a query
   ending in a quote made Python read four quotes together and silently return "" — read as
   zero; gate JSON must be PARSED, never string-matched, because backslashes are doubled and
   quotes are `\u0022` (m4:158-168, 296-302); captured native stderr must be collapsed with
   `-replace '\s+', ' '` before matching (publish:234); `ConvertFrom-Json` on an array must be
   landed in a variable first (publish:74-77); `[int]` casts before `-shl` in `PngDims`
   (m3:41-47).
