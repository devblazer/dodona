using System.Reflection;
using System.Text;
using Xunit;

namespace Dodona.Testing.Ledger;

/// <summary>
/// The four facts of rung 2, written ONCE and compiled into both test projects (see
/// <see cref="DoubleLedger"/> for why a link and not a reference).
///
/// Each project's <c>DoubleLedgerTests</c> is four one-line `[Fact]`s handing this its own
/// explicit assembly list and its own project directory. Nothing here reads
/// `GetExecutingAssembly()`, and <see cref="Assemblies_are_exactly_what_is_declared"/> exists so
/// that "the list is explicit" cannot quietly become "the list is wrong".
/// </summary>
static class DoubleLedgerAssertions
{
    /// <summary>
    /// FACT 4 (added at W4, and it is the direct answer to review finding 1). Both directions:
    /// every assembly this project loads has a row naming this project as its rung-2 home, and
    /// every row naming this project as its rung-2 home is in the list this project loads.
    ///
    /// The second direction is the one that matters. Without it, deleting an entry from a test's
    /// assembly array leaves that assembly's doubles unchecked by anything except rung 1, and
    /// every remaining fact goes green over a smaller set -- which is the empty-population
    /// failure arriving one assembly at a time instead of all at once.
    /// </summary>
    public static void Assemblies_are_exactly_what_is_declared(string projectDir, params Assembly[] loaded)
    {
        var rows = CheckLedger.DoubleAssemblies;
        Assert.True(rows.Length > 0,
            "tests\\ledger\\double-assemblies.tsv resolved to no rows -- a missing artefact must never read as an empty one");

        var loadedNames = loaded.Select(a => a.GetName().Name ?? "?").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var declared = rows.Where(r => r.Rung2 == projectDir)
                           .Select(r => r.Assembly)
                           .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.True(
            loadedNames.SequenceEqual(declared, StringComparer.Ordinal),
            projectDir + " loads [" + string.Join(", ", loadedNames) + "] but double-assemblies.tsv declares its rung-2 coverage as [" +
            string.Join(", ", declared) + "]. Those must match exactly: an assembly declared and not loaded is a population " +
            "this test cannot see, and an assembly loaded and not declared is coverage nothing records.");
    }

    // SEEN RED, and by accident, which is the best kind: double-assemblies.tsv said the Dodona
    // project's assembly was named `Dodona`, and it is named `dodona` (Dodona.csproj:9).
    //
    //   tests\Dodona.Tests loads [Dodona.Tests, dodona] but double-assemblies.tsv declares its
    //   rung-2 coverage as [Dodona, Dodona.Tests]. Those must match exactly: an assembly declared
    //   and not loaded is a population this test cannot see, and an assembly loaded and not
    //   declared is coverage nothing records.

    /// <summary>
    /// FACT 1. An <see cref="Anchor.Interface"/> anchor claims the COMPILER catches shape drift,
    /// and it only does so if the interface has a second implementation that SHIPS. The count
    /// excludes every `[Double]`-carrying type, so a fake can never anchor itself (finding 2).
    ///
    /// The survivors are printed BY NAME rather than counted, so a reader can see whether the
    /// second one ships instead of trusting an integer.
    ///
    /// <c>SeamOnlyInterface</c> is the one way past it, and it is a DECLARATION rather than an
    /// exemption: an open issue number, refused by rung 1 if absent, counted separately in
    /// `dev gate`'s ledger reading. See DoubleAttribute's own comment for why the plan as
    /// written could not ship without it -- it anchors FakeRecognizer as Interface, states that
    /// IRecognizer does not qualify, and offers two remedies that are both about behaviour.
    /// </summary>
    public static void Every_Interface_anchor_has_two_shipping_implementers(params Assembly[] loaded)
    {
        var bad = new StringBuilder();
        foreach (var e in DoubleLedger.In(loaded))
        {
            if (e.Anchor != Anchor.Interface) continue;

            var shared = DoubleLedger.SharedInterfaces(e, loaded);
            if (shared.Count == 0)
            {
                bad.AppendLine(e + ": anchored Interface, but it shares no interface DECLARED IN THESE ASSEMBLIES " +
                               "with its Real (" + (e.Real.FullName ?? e.Real.Name) + ") -- there is nothing for the " +
                               "compiler to hold. A shared IDisposable does not count: nobody grows IDisposable.");
                continue;
            }

            var best = shared[0];
            var bestSurvivors = (IReadOnlyList<Type>)Array.Empty<Type>();
            foreach (var i in shared)
            {
                var s = DoubleLedger.ShippingImplementers(i, loaded);
                if (s.Count > bestSurvivors.Count) { best = i; bestSurvivors = s; }
            }

            if (!bestSurvivors.Contains(e.Real))
            {
                bad.AppendLine(e + ": Real (" + (e.Real.FullName ?? e.Real.Name) + ") is not among the shipping implementers of " +
                               (best.FullName ?? best.Name) + " -- shipping implementers: " + DoubleLedger.Names(bestSurvivors));
                continue;
            }

            if (bestSurvivors.Count >= 2) continue;
            if (e.SeamOnlyInterface > 0) continue;      // declared, ticketed, counted in the gate reading

            bad.AppendLine(e + ": " + (best.FullName ?? best.Name) + " has " + bestSurvivors.Count +
                           " shipping implementer(s) once every [Double] is excluded -- " + DoubleLedger.Names(bestSurvivors) +
                           ". An Interface anchor claims the compiler catches shape drift, and with one shipping " +
                           "implementation the interface's shape is whatever the fake finds convenient. Either point it at an " +
                           "interface production really implements twice, or declare the shortfall: SeamOnlyInterface = <open issue>.");
        }
        Assert.True(bad.Length == 0, "Interface anchors that do not hold:\r\n" + bad);
    }

    // SEEN RED TWICE (docs/TEST-ARCHITECTURE-PLAN.md W4 reds 5 and 6b; tests\ledger\README.md
    // carries both in full). Once against a throwaway interface with two fakes and one real
    // implementation, and once -- unprompted -- against the untouched tree:
    //
    //   Interface anchors that do not hold:
    //   DodonaUi.FakeRecognizer [DodonaUi]: DodonaUi.IRecognizer has 1 shipping implementer(s)
    //   once every [Double] is excluded -- DodonaUi.DeepgramRecognizer. ...
    //
    // That second one is the mechanism proving itself against code nobody wrote for it, and it is
    // what forced SeamOnlyInterface to exist: the plan anchors FakeRecognizer as Interface, states
    // that IRecognizer does not qualify, and offers two remedies that are both about behaviour.
    //
    // AND IT WAS GREEN FIRST, WRONGLY. FakeRecognizer and DeepgramRecognizer share IDisposable as
    // well as IRecognizer, and the strongest shared interface won -- so the count was satisfied by
    // something nobody grows. See DoubleLedger.SharedInterfaces.

    /// <summary>
    /// FACT 2. <see cref="Anchor.Interface"/> reaches D1 (shape) AND NOTHING ELSE -- plan 3.1's
    /// own table says so. A double that satisfies the interface and DECIDES DIFFERENTLY is
    /// invisible to it, and that is the drift that actually bites: `DeepgramRecognizer` moving
    /// when it raises `Ready` leaves `IRecognizer` unchanged, so `dev build` is silent while the
    /// live mic sits in `starting` for ever.
    ///
    /// So every Interface anchor carries a <c>Contract</c> (one body, two subjects) or a
    /// ticketed <c>KnownDivergence</c>. And a named contract is RESOLVED here, not trusted:
    /// it must exist in the test assembly, be abstract, and have at least two concrete
    /// subclasses -- one supplying the double, one supplying the real thing. A contract with one
    /// subclass is a test of the fake wearing a contract's name.
    /// </summary>
    public static void Interface_is_never_a_sole_anchor(Assembly testAssembly, params Assembly[] loaded)
    {
        var bad = new StringBuilder();
        var testTypes = DoubleLedger.Types(testAssembly);
        foreach (var e in DoubleLedger.In(loaded))
        {
            if (e.Anchor != Anchor.Interface) continue;

            var hasDivergence = !string.IsNullOrWhiteSpace(e.KnownDivergence) && e.Issue > 0;
            if (string.IsNullOrWhiteSpace(e.Contract) && !hasDivergence)
            {
                bad.AppendLine(e + ": anchored Interface alone. Interface reaches SHAPE drift and nothing else, so every " +
                               "Interface anchor needs Contract = \"<X>Contract\" (one body, two subjects) or " +
                               "KnownDivergence = \"<one sentence>\" with Issue = <open issue>.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(e.Contract)) continue;

            var contract = testTypes.FirstOrDefault(t => t.Name == e.Contract);
            if (contract is null)
            {
                bad.AppendLine(e + ": names Contract \"" + e.Contract + "\", which is no type in " +
                               (testAssembly.GetName().Name ?? "?") + ". The name is a hand copy and this is what closes it.");
                continue;
            }
            if (!contract.IsAbstract)
            {
                bad.AppendLine(e + ": Contract \"" + e.Contract + "\" is not abstract -- a contract is one body run against " +
                               "two subjects, so the body cannot live on a class that IS one of them.");
                continue;
            }
            var subs = testTypes.Where(t => !t.IsAbstract && contract.IsAssignableFrom(t)).ToArray();
            if (subs.Length < 2)
            {
                bad.AppendLine(e + ": Contract \"" + e.Contract + "\" has " + subs.Length + " concrete subclass(es) -- " +
                               DoubleLedger.Names(subs) + ". It needs two: one supplying the double, one supplying the real thing. " +
                               "With one it is a test of the fake wearing a contract's name.");
            }
        }
        Assert.True(bad.Length == 0, "Interface anchors with nothing behind them:\r\n" + bad);
    }

    // SEEN RED against the tree as it stands (W4 red 6), with FakeRecognizer carrying Anchor, Real
    // and Wire and nothing else:
    //
    //   Interface anchors with nothing behind them:
    //   DodonaUi.FakeRecognizer [DodonaUi]: anchored Interface alone. Interface reaches SHAPE drift
    //   and nothing else, so every Interface anchor needs Contract = "<X>Contract" (one body, two
    //   subjects) or KnownDivergence = "<one sentence>" with Issue = <open issue>.

    /// <summary>
    /// FACT 3. Every double names a wire it DOES NOT replace (plan 3.1's second hard rule
    /// against the self-fulfilling lookup), and that wire must still be in the register.
    ///
    /// WHAT THIS IS NOT, said here because the plan's first draft over-claimed it and its own
    /// section 5.1 refutes the over-claim four sections later: this is an ANTI-ROT CHECK on the
    /// register. A surviving name can be green while asserting something strictly weaker, and no
    /// name check can see that. `wires.tsv`'s `owner_body_sha` is the answer to the silent
    /// narrowing, it is a speed bump rather than a proof, and it is populated at W7.
    /// </summary>
    public static void Every_double_names_a_wire_that_still_exists(params Assembly[] loaded)
    {
        var bad = new StringBuilder();
        foreach (var e in DoubleLedger.In(loaded))
        {
            if (string.IsNullOrWhiteSpace(e.Wire))
            {
                bad.AppendLine(e + ": names no Wire. Every double names a wire it does not replace, so that something " +
                               "in this repository is still proved against the real machinery beside it.");
                continue;
            }
            var row = CheckLedger.ResolveWire(e.Wire);
            if (row is null)
            {
                bad.AppendLine(e + ": names Wire \"" + e.Wire + "\", which resolves to no row in tests\\ledger\\wires.tsv. " +
                               "The wire was deleted, renamed or misspelled, and this double is now standing beside nothing.");
                continue;
            }
            if (CheckLedger.BaselineChecks.Count > 0 && !CheckLedger.BaselineChecks.Contains(row.OwnerCheck))
            {
                bad.AppendLine(e + ": Wire \"" + e.Wire + "\" resolves to wire " + row.WireId + ", whose owner_check \"" +
                               row.OwnerCheck + "\" is in no baseline.tsv row -- it is not a check that has ever been " +
                               "observed to run.");
            }
        }
        Assert.True(bad.Length == 0, "doubles standing beside a wire that has moved:\r\n" + bad);
    }

    // SEEN RED (W4 red 7) by renaming wires.tsv's F9 owner_check and running this suite:
    //
    //   doubles standing beside a wire that has moved:
    //   DodonaUi.FakeRecognizer [DodonaUi]: names Wire "voice:clicking_the_mic_toggles_listening",
    //   which resolves to no row in tests\ledger\wires.tsv. The wire was deleted, renamed or
    //   misspelled, and this double is now standing beside nothing.
    //
    // The same edit reddens rung 1 from the opposite direction (a register row naming a check no
    // suite registers), and both are recorded: neither rung alone sees both halves.
}
