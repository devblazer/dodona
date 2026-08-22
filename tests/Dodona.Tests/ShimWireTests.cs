using Dodona;
using Dodona.Testing.Ledger;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// ══ THE SHIM WIRE PARSER, ONE LAYER DOWN (pilot slice S-WIRE, plan W5) ══
///
/// Three checks that used to need a real daemon, a real shim, a real child process and a real
/// SQLite file to reach `LaneRuntime.HandleShimLine`. They reach it directly now, through the one
/// keyword commit A widened and a <see cref="RecordingLaneSink"/>, and they assert the same
/// things their acceptance ancestors asserted.
///
/// **Each method is named after the check it replaces, character for character.** That is the
/// last-segment rule (plan 5.2): `dev ledger` resolves `moves\s-wire.tsv`'s `destination` against
/// the method name and refuses a mismatch, so a typo cannot silently orphan a name. It breaks the
/// `Sentence_case_with_underscores` habit in this project on purpose.
///
/// **Every one of these was seen RED before its ancestor was deleted**, under a checked-in defect
/// in `tests\mutants\`, and the same defect was seen to redden the ancestor. That paired red -- not
/// the equal name -- is what makes the move reviewable (plan 5.3). The literal reds are in
/// `tests\ledger\moves\s-wire.tsv`.
///
/// WHAT DID NOT MOVE, and it is written here because a reader of this file is the person most
/// likely to try: the WIRE stays. `m0:orphaned_result_landed` still kills a real daemon mid-turn
/// and demands the buffered result land exactly once through a real shim pipe. Nothing here
/// touches that, and `RecordingLaneSink` names it as the wire it does not replace.
/// </summary>
public class ShimWireTests
{
    /// <summary>A shim line is `seq &lt;TAB&gt; json`. Anything else is greeting or noise and the
    /// parser drops it before it ever parses.</summary>
    static (RecordingLaneSink Sink, long Lane, LaneRuntime Runtime) Wire()
    {
        var sink = new RecordingLaneSink();
        var lane = sink.NewLane("SKY");
        return (sink, lane, new LaneRuntime(lane, "dodona-test-lane" + lane, sink));
    }

    // ---------------------------------------------------------------- m0:session_id_recorded

    /// <summary>
    /// MOVED from `m0:session_id_recorded` (`m0-acceptance.ps1`), which read
    /// `dodona status` and matched `session=fake-`. The property is the one the OLD check was
    /// really about: the session id the agent announces in its `system/init` line is recorded
    /// against the lane, so a replacement daemon can resume that session rather than starting a
    /// new one.
    ///
    /// A `[Theory]`, because the variation is what the id LOOKS like and that is content: the
    /// fake agent's `fake-` prefix (what the suite matched on) and a real recorded UUID out of
    /// `tests\assets\wire\real\wire.jsonl` (what `claude` actually sends) must both survive.
    /// </summary>
    [Theory]
    [InlineData("fake-3f2a1c")]                                 // DodonaFakeAgent's own shape
    [InlineData("8e6542aa-9e76-4464-a4af-683458a731a5")]        // the real recorded session, wire.jsonl line 1
    public void session_id_recorded(string sessionId)
    {
        var (sink, lane, rt) = Wire();

        rt.HandleShimLine("1\t" + $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}","model":"fake-agent"}""");

        Assert.Equal(sessionId, sink.SessionOf(lane));
        // ...and the pane says so in words, which is what the operator reads. `status` was the
        // suite's instrument; the row is the thing status renders.
        var row = Assert.Single(sink.OfKind("system"));
        Assert.Equal("init session=" + sessionId, row.Body);
    }

    // ------------------------------------------ compression:midturn_narration_is_still_a_row

    /// <summary>
    /// MOVED from `compression:midturn_narration_is_still_a_row`
    /// (`compression-acceptance.ps1`), which counted
    /// `pane_events WHERE kind='agent_line' AND body LIKE 'working on:%'`.
    ///
    /// THE POINT IS THE PAIR, and half of it deliberately stays upstairs. Mid-turn narration is
    /// filtered OUT of the grid and must never be LOST -- so the row has to exist even though
    /// nothing renders it. This is the "never lost" half, and it is a pure parser question: an
    /// assistant message whose content is a text block becomes one `agent_line` row carrying the
    /// agent's own words. The "not in the pane" half is an ABSENCE asserted through a live
    /// window, and plan 2.2's second note is explicit that such an absence stays at a window --
    /// a renderer that never consulted the filter produces the same absence.
    /// </summary>
    [Fact]
    public void midturn_narration_is_still_a_row()
    {
        var (sink, lane, rt) = Wire();

        rt.HandleShimLine("1\t" + """
            {"type":"assistant","session_id":"fake-1","message":{"role":"assistant","content":[{"type":"text","text":"working on: make the water darker"}]}}
            """.Trim());

        var row = Assert.Single(sink.OfKind("agent_line"));
        Assert.Equal("working on: make the water darker", row.Body);
        Assert.Equal(lane, row.LaneId);
        // The seq is the WIRE's seq, not null: this row is the line, and UNIQUE(lane_id, seq) on
        // it is what makes a shim redelivery exactly-once.
        Assert.Equal(1L, row.Seq);
    }

    // ------------------------------------------------ compression:progress_rows_are_written

    /// <summary>
    /// MOVED from `compression:progress_rows_are_written` (`compression-acceptance.ps1`), which
    /// counted `pane_events WHERE kind='progress'` at three or more after a turn that made three
    /// tool calls.
    ///
    /// THREE, not one, and the old check's comment says why: presence is a single column that
    /// every event overwrites, so eighteen tool calls in one measured turn left two sentences on
    /// screen and no trace of the other sixteen. The row is the trace. One call would prove
    /// presence and nothing about the trace.
    ///
    /// The derived row carries `seq: null` on purpose -- the seq of the wire line belongs to the
    /// row written for the line itself, and a derived row must not compete for the key that makes
    /// redelivery exactly-once. Asserted here because it is free to assert and it is the thing a
    /// well-meaning edit would break first.
    /// </summary>
    [Fact]
    public void progress_rows_are_written()
    {
        var (sink, lane, rt) = Wire();

        var calls = new[]
        {
            ("Read", "file_path", "src/a.cs"),
            ("Read", "file_path", "src/b.cs"),
            ("Edit", "file_path", "src/c.cs"),
        };
        var seq = 0;
        foreach (var (tool, key, value) in calls)
            rt.HandleShimLine(++seq + "\t" +
                $$$"""{"type":"assistant","session_id":"fake-1","message":{"role":"assistant","content":[{"type":"tool_use","id":"t{{{seq}}}","name":"{{{tool}}}","input":{"{{{key}}}":"{{{value}}}"}}]}}""");

        var progress = sink.OfKind(PaneProgress.Kind).ToList();
        Assert.True(progress.Count >= 3, "expected 3 or more progress rows, got " + progress.Count +
                                         " -- [" + string.Join(" | ", progress.Select(p => p.Body)) + "]");
        Assert.All(progress, p => Assert.Null(p.Seq));
        Assert.All(progress, p => Assert.Equal(lane, p.LaneId));
    }
}
