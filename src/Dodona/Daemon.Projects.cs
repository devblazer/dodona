using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Dodona;

// Part of Daemon, split out of a 6,791-line file (issue #23). Same class, same
// behaviour -- only the file boundary is new.
sealed partial class Daemon
{
    /// <summary>This workspace's members, re-read from the registry on demand — the same
    /// doctrine as repo discovery below: a member attached while the daemon runs must be
    /// usable without a restart. Falls back to the primary alone if the registry cannot be
    /// opened, so a locked or missing registry degrades to today's single-root behaviour
    /// rather than to a daemon that can find no repositories at all.</summary>
    List<Member> Members()
    {
        try
        {
            using var reg = new Registry();
            var ws = reg.ById(_instanceId);
            if (ws is not null && ws.Members.Count > 0) return ws.Members;
        }
        catch { }
        return new List<Member> { new(_primary, _primary.ToLowerInvariant(), Registry.LooksLikeRepo(_primary), "") };
    }

    /// <summary>This workspace's project folders, in attach order — <see cref="Members"/>
    /// reduced to the one thing <see cref="Projects"/> takes. Re-read per call for the same
    /// reason: a project attached while the daemon runs must be usable without a restart, and
    /// a project DETACHED while it runs must stop being usable without one (T4).</summary>
    List<string> ProjectPaths() => Members().Select(m => m.Path).ToList();

    /// <summary>
    /// This workspace's projects, MOST RECENTLY USED FIRST — the ordering half of the router's
    /// memory (docs/LOCATIONS-PLAN.md Phase 3, D-L5), used to order the candidates rung 4
    /// offers.
    ///
    /// **Derived from the store's own lane rows, NOT from a registry column, and that is a
    /// decision.** A `members.last_used_ts` was the obvious shape and was rejected: only a
    /// daemon knows a project was used, so the column would need a SECOND writer into the
    /// machine-wide registry the concierge is meant to own (Phase 3: *the concierge stays the
    /// registry's sole writer; a daemon that learns a project tells it*) — a cross-process
    /// channel, and a fact that then exists in two places and can disagree. `lanes.cwd` plus
    /// `lanes.id` already record it, workspace-locally, transactionally, in the store this
    /// daemon owns outright. Projects never used come last, in attach order, so every project
    /// is always offered.
    /// </summary>
    List<string> ProjectsByRecency()
    {
        var projects = ProjectPaths();
        var ordered = new List<string>();
        foreach (var l in _store.LanesAll().OrderByDescending(l => l.Id))
            if (Projects.Of(projects, l.Cwd) is string p &&
                !ordered.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(p);
        foreach (var p in projects)
            if (!ordered.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase))) ordered.Add(p);
        return ordered;
    }

    /// <summary>The spoken handles taught for this workspace's projects — rung 3's memory, read
    /// from the registry per call for the same reason <see cref="Members"/> is. A registry that
    /// will not open degrades to "no handles taught", which costs a rung and never a
    /// refusal.</summary>
    List<(string Alias, string Key)> ProjectHandles()
    {
        try { using var reg = new Registry(); return reg.ProjectHandles(_instanceId); }
        catch { return new List<(string, string)>(); }
    }

    /// <summary>
    /// The I/O half of rung 2's evidence: gather the three liveness answers and hand them to
    /// <see cref="Projects.Live"/>, which is where the decision lives and is unit-tested.
    ///
    /// The split is D-L6 made unnarrowable. `LaneLiveness.Live` is already the union of the pipe
    /// namespace and a live recorded shim pid — never one instantaneous read, because a pipe name
    /// blinks out while its shim swaps server instances (8 of 192 reads over 1.5 s, and the gap
    /// is synchronised with a daemon restart). This adds the third answer only this process has:
    /// a runtime it is holding an open handle to right now.
    /// </summary>
    List<string> LiveProjectPaths()
    {
        var lanes = _store.LanesAll();
        return Projects.Live(
            ProjectPaths(),
            lanes.Select(l => (l.Id, l.Role, l.State, l.Cwd)),
            Instance.LiveLanes(_instanceId),
            LaneLiveness.LiveRecords(Paths.WorkspaceDir(_instanceId)).Select(t => t.Lane),
            lanes.Where(l => _lanes.TryGetValue(l.Id, out var rt) && rt.Connected).Select(l => l.Id));
    }

    /// <summary>
    /// The workspace's projects, AND WHETHER THE ANSWER IS TRUSTWORTHY (P5.2). Reaping decides
    /// whether to stop an agent, so it may only ever act on a membership list the registry
    /// actually gave us.
    ///
    /// This distinction is the single most dangerous thing in Phase 5. <see cref="Members"/>
    /// degrades to `{_primary}` when the registry cannot be opened — deliberately, so a locked
    /// registry leaves a daemon working rather than unable to find any repository at all. But
    /// fed to a reaper, that fallback says *every project except the first one is gone*, and the
    /// reaper would shut down every brain outside the first project. That is precisely the
    /// kill-healthy-sessions bug this phase removes, wearing the costume of the fix.
    ///
    /// So: false means "do not reap anything for being unregistered". A workspace that has been
    /// FORGOTTEN also lands here (`ById` returns null), which is the right conservative answer —
    /// forgetting is handled explicitly by the `workspace-forgotten` command (P2.7), where the
    /// intent is known, rather than inferred from an absence.
    /// </summary>
    (List<string> Projects, bool Trusted) TrustedProjects()
    {
        try
        {
            using var reg = new Registry();
            var ws = reg.ById(_instanceId);
            if (ws is not null && ws.Members.Count > 0) return (ws.Members.Select(m => m.Path).ToList(), true);
        }
        catch { }
        return (new List<string> { _primary }, false);
    }

    /// <summary>
    /// The project a lane row is REGISTERED to (P5.1/P5.2) — the key a manager's validity is
    /// decided by, and never a guess dressed as a fact.
    ///
    /// Reading order, and each rung exists for a case that happened:
    ///   1. `lanes.project`, when it is set. This is the registration itself.
    ///   2. the project that owns `lanes.cwd`, for a WORK lane — its folder IS its project, so
    ///      the column is a cache of a derivable fact and either answer is the same answer.
    ///   3. the workspace's first project, for a MANAGEMENT lane with neither. That is a lane
    ///      older than schema 10 whose stamp did not land (a locked store, a store copied out of
    ///      a suite), and answering "" for it would make it unregistered — i.e. reaped. A brain
    ///      that has done nothing wrong must not be killed by a migration that failed quietly,
    ///      so it is adopted for the first project, which is exactly what "the brain" meant
    ///      before this phase existed.
    /// </summary>
    string RegistrationKey(Store.LaneRow l, IReadOnlyList<string> projects) =>
        l.Project.Length > 0 ? l.Project
        : Projects.Of(projects, l.Cwd) is string owner ? owner
        : Projects.IsManagementRole(l.Role) ? _primary
        : "";

    /// <summary>
    /// WHERE A LANE MAY OPEN (docs/LOCATIONS-PLAN.md P2.1). Resolves a requested folder to a
    /// project of THIS workspace, or refuses — loudly, naming the projects it does know and the
    /// command that would add the one it does not.
    ///
    /// Three things make this the whole of P2.1:
    ///
    /// * **Nothing requested means the first project**, which is byte-for-byte what every spawn
    ///   site did before this phase, and is what keeps a one-project workspace unchanged.
    /// * **A requested folder must be a registered project or inside one.** Inside one resolves
    ///   UP to the project — a lane opens in a project, not in whichever subdirectory a caller
    ///   happened to name, so `lanes.cwd` stays a project path and `Projects.Field` keeps
    ///   answering in the operator's units. (A TICKET lane is the deliberate exception and does
    ///   not come through here: its folder is its worktree, `&lt;project&gt;\.dodona\wt\tN`,
    ///   which resolves to the same project by ancestor.)
    /// * **Anything else is refused, never substituted.** Substituting is how `LandOp` could
    ///   fast-forward the wrong repository (P0.1) and how `ticket-agent --repo` could open a
    ///   ticket against a repo it had never seen. An agent started in a folder no workspace owns
    ///   is ungated (T7) in a tree nothing here is tracking.
    ///
    /// `Instance.Canonical` first, because the registry's paths are canonical and the requested
    /// one arrives from a command line — 8.3 names, junctions and casing all otherwise read as
    /// "no project owns this".
    /// </summary>
    bool TryProject(string? requested, out string project, out string? refusal)
    {
        refusal = null;
        if (string.IsNullOrWhiteSpace(requested)) { project = _primary; return true; }

        var projects = ProjectPaths();
        var canonical = Instance.Canonical(requested!);
        var owner = Projects.Of(projects, canonical);
        if (owner is not null) { project = owner; return true; }

        project = "";
        refusal = $"refused: {canonical} is in no project of workspace {_wsName} " +
                  $"(projects here: {(projects.Count == 0 ? "none" : string.Join(", ", projects))}) -- " +
                  $"attach it with `dodona workspace-attach --member {canonical}`, or name one of those";
        return false;
    }

    /// <summary>
    /// WHICH PROJECT A COMMAND MEANT (trap T5, P2.4) — for the commands that ACT on one folder:
    /// `repo-init` and `repo-status`. Two inputs, and the difference between them is the whole
    /// point:
    ///
    /// * **`project`** is explicit: a person or an agent typed `--project`. It is validated, and
    ///   an unowned one is REFUSED. `repo-init` runs `git init`, which nothing here can undo.
    /// * **`cwd`** is the calling process's working directory, sent by every client. It is a
    ///   HINT: if a project owns it, that is obviously the project meant — an agent in project B
    ///   running `dodona repo-init` means B. If nothing owns it (the operator typed from
    ///   `C:\`, or from a folder in another workspace), it falls back to the first project, which
    ///   is byte-for-byte what this command always did. The fallback is not silent: both commands
    ///   print the project path they acted on.
    /// </summary>
    bool TryCommandProject(JsonElement e, out string project, out string? refusal)
    {
        if (One(e, "project") is string explicitly) return TryProject(explicitly, out project, out refusal);
        refusal = null;
        project = One(e, "cwd") is string cwd ? Projects.Of(ProjectPaths(), Instance.Canonical(cwd)) ?? _primary : _primary;
        return true;
    }

    /// <summary>The workspace's repositories, rediscovered on demand — git is the truth,
    /// the registry is a cache of it (§12). A repo added to the workspace while the
    /// daemon runs must be usable without a restart.</summary>
    List<RepoRef> Repositories() => Repos.Discover(Members());

    /// <summary>Where a ticket's git work happens. Falls back to the primary member, so a
    /// ticket written before its repo disappeared still reports honestly rather than
    /// throwing.</summary>
    string RepoPath(string repoName) =>
        Repos.ByName(Repositories(), repoName)?.Path ?? _primary;

    Config ConfigFor(string repoName) => Config.For(_primary, RepoPath(repoName));

    /// <summary>
    /// The repository a ticket belongs to, resolved by IDENTITY (P0.1). Everything that acts
    /// on a ticket's repository goes through here: the gate deployment, claim-check, the merge
    /// backstop and the land.
    ///
    /// Two halves, and both matter:
    ///
    /// **Located by path.** `Repos.ByName(repos, t.Repo)` was the old answer and it silently
    /// stopped working the moment a second project was attached, because the naming rule
    /// changes with project count: the pre-existing ticket still says "." while discovery now
    /// calls that repository `proj`, so the lookup returned null — and every caller had a
    /// different bad fallback for null. The gate skipped redeployment (`continue`), so
    /// enforcement layer 1 quietly died; `LandOp` fell back to `_primary`, so a fast-forward
    /// could be executed against THE WRONG REPOSITORY. And when `leaf~2` was recycled onto a
    /// different folder, the name RESOLVED — to a repo the ticket had never been in.
    ///
    /// **Named as it was born.** The returned ref carries the ticket's own recorded name, not
    /// the live one, because <see cref="RepoRef.ClaimPrefix"/> is derived from it and the
    /// ticket's claims are stored workspace-relative to the name in force when they were
    /// written. An open ticket's claim namespace must not move underneath it; where it IS is a
    /// lookup, what it is CALLED is history. Reconciling those names ACROSS tickets — so two
    /// spellings of one folder are still one claim — is <see cref="Claims.Overlap(Claims.Held,
    /// Claims.Held)"/>, which reduces both to repo-relative terms rather than moving either
    /// ticket's namespace (Phase 0b).
    ///
    /// Null means "this workspace no longer contains that repository" — a real answer, and
    /// every caller must say so out loud rather than substitute a folder of its own choosing.
    /// </summary>
    RepoRef? RepoOf(Store.TicketRow t)
    {
        var repos = Repositories();
        if (t.RepoPath.Length > 0)
            return Repos.ByPath(repos, t.RepoPath) is RepoRef byPath ? byPath with { Name = t.Repo } : null;
        // A pre-schema-9 row that StampRepoPaths could not resolve: the name is all there is.
        return Repos.ByName(repos, t.Repo);
    }

    /// <summary>The merge token's key for a ticket — read off the TICKET ROW, not from a
    /// lookup, so a repository that has been renamed, re-prefixed or detached cannot make one
    /// ticket ask for a second token over the same `main` (P0.2). The `#unresolved:` form is
    /// for a pre-v9 row that never resolved: it keeps that ticket's own token row rather than
    /// letting it share a real repository's by accident.</summary>
    Store.RepoId TokenIdOf(Store.TicketRow t) =>
        new(t.RepoPath.Length > 0 ? t.RepoPath
            : RepoOf(t) is RepoRef r ? Repos.Key(r.Path) : "#unresolved:" + t.Repo,
            t.Repo);

    /// <summary>
    /// Finish schema 9 (P0.3): stamp every pre-v9 ticket and token row with the repository
    /// PATH its name resolves to, and announce the backup that made the migration undoable.
    ///
    /// It runs here rather than in <c>Store.Migrate</c> because resolving a name needs the
    /// registry's member list and a filesystem walk, and the store deliberately knows about
    /// neither. Idempotent: on an already-stamped store it is two indexed scans.
    ///
    /// Nothing is dropped and nothing is guessed. A name that resolves to no repository keeps
    /// its row and is ANNOUNCED — the operator has to be able to see that ticket 4 is stranded,
    /// because the alternative (letting it resolve to whatever the name means today) is how a
    /// recycled `leaf~2` inherits another repository's merge token.
    /// </summary>
    void MigrateRepoIdentity()
    {
        if (_store.PreMigrationBackup is string bak)
        {
            _store.Event("store_backed_up", null, $"cold start: schema v{_store.SchemaAtOpen} -> v{Ver.Schema}, backup {bak}");
            Announce($"[dodona] store migrated v{_store.SchemaAtOpen} -> v{Ver.Schema}; backed up first — undo: dodona stop-daemon, then restore {bak} over store.db");
        }
        else if (_store.PreMigrationBackupError is string err)
        {
            _store.Event("store_backup_failed", null, $"schema v{_store.SchemaAtOpen} -> v{Ver.Schema}: {err}");
            Announce($"[dodona] store migrated v{_store.SchemaAtOpen} -> v{Ver.Schema} but COULD NOT BE BACKED UP FIRST ({err}) — the migration is not undoable");
        }

        try
        {
            var repos = Repositories();
            var members = Members();
            var report = _store.StampRepoPaths(name =>
            {
                if (Repos.ByName(repos, name) is RepoRef r) return Repos.Key(r.Path);
                // "." only ever existed while the workspace had exactly ONE project and that
                // project WAS the repository (Repos.Under's empty prefix, which is what keeps
                // the one-project case byte-identical). Attaching a second project renames it,
                // after which "." resolves to nothing at all — so the historical meaning is
                // honoured explicitly here. This is the case that repairs the live defect:
                // the old ticket's "." and the new ticket's `proj` fold onto one token row.
                if (name is "." or "" && members.Count > 0 && Repos.ByPath(repos, members[0].Path) is RepoRef first)
                    return Repos.Key(first.Path);
                return null;
            });
            foreach (var line in report.Stamped) _store.Event("repo_path_stamped", null, line);
            foreach (var line in report.Merged)
            {
                _store.Event("merge_token_merged", null, line);
                Announce($"[dodona] {line}");
            }
            foreach (var line in report.Unresolved)
            {
                _store.Event("repo_path_unresolved", null, line);
                Announce($"[dodona] repo identity: {line}");
            }
        }
        catch (Exception ex) { _store.Event("repo_path_stamp_failed", null, ex.Message); }
    }

}
