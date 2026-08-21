# Survey — daemon group: `m0`, `m2`, `compression`

Files read in full:

- `C:\Users\devbl\Documents\personel\Dodona\tests\m0-acceptance.ps1` (461 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\m2-acceptance.ps1` (364 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\compression-acceptance.ps1` (208 lines)

## How the checks were counted

`m0` does not use a `Check` helper — it assigns `$results['<name>'] = 'PASS'|'FAIL…'` directly.
`m2` and `compression` both define `function Check([string]$name, [bool]$cond, [string]$detail = '')`.

- `m0`: 26 `$results[...]` assignments (`grep -c "^\s*\$results\["` = 26) **+ 1** contributed by
  `Assert-NoBuildOutputProcesses` in the `finally` (`tests\_workspace.ps1:371`, writes
  `$results['no_process_left_in_the_build_output']`) = **27**.
- `m2`: 31 `Check '…'` call sites, but `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone`
  appears twice (line 330 and its `else` at line 334) — **30 distinct names** + 1 from
  `Assert-NoBuildOutputProcesses` = **31**.
- `compression`: 18 `Check '…'` call sites + 1 from `Assert-NoBuildOutputProcesses` = **19**.

**Total counted: 77.** This is the number each suite prints on its own tally line
(`"{0} checks, {1} failed" -f $results.Count, …`).

## Verdict totals

| | count |
|---|---|
| unit (content question, movable down a layer) | 41 |
| integration (real wiring) | 35 |
| unclear | 1 |
| **total** | **77** |

35 integration checks prove **18 distinct wires**.

---

## Classification table

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| `daemon1_killed_mid_turn` | m0 | integration | `shim-lane<N>.json` exists and parses after a `lane-start` with a fake agent | W14 — a lane really spawns: a shim process pair and its on-disk record | Misleadingly named: it asserts nothing about the kill. It is a fixture precondition that happens to be the only place the spawn wire is asserted at all. |
| `shim_alive_no_daemon` | m0 | integration | the shim pid is still a live process after `Stop-Process` on the daemon | W1 — a killed daemon does not take its shim or agent with it | Irreducible: a process was really killed with `-Force` and another really survived. |
| `agent_alive_no_daemon` | m0 | integration | the agent (child) pid is still live with no daemon in existence | W1 | The half that matters — the agent is the expensive process. |
| `orphaned_result_landed` | m0 | integration | a result produced while NO daemon existed appears in `tail` after daemon #2 starts | W2 — a replacement daemon re-adopts the orphaned shim and drains its buffer | The whole of design §16 in one check. Genuinely about timing and buffered process I/O. |
| `landed_exactly_once` | m0 | **unit** | the drained result appears exactly once, not twice | `Store.PaneEventId` — `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)`, [Store.cs:1022](src/Dodona/Store.cs#L1022), `return 0` at line 1030 "duplicate seq: the shim redelivered" | This fixture does not actually produce a redelivery: the result line was never delivered to daemon #1, so daemon #2 replays it once from `delivered`. The only way it could double is the store dedup breaking — which is a pure two-insert-same-seq store test and strictly stronger than what is asserted here. |
| `session_id_recorded` | m0 | **unclear** | `dodona status` output matches `session=fake-` | content: `LaneRuntime.HandleShimLine` `system`/`init` → `Store.LaneSession` ([LaneRuntime.cs:118-121](src/Dodona/LaneRuntime.cs#L118)) + the `status` renderer | Reads as a content check on a rendered string, but it is placed AFTER the daemon restart, so it may have been intended as "adoption preserved the session id". Cannot tell which from the file; both readings are defensible. |
| `same_agent_answers_daemon2` | m0 | integration | a fresh `say` through daemon #2 is answered by the SAME agent process | W3 — a typed sentence crosses the control pipe, is routed, reaches a real agent, and the reply comes back on `tail` | Real, but the same wire `tier0_message_delivered` proves from the operator's own entry point. |
| `a_lane_agent_is_told_its_workspace` | m0 | integration | the agent reports `DODONA_WORKSPACE` = the workspace id, asked of the agent itself | W4 — the spawn-site environment reaches the agent two process hops away (daemon → shim → child) | A spawn-site environment is invisible to any in-proc test; the shim not touching its child's environment is the assertion. Keep. |
| `shim_exits_when_its_agent_dies` | m0 | integration | the shim pid disappears after ONLY its child was killed | W5 — a shim exits by itself when its agent dies, taking its pipe name and record with it | Irreducible process death, and nothing killed the shim. |
| `dead_lane_pipe_leaves_the_namespace` | m0 | integration | the lane pipe name is gone from `\\.\pipe\` | W5 | A consequence of W5 plus an OS fact; it re-proves the same wire. |
| `shim_record_dies_with_the_shim` | m0 | integration | `shim-lane<N>.json` is deleted on the shim's exit path | W5 | Same wire; the file deletion is the third symptom of one exit. |
| `ps_counts_a_lane_whose_record_is_gone` | m0 | integration | `dodona ps --json` reports the same lane count for this workspace with and without the record file | W6 — `dodona ps` counts from the OS (pipe namespace ∪ live shim pid), not from `shim-lane*.json` | The union ALGEBRA is pure (`LaneLiveness.Live`, [LaneLiveness.cs:79](src/Dodona/LaneLiveness.cs#L79)) and deserves a unit test beside this, but a pure test cannot catch the `ps` handler going back to `Records()`. Keep the wire. |
| `stopall_stops_a_lane_whose_record_is_gone` | m0 | integration | the shim process is gone after `stop-all --lanes` with no record on disk | W7 — `stop-all --lanes` reaches a shim over its own pipe (`##shutdown`) and takes the child tree with it | Needs no bookkeeping by design; that is precisely what only a real pipe can prove. |
| `and_its_agent_dies_with_it` | m0 | integration | the child pid is gone too | W7 | Same wire; the child-tree kill is the point. |
| `lease_expires_when_no_daemon_ever_returns` | m0 | integration | six shims exit on their own after `DODONA_SHIM_LEASE_SEC=2`, with the daemon `-Force`-killed so no `##shutdown` can reach them | W8 — a shim with no daemon left to deliver to ends its own lease and takes its agent | A real timer in a real process with nothing left to stop it. Irreducible. |
| `the_lease_takes_the_agents_too` | m0 | integration | their child pids are gone too | W8 | Same wire. |
| `and_the_lane_pipes_are_gone` | m0 | integration | `LanePipes` is empty | W8 | Same wire; an OS consequence of the two above. |
| `reconcile_does_not_knock_on_pipes_that_are_gone` | m0 | integration | daemon #4 answers its control pipe in < 4000 ms with N `alive` rows and zero pipes | W9 — reconcile asks the OS before knocking, and answers the control pipe promptly | A wall-clock budget over a real `\\.\pipe\` namespace. The decision itself (`attempts = !pipeLive ? 0 : …`, [Daemon.cs:713](src/Dodona/Daemon.cs#L713)) is a pure function begging to be extracted (`ReconcileAttempts(role, pipeLive, predecessorPid)`) and unit-tested alongside. |
| `reconcile_says_it_asked_the_os` | m0 | **unit** | the latest `lane_unreachable` detail matches `no pipe and no live shim process` | content: the reconcile unreachable branch's message string, [Daemon.cs:720-721](src/Dodona/Daemon.cs#L720) | Pure string. Blocker: written inline via `_store.Event` inside `ReconcileAsync`; needs the message assembled by a named function to be asserted without a daemon. |
| `a_missing_shim_is_named_not_guessed` | m0 | **unit** | `lane-start` output matches `shim binary not found` | content: `Daemon.AttachShimAsync`'s `File.Exists(shimExe)` branch, [Daemon.cs:4113-4120](src/Dodona/Daemon.cs#L4113) | Pure string over one boolean. Blocker: the branch is inside a private `async Task` that also calls `Process.Start` — no seam for the spawner. |
| `a_failed_spawn_leaves_no_lane_claiming_alive` | m0 | **unit** | the count of `lanes` rows with `state='alive'` is unchanged after a failed spawn | content: same branch — `_store.LaneState(id, "unreachable")` then `return (-1, …)` | State-transition question over a `Store`; a store + a stubbed spawner answers it in milliseconds. Same seam blocker. |
| `a_failed_spawn_is_recorded` | m0 | **unit** | at least one `shim_spawn_failed` event exists | content: same branch, `_store.Event("shim_spawn_failed", …)` | Same. |
| `status_does_not_summon_a_daemon` | m0 | integration | no ctl pipe appears within 4 s of `dodona status` with autostart CLEARED | W10 — a command on the no-summon list starts no daemon process and no lanes | Irreducible: the assertion is that a PROCESS did not come into existence. |
| `ticket_record_does_not_summon_a_daemon` | m0 | integration | no daemon appeared AND stderr says `daemon not running` | W10 | Same wire; the string half is content on the `Fail(...)` message at [Program.cs:1621](src/Dodona/Program.cs#L1621). |
| `status_says_the_workspace_is_asleep` | m0 | **unit** | stderr matches `ASLEEP` | content: the no-summon message at [Program.cs:1618](src/Dodona/Program.cs#L1618), reached via `neverSummons = neverSummon \|\| cmd is "stop-daemon" or "status" or "land-status" or "ticket-record"` ([Program.cs:1611](src/Dodona/Program.cs#L1611)) | Two pure pieces: the membership predicate and the message. Blocker: both live inside the pipe-connect method — extracting `NeverSummons(cmd, flag)` and `AsleepMessage(ws)` makes them free. |
| `status_creates_no_lanes` | m0 | integration | `COUNT(*) FROM lanes` is unchanged | W10 | Same wire; the consequence that costs money (four warm-up haiku lanes). |
| `no_process_left_in_the_build_output` | m0 | integration | no live process has a path under `<repo>\src\…\bin\` | W18 — the suite leaks no process into the build output | Harness hygiene, not a product wire. `dev gate`'s I3 asserts the same thing over the whole run. |
| `an_out_of_claim_branch_is_granted_the_token` | m2 | **unit** | `token-request` exits 0 and says `granted ticket 1` for a branch touching a path outside its claim | content: `Store.TokenRequest` + the absence of a claim refusal in the `token-request` case, [Daemon.cs:1789-1821](src/Dodona/Daemon.cs#L1789) | A "the refusal is gone" check. Blocker: the grant decision is not a named function — it is a `case` in `HandleAsync`; a pure test needs `TokenDecision(...)` extracted or the check keeps needing a live daemon to be sure no NEW refusal was added. |
| `the_branch_touch_is_recorded_for_the_reviewer` | m2 | **unit** | the `branch_touched` detail names `src/water/sim.cs` | content: `Claims.Normalize` ([Claims.cs:11](src/Dodona/Claims.cs#L11)) + the detail string built at [Daemon.cs:1816-1818](src/Dodona/Daemon.cs#L1816) | Given a list of `git diff --name-only` lines and a claim prefix, what does the detail say. The `git diff` is an input, not the subject. |
| `the_record_singles_out_the_undeclared_path` | m2 | **unit** | the same detail matches `undeclared:.*src/sky/box\.cs` | content: `Claims.Covers` ([Claims.cs:153](src/Dodona/Claims.cs#L153)) + the same formatter | Pure set difference plus formatting. |
| `the_token_is_granted_after_an_extend_too` | m2 | **unit** | `token-request` says `granted ticket 1` after `claim-extend` | content: `Store.TokenRequest` | Same content question as the first row, re-asked with different claim rows. |
| `an_extended_claim_leaves_nothing_undeclared` | m2 | **unit** | the newest `branch_touched` detail names the path and has no `undeclared:` | content: `Claims.Covers` over the extended claim set + the formatter | Pure. |
| `presence_shows_tool` | m2 | **unit** | `status` shows `presence=write: box.cs` while a tool runs | content: `LaneRuntime.HandleShimLine` `assistant`/`tool_use` → `LanePresence($"{tool.ToLowerInvariant()}: {detail}")`, [LaneRuntime.cs:154-160](src/Dodona/LaneRuntime.cs#L154) | **`ILaneSink` ([LaneSink.cs:22](src/Dodona/LaneSink.cs#L22)) is a ready-made seam**: a fake sink lets one wire line be fed in and the presence string read out with no daemon, no store, no pipe. Only `HandleShimLine` being `private void` stands in the way. |
| `presence_idle_after_result` | m2 | **unit** | `status` shows `presence=idle` after the turn ends | content: same method, `result` branch → `LanePresence(Id, "idle")` [LaneRuntime.cs:196](src/Dodona/LaneRuntime.cs#L196) | Same seam. The `Start-Sleep -Seconds 2` in front of it is a real duration only because the check runs against a live agent. |
| `presence_shows_thinking_not_a_stale_tool` | m2 | **unit** | `presence=thinking` and NOT `presence=read: box.cs` | content: same method, `system`/`thinking_tokens` branch → `LanePresence(Id, "thinking…")` [LaneRuntime.cs:141](src/Dodona/LaneRuntime.cs#L141) | Same seam. Two wire lines in, one string out. |
| `thinking_writes_no_pane_rows` | m2 | **unit** | 20 thinking events add exactly the +1 progress row the Read earned | content: `HandleShimLine` (thinking writes no `PaneEvent`) + `PaneProgress.FromTool` tier ([PaneProgress.cs:74](src/Dodona/PaneProgress.cs#L74)) | Same seam; a fake sink counts the rows directly. `PaneProgress` already has `tests\Dodona.Tests\PaneProgressTests.cs`. |
| `tier0_prefix_routes` | m2 | **unit** | `dodona input "sky: …"` answers `-> SKY (tier 0)` | content: `Daemon.LanePrefix` ([Daemon.cs:5759](src/Dodona/Daemon.cs#L5759)) + `RouteInput`'s tier-0 verdict string ([Daemon.cs:5545](src/Dodona/Daemon.cs#L5545)) | `LanePrefix` is ALREADY unit-tested (`tests\Dodona.Tests\PureLogicTests.cs:626-645`). All this adds is the verdict string, which is content. |
| `tier0_message_delivered` | m2 | integration | the sentence appears in the target lane's `tail` | W3 — a typed sentence crosses the control pipe, is routed, reaches a real agent, and the reply comes back on `tail` | This is the operator's own path (`dodona input`) end to end. Keep exactly one of these. |
| `focus_routes_optimistically` | m2 | **unit** | `dodona input` answers `-> WATER (focus…` with no classifier warm | content: `RouteInput`'s `routerId < 0` branch verdict string, [Daemon.cs:5615](src/Dodona/Daemon.cs#L5615) | Pure string over `_config.Brain` and the focused row. |
| `focus_message_delivered` | m2 | integration | the sentence appears in the focused lane's `tail` | W3 | The same wire as `tier0_message_delivered`, one rung of the ladder along. |
| `unrouted_fallback_is_announced` | m2 | **unit** | a `routing_unrouted` event exists whose detail matches `classifier\|brain` | content: `RouteInput`'s once-per-daemon `_saidNoClassifier` branch, [Daemon.cs:5605-5613](src/Dodona/Daemon.cs#L5605) | Pure: a boolean latch, `_config.Brain`, and two strings. |
| `stale_focus_falls_back_to_a_live_lane` | m2 | **unit** | exit 0 and `-> (WATER\|SKY)` after `focus 999` | content: `RouteInput`'s focus-pick rung — focused id not in `live` → `live[^1]`, [Daemon.cs:5561-5571](src/Dodona/Daemon.cs#L5561) | Pure over a lane list and a kv value. |
| `routing_rows_recorded` | m2 | **unit** | `routing_decisions` contains rows tiered `prefix` and `focus` | content: `Store.RoutingInsert` + the tier labels `RouteInput` passes | Pure: which literal each rung records. |
| `promotion_lane_started` | m2 | integration | `lane-start` returned `lane <n>` | W14 — a lane really spawns | Fixture precondition; re-proves W14. |
| `the_write_is_still_refused` | m2 | integration | a real `dodona gate-hook` child process, fed the PreToolUse JSON on stdin via `cmd /c`, prints `"permissionDecision":"deny"` | W11 — a PreToolUse gate-hook process fed JSON on stdin denies a shared-checkout write | A separate process, stdin, and an exit protocol. `m1` owns this wire as its subject; here it is the promotion's trigger. |
| `the_refusal_names_the_new_ticket_and_the_new_path` | m2 | **unit** | the deny reason matches `ticket \d+`, `wt[\\/]+t\d+` and `Nothing was written` | content: `Daemon.PromoteLane`'s return message, [Daemon.cs:3666-3670](src/Dodona/Daemon.cs#L3666) | Pure string assembly from a ticket id and a path. |
| `the_lane_ends_up_in_a_worktree` | m2 | integration | `lanes.cwd` for the promoted lane matches `wt[\\/]+t\d+` | W12 — a refused write promotes the lane: a real worktree is created and the agent process is killed and respawned inside it, session resumed | `git worktree add`, a real process killed, `WaitLaneProcessesGone`, a real respawn. Irreducible, and the specific proof the respawn happened. Keep. |
| `the_promotion_is_recorded` | m2 | integration | ≥1 `lane_promoted` event for the lane | W12 | Written only on a successful respawn ([Daemon.cs:3649](src/Dodona/Daemon.cs#L3649)), so it re-proves the same wire. |
| `nothing_was_written_to_the_shared_checkout` | m2 | integration | `src\engine\e.cs` does not exist in the shared tree | W12 | Weak: the hook never invokes the tool, so this asserts that promotion did not create the file. A filesystem fact on the same wire. |
| `the_ticket_is_linked_to_the_same_lane` | m2 | **unit** | an `open` ticket row has `lane_id` = the promoted lane | content: `Store.TicketSetLane` + `MakeTicket`'s row shape | Pure store question. |
| `stopping_a_promoted_lane_abandons_its_ticket` | m2 | **unit** | the ticket's state is `abandoned` after `lane-stop` | content: the `lane-stop` promoted-ticket branch + `Daemon.AbandonTicket`'s `TicketState(t.Id,"abandoned")` [Daemon.cs:3690](src/Dodona/Daemon.cs#L3690) | The DECISION (which lanes take the abandon path) is pure; only the git side effects below are not. |
| `the_undo_prunes_the_worktree_and_the_branch` | m2 | integration | the worktree directory is gone from disk AND `git branch --list` is empty | W13 — the undo really removes a worktree directory and a git branch ref, after the agent process has let go of the cwd | Irreducibly a real race: Windows refuses to delete a directory that is any process's cwd, which is why `WaitLaneProcessesGone(stopping, 10000)` is at [Daemon.cs:3705](src/Dodona/Daemon.cs#L3705). Keep. |
| `a_pr_repo_still_promotes_a_refused_write` | m2 | **unit** | with `"delivery": "pr"` a promotion still yields a ticket and a branch | content: `Config.For(...).IsPr` does NOT gate `PromoteLane` | Vacuous against HEAD by construction (its own comment says so). A pure test of "IsPr is not consulted on this path" is exactly as vacuous and 200× cheaper. |
| `abandoning_a_pr_ticket_keeps_its_branch` | m2 | **unit** | `git branch --list <branch>` is still non-empty after `lane-stop` | content: `prKeepsBranch = Config.For(_primary, repoPath).IsPr && t.Branch.Length > 0`, [Daemon.cs:3716-3717](src/Dodona/Daemon.cs#L3716) | One boolean over a config value. The git observation is the consequence, not the question. |
| `abandoning_a_pr_ticket_still_prunes_the_worktree` | m2 | **unit** | the worktree directory is gone | content: same predicate — the prune is unconditional on `IsPr` | Same. |
| `the_undo_says_the_branch_was_kept_rather_than_reporting_a_deletion` | m2 | **unit** | `lane-stop` output matches `KEPT` and not `deleted` | content: the receipt line at [Daemon.cs:3720-3722](src/Dodona/Daemon.cs#L3720) | Pure string over `prKeepsBranch`. |
| `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone` | m2 | **unit** | ticket 1's state is not `abandoned` after stopping its agent lane | content: the `lane-stop` predicate that only PROMOTED tickets are Dodona's to withdraw | Vacuous by construction against HEAD (its own comment says so); it is a guard against a future widening, and a pure predicate test guards it identically. |
| `no_process_left_in_the_build_output` | m2 | integration | no live process under `<repo>\src\…\bin\` | W18 | Harness hygiene. |
| `store_migrated_to_declared_schema` | compression | **unit** | `PRAGMA user_version` equals the schema the binary declares in `version --json` | content: the `Store` migration ladder + `Ver.Schema` | Needs a real SQLite file but **no daemon, no pipe, no window** — `new Store(tempPath)` then read `PRAGMA user_version`. That is an in-proc test measured in milliseconds. |
| `no_pool_leaves_the_row_uncompressed` | compression | **unit** | the newest `result` row has `compressed IS NULL` with no pool warm | content: `Daemon.CompressResult`'s `if (pool.Count == 0) return;`, [Daemon.cs:4248](src/Dodona/Daemon.cs#L4248) | Pure early return. Blocker: `CompressResult` is a private void that reads `_store.LanesAll()` and `_lanes` directly — no seam for "the pool". |
| `no_pool_still_shows_the_agents_words` | compression | integration | a live `DodonaUi --test-window` renders the lane's pane lines including the agent's full text | W15 — a live window opened on this workspace renders lane panes from the store | Kept as this group's ONE proof that a window can open on this store and render a lane at all. The kind/column question inside it is `StoreReader.Tail`. |
| `midturn_narration_is_not_in_the_pane` | compression | **unit** | the rendered pane lines do not contain `working on:` | content: `StoreReader.Tail`'s kind filter — `agent_line` is absent from `kinds` once `HasCompressed()`, [StoreReader.cs:216-218](src/DodonaUi/StoreReader.cs#L216) | A SQL projection over a store file. Asking it through a live WPF window and a UIA dump is the whole cost of this check. |
| `midturn_narration_is_still_a_row` | compression | **unit** | ≥1 `pane_events` row of kind `agent_line` with body `working on:%` | content: `LaneRuntime.HandleShimLine` `assistant` branch → `PaneEvent(…, "agent_line", …)` | `ILaneSink` seam again. |
| `pool_is_two_sessions_not_one` | compression | integration | exactly 2 lanes with `role='compressor' AND state='alive'` | W17 — the compressor pool starts real sessions and a turn-final crosses to one and lands back on the row | Two real child processes were started. The arithmetic (`Math.Clamp(count,1,4)`, `alive >= count`) is pure and worth a separate unit test. |
| `pool_takes_no_grid_slot` | compression | **unit** | exactly one non-empty slot in `ui dump` with two compressors alive | content: the grid filter `lanes.Where(l => l.Role == "work" && l.State != "dead")`, [Poller.cs:108](src/DodonaUi/Poller.cs#L108) | A one-line predicate over `StoreReader.LaneR` rows, asked through a live window. |
| `turn_final_gets_compressed` | compression | integration | the newest `result` row for lane 1 has `compressed IS NOT NULL` and shorter than `body` | W17 | Needs the pool alive, `HookTurnEnd` wired onto `rt.OnResult`, an `AskAsync` round trip over a real shim pipe, and `PaneCompressed` written. The loudest single proof of this wire. Keep. |
| `compression_is_recorded_as_an_event` | compression | integration | ≥1 `compressed` event | W17 | Same wire; the event is written two lines after `PaneCompressed` ([Daemon.cs:4290-4292](src/Dodona/Daemon.cs#L4290)). |
| `raw_body_is_never_overwritten` | compression | **unit** | `length(body)` still equals the original text's length | content: `Store.PaneCompressed` writes the `compressed` column and never `body` | Pure store question: one insert, one `PaneCompressed`, read both columns. |
| `pane_shows_the_short_version` | compression | **unit** | the rendered pane contains the compressed headline | content: `StoreReader.Tail`'s `shown = "COALESCE(compressed, body)"`, [StoreReader.cs:214](src/DodonaUi/StoreReader.cs#L214) | A SQL projection, asked through a real window. |
| `compressed_turn_hides_its_long_tail` | compression | **unit** | the LAST `✓`-prefixed pane line does not contain the paragraph's tail | content: the same `COALESCE` projection | Same function, a second phrasing of the same question. |
| `overlay_selected` | compression | integration | `ui dump`'s `overlay` key equals the lane title after `dodona ui overlay <title>` | W16 — a `ui` verb reaches the live window and changes its state | A real command reaching a real window. `ui-grid` owns this wire as its subject. |
| `overlay_keeps_midturn_and_full_text` | compression | **unit** | `COUNT(*) FROM pane_events WHERE kind='agent_line'` is not `'0'` | content: nothing about the overlay at all — it is a store row count | **Mis-aimed.** The name claims the overlay shows unfiltered kinds; the assertion never reads the overlay. `StoreReader.Tail(all: true)` is what it means to test, and that is a pure projection. |
| `short_results_skip_the_compressor` | compression | **unit** | the `compressed` event count is unchanged after a short turn-final | content: `CompressResult`'s `if (body.Length <= 120 && !body.Contains('\n')) return;`, [Daemon.cs:4242](src/Dodona/Daemon.cs#L4242) | A pure length predicate. Extract as `Compression.WorthCompressing(body)`. |
| `blocked_uses_the_fixed_schema` | compression | **unit** | the compressed text matches `BLOCKED —` and `options:` | content: the fixed-shape assembly at [Daemon.cs:4278-4289](src/Dodona/Daemon.cs#L4278) — the `BLOCKED` de-duplication, `Truncate(flat, 90)`, and the `\n   options: ` join | Entirely pure: `(headline, needsYou, options[]) → string`. Extract as `Compression.Render(...)`. Today it costs a warm pool and a 30 s wait. |
| `progress_rows_are_written` | compression | **unit** | ≥3 `pane_events` rows of kind `progress` after three tool calls | content: `PaneProgress.FromTool` tier ≠ `Noise` + `HandleShimLine` writing the row | `ILaneSink` seam; `PaneProgress` already has its own unit suite. |
| `progress_rows_never_reach_a_compressor` | compression | **unit** | ≥3 progress rows AND none has a `compressed` value | content: `HookTurnEnd` sets only `rt.OnResult` ([Daemon.cs:4174-4183](src/Dodona/Daemon.cs#L4174)), so no progress row can enter `CompressResult` | Pure: which callback the compressor hangs off. |
| `no_process_left_in_the_build_output` | compression | integration | no live process under `<repo>\src\…\bin\` | W18 | Harness hygiene. |

---

## The 18 wires

| # | wire | checks proving it | best single check |
|---|---|---|---|
| W1 | a killed daemon does not take its shim or its agent with it | 2 | `m0:agent_alive_no_daemon` |
| W2 | a replacement daemon re-adopts the orphaned shim and drains its buffer | 1 | `m0:orphaned_result_landed` |
| W3 | a typed sentence crosses the control pipe, is routed, reaches a real agent process, and the reply comes back on `tail` | 3 | `m2:tier0_message_delivered` |
| W4 | the spawn-site environment reaches the agent two process hops away (daemon → shim → child) | 1 | `m0:a_lane_agent_is_told_its_workspace` |
| W5 | a shim exits by itself when its agent dies, taking its pipe name and its record with it | 3 | `m0:shim_exits_when_its_agent_dies` |
| W6 | `dodona ps` counts lanes from the OS, not from `shim-lane*.json` | 1 | `m0:ps_counts_a_lane_whose_record_is_gone` |
| W7 | `stop-all --lanes` reaches a shim over its own pipe and takes the child tree with it | 2 | `m0:and_its_agent_dies_with_it` |
| W8 | a shim with no daemon left to deliver to ends its own lease and takes its agent | 3 | `m0:the_lease_takes_the_agents_too` |
| W9 | reconcile asks the OS before knocking, and the daemon answers its control pipe promptly | 1 | `m0:reconcile_does_not_knock_on_pipes_that_are_gone` |
| W10 | a command on the no-summon list starts no daemon process and no lanes | 3 | `m0:status_does_not_summon_a_daemon` |
| W11 | a PreToolUse gate-hook process, fed JSON on stdin, denies a shared-checkout write | 1 | `m2:the_write_is_still_refused` |
| W12 | a refused write promotes the lane: a real worktree is created and the agent process is killed and respawned inside it | 3 | `m2:the_lane_ends_up_in_a_worktree` |
| W13 | the undo really removes a worktree directory and a git branch ref, after the agent lets go of the cwd | 1 | `m2:the_undo_prunes_the_worktree_and_the_branch` |
| W14 | `lane-start` really produces a live shim + agent pair with a record on disk | 2 | `m0:daemon1_killed_mid_turn` |
| W15 | a live window opened on this workspace renders lane panes from the store | 1 | `compression:no_pool_still_shows_the_agents_words` |
| W16 | a `ui` verb reaches the live window and changes its state | 1 | `compression:overlay_selected` |
| W17 | the compressor pool starts real sessions and a turn-final crosses to one and lands back on the row | 3 | `compression:turn_final_gets_compressed` |
| W18 | the suite leaks no process into the build output | 3 | `m0:no_process_left_in_the_build_output` |

Wires already owned as SUBJECT by another suite (so this group re-proves them rather than owning
them): **W11** is `m1`'s write gate; **W15/W16** are `ui-grid`/`m3`'s; **W18** is `dev gate`'s I3.

---

## Blockers (what makes a check hard to move down)

1. **`LaneRuntime.HandleShimLine` is `private void` but `ILaneSink` is already the seam.** Seven
   checks (`presence_shows_tool`, `presence_idle_after_result`,
   `presence_shows_thinking_not_a_stale_tool`, `thinking_writes_no_pane_rows`,
   `midturn_narration_is_still_a_row`, `progress_rows_are_written`, `session_id_recorded`) are
   "one stream-json line in, one presence string or one row out". `ILaneSink`
   (`src\Dodona\LaneSink.cs`) exists precisely so the concierge can drive this machinery without a
   workspace `Store` — a recording fake sink plus making `HandleShimLine` `internal` converts all
   seven to ~10 ms tests. **This is the single largest movable block in the group.**

2. **`Daemon.CompressResult` has no seam at all.** Private void; reads `_store.LanesAll()` and the
   `_lanes` dictionary directly; the pool pick, the `AskAsync`, the JSON parse and the BLOCKED
   rendering are all one method. Four content checks
   (`no_pool_leaves_the_row_uncompressed`, `short_results_skip_the_compressor`,
   `blocked_uses_the_fixed_schema`, and half of `turn_final_gets_compressed`) are stuck behind a
   warm pool of two real processes. Two extractions unlock them: `WorthCompressing(body)` and
   `Render(headline, needsYou, options)`.

3. **Verdict strings are produced inside `RouteInput`, not by a function.** `tier0_prefix_routes`,
   `focus_routes_optimistically`, `stale_focus_falls_back_to_a_live_lane`,
   `unrouted_fallback_is_announced` and `routing_rows_recorded` all assert what `RouteInput`
   *returns* or *records*, over a lane list and a kv value. `LanePrefix` and `IsObviousGeneric`
   were already pulled out for exactly this reason (see the P4.5 comment at
   `src\Dodona\Daemon.cs:5755`) — the rungs BELOW them were not.

4. **The `branch_touched` detail is assembled inline** in the `token-request` `case`
   (`Daemon.cs:1816`), and the token grant decision is a `case` body rather than a function. Four
   m2 checks need a live daemon + a real git repo to ask a `Claims.Covers` question.

5. **The gate/no-summon predicates live inside I/O methods.** `neverSummons` (`Program.cs:1611`)
   and the ASLEEP message (`Program.cs:1618`) sit inside the pipe-connect path, so the membership
   question cannot be asked without attempting a connection.

6. **Reconcile's `attempts` expression** (`Daemon.cs:713`) is the decision
   `reconcile_does_not_knock_on_pipes_that_are_gone` is really about, but the check can only observe
   it as wall-clock, because reconcile runs before the control pipe server exists and writes nothing
   until it is done.

7. **Four UI content checks require a live WPF window** (`midturn_narration_is_not_in_the_pane`,
   `pane_shows_the_short_version`, `compressed_turn_hides_its_long_tail`, `pool_takes_no_grid_slot`)
   although all four are answered by `StoreReader.Tail` / the `Poller.cs:108` role filter over a
   store FILE. `StoreReader` opens a path — it does not need a window — so these are movable to an
   in-proc test against a fixture `.db`, provided the fixture is built by production code rather
   than by hand (drift risk).

8. **`a_missing_shim_is_named_not_guessed` / `a_failed_spawn_*`**: the failure branch is inside
   `AttachShimAsync`, which also calls `Process.Start`. No spawner seam, so a "spawn that fails"
   currently has to be produced by pointing `DODONA_SHIM` at a nonexistent path and restarting a
   real daemon (the suite does exactly that, at the cost of a whole extra daemon — the comment at
   `m0-acceptance.ps1:335` explains why it cannot reuse daemon #4).

## Notes for synthesis

- **`landed_exactly_once` is not proving what its name says.** The fixture never creates a
  redelivery: daemon #1 died before the result line existed, so daemon #2 replays it once. The only
  mechanism that could double it is `Store.PaneEventId`'s `INSERT OR IGNORE` on
  `UNIQUE(lane_id, seq)` failing — a two-insert store test that is both stronger and free.
- **`overlay_keeps_midturn_and_full_text` never reads the overlay.** It asserts an `agent_line` row
  count. Whatever replaces it should assert `StoreReader.Tail(all: true)`, which is the function the
  name is about.
- **Three checks are VACUOUS against HEAD by construction and say so in their own comments**
  (`a_pr_repo_still_promotes_a_refused_write`, `abandoning_a_pr_ticket_still_prunes_the_worktree`'s
  sibling reasoning, `stopping_a_deliberate_ticket_lane_leaves_the_ticket_alone`). They are guards
  against a future widening. A pure predicate test guards identically and cannot be flaky — moving
  them down loses nothing and removes three live-daemon fixtures.
- **`daemon1_killed_mid_turn` should be renamed if it survives as W14's proof.** It asserts the
  shim-info record exists; nothing in it is about the kill.
- **W5's three checks are one exit observed three ways** (process gone, pipe name gone, record file
  gone). Two of the three are downstream consequences, and `dead_lane_pipe_leaves_the_namespace` is
  partly an assertion about Windows.
- **Do not move the pipe-namespace checks to instantaneous reads.** `CLAUDE.md` §0.2 measured
  8 of 192 reads over 1.5 s seeing no pipe while the shim was alive; the suite already knows this
  (`m0-acceptance.ps1:171,241,286` each carry the reasoning) and asserts on PROCESSES first, then on
  the namespace only after the process is provably gone. Any rework must preserve that ordering.
- **`m0` and `m2` both count on `Assert-NoBuildOutputProcesses` running LAST in the `finally`.**
  If suites are merged or restructured, that check must stay after the suite's own cleanup, or it
  reports leaks it caused itself.
