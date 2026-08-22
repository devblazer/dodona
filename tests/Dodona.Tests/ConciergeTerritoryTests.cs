using System;
using System.IO;
using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// WHERE THE MACHINE-WIDE STATE LIVES, and how far the resolver is allowed to look.
///
/// Both came down from `concierge-acceptance.ps1`, where each needed a real machine-global
/// concierge process. Neither question is about that process: one is a `Paths` derivation plus a
/// real `Registry` write, the other is `Fence.Roots` over member paths.
///
/// The wire stays where it was -- `concierge_answers_its_pipe` is still a separate process on a
/// real named pipe.
/// </summary>
public class ConciergeTerritoryTests
{
    /// <summary>`registry.db` is the ONE machine-wide table in the product: every workspace name,
    /// id, alias and project row. It must derive from DODONA_HOME. Nothing in the tree asserted
    /// this before the `concierge` suite did, and a change that reached for %LOCALAPPDATA%
    /// directly -- the way `Ver.BinRoot` legitimately does one file over -- would pass every suite
    /// while writing into THE OPERATOR'S REAL REGISTRY, where a test of the repo-exclusivity
    /// refusal could then refuse one of their real repos.
    ///
    /// A REGRESSION guard, not a defect guard, and the suite said so: `Paths.Registry` derives
    /// from `Paths.Home` today and nothing is broken. It is kept because the defect it watches
    /// for is a one-line edit away and silent.</summary>
    [Fact]
    public void registry_is_reported_under_dodona_home()
    {
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(Paths.Home) + Path.DirectorySeparatorChar,
                          Paths.Registry, StringComparison.OrdinalIgnoreCase);
    }

    // THE SUITE CHECK `registry_file_exists_where_it_is_reported` HAS NO METHOD HERE, AND THAT IS
    // A FINDING RATHER THAN AN OMISSION. Written the obvious way -- construct a Registry, assert
    // File.Exists(Paths.Registry) -- it came back VACUOUS under s-identity-13, the defect that
    // makes the constructor open a file somewhere else entirely. The reason is this assembly: the
    // method below opens `new Registry(Paths.Registry)` BY NAME, so that file exists however the
    // parameterless constructor misbehaves, and xunit gives no ordering that could prevent it.
    // At the unit layer part two is therefore subsumed by part three -- which asserts the file is
    // not merely present but LIVE -- and a check that cannot fail is worse than no check.
    // The suite check itself is untouched and still runs.

    /// <summary>Part three, and it is what a decoy cannot fake: the file under DODONA_HOME is the
    /// LIVE one -- it holds the workspace that was just created. A stray %LOCALAPPDATA% write
    /// leaves a file at the reported path that exists and is EMPTY, which parts one and two are
    /// both satisfied by.
    ///
    /// This is what seam S5 was opened for: the second `Registry` is a second connection to the
    /// path the product named, opened to read back what the first one wrote.</summary>
    [Fact]
    public void the_registry_under_dodona_home_is_the_live_one()
    {
        var name = "harbour-" + Guid.NewGuid().ToString("N")[..8];
        using (var live = new Registry()) live.Create(name);
        using var reader = new Registry(Paths.Registry);
        Assert.Contains(reader.All(), w => w.Name == name);
    }

    /// <summary>THE FENCE IS THE MEMBERS' PARENTS, never the members themselves. If
    /// `C:\repos\engine` is a member then `C:\repos` is a place work plausibly lives -- and
    /// `engine` is not, because a fence rooted at the member can only ever rediscover the member.
    ///
    /// The rejection this pins is in `Fence.cs`'s own header: the fence NEVER WIDENS ITSELF, and
    /// that is enforced in code rather than asked of a model in a prompt.</summary>
    [Fact]
    public void fence_is_derived_from_member_parents()
    {
        var roots = Fence.Roots(new[] { @"c:\repos\engine", @"c:\repos\tools" },
                                Array.Empty<string>(), _ => true);
        Assert.Single(roots);
        Assert.Equal(@"c:\repos", roots[0], ignoreCase: true);
        Assert.DoesNotContain(roots, r => r.EndsWith("engine", StringComparison.OrdinalIgnoreCase));
    }
}
