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
    public const int Schema = 7;
    public const int ShimProtocol = 1;

    public static string Build { get; } = Compute();
    public static string ExePath => Environment.ProcessPath ?? AppContext.BaseDirectory;

    /// <summary>Where published builds live. Machine-wide on purpose: a published build
    /// swaps into every running instance at once (§14), so it cannot live under one
    /// project root. Overridable for tests.</summary>
    public static string BinRoot =>
        Environment.GetEnvironmentVariable("DODONA_BIN_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dodona", "bin");

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
