using System;
using System.IO;
using Dodona;
using Xunit;

namespace Dodona.Testing;

/// <summary>
/// THE SWEEP THAT MAKES A STUCK CLEANUP COST ONE LEFTOVER INSTEAD OF ONE PER PUBLISH (issue #24).
///
/// `TempWorktree.Dispose` was best-effort and every failure it had was permanent: `Run` returns an
/// exit code and it discarded it, `git worktree remove` failing is not an exception so the `catch`
/// never fired, and `prune` cannot help because prune drops the record for a worktree whose
/// DIRECTORY IS GONE and these were still there. Thirteen accumulated on the operator's machine
/// over two days -- and the 103 MB was never the point. Git counts each one as a real checkout, so
/// `git worktree list` answered "where is this repository checked out" with thirteen fictional
/// entries against two real ones, and that is a question this repo has already paid for once.
///
/// A REAL REPOSITORY IN A REAL TEMP DIRECTORY, not a double: the whole behaviour is what `git
/// worktree remove` does and does not do, and a fake git would be asserting my model of git rather
/// than git. It costs a handful of git invocations and no daemon, no store, no window.
///
/// NOT AN ACCEPTANCE CHECK, deliberately, and this is the trade being made in the open: reaching
/// `Sweep` through the product's own front door means `publish --from &lt;ref&gt;`, which runs a
/// REAL build -- roughly +30s on a 20s suite, against a gate whose headroom is issue #1's ~40s.
/// CLAUDE.md §0.1 ranks speed over thoroughness where they conflict and names the missed catch as
/// the accepted cost. What that leaves unchecked is one line of wiring at the call site in
/// `Program.cs` (that `For` is handed a `say` which reaches stderr), verified by reading.
/// </summary>
public sealed class TempWorktreeSweepTests : IDisposable
{
    readonly string _root;
    readonly string _repo;
    readonly string _parent;

    public TempWorktreeSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dodona-sweeptest-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        _parent = Path.Combine(_root, "from");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_parent);
        Git.Run(_repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");
        Git.Run(_repo, "add", "-A");
        Git.Run(_repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init");
    }

    /// <summary>A worktree of the test repo at <paramref name="stamp"/>, aged by
    /// <paramref name="minutesOld"/> so the sweep's cutoff can be exercised without waiting.</summary>
    string Leftover(string stamp, int minutesOld)
    {
        var dir = Path.Combine(_parent, stamp);
        var (code, output) = Git.Run(_repo, "worktree", "add", "--detach", dir, "HEAD");
        Assert.True(code == 0, $"fixture could not create a worktree: {output}");
        Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddMinutes(-minutesOld));
        return dir;
    }

    bool GitListsIt(string dir) => Git.Run(_repo, "worktree", "list").Out.Contains(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void An_old_leftover_goes_and_the_sweep_says_how_many()
    {
        var stale = Leftover("20260820-223404", minutesOld: 60);
        Assert.True(GitListsIt(stale), "fixture: git should list it before the sweep");

        var said = new System.Collections.Generic.List<string>();
        Git.TempWorktree.Sweep(_repo, _parent, mine: "20260822-999999", say: said.Add);

        Assert.False(Directory.Exists(stale), "the stale build tree is still on disk");
        Assert.False(GitListsIt(stale), "git still lists the stale build tree as a checkout of this repo -- " +
                                        "which is the half of this bug that mattered");
        Assert.Contains(said, m => m.Contains("swept 1", StringComparison.Ordinal));
    }

    /// <summary>THE AGE GUARD IS NOT TIDINESS -- it is what makes a concurrent publish safe. The
    /// stamp is a timestamp, so two publishes close together are two LIVE directories, and a sweep
    /// with no guard would delete a tree another process is mid-build in.</summary>
    [Fact]
    public void A_young_leftover_is_left_alone_because_another_publish_may_be_building_in_it()
    {
        var young = Leftover("20260822-120000", minutesOld: 2);
        var said = new System.Collections.Generic.List<string>();

        Git.TempWorktree.Sweep(_repo, _parent, mine: "20260822-999999", say: said.Add);

        Assert.True(Directory.Exists(young), "a build tree younger than the cutoff was swept -- " +
                                             "a concurrent publish would have lost its tree mid-build");
        Assert.Empty(said);
    }

    /// <summary>The caller's OWN directory is never swept, whatever its age says. The stamp is
    /// second-granular and the clock is not a promise.</summary>
    [Fact]
    public void The_tree_this_publish_is_about_to_use_is_never_swept()
    {
        var mine = Leftover("20260822-131313", minutesOld: 90);

        Git.TempWorktree.Sweep(_repo, _parent, mine: "20260822-131313", say: null);

        Assert.True(Directory.Exists(mine), "the sweep deleted the tree the caller was about to build in");
    }

    [Fact]
    public void Sweeping_a_directory_that_does_not_exist_is_not_an_error()
    {
        var said = new System.Collections.Generic.List<string>();
        Git.TempWorktree.Sweep(_repo, Path.Combine(_root, "never-made"), mine: "x", say: said.Add);
        Assert.Empty(said);
    }

    public void Dispose()
    {
        foreach (var d in Directory.Exists(_parent) ? Directory.GetDirectories(_parent) : Array.Empty<string>())
            Git.Run(_repo, "worktree", "remove", "--force", d);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
