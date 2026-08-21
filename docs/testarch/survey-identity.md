# survey-identity — classification of every check in tests/workspace-acceptance.ps1 and tests/concierge-acceptance.ps1

Counted, not estimated. `workspace` registers 149 named checks plus `no_process_left_in_the_build_output`
(written by `Assert-NoBuildOutputProcesses`, tests/_workspace.ps1:353) = **150**. `concierge` registers 41
named checks, 6 generated in the `foreach ($rung in 'only','registry','path','fuzzy','discovery','ask')`
loop at concierge-acceptance.ps1:307, plus the same harness check = **48**. **Total 198.**

Verdicts: **163 unit, 33 integration, 2 unclear.**

Classification rule applied (stated so the synthesis agent can re-derive it): a check is INTEGRATION only
when the answer requires a real OS-level event — a process started or died, a child agent replied through
a shim pipe, a real git ref/worktree/init mutated the disk, a file moved, a WPF window rendered, a daemon
restarted, or two things raced. A check whose assertion is a store row, a registry row, a CLI string, a
feed line, an event detail, a schema, or a path derivation is a CONTENT question, even when a daemon was
needed to produce it. Store- and Registry-backed content checks are marked `unit` and flagged in the
"pure function" column with the type that really answers them; see the blockers list at the bottom —
most of them need a Store/Registry fixture the unit suite does not have today.

| check name | suite | verdict | what it really asserts | pure function or wire | note |
|---|---|---|---|---|---|
| discovers_repos | workspace | unit | which folders under the members are repositories, and that a non-repo folder is not one | Repos.Discover / Repos.Under | input is a real directory tree; needs a temp-tree fixture or a seam taking found paths |
| infers_repo_from_claims | workspace | unit | a claim path resolves to exactly one repository | Repos.ForClaims | reply text `ticket 1 repo engine` is built inline in Daemon.HandleAsync |
| second_repo_ticket | workspace | unit | same, for the second repository | Repos.ForClaims | variation on the row above |
| worktree_belongs_to_its_repo | workspace | integration | `git worktree add` really ran against the inferred repo and the files on disk are that repo's | WIRE: ticket-create creates a real git worktree beside the repo its claims resolve to | survivor for this wire |
| second_worktree_is_other_repo | workspace | unit | which repo the second worktree came from | Repos.ForClaims + Paths.Worktrees | same wire as the row above, variation only |
| cross_repo_ticket_refused | workspace | unit | claims spanning two repositories are refused, with the reason | Repos.ForClaims error text | |
| claim_outside_any_repo_refused | workspace | unit | a claim in no repository is refused and names what exists | Repos.ForClaims error text | |
| gate_allows_claimed_path | workspace | unit | a worktree path maps to a workspace-relative path a claim covers | Claims.Normalize + Claims.Covers | |
| gate_denies_other_repo | workspace | unit | a path in another repository is not covered | Claims.Covers | |
| both_repos_hold_tokens_at_once | workspace | unit | the merge token is keyed per repository, so two grants coexist | Store.TokenRequest keyed on repo_path | needs a Store fixture, not pure |
| token_status_is_per_repo | workspace | unit | token-status renders one holder row per repository | Store token read plus CLI render | |
| same_repo_still_serializes | workspace | unit | a second ticket in one repository queues | Store.TokenRequest | |
| named_repo_still_validates_its_claims | workspace | unit | `--repo X` does not bypass claim validation | Repos.CheckClaims | |
| claim_extend_cannot_cross_repositories | workspace | unit | an extension cannot widen a ticket into another repository | Store.ClaimExtend + Repos.CheckClaims | |
| lands_in_its_own_repo | workspace | integration | the land reports the repository it fast-forwarded | WIRE: a land fast-forwards exactly one real repository's main | dup of engine_main_advanced |
| engine_main_advanced | workspace | integration | the real git ref moved and carries the branch's content | WIRE: a land fast-forwards exactly one real repository's main | survivor for this wire — the loudest and most specific |
| tools_main_untouched | workspace | integration | the other repository's ref did not move | same wire | dup |
| second_repo_lands_too | workspace | integration | the second repository lands as well | same wire | dup |
| queue_advances_within_repo | workspace | unit | the queue grants the next ticket after a release | Store.TokenRelease and TokenRequest | |
| an_out_of_claim_branch_is_no_longer_refused_the_token | workspace | unit | token-request no longer consults claims (D-R5) | Store.TokenRequest | |
| the_touch_record_uses_workspace_paths | workspace | unit | git's repo-relative diff paths are prefixed to workspace-relative before recording | Claims.Normalize over the repo prefix, Daemon.cs:1810-1817 | the git diff is fixture input; the prefixing is pure |
| a_symbol_claim_can_be_held_in_one_repo | workspace | unit | a pathless claim is scoped to a repository | Store.FindConflicts + Claims.Overlap | |
| the_same_claim_string_is_free_in_a_different_repo | workspace | unit | the same claim string in another repository does not conflict | Store.FindConflicts scoping | |
| the_same_claim_string_in_the_SAME_repo_is_still_detected | workspace | unit | the detection survived the scoping | Claims.Overlap + Store.FindConflicts | |
| a_lane_needs_no_repository | workspace | integration | a lane with no repository takes input and its agent answers | WIRE: input reaches a real child agent and its reply comes back through the shim | dup of the_agent_process_really_runs_in_that_project |
| one_causal_chain_names_repos | workspace | unit | ticket_created event details name their repository | Store.Event detail text | |
| identity_is_a_slug_not_a_path_hash | workspace | unit | the workspace id is a generated slug, not a path hash | Workspaces.NewId (Workspaces.cs:319) | |
| store_lives_in_workspace_territory | workspace | unit | the store path derives from DODONA_HOME | Paths.Store / Paths.Home | |
| store_is_not_under_the_project_root | workspace | unit | nothing writes a store into the project tree | Paths.Store | |
| worktrees_stayed_beside_the_repo | workspace | unit | the worktree root is beside the member, deliberately | Paths.Worktrees | existence is proved by the worktree wire |
| repo_in_two_workspaces_refused | workspace | unit | attaching an owned repo to a second workspace is refused | Registry.Attach + Registry.RepoConflict | needs a Registry fixture |
| refusal_says_why_two_tokens_is_the_problem | workspace | unit | the refusal text names the two-merge-tokens reason | Registry.Attach error text | |
| refusal_offers_the_move_affordance | workspace | unit | the refusal names `workspace-move --member` | Registry.Attach error text | CLAUDE.md 0.1: a refusal names what un-sticks it |
| bare_folder_may_be_shared | workspace | unit | a non-repo folder is exempt from exclusivity | Registry.LooksLikeRepo + Attach | |
| move_reassigns_the_repo | workspace | unit | after a move exactly one workspace owns the repo | Registry.Move | asserted after the fact, so not a race check |
| bare_folder_in_two_workspaces_is_allowed | workspace | unit | two workspaces may hold one bare folder | Registry.Create + Attach + LooksLikeRepo | |
| ticket_refused_when_repo_is_not_exclusive | workspace | unit | the point-of-use check refuses when a bare folder became a repo | Registry.RepoConflict at ticket-create | `git init` behind the registry's back is fixture setup, not the assertion |
| exclusivity_backstop_offers_the_move | workspace | unit | that refusal names workspace-move | refusal text | |
| exclusivity_refusal_is_in_the_causal_chain | workspace | unit | a `ticket_repo_not_exclusive` event row exists | Store.Event | |
| rename_keeps_the_id | workspace | unit | rename changes display only | Registry.Rename | |
| rename_keeps_the_store_path | workspace | unit | the store path is derived from the id, not the name | Paths.Store | |
| rename_keeps_the_ctl_pipe | workspace | unit | the pipe name is derived from the id | Instance ctl-pipe naming | |
| new_name_resolves | workspace | unit | the new name resolves to the same id | Registry.ByNameOrId | |
| alias_resolves_to_the_workspace | workspace | unit | an alias resolves to the workspace | Registry.AddAlias + ByNameOrId | |
| unknown_workspace_name_is_refused | workspace | unit | an unknown name is a typo, not an invitation | WorkspaceResolve.ByNameOrId | |
| migrated_workspace_named_after_its_root | workspace | unit | the migrated workspace takes the root's leaf name | WorkspaceResolve.ForPath migration naming | |
| migration_moved_the_store | workspace | integration | this exact file left `<root>\.dodona` and arrived in workspace territory | WIRE: a legacy store is physically relocated on first explicit resolve | survivor for this wire |
| migration_moved_the_shim_info | workspace | integration | the shim-info file moved too | same wire | dup |
| migrated_root_is_the_sole_member | workspace | unit | the migrated workspace has one member | Registry rows | |
| inherited_cwd_creates_no_workspace | workspace | integration | an argument-free invocation whose cwd is an unowned folder refuses and writes no registry row | WIRE: the CLI's argument-free entry resolves through PathSource.InheritedCwd and refuses | borderline: the decision is pure (WorkspaceResolve.ForPath takes PathSource); the residual wire is that Program.cs passes InheritedCwd when no --root/--workspace is given |
| inherited_cwd_does_not_move_a_legacy_store | workspace | unit | the refusal path performs no migration | WorkspaceResolve.ForPath decision | |
| the_refusal_names_the_command_that_makes_a_workspace | workspace | unit | the refusal names `workspace-create --name` and "user action" | refusal text | |
| env_workspace_is_used_before_any_folder | workspace | unit | DODONA_WORKSPACE beats the inherited cwd | WorkspaceResolve precedence | |
| an_explicit_root_beats_the_inherited_env | workspace | unit | an explicit --root beats the env variable | WorkspaceResolve precedence | suite records this as VACUOUS by construction |
| a_stale_env_workspace_is_announced_and_still_creates_nothing | workspace | unit | a stale id is announced and falls through, creating nothing | WorkspaceResolve precedence and announcement text | |
| creating_a_workspace_cannot_steal_an_owned_repo | workspace | unit | create-with-member is refused when the member is owned | Registry.Create + Attach | |
| multi_member_repo_names_are_member_prefixed | workspace | unit | the naming rule changes with member count | Repos.Discover / Repos.Under prefix rule | the canonical content question in this suite |
| multi_member_ticket_names_its_repo | workspace | unit | the ticket names the prefixed repo | Repos.ForClaims | |
| multi_member_worktree_sits_beside_its_own_member | workspace | integration | the worktree directory is on disk beside the right member and not the other | WIRE: ticket-create creates a real git worktree beside the repo its claims resolve to | dup of worktree_belongs_to_its_repo |
| named_repo_accepts_its_own_claims | workspace | unit | `--repo X` with a claim really in X is accepted | Repos.CheckClaims | |
| a_lone_project_that_is_a_repo_is_still_named_dot | workspace | unit | the degenerate naming case | Repos.Under empty prefix | |
| attaching_a_second_project_renames_the_first_repository | workspace | unit | discovery recomputes names on attach | Repos.Discover | |
| second_ticket_lands_in_the_same_repository_under_its_new_name | workspace | unit | a ticket created under the new name resolves to the same repository | Repos.ForClaims + Repos.ByPath | |
| the_extend_conflict_search_still_sees_across_a_rename | workspace | unit | claim-extend's conflict search reduces both spellings to one namespace | Store.FindConflicts via Store.TxRepoId + Claims reduction | |
| one_folder_under_two_names_is_still_one_claim | workspace | unit | a path inside a subtree overlaps across a rename | Claims.Overlap after repo-relative reduction | |
| a_disjoint_directory_in_the_renamed_repository_is_still_free | workspace | unit | the scoping did not start refusing the whole repository | Claims.Overlap | VACUOUS by construction per the suite comment |
| one_repository_grants_one_merge_token_after_a_rename | workspace | unit | one repository, one token, across two display names | Store.TokenRequest keyed on repo_path | |
| two_tickets_in_one_repository_cannot_both_hold_the_token | workspace | unit | exactly one holder row | Store merge_token rows | |
| a_drifted_ticket_still_refuses_a_path_it_does_not_own | workspace | integration | after a real daemon stop and start, resolution is rebuilt from the store and still refuses | WIRE: a daemon restart re-resolves ticket to repository to claims from the store, caching no repository name | survivor for this wire |
| a_drifted_tickets_claims_still_cover_its_own_files | workspace | unit | the ticket's claims still cover its own files | Claims.Covers | same wire as the row above |
| a_pre_v9_store_is_copied_before_it_is_migrated | workspace | integration | a real daemon opening an older on-disk store writes a `.pre-v8` backup file and survives the migration | WIRE: an older on-disk store is backed up and migrated when a real daemon opens it | survivor; the Store constructor kills the daemon if migration throws (CLAUDE.md 0.2) |
| a_pre_v9_ticket_is_stamped_with_its_repository_path | workspace | unit | the migration stamps repo_path onto old tickets | Store.Migrate | needs a Store fixture built in the v8 shape |
| two_token_rows_over_one_repository_are_merged_and_announced | workspace | unit | two token rows over one repository are merged and an event says so | Store.Migrate repair | |
| the_merged_token_keeps_the_highest_generation | workspace | unit | the fencing counter does not go backwards | Store.Migrate repair | |
| a_colliding_leaf_gets_the_tilde_name | workspace | unit | two same-leaf projects produce `leaf~2` | Repos.Discover naming | |
| the_tilde_name_is_recycled_onto_the_new_project | workspace | unit | detach plus attach re-points `leaf~2` | Repos.Discover naming | |
| a_recycled_repo_name_cannot_inherit_another_repos_ticket | workspace | unit | a ticket keyed on repo path is refused when its repository left | Repos.ByPath + Store token path | |
| a_plain_lane_records_the_project_it_opened_in | workspace | unit | lanes.cwd holds the first project for a plain lane | spawn-site project resolution, Store.LaneCwd | the suite itself notes the row is written before Process.Start, so it is intent, i.e. content |
| a_ticket_lane_records_a_directory_inside_its_own_project | workspace | unit | a ticket lane's cwd is inside its own project | Paths.Worktrees + spawn-site resolution | |
| two_lanes_in_one_workspace_run_in_different_projects | workspace | unit | the two recorded cwds are not the same place | spawn-site resolution | |
| status_names_the_project_of_a_plain_lane | workspace | unit | status reports the project of a plain lane | Projects.Field | |
| status_names_a_ticket_lanes_project_not_its_worktree | workspace | unit | the ancestor project, not the worktree | Projects.Field | |
| status_does_not_report_two_projects_as_one | workspace | unit | two lanes report two projects | Projects.Field | |
| a_management_lane_is_not_reported_against_a_project | workspace | unit | a management role reports no project and sits in the neutral dir | Projects.Field + Projects.IsManagementRole + Paths.NeutralDir | |
| the_window_shows_both_projects_lanes | workspace | integration | a real WPF window rendered two non-empty slots with the right titles | WIRE: a real window renders lanes from the store and computes the pane project with the same function status uses | dup; this wire is already owned by m3 and ui-grid |
| a_pane_names_the_project_its_lane_is_in | workspace | unit | the pane's project field value | Projects.Field | |
| a_ticket_panes_project_is_its_project_not_its_worktree | workspace | unit | the pane shows the project, not the worktree | Projects.Field | |
| the_window_and_status_agree_about_a_lanes_project | workspace | integration | the UI call site really uses the same production function the daemon does | same window wire | survivor: fails if the window did not render AND if the UI grew its own copy of the field logic |
| a_lane_opens_in_the_project_it_was_given | workspace | unit | `--project` is recorded as the lane's cwd | spawn-site resolution + Store.LaneCwd | |
| the_agent_process_really_runs_in_that_project | workspace | integration | the OS's answer: the child agent's own CurrentDirectory, reported back through shim and store | WIRE: input reaches a real child agent running where the daemon put it, and its reply comes back | survivor for this wire — the loudest and most specific in either suite |
| a_folder_inside_a_project_opens_in_that_project | workspace | unit | a subdirectory resolves up to its project | Projects.Of | |
| a_lane_in_a_folder_no_project_owns_is_refused | workspace | unit | an unowned folder is refused with the reason | Projects.Of / Projects.IsOwned + refusal text | |
| a_refused_lane_leaves_no_row_behind | workspace | unit | the lane count did not move | Store lanes count | |
| a_lanes_permission_mode_comes_from_its_own_project | workspace | unit | a project's dodona.json chooses its lane's permissionMode | Config.For (Daemon.cs:106) | observable is the lane_config event written at Daemon.cs:3850 |
| a_lane_does_not_inherit_another_projects_permission_mode | workspace | unit | the other project keeps the built-in default | Config.For | |
| two_lanes_in_one_workspace_are_configured_by_different_projects | workspace | unit | one daemon, two configs | Config.For | |
| claim_check_covers_a_write_in_the_tickets_own_project | workspace | unit | the gate's base list includes the ticket's own project | claim-check base resolution + Claims.Covers | |
| claim_check_still_denies_a_write_no_project_owns | workspace | unit | the base list did not widen to everything | claim-check base resolution | |
| repo_status_reports_the_project_it_was_given | workspace | unit | the command acts on the `--project` argument | repo-status argument routing | |
| repo_init_initialises_the_project_it_was_given | workspace | integration | `git init` really happened in the named project (a `.git` appeared on disk) | WIRE: repo-init runs a real git init in the project it was given | survivor; could fold into one real-git fixture with the land and worktree wires |
| repo_init_does_not_answer_about_another_project | workspace | unit | the answer never names another project | repo-init reply text | this is the harm signature of the defect, but it is text |
| detaching_a_project_stops_the_lanes_that_were_in_it | workspace | integration | a registry edit killed a live shim pid | WIRE: a registry mutation reaps the live agent processes it orphans | survivor for this wire |
| a_detached_projects_lane_records_why_it_stopped | workspace | unit | a `lane_project_detached` event names the project | Store.Event | |
| a_lane_is_not_respawned_into_a_project_that_left | workspace | unit | respawn refuses a folder the workspace no longer owns | Projects.IsOwned + refusal text | |
| a_stranded_lane_can_be_re_homed_to_a_project_that_is_still_here | workspace | unclear | respawn with `--project` succeeds and rewrites lanes.cwd | Store.LaneCwd for the row; a real respawn spawns a child nothing here asserts | the check as written is content, but whether the respawn-spawns-a-process wire is covered elsewhere (m0/m3) was not verified in this pass |
| a_one_project_workspace_writes_no_project_ladder_event | workspace | integration | the whole typed-input path in the operator's own workspace shape writes only the pre-existing event kinds | WIRE: input reaches a real child agent and its reply comes back | dup; this is a constraint-3 operator-shape guard and its whitelist should survive as an assertion inside that wire's check |
| a_one_project_workspace_opens_no_question | workspace | unit | one project opens no question row | ProjectLadder.Decide `only` short-circuit | |
| a_typed_sentence_with_no_project_to_infer_is_held | workspace | unit | rung 4 holds rather than guessing | ProjectLadder.Decide | |
| a_held_sentence_invents_no_lane | workspace | unit | the lane count did not move | ProjectLadder.Decide + Store lanes count | |
| the_project_hold_is_recorded_as_asked | workspace | unit | routing_decisions row reads ask/no-project | Store routing_decisions | |
| the_project_hold_offers_every_project_it_knows | workspace | unit | the project_unknown event names both projects | Store.Event detail | |
| the_project_hold_says_how_to_answer_it | workspace | unit | the announcement names `lane-start` | announcement text | the async lag is handled by a Wait-Until; the assertion is text |
| the_project_hold_opens_a_question_row | workspace | integration | the hold path is actually connected to the questions table (the defect: "ask asked nobody" for two days) | WIRE: a held sentence becomes a question row, and answering it spawns the lane and delivers the stored sentence | dup |
| the_question_carries_the_held_sentence_whole | workspace | unit | questions.subject is the verbatim sentence | Store question write | |
| the_question_offers_every_project_by_name | workspace | unit | candidates hold both project leaf names | candidate builder | |
| the_question_offers_no_filesystem_navigation | workspace | unit | no candidate id contains a path separator or drive letter | candidate builder | operator directive 3.1 |
| opening_a_question_still_invents_no_lane | workspace | unit | opening a question creates no lane | Store lanes count | |
| a_route_answer_naming_nothing_offered_is_refused | workspace | unit | a near-miss answer is refused and the row stays open | QuestionAnswer validation | |
| a_typed_sentence_naming_a_project_opens_a_lane_there | workspace | unit | rung `named` places the lane by leaf | ProjectLadder.NameMatch / Mentions | |
| the_named_rung_records_which_evidence_answered | workspace | unit | project_chosen says rung=named how=leaf | Store.Event detail | |
| naming_a_project_costs_no_model | workspace | unit | no classified_project event was written | ProjectLadder.Decide short-circuit order | VACUOUS by construction per the suite comment |
| a_new_task_joins_the_only_project_with_a_live_lane | workspace | unit | rung `live` with one live project | ProjectLadder.Decide + Projects.Live | |
| the_live_rung_records_that_it_needed_no_model | workspace | unit | project_chosen says rung=live how=sole-live | Store.Event detail | |
| one_live_project_costs_no_model_either | workspace | unit | no classifier call on that rung | ProjectLadder.Decide | |
| several_live_projects_reach_the_cheap_tier | workspace | integration | a classifier lane was actually consulted (classified_project count moved) | WIRE: the project ladder consults a live classifier lane over the shim and acts on its answer | dup |
| the_lane_opens_in_the_project_the_classifier_chose | workspace | integration | the classifier's answer came back and placed a new lane | same wire | survivor: asserts the lane count moved AND the cwd, so a held or lost answer is red |
| the_classified_rung_records_that_a_model_answered | workspace | unit | project_chosen says rung=live how=classified | Store.Event detail | |
| a_classifier_that_will_not_choose_holds_the_sentence | workspace | unit | a `none` answer holds rather than falling back | ProjectLadder.Decide | |
| an_unchosen_project_invents_no_lane | workspace | unit | the lane count did not move | Store lanes count | |
| a_classifier_that_would_not_choose_says_so_in_the_chain | workspace | unit | a project_unclassified event exists | Store.Event | |
| a_project_can_be_taught_a_spoken_handle | workspace | unit | project-alias succeeds | Registry.AddProjectAlias | |
| a_project_handle_is_stored_against_the_project_not_only_the_workspace | workspace | unit | aliases.member_key holds the project key | Registry.AddProjectAlias + registry schema 2 | |
| a_taught_handle_opens_a_lane_in_its_project | workspace | unit | the alias rung places the lane | ProjectLadder.NameMatch alias branch | |
| the_alias_rung_records_that_evidence | workspace | unit | project_chosen says rung=named how=alias | Store.Event detail | |
| a_named_project_is_not_overruled_by_a_busy_one | workspace | unit | a name beats a live project and costs no call | ProjectLadder.Decide rung order | |
| a_handle_for_a_folder_that_is_not_a_project_is_refused | workspace | unit | an alias for a non-project is refused | Registry.AddProjectAlias validation | |
| autostart_builds_the_classifier_the_ladder_will_use | workspace | integration | a daemon started with autostart on creates a router lane process nothing in the test built | WIRE: the operator-shaped daemon builds its own classifier and the ladder decides on that path | dup; this is CLAUDE.md 3's dead-routing-ladder guard |
| the_project_ladder_is_live_on_the_path_the_operator_uses | workspace | integration | a typed sentence on that daemon really placed a lane in the named project | same wire | survivor: red if the ladder is dead in production, which is the incident this exists for |
| a_ladder_decision_is_recorded_on_that_path_too | workspace | unit | project_chosen count moved | Store.Event count | |
| answering_the_project_question_opens_the_lane_there | workspace | integration | answering the question created a lane in the answered project | WIRE: a held sentence becomes a question row, and answering it spawns the lane and delivers the stored sentence | dup |
| the_held_sentence_itself_reaches_the_new_lane | workspace | integration | the words typed twenty checks earlier arrive as user_input in the lane the answer created, across a daemon restart in between | same wire | survivor: red if the row did not open, did not survive, did not carry the sentence, or did not deliver |
| the_answered_rung_records_that_the_operator_decided | workspace | unit | project_chosen says rung=answered how=operator | Store.Event detail | |
| the_answered_delivery_joins_the_routing_chain | workspace | unit | routing_decisions row reads answered/operator | Store routing_decisions | |
| answering_closes_the_question_row | workspace | unit | the question is answered and holds the answer | QuestionAnswer state transition | |
| forget_removes_the_registry_row | workspace | unit | forget deletes the registry row | Registry.Forget | |
| forget_keeps_the_store_directory | workspace | unit | forget deletes no store directory | Registry.Forget scope | |
| forgetting_a_workspace_stops_its_agents | workspace | integration | a live shim pid died after the registry edit | WIRE: a registry mutation reaps the live agent processes it orphans | dup of detaching_a_project_stops_the_lanes_that_were_in_it |
| forgetting_a_workspace_stops_its_orphaned_daemon | workspace | integration | the daemon PROCESS exited because its workspace stopped existing | WIRE: forgetting a workspace stops its now-orphaned daemon process | survivor; distinct from reaping shims — an orphaned daemon can never be hot-swapped again |
| a_forgotten_workspaces_transcripts_survive | workspace | unit | the store directory and the lane row survive a forget | Registry.Forget scope + Store rows | |
| no_process_left_in_the_build_output | workspace | integration | no live process's image path is under src\...\bin after the suite | WIRE: suite hygiene — nothing leaked into the build output | harness assertion (tests/_workspace.ps1:353), not a product wire; must stay in any suite that starts processes |
| concierge_answers_its_pipe | concierge | integration | a separate machine-global concierge process is up and answering its own named pipe | WIRE: the concierge is a real machine-global process reachable and stoppable on its control pipe | survivor for this wire |
| concierge_store_is_its_own | concierge | unit | the status line names concierge\store.db | Paths.ConciergeStore | |
| registry_is_reported_under_dodona_home | concierge | unit | the reported registry path is under DODONA_HOME | Paths.Registry derived from Paths.Home | VACUOUS by design per the suite comment |
| registry_file_exists_where_it_is_reported | concierge | unit | a file is really at the reported path | Paths.Registry + Registry ctor | |
| the_registry_under_dodona_home_is_the_live_one | concierge | unit | that file holds the workspace just created, so it is not a decoy | Registry write path honours Paths.Registry | |
| sole_workspace_needs_no_model | concierge | unit | rung `only` answers when one workspace exists | Concierge resolution ladder (Concierge.ResolveAsync) | no pure seam today — see blockers |
| exact_name_resolves_in_code | concierge | unit | rung `registry` matches a name in the sentence | Concierge ladder + ProjectLadder.Mentions | |
| exact_name_is_explicit_confidence | concierge | unit | that rung is labelled explicit | Concierge ladder verdict | |
| name_does_not_match_inside_a_word | concierge | unit | "network" does not match the workspace "work" | ProjectLadder.Mentions word-boundary rule | the canonical content question in this suite |
| explicit_path_attaches_outright | concierge | unit | rung `path` short-circuits and creates a workspace | Fence.ExplicitPath + Registry.Create/Attach | |
| explicit_path_is_announced_with_undo | concierge | unit | the feed line names workspace-forget | feed text | |
| explicit_path_reuses_its_workspace | concierge | unit | a second mention resolves without creating again | Registry.Owner lookup | |
| fuzzy_match_on_the_cheap_tier | concierge | integration | the concierge really spawned a tier agent through a shim, got an answer back, and acted on it | WIRE: the concierge consults a tier agent child process and acts on its answer | survivor for this wire |
| fuzzy_match_is_announced | concierge | unit | the feed says "fuzzy match" | feed text | |
| low_confidence_does_not_act | concierge | unit | a low-confidence tier answer escalates instead of acting | Concierge ladder confidence rule | |
| fence_is_derived_from_member_parents | concierge | unit | the fence roots are the members' parents | Fence.Roots (Fence.cs:41) | |
| the_fence_never_runs_unbidden | concierge | unit | an unresolvable sentence reaches `ask` with no discovery event written | Concierge ladder control flow after D-L3 | assertion is a rung plus an event count |
| the_ask_offers_going_to_look | concierge | unit | the feed offers `new:NAME` or `look` | feed text | |
| looking_on_request_finds_a_folder_in_the_fence | concierge | unit | the fence walk finds `bay` when asked | Fence.Enumerate over a fixture tree | deterministic directory walk; no daemon, no pipe |
| discovery_is_announced_with_undo | concierge | unit | the feed says "inside the search fence" | feed text | |
| looking_attaches_what_it_found | concierge | unit | a registry row for `bay` exists after the look | Concierge.LookAsync + Registry.Create/Attach | |
| looking_closes_the_question_it_answered | concierge | unit | the answered question leaves the open list | ConciergeStore question state | |
| an_unresolvable_sentence_still_asks | concierge | unit | rung `ask` for an unknown sentence | Concierge ladder | |
| fence_never_reaches_outside_itself | concierge | unit | the walk never reaches a folder outside every member's parent | Fence.Roots + Fence.Enumerate bound | |
| a_look_that_found_nothing_leaves_the_question_open | concierge | unit | a miss does not close the row | Concierge.LookAsync outcome handling | |
| outside_folder_was_never_attached | concierge | unit | no workspace holds the outside folder | Registry rows | |
| double_uncertainty_asks_the_operator | concierge | unit | rung `ask` with a question id | Concierge ladder | |
| question_lands_in_the_merged_feed | concierge | unit | the feed carries "not sure which workspace" | feed text | |
| question_offers_candidates_and_new | concierge | unit | the feed offers "or new?" | feed text | |
| question_is_a_row_that_survives | concierge | unit | the question is a row, listable later | ConciergeStore questions | |
| answer_resolves_the_question | concierge | unit | answering reports the chosen workspace | Concierge.AnswerAsync | |
| answer_teaches_an_alias | concierge | unit | the answer becomes an alias | Concierge.Teach (Concierge.cs:804) | |
| rung_4_decays_to_rung_1 | concierge | unit | the same sentence now resolves at rung `registry` for free | Concierge ladder over the taught alias | the decay property, pure over registry state |
| answering_twice_is_refused | concierge | unit | a second answer to the same question errors | ConciergeStore state guard | |
| review_behind_is_silent_when_it_agrees | concierge | unclear | the feed line count did not change after a review | Concierge review-behind agreement path | as written this cannot distinguish "the review agreed" from "the review never ran"; whether it needs the real tier agent depends on what the replacement asserts |
| review_behind_reports_a_group_misroute | concierge | integration | a review ran asynchronously BEHIND an already-delivered sentence and its disagreement reached the feed afterwards | WIRE: the concierge reviews an already-delivered sentence from behind and its verdict lands in the feed later | survivor for this wire |
| review_behind_admits_it_cannot_undo | concierge | unit | the wording says "already delivered" | feed text | |
| review_behind_hands_over_the_resend | concierge | unit | the wording offers `dodona input ... --workspace` | feed text | |
| concierge_store_holds_no_work_state | concierge | unit | no lanes/tickets/claims/merge_token/token_queue tables exist | ConciergeStore schema | |
| concierge_store_holds_what_must_survive_a_window | concierge | unit | questions, resolutions and feed tables exist | ConciergeStore schema | |
| resolution_recorded_only | concierge | unit | a resolutions row was written for rung `only` | ConciergeStore resolutions write | one of six generated at concierge-acceptance.ps1:307 |
| resolution_recorded_registry | concierge | unit | a resolutions row for rung `registry` | ConciergeStore resolutions write | |
| resolution_recorded_path | concierge | unit | a resolutions row for rung `path` | ConciergeStore resolutions write | |
| resolution_recorded_fuzzy | concierge | unit | a resolutions row for rung `fuzzy` | ConciergeStore resolutions write | |
| resolution_recorded_discovery | concierge | unit | a resolutions row for rung `discovery` | ConciergeStore resolutions write | |
| resolution_recorded_ask | concierge | unit | a resolutions row for rung `ask` | ConciergeStore resolutions write | |
| concierge_stops_gracefully | concierge | integration | the concierge process really exited on `concierge-stop` | WIRE: the concierge is a real machine-global process reachable and stoppable on its control pipe | dup of concierge_answers_its_pipe |
| no_process_left_in_the_build_output | concierge | integration | no live process's image path is under src\...\bin after the suite | WIRE: suite hygiene — nothing leaked into the build output | harness assertion; a separate registration from the workspace suite's |

## The 18 distinct wires, and the check that should survive as the one proving each

| # | wire | checks on it now | survivor |
|---|---|---|---|
| 1 | input reaches a real child agent running where the daemon put it, and its reply comes back through the shim into the store | 3 | the_agent_process_really_runs_in_that_project |
| 2 | ticket-create creates a real git worktree beside the repo its claims resolve to | 2 | worktree_belongs_to_its_repo |
| 3 | a land fast-forwards exactly one real repository's main | 4 | engine_main_advanced |
| 4 | repo-init runs a real git init in the project it was given | 1 | repo_init_initialises_the_project_it_was_given |
| 5 | a legacy `<root>\.dodona` store is physically relocated into workspace territory | 2 | migration_moved_the_store |
| 6 | an older on-disk store is backed up and migrated when a real daemon opens it | 1 | a_pre_v9_store_is_copied_before_it_is_migrated |
| 7 | a daemon restart re-resolves ticket to repository to claims from the store, caching no repository name | 1 | a_drifted_ticket_still_refuses_a_path_it_does_not_own |
| 8 | a real window renders lanes from the store and computes the pane project with the same function status uses | 2 | the_window_and_status_agree_about_a_lanes_project |
| 9 | a registry mutation reaps the live agent processes it orphans | 2 | detaching_a_project_stops_the_lanes_that_were_in_it |
| 10 | forgetting a workspace stops its now-orphaned daemon process | 1 | forgetting_a_workspace_stops_its_orphaned_daemon |
| 11 | the CLI's argument-free entry resolves through the inherited cwd and refuses rather than inventing a workspace | 1 | inherited_cwd_creates_no_workspace |
| 12 | a held sentence becomes a question row that outlives a daemon restart, and answering it spawns the lane and delivers the stored sentence | 3 | the_held_sentence_itself_reaches_the_new_lane |
| 13 | the project ladder consults a live classifier lane over the shim and acts on its answer | 2 | the_lane_opens_in_the_project_the_classifier_chose |
| 14 | the operator-shaped daemon (autostart on, nothing pre-built by the test) builds its own classifier and the ladder decides on that path | 2 | the_project_ladder_is_live_on_the_path_the_operator_uses |
| 15 | the concierge is a real machine-global process reachable and stoppable on its control pipe | 2 | concierge_answers_its_pipe |
| 16 | the concierge consults a tier agent child process and acts on its answer | 1 | fuzzy_match_on_the_cheap_tier |
| 17 | the concierge reviews an already-delivered sentence from behind and its verdict lands in the feed later | 1 | review_behind_reports_a_group_misroute |
| 18 | suite hygiene: nothing leaked into the build output | 2 | keep per suite (harness, not a product wire) |

Sum of "checks on it now" = 33, which is the integration count.

Wires 2, 3 and 4 are three distinct production paths but ONE kind of machinery (a real git repo on disk).
If the target is "a handful", they fold into a single git-mutation integration test with three assertions;
they do not fold into anything cheaper. Wire 8 duplicates a wire m3 and ui-grid already own — the only
thing this group adds there is the two-project fixture, and the project field itself is Projects.Field.
