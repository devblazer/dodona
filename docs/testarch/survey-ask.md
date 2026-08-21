# survey-ask — check-by-check classification

Files surveyed, read end to end:

- `C:\Users\devbl\Documents\personel\Dodona\tests\ui-ask-acceptance.ps1` (589 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\voice-acceptance.ps1` (583 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\brain-acceptance.ps1` (1169 lines)

Counted, not estimated. `grep -c "Check '"` gives 44 / 24 / 77. Each suite additionally
writes exactly one result row from `tests/_workspace.ps1:353 Assert-NoBuildOutputProcesses`
(`$results['no_process_left_in_the_build_output']`), which the tally line counts, so the
printed totals are **45 / 25 / 78 = 148**.

| | ui-ask | voice | brain | group |
|---|---|---|---|---|
| checks | 45 | 25 | 78 | **148** |
| movable to unit | 18 | 12 | 43 | **73** |
| keep integration | 26 | 13 | 34 | **73** |
| unclear | 1 | 0 | 1 | **2** |
| distinct wires touched | 8 | 8 | 12 | **26 rows, 21 distinct product wires + 1 harness invariant** |

---

## ui-ask-acceptance.ps1 (45)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| ticket_create_without_a_repo_still_refuses | ui-ask | unit | `ticket-create` in a folder with no `.git` prints an error | `Repos.ForClaims` (src/Dodona/Repos.cs:198) | `ReposForClaimsTests` already exists in `tests/Dodona.Tests/PureLogicTests.cs:440` — this is the same question through a daemon |
| the_refusal_opens_a_question_row | ui-ask | integration | a real refusal writes an `open` row into `questions` | **W1** row→overlay chain, producer end | the only check here that proves a refusal *becomes* a row rather than a printed sentence |
| the_question_is_about_this_project | ui-ask | unit | `questions.kind == 'repo-init'` and `subject` is the project path | `Daemon.AskForRepo` (Daemon.cs:3120) + `Ask.KindRepoInit` | which constant is stamped on the row is content |
| the_question_is_listed_by_the_cli_too | ui-ask | unit | `dodona questions` output matches `no git repo` | `Daemon.AskForRepo`'s text (Daemon.cs:3128) | assertion is on the wording, not on the CLI reaching the store |
| the_window_renders_the_open_question | ui-ask | integration | `ui dump`'s `ask.id` equals the row id at a live window | **W1** | *survivor candidate for W1* |
| the_ask_is_on_screen | ui-ask | integration | `ask.shown == true` | **W1** | second assertion on the same dump as the row above |
| the_ask_carries_the_question_text | ui-ask | unit | the rendered question text matches `no git repo` | `Daemon.AskForRepo` text | the window relays a column verbatim |
| the_ask_carries_its_choices | ui-ask | unit | choice values are `yes` and `no` | `Ask.Choices(Ask.RepoInitCandidates(...))` | `AskTests` (PureLogicTests.cs:1454) already parses this exact blob |
| the_ask_offers_no_filesystem_navigation | ui-ask | unit | no choice value contains `\`, `/` or a drive letter | pure predicate over `Ask.Choices(Ask.RepoInitCandidates(leaf))` | **one of the three copies of this predicate**; `Ask.cs`'s own doc says it exists "so `unit` can hold them to it" |
| the_ask_is_not_a_modal | ui-ask | integration | the ui pipe still answers while a question is up, and `input`/`window.h` are still there | **W5** no-modal | its stated mechanism is FALSE — see the note under W5; voice measured that a MessageBox pumps its own loop and the dump still answers |
| the_ask_renders_to_a_screenshot | ui-ask | unclear | `Test-Path out\ask-live.png` | — | the assertion is only that a file exists; whether `ui screenshot` needs its own wire check or is covered by the ui-grid/m3 capture path is not answerable from these three files |
| the_ask_window_is_findable | ui-ask | integration | a top-level UIA window titled `Dodona*<leaf>` exists | **W3** UIA-button wire (precondition) | fixture precondition for the two below |
| a_choice_is_a_real_button_a_person_can_click | ui-ask | integration | a UIA element named `ask:yes` exists in the live tree | **W3** | *survivor for W3* — the pixels↔value link, unreachable without a real WPF visual tree |
| the_button_value_is_the_verb_value | ui-ask | integration | the same button exists AND `yes` is a choice value | **W3** | re-proves W3; its second half is `Ask.Choices` content |
| escape_puts_the_ask_down | ui-ask | integration | `ui key escape` → `Window_KeyDown` → `ask.shown=false, dismissed=true` | **W4** escape-is-view-state | *survivor for W4* |
| putting_it_down_does_not_answer_it | ui-ask | integration | the row is still `open` after Esc | **W4** | the negative half of the same keypress; should be one assertion with the row above |
| the_ui_answer_verb_reaches_the_ask | ui-ask | integration | `ui answer yes` returns `answered` | **W2** answer path | |
| answering_records_the_answer_on_the_row | ui-ask | integration | `questions.state == 'answered'` | **W2** | |
| answering_yes_actually_creates_the_repo | ui-ask | integration | `.git` exists on disk in `$root3` | **W2** (the repo-init handler's real effect) | the loudest repo-init instance; if W2's survivor is the token one, `RepoInitOp` needs a named elsewhere |
| the_repo_has_the_first_commit | ui-ask | integration | `git rev-parse HEAD` is a sha | **W2** | second assertion on the same `git init` |
| answering_is_announced_in_the_feed | ui-ask | unit | a `pane_events` row body matches `git repository ready` | inline interpolation in `Daemon` (Daemon.cs:2976) | content — but see BLOCKER: the string has no seam, it is `Announce($"[dodona] git repository ready …")` inline |
| the_overlay_closes_when_the_row_closes | ui-ask | integration | `ask` is null once the row is answered | **W1** (the poll, other direction) | |
| answering_the_same_question_twice_is_refused | ui-ask | unit | a second `answer` on the same id errors | `Daemon.AnswerQuestion`'s state guard | content; BLOCKER: guard reads the store row, needs a store seam |
| an_answer_the_question_never_offered_is_refused | ui-ask | unit | `answer <id> maybe` errors | `Ask.Match(choices, "maybe") == null` | `PureLogicTests.cs:1523` already asserts exactly this for unoffered picks |
| the_ticket_the_question_was_blocking_now_works | ui-ask | unit | `ticket-create` succeeds once the repo exists | `Repos.ForClaims` (positive direction) | the mirror of the first row in this table |
| a_finished_ticket_asks_the_operator_to_approve_the_merge | ui-ask | integration | a turn that moved the worktree opens a `land` row whose subject is the ticket | **W6** ticket-turn→land-ask | |
| the_approval_ask_does_not_wait_for_a_review | ui-ask | integration | the row exists AND `manager_review_skipped` fired AND zero `manager_review` | **W6** | *survivor for W6* — fails loudly both if no ask appears and if the ask is gated on a model |
| the_approval_ask_says_what_code_knows_when_no_review_ran | ui-ask | unit | the ask text says `ready to merge` / `1 file` / `verify not-run` / `no review ran` / `Approve the merge` | `Daemon.LandAskText` (Daemon.cs:3311) | clean pure-ish string builder over a record JSON + last-event kind; the strongest single unit candidate in this file |
| the_approval_ask_renders_at_a_live_window | ui-ask | integration | the land row's id and text render in `ui dump.ask` | **W1** | third instance of the same row→overlay wire |
| the_approval_ask_offers_no_filesystem_navigation | ui-ask | unit | no land choice value contains a path separator | pure predicate over `Ask.Choices(Ask.LandCandidates(tid))` | **second copy of the same predicate** |
| answering_the_approval_ask_grants_the_merge_token | ui-ask | integration | `token-request` says `not approved` before and `granted` after `ui answer yes` | **W2** | *survivor for W2* — before/after in one assertion, and the effect is the irreversible one |
| the_answer_is_recorded_as_the_operators_own_approval | ui-ask | integration | a `ticket_approved` event exists and the row is `answered` | **W2** | both surfaces going through one `Daemon.ApproveTicket` |
| an_approved_ticket_is_never_asked_about_again | ui-ask | unit | no second `land` row opens for an approved ticket | `Daemon.AskToLand` early return on `approved` | content; the `$wasUp` half rides W1 |
| an_ambiguous_sentence_is_held_rather_than_placed | ui-ask | unit | `dodona input` prints `held: not sure which project` | `ProjectLadder.Decide` → `ProjectLadder.Ask` rung | `ProjectLadderTests` (PureLogicTests.cs:1200) already covers the Ask rung |
| the_routing_question_reaches_the_operators_window | ui-ask | integration | the router's Ask rung produced a `questions` row that the window renders | **W8** router-rung→row | *survivor for W8* — this is the check that closed "the rung asked nobody" |
| the_routing_question_asks_which_project | ui-ask | unit | the text matches `Which project` | `Daemon.AskWhichProject` (Daemon.cs:3164) | |
| the_routing_question_offers_the_projects | ui-ask | unit | choice values are the two project leaves | `Ask.RouteCandidates(names)` + `ProjectLadder.Leaf` | |
| the_routing_question_offers_no_filesystem_navigation | ui-ask | unit | no route choice value contains a path separator | pure predicate over `Ask.Choices(Ask.RouteCandidates(names))` | **third copy of the same predicate**, and the one `Ask.cs`'s comment names explicitly |
| a_rendered_routing_question_still_invents_no_lane | ui-ask | unit | zero `role='work'` lanes while the question is open | the Ask rung spawns nothing (`Daemon.RouteInput`) | content; BLOCKER: the spawn call site is inline in `RouteInput`, and `brain:held_input_invents_no_lane` is the identical claim |
| a_project_choice_is_a_real_button_a_person_can_click | ui-ask | integration | a UIA element named `ask:<projectB leaf>` exists | **W3** | second instance of W3 |
| the_ui_answer_verb_reaches_a_routing_question | ui-ask | integration | `ui answer <leaf>` returns `answered` | **W2** | third instance of W2 |
| answering_in_the_window_delivers_the_held_sentence_to_the_chosen_project | ui-ask | integration | exactly one work lane appears and its `cwd` is project **B** | **W7** routed-sentence→lane | *survivor for W7* — asserts both that a lane was spawned and that it opened in the right project |
| the_words_the_operator_typed_are_what_the_agent_receives | ui-ask | integration | exactly one `user_input` pane row carries the held sentence | **W7** | the delivery half; merge into the row above rather than delete |
| the_routing_overlay_closes_when_its_row_closes | ui-ask | integration | `ask` is null after answering | **W8** | |
| no_process_left_in_the_build_output | ui-ask | integration | no OS process is running out of `src\**\bin\` | **W26** harness invariant | written by `Assert-NoBuildOutputProcesses`, not by the suite body |

## voice-acceptance.ps1 (25)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| dictation_starts_off | voice | unit | `listen.state == 'off'` and `says` is empty at a fresh window | `Dictation.ListenState.Off` + `Dictation.Describe(Off, null)` | `DictationTests` already owns the state machine |
| the_listen_verb_reaches_the_window_at_all | voice | integration | `ui listen on` is a known verb and the window reports `listening` | **W9** mic affordance→`SetListening` | the verb half of the same method the button click lands in |
| the_suite_never_opens_a_real_microphone | voice | integration | `listen.engine == 'fake'` under `DODONA_UI_MIC=off` | **W13** real-process egress | safety invariant; strictly weaker than `mic_off_opens_no_socket` |
| heard_text_lands_in_box_unsent | voice | unit | the phrase is in `input.text` and no feed row echoes it | `Dictation.Decide` (never `Submit`) + `Dictation.Splice` | `DictationTests.Dictation_never_submits` exists |
| dictation_capitalises_the_start_of_the_box | voice | unit | `input.text -cmatch '^Hello from dictation'` | `Dictation.Splice` | `DictationTests.Splice_spaces_and_capitalises` already covers it |
| spoken_send_words_do_not_submit | voice | integration | "enter/send/submit/go" all land as text and nothing is sent, at a live window | **W10** `ui heard`→`MainWindow.OnHeard` | *survivor for W10* — CLAUDE.md §3 names this check by name as the live-window claim; constraint 3 protects it |
| spoken_new_line_inserts_one_at_the_window | voice | unit | `input.lines` goes up by exactly one | `Dictation.Decide` → NewLine act | `DictationTests.Spoken_new_line_inserts_one` exists |
| the_unsettled_tail_shows_in_the_box_at_the_caret | voice | unit | the partial is in `input.text`, `pending > 0`, committed text unchanged | the pending-tail model in `MainWindow.OnHeard`/`ClearPending` (MainWindow.xaml.cs:720, 791) | BLOCKER: the pending range is window state with no pure seam — extract a `PendingTail` type and this and the three below move together |
| a_rewritten_partial_replaces_the_tail_instead_of_appending | voice | unit | exactly one copy of the tail after a rewriting interim | same pending-tail model | BLOCKER: same |
| an_unsettled_tail_does_not_outlive_the_microphone | voice | unit | turning the mic off clears the pending range and the words with it | `DisarmMic` → `ClearPending` | BLOCKER: same |
| stale_epoch_result_dropped_at_the_window | voice | unit | an older-epoch result changes no committed text and bumps `dropped` | `Dictation.ShouldDrop(resultEpoch, submitEpoch)` | `DictationTests.Stale_epoch_result_dropped` exists; only the `dropped` counter is window state |
| enter_still_sends_after_dictation | voice | integration | Enter clears the box after dictation has written into it | **W11** `PreviewKeyDown`→`InputKey`→`SubmitInput` | keep: CLAUDE.md §0.2's WPF trap (the TextBox class handler eats Enter) is only observable at a real window. `ui-grid` proves the same wire — a cross-suite duplicate worth resolving |
| the_mic_is_a_real_button | voice | integration | a UIA element named `microphone` exists | **W9** (precondition) | |
| clicking_the_mic_toggles_listening | voice | integration | invoking that UIA element flips `listen.state` | **W9** | *survivor for W9* — the click path into `Mic_Click`→`SetListening` |
| listening_toggle_persists | voice | integration | a NEWLY STARTED window reads `ui.json` back and arms | **W12** toggle persistence across a process restart | keep: a real file plus a real second process; the `UiSettings.Save/Load` round trip alone is unit-able |
| no_modal_when_the_mic_fails | voice | integration | the UI process owns exactly ONE top-level window | **W5** no-modal | *survivor for W5* — the only mechanism in the repo that actually detects a modal; the suite's own comment records the measurement that killed the "the pipe still answers" version |
| a_failed_mic_reads_as_error_not_listening | voice | unit | `state=='error'`, `says` does not say listening, `error` is non-empty | `Dictation.Describe(Error, reason)` + `Recognizers.Create`'s `why` | `DictationTests.Error_state_is_not_listening` exists |
| a_failed_mic_says_why_in_words | voice | unit | `says` matches `microphone` | `Recognizers.Describe(ex)` (Recognizer.cs:210) / `Dictation.Describe` | pure string function |
| mic_off_opens_no_socket | voice | integration | engine is `fake` AND the UI process holds zero remote-443 connections | **W13** | the suite's OWN comment says the socket half is "VACUOUS BY CONSTRUCTION here"; the ordering claim (`MicDisabled` short-circuits before construction) is `Recognizers.Create` and is unit-able |
| an_interim_stream_leaves_exactly_one_copy_in_the_box | voice | unit | three rewriting interims plus a final leave one copy, `pending` back to 0 | `Dictation.Splice` + the pending-tail model | BLOCKER: same pending-tail seam as the three rows above |
| a_dead_network_reads_as_error_not_listening | voice | integration | the REAL `DeepgramRecognizer` connects to a closed loopback port, fails, classifies, lands in error | **W14** real socket failure path | keep: irreducibly a real socket; runs the shipped engine with no egress and no credential |
| a_suite_cannot_authenticate_even_with_the_real_engine | voice | integration | the real engine armed with no overrides lands in `error: no speech credential` and opens no socket | **W13** | *survivor for W13* — its recorded red was `state=[listening] engine=[deepgram]`, i.e. a test run authenticated on the operator's bill |
| starting_has_a_deadline | voice | integration | `Starting` is reached, then a timer moves it to `Error` | **W15** the starting deadline timer | keep: about timing. `Dictation.CanTransition(Starting, Error)` is the unit-able half |
| the_listening_pose_exists | voice | unit | `ui pose listening` is a known pose and sets state+partial | `Poses` (DodonaUi/Poses.cs) fixture content | a pose is a deterministic fixture — content, not wiring |
| no_process_left_in_the_build_output | voice | integration | no OS process running out of `src\**\bin\` | **W26** harness invariant | |

## brain-acceptance.ps1 (78)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| brain_is_warm_and_off_grid | brain | integration | exactly one alive `brain` lane for this project | **W17** utility-lane lifecycle | precondition for the whole file |
| brain_reviewed_and_agreed | brain | integration | a real brain agent turn wrote `brain_review` with `agree=True` | **W20** brain-reviews-behind | |
| agreement_is_silent | brain | unit | zero `announcement` rows mentioning `dispatcher` | the agree branch of `Daemon.BrainReview` (Daemon.cs:5414) | content; BLOCKER: no seam, the branch is inline |
| rename_applied | brain | integration | the brain's verdict changed `lanes.title` | **W20** | *survivor for W20* — requires both the turn and the application of its verdict |
| rename_in_causal_chain | brain | unit | exactly one `brain_renamed` event | which event kind `BrainReview` writes | content |
| rename_receipt_carries_undo | brain | unit | the receipt body carries `undo: dodona lane-rename` and `acked=1` | the receipt string in `BrainReview` | content; BLOCKER: inline interpolation |
| ticket_is_suggested_not_created | brain | unit | the suggestion names `ticket-create --title FOAM`, `acked=0`, zero tickets | `BrainReview`'s suggest-never-create branch | content |
| operator_rename_wins | brain | unit | `lane-rename` sets the title | `Store.LaneRename` | content; BLOCKER: store round-trip needs a store seam |
| brain_runs_outside_the_project | brain | unit | a `shim_spawned` detail contains `neutral` | the cwd choice for management roles at the spawn site | content; BLOCKER: computed inline at spawn |
| pulse_on_arrival | brain | integration | the receiving pane's `pulsing` is true while it is on | **W21** pane pulse (a transient) | *survivor for W21* — a transient caught in flight at a live window |
| pulse_fades | brain | integration | `pulsing` returns to false | **W21** | timing, same wire |
| generic_goes_to_the_focused_lane | brain | unit | a generic verdict delivers to the focused lane | `Daemon.RouteInput` verdict table | `RoutingInCodeTests` (PureLogicTests.cs:601) already owns the code-only verdicts |
| obvious_generic_needs_no_model | brain | unit | `routing_decisions` reads `generic\|explicit` | the code-only generic rung | already partly in `RoutingInCodeTests` |
| addendum_goes_to_the_named_lane | brain | unit | the addendum reaches the lane it names | `RouteInput` verdict table | content |
| addendum_records_its_reason | brain | unit | `routed_addendum` detail matches `direct` | event detail content | |
| tweak_is_an_addendum_too | brain | unit | `routed_addendum` detail matches `tweak` | event detail content | pure variation on the row above |
| new_task_spawns_its_own_lane | brain | integration | the work-lane count goes up by exactly one | **W7** routed-sentence→lane | same wire as ui-ask's route answer |
| new_task_lane_gets_the_message | brain | integration | the new lane receives the sentence as `user_input` | **W7** | |
| new_task_is_named_from_the_text | brain | unit | the lane title is `CONFIGURATION` | `Daemon.NameFromText` (Daemon.cs:3863, `static string`) | already a pure static — the cheapest single move in this file |
| new_task_receipt_carries_its_undo | brain | unit | the announcement matches `lane-stop <n>` | announcement string content | BLOCKER: inline |
| new_task_in_the_causal_chain | brain | unit | exactly one `routed_new_task` event | event kind content | |
| new_task_never_touched_the_focused_lane | brain | unit | zero pane rows on the focused lane carry the sentence | `RouteInput`'s new-task branch delivers only to the new lane | content (a negative about a delivery decision) |
| unclear_escalates_to_the_expensive_tier | brain | unit | a `classified_escalated` event exists | the escalation rung in `RouteInput` | content; BLOCKER: currently only observable via a second real agent |
| escalated_new_task_spawns_a_lane | brain | integration | the work-lane count goes up by one after escalation | **W7** | third instance of the spawn wire |
| escalated_decision_is_recorded | brain | unit | `routing_decisions.confidence == 'escalated'` | recorded-decision content | |
| double_uncertainty_asks_the_operator | brain | unit | the ask body says `NOT delivered` and `acked=0` | the clarification announcement string | content; BLOCKER: inline |
| clarification_in_causal_chain | brain | unit | exactly one `routing_clarification` event | event kind content | |
| held_input_is_delivered_nowhere | brain | unit | the work-lane `user_input` count is unchanged | the Ask rung delivers nothing | content; the same claim as ui-ask's `a_rendered_routing_question_still_invents_no_lane` |
| held_input_invents_no_lane | brain | unit | the work-lane count is unchanged | same | duplicate of the ui-ask sibling |
| held_input_is_recorded_as_asked | brain | unit | `routing_decisions.tier == 'ask'` | recorded-decision content | |
| restart_adopts_the_brain_it_already_had | brain | integration | the brain lane id survives a real daemon restart | **W17** | |
| restart_does_not_leak_a_second_brain | brain | integration | still exactly one alive brain for the project | **W17** | |
| brain_start_after_restart_reuses_it | brain | integration | `brain-start` after a restart makes no second one | **W17** | |
| reconcile_records_which_brain_it_adopted | brain | unit | `reconcile_done` detail matches `brains=[<leaf>=<id>]` | the `reconcile_done` detail format | content — and the format is exactly what P5.6 renamed so a stale regex would break loudly |
| autostart_creates_a_classifier | brain | integration | with autostart ON, a `router` lane appears unbidden | **W16** operator-path classifier | |
| autostart_records_the_classifier | brain | unit | `router_started` detail is `classifier warm` | event detail content | |
| typed_input_reaches_the_classifier_autostart_made | brain | integration | a typed sentence produces a `classified` event on the lane autostart created | **W16** | *survivor for W16* — the create-side role equals the use-side role; this is the check whose absence cost two dead days |
| routing_did_not_fall_back_to_focus | brain | integration | confidence is not `no-classifier` | **W16** | the silent-degrade signature |
| restart_adopts_the_classifier_it_already_had | brain | integration | the router lane id survives a restart | **W17** | same wire, different role |
| restart_does_not_leak_a_second_classifier | brain | integration | still exactly one alive router | **W17** | |
| a_completed_ticket_turn_reaches_the_manager | brain | integration | a turn that moved the worktree produced a record a real manager reviewed | **W22** ticket-turn→manager→lane | |
| a_send_back_reaches_the_lane_as_input | brain | integration | the send-back arrives on the working lane as `user_input` with the round prefix | **W22** | *survivor for W22* — a cut anywhere in the chain kills it |
| the_managers_write_up_reaches_the_operators_approval_ask | brain | unit | the ask text carries the manager's note and `sent this back, round 1 of 3` | `Daemon.LandAskText` / its `ReviewLine` local (Daemon.cs:3384) | pure over the `manager_review` detail JSON; the refresh-in-place half rides W22 |
| a_manager_approval_grants_nothing | brain | integration | asked for `approve`: ticket unapproved, no `ticket_approved`, `token-request` refuses | **W23** no-path-from-review-to-approval | *survivor for W23* — an absence proof that only running the real manager can make |
| a_manager_verdict_never_answers_the_approval_ask | brain | integration | the `land` question is still `open` and zero are `answered` | **W23** | the R6 surface of the same rule |
| the_review_escalates_to_the_expensive_tier_when_the_cheap_one_is_unsure | brain | unit | the review detail says `"tier":"hi"` | the tier ladder in `Daemon.ManagerReview` (Daemon.cs:4602) | content; BLOCKER: needs a second agent to observe today |
| three_send_backs_is_the_bound_and_it_survives_a_daemon_restart | brain | integration | the bound holds across a REAL daemon restart mid-loop | **W24** bound-counted-in-the-store | keep: an in-memory counter would look green in a single-daemon test and be wrong on the operator's machine |
| the_bound_hands_the_operator_the_history_instead_of_a_fourth_round | brain | unit | the announcement carries `sent back 3 times` and all three notes | the bound announcement string (Daemon.cs:4637) | content; BLOCKER: inline |
| the_ask_says_the_bound_was_reached_rather_than_looking_like_an_ordinary_approval | brain | unit | the ask text says `sent this back 3 times`, `the bound`, `yours to judge`, and there is one row | `Daemon.LandAskText` (Daemon.cs:3364-3367) | content — the bound branch of the same string builder |
| a_manager_claiming_its_objection_was_mechanical_earns_no_exemption | brain | unit | with `verify: not-run`, `exempt=false` and the round counts | the exemption predicate at Daemon.cs:4777 (`sendBack && verifyState=="red" && !repeat`) over `Daemon.RecordVerify` | BLOCKER: the predicate is an inline local inside `ManagerReview`; extracting it makes this, the next two and D-R26 all unit checks |
| a_send_back_on_a_red_verify_spends_none_of_the_three_rounds | brain | unit | with a real red verify on record, `exempt=true` and `manager_sent_back` does not rise | same predicate + which event kind is written | the *red verify itself* is fixture written by a real `LandFlow`; the exemption decision is content |
| the_second_objection_on_the_same_red_verify_is_not_exempt | brain | unit | a repeat on the same verify timestamp is not exempt | `Daemon.RecordVerify` + `Daemon.VerifyWhenOf` (both `static`, Daemon.cs:4855/4869) | already pure statics — a direct move |
| a_review_that_asks_for_a_named_file_is_given_that_file_and_reads_it | brain | integration | a token that exists ONLY inside the file's bytes comes back in the review | **W25** GrantDetails reads a real file | keep: a real file read plus a real second reviewer round; the token proves content, not the name |
| the_details_round_happens_once_and_a_second_request_is_not_read | brain | unit | `manager_details_granted` count is 1 and `d2.cs` never appears | the once-bound in `ManagerReview` | content (a counting decision); BLOCKER: observed across two real rounds today |
| a_details_request_that_is_not_a_named_changed_file_is_refused_and_recorded | brain | unit | `*` is refused with `not one of the files this change touched` | `Daemon.GrantDetails` (Daemon.cs:4901) — a predicate over (worktree root, record's `changed` set, `want`) | strong unit candidate: the refusal logic is pure over three inputs |
| the_predecessor_brain_is_provably_alive | brain | integration | a raw `NamedPipeClientStream` connects and reads `!hello` | **W18** live-predecessor pipe | precondition; without it the next check goes green against no predecessor |
| no_second_brain_beside_a_live_one | brain | integration | `shim_spawned` and brain-row counts are unchanged after a restart whose adopt must fail | **W18** | *survivor for W18* — the 14-leaked-brains incident, reproduced with no timing luck |
| the_refusal_is_recorded | brain | unit | at least one `utility_predecessor_live` event | event kind content | |
| a_live_pipe_is_never_called_shim_gone | brain | integration | zero `utility_lane_reaped` rows for that lane | **W18** | the negative half of the same restart |
| a_one_project_workspace_says_nothing_about_scope | brain | unit | `dodona status` prints no `scope=` | the `status` lane-line formatter | content; the suite itself calls it VACUOUS by construction |
| two_projects_get_two_distinct_brains | brain | integration | two brain lanes with different ids in one workspace | **W17** | |
| a_brains_project_is_recorded_on_its_lane_row | brain | unit | `lanes.project` holds both project paths | the registration value written at spawn | content; BLOCKER: store column |
| status_names_the_project_a_brain_is_scoped_to | brain | unit | `status` prints a distinct `scope=` per brain | the `status` formatter + `Get-StatusScope` | content |
| the_escalation_asks_the_focused_lanes_own_projects_manager | brain | unclear | every `brain-hi` row's `project` is B and never A | `RouteInput`'s escalation passes the focused lane's project to `AskBrainHiAsync` | the decision is a parameter choice (content), but it is only observable today through a spawned lane's column — I cannot tell without reading `AskBrainHiAsync` whether a seam exists |
| restart_adopts_a_brain_for_every_project | brain | integration | both brain ids survive a restart, and there are exactly two | **W17** | *survivor for W17* — N=2 is where the `_brainLo` scalar bug lived, and it still fails if adoption breaks entirely |
| no_healthy_brain_is_retired_as_a_surplus | brain | integration | zero `brain_surplus_retired` rows and both brains alive | **W17** | the harm signature the operator would have seen |
| reconcile_lists_a_brain_per_project | brain | unit | `reconcile_done` lists `brains=[<Aleaf>=id, <Bleaf>=id]` | the `reconcile_done` detail format | content |
| a_detached_projects_brain_is_reaped | brain | integration | the lane row goes `dead` after `workspace-detach` | **W19** detach reaps the departing project's brain | |
| a_detached_projects_brain_shim_really_exits | brain | integration | the recorded shim pid is no longer a live OS process | **W19** | *survivor for W19* — a process actually died; the row is not the point |
| the_reaping_says_which_registration_went_stale | brain | unit | `brain_unregistered` detail names project B | event detail content | |
| a_brain_in_a_project_that_stayed_is_untouched | brain | unit | project A's brain is still `alive` | the reaper's selection predicate | content; the suite calls it VACUOUS by construction and keeps it deliberately |
| the_wedged_brain_is_provably_alive | brain | integration | a raw pipe client holds project A's brain pipe and reads `!hello` | **W18** | precondition |
| a_wedged_brain_in_one_project_does_not_block_another | brain | integration | project C still gets a brain, and a new `shim_spawned` row proves it | **W18** | the complementary outcome to `no_second_brain_beside_a_live_one` — same machinery, opposite conclusion; both must survive as assertions even though one wire carries them |
| the_wedged_brain_was_never_called_gone | brain | integration | zero `utility_lane_reaped` / `brain_unregistered` rows for the wedged lane | **W18** | |
| the_brain_cap_refuses_a_new_project | brain | unit | one brain, no new shim, and the refusal names `maxBrains` | the cap decision from `Config.For(...).MaxBrains` | content (a config-driven refusal) |
| the_brain_cap_never_evicts_an_existing_brain | brain | unit | the first brain is still `alive` | the refuse-never-evict branch | content; the suite calls it VACUOUS by construction |
| the_brain_cap_refusal_names_the_setting_that_lifts_it | brain | unit | the `brain_cap_reached` detail matches `maxBrains` | event detail content | |
| no_process_left_in_the_build_output | brain | integration | no OS process running out of `src\**\bin\` | **W26** harness invariant | |

---

## Wires (deduplicated across the three files)

21 distinct product wires plus one harness invariant carry all 73 integration checks.

| id | wire | survivor | checks on it |
|---|---|---|---|
| W1 | an open `questions` row reaches a live window's overlay, and the overlay follows the row closing | `the_window_renders_the_open_question` | 5 |
| W2 | `ui answer <v>` → `MainWindow.AnswerAsk` → daemon `answer` → the row closes and the handler's real effect runs | `answering_the_approval_ask_grants_the_merge_token` | 7 |
| W3 | overlay choices are real UIA elements named `ask:<value>` | `a_choice_is_a_real_button_a_person_can_click` | 4 |
| W4 | `Esc` is view state only — the overlay goes down, the row stays open | `escape_puts_the_ask_down` | 2 |
| W5 | a test window never produces a modal | `no_modal_when_the_mic_fails` | 2 |
| W6 | a ticket turn that moved the worktree opens a `land` row without waiting for a review | `the_approval_ask_does_not_wait_for_a_review` | 2 |
| W7 | a routing decision spawns a work lane in the chosen project and the operator's exact words arrive in it | `answering_in_the_window_delivers_the_held_sentence_to_the_chosen_project` | 5 |
| W8 | the router's Ask rung opens a real `questions` row (not just a `routing_decisions` row) | `the_routing_question_reaches_the_operators_window` | 2 |
| W9 | the mic affordance (UIA click and `ui listen`) lands in `MainWindow.SetListening` | `clicking_the_mic_toggles_listening` | 3 |
| W10 | `ui heard` lands in `MainWindow.OnHeard` — the method the real engine raises into — and nothing it produces can submit | `spoken_send_words_do_not_submit` | 9 (1 integration + 8 content checks that drive the same window) |
| W11 | Enter through the real `PreviewKeyDown` path submits and clears the box | `enter_still_sends_after_dictation` | 1 |
| W12 | the listen toggle persists to `ui.json` and a newly started window arms from it | `listening_toggle_persists` | 1 |
| W13 | the real UI process's egress is observed to be zero — no socket without a credential, no credential inside a suite | `a_suite_cannot_authenticate_even_with_the_real_engine` | 3 |
| W14 | the real `DeepgramRecognizer` runs its genuine connect-refusal path and lands in error | `a_dead_network_reads_as_error_not_listening` | 1 |
| W15 | the `Starting` deadline timer actually fires | `starting_has_a_deadline` | 1 |
| W16 | autostart creates a classifier and a typed sentence reaches THAT lane (create-side role == use-side role) | `typed_input_reaches_the_classifier_autostart_made` | 3 |
| W17 | a real daemon restart adopts every live utility lane over its pipe, one per project, retiring none | `restart_adopts_a_brain_for_every_project` | 9 |
| W18 | a live predecessor whose pipe cannot be connected: no duplicate spawned, never called gone, and one project's wedge does not block another | `no_second_brain_beside_a_live_one` | 6 |
| W19 | `workspace-detach` reaps the departing project's brain and its OS processes actually exit | `a_detached_projects_brain_shim_really_exits` | 2 |
| W20 | a typed input causes a real brain turn behind the instant path whose verdict lands on the lane row | `rename_applied` | 2 |
| W21 | a routed message pulses the receiving pane and the pulse fades | `pulse_on_arrival` | 2 |
| W22 | a completed ticket turn's record reaches a real manager and the send-back returns to the lane as input | `a_send_back_reaches_the_lane_as_input` | 2 |
| W23 | a manager verdict of `approve` grants nothing and answers nothing | `a_manager_approval_grants_nothing` | 2 |
| W24 | the send-back bound is counted in the store and survives a real daemon restart mid-loop | `three_send_backs_is_the_bound_and_it_survives_a_daemon_restart` | 1 |
| W25 | `GrantDetails` reads the real file off disk — its content, not its name, comes back | `a_review_that_asks_for_a_named_file_is_given_that_file_and_reads_it` | 1 |
| W26 | (harness) a suite leaks no process into `src\**\bin\` | `no_process_left_in_the_build_output` | 3 (one per suite process) |

Total: 73 integration checks on 26 wire rows (25 product + 1 harness).

---

## Findings the synthesis agent should not lose

1. **The no-filesystem-navigation predicate is asserted three times at two live windows and is
   pure.** `the_ask_offers_no_filesystem_navigation`, `the_approval_ask_offers_no_filesystem_navigation`,
   `the_routing_question_offers_no_filesystem_navigation` are all
   `@(Ask.Choices(<candidates>).Value | where { $_ -match '[\\/]' -or $_ -match '^[A-Za-z]:' }).Count -eq 0`.
   `src/Dodona/Ask.cs` already carries the three candidate builders (`RepoInitCandidates`,
   `RouteCandidates`, `LandCandidates`) with a class comment that says they were factored out
   "so `unit` can hold them to it", and `AskTests` in `tests/Dodona.Tests/PureLogicTests.cs:1454`
   already parses two of them. This is a three-for-one move with no new seam needed.

2. **`the_ask_is_not_a_modal`'s stated mechanism was measured FALSE and it has never been
   corrected.** ui-ask lines 228-242 assert "the ui pipe still answers while a question is up"
   on the reasoning that a MessageBox blocks the dispatcher. voice lines 303-327 record the
   measurement that disproves it: a real `MessageBox.Show` was put on the failure path, the
   dispatcher kept pumping (Win32 modals run a nested message loop), the dump answered and the
   check passed — `answered=True topLevelWindows=2`. Only `no_modal_when_the_mic_fails`'s
   top-level-window count actually detects a modal. Any consolidation must keep the counting
   mechanism and drop the answering one, not the other way round.

3. **`mic_off_opens_no_socket`'s socket half is vacuous by construction in its own comment**
   (voice lines 368-376): with `DODONA_STT_NO_CLI_AUTH` set and no token file, `SpeechAuth` has
   nothing to present, so the shipped code returns before a socket exists and the count is
   structurally 0. `a_suite_cannot_authenticate_even_with_the_real_engine` arms the REAL engine
   with no overrides and is the check that would actually catch a suite authenticating.

4. **The pending-tail model is the single biggest unit-test seam missing in voice.** Four checks
   (`the_unsettled_tail_shows_in_the_box_at_the_caret`,
   `a_rewritten_partial_replaces_the_tail_instead_of_appending`,
   `an_unsettled_tail_does_not_outlive_the_microphone`,
   `an_interim_stream_leaves_exactly_one_copy_in_the_box`) are pure string-and-range algebra that
   currently needs a live WPF window because the pending range lives in `MainWindow` fields
   (`ClearPending`, MainWindow.xaml.cs:791). `Dictation.Splice` is already pure; extracting the
   range bookkeeping beside it moves all four in one change.

5. **`Daemon.LandAskText` (Daemon.cs:3311) answers four checks across two suites** —
   `the_approval_ask_says_what_code_knows_when_no_review_ran` and
   `the_approval_ask_offers_no_filesystem_navigation` (ui-ask),
   `the_managers_write_up_reaches_the_operators_approval_ask` and
   `the_ask_says_the_bound_was_reached_rather_than_looking_like_an_ordinary_approval` (brain).
   It is already almost pure: it takes a `TicketRow` and a record JSON, and touches the store only
   for `CountTicketEvents` / `LastTicketEvent`. Passing those two values in makes it a pure
   function and moves four expensive checks in one step.

6. **The R8 exemption logic is one inline boolean guarding three checks.** Daemon.cs:4777,
   `var exempt = sendBack && verifyState == "red" && !repeat;`, over the already-`static`
   `Daemon.RecordVerify` (4855) and `Daemon.VerifyWhenOf` (4869). Extracting it makes
   `a_manager_claiming_its_objection_was_mechanical_earns_no_exemption`,
   `a_send_back_on_a_red_verify_spends_none_of_the_three_rounds` and
   `the_second_objection_on_the_same_red_verify_is_not_exempt` pure — each currently costs a real
   land, a real red verify, and a real manager round.

7. **Six brain checks are already about pure statics.** `Daemon.NameFromText` (3863),
   `Daemon.Requested` (4843), `Daemon.RecordVerify`, `Daemon.VerifyWhenOf`, plus
   `Ask.*` and `ProjectLadder.*`. `new_task_is_named_from_the_text` in particular currently
   requires a routed input, a spawned agent and a store read to assert what a `static string`
   returns.

8. **Duplicates across the group.** `held_input_invents_no_lane` (brain) and
   `a_rendered_routing_question_still_invents_no_lane` (ui-ask) are the same claim from two
   fixtures — the ui-ask copy's own comment says so. `enter_still_sends_after_dictation` (voice)
   duplicates the Enter wire that `tests/ui-grid-acceptance.ps1` also owns (CLAUDE.md §3 names it
   there). Neither is in scope for this survey to resolve, but both are real overlaps.

9. **Three checks are kept deliberately VACUOUS and must not be read as coverage.** The suites say
   so themselves: `a_brain_in_a_project_that_stayed_is_untouched`,
   `the_brain_cap_never_evicts_an_existing_brain`, `a_one_project_workspace_says_nothing_about_scope`
   (plus the two `*_is_provably_alive` preconditions, which are vacuous by design because HEAD
   wedges too). They are guards against a future widening, not proofs of present behaviour — a
   rework that "moves them down a layer" changes nothing about what they can catch, and deleting
   them loses the only line in the tree that would notice.

10. **Both of the two irreducibly-timing checks are in voice** (`starting_has_a_deadline`,
    `pulse_on_arrival`/`pulse_fades` in brain). Nothing about them can move: a timer fires or it
    does not.
