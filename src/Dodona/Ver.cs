using System.IO;                 // explicit: this file also compiles into the WPF project,
                                 // whose implicit usings are narrower than the console one's

namespace Dodona;

/// <summary>
/// What a binary is, and what it is compatible with (design §13/§14). Three numbers
/// decide whether a running system can swap to a new build without a beat:
///
///   Schema        — the store shape this binary expects. A build that would MIGRATE
///                   the live store is not hot-swappable: a half-applied migration is
///                   not undoable with a keystroke, so it must be asked about (§14).
///                   Must equal the highest migration in Store.Migrate.
///   ShimProtocol  — the wire the shims speak. Live shims were spawned by the OLD
///                   binary; a successor that speaks a different protocol would orphan
///                   every running agent. Also not hot-swappable.
///   Build         — identity, not compatibility: assembly version + the build stamp of
///                   the image on disk. This is what `dodona status` reports and what a
///                   swap test asserts changed.
/// </summary>
static class Ver
{
    public const int Schema = 8;
    public const int ShimProtocol = 1;

    public static string Build { get; } = Compute();
    public static string ExePath => Environment.ProcessPath ?? AppContext.BaseDirectory;

    /// <summary>Where published builds live. Machine-wide on purpose: a published build
    /// swaps into every running instance at once (§14), so it cannot live under one
    /// project root. Overridable for tests.</summary>
    public static string BinRoot =>
        Environment.GetEnvironmentVariable("DODONA_BIN_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dodona", "bin");


    // ---------------------------------------------------------- what a build was built FROM

    /// <summary>The newest source timestamp in a Dodona tree — the thing auto-publish asks
    /// "am I behind?" about. Lives here, not in the daemon, because BOTH sides of that
    /// question need the identical definition: the watcher computes it, and publish stamps
    /// it into the build so the comparison is like-for-like.</summary>
    public static DateTime NewestSource(string project)
    {
        var newest = DateTime.MinValue;
        var src = Path.Combine(project, "src");
        if (Directory.Exists(src))
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (!(f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj"))) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
        var dj = Path.Combine(project, "dodona.json");
        if (File.Exists(dj) && File.GetLastWriteTimeUtc(dj) > newest) newest = File.GetLastWriteTimeUtc(dj);
        return newest;
    }

    /// <summary>The name of the stamp publish leaves beside the binaries.</summary>
    public const string BuiltFromFile = ".built-from";

    /// <summary>Record the source snapshot a published directory was built from.
    ///
    /// This exists because the obvious comparison — newest source vs. the mtime of the
    /// running dodona.exe — is not like-for-like, and looped forever on 2026-08-18.
    /// `NewestSource` spans ALL THREE projects, while the image is only ONE of them: edit
    /// `src\DodonaUi\MainWindow.xaml.cs` and MSBuild correctly skips the up-to-date Dodona
    /// project, the publish copy preserves LastWriteTime, and dodona.exe's mtime can never
    /// catch up. The condition stays true forever. Measured: 64 auto-publishes and 72 daemon
    /// restarts in one afternoon, one full three-project build every ~65 seconds, four
    /// consecutive swaps reporting the byte-identical `sources 15:56:19 > image 15:55:55`.
    ///
    /// Stamp it BEFORE building, never after: an edit that lands mid-build is genuinely not
    /// in this build, and claiming it is would swallow the operator's change silently. The
    /// honest error is one extra publish, not a lost edit.</summary>
    public static void WriteBuiltFrom(string outDir, DateTime builtFrom)
    {
        try { File.WriteAllText(Path.Combine(outDir, BuiltFromFile), builtFrom.ToString("o")); }
        catch { /* a missing stamp degrades to the legacy mtime compare, never to a crash */ }
    }

    /// <summary>What the source tree looked like when THIS running image was built. Falls
    /// back to the image's own mtime for a build published before stamps existed (and for
    /// `publish --exe <prebuilt>`, where nobody knows what it was built from) — the old,
    /// loop-prone behaviour, but only for builds that predate the fix.</summary>
    public static DateTime ImageBuiltFrom(string exePath)
    {
        try
        {
            var f = Path.Combine(Path.GetDirectoryName(exePath) ?? ".", BuiltFromFile);
            if (File.Exists(f) && DateTime.TryParse(File.ReadAllText(f).Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return t.ToUniversalTime();
            return File.GetLastWriteTimeUtc(exePath);
        }
        catch { return DateTime.MinValue; }
    }

    static string Compute()
    {
        var asm = typeof(Ver).Assembly;
        var v = asm.GetName().Version?.ToString(3) ?? "0.0.0";
        string stamp;
        try { stamp = File.GetLastWriteTimeUtc(asm.Location).ToString("yyyyMMddHHmmss"); }
        catch { stamp = "unknown"; }
        return $"{v}+{stamp}";
    }
}
