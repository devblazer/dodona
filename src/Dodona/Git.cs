using System.Diagnostics;

namespace Dodona;

/// <summary>Thin git runner. Git is the truth for git (design §12) — the store only
/// caches what these calls report.</summary>
static class Git
{
    public static (int Code, string Out) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = Task.Run(() => p.StandardError.ReadToEnd());
        var so = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var se = errTask.Result;
        return (p.ExitCode, (so + se).Trim());
    }

    /// <summary>HEAD sha of a ref; throws if the ref does not resolve.</summary>
    public static string Sha(string repo, string @ref)
    {
        var (code, output) = Run(repo, "rev-parse", @ref);
        if (code != 0) throw new InvalidOperationException($"rev-parse {@ref}: {output}");
        return output;
    }

    /// <summary>The same resolution, but "there is no answer" is a VALUE rather than an
    /// exception: an empty string.
    ///
    /// This exists because <see cref="Sha"/>'s throw reached a caller that had already been
    /// written to expect emptiness. `publish` resolves HEAD and main up front, then decides
    /// `haveProvenance = head.Length == 40` and carries a message for the negative case
    /// ("provenance: NONE -- ... is not a git repository"). That message was UNREACHABLE:
    /// Sha threw first, so publishing from a folder that is not a repository died with a raw
    /// InvalidOperationException stack trace instead of the sentence the author intended.
    /// tests/publish-acceptance.ps1 had been red on it, which is how it was found.
    ///
    /// Sha keeps throwing, deliberately: a caller asking for the merge base of a ticket it is
    /// about to land must not receive "" and carry on. The choice belongs at the call site,
    /// so there are two functions rather than one with a flag.</summary>
    public static string ShaOrEmpty(string repo, string @ref)
    {
        try
        {
            var (code, output) = Run(repo, "rev-parse", @ref);
            return code == 0 ? output.Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Is this directory itself the top of a work tree? Asked of git rather than
    /// guessed from a `.git` entry, because a worktree checkout's `.git` is a file and a
    /// subdirectory of a repo would answer yes to any naive test.</summary>
    public static bool IsRepo(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        var (code, output) = Run(dir, "rev-parse", "--show-toplevel");
        if (code != 0) return false;
        return Instance.Canonical(output.Trim()).Equals(Instance.Canonical(dir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does it have a commit yet? An empty repo has no branch to worktree from,
    /// which is a different problem with a different fix than having no repo at all.</summary>
    public static bool HasCommit(string repo) => Run(repo, "rev-parse", "--verify", "HEAD").Code == 0;

    /// <summary>Repositories sitting inside a folder — the workspace case, where the
    /// thing you point Dodona at is a container and the repos are underneath it. Shallow
    /// by design: a deep scan of a source tree is slow and finds vendored junk.</summary>
    public static List<string> FindRepos(string root, int maxDepth = 2)
    {
        var found = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dodona", ".git", "node_modules", "bin", "obj", "packages", "vendor", ".vs", ".idea", "target", "dist" };

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            IEnumerable<string> children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || skip.Contains(name)) continue;
                if (IsRepo(child)) { found.Add(child); continue; }   // a repo's insides are its own business
                Walk(child, depth + 1);
            }
        }

        Walk(root, 1);
        return found;
    }

    /// <summary>
    /// A detached worktree that removes itself. Publish uses it for <c>--from &lt;ref&gt;</c>
    /// and the drift watcher for the commit it is catching up to (RECOVERY-PHASES P2.3): both
    /// must build a specific commit in a tree of their OWN, never the live one an operator or
    /// another session is working in.
    ///
    /// It is <c>IDisposable</c> and used with <c>using</c> on purpose. Publish has a dozen
    /// early <c>return Fail(...)</c> paths, and a cleanup that has to be repeated at each of
    /// them is a leak waiting for the one that gets forgotten -- which in this codebase means
    /// a worktree nobody removes, quietly holding a checkout of an old commit.
    /// </summary>
    public sealed class TempWorktree : IDisposable
    {
        readonly string _repo;
        /// <summary>Where a cleanup failure gets SAID (issue #24). Optional so the no-op
        /// instance and every existing caller stay unchanged.</summary>
        readonly Action<string>? _say;

        /// <summary>The checkout, or null when there is none -- either nothing was asked for,
        /// or the checkout failed and <see cref="Error"/> says why.</summary>
        public string? Path { get; }

        /// <summary>Git's complaint when <see cref="For"/> could not check the ref out.</summary>
        public string Error { get; }

        TempWorktree(string repo, string? path, string error, Action<string>? say = null) { _repo = repo; Path = path; Error = error; _say = say; }

        /// <summary>Nothing to do (returns a no-op) when <paramref name="spec"/> is null or is
        /// already a directory -- <c>--from &lt;worktree&gt;</c> is a tree the caller owns and
        /// must NOT be deleted by us.</summary>
        public static TempWorktree None(string repo) => new(repo, null, "", null);

        /// <summary>Check <paramref name="sha"/> out detached, under the temp directory. On
        /// failure it returns an instance whose <see cref="Path"/> is null and whose
        /// <see cref="Error"/> holds git's complaint -- never null, and never an `out`
        /// parameter, so the caller can keep this in a `using` inside a conditional
        /// expression (which is what an `out` here made impossible: CS0165).</summary>
        public static TempWorktree For(string repo, string sha, string stamp, Action<string>? say = null)
        {
            // `Paths.Home`, NOT `Path.GetTempPath()`. Its own comment is the rule this was
            // breaking: "DODONA_HOME redirects the whole tree ... every acceptance suite must be
            // able to [work] WITHOUT touching [what] the operator is using right now (§17)". A
            // throwaway build tree is Dodona's own state (§5) and it was landing in the machine's
            // real %TEMP% for every caller including the suites -- which is why nothing could test
            // the sweep below without risking a force-delete of the operator's own leftovers.
            // Nothing writes to `%TEMP%\dodona-from` after this commit; the thirteen already there
            // were removed by hand in the same change.
            var parent = System.IO.Path.Combine(Paths.Home, "from");
            Sweep(repo, parent, stamp, say);
            var dir = System.IO.Path.Combine(parent, stamp);
            var (code, output) = Run(repo, "worktree", "add", "--detach", dir, sha);
            return code == 0 ? new TempWorktree(repo, dir, "", say) : new TempWorktree(repo, null, output, say);
        }

        /// <summary>
        /// TAKE OUT WHAT AN EARLIER PUBLISH COULD NOT (issue #24).
        ///
        /// <see cref="Dispose"/> is best-effort by design, and every failure it had was permanent:
        /// nothing said so and nothing tried again, so one stuck delete became one leftover PER
        /// PUBLISH, forever. Thirteen were found on this machine holding 103 MB — and the disk was
        /// never the point. Git counts every one as a real checkout of the repository, so
        /// `git worktree list` answered "where is this repo checked out" with thirteen fictional
        /// entries against two real ones, and that question has already cost this repo a bad
        /// commit once (§0.0).
        ///
        /// SWEEPING ON THE NEXT RUN IS WHAT MAKES IT SELF-HEALING, and it works because the holder
        /// is TRANSIENT — measured 2026-08-22 rather than assumed, which the ticket asked for:
        /// `git worktree remove --force` on a two-day-old leftover succeeded instantly, exit 0.
        /// The publish builds INTO that tree, so MSBuild's reusable build nodes hold handles in its
        /// `obj\` for a while after the build returns; by the next publish they are long gone.
        /// A retry inside one publish would therefore have to outwait node reuse (15 minutes by
        /// default) to buy what one line here buys for nothing.
        ///
        /// THE AGE GUARD IS NOT TIDINESS — it is the only thing making a CONCURRENT publish safe.
        /// The stamp is a timestamp, so two publishes close together are two live directories, and
        /// a sweep with no guard would delete a tree another process is mid-build in. Fifteen
        /// minutes is deliberately the same number as node reuse: younger than that and the remove
        /// would very likely fail anyway, so nothing is lost by leaving it to the next run.
        /// </summary>
        internal static void Sweep(string repo, string parent, string mine, Action<string>? say)
        {
            if (!Directory.Exists(parent)) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            int gone = 0, stuck = 0;
            foreach (var dir in Directory.GetDirectories(parent))
            {
                if (System.IO.Path.GetFileName(dir) == mine) continue;
                try { if (Directory.GetCreationTimeUtc(dir) > cutoff) continue; } catch { continue; }
                // git first: it removes the administrative record too, which `prune` will NOT do
                // for a directory that still exists (the other half of why these accumulated).
                if (Run(repo, "worktree", "remove", "--force", dir).Code == 0) { gone++; continue; }
                try { Directory.Delete(dir, recursive: true); gone++; } catch { stuck++; }
            }
            if (gone > 0)
            {
                Run(repo, "worktree", "prune");
                say?.Invoke($"swept {gone} leftover build tree(s) from {parent}");
            }
            if (stuck > 0) say?.Invoke($"could not sweep {stuck} leftover build tree(s) in {parent} -- still held; the next publish will try again");
        }

        public void Dispose()
        {
            if (Path is null) return;
            // Best effort: a failure here must never turn a good publish into a bad exit code.
            // BUT IT MUST NOT BE SILENT ABOUT IT (issue #24). `Run` returns an exit code and this
            // discarded it, so `git worktree remove` failing was not an exception, never reached
            // the `catch`, and nobody has ever known it was happening -- the same silence at the
            // wire as issue #9's ANSWERED NOTHING, one directory over. And `prune` does not cover
            // it: prune drops the record for a worktree whose DIRECTORY IS GONE, and these are
            // still there, so it correctly leaves them and the entry survives.
            try
            {
                var (code, output) = Run(_repo, "worktree", "remove", "--force", Path);
                Run(_repo, "worktree", "prune");
                if (code != 0)
                    _say?.Invoke($"could not remove the temporary build tree {Path} " +
                                 $"({output.Trim()}) -- the publish is fine; the next one sweeps it");
            }
            catch (Exception ex) { _say?.Invoke($"could not remove the temporary build tree {Path} ({ex.Message})"); }
        }
    }
}
