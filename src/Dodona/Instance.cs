using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dodona;

/// <summary>
/// Who an instance is (design §14, as revised by docs/WORKSPACES-CONCIERGE.md §1/§3).
/// Compiled into both the daemon/CLI binary and the UI (as a linked source file, not an
/// assembly reference: the UI must stay runtime-decoupled and disposable, §13).
///
/// **Identity is now the workspace id**, not a hash of a project root. A workspace is
/// named, not located, so there is no path to hash — the pipe names and the OS mutex key
/// off the registry's generated slug (see <see cref="Registry.NewId"/>). Every helper
/// below that takes an `id` takes that slug; nothing about the pipe namespace changed
/// shape, only where the id comes from.
///
/// Canonicalization did NOT go away with the hash, and that is the important part.
/// One path has many spellings on Windows — case, 8.3 short names, junctions, subst
/// drives, trailing slashes, UNC — and two spellings of one repo used to derive two
/// instance ids, which meant two registries and therefore two merge tokens over one main:
/// exactly the race this system exists to prevent, reintroduced by a string comparison.
/// The hash is gone, so that structural guarantee is gone with it, and it has been
/// replaced deliberately: <see cref="Canonical"/> is what the registry dedupes MEMBERS by,
/// and repo-exclusivity (Registry's three enforcement layers) is what now carries the
/// "one merge token per main" invariant. GetFinalPathNameByHandle is still the OS's own
/// answer to "what is the final path", which is why it collapses every spelling into one.
/// </summary>
static class Instance
{
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            // FILE_FLAG_BACKUP_SEMANTICS (0x02000000) is required to open a DIRECTORY handle.
            using var h = CreateFileW(full, 0, 0x00000007, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!h.IsInvalid)
            {
                var buf = new char[1024];
                uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0);
                if (n > 0 && n < buf.Length)
                {
                    var resolved = new string(buf, 0, (int)n);
                    if (resolved.StartsWith(@"\\?\UNC\")) resolved = @"\\" + resolved[8..];
                    else if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
                    full = resolved;
                }
            }
        }
        catch { /* not yet created, or a filesystem that will not say: GetFullPath stands */ }
        return full.TrimEnd('\\', '/');
    }

    /// <summary>
    /// The PRE-WORKSPACE instance id: first 8 hex of SHA256 over the lowercased canonical
    /// root. Kept for exactly one job — migration needs to know whether a daemon of the
    /// old shape is still running over a store it is about to move, and the only way to
    /// ask is to recompute the pipe name that daemon would be holding. Nothing else may
    /// call this: deriving identity from a path is the thing workspaces replaced.
    /// </summary>
    public static string LegacyId(string anyPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(anyPath).ToLowerInvariant())))[..8].ToLowerInvariant();

    public static string CtlPipe(string id) => $"dodona-{id}-ctl";
    public static string UiPipe(string id) => $"dodona-{id}-ui";
    public static string HandoffPipe(string id) => $"dodona-{id}-handoff";
    public static string UiHandoffPipe(string id) => $"dodona-{id}-uihandoff";
    public static string LanePipe(string id, long laneId) => $"dodona-{id}-lane{laneId}";

    /// <summary>Every instance running right now, found the way Windows lets you: the
    /// pipe namespace is a directory. No shared registry, no lock file, nothing global
    /// (§14) — liveness is read off the OS instead of stored.</summary>
    public static List<string> LiveCtlPipes() => LivePipes("-ctl");

    /// <summary>Every UI running right now. A UI is a separate process from its daemon
    /// and can outlive or predate it, so publish has to find them the same way and
    /// refresh them separately (§13) — a swapped daemon behind a stale window is the one
    /// combination that looks like nothing happened.</summary>
    public static List<string> LiveUiPipes() => LivePipes("-ui");

    static List<string> LivePipes(string suffix)
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith("dodona-") && n.EndsWith(suffix))
                .Select(n => n!)
                .Distinct()
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public static bool IsLive(string id) => LiveCtlPipes().Contains(CtlPipe(id), StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}
