using System.IO;                 // explicit: this file also compiles into the WPF project,
                                 // whose implicit usings are narrower than the console one's

namespace Dodona;

/// <summary>
/// Where Dodona's own state lives (docs/WORKSPACES-CONCIERGE.md §1). A workspace is
/// NAMED, not located — it has no natural folder — so its store cannot live under a
/// project root any more. This extends CLAUDE.md §5 (Dodona state is never repo content)
/// one step further: not even repo-adjacent. Everything below is Dodona's own territory.
///
/// The one exception, and it is deliberate: <see cref="Worktrees"/>. Git worktrees are
/// volume- and path-sensitive (a worktree on another drive from its repo is a different
/// class of problem), and moving them into workspace-land buys nothing — so they stay
/// beside the member repo they belong to, exactly where they are today.
///
/// DODONA_HOME redirects the whole tree. That is not a convenience: every acceptance
/// suite must be able to create workspaces, migrate stores and test the repo-exclusivity
/// refusal WITHOUT touching the registry the operator is using right now (§17 — tests
/// collide with nothing, including the instance the operator is working in).
/// </summary>
static class Paths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("DODONA_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dodona");

    public static string WorkspacesDir => Path.Combine(Home, "workspaces");

    /// <summary>A workspace's own directory, keyed by its generated id — never by its
    /// display name, so a rename moves nothing and orphans nothing (§1).</summary>
    public static string WorkspaceDir(string id) => Path.Combine(WorkspacesDir, id);

    public static string Store(string id) => Path.Combine(WorkspaceDir(id), "store.db");

    /// <summary>Shim identity per lane: {shimPid, childPid, pipeName}. This is workspace
    /// state, not project state — lanes are workspace-wide and a workspace has N members,
    /// so there is no single project root to put it under any more. CLAUDE.md §4 (never
    /// kill by name, resolve pids from these files) now reads them from here.</summary>
    public static string ShimInfo(string id, long laneId) =>
        Path.Combine(WorkspaceDir(id), $"shim-lane{laneId}.json");

    /// <summary>The concierge's own territory (§2). It belongs to no workspace, so its
    /// store and config sit beside the workspaces rather than inside one.</summary>
    public static string ConciergeDir => Path.Combine(Home, "concierge");

    public static string Registry => Path.Combine(ConciergeDir, "registry.db");

    public static string ConciergeStore => Path.Combine(ConciergeDir, "store.db");

    public static string ConciergeConfig => Path.Combine(ConciergeDir, "concierge.json");

    /// <summary>Where management-role agents live: a neutral directory OUTSIDE any
    /// repository. Claude discovers project context by walking up from its cwd, so a
    /// router/compressor/brain started inside a project would load that project's
    /// CLAUDE.md and skills — files that order WORK agents to build, test and publish.
    /// A manager reading a worker's orders is how a classifier ends up running /ship
    /// (commit 19dad3d; operator: "that could be disastrous").</summary>
    public static string NeutralCwd()
    {
        var dir = Path.Combine(Home, "neutral");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Ticket worktrees stay beside the MEMBER that holds the repository — the
    /// documented exception above. For a one-member workspace whose member is the project
    /// root this is byte-for-byte the path it has always been (`&lt;root&gt;\.dodona\wt\tN`),
    /// which is what keeps the degenerate case indistinguishable from today.</summary>
    public static string Worktrees(string memberPath) => Path.Combine(memberPath, ".dodona", "wt");
}
