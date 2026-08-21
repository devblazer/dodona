# survey-delivery — classification of every check in `tests/m1-acceptance.ps1`

Group: **delivery** (the write gate, the merge token, the land, the completion record, the lane briefing).
File surveyed: `C:\Users\devbl\Documents\personel\Dodona\tests\m1-acceptance.ps1` (1195 lines).

**Counted, not estimated.** 118 `Check '<name>'` registrations + 16 registered by the `foreach`
loop at line 1166 (`Check "event_$k"`) + 1 registered by `Assert-NoBuildOutputProcesses`
(`tests\_workspace.ps1:371`, key `no_process_left_in_the_build_output`) = **135**.
That matches CLAUDE.md's measured "135 checks, 0 failed, every time" for m1.

## The rule applied

A check is **integration** only if cutting a *wire* is what makes it fail — an IPC hop, a process
spawn or death, a git ref or file actually moving, a call site actually being called, or two
things actually racing. A check is **unit** if the same assertion could be made by calling a
production function with constructed inputs, **even where that input is a scripted git repo
directory or a temp SQLite file** — those are cheap, deterministic, in-process dependencies, not
a daemon, a named pipe, a window or a child agent. Where that grey zone applies the `note` column
says so.

Duplicated wire-provers whose own assertion is pure content are marked **unit** and are still
counted in `checksProvingIt` for their wire below, so the wire table shows the real re-proof
factor while `movable` stays an honest count of what a pure test can answer.

## Wire ids used in the table

| id | wire |
|---|---|
| W1 | `ticket-create` runs a real `git worktree add`; a checked-out tree appears on disk |
| L | a lane spawn starts a real shim + child agent the daemon can address |
| G1 | `DeployGate` writes a per-lane gate registration into workspace state, and the command it names runs as a real subprocess, reaches the daemon over the ctl pipe (`tree-check`) and returns a verdict |
| G3 | layer 2: a denied write promotes the plain lane into a ticket + worktree and **respawns** it, and the respawn rebuilds the briefing |
| R1 | a fake agent's turn end crosses the shim wire into the daemon and fires the per-turn hook at the **spawn** site |
| R2 | the completion record triggers the manager review, which honours `DODONA_NO_AUTOSTART` and starts **no model process** |
| R3 | a daemon restart, reconcile adopting a live shim, and the per-turn hook wired at the **adoption** site |
| M | the daemon performs a real `git merge main` + `merge --ff-only` that moves the shared checkout's `main` ref, prunes the worktree and deletes the branch — and every refusal leaves that ref untouched |
| V | the configured `verify` command runs as a real child process in the worktree and its exit code gates the ref advance, **before** it |
| D | `LandFlow` consults `SilentDrops` over the branch's real merge history and refuses on a non-empty result |
| A | the land runs **off** the serial control pipe: `LandBegin` answers in ms while `LandFlow`'s verify still runs, and the daemon still serves other commands |
| B | the per-turn briefing is delivered on the **agent** wire and never on the pane/event rows the operator and the compressor read |
| H | suite hygiene: no process this suite started is still executing out of `src\*\bin` |

## The table

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| ticket1_created | m1 | unit | `ticket-create` prints `ticket 1 branch ticket/1` | `Store.TicketCreate` + the `ticket/{id}` branch-naming rule | id allocation and message format; the pipe hop is proved everywhere else |
| worktree1_exists | m1 | integration | a file from `main` exists at `.dodona\wt\t1\src\water\sim.cs` | wire W1 | a real `git worktree add` put files on disk |
| gate_writes_nothing_into_the_project | m1 | unit | no `settings.local.json`, no `dodona-gate.ps1`, no `info/exclude` block in the project | `Daemon.DeployGate` (its target path) | D-17 regression guard; pure once `DeployGate`'s target is a seam, though it is a filesystem-effect question today (rides G1) |
| tracked_settings_untouched | m1 | unclear | either no tracked `.claude/settings.json`, or `git status` clean for it | `Daemon.DeployGate` | **vacuous in this fixture** — the file never exists, so the first disjunct always satisfies it. Cannot tell what it would assert against a repo that has one |
| an_overlapping_ticket_is_created | m1 | unit | a second ticket over a claimed path is created (exit 0) | `Store.TicketCreate` (D-R5: no refusal) | |
| the_overlap_is_reported_and_names_the_holder | m1 | unit | the create output carries `overlap:` and `ticket 1` | `Claims.Overlap` + the create-reply formatter | |
| disjoint_parallel | m1 | unit | a disjoint claim creates cleanly | `Claims.Overlap` (negative) | |
| claim_extend_refuses_a_spec_it_cannot_parse | m1 | unit | `claim-extend` with a bare path exits non-zero, says `bad claim spec`, never says `extended ticket` | `Claims.Parse` + `claim-extend` arg validation | P0.5 |
| claim_extend_refuses_the_whole_batch_on_one_bad_spec | m1 | unit | one good spec among bad ones does not smuggle the batch through | `Claims.Parse` + `claim-extend` arg validation | |
| ticket_agent_started | m1 | integration | `ticket-agent 1 --child DodonaFakeAgent.exe` prints a lane id | wire L | a real shim + child process was started |
| gate_registration_names_a_command | m1 | unit | `gate-lane<N>.json` has a non-empty `hooks.PreToolUse[0].hooks[0].command` | `Daemon.DeployGate` | content of the file DeployGate writes |
| gate_file_carries_only_the_hook | m1 | unit | the gate file has exactly the keys `hooks` then `PreToolUse` | `Daemon.DeployGate` | one-key rule; command-line settings outrank project settings on collisions |
| gate_names_the_lane | m1 | unit | the deployed command contains `--lane <N>` | `Daemon.DeployGate` | |
| gate_allows_a_write_inside_the_worktree | m1 | unit | the hook is silent for a path inside the worktree | `Program.GateHook` + `Trees.Locate` / `Trees.Allowed` | rides G1; its own comment records that silence also means "produced nothing", so it is the weak half |
| a_write_outside_the_claim_but_inside_the_worktree_is_allowed | m1 | unit | the hook is silent for an in-worktree path outside the claim | `Program.GateHook` (no claim question, D-R5/R3) | rides G1 |
| the_allow_is_a_decision_not_a_fail_open | m1 | unit | `.dodona-bypass.log` is empty after that allow | `Program.GateAllowedUnchecked` (never called) | rides G1; a file-absence assertion, pure once the log path is a seam |
| plain_lane_started | m1 | integration | `lane-start --child DodonaFakeAgent.exe` prints a lane id | wire L | duplicate of `ticket_agent_started` |
| gate_denies_a_plain_lane_writing_the_shared_checkout | m1 | unit | `"permissionDecision":"deny"` for a shared-checkout path on a ticketless lane | `Trees.Locate` -> `SharedCheckout` -> `Program.GateDeny` | rides G1; also *triggers* the G3 promotion but asserts nothing about it |
| the_refusal_names_the_shared_checkout_and_where_to_write_instead | m1 | unit | the refusal says `SHARED CHECKOUT`, `Write this file at`, `ticket \d+` | `Daemon.PromoteLane`'s message | the `ticket \d+` fragment is the only wiring-dependent part; rides G3 |
| the_refusal_confirms_nothing_was_written_to_the_shared_tree | m1 | unit | the refusal says `Nothing was written` | `Daemon.PromoteLane` / `Program.GateDeny` wording | |
| gate_tells_the_two_trees_apart_for_a_plain_lane | m1 | unit | deny for the shared path AND silence for the worktree path, in one assertion | `Trees.Locate` discrimination | rides G1; the strongest *content* form of the gate question |
| gate_denies_a_ticket_lane_writing_its_claim_in_the_shared_checkout | m1 | **integration** | driving the **deployed** command out of `gate-lane<N>.json` denies an absolute shared-checkout write from a ticket lane | wire G1 — **survivor** | the only check that runs DeployGate file -> real subprocess -> ctl pipe -> verdict end to end |
| unparseable_input_is_recorded | m1 | unit | stderr says `gate fail-open` and `unparseable` | `Program.GateAllowedUnchecked` diagnostic text | |
| the_fail_open_says_how_much_it_got | m1 | unit | the diagnostic contains a byte count | `Program.GateAllowedUnchecked` | R3's byte-count-and-prefix diagnostic |
| the_fail_open_reaches_the_backstops_log | m1 | unit | `.dodona-bypass.log` carries `fail-open ... unparseable` | `Program.GateAllowedUnchecked` (log write) | pure once the log path is a seam |
| unreadable_input_is_refused_not_allowed_into_the_live_tree | m1 | unit | unparseable stdin with a readable lane yields `permissionDecision ... deny` | `Program.GateHook` unparseable branch -> `GateDeny` | |
| the_gate_denies_a_lane_argument_it_cannot_read | m1 | unit | `--lane not-a-number` denies | `Program.GateHook` argument parsing | **no daemon involved at all** — this one never reaches `GateAsk` |
| the_misconfiguration_refusal_says_it_is_not_the_agents_mistake | m1 | unit | that refusal contains `misconfiguration` | `Program.GateDeny` wording | |
| the_gate_still_checks_the_tree_when_the_ticket_argument_is_unreadable | m1 | unit | `--lane <good> --ticket abc` still reaches the tree question and denies | `Program.GateHook` fall-through (issue #4) | needs the tree answer, so it rides G1; the decision itself is pure |
| the_unreadable_ticket_is_reported_without_claiming_a_fail_open | m1 | unit | stderr names the bad `--ticket` and never says `gate fail-open` | `Program.GateHook` stderr text | |
| a_finished_turn_produces_a_completion_record | m1 | **integration** | a `say` to a fake agent ends a turn and one `completion_record` event appears | wire R1 — **survivor** | proves `HookTurnEnd` / `OnResult` is wired at the SPAWN site; fails with "no record" if the wire is cut |
| the_record_names_the_ticket_its_branch_and_its_worktree | m1 | unit | `branch == ticket/1`, `worktree == <wt1>` | `Daemon.CompletionRecord` field assembly | |
| the_record_carries_what_the_branch_changed | m1 | unit | `files == 1`, `changed` contains `src/water/sim.cs`, `diffstat` names it | `Daemon.CompletionRecord` + `git diff --stat main...branch` | needs a scripted git repo, not a daemon |
| the_record_carries_the_agents_own_report | m1 | unclear | `record.report` equals the sentence the fake agent echoed back | `Daemon.CompletionRecord` | genuinely ambiguous: field mapping (content) vs "the agent's own words survived the shim wire" (R1). Resolves either way once `CompletionRecord` takes the turn body as a parameter |
| the_record_says_the_verify_has_not_run_rather_than_leaving_it_blank | m1 | unit | `verify.state == 'not-run'` with a detail longer than 20 chars | `Daemon.CompletionRecord` verify slot (D-R15) | |
| the_drop_check_runs_at_completion_and_says_moot_before_main_is_merged_in | m1 | unit | `drop.state == 'moot'` | `Daemon.CompletionRecord` + `Daemon.SilentDrops` moot path | |
| the_manager_review_starts_no_model_agent_when_autostart_is_off | m1 | **integration** | a `manager_review_skipped` event naming ticket 1 and `DODONA_NO_AUTOSTART`, and **zero** `brain%` lanes in the store | wire R2 — **survivor** | asserts a process was NOT spawned; quota guard, irreducible |
| a_chatty_turn_produces_no_second_record | m1 | unit | still exactly 1 `completion_record` after a turn that moved nothing | `Daemon.CompletionRecord` digest gate (D-R13) | |
| the_skipped_record_says_why_rather_than_being_silent | m1 | unit | the skip event detail names `ticket 1` and `unchanged` | `Daemon.CompletionRecord` skip event text | |
| a_turn_that_changed_the_worktree_gets_its_own_record | m1 | unit | `files == 2`, `changed` contains `foam.cs`, digest differs | `Daemon.CompletionRecord` digest gate | needs a git fixture |
| an_adopted_lane_still_produces_a_record_after_a_daemon_restart | m1 | **integration** | after `stop-daemon` plus a fresh daemon, the adopted lane's next turn still writes a record | wire R3 — **survivor** | real process death + reconcile + adoption; nothing in the record's own code path can catch this (the §3 dead-routing-ladder shape) |
| the_record_gate_survives_a_restart_rather_than_resetting | m1 | unit | still exactly 3 records after an unchanged turn post-restart | `Daemon.CompletionRecord` reads the prior digest from the store, not memory | rides R3; the "reads from the store" property is pure given a Store |
| unapproved_token_refused | m1 | unit | `token-request 1` exits 1 with `not approved` | `Daemon.LandGate` / `Store.TokenRequest` approval gate | a table decision |
| the_unapproved_refusal_names_the_question_instead_of_only_a_command | m1 | unit | refusal matches `dodona answer \d+ yes` and exactly one open `land` question row for ticket 1 | refusal wording + `Store.QuestionUpsert` idempotence | |
| approved_token_granted | m1 | unit | after `approve 1`, `token-request 1` says `granted ticket 1` | `Store.TicketApprove` + `Store.TokenRequest` | |
| second_ticket_queued | m1 | unit | a second approved ticket gets `queued` | `Store.TokenRequest` FIFO | |
| ticket1_landed | m1 | unit | `land 1` prints `landed ticket 1` | `Daemon.LandFlow` outcome message | rides M |
| verify_ran_green | m1 | unit | the land output says `verify green` | `Daemon.LandFlow` verify reporting | rides V; the file records that this passed under BOTH verify orders, so it is not the order check |
| main_advanced | m1 | integration | `main`'s sha equals the pre-land `ticket/1` tip | wire M | duplicate of the stale-branch survivor |
| worktree1_pruned | m1 | integration | `.dodona\wt\t1` no longer exists | wire M | a directory really went away |
| released_claim_reclaimable | m1 | unit | a new ticket can claim `subtree:src/water` again after the land | `Store` claim release on land + `Store.TicketCreate` | |
| queued_ticket_now_granted | m1 | unit | the queued ticket now says `granted ticket <n>` | `Store.TokenRequest` FIFO handoff | |
| stale_branch_was_really_stale | m1 | unit | `main` is not an ancestor of `ticket/<n>` before the land | fixture precondition guard | disappears in a pure test, which constructs the stale state directly |
| stale_branch_lands_with_no_human_rebase | m1 | integration | a genuinely stale branch lands with exit 0 | wire M | the daemon really ran `git merge main` in the worktree |
| the_land_says_it_merged_main_in | m1 | unit | the land output says `merged main in` | `Daemon.LandFlow` message | |
| main_is_now_the_merge_that_was_verified | m1 | **integration** | `main`'s tip subject is the daemon's own `merge main into ticket/<n> before landing` commit | wire M — **survivor** | proves merge-main-in + verify + ff-only + the ref advance in one assertion. Fold `worktree1_pruned` and a branch-deleted assertion into it |
| the_branch_work_came_with_it | m1 | integration | `sky v2` is in `main`'s log after the land | wire M | duplicate |
| red_verify_refuses_the_land | m1 | integration | with `verify: ["exit 3"]` the land exits 1 saying `VERIFY RED` | wire V | a real child process exited 3 |
| red_verify_leaves_main_unchanged | m1 | **integration** | `main`'s sha before == after the red land | wire V — **survivor** | the only check that tells verify-before-ref-advance from verify-after (D-5) |
| the_red_verify_refusal_says_main_is_untouched | m1 | unit | the refusal says `main unchanged` | `Daemon.LandFlow` wording | |
| the_same_ticket_lands_once_verify_is_green | m1 | integration | flipping `dodona.json` back lets the same ticket land | wire V | duplicate; also proves `Config.For` re-reads per call |
| a_conflicting_merge_refuses_the_land | m1 | integration | an add/add conflict makes the land exit 1 saying `conflict` | wire M | a real `git merge` conflicted |
| the_conflict_refusal_names_the_file | m1 | unit | the refusal names `src/clash/pane.cs` | `Daemon.LandFlow` conflict message | |
| a_conflict_leaves_main_unchanged | m1 | integration | `main`'s sha unchanged after the refused conflict land | wire M | duplicate |
| the_worktree_is_left_clean_not_half_merged | m1 | integration | `git status --porcelain` in the worktree is empty | wire M | `git merge --abort` really ran |
| no_merge_is_left_in_progress | m1 | integration | `git rev-parse --verify MERGE_HEAD` fails in the worktree | wire M | same fact as the row above, asked of git rather than the filesystem |
| an_agent_resolved_merge_lands | m1 | integration | after the agent resolves in its own worktree the same ticket lands | wire M | D-R3 |
| the_daemon_had_nothing_left_to_merge | m1 | unit | the land output says `already current with main` | `Daemon.LandFlow` message | |
| the_resolution_is_what_landed | m1 | integration | `src/clash/pane.cs` in the shared checkout holds the agent's resolution | wire M | the ff-only really moved the working tree |
| a_dirty_worktree_is_refused | m1 | unit | an uncommitted worktree makes the land exit 1 saying `uncommitted changes` | `Daemon.LandFlow` dirty-tree check | decision over `git status`; needs a git fixture, not a daemon |
| the_dirty_refusal_says_commit_and_warns_off_the_stash | m1 | unit | the refusal says `commit` and `stash` | `Daemon.LandFlow` wording | CLAUDE.md §5.2's repo-global stash trap |
| it_lands_once_the_work_is_committed | m1 | integration | committing then landing succeeds | wire M | duplicate |
| the_dropper_ticket_was_created | m1 | unit | ticket B exists | fixture precondition guard | |
| main_carries_the_change_that_is_about_to_be_dropped | m1 | unit | `main`'s `keep.cs` holds v2 | fixture precondition guard | |
| the_merge_brought_mains_change_into_the_branch | m1 | unit | the branch's `keep.cs` holds v2 after the agent's own merge | fixture precondition guard | |
| a_silent_drop_is_refused | m1 | **integration** | the land exits 1 saying `reverts` after the branch quietly restored v1 | wire D — **survivor** | proves `LandFlow` actually consults `SilentDrops` and refuses on it |
| the_drop_refusal_names_the_file | m1 | unit | the refusal names `src/drop/keep.cs` | `Daemon.SilentDrops` result -> message | |
| a_silent_drop_leaves_main_unchanged | m1 | integration | `main`'s sha unchanged | wire M | duplicate |
| the_drop_check_recorded_the_pre_merge_it_compared_against | m1 | unit | the `land_silent_drop` event detail matches `pre-merge [0-9a-f]{8}` | event-detail format from `Daemon.SilentDrops` | asserts the *format*, not that the reference point is correct |
| the_drop_check_did_not_quietly_skip | m1 | unit | that event detail names `src/drop/keep.cs` | `Daemon.SilentDrops` | |
| the_drop_check_reports_what_it_examined_on_a_clean_land | m1 | unit | a `land_drop_check` event says `drop(s) against pre-merge` | `Daemon.SilentDrops` reporting | the armed-not-vacuous guard |
| it_lands_once_mains_change_is_restored | m1 | integration | restoring v2 lets the branch land | wire D | duplicate; the refusal is a correction, not a dead end |
| an_ordinary_resolution_is_not_flagged_as_a_drop | m1 | unit | the 10c land output has no `reverts` | `Daemon.SilentDrops` no-false-positive | asserts a value captured ~200 lines earlier; needs a git fixture, not a daemon |
| expired_lease_cannot_land | m1 | unit | a 1-second lease slept past makes the land exit 1 saying `expired` | `Store.TokenRequest` / `Daemon.LandGate` lease check | reads `DateTime.UtcNow`; needs a clock seam. Carries this suite's only real `Start-Sleep -Seconds 2` |
| regrant_after_expiry_lands | m1 | unit | re-requesting after expiry reclaims the token and the land succeeds | `Store.TokenRequest` reclaim | the land half duplicates M |
| the_whole_tree_claim_is_created_and_its_overlap_reported | m1 | unit | `subtree:/` creates and reports overlapping ticket `<t3id>` | `Claims.Normalize` + `Claims.Overlap` with an empty value | the algebra bug this was written for is entirely pure |
| an_empty_path_claim_is_refused_rather_than_created | m1 | unit | `path:/` exits non-zero with `bad claim spec` | `Claims.Parse` | |
| a_wide_extension_succeeds_whatever_else_is_held | m1 | unit | `claim-extend <t3id> subtree:/` exits 0 | `Store.ClaimExtend` | |
| the_wide_extension_still_names_what_it_overlaps | m1 | unit | that output carries `overlap:` | `Claims.Overlap` | |
| the_whole_tree_covers_a_file_no_other_claim_names | m1 | unit | `claim-check` says `covered:` for a file under the wide claim | `Claims.Covers` | |
| land_returns_before_its_verify_has_run | m1 | integration | `land --no-wait` returns in under 3000 ms saying `landing ticket <n>` while the verify sleeps ~8 s | wire A | a stopwatch over a real async split |
| land_status_says_running_while_it_runs | m1 | integration | `land-status` says `state=running` | wire A | what makes the probes below evidence rather than a race |
| status_answers_during_a_land | m1 | integration | `status` answers in under 3000 ms while `state=running` | wire A | duplicate of the `say` probe |
| say_answers_during_a_land | m1 | **integration** | `say` to a live lane answers in under 3000 ms, exit 0, while `state=running` | wire A — **survivor** | the loudest cut-detection: under the old build this could not be answered at all until the land finished |
| a_second_land_of_the_same_ticket_is_refused_while_one_runs | m1 | integration | a second `land --no-wait` exits 1 saying `already landing` | wire A | the one new race the split creates |
| the_outcome_of_an_async_land_reaches_a_later_reader | m1 | integration | `land-status` eventually says `state=done ok=1 ... landed ticket <n>` | wire A | the outcome is recorded off the pipe |
| an_async_land_still_advances_main | m1 | integration | the branch tip is an ancestor of `main` and `main` moved | wires A + M | duplicate of M |
| the_async_land_announces_its_outcome | m1 | integration | a `pane_events` announcement row names `landed ticket <n>` | wire A | how the outcome reaches the operator when nobody is waiting |
| the_waiting_form_prints_the_start_and_the_outcome | m1 | integration | plain `dodona land` prints both `landing` and `landed` and exits 0 | wire A | the waiting client really blocks; a land returning 0 at START would be a fail-open in every landing script |
| a_pr_repo_still_gets_its_worktree | m1 | integration | a `delivery: pr` ticket still gets `.dodona\wt\t<n>` | wire W1 | **vacuous against HEAD by construction** (the file says so); kept as a future guard |
| a_pr_repo_still_assembles_the_completion_record | m1 | unit | the pr ticket's record carries `src/prmode/x.cs` | `Daemon.CompletionRecord` (delivery-agnostic) | rides R1 |
| a_pr_repo_raises_no_approval_question | m1 | unit | no `land` question row for the pr ticket | `Config.IsPr` gating `Ask` / `Store.QuestionUpsert` | a store-table absence |
| the_missing_question_says_why_rather_than_being_silent | m1 | unit | `land_ask_skipped_pr_mode` names the ticket and `delivery: pr` | event detail text | |
| a_pr_repo_grants_no_merge_token | m1 | unit | `token-request` exits 1 with `delivery: pr`, never `granted` | `Config.IsPr` in the token path | |
| the_token_refusal_rewrites_rather_than_walls | m1 | unit | the refusal says `open a PR` and `ticket-record <n>` | refusal wording | |
| approving_a_pr_ticket_still_grants_no_token | m1 | unit | approval does not unlock the token | refusal **ordering** ahead of the approval gate | |
| no_token_records_the_pr_ticket_as_its_holder | m1 | unit | `token-status` has no `holder=<n>` | `Store` token table state | |
| a_pr_repo_performs_no_local_merge | m1 | unit | `land` exits 1 with `delivery: pr` | `Daemon.LandGate` pr branch | |
| the_refused_land_leaves_main_unchanged | m1 | integration | `main`'s sha before == after | wire M | duplicate |
| the_refused_land_keeps_the_branch_and_the_worktree | m1 | integration | `ticket/<n>` still listed and the worktree still on disk | wire M | the inverse of prune/delete; worth folding into M's survivor |
| the_land_never_got_past_the_cheap_gate | m1 | unit | zero `land_started` events for the pr ticket | structural: `LandGate` returns before `LandBegin` writes it | absence-of-event assertion; the strongest pr-mode claim in the file and it is pure |
| the_pr_refusal_is_recorded | m1 | unit | `land_refused_pr_mode` names the ticket | event detail | |
| the_briefing_reaches_a_ticket_agent | m1 | **integration** | the fake agent reports back a block naming `worktree for ticket <n>`, `ticket/<n>` and `git stash` | wire B — **survivor** | the agent is the only witness; `(none)` distinguishes "no briefing" from "no turn". The three content matches are already covered by `tests/Dodona.Tests/PureLogicTests.cs:1101-1156` |
| a_pr_lane_is_told_dodona_will_not_merge | m1 | unit | the block says `will not merge` and `ticket-record <n>` | `Briefing.Ticket(id, branch, pr: true)` | already asserted purely at `tests/Dodona.Tests/PureLogicTests.cs:1120` |
| a_lane_with_no_ticket_is_told_its_checkout_is_shared | m1 | unit | the block says `SHARED checkout`, `no ticket`, and not `worktree for ticket` | `Briefing.Plain(workDir)` | already asserted purely at `PureLogicTests.cs:1101`; only the lane-kind *selection* is wired |
| a_promoted_lane_is_re_briefed_as_a_ticket_lane | m1 | **integration** | after layer-2 promotion the same lane's block says `worktree for ticket` and not `You have no ticket` | wire G3 — **survivor** | the briefing is rebuilt at the **respawn** site, not carried from the spawn — the exact "a call site nobody calls" shape |
| the_briefing_is_never_in_the_operators_feed | m1 | integration | zero `pane_events.user_input` rows and zero `say` events containing `Dodona system` | wire B | **vacuous against HEAD by construction**; the file records it reading `FAIL ... 9` against the naive one-string-two-destinations implementation |
| the_same_ticket_lands_once_the_repository_is_local_merge_again | m1 | integration | flipping `delivery` off lets the same ticket land | wire M | proves it is the FIELD and not the ticket; duplicate of M |
| event_ticket_created | m1 | unit | the kind appears in `events` | `Store.Event` call site in `TicketCreate` | audit tripwire; the path is already covered by `ticket1_created` |
| event_claim_overlap | m1 | unit | the kind appears | `Store.Event` at the overlap report | covered by `the_overlap_is_reported_and_names_the_holder` |
| event_token_refused_unapproved | m1 | unit | the kind appears | `Store.Event` in the token path | covered by `unapproved_token_refused` |
| event_token_granted | m1 | unit | the kind appears | `Store.Event` in `TokenRequest` | covered by `approved_token_granted` |
| event_token_queued | m1 | unit | the kind appears | `Store.Event` in `TokenRequest` | covered by `second_ticket_queued` |
| event_landed | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `ticket1_landed` |
| event_verify_green | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `verify_ran_green` |
| event_token_expired_reclaimed | m1 | unit | the kind appears | `Store.Event` in `TokenRequest` reclaim | covered by `regrant_after_expiry_lands` |
| event_worktree_pruned | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `worktree1_pruned` |
| event_land_merged_main | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `the_land_says_it_merged_main_in` |
| event_land_conflict | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `a_conflicting_merge_refuses_the_land` |
| event_verify_red | m1 | unit | the kind appears | `Store.Event` in `LandFlow` | covered by `red_verify_refuses_the_land`; its existence is what proves verify ran before the ref moved |
| event_land_started | m1 | unit | the kind appears | `Store.Event` in `LandBegin` | covered by the A-wire checks |
| event_land_finished | m1 | unit | the kind appears | `Store.Event` in `LandBegin`'s task | covered by `the_outcome_of_an_async_land_reaches_a_later_reader` |
| event_completion_record | m1 | unit | the kind appears | `Store.Event` in `CompletionRecord` | covered by `a_finished_turn_produces_a_completion_record` |
| event_completion_record_unchanged | m1 | unit | the kind appears | `Store.Event` in the D-R13 skip | covered by `a_chatty_turn_produces_no_second_record` |
| no_process_left_in_the_build_output | m1 | integration | no live OS process has a path under `<repo>\src\...\bin\` | wire H | registered by `tests/_workspace.ps1:371`, not by this file. Infrastructure invariant every suite carries; not a product wire |

## Totals

| bucket | count |
|---|---|
| unit (movable) | 91 |
| integration (keep) | 42 |
| unclear | 2 |
| **total** | **135** |

After deduplication the 42 integration checks prove **13 distinct wires** (12 product wires plus
1 suite-hygiene invariant).

Checks currently riding each wire — counted as "any check whose assertion travels this path",
whether I marked it unit or integration, because that is the real re-proof factor:

| wire | checks riding it | survivor |
|---|---|---|
| M | 26 | `main_is_now_the_merge_that_was_verified` |
| G1 | 21 | `gate_denies_a_ticket_lane_writing_its_claim_in_the_shared_checkout` |
| R1 | 10 | `a_finished_turn_produces_a_completion_record` |
| D | 10 | `a_silent_drop_is_refused` |
| A | 9 | `say_answers_during_a_land` |
| V | 5 | `red_verify_leaves_main_unchanged` |
| B | 4 | `the_briefing_reaches_a_ticket_agent` |
| W1 | 2 | `worktree1_exists` |
| L | 2 | `ticket_agent_started` |
| G3 | 2 | `a_promoted_lane_is_re_briefed_as_a_ticket_lane` |
| R3 | 2 | `an_adopted_lane_still_produces_a_record_after_a_daemon_restart` |
| R2 | 1 | `the_manager_review_starts_no_model_agent_when_autostart_is_off` |
| H | 1 | `no_process_left_in_the_build_output` |

(Wires overlap on three checks — `the_refusal_names_the_shared_checkout_and_where_to_write_instead`
rides G1 and G3, `an_async_land_still_advances_main` rides A and M, `it_lands_once_mains_change_is_restored`
rides D and M — so the column sums to more than 135 and is meant to.)

## What blocks a move down a layer

- **`Daemon.DeployGate` writes a file directly.** Five gate-registration checks are asking what
  string it built; there is no builder to call. Splitting it into
  `GateRegistrationJson(laneId, ticketId, worktree, exePath) : string` plus a writer moves all
  five at zero cost.
- **`Daemon.CompletionRecord` is a private instance method** that reads `_store` and shells out to
  git. Nine record checks are asking what it assembled. It needs a record-shaped return value
  built from (ticket row, git output, turn body), which is already how it reads.
- **`Program.GateHook` reads `Console.In` and its own argv and calls `GateAsk` (a daemon round
  trip) inline.** Fourteen checks are asking for its verdict or its wording given inputs. A
  `GateDecide(laneArg, ticketArg, stdin, Func<...> treeCheck)` seam moves every one of them, and
  the `Func` is what keeps the fake honest — the real call site must pass the real `GateAsk`, so a
  fake that drifts cannot be reached from production.
- **`Daemon.SilentDrops` and the land's git work need a scripted git repository.** Cheap and
  deterministic, but not a `dotnet test` in the current sense — it needs a fixture helper that
  builds a repo in a temp directory. Seven drop checks and roughly ten land-message checks depend
  on it.
- **Lease expiry reads `DateTime.UtcNow`** and the suite pays a real `Start-Sleep -Seconds 2` for
  it. There is no clock seam.
- **`land_started`, `manager_review_skipped` and `land_ask_skipped_pr_mode` absences** are asserted
  by SQL against the live store. They are pure decisions but only observable as store rows today.
- **The briefing's lane-kind selection** (`Briefing.Plain` vs `Briefing.Ticket`) happens at the
  delivery site inside `LaneRuntime`, so "the right block for this lane kind" cannot be asked
  without a lane. The *blocks themselves* are already unit-tested.
- **Order dependence inside the file.** `an_ordinary_resolution_is_not_flagged_as_a_drop` asserts
  on `$landResolved`, captured ~200 lines earlier in section 10c; `$plainId` changes meaning
  mid-file when layer 2 promotes it (the file records this trap explicitly, and a check was
  written wrong because of it). Any split has to break these couplings rather than carry them.

## Two things a refactor must not lose

1. **`the_briefing_is_never_in_the_operators_feed` and `a_pr_repo_still_gets_its_worktree` are
   vacuous against HEAD by construction and the file says so, twice, with the reasoning.** They
   are deliberate future guards. `dev prove` will call them VACUOUS; that is expected and is not
   a licence to delete them.
2. **`tracked_settings_untouched` is vacuous by accident** — the file it guards never exists in
   this fixture, so the `-not (Test-Path ...)` disjunct always satisfies it. It is the one check
   in this file that currently asserts nothing about the product.

## One correctness note found while reading

`the_briefing_reaches_a_ticket_agent`, `a_pr_lane_is_told_dodona_will_not_merge`,
`a_lane_with_no_ticket_is_told_its_checkout_is_shared` and `a_promoted_lane_is_re_briefed_as_a_ticket_lane`
route a six-line string through daemon -> shim -> fake agent -> store -> SQL -> whitespace-flatten
-> regex, at four `Wait-Until`s of 25000 ms each, to assert what `Briefing.Block` (a pure static
builder, `src/Dodona/Briefing.cs:77`) already returns — and `tests/Dodona.Tests/PureLogicTests.cs`
lines 1101-1156 already assert the same content directly, on all four kinds, with a bullet count
and a character bound. The wiring question underneath them is a single one: *the block is
delivered on the agent wire and never on the pane wire*, plus *it is rebuilt at the respawn site*.
That is two checks, not four, and the other content is already covered elsewhere by name.
