using System.IO;
using System.Linq;

namespace Dodona;

/// <summary>One thing the resolver is allowed to see inside the fence.</summary>
sealed record Candidate(string Name, string Path, bool IsGit);

/// <summary>
/// The discovery fence (docs/WORKSPACES-CONCIERGE.md §4, rung 3).
///
/// This file carves the single narrow exception to "management brains never run tools":
/// **the resolver gets exactly one capability — enumerating candidate folders inside the
/// fence.** A classifier with a flashlight, not a crawler with opinions.
///
/// The fence is:
///   * the PARENT directory of every member ever registered (if `C:\repos\engine` is a
///     member, `C:\repos` is a place work plausibly lives), and
///   * any explicitly configured search roots.
///
/// **The fence never widens itself** (§8 records the rejection). A rung-3 miss falls to
/// rung 4 — asking the operator — and does not go looking one directory further up. That is
/// the whole difference between a bounded lookup and a filesystem crawl, and it is enforced
/// here in code rather than asked of a model in a prompt: <see cref="Enumerate"/> takes the
/// roots it is given, descends a fixed depth, and has no way to express "try somewhere else".
///
/// It is also what makes rung 3 deterministic under a fixture directory tree, which is what
/// keeps the concierge suite model-free (§17).
/// </summary>
static class Fence
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dodona", ".git", "node_modules", "bin", "obj", "packages", "vendor", ".vs", ".idea",
        "target", "dist", "AppData", "$Recycle.Bin", "System Volume Information", "Windows",
    };

    /// <summary>Where rung 3 may look. Deduped case-insensitively, and deliberately NOT
    /// including drive roots: `C:\` as a member's parent would turn the fence into the
    /// filesystem, so a member sitting directly on a drive root contributes nothing.</summary>
    public static List<string> Roots(Registry reg, IEnumerable<string> configured) =>
        Roots(reg.All().SelectMany(w => w.Members).Select(m => m.Path), configured, Directory.Exists);

    /// <summary>The same derivation over MEMBER PATHS and an injected probe - seam S10
    /// (docs/testarch/seams.md), the `Trees.Locate` shape, so production keeps exactly one
    /// path (the overload above). What it makes answerable in a millisecond is the rule the
    /// design's rejection list is about: the fence is the members' PARENTS, never the members
    /// themselves and never a drive root - because a fence that widens itself is a filesystem
    /// crawl wearing a bounded lookup's name.</summary>
    public static List<string> Roots(IEnumerable<string> memberPaths, IEnumerable<string> configured,
                                     Func<string, bool> dirExists)
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (p is null or "") return;
            var full = Instance.Canonical(p);
            // A drive root ("C:") has no parent and is not a place to search — it is every
            // place. Excluded so that one carelessly-placed member cannot silently widen the
            // fence to the whole volume.
            if (full.Length <= 3 || !dirExists(full)) return;
            if (!roots.Any(r => r.Equals(full, StringComparison.OrdinalIgnoreCase))) roots.Add(full);
        }

        foreach (var m in memberPaths) Add(Path.GetDirectoryName(m));
        foreach (var c in configured) Add(c);
        return roots;
    }

    /// <summary>
    /// Every candidate folder inside the fence. Bounded: these roots, this depth, this skip
    /// list, this cap. The cap matters — a resolver prompt carrying four thousand directory
    /// names is not a classifier with a flashlight, it is a crawler that also costs money.
    /// </summary>
    public static List<Candidate> Enumerate(IEnumerable<string> roots, int maxDepth = 2, int cap = 200)
    {
        var found = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth || found.Count >= cap) return;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; }                                  // unreadable is not an error here
            foreach (var child in children)
            {
                if (found.Count >= cap) return;
                var name = Path.GetFileName(child);
                if (name.Length == 0 || name.StartsWith('.') || Skip.Contains(name)) continue;
                if (!seen.Add(child)) continue;
                var isGit = Registry.LooksLikeRepo(child);
                found.Add(new Candidate(name, child, isGit));
                // A repository's insides are its own business (same rule as Repos.Discover):
                // never descend into one looking for a workspace name.
                if (!isGit) Walk(child, depth + 1);
            }
        }

        foreach (var r in roots) Walk(r, 1);
        return found.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// An explicit path in the prompt. §4 is emphatic that **explicit information never
    /// triggers a search**: if the operator typed a path, that is the answer, and spending a
    /// model call or a directory walk to rediscover what they already said is pure waste.
    /// Matches a Windows absolute path, quoted or bare, and only returns one that exists.
    /// </summary>
    public static string? ExplicitPath(string text)
    {
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"""([a-zA-Z]:\\[^""]+)""|([a-zA-Z]:\\[^\s""]+)").Cast<System.Text.RegularExpressions.Match>())
        {
            var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).TrimEnd('.', ',', ';', ')');
            if (Directory.Exists(raw)) return Instance.Canonical(raw);
        }
        return null;
    }
}
