using System.Diagnostics;

namespace Dodona;

/// <summary>Thin git runner. Git is the truth for git (design §12) — the store only
/// caches what these calls report.</summary>
static class Git
{
    public static (int Code, string Out) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = Task.Run(() => p.StandardError.ReadToEnd());
        var so = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var se = errTask.Result;
        return (p.ExitCode, (so + se).Trim());
    }

    /// <summary>HEAD sha of a ref; throws if the ref does not resolve.</summary>
    public static string Sha(string repo, string @ref)
    {
        var (code, output) = Run(repo, "rev-parse", @ref);
        if (code != 0) throw new InvalidOperationException($"rev-parse {@ref}: {output}");
        return output;
    }

    /// <summary>Is this directory itself the top of a work tree? Asked of git rather than
    /// guessed from a `.git` entry, because a worktree checkout's `.git` is a file and a
    /// subdirectory of a repo would answer yes to any naive test.</summary>
    public static bool IsRepo(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        var (code, output) = Run(dir, "rev-parse", "--show-toplevel");
        if (code != 0) return false;
        return Instance.Canonical(output.Trim()).Equals(Instance.Canonical(dir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does it have a commit yet? An empty repo has no branch to worktree from,
    /// which is a different problem with a different fix than having no repo at all.</summary>
    public static bool HasCommit(string repo) => Run(repo, "rev-parse", "--verify", "HEAD").Code == 0;

    /// <summary>Repositories sitting inside a folder — the workspace case, where the
    /// thing you point Dodona at is a container and the repos are underneath it. Shallow
    /// by design: a deep scan of a source tree is slow and finds vendored junk.</summary>
    public static List<string> FindRepos(string root, int maxDepth = 2)
    {
        var found = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dodona", ".git", "node_modules", "bin", "obj", "packages", "vendor", ".vs", ".idea", "target", "dist" };

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            IEnumerable<string> children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || skip.Contains(name)) continue;
                if (IsRepo(child)) { found.Add(child); continue; }   // a repo's insides are its own business
                Walk(child, depth + 1);
            }
        }

        Walk(root, 1);
        return found;
    }

    /// <summary>
    /// A detached worktree that removes itself. Publish uses it for <c>--from &lt;ref&gt;</c>
    /// and the drift watcher for the commit it is catching up to (RECOVERY-PHASES P2.3): both
    /// must build a specific commit in a tree of their OWN, never the live one an operator or
    /// another session is working in.
    ///
    /// It is <c>IDisposable</c> and used with <c>using</c> on purpose. Publish has a dozen
    /// early <c>return Fail(...)</c> paths, and a cleanup that has to be repeated at each of
    /// them is a leak waiting for the one that gets forgotten -- which in this codebase means
    /// a worktree nobody removes, quietly holding a checkout of an old commit.
    /// </summary>
    public sealed class TempWorktree : IDisposable
    {
        readonly string _repo;

        /// <summary>The checkout, or null when there is none -- either nothing was asked for,
        /// or the checkout failed and <see cref="Error"/> says why.</summary>
        public string? Path { get; }

        /// <summary>Git's complaint when <see cref="For"/> could not check the ref out.</summary>
        public string Error { get; }

        TempWorktree(string repo, string? path, string error) { _repo = repo; Path = path; Error = error; }

        /// <summary>Nothing to do (returns a no-op) when <paramref name="spec"/> is null or is
        /// already a directory -- <c>--from &lt;worktree&gt;</c> is a tree the caller owns and
        /// must NOT be deleted by us.</summary>
        public static TempWorktree None(string repo) => new(repo, null, "");

        /// <summary>Check <paramref name="sha"/> out detached, under the temp directory. On
        /// failure it returns an instance whose <see cref="Path"/> is null and whose
        /// <see cref="Error"/> holds git's complaint -- never null, and never an `out`
        /// parameter, so the caller can keep this in a `using` inside a conditional
        /// expression (which is what an `out` here made impossible: CS0165).</summary>
        public static TempWorktree For(string repo, string sha, string stamp)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dodona-from", stamp);
            var (code, output) = Run(repo, "worktree", "add", "--detach", dir, sha);
            return code == 0 ? new TempWorktree(repo, dir, "") : new TempWorktree(repo, null, output);
        }

        public void Dispose()
        {
            if (Path is null) return;
            // Best effort: a failure here must never turn a good publish into a bad exit code.
            // `prune` mops up the administrative record even if the directory itself is stuck.
            try
            {
                Run(_repo, "worktree", "remove", "--force", Path);
                Run(_repo, "worktree", "prune");
            }
            catch { }
        }
    }
}
