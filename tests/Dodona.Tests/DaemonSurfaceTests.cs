using Dodona;
using Xunit;
using static Dodona.DaemonSurface;

namespace Dodona.Tests;

/// <summary>
/// ISSUE #13: which commands are worth STARTING a daemon for.
///
/// Summoning a workspace daemon runs its warm-up -- the router, the brain and the compressor
/// pool, four real `claude -p --model haiku` processes in this repo. The decision used to be
/// four literals in `Client`, keyed on the verb the operator typed, and it was right about those
/// four names and silent about the other sixty: `dodona policy`, whose handler comment reads
/// "Inspectable without spawning anything", started four model agents to print a static table.
///
/// COMPLETENESS IS NOT ASSERTED HERE, deliberately. `dev lint` reconciles the table against the
/// `case` labels in `Daemon.Commands.cs` and `Concierge.cs` -- a text scan, so it needs no build
/// and cannot miss an assembly, and it is what makes a new command impossible to add without
/// answering the question. Both directions of that check were proved red before this file was
/// written. What is left for a unit is the JUDGEMENT: that the specific commands the incident
/// was about answer the way the ticket says they must, and that the two-file lint has something
/// true to reconcile.
///
/// Pure: no daemon, no pipe, no store. That is the point of `Declared` being a lookup rather
/// than a branch inside `Client`, where every question about it needed a live workspace.
/// </summary>
public class DaemonSurfaceTests
{
    /// <summary>The six the ticket names as the worst offenders, plus the two `status` and
    /// `ticket-record` rows that were already enforcement (m0 holds those against a live
    /// daemon; this holds them against the table they now come from).</summary>
    [Theory]
    [InlineData("policy")]          // "the clearest instance" -- a static config table
    [InlineData("swaps")]           // pure read, never on the old list, nobody noticed
    [InlineData("claim-check")]
    [InlineData("questions")]
    [InlineData("repo-status")]
    [InlineData("token-status")]    // materialises a merge_token row, and is a reading by name
    [InlineData("repos")]           // the same
    [InlineData("tickets")]
    [InlineData("tail")]            // 20 pane rows out of SQLite
    [InlineData("status")]
    [InlineData("land-status")]
    [InlineData("ticket-record")]
    public void a_command_that_only_reports_starts_nothing(string wire) =>
        Assert.Equal(Summon.Never, Declared(Surface.Workspace, wire));

    /// <summary>The control that keeps the row above honest: a table returning Never for
    /// everything would pass it and would break every delivery verb in the product. CLAUDE.md
    /// 3.2 names these as summoning DELIBERATELY -- the shims have been buffering, and bringing
    /// the daemon back is exactly what the caller wants.</summary>
    [Theory]
    [InlineData("say")]
    [InlineData("input")]
    [InlineData("lane-start")]
    [InlineData("lane-respawn")]
    [InlineData("ticket-create")]
    [InlineData("land")]
    public void a_command_that_acts_summons_one(string wire) =>
        Assert.Equal(Summon.Always, Declared(Surface.Workspace, wire));

    /// <summary>
    /// THE WIRE COMMAND IS NOT THE VERB THE OPERATOR TYPED, and that gap is half of issue #13.
    /// `dodona publish` sends `swap`; against a sleeping workspace the old code read "publish",
    /// found it on no list, and woke a daemon ON THE OLD BUILD purely to have something to hand
    /// the new one to. `dodona land`'s poll sends `land-status` and needed a `neverSummon`
    /// parameter threaded through `Client` to compensate -- now deleted, because the wire name
    /// carries its own answer.
    ///
    /// Nothing to stop or swap if nothing is running: summoning first would be starting a
    /// process in order to talk to it about ending.
    /// </summary>
    [Theory]
    [InlineData("swap")]
    [InlineData("swap-answer")]
    [InlineData("stop-daemon")]
    public void there_is_nothing_to_stop_or_swap_in_a_daemon_that_is_not_running(string wire) =>
        Assert.Equal(Summon.Never, Declared(Surface.Workspace, wire));

    /// <summary>The write gate's tree question, asked from a PreToolUse hook on EVERY agent
    /// write. `GateAsk` also sets DODONA_NO_AUTOSTART=1; this is the second of two independent
    /// guards on the one path where summoning would cost four model processes per edit.</summary>
    [Fact]
    public void the_write_gate_never_starts_four_model_processes_per_edit() =>
        Assert.Equal(Summon.Never, Declared(Surface.Workspace, "tree-check"));

    /// <summary>The concierge had ONE exemption (`stop`), so `concierge-status`, `concierge-feed`,
    /// `concierge-questions` and `concierge-resolve` all summoned. A concierge is one process and
    /// no model lanes, which is why it went unnoticed and is not why it is right.</summary>
    /// <remarks>Two rows rather than one parameterised by the enum: a `public` xunit method
    /// cannot take an `internal` parameter type, and widening `Summon` so a test could name it
    /// would be the product's shape bending to the harness.</remarks>
    [Theory]
    [InlineData("status")]
    [InlineData("feed")]
    [InlineData("questions")]
    [InlineData("resolve")]                     // "walk the ladder and print the verdict as JSON"
    [InlineData("stop")]
    [InlineData("swap")]
    public void the_concierge_answers_the_same_question(string wire) =>
        Assert.Equal(Summon.Never, Declared(Surface.Concierge, wire));

    [Theory]
    [InlineData("route")]                       // resolves, WAKES the workspace, delivers the sentence
    [InlineData("answer")]
    [InlineData("review")]
    public void the_concierge_still_summons_for_what_acts(string wire) =>
        Assert.Equal(Summon.Always, Declared(Surface.Concierge, wire));

    /// <summary>
    /// FAILS CLOSED. An undeclared command starts nothing -- the safe direction, because not
    /// starting costs one refusal a person can read and starting costs four model processes
    /// nobody asked for. Null is the value both call sites test against `Summon.Always`, so a
    /// command nobody declared can never reach `Autostart`.
    ///
    /// The two surfaces are separate namespaces, and that is the second row: `route` is an act
    /// on the concierge and no command at all on a workspace daemon. A single flat table would
    /// have made one of them wrong and looked complete.
    /// </summary>
    [Fact]
    public void an_undeclared_command_starts_nothing()
    {
        Assert.Null(Declared(Surface.Workspace, "no-such-command"));
        Assert.Null(Declared(Surface.Workspace, null));
        Assert.Null(Declared(Surface.Workspace, "route"));
        Assert.Null(Declared(Surface.Concierge, "policy"));
    }

    /// <summary>The refusal has to leave a next step, or the enforcement is a new way to be
    /// stuck (CLAUDE.md 0.1): it names the command, the table half and the file whose `case`
    /// labels the lint reconciles it against.</summary>
    [Fact]
    public void the_refusal_names_the_two_files_that_have_to_agree()
    {
        var msg = Undeclared(Surface.Workspace, "no-such-command");
        Assert.Contains("no-such-command", msg);
        Assert.Contains("DaemonSurface.Ws", msg);
        Assert.Contains("Daemon.Commands.cs", msg);
        Assert.Contains("Concierge.cs", Undeclared(Surface.Concierge, "no-such-command"));
    }
}
