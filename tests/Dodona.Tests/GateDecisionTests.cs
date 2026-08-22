using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// == THE WRITE GATE'S VERDICT ON ARGUMENTS AND INPUT IT CANNOT READ (slice S-GATE, plan W8
/// wave 2) ==
///
/// Five checks that each started a real `dodona gate-hook` subprocess, and in three cases a
/// real daemon and a real ctl pipe, to ask a question that is pure over (laneArg, ticketArg,
/// stdin, the tree answer). They ask <see cref="GateDecision.Decide"/> directly now.
///
/// **Each method is named after the check it replaces, character for character** -- the
/// last-segment rule (plan 5.2), which `dev ledger` resolves against
/// `tests\ledger\moves\s-gate.tsv` and refuses on a mismatch.
///
/// **Every one of these was seen RED before its ancestor was deleted**, under a checked-in
/// defect in `tests\mutants\s-gate-0*.patch`, and the same defect was seen to redden the
/// ancestor. That paired red -- not the equal name -- is what makes the move reviewable
/// (plan 5.3). The literal reds are in the ledger rows.
///
/// WHAT DID NOT MOVE, AND MUST NOT, because a reader of this file is the person most likely to
/// try. TEST-ARCHITECTURE-PLAN 3.5 lists `GateHook`'s stdin among the nine things that are
/// never faked, and the reason is measured rather than stylistic: `Console.In` hands a leading
/// U+FEFF back as an ordinary character and PS 5.1 writes BOMs by default, and that pair made
/// the claim gate FAIL OPEN on every run while looking green (2026-08-19). A `Func` returning a
/// string cannot reproduce a producer's encoding.
///
///   * **wire B1** (`m2:the_lane_ends_up_in_a_worktree`) pipes real bytes -- including a real
///     UTF-8 BOM -- into the real exe, and is the only thing that can. It stays;
///   * **`m1:gate_denies_a_ticket_lane_writing_its_claim_in_the_shared_checkout`** drives the
///     command out of the deployed `gate-lane&lt;N&gt;.json` end to end: DeployGate's file, a
///     real subprocess, the ctl pipe, a verdict. It stays;
///   * **the two plain-lane tree checks** (`gate_denies_a_plain_lane_writing_the_shared_checkout`,
///     `gate_tells_the_two_trees_apart_for_a_plain_lane`) and the two allows either side of
///     them are the real discrimination through the real daemon. What `Decide` decides is what
///     to do with the ANSWER; what the answer IS belongs to `Trees.Locate` and the daemon.
///     They stay, and they are not disposed of here.
///
/// So this file is deliberately the argument-and-input half, not the tree half.
/// </summary>
public class GateDecisionTests
{
    /// <summary>stdin that would parse and would name a path, so that nothing in a test is
    /// decided by the payload unless the test is about the payload.</summary>
    const string GoodPayload = """{"tool_name":"Write","tool_input":{"file_path":"C:\\wt\\t1\\a.cs"}}""";

    /// <summary>A tree answer that says "inside a worktree": exit 0, nothing to say.</summary>
    static (int, string) Inside(long lane, string path) => (0, "");

    /// <summary>A tree answer that says "the shared checkout": exit 1 and the daemon's
    /// refusal. The TEXT is the daemon's, not this function's, which is why a test here
    /// asserts that it is passed through rather than what it says.</summary>
    static (int, string) Shared(long lane, string path) => (1, "denied: SHARED CHECKOUT");

    /// <summary>The tree question, wrapped so a test can ask whether it was asked at all. The
    /// gate's whole guarantee is a statement about this: an allow that never called it is an
    /// allow that never placed the write in a worktree.</summary>
    sealed class TreeProbe
    {
        readonly Func<long, string, (int, string)> _answer;
        internal int Calls;
        internal TreeProbe(Func<long, string, (int, string)> answer) => _answer = answer;
        internal (int, string) Ask(long lane, string path) { Calls++; return _answer(lane, path); }
    }

    // ---- the arguments the gate cannot read ------------------------------------------------

    /// <summary>
    /// R3's hole, and the reason it is asserted on the ARGUMENT rather than on the parsed
    /// number: `--lane not-a-number` parses to 0, and the pre-R3 guard asked
    /// `if (lane &lt;= 0 &amp;&amp; ticket &lt;= 0) return 0`, so this input took the
    /// no-gate-was-deployed early return and ALLOWED the write silently -- before ever reaching
    /// the deny written for exactly it.
    ///
    /// The ancestor drove a real subprocess for this and reached no daemon at all
    /// (`survey-delivery.md`: "no daemon involved at all -- this one never reaches GateAsk"),
    /// which is what makes it a pure decision with a process bill attached.
    /// </summary>
    [Fact]
    public void the_gate_denies_a_lane_argument_it_cannot_read()
    {
        var tree = new TreeProbe(Inside);
        var o = GateDecision.Decide("not-a-number", null, () => GoodPayload, tree.Ask);

        Assert.Equal(GateDecision.Verdict.Deny, o.Verdict);
        // ...and it refused BEFORE asking anything, which is the half a subprocess could not
        // see: the ancestor could only observe that a deny came out, never that the gate had
        // declined to guess rather than asked and been told.
        Assert.Equal(0, tree.Calls);
        // A deny is not a trace. Nothing was let through, so nothing is recorded as let through
        // -- the distinction the bypass log's marker got wrong until 2026-08-22.
        Assert.Null(o.Unchecked);
    }

    /// <summary>
    /// The refusal has to say whose fault it is. An agent cannot fix a Dodona misconfiguration
    /// and must not spend a turn trying (CLAUDE.md 0.3: name the real cause), and a lane has no
    /// channel to ask a human what happened (CLAUDE.md 7).
    /// </summary>
    [Fact]
    public void the_misconfiguration_refusal_says_it_is_not_the_agents_mistake()
    {
        var o = GateDecision.Decide("not-a-number", null, () => GoodPayload, Inside);

        Assert.Equal(GateDecision.Verdict.Deny, o.Verdict);
        Assert.Contains("misconfiguration", o.DenyReason!);
        // The ancestor matched the word alone against wrapped native stderr. Two more things
        // are asserted here because they are free once the string is in hand and they are what
        // the sentence is FOR: the bad argument is quoted back, so the reporter knows what to
        // report, and the outcome is stated so the agent knows the write did not land.
        Assert.Contains("not-a-number", o.DenyReason!);
        Assert.Contains("refused rather than allowed unchecked", o.DenyReason!);
    }

    /// <summary>
    /// Issue #4's hole, one argument along from R3's. A READABLE lane with an unreadable
    /// `--ticket` returned an unchecked ALLOW and never asked the tree question at all -- so
    /// `--lane 5 --ticket abc` wrote into the operator's live checkout. It survived R3 because
    /// the ticket number stopped being load-bearing the moment D-R5 deleted the claim question,
    /// and nobody re-read the branch; `DeployGate` only ever writes a numeric `--ticket`, so
    /// nothing reached it, which is precisely why it sat there unread for a day.
    ///
    /// The ancestor asserted a deny came out of a subprocess pointed at the shared checkout.
    /// This asserts the same verdict AND the thing the deny was standing in for: the tree
    /// question was actually asked, with the lane it was asked about.
    /// </summary>
    [Fact]
    public void the_gate_still_checks_the_tree_when_the_ticket_argument_is_unreadable()
    {
        var asked = new List<(long, string)>();
        var o = GateDecision.Decide("7", "not-a-number", () => GoodPayload,
            (lane, path) => { asked.Add((lane, path)); return Shared(lane, path); });

        Assert.Equal(GateDecision.Verdict.Deny, o.Verdict);
        Assert.Equal(new[] { (7L, @"C:\wt\t1\a.cs") }, asked);
        // The daemon's own refusal is passed through rather than replaced, which is what makes
        // the message actionable: it names the tree and where to write instead.
        Assert.Equal("denied: SHARED CHECKOUT", o.DenyReason);
    }

    /// <summary>
    /// ...and it does not do that by calling the write a fail-open. The trace line means "a
    /// write was not checked"; nothing is being let through here, so there must be no trace at
    /// all -- while the misconfiguration itself still has to be said out loud, or a bad
    /// `--ticket` becomes the silent degrade CLAUDE.md 0.1 forbids.
    ///
    /// The ancestor asserted this by stripping ALL whitespace out of captured native stderr and
    /// matching two substrings, because a wrapped console breaks a phrase at a column rather
    /// than at a word (CLAUDE.md 0.2). The distinction it was really making is structural --
    /// a NOTE was emitted and a TRACE was not -- and it is two fields here.
    /// </summary>
    [Fact]
    public void the_unreadable_ticket_is_reported_without_claiming_a_fail_open()
    {
        var o = GateDecision.Decide("7", "not-a-number", () => GoodPayload, Inside);

        Assert.NotNull(o.Note);
        Assert.Contains("--ticket 'not-a-number' is not a number", o.Note!);
        Assert.Contains("misconfiguration", o.Note!);
        Assert.Null(o.Unchecked);
        // And the note changed no verdict: the lane is readable and the tree answered, so the
        // write is allowed on the tree's authority and not in spite of the bad argument.
        Assert.Equal(GateDecision.Verdict.Allow, o.Verdict);
    }

    // ---- input the gate cannot read --------------------------------------------------------

    /// <summary>
    /// A RECORDED RATIONALE, REVERSED. This check's ancestor was
    /// `a_fail_open_does_not_block_the_write`, asserting the opposite, with its reason beside
    /// it: layer 1 failing closed would strand a lane that has no way to ask a human for
    /// permission, and the merge-time diff backstop is what catches what slips through.
    ///
    /// That was correct for the gate it was written about -- one that lived only inside ticket
    /// worktrees, where a fail-open let a write slip to a backstop that caught it before
    /// anything could land. Layer 1 changed what is behind the fail-open: nothing. A write
    /// allowed into the SHARED CHECKOUT is in the operator's live tree next to their
    /// uncommitted work, and no backstop sees it -- it was never going to be merged, it is
    /// already there. (The backstop is retired outright now, D-R5/R3.)
    ///
    /// So unparseable stdin with a readable lane DENIES, and the trace beside it is a trace and
    /// not a verdict.
    /// </summary>
    [Fact]
    public void unreadable_input_is_refused_not_allowed_into_the_live_tree()
    {
        var tree = new TreeProbe(Inside);
        var o = GateDecision.Decide("7", null, () => "this is not json at all", tree.Ask);

        Assert.Equal(GateDecision.Verdict.Deny, o.Verdict);
        Assert.Contains("did not parse", o.DenyReason!);
        // The tree was never asked, because there is no path to ask about -- so the refusal is
        // the gate declining to guess, which is the only safe answer when it cannot tell.
        Assert.Equal(0, tree.Calls);
        // The trace is still written: "a fail-open must leave a trace" (operator decision
        // 2026-08-19) generalised to "anything the gate could not check leaves one", and R3's
        // byte count is in it because an empty-or-truncated stdin under load was the one
        // remaining explanation for a hook that produced no verdict and left nothing to read.
        Assert.NotNull(o.Unchecked);
        Assert.Contains("23 bytes", o.Unchecked!);
    }

    // ---- the property the whole file exists for --------------------------------------------

    /// <summary>
    /// GROWTH, not a moved name: nothing in `baseline.tsv` asserted this, and settling it the
    /// last two times meant a person reading every `return` in the function by hand.
    ///
    /// THE GUARANTEE (CLAUDE.md 7, docs/WORK-ISOLATION-PLAN.md 3): no agent writes into a
    /// project outside a worktree, and the gate fails CLOSED -- an unreadable argument,
    /// unparseable stdin, a path it cannot find or a daemon that does not answer all DENY. The
    /// exact form of that, and the only form an instrument can hold: **every ALLOW is either a
    /// write the tree question positively placed inside a worktree, or an invocation no gate
    /// was ever deployed for.**
    ///
    /// It has twice been asserted in prose and been false. R3 closed the `--lane` hole and left
    /// its `--ticket` sibling open for a day, while CLAUDE.md, `GateHook`'s own header comment
    /// and issue #4 disagreed about whether any hole remained -- and the code was what was
    /// wrong, not the doc. The lesson recorded then was: if you assert this property again,
    /// assert it by ENUMERATING every return. This enumerates the input matrix instead, which
    /// is the same enumeration from the other side and does not go stale when a return moves.
    ///
    /// A new exit that allows without asking reddens this. That is the whole point of it.
    /// </summary>
    [Fact]
    public void every_allow_asked_the_tree_or_had_no_gate_deployed()
    {
        string?[] lanes = { null, "", "7", "0", "-1", "not-a-number", "7x" };
        string?[] tickets = { null, "", "3", "0", "not-a-number" };
        Func<string>[] stdins =
        {
            () => GoodPayload,
            () => "\uFEFF" + GoodPayload,                                  // the 2026-08-19 BOM
            () => """{"tool_name":"NotebookEdit","tool_input":{"notebook_path":"C:\\wt\\n.ipynb"}}""",
            () => "this is not json at all",
            () => "",
            () => """{"tool_name":"Bash","tool_input":{"command":"ls"}}""", // no path at all
            () => throw new IOException("the pipe went away"),
        };
        // Every answer a daemon can give, including the ones that are not a verdict: 2 is what
        // GateAsk returns when the call THREW, and a down daemon is the case the gate must
        // refuse rather than wave through (a shim is buffering the lane's output into it, so
        // refusing costs a message and not work).
        (int Code, string Reply)[] answers = { (0, ""), (1, "denied: SHARED CHECKOUT"), (2, "no daemon"), (7, "") };

        var allowed = 0;
        var deployed = 0;
        foreach (var lane in lanes)
            foreach (var ticket in tickets)
                foreach (var stdin in stdins)
                    foreach (var answer in answers)
                    {
                        var tree = new TreeProbe((_, _) => (answer.Code, answer.Reply));
                        var o = GateDecision.Decide(lane, ticket, stdin, tree.Ask);
                        var noGateDeployed = string.IsNullOrEmpty(lane) && string.IsNullOrEmpty(ticket);
                        if (!noGateDeployed) deployed++;
                        if (o.Verdict != GateDecision.Verdict.Allow) continue;
                        allowed++;
                        if (noGateDeployed) continue;
                        Assert.True(
                            tree.Calls == 1 && answer.Code == 0,
                            $"ALLOW without a satisfied tree question: lane={lane ?? "<null>"} " +
                            $"ticket={ticket ?? "<null>"} treeCalls={tree.Calls} treeExit={answer.Code}. " +
                            "That is layer 1 undone -- an agent writing into the operator's live checkout " +
                            "(CLAUDE.md 7). If a new exit was added, it must ask the tree or deny.");
                    }

        // A guard on the guard: if a refactor made every combination deny, the loop above would
        // pass while asserting nothing (CLAUDE.md 0.3, believing a green check). Both numbers
        // are stated so the enumeration cannot silently become vacuous in either direction.
        Assert.Equal((7 * 5 - 4) * 7 * 4, deployed);   // 4 of the 35 argument pairs are the no-gate case
        Assert.True(allowed > 0, "no combination allowed anything, so this proved nothing");
    }

    // ---- the other half of S11: the argument ladder ----------------------------------------

    /// <summary>
    /// GROWTH. `Cli.ParseArgs` was already static and already pure and was still unreachable
    /// from a test, because a top-level local function is compiled into the synthesised entry
    /// point. Its own first comment carries the incident: `orphans` was missing from
    /// `boolFlags`, so a `--flag` not in that set registers only when another argument follows
    /// it -- and as the LAST WORD on the line it fell through to the positional list instead.
    /// `stop-all --orphans` therefore did nothing at all, for as long as the flag existed, and
    /// the only way anyone found out was by running it. Reading the code that prints the
    /// message would never have shown it.
    /// </summary>
    [Fact]
    public void a_declared_valueless_flag_registers_as_the_last_word_on_the_line()
    {
        var (cmd, _, _, opts, pos) = Cli.ParseArgs(new[] { "stop-all", "--lanes", "--orphans" });

        Assert.Equal("stop-all", cmd);
        Assert.True(opts.ContainsKey("orphans"), "--orphans fell through to the positional list");
        Assert.True(opts.ContainsKey("lanes"));
        Assert.Empty(pos);
    }

    /// <summary>
    /// GROWTH. `--root` NAMES a path; `--adopt` is what turns naming one into taking it on
    /// (issue #12). A session answering the innocent question "is Dodona running anything in
    /// MassWorks?" registered MassWorks as a workspace by pointing a read-only-sounding command
    /// at it, and the operator's answer became a standing directive (CLAUDE.md 0.1). The flags
    /// may be written in either order and must mean the same thing, which is why the promotion
    /// is resolved after the parse loop and not inside it.
    /// </summary>
    [Fact]
    public void adopt_promotes_a_named_root_to_explicit_in_either_order()
    {
        // Both orders in one body rather than two [InlineData] rows: an array attribute
        // argument is not a constant expression, and the two orders are one assertion anyway --
        // the claim IS that they cannot differ.
        foreach (var argv in new[]
                 {
                     new[] { "status", "--root", @"C:\p", "--adopt" },
                     new[] { "status", "--adopt", "--root", @"C:\p" },
                 })
        {
            var (_, root, source, _, _) = Cli.ParseArgs(argv);
            Assert.Equal(@"C:\p", root);
            Assert.Equal(PathSource.Explicit, source);
        }

        // ...and without it a typed --root stays NAMED, which is the whole distinction.
        var (_, _, named, _, _) = Cli.ParseArgs(new[] { "status", "--root", @"C:\p" });
        Assert.Equal(PathSource.Named, named);
    }
}
