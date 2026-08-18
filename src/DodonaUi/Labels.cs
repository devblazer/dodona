using System.IO;

namespace DodonaUi;

/// <summary>
/// The shortest name that still says which project this is (ORCHESTRATOR-REVIEW,
/// "Carried into M5"): the deepest folder segment normally (`dodona`, `masswork`),
/// extended leftward a segment at a time and joined with `/` only where that collides
/// (`client/src` vs `proj/src`). Computed against the set of projects the picker knows,
/// so two windows never wear the same name while a lone project keeps its plain one.
/// </summary>
static class Labels
{
    public static string For(string path, IEnumerable<string> allKnown)
    {
        var mine = Segments(path);
        var others = allKnown
            .Select(Segments)
            .Where(s => !s.SequenceEqual(mine, StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (int k = 1; k <= mine.Length; k++)
        {
            var candidate = Suffix(mine, k);
            if (!others.Any(o => Suffix(o, k).Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return Suffix(mine, mine.Length);
    }

    static string[] Segments(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    static string Suffix(string[] segs, int k) =>
        string.Join("/", segs.TakeLast(Math.Min(k, segs.Length)));
}
