using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// == WHO PUBLISH SWAPS, AND WHAT A REPLY MEANT (slice S-PUBLISH, plan W8 wave 1) ==
///
/// Twelve checks that used to need three registered workspaces, two live daemons, a live
/// concierge and a real MSBuild-free `publish --exe` per question. Every one of them was asking
/// something pure: which targets does this combination of flags resolve to, what does an exit
/// code plus a reply mean, and what does a build with no stamp say about itself. They ask
/// <see cref="PublishPlan"/> and <see cref="BuildProvenance"/> directly now.
///
/// **Each method is named after the check it replaces, character for character** -- the
/// last-segment rule (plan 5.2), which `dev ledger` resolves against
/// `tests\ledger\moves\s-publish.tsv` and refuses on a mismatch. The ONE exception is
/// <see cref="named_target_is_selected"/>, whose ancestor was `named_target_is_swapped`: that
/// is a `renamed` row, not a `moved` one, and the reason is written on it.
///
/// **Every one of these was seen RED before its ancestor was deleted**, under a checked-in
/// defect in `tests\mutants\s-publish-*.patch`, and the same defect was seen to redden the
/// ancestor. That paired red -- not the equal name -- is what makes the move reviewable
/// (plan 5.3). The literal reds are in the ledger row.
///
/// WHAT DID NOT MOVE, and a reader of this file is the person most likely to try:
///
///   * **`publish:foreign_daemon_survived_untouched`** -- wire I5. A live daemon belonging to
///     ANOTHER registry is never a target. That property cannot be asserted here and it was a
///     mistake to try: <see cref="PublishPlan.Resolve"/> can only ever build a target out of a
///     row of `registered`, so no mutation of it could make a foreign id appear, and a test
///     saying so would be green forever by construction. The property lives at the CALL SITE
///     -- where `registered` comes from the registry rather than from the pipe namespace -- and
///     the fixture that can see it is a real foreign daemon that has to still be running
///     afterwards. Its old sibling `all_never_swaps_a_workspace_from_another_registry` folded
///     into that check rather than coming here.
///   * **`publish:publish_names_a_target_that_did_not_take_the_build`** -- wire I6. Silence at
///     the wire is what <see cref="PublishPlan.Judge"/> reads, and a bare named-pipe server that
///     accepts, reads and says nothing is the only thing that produces it honestly.
///   * **`publish:a_swapped_concierge_reports_the_new_build`** -- wire I7, and
///     **`publish:no_provenance_daemon_refuses_to_guess`** -- wire I9, which is this repo's
///     every-suite-runs-one-operator-path rule and must keep its real daemon.
/// </summary>
public class PublishPlanTests
{
    // A registry of three, which is what the acceptance fixture built with three real
    // `workspace-create` calls: two that will be live, and one deliberately left asleep so
    // "--all means every LIVE one" is separable from "every one".
    static readonly PublishPlan.Ws Alpha = new("alpha-1a2b", "alpha");
    static readonly PublishPlan.Ws Beta = new("beta-3c4d", "beta");
    static readonly PublishPlan.Ws Asleep = new("asleep-5e6f", "asleep");
    static readonly PublishPlan.Ws[] Registered = { Alpha, Beta, Asleep };

    const string Cx = "concierge-7a7a";

    /// <summary>A daemon that is LIVE and is in no row of <see cref="Registered"/> -- the
    /// operator's own instances, relative to a suite with its own DODONA_HOME. It is here so
    /// that every liveness answer in this file is given by something that has one, rather than
    /// by a registry lookup wearing liveness's clothes.</summary>
    const string Foreign = "foreign-9f9f";

    static bool Live(string id) => id == Alpha.Id || id == Beta.Id || id == Cx || id == Foreign;

    static PublishPlan.Ws? ByNameOrId(string n)
    {
        foreach (var w in Registered)
            if (w.Name == n || w.Id == n) return w;
        return null;
    }

    /// <summary>What the DEFAULT branch resolves to: the workspace owning what was just built.
    /// A delegate in production because resolving it can throw and can migrate a store, and a
    /// fixed value here because what this file asks is whether it is consulted at all.</summary>
    static PublishPlan.Target Owning => PublishPlan.For(Beta);

    static (List<PublishPlan.Target> Targets, string? Error, int ExitCode) Resolve(
        bool all = false, string[]? named = null, bool concierge = false) =>
        PublishPlan.Resolve(all, named ?? Array.Empty<string>(), concierge, Registered,
                            ByNameOrId, Live, Cx, () => Owning);

    static string Labels((List<PublishPlan.Target> Targets, string? Error, int ExitCode) r) =>
        string.Join(" | ", r.Targets.Select(t => t.Label + " on " + t.Pipe));

    // ---------------------------------------------------------------- --workspace <name>

    /// <summary>
    /// RENAMED from `publish:named_target_is_swapped`, and the rename is the finding rather
    /// than a tidy-up. That check matched `swapping alpha` in publish's output -- a line printed
    /// BEFORE the call, which the code's own comment calls "a statement of INTENT". So what it
    /// asserted was never that alpha took the build; it was that alpha was SELECTED. Issue #9 is
    /// exactly what an assertion on that line cannot see, and a name promising the outcome over
    /// an assertion about the intent is the shape that let it hide for two days.
    ///
    /// The assertion is unchanged. The name now says what it does.
    ///
    /// PRESENCE, not exclusivity, because that is what the ancestor asserted: it matched
    /// `swapping alpha` in a stream of output, which cannot see whether anything else was
    /// targeted too. The exclusivity half is <see cref="unnamed_workspace_is_untouched"/> and
    /// <see cref="asleep_workspace_is_untouched"/>, and keeping the split is what lets a defect
    /// that ADDS targets redden those two while this one correctly survives -- which is exactly
    /// what `s-publish-05.patch` declares.
    /// </summary>
    [Fact]
    public void named_target_is_selected()
    {
        var r = Resolve(named: new[] { "alpha" });
        Assert.Null(r.Error);
        var alpha = Assert.Single(r.Targets, t => t.Label == "alpha (alpha-1a2b)");
        Assert.Equal("dodona-alpha-1a2b-ctl", alpha.Pipe);
    }

    [Fact]
    public void unnamed_workspace_is_untouched()
    {
        var r = Resolve(named: new[] { "alpha" });
        Assert.DoesNotContain("beta", Labels(r));
    }

    [Fact]
    public void asleep_workspace_is_untouched()
    {
        var r = Resolve(named: new[] { "alpha" });
        Assert.DoesNotContain("asleep", Labels(r));
    }

    [Fact]
    public void unknown_target_is_refused()
    {
        var r = Resolve(named: new[] { "no-such-workspace" });
        Assert.Equal(2, r.ExitCode);
        Assert.NotNull(r.Error);
        Assert.Contains("no workspace \"no-such-workspace\"", r.Error);
    }

    /// <summary>The refusal comes before anything is swapped, which is why the bad name is put
    /// SECOND here: a resolution that refused only after collecting a good target would leave a
    /// publish that swapped some things and then failed. The acceptance ancestor could only ask
    /// the weaker version (one bad name, no `swapping` line anywhere), because a real publish
    /// with a good target in it would have swapped a real daemon.</summary>
    [Fact]
    public void refusal_swapped_nothing()
    {
        var r = Resolve(named: new[] { "alpha", "no-such-workspace" });
        Assert.NotNull(r.Error);
        Assert.Empty(r.Targets);
    }

    // ------------------------------------------------------------------------------ --all

    [Fact]
    public void all_reaches_every_live_workspace()
    {
        var labels = Resolve(all: true).Targets.Select(t => t.Label).ToList();
        Assert.Contains("alpha (alpha-1a2b)", labels);
        Assert.Contains("beta (beta-3c4d)", labels);
    }

    [Fact]
    public void all_skips_a_workspace_with_no_daemon()
    {
        Assert.DoesNotContain("asleep", Labels(Resolve(all: true)));
    }

    /// <summary>The concierge has no workspace of its own, so no registry row carries it and
    /// nothing but this line puts it in `--all`. It ran a two-day-old build across many
    /// publishes (issue #9), and the half of that incident which lived HERE is that a publish
    /// which says "everything" has to mean the machine-global process too.</summary>
    [Fact]
    public void all_includes_the_concierge()
    {
        Assert.Contains("concierge", Resolve(all: true).Targets.Select(t => t.Label));
    }

    // -------------------------------------------------------------- neither flag: the owner

    /// <summary>
    /// Two assertions, and the acceptance ancestor made three. It ran `publish --root <beta>`
    /// and demanded `swapping beta` and no `swapping alpha`, which is (1) the default branch
    /// consults nothing but the owning workspace and (2) `--root <path>` resolves to the
    /// workspace that owns that path. Only (1) is a decision this function makes; (2) is
    /// `WorkspaceResolve`'s, it is asserted in the `workspace` suite, and it is S-IDENTITY's
    /// seam rather than this slice's. Recorded in the ledger row, not left to be noticed.
    /// </summary>
    [Fact]
    public void default_target_is_the_owning_workspace()
    {
        var r = Resolve();                       // neither --all nor --workspace
        var only = Assert.Single(r.Targets);
        Assert.Equal(Owning.Label, only.Label);
        Assert.DoesNotContain("alpha", Labels(r));    // the registry is not consulted at all
    }

    // ----------------------------------------------------------------- what a reply meant

    /// <summary>
    /// ISSUE #9. `Client` returns 0 for a target that connects, reads the request and answers
    /// NOTHING, because silence carries no `error:` line -- so at the wire it is
    /// indistinguishable from success. The promotion of that silence to exit 1 is the whole fix
    /// and it is this row.
    ///
    /// The other three rows are the controls that keep the first one honest: a real refusal
    /// stays a refusal, `armed` still counts as taken (a daemon read the binary and committed to
    /// it), and an ordinary reply is an ordinary success. A `Judge` that failed everything would
    /// pass the silence row and be worthless.
    /// </summary>
    [Theory]
    [InlineData(2, new[] { "error: schema would migrate" }, 2, true, "DID NOT SWAP (exit 2)")]
    [InlineData(0, new string[0], 1, true, "ANSWERED NOTHING")]
    [InlineData(0, new[] { "armed: a turn is in flight" }, 0, false, "armed")]
    [InlineData(0, new[] { "swapped to 20260822-101500" }, 0, false, "took this build")]
    public void publish_fails_when_a_target_did_not_take_the_build(
        int wireCode, string[] reply, int code, bool stillOld, string text)
    {
        var v = PublishPlan.Judge(wireCode, reply);
        Assert.Equal(code, v.Code);
        Assert.Equal(stillOld, v.StillOld);
        Assert.Contains(text, v.Text);
    }

    // ------------------------------------------------------------------------ provenance

    /// <summary>
    /// A `publish --exe <prebuilt>` compiled nothing, so the binary must claim NOTHING about a
    /// commit -- and must SAY so rather than degrading into a plausible answer, which is the
    /// shape of the bug that produced 64 auto-publishes in one afternoon.
    ///
    /// The second half is the distinction the acceptance ancestor named and could only reach
    /// through a real binary: the .NET SDK writes a bare commit SHA into
    /// `AssemblyInformationalVersion` all by itself, so "the version string carries a SHA" would
    /// NOT be a valid test of provenance. Only Dodona's own named keys count.
    ///
    /// (The ancestor's comment cited a `c=` marker inside a combined informational-version
    /// string. That marker no longer exists -- P2.3/P2.4 replaced the combined string with four
    /// named `AssemblyMetadata` keys, after the dotnet CLI was found splitting a `-p:k=v` value
    /// on commas. The property survived the redesign; the comment did not, and is reported as a
    /// stale citation rather than silently carried down here.)
    /// </summary>
    [Fact]
    public void prebuilt_publish_claims_no_provenance()
    {
        var none = BuildProvenance.Read(_ => "");
        Assert.Equal("", none.Commit);
        Assert.True(none.None);
        Assert.False(none.IsTrial);              // unknown is not a claim
        Assert.Equal("", none.Text);
        Assert.Contains("build=unknown", none.Line);

        var sdkOnly = BuildProvenance.Read(k => k == "SourceRevisionId" ? new string('a', 40) : "");
        Assert.True(sdkOnly.None);
        Assert.False(sdkOnly.IsTrial);
        Assert.Contains("build=unknown", sdkOnly.Line);
    }
}
