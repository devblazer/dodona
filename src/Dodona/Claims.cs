namespace Dodona;

/// <summary>
/// The claim algebra (design §6), deliberately small: literal paths, directory-subtree
/// prefixes, declared-new files, and literal symbols. Intersection is equality/prefix
/// checks — microseconds, no model — and files that do not exist yet are covered by
/// their subtree or declared as `new:`. No arbitrary globs, on purpose.
/// </summary>
static class Claims
{
    public static string Normalize(string p)
    {
        var s = p.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        // "./src/a.cs" and "src/a.cs" are one file, and this algebra is string comparison —
        // so an unfolded "./" would make them fail to overlap and let two tickets both claim
        // it. A lone "." is the root, which is the empty value (see Parse).
        while (s.StartsWith("./")) s = s[2..].TrimStart('/');
        if (s == ".") s = "";
        return s.ToLowerInvariant();
    }

    /// <summary>Spec syntax: "subtree:src/water", "path:README.md", "new:src/x.cs",
    /// "symbol:WaterController" — plus "subtree:/" (or ".", or "./") for the WHOLE TREE.
    ///
    /// An empty value after normalization means the whole tree for a `subtree` and nothing
    /// at all for the other three: `path:/` names no file and `symbol:   ` names no
    /// identifier, so both are refused here rather than stored as a claim that covers nothing
    /// while reporting success (P0.5's lesson, reached from the other side). A colon with
    /// NOTHING after it stays refused for every kind including `subtree` — "/" and "." are
    /// deliberate spellings of the root, an empty tail is a truncated command line.</summary>
    public static (string Kind, string Value)? Parse(string spec)
    {
        var i = spec.IndexOf(':');
        if (i <= 0 || i == spec.Length - 1) return null;
        var kind = spec[..i] switch
        {
            "path" => "path",
            "new" => "newfile",
            "subtree" => "subtree",
            "symbol" => "symbol",
            _ => null,
        };
        if (kind is null) return null;
        var v = spec[(i + 1)..];
        v = kind == "symbol" ? v.Trim() : Normalize(v);
        if (v.Length == 0 && kind != "subtree") return null;
        return (kind, v);
    }

    /// <summary>The prefix a repository's claims are written with, derived from its DISPLAY
    /// NAME. One definition, called by <see cref="RepoRef.ClaimPrefix"/> as well: two copies
    /// of this rule drifting apart is how an open ticket's claim namespace moves underneath
    /// it, and the whole of Phase 0 exists because that already happened once.</summary>
    public static string Prefix(string repoName)
    {
        var n = repoName.Replace('\\', '/').Trim().Trim('/');
        return n is "" or "." ? "" : n + "/";
    }

    /// <summary>How to print a claim in a refusal. An empty subtree value is the whole tree,
    /// and a bare "subtree:" in an error message reads as a truncated line, not a claim.</summary>
    public static string Spec(string kind, string value) =>
        kind == "subtree" && value.Length == 0 ? "subtree:/ (the whole tree)" : $"{kind}:{value}";

    static bool PathLike(string kind) => kind is "path" or "newfile";

    /// <summary>Does a subtree claim contain a path, in the same namespace? An EMPTY subtree
    /// value is the whole tree — the one case the original three comparisons all answered
    /// "no" to, which made `subtree:/` a lock over everything that blocked nobody.</summary>
    static bool Under(string sub, string p) => sub.Length == 0 || p == sub || p.StartsWith(sub + "/");

    public static bool Overlap(string kindA, string a, string kindB, string b)
    {
        if (kindA == "symbol" || kindB == "symbol")
            return kindA == "symbol" && kindB == "symbol" && a == b;
        if (PathLike(kindA) && PathLike(kindB))
            return a == b;
        if (kindA == "subtree" && kindB == "subtree")
            return Under(a, b) || Under(b, a);
        var (sub, path) = kindA == "subtree" ? (a, b) : (b, a);
        return Under(sub, path);
    }

    /// <summary>One claim as it is HELD: the claim itself, which repository holds it
    /// (<paramref name="RepoKey"/> — the canonical path from <c>Repos.Key</c>, P0.1's
    /// identity), and what that repository was CALLED when the claim was written
    /// (<paramref name="RepoName"/>, which is the prefix the value carries). An empty
    /// <paramref name="RepoKey"/> means the repository is not known: a pre-schema-9 ticket
    /// whose display name no longer resolves to anything.</summary>
    public readonly record struct Held(string RepoKey, string RepoName, string Kind, string Value);

    /// <summary>
    /// Do two claims overlap, when the tickets holding them may be in DIFFERENT repositories?
    /// (Phase 0b — <c>Store.FindConflicts</c> is the only caller.)
    ///
    /// Claims are stored workspace-relative to the repository's display name **at the moment
    /// they were written**, and that name is frozen for an open ticket while discovery is free
    /// to rename the repository underneath it (Phase 0). So comparing raw values is wrong in
    /// both directions:
    ///
    /// - **False NEGATIVE, the dangerous one.** A one-project repository is named "." and
    ///   attaching a second project renames it to its folder leaf. A ticket born before the
    ///   attach holds `src/one`; a ticket born after holds `proj/src/one`. Same directory, no
    ///   shared prefix, no conflict reported — two agents into one folder, which is precisely
    ///   the failure the claim system exists to prevent. Nothing in the tree detected this
    ///   before; `workspace-acceptance`'s drift fixture carried a comment saying so.
    /// - **False POSITIVE.** `symbol:Config` carries no path, so it named the whole workspace:
    ///   holding it in `engine` refused it in `tools`, where it is a different file entirely.
    ///
    /// One idea fixes both: reduce each claim to REPO-RELATIVE terms and compare them only
    /// when they are in the same repository — keyed on the canonical path, never on the name,
    /// because the name is the thing that moves.
    ///
    /// **Every uncertainty falls through to the unscoped comparison**, which is what the whole
    /// tree did before scoping existed, and the bias is deliberate rather than cautious: a
    /// false positive refuses work that would have been fine, which is visible, annoying, and
    /// fixable by widening a claim; a false negative puts two agents in one file and says
    /// nothing. In a ONE-REPOSITORY workspace every ticket shares one key and the prefix is
    /// empty, so this reduces to the old comparison exactly — the property the operator's own
    /// single-project machine depends on.
    /// </summary>
    public static bool Overlap(Held a, Held b)
    {
        var (la, lb) = (Local(a), Local(b));
        // Either claim unplaceable → no scoping at all. Two ways to get here: a ticket with
        // no repo_path, and a claim that does not live in the repository its ticket lands in
        // (a pre-P0.6 row, where `--repo tools --claim path:engine/...` skipped validation
        // entirely). Neither can be reduced honestly, so neither is narrowed.
        if (la is null || lb is null) return Overlap(a.Kind, a.Value, b.Kind, b.Value);
        if (!a.RepoKey.Equals(b.RepoKey, StringComparison.OrdinalIgnoreCase)) return false;
        return Overlap(a.Kind, la, b.Kind, lb);
    }

    /// <summary>A claim in its own repository's terms, or null when it cannot be placed there
    /// — see <see cref="Overlap(Held, Held)"/> for what null then means.
    ///
    /// A symbol is repo-local already and merely never said which repo: it names an identifier
    /// inside the repository its ticket lands in, and neither `ticket-create` nor
    /// `claim-extend` will let a ticket's claims leave that repository. Two repositories' each
    /// having a `Config` is two different files, and no agent in one can reach the other.</summary>
    static string? Local(Held h)
    {
        if (h.RepoKey.Length == 0) return null;
        if (h.Kind == "symbol") return h.Value;
        var p = Prefix(h.RepoName);
        if (p.Length == 0) return h.Value;                  // the root repo: workspace-relative IS repo-relative
        if (h.Value.Length == 0) return null;               // the whole WORKSPACE, wider than any one repo
        if (h.Value.Equals(p[..^1], StringComparison.OrdinalIgnoreCase)) return "";   // the repo root itself
        return h.Value.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? h.Value[p.Length..] : null;
    }

    /// <summary>Is a repo-relative file path covered by this claim?</summary>
    public static bool Covers(string kind, string value, string relPath)
    {
        if (PathLike(kind)) return value == relPath;
        if (kind == "subtree") return Under(value, relPath);
        return false;   // symbols do not gate file writes
    }
}
