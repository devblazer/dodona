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

    /// <summary>
    /// A repository's IDENTITY — its canonical path (P0.1). <see cref="RepoRef.Name"/> is a
    /// DISPLAY name and it is not stable: it is recomputed by <see cref="Discover"/> on every
    /// call, and the rule changes with project count, so the *same repository* is called "."
    /// in a one-project workspace and `proj` the moment a second project is attached. A
    /// merge token keyed by that name is therefore two rows for one `main` — two agents each
    /// believing they hold the token, both fast-forwarding the same branch, which is the
    /// exact race this system exists to prevent (found 2026-08-19; nothing of ours caused it).
    ///
    /// <see cref="Instance.Canonical"/> is the same folding the registry already dedupes
    /// members by, so one repository has exactly one key however it is spelled.
    /// </summary>
    public static string Key(string repoPath) => Instance.Canonical(repoPath);

    /// <summary>Resolve a repository by identity rather than by display name. This is what a
    /// ticket must use: its name was frozen when it was created and the naming rule has been
    /// free to change underneath it ever since, while its path has not.</summary>
    public static RepoRef? ByPath(List<RepoRef> repos, string repoPath)
    {
        if (repoPath.Length == 0) return null;
        var want = Key(repoPath);
        return repos.FirstOrDefault(r => Key(r.Path).Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Are these claims all inside <paramref name="named"/>? Asked when the caller named the
    /// repository itself (`ticket-create --repo X`, and every `claim-extend`), which used to
    /// skip <see cref="ForClaims"/> ENTIRELY — so `--repo tools --claim path:engine/sim.cs`
    /// created a ticket in `tools` holding a claim over `engine`, and the gate, the merge
    /// backstop and the land all then disagreed about which repository they were talking
    /// about (P0.6). Returns null when every claim belongs, or the refusal to print.
    ///
    /// Symbol claims carry no path and so name no repository — they are skipped here exactly
    /// as <see cref="ForClaims"/> skips them. Narrowing them is Phase 0b's business.
    ///
    /// In a one-repository workspace the root repo swallows every path, so this can only
    /// return null: the ordinary case is unchanged, which is the property the whole
    /// workspace migration rests on.
    /// </summary>
    public static string? CheckClaims(List<RepoRef> repos, RepoRef named, List<(string Kind, string Value)> claims)
    {
        var strays = new List<string>();
        foreach (var (kind, value) in claims)
        {
            if (kind == "symbol") continue;
            var r = ForPath(repos, value);
            if (r is null) strays.Add($"{kind}:{value} is in no repository");
            else if (!r.Name.Equals(named.Name, StringComparison.OrdinalIgnoreCase))
                strays.Add($"{kind}:{value} is in repository {r.Name}");
        }
        if (strays.Count == 0) return null;
        return $"these claims are not in repository {named.Name}: {string.Join("; ", strays)} — " +
               "a ticket lands by fast-forwarding one repository, so its claims must all live in that one";
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
