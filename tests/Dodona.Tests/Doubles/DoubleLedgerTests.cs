using System.Reflection;
using Dodona;
using Xunit;

namespace Dodona.Testing.Ledger;

/// <summary>
/// Rung 2 of the double ledger over the <c>Dodona</c> assembly (net8.0).
///
/// ══ THE ASSEMBLY LIST IS EXPLICIT, AND IT IS TWO LINES LONG ON PURPOSE ══
///
/// `typeof(Store).Assembly` is `Dodona`; `typeof(DoubleLedgerTests).Assembly` is `Dodona.Tests`,
/// whose own fixtures and contract classes may carry `[Double]` too. What it CANNOT reach is
/// `DodonaUi` -- a net8.0 assembly cannot load a net8.0-windows one at all -- which is exactly
/// the fact the first design of this mechanism did not survive: it reflected from here and
/// enumerated a population containing none of the three doubles that already existed.
/// tests\Dodona.Ui.Tests\DoubleLedgerTests.cs is the other half, and
/// tests\ledger\double-assemblies.tsv is what makes the pair complete rather than assumed.
///
/// ══ TODAY THIS SIDE FINDS NOTHING, AND THAT IS RECORDED RATHER THAN HIDDEN ══
///
/// `Dodona` carries no `[Double]` yet -- `RecordingLaneSink` arrives with the pilot slice (W5).
/// Three of the four facts below are therefore vacuously true here today, which would be the
/// empty-set failure all over again if nothing noticed. Two things notice:
/// <see cref="The_assemblies_this_project_loads_are_exactly_the_ones_it_is_declared_to_cover"/>
/// fails if this list ever stops matching the register, and rung 1 -- a TEXT scan that cannot
/// miss an assembly -- refuses any `[Double]` in a project with no rung-2 row at all.
/// </summary>
public class DoubleLedgerTests
{
    static Assembly[] Loaded => new[]
    {
        typeof(Store).Assembly,                 // Dodona
        typeof(DoubleLedgerTests).Assembly,     // Dodona.Tests
    };

    const string ProjectDir = @"tests\Dodona.Tests";

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
