using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Dodona.Tests;

/// <summary>
/// ONE PLACE THAT MAKES A TEMP DIRECTORY AND ONE PLACE THAT TAKES IT AWAY (issue #25).
///
/// Six fixtures in this assembly made their own directory under `%TEMP%` and four of them already
/// tried to delete it -- inside a `try { } catch { }` whose comment excused the failure ("a temp
/// dir that will not go is not a failure", "the temp dir is the OS's problem"). Those deletes were
/// failing on EVERY run and nobody knew: **one `dev test unit` leaked 25 directories**, measured
/// 2026-08-22, and 1,574 had piled up since 20 August in a `%TEMP%` holding 32,861 of them. That
/// folder is where every acceptance suite creates its sandbox, so the cost is paid by the whole
/// gate, not by the tests that caused it.
///
/// It is the same failure as issue #24 one layer down, and the same lesson: a best-effort cleanup
/// that never says it failed is a leak with a comment on it.
///
/// TWO CAUSES, BOTH MEASURED, NEITHER OBVIOUS:
///
///   1. `Microsoft.Data.Sqlite` POOLS CONNECTIONS. `store.Dispose()` returns the connection to the
///      pool instead of closing the file, so `store.db` is still open when the delete runs -- the
///      handle belongs to a pool the test cannot see. `ClearAllPools()` is what actually closes
///      it. This is why `dodona-reg-*`, `dodona-lanesink-*` and `dodona-compress-*` leaked despite
///      having a `Dispose` that looked right.
///   2. GIT WRITES READ-ONLY OBJECT FILES. `Directory.Delete(recursive: true)` throws
///      `UnauthorizedAccessException` on the first one, so any fixture holding a real repository
///      could never remove itself. `dodona-sweeptest-*` and `dodona-git-*` are that.
///
/// AND IT SWEEPS WHAT EARLIER RUNS LEFT, for the reason issue #24 landed on: fixing the cause
/// helps the next run and does nothing about the pile already there, and a machine nobody cleans
/// is the state this was found in. Bounded to the prefixes this assembly owns and to directories
/// older than the cutoff -- never `dodona-*`, which would take `dodona-prove`'s live worktree
/// cache and a concurrent test process's home with it.
/// </summary>
static class TempTree
{
    /// <summary>The prefixes THIS assembly creates. An explicit list rather than `dodona-*`:
    /// `dodona-prove`, `dodona-publish` and `dodona-from` belong to the tooling and to the
    /// product, are live while a proof or a publish is running, and are not ours to remove.</summary>
    static readonly string[] Owned =
    {
        "dodona-reg-", "dodona-lanesink-", "dodona-compress-", "dodona-canon-",
        "dodona-sweeptest-", "dodona-unit-home-",
    };

    /// <summary>Old enough that no live test process can still be using it. A unit run is seconds;
    /// this is generous so a suite running beside another cannot lose its directory mid-test.</summary>
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    internal static string New(string tag) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), tag + Guid.NewGuid().ToString("N")[..12])).FullName;

    /// <summary>Remove it, and say whether it went. Callers keep passing this through their
    /// `Dispose`; what changed is that it now succeeds.</summary>
    internal static bool Delete(string path)
    {
        if (!Directory.Exists(path)) return true;
        // Cause 1. Cheap, idempotent, and the only thing that actually releases a pooled handle.
        // Called on every delete rather than only where a Store is suspected: a fixture that grows
        // a database later must not silently start leaking again.
        try { SqliteConnection.ClearAllPools(); } catch { }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { Unprotect(path); Directory.Delete(path, recursive: true); return true; }
            catch when (attempt < 3) { System.Threading.Thread.Sleep(50 * attempt); }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Cause 2: clear the read-only bit git sets on every loose object, or the recursive
    /// delete throws on the first one it meets.</summary>
    static void Unprotect(string path)
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            try { var a = File.GetAttributes(f); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly); }
            catch { }
    }

    /// <summary>
    /// What earlier runs left, swept from <paramref name="root"/>. Returns the counts, and IS NOT
    /// SILENT ABOUT IT BY BEING CHECKED -- the first draft of this printed a line and claimed that
    /// was the reporting half, which was false: `Console.WriteLine` from a module initializer never
    /// reaches `dotnet test`'s output at all, so the claim would have sat here being untrue exactly
    /// as long as nobody read it back (CLAUDE.md §3, §7). A returned count that four facts assert
    /// on is the version that cannot quietly stop being true.
    /// </summary>
    internal static (int Gone, int Stuck) Sweep(string root, DateTime cutoffUtc, string? mine)
    {
        int gone = 0, stuck = 0;
        if (!Directory.Exists(root)) return (0, 0);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            // Never the caller's own home, and never a prefix this assembly does not own.
            if (mine is { Length: > 0 } && string.Equals(name, mine, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Owned.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try { if (Directory.GetCreationTimeUtc(dir) > cutoffUtc) continue; } catch { continue; }
            if (Delete(dir)) gone++; else stuck++;
        }
        return (gone, stuck);
    }

    /// <summary>A module initializer for the same reason <see cref="UnitHome"/> is one: it runs
    /// once, before any type in this assembly is touched, so it cannot race a test. It never fails
    /// the run -- a machine that will not let go of a directory is not a red test.</summary>
    [ModuleInitializer]
    internal static void SweepStaleAtStartup()
    {
        try
        {
            Sweep(Path.GetTempPath(), DateTime.UtcNow - Stale,
                  Path.GetFileName(Environment.GetEnvironmentVariable("DODONA_HOME") ?? ""));
        }
        catch { /* a sweep must never stop the tests from running */ }
    }
}
