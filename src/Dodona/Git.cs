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
}
