using System.Collections.Generic;
using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// WHICH REPOSITORY A TICKET IS IN, decided from its claim paths alone.
///
/// Every check here came down from `workspace-acceptance.ps1`, where each one cost a daemon, a
/// `git init` per repository and a real `ticket-create`. The answer they were reading off the
/// reply line is `Repos.ForClaims` and `Repos.CheckClaims`, which are pure functions over a
/// `List&lt;RepoRef&gt;` -- no seam was needed for any of this, only somewhere to put it.
///
/// The rule they are all about, stated once: **a ticket lands by fast-forwarding ONE repository,
/// and two fast-forwards cannot be atomic.** Everything below is that sentence enforced from a
/// different direction.
///
/// The WIRE stays where it was: `worktree_belongs_to_its_repo` still creates a real git worktree
/// beside the repository its claims resolved to, in the suite, because a canned success is the
/// one input that cannot catch a worktree made beside the wrong member.
/// </summary>
public class RepoResolutionTests
{
    /// <summary>The workspace the suite builds: one member holding two repositories and a
    /// `docs` folder that is not one.</summary>
    static List<RepoRef> TwoRepos()
    {
        var t = new TreeSpec()
            .Repo(@"c:\ws\root\engine")
            .Repo(@"c:\ws\root\tools");
        return t.Under(@"c:\ws\root");
    }

    /// <summary>A claim path resolves to exactly one repository, and the ticket is created
    /// there. `subtree:engine/src` is inside `engine`; nothing else has to be said.</summary>
    [Fact]
    public void infers_repo_from_claims()
    {
        var (repo, error) = Repos.ForClaims(TwoRepos(), new List<(string, string)> { ("subtree", "engine/src") });
        Assert.Null(error);
        Assert.Equal("engine", repo!.Name);
    }

    /// <summary>The same question about the other repository, kept as its own name because it is
    /// the one that says the answer is DERIVED rather than defaulted: a resolver that always
    /// returned the first repository would satisfy the row above and fail this one.</summary>
    [Fact]
    public void second_repo_ticket()
    {
        var (repo, error) = Repos.ForClaims(TwoRepos(), new List<(string, string)> { ("subtree", "tools/src") });
        Assert.Null(error);
        Assert.Equal("tools", repo!.Name);
    }

    /// <summary>Claims spanning two repositories are refused, and the refusal says WHY -- two
    /// fast-forwards cannot be made atomic. The wording is asserted because it is the whole of
    /// what the operator gets: this is a refusal, so there is no later screen to explain it.</summary>
    [Fact]
    public void cross_repo_ticket_refused()
    {
        var (repo, error) = Repos.ForClaims(TwoRepos(), new List<(string, string)>
        {
            ("path", "engine/src/a.cs"),
            ("path", "tools/src/b.cs"),
        });
        Assert.Null(repo);
        Assert.Contains("span 2 repositories", error);
        Assert.Contains("cannot be atomic", error);
    }

    /// <summary>A claim in no repository is refused, and the refusal NAMES WHAT THERE IS. A bare
    /// "no repository covers docs/notes.md" leaves the operator guessing at spelling; the list is
    /// the thing that un-sticks them (CLAUDE.md 0.1: a refusal names what un-sticks it).</summary>
    [Fact]
    public void claim_outside_any_repo_refused()
    {
        var (repo, error) = Repos.ForClaims(TwoRepos(), new List<(string, string)> { ("path", "docs/notes.md") });
        Assert.Null(repo);
        Assert.Contains("no repository covers", error);
        Assert.Contains("engine", error);
        Assert.Contains("tools", error);
    }

    /// <summary>`--repo X` does not buy a way past claim validation (P0.6). It used to skip
    /// `ForClaims` ENTIRELY, so `--repo tools --claim path:engine/src/main.cs` made a ticket in
    /// `tools` holding a claim over `engine` -- and the gate, the merge backstop and the land then
    /// each silently disagreed about which repository they were talking about.</summary>
    [Fact]
    public void named_repo_still_validates_its_claims()
    {
        var repos = TwoRepos();
        var named = Repos.ByName(repos, "tools")!;
        var refusal = Repos.CheckClaims(repos, named, new List<(string, string)> { ("path", "engine/src/main.cs") });
        Assert.NotNull(refusal);
        Assert.Contains("not in repository tools", refusal);
        Assert.Contains("engine", refusal);
    }

    /// <summary>P0.6's other half, and it is not decoration: a validator that refused everything
    /// would satisfy the row above. Two members, each its own repository, and a claim that really
    /// is inside the named one passes with no complaint at all.</summary>
    [Fact]
    public void named_repo_accepts_its_own_claims()
    {
        var t = new TreeSpec().Repo(@"c:\ws\mA").Repo(@"c:\ws\mB");
        var repos = t.Discover(@"c:\ws\mA", @"c:\ws\mB");
        var named = Repos.ByName(repos, "mB")!;
        Assert.Null(Repos.CheckClaims(repos, named, new List<(string, string)> { ("path", "mB/src/main.cs") }));
    }
}
