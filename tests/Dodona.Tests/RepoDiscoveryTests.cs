using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// WHAT DISCOVERY CALLS A REPOSITORY, which is a naming rule and not a cosmetic one.
///
/// `RepoRef.Name` is recomputed by `Repos.Discover` on EVERY call and the rule changes with
/// member count, while `tickets.repo` is written once. That is the P0.1 incident, in the
/// `workspace` suite's own words: one project that is a repo is named ".", attaching a second
/// renames the same repository to its leaf, and the pre-existing ticket then asked for merge
/// token "." while a new ticket in the SAME repository asked for token "&lt;leaf&gt;" -- two rows
/// over one main, two agents each told "granted", both able to fast-forward the same branch.
/// The suite's banner says a red here is a correctness incident rather than a test problem, and
/// that has not changed by coming down a layer.
///
/// Seam S10 is what makes these free: the tree is a set of strings and `Repos.Under` /
/// `Repos.Discover` take the probes as arguments (`docs/testarch/seams.md`, the `Trees.Locate`
/// shape). The suite was paying a `git init` plus a commit per repository to ask a question about
/// string prefixes.
/// </summary>
public class RepoDiscoveryTests
{
    /// <summary>Which folders under a member are repositories -- and, just as much, which are
    /// NOT. `docs` is a perfectly good folder with no `.git` in it and it must never appear.</summary>
    [Fact]
    public void discovers_repos()
    {
        var t = new TreeSpec()
            .Repo(@"c:\ws\root\engine")
            .Repo(@"c:\ws\root\tools")
            .Folder(@"c:\ws\root\docs");
        var names = TreeSpec.Names(t.Under(@"c:\ws\root"));
        Assert.Equal(new[] { "engine", "tools" }, names);
    }

    /// <summary>A worktree's and a submodule's `.git` is a FILE, not a directory, and
    /// `LooksLikeRepo` has always accepted both. Kept beside the row above so the file arm cannot
    /// rot unnoticed: this is coverage the suite never had, declared in added.tsv.</summary>
    [Fact]
    public void A_git_FILE_is_a_repository_too()
    {
        var t = new TreeSpec().RepoWithGitFile(@"c:\ws\root\linked");
        Assert.Equal(new[] { "linked" }, TreeSpec.Names(t.Under(@"c:\ws\root")));
    }

    /// <summary>THE DEGENERATE CASE, and the one every pre-workspace claim, ticket and gate
    /// prefix in the repo depends on: a lone member that IS a repository is called ".", not "" and
    /// not its own leaf. `Claims.Prefix(".")` is empty, which is what makes a one-project
    /// workspace byte-for-byte what it always was.</summary>
    [Fact]
    public void a_lone_project_that_is_a_repo_is_still_named_dot()
    {
        var t = new TreeSpec().Repo(@"c:\ws\drift-a");
        var repos = t.Discover(@"c:\ws\drift-a");
        Assert.Equal(new[] { "." }, TreeSpec.Names(repos));
        Assert.True(repos[0].IsRoot);
    }

    /// <summary>THE ATTACH. Nothing about the repository changed; only what discovery calls it.
    /// One member and it is "."; attach a second and the same repository is its member leaf. The
    /// rename is correct and it is exactly why a ticket must key on the PATH
    /// (`Repos.Key` / `Repos.ByPath`) and never on this name.</summary>
    [Fact]
    public void attaching_a_second_project_renames_the_first_repository()
    {
        var t = new TreeSpec().Repo(@"c:\ws\drift-a").Repo(@"c:\ws\drift-b");
        var names = TreeSpec.Names(t.Discover(@"c:\ws\drift-a", @"c:\ws\drift-b"));
        Assert.Contains("drift-a", names);
        Assert.DoesNotContain(".", names);
    }

    /// <summary>Multi-member naming: the member's leaf, then the repo's member-relative path
    /// under it. And no "." survives -- with two members `engine` is ambiguous, so the prefix is
    /// what disambiguates it.</summary>
    [Fact]
    public void multi_member_repo_names_are_member_prefixed()
    {
        var t = new TreeSpec().Repo(@"c:\ws\mA").Repo(@"c:\ws\mB\engine").Folder(@"c:\ws\mB");
        var names = TreeSpec.Names(t.Discover(@"c:\ws\mA", @"c:\ws\mB"));
        Assert.Equal(new[] { "mA", "mB/engine" }, names);
        Assert.DoesNotContain(".", names);
    }

    /// <summary>Two members with the same leaf get `leaf~2`. A DISPLAY collision would otherwise
    /// become a claim-routing collision, which is a correctness problem rather than a cosmetic
    /// one (WORKSPACES-CONCIERGE.md 2.1).</summary>
    [Fact]
    public void a_colliding_leaf_gets_the_tilde_name()
    {
        var t = new TreeSpec().Repo(@"c:\ws\p1\twin").Repo(@"c:\ws\p2\twin");
        Assert.Equal(new[] { "twin", "twin~2" }, TreeSpec.Names(t.Discover(@"c:\ws\p1\twin", @"c:\ws\p2\twin")));
    }

    /// <summary>...and the name is RECYCLED. Detach the second twin, attach a third from
    /// somewhere else entirely, and `twin~2` now points at a repository the old ticket has never
    /// been in -- so its token, its claim gate and its land would all silently move to a
    /// stranger's `main`. The recycling is correct behaviour; the reason it is pinned is that it
    /// is what makes `Repos.ByPath` load-bearing rather than tidy.</summary>
    [Fact]
    public void the_tilde_name_is_recycled_onto_the_new_project()
    {
        var t = new TreeSpec()
            .Repo(@"c:\ws\p1\twin").Repo(@"c:\ws\p2\twin").Repo(@"c:\ws\p3\twin");
        var after = t.Discover(@"c:\ws\p1\twin", @"c:\ws\p3\twin");
        Assert.Equal(new[] { "twin", "twin~2" }, TreeSpec.Names(after));
        Assert.Equal(@"c:\ws\p3\twin", after.Single(r => r.Name == "twin~2").Path);
    }
}
