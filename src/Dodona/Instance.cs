using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dodona;

/// <summary>
/// Who an instance is (design §14). Everything is scoped to the *canonical* project
/// root, and this file is the single definition of what that means — compiled into both
/// the daemon/CLI binary and the UI (as a linked source file, not an assembly reference:
/// the UI must stay runtime-decoupled and disposable, §13).
///
/// Canonicalization is load-bearing, not tidiness. One path has many spellings on
/// Windows — case, 8.3 short names, junctions, subst drives, trailing slashes, UNC — and
/// two spellings of one repo would derive two instance ids, which means two registries
/// and therefore two merge tokens over one main: exactly the race this system exists to
/// prevent, reintroduced by a string comparison. GetFinalPathNameByHandle is what the OS
/// itself considers the final path, so it collapses every spelling into one.
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

    /// <summary>The instance id: first 8 hex of SHA256 over the lowercased canonical
    /// root. Short enough to read in a pipe name, wide enough not to collide.</summary>
    public static string Id(string anyPath) =>
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
