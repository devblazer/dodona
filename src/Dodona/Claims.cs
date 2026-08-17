namespace Dodona;

/// <summary>
/// The claim algebra (design §6), deliberately small: literal paths, directory-subtree
/// prefixes, declared-new files, and literal symbols. Intersection is equality/prefix
/// checks — microseconds, no model — and files that do not exist yet are covered by
/// their subtree or declared as `new:`. No arbitrary globs, on purpose.
/// </summary>
static class Claims
{
    public static string Normalize(string p) =>
        p.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/').ToLowerInvariant();

    /// <summary>Spec syntax: "subtree:src/water", "path:README.md", "new:src/x.cs", "symbol:WaterController".</summary>
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
        return (kind, kind == "symbol" ? v.Trim() : Normalize(v));
    }

    static bool PathLike(string kind) => kind is "path" or "newfile";

    public static bool Overlap(string kindA, string a, string kindB, string b)
    {
        if (kindA == "symbol" || kindB == "symbol")
            return kindA == "symbol" && kindB == "symbol" && a == b;
        if (PathLike(kindA) && PathLike(kindB))
            return a == b;
        if (kindA == "subtree" && kindB == "subtree")
            return a == b || a.StartsWith(b + "/") || b.StartsWith(a + "/");
        var (sub, path) = kindA == "subtree" ? (a, b) : (b, a);
        return path == sub || path.StartsWith(sub + "/");
    }

    /// <summary>Is a repo-relative file path covered by this claim?</summary>
    public static bool Covers(string kind, string value, string relPath)
    {
        if (PathLike(kind)) return value == relPath;
        if (kind == "subtree") return relPath == value || relPath.StartsWith(value + "/");
        return false;   // symbols do not gate file writes
    }
}
