using System.Reflection;
using Dodona.Testing.Ledger;
using DodonaUi;
using Xunit;

namespace Dodona.Ui.Tests;

/// <summary>
/// Rung 2 of the double ledger over the <c>DodonaUi</c> assembly (net8.0-windows).
///
/// ══ THIS CLASS IS THE HALF THE FIRST DESIGN COULD NOT HAVE ══
///
/// TWO OF THE THREE DOUBLES THIS REPOSITORY ALREADY HAD LIVE IN `src\DodonaUi` --
/// `FakeRecognizer` (Recognizer.cs:73) and `Poses` (Poses.cs:9) -- and tests\Dodona.Tests is
/// net8.0, which cannot load a net8.0-windows assembly at all. The ledger's original single
/// reflection test would have enumerated a population containing neither, and gone green because
/// the set was empty. That is the routing ladder's own failure shape, in the mechanism written
/// to prevent it, and it is why W3 stood this project up.
///
/// The body of every fact is shared with tests\Dodona.Tests by `Compile Include` link, not
/// copied: two hand copies of one algorithm is what the whole plan exists to stop.
///
/// `Poses` is deliberately NOT anchored yet. It does not match the `Fake*`/`Recording*` name
/// rung 1 enforces, its contract (`Poses_cover_every_snapshot_member`) is not in W4's file list,
/// and inventing one here would be W4 quietly doing W8's work. It is a gap, and
/// tests\ledger\README.md names it as one.
/// </summary>
public class DoubleLedgerTests
{
    static Assembly[] Loaded => new[]
    {
        typeof(MainVm).Assembly,                // DodonaUi -- and never `Dodona` beside it: the
                                                // window LINKS a dozen of its files by source, so
                                                // referencing both would put two copies of every
                                                // linked type in scope.
        typeof(DoubleLedgerTests).Assembly,      // Dodona.Ui.Tests
    };

    const string ProjectDir = @"tests\Dodona.Ui.Tests";

    [Fact]
    public void The_assemblies_this_project_loads_are_exactly_the_ones_it_is_declared_to_cover() =>
        DoubleLedgerAssertions.Assemblies_are_exactly_what_is_declared(ProjectDir, Loaded);

    [Fact]
    public void Every_Interface_anchor_has_two_shipping_implementers() =>
        DoubleLedgerAssertions.Every_Interface_anchor_has_two_shipping_implementers(Loaded);

    [Fact]
    public void Interface_is_never_a_sole_anchor() =>
        DoubleLedgerAssertions.Interface_is_never_a_sole_anchor(typeof(DoubleLedgerTests).Assembly, Loaded);

    [Fact]
    public void Every_double_names_a_wire_that_still_exists() =>
        DoubleLedgerAssertions.Every_double_names_a_wire_that_still_exists(Loaded);
}
