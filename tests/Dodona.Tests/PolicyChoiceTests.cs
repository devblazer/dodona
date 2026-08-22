using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// The three model/effort choices the `ui-grid` suite used to ask a CHILD PROCESS for.
///
/// Moved here by slice S-POLLER (docs/TEST-ARCHITECTURE-PLAN.md W8). They were in a WINDOW
/// suite and never touched the window: each shelled out to `dodona policy "<sentence>"` and
/// matched the first two words of its output, so a suite whose whole cost is process starts
/// and layout passes was paying for three more process starts to ask a pure function three
/// questions. `survey-window.md` marks all three **KNOWN DUPLICATION** for exactly that reason.
///
/// WHY THEY ARE NOT `obsolete / duplicate-of`, which is what they look like. That disposition
/// needs the survivor to be a NAMED check a suite still registers (plan 5.4.1, and `dev ledger`
/// resolves it against the live suites); the elsewhere here is `PolicyTests` in
/// PureLogicTests.cs, which is a unit method, so the rule does not admit it. They are `moved`,
/// which keeps every name and costs milliseconds -- and the overlap with `PolicyTests` is
/// deliberate and recorded rather than tidied away, because deleting a name is the one thing
/// this migration may not do on judgement.
///
/// WHAT DID NOT COME WITH THEM: the `dodona policy` VERB. Rendering a choice as
/// `<model> <effort>` on stdout is what `ui-grid:policy_table_is_inspectable` asserts, and that
/// check stays exactly where it is.
///
/// All three proved by `tests/mutants/s-poller-03.patch` -- one defect, the rule-match predicate
/// inverted, reddening the old checks and these three together.
/// </summary>
public class PolicyChoiceTests
{
    /// <summary>**Moved from `ui-grid:policy_picks_cheap_for_mechanical`**
    /// (ui-grid-acceptance.ps1:307, deleted in the same commit), which asserted
    /// `(dodona policy "fix the spelling in the readme") -match '^haiku low'`.
    /// ORCHESTRATOR-DESIGN sec 9: mechanical work runs cheap and low, because quota is the
    /// scarce resource (CLAUDE.md sec 0.1) and a spelling fix must never cost an opus turn.</summary>
    [Fact]
    public void policy_picks_cheap_for_mechanical()
    {
        var c = Policy.Resolve("fix the spelling in the readme", Policy.Default, "opus", "high");
        Assert.Equal(("haiku", "low", "mechanical"), (c.Model, c.Effort, c.Why));
        Assert.False(c.Overridden);
    }

    /// <summary>**Moved from `ui-grid:policy_picks_max_for_design`**
    /// (ui-grid-acceptance.ps1:308, deleted in the same commit), which asserted
    /// `(dodona policy "redesign the schema") -match '^opus max'`. The other end of the same
    /// table: design-tier work is where the expensive model earns its keep.</summary>
    [Fact]
    public void policy_picks_max_for_design()
    {
        var c = Policy.Resolve("redesign the schema", Policy.Default, "opus", "high");
        Assert.Equal(("opus", "max", "design-tier"), (c.Model, c.Effort, c.Why));
        Assert.False(c.Overridden);
    }

    /// <summary>**Moved from `ui-grid:policy_default_is_opus_high`**
    /// (ui-grid-acceptance.ps1:309, deleted in the same commit), which asserted
    /// `(dodona policy "make the toolbar collapsible") -match '^opus high'`. Anything the
    /// short table does not recognise falls to the default, and the table is deliberately
    /// short "because a long table nobody can predict is worse than a default everybody
    /// can" (Policy.cs's own comment).</summary>
    [Fact]
    public void policy_default_is_opus_high()
    {
        var c = Policy.Resolve("make the toolbar collapsible", Policy.Default, "opus", "high");
        Assert.Equal(("opus", "high", "default"), (c.Model, c.Effort, c.Why));
        Assert.False(c.Overridden);
    }
}
