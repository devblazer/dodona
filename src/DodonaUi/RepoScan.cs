using System.IO;

namespace DodonaUi;

/// <summary>
/// Recognizing the *shape* of a folder, for the picker's description — filesystem checks
/// only, no git processes. The UI does not do git (the daemon owns git, §12); it only
/// needs to say what it is looking at fast enough to describe a list on hover. Every
/// answer here is provisional: the daemon re-checks with real git when it matters, at
/// `repo-init` and at the first ticket.
/// </summary>
static class RepoScan
{
    /// <summary>A repo's top level. `.git` is a directory normally, a file in a worktree
    /// checkout or a submodule.</summary>
    public static bool IsRepoRoot(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>The repository this folder sits inside, if any. Without this, browsing to
    /// a subdirectory of a repo would look repo-less and get offered a nested repo —
    /// which is a mess to undo and never what anyone meant.</summary>
    public static string? EnclosingRepo(string dir)
    {
        var d = new DirectoryInfo(dir).Parent;
        while (d is not null)
        {
            if (IsRepoRoot(d.FullName)) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    /// <summary>Repositories directly underneath — the workspace shape. Shallow: a deep
    /// scan is slow and turns up vendored copies nobody meant to orchestrate.</summary>
    public static List<string> FindNested(string root, int maxDepth = 2)
    {
        var found = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dodona", ".git", "node_modules", "bin", "obj", "packages", "vendor", ".vs", ".idea", "target", "dist" };

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || skip.Contains(name)) continue;
                if (IsRepoRoot(child)) { found.Add(child); continue; }
                Walk(child, depth + 1);
            }
        }

        Walk(root, 1);
        return found;
    }

    /// <summary>Has anything been committed? A repo with no commits has no branch, so no
    /// worktree can be cut from it. Approximated from refs — the daemon confirms with
    /// `rev-parse --verify HEAD` before it matters.</summary>
    public static bool LooksCommitted(string repoRoot)
    {
        var dotGit = Path.Combine(repoRoot, ".git");
        if (File.Exists(dotGit)) return true;                       // a worktree checkout always has history
        if (!Directory.Exists(dotGit)) return false;
        try
        {
            var heads = Path.Combine(dotGit, "refs", "heads");
            if (Directory.Exists(heads) && Directory.EnumerateFiles(heads, "*", SearchOption.AllDirectories).Any()) return true;
            return File.Exists(Path.Combine(dotGit, "packed-refs"));
        }
        catch { return true; }                                       // unreadable: assume normal, let git decide
    }

    public static bool HasContent(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Any(p => Path.GetFileName(p) is not ".dodona" and not ".git" and not ".gitignore");
        }
        catch { return false; }
    }
}
