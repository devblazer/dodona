using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's

namespace Dodona;

/// <summary>No workspace could be resolved, and the message says what to do about it. A
/// distinct type so <c>Program</c> can turn it into a usage-style failure rather than a stack
/// trace — and so it is never confused with a genuine invariant violation.</summary>
sealed class WorkspaceUnavailable : Exception
{
    public WorkspaceUnavailable(string message) : base(message) { }
}

/// <summary>
/// WHERE A PATH CAME FROM — and creating a workspace depends on it (plan decision D-L9,
/// operator 2026-08-19: *creating a workspace is a user action; Dodona must never do it on
/// its own*).
///
/// Before this existed, <c>Program</c> handed <see cref="WorkspaceResolve.ForPath"/> a single
/// string that was EITHER a typed <c>--root</c> OR <c>Environment.CurrentDirectory</c>, and
/// nothing downstream could tell which. So an agent inside a lane running any `dodona`
/// command got a workspace invented for whatever folder its process happened to be started
/// in — named after that folder, and, if that folder held a `.dodona\store.db`, with a store
/// MOVED into workspace territory as a side effect of running `dodona tickets`.
///
/// The distinction is a parameter rather than something the resolver infers, because there is
/// nothing in a path that says whether a person typed it. Every call site has to answer.
/// </summary>
enum PathSource
{
    /// <summary>Somebody said this path out loud: a typed <c>--root</c>, a path the operator
    /// wrote in a sentence (concierge rung 0), a folder chosen inside the discovery fence.
    /// Creating is CORRECT here, and is the whole legacy-store migration mechanism
    /// (<c>tests/workspace-acceptance.ps1</c>'s migration set, which passes <c>--root</c>).</summary>
    Explicit,

    /// <summary>Nobody said it. It is the process's inherited working directory, which for an
    /// agent in a lane is a folder chosen by the daemon and for a shell is wherever the
    /// operator happened to be standing. An unowned one is a REFUSAL, never a creation.</summary>
    Inherited,
}

/// <summary>
/// Turning what the operator said into a workspace (docs/WORKSPACES-CONCIERGE.md §1/§4).
/// Two entry points, and the difference between them matters:
///
///   <see cref="ByNameOrId"/> — "--workspace personal". Registry rung 1: exact id, exact
///     name, exact alias. Code, no model, and it NEVER creates: naming a workspace that
///     does not exist is a typo, and inventing one would hide the typo.
///
///   <see cref="ForPath"/> — "--root C:\repos\thing", which is every command the CLI has
///     today and every acceptance suite. Ask the registry who owns the path; if nobody
///     does, MIGRATE or CREATE a workspace for it on the spot — but only when the path was
///     <see cref="PathSource.Explicit"/>.
///
/// The auto-create is deliberate and is what keeps the whole migration invisible: every
/// existing instance becomes "a workspace named after its root, with that root as sole
/// member" (§1) the first time anything addresses it, with its store moved into workspace
/// territory. That is the degenerate case the doc promises behaves exactly as today, and
/// it is why the pre-existing suites keep passing without knowing workspaces exist.
///
/// It is also §11 applied (act, announce, allow undo): a workspace is a registry row and a
/// directory, the note says which one happened, and `dodona workspace-forget` reverses it
/// without touching a single transcript.
///
/// What it is NOT allowed to do, since 2026-08-19: create for a path nobody named. See
/// <see cref="PathSource"/> for the incident.
/// </summary>
static class WorkspaceResolve
{
    public sealed record Resolved(Workspace Ws, string? Note);

    public static Workspace? ByNameOrId(Registry reg, string nameOrId) => reg.ByNameOrId(nameOrId);

    public static Resolved ForPath(Registry reg, string path, PathSource source)
    {
        var owner = reg.Owner(path);
        if (owner is not null) return new Resolved(owner.Value.Ws, null);

        var canonical = Instance.Canonical(path);

        // AN INHERITED CWD THAT NOBODY OWNS IS A REFUSAL (D-L9). It is checked before the
        // legacy-store probe on purpose: a folder with a `.dodona\store.db` in it is exactly
        // the case where creating would also MOVE that store, and a store must never relocate
        // because an agent ran `dodona tickets` from a folder it did not choose.
        //
        // It refuses rather than parking behind a question (CLAUDE.md §0.1: never hung,
        // halted, stuck): both un-sticking commands are in the message, and running either
        // one makes the very next invocation succeed.
        if (source == PathSource.Inherited)
        {
            var have = reg.All();
            throw new WorkspaceUnavailable(
                $"no workspace owns {canonical}, and that path was not asked for — it is just" +
                " the folder this process happens to be running in.\n" +
                "       Creating a workspace is a user action, so Dodona will not invent one here.\n" +
                (have.Count > 0
                    ? $"       address one:  dodona <command> --workspace <{string.Join("|", have.Take(6).Select(x => x.Name))}>\n"
                    : "       (no workspaces exist yet)\n") +
                $"       make one:     dodona workspace-create --name <NAME> --member \"{canonical}\"\n" +
                $"       or say where: dodona <command> --root \"{canonical}\"   (an explicit path DOES create)");
        }

        var legacyStore = Path.Combine(canonical, ".dodona", "store.db");
        var migrating = File.Exists(legacyStore);

        // Never move a WAL file out from under a live writer. The old identity was a hash
        // of the canonical root, so the OLD ctl pipe name is still computable — if it is
        // in the pipe namespace, a pre-workspace daemon owns that store right now and
        // moving it would be the corruption vector §12 is built to avoid.
        if (migrating && Instance.IsLive(Instance.LegacyId(canonical)))
            throw new WorkspaceUnavailable(
                $"a pre-workspace daemon is still running for {canonical}.\n" +
                "       Its store cannot be moved while it is open (a live WAL file is not a file you move).\n" +
                $"       Stop it first:  dodona stop-daemon --root \"{canonical}\"");

        var ws = reg.Create(UniqueName(reg, canonical));
        if (!reg.Attach(ws.Id, canonical, out var attachErr))
        {
            // The one way this fails on a fresh workspace is repo-exclusivity: the path is
            // a git repo already owned elsewhere. Roll the empty workspace back so a
            // refusal does not litter the registry, and re-raise the loud message.
            reg.Forget(ws.Id, out _);
            throw new WorkspaceUnavailable(attachErr);
        }

        string note;
        if (migrating)
        {
            MigrateStore(canonical, ws.Id);
            note = $"migrated {canonical} into workspace \"{ws.Name}\" ({ws.Id}); store now at {Paths.Store(ws.Id)}";
            reg.Event("workspace_migrated", ws.Id, $"from {canonical}");
        }
        else
            note = $"new workspace \"{ws.Name}\" ({ws.Id}) for {canonical} — undo: dodona workspace-forget --workspace {ws.Id}";

        return new Resolved(reg.ById(ws.Id)!, note);
    }

    /// <summary>
    /// Move a root-anchored instance's state into workspace territory. The store and BOTH
    /// its WAL twins move together — a store.db separated from its -wal is a store missing
    /// its most recent transactions, which is worse than either half alone.
    ///
    /// Worktrees are deliberately not touched: they stay beside their repo (§1's stated
    /// exception), and the tickets that name them keep working because the recorded
    /// worktree path never changed.
    /// </summary>
    static void MigrateStore(string root, string wsId)
    {
        var dst = Paths.WorkspaceDir(wsId);
        Directory.CreateDirectory(dst);
        var srcDir = Path.Combine(root, ".dodona");

        foreach (var name in new[] { "store.db", "store.db-wal", "store.db-shm" })
        {
            var src = Path.Combine(srcDir, name);
            if (File.Exists(src)) File.Move(src, Path.Combine(dst, name), overwrite: true);
        }

        // Shim identity files move too. CLAUDE.md §4 resolves pids from these to stop a
        // test's processes without ever killing by name; leaving them behind would leave
        // that rule pointing at an empty directory.
        try
        {
            foreach (var f in Directory.EnumerateFiles(srcDir, "shim-lane*.json"))
                File.Move(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        catch (DirectoryNotFoundException) { /* no .dodona at all: nothing to carry over */ }

        // A breadcrumb where the store used to be. This is the one genuinely irreversible
        // step in the whole workspace change — a real store carries every lane, every
        // transcript and every ticket this project has ever had — and the first thing
        // someone will do when they cannot find it is look in `.dodona`. Leave the answer
        // there rather than in a scrollback they have already lost.
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "MOVED-TO-WORKSPACE.txt"),
                $"""
                This project's Dodona store moved on {DateTime.Now:yyyy-MM-dd HH:mm}.

                A workspace is now named rather than located (docs/WORKSPACES-CONCIERGE.md §1),
                so its state lives in Dodona's own territory instead of beside the project:

                  workspace id : {wsId}
                  store        : {Paths.Store(wsId)}
                  directory    : {Paths.WorkspaceDir(wsId)}   (shim-lane<N>.json moved here too)
                  registry     : {Paths.Registry}

                Nothing was deleted. Ticket worktrees deliberately stayed here, under wt\.

                  dodona where --workspace {wsId}
                  dodona workspaces
                """);
        }
        catch { /* a missing breadcrumb must never fail a migration that already worked */ }
    }

    /// <summary>A name for a migrated root: its folder leaf, then `parent/leaf`, then a
    /// counter. Display names are not identity (the id is), so a collision is a cosmetic
    /// problem — but two workspaces both called `src` would be a cosmetic problem the
    /// operator has to live in.</summary>
    static string UniqueName(Registry reg, string canonical)
    {
        var leaf = Path.GetFileName(canonical);
        if (leaf.Length == 0) leaf = canonical.Replace('\\', '-').Replace(':', '-').Trim('-');
        if (reg.ByNameOrId(leaf) is null) return leaf;

        var parent = Path.GetFileName(Path.GetDirectoryName(canonical) ?? "");
        if (parent.Length > 0 && reg.ByNameOrId($"{parent}/{leaf}") is null) return $"{parent}/{leaf}";

        for (int n = 2; n < 1000; n++)
            if (reg.ByNameOrId($"{leaf}-{n}") is null) return $"{leaf}-{n}";
        return $"{leaf}-{Guid.NewGuid():N}"[..40];
    }

    /// <summary>Expand a folder into the repositories under it — the bulk-attach
    /// convenience §1 keeps the old "workspace" word for. Returns the folder itself when
    /// it holds no repos, because a bare folder is a perfectly good member.</summary>
    public static List<string> BulkCandidates(string folder)
    {
        var full = Instance.Canonical(folder);
        if (Registry.LooksLikeRepo(full)) return new List<string> { full };
        var repos = Repos.Under(full).Where(r => !r.IsRoot).Select(r => r.Path).ToList();
        return repos.Count > 0 ? repos : new List<string> { full };
    }
}
