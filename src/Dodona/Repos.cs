using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's

namespace Dodona;

/// <summary>One repository inside the workspace. <see cref="Name"/> is its
/// workspace-relative path with forward slashes — or "." when the workspace's single
/// member is itself the repository, which is the ordinary single-repo project and the case
/// every path below must reduce to exactly.
///
/// <see cref="MemberPath"/> is the workspace member this repo was discovered under. It
/// exists because ticket worktrees stay beside their repo's member
/// (WORKSPACES-CONCIERGE.md §1's stated exception to "state leaves user folders"), so the
/// worktree root cannot be a single per-workspace directory any more.</summary>
sealed record RepoRef(string Name, string Path, string MemberPath)
{
    public bool IsRoot => Name == ".";
    /// <summary>What to prepend to a repo-relative path to make it a claim path.</summary>
    public string ClaimPrefix => IsRoot ? "" : Name + "/";
}

/// <summary>
/// The workspace's repositories (§14, as extended by WORKSPACES-CONCIERGE.md §1). A
/// workspace holds N members, and each member is either a repository itself or a folder
/// with repositories under it. Discovery is filesystem-only so it can run on every ticket
/// without spawning a git process per directory; git remains the authority wherever the
/// answer actually matters.
///
/// The hard rule, and the reason a ticket names exactly one repository: landing is a
/// fast-forward of one branch onto one main, and two fast-forwards into two repositories
/// cannot be made atomic. A change spanning repositories is therefore two tickets, and
/// sequencing them is a judgement call — which is the adjudicator's job, not the merge
/// queue's.
/// </summary>
static class Repos
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { ".dodona", ".git", "node_modules", "bin", "obj", "packages", "vendor", ".vs", ".idea", "target", "dist" };

    static bool LooksLikeRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// Every repository in the workspace, named workspace-relatively.
    ///
    /// **A one-member workspace produces byte-for-byte the names it always did** — that is
    /// not an accident, it is the property that keeps claims, tickets, gate prefixes and
    /// every pre-existing acceptance suite meaningful through the migration (§1's
    /// degenerate case). Only when a second member is attached does a member prefix appear,
    /// because only then is `engine` ambiguous.
    ///
    /// Multi-member naming: the member's folder leaf, then the repo's member-relative path
    /// under it (`work/engine`), or just the leaf when the member IS a repo (`dodona`).
    /// Two members with the same leaf get `leaf~2` — a display collision would otherwise
    /// become a claim-routing collision, which is a correctness problem rather than a
    /// cosmetic one. (Decision recorded in WORKSPACES-CONCIERGE.md §2.1.)
    /// </summary>
    public static List<RepoRef> Discover(List<Member> members, int maxDepth = 2)
    {
        if (members.Count == 0) return new List<RepoRef>();
        if (members.Count == 1) return Under(members[0].Path, "", maxDepth);

        var found = new List<RepoRef>();
        var usedLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            var leaf = Path.GetFileName(m.Path);
            if (leaf.Length == 0) leaf = m.Path.Replace('\\', '-').Replace(':', '-').Trim('-');
            var unique = leaf;
            for (int n = 2; !usedLeaves.Add(unique); n++) unique = $"{leaf}~{n}";
            found.AddRange(Under(m.Path, unique, maxDepth));
        }
        return found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Single-root discovery — the shape this method has always had. `prefix` is
    /// empty for a one-member workspace, which is what makes that case unchanged.</summary>
    public static List<RepoRef> Under(string root, string prefix = "", int maxDepth = 2)
    {
        // A repository's insides are its own business: if the root is one, that is the
        // whole answer, and anything below is a submodule or a vendored copy.
        if (LooksLikeRepo(root))
            return new List<RepoRef> { new(prefix.Length == 0 ? "." : prefix, root, root) };

        var found = new List<RepoRef>();
        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || Skip.Contains(name)) continue;
                if (LooksLikeRepo(child))
                {
                    var rel = Path.GetRelativePath(root, child).Replace('\\', '/');
                    found.Add(new RepoRef(prefix.Length == 0 ? rel : $"{prefix}/{rel}", child, root));
                    continue;
                }
                Walk(child, depth + 1);
            }
        }
        Walk(root, 1);
        return found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static RepoRef? ByName(List<RepoRef> repos, string name)
    {
        var wanted = name.Replace('\\', '/').Trim('/');
        return repos.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? (wanted is "." or "" ? repos.FirstOrDefault(r => r.IsRoot) : null);
    }

    /// <summary>Which repository a claim path falls in. Claims are workspace-relative, so
    /// the path already says — no extra syntax, and the claim algebra is untouched.</summary>
    public static RepoRef? ForPath(List<RepoRef> repos, string claimValue)
    {
        var v = claimValue.Replace('\\', '/').TrimStart('/');
        // Longest name first: a repo nested under another must win over its ancestor.
        foreach (var r in repos.OrderByDescending(r => r.Name.Length))
        {
            if (r.IsRoot) return r;
            if (v.Equals(r.Name, StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith(r.Name + "/", StringComparison.OrdinalIgnoreCase)) return r;
        }
        return null;
    }

    /// <summary>The repository a whole ticket belongs to, inferred from its claims.
    /// Symbol claims name no path, so they follow whatever the path claims decided.</summary>
    public static (RepoRef? Repo, string? Error) ForClaims(List<RepoRef> repos, List<(string Kind, string Value)> claims)
    {
        if (repos.Count == 0)
            return (null, "no git repository in this workspace — run `dodona repo-init`, or open a repository as the project");

        var hits = new Dictionary<string, RepoRef>(StringComparer.OrdinalIgnoreCase);
        var homeless = new List<string>();
        foreach (var (kind, value) in claims)
        {
            if (kind == "symbol") continue;
            var r = ForPath(repos, value);
            if (r is null) homeless.Add($"{kind}:{value}");
            else hits[r.Name] = r;
        }

        if (hits.Count > 1)
            return (null, $"claims span {hits.Count} repositories ({string.Join(", ", hits.Keys)}) — " +
                          "a ticket lands by fast-forwarding one repository, and two fast-forwards cannot be atomic; " +
                          "split it into one ticket per repository");
        if (hits.Count == 1 && homeless.Count > 0)
            return (null, $"these claims are in no repository: {string.Join(", ", homeless)}");
        if (hits.Count == 1) return (hits.Values.First(), null);

        if (homeless.Count > 0)
            return (null, $"no repository covers {string.Join(", ", homeless)} — " +
                          $"repositories here: {string.Join(", ", repos.Select(r => r.Name))}");
        // Symbol-only claims in a single-repo workspace: no ambiguity to resolve.
        return repos.Count == 1 ? (repos[0], null)
            : (null, $"say which repository with --repo (one of: {string.Join(", ", repos.Select(r => r.Name))})");
    }
}
