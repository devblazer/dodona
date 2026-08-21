# survey-window — classification of every check in ui-grid / ui-shell / ui-wake

Files surveyed, in full:
- `C:\Users\devbl\Documents\personel\Dodona\tests\ui-grid-acceptance.ps1` (566 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\ui-shell-acceptance.ps1` (344 lines)
- `C:\Users\devbl\Documents\personel\Dodona\tests\ui-wake-acceptance.ps1` (274 lines)

Split from `ui-use` by `3b235ab` (2026-08-21). That commit's own arithmetic: ui-grid 61,
ui-shell 19, ui-wake 8 = **88** checks. Counted here the same way: 60 + 18 + 7 literal
`Check '...'` registrations, plus one `no_process_left_in_the_build_output` per suite written by
`Assert-NoBuildOutputProcesses $repo $results` in each `finally`. **88 total, counted, not
estimated.**

Totals: **38 integration, 48 unit, 2 unclear**.
Deduplicated, those 38 integration checks prove **14 distinct wires**.

## Wire index (used in the `pure function or wire` column)

| id | wire | checks on it |
|---|---|---|
| W1 | typed text in the window's box crosses the UI pipe to the daemon, which creates/routes a lane and delivers the sentence | 7 |
| W2 | an agent's stdout (and its out-of-band wire fields) crosses shim -> daemon -> store and reaches the pane | 3 |
| W3 | a real WPF layout measures the multiline box; its height changes come out of the FEED's pixels, never the window's | 6 |
| W4 | the window publishes its interactive controls to the UI Automation tree under stable automation names | 3 |
| W5 | a store mutation made outside the window (a CLI command) reaches the live window's render | 1 |
| W6 | a real UIA `Invoke()` on a rendered control reaches `MainWindow.LaneAction` and the daemon acts | 2 |
| W7 | nothing lives in the window's memory: a relaunched window process restores state from the store and from `ui.json` | 2 |
| W8 | harness hygiene — this suite left no process running out of `src\...\bin` | 3 |
| W9 | a bare `DodonaUi.exe` launch opens the SHELL and serves the shell control pipe (no dialog, no folder picker) | 1 |
| W10 | the shell notices a workspace daemon that came up in another process, with no operator action | 1 |
| W11 | the shell's workspace verb reaches the same `FocusWorkspace` a band click lands in, and the grid swaps | 1 |
| W12 | a live shell window hot-swaps: it spawns a successor from a DIFFERENT binary path, hands off, and the successor serves the shell pipe as `--shell` | 3 |
| W13 | the window outlives its daemon — one process dies, the other keeps answering | 1 |
| W14 | a write from the window starts a SLEEPING workspace daemon on demand (a real process comes into existence), on both the input-box path and the lane-tile path | 4 |

---

## ui-grid (61 checks: 25 integration, 36 unit, 0 unclear)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| starts_empty | ui-grid | unit | zero alive lanes render as zero non-empty tiles | `Vm.Slots` / `Poller` snapshot mapping | line 131; a precondition, and the mapping is the content |
| typing_does_not_error | ui-grid | integration | the status line after a typed sentence does not start with `error` | W1 | line 142; negative form of W1 — a cut wire shows here first |
| typing_never_tells_you_to_use_the_cli | ui-grid | unit | the daemon's reply string contains no `dodona <verb>` and no `<LANE>` placeholder | the status string built in the daemon's input handler | line 143; pure string content; blocker — no seam, built inline in `Daemon` |
| a_pane_carries_a_project_key | ui-grid | unit | every slot object in the dump carries a `project` property | `MainWindow` `ui dump` serializer (MainWindow.xaml.cs:225-320) | line 158; a JSON-shape contract |
| a_one_project_workspace_shows_no_project_tag | ui-grid | unit | with one project the project field is empty | `Projects.Field(role, cwd, projects, neutralDir)` (Projects.cs:107) | line 160; named in the check's own comment |
| a_one_project_workspace_is_never_asked_anything | ui-grid | unit | `ask` is null when there is nothing to ask | the predicate that raises a question row (`Ask` / `ProjectLadder`) | line 174; absence-of-a-row; blocker — "nothing anywhere raised one" is system-wide, a pure test must target the raise decision |
| the_dump_reports_an_ask_field_at_all | ui-grid | unit | the dump has an `ask` key | `MainWindow` `ui dump` serializer | line 175; JSON-shape contract |
| a_lane_now_exists | ui-grid | integration | exactly one lane exists after typing | W1 | line 180 |
| lane_named_from_the_text | ui-grid | unit | "add a settings dialog to the app" names the lane SETTINGS | `Daemon.NameFromText` (Daemon.cs:3863) | line 181; longest non-stopword, uppercased — entirely pure |
| lane_is_focused | ui-grid | unit | a newly created lane is the focused one | `kv.focused_lane` set on the spawn path (Daemon.cs:6053) | line 182; blocker — daemon-internal side effect, no seam |
| the_message_was_delivered | ui-grid | integration | the typed sentence appears in the lane's pane lines | W1 | line 183 |
| the_agent_answered | ui-grid | integration | the fake agent's reply came back into the pane | W2 | line 184; the round trip |
| creation_is_announced | ui-grid | unit | an announcement row exists for the new lane | the `PaneEvent("announcement", ...)` written by `SpawnAgentLaneAsync` | line 188; blocker — daemon-internal, no seam |
| announcement_offers_undo | ui-grid | unit | the announcement body matches `undo: dodona lane-stop \d+` | the format string at Daemon.cs:6056 | line 189; pure string format |
| second_sentence_reuses_the_lane | ui-grid | unit | with a live lane and no classifier, a second sentence goes to it rather than making a second lane | the code-only routing rung in `Daemon.RouteInput` (focused-lane fallback) | line 204 |
| second_message_delivered | ui-grid | integration | the second sentence reached the existing lane's pane | W1 | line 205 |
| the_box_opens_at_three_lines | ui-grid | integration | `fit` measures into 55..85 px, `height == fit`, `sized == false` | W3 | line 211; `fit` is a MEASURED `MinHeight` — no stub can produce it |
| the_box_grows_past_the_default | ui-grid | integration | after 5 shift+enters, `lines == 6` and `height > fit` | W3 | line 220; auto-grow is a layout pass |
| the_box_shrinks_back_to_the_default | ui-grid | integration | after Enter empties it, height returns to `fit` | W3 | line 223 |
| shift_enter_makes_a_second_line | ui-grid | unit | `InputKey(shift: true)` inserts a newline | `MainWindow.InputKey` (MainWindow.xaml.cs:636) | line 245; `ui key` CALLS the handler — it does NOT synthesize a keystroke, so this does not prove the WPF class-handler trap; blocker — the method mutates `InputBox` directly |
| shift_enter_sends_nothing | ui-grid | unit | the draft text survives a shift+enter | `MainWindow.InputKey` | line 246 |
| shift_enter_delivered_nothing_anywhere | ui-grid | unit | no new `pane_events.user_input` row appeared | `MainWindow.InputKey` (the branch that does NOT call `SubmitInput`) | line 248; the store count is the mechanism, the decision is the content |
| resize_grip_exists | ui-grid | integration | a UIA element named `resize-input` is in the window's tree | W4 | line 253; the only proof the grip is a real control (its ACTION is driven by the verb, not the control) |
| the_box_drags_taller | ui-grid | integration | after `input-resize 70`, `height >= fit+50` and `sized == true` | W3 | line 261; `SetInputHeight` clamps against `ActualHeight * 0.6` and returns `InputBox.ActualHeight`; the arithmetic is extractable, the values are not |
| a_taller_box_never_resizes_the_window | ui-grid | integration | the window's height is unchanged after the box grew | W3 | line 262; the feed gives up the pixels — a pure layout consequence; NOTE it passes vacuously if nothing renders, so it is not W3's best single |
| double_click_refits_the_box | ui-grid | integration | reset returns `height <= fit+2` and `sized == false` | W3 | line 265; the refit is a re-measure |
| resizing_kept_the_draft | ui-grid | unit | `ResizeInput` does not touch `InputBox.Text` | `MainWindow.ResizeInput` / `SetInputHeight` (MainWindow.xaml.cs:1055) | line 266 |
| enter_still_sends | ui-grid | unit | `InputKey(shift: false)` submits and clears the box | `MainWindow.InputKey` -> `SubmitInput` | line 276 |
| the_hint_comes_back_when_the_box_empties | ui-grid | unit | `hint` is true when the box is empty | the hint-visibility predicate in the dump serializer / `Vm` | line 277 |
| the_newline_survived_to_the_agent | ui-grid | integration | the stored `user_input` body carries the embedded U+000A | W1 | line 286; the comment claims "all the way to the agent's stdin" but the assertion reads `pane_events` — it proves UI -> daemon only |
| undo_stops_the_lane | ui-grid | integration | running the undo the announcement printed empties the grid | W5 | line 292; the single representative of W5 |
| undo_keeps_the_transcript | ui-grid | unit | stopping a lane does not delete its `pane_events` | `Store` retention on `lane-stop` | line 294 |
| typing_after_undo_starts_a_fresh_lane | ui-grid | integration | typing works again from zero lanes | W1 | line 303 |
| policy_table_is_inspectable | ui-grid | unit | the printed table contains `design-tier` | `Policy.Default` (Policy.cs:37) | line 306; **KNOWN DUPLICATION** — shells out to `dodona policy`, never touches the window |
| policy_picks_cheap_for_mechanical | ui-grid | unit | "fix the spelling in the readme" -> `haiku low` | `Policy.Resolve` (Policy.cs:66) | line 307; **KNOWN DUPLICATION** with the unit suite |
| policy_picks_max_for_design | ui-grid | unit | "redesign the schema" -> `opus max` | `Policy.Resolve` | line 308; **KNOWN DUPLICATION** |
| policy_default_is_opus_high | ui-grid | unit | "make the toolbar collapsible" -> `opus high` | `Policy.Resolve` | line 309; **KNOWN DUPLICATION** |
| override_lane_started | ui-grid | integration | `@haiku @low say fix the spelling...` starts one SPELLING lane | W1 | line 324 |
| override_tokens_stripped_from_prompt | ui-grid | unit | the delivered prompt has no `@haiku` / `@low` | `Policy.StripOverrides` (Policy.cs:49) | line 326 |
| override_recorded_in_causal_chain | ui-grid | unit | the `policy_choice` event detail reads `haiku/low ... overridden=True` | `Policy.Resolve` + the detail format at Daemon.cs:6052 | line 333 |
| choice_announced_to_operator | ui-grid | unit | the announcement body carries `haiku/low` | `Policy.Choice.Describe` in the announcement format | line 334 |
| badge_defers_while_agent_works | ui-grid | unit | badge is 0 while `presence` is not idle | `StoreReader.Badges()` SQL predicate (StoreReader.cs:104) | line 378; blocker — the predicate is SQL, so a pure test needs a temp SQLite fixture, NOT a daemon or a window; the "during a turn" framing is only how `presence` gets set |
| liveness_shows_a_moving_clock | ui-grid | unit | presence renders as `<presence> <n>s` past the 10 s threshold | `Poller.Liveness(presence, state, lastSeen, now)` (Poller.cs:65) | line 379; a pure static with an injected `now` — the cleanest unit candidate in these three files |
| badge_flushes_at_turn_end | ui-grid | unit | badge >= 1 once presence is idle | `StoreReader.Badges()` | line 385; same SQL predicate, opposite branch |
| quota_line_from_wire | ui-grid | integration | `5h window 42%` appears in the dump after the agent emitted `ratelimit:0.42` | W2 | line 390; the FORMAT half is Poller.cs:171 and is unit-able; what remains is the wire field crossing |
| close_button_exists | ui-grid | integration | a UIA element named `close-lane` is in the tree | W4 | line 402; subsumed by `close_button_stops_the_lane` |
| close_button_stops_the_lane | ui-grid | integration | UIA `Invoke()` on that control stops the lane | W6 | line 407; the strongest affordance check in the file |
| grid_grows_to_the_number_of_lanes | ui-grid | unit | 3 alive lanes -> 3 tiles | `Vm.Slots` mapping | line 436 |
| grid_has_no_empty_placeholders | ui-grid | unit | no `empty: true` slots once lanes exist | `Vm.Slots` mapping | line 437 |
| three_lanes_divide_into_two_columns | ui-grid | unit | 3 slots -> 2 columns | `Vm.GridColumns` switch (Vm.cs:432) | line 438; a pure switch expression over a count |
| collapse_button_exists | ui-grid | integration | a UIA element named `collapse-lane` is in the tree | W4 | line 443; subsumed by `collapse_takes_it_out_of_the_grid` |
| collapse_takes_it_out_of_the_grid | ui-grid | integration | UIA `Invoke()` on that control removes the tile | W6 | line 447 |
| collapsed_lane_becomes_a_chip | ui-grid | unit | a collapsed lane appears in `collapsedLanes`, not `slots` | `Vm.CollapsedLanes` / `Vm.Slots` split | line 448 |
| collapse_does_not_stop_the_lane | ui-grid | unit | `status` still reports `state=alive` for a collapsed lane | collapse writes a VIEW flag, not `lanes.state` (`Store`) | line 455 |
| a_collapsed_lane_still_works | ui-grid | integration | `say` to a collapsed lane still reaches its agent | W2 | line 458; CLI-side, the window is not involved |
| collapse_survives_reopening_the_window | ui-grid | integration | after `ui close` + relaunch, the chip is still a chip | W7 | line 466; W7's best single — a NEW process reads persisted state |
| the_box_remembers_the_size_i_set | ui-grid | integration | after relaunch, `sized == true` and height ~= what was dragged | W7 | line 472; different medium (`ui.json`, not the store), same wire shape |
| the_remembered_size_is_on_disk | ui-grid | unit | the dump's `remembered` equals what was set | `UiSettings.Load()` / `SaveInputHeight` (UiSettings.cs:29/49) | line 475 |
| expanding_returns_it_to_the_grid | ui-grid | unit | `lane-expand` -> 3 tiles, 0 chips | `Vm.Slots` / `Vm.CollapsedLanes` split (the inverse mapping) | line 482; rides W5, but what it TESTS is the mapping |
| a_collapsed_lane_still_shows_its_badge | ui-grid | unit | a chip carries badge >= 1 | `Vm` chip projection of `StoreReader.Badges()` | line 506; §8's "no active-but-invisible lane" — content of the chip projection |
| no_process_left_in_the_build_output | ui-grid | integration | this suite leaked no process running out of `src\...\bin` | W8 | written by `Assert-NoBuildOutputProcesses` in the `finally`; a harness invariant, not a product wire |

## ui-shell (19 checks: 7 integration, 11 unit, 1 unclear)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| bare_launch_is_the_shell_booted_to_zero | ui-shell | integration | `DodonaUi.exe --test-window` with no args answers the SHELL pipe with `bootToZero` | W9 | line 179; which pipe a process opens is a process question; guards the deleted folder picker |
| a_bare_launch_is_never_asked_anything | ui-shell | unit | `ask` is null with no question rows anywhere | the question-raise predicate (`Ask`) | line 189; same family as ui-grid's one-project ask check |
| shell_boots_to_zero_with_nothing_awake | ui-shell | unit | zero open workspaces -> `bootToZero == true` | `Shell.Build()` (Shell.cs:157) | line 198 |
| boot_to_zero_shows_no_bands | ui-shell | unit | zero open workspaces -> zero bands | `Shell.Build()` | line 199 |
| boot_to_zero_still_has_an_input_box | ui-shell | unclear | `window.h > 0` and `workspaceName == ''` | half `Shell.Build()`, half a rendered window dimension | line 200; the `workspaceName` half is pure content, the `window.h > 0` half is a real window and is trivially satisfied — cannot honestly call it either way as written |
| a_waking_workspace_takes_the_grid | ui-shell | integration | starting a daemon in another process makes the shell adopt it, unprompted | W10 | line 209 |
| second_awake_workspace_becomes_a_band | ui-shell | unit | a non-focused open workspace renders as a band | `Shell.Build()` band projection | line 234 |
| band_carries_its_lane_chips | ui-shell | unit | a band's lanes map to `BandLaneSnap` rows with titles | `Shell.Build()` / `BandLaneSnap` (Vm.cs:53) | line 235 |
| focused_workspace_still_holds_the_grid | ui-shell | unit | adding a band does not change which workspace is focused | `Shell.Build()` / `Shell.Focused` | line 236 |
| band_does_not_evict_or_demote_a_lane | ui-shell | unit | the banded workspace's lane is still `state=alive` in its own store | banding writes nothing — `Shell` holds read-only `StoreReader`s | line 240; an absence-of-side-effect assertion over a second store |
| band_click_swaps_the_grid | ui-shell | integration | `ui workspace <name> --shell` swaps which workspace holds the grid | W11 | line 248; `FocusWorkspace` is the method a click lands in |
| the_previous_workspace_becomes_a_band | ui-shell | unit | after a focus swap the old focus renders as a band | `Shell.Build()` after `Shell.Focus()` | line 250 |
| the_grid_shows_the_new_workspaces_lanes | ui-shell | unit | the grid's slots come from the newly focused reader | `Shell.Build()` slot projection | line 251 |
| merged_feed_spans_both_workspaces | ui-shell | unit | the feed union carries rows labelled from >= 2 workspaces | `Shell.Build()` feed union | line 257; needs a two-store fixture, not a live daemon; this is the check `3b235ab` had to prime by hand |
| merged_feed_labels_rows_by_workspace | ui-shell | unit | both specific workspace names appear as labels | `Shell.Build()` feed union labelling | line 258 |
| shell_ui_update_hands_off | ui-shell | integration | `ui update <newexe> --shell` reports `updated:` | W12 | line 270 |
| shell_successor_is_the_new_binary | ui-shell | integration | a process is running from the COPIED build path | W12 | line 284; W12's best single — the incident was a successor that died at startup while the incumbent silently kept the old build |
| shell_survives_the_swap | ui-shell | integration | the shell pipe still answers after the swap, holding a real workspace | W12 | line 285 |
| no_process_left_in_the_build_output | ui-shell | integration | this suite leaked no process into `src\...\bin` | W8 | `finally`; harness invariant |

## ui-wake (8 checks: 6 integration, 1 unit, 1 unclear)

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| the_sleeping_section_has_its_own_live_lane | ui-wake | unclear | `lane-start --title SLEEPER` produced a lane id > 0 | fixture guard | line 174; exists to make the four checks below diagnosable (the comment records four drafts that each failed silently); not really a content question and not the wire the suite is about |
| the_window_outlives_its_daemon | ui-wake | integration | after `stop-daemon` the window process still answers `ui dump` | W13 | line 189; irreducibly two processes, one dead; the suite itself records this as VACUOUS against HEAD and knowingly kept as the precondition for everything below |
| typing_at_a_sleeping_workspace_never_says_daemon_not_running | ui-wake | integration | the status after typing at a sleeping workspace is not the literal words `daemon not running` | W14 | line 201; the string is only wrong when the wire is cut |
| typing_wakes_a_sleeping_workspace | ui-wake | integration | `Wait-Daemon` succeeds — a daemon process came into existence | W14 | line 203; asserts a real process appeared |
| the_lane_verb_reaches_the_window_at_all | ui-wake | unit | the reply is not `unknown ui verb` and echoes `collapse <n>` | the `ui` verb dispatch switch (MainWindow.xaml.cs:140-200) | line 213; a dispatch-table content question |
| a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts | ui-wake | integration | a lane-tile action taken against a sleeping workspace wakes it AND the collapse lands in the store AND reaches the grid | W14 | line 218; W14's best single — this is the exact path that shipped broken (`MainWindow.Send` did not ensure; two of three write paths did) |
| a_lane_click_never_says_daemon_not_running | ui-wake | integration | the status after the lane click is not `daemon not running` | W14 | line 221 |
| no_process_left_in_the_build_output | ui-wake | integration | this suite leaked no process into `src\...\bin` | W8 | `finally`; harness invariant |

---

## Dedup: the one check that should survive per wire

| wire | checks on it now | the one that should survive | why that one |
|---|---|---|---|
| W1 | 7 | `the_newline_survived_to_the_agent` | reads the STORE, not the render, and prints the exact body it saw; a cut wire prints `none`, a mangled payload prints the mangling. Every other W1 check is satisfied by it passing at all. |
| W2 | 3 | `the_agent_answered` | the full round trip agent -> shim -> daemon -> store -> pane. `quota_line_from_wire` and `a_collapsed_lane_still_works` are the same wire carrying a different payload / in a different lane state. |
| W3 | 6 | `the_box_opens_at_three_lines` | it pins `fit` to a MEASURED range (55..85) plus `height == fit` and `sized == false`; with no real layout `fit` is 0 and it goes red specifically. `a_taller_box_never_resizes_the_window` carries the better incident but passes vacuously when nothing renders. |
| W4 | 3 | `resize_grip_exists` | the close and collapse elements are already implied by W6's `Invoke()`; the grip is the only control whose PRESENCE is the sole assertion (its action is driven by `ui input-resize`, not by the control). |
| W5 | 1 | `undo_stops_the_lane` | already the only one. Note ~10 unit-marked checks in ui-grid are OBSERVED through this wire (`grid_grows_to_the_number_of_lanes`, `expanding_returns_it_to_the_grid`, `collapsed_lane_becomes_a_chip`, ...), so it must survive somewhere even though it overlaps `m3`'s remit ("the UI as a view over the store"). |
| W6 | 2 | `close_button_stops_the_lane` | it finds a real control by automation name, `Invoke()`s it, and demands the DAEMON acted (the lane is gone). `collapse_takes_it_out_of_the_grid` is the same three steps with a different verb. |
| W7 | 2 | `collapse_survives_reopening_the_window` | a genuinely new window process reading persisted state. `the_box_remembers_the_size_i_set` rides the same relaunch but through `ui.json` rather than the store — the only real reason to keep both. |
| W8 | 3 (4 across all four ui-* suites) | cannot be deduplicated | `no_process_left_in_the_build_output` reports what THIS suite's `finally` left behind. It is per-suite by construction and is a harness invariant, not a product wire. |
| W9 | 1 | `bare_launch_is_the_shell_booted_to_zero` | already the only one. |
| W10 | 1 | `a_waking_workspace_takes_the_grid` | already the only one. |
| W11 | 1 | `band_click_swaps_the_grid` | already the only one. |
| W12 | 3 | `shell_successor_is_the_new_binary` | the recorded incident is precisely "the successor died at startup and the incumbent silently kept the old build"; this is the only one of the three a silent no-op cannot satisfy. `shell_ui_update_hands_off` reads an intention, `shell_survives_the_swap` passes if the incumbent simply never handed off. |
| W13 | 1 | `the_window_outlives_its_daemon` | already the only one. |
| W14 | 4 | `a_lane_click_at_a_sleeping_workspace_wakes_it_and_acts` | it is the exact path that shipped broken (`MainWindow.Send`), and it asserts three things at once: the daemon woke, the action landed in the store, and the grid re-rendered. The two `never_says_daemon_not_running` checks are the same wire read as a string, and `typing_wakes_a_sleeping_workspace` is the same wire on the sibling write path. |

**14 wires, 38 checks today.** One check per wire is 13 product checks (W8 stays per-suite, so
13 + 3 harness rows = 16 rows kept out of 38).

## Blockers — things that make a check hard to move down

- **`Policy.Resolve` / `Policy.StripOverrides` / `Policy.Default` already have a home.** The four
  `policy_*` checks and `override_tokens_stripped_from_prompt` shell out to `dodona policy` from
  inside a window suite. They touch NO window. Named duplication; free to delete here once the
  unit suite's coverage of the same rules is confirmed BY NAME (constraint 1).
- **`StoreReader.Badges()` is a SQL predicate, not a C# function.** Three checks
  (`badge_defers_while_agent_works`, `badge_flushes_at_turn_end`,
  `a_collapsed_lane_still_shows_its_badge`) are about that one `WHERE` clause. Moving them down
  needs a temp-SQLite fixture that can set `lanes.presence` and insert unacked announcements —
  cheap, but it is not a "pure function" test and there is no seam that returns the predicate.
- **`SetInputHeight` mixes pure arithmetic with WPF property reads.** `the_box_drags_taller` and
  `double_click_refits_the_box` are mostly `Math.Max(floor, Math.Min(want, ActualHeight * 0.6))`.
  Extracting `ClampInputHeight(want, fit, windowHeight, autoCap)` would move both down; without it
  they need a rendered window.
- **`MainWindow.InputKey` / `ComposeInput` mutate `InputBox` directly.** Three checks
  (`shift_enter_makes_a_second_line`, `shift_enter_sends_nothing`, `enter_still_sends`) are pure
  decisions ("shift -> newline, bare -> submit") wrapped around TextBox mutation. No seam.
- **Several values only the daemon computes, with no seam at all**: `lane_is_focused`
  (`kv.focused_lane` written inside `SpawnAgentLaneAsync`), `creation_is_announced` and
  `announcement_offers_undo` (a `PaneEvent` written inline at Daemon.cs:6053-6056),
  `typing_never_tells_you_to_use_the_cli` (a status string built inside the input handler).
  Moving these down means extracting a formatter first, not writing a test.
- **`Shell.Build()` is the single most reusable seam here.** NINE ui-shell checks are
  `Shell.Build()` over a fixture list of open workspaces — bands, focus, slots, and the feed
  union with its labels. It already takes read-only `StoreReader`s, so a fixture is two temp
  SQLite files, no daemon and no window.
- **`ui type` / `ui key` do NOT synthesize keystrokes** — they call `SubmitInput` / `InputKey`
  directly (MainWindow.xaml.cs:148-165). So no check in these three files proves the WPF
  class-handler trap CLAUDE.md §0.2 records (with `AcceptsReturn` the TextBox class handler eats
  Enter before an instance `KeyDown`). CLAUDE.md's claim that "`tests/ui-grid-acceptance.ps1` now
  proves Enter still sends" is true only of `InputKey(false)`, not of a real keypress. Flagging to
  the synthesis agent: that is an UNCOVERED wire, not a duplicated one — and it is the one wire in
  this group that a refactor could plausibly be blamed for losing.
- **The absence checks cannot be made pure by moving them.**
  `a_one_project_workspace_is_never_asked_anything`, `a_bare_launch_is_never_asked_anything` and
  `band_does_not_evict_or_demote_a_lane` assert that nothing anywhere did something. A pure test
  can only target the decision function (the raise predicate, `Shell.Focus`), which is a narrower
  claim than the check makes.
