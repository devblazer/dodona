using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// A GIT REPO BELONGS TO AT MOST ONE WORKSPACE AT A TIME, which CLAUDE.md section 5 calls the
/// invariant this system exists to provide: two workspaces over one repo is TWO MERGE TOKENS
/// OVER ONE MAIN, two agents each told "granted", both able to fast-forward the same branch.
///
/// A red here is a correctness incident, not a flaky test. That sentence came down from
/// `workspace-acceptance.ps1` with the checks and it still applies.
///
/// EVERYTHING HERE RUNS THE REAL `Registry` (seam S5, a temp file), because plan 3.5 forbids
/// faking it BY NAME and gives the reason: the enforcement is a partial
/// `UNIQUE(members.key) WHERE is_git` index that the class comment calls *"the real arbiter"*, so
/// a `HashSet` stand-in would be a different mechanism passing a test written about the index.
/// What the folders lose is `git init` and a commit; what they keep is a real `.git`, which is
/// the only thing `Registry.LooksLikeRepo` ever asked about.
/// </summary>
public class RegistryExclusivityTests
{
    /// <summary>Attaching a repo owned by another workspace is REFUSED. Layer 2, at the point
    /// somebody asks for it -- and the wording is the operator's whole diagnosis.</summary>
    [Fact]
    public void repo_in_two_workspaces_refused()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var repo = t.GitFolder("solo");
        var owner = reg.Create("owner");
        Assert.True(reg.Attach(owner.Id, repo, out var first), first);

        var rival = reg.Create("rival");
        Assert.False(reg.Attach(rival.Id, repo, out var refusal));
        Assert.Contains("already belongs to workspace", refusal);
        // ...and the refusal is not decoration: the second workspace really did not get it.
        Assert.Empty(reg.ById(rival.Id)!.Members);
    }

    /// <summary>The refusal NAMES THE REASON. Not "denied" -- the two-merge-tokens sentence, so
    /// the person reading it can tell a policy from a bug.</summary>
    [Fact]
    public void refusal_says_why_two_tokens_is_the_problem()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var repo = t.GitFolder("solo");
        reg.Attach(reg.Create("owner").Id, repo, out _);
        Assert.False(reg.Attach(reg.Create("rival").Id, repo, out var refusal));
        Assert.Contains("two merge tokens over one main", refusal);
    }

    /// <summary>...and it names the command that UN-STICKS it (CLAUDE.md 0.1). Reassignment is
    /// legitimate; silent double ownership never is. A refusal with no way out is a wait with no
    /// condition that ends it.</summary>
    [Fact]
    public void refusal_offers_the_move_affordance()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var repo = t.GitFolder("solo");
        reg.Attach(reg.Create("owner").Id, repo, out _);
        Assert.False(reg.Attach(reg.Create("rival").Id, repo, out var refusal));
        Assert.Contains("dodona workspace-move --member", refusal);
    }

    /// <summary>`workspace-create --member &lt;owned repo&gt;` is the same theft through a different
    /// door, and it must not half-succeed: the workspace may exist, but it does not get the repo,
    /// and the repo's real owner is unchanged.</summary>
    [Fact]
    public void creating_a_workspace_cannot_steal_an_owned_repo()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var repo = t.GitFolder("solo");
        var owner = reg.Create("owner");
        reg.Attach(owner.Id, repo, out _);

        var thief = reg.Create("thief");
        Assert.False(reg.Attach(thief.Id, repo, out _));
        Assert.Empty(reg.ById(thief.Id)!.Members);
        Assert.Single(reg.All(), w => w.Members.Any(m => m.Key == TempReg.Key(repo)));
        Assert.Equal("owner", reg.All().Single(w => w.Members.Any(m => m.Key == TempReg.Key(repo))).Name);
    }

    /// <summary>A BARE FOLDER IS EXEMPT AND MUST STAY EXEMPT. There is no merge token to split,
    /// and a shared notes folder in two workspaces harms nobody. The exemption is why the index
    /// is PARTIAL (`WHERE is_git = 1`) rather than absolute.</summary>
    [Fact]
    public void bare_folder_may_be_shared()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var notes = t.Folder("notes");
        Assert.True(reg.Attach(reg.Create("rival").Id, notes, out var a), a);
        Assert.True(reg.Attach(reg.Create("solo").Id, notes, out var b), b);
    }

    /// <summary>The same exemption reached through `workspace-create --member`, and it is not a
    /// duplicate of the row above: this one asserts BOTH WORKSPACES EXIST afterwards, which is
    /// what the point-of-use backstop later depends on -- the drift case only arises because two
    /// workspaces were legitimately allowed to hold one folder before somebody ran `git init` in
    /// it behind the registry's back.</summary>
    [Fact]
    public void bare_folder_in_two_workspaces_is_allowed()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var drift = t.Folder("drift");
        var a = reg.Create("drift-a");
        var b = reg.Create("drift-b");
        Assert.True(reg.Attach(a.Id, drift, out var ea), ea);
        Assert.True(reg.Attach(b.Id, drift, out var eb), eb);
        Assert.Equal(2, reg.All().Count(w => w.Members.Any(m => m.Key == TempReg.Key(drift))));
    }
}
