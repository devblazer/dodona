using System.IO;                 // explicit: this file also compiles into the WPF project,
                                 // whose implicit usings are narrower than the console one's
using System.Reflection;         // AssemblyInformationalVersionAttribute -- the build's provenance

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
///   Build         — IMAGE identity, not compatibility and not provenance: assembly version
///                   plus the mtime stamp of the image on disk. It stays a stamp because its
///                   job is to tell two publishes apart, and two publishes of the same commit
///                   are genuinely different images. WHAT THE BUILD WAS MADE FROM is a
///                   separate question with a separate answer -- `Commit`/`Provenance` below,
///                   which is what `status` reports and what the drift watcher compares
///                   (P2.6: "a timestamp mapping to nothing" was the complaint, and the fix
///                   is to ADD the commit, not to lose image identity).
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


    // ------------------------------------------------- a build output is not an installation

    /// <summary>
    /// Is this image a SOURCE TREE build output - the <c>...\src\&lt;project&gt;\bin\...</c> shape?
    ///
    /// A daemon started from one holds the very file MSBuild must overwrite, and it outlives
    /// the window that started it, so the block is invisible: on 2026-08-18 four of them
    /// turned a fifteen-minute change into an hour, reported as "Build FAILED" when what it
    /// meant was "an invisible daemon holds a file" (docs/INVESTIGATION-2026-08-18.md RC3).
    ///
    /// The test is the <b>src+bin shape</b>, deliberately NOT "any path containing \bin\",
    /// and that distinction is load-bearing - the two places a daemon SHOULD run from both
    /// contain a \bin\, so keying on that alone would refuse both and take the whole system
    /// down with it. Deliberately ALLOWED:
    ///
    ///   %LOCALAPPDATA%\Dodona\bin\&lt;stamp&gt;   the installed app (<see cref="BinRoot"/>);
    ///                                       its `bin` sits under `Dodona`, under `Local` -
    ///                                       no `src` two components up, so it never matches
    ///   $DODONA_HOME\bin                    the suites' own copy of the binaries
    ///                                       (tests/_workspace.ps1, Use-TestBinaries)
    ///
    /// Refused: <c>...\src\Dodona\bin\Release\net8.0</c> and its siblings - including inside a
    /// lane worktree (<c>...\.dodona\wt\t7\src\...\bin\...</c>), which is a source tree like
    /// any other.
    /// </summary>
    public static bool IsSourceTreeBuildOutput(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            var parts = Path.GetFullPath(exePath).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            // Three CONSECUTIVE components: src / <project> / bin.
            for (int i = 2; i < parts.Length; i++)
                if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    parts[i - 2].Equals("src", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch { return false; }   // an unparseable path is not evidence of anything
    }

    /// <summary>Why we would not daemonize, and what to do instead. One text, because the
    /// refusal fires from three spawn sites and whoever meets it should not have to work out
    /// which one they hit.
    ///
    /// This refuses to CREATE a long-lived process, never to clean one up. <c>stop-daemon</c>,
    /// <c>stop-all</c>, <c>ps</c>, <c>where</c>, <c>publish</c> and every other verb keep
    /// working from any path, always - a guard standing between the operator and a cleanup is
    /// a bug, not a safety feature (CLAUDE.md 0.1). An explicit <c>dodona daemon</c> still
    /// runs from anywhere too; it is AUTOSTART that refuses, because autostart is the
    /// invisible one.</summary>
    public static string BuildOutputRefusal(string exePath, string what) =>
        $"refusing to start {what} from a build output: {exePath}" + Environment.NewLine +
        "  A build output is not an installation. A daemon started there holds the file the" + Environment.NewLine +
        "  compiler must overwrite, and it outlives the window that started it, so the block" + Environment.NewLine +
        "  is invisible (2026-08-18: four of them, one hour, reported as \"Build FAILED\")." + Environment.NewLine +
        "  Publish, then run the published binary:" + Environment.NewLine +
        @"    powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev.ps1 ship" + Environment.NewLine +
        $"    {Path.Combine(BinRoot, "<newest>", "dodona.exe")} ..." + Environment.NewLine +
        "  `dodona daemon` still starts one explicitly from any path, and stop-daemon, stop-all," + Environment.NewLine +
        "  ps, where and publish are unaffected.";


    // ------------------------------------------------------- what commit a build was made FROM

    /// <summary>
    /// The commit a published build was made from, carried INSIDE the binary.
    ///
    /// THIS REPLACED FOUR MTIME HELPERS AND FIVE GUARDS (RECOVERY-PHASES P2.3/P2.4). The drift
    /// watcher used to ask "is any source file newer than the running image?" -- a question no
    /// filesystem can answer exactly. It needed a debounce so a half-saved edit was not built,
    /// a <c>.built-from</c> stamp because the newest source spans three projects while the
    /// image is one of them, a persisted <c>kv.autopublish_last_tried</c> because an
    /// in-process guard is reset by the swap it triggers, and a 30-minute dirty-tree nag. The
    /// asymmetry still looped 64 times in one afternoon: 72 daemon restarts, a full
    /// three-project build every ~65 seconds, four consecutive swaps reporting the
    /// byte-identical <c>sources 15:56:19 &gt; image 15:55:55</c>.
    ///
    /// <c>git rev-parse main</c> against the SHA this build was made from is EXACT: no clock,
    /// no partial-write window, no project asymmetry. A commit is atomic and already quiet, so
    /// the debounce is unnecessary; the SHA is its own guard, so lastTried is unnecessary.
    ///
    /// IN THE ASSEMBLY, not in a file beside it, and that is deliberate. A side stamp can go
    /// missing (and did: <c>publish --exe</c> never had one), and a missing stamp used to
    /// DEGRADE to the loop-prone mtime compare -- a silent fallback into the exact bug. An
    /// image that cannot say what it was built from now says so out loud instead, and the
    /// watcher refuses to guess.
    ///
    /// Format, after the '+' of InformationalVersion. Written by <see cref="Stamp"/> and read
    /// here, so the two can never drift apart:
    /// <code>
    ///   c=&lt;sha&gt;~d=&lt;0|1&gt;~m=&lt;mainSha&gt;~b=&lt;branch&gt;
    /// </code>
    /// TILDE, and not a comma, MEASURED THE HARD WAY: the dotnet CLI splits a <c>-p:k=v</c>
    /// value on commas, so a comma-separated stamp arrived as <c>c=&lt;sha&gt;</c> with
    /// everything after the first comma silently dropped -- trial detection and the main
    /// baseline both gone, with no error anywhere. Git forbids <c>~</c> in a ref name
    /// (git-check-ref-format), so it cannot collide with a branch either, which is what lets
    /// <c>b=</c> sit LAST and run to the end of the string.
    ///
    /// A build with no <c>c=</c> has NO Dodona provenance, and every consumer must treat that
    /// as "unknown", never as "behind". That check is deliberately for OUR marker rather than
    /// merely for a non-empty suffix: the .NET SDK puts a bare commit SHA there by itself, and
    /// mistaking it for provenance would look like an answer while saying nothing about which
    /// branch, which baseline, or whether the tree was clean.
    /// </summary>
    public static string Provenance { get; } = ReadProvenance();

    /// <summary>The commit this build was made from; empty when unknown.</summary>
    public static string Commit => Field("c");

    /// <summary>Where <c>main</c> stood when this build was made. For a main build that is the
    /// same as <see cref="Commit"/>; for a TRIAL it is the baseline the trial was cut against,
    /// which is what lets "the next commit to main replaces the trial" (P2.5) work without any
    /// remembered state: the binary carries its own baseline, so nothing survives a handoff
    /// wrongly and nothing has to be reset.</summary>
    public static string MainBaseline => Field("m") is { Length: > 0 } m ? m : Commit;

    /// <summary>The branch this build was made from; empty when unknown.</summary>
    public static string Branch => Field("b");

    /// <summary>Was the tree dirty when this was built? Then the SHA does not fully describe
    /// the binary, and saying <c>build=&lt;sha&gt;</c> alone would be a small lie.</summary>
    public static bool Dirty => Field("d") == "1";

    /// <summary>A build whose commit is not <c>main</c> -- a deliberate trial (P2.5/D-1).</summary>
    public static bool IsTrial => Commit.Length > 0 && Field("m") is { Length: > 0 } m && m != Commit;

    /// <summary>True when this image cannot say what commit it came from.</summary>
    public static bool NoProvenance => Commit.Length == 0;

    /// <summary>One line for <c>status</c> and the swap feed. Bisectable: the SHA it prints is
    /// a commit <c>git log</c> knows (RECOVERY-PHASES P2.6).</summary>
    public static string ProvenanceLine
    {
        get
        {
            if (NoProvenance)
                return "build=unknown (no commit provenance -- built by `dev build`, or published with --exe)";
            var dirty = Dirty ? " +uncommitted-changes" : "";
            return IsTrial
                ? $"trial: {(Branch.Length > 0 ? Branch : "detached")}@{Short(Commit)}{dirty} (main was {Short(MainBaseline)})"
                : $"build={Short(Commit)}{dirty}";
        }
    }

    public static string Short(string sha) => sha.Length >= 12 ? sha.Substring(0, 12) : sha;

    /// <summary>Build the provenance value publish stamps in. Lives beside the reader on
    /// purpose: both sides of this question need the identical definition, which is the one
    /// thing the old mtime comparison never had.</summary>
    public static string Stamp(string commit, string mainSha, string branch, bool dirty) =>
        $"c={commit}~d={(dirty ? "1" : "0")}~m={mainSha}~b={branch}";

    static string ReadProvenance()
    {
        try
        {
            var iv = typeof(Ver).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(iv)) return "";
            var plus = iv.IndexOf('+');
            var suffix = plus >= 0 && plus + 1 < iv.Length ? iv.Substring(plus + 1) : "";
            // Only OUR stamp counts. The SDK writes a bare SHA here on its own.
            return suffix.StartsWith("c=", StringComparison.Ordinal) ? suffix : "";
        }
        catch { return ""; }      // an unreadable attribute is "unknown", never a crash
    }

    /// <summary>Pull one field out of the provenance string. <c>b</c> runs to the end.</summary>
    static string Field(string key)
    {
        var prov = Provenance;
        if (prov.Length == 0) return "";
        var needle = key + "=";
        var i = prov.StartsWith(needle, StringComparison.Ordinal) ? 0 : prov.IndexOf("~" + needle, StringComparison.Ordinal);
        if (i < 0) return "";
        var from = (i == 0 ? 0 : i + 1) + needle.Length;
        if (key == "b") return prov.Substring(from);          // b= runs to the end: git forbids ~ in a ref
        var sep = prov.IndexOf('~', from);
        return sep < 0 ? prov.Substring(from) : prov.Substring(from, sep - from);
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
