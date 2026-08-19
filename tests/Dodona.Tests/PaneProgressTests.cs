using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// Mid-turn progress: the three tiers, the phrasing, and the fold (PaneProgress.cs).
///
/// This is the whole component. It is a pure function over a tool name, its input JSON and
/// a list of rows, so every case below runs with no daemon, no store and no window — which
/// matters more than usual here, because the defect this component fixes was invisible for
/// exactly as long as it took someone to watch a real four-minute turn and notice the pane
/// had been silent for it. A blind spot is not something an acceptance suite trips over by
/// accident; it has to be stated as a case.
/// </summary>
public class PaneProgressTiersTests
{
    static (ProgressTier Tier, string Text) Of(string tool, string json) => PaneProgress.FromToolJson(tool, json);

    // ---- Step: looking things up -------------------------------------------------------

    [Fact]
    public void Read_is_a_step_named_by_the_files_leaf() =>
        Assert.Equal((ProgressTier.Step, "read Sim.cs"), Of("Read", """{"file_path":"C:\\work\\src\\Water\\Sim.cs"}"""));

    [Fact]
    public void Grep_shows_the_pattern_and_where() =>
        Assert.Equal((ProgressTier.Step, "grep \"PRAGMA user_version\" in src/Dodona"),
            Of("Grep", """{"pattern":"PRAGMA user_version","path":"C:/work/src/Dodona"}"""));

    /// <summary>A grep with no path is still a grep — a missing field must never turn a
    /// classified tool into the unknown-tool fallback.</summary>
    [Fact]
    public void Grep_without_a_path_is_still_a_step() =>
        Assert.Equal((ProgressTier.Step, "grep \"seq\""), Of("Grep", """{"pattern":"seq"}"""));

    [Fact]
    public void Web_fetch_is_named_by_host_not_by_url() =>
        Assert.Equal((ProgressTier.Step, "fetched docs.microsoft.com"),
            Of("WebFetch", """{"url":"https://docs.microsoft.com/a/b/c?d=e"}"""));

    /// <summary>The one todo marked in_progress, not the first in the list — the agent's own
    /// statement of what it is doing right now is the single most useful line in the stream,
    /// and it is free.</summary>
    [Fact]
    public void Todo_write_reports_the_in_progress_item()
    {
        var json = """
            {"todos":[{"content":"read the filter","status":"completed"},
                      {"content":"write the classifier","status":"in_progress"},
                      {"content":"prove the checks","status":"pending"}]}
            """;
        Assert.Equal((ProgressTier.Step, "now write the classifier"), Of("TodoWrite", json));
    }

    /// <summary>Nothing in progress yet: the first pending item is what it is about to do,
    /// which is still a true and useful line.</summary>
    [Fact]
    public void Todo_write_falls_back_to_the_first_pending_item() =>
        Assert.Equal((ProgressTier.Step, "now write the classifier"),
            Of("TodoWrite", """{"todos":[{"content":"write the classifier","status":"pending"}]}"""));

    [Fact]
    public void An_mcp_tool_is_named_by_server_and_verb() =>
        Assert.Equal((ProgressTier.Step, "called slack send_message"),
            Of("mcp__slack__send_message", """{"channel":"#eng"}"""));

    // ---- Act: something changed, or something ran --------------------------------------

    [Fact]
    public void Write_and_edit_are_acts()
    {
        Assert.Equal((ProgressTier.Act, "wrote Sim.cs"), Of("Write", """{"file_path":"src/Sim.cs"}"""));
        Assert.Equal((ProgressTier.Act, "edited Sim.cs"), Of("Edit", """{"file_path":"src/Sim.cs"}"""));
    }

    [Fact]
    public void Multi_edit_carries_its_edit_count() =>
        Assert.Equal((ProgressTier.Act, "edited Sim.cs (3 edits)"),
            Of("MultiEdit", """{"file_path":"src/Sim.cs","edits":[{"a":1},{"a":2},{"a":3}]}"""));

    /// <summary>The COMMAND, not the agent's description of it. This pane is the only place
    /// the operator sees what is about to run on their machine before it does.</summary>
    [Fact]
    public void Bash_shows_the_command_itself() =>
        Assert.Equal((ProgressTier.Act, "$ dotnet build -c Release"),
            Of("Bash", """{"command":"dotnet build -c Release","description":"Build the solution"}"""));

    /// <summary>A pane row is one line by construction, and a real command is routinely a
    /// heredoc. A raw newline in a body would break the transcript's shape everywhere it is
    /// rendered — the store, `ui dump`, and the tile.</summary>
    [Fact]
    public void A_multiline_command_is_collapsed_to_one_line()
    {
        var (tier, text) = Of("Bash", """{"command":"python - <<'PY'\nprint(1)\nPY"}""");
        Assert.Equal(ProgressTier.Act, tier);
        Assert.DoesNotContain("\n", text);
        Assert.Equal("$ python - <<'PY' print(1) PY", text);
    }

    [Fact]
    public void A_long_command_is_truncated_with_an_ellipsis()
    {
        var (_, text) = Of("Bash", $$"""{"command":"{{new string('x', 400)}}"}""");
        Assert.True(text.Length <= 80, $"length was {text.Length}: {text}");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void A_subagent_is_named_by_what_it_was_asked_to_do() =>
        Assert.Equal((ProgressTier.Act, "subagent audit the store schema"),
            Of("Task", """{"description":"audit the store schema","subagent_type":"general-purpose"}"""));

    // ---- Noise -------------------------------------------------------------------------

    [Theory]
    [InlineData("BashOutput")]
    [InlineData("KillShell")]
    public void Polling_its_own_shell_is_noise(string tool) =>
        Assert.Equal(ProgressTier.Noise, Of(tool, """{"bash_id":"1"}""").Tier);

    // ---- The anti-silent-degrade property ---------------------------------------------

    /// <summary>
    /// AN UNKNOWN TOOL MUST APPEAR. This is the check that keeps this component from
    /// becoming the very defect it fixes: Claude Code ships new tools, and a classifier
    /// whose default is "say nothing" would turn every one of them back into pane silence —
    /// quietly, with no error anywhere, exactly like the dead routing ladder (CLAUDE.md §3).
    /// Over-showing a tool nobody has classified is the cheap failure; hiding it is not.
    /// </summary>
    [Fact]
    public void An_unknown_tool_is_shown_not_hidden()
    {
        var (tier, text) = Of("SomeToolShippedNextYear", """{"target":"the thing"}""");
        Assert.Equal(ProgressTier.Act, tier);
        Assert.Contains("sometoolshippednextyear", text);
        Assert.Contains("the thing", text);
    }

    /// <summary>Totality, and it is not defensive habit: this runs inside the wire pump, and
    /// an exception here would take down the pump that carries every other row.</summary>
    [Theory]
    [InlineData("Read", "{}")]
    [InlineData("Read", """{"file_path":null}""")]
    [InlineData("Bash", """{"command":""}""")]
    [InlineData("Grep", "not json at all")]
    [InlineData("TodoWrite", """{"todos":"not an array"}""")]
    [InlineData("Task", "[]")]
    public void A_malformed_input_still_answers_and_never_throws(string tool, string json)
    {
        var (_, text) = Of(tool, json);
        Assert.NotNull(text);
    }

    [Fact]
    public void An_empty_tool_name_is_noise() =>
        Assert.Equal(ProgressTier.Noise, Of("", "{}").Tier);

    // ---- Failed tool results ----------------------------------------------------------

    /// <summary>The half of the stream the pane never had. `is_error` on a tool_result is
    /// the earliest possible warning that a turn is going wrong, and it used to reach the
    /// operator — if at all — only when the turn ended.</summary>
    [Fact]
    public void A_failed_result_names_the_tool_and_the_first_line()
    {
        var line = PaneProgress.FromFailedResult("Bash", "Exit code 2\n/usr/bin/bash: unexpected EOF");
        Assert.Equal("! bash failed: Exit code 2 /usr/bin/bash: unexpected EOF", line);
    }

    /// <summary>An id whose call has aged out of the lane's bounded memory still reports.
    /// "Something failed" beats silence, which is what the old code had.</summary>
    [Fact]
    public void A_failed_result_with_no_known_tool_still_reports() =>
        Assert.StartsWith("! tool failed:", PaneProgress.FromFailedResult(null, "boom")!);

    [Fact]
    public void A_failed_result_with_nothing_in_it_is_no_row_at_all() =>
        Assert.Null(PaneProgress.FromFailedResult(null, ""));

    [Fact]
    public void A_long_failure_is_truncated()
    {
        var line = PaneProgress.FromFailedResult("Bash", new string('y', 500))!;
        Assert.True(line.Length <= 110, $"length was {line.Length}");
        Assert.EndsWith("…", line);
    }
}

/// <summary>
/// The fold — "mega condense the medium-value steps", in the operator's words. Consecutive
/// same-verb steps become one line; anything that CHANGED something keeps its own.
///
/// Folding happens at RENDER, over rows the store keeps one-per-tool-call, so these cases
/// are also the contract that makes that choice safe: the fold has to be correct over a
/// transcript it is seeing for the second time, and over rows written by an older build.
/// </summary>
public class PaneProgressFoldTests
{
    const string P = "progress";

    static List<string> Fold(params (string Kind, string Body)[] rows) =>
        PaneProgress.Fold(rows).Select(r => $"{r.Kind}|{r.Body}").ToList();

    [Fact]
    public void One_step_is_left_alone() =>
        Assert.Equal(new[] { "progress|read Sim.cs" }, Fold((P, "read Sim.cs")));

    [Fact]
    public void A_run_of_reads_becomes_one_line_with_a_count_and_a_remainder() =>
        Assert.Equal(new[] { "progress|read 6 files: a.cs, b.cs, c.cs +3" },
            Fold((P, "read a.cs"), (P, "read b.cs"), (P, "read c.cs"),
                 (P, "read d.cs"), (P, "read e.cs"), (P, "read f.cs")));

    /// <summary>An agent re-reading one file must not inflate the count it is folded into —
    /// otherwise a poll loop reads as progress.</summary>
    [Fact]
    public void Identical_subjects_collapse_rather_than_counting_twice() =>
        Assert.Equal(new[] { "progress|read 2 files: a.cs, b.cs" },
            Fold((P, "read a.cs"), (P, "read b.cs"), (P, "read a.cs"), (P, "read b.cs")));

    [Fact]
    public void Repeating_one_subject_folds_back_to_the_single_form() =>
        Assert.Equal(new[] { "progress|read a.cs" },
            Fold((P, "read a.cs"), (P, "read a.cs"), (P, "read a.cs")));

    /// <summary>Different verbs are different facts about the turn. Reading six files then
    /// grepping three patterns is two lines, not one nine-step blur.</summary>
    [Fact]
    public void Different_verbs_do_not_fold_together() =>
        Assert.Equal(new[] { "progress|read 2 files: a.cs, b.cs", "progress|grep 2 patterns: \"x\", \"y\"" },
            Fold((P, "read a.cs"), (P, "read b.cs"), (P, "grep \"x\""), (P, "grep \"y\"")));

    /// <summary>THE POINT OF THE TIERS. An edit is a thing that happened to the operator's
    /// files: it gets its own line, it breaks the run around it, and the reads either side
    /// of it stay separate — because "read four files, edited Store.cs, read one more" is
    /// the shape of the turn, and folding it flat would hide the only line that matters.</summary>
    [Fact]
    public void An_act_keeps_its_own_line_and_breaks_the_run() =>
        Assert.Equal(new[]
            {
                "progress|read 2 files: a.cs, b.cs",
                "progress|edited Store.cs",
                "progress|read c.cs",
            },
            Fold((P, "read a.cs"), (P, "read b.cs"), (P, "edited Store.cs"), (P, "read c.cs")));

    [Fact]
    public void A_command_never_folds_into_its_neighbour() =>
        Assert.Equal(new[] { "progress|$ dotnet build", "progress|$ dotnet test" },
            Fold((P, "$ dotnet build"), (P, "$ dotnet test")));

    /// <summary>A failure is never averaged into a count of steps.</summary>
    [Fact]
    public void A_failure_row_stands_alone() =>
        Assert.Equal(new[] { "progress|read a.cs", "progress|! bash failed: Exit code 2", "progress|read b.cs" },
            Fold((P, "read a.cs"), (P, "! bash failed: Exit code 2"), (P, "read b.cs")));

    /// <summary>The transcript's order is the operator's mental model of the turn; folding
    /// must never reorder it, and must never touch a row that is not a progress row.</summary>
    [Fact]
    public void Everything_else_passes_through_in_order() =>
        Assert.Equal(new[]
            {
                "user_input|make the waves taller",
                "progress|read 2 files: Sim.cs, Water.cs",
                "progress|edited Sim.cs",
                "agent_line|Raising amplitude in the shallow band.",
                "result|done: wave amplitude now depth-scaled",
            },
            Fold(("user_input", "make the waves taller"),
                 (P, "read Sim.cs"), (P, "read Water.cs"),
                 (P, "edited Sim.cs"),
                 ("agent_line", "Raising amplitude in the shallow band."),
                 ("result", "done: wave amplitude now depth-scaled")));

    /// <summary>A run separated by a non-progress row is two runs. The rows either side of a
    /// turn boundary are not one sequence of steps, whatever their verb.</summary>
    [Fact]
    public void A_turn_boundary_splits_a_run() =>
        Assert.Equal(new[] { "progress|read a.cs", "result|done", "progress|read b.cs" },
            Fold((P, "read a.cs"), ("result", "done"), (P, "read b.cs")));

    [Fact]
    public void Folding_nothing_is_nothing() => Assert.Empty(PaneProgress.Fold(Array.Empty<(string, string)>()));

    /// <summary>Idempotent, which is what makes render-time folding safe: the pane re-folds
    /// the same rows on every poll, and a fold whose output folded differently would make a
    /// tile flicker between two renderings of one truth.</summary>
    [Fact]
    public void Folding_a_folded_transcript_changes_nothing()
    {
        var once = PaneProgress.Fold(new[] { (P, "read a.cs"), (P, "read b.cs"), (P, "edited c.cs") });
        var twice = PaneProgress.Fold(once);
        Assert.Equal(once, twice);
    }

    /// <summary>A folded line still has to fit a narrow pane beside `you>` and `✓` rows.</summary>
    [Fact]
    public void A_folded_line_stays_within_the_pane_budget()
    {
        var many = Enumerable.Range(0, 40).Select(i => (P, $"read {new string('n', 30)}{i}.cs")).ToArray();
        var line = PaneProgress.Fold(many).Single().Body;
        Assert.True(line.Length <= 110, $"length was {line.Length}: {line}");
    }

    /// <summary>Rows written by an OLDER build fold by the same rule, because the key is
    /// derived from the text rather than stored beside it. A store is read by whatever build
    /// happens to be running (§13), so this is the compatibility contract.</summary>
    [Fact]
    public void An_unrecognised_verb_simply_does_not_fold() =>
        Assert.Equal(new[] { "progress|sniffed a.cs", "progress|sniffed b.cs" },
            Fold((P, "sniffed a.cs"), (P, "sniffed b.cs")));
}
