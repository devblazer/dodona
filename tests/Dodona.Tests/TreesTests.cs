using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// LAYER 1 OF WORK ISOLATION (docs/WORK-ISOLATION-PLAN.md section 3): no agent writes into a
/// project outside a worktree. Every Edit/Write/MultiEdit/NotebookEdit of every work lane now
/// passes through this decision, so it is held here -- with no filesystem and no daemon -- as
/// well as end to end in `m1`.
///
/// The test is deliberately the same stateless one the other two layers use: `.git` is a FILE in
/// a linked worktree and a DIRECTORY in the shared checkout. `Locate` takes its two probes as
/// parameters so this file can describe a repository, a worktree inside it and a bare folder
/// without creating any of them -- which is what keeps `dev test unit` at about a second.
/// </summary>
public class TreesTests
{
    const string Proj = @"C:\work\dodona";
    static readonly string[] Projects = { Proj };

    /// <summary>Directories that exist, and files that exist, named exactly.</summary>
    static Trees.Where Locate(string path, string[] dirs, string[] files) =>
        Trees.Locate(path, Projects,
                     d => System.Array.Exists(dirs, x => string.Equals(x, d, System.StringComparison.OrdinalIgnoreCase)),
                     f => System.Array.Exists(files, x => string.Equals(x, f, System.StringComparison.OrdinalIgnoreCase)));

    /// <summary>THE ONE THAT MATTERS. `.git` is a directory, so this is the checkout the operator
    /// and every other lane share -- and `.githooks/pre-commit` refuses commits from it, so work
    /// done here cannot even be delivered. This is the operator's named failure.</summary>
    [Fact]
    public void A_write_in_the_shared_checkout_is_the_refused_case()
    {
        var w = Locate(@"C:\work\dodona\src\Dodona\Daemon.cs", new[] { @"C:\work\dodona\.git" }, new string[0]);
        Assert.Equal(Trees.Where.SharedCheckout, w);
        Assert.False(Trees.Allowed(w));
    }

    /// <summary>`.git` is a FILE in a linked worktree. This is the allowed case, and the only one
    /// load-bearing work is supposed to happen in.</summary>
    [Fact]
    public void A_write_in_a_ticket_worktree_is_allowed()
    {
        var w = Locate(@"C:\work\dodona\.dodona\wt\t3\src\Dodona\Daemon.cs",
                       new string[0], new[] { @"C:\work\dodona\.dodona\wt\t3\.git" });
        Assert.Equal(Trees.Where.Worktree, w);
        Assert.True(Trees.Allowed(w));
    }

    /// <summary>NEAREST `.git` WINS, and getting this backwards inverts the whole layer. A ticket
    /// worktree lives at `&lt;member&gt;/.dodona/wt/tN` -- INSIDE the repository it came from -- so
    /// walking to the outermost `.git` would call every worktree write a shared-checkout write and
    /// refuse all of them, while the reverse ordering would allow everything.</summary>
    [Fact]
    public void The_nearest_dot_git_decides_so_a_worktree_inside_its_repo_is_still_a_worktree()
    {
        var w = Locate(@"C:\work\dodona\.dodona\wt\t3\src\x.cs",
                       new[] { @"C:\work\dodona\.git" },                 // the shared checkout, above
                       new[] { @"C:\work\dodona\.dodona\wt\t3\.git" });  // the worktree, below
        Assert.Equal(Trees.Where.Worktree, w);
    }

    /// <summary>A project with no repository at all. ALLOWED, and not a carve-out: the hazard is a
    /// shared CHECKOUT -- one branch, one set of tracked files, two lanes and a human. A folder
    /// with no git has none of that, cannot be given a worktree either (`ticket-create` needs
    /// git), and refusing would brick every lane in such a project with nothing available to
    /// un-stick it (CLAUDE.md 0.1: a wait must name the condition that ends it).</summary>
    [Fact]
    public void A_project_that_is_not_a_repo_is_allowed()
    {
        var w = Locate(@"C:\work\dodona\notes\todo.md", new string[0], new string[0]);
        Assert.Equal(Trees.Where.NotARepo, w);
        Assert.True(Trees.Allowed(w));
    }

    /// <summary>Outside every project -- a scratch file in %TEMP%, a note in a home directory.
    /// Allowed, and flagged as an OPEN QUESTION in the plan's section 10 rather than settled:
    /// refusing it may be right and only use will show. `claim-check` still refuses it for a
    /// ticket lane, so this changes nothing there.</summary>
    [Fact]
    public void Outside_every_project_is_allowed_and_says_so()
    {
        var w = Locate(@"C:\Users\someone\AppData\Local\Temp\scratch.txt",
                       new[] { @"C:\work\dodona\.git" }, new string[0]);
        Assert.Equal(Trees.Where.OutsideEveryProject, w);
        Assert.True(Trees.Allowed(w));
    }

    /// <summary>The project root itself, and the `.git` directory's own parent: the walk includes
    /// the path it is given, because a write may name a directory and starting one level up would
    /// ask the question about the wrong tree.</summary>
    [Fact]
    public void The_path_itself_is_examined_not_only_its_ancestors()
    {
        Assert.Equal(Trees.Where.SharedCheckout,
                     Locate(@"C:\work\dodona", new[] { @"C:\work\dodona\.git" }, new string[0]));
        Assert.Equal(Trees.Where.Worktree,
                     Locate(@"C:\work\dodona\.dodona\wt\t1", new string[0],
                            new[] { @"C:\work\dodona\.dodona\wt\t1\.git" }));
    }

    /// <summary>A trailing separator and forward slashes are the same path. `Projects.Of` compares
    /// prefixes literally, so an unnormalised path would miss its own project and answer
    /// `OutsideEveryProject` -- which is an ALLOW reached by a string detail rather than a
    /// decision. That is the shape of a gate that silently stops enforcing.</summary>
    [Theory]
    [InlineData(@"C:\work\dodona\src\x.cs")]
    [InlineData("C:/work/dodona/src/x.cs")]
    [InlineData(@"C:\work\dodona\src\")]
    [InlineData(@"C:\work\dodona\.\src\x.cs")]
    public void Separators_and_edges_do_not_change_the_verdict(string path) =>
        Assert.Equal(Trees.Where.SharedCheckout, Locate(path, new[] { @"C:\work\dodona\.git" }, new string[0]));

    /// <summary>An empty path is not a project match. It must not fall through to "allowed by
    /// accident" via some other branch -- `Projects.Of` has the same rule for the same reason.</summary>
    [Fact]
    public void An_empty_path_is_outside_every_project() =>
        Assert.Equal(Trees.Where.OutsideEveryProject, Locate("   ", new string[0], new string[0]));
}
