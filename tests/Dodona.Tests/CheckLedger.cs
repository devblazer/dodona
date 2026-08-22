using System.Collections.Immutable;
// System.IO EXPLICITLY: this file is linked into tests\Dodona.Ui.Tests, and WPF's implicit
// usings OMIT System.IO (CLAUDE.md 0.2). It compiles here and not there, which is the shape of
// bug a linked file is uniquely good at producing.
using System.IO;

namespace Dodona.Testing.Ledger;

/// <summary>
/// The C# side of the check-name ledger: A READER OF TRACKED ARTEFACTS, AND NOTHING ELSE.
///
/// ══ IT NEVER PARSES A .ps1, AND THAT IS D-T6 ══
///
/// Two independent parsers of the same check names -- one in PowerShell for `dev ledger`, one in
/// C# for the <c>Wire</c> field -- would be two hand copies of one thing, which is the exact
/// failure docs/TEST-ARCHITECTURE-PLAN.md exists to prevent. So there is ONE parser:
/// `dev ledger`, in PowerShell, because `tools\dev.ps1` must run on a tree that will not compile
/// (CLAUDE.md 1). It validates the sources and maintains these TSVs. This class reads the
/// artefact.
///
/// The staleness that arrangement could hide -- a check deleted from a `.ps1` while its TSV row
/// survives -- is caught on the OTHER side, by `dev ledger`'s reachability rung, which since W4
/// runs inside Repo-Lint (I8) and is therefore asserted by `dev gate` without adding an eleventh
/// assertion (D-T23). Neither half is sufficient alone; together there is no unparsed gap.
///
/// TSV and not JSON, for a reason this repo has paid for three times: `ConvertFrom-Json` emits a
/// JSON array as ONE pipeline item (CLAUDE.md 0.2), and the PowerShell half has to read the same
/// files. ASCII, CRLF, no BOM -- `Ledger-ReadTsv` asserts all three, so this reader may be plain.
/// </summary>
static class CheckLedger
{
    /// <summary>One wire, as `tests\ledger\wires.tsv` records it.</summary>
    internal sealed class WireRow
    {
        public string WireId = "";
        public string OwnerSuite = "";
        public string OwnerCheck = "";
        public string OwnerBodySha = "";
        public string WhatItProves = "";
        public string WhyRealMachinery = "";
    }

    /// <summary>One row of `tests\ledger\double-assemblies.tsv`: which assemblies rung 2 loads,
    /// and which test project loads them.</summary>
    internal sealed class AssemblyRow
    {
        public string Project = "";
        public string Assembly = "";
        public string Rung2 = "";
        public string Note = "";
    }

    /// <summary>
    /// The repository root, found by WALKING UP from the test binary rather than by a relative
    /// path constant. `dev prove --with` runs `Run-Unit -Root $wt` against a THROWAWAY WORKTREE
    /// of HEAD, so the binary's location is not where anyone would guess -- and a hard-coded
    /// `..\..\..\..` is a hand copy of the build layout, which the next SDK change breaks
    /// silently.
    /// </summary>
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "tests", "ledger", "wires.tsv"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException(
            "could not find tests\\ledger\\wires.tsv above " + AppContext.BaseDirectory +
            " -- the ledger reader is not optional and a missing artefact must never read as an empty one");
    }

    public static string LedgerDir => Path.Combine(RepoRoot, "tests", "ledger");

    public static ImmutableArray<WireRow> Wires { get; } = ReadWires();

    public static ImmutableArray<AssemblyRow> DoubleAssemblies { get; } = ReadAssemblies();

    /// <summary>Every check name in the frozen census. Ordinal, because `A_x` and `a_x` are
    /// different names and merging them is never right -- the first capture lost two names to a
    /// case-insensitive hashtable and nothing said so (tests\ledger\README.md).</summary>
    public static ImmutableHashSet<string> BaselineChecks { get; } = ReadBaseline();

    /// <summary>The wires.tsv row a `[Double]`'s `Wire = "&lt;suite&gt;:&lt;check&gt;"` names, or
    /// null. Null is the answer a caller must handle: it means the register moved out from under
    /// the double, which is the whole thing `Wire` is for.</summary>
    public static WireRow? ResolveWire(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return null;
        var at = wire.IndexOf(':');
        if (at <= 0 || at == wire.Length - 1) return null;
        var suite = wire.Substring(0, at);
        var check = wire.Substring(at + 1);
        foreach (var w in Wires)
            if (w.OwnerSuite == suite && w.OwnerCheck == check) return w;
        return null;
    }

    // ---- the reader. Comment lines start '#', the first non-comment line is the header. ----
    static IEnumerable<string[]> Rows(string file, int columns)
    {
        var path = Path.Combine(LedgerDir, file);
        if (!File.Exists(path)) yield break;
        var header = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimStart('\uFEFF');          // GateHook's incident: a BOM arrives as a character
            if (line.Length == 0 || line[0] == '#') continue;
            if (!header) { header = true; continue; }
            var f = line.Split('\t');
            if (f.Length != columns) continue;           // Ledger-ReadTsv refuses it; here it is not ours to judge
            yield return f;
        }
    }

    static ImmutableArray<WireRow> ReadWires()
    {
        var b = ImmutableArray.CreateBuilder<WireRow>();
        foreach (var f in Rows("wires.tsv", 6))
            b.Add(new WireRow
            {
                WireId = f[0], OwnerSuite = f[1], OwnerCheck = f[2],
                OwnerBodySha = f[3], WhatItProves = f[4], WhyRealMachinery = f[5],
            });
        return b.ToImmutable();
    }

    static ImmutableArray<AssemblyRow> ReadAssemblies()
    {
        var b = ImmutableArray.CreateBuilder<AssemblyRow>();
        foreach (var f in Rows("double-assemblies.tsv", 4))
            b.Add(new AssemblyRow { Project = f[0], Assembly = f[1], Rung2 = f[2], Note = f[3] });
        return b.ToImmutable();
    }

    static ImmutableHashSet<string> ReadBaseline()
    {
        var b = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var f in Rows("baseline.tsv", 3)) b.Add(f[0]);
        return b.ToImmutable();
    }
}
