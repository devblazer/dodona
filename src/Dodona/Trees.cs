namespace Dodona;

/// <summary>
/// WHICH KIND OF TREE A PATH IS IN (docs/WORK-ISOLATION-PLAN.md section 3, layer 1).
///
/// Layer 1 of work isolation is unconditional and belongs to no model: no agent writes into a
/// project outside a worktree. This is the whole test, and it is deliberately the SAME stateless
/// one both existing layers already use -- `.git` is a FILE in a linked worktree and a DIRECTORY
/// in the shared checkout (`.githooks/pre-commit` refuses commits on exactly that basis, and
/// `dev.ps1` handles `--git-common-dir` for the same reason). No registry, no recorded list of
/// worktrees, nothing that can go stale while looking installed.
///
/// **It walks the ancestors instead of shelling out to `git rev-parse --show-toplevel`**, which
/// is what the plan's appendix names. Same answer, and the reason is the per-edit cost this runs
/// under: the gate is a PreToolUse hook on EVERY write of EVERY work lane, the two hooks deleted
/// in CLAUDE.md section 0.0 were deleted for costing 255 ms each, and a `git` process is tens of
/// milliseconds that a few `File.Exists` calls are not. A path that does not exist yet (a new
/// file, which is most of what an agent writes) also has no directory for `rev-parse` to run in,
/// so the walk handles the common case the process cannot.
/// </summary>
static class Trees
{
    public enum Where
    {
        /// <summary>Inside a linked git worktree -- `.git` is a file. This is the allowed one.</summary>
        Worktree,

        /// <summary>Inside a git repository's own checkout -- `.git` is a directory. THE ONE
        /// LAYER 1 REFUSES: it is the tree the operator and every other lane share, so a write
        /// here is the failure this whole plan exists to make structurally impossible.</summary>
        SharedCheckout,

        /// <summary>Inside a project, but that project is not a git repository at all -- no
        /// `.git` anywhere above the path.</summary>
        NotARepo,

        /// <summary>Under no project of this workspace.</summary>
        OutsideEveryProject,
    }

    /// <summary>Locate a path. `dirExists`/`fileExists` are injected so the algebra is testable
    /// in `dev test unit` with no filesystem at all -- a real repository costs a `git init`, and
    /// this is the decision every write in the system now passes through.</summary>
    public static Where Locate(string fullPath, IEnumerable<string> projects,
                               Func<string, bool> dirExists, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return Where.OutsideEveryProject;
        // Normalised ONCE, and before the project test: `Projects.Of` compares prefixes
        // literally, so a mixed-separator or unrooted path would miss its own project and
        // answer `OutsideEveryProject` -- an ALLOW, arrived at by a string detail.
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        if (Projects.Of(projects, full) is null) return Where.OutsideEveryProject;

        // Nearest `.git` wins, so a worktree nested inside its own project answers `Worktree`
        // rather than being swallowed by the project's checkout above it -- the same
        // longest-base-first ordering `claim-check` needs for the same reason.
        //
        // The path ITSELF is included: a write may name a directory, and excluding it would ask
        // the question one level too high.
        // `Path` does the separator handling: a literal backslash in a generated file is how
        // this project's own file-patching trap bites (CLAUDE.md 0.2), and `GetDirectoryName`
        // walks up correctly for a path that does not exist yet.
        var at = full;
        while (!string.IsNullOrEmpty(at))
        {
            var dotGit = Path.Combine(at, ".git");
            if (fileExists(dotGit)) return Where.Worktree;
            if (dirExists(dotGit)) return Where.SharedCheckout;
            var parent = Path.GetDirectoryName(at);
            if (string.IsNullOrEmpty(parent) || parent == at) break;
            at = parent;
        }
        return Where.NotARepo;
    }

    /// <summary>The live form: the real filesystem.</summary>
    public static Where Locate(string fullPath, IEnumerable<string> projects) =>
        Locate(fullPath, projects, Directory.Exists, File.Exists);

    /// <summary>Whether layer 1 lets a write through. `NotARepo` and `OutsideEveryProject` are
    /// ALLOWED, and neither is a carve-out:
    ///
    ///  * `NotARepo` -- the hazard layer 1 names is a SHARED CHECKOUT: two lanes and a human on
    ///    one set of tracked files and one branch. A project with no repository has no branch to
    ///    reassign and no commit to carry someone else's work, and it cannot be given a worktree
    ///    either (`ticket-create` needs git), so refusing here would brick every lane in such a
    ///    project with no promotion available to un-stick it -- a wait with no condition that
    ///    ends it (CLAUDE.md section 0.1).
    ///  * `OutsideEveryProject` -- a scratch file in %TEMP%, a note in the operator's home.
    ///    Left allowed deliberately and flagged as open in the plan's section 10: refusing it may
    ///    be right, and only use will show. `claim-check` still refuses it for a TICKET lane, so
    ///    this changes nothing there.</summary>
    public static bool Allowed(Where w) => w != Where.SharedCheckout;
}
