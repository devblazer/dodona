using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Dodona;

/// <summary>Per-project config, dodona.json at the project root (design §10).</summary>
/// <summary>
/// Per-project config, dodona.json at the project root (design §10), plus the model and
/// effort policy (§9's lever, made settable).
///
/// Effort was previously never passed at all, which is worth stating: a lane is its own
/// `claude -p` process and inherits none of the operator's interactive session settings,
/// so "I always run high" was silently not true of any agent Dodona started. It is now a
/// decision with a name and a default rather than an omission.
/// </summary>
sealed record Config(string Main, string[] Verify, string Agent = "claude",
                     string Model = "opus", string Effort = "high",
                     string RouterModel = "haiku", string RouterEffort = "low",
                     string CompressorModel = "haiku", string CompressorEffort = "low",
                     // 0 = no pool unless asked for by hand. Off by default because a warm
                     // pool is real quota drawn from the same window as the lanes (§2.6),
                     // and because every acceptance suite runs on a root with no
                     // dodona.json — a default of 2 would silently put real Haiku sessions
                     // inside seven deliberately model-free suites.
                     int Compressors = 0,
                     PolicyRule[]? Policy = null, string[]? AllowedTools = null,
                     string PermissionMode = "bypassPermissions",
                     // Live updates are not sometimes (operator's standing order): when
                     // main moves, the daemon publishes that commit and swaps ITSELF. Every
                     // M4 guard applies -- a build that fails, or a successor that never
                     // answers, leaves the running system untouched and announces why.
                     // autoPublishProject defaults to the root.
                     //
                     // There is no debounce any more, and its absence is the point (P2.4):
                     // a commit is atomic and already quiet, so there is no half-saved
                     // state to wait out. autoPublishDebounceSec is GONE, with the four
                     // other guards that existed only to make an mtime comparison behave.
                     bool AutoPublish = false, string? AutoPublishProject = null,
                     // The dispatcher brain (§3's middle rung): management decisions only —
                     // naming, routing, is-this-ticket-worthy. Cheap model first; when it
                     // says its own confidence is low, the SAME question goes to the
                     // expensive tier (operator: "unless it's not very confident, then
                     // route to a more expensive model"). Silent unless it disagrees.
                     bool Brain = true, string BrainModel = "haiku", string BrainEffort = "low",
                     // THE CEILING ON A BRAIN PER PROJECT (P5.7). Measured before that phase:
                     // each lane is two OS processes, and the steady state was 4 lanes / 8
                     // processes. Ten projects would be 13 / 26, peaking at 23 / 46 with every
                     // `brain-hi` warm -- ten of them opus. Quota is the scarce resource (§2.6),
                     // so a per-project brain needs a limit that is not "however many projects
                     // you attached".
                     //
                     // 6 is three projects fully warm, or six on the cheap tier alone, and it
                     // is deliberately out of reach of a ONE-project workspace (which can only
                     // ever want 2, one per tier) so that case stays byte-for-byte unchanged.
                     //
                     // IT REFUSES; IT NEVER EVICTS. Making room by shutting an existing brain
                     // down is the count-and-kill loop growing back somewhere else, and it
                     // would kill a session mid-question to serve one that is not. A refusal
                     // degrades that project to no judgement, which every caller already
                     // handles (the brain is an improver, never a gate) and which announces
                     // itself out loud rather than silently.
                     int MaxBrains = 6)
{
    public PolicyRule[] Rules => Policy ?? Dodona.Policy.Default;

    /// <summary>What a lane may run without asking, beyond edits (§2.9 made concrete —
    /// found by dogfooding: acceptEdits covers edits but not shell, headless mode
    /// auto-denies what it cannot ask about, so the first real lane wrote its change and
    /// then could not build it. Claude allowedTools syntax, e.g. "Bash(dotnet build:*)".
    /// Empty means edits only, which is the safe default for a repo you do not know.</summary>
    public string[] Allowed => AllowedTools ?? Array.Empty<string>();

    /// <summary>A repository's config, falling back to the workspace's. Verify steps and
    /// even the name of `main` belong to the repository, not to the workspace holding
    /// it — one repo may be on `main` and another still on `master`.</summary>
    public static Config For(string workspaceRoot, string repoPath) =>
        File.Exists(Path.Combine(repoPath, "dodona.json")) ? Load(repoPath) : Load(workspaceRoot);

    public static Config Load(string root)
    {
        var path = Path.Combine(root, "dodona.json");
        if (!File.Exists(path)) return new Config("main", Array.Empty<string>());
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        var main = d.RootElement.TryGetProperty("main", out var m) ? m.GetString() ?? "main" : "main";
        var verify = d.RootElement.TryGetProperty("verify", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
        // "agent" is which binary a lane runs. It exists so a project can point at a
        // specific claude, and so the acceptance suite can point at the fake agent and
        // test the paths where the daemon spawns an agent on its own initiative.
        var agent = d.RootElement.TryGetProperty("agent", out var a) ? a.GetString() ?? "claude" : "claude";
        string Str(string key, string fallback) =>
            d.RootElement.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? fallback : fallback;
        int Num(string key, int fallback) =>
            d.RootElement.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : fallback;
        string[]? allowed = null;
        if (d.RootElement.TryGetProperty("allowedTools", out var at) && at.ValueKind == JsonValueKind.Array)
            allowed = at.EnumerateArray().Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray();

        PolicyRule[]? policy = null;
        if (d.RootElement.TryGetProperty("policy", out var p) && p.ValueKind == JsonValueKind.Array)
            policy = p.EnumerateArray().Select(r => new PolicyRule(
                r.TryGetProperty("when", out var wq) ? wq.GetString() ?? "" : "",
                r.TryGetProperty("model", out var mq) ? mq.GetString() ?? "opus" : "opus",
                r.TryGetProperty("effort", out var eq) ? eq.GetString() ?? "high" : "high",
                r.TryGetProperty("why", out var yq) ? yq.GetString() ?? "" : "")).ToArray();

        return new Config(main, verify, agent,
            Str("model", "opus"), Str("effort", "high"),
            Str("routerModel", "haiku"), Str("routerEffort", "low"),
            Str("compressorModel", "haiku"), Str("compressorEffort", "low"), Num("compressors", 2),
            policy, allowed,
            Str("permissionMode", "bypassPermissions"),
            d.RootElement.TryGetProperty("autoPublish", out var ap) && ap.ValueKind == JsonValueKind.True,
            Str("autoPublishProject", ""),
            !(d.RootElement.TryGetProperty("brain", out var br) && br.ValueKind == JsonValueKind.False),
            Str("brainModel", "haiku"), Str("brainEffort", "low"),
            Num("maxBrains", 6));
    }
}

sealed class Daemon
{
    /// <summary>_instanceId is the WORKSPACE ID (WORKSPACES-CONCIERGE.md §1) — a generated
    /// slug, no longer a hash of a path. _primary is the workspace's primary member: the
    /// folder that stands in wherever this code used to say "the project root" (where a
    /// lane spawns, which dodona.json we fall back to, what repo-init acts on). For a
    /// one-member workspace they are the same thing they always were.</summary>
    readonly string _primary, _instanceId, _wsName, _ctlPipe;
    readonly Store _store;
    /// <summary>CONCURRENT SINCE R3.5, and that is a correctness fix rather than a precaution.
    /// The land now runs on its own task (D-R14), and its tail RETIRES THE LANE — so a plain
    /// `Dictionary` would be written from a background thread for minutes at a time while the
    /// control pipe reads and writes the same buckets. `_brainLo` above carries the identical
    /// reasoning for the identical reason; this one only became unsafe when something long-running
    /// left the pipe.</summary>
    readonly ConcurrentDictionary<long, LaneRuntime> _lanes = new();
    readonly SemaphoreSlim _routerLock = new(1, 1);   // one classification at a time on the warm session
    /// <summary>The two brain tiers, PER PROJECT (P5.3, decision D-L8) — a manager is a
    /// per-project scope (GLOSSARY), so "the brain" is not one thing any more.
    ///
    /// THESE WERE TWO SCALARS AND THAT IS THE BUG, not a style point. Reconcile's adoption loop
    /// assigned `_brainLo = l.Id` for every brain row it adopted, so with two projects the
    /// second overwrote the first — and the surplus loop then shut "the other one" down as a
    /// leak. Verified by reading the code, not inferred: adoption keeps the last row iterated
    /// and `keep` is that single value.
    ///
    /// Ordinal-ignore-case because the keys are filesystem paths and `=` is not. `C:\Proj` and
    /// `c:\proj` as two keys is two brains over one project, reached by a folder rename — the
    /// same drift schema 9 records for repo names.
    ///
    /// CONCURRENT, and that is not belt-and-braces: `EnsureBrainAsync` is reached from the
    /// startup warm-up task, from the control-pipe handler, and from `BrainReview`'s
    /// fire-and-forget task — all of which can be in flight at once. A `long` field tolerated
    /// that; a plain `Dictionary` written from three threads can corrupt its buckets, so
    /// replacing the scalars with one would have been a regression dressed as a fix.</summary>
    readonly ConcurrentDictionary<string, long> _brainLo = new(StringComparer.OrdinalIgnoreCase),
                                                _brainHi = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One lock per brain SESSION, keyed by lane id — the shape `_compressorLocks`
    /// below already has, and for the reason stated there. Two scalars were two serialization
    /// points across every project: project B's review waited behind project A's, on a lock
    /// that guards a conversation neither of them shares.
    ///
    /// Keyed by lane id rather than by project so a stale key cannot outlive the session it
    /// guards: a project whose brain is reaped and re-created gets a new id and therefore a new
    /// lock, where a project-keyed dictionary would hand the new session the old lock.</summary>
    readonly ConcurrentDictionary<long, SemaphoreSlim> _brainLocks = new();
    long _routerLo = -1;                              // lane id of the warm input classifier
    bool _saidNoClassifier;                           // the fallback announces ONCE per daemon
    bool _saidBrainCap;                               // ...and so does the brain cap (P5.7)
    bool _saidUntrustedProjects;                      // ...and so does a registry we could not read
    // One lock per compressor session, not one for the pool: the point of a pool is that
    // two lanes finishing at once compress concurrently. A single lock would rebuild the
    // serialization point §3 forbids the dispatcher to be (§5).
    readonly Dictionary<long, SemaphoreSlim> _compressorLocks = new();
    int _compressorNext;                              // round-robin cursor
    Config _config;

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

    Daemon(string primary, string wsId, string wsName, string ctlPipe, Store store)
    {
        _primary = primary;
        _instanceId = wsId;
        _wsName = wsName;
        _ctlPipe = ctlPipe;
        _store = store;
        _config = Config.Load(primary);
    }

    public static async Task<int> RunAsync(string primary, string wsId, string wsName, string ctlPipe, bool successor)
    {
        // A successor waits its turn BEFORE touching anything: it handshakes with the
        // predecessor, then waits for it to actually exit. Only then is it safe to take
        // the mutex, open the store (a migration must never race a live writer) and
        // adopt the shim pipes (a shim serves one client at a time).
        int predecessor = 0;
        if (successor)
        {
            predecessor = await HandshakeAsSuccessorAsync(wsId);
            if (predecessor < 0) { Console.Error.WriteLine("successor handshake failed; predecessor keeps running"); return 4; }
        }

        // One daemon per WORKSPACE, enforced at the OS (design §14). The id is now the
        // registry's slug rather than a path hash, so what this mutex protects is the
        // workspace's store — and repo-exclusivity (Registry's three layers) is what keeps
        // two workspaces from ever aiming two of these at one main.
        Mutex? mutex = null;
        for (int i = 0; i < (successor ? 80 : 1); i++)
        {
            mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{wsId}", out bool createdNew);
            if (createdNew) break;
            mutex.Dispose();
            mutex = null;
            await Task.Delay(250);
        }
        if (mutex is null)
        {
            Console.Error.WriteLine($"another daemon already owns workspace {wsName} ({wsId})");
            return 3;
        }
        using (mutex)
        {
            using var store = new Store(Paths.Store(wsId));
            return await new Daemon(primary, wsId, wsName, ctlPipe, store).LoopAsync(predecessor);
        }
    }

    /// <summary>The successor half of the handoff (§13). Connect to the predecessor's
    /// handoff pipe, declare what this build is, wait for `go`, then wait for the
    /// predecessor's process to actually be gone. Returns its pid, or -1 on failure —
    /// in which case this process exits and the predecessor stays up, unharmed.</summary>
    static async Task<int> HandshakeAsSuccessorAsync(string instanceId)
    {
        var pipe = new NamedPipeClientStream(".", Instance.HandoffPipe(instanceId), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(20000); }
        catch { return -1; }
        try
        {
            var w = new StreamWriter(pipe) { AutoFlush = true };
            var r = new StreamReader(pipe);
            w.WriteLine($"ready pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} shim={Ver.ShimProtocol}");
            var go = await r.ReadLineAsync();
            if (go is null || !go.StartsWith("go ")) return -1;
            var oldPid = int.Parse(go[3..].Trim());
            try
            {
                using var old = Process.GetProcessById(oldPid);
                using var cts = new CancellationTokenSource(20000);
                await old.WaitForExitAsync(cts.Token);
            }
            catch { /* already gone, or never was: either way the road is clear */ }
            return oldPid;
        }
        catch { return -1; }
        finally { try { pipe.Dispose(); } catch { } }
    }

    async Task<int> LoopAsync(int predecessorPid)
    {
        _store.Event("daemon_start", null,
            $"pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath} " +
            $"workspace={_wsName} ({_instanceId}) first-project={_primary}" +
            (predecessorPid > 0 ? $" successor_of={predecessorPid}" : ""));
        Console.WriteLine($"dodona daemon: workspace {_wsName} ({_instanceId}), ctl pipe {_ctlPipe}, " +
                          $"pid {Environment.ProcessId}, build {Ver.Build}, store {Paths.Store(_instanceId)}");
        MigrateRepoIdentity();

        // Reconcile (design §12): rows are the claim; the pipe is the proof. A successor
        // is adopting shims the predecessor only just let go of, so give them room.
        //
        // P3.4 -- ASK THE OS FIRST, and ask it BOTH ways. A lane that is neither in the pipe
        // namespace nor holding a live shim process does not exist, and there is nothing to
        // connect to however patiently you try. Read once, before the loop.
        //
        // The two-answer part is not caution, it is a bug this very phase shipped and caught:
        // a lane pipe blinks out while the shim swaps server instances between clients, and
        // every shim in a workspace disconnects at the same instant its daemon exits -- which
        // is one reconcile away. Pipes alone declared four to seven live lanes "gone" per
        // restart. LaneLiveness carries the measurement.
        var liveLanes = LaneLiveness.Live(_instanceId, Paths.WorkspaceDir(_instanceId));

        // WHICH PROJECT EVERY EXISTING LANE IS FOR (P5.1's migration), before anything reads a
        // registration. `lanes.project` is new in schema 10 and every carried-over row is empty,
        // so a reaper run first would see a store full of unregistered managers and shut the
        // operator's live brain down — the bug this phase removes, delivered by its own
        // migration. The store cannot resolve this itself (it knows nothing about membership,
        // exactly as with schema 9's repo paths), so the daemon does it here, where the registry
        // is.
        //
        // The rule per lane, and it ANNOUNCES an assumption rather than making one quietly:
        //   * a work lane   -> the project that owns its cwd. A fact.
        //   * a manager     -> the workspace's FIRST project, ASSUMED and said out loud. Before
        //                      this phase there was exactly one brain per role for the whole
        //                      workspace, and the first project is what `_primary` stood in for
        //                      everywhere else, so it is the only non-arbitrary answer. In the
        //                      one-project case it is not even an assumption, which is what
        //                      keeps that case byte-for-byte identical.
        //   * anything else -> left empty and REPORTED, never guessed (P0.3's shape).
        var stampProjects = ProjectPaths();
        var laneStamp = _store.StampLaneProjects(l =>
            Projects.Of(stampProjects, l.Cwd) is string owner ? (owner, false)
            : Projects.IsManagementRole(l.Role) ? (_primary, true)
            : (null, false));
        if (laneStamp.Stamped.Count > 0 || laneStamp.Assumed.Count > 0 || laneStamp.Unresolved.Count > 0)
        {
            _store.Event("lane_projects_stamped", null,
                $"stamped={laneStamp.Stamped.Count} assumed={laneStamp.Assumed.Count} unresolved={laneStamp.Unresolved.Count}");
            foreach (var line in laneStamp.Assumed)
                _store.Event("lane_project_assumed", null, line);
            foreach (var line in laneStamp.Unresolved)
                _store.Event("lane_project_unresolved", null, line);
            if (laneStamp.Assumed.Count > 0)
                Announce($"[dodona] schema {Ver.Schema}: {laneStamp.Assumed.Count} management lane(s) had no recorded project and were " +
                         $"assumed to be for {_primary} (the workspace's first project) — `dodona status` shows the scope of each" +
                         (_store.PreMigrationBackup is string bak ? $"; the pre-migration store is at {bak}" : ""));
            if (laneStamp.Unresolved.Count > 0)
                Announce($"[dodona] {laneStamp.Unresolved.Count} lane(s) are in a folder no project of this workspace owns, so they have " +
                         "no project recorded: " + string.Join("; ", laneStamp.Unresolved));
        }
        var adoptProjects = stampProjects;

        foreach (var l in _store.LanesAll().Where(l => l.State == "alive" && l.Role != "dispatcher"))
        {
            // The stored pipe name, hoisted once. `LaneRow.Pipe` is declared non-nullable, but
            // testing it for emptiness below is enough for the compiler to treat every earlier
            // read of it as a maybe -- and a new CS8604 in a tree that builds with zero warnings
            // is a real signal, not noise to wave through.
            var lanePipe = l.Pipe ?? "";
            var rt = new LaneRuntime(l.Id, lanePipe, _store);
            HookTurnEnd(rt, l.Role);
            // A WORK lane gets the patient retry: it may hold a real agent mid-turn, and a
            // successor is adopting shims the predecessor only just let go of. A UTILITY lane
            // gets one attempt — a brain, router or compressor whose pipe does not answer
            // immediately is simply gone, and nothing about it is worth waiting for.
            //
            // This is not a micro-optimisation. Reconcile runs BEFORE the control pipe server,
            // so every wasted attempt is time the daemon is not answerable at all. Measured on
            // a copy of the operator's store, carrying 14 leaked brain lanes with dead pipes:
            // ~2.4s each, about 35 seconds of a daemon that looked hung and refused
            // `stop-daemon` because it had not started listening yet.
            //
            // ...and `attempts: 1` for a utility lane was the assertion "a brain whose pipe does
            // not answer immediately is simply gone" -- an assertion nothing ever checked. It was
            // false on the operator's own store: lane 20 was declared unreachable after ONE 500 ms
            // knock, reaped as "shim gone", and replaced 160 ms later, while its shim, its child
            // and its pipe were all still alive. That is how an immortal orphan is manufactured.
            //
            // So the OS decides how hard to try, not the role:
            //   pipe ABSENT  -> zero attempts. Faster than the old one-knock path, which is what
            //                   the 35-second reconcile hang actually wanted (14 dead brains x
            //                   ~2.4 s of connect attempts, all of it before the control pipe was
            //                   even listening).
            //   pipe PRESENT -> be patient whatever the role, because there is definitely a
            //                   process there and adopting it is always better than leaving it.
            var patient = l.Role == "work";
            var pipeLive = liveLanes.Contains(l.Id);
            var attempts = !pipeLive ? 0 : patient ? (predecessorPid > 0 ? 20 : 3) : 3;
            if (attempts > 0 && await rt.ConnectAndPumpAsync(attempts)) _lanes[l.Id] = rt;
            else
            {
                _store.LaneState(l.Id, "unreachable");
                if (!pipeLive)
                    _store.Event("lane_unreachable", l.Id,
                        $"reconcile: no pipe and no live shim process for {(lanePipe.Length > 0 ? lanePipe : "(no pipe recorded)")} -- the shim is gone");
                else
                {
                    // Live pipe, will not converse. Do NOT walk away: walking away is what left
                    // three unkillable shims running out of the compiler's output directory. Tell
                    // it to go, and say whether the message landed.
                    var told = await LaneRuntime.ShutdownShimAsync(lanePipe);
                    var gone = told && await LaneRuntime.WaitPipeGoneAsync(lanePipe);
                    _store.Event("lane_unreachable", l.Id,
                        $"reconcile: {lanePipe} is LIVE but did not answer in {attempts} attempt(s); " +
                        (gone ? "sent ##shutdown, pipe gone" : told ? "sent ##shutdown, pipe still there" : "##shutdown could not be delivered either"));
                }
            }
            // An adopted pool member needs its lock back, or its turns would never gate.
            if (l.Role == "compressor" && _lanes.ContainsKey(l.Id)) _compressorLocks[l.Id] = new SemaphoreSlim(1, 1);

            // ...and an adopted BRAIN needs its tier pointer back, for exactly the same
            // reason. Without this, `_brainLo` was still -1 after reconcile, so the startup
            // warm-up decided no brain existed and spawned a fresh lane — every single
            // daemon start, while the previous brain sat connected, idle and unreachable.
            //
            // Measured on the operator's own instance: 14 BRAIN lanes (lane6…lane19), one per
            // daemon start across a morning of auto-publish swaps, each an idle `claude -p`
            // process nobody could reach. Compressors never leaked because the line above
            // has always re-adopted them; the brain was simply missed when it was added.
            //
            // No quota was burned (LANE-LIFECYCLE §2: quota is consumed by turns, not by
            // existing) — but it grows without bound and buries `dodona status`.
            //
            // ...AND IT ADOPTS ONE PER PROJECT NOW (P5.3). The pointer was a scalar, so with two
            // projects the second adoption overwrote the first and the retirement loop below shut
            // "the other one" down. Keyed on the lane's REGISTRATION, and first-wins on purpose:
            // the oldest row for a key is the one that has been serving, so a duplicate claimant
            // is always the newer arrival and the reap below is deterministic rather than
            // dependent on iteration order.
            if (_lanes.ContainsKey(l.Id))
            {
                var key = RegistrationKey(l, adoptProjects);
                if (l.Role == "brain") _brainLo.TryAdd(key, l.Id);
                else if (l.Role == "brain-hi") _brainHi.TryAdd(key, l.Id);
                // ...and the ROUTER, for exactly the same reason. It was added later than the
                // brain and inherited none of the brain's lessons: adopt it or every start
                // spawns another one, retire the surplus or a store accumulates them. The router
                // stays ONE PER WORKSPACE (GLOSSARY) and therefore stays a scalar.
                else if (l.Role == "router") _routerLo = l.Id;
                if (l.Role is "brain" or "brain-hi") BrainLock(l.Id);
            }
        }
        // Retire UTILITY lanes whose shim is gone. A brain, router or compressor is fungible
        // infrastructure with no thread: nothing resumes it, nobody reads its transcript, and
        // leaving the row `alive` means every future start spends attempts reconnecting to a
        // pipe that will never answer — 14 leaked brains cost about twelve seconds of dead
        // reconnects at every startup.
        //
        // WORK lanes are deliberately untouched. An unreachable work lane stays visible
        // because that one is a problem to notice, and `lane-respawn` can bring its session
        // back (LANE-LIFECYCLE §1: agents are disposable, the lane is the thread).
        //
        // ...but ONLY when the shim really is gone, which is a question for the OS (P3.4). Writing
        // "shim gone" over a live pipe is not a cosmetic inaccuracy: marking the row `dead` drops
        // the last reference to that process, and the very next thing the warm-up does is start a
        // replacement beside it. That pair of lines is the entire mechanism behind 14 leaked BRAIN
        // lanes on the operator's instance, one per daemon start. Leaving the row `unreachable`
        // instead is what lets ClearOfLivePredecessorsAsync (P3.5) still see it and refuse.
        var stillLive = LaneLiveness.Live(_instanceId, Paths.WorkspaceDir(_instanceId));
        foreach (var l in _store.LanesAll()
                     .Where(l => l.State == "unreachable" && l.Role is "brain" or "brain-hi" or "router" or "compressor")
                     .ToList())
        {
            if (stillLive.Contains(l.Id))
            {
                _store.Event("utility_lane_stubborn", l.Id,
                    $"role={l.Role}: pipe {l.Pipe} is STILL LIVE, so it was not reaped -- and no " +
                    "replacement will be started while it is (`dodona stop-all --lanes` clears it)");
                continue;
            }
            _store.LaneState(l.Id, "dead");
            _store.Event("utility_lane_reaped", l.Id, $"role={l.Role}: shim gone or shut down, nothing to resume");
        }

        // REGISTRATION, NOT COUNTING (P5.2, decision D-L8). What was here counted: it kept one
        // `keep` lane id per utility role and shut every other alive lane of that role down as
        // "a duplicate left by a fixed leak". With a brain per project that kills N-1 HEALTHY
        // SESSIONS on every daemon start — including every auto-publish swap — and announces it
        // as a repair.
        //
        // The operator's correction, which is what dissolved the blocker: *"You just use a
        // global system to keep track of that stuff. If it's not tracked, it's not valid. Why
        // must you do some weird kill to count?"* So a manager is valid iff a row says it should
        // exist for (role, project), and "surplus" stops being arithmetic:
        //
        //   * NO REGISTRATION -> its project is not a project of this workspace any more (P5.5).
        //     A lifecycle event that did not exist: `project-gone` reaches lanes by CWD, and a
        //     manager's cwd is the neutral directory, so a brain for a departed project was
        //     invisible to every existing path and was the obvious source of the next leak.
        //   * DUPLICATE CLAIMANT -> another lane already holds this exact (role, project). At
        //     most one can, by definition, so this is never "N brains because N projects" — it
        //     is two rows over one slot, which is what a store carrying the old leak looks like
        //     after its lanes are stamped, and it is how such a store still heals itself.
        //
        // COMPRESSORS ARE EXEMPT FROM THE DUPLICATE RULE and that is not an oversight: a POOL is
        // meant to have several members per project (§5 — one lock for the pool would rebuild
        // the serialization point the design forbids), so "another lane holds this key" is the
        // normal, configured state for them.
        //
        // AND NONE OF IT RUNS ON AN UNTRUSTED MEMBERSHIP LIST. `Members()` degrades to the first
        // project alone when the registry cannot be opened, which fed to this loop reads as
        // "every project but the first is gone" — every brain outside the first project reaped,
        // which is this phase's own bug in the costume of its fix.
        var (livePro, projectsTrusted) = TrustedProjects();
        if (!projectsTrusted && !_saidUntrustedProjects)
        {
            _saidUntrustedProjects = true;
            _store.Event("reap_skipped_untrusted_projects", null,
                $"the registry did not answer for workspace {_instanceId}: nothing is reaped for being unregistered this start");
        }
        var reapable = _store.LanesAll()
            .Where(l => l.State == "alive" && Projects.IsManagementRole(l.Role))
            .ToList();
        var claimed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var reaped = new List<string>();
        foreach (var l in reapable)
        {
            var key = RegistrationKey(l, livePro);
            var slot = $"{l.Role}|{key}";
            string? why = null;
            if (projectsTrusted && (key.Length == 0 || Projects.Of(livePro, key) is null))
                why = $"role={l.Role}: registered to '{(key.Length > 0 ? key : "(nothing)")}', which is no project of this workspace";
            else if (l.Role != "compressor" && claimed.TryGetValue(slot, out var holder))
                why = $"role={l.Role} project={key}: lane {holder} already holds that registration";
            if (why is null) { claimed.TryAdd(slot, l.Id); continue; }

            // Ask the SHIM to go, over its own pipe — it takes the child tree with it and exits
            // cleanly, which needs no pid bookkeeping (CLAUDE.md §4). Walking away instead is
            // what left three unkillable shims running out of the compiler's output directory.
            if (_lanes.TryGetValue(l.Id, out var rrt)) { rrt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
            else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
            _brainLocks.TryRemove(l.Id, out _);
            if (l.Role == "brain" && _brainLo.TryGetValue(key, out var lo) && lo == l.Id) _brainLo.TryRemove(key, out _);
            if (l.Role == "brain-hi" && _brainHi.TryGetValue(key, out var hi) && hi == l.Id) _brainHi.TryRemove(key, out _);
            if (l.Role == "router" && _routerLo == l.Id) _routerLo = -1;
            _store.LaneState(l.Id, "dead");
            _store.Event("brain_unregistered", l.Id, why);
            reaped.Add($"lane {l.Id} ({l.Role})");
        }
        if (reaped.Count > 0)
            Announce($"[dodona] stopped {reaped.Count} management agent(s) with no valid registration: {string.Join(", ", reaped)} — " +
                     "each was for a project this workspace no longer has, or was a second claimant on one project's slot");

        // GATE REDEPLOYMENT IS GONE, AND DELETING IT WAS NOT A TIDY-UP -- IT HAD BECOME HARMFUL.
        //
        // It rewrote every open ticket's gate file so the hook named the build actually running,
        // because `GcOldBuilds` deletes old build directories and a gate pointing at a collected
        // exe fails OPEN (found live 2026-08-18: every lane older than the running build had lost
        // enforcement layer 1). The plan (D-17) refused to let this be deleted until one question
        // was measured: are hooks re-read after a publish, or fixed at session start?
        //
        // MEASURED 2026-08-20 -- FIXED AT SESSION START. A two-turn `--input-format stream-json`
        // session kept firing its hook on turn 2 after the hook had been REMOVED from the settings
        // file between turns. So rewriting the file under a live agent never reached it, and this
        // loop was not solving the incident it was written for; the lanes it appeared to fix were
        // the ones that respawned afterwards, which `AttachShimAsync` now writes a fresh file for
        // anyway.
        //
        // Worse than useless, once the gate moved out of the worktree: the live agent holds the
        // OLD exe path, and rewriting the file with the new one is what lets `GcOldBuilds` delete
        // the directory that agent's gate still needs. The retention scan there is the real fix,
        // and it reads these files -- so refreshing them would erase the evidence it depends on.
        //
        // What replaces it: a dead lane's gate file is removed, so it stops pinning a build
        // directory that nothing needs any more.
        try
        {
            var live = _store.LanesAll().Where(l => l.State != "dead").Select(l => l.Id).ToHashSet();
            foreach (var f in Directory.GetFiles(Paths.WorkspaceDir(_instanceId), "gate-lane*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (long.TryParse(name.AsSpan("gate-lane".Length), out var gl) && !live.Contains(gl))
                    try { File.Delete(f); } catch { /* untidy, never fatal */ }
            }
        }
        catch (Exception ex) { _store.Event("gate_cleanup_failed", null, ex.Message); }

        // A leak this quiet needs to be visible in the chain, not just absent.
        //
        // ONE ENTRY PER PROJECT, AND THE KEY IS DELIBERATELY `brains=` RATHER THAN `brain=`
        // (P5.6). The check that reads this line regexed `brain=\d+`, so `brain=3,7` would have
        // kept matching while asserting nothing at all — a check that degrades into a green
        // proving nothing, which is the failure mode this whole plan keeps hitting. Renaming the
        // key makes the old pattern impossible to match, so a degraded check goes RED and has to
        // be rewritten instead of going quiet.
        _store.Event("reconcile_done", null,
            $"connected={_lanes.Count} brains=[{BrainList(_brainLo)}] brains-hi=[{BrainList(_brainHi)}] " +
            $"compressors={_compressorLocks.Count}");
        if (predecessorPid > 0)
        {
            Announce($"[dodona] swapped to build {Ver.Build} — {_lanes.Count} lane(s) adopted, nothing interrupted");
            GcOldBuilds();
        }
        StartSwapTicker();
        StartDriftWatcher();

        // Warm the compressor pool at daemon start (§5) — a pool that has to be summoned
        // by hand after every restart is a pool that is cold exactly when the first turn
        // finishes. Fire-and-forget: spawning sessions must not delay the daemon becoming
        // answerable, and a pool that fails to start costs nothing but full-length panes.
        if (_config.Compressors > 0 && Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") != "1")
            _ = Task.Run(async () =>
            {
                try
                {
                    var msg = await StartCompressorsAsync(_config.Agent, _config.CompressorModel,
                                                          _config.CompressorEffort, _config.Compressors);
                    _store.Event("compressor_pool", null, msg);
                }
                catch (Exception ex) { _store.Event("compressor_pool_failed", null, ex.Message); }
            });

        // Warm the brain's cheap tier the same way; the expensive tier is spawned lazily
        // on the first escalation — most days it is never needed at all.
        if (_config.Brain && Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") != "1")
            _ = Task.Run(async () =>
            {
                try { _store.Event("brain_started", null, await EnsureBrainAsync(hi: false) >= 0 ? "cheap tier warm" : "failed to warm"); }
                catch (Exception ex) { _store.Event("brain_failed", null, ex.Message); }
                // The classifier is warmed here too — not because routing depends on it
                // (EnsureRouterAsync creates it on demand now), but so the operator's FIRST
                // sentence does not pay a cold session's startup. Same guard, same block:
                // one place decides whether this daemon starts things by itself.
                try { _store.Event("router_started", null, await EnsureRouterAsync() >= 0 ? "classifier warm" : "failed to warm"); }
                catch (Exception ex) { _store.Event("router_failed", null, ex.Message); }
            });

        // No `using` on pipe streams near a peer that may close first (spike 2's lesson).
        bool stopping = false;
        while (!stopping)
        {
            var server = new NamedPipeServerStream(_ctlPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            var r = new StreamReader(server);
            var w = new StreamWriter(server) { AutoFlush = true };
            try
            {
                var req = await r.ReadLineAsync();
                if (req is not null)
                {
                    try { stopping = await HandleAsync(req, w); }
                    catch (Exception ex) { w.WriteLine($"error: {ex.Message}"); }
                    w.WriteLine("##end");
                }
            }
            catch { /* client vanished mid-conversation */ }
            try { server.Disconnect(); } catch { }
            try { server.Dispose(); } catch { }
        }

        _store.Event("daemon_stop", null, "graceful; lanes keep running");
        return 0;
    }

    async Task<bool> HandleAsync(string req, StreamWriter w)
    {
        using var d = JsonDocument.Parse(req);
        var e = d.RootElement;
        switch (e.GetProperty("cmd").GetString())
        {
            // ---------------- lanes (M0) ----------------
            case "lane-start":
            {
                var title = e.GetProperty("title").GetString()!;
                var child = e.TryGetProperty("child", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
                var childArgs = e.TryGetProperty("childArgs", out var ca) && ca.ValueKind == JsonValueKind.Array
                    ? ca.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();

                // WHICH PROJECT (P2.1). `--project` names one; nothing named is the first
                // project, which is byte-for-byte what this site did before. Resolved BEFORE the
                // lane row is created, so a refusal leaves no row behind -- a half-born lane
                // pointing at a folder we just refused is worse than the refusal.
                if (!TryProject(One(e, "project"), out var laneProject, out var laneRefusal))
                { w.WriteLine(laneRefusal!); w.WriteLine("##exit 1"); break; }

                // No --child means the real thing. A lane with no ticket has no CLAIM -- but it is
                // no longer UNGATED, and this comment used to say it was: layer 1 (P1 of
                // docs/WORK-ISOLATION-PLAN.md) deploys the write gate to every work lane in
                // `AttachShimAsync`, so a plain lane can read anywhere and still cannot write
                // into a project outside a worktree. What a ticket adds is the claim, which
                // bounds it against OTHER lanes; what layer 1 adds is that the shared checkout
                // is nobody's workspace. The T7 expansion noted here is closed by that.
                var lcfg = ConfigForProject(laneProject);
                if (child is null)
                {
                    w.WriteLine((await SpawnAgentLaneAsync(title, laneProject, Pick(e, "model", lcfg.Model), Pick(e, "effort", lcfg.Effort))).Msg);
                    break;
                }
                // A --child lane is configured by its project too, and records it for the same
                // reason -- `--child` chooses the BINARY, not the permissions. It is also the only
                // spawn a suite can drive model-free, so without this the T2 fix would have no
                // observable surface at all in any acceptance suite (IsClaude is false for the
                // fake agent, so no claude argv is ever built for it to be read back from).
                var lr = await SpawnLaneAsync(title, "work", laneProject, child, childArgs);
                if (lr.Id > 0) RecordLaneConfig(lr.Id, laneProject, lcfg, childArgs);
                w.WriteLine(lr.Msg);
                break;
            }
            case "say":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var text = e.GetProperty("text").GetString()!;
                if (!_lanes.TryGetValue(lane, out var rt)) { w.WriteLine($"error: lane {lane} not connected"); break; }
                rt.Say(text);
                w.WriteLine($"-> lane {lane}");
                break;
            }
            case "lane-rename":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var title = e.GetProperty("title").GetString()!.Trim().ToUpperInvariant();
                if (title.Length == 0 || title.Contains(' ')) { w.WriteLine("error: one word"); break; }
                var old = _store.LanesAll().FirstOrDefault(l => l.Id == lane)?.Title;
                if (old is null) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneTitle(lane, title);
                _store.Event("lane_renamed", lane, $"{old} → {title} (operator)");
                _store.PaneEvent(lane, "announcement", $"renamed to {title} (was {old})", null, null, acked: true);
                w.WriteLine($"renamed lane {lane}: {old} → {title}");
                break;
            }
            case "lane-respawn":
            {
                // Agents are fungible; the lane is the thread (§11). A dormant lane (its
                // ticket landed) or an unreachable one (its shim died) comes back as a
                // fresh process resuming the recorded session — spike 1 measured that
                // `--resume` restores full context with the same id and no fork. This is
                // what makes retiring agents cheap enough to do automatically.
                var lane = e.GetProperty("lane").GetInt64();
                var row = _store.LanesAll().FirstOrDefault(l => l.Id == lane);
                if (row is null) { w.WriteLine($"error: no lane {lane}"); break; }
                if (_lanes.TryGetValue(lane, out var lrt2) && lrt2.Connected) { w.WriteLine($"lane {lane} is already connected"); break; }

                var args2 = new List<string>();

                // WHERE it comes back, and WHAT it is told it is, were both wrong (M5.1).
                // Respawn hardcoded `_primary` and always rebuilt the PLAIN-lane prompt, so a
                // resumed TICKET agent ran in the operator's live working copy while being
                // told "your worktree is the current working directory; work only there" — a
                // gated agent, resumed, editing main's tree. The ticket is the authority on
                // both answers; the recorded cwd covers every other kind of lane.
                var t2 = _store.Tickets().FirstOrDefault(t => t.LaneId == lane && t.State == "open");

                // RE-HOMING (P2.6): `--project` is the operator's answer to the refusal below.
                // It is validated exactly like a fresh spawn, so re-homing cannot land a lane
                // somewhere a fresh one could not have opened.
                if (!TryProject(One(e, "project"), out var reProject, out var reRefusal))
                { w.WriteLine(reRefusal!); w.WriteLine("##exit 1"); break; }
                var reHomed = One(e, "project") is not null;
                // A TICKET lane cannot be re-homed: its claim gate is deployed into its worktree
                // and its prompt says "work only there", so moving the process out of the
                // worktree is precisely the M5.1 incident performed on purpose.
                if (reHomed && t2 is not null)
                {
                    w.WriteLine($"refused: lane {lane} works ticket {t2.Id}, so its directory is that ticket's worktree " +
                                "-- a ticket lane cannot be re-homed. Land or abandon the ticket instead.");
                    w.WriteLine("##exit 1");
                    break;
                }

                // The rung ORDER lives in ResolveLaneCwd, on the unit loop (P1.3). What stays
                // here is the I/O: a directory that has been deleted is not a candidate, and
                // ruling it out is this site's business, not a pure function's.
                var cwd2 = ResolveLaneCwd(
                    reHomed ? reProject
                    : t2?.Worktree is { Length: > 0 } twt && Directory.Exists(twt) ? twt : null,
                    row.Cwd is { Length: > 0 } rcwd && Directory.Exists(rcwd) ? rcwd : null,
                    _primary);

                // TRAP T4, REFUSED RATHER THAN RE-OPENED. `workspace-detach` and
                // `workspace-move` change nothing about a lane row, and the only test this site
                // ever applied was `Directory.Exists` -- which PASSES, because the folder is
                // still there; it just belongs to another workspace now. Respawning into it puts
                // an ungated agent (T7) into somebody else's repository, holding somebody else's
                // merge token's tree. Re-homing to the first project instead would be worse in
                // the other direction: an agent whose entire conversation is about project B,
                // silently editing project A. So: refuse, and name the two commands that
                // un-stick it (CLAUDE.md §0.1 -- a wait or a refusal must name the condition).
                if (!Projects.IsOwned(ProjectPaths(), cwd2, NeutralCwd()))
                {
                    w.WriteLine($"refused: lane {lane}'s directory {cwd2} belongs to no project of workspace {_wsName} " +
                                "-- it was detached or moved while this lane existed. " +
                                $"Bring it back with `dodona workspace-attach --member {cwd2}`, " +
                                $"or re-home the lane with `dodona lane-respawn {lane} --project <project>`.");
                    _store.Event("lane_respawn_refused", lane, $"cwd={cwd2} owned=no");
                    w.WriteLine("##exit 1");
                    break;
                }

                // T2 again: a respawned lane is configured by the project it is going back INTO,
                // not by the workspace's first one. This path read `_config` and so could hand a
                // lane in project B project A's permission mode on every respawn.
                var cfg2 = ConfigForProject(Projects.Of(ProjectPaths(), cwd2) ?? _primary);
                var child2 = cfg2.Agent;
                if (IsClaude(child2))
                {
                    var sys2 = t2 is null
                        ? LaneSystemPrompt(row.Title, cwd2)
                        : TicketSystemPrompt(t2.Id, t2.Title,
                            string.Join(", ", _store.TicketClaims(t2.Id).Select(cl => $"{cl.Kind}:{cl.Value}")));
                    args2 = ClaudeArgs(cfg2, cfg2.Model, cfg2.Effort, sys2, acceptEdits: true);
                    args2.AddRange(Projects.ResumeArgs(row.Session));
                }
                // The pipe name is deterministic per lane, and the old shim is gone —
                // the name is free to reclaim, which is the whole point of never keying
                // anything to pids (§13).
                var (rid, rmsg) = await RespawnLaneAsync(row.Id, row.Title, args2, child2, cwd2);
                if (rid > 0)
                {
                    _store.LaneState(lane, "alive");
                    _store.LanePresence(lane, "idle");
                    _store.PaneEvent(lane, "announcement",
                        row.Session is { Length: > 0 } ? "agent respawned — session resumed, context intact" : "agent respawned — fresh session", null, null, acked: true);
                    _store.Event("lane_respawned", lane, $"session={row.Session ?? "-"}");
                }
                w.WriteLine(rmsg);
                break;
            }
            case "lane-stop":
            {
                // The undo for an auto-started lane. The shim owns the agent, so stopping
                // is a message to the shim, not a kill: it takes the child down with it
                // and the pane's rows stay exactly where they are (§12 — nothing is
                // deleted, the lane is simply no longer alive).
                var lane = e.GetProperty("lane").GetInt64();
                if (_lanes.TryGetValue(lane, out var srt))
                {
                    srt.Shutdown();
                    _lanes.TryRemove(lane, out _);
                }
                _store.LaneState(lane, "dead");
                if (_store.KvGet("focused_lane") == lane.ToString()) _store.KvSet("focused_lane", "");
                _store.Event("lane_stopped", lane, "operator");
                w.WriteLine($"stopped lane {lane}");

                // D-9: THE UNDO LINE HAS TO BE TRUE. Promotion announces `dodona lane-stop <n>` as
                // the way to undo it, so stopping a lane that was PROMOTED must actually undo the
                // promotion: abandon the ticket, prune the worktree, delete the branch, release the
                // claims (the conflict query only sees `state='open'`, so the state change is the
                // release). An announcement offering an undo that does not undo is worse than no
                // undo at all.
                //
                // ONLY A PROMOTED TICKET, and the distinction is the whole care here. A ticket the
                // operator created deliberately with `ticket-create` is THEIR work; deleting its
                // branch because a lane was stopped would be section 11's "nothing is deleted"
                // violated on their behalf. The promotion event is the only record of which is
                // which, which is why `Store.HasEvent` exists.
                var stopped = _store.Tickets().FirstOrDefault(t => t.State == "open" && t.LaneId == lane);
                if (stopped is not null && _store.HasEvent("lane_promoted", lane, $"ticket {stopped.Id} %"))
                    foreach (var line in AbandonTicket(stopped, $"lane {lane} stopped")) w.WriteLine(line);
                break;
            }
            case "tail":
                foreach (var row in _store.Tail(e.GetProperty("lane").GetInt64(), e.GetProperty("n").GetInt32()))
                    w.WriteLine(row);
                break;
            case "status":
                w.WriteLine($"daemon pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath}");
                w.WriteLine($"workspace {_wsName} ({_instanceId})  store={Paths.Store(_instanceId)}");
                w.WriteLine($"members: {string.Join(", ", Members().Select(m => m.Path))}");
                w.WriteLine($"lanes: model={_config.Model} effort={(_config.Effort is { Length: > 0 } ? _config.Effort : "cli default")}  " +
                            // The LANE, not only its config. Printing `router: model=haiku` for a
                            // classifier that had never once been created is how a dead routing
                            // ladder looked healthy for two days.
                            $"router: {(_routerLo > 0 && _lanes.TryGetValue(_routerLo, out var rrt) && rrt.Connected ? $"lane {_routerLo}" : "NOT RUNNING")} " +
                            $"model={_config.RouterModel} effort={(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}  " +
                            $"agent={_config.Agent}");
                // WHICH PROJECT EACH LANE IS IN (P1.2). Until this line `lanes.cwd` had no
                // surface anywhere a person looks: not here, not in `ui dump`, only the
                // `shim_spawned` event detail -- so a lane opening in the wrong project was
                // invisible to the operator and to every check but two. Projects are read ONCE
                // for the whole listing rather than per lane: Members() re-reads the registry
                // on every call, and a status line is not worth N registry opens.
                //
                // Projects.Field returns null for "say nothing", which is what keeps a
                // one-project workspace's output byte-for-byte what it has always been --
                // read its doc comment before changing the shape of this line.
                var projects = Members().Select(m => m.Path).ToList();
                foreach (var l in _store.LanesAll())
                {
                    var connected = _lanes.TryGetValue(l.Id, out var rt) && rt.Connected;
                    var proj = Projects.Field(l.Role, l.Cwd, projects, Paths.NeutralDir);
                    // WHICH PROJECT A MANAGER IS FOR (P5.6). `project=` above reads `lanes.cwd`
                    // and a brain's cwd is the neutral directory on purpose (P5.8), so it is
                    // silent about a brain by design -- which left "one brain per project" with
                    // no surface a person could read at all. Null for "say nothing", including
                    // for every one-project workspace, which is what keeps that output identical.
                    //
                    // IT GOES BEFORE `project=` AND MUST STAY THERE: tests/_workspace.ps1's
                    // Get-StatusProject anchors on `project=(.+?)\s*$`, so a field appended after
                    // it would be captured as part of the project path and five checks in two
                    // suites would start comparing a path against a path-plus-a-field.
                    var scope = Projects.ScopeField(l.Role, l.Project, projects);
                    w.WriteLine($"lane {l.Id}  {l.Title,-10}  role={l.Role,-6}  state={l.State}  connected={connected}  presence={l.Presence,-16}  session={l.Session ?? "-"}" +
                                (scope is null ? "" : $"  scope={scope}") +
                                (proj is null ? "" : $"  project={proj}"));
                }
                break;

            // ---------------- routing (M2, §4) ----------------
            case "focus":
            {
                var lane = e.GetProperty("lane").GetInt64();
                _store.KvSet("focused_lane", lane.ToString());
                w.WriteLine($"focused lane {lane}");
                break;
            }
            case "lane-collapse":
            {
                // A view choice, but a durable one, so it goes through the daemon like every
                // other write (m3: the UI owns nothing). Collapsing NEVER touches the lane's
                // life — no agent stops, no slot frees, nothing is demoted. It only says how
                // much room you want it to take, which is why LANE-LIFECYCLE §2's rejection of
                // slot-pressure eviction is untouched by it: this is the operator's hand, not
                // the system reclaiming space.
                var lane = e.GetProperty("lane").GetInt64();
                var on = !e.TryGetProperty("collapsed", out var cv) || cv.ValueKind != JsonValueKind.False;
                if (_store.LanesAll().All(l => l.Id != lane)) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneCollapsed(lane, on);
                _store.Event(on ? "lane_collapsed" : "lane_expanded", lane, "operator");
                w.WriteLine($"{(on ? "collapsed" : "expanded")} lane {lane}");
                break;
            }
            case "input":
            {
                var text = e.GetProperty("text").GetString()!;
                w.WriteLine(await RouteInput(text));
                break;
            }
            case "router-start":
            {
                // By hand: for suites (which set DODONA_NO_AUTOSTART and own every lifetime
                // themselves) and for restarting the classifier after a config change. The
                // ordinary path is EnsureRouterAsync, which creates it at the point of use.
                var child = e.TryGetProperty("child", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()! : _config.Agent;
                w.WriteLine((await SpawnRouterAsync(child, Pick(e, "model", _config.RouterModel), Pick(e, "effort", _config.RouterEffort))).Msg);
                break;
            }
            case "brain-start":
            {
                // For suites (NO_AUTOSTART skips the warm-at-start) and for restarting a
                // brain by hand after changing its config.
                // P5.3: WHICH project's brain. `BrainProject` resolves a subfolder up to its
                // project and falls back to the first one, so the no-argument call is exactly
                // what it always was.
                var bProject = BrainProject(One(e, "project"));
                var lo = await EnsureBrainAsync(hi: false, bProject);
                var wantHi = e.TryGetProperty("hi", out var bh) && bh.ValueKind == JsonValueKind.True;
                var hi2 = wantHi ? await EnsureBrainAsync(hi: true, bProject) : -2;
                // THE REASON IT FAILED, NOT JUST "FAILED" (CLAUDE.md §0.1 -- a silent degrade is
                // a bug, and "FAILED" with no cause is the same thing wearing a word). The cap is
                // the answer a caller can act on, so it is the one named here.
                var capped = BrainLaneCount() >= Math.Max(1, _config.MaxBrains);
                var why = capped ? $"CAPPED (maxBrains={_config.MaxBrains})" : "FAILED";
                w.WriteLine($"brain for {bProject}: cheap tier lane {(lo > 0 ? lo.ToString() : why)}" +
                            (wantHi ? $", expensive tier lane {(hi2 > 0 ? hi2.ToString() : why)}" : ""));
                break;
            }
            case "compressor-start":
            {
                var child = e.TryGetProperty("child", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString()! : _config.Agent;
                var model = Pick(e, "model", _config.CompressorModel);
                var effort = Pick(e, "effort", _config.CompressorEffort);
                // Asked for by hand with the pool configured off, "how many" still has an
                // obvious answer — two, the smallest number that is not a serialization point.
                var count = e.TryGetProperty("count", out var cn) && cn.ValueKind == JsonValueKind.Number ? cn.GetInt32()
                          : _config.Compressors > 0 ? _config.Compressors : 2;
                w.WriteLine(await StartCompressorsAsync(child, model, effort, count));
                break;
            }
            case "ticket-agent":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                // T2 FOR A GATED LANE (P2.3). This read `_config` -- the first project's -- while
                // spawning into a worktree that may belong to a different project entirely, and a
                // ticket lane has been able to do that since multi-repo landed. `ConfigFor` was
                // already the right answer here and was already used two lines away for `Main`;
                // the permission mode simply never went through it.
                var tcfg = ConfigFor(t.Repo);
                var child = e.TryGetProperty("child", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString()! : tcfg.Agent;
                var model = Pick(e, "model", tcfg.Model);
                var effort = Pick(e, "effort", tcfg.Effort);
                var claims = string.Join(", ", _store.TicketClaims(tid).Select(cl => $"{cl.Kind}:{cl.Value}"));

                // The lane-agent framing (§5, spike 3): declare the [DISPATCHER] channel or
                // the model treats mid-turn instructions as a prompt-injection attempt.
                var sys = TicketSystemPrompt(tid, t.Title, claims);
                var args = IsClaude(child) ? ClaudeArgs(tcfg, model, effort, sys, acceptEdits: true) : new List<string>();
                var (laneId, msg) = await SpawnLaneAsync(t.Title, "work", t.Worktree, child, args);
                // Link ticket ↔ lane: "waiting on you: merge" (§8) needs a pane to land in.
                if (laneId > 0)
                {
                    _store.TicketSetLane(tid, laneId);
                    RecordLaneConfig(laneId, Projects.Of(ProjectPaths(), t.Worktree) ?? _primary, tcfg, args);
                }
                w.WriteLine(msg);
                break;
            }

            // ---------------- tickets & claims (M1, §6/§11) ----------------
            case "ticket-create":
            {
                var title = e.GetProperty("title").GetString()!;
                var mode = e.TryGetProperty("mode", out var m) ? m.GetString()! : "on-approval";
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                var claims = new List<(string, string)>();
                foreach (var s in specs)
                {
                    var parsed = Claims.Parse(s);
                    if (parsed is null) { w.WriteLine($"error: bad claim spec '{s}' (use path:|new:|subtree:|symbol:)"); return false; }
                    claims.Add(parsed.Value);
                }
                // A TICKET WITH NO CLAIM IS LEGAL NOW (`REVIEW-AND-MERGE-PLAN.md` D-R5, R3).
                // This used to refuse with "at least one --claim required", which made sense
                // while a claim was a lock: a ticket holding nothing bounded nothing. It is not
                // a lock any more, so requiring one is requiring ceremony — and it is ceremony a
                // spoken sentence cannot supply, which is precisely why layer 2's promotion had
                // to invent a seed claim from whichever path happened to be denied first. That
                // seed is what stranded a promoted agent on its second file. Claims stay
                // available as a deliberate annotation (`--claim`, `claim-extend`); what a
                // branch actually touched is `git diff`, which needs nothing from anybody.
                if (specs.Count > 0 && claims.Count == 0) { w.WriteLine("error: no usable claim in the given specs"); break; }

                // Git is needed HERE — at the first branch and worktree — not at the door.
                // A project can be opened, and lanes can run in it, long before it has a
                // repo; refusing to open would be refusing too early (and for too long).
                var repos = Repositories();
                RepoRef? repo;
                if (e.TryGetProperty("repo", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString() is string rname && rname.Length > 0)
                {
                    repo = Repos.ByName(repos, rname);
                    if (repo is null)
                    {
                        w.WriteLine($"error: no repository '{rname}' in this workspace" +
                                    (repos.Count > 0 ? $" (have: {string.Join(", ", repos.Select(r => r.Name))})" : ""));
                        w.WriteLine("##exit 1");
                        break;
                    }
                    // NAMING THE REPO USED TO SKIP CLAIM VALIDATION ENTIRELY (P0.6). The
                    // inference branch below has always refused claims that span repositories,
                    // because a ticket lands by fast-forwarding ONE main — but `--repo X` went
                    // straight past it, so `--repo tools --claim path:engine/sim.cs` created a
                    // ticket in `tools` holding a claim over `engine`. Everything downstream
                    // then disagreed about which repository it was talking about: the gate
                    // prefixed the claim with `tools/`, the merge backstop diffed `tools`, and
                    // the land fast-forwarded `tools` while the agent edited `engine`.
                    var mismatch = Repos.CheckClaims(repos, repo, claims);
                    if (mismatch is not null)
                    {
                        _store.Event("ticket_repo_unresolved", null, $"'{title}': {mismatch}");
                        w.WriteLine($"error: {mismatch}");
                        w.WriteLine("##exit 1");
                        break;
                    }
                }
                else
                {
                    // Claims are workspace-relative paths, so they already say which
                    // repository this ticket is for — no extra syntax needed.
                    var (inferred, err) = Repos.ForClaims(repos, claims);
                    if (inferred is null)
                    {
                        _store.Event("ticket_repo_unresolved", null, $"'{title}': {err}");
                        w.WriteLine($"error: {err}");
                        // P4.5: ASK, do not instruct. This used to print "(lanes work without
                        // git; only tickets need a repository)" and leave the operator to go and
                        // type `dodona repo-init` — a GUI telling a person to use the CLI, which
                        // is this project's original sin (the same reasoning that turned "undo:
                        // dodona lane-stop 3" in the feed into a button). The refusal still
                        // stands and still costs nothing; what is new is that the missing repo
                        // becomes a QUESTION ROW, which the ask overlay renders and one verb
                        // answers — and which survives the window closing, because a pending
                        // question that evaporated would make asking worse than guessing
                        // (ConciergeStore's class note).
                        if (repos.Count == 0 && !Git.IsRepo(_primary))
                            foreach (var line in AskForRepo(_primary, title)) w.WriteLine($"       {line}");
                        w.WriteLine("##exit 1");
                        break;
                    }
                    repo = inferred;
                }

                // Repo-exclusivity, layer 3 (Registry's doc comment): asked HERE because
                // here is where a merge token first comes into existence for this repo.
                // MATERIALISED BY `MakeTicket`, which layer 2's promotion calls too (P2). It used
                // to be inline here and nowhere else; two implementations of "make a ticket" would
                // drift on exactly the checks that matter -- repo exclusivity and claim conflict.
                var made = MakeTicket(repo, title, mode, claims, specs);
                if (made.Error is not null)
                {
                    foreach (var line in made.Error.Split('\n')) w.WriteLine(line);
                    if (made.Exit1) w.WriteLine("##exit 1");
                    break;
                }
                // A single-repo project never sees the word "repo": there is only one, and
                // naming it would be noise in the ordinary case.
                w.WriteLine($"ticket {made.Id}{RepoTag(repo.Name)} branch {made.Branch} worktree {made.Worktree}");
                // AN OVERLAP IS REPORTED AFTER THE TICKET, NOT INSTEAD OF IT (D-R5, R3). This
                // block used to print `conflict:` lines and exit 1 without creating anything.
                // The ticket is created now and the overlap is said out loud, in that order, so
                // the line a script reads first is still the one that names the ticket.
                if (made.Conflicts.Count > 0)
                {
                    w.WriteLine($"note: overlaps {made.Conflicts.Count} open claim(s) — two agents on one file is");
                    w.WriteLine("      ordinary; duplicated effort is the manager's to raise (D-R5):");
                    foreach (var cf in made.Conflicts) w.WriteLine($"      overlap: {cf}");
                }
                break;
            }
            // ---------------- layer 1: which TREE a write is in (WORK-ISOLATION-PLAN section 3) ----
            //
            // Unconditional, model-free, and asked of EVERY work lane rather than only ticket
            // lanes: no agent writes into a project outside a worktree. The operator's named
            // failure was real work started in the shared checkout, where `.githooks/pre-commit`
            // then refuses the commit -- so today's default destination for load-bearing work is
            // a tree that cannot deliver it, and nothing stopped an agent editing it.
            //
            // IT APPLIES TO TICKET LANES TOO, and that is not belt-and-braces. `claim-check`
            // resolves an absolute path through its repository and project rungs, so a ticket
            // agent writing the ABSOLUTE path of a file its claim covers -- in the operator's
            // live checkout rather than its own worktree -- resolves to the same claim-relative
            // string and is ALLOWED. Found by reading the rungs while implementing this phase;
            // it has been reachable since multi-repo landed. The tree question has to be asked
            // first, and the claim question second.
            case "tree-check":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var row = _store.LanesAll().FirstOrDefault(l => l.Id == lane);
                // A relative `file_path` is relative to the AGENT'S working directory, which is
                // the lane's own recorded cwd -- not this daemon's, and not the first project's.
                var baseDir = row?.Cwd is { Length: > 0 } lc && Directory.Exists(lc) ? lc : _primary;
                var full = Path.GetFullPath(path, baseDir);
                var where = Trees.Locate(full, ProjectPaths());
                if (Trees.Allowed(where)) { w.WriteLine($"tree-ok: {where.ToString().ToLowerInvariant()} {full}"); break; }

                // LAYER 2: THE REFUSAL IS A PROMOTION, NOT A WALL (P2).
                //
                // A plain work lane that tried to write here needed a checkout of its own, so it
                // gets one: ticket, worktree, gate, and the same session carried in. Nothing has
                // been written yet, which is the entire reason layer 1 sits at the write attempt
                // rather than at the commit -- afterwards the edits would be in the wrong tree and
                // there is no safe way to move them (`git stash` is repo-global, so two lanes
                // stashing interleave one stack; CLAUDE.md 5.2).
                //
                // Three lanes do NOT get promoted, each for its own reason:
                //   * one that already works a ticket -- it HAS a worktree and should be writing
                //     there. Promoting again would give one lane two, and this is the hole P1
                //     found in `claim-check` (an absolute path inside its own claim), so the
                //     message names the worktree it already owns.
                //   * a management lane -- it runs in the neutral directory and writes nothing.
                //   * a path in no repository of this workspace -- there is nothing to branch.
                var openTicket = _store.Tickets().FirstOrDefault(t => t.State == "open" && t.LaneId == lane);
                var rr = RepoRelOf(full);
                if (row is not null && row.Role == "work" && openTicket is null && rr is not null)
                {
                    var (pmsg, move) = PromoteLane(row, rr.Value.Repo, rr.Value.Rel, full);
                    _store.Event("tree_check_denied", lane, $"{full} -> promotion");
                    w.WriteLine(pmsg);
                    w.WriteLine("##exit 1");
                    // AFTER the reply is on the wire, never before: the move respawns this lane,
                    // and the process it kills is the one currently waiting for this answer.
                    move?.Invoke();
                    break;
                }

                // D-13: A REFUSAL NAMES THE HOLDER. "outside your claim" sends the reader
                // hunting (CLAUDE.md 0.3); the holder is a store read and therefore free.
                var holder = ClaimHolder(full);
                var msg = openTicket is not null
                    ? $"denied: {full} is in the SHARED CHECKOUT, not a worktree. You already have one for " +
                      $"ticket {openTicket.Id}: write this file under {openTicket.Worktree} instead. Editing the " +
                      "shared checkout would put your work in the tree your operator and every other lane are " +
                      "using, and its pre-commit hook refuses commits from there, so it could not be delivered."
                    : $"denied: {full} is in the SHARED CHECKOUT, not a worktree" +
                      (holder is null ? "" : $" -- that path is held by {holder}") +
                      ". The shared checkout is a source of truth, not a workspace: other lanes and " +
                      "your operator are in it, and its pre-commit hook refuses commits from it, so work " +
                      "done here cannot be delivered. Work that changes files needs a ticket worktree of " +
                      "its own.";
                _store.Event("tree_check_denied", lane, $"{full} holder={holder ?? "-"} ticket={openTicket?.Id.ToString() ?? "-"}");
                w.WriteLine(msg);
                w.WriteLine("##exit 1");
                break;
            }
            case "claim-check":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }

                // Claims are workspace-relative, but the agent writes inside a worktree of
                // one repository — so a path resolved against the worktree must be put
                // back into workspace terms before it can be matched. For a single-repo
                // project the prefix is empty and this is exactly the old behaviour.
                var ticketRepo = RepoOf(t);
                var prefix = ticketRepo?.ClaimPrefix ?? "";
                var full = Path.GetFullPath(path, t.Worktree).Replace('\\', '/');

                // TRAP T6, FIXED (docs/LOCATIONS-PLAN.md P2.5). The two bases here were the
                // ticket's worktree and THE FIRST PROJECT -- so a write anywhere in a second
                // project resolved to neither, and the gate denied it with "outside the worktree
                // and the project root" while the agent was writing inside a repository the
                // workspace owns. That was already broken before this phase; Phase 2 is what
                // makes it NORMAL, so the latent hole starts firing on every lane that opens
                // outside the first project.
                //
                // The rungs, longest base first so a repo nested under another wins:
                //   1. the ticket's own WORKTREE, carrying the ticket's recorded claim prefix.
                //      First, always: `m3:186-187` and `LaneCwdPrecedenceTests` both pin that a
                //      ticket lane's folder is its worktree, and a worktree lives INSIDE its
                //      project, so without this rung the project rung would swallow it and hand
                //      back `.dodona/wt/t1/...` -- a path no claim can ever cover.
                //   2. any REPOSITORY of the workspace, prefixed with ITS claim name. Claims are
                //      workspace-relative and a repo's name IS its workspace-relative path, so
                //      this is the general form of what rung 3 did by hand.
                //   3. any PROJECT, unprefixed -- kept only because it is exactly what the old
                //      `_primary` base was, and dropping it would change the ordinary
                //      single-project message for a path inside the project but outside every
                //      repo. It cannot produce a false COVER in a multi-project workspace: the
                //      bare relative form it yields can only match a claim with no repo prefix,
                //      and `Repos.Discover` prefixes every repo name the moment a second project
                //      is attached, so no such claim can exist there.
                string? rel = null;
                var bases = new List<(string Dir, string Prefix)> { (t.Worktree, prefix) };
                bases.AddRange(Repositories().OrderByDescending(r => r.Path.Length).Select(r => (r.Path, r.ClaimPrefix)));
                bases.AddRange(ProjectPaths().OrderByDescending(p => p.Length).Select(p => (p, "")));
                foreach (var (baseDir, basePrefix) in bases)
                {
                    if (baseDir.Length == 0) continue;
                    var b = Path.GetFullPath(baseDir).Replace('\\', '/').TrimEnd('/') + "/";
                    if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = basePrefix + full[b.Length..];
                        break;
                    }
                }
                if (rel is null)
                {
                    w.WriteLine($"denied: {path} is outside the worktree and every project of workspace {_wsName}");
                    w.WriteLine("##exit 1");
                    break;
                }
                rel = Claims.Normalize(rel);
                var claims = _store.TicketClaims(tid);
                if (claims.Any(cl => Claims.Covers(cl.Kind, cl.Value, rel)))
                    w.WriteLine($"covered: {rel}");
                else
                {
                    w.WriteLine($"denied: {rel} not covered by ticket {tid} claims [{string.Join(", ", claims.Select(c => $"{c.Kind}:{c.Value}"))}]");
                    w.WriteLine("##exit 1");
                }
                break;
            }
            case "claim-extend":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                // THIS USED TO BE `.Where(p => p is not null)` AND NOTHING ELSE (P0.5): every
                // spec it could not parse was dropped in silence and the reply was still
                // "extended ticket N" with exit 0 — so `--claim src/water` (no `path:`) widened
                // nothing while telling the agent it had. All of them unparseable meant an
                // empty list, an insert of nothing, and a success message. `ticket-create` has
                // always refused a bad spec by name; this now does too.
                var claims = new List<(string, string)>();
                foreach (var s in specs)
                {
                    var parsed = Claims.Parse(s);
                    if (parsed is null) { w.WriteLine($"error: bad claim spec '{s}' (use path:|new:|subtree:|symbol:)"); w.WriteLine("##exit 1"); return false; }
                    claims.Add(parsed.Value);
                }
                if (claims.Count == 0) { w.WriteLine("error: at least one --claim required"); w.WriteLine("##exit 1"); break; }

                // AND IT HAD NO REPOSITORY AT ALL. `Store.ClaimExtend` takes a ticket id, so an
                // extension could widen an open ticket into a DIFFERENT repository than the one
                // it lands in — the same hole P0.6 leaves in `ticket-create --repo`, reached
                // from the other side. A ticket's repo is fetched here and the new claims are
                // held to it.
                var xt = _store.Ticket(tid);
                if (xt is null || xt.State != "open") { w.WriteLine($"error: ticket {tid} not open"); w.WriteLine("##exit 1"); break; }
                var xRepo = RepoOf(xt);
                if (xRepo is null)
                {
                    w.WriteLine($"error: ticket {tid}'s repository is no longer in this workspace ({(xt.RepoPath.Length > 0 ? xt.RepoPath : $"'{xt.Repo}'")})");
                    w.WriteLine("##exit 1");
                    break;
                }
                var xMismatch = Repos.CheckClaims(Repositories(), xRepo, claims);
                if (xMismatch is not null)
                {
                    _store.Event("claim_extend_refused", null, $"ticket {tid}: {xMismatch}");
                    w.WriteLine($"error: {xMismatch}");
                    w.WriteLine("##exit 1");
                    break;
                }

                // EXTENDED, THEN THE OVERLAP REPORTED -- in that order, and it used to be
                // instead-of rather than after (D-R5, R3; see Store.ClaimExtend for why this
                // fourth refusal had to go with the three D-R5 names).
                var conflicts = _store.ClaimExtend(tid, claims);
                _store.Event("claim_extended", null, $"ticket {tid} += [{string.Join(", ", specs)}]");
                w.WriteLine($"extended ticket {tid}");
                if (conflicts.Count > 0)
                {
                    _store.Event("claim_overlap", null, $"extend ticket {tid}: {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"      overlap: {cf}");
                }
                break;
            }
            case "approve":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                // ONE implementation, shared with the approval ask (R6) — see `ApproveTicket`
                // for why its caller list is the load-bearing part of D-R10.
                ApproveTicket(tid, "dodona approve");
                w.WriteLine($"approved ticket {tid}");
                break;
            }
            case "ack":
            {
                var id = e.GetProperty("id").GetInt64();
                w.WriteLine(_store.PaneAck(id) ? $"acked {id}" : $"error: {id} is not an unacked announcement");
                break;
            }
            case "undo-route":
            {
                var id = e.GetProperty("id").GetInt64();
                var undone = _store.RoutingUndo(id);
                if (undone is null) { w.WriteLine($"error: routing decision {id} not found or already undone"); break; }
                var (dl, input) = undone.Value;
                // Retraction to the lane that consumed the misroute — [DISPATCHER] framing
                // so the agent treats it as operator-authentic (spike 3).
                if (dl is long dlid && _lanes.TryGetValue(dlid, out var drt) && drt.Connected)
                {
                    drt.Say($"[DISPATCHER] Disregard this earlier message, it was routed to you by mistake: \"{input}\". Do not act on it; if you already started, stop and undo.");
                    _store.PaneEvent(dlid, "announcement", $"↩ undone: \"{Truncate(input, 60)}\" retracted", null, null);
                }
                _store.Event("route_undone", dl, $"decision {id}: {input}");
                w.WriteLine($"undone routing decision {id}");
                break;
            }
            case "tickets":
            {
                var multi = _store.Tickets().Any(t => t.Repo != ".");
                foreach (var t in _store.Tickets())
                    w.WriteLine($"ticket {t.Id}  {t.Title,-12}  {(multi ? $"repo={t.Repo,-10}  " : "")}state={t.State}  mode={t.MergeMode}  approved={t.Approved}  branch={t.Branch}");
                break;
            }
            case "repos":
            {
                var found = Repositories();
                if (found.Count == 0)
                {
                    w.WriteLine($"no git repository in {_primary}");
                    w.WriteLine("run `dodona repo-init` to make this folder one (lanes work meanwhile; only tickets need git)");
                    break;
                }
                foreach (var r in found)
                {
                    var cfg = Config.For(_primary, r.Path);
                    var key = Repos.Key(r.Path);
                    var tok = _store.TokenRead(new Store.RepoId(key, r.Name));
                    // Counted by identity, not by name: a ticket created before this repository
                    // was renamed (or re-prefixed by an attach) is still one of its tickets.
                    var open = _store.Tickets().Count(t => t.State == "open" &&
                        (t.RepoPath.Length > 0 ? t.RepoPath.Equals(key, StringComparison.OrdinalIgnoreCase)
                                               : t.Repo.Equals(r.Name, StringComparison.OrdinalIgnoreCase)));
                    w.WriteLine($"{r.Name,-14} main={cfg.Main,-8} open-tickets={open}  token={(tok.Holder?.ToString() ?? "free"),-6} verify={cfg.Verify.Length} step(s)  {r.Path}");
                }
                break;
            }

            // ---------------- merge token & land (M1, §7) ----------------
            case "token-request":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                if (t.MergeMode == "on-approval" && !t.Approved)
                {
                    _store.Event("token_refused_unapproved", null, $"ticket {tid}");
                    // Blocked-on-you is categorically distinct (§8): presence flips to
                    // "waiting on you" and the announcement lands in the pane AND the feed.
                    if (t.LaneId is long blid)
                    {
                        _store.LanePresence(blid, "waiting on you: merge");
                        _store.PaneEvent(blid, "announcement", $"waiting on you: merge ticket {tid} '{t.Title}' — dodona approve {tid}", null, null);
                    }
                    w.WriteLine($"refused: ticket {tid} is merge:on-approval and not approved");
                    // A REFUSAL THAT ASKS, NOT ONE THAT INSTRUCTS (`WORK-ISOLATION-PLAN` P5, which
                    // R6 absorbs; the same correction P4.5 made to `ticket-create`). The primary
                    // moment is COMPLETION — the record is what carries the manager's write-up, and
                    // asking then means the operator is not waiting for an agent to bump into a
                    // wall first — but this moment is unmistakable and costs milliseconds, and it
                    // covers the ticket whose record was impossible (no worktree) or has not
                    // happened yet. `QuestionUpsert` makes the second raise a no-op on the first's
                    // row, so a person is never asked the same thing twice.
                    // IN ITS OWN TRY, like the other two call sites and for a sharper reason: this
                    // one is ON THE SERIAL CONTROL PIPE, so an exception here would turn a refusal
                    // into an unhandled throw in the handler every other command is queued behind.
                    // The refusal itself is already written above and stands whatever happens next.
                    long qid = 0;
                    try { qid = AskToLand(t, t.LaneId ?? 0, null); }
                    catch (Exception ex) { _store.Event("land_ask_failed", null, $"ticket {tid}: at token-request: {ex.Message}"); }
                    if (qid > 0)
                        w.WriteLine($"         answer it in the window, or: dodona answer {qid} yes");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Merge-time backstop (§6 layer 2): diff the branch against its merge
                // base; any touched path outside the claim refuses the token. This
                // catches everything the fail-open hook gate cannot see.
                // REFUSED, NOT SUBSTITUTED. `reqRepo?.Path ?? _primary` meant a ticket whose
                // repository had left the workspace got its branch diffed against the FIRST
                // project's main — a diff of two unrelated histories, which is either every
                // file or none, and either way the backstop stopped answering the question it
                // was asked. There is no safe default for "which repository is this", so the
                // token is refused and the reason names the path that was recorded.
                var reqRepo = RepoOf(t);
                if (reqRepo is null)
                {
                    _store.Event("token_refused_no_repo", null, $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
                    w.WriteLine($"refused: ticket {tid}'s repository is no longer in this workspace ({(t.RepoPath.Length > 0 ? t.RepoPath : $"'{t.Repo}'")})");
                    w.WriteLine("         re-attach it (dodona workspace-attach --member <path>) or abandon the ticket");
                    w.WriteLine("##exit 1");
                    break;
                }
                var reqPath = reqRepo.Path;
                var reqPrefix = reqRepo.ClaimPrefix;
                var reqCfg = Config.For(_primary, reqPath);

                // WHAT THE BRANCH TOUCHED IS RECORDED, NOT JUDGED (D-R5/D-R7, R3).
                //
                // This block used to REFUSE the token when the diff touched a path outside the
                // ticket's declared claim. It was asking whether reality matched a prediction,
                // and with the prediction retired the question has no content left: the paths
                // came out of `git diff`, the claim came out of whatever the agent or the
                // promotion happened to declare up front, and a mismatch means the declaration
                // was incomplete rather than that anything is wrong.
                //
                // It also actively blocked R1's flow. The diff is taken from the merge base, so
                // once an agent has merged main into its branch itself -- D-R3's path, and the
                // only way a silent drop can exist -- the base IS main's tip and every file the
                // branch touched relative to main reads as "outside the claim". R2's own fixture
                // could not obtain a token while this refusal lived.
                //
                // The DIFF ITSELF IS KEPT, because it is the derived ownership signal D-R7 asks
                // for: a fact, needing no ceremony from the agent, that cannot go stale. It is
                // recorded for the manager to read (R4/R5) and it gates nothing.
                var (dc, diff) = Git.Run(reqPath, "diff", "--name-only", $"{reqCfg.Main}...{t.Branch}");
                if (dc == 0 && diff.Length > 0)
                {
                    var touched = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => Claims.Normalize(reqPrefix + f.Trim()))   // git speaks repo-relative; claims are workspace-relative
                        .Where(f => f.Length > 0)
                        .ToList();
                    var ticketClaims = _store.TicketClaims(tid);
                    var undeclared = touched.Where(f => !ticketClaims.Any(cl => Claims.Covers(cl.Kind, cl.Value, f))).ToList();
                    _store.Event("branch_touched", null,
                        $"ticket {tid} touched {touched.Count} path(s): {string.Join(", ", touched)}" +
                        (undeclared.Count > 0 ? $" | undeclared: {string.Join(", ", undeclared)}" : ""));
                }

                var (status, gen, pos) = _store.TokenRequest(tid, TokenIdOf(t), lease, () => Git.Sha(reqPath, reqCfg.Main));
                w.WriteLine(status == "granted"
                    ? $"granted ticket {tid} generation {gen}{RepoTag(t.Repo)}"
                    : $"queued ticket {tid} position {pos}{RepoTag(t.Repo)}");
                break;
            }
            case "token-renew":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                if (_store.TokenRenew(tid, TokenIdOf(rt), lease)) w.WriteLine($"renewed ticket {tid}");
                else { w.WriteLine($"refused: ticket {tid} is not the live holder"); w.WriteLine("##exit 1"); }
                break;
            }
            case "token-release":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                _store.TokenRelease(tid, TokenIdOf(rt));
                w.WriteLine("released");
                break;
            }
            case "token-status":
            {
                // One token per repository: they land in parallel, so they report in
                // parallel too.
                var tokens = _store.TokensAll();
                if (tokens.Count == 0)
                {
                    // Nothing has ever been landed here. Materialise the one for the repository
                    // this workspace has, so the reading is a reading rather than a blank.
                    var only = Repositories().FirstOrDefault();
                    tokens = new List<Store.TokenRow> { _store.TokenRead(
                        only is null ? new Store.RepoId("#unresolved:.", ".") : new Store.RepoId(Repos.Key(only.Path), only.Name)) };
                }
                var manyRepos = tokens.Any(x => x.Repo != ".");
                foreach (var tok in tokens)
                    w.WriteLine($"{(manyRepos ? $"repo={tok.Repo,-12} " : "")}holder={(tok.Holder?.ToString() ?? "none")} generation={tok.Generation} expires={tok.ExpiresTs ?? "-"} main={(tok.MainSha is { Length: >= 8 } s ? s[..8] : "-")}");
                break;
            }
            // R3.5 / D-R14: THIS HANDLER NO LONGER PERFORMS THE LAND. It answers in
            // milliseconds — the cheap gate, then `landing…` — and the merge, the verify and the
            // fast-forward run on their own task. See LandBegin for why, and for the two
            // constraints that survive the change.
            case "land":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                w.WriteLine(LandBegin(tid, out var landStarted));
                if (!landStarted) w.WriteLine("##exit 1");
                break;
            }
            // The other half of the protocol: where the outcome is read from. A land also
            // ANNOUNCES its outcome (into the ticket's pane, or the dispatcher's), so nothing
            // depends on anyone polling — this exists so `dodona land` can still hand a shell an
            // exit code, and so a person can ask.
            //
            // IT MUST NEVER SUMMON A DAEMON, and the client end enforces that (CLAUDE.md §3.2's
            // incident: a summoned daemon runs its warm-up and spawns four model-backed
            // processes). A poll that woke a daemon to be told "no land here" would be that
            // incident on a 250 ms timer.
            // R4: read the ticket's completion record (D-R8). A READ ONLY -- it assembles
            // nothing, because assembly is triggered by a turn ending and a command that built
            // one on demand would be a second, differently-timed producer of the same artifact
            // (the "two implementations of make-a-ticket drift on exactly the checks that
            // matter" lesson, from `MakeTicket`).
            //
            // It exists because R6 is the surface a person will actually read this through, and
            // until then an affordance no verb can reach is where the next defect lives
            // (CLAUDE.md §3.1). It is also how `m1` reads a record without hand-rolling SQL.
            // NEVER SUMMONS a daemon -- see the no-summon list in Program.cs, and §3.2.
            case "ticket-record":
            {
                var rtid = e.GetProperty("ticket").GetInt64();
                var rec = _store.LastTicketEvent(rtid, "completion_record");
                if (rec is null)
                {
                    // Says WHICH nothing this is. A ticket that has never finished a turn, one
                    // whose worktree could not be read, and one whose lane never had the trigger
                    // wired all look identical from the outside, and the last of those is the
                    // failure mode this phase was warned about -- so the reasons are named and
                    // the events that carry them are named too.
                    var why = _store.LastTicketEvent(rtid, "completion_record_impossible", "completion_record_failed");
                    w.WriteLine($"no record for ticket {rtid}" +
                                (why is not null ? $" -- last attempt: {why.Value.Kind} {why.Value.Detail}" : ""));
                    if (why is null)
                        w.WriteLine("       (a record is written when a turn ENDS on the ticket's lane and the worktree has " +
                                    "changed since the last one; `dodona tickets` shows whether the ticket has a lane at all)");
                    w.WriteLine("##exit 1");
                    break;
                }
                var braceAt = rec.Value.Detail.IndexOf('{');
                w.WriteLine(braceAt < 0 ? rec.Value.Detail : rec.Value.Detail[braceAt..]);
                break;
            }
            case "land-status":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                if (!_lands.TryGetValue(tid, out var run))
                {
                    // Deliberately NOT an error about the ticket: this daemon simply has no land
                    // for it. In-flight lands are in memory only, so a restart forgets them —
                    // which is correct (nothing can go stale) and has to be said out loud.
                    w.WriteLine($"state=none");
                    w.WriteLine($"no land in flight for ticket {tid} in this daemon — `dodona tickets` says whether it landed, and a daemon restart forgets lands it did not finish");
                    w.WriteLine("##exit 1");
                    break;
                }
                if (run.Done)
                {
                    w.WriteLine($"state=done ok={(run.Ok ? 1 : 0)}");
                    w.WriteLine(run.Message);
                    if (!run.Ok) w.WriteLine("##exit 1");
                }
                else w.WriteLine($"state=running elapsed={(int)(DateTime.UtcNow - run.StartedUtc).TotalSeconds}s");
                break;
            }
            case "policy":
            {
                // Inspectable without spawning anything: ask what a sentence would get.
                var probe = e.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString()! : "";
                if (probe.Length > 0)
                {
                    var (clean, om, oe) = Policy.StripOverrides(probe);
                    var c = Policy.Resolve(clean, _config.Rules, _config.Model, _config.Effort, om, oe);
                    w.WriteLine($"{c.Model} {(c.Effort is { Length: > 0 } ? c.Effort : "-")}  {c.Describe}");
                    if (clean != probe.Trim()) w.WriteLine($"prompt: {clean}");
                    break;
                }
                w.WriteLine($"default    {_config.Model,-8} {_config.Effort}");
                w.WriteLine($"router     {_config.RouterModel,-8} {(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}");
                foreach (var r in _config.Rules)
                    w.WriteLine($"rule       {r.Model,-8} {r.Effort,-7} {(r.Why is { Length: > 0 } ? r.Why : "-"),-12} {r.When}");
                w.WriteLine("override   @opus @max <text>   (model and effort are fixed when a lane starts)");
                break;
            }
            case "repo-status":
            {
                // What the picker (and anyone else) needs to know before offering a fix -- ABOUT
                // THE PROJECT IT WAS ASKED ABOUT (trap T5, P2.4).
                if (!TryCommandProject(e, out var statProj, out var statRefusal))
                { w.WriteLine(statRefusal!); w.WriteLine("##exit 1"); break; }
                var statCfg = ConfigForProject(statProj);
                var isRepo = Git.IsRepo(statProj);
                var nested = isRepo ? new List<string>() : Git.FindRepos(statProj);
                var entries = Directory.Exists(statProj)
                    ? Directory.EnumerateFileSystemEntries(statProj).Where(p => Path.GetFileName(p) is not ".dodona" and not ".git").Take(1).Count()
                    : 0;
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    root = statProj,
                    isRepo,
                    hasCommit = isRepo && Git.HasCommit(statProj),
                    empty = entries == 0,
                    nested = nested.Select(r => Path.GetRelativePath(statProj, r)).ToList(),
                    main = statCfg.Main,
                }));
                break;
            }
            case "repo-init":
            {
                // TRAP T5, FIXED (P2.4). This acted on the FIRST project unconditionally, so an
                // agent working in project B that ran `dodona repo-init` ran `git init` in
                // project A. Silently: every line it printed named A, and an agent that had
                // never seen A's path had no way to notice. `git init` in the wrong folder is
                // not reversible by anything Dodona knows how to do.
                if (!TryCommandProject(e, out var initProj, out var initRefusal))
                { w.WriteLine(initRefusal!); w.WriteLine("##exit 1"); break; }
                var adopt = e.TryGetProperty("adopt", out var ad) && ad.ValueKind == JsonValueKind.True;
                RepoInitOp(initProj, adopt, w);
                break;
            }
            case "questions":
            {
                // The workspace's own open questions, in the same tab-separated shape the
                // concierge's `questions` command prints (Concierge.cs:198). One shape, because
                // the ask overlay and this command are two renderings of one row (D-L4) and a
                // second format would be a second thing to keep in step.
                foreach (var q in _store.OpenQuestions())
                    w.WriteLine($"{q.Id}\t{q.Input}\t{q.Candidates}");
                break;
            }
            case "answer":
            {
                foreach (var line in await AnswerQuestion(e.GetProperty("id").GetInt64(),
                                                          e.GetProperty("answer").GetString() ?? ""))
                    w.WriteLine(line);
                break;
            }
            case "project-gone":
            {
                // P2.6 / trap T4: `workspace-detach` and `workspace-move` are REGISTRY edits made
                // by the CLI, and they touched no lane row at all -- so a live agent kept working
                // in a folder this workspace no longer owns, and `lane-respawn` would have put a
                // fresh one there too (its only test was `Directory.Exists`, which passes: the
                // folder is still there, it just belongs elsewhere now).
                //
                // The CLI sends this ONLY when the daemon is already live -- it must never summon
                // one, because summoning runs the warm-up and a registry edit that starts four
                // haiku processes is the §3.2 incident wearing a different hat.
                var gonePath = Instance.Canonical(e.GetProperty("project").GetString()!);
                var stopped = new List<long>();
                // P5.5 FIRST: THE MANAGERS THIS PROJECT HAD, whose cwd can never name it.
                // `project-gone` matched on `lanes.cwd`, and a brain's cwd is the neutral
                // directory (P5.8) -- so a brain for a departing project was invisible to this
                // handler, invisible to reconcile's old count-and-kill loop (which only ever
                // asked "how many of this role"), and would have sat there answering questions
                // about a project the workspace no longer has until its 30-minute lease ran out.
                // The obvious source of the next leak, and a lifecycle event that did not exist.
                //
                // A manager is fungible infrastructure with no transcript anyone reads, so its
                // row is retired rather than left visible the way a work lane's is.
                var goneManagers = new List<long>();
                // Read the project list ONCE, not per lane: Members() re-opens the registry on
                // every call, and this is a loop over every row in the store.
                var goneProjects = ProjectPaths();
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || !Projects.IsManagementRole(l.Role)) continue;
                    if (!string.Equals(RegistrationKey(l, goneProjects), gonePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_lanes.TryGetValue(l.Id, out var mrt)) { mrt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _brainLocks.TryRemove(l.Id, out _);
                    if (l.Role == "brain") _brainLo.TryRemove(gonePath, out _);
                    if (l.Role == "brain-hi") _brainHi.TryRemove(gonePath, out _);
                    if (l.Role == "router" && _routerLo == l.Id) _routerLo = -1;
                    _store.LaneState(l.Id, "dead");
                    _store.Event("brain_unregistered", l.Id, $"role={l.Role}: project {gonePath} left this workspace");
                    goneManagers.Add(l.Id);
                }
                if (goneManagers.Count > 0)
                    Announce($"[dodona] {gonePath} left this workspace: stopped {goneManagers.Count} management agent(s) that were for it " +
                             $"({string.Join(", ", goneManagers)})");
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || l.Cwd is not { Length: > 0 }) continue;
                    if (Projects.Of(new[] { gonePath }, l.Cwd) is null) continue;
                    // The AGENT goes; the lane ROW and its whole transcript stay (§12 -- nothing
                    // here deletes history). `lane-respawn --project <p>` is the way back, and
                    // the refusal in that handler names it.
                    // Ask the SHIM to go, over its own pipe -- it takes the child tree with it and
                    // exits cleanly, which needs no pid bookkeeping (CLAUDE.md §4). A lane this
                    // daemon never connected to still has a recorded pipe, and a shim that has
                    // been buffering for a predecessor is exactly the case worth covering.
                    if (_lanes.TryGetValue(l.Id, out var grt)) { grt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _store.LaneState(l.Id, "unreachable");
                    _store.Event("lane_project_detached", l.Id, $"project={gonePath} cwd={l.Cwd}");
                    _store.PaneEvent(l.Id, "announcement",
                        $"this project left the workspace, so the agent was stopped -- re-home with `dodona lane-respawn {l.Id} --project <project>`",
                        null, null, acked: true);
                    stopped.Add(l.Id);
                }
                if (stopped.Count > 0)
                    Announce($"[dodona] {gonePath} left this workspace: stopped {stopped.Count} lane(s) that were working in it ({string.Join(", ", stopped)})");
                w.WriteLine($"project {gonePath}: stopped {stopped.Count} lane(s)");
                break;
            }

            // ---------------- hot swap (M4, §13/§14) ----------------
            case "swap":
            {
                var exe = e.GetProperty("exe").GetString()!;
                var mode = e.TryGetProperty("mode", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString()! : "ask";
                var (handedOff, lines) = await ConsiderSwapAsync(exe, mode);
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swap-answer":
            {
                var answer = e.GetProperty("answer").GetString()!;
                var live = _store.SwapLive();
                if (live is null) { w.WriteLine("error: no update is waiting on an answer"); break; }
                switch (answer)
                {
                    case "now":
                    {
                        // The explicit override: swap even though something is in the way.
                        var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "now");
                        foreach (var l in lines) w.WriteLine(l);
                        return handedOff;
                    }
                    case "when-it-lands":
                        _store.SwapSet(live.Id, "when-it-lands", "armed");
                        _store.Event("swap_armed", null, $"swap {live.Id} build {live.Build}: {live.Blocker}");
                        Announce($"[dodona] update {live.Build} armed — swapping the instant this clears: {live.Blocker}");
                        w.WriteLine($"armed: swap {live.Id} fires the instant the blocker clears ({live.Blocker})");
                        break;
                    case "hold":
                        _store.SwapSet(live.Id, "hold", "held");
                        _store.Event("swap_held", null, $"swap {live.Id} build {live.Build}");
                        Announce($"[dodona] update {live.Build} held — say `dodona swap-answer now` when you want it");
                        w.WriteLine($"held: swap {live.Id} parked until you say so");
                        break;
                    default:
                        w.WriteLine("error: answer must be now | when-it-lands | hold");
                        break;
                }
                break;
            }
            case "swap-fire":
            {
                // The armed swap's condition cleared; the ticker woke us through our own
                // control pipe so this lands on the loop thread like any other command.
                var live = _store.SwapLive();
                if (live is null || live.State != "armed") { w.WriteLine("no armed swap"); break; }
                var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "armed");
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swaps":
                foreach (var row in _store.SwapsAll()) w.WriteLine(row);
                w.WriteLine($"running: build {Ver.Build} schema {Ver.Schema} shim-protocol {Ver.ShimProtocol} exe {Ver.ExePath}");
                // The COMMIT, so what is running can be checked against `git log` and
                // bisected (P2.6). The build stamp above maps to nothing off this machine.
                w.WriteLine($"  {Ver.ProvenanceLine}");
                break;

            case "stop-daemon":
            {
                // A STOP CAN NOW ARRIVE DURING A LAND, which R3.5 made possible: the pipe used to
                // be held for the whole land, so this command physically could not be delivered
                // until it finished. Losing the land's task with the process is recoverable —
                // main only moves in the very last step, and re-running `land` re-merges (a
                // no-op), re-verifies and re-fast-forwards under the token the ticket still
                // holds. What is NOT acceptable is that happening silently (CLAUDE.md §0.1's
                // quietly-stale), so it is announced, recorded, and said on this reply.
                //
                // A hot SWAP needs no equivalent: `Blockers` already refuses to swap while any
                // merge token is held, and R3.5's first load-bearing constraint is that the token
                // is held across the whole land. So an in-flight land arms the swap rather than
                // being cut in half by it.
                foreach (var inflight in _lands.Values.Where(x => !x.Done).ToList())
                {
                    _store.Event("land_interrupted", null, $"ticket {inflight.Ticket}: daemon stopped mid-land");
                    if (_store.Ticket(inflight.Ticket) is Store.TicketRow it)
                        Announce(it, $"ticket {inflight.Ticket}'s land was interrupted by a daemon stop — nothing was lost (the trunk only moves in the last step): re-run dodona land {inflight.Ticket}");
                    w.WriteLine($"warning: ticket {inflight.Ticket} was mid-land — nothing was lost, re-run dodona land {inflight.Ticket}");
                }
                w.WriteLine("stopping (lanes keep running)");
                return true;
            }

            case "workspace-forgotten":
            {
                // P2.7, HANDED TO PHASE 5 BY PHASE 2 ON PURPOSE. `Registry.Forget` deletes every
                // `members` row in one transaction, and unlike `workspace-detach` it was wired to
                // nothing -- so forgetting a live workspace left agents working in folders the
                // registry no longer records, exactly the trap-T4 state Phase 2 closed for
                // detach. It was deferred because forget also orphans the DAEMON, which is a
                // lifecycle call and belongs beside this phase's reaping rather than bolted onto
                // detach.
                //
                // WHY THE DAEMON MUST GO TOO, and it is not tidiness: `publish --all` resolves
                // its swap targets by id FROM THE REGISTRY, so a daemon whose workspace has been
                // forgotten can never be hot-swapped again. It becomes an un-updatable process
                // holding agents nothing lists -- the shape of every orphan incident in this
                // codebase.
                //
                // AND IT IS REVERSIBLE, which is what makes acting rather than asking correct
                // (CLAUDE.md §0.1): forget keeps the store directory, so re-creating a workspace
                // over the same folder wakes it with every transcript intact. The announcement
                // says so.
                //
                // Every project is gone by definition, so every lane is stranded. Work lanes keep
                // their rows and their transcripts (§12); managers are retired, being fungible
                // infrastructure nobody reads.
                var forgottenLanes = new List<long>();
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || l.Role == "dispatcher") continue;
                    if (_lanes.TryGetValue(l.Id, out var frt)) { frt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _brainLocks.TryRemove(l.Id, out _);
                    if (Projects.IsManagementRole(l.Role))
                    {
                        _store.LaneState(l.Id, "dead");
                        _store.Event("brain_unregistered", l.Id, $"role={l.Role}: workspace {_wsName} was forgotten");
                    }
                    else
                    {
                        _store.LaneState(l.Id, "unreachable");
                        _store.Event("lane_project_detached", l.Id, $"workspace {_wsName} was forgotten; project={(l.Project.Length > 0 ? l.Project : l.Cwd)}");
                        _store.PaneEvent(l.Id, "announcement",
                            "this workspace was forgotten, so the agent was stopped -- the transcript is kept; re-create the workspace to resume",
                            null, null, acked: true);
                    }
                    forgottenLanes.Add(l.Id);
                }
                _brainLo.Clear();
                _brainHi.Clear();
                _routerLo = -1;
                _store.Event("workspace_forgotten", null,
                    $"stopped {forgottenLanes.Count} lane(s) and this daemon; store kept at {Paths.Store(_instanceId)}");
                Announce($"[dodona] workspace {_wsName} was forgotten: stopped {forgottenLanes.Count} agent(s) and this daemon. " +
                         $"Nothing was deleted -- the store is still at {Paths.Store(_instanceId)}, so re-creating the workspace brings it all back.");
                w.WriteLine($"workspace {_wsName} forgotten: stopped {forgottenLanes.Count} lane(s), stopping this daemon");
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------- hot swap (§13/§14)

    sealed record NewBuild(string Exe, string Build, int Schema, int ShimProtocol);

    /// <summary>Ask a candidate binary what it is. Running `<exe> version --json` is the
    /// only honest way — the file name proves nothing, and we must know its schema and
    /// shim protocol BEFORE it touches the store.</summary>
    static NewBuild? Probe(string exe, out string error)
    {
        error = "";
        if (!File.Exists(exe)) { error = $"no such binary: {exe}"; return null; }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("--json");
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            using var d = JsonDocument.Parse(so);
            return new NewBuild(exe,
                d.RootElement.GetProperty("build").GetString()!,
                d.RootElement.GetProperty("schema").GetInt32(),
                d.RootElement.GetProperty("shimProtocol").GetInt32());
        }
        catch (Exception ex) { error = $"binary did not answer `version --json` ({ex.Message})"; return null; }
    }

    /// <summary>What stands in the way of a seamless swap (§14). Empty means go.
    ///
    /// A schema MIGRATION is deliberately not here any more: it used to park every
    /// migrating build behind a question, and the answer was always "yes, but keep an
    /// undo" — so HandoffAsync now takes the backup itself and proceeds (never-stuck,
    /// 2026-08-18). Only a DOWNGRADE still refuses, in ConsiderSwapAsync, because a
    /// build that cannot read the store cannot be allowed to open it at all.</summary>
    List<string> Blockers(NewBuild nb)
    {
        var blockers = new List<string>();

        // The live shims are the authority, not our own constant: they were spawned by
        // whichever binary was running then, and the successor has to talk to THEM.
        // Only a shim NEWER than the candidate blocks: shims can never be swapped (they
        // own their child's stdio), so every daemon commits to speaking all protocols
        // ≤ its own — that commitment is what keeps protocol bumps from freezing
        // updates for as long as one long-lived lane survives. Bump Ver.ShimProtocol
        // WITHOUT keeping the old dialect readable and this line is what breaks.
        var stranded = _lanes.Values.Where(l => l.Connected && l.ShimProtocol > nb.ShimProtocol).ToList();
        if (stranded.Count > 0)
            blockers.Add($"shim protocol v{stranded[0].ShimProtocol}→v{nb.ShimProtocol} with {stranded.Count} live shim(s)");

        // Any repository mid-merge blocks the swap — the tokens are independent, but the
        // daemon that would vanish underneath them is not.
        //
        // THIS IS ALSO WHAT COVERS AN IN-FLIGHT LAND (R3.5). Since the land left the control pipe
        // a swap could arrive in the middle of one, and cutting a land in half is exactly what
        // this list exists to prevent — it needs no new entry only because R3.5's first
        // load-bearing constraint is that the token is held across the WHOLE flow. Break that
        // constraint and this stops covering it, silently.
        foreach (var tok in _store.TokensAll())
        {
            if (tok.Holder is not long h) continue;
            if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() <= DateTime.UtcNow) continue;
            var t = _store.Ticket(h);
            blockers.Add($"{t?.Title ?? $"ticket {h}"} is mid-merge{(tok.Repo == "." ? "" : $" in {tok.Repo}")}");
        }
        return blockers;
    }

    /// <summary>The swap decision. Clear road → hand off. Something in the way → arm:
    /// record the proposal and fire it the instant the blocker clears, announcing both.
    /// Nothing waits on a human (never-stuck, 2026-08-18) — the operator lost a morning
    /// to updates parked behind questions. `swap-answer hold` parks one on purpose;
    /// `swap-answer now` forces through a blocker. Only a schema DOWNGRADE refuses.</summary>
    async Task<(bool HandedOff, List<string> Lines)> ConsiderSwapAsync(string exe, string mode)
    {
        var lines = new List<string>();
        var nb = Probe(exe, out var probeError);
        if (nb is null)
        {
            _store.Event("swap_refused", null, $"{exe}: {probeError}");
            lines.Add($"error: {probeError}");
            lines.Add("##exit 1");
            return (false, lines);
        }
        if (nb.Schema < Ver.Schema)
        {
            // A downgrade cannot read this store at all. Not a decision — a refusal.
            _store.Event("swap_refused", null, $"{nb.Build}: schema v{nb.Schema} < live v{Ver.Schema}");
            lines.Add($"refused: build {nb.Build} expects schema v{nb.Schema}, this store is v{Ver.Schema} — a downgrade would not be able to read it");
            lines.Add("##exit 1");
            return (false, lines);
        }

        var blockers = Blockers(nb);
        if (blockers.Count > 0 && mode != "now")
        {
            var blocker = string.Join("; ", blockers);
            if (mode == "armed")
            {
                lines.Add($"still blocked: {blocker}");
                return (false, lines);
            }
            // Blocked → armed, not asked. The ticker fires it the moment the blocker
            // clears; the announcement carries the two overrides. "ask" survives only
            // as the wire default's name — its behavior is now when-it-lands.
            var id = _store.SwapCreate(nb.Exe, nb.Build, nb.Schema, nb.ShimProtocol, blocker, "when-it-lands", "armed");
            _store.Event("swap_armed", null, $"swap {id} build {nb.Build}: {blocker}");
            Announce($"[dodona] update {nb.Build} armed — lands the instant this clears: {blocker} (dodona swap-answer now to force, hold to park)");
            lines.Add($"armed: update {nb.Build} fires when this clears — {blocker}");
            lines.Add("override: dodona swap-answer now | hold");
            return (false, lines);
        }

        var swapId = _store.SwapCreate(nb.Exe, nb.Build, nb.Schema, nb.ShimProtocol,
                                       blockers.Count > 0 ? string.Join("; ", blockers) : null,
                                       mode, "pending");
        var (ok, msg) = await HandoffAsync(nb, swapId, blockers);
        lines.Add(msg);
        if (!ok) lines.Add("##exit 1");
        return (ok, lines);
    }

    /// <summary>Successor handoff (§13). The old daemon spawns the new binary, waits for
    /// it to signal ready, then releases everything and exits. If the successor never
    /// answers, THIS daemon keeps running — a bad publish must never take the system
    /// down.</summary>
    async Task<(bool Ok, string Msg)> HandoffAsync(NewBuild nb, long swapId, List<string> blockers)
    {
        var handoffPipe = Instance.HandoffPipe(_instanceId);
        var server = new NamedPipeServerStream(handoffPipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Process? p = null;
        try
        {
            // A migrating successor gets an undo BEFORE it exists (§14 revised): back up
            // the store, then proceed. This is what turned "schema migration" from a
            // parked question into an ordinary swap — act, announce, allow undo (§11).
            // The backup is taken by the ONE writer while it is still the one writer.
            if (nb.Schema > Ver.Schema)
            {
                var bak = Paths.Store(_instanceId) + $".pre-v{Ver.Schema}";
                _store.Backup(bak);
                _store.Event("store_backed_up", null, $"swap {swapId}: schema v{Ver.Schema}→v{nb.Schema}, backup {bak}");
                Announce($"[dodona] store backed up before migration v{Ver.Schema}→v{nb.Schema} — undo: dodona stop-daemon, then restore {bak} over store.db");
            }

            var psi = new ProcessStartInfo(nb.Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _primary };
            psi.ArgumentList.Add("daemon");
            psi.ArgumentList.Add("--workspace");
            psi.ArgumentList.Add(_instanceId);
            psi.ArgumentList.Add("--successor");
            p = Process.Start(psi);
            _store.Event("swap_spawned", null, $"swap {swapId} build {nb.Build} pid={p?.Id} exe={nb.Exe}");

            using var cts = new CancellationTokenSource(30000);
            await server.WaitForConnectionAsync(cts.Token);
            var r = new StreamReader(server);
            var w = new StreamWriter(server) { AutoFlush = true };
            var ready = await r.ReadLineAsync();
            if (ready is null || !ready.StartsWith("ready "))
                throw new InvalidOperationException($"successor said '{ready}' instead of ready");

            if (blockers.Count > 0)
                _store.Event("swap_forced", null, $"swap {swapId} over: {string.Join("; ", blockers)}");
            _store.Event("daemon_handoff", null, $"swap {swapId}: {Ver.Build} (pid {Environment.ProcessId}) → {nb.Build} ({ready})");
            _store.SwapSet(swapId, "now", "swapped", ready);

            w.WriteLine($"go {Environment.ProcessId}");
            await Task.Delay(150);          // let the successor read `go` before our handles close
            return (true, $"handed off to build {nb.Build} (pid {p?.Id}); this daemon is exiting — lanes keep running");
        }
        catch (Exception ex)
        {
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            _store.SwapSet(swapId, "now", "failed", ex.Message);
            _store.Event("swap_failed", null, $"swap {swapId} build {nb.Build}: {ex.Message}");
            Announce($"[dodona] update {nb.Build} FAILED to start — staying on {Ver.Build}");
            return (false, $"swap failed ({ex.Message}) — this daemon is still running, nothing was lost");
        }
        finally
        {
            try { server.Dispose(); } catch { }
        }
    }

    /// <summary>"When it lands" defers to a CONDITION, not a timer (§14): poll the
    /// blockers and fire the instant they clear. The fire itself goes through our own
    /// control pipe so it is serialized with every other command.</summary>
    void StartSwapTicker() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                var live = _store.SwapLive();
                if (live is null || live.State != "armed") continue;
                var nb = Probe(live.Exe, out _);
                if (nb is null) continue;
                if (Blockers(nb).Count > 0) continue;

                var pipe = new NamedPipeClientStream(".", _ctlPipe, PipeDirection.InOut);
                try
                {
                    await pipe.ConnectAsync(2000);
                    var w = new StreamWriter(pipe) { AutoFlush = true };
                    var r = new StreamReader(pipe);
                    w.WriteLine(JsonSerializer.Serialize(new { cmd = "swap-fire" }));
                    while (await r.ReadLineAsync() is string l && l != "##end") { }
                }
                finally { try { pipe.Dispose(); } catch { } }
                // Fired. On success this process is exiting anyway; on failure the swap
                // row is 'failed' and SwapLive() goes quiet — but blocked swaps now arm
                // THEMSELVES (auto-arm), so a later one needs this ticker still alive.
                // A `return` here left the first failed fire as the last fire ever.
            }
            catch { /* next tick */ }
        }
    });

    /// <summary>
    /// Publish-on-drift: when <c>main</c> moves, this daemon builds that commit and swaps
    /// itself to it, so "work is done but not live" cannot happen and no person and no agent
    /// has to remember to publish. It exists because edited-not-built, built-not-published and
    /// published-not-committed each blocked the operator once in a single day, and an
    /// instruction in CLAUDE.md is advisory while a watcher is not -- the claim-gate reasoning
    /// (design §6).
    ///
    /// THE QUESTION IS NOW EXACT (RECOVERY-PHASES P2.3). It used to be "is any source file
    /// newer than the running image?", which needed five separate guards to behave and looped
    /// 64 times in one afternoon regardless. It is now <c>git rev-parse main</c> against the
    /// commit this build was made from: a comparison of two SHAs, with no clock, no filesystem,
    /// no partial-write window and no three-projects-versus-one-binary asymmetry.
    /// <see cref="Ver.Provenance"/> carries the numbers of the failure this replaced.
    ///
    /// WHAT WENT WITH IT, all of it deletion (P2.4): the debounce (a commit is already atomic
    /// and quiet), the <c>.built-from</c> stamp (the commit is in the assembly),
    /// <c>kv.autopublish_last_tried</c> (the SHA is its own guard -- if main has not moved there
    /// is nothing to do, and if it has, doing it once is correct), and the 30-minute dirty-tree
    /// nag (uncommitted work can no longer reach the app at all, so nagging about it answers a
    /// question nobody can now ask).
    ///
    /// WHAT STAYED, because it was never about mtimes: consecutive-failure surrender. Measured
    /// on the operator's instance at 16 attempts and 16 failures in one afternoon, each a full
    /// three-project build, until the failure that mattered was buried under fifteen copies of
    /// itself. A broken main must not rebuild forever.
    ///
    /// Safety is inherited, not added: the publish it runs goes through the ordinary M4 path, so
    /// a failed build changes nothing and is announced, the new binary must answer
    /// <c>version --json</c> before anything is promoted, the desktop shortcut moves only onto a
    /// build a daemon accepted, and a mid-merge lane blocks the swap with the usual three
    /// answers. None of those guards are about mtimes, so none of them were touched.
    /// </summary>
    void StartDriftWatcher()
    {
        if (!_config.AutoPublish) return;
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1") return;   // suites own their lifetime

        // A build that cannot say what commit it came from cannot ask the exact question, and
        // it must NOT fall back to guessing -- the old code degraded to the image's own mtime in
        // exactly this case, which is the loop-prone comparison wearing a fallback.
        //
        // BUT PARKING HERE WAS WRONG TOO, and it is the operator's standing directive that says
        // so: never hung, halted, stuck or outdated, and when you add a wait, name the thing
        // that un-sticks it -- a condition, never a person (CLAUDE.md 0.1). The first version
        // announced "I am not watching" and stopped, which left auto-publish silently OFF until
        // somebody read a line in the feed. That happened for real: the very first publish of
        // this feature was performed by the PREVIOUS binary, which had no stamping code, so the
        // installed build came out unlabelled and its watcher declined. Nobody would have known.
        //
        // THIS IS A BOOTSTRAP, and a bootstrap is exactly the shape that should arm itself: the
        // action is bounded (one publish), reversible (a failed build changes nothing), and it
        // can only ever publish MAIN, which is the one thing this daemon is already allowed to
        // publish. So: do it once, say what was done, and if the result STILL carries no
        // provenance, surrender loudly rather than trying again -- an unstamped build that
        // republishes itself is the 64-iteration loop with a new cause.
        if (Ver.NoProvenance)
        {
            _store.Event("autopublish_no_provenance", null, $"build {Ver.Build} exe {Ver.ExePath}");
            if (_store.KvGet("autopublish_bootstrap_tried") == "1")
            {
                _store.Event("autopublish_surrendered", null, "bootstrap publish did not produce a stamped build");
                Announce("[dodona] auto-publish is ON, but the build it produced STILL carries no commit stamp, so " +
                         "it has stopped trying. The live app will not follow main until this is fixed. " +
                         "(A build made by `dev build`, or published with --exe, cannot be stamped.)");
                return;
            }
            _store.KvSet("autopublish_bootstrap_tried", "1");
            Announce("[dodona] this build carries no commit stamp (it was built before stamping existed, or with " +
                     "--exe), so it cannot tell whether main has moved. Publishing main ONCE to arm itself — " +
                     "nothing else changes, and a failed build changes nothing at all.");
            _ = Task.Run(() =>
            {
                try
                {
                    var boot = string.IsNullOrEmpty(_config.AutoPublishProject) ? _primary : _config.AutoPublishProject;
                    if (string.IsNullOrWhiteSpace(boot) || !Path.IsPathRooted(boot) || !Git.IsRepo(boot)) return;
                    var (bc, bout) = Git.Run(boot, "rev-parse", ConfigFor(".").Main);
                    if (bc != 0 || bout.Trim().Length != 40) return;
                    var (code, output) = RunPublish(Path.GetFullPath(boot), bout.Trim());
                    if (code != 0)
                        _store.Event("autopublish_failed", null, $"bootstrap publish: {Truncate(output, 800)}");
                }
                catch (Exception ex) { _store.Event("autopublish_error", null, $"bootstrap: {ex.Message}"); }
            });
            return;     // a successful bootstrap hands off and this process exits
        }

        var project = string.IsNullOrEmpty(_config.AutoPublishProject) ? _primary : _config.AutoPublishProject;

        // AN ABSOLUTE PATH OR NOTHING, and this is not defensive dressing -- it is the bug this
        // guard was found by. A workspace with no attached member has an EMPTY _primary, so
        // `Path.Combine("", "src", ...)` is a RELATIVE path, resolved against whatever directory
        // this daemon happened to be started from. Measured: a test workspace with no members
        // watched the OPERATOR'S live repo, found its main, and published it -- because the
        // daemon's cwd was that repo. An actor silently building a tree it does not own is the
        // whole failure class Phase 2 exists to close (RECOVERY-PHASES section 0), so the answer
        // is to refuse and say which, never to resolve it against a convenient default.
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathRooted(project))
        {
            _store.Event("autopublish_misconfigured", null,
                $"no absolute project to watch (first project='{_primary}', autoPublishProject='{_config.AutoPublishProject}')");
            Announce("[dodona] autoPublish is on, but this workspace has no absolute source tree to watch " +
                     "(no member attached, and no autoPublishProject set) — nothing is being watched");
            return;
        }
        project = Path.GetFullPath(project);
        if (!File.Exists(Path.Combine(project, "src", "Dodona", "Dodona.csproj")))
        {
            _store.Event("autopublish_misconfigured", null, $"{project} is not a Dodona source tree");
            Announce($"[dodona] autoPublish is on, but {project} has no src/Dodona — nothing is being watched");
            return;
        }
        if (!Git.IsRepo(project))
        {
            // The question is `git rev-parse main`. Without a repo there is no question, and
            // guessing from mtimes is what this phase deleted.
            _store.Event("autopublish_misconfigured", null, $"{project} is not a git repository");
            Announce($"[dodona] autoPublish is on, but {project} is not a git repository — nothing is being watched");
            return;
        }

        // We know our own commit, so any previous bootstrap succeeded. Clearing this is what
        // keeps the guard from outliving the problem: it is a one-shot for THIS situation, not a
        // permanent mark against the workspace.
        if (_store.KvGet("autopublish_bootstrap_tried") == "1") _store.KvSet("autopublish_bootstrap_tried", "");

        var mainBranch = ConfigFor(".").Main;
        _store.Event("autopublish_watching", null,
            $"{project} tracking {mainBranch}; running {Ver.Short(Ver.Commit)}, baseline {Ver.Short(Ver.MainBaseline)}");
        if (Ver.IsTrial)
            Announce($"[dodona] running a TRIAL of {Ver.Branch}@{Ver.Short(Ver.Commit)} — the next commit to " +
                     $"{mainBranch} replaces it (it was cut at {Ver.Short(Ver.MainBaseline)})");

        _ = Task.Run(async () =>
        {
            // Give up after three consecutive failures, say so ONCE, and stay quiet until
            // something changes. A publish by hand clears it, because a successful swap ends
            // this process entirely.
            const int giveUpAfter = 3;
            int consecutiveFailures = 0;
            bool surrendered = false;

            while (true)
            {
                await Task.Delay(15000);
                if (surrendered) continue;
                try
                {
                    // TWO SHAs. That is the whole comparison.
                    //
                    // The baseline is Ver.MainBaseline and not Ver.Commit, which is what makes a
                    // trial behave as P2.5 promises: a trial carries the main SHA it was cut
                    // against, so it sits still until main moves PAST that point and is then
                    // replaced. For an ordinary main build the two are the same value, so this
                    // costs no special case.
                    // Git.Run and not Git.Sha: Sha THROWS when the ref does not resolve, so a
                    // `target.Length == 0` guard after it is dead code that reads like a check.
                    // A missing main is a normal transient state (a fresh repo, a fetch in
                    // flight), not an error worth an event every 15 seconds.
                    var (rc, rout) = Git.Run(project, "rev-parse", mainBranch);
                    if (rc != 0) continue;                          // no such ref yet; nothing to compare
                    var target = rout.Trim();
                    if (target.Length != 40) continue;              // not a resolved sha
                    if (target == Ver.MainBaseline) continue;       // up to date, exactly

                    _store.Event("autopublish_started", null,
                        $"{mainBranch} at {Ver.Short(target)}, this build baselined {Ver.Short(Ver.MainBaseline)}");
                    Announce($"[dodona] {mainBranch} moved to {Ver.Short(target)} — building that commit and swapping to stay live");

                    var (code, output) = RunPublish(project, target);
                    if (code != 0)
                    {
                        var reason = output.Split('\n').LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                                     ?? output.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "unknown";
                        consecutiveFailures++;
                        _store.Event("autopublish_failed", null,
                            $"attempt {consecutiveFailures}/{giveUpAfter} for {Ver.Short(target)}: {Truncate(output, 800)}");
                        if (consecutiveFailures >= giveUpAfter)
                        {
                            surrendered = true;
                            _store.Event("autopublish_surrendered", null,
                                $"{consecutiveFailures} consecutive failures; watching stopped until a manual publish");
                            Announce($"[dodona] auto-publish has failed {consecutiveFailures} times running and has STOPPED trying — " +
                                     $"the live app stays behind {mainBranch} until you publish by hand. Last reason: {Truncate(reason.Trim(), 140)}");
                        }
                        else
                            Announce($"[dodona] auto-publish FAILED — the live app is now BEHIND {mainBranch}: {Truncate(reason.Trim(), 160)}");
                    }
                    else consecutiveFailures = 0;
                    // success: the swap arrives through our own control pipe and this daemon
                    // exits mid-handoff — nothing more to do here. A parked swap (mid-merge
                    // blocker) already announced its three answers.
                }
                catch (Exception ex) { _store.Event("autopublish_error", null, ex.Message); }
            }
        });
    }

    /// <summary>Publish <paramref name="commit"/> out of <paramref name="project"/>.
    ///
    /// <c>--from &lt;sha&gt;</c> is what keeps the publisher out of everybody's way (P2.3): it
    /// makes publish check that commit out into a detached worktree of its OWN and build there,
    /// so the tree an operator or another session is working in is never touched and its
    /// <c>obj/</c> is never contended for. Publish then sees HEAD == main inside that worktree
    /// and stamps main provenance, with no flag to pass and no special case.</summary>
    (int Code, string Output) RunPublish(string project, string commit)
    {
        var psi = new ProcessStartInfo(Ver.ExePath)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _primary };
        // Scoped to THIS workspace, never --all: an auto-publish is one workspace noticing
        // its own sources moved, and broadcasting a swap to every daemon on the machine
        // because one repo was edited is exactly what §7 set out to stop.
        foreach (var a in new[] { "publish", "--project", project, "--from", commit, "--workspace", _instanceId }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errT = Task.Run(() => p.StandardError.ReadToEnd());
        var so = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(600000)) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, "publish timed out after 10 minutes"); }
        return (p.ExitCode, so + "\n" + errT.Result);
    }

    /// <summary>Old binary directories are garbage once no instance runs them (§13). A
    /// running image is locked by Windows, which makes "is anyone using it?" a question
    /// the filesystem answers for us: try, and skip what refuses.</summary>
    void GcOldBuilds()
    {
        var binRoot = Ver.BinRoot;
        var mine = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        if (!Directory.Exists(binRoot)) return;
        if (!mine.StartsWith(Path.GetFullPath(binRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;  // dev build: not ours to collect
        foreach (var dir in Directory.GetDirectories(binRoot))
        {
            if (Path.GetFullPath(dir).TrimEnd('\\').Equals(mine, StringComparison.OrdinalIgnoreCase)) continue;
            // AND NOT ONE A LIVE AGENT'S GATE STILL POINTS AT (WORK-ISOLATION-PLAN D-17).
            //
            // Hooks are read at SESSION START and never re-read (measured 2026-08-20 -- see the
            // note where redeployment used to be), so an agent launched by an older build holds
            // that build's exe path for its whole life. Collecting the directory therefore breaks
            // its gate, and a gate whose command cannot even start is a FAIL-OPEN that never
            // reaches our code to be logged -- which under layer 1 is a write into the operator's
            // live checkout. That is the 2026-08-18 incident, and rewriting the gate file was
            // never able to fix it.
            //
            // A text scan over small files, across EVERY workspace rather than just this one: any
            // daemon may be the one that swapped, and another workspace's agent is no less live
            // for it. Dead lanes' gate files are deleted in reconcile, so nothing pins a
            // directory once its agent is gone.
            if (GateFilesNaming(dir)) { _store.Event("binary_gc_kept", null, $"{dir}: a live lane's gate names it"); continue; }
            try { Directory.Delete(dir, recursive: true); _store.Event("binary_gc", null, dir); }
            catch (Exception ex) { _store.Event("binary_gc_skipped", null, $"{dir}: {ex.Message}"); }
        }
    }

    /// <summary>Does any workspace's per-lane gate file name this build directory? See
    /// <see cref="GcOldBuilds"/> for why that must veto collection. Text, not JSON: the question
    /// is whether the path appears at all, and a parse failure must not read as "no".</summary>
    static bool GateFilesNaming(string buildDir)
    {
        var needle = Path.GetFullPath(buildDir).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            if (!Directory.Exists(Paths.WorkspacesDir)) return false;
            foreach (var ws in Directory.GetDirectories(Paths.WorkspacesDir))
                foreach (var f in Directory.GetFiles(ws, "gate-lane*.json"))
                    try
                    {
                        // UNESCAPED FIRST. The file is JSON, so every backslash in the command is
                        // DOUBLED -- a raw Contains() against a Windows path with single
                        // backslashes can never match, and the retention silently retains
                        // nothing while looking installed. m4 carries the same trap for the
                        // same file from the reading side, and this code walked straight into
                        // it: caught by tightening the check to demand that a retention
                        // actually FIRED rather than that few directories survived.
                        var text = File.ReadAllText(f).Replace("\\\\", "\\");
                        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { return true; }   // unreadable: assume it names us rather than break a gate
        }
        catch { return true; }               // ditto -- a stale directory costs disk, the other way
                                             // costs enforcement
        return false;
    }

    /// <summary>System-level announcements land in the dispatcher pane, and therefore in
    /// the decision feed (§8). The dispatcher lane holds no agent — it is a place for
    /// the system to speak in its own voice.</summary>
    void Announce(string text)
    {
        var id = _store.KvGet("dispatcher_lane") is string s && long.TryParse(s, out var l) ? l : 0;
        if (id == 0)
        {
            id = _store.LaneCreate("DODONA");
            _store.LaneRole(id, "dispatcher");
            _store.LanePresence(id, "system");
            _store.KvSet("dispatcher_lane", id.ToString());
        }
        _store.PaneEvent(id, "announcement", text, null, null);
    }

    /// <summary>The same, but for something that happened to a TICKET: it lands in that
    /// ticket's own lane pane, where the agent doing the work and the operator watching it
    /// are both already looking, and falls back to the dispatcher voice when the ticket has
    /// no lane. Every refusal on the land path uses this, because "refused" written only to
    /// a daemon log is the failure mode CLAUDE.md §0.1 calls quietly stale — the caller sees
    /// one line and the reason lives somewhere nobody opens.</summary>
    void Announce(Store.TicketRow t, string text)
    {
        if (t.LaneId is long lid) _store.PaneEvent(lid, "announcement", text, null, null);
        else Announce($"[dodona] {text}");
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>The argv every claude lane is started with — one place, so model and
    /// effort are policy rather than four scattered literals. `--effort` is omitted when
    /// blank so a project can opt out of setting it at all.</summary>
    /// <summary>Where management-role agents live — see <see cref="Paths.NeutralCwd"/>.
    /// The definition moved there because the concierge (§2) runs its own management models
    /// and must get the identical treatment: utility roles get no project context at all,
    /// and their whole job description is their system prompt.</summary>
    static string NeutralCwd() => Paths.NeutralCwd();

    /// <summary>
    /// STATIC, AND IT TAKES THE CONFIG (T2, docs/LOCATIONS-PLAN.md P2.3). It read `_config`
    /// directly until Phase 2, i.e. the config loaded once from the workspace's FIRST project —
    /// so a lane opening in project B would have run with project A's `permissionMode` and
    /// `allowedTools`, and **a repo deliberately kept on a leash loses it** (CLAUDE.md §7: that
    /// leash is the only thing a project gets to ask for). `Config.For` has existed since
    /// multi-repo landed and had never once been used to configure a lane.
    ///
    /// Static is not tidiness either: with no `this` it is callable from `unit`, so "the
    /// permission mode in the argv is the one this config asked for" costs a millisecond to
    /// hold instead of eight seconds of daemon — and the fake agent takes no claude flags at
    /// all (<see cref="IsClaude"/> is false for it), so no acceptance suite can see this argv.
    /// </summary>
    internal static List<string> ClaudeArgs(Config cfg, string model, string effort, string systemPrompt, bool acceptEdits, bool utility = false)
    {
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                                      "--verbose", "--model", model };
        if (!string.IsNullOrWhiteSpace(effort)) { args.Add("--effort"); args.Add(effort); }
        // Belt to the neutral-cwd braces: even if a future claude finds project context
        // some other way, utility roles ask for user-level settings only.
        if (utility) { args.Add("--setting-sources"); args.Add("user"); }
        // A lane has no way to ASK. The operator's own session carries a permission-prompt
        // tool wired to a dialog, so an unapproved command becomes a question; a headless
        // `-p` lane has no such channel, so the same command is denied outright and the
        // agent is simply stuck — it edits fine and then cannot build what it edited.
        // Hence the default matches what the operator's IDE grants in auto mode.
        //
        // This does NOT loosen Dodona's own guarantees, and that is not an assumption:
        // measured, a PreToolUse hook still fires under bypassPermissions. The claim gate
        // IS a PreToolUse hook, so a ticket lane is still bounded to its claim, and the
        // merge-time diff backstop still refuses anything that slips. The safety model
        // never rested on Claude's permission prompt — it rests on the gate and the fence.
        if (acceptEdits) { args.Add("--permission-mode"); args.Add(cfg.PermissionMode); }
        if (acceptEdits && cfg.Allowed.Length > 0)
        {
            // Work lanes get the project's allowlist; the router never does — it has no
            // business running anything.
            args.Add("--allowedTools");
            args.Add(string.Join(",", cfg.Allowed));
        }
        args.Add("--append-system-prompt");
        args.Add(systemPrompt);
        return args;
    }

    /// <summary>An optional string a request carried, or null. Distinct from <see cref="Pick"/>
    /// because "the caller said nothing" and "the caller said the default" are different facts
    /// for a project: one means the first project, the other has to be validated.</summary>
    static string? One(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>What a request asked for, else what the project settled on.</summary>
    static string Pick(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : fallback;

    /// <summary>
    /// Spawn a plain agent lane IN A PROJECT — no ticket, no claim, no gate. The binary is
    /// `agent` from that project's dodona.json (default `claude`), which is also how the
    /// acceptance suite exercises the paths where the daemon starts an agent itself.
    ///
    /// **`project` is ONE parameter and it is used three times** (docs/LOCATIONS-PLAN.md P2.2,
    /// trap T1): it picks the config, it is written into the system prompt, and it is the
    /// process's working directory. It used to be `_primary` written out three times in two
    /// places, and the M5.1 incident is what a divergence between the second and third looks
    /// like — an agent told a folder it is not in, which compiles clean. There is no overload
    /// that defaults it: a caller must say where, because "wherever the workspace happens to
    /// start" is the answer this phase exists to delete.
    ///
    /// The caller has already validated the project through <see cref="TryProject"/>; nothing
    /// here re-derives it, because two places deciding where a lane goes is the shape of the
    /// bug rather than a safety net.
    /// </summary>
    async Task<(long Id, string Msg)> SpawnAgentLaneAsync(string title, string project, string? model = null, string? effort = null)
    {
        var cfg = ConfigForProject(project);
        var child = cfg.Agent;
        var args = IsClaude(child)
            ? ClaudeArgs(cfg, model ?? cfg.Model, effort ?? cfg.Effort, LaneSystemPrompt(title, project), acceptEdits: true)
            : new List<string>();                       // a stand-in agent takes no claude flags
        var r = await SpawnLaneAsync(title, "work", project, child, args);
        if (r.Id > 0) RecordLaneConfig(r.Id, project, cfg, args);
        return r;
    }

    /// <summary>Which dodona.json configures a lane in this project — the project's own, falling
    /// back to the workspace's first project (<see cref="Config.For"/>). For a ONE-project
    /// workspace `project` IS the first project, so this returns exactly `_config` and the case
    /// is byte-for-byte unchanged, which is the property the whole workspace migration rested
    /// on.
    ///
    /// The sharp edge, stated because it will surprise someone: `Config.For` picks a WHOLE FILE,
    /// it does not merge two. A project with a `dodona.json` that sets only `permissionMode`
    /// therefore gets the built-in default for `agent`, `model` and everything else — not the
    /// workspace's. That is the same rule per-repo config has always had.</summary>
    Config ConfigForProject(string project) => Config.For(_primary, project);

    // ---------------------------------------------------------------- asking (P4.1/P4.5)

    /// <summary>
    /// `git init` plus the first commit, on ONE named project. Extracted from the `repo-init`
    /// case so that answering the repo question runs **the same code** the command runs
    /// (LOCATIONS-PLAN P4.3, D-L4: one answer path). A second copy behind the overlay is
    /// exactly the two-systems-one-tested divergence Phase 4 exists to prevent — and it would
    /// be the copy that ran `git init`, which is the one act here that nothing can undo.
    /// </summary>
    void RepoInitOp(string project, bool adopt, StreamWriter w)
    {
        var cfg = ConfigForProject(project);
        if (Git.IsRepo(project) && Git.HasCommit(project))
        { w.WriteLine($"error: {project} is already a git repository with commits"); return; }

        if (!Git.IsRepo(project))
        {
            var (ic, io) = Git.Run(project, "init", "-b", cfg.Main);
            if (ic != 0) { w.WriteLine($"error: git init failed: {io}"); w.WriteLine("##exit 1"); return; }
            w.WriteLine($"initialized empty repository on '{cfg.Main}'");
        }

        // Dodona's own state is never repo content: worktrees, the store and the
        // deployed gate files all live under .dodona/ and would otherwise be
        // committed by an agent's `git add -A` (the bug M1's test caught).
        var ignore = Path.Combine(project, ".gitignore");
        var ignoreText = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
        if (!ignoreText.Split('\n').Any(l => l.Trim() == ".dodona/"))
        {
            File.AppendAllText(ignore, (ignoreText.Length > 0 && !ignoreText.EndsWith("\n") ? "\n" : "") + ".dodona/\n");
            w.WriteLine("added .dodona/ to .gitignore");
        }

        if (!Git.HasCommit(project))
        {
            // An empty repo has no branch, so no worktree can be cut from it. What
            // goes into the first commit is the user's call, not ours: adopt takes
            // the files that are already here, otherwise the commit is empty and
            // they stay untracked.
            if (adopt) Git.Run(project, "add", "-A");
            var args = new List<string> { "commit", "-m", adopt ? "Initial commit" : "Initial commit (empty)" };
            if (!adopt) args.Insert(1, "--allow-empty");
            var (cc, co) = Git.Run(project, args.ToArray());
            if (cc != 0) { w.WriteLine($"error: initial commit failed: {co}"); w.WriteLine("##exit 1"); return; }
            w.WriteLine(adopt ? "committed the existing files as the initial commit" : "made an empty initial commit; existing files left untracked");
        }
        _store.Event("repo_init", null, $"{project} main={cfg.Main} adopt={adopt}");
        Announce($"[dodona] git repository ready on '{cfg.Main}' — tickets can branch now");
        w.WriteLine($"ready: {project} is a git repository on '{cfg.Main}'");
    }

    /// <summary>
    /// Answer one of this workspace's open questions. **This is THE answer path** — the
    /// `answer` command, the ask overlay's buttons and `dodona ui answer` all arrive here,
    /// which is what makes D-L4's "only pixels diverge" a fact about the code rather than a
    /// hope (P4.3). It mirrors <c>Concierge.Answer</c> deliberately, line for line where it
    /// can: same guard against answering twice, same "the row is the record" shape.
    ///
    /// An answer the question does not offer is REFUSED, not guessed. Asking exists because
    /// guessing was wrong; a fuzzy answer would reintroduce the guess at the one moment the
    /// operator had actually told us the truth.
    /// </summary>
    async Task<List<string>> AnswerQuestion(long id, string answer)
    {
        var lines = new List<string>();
        var q = _store.Question(id);
        if (q is null) { lines.Add($"error: no question {id}"); return lines; }
        if (q.State != "open") { lines.Add($"error: question {id} is already {q.State}"); return lines; }

        var choices = Ask.Choices(q.Candidates);
        var picked = Ask.Match(choices, answer);
        if (picked is null)
        {
            lines.Add($"error: \"{answer}\" is not one of the answers to question {id}" +
                      (choices.Count > 0 ? $" ({string.Join(" / ", choices.Select(c => c.Value))})" : ""));
            return lines;
        }

        // A ROUTE ANSWER IS RESOLVED BEFORE THE ROW IS CLOSED, and that ordering is the whole
        // guard. `QuestionAnswer` is guarded on `state='open'`, so there is no re-opening a
        // question -- and a route question closed without delivering loses the held sentence,
        // which is the one thing this rung exists to protect. Every other kind is safe to close
        // first because a failed action leaves nothing unrecoverable behind.
        string? answeredProject = null;
        if (q.Kind == Ask.KindRoute)
        {
            answeredProject = ProjectLadder.ByName(ProjectPaths(), picked.Value);
            if (answeredProject is null)
            {
                // Only reachable if the project was detached between the ask and the answer --
                // trap T4 arriving on the answer path. Say what un-sticks it and leave the
                // question open, so the sentence is still deliverable to a project that is here.
                lines.Add($"error: \"{picked.Label}\" is no longer a project of workspace {_wsName} " +
                          $"(projects here: {string.Join(", ", ProjectPaths().Select(ProjectLadder.Leaf))}) — " +
                          $"question {id} is still open; answer it with one of those");
                _store.Event("question_answer_refused", null, $"question {id} kind={q.Kind} answer={picked.Value}: project gone");
                return lines;
            }
        }

        // `withdrawn`, not `answered`, for a declined question: the two are different facts and
        // a later "why is there no repo" wants to know which one happened.
        //
        // A ROUTE QUESTION HAS NO DECLINATION, and excluding it is not tidiness: its choices are
        // project names, so a project in a folder called `no` would otherwise have a perfectly
        // good answer recorded as `withdrawn` and its sentence silently never delivered.
        var declined = q.Kind != Ask.KindRoute && picked.Value.Equals("no", StringComparison.OrdinalIgnoreCase);
        _store.QuestionAnswer(id, picked.Value, declined ? "withdrawn" : "answered");
        _store.Event("question_answered", null, $"question {id} kind={q.Kind} -> {picked.Value}");
        lines.Add($"answered: question {id} -> {picked.Label}");

        switch (q.Kind)
        {
            case Ask.KindRepoInit when !declined:
                // `adopt: true` because the files are already there — that is the whole shape of
                // this question. A GUI that made a git repo and then left the operator's own
                // files untracked would have answered a question they did not ask.
                foreach (var line in RepoInitLines(q.Subject)) lines.Add(line);
                break;
            case Ask.KindRepoInit:
                Announce("[dodona] no repo made — lanes keep working without git; only tickets need one");
                lines.Add("nothing was created; ask again by creating a ticket");
                break;
            // ROUTING'S RUNG 4 (LOCATIONS-PLAN P3.A, part 2). The sentence has been sitting in
            // `subject` undelivered since the ladder held it; the operator has now said where, so
            // deliver it — through `SpawnForAsync`, the ONE spawn path, with the answered project
            // forced past a ladder that has already admitted it does not know.
            //
            // THE LANE IS CREATED HERE AND NOWHERE EARLIER. `brain:held_input_invents_no_lane` and
            // `workspace:a_held_sentence_invents_no_lane` are the two checks that hold the other
            // half of it: holding invents nothing, and answering is what makes a lane exist.
            case Ask.KindRoute:
            {
                var (laneId, msg, choice) = await SpawnForAsync(q.Subject, null, null, answeredProject);
                if (laneId < 0) { lines.Add(msg); break; }
                // The routing row the hold could not write: it recorded tier `ask` with no lane,
                // and this is the same sentence finally reaching one. Two rows for one sentence is
                // the honest record — it WAS asked about, and it WAS then delivered.
                _store.RoutingInsert(q.Subject, "answered", laneId, laneId, "operator");
                lines.Add($"delivered to {msg} in {ProjectLadder.Leaf(answeredProject!)} " +
                          $"on {choice.Describe} — undo: dodona lane-stop {laneId}");
                break;
            }
            // THE OPERATOR'S YES ON A MERGE (R6, D-R11), and it is the one legitimate approval
            // path there is. `case "approve"` and this arm are the ONLY two callers of
            // `ApproveTicket`, and both of them are a person: D-R10 gives the manager the block
            // and keeps the bless, so nothing that is not a person may answer a question of kind
            // `land` — no timeout answers it, no default answers it, and `ManagerReview` has no
            // path to here (`brain:a_manager_approval_grants_nothing` is what goes red if one
            // ever appears).
            case Ask.KindLand when !declined:
            {
                if (!long.TryParse(q.Subject, out var ltid) || _store.Ticket(ltid) is not { } lt)
                {
                    // Only reachable if the ticket was deleted between the ask and the answer.
                    // Refusing beats approving something we cannot name.
                    lines.Add($"error: question {id} is about ticket '{q.Subject}', which this workspace does not have");
                    _store.Event("question_answer_refused", null, $"question {id} kind={q.Kind}: no such ticket");
                    break;
                }
                if (lt.State != "open")
                {
                    lines.Add($"ticket {ltid} is {lt.State} — there is nothing left to approve");
                    break;
                }
                ApproveTicket(ltid, $"answered question {id}");
                lines.Add($"approved ticket {ltid} — the merge can proceed (dodona token-request {ltid}, then dodona land {ltid})");
                break;
            }
            // "not yet" (R6). The row goes `withdrawn` above and the TICKET IS UNTOUCHED: the
            // agent keeps working, and the next completed turn that moves the worktree opens a
            // fresh question. That is what makes declining safe to offer — neither answer can
            // lose the ticket, and only one of them advances a ref.
            case Ask.KindLand:
                lines.Add($"not approved — ticket {q.Subject} stays open, and you are asked again when its work changes");
                break;
            // A kind with no case here answers the ROW and does nothing else, which is the right
            // default for a question that was only ever "tell me which one" — the caller reads the
            // answer off the row.
        }
        return lines;
    }

    /// <summary>
    /// Open (or re-find) the "this project has no git repo; create one?" question — P4.5.
    ///
    /// **Idempotent on purpose.** A ticket-create that is refused twice must not leave two
    /// identical open questions: the overlay renders one at a time, so the second would appear
    /// the instant the first was answered and read as the system not having listened. Existing
    /// open question of the same kind and subject wins, and its id is reported again.
    /// </summary>
    List<string> AskForRepo(string project, string forWhat)
    {
        var lines = new List<string>();
        var existing = _store.OpenQuestions()
            .FirstOrDefault(q => q.Kind == Ask.KindRepoInit &&
                                 q.Subject.Equals(project, StringComparison.OrdinalIgnoreCase));
        var leaf = Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (leaf.Length == 0) leaf = project;
        var text = $"{leaf} has no git repo, so \"{forWhat}\" cannot become a ticket. Create one?";
        var id = existing?.Id ?? _store.QuestionOpen(text, Ask.RepoInitCandidates(leaf), Ask.KindRepoInit, project);
        if (existing is null)
        {
            _store.Event("question_opened", null, $"question {id} kind={Ask.KindRepoInit} subject={project}");
            // The announcement is what puts it in the feed, which is where a person who closed
            // the window finds it again. It carries the command as well as the words, for the
            // same reason every other announcement does: the overlay is the fast path, not the
            // only one.
            Announce($"[dodona] {text} answer: dodona answer {id} yes|no");
        }
        lines.Add(text);
        lines.Add($"answer it in the window, or: dodona answer {id} yes   (or: dodona answer {id} no)");
        return lines;
    }

    /// <summary>
    /// Open (or re-find) the "which project is this sentence for?" question — LOCATIONS-PLAN
    /// P3.A, part 1. Returns its id, which every announcement and reply carries so the sentence
    /// can be released from anywhere.
    ///
    /// **The candidates are NAMES.** No paths reach the question row: §3.1 has no folder UI, and
    /// a routing question names projects rather than offering somewhere to browse. The answer
    /// comes back as a name and <see cref="ProjectLadder.ByName"/> resolves it against the
    /// projects this workspace still has.
    ///
    /// **`subject` is the held sentence, whole and untruncated**, because answering DELIVERS it.
    /// That is the one column that must survive verbatim; the `input` column is the question a
    /// person reads, so it is the one that gets shortened.
    ///
    /// **Idempotent on (kind, subject)**, for the reason <see cref="AskForRepo"/> is: the overlay
    /// renders one question at a time, so a second identical row would appear the instant the
    /// first was answered and read as the system not having listened. Two DIFFERENT held
    /// sentences are two genuine questions and do queue — oldest first, which is the order the
    /// uncertainty was created in.
    /// </summary>
    long AskWhichProject(string text, IReadOnlyList<string> candidates)
    {
        var existing = _store.OpenQuestions()
            .FirstOrDefault(q => q.Kind == Ask.KindRoute &&
                                 q.Subject.Equals(text, StringComparison.Ordinal));
        if (existing is not null) return existing.Id;
        var names = candidates.Select(ProjectLadder.Leaf).ToList();
        var id = _store.QuestionOpen($"Which project is “{Truncate(text, 60)}” for?",
                                     Ask.RouteCandidates(names), Ask.KindRoute, text);
        _store.Event("question_opened", null,
            $"question {id} kind={Ask.KindRoute} candidates={(names.Count == 0 ? "none" : string.Join(",", names))} " +
            $"subject={Truncate(text, 80)}");
        return id;
    }

    // ------------------------------------- the approval ask (R6, D-R11) --------------------

    /// <summary>
    /// THE OPERATOR'S YES, in one implementation. `dodona approve` and the approval ask both
    /// land here, so there is no second copy of "what approving does" to drift — and the ask
    /// answers through `MainWindow.AnswerAsk`, which is the same method a button click lands in
    /// (D-L4's one answer path), so a click and a verb and a typed command are three surfaces
    /// over one act.
    ///
    /// **THE CALLER LIST IS THE LOAD-BEARING PART, not the method** (D-R10). Approval advances a
    /// ref that has no undo, so both callers are a PERSON: the operator typing, and the operator
    /// answering. There is no timeout that answers a `land` question, no default, no
    /// auto-approve when the manager says `ok`, and no path from <see cref="ManagerReview"/> to
    /// here at all — a model as the sole gate on the irreversible step is *a prompt providing
    /// safety*, which `WORK-ISOLATION-PLAN` §2 forbids however the model is dressed. If a third
    /// caller ever appears, the question to ask of it is "is this a person?";
    /// `brain:a_manager_approval_grants_nothing` is what goes red when the answer is no.
    /// </summary>
    void ApproveTicket(long tid, string how)
    {
        _store.TicketApprove(tid);
        _store.Event("ticket_approved", null, $"ticket {tid}: {how}");
        // Unblock the lane: presence back to idle, receipt in the pane.
        if (_store.Ticket(tid)?.LaneId is long alid)
        {
            _store.LanePresence(alid, "idle");
            _store.PaneEvent(alid, "announcement", $"ticket {tid} approved — merge unblocked", null, null);
        }
        // An ask that is still standing has been SUPERSEDED rather than answered — the operator
        // said yes through the other surface. Answering through the ask itself has already
        // closed the row (`QuestionAnswer` is guarded on `state='open'`), so this is a no-op on
        // that path and a tidy-up on the other.
        _store.WithdrawQuestions(Ask.KindLand, tid.ToString(), $"approved: {how}");
    }

    /// <summary>
    /// Raise — or refresh — the one question that asks the operator to approve a ticket's merge
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` R6, D-R11; `WORK-ISOLATION-PLAN` D-7 and P5, which this
    /// absorbs). The write-up the manager wrote for a person is finally put in front of that
    /// person: approving becomes a two-second decision instead of a diff-reading session, which
    /// is the payoff for R4's record and R5's reviewer both.
    ///
    /// **IT MUST NOT DEPEND ON A REVIEW EXISTING, and that is the single most important
    /// property here.** Four ordinary things leave a ticket with no `manager_review` row:
    /// `DODONA_NO_AUTOSTART` (D-R17), `"brain": false` for the project, a cheap tier that timed
    /// out, and the send-back bound being spent (D-R18). If the ask only appeared when a note
    /// existed, approving a merge would be gated on a model having answered — judgement
    /// switched off would mean nothing could ever be merged, which is the fail-closed mirror of
    /// the trap D-R10 is about. So it is raised by THE RECORD EXISTING, from
    /// <see cref="BuildRecord"/>, and every no-review case renders as words that say so, over
    /// facts CODE knows: what changed, the verify state, the drop check, uncommitted work.
    ///
    /// **ONE ROW PER TICKET, REFRESHED IN PLACE** (`Store.QuestionUpsert`). The record arrives
    /// first and the review seconds later, so the text has to be able to change under a question
    /// already on screen; a second row would be a queue of overlays for one decision.
    ///
    /// **IT IS RAISED WHATEVER THE VERDICT, including a send-back.** The manager's objection is
    /// RENDERED, not enforced: blocking the agent is its job, and hiding the operator's own
    /// question behind its opinion would quietly promote it to the gatekeeper D-R10 says it may
    /// never be. The operator sees "sent this back, round 2 of 3: <why>" and decides.
    ///
    /// **NOTHING TO ASK IS A STATE, NOT A FAILURE.** An `auto` ticket needs no approval, an
    /// approved one has its answer, and a ticket that is no longer open has nothing to merge —
    /// each returns silently, because a question nobody can act on is worse than no question.
    /// </summary>
    long AskToLand(Store.TicketRow t, long laneId, string? recordJson)
    {
        var tid = t.Id;
        if (t.MergeMode != "on-approval") return 0;
        // RE-READ rather than trusting the row this was called with: the record is assembled off
        // the pipe and the review takes up to 25 s, and the operator can approve — or the land
        // can happen — in between. Asking to approve something already approved is the "outdated"
        // half of CLAUDE.md §0.1.
        var fresh = _store.Ticket(tid);
        if (fresh is null || fresh.State != "open" || fresh.Approved) return 0;

        var (id, opened) = _store.QuestionUpsert(Ask.KindLand, tid.ToString(),
                                                 LandAskText(fresh, recordJson), Ask.LandCandidates(tid));
        if (!opened) return id;
        _store.Event("question_opened", laneId, $"question {id} kind={Ask.KindLand} subject={tid}");
        // ONCE, on opening, never on every refresh. The overlay is the fast path and the feed is
        // where somebody who closed the window finds it again — but a line per manager round
        // would be the never-stuck fix turning into never-quiet (D-R18's reasoning, one surface
        // over).
        //
        // AND IT ARRIVES ACKED, which is the deliberate half. A badge is a DEMAND for attention
        // (§8), and the demand here is the overlay itself — the feed line is the durable copy for
        // somebody who closed the window. The moment that genuinely earns a badge is the agent
        // BLOCKING on the token, which `token-request` still raises exactly as it did; two
        // unacked lines for one decision would be this phase making the feed noisier while
        // claiming to make deciding easier. `m3`'s badge checks are what would have gone red, and
        // they are right: they measure attention, not events.
        var text = $"ready to merge: ticket {tid} '{t.Title}' — answer it in the window, or: dodona answer {id} yes|no";
        if (t.LaneId is long qlid) _store.PaneEvent(qlid, "announcement", text, null, null, acked: true);
        else Announce($"[dodona] {text}");
        return id;
    }

    /// <summary>
    /// What the approval ask SAYS. The manager's `note` when there is one, and what code knows
    /// when there is not — never an empty box, and never a blank where a reason belongs
    /// (`land_drop_check_moot` and R4's `verify: not-run` are the pattern being copied).
    ///
    /// **WHICH EVENT IS NEWEST IS HOW IT KNOWS WHETHER THE REVIEW IS THIS TURN'S.** One query
    /// over the record kind AND every review-outcome kind, newest by id: if the record is on top,
    /// no review has come back for it yet, and showing the PREVIOUS turn's note against new work
    /// would be a write-up about a diff that no longer exists. That is a fact about ordering
    /// rather than a guess, which is what `Store.LastTicketEvent` is for.
    ///
    /// Bounded, because this is a paragraph in an overlay and not a report: three file names,
    /// the manager's note as written (R5 already caps it at 240), and the whole thing truncated.
    /// </summary>
    string LandAskText(Store.TicketRow t, string? recordJson)
    {
        var files = 0; var uncommitted = 0;
        var verify = "not-run"; var drop = "moot";
        var names = new List<string>();
        var haveRecord = false;
        // THE SECOND CALLER HANDS NOTHING (`token-request`'s unapproved refusal), so the record
        // is looked up. There may not be one: `completion_record_impossible` is a real state — a
        // ticket can outlive its worktree — and a ticket can also ask for the token before any
        // turn of its has ended. Either way the question is legitimate and must be asked; what it
        // must NOT do is print "0 files" and pass an absence off as a measurement.
        recordJson ??= _store.LastTicketEvent(t.Id, "completion_record") is { Detail: string rd } &&
                       rd.IndexOf('{') is int b && b >= 0 ? rd[b..] : null;
        try
        {
            if (recordJson is null) throw new JsonException("no completion record");
            using var d = JsonDocument.Parse(recordJson);
            var r = d.RootElement;
            if (r.TryGetProperty("files", out var f) && f.TryGetInt32(out var fi)) files = fi;
            if (r.TryGetProperty("uncommitted", out var u) && u.TryGetInt32(out var ui)) uncommitted = ui;
            if (r.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array)
                names = ch.EnumerateArray().Take(3).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            if (r.TryGetProperty("verify", out var v) && v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty("state", out var vs)) verify = vs.GetString() ?? "not-run";
            if (r.TryGetProperty("drop", out var dp) && dp.ValueKind == JsonValueKind.Object &&
                dp.TryGetProperty("state", out var ds)) drop = ds.GetString() ?? "moot";
            haveRecord = true;
        }
        // A record we cannot parse still gets an ask: the facts line just says less. Refusing to
        // ask because a field was unreadable would make the merge unapprovable from the window.
        catch (JsonException) { }

        var facts = new List<string>
        {
            haveRecord ? files == 1 ? "1 file" : $"{files} files"
                       : "no completion record for it yet — the agent has asked for the merge token",
        };
        if (haveRecord && names.Count > 0) facts[0] += $" ({string.Join(", ", names)}{(files > names.Count ? ", …" : "")})";
        if (haveRecord) facts.Add($"verify {verify}");
        // `moot` and `clean` are the ordinary values and reading them out would be noise; a real
        // DROP is the one thing on this line a person must not skim past (D-R4).
        if (drop == "dropped") facts.Add("IT RESOLVED BY DISCARDING SOMETHING MAIN CHANGED");
        if (uncommitted > 0) facts.Add($"{uncommitted} uncommitted");

        // THE BOUND IS A COUNT, NOT AN EVENT ORDERING (D-R12/D-R18), and asking it FIRST is a
        // correction rather than a tidy-up: it was written as one more arm of the switch below
        // and `brain` caught it. Past the bound no further review will ever run, so any later
        // turn's record lands on top of `manager_bound_reached` and the ask reverted to "not
        // reviewed yet" — permanently, for a ticket that is precisely the one the operator has
        // been handed. Counted in the store for the same reason the bound itself is: a daemon
        // restarts on every publish.
        var rounds = _store.CountTicketEvents(t.Id, "manager_sent_back");
        var last = _store.LastTicketEvent(t.Id, "completion_record", "manager_review",
                                          "manager_review_skipped", "manager_review_failed");
        var review = rounds >= SendBackBound
            ? $"the manager sent this back {rounds} times, which is the bound — it is yours to judge now"
            : last?.Kind switch
            {
                "manager_review" => ReviewLine(last.Value.Detail),
                "manager_review_skipped" => $"no review ran ({Tail(last.Value.Detail)})",
                "manager_review_failed" => $"the review did not finish ({Tail(last.Value.Detail)})",
                // The record is on top, so no review has come back for THIS change yet. Showing
                // the previous turn's note here would be a write-up about a diff that is gone.
                _ => _store.CountTicketEvents(t.Id, "manager_review") > 0
                        ? "the manager has not reviewed this latest change yet"
                        : "no review has run",
            };

        return Truncate($"ticket {t.Id} \"{t.Title}\" is ready to merge — {string.Join(", ", facts)}.\n" +
                        $"{review}\nApprove the merge?", 700);

        // The manager's own words. A verdict with no note is reported AS a verdict with no note:
        // D-R11 says the write-up is the point, so its absence is worth a person seeing.
        static string ReviewLine(string detail)
        {
            var brace = detail.IndexOf('{');
            if (brace < 0) return "a review ran; its row could not be read";
            try
            {
                using var d = JsonDocument.Parse(detail[brace..]);
                var r = d.RootElement;
                string S(string n) => r.TryGetProperty(n, out var x) ? x.ToString() : "";
                var sentBack = S("verdict") == "send-back";
                var where = sentBack ? $"sent this back, round {S("round")} of {S("bound")}" : "raised no objection";
                var note = S("note");
                return note.Length > 0 ? $"the manager {where}: {note}"
                                       : $"the manager {where} and left no note";
            }
            catch (JsonException) { return "a review ran; its row could not be read"; }
        }

        // `ticket 7: <why>` -> `<why>`. The ticket number is already the first word of the ask.
        static string Tail(string detail)
        {
            var colon = detail.IndexOf(':');
            return Truncate(colon >= 0 ? detail[(colon + 1)..].Trim() : detail.Trim(), 200);
        }
    }

    /// <summary>`RepoInitOp` writes to a pipe; an answer needs its words as a list. One buffer
    /// rather than two implementations — the alternative was a second repo-init, which is the
    /// thing P4.3 forbids.</summary>
    List<string> RepoInitLines(string project)
    {
        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms) { AutoFlush = true };
        RepoInitOp(project, adopt: true, sw);
        ms.Position = 0;
        using var sr = new StreamReader(ms);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (!line.StartsWith("##")) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// WHICH PROJECT CONFIGURED THIS LANE, as a row (T2's only observable surface). A lane's
    /// permission mode was previously unanswerable from outside the process: it lands in a
    /// claude argv nobody reads back, and `IsClaude` is false for the acceptance suites' fake
    /// agent, so no suite could ever see the flag at all.
    ///
    /// It reports the argv when there IS one and the config otherwise, and says which — so a
    /// fake-agent lane still proves *which project's config was resolved* without the event
    /// pretending to be evidence of an argv that was never built.
    /// </summary>
    /// <summary>The OUTCOME of trying to materialise a ticket: the row, its branch and its
    /// worktree, or why not.
    ///
    /// **`Conflicts` is no longer a refusal (D-R5, R3) — it is an OVERLAP REPORT.** It used to
    /// be separate from `Error` because a claim conflict was the one refusal a caller might act
    /// on rather than print: promotion degraded to a refused write naming the holder, while
    /// `ticket-create` printed the conflicts and stopped. Both of those are gone. The list is
    /// kept, and is still the reason it is a separate field, because a ticket that overlaps
    /// another open one is worth SAYING — it is the derived signal D-R7 wants in front of the
    /// manager — but `Ok` no longer consults it, and no caller may refuse on it.</summary>
    sealed record TicketMade(long Id, string Branch, string Worktree,
                             string? Error, bool Exit1, List<string> Conflicts)
    {
        public bool Ok => Id > 0 && Error is null;
        public static TicketMade Failed(string error, bool exit1 = true) =>
            new(-1, "", "", error, exit1, new List<string>());
    }

    /// <summary>
    /// Create a ticket and materialise its branch and worktree. FACTORED OUT OF THE
    /// `ticket-create` HANDLER, where it existed only inline, so that layer 2 can call it: a
    /// refused write in the shared checkout promotes itself into a ticket
    /// (docs/WORK-ISOLATION-PLAN.md section 3, P2), and two implementations of "make a ticket"
    /// would drift on exactly the checks that matter -- repo exclusivity and claim conflict.
    ///
    /// Every refusal in here is a refusal for BOTH callers, which is the point. The messages keep
    /// their CLI shape (leading spaces, several lines) because `ticket-create` prints them
    /// verbatim; `Exit1` carries the one case that deliberately did not set `##exit 1`.
    /// </summary>
    TicketMade MakeTicket(RepoRef repo, string title, string mode,
                          List<(string, string)> claims, IEnumerable<string> specs)
    {
        // Attach-time enforcement and the partial unique index both cover the
        // ordinary case; neither can cover a BARE FOLDER legitimately attached to
        // two workspaces (exempt, harmless) that someone later ran `git init` in.
        // Only a check at the point of use notices the ground moved -- the same
        // reasoning that puts a diff backstop behind the claim gate (section 6).
        try
        {
            using var reg = new Registry();
            if (reg.RepoConflict(repo.Path, _instanceId) is Workspace other)
            {
                _store.Event("ticket_repo_not_exclusive", null, $"'{title}': {repo.Path} also in {other.Id}");
                return TicketMade.Failed(
                    $"error: {repo.Path} also belongs to workspace \"{other.Name}\" ({other.Id})\n" +
                    "       a repo belongs to at most ONE workspace at a time -- two workspaces over one\n" +
                    "       repo is two merge tokens over one main, the race this system exists to prevent\n" +
                    $"       move it:  dodona workspace-move --member \"{repo.Path}\" --workspace \"{_wsName}\"");
            }
        }
        catch (Exception ex) { _store.Event("registry_unreadable", null, ex.Message); }

        var repoCfg = Config.For(_primary, repo.Path);
        if (!Git.HasCommit(repo.Path))
            return TicketMade.Failed(
                $"error: {repo.Name} is a git repository with no commits, so there is no '{repoCfg.Main}' to branch from\n" +
                "       run `dodona repo-init` to make the first commit");

        // Both: the display name the claims were written relative to, and the canonical
        // path that says WHICH repository this is whatever it gets called later (P0.1).
        var (id, conflicts) = _store.TicketCreate(null, title, mode, repo.Name, Repos.Key(repo.Path), claims);
        // AN OVERLAP IS NOW RECORDED AND CARRIED ON WITH (D-R5). The event stays -- it is the
        // trail a reviewer reads, and two tickets over one file is a thing worth knowing even
        // though it is not a thing worth blocking. `TicketCreate` no longer returns -1 for an
        // overlap at all, so there is nothing to branch on here.
        if (conflicts.Count > 0)
            _store.Event("claim_overlap", null, $"'{title}' (ticket {id}) overlaps: {string.Join(" | ", conflicts)}");

        // Branch names are workspace-unique because ticket ids are. The worktree
        // lives beside the MEMBER holding this repository -- worktrees are the one
        // piece of state that deliberately did NOT move into workspace territory
        // (WORKSPACES-CONCIERGE.md section 1: they are volume- and path-sensitive, and
        // moving them buys nothing). For a one-member workspace this is the exact
        // path it has always been.
        var branch = $"ticket/{id}";
        var wt = Path.Combine(Paths.Worktrees(repo.MemberPath), $"t{id}");
        var (code, output) = Git.Run(repo.Path, "worktree", "add", "-b", branch, wt, repoCfg.Main);
        if (code != 0)
        {
            _store.TicketState(id, "abandoned");
            _store.Event("ticket_git_failed", null, $"ticket {id} repo {repo.Name}: {output}");
            // NO `##exit 1` here, preserved from the inline original: this path printed the error
            // and broke without one, and changing an exit code while moving code is how a
            // refactor becomes a behaviour change nobody asked for.
            return TicketMade.Failed($"error: worktree add failed in {repo.Name}: {output}", exit1: false);
        }
        _store.TicketSetGit(id, branch, wt);
        // NO GATE DEPLOYED HERE (D-17). A ticket has no lane yet -- `ticket-agent` creates it, or
        // promotion attaches the one it came from -- and the gate is per-LANE and handed over on
        // the launch line, so `AttachShimAsync` is the one place that writes it. Same funnel
        // correction `DaemonClient.Send` needed for start-on-demand: a call site cannot forget
        // what it does not do.
        _store.Event("ticket_created", null,
            $"ticket {id} '{title}' repo {repo.Name} branch {branch} claims [{string.Join(", ", specs)}]");
        // THE OVERLAPS TRAVEL WITH THE SUCCESS NOW. This returned `new List<string>()` while the
        // overlap branch above returned the real list -- correct while an overlap was a refusal
        // (the two were mutually exclusive), and silently wrong the moment an overlap became
        // something a SUCCESSFUL create has to report. Caught by the re-aimed
        // `the_overlap_is_reported_and_names_the_holder`, which is the whole argument for
        // re-aiming a check rather than deleting it.
        return new TicketMade(id, branch, wt, null, false, conflicts);
    }

    /// <summary>
    /// LAYER 2: THE REFUSAL IS A PROMOTION, NOT A WALL (WORK-ISOLATION-PLAN section 3, P2).
    ///
    /// A plain lane has just tried to write into the shared checkout and layer 1 refused it. So
    /// give it the thing it actually needed: a ticket, a worktree, a gate, and the same session
    /// carried into it. **Nothing has been written yet** -- that is the whole reason layer 1 sits
    /// at the write attempt and not at the commit, because moving edits afterwards is not
    /// available: they would be in the wrong tree and `git stash` is repo-global, so two lanes
    /// stashing interleave one stack (CLAUDE.md 5.2).
    ///
    /// This is NOT a relaxation of `lane-respawn`'s refusal to re-home a ticket lane, which is
    /// correct and untouched. That refusal is about moving a lane OUT of its worktree; this moves
    /// a plain lane IN, which it never covered.
    ///
    /// Returns the message the gate hands back to the agent: a REWRITE naming where the same file
    /// now lives, never a bare "no".
    /// </summary>
    (string Message, Action? Move) PromoteLane(Store.LaneRow row, RepoRef repo, string relForClaim, string deniedPath)
    {
        // NO SEED CLAIM ANY MORE (D-R5, R3), and this is the edge that decision was largely
        // about. Promotion used to open the ticket holding `path:<the file that was refused>` --
        // the only path it could possibly know, because a promotion happens on the FIRST write
        // and the agent has not said what else it intends to touch. The claim gate then bounded
        // the agent to that one file, so the SECOND file it needed was refused by the very
        // ticket that had just been created to unblock it. A promoted agent could write exactly
        // one file. Nothing declared here is nothing to be wrongly bounded by, and what the
        // branch actually touches is read from `git diff` when it matters (D-R7).
        var made = MakeTicket(repo, row.Title, "on-approval",
                              new List<(string, string)>(),
                              Array.Empty<string>());
        if (!made.Ok)
        {
            // THE PROMOTION CAN FAIL AT THE MOMENT IT IS NEEDED, and it must degrade to a refused
            // write naming the reason -- never to a silent allow (section 9). What can still fail
            // here is real: repo exclusivity, a repository with no commits, `git worktree add`.
            // A claim conflict is no longer among them and cannot be: overlap does not refuse.
            var why = (made.Error ?? "unknown").Replace("\n", " ").Trim();
            _store.Event("promotion_refused", row.Id, $"{deniedPath}: {why}");
            return ($"denied: {deniedPath} is in the SHARED CHECKOUT, not a worktree, and a private " +
                    $"checkout could not be opened for it: {why}. Nothing was written.", null);
        }

        _store.TicketSetLane(made.Id, row.Id);
        var moved = Path.Combine(made.Worktree, relForClaim.Replace('/', Path.DirectorySeparatorChar));

        // THE MOVE ITSELF IS BEHIND THE ANSWER, not in front of it. `git worktree add` is already
        // done (it has to be -- the message names the new path), but the RESPAWN kills this
        // agent's process, and it is the process currently waiting on this reply. So the reply is
        // written first by the caller and the respawn runs after it, fire-and-forget, the same
        // shape `BrainReview` uses for work that must not be in front of the operator (D-14).
        Action move = () => _ = Task.Run(async () =>
        {
            try
            {
                var cfg = ConfigForProject(Projects.Of(ProjectPaths(), made.Worktree) ?? repo.Path);
                // THE SAME BINARY IT WAS ALREADY RUNNING, not whatever the config names now.
                //
                // This read `cfg.Agent`, and the bug it caused is worth the whole comment: a lane
                // started with `--child <stand-in>` came back from promotion as `claude`. In m2 that
                // meant a REAL model process spawned inside a model-free suite -- and it then held
                // the new worktree as its working directory, so D-9's undo could not prune it and
                // git reported "Permission denied". One wrong line produced a quota leak and a
                // broken undo, and the undo failure is what surfaced it.
                //
                // Recovered from the lane's own `shim_spawned` record rather than from a column: a
                // schema bump for one field costs a migration and every older-store fixture has to
                // drop it (CLAUDE.md 0.2 carries that trap), which is more risk than this is worth.
                // The fallback is the config, which is the right answer for every lane that never
                // overrode its binary.
                var child = ChildOfLane(row.Id) ?? cfg.Agent;
                var args = new List<string>();
                if (IsClaude(child))
                {
                    var sys = TicketSystemPrompt(made.Id, row.Title, $"path:{relForClaim}");
                    args = ClaudeArgs(cfg, cfg.Model, cfg.Effort, sys, acceptEdits: true);
                    // THE SESSION IS THE POINT. Promotion is only free because `--resume` rebuilds
                    // the context the agent already has (spike 1: same id, no fork) -- without it
                    // the operator's sentence and everything the agent worked out would be gone,
                    // and a promotion that costs the conversation is worse than a refusal.
                    args.AddRange(Projects.ResumeArgs(row.Session));
                }
                // ASK IT TO GO, THEN WAIT FOR IT TO BE GONE. The pipe name is deterministic per
                // lane, so the new shim cannot own it until the old one has actually exited -- see
                // `WaitLaneProcessesGone` for what racing this produced.
                if (_lanes.TryGetValue(row.Id, out var old)) { old.Shutdown(); _lanes.TryRemove(row.Id, out _); }
                if (!WaitLaneProcessesGone(row.Id, 15000))
                    _store.Event("lane_promotion_slow_exit", row.Id,
                        "the previous shim did not exit in 15s; respawning anyway (the pipe name may still be held)");
                var (rid, rmsg) = await RespawnLaneAsync(row.Id, row.Title, args, child, made.Worktree);
                if (rid > 0)
                {
                    _store.LaneState(row.Id, "alive");
                    _store.LanePresence(row.Id, "idle");
                    _store.Event("lane_promoted", row.Id,
                        $"ticket {made.Id} branch {made.Branch} worktree {made.Worktree} session={row.Session ?? "-"}");
                    _store.PaneEvent(row.Id, "announcement",
                        $"moved into its own checkout for ticket {made.Id} — {made.Branch}, session resumed. " +
                        $"Undo: dodona lane-stop {row.Id} (abandons the ticket and prunes the worktree)",
                        null, null, acked: true);
                    Announce($"[dodona] {row.Title} tried to write in the shared checkout, so it now has ticket " +
                             $"{made.Id} and a checkout of its own ({made.Worktree}). Undo: dodona lane-stop {row.Id}");
                }
                else
                {
                    // The ticket and worktree exist and the agent is still where it was. Say so:
                    // the alternative is a lane that looks promoted and is not.
                    _store.Event("lane_promotion_respawn_failed", row.Id, $"ticket {made.Id}: {rmsg}");
                    Announce($"[dodona] {row.Title} has ticket {made.Id} and a worktree at {made.Worktree}, but could " +
                             $"not be moved into it: {rmsg}. It is still in the shared checkout and still refused there.");
                }
            }
            catch (Exception ex) { _store.Event("lane_promotion_failed", row.Id, $"{ex.GetType().Name}: {ex.Message}"); }
        });

        return ($"denied: {deniedPath} is in the SHARED CHECKOUT, which is nobody's workspace -- other lanes and " +
                $"your operator are working in it, and its pre-commit hook refuses commits from it. You now have " +
                $"ticket {made.Id} and a private checkout of your own, and your session is being resumed there. " +
                $"Write this file at {moved} instead. Nothing was written to the shared tree.", move);
    }

    /// <summary>Which OPEN ticket, if any, holds a path -- named so a layer-1 refusal can say
    /// who (D-13). The path is put back into workspace-relative claim terms with the same
    /// repository-then-project rungs `claim-check` uses, minus the worktree rung: the caller has
    /// already established this path is NOT in a worktree, which is why it is being refused.
    ///
    /// A store read and a string compare, so it costs nothing on the write path -- which matters,
    /// because this runs behind a PreToolUse hook.</summary>
    /// <summary>Undo a ticket that should never have existed: prune the worktree, delete the
    /// branch, mark it abandoned. Claims are released by the state change alone -- the conflict
    /// query in `Store.FindConflicts` only looks at `state = 'open'`.
    ///
    /// Reached from `lane-stop` for a PROMOTED lane (D-9). The prune is retryable and never silent,
    /// the same discipline `LandOp` uses: a worktree nobody removed is a checkout of an old commit
    /// sitting in the repository forever.</summary>
    List<string> AbandonTicket(Store.TicketRow t, string why)
    {
        var lines = new List<string>();
        _store.TicketState(t.Id, "abandoned");
        var repoPath = RepoOf(t)?.Path ?? (t.RepoPath.Length > 0 ? t.RepoPath : _primary);
        if (t.Worktree.Length > 0 && Directory.Exists(t.Worktree))
        {
            // THE AGENT HAS TO LET GO OF THE DIRECTORY FIRST, and this is measured rather than
            // guessed: `lane-stop` asks the shim to shut down and returns immediately, so the child
            // is still alive with the worktree as its WORKING DIRECTORY -- and Windows refuses to
            // delete a directory that is any process's cwd. git reported exactly that:
            // "failed to delete '...wt/t2': Permission denied", so the ticket was abandoned while
            // its worktree and branch survived. An undo that half-happens is the announcement
            // lying, which is what D-9 exists to prevent.
            //
            // A CONDITION WITH A DEADLINE, never a sleep (CLAUDE.md 0.1): what un-sticks it is the
            // shim's own exit, read off the OS from this lane's recorded pids. On timeout it falls
            // through and prints the manual command rather than parking -- nothing waits on a person.
            if (t.LaneId is long stopping) WaitLaneProcessesGone(stopping, 10000);
            var (wc, wOut) = Git.Run(repoPath, "worktree", "remove", "--force", t.Worktree);
            if (wc == 0)
            {
                if (t.Branch.Length > 0) Git.Run(repoPath, "branch", "-D", t.Branch);
                _store.Event("worktree_pruned", null, $"ticket {t.Id} abandoned: {why}");
                lines.Add($"abandoned ticket {t.Id}: worktree pruned, branch {t.Branch} deleted, claims released");
            }
            else
            {
                // WHAT HOLDS IT, not merely that it is held. Windows says "Permission denied" and
                // nothing about which process, and CLAUDE.md 0.3 is explicit that a message naming
                // the wrong cause sends the next reader hunting through their own code. The pids
                // come from this lane's own shim-info record (section 4: never by process name).
                var holders = t.LaneId is long hl
                    ? string.Join(", ", LaneLiveness.Records(Paths.WorkspaceDir(_instanceId))
                        .Where(r => r.Lane == hl)
                        .Select(r => $"shim {r.Shim}={(LaneLiveness.PidAlive(r.Shim, "DodonaShim") ? "alive" : "gone")} " +
                                     $"child {r.Child}={(LaneLiveness.PidAlive(r.Child, "") ? "alive" : "gone")}"))
                    : "no lane";
                _store.Event("worktree_prune_failed", null, $"ticket {t.Id}: {wOut} [{holders}]");
                lines.Add($"abandoned ticket {t.Id} and released its claims, but its worktree could not be pruned: {wOut.Trim()}");
                lines.Add($"       remove it by hand:  git -C \"{repoPath}\" worktree remove --force \"{t.Worktree}\"");
            }
        }
        else
        {
            _store.Event("ticket_abandoned", null, $"ticket {t.Id}: {why} (no worktree on disk)");
            lines.Add($"abandoned ticket {t.Id}: claims released");
        }
        Announce($"[dodona] ticket {t.Id} abandoned ({why}) — worktree and branch removed, claims released");
        return lines;
    }

    /// <summary>Wait for the processes a lane is running RIGHT NOW to be gone. Returns false on
    /// timeout, which is a normal return: the caller then says what it could not do rather than
    /// parking (CLAUDE.md 0.1 -- a wait names the thing that un-sticks it, and this one names the
    /// shim's own exit).
    ///
    /// THE PIDS ARE SNAPSHOTTED FIRST, and that is the whole trick. `shim-lane&lt;N&gt;.json` is
    /// rewritten by the next spawn, so re-reading it inside the loop would start waiting on the
    /// REPLACEMENT process and never finish.
    ///
    /// Written for two callers that each got this wrong:
    ///  * promotion respawns a lane that is still CONNECTED, which nothing else in this codebase
    ///    does -- `lane-respawn` refuses a connected lane outright. The pipe name is deterministic
    ///    per lane and is only "free to reclaim" once the old shim is GONE, so respawning
    ///    immediately after `##shutdown` raced its exit: the new shim could not own the name, the
    ///    runtime came up disconnected, and the NEXT `##shutdown` then went nowhere -- leaving a
    ///    shim and an agent alive with the worktree as their working directory.
    ///  * abandoning a ticket then cannot prune that worktree, because Windows refuses to delete a
    ///    directory that is any process's cwd. Measured: git said "Permission denied", and the
    ///    holder diagnostic said `shim 18720=alive child 51408=alive` ten seconds later.</summary>
    bool WaitLaneProcessesGone(long laneId, int timeoutMs)
    {
        var pids = LaneLiveness.Records(Paths.WorkspaceDir(_instanceId)).Where(r => r.Lane == laneId).ToList();
        if (pids.Count == 0) return true;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!pids.Any(r => LaneLiveness.PidAlive(r.Shim, "DodonaShim") || LaneLiveness.PidAlive(r.Child, "")))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>The agent binary a lane was last spawned with, read off its own `shim_spawned`
    /// record. Null when there is none to read, and the caller falls back to the project's config.
    ///
    /// The detail is written by <see cref="AttachShimAsync"/> as `pipe=... child=... cwd=...`, so
    /// the value is delimited by ` cwd=` rather than by whitespace -- an agent path with spaces in
    /// it is ordinary on Windows, and splitting on the space would silently truncate it to
    /// `C:\Program`.</summary>
    string? ChildOfLane(long laneId)
    {
        var detail = _store.LastEventDetail("shim_spawned", laneId);
        if (detail is null) return null;
        var i = detail.IndexOf("child=", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = detail[(i + "child=".Length)..];
        var j = rest.IndexOf(" cwd=", StringComparison.Ordinal);
        var child = (j >= 0 ? rest[..j] : rest).Trim();
        return child.Length > 0 ? child : null;
    }

    /// <summary>Which REPOSITORY of this workspace holds a path, and the path in the
    /// workspace-relative claim terms that repository's claims are written in. Longest base first,
    /// so a repo nested under another wins -- the same ordering `claim-check` needs for the same
    /// reason. Null when the path is under no repository, which is where promotion stops: there is
    /// nothing to branch.</summary>
    (RepoRef Repo, string Rel)? RepoRelOf(string fullPath)
    {
        var full = Path.GetFullPath(fullPath).Replace(Path.DirectorySeparatorChar, '/');
        foreach (var r in Repositories().OrderByDescending(r => r.Path.Length))
        {
            if (r.Path.Length == 0) continue;
            var b = Path.GetFullPath(r.Path).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/') + "/";
            if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return (r, Claims.Normalize(r.ClaimPrefix + full[b.Length..]));
        }
        return null;
    }

    string? ClaimHolder(string fullPath)
    {
        var full = Path.GetFullPath(fullPath).Replace(Path.DirectorySeparatorChar, '/');
        // The repository rung is `RepoRelOf`, shared with promotion so the two cannot disagree
        // about which repo owns a path. The PROJECT rung below it is kept because it is exactly
        // what the old `_primary` base was: a path inside a project but outside every repo.
        string? rel = RepoRelOf(fullPath)?.Rel;
        if (rel is null)
            foreach (var pp in ProjectPaths().OrderByDescending(pp => pp.Length))
            {
                if (pp.Length == 0) continue;
                var b = Path.GetFullPath(pp).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/') + "/";
                if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase)) { rel = full[b.Length..]; break; }
            }
        if (rel is null) return null;
        rel = Claims.Normalize(rel);
        foreach (var t in _store.Tickets().Where(t => t.State == "open"))
        {
            if (!_store.TicketClaims(t.Id).Any(cl => Claims.Covers(cl.Kind, cl.Value, rel))) continue;
            var title = t.LaneId is long lid
                ? _store.LanesAll().FirstOrDefault(l => l.Id == lid)?.Title
                : null;
            return $"ticket {t.Id}" + (title is { Length: > 0 } ? $" (lane {title})" : "");
        }
        return null;
    }

    void RecordLaneConfig(long laneId, string project, Config cfg, List<string> args)
    {
        var fromArgv = Projects.ArgValue(args, "--permission-mode");
        _store.Event("lane_config", laneId,
            $"project={project} agent={cfg.Agent} permissionMode={fromArgv ?? cfg.PermissionMode} " +
            $"source={(fromArgv is null ? "config" : "argv")} allowedTools={cfg.Allowed.Length}");
    }

    static bool IsClaude(string child) =>
        child.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>A lane name derived from what was typed: the longest substantial word,
    /// which is usually the subject. Code, not a model — it must be instant, and a name is
    /// cheap to change. (§2.2: derive in code what is not really a judgement.)</summary>
    static string NameFromText(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","for","to","in","on","of","with","that","this","some","new","please","its","it",
            "add","make","fix","let","can","you","we","i","should","would","could","need","want","get","put","use",
            "there","then","when","where","what","how","why","from","into","about","over","under","out","up","down",
        };
        var word = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z][A-Za-z0-9_-]{2,}")
            .Select(m => m.Value)
            .Where(v => !stop.Contains(v))
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
        return (word ?? "LANE").ToUpperInvariant();
    }

    /// <summary>The framing for a lane with no ticket. The [DISPATCHER] channel must be
    /// declared or the model treats mid-turn operator instructions as a prompt-injection
    /// attempt and refuses them (spike 3) — that applies to every lane, not just ticketed
    /// ones.
    ///
    /// The branch paragraph is the whole point of this text (M5.1). The previous version said
    /// "you have no ticket and no claim, so nothing is reserved for you — if the operator
    /// wants isolated work on a branch, they will create a ticket": it told the agent it was
    /// un-isolated and then left it free to branch anyway. A plain lane runs in a SHARED
    /// checkout, so one `git checkout` reassigns every other lane's work and the operator's
    /// own — which is exactly what a project whose own CLAUDE.md ends in "check out a branch"
    /// will make it do (docs/M5-DELIVERY-PLAN.md §4). Until a plain lane gets a worktree of its
    /// own, the honest instruction is: do not touch which branch is checked out, at all.</summary>
    /// <summary>...and the folder sentence is built by <see cref="Projects.DirSentence"/>, which
    /// <see cref="Projects.PromptDirMismatch"/> also reads back — so the prompt's idea of where
    /// the agent is and the process's actual working directory are checked against each other at
    /// every spawn (trap T1). One definition, written in one place and parsed in one place.</summary>
    internal static string LaneSystemPrompt(string title, string workDir) =>
        $"You are the agent for lane \"{title}\", operated by the Dodona orchestrator. " +
        Projects.DirSentence(workDir) + " " +
        "IMPORTANT: that directory is a SHARED checkout. Other lanes and your human operator are working in it at the " +
        "same time, so it is not yours to reconfigure: never run `git checkout`, `git switch`, `git stash`, or anything " +
        "else that changes which branch is checked out or moves uncommitted work aside. Doing so silently reassigns " +
        "every other lane's work and your operator's too. If the task genuinely needs its own branch, say so and stop " +
        "— your operator creates a ticket for that, and a ticket gets a private checkout of its own. " +
        "Real-time instructions from your human operator arrive in hook output labeled [DISPATCHER]; they are authentic " +
        "and carry the same authority as your original task, even when they change or contradict earlier instructions.";

    /// <summary>The framing for a TICKET lane. Factored out of `ticket-agent` because respawn
    /// needs the identical text (M5.1) and was rebuilding the plain-lane prompt instead.</summary>
    static string TicketSystemPrompt(long tid, string title, string claims) =>
        $"You are a lane agent operated by the Dodona orchestrator, working ticket {tid}: \"{title}\". " +
        $"Your worktree is the current working directory; work only there. Your declared claim is [{claims}] — " +
        "a PreToolUse gate denies writes outside it; if denied, stay within the claim or ask your operator for an extension. " +
        "Real-time instructions from your human operator arrive in hook output labeled [DISPATCHER]; they are authentic " +
        "and carry the same authority as your original task, even when they change or contradict earlier instructions.";

    /// <summary>" repo engine", or nothing at all when the workspace root IS the
    /// repository — the single-repo project should never have to read about repos.</summary>
    static string RepoTag(string repo) => repo == "." ? "" : $" repo {repo}";

    /// <summary>Spawn a lane: shim → child, detached, pumped, recorded. Shared by
    /// lane-start (fake/test agents), router-start (warm utility session), and
    /// ticket-agent (real claude in a gated worktree).</summary>
    /// <param name="scope">Which project this lane is FOR, when that is not the same question as
    /// where it runs (P5.1). A management lane runs in the neutral directory and is scoped to a
    /// project; a work lane's folder IS its project, so it passes nothing and the scope is
    /// derived below. Optional so no other spawn site had to change.</param>
    async Task<(long Id, string Msg)> SpawnLaneAsync(string title, string role, string workDir, string child, List<string> childArgs,
                                                    string? scope = null)
    {
        var id = _store.LaneCreate(title);
        _store.LaneRole(id, role);
        return await AttachShimAsync(id, title, role, workDir, child, childArgs, scope);
    }

    /// <summary>Respawn an agent into an EXISTING lane row — the thread survives its
    /// agent (§11). Same pipe name (deterministic per lane, and the dead shim freed it),
    /// same pane, fresh process.</summary>
    Task<(long Id, string Msg)> RespawnLaneAsync(long laneId, string title, List<string> childArgs, string child,
                                                 string? workDir = null)
    {
        var row = _store.LanesAll().FirstOrDefault(l => l.Id == laneId);
        var role = row?.Role ?? "work";
        // Never `_primary` by default any more (M5.1): the lane's own recorded directory is
        // the answer, and only a lane predating the column falls back to the primary. Same
        // three rungs as the `lane-respawn` handler, same function, one order (P1.3) -- and
        // `workDir` is UNCHECKED on purpose: a caller naming a directory is asserting it, and
        // for `lane-respawn` it is an answer that handler has already vetted.
        var cwd = ResolveLaneCwd(
            workDir,
            row?.Cwd is { Length: > 0 } rc && Directory.Exists(rc) ? rc : null,
            _primary);
        return AttachShimAsync(laneId, title, role, cwd, child, childArgs);
    }

    async Task<(long Id, string Msg)> AttachShimAsync(long id, string title, string role, string workDir, string child, List<string> childArgs,
                                                     string? scope = null)
    {
        // TRAP T1, ENFORCED AT THE ONE PLACE BOTH FACTS EXIST (docs/LOCATIONS-PLAN.md Phase 2).
        // The prompt says "your working directory is X"; the ProcessStartInfo below sets the real
        // one. Every spawn in the product funnels through here, so this is the only place that
        // can compare them -- and comparing them is the difference between "one parameter, used
        // twice" as an instruction and as a fact. The M5.1 incident was exactly this divergence,
        // it compiled clean, and no acceptance suite could see it because the prompt lives in an
        // argv nobody reads back.
        //
        // It REFUSES rather than correcting, and the row is left `unreachable` like any other
        // failed spawn. This can only fire on a code defect (no configuration reaches it), so it
        // fires in a suite and never on the operator's machine -- and an agent working in a
        // folder it was told it is not in is not a lane worth starting.
        if (Projects.PromptDirMismatch(childArgs, workDir) is string mismatch)
        {
            _store.LaneState(id, "unreachable");
            _store.Event("shim_spawn_refused", id, mismatch);
            return (-1, $"error: lane {id} not started -- {mismatch} (docs/LOCATIONS-PLAN.md Phase 2, trap T1)");
        }

        var pipe = Instance.LanePipe(_instanceId, id);
        _store.LanePipe(id, pipe);
        _store.LaneCwd(id, workDir);      // so a respawn lands here too, not in _primary (M5.1)
        // ...AND WHICH PROJECT IT IS FOR (P5.1). Two different questions, written down separately
        // because for a management lane they have two different answers: a brain is scoped to a
        // project while running in the neutral directory (P5.8). For a work lane the folder IS
        // the project, so it is derived rather than passed, and a re-homed lane re-derives it.
        // The management fallback to the first project exists so a spawn that somehow reaches
        // here with no scope still has a registration -- an empty one reads as "unregistered",
        // which the reaper acts on.
        _store.LaneProject(id, scope
                               ?? Projects.Of(ProjectPaths(), workDir)
                               ?? (Projects.IsManagementRole(role) ? _primary : ""));

        // ---- LAYER 1: THE GATE, ON EVERY WORK LANE (WORK-ISOLATION-PLAN section 3, P1) ----
        //
        // HERE because every spawn funnels through here -- `SpawnLaneAsync` and
        // `RespawnLaneAsync` both call it -- so a call site cannot forget to gate a lane. That
        // is the same correction `DaemonClient.Send` needed for start-on-demand (CLAUDE.md 3.1):
        // two of three write paths ensured, the third carried the most traffic and did not.
        //
        // WORK LANES ONLY, and not as a carve-out: management roles (router, brain, compressor)
        // run in the neutral directory and write nothing, so a gate on them would be a hook cost
        // on every utility turn for a question whose answer is always the same. A non-claude
        // child takes no claude flags at all (the fake agent of section 17), which is also why
        // no acceptance suite using it can see this argv -- `unit` holds the argv shape instead.
        //
        // The lane's TICKET is looked up rather than passed: `ticket-agent` links ticket to lane
        // AFTER the spawn, and a lane's ticket changes during its life (P2 promotes a plain lane
        // into one). The file names only the lane, so the daemon answers from current state --
        // which is the shape forced by hooks being fixed at session start (see `DeployGate`).
        if (role == "work")
        {
            // THE TICKET IS RESOLVED BY WORKING DIRECTORY, NOT BY THE LANE LINK, and that is
            // not a preference: `ticket-agent` calls `TicketSetLane` AFTER the spawn returns,
            // so at this point the link does not exist yet and matching on it silently
            // produced a ticket lane with no `--ticket` -- the claim question never asked, and
            // m1's two gate checks red. A ticket lane's cwd IS its worktree (pinned by
            // `m3:186-187` and `LaneCwdPrecedenceTests`), so the directory answers it with no
            // ordering to get wrong. The lane link is still consulted, for a respawn whose
            // recorded cwd has drifted.
            var t = _store.Tickets().FirstOrDefault(t => t.State == "open" &&
                        (t.LaneId == id ||
                         (t.Worktree.Length > 0 && Paths.SamePath(t.Worktree, workDir))));
            var gate = DeployGate(id, t?.Id ?? 0, t?.Worktree);
            // THE FILE IS WRITTEN FOR EVERY WORK LANE; THE FLAG IS ONLY FOR A REAL CLAUDE.
            // Splitting the two is what gives this a model-free surface: `IsClaude` is false
            // for the fake agent of section 17, so gating only claude lanes would leave the
            // deployment invisible to all thirteen suites -- and section 3 has the incident for
            // what unobservable wiring costs (the routing ladder: fully covered, fully green,
            // and dead in production for two days). The fake agent must not be handed a flag it
            // does not understand, so it gets the file and not the argument.
            if (gate is not null && IsClaude(child)) { childArgs.Add("--settings"); childArgs.Add(gate); }
        }

        var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                      ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
        var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = workDir };
        psi.ArgumentList.Add(pipe);
        psi.ArgumentList.Add(child);
        foreach (var a in childArgs) psi.ArgumentList.Add(a);
        psi.Environment["DODONA_SHIM_INFO"] = Paths.ShimInfo(_instanceId, id);
        // What this lane is for. A real claude learns its job from the system prompt; this
        // says the same thing to a child that has no system prompt to read (§17's fake
        // agent), and is worth having in the environment of any child when debugging.
        psi.Environment["DODONA_LANE_ROLE"] = role;
        // WHICH WORKSPACE THIS AGENT BELONGS TO (Phase 0c, P0c.1). Without it a `dodona`
        // command run by the agent inside this lane had nothing to resolve by except
        // Environment.CurrentDirectory — and that fallback CREATED a workspace named after
        // whatever folder the process happened to be in, moving a legacy store into workspace
        // territory as a side effect of `dodona tickets`. Creating a workspace is a user
        // action (operator, 2026-08-19; docs/LOCATIONS-PLAN.md D-L9), so the agent is told
        // where it is instead of being left to guess. Inherited by the agent through the shim,
        // which does not touch its child's environment.
        psi.Environment["DODONA_WORKSPACE"] = _instanceId;

        // A SPAWN THAT NEVER HAPPENED MUST NOT LEAVE THE ROW SAYING `alive`.
        //
        // The row is created by SpawnLaneAsync before we get here, and the only failure this
        // method used to handle was "the pipe never answered" -- which marks the lane
        // `unreachable` further down. `Process.Start` THROWING is a different path and was not
        // handled at all, so the exception escaped and the lane stayed `alive` forever: no
        // process, no shim-info record, and nothing to notice it until the next daemon restart
        // ran reconcile (P3.4).
        //
        // Found by running the app, 2026-08-19: a probe copied Dodona, DodonaUi and the fake
        // agent into a directory and forgot DodonaShim. `dodona ps` correctly said LANES 0 --
        // it reads the OS -- while the window faithfully rendered a live FOAM tile from the
        // store row. That is the count lying in the direction this whole phase exists to stop,
        // and the UI was not wrong: it showed exactly what it was told.
        //
        // The existence check is separate from the catch on purpose: a missing shim is the
        // overwhelmingly likely cause and deserves to be NAMED rather than reported as a
        // Win32Exception, because "name the real cause" is the difference between a five-second
        // fix and an hour (CLAUDE.md 0.3).
        if (!File.Exists(shimExe))
        {
            _store.LaneState(id, "unreachable");
            var missing = $"shim binary not found: {shimExe}" +
                          (Environment.GetEnvironmentVariable("DODONA_SHIM") is null
                              ? " (looked beside this daemon; a published build has it there)"
                              : " (DODONA_SHIM points at it)");
            _store.Event("shim_spawn_failed", id, missing);
            return (-1, $"error: lane {id} not started -- {missing}");
        }
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            _store.LaneState(id, "unreachable");
            _store.Event("shim_spawn_failed", id, $"{ex.GetType().Name}: {ex.Message} (shim={shimExe})");
            return (-1, $"error: lane {id} not started -- could not launch {shimExe}: {ex.Message}");
        }
        _store.Event("shim_spawned", id, $"pipe={pipe} child={child} cwd={workDir}");

        var rt = new LaneRuntime(id, pipe, _store);
        HookTurnEnd(rt, role);
        if (await rt.ConnectAndPumpAsync(attempts: 20))
        {
            _lanes[id] = rt;
            _store.Event("lane_started", id, $"{title} role={role}");
            return (id, $"lane {id} title {title} role {role} pipe {pipe}");
        }
        _store.LaneState(id, "unreachable");
        return (-1, $"error: lane {id} shim pipe never answered");
    }

    // -------------------------------------------- what a work lane's turn-final feeds (§5, R4)

    /// <summary>Everything that consumes a work lane's turn-final, wired in ONE place — because
    /// `LaneRuntime.OnResult` is a single delegate field and there are two consumers now.
    ///
    /// **IT IS AN ASSIGNMENT, NOT `+=`, AND THAT IS THE TRAP.**
    /// `docs/REVIEW-AND-MERGE-PLAN.md` §10 named it before R4 existed, and it is the one this
    /// phase was most likely to walk into: a second consumer added the obvious way — another
    /// `rt.OnResult = …` at whichever call site happened to need it — silently REPLACES the
    /// compressor, and the symptom is "the panes went verbose" with nothing anywhere pointing
    /// here. So the composition is explicit, it lives in this one method, and both consumers are
    /// named. Anything added later goes in the lambda below, next to them.
    ///
    /// **Only WORK lanes**, for two separate reasons rather than one: a compressor whose own
    /// result was compressed would ask itself to summarise its summary, forever; and a utility
    /// lane has no ticket, so there is nothing for it to produce a completion record about.
    ///
    /// **Each consumer is isolated.** `OnResult` is invoked from the wire pump and not inside its
    /// try/catch (`LaneRuntime.OnLine`), so an exception from the first consumer would take the
    /// second one with it and the pump besides — the same trap in a second costume, where the
    /// compressor silently kills the record instead of the other way round.
    ///
    /// **BOTH construction sites call this, and the second is the one that goes quietly dead.**
    /// `SpawnLaneAsync` wires a lane the daemon starts; reconcile wires every lane it ADOPTS at
    /// startup. A daemon restarts on every publish and hot swap, so a record wired only at spawn
    /// would simply stop happening for every lane the operator already had — fully covered and
    /// dead in production, which is §3's routing ladder exactly. `m1` restarts the daemon and
    /// demands a record from an adopted lane for that reason and no other.</summary>
    void HookTurnEnd(LaneRuntime rt, string role)
    {
        if (role != "work") return;
        rt.OnResult = (laneId, paneEventId, body) =>
        {
            try { CompressResult(laneId, paneEventId, body); }
            catch (Exception ex) { _store.Event("compressor_failed", laneId, $"hook threw: {ex.Message}"); }
            try { CompletionRecord(laneId, paneEventId, body); }
            catch (Exception ex) { _store.Event("completion_record_failed", laneId, $"hook threw: {ex.Message}"); }
        };
    }

    /// <summary>
    /// The compressor pool (§5). Warm sessions, so a turn lands on ~1s of latency instead
    /// of a cold start, and a POOL of them rather than one: a single compressor
    /// accumulating six lanes' turn-finals is exactly the unbounded serialization point
    /// §3 forbids the dispatcher to be. Cheap model, low effort — shortening a paragraph
    /// is not where judgement compounds (§9's ladder), and it runs 5–10× more often than
    /// anything else in the system.
    /// </summary>
    async Task<string> StartCompressorsAsync(string child, string model, string effort, int count)
    {
        count = Math.Clamp(count, 1, 4);
        // A schema, not an instruction to be brief: "be concise" is advice a model may
        // decline, a character cap on a named field is not (§4/§5).
        var sys = "You are Dodona's pane compressor. You will be given one agent's turn-final message. " +
                  "Reply with ONLY one line of JSON, no prose, no markdown, no code fence: " +
                  "{\"headline\":\"<=90 characters\",\"needs_you\":true|false,\"options\":[\"<a few words>\"]} " +
                  "headline is what the operator must know, written for someone glancing at one pane of six: " +
                  "past tense for work that happened, imperative for what is wanted. No preamble, no markdown, " +
                  "never mention 'the user', never restate the question. " +
                  "needs_you is true only when the work cannot continue without a human decision. " +
                  "options lists those choices, at most three, and is [] whenever needs_you is false.";
        var args = IsClaude(child) ? ClaudeArgs(_config, model, effort, sys, acceptEdits: false, utility: true) : new List<string>();

        var alive = _store.LanesAll().Count(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id));
        if (alive >= count) return $"compressor pool already warm ({alive})";
        // P3.5 applies to the POOL too, and the plan named only the brain and the router. The
        // arithmetic above counts a compressor as present only if it is ADOPTED, so a pool member
        // whose pipe is live but unreachable is invisible here and gets a replacement started
        // beside it -- one leaked `claude -p` per restart, the brain leak in a third costume.
        // Closing the class rather than the two instances the plan happened to list.
        if (!await ClearOfLivePredecessorsAsync(null, "compressor"))
            return "compressor pool left as it is: a previous pool member is still holding its pipe " +
                   "(`dodona ps` shows it; `dodona stop-all --lanes` clears it)";
        var started = new List<long>();
        for (int i = alive; i < count; i++)
        {
            var (id, msg) = await SpawnLaneAsync($"COMPRESS{i + 1}", "compressor", NeutralCwd(), child, args);
            if (id < 0) return started.Count > 0
                ? $"compressor pool partially up: {started.Count} warm, then: {msg}"
                : $"error: {msg}";
            _compressorLocks[id] = new SemaphoreSlim(1, 1);
            started.Add(id);
        }
        return $"compressor pool warm: {alive + started.Count} session(s) on {model}" +
               (effort is { Length: > 0 } ? $"/{effort}" : "") + $" — lanes {string.Join(", ", started)}";
    }

    /// <summary>
    /// A turn ended (§5). The row is already in the store and already on screen; this only
    /// ever fills in a shorter rendering of it, so every failure path below simply leaves
    /// the operator reading the agent's own words — which is the current behaviour, and
    /// therefore a safe floor. Nothing here is ever awaited by the wire pump.
    /// </summary>
    void CompressResult(long laneId, long paneEventId, string body)
    {
        // Already the length a compressor would produce: spending a model call here would
        // be exactly the no-judgment volume §2.2 says not to buy.
        if (body.Length <= 120 && !body.Contains('\n')) return;

        var pool = _store.LanesAll()
            .Where(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id))
            .ToList();
        if (pool.Count == 0) return;                  // no pool warm: the full text stands

        var pick = pool[(int)((uint)Interlocked.Increment(ref _compressorNext) % pool.Count)];
        if (!_compressorLocks.TryGetValue(pick.Id, out var gate)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? reply;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await gate.WaitAsync();
                try { reply = await _lanes[pick.Id].AskAsync(body, 25000); }
                finally { gate.Release(); }
                if (reply is null) { _store.Event("compressor_timeout", pick.Id, $"lane={laneId} row={paneEventId}"); return; }

                var open = reply.IndexOf('{');
                var close = reply.LastIndexOf('}');
                if (open < 0 || close <= open) { _store.Event("compressor_failed", pick.Id, $"no json in reply: {Truncate(reply, 120)}"); return; }
                using var d = JsonDocument.Parse(reply[open..(close + 1)]);
                var headline = d.RootElement.TryGetProperty("headline", out var h) ? h.GetString() ?? "" : "";
                if (headline.Trim().Length == 0) { _store.Event("compressor_failed", pick.Id, "empty headline"); return; }

                var needsYou = d.RootElement.TryGetProperty("needs_you", out var ny) && ny.ValueKind == JsonValueKind.True;
                var options = new List<string>();
                if (d.RootElement.TryGetProperty("options", out var op) && op.ValueKind == JsonValueKind.Array)
                    options.AddRange(op.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3));

                // The fixed shape from §5. The lane's name is NOT repeated here the way the
                // design sketch shows it: in a pane the row already sits under that lane's
                // own coloured header, and in the feed the title is already the first thing
                // on the row. Printing it a third time is noise, not structure.
                var flat = headline.Trim().ReplaceLineEndings(" ");
                // A model that already opened with the word would otherwise render
                // "BLOCKED — BLOCKED ..." — the prefix is structure, so it is added exactly
                // once and never echoed.
                if (flat.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase))
                    flat = flat[7..].TrimStart(' ', ':', '-', '—');
                var text = new StringBuilder();
                if (needsYou) text.Append("BLOCKED — ");
                text.Append(Truncate(flat, 90));
                if (needsYou && options.Count > 0) text.Append("\n   options: ").Append(string.Join(" / ", options));

                _store.PaneCompressed(paneEventId, text.ToString());
                _store.Event("compressed", pick.Id,
                    $"{sw.ElapsedMilliseconds}ms lane={laneId} row={paneEventId} {body.Length}->{text.Length} chars needs_you={needsYou}");
            }
            catch (Exception ex) { _store.Event("compressor_failed", pick.Id, ex.Message); }
        });
    }

    // ------------------------------------------------- the completion record (R4, D-R8/D-R13)

    /// <summary>One lock per TICKET, held across "read the last record, decide, write the next
    /// one". `OnResult` fires on a lane's wire-pump thread, so two turns of one ticket can arrive
    /// concurrently -- and without this both would read the same previous digest, both would
    /// decide the worktree had changed, and D-R13's whole point (one record, not one per turn)
    /// would fail exactly when a lane is busiest. Concurrent for the same reason `_lanes` had to
    /// become concurrent in R3.5: it is written from background threads while the control pipe
    /// reads the store beside it.</summary>
    readonly ConcurrentDictionary<long, object> _recordLocks = new();

    /// <summary>
    /// A turn ended on a work lane holding an open ticket, so the ticket gets a PR-shaped record
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` D-R8). Assembled by CODE and carrying NO OPINIONS: the
    /// ticket, its branch and worktree, what the branch changed, the verify result, the
    /// silent-drop check, and **the agent's own end-of-turn report** -- which is the closest
    /// thing this system has to a PR description and the one thing the manager has never once
    /// been shown (§1: `BrainReview` fires at lane creation and never sees a lane agent's output).
    ///
    /// **It writes no judgement and it decides nothing.** The manager reading it is R5; the
    /// operator's `approve` is still the only yes (§6, D-R10). R4 is the assembly.
    ///
    /// **THE VERIFY RESULT IS REPORTED, NEVER RUN (D-R15).** This is the phase's one real design
    /// decision, so it is written out in the plan rather than left implicit here. In short: a
    /// record assembled on the LAND path would be produced after the approval it exists to
    /// inform, so completion is the only moment it can change anything; and a verify run *here*
    /// would cost a build plus suites per completed turn (quota and wall clock, CLAUDE.md §0.1)
    /// to answer a different question from D-R1's -- this branch has not had main merged into it,
    /// so a green here says nothing about the tree that would land while reading as though it
    /// did. So the slot carries the newest verify already recorded for the ticket, and says
    /// `not-run` in as many words when there is none.
    ///
    /// **The drop check DOES run here**, because it is pure git -- `MainMergeOnBranch` plus
    /// `SilentDrops`, no build and no test. Until a land has merged main in there is nothing for
    /// the branch to have discarded, and that is `moot`: a real state, said out loud, and not the
    /// same thing as a check that failed to run. `land_drop_check_moot` is the pattern.
    ///
    /// **Gated on the worktree having CHANGED since the last record (D-R13).** A `result` is the
    /// end of a turn, not of the conversation (`LANE-LIFECYCLE.md` §2 -- "the agent said it was
    /// done" is turn-completion), so a chatty lane must produce ONE record and not one per turn.
    /// The digest is the branch tip plus a hash of `git status --porcelain`: committed *and*
    /// uncommitted work, because a turn that edited without committing has changed the worktree
    /// and a reviewer wants to know it (the land refuses a dirty worktree outright).
    ///
    /// **NO PANE ROW, DELIBERATELY.** A record is a machine-shaped artifact for a reviewer, and
    /// an announcement per completed turn would put a JSON blob in the operator's pane and press
    /// on the badge -- while §4's rule is that attention is owed when a person is NEEDED, and
    /// nobody is needed by a record. It reaches people through R6's write-up in the approval ask;
    /// until then `dodona ticket-record &lt;ticket&gt;` reads it, which is also what makes it
    /// reachable from a check at all (CLAUDE.md §3.1: an affordance no verb can reach is where
    /// the next defect lives).
    ///
    /// Every giving-up path below records WHY. An empty record, or a silent return where a record
    /// was expected, is the fail-open this codebase has paid for twice (§3's dead routing ladder,
    /// `GateHook`'s BOM) -- so "there is nothing to record" and "the record could not be built"
    /// are different events with different names.
    /// </summary>
    void CompletionRecord(long laneId, long paneEventId, string body)
    {
        // A plain lane's turn is the overwhelmingly common case and there is no PR to shape, so
        // it is silent rather than event-per-turn noise. Every case PAST this point is a ticket
        // lane, where saying nothing would be indistinguishable from being broken.
        var t = _store.Tickets().FirstOrDefault(x => x.State == "open" && x.LaneId == laneId);
        if (t is null) return;

        // Off the pump thread: this shells out to git several times, and the pump is what
        // delivers the agent's output to the pane. Nothing here is ever awaited by anybody.
        _ = Task.Run(() =>
        {
            try { BuildRecord(t, laneId, paneEventId, body); }
            catch (Exception ex)
            {
                _store.Event("completion_record_failed", laneId, $"ticket {t.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    void BuildRecord(Store.TicketRow t, long laneId, long paneEventId, string body)
    {
        var tid = t.Id;
        // Resolved the same way the land resolves them, and for the same reason: a record that
        // named a different repository from the one the land will fast-forward would be a report
        // about a tree nobody is going to ship (P0.1's wrong-main incident, one step upstream).
        var repo = RepoOf(t);
        if (repo is null)
        {
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
            return;
        }
        var cfg = Config.For(_primary, repo.Path);

        // NO WORKTREE, NO RECORD -- and it says so. `git diff --stat main...branch` would still
        // answer from the shared checkout, so this is a place where a plausible-looking record
        // could be assembled about a tree the agent is not standing in. A ticket can legitimately
        // outlive its checkout (the land carries the same case), so this is a state and not an
        // error; what it is not is something to paper over.
        if (t.Worktree.Length == 0 || !Directory.Exists(t.Worktree))
        {
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: no worktree at '{t.Worktree}' -- nothing to diff or digest");
            return;
        }

        var (headCode, head) = Git.Run(t.Worktree, "rev-parse", "HEAD");
        var (statusCode, porcelain) = Git.Run(t.Worktree, "status", "--porcelain");
        if (headCode != 0 || statusCode != 0)
        {
            // git itself could not answer, so there is no digest and therefore no way to honour
            // D-R13 either. Refusing beats writing a record whose gate is a guess.
            _store.Event("completion_record_impossible", laneId,
                $"ticket {tid}: git could not read the worktree (rev-parse={headCode} status={statusCode}) at {t.Worktree}");
            return;
        }
        var digest = Digest(head + "\n" + porcelain);
        // The record's own JSON, set inside the lock and read after it -- see the comment on
        // the `ManagerReview` call below for why R5 fires from OUTSIDE the lock and not inside.
        string? written = null;

        lock (_recordLocks.GetOrAdd(tid, _ => new object()))
        {
            // D-R13's gate. The previous record is read back out of its own event rather than
            // held in memory: a daemon restarts on every publish, and an in-memory digest would
            // make the first turn after every restart produce a duplicate record -- which is the
            // "outlives its reason" failure in reverse, a gate that quietly stops gating.
            var prev = _store.LastTicketEvent(tid, "completion_record");
            if (prev is { Detail: string pd } && DigestOf(pd) == digest)
            {
                _store.Event("completion_record_unchanged", laneId,
                    $"ticket {tid}: worktree unchanged since the last record ({digest}) -- one record per change, not per turn (D-R13)");
                return;
            }

            // What the branch changed. THE THREE-DOT FORM IS ALREADY MERGE-BASE-RELATIVE:
            // `git diff A...B` diffs from the merge base of A and B to B, which is precisely
            // D-R8's `<merge-base>...<branch>`. §10's merge-base trap is about the DROP check --
            // where the reference point has to survive main having been merged in -- and does not
            // apply here: before that merge this is the fork-point diff, and after it, it is the
            // branch's net contribution over main's tip. Both are what a PR shows.
            var range = $"{cfg.Main}...{t.Branch}";
            var (dsCode, diffstat) = Git.Run(t.Worktree, "diff", "--stat", range);
            var (nmCode, names) = Git.Run(t.Worktree, "diff", "--name-only", range);
            var changed = nmCode == 0
                ? names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
                : new List<string>();

            // The silent drop (D-R4), run here because it is free. `moot` until a land has
            // merged main in; meaningful from the second land attempt onward, which is exactly
            // the D-R3 flow -- the land refuses on a conflict, the agent resolves and commits,
            // the turn ends, and this is the record the manager reads BEFORE the next land.
            var (mergeCommit, preMerge) = MainMergeOnBranch(t.Worktree, cfg.Main, t.Branch);
            var drops = mergeCommit.Length == 0 ? new List<string>() : SilentDrops(t.Worktree, preMerge, mergeCommit, t.Branch);
            var dropState = mergeCommit.Length == 0 ? "moot" : drops.Count == 0 ? "clean" : "dropped";

            // D-R15: reported, never run. `not-run` is a value here, not an omission.
            var v = _store.LastTicketEvent(tid, "verify_green", "verify_red");
            var verifyState = v?.Kind switch { "verify_green" => "green", "verify_red" => "red", _ => "not-run" };

            var record = new
            {
                ticket = tid,
                title = t.Title,
                branch = t.Branch,
                worktree = t.Worktree,
                repo = t.Repo,
                main = cfg.Main,
                head,
                digest,
                row = paneEventId,          // the transcript row the report came from, for R6
                range,
                files = changed.Count,
                changed = changed.Take(60).ToList(),
                diffstat = dsCode == 0 ? diffstat : $"(git diff --stat failed: {diffstat})",
                uncommitted = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                verify = new
                {
                    state = verifyState,
                    when = v?.Ts ?? "",
                    // D-R15 in one sentence, IN the record, because the record is what a reviewer
                    // reads and "verify: not-run" with no reason invites someone to add a verify
                    // run here.
                    detail = v?.Detail ?? "no verify has run for this ticket; the one that gates is the land's own, on the merged result (D-R15)",
                },
                drop = new { state = dropState, files = drops, merge = mergeCommit, preMerge },
                // The agent's own words, whole unless they are enormous. This is the field that
                // did not exist anywhere before R4.
                report = Truncate(body, 4000),
            };
            var json = JsonSerializer.Serialize(record);
            // `ticket <id> {json}` -- the house shape for a ticket event (`Store.LastTicketEvent`
            // matches on it), with the JSON starting at the first brace.
            _store.Event("completion_record", laneId, $"ticket {tid} {json}");
            written = json;
        }

        // R5 FIRES FROM HERE, AND BOTH HALVES OF THAT ARE DELIBERATE.
        //
        // OUTSIDE THE LOCK, and on a task of its own -- belt and braces, because either one
        // alone is easy to lose in a later edit. `_recordLocks` is per TICKET and is held above
        // across read-decide-write; the manager review is a model call with a 25 s timeout, so
        // firing it inside would hold one ticket's lock across that call and serialise every
        // later turn of the same ticket behind a manager thinking.
        //
        // TRIGGERED BY THE RECORD EXISTING, not by a third consumer of `rt.OnResult`.
        // `HookTurnEnd` is an ASSIGNMENT with two consumers already (plan §10), and a review
        // wired there would have to re-derive D-R13's gate to know whether anything was new to
        // review. The record IS that answer: it exists exactly when the worktree moved.
        //
        // AND THE ASK IS RAISED AFTER THE REVIEW IS FIRED, NEVER BEHIND WHETHER IT ANSWERS (R6,
        // D-R11). Both calls are unconditional on the record existing and neither is conditional
        // on the other's outcome: `ManagerReview` returns immediately in every case (its work is
        // on a task, and its one synchronous path is the `DODONA_NO_AUTOSTART` skip), and the ask
        // then renders whatever the store now says — including "no review ran (...)" when that
        // skip is what just happened. Ordering them the other way would leave that case reading
        // "no review has run" for ever, and making the ask wait for a verdict would gate the
        // operator's approval on a model having answered, which is the one thing R6 may not do.
        // Each is in its OWN try: the record is already written, and neither the reviewer nor the
        // question is allowed to take the other down.
        if (written is not null)
        {
            try { ManagerReview(t, laneId, repo.Path, written); }
            catch (Exception ex) { _store.Event("manager_review_failed", laneId, $"ticket {tid}: firing the review threw: {ex.Message}"); }
            try { AskToLand(t, laneId, written); }
            catch (Exception ex) { _store.Event("land_ask_failed", laneId, $"ticket {tid}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>The D-R13 gate's value: 16 hex over the branch tip plus the porcelain status, so
    /// committed and uncommitted work both move it. Short because it is read by people in event
    /// details, and a full SHA256 in a log line is noise.</summary>
    static string Digest(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>The digest out of a stored record's detail, or "" when it cannot be read. A
    /// record whose digest is unreadable must compare UNEQUAL, so the next turn writes a fresh
    /// record rather than skipping on a value nobody could parse -- one duplicate record is a
    /// cost, a gate that silently swallows every completion is a phase that does nothing.</summary>
    static string DigestOf(string detail)
    {
        var brace = detail.IndexOf('{');
        if (brace < 0) return "";
        try
        {
            using var d = JsonDocument.Parse(detail[brace..]);
            return d.RootElement.TryGetProperty("digest", out var g) ? g.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    // ------------------------------------------ the manager's review (R5, D-R9/D-R10/D-R12)

    /// <summary>How many times one ticket may be sent back before it goes to the operator
    /// regardless (D-R12). An unbounded send-back loop is CLAUDE.md §0.1's *never stuck*
    /// violated in a costume where everyone is being reasonable.</summary>
    const int SendBackBound = 3;

    /// <summary>
    /// The manager reads R4's completion record and MAY SEND THE WORK BACK
    /// (`docs/REVIEW-AND-MERGE-PLAN.md` D-R9). This is the chair R3 left empty: the file
    /// reservations were retired on the operator's reasoning that *"if that is problematic in
    /// some way, it's the manager's job to say something about it"*, and until this method
    /// existed `claim_overlap` and `branch_touched` were recorded and nothing read either.
    ///
    /// **IT CAN BLOCK, BUT IT CANNOT BLESS (D-R10) — the load-bearing rule of the whole plan.**
    /// There is deliberately no path from here to `Store.TicketApprove`, and `case "approve"`
    /// stays reachable only from the operator's own `approve` / `dodona ui answer`. Rejection is
    /// free and reversible — worst case it costs a round; approval advances a ref that has no
    /// undo. A model as the sole gate on that step is *a prompt providing safety*, which
    /// `WORK-ISOLATION-PLAN` §2 forbids and which this phase is not allowed to reintroduce just
    /// because the model is called a manager. So the schema offers `ok | send-back` and nothing
    /// else, **anything that is not literally `send-back` is read as no objection**, and `ok`
    /// grants nothing — `brain:a_manager_approval_grants_nothing` asks for `approve` on purpose
    /// and watches it change nothing at all.
    ///
    /// **BOTH ENDS ARE BOUNDED (D-R12).** Reading: the diffstat, the changed-file NAMES and the
    /// agent's own report, on the cheap tier, escalating to the expensive one only when the
    /// cheap tier says its own confidence is low — the pattern `BrainReview` already uses. Never
    /// the diff CONTENT, which plan §9 rejects by name: a reviewer that reads every diff in full
    /// is a reviewer that cannot be afforded (CLAUDE.md §0.1). Loop: three send-backs, then the
    /// ticket goes to the operator with the history attached and no fourth model call.
    ///
    /// **THE BOUND IS COUNTED IN THE STORE, NEVER IN MEMORY** (`Store.CountTicketEvents`). A
    /// daemon restarts on every publish, so a field or a counter would reset the bound at
    /// exactly the moment three rounds have gone by — the same reason R4 reads its previous
    /// digest back out of its own event, and §3's dead routing ladder wearing a third costume.
    /// `brain` restarts the daemon between round two and round three for that reason and no
    /// other.
    ///
    /// **A SEND-BACK CANNOT REVIEW ITSELF, and D-R13's gate is what guarantees it.** The
    /// send-back is delivered with `Say`, which starts a turn, which ends, which arrives back at
    /// `CompletionRecord`. That turn has not moved the worktree, so the digest matches, so there
    /// is no record and therefore no second review. The loop terminates on a fact rather than on
    /// a model choosing to stop — which is what "bounded" has to mean here.
    /// </summary>
    void ManagerReview(Store.TicketRow t, long laneId, string project, string recordJson)
    {
        var tid = t.Id;
        // THE DAEMON ACTING ON ITS OWN INITIATIVE, not on operator input — so it honours the same
        // guard as the startup warm-up, the drift watcher and `EnsureRouterAsync`, and all four
        // now agree on what "do not start things by yourself" means. This is not a test hook:
        // without it every model-free suite that finishes a ticket turn would spawn a real
        // `claude -p --model haiku`, because a fixture with no `agent` in its dodona.json gets
        // the real CLI by default (m1 is exactly that) — the one thing a model-free suite may
        // never do (CLAUDE.md §3.2's incident). The operator never sets it; `brain-acceptance`
        // clears it for this phase's checks, the way it already does for the classifier's.
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1")
        {
            _store.Event("manager_review_skipped", laneId,
                $"ticket {tid}: DODONA_NO_AUTOSTART=1, so this daemon starts no judgement agent of its own");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // ALREADY AT THE BOUND: no model call, and the operator hears about it ONCE.
                // D-R12's "then it goes to the operator regardless" is not "then it asks a
                // fourth time", and an announcement on every later turn would be the never-stuck
                // fix turning into never-quiet — so the announcement is gated on its own event,
                // in the store, the same way the bound itself is counted.
                var rounds = _store.CountTicketEvents(tid, "manager_sent_back");
                if (rounds >= SendBackBound)
                {
                    if (_store.CountTicketEvents(tid, "manager_bound_reached") == 0)
                    {
                        var history = SendBackHistory(tid);
                        _store.Event("manager_bound_reached", laneId,
                            $"ticket {tid}: {rounds} send-backs is the bound (D-R12) — to the operator, history: {Truncate(history, 900)}");
                        Announce(t, $"manager review: ticket {tid} '{t.Title}' has been sent back {rounds} times, which is the bound — " +
                                    $"it is yours to judge now. What the manager asked for, in order: {Truncate(history, 600)}");
                    }
                    return;
                }

                // ENSURE AT THE POINT OF USE, NEVER LOOK UP (CLAUDE.md §3). A lookup that misses
                // is indistinguishable from one that was never going to hit, which is how the
                // routing ladder stayed fully green and dead in production for two days.
                var loId = await EnsureBrainAsync(hi: false, project);
                if (loId < 0)
                {
                    // A review that could not run SAYS SO. A check that quietly does nothing is
                    // worse than no check, and "judgement is switched off for this project" must
                    // not look identical to "the review is broken" from the store
                    // (`completion_record_impossible` is the pattern being copied).
                    _store.Event("manager_review_skipped", laneId,
                        $"ticket {tid}: no judgement agent for {project} — brain off in dodona.json, failed to start, or the maxBrains cap");
                    return;
                }

                var q = ManagerQuestion(t, recordJson, rounds);
                var gate = BrainLock(loId);
                await gate.WaitAsync();
                string? reply;
                try { reply = await _lanes[loId].AskAsync(q, 25000); }
                finally { gate.Release(); }
                if (reply is null)
                {
                    _store.Event("manager_review_failed", laneId, $"ticket {tid}: the cheap tier did not answer in 25s");
                    return;
                }
                JsonElement v;
                try { v = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]).RootElement.Clone(); }
                catch
                {
                    _store.Event("manager_review_failed", laneId, $"ticket {tid}: unparseable reply: {Truncate(reply, 160)}");
                    return;
                }

                // Cheap tier unsure -> the SAME question, expensive tier (D-R12's bound on
                // reading, and the operator's rule #1). Which tier answered is a FIELD of the
                // one review row rather than an event of its own: one review, one row, and R6
                // reads that row.
                var conf = v.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                var tier = "lo";
                if (conf == "low")
                {
                    var hiV = await AskBrainHiAsync(q, project);
                    if (hiV is not null)
                    {
                        v = hiV.Value;
                        tier = "hi";
                        conf = v.TryGetProperty("confidence", out var cf2) ? cf2.GetString() ?? "low" : "low";
                    }
                }

                // D-R10 IN ONE LINE: `send-back` is the only verdict that does anything. There is
                // no branch below this that grants, approves or advances anything, and a reply of
                // `{"verdict":"approve"}` lands here as no objection.
                var verdict = v.TryGetProperty("verdict", out var vd) ? vd.GetString() ?? "ok" : "ok";
                var sendBack = verdict == "send-back";
                var note = v.TryGetProperty("note", out var nt) ? Truncate(nt.GetString() ?? "", 240) : "";
                var message = v.TryGetProperty("message", out var mg) ? Truncate(mg.GetString() ?? "", 1200) : "";

                // THE WRITE-UP IS THE POINT, NOT THE VERDICT (D-R11): R6 renders `note` in the
                // approval ask so the operator's yes is a two-second decision instead of a
                // diff-reading session. So the row is written whatever the verdict, in the same
                // `ticket <id> {json}` shape R4's record uses — `LastTicketEvent` finds it and R6
                // parses it.
                var row = JsonSerializer.Serialize(new
                {
                    ticket = tid,
                    verdict = sendBack ? "send-back" : "ok",
                    asked = verdict,        // what the model actually said, including `approve`
                    confidence = conf,
                    tier,
                    round = rounds + 1,
                    bound = SendBackBound,
                    note,
                    message,
                });
                _store.Event("manager_review", laneId, $"ticket {tid} {row}");
                if (!sendBack) return;                 // silent on agreement (operator's rule #3)

                var text = message.Length > 0 ? message : note;
                if (text.Length == 0)
                {
                    // A send-back with nothing to say cannot be delivered, and quietly treating
                    // it as agreement would be a block that evaporated.
                    _store.Event("manager_review_failed", laneId,
                        $"ticket {tid}: verdict send-back with no message and no note — nothing to send, so nothing was sent");
                    return;
                }
                await SendBackAsync(t, laneId, rounds + 1, text, note);
            }
            catch (Exception ex) { _store.Event("manager_review_failed", laneId, $"ticket {tid}: {ex.GetType().Name}: {ex.Message}"); }
            // R6 (D-R11): the note is written FOR THE OPERATOR, so every way out of this method
            // ends by refreshing the question the operator is deciding in. A `finally` rather
            // than a line beside each `return` on purpose — there are six exits (the bound, no
            // judgement agent, no answer in 25 s, an unparseable reply, agreement, a delivered
            // send-back) and a rule that has to be remembered at six sites is a rule that gets
            // skipped at the seventh. The ask itself already exists by now, opened by
            // `BuildRecord`, so this only ever changes what it SAYS.
            finally
            {
                try { AskToLand(t, laneId, recordJson); }
                catch (Exception ex) { _store.Event("land_ask_failed", laneId, $"ticket {tid}: after the review: {ex.Message}"); }
            }
        });
    }

    /// <summary>The question, and D-R12's bound on reading is IN it: the diffstat, the
    /// changed-file NAMES and the agent's own report — never the diff content, which plan §9
    /// rejects by name.
    ///
    /// **TWO OF THE RECORD'S FIELDS WOULD MAKE A NAIVE REVIEWER BLOCK EVERY TICKET, so the
    /// prompt says what they mean out loud.** `verify.state = not-run` is the NORMAL value and it
    /// is correct — D-R15: the verify that gates is the land's own, on the result of merging main
    /// in, and one run here would answer a different question while reading as though it did not.
    /// `drop.state = moot` means main has not been merged into the branch yet, not that a check
    /// failed. A manager that read either as red would send every ticket back forever, which is
    /// D-R12's infinite politeness arriving through the front door.
    ///
    /// The history goes in too, so round three does not repeat round one — the same rows the
    /// operator gets at the bound.</summary>
    string ManagerQuestion(Store.TicketRow t, string recordJson, int rounds)
    {
        static string S(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var x) ? x.ToString() : "";
        using var d = JsonDocument.Parse(recordJson);
        var r = d.RootElement;
        var changed = r.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? string.Join(", ", ch.EnumerateArray().Take(40).Select(x => x.GetString())) : "";
        var verify = r.TryGetProperty("verify", out var vv) ? vv : default;
        var drop = r.TryGetProperty("drop", out var dd) ? dd : default;
        var hist = SendBackHistory(t.Id);
        return "Review the completed work on a ticket and decide whether to SEND IT BACK.\n" +
               $"Ticket {t.Id}: {t.Title}   branch {t.Branch}\n" +
               $"Files changed ({S(r, "files")}): {changed}\n" +
               $"Diffstat:\n{Truncate(S(r, "diffstat"), 1500)}\n" +
               $"Uncommitted files in the worktree: {S(r, "uncommitted")}\n" +
               $"Verify: {S(verify, "state")} — `not-run` is the NORMAL value and it is CORRECT: no verify has run " +
               "yet, and the one that gates runs at land time on the result of merging main in. Never send work back " +
               "for that, and never for missing tests.\n" +
               $"Silent-drop check: {S(drop, "state")} — `moot` means main has not been merged into the branch yet, " +
               "which is the ordinary state before a land. `dropped` means the branch discarded something main " +
               $"changed, and THAT is worth raising: {S(drop, "files")}\n" +
               $"The agent's own report of the turn it just finished:\n{Truncate(S(r, "report"), 2500)}\n" +
               (hist.Length > 0
                   ? $"You have already sent this ticket back {rounds} time(s): {Truncate(hist, 800)}\n" +
                     "Do not repeat a point it has already dealt with.\n"
                   : "") +
               "You may BLOCK and you may NOT BLESS: you cannot approve anything, and `ok` grants nothing — the " +
               "operator's approval is the only yes and it is not yours to give. Send work back only for something " +
               "real: work that does not match the ticket, a change the report does not mention, a discarded file, " +
               "a schema or interface change slipped in quietly. Not for style.\n" +
               "Reply ONLY one line of JSON, no prose, no markdown, no code fence: " +
               "{\"verdict\":\"ok|send-back\",\"confidence\":\"high|medium|low\"," +
               "\"note\":\"<=200 chars, written for the operator deciding whether to merge\"," +
               "\"message\":\"<what to tell the agent, only when send-back>\"}";
    }

    /// <summary>What the manager has already asked for on this ticket, oldest first — D-R12's
    /// "with the history attached". Read out of the events because they are the only copy: no
    /// field, no column, and it survives the publish that restarts the daemon.</summary>
    string SendBackHistory(long tid)
    {
        var parts = new List<string>();
        foreach (var (_, _, detail) in _store.TicketEvents(tid, SendBackBound + 1, "manager_sent_back"))
        {
            var brace = detail.IndexOf('{');
            if (brace < 0) continue;
            try
            {
                using var d = JsonDocument.Parse(detail[brace..]);
                var round = d.RootElement.TryGetProperty("round", out var rd) ? rd.ToString() : "?";
                var said = d.RootElement.TryGetProperty("message", out var mg) && mg.GetString() is { Length: > 0 } m
                    ? m
                    : d.RootElement.TryGetProperty("note", out var nt) ? nt.GetString() ?? "" : "";
                parts.Add($"({round}) {said}");
            }
            catch (JsonException) { }      // a row we cannot read is worth less than the ones we can
        }
        return string.Join("  ", parts);
    }

    /// <summary>Deliver the manager's objection to the lane AS INPUT — `LaneRuntime.Say`, the
    /// same path a typed sentence takes, so the agent keeps its warm context and simply carries
    /// on (D-R9). That is what makes "request changes" cheap here: the lane is a thread, not a
    /// pull request. `Say` also writes the pane's `user_input` row, so the operator sees the
    /// send-back exactly where they see their own sentences, and the `[manager review …]` prefix
    /// is what tells the two apart — no announcement, deliberately, because a machine handling
    /// its own round is not a person being needed (§4).
    ///
    /// **IT MUST NOT VANISH.** `Say` throws when the lane is not connected, and a send-back that
    /// disappeared would be the silent-failure class this codebase pays for most. So this waits
    /// for the lane to come back — a shim reconnecting, or a reconcile adopting it — and if it
    /// does not, records the whole message and puts it in front of the operator with the two
    /// commands that deliver it by hand. It does NOT respawn from here: that is forty lines the
    /// `lane-respawn` handler already owns (project ownership, the lane's own config, the ticket
    /// system prompt, the resume args), and a second implementation of it would drift on exactly
    /// the cases that matter — `MakeTicket`'s lesson. An undelivered send-back also counts as NO
    /// ROUND against the bound, because nothing was said.</summary>
    async Task SendBackAsync(Store.TicketRow t, long laneId, int round, string message, string note)
    {
        var text = $"[manager review, round {round} of {SendBackBound}] {message}" + "\n\n" +
                   "This is a review of the turn you just finished, not a new task. Address it on this branch and " +
                   "commit; nothing has been merged and nothing has been approved.";
        // A CONDITION WITH A DEADLINE, never a sleep — CLAUDE.md §3's rule for waits, in code. The
        // turn that produced this record came off this lane's own wire, so it is normally
        // connected already and this loop runs exactly once; 20 s covers a shim reconnecting or a
        // reconcile adopting it, and what un-sticks the wait is named in the refusal below.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_lanes.TryGetValue(laneId, out var rt) && rt.Connected)
            {
                try
                {
                    // The event is written AFTER the write to the wire, because what it records
                    // is a send-back that was DELIVERED — a round burned on a message the lane
                    // never received would be the bound eating the agent's chances silently.
                    rt.Say(text);
                    _store.Event("manager_sent_back", laneId, $"ticket {t.Id} " + JsonSerializer.Serialize(new
                    {
                        ticket = t.Id, lane = laneId, round, bound = SendBackBound, note, message,
                    }));
                    return;
                }
                catch (InvalidOperationException) { }   // it dropped between the check and the write: keep waiting
            }
            await Task.Delay(250);
        }
        _store.Event("manager_send_back_undelivered", laneId, $"ticket {t.Id} " + JsonSerializer.Serialize(new
        {
            ticket = t.Id, lane = laneId, round, note, message,
        }));
        Announce(t, $"manager review: ticket {t.Id} '{t.Title}' should go back to its agent, but lane {laneId} has not " +
                    $"answered for 20s — `dodona lane-respawn {laneId}`, then `dodona say {laneId} \"{Truncate(message, 200)}\"`. " +
                    "It was NOT delivered and it counts as no round against the bound.");
    }

    // ------------------------------------------------------------- the input classifier (§4)

    /// <summary>The router is a mechanical classifier, not a thinker: cheap model, low
    /// effort, deliberately not the project's lane policy (§9's ladder — spend where
    /// judgement compounds, and this is not where it compounds).
    ///
    /// The operator's routing policy, verbatim intent (2026-08-18): a GENERIC remark
    /// ("don't do that", "stop", "try again") belongs to the focused lane, full stop — no
    /// cleverness. A remark CLEARLY AIMED by content ("make the skybox red") goes to the
    /// lane it names, cheap thought. Only text that is neither obviously generic nor
    /// obviously aimed earns expensive thought (the brain's high tier picks it up).
    ///
    /// Four verdicts (WORKSPACES-CONCIERGE.md §5). The old vocabulary was
    /// generic|specific|unclear, where `specific` meant "an existing lane's title" — so no
    /// rung of the ladder could ever answer "this deserves a fresh lane", and while any lane
    /// was alive every input was a continuation of something.
    ///
    /// The tie-break and its REASON are both in the prompt deliberately: the cheap model has
    /// to know WHY the tie breaks toward new-task, or it will break it the other way
    /// whenever the input is vague.</summary>
    const string RouterPrompt =
        "You are Dodona's input router. You are given some lanes (each an agent working on " +
        "something), which lane the operator is looking at, and one line of operator input. " +
        "You decide where it goes. Reply with ONLY one line of JSON, no prose, no markdown: " +
        "{\"kind\":\"generic|addendum|new-task|unclear\",\"target\":\"<LANE TITLE, for addendum only>\"," +
        "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars, say WHY\"}\n" +
        "kind=generic — the remark could apply to any ongoing work: stop, no, try again, yes, " +
        "an acknowledgement, a correction naming no subject. It belongs to the FOCUSED lane. " +
        "target must be omitted.\n" +
        "kind=addendum — it continues an existing lane's thread. Two ways that happens, and both " +
        "are common: it is aimed at what that lane is doing NOW (reason: direct), or it is a small " +
        "correction or refinement of what that lane JUST FINISHED (reason: tweak). target names " +
        "that lane.\n" +
        "kind=new-task — a distinct piece of work. It gets its own fresh lane. Do not name a target.\n" +
        "kind=unclear — you genuinely cannot tell. Say so; someone with more budget looks, and " +
        "then the operator is asked. Nothing is delivered meanwhile, so unclear is SAFE.\n" +
        "WHEN TORN BETWEEN addendum AND new-task, CHOOSE new-task. Here is why, and it should " +
        "change how you weigh it: a wrong new lane costs one command to undo and pollutes nothing. " +
        "A wrong addendum cannot be undone at all — the agent has already been told, may already " +
        "be acting, and its context is spoiled. Prefer the mistake that is free.\n" +
        "But do not overcorrect: an operator interrupting a working agent is completely normal, " +
        "and the length of the input tells you nothing about which kind it is. What tells you is " +
        "the SUBJECT — does the input concern what that lane is about, or something else?\n" +
        "Be willing to say unclear or confidence low — an honest unsure is cheap here, and a " +
        "confident wrong guess is the one error that cannot be taken back.\n" +
        // TWO QUESTIONS, ONE WARM SESSION. Phase 3 asks this same classifier a second, narrower
        // question -- which PROJECT a new lane opens in -- and a system prompt that described only
        // the four verdicts would fight it: a cheap model told "reply with ONLY that JSON" answers
        // in that schema whatever it is asked. Naming both question shapes here, each by the first
        // line it arrives with, is what keeps one warm session honest for both. A second router
        // lane would be a second `claude -p` per workspace for one extra sentence of prompt
        // (CLAUDE.md 0.1: quota is the scarce resource).
        "SOMETIMES YOU ARE ASKED A DIFFERENT QUESTION. If the input begins \"" + ProjectQuestionLead +
        "\", answer THAT question in the schema it asks for instead of the one above.";

    /// <summary>The first line of the project question, and the marker that tells the classifier
    /// (and the fake agent) which of the two questions it is being asked. One constant so the
    /// prompt that WARNS about it and the question that SENDS it cannot drift.</summary>
    internal const string ProjectQuestionLead = "Choose which PROJECT a new lane for this input should open in.";

    /// <summary>Start a classifier and remember it. Separate from EnsureRouterAsync so
    /// `router-start` can force a fresh one with a different child or model.</summary>
    async Task<(long Id, string Msg)> SpawnRouterAsync(string child, string model, string effort)
    {
        var args = IsClaude(child) ? ClaudeArgs(_config, model, effort, RouterPrompt, acceptEdits: false, utility: true) : new List<string>();
        var (id, msg) = await SpawnLaneAsync("ROUTER", "router", NeutralCwd(), child, args);
        if (id < 0) { _store.Event("router_failed", null, msg); return (-1, msg); }
        _routerLo = id;
        return (id, msg);
    }

    /// <summary>The classifier, CREATED AT THE POINT OF USE — the shape EnsureBrainAsync
    /// already had, and the reason this exists.
    ///
    /// RouteInput used to look the classifier up by role and fall back when it found
    /// nothing. Nothing in the daemon ever created a lane with that role: the startup
    /// warm-up and `brain-start` both make `brain`, and the ONLY producer of `router` was
    /// the manual command above — whose only caller in the whole tree was
    /// tests/brain-acceptance.ps1. So the suite proved the routing ladder on a wiring the
    /// real daemon never took, and every sentence the operator ever typed took the
    /// `no-classifier` fallback instead. Measured on the operator's own store: 14 routed
    /// inputs, every one `tier=focus confidence=no-classifier`, ZERO `classified` events,
    /// ZERO router lanes ever created — across two days, while `dodona status` cheerfully
    /// printed `router: model=haiku effort=low` for a lane that had never existed.
    ///
    /// A lookup can miss silently. A create cannot. That is the whole change: after this,
    /// "no classifier" means the brain is switched off in config or the spawn actually
    /// failed — both of which now say so out loud.</summary>
    async Task<long> EnsureRouterAsync()
    {
        if (_routerLo > 0 && _lanes.TryGetValue(_routerLo, out var live) && live.Connected) return _routerLo;
        if (!_config.Brain) return -1;                 // judgement is off by config: honour it
        // Suites own every lifetime themselves and assert the model-free fallback path;
        // start-on-demand must not join in (the same guard the drift watcher and the
        // startup warm-up use, so all three agree on what "don't start things" means).
        if (Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1") return -1;
        // LAST of the cheap refusals, because it is the only one that does I/O (P3.5).
        if (!await ClearOfLivePredecessorsAsync(null, "router")) return -1;
        return (await SpawnRouterAsync(_config.Agent, _config.RouterModel, _config.RouterEffort)).Id;
    }

    // ------------------------------------------------------------- the dispatcher brain (§3)

    /// <summary>P3.5 -- ADOPTION FAILURE IS NOT A SPAWN TRIGGER.
    ///
    /// Every Ensure* above asks "is my lane in _lanes and connected?" and treats no as permission
    /// to start another one. But "I could not adopt it" and "it is not there" were the same
    /// branch, and only the second one justifies a spawn. Measured on the operator's own
    /// instance: 14 BRAIN lanes, one per daemon start across a morning of auto-publish swaps,
    /// each an idle `claude -p` process nobody could reach -- the predecessor sat there
    /// connected-to-nothing while its replacement was started beside it, fourteen times.
    ///
    /// So before spawning: if a lane of this role has a pipe that is still in the OS namespace,
    /// there is a live process. Tell it to go and wait for the name to leave. Only then is the
    /// road clear. If it will not go, REFUSE to spawn and say so -- one degraded call that
    /// announces itself is cheaper than a second orphan, and the next call retries.</summary>
    /// <param name="project">The registration being cleared, or null for "this role is
    /// per-workspace, so any lane of it is a predecessor". WITHOUT THIS FILTER ONE WEDGED BRAIN
    /// BLOCKED EVERY PROJECT (P5.4): the roles all share the name `brain`, so a shim in project A
    /// that would not let go of its pipe made this return false for project B, project C and
    /// every project after them, for the life of the daemon — and B never had a predecessor at
    /// all. A refusal that is correct for one project and nonsense for the next is worse than no
    /// refusal, because it announces itself as a safety measure.</param>
    async Task<bool> ClearOfLivePredecessorsAsync(string? project, params string[] roles)
    {
        var projects = ProjectPaths();
        var candidates = _store.LanesAll()
            .Where(l => roles.Contains(l.Role) && l.State is "alive" or "unreachable")
            .Where(l => project is null || string.Equals(RegistrationKey(l, projects), project, StringComparison.OrdinalIgnoreCase))
            .Where(l => !(_lanes.TryGetValue(l.Id, out var rt) && rt.Connected))
            .Where(l => l.Pipe is { Length: > 0 })
            .ToList();
        if (candidates.Count == 0) return true;

        var live = LaneLiveness.Live(_instanceId, Paths.WorkspaceDir(_instanceId));
        var clear = true;
        foreach (var l in candidates.Where(l => live.Contains(l.Id)))
        {
            // BOUNDED, NOT ONCE (P5.4). This runs from EnsureRouterAsync, which is on the path of
            // every routed sentence the operator types -- a poke plus a wait on each of them
            // would be seconds of latency per keystroke-to-lane, paid forever, for a message the
            // shim has already declined. So it is not asked on every call.
            //
            // But it used to be asked exactly ONCE, ever: `_shutdownAsked` was a HashSet with an
            // Add and no Remove and no Clear anywhere in the file (verified -- two references in
            // the whole tree, the Add and the declaration). So a shim that declined the first
            // `##shutdown` and would have accepted the second was never asked again, and the
            // refusal below stood for the life of the daemon with nothing but an operator running
            // `stop-all --lanes` to un-stick it. A wait has to name the thing that clears it
            // (CLAUDE.md §0.1) and "a person notices" is not that thing.
            //
            // Three attempts per lane per daemon: still nowhere near per-sentence cost, and now
            // self-healing for the ordinary case of a shim that was mid-handover.
            var asked = _shutdownAttempts.TryGetValue(l.Id, out var prev) ? prev : 0;
            if (asked >= ShutdownAttemptLimit) { clear = false; continue; }
            _shutdownAttempts[l.Id] = asked + 1;
            var told = await LaneRuntime.ShutdownShimAsync(l.Pipe!);
            var gone = told && await LaneRuntime.WaitPipeGoneAsync(l.Pipe!);
            _lanes.TryRemove(l.Id, out _);           // whatever we had, it is not usable
            if (gone) { _store.LaneState(l.Id, "dead"); }
            else clear = false;
            _store.Event("utility_predecessor_live", l.Id,
                $"role={l.Role}: pipe {l.Pipe} was still live, so a replacement would have been a " +
                $"second orphan; " + (gone ? "shut it down, spawning now" : told ? "sent ##shutdown, pipe still there -- refusing to spawn this time" : "##shutdown could not be delivered -- refusing to spawn this time"));
        }
        if (!clear) Announce("[dodona] a previous utility agent will not let go of its pipe; not starting a second one. " +
                             "`dodona ps` shows it; `dodona stop-all --lanes` clears it.");
        return clear;
    }

    /// <summary>How many times each lane has been told to go. See the loop above for why asking
    /// on every call is not free, and why asking exactly once was a wait with nothing to
    /// un-stick it.</summary>
    readonly Dictionary<long, int> _shutdownAttempts = new();
    const int ShutdownAttemptLimit = 3;

    /// <summary>The middle rung of the escalation ladder: management judgement between
    /// code-that-checks-facts and the operator-who-decides-intent. Two warm sessions —
    /// cheap for the everyday calls, expensive only when the cheap one says it is not
    /// sure (operator's rule). It is deliberately kept AWAY from code: neutral cwd, no
    /// project CLAUDE.md, no skills, no tools it could run — its whole world is the
    /// management question in front of it.</summary>
    /// <summary>The `<project-leaf>=<lane>` list `reconcile_done` and `status` print for one
    /// tier. The LEAF, not the whole path, because this is a line a person reads and the lane id
    /// beside it is already unambiguous; `-` for a tier with none.</summary>
    static string BrainList(IReadOnlyDictionary<string, long> tier) =>
        tier.Count == 0 ? "-"
        : string.Join(",", tier.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                               .Select(kv => $"{Path.GetFileName(kv.Key.TrimEnd('\\', '/'))}={kv.Value}"));

    /// <summary>
    /// Which project a brain request is FOR (P5.3). Nothing requested means the workspace's
    /// first project, which is byte-for-byte what "the brain" meant before this phase and is
    /// what keeps a one-project workspace identical. A folder inside a project resolves up to
    /// the project, so a caller passing an agent's cwd gets the right registration.
    ///
    /// An unowned folder falls back to the first project rather than refusing, deliberately, and
    /// this is the one place in Phase 5 that substitutes instead of refusing: the brain is an
    /// improver and never a gate, so "I could not tell which project, so you get no judgement at
    /// all" is a worse answer than "you get the workspace's default brain". `lane-start` refuses
    /// in the same situation because it would put an ungated AGENT in a folder nothing tracks
    /// (trap T7); a brain runs in the neutral directory and touches no project's files at all.
    /// </summary>
    string BrainProject(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return _primary;
        return Projects.Of(ProjectPaths(), Instance.Canonical(requested!)) ?? _primary;
    }

    /// <summary>How many brain sessions exist right now, across every project and both tiers —
    /// the number the cap is measured against (P5.7). Counting ROWS and not pointers: a lane
    /// this daemon failed to adopt is still two OS processes, and a cap that could not see them
    /// would be a cap on bookkeeping rather than on the machine.</summary>
    int BrainLaneCount() =>
        _store.LanesAll().Count(l => l.State is "alive" or "unreachable" && l.Role is "brain" or "brain-hi");

    /// <summary>The lock for one brain session, created on demand. On demand rather than only at
    /// spawn because an ADOPTED brain arrives without one, and a brain with no lock would run
    /// two questions down one `claude -p` stdin at once — which is not a slow answer, it is two
    /// interleaved ones.</summary>
    SemaphoreSlim BrainLock(long id) => _brainLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    async Task<long> EnsureBrainAsync(bool hi, string? project = null)
    {
        // ONE PROJECT'S TIER, NOT "THE" TIER (P5.3). These were two scalars, so once a project
        // parameter existed at all, `EnsureBrainAsync` for project A would have returned project
        // B's session -- whichever had been created last -- and B's brain would then be asked A's
        // questions about A's lanes. Verified against the source before it was changed: the
        // adoption loop assigned `_brainLo = l.Id` unconditionally for every brain row it
        // adopted, so with two brains the scalar held the last one iterated and both projects
        // resolved to it.
        var key = BrainProject(project);
        var tier = hi ? _brainHi : _brainLo;
        if (tier.TryGetValue(key, out var current) && _lanes.TryGetValue(current, out var live) && live.Connected) return current;
        // Config from THE PROJECT (T2/P2.3's reasoning, applied to judgement): a project may
        // switch its brain off, or point it at a different model or a different agent binary.
        // For a one-project workspace this reads the same file `_config` came from, so nothing
        // about that case moves.
        var pcfg = ConfigForProject(key);
        if (!pcfg.Brain) return -1;
        // THE CAP REFUSES; IT NEVER EVICTS (P5.7). Making room by shutting an existing brain
        // down is the count-and-kill loop growing back somewhere else, and it would stop a
        // session that is mid-question to start one that is not. And it is not silent: a
        // project with no judgement says so once and names the setting that lifts it, because a
        // silent degrade is a bug (CLAUDE.md §3's two dead routing days).
        if (BrainLaneCount() >= Math.Max(1, _config.MaxBrains))
        {
            _store.Event("brain_cap_reached", null,
                $"maxBrains={_config.MaxBrains} reached ({BrainLaneCount()} brain lane(s) live); no {(hi ? "brain-hi" : "brain")} for {key}");
            if (!_saidBrainCap)
            {
                _saidBrainCap = true;
                Announce($"[dodona] the brain cap is reached (maxBrains={_config.MaxBrains}): {key} gets no judgement agent, so its " +
                         "routing and naming fall back to code. Raise `maxBrains` in dodona.json, or stop a brain you are not using " +
                         "(`dodona status` lists them per project).");
            }
            return -1;
        }
        if (!await ClearOfLivePredecessorsAsync(key, hi ? "brain-hi" : "brain")) return -1;

        var sys = "You are Dodona's dispatcher brain. You make MANAGEMENT decisions for a multi-agent " +
                  "orchestrator: what a piece of work should be called, which lane an input belongs to, whether work " +
                  "deserves its own ticket and which paths that ticket should claim. You never read or write code, " +
                  "never run tools, and never do the work yourself — you are the coordinator's judgement, not a worker. " +
                  "Answer ONLY in the single-line JSON schema each request specifies: no prose, no markdown, no code fences. " +
                  "State your confidence honestly — saying low is how hard questions reach someone with more budget than you.";
        var model = hi ? pcfg.Model : pcfg.BrainModel;
        var effort = hi ? pcfg.Effort : pcfg.BrainEffort;
        var args = IsClaude(pcfg.Agent) ? ClaudeArgs(pcfg, model, effort, sys, acceptEdits: false, utility: true) : new List<string>();
        // NeutralCwd, and `key` as the SCOPE -- P5.8, and the distinction the whole phase rests
        // on. Per-project means SCOPED TO a project, never RUNNING IN one: a manager started
        // inside a project loads that project's CLAUDE.md and skills, i.e. a judgement agent
        // that can end up running `/ship` (commit 19dad3d). Do not "fix" this by passing `key`
        // as the working directory -- they are two arguments because they are two facts.
        var (id, msg) = await SpawnLaneAsync(hi ? "BRAIN-HI" : "BRAIN", hi ? "brain-hi" : "brain",
                                             NeutralCwd(), pcfg.Agent, args, scope: key);
        if (id < 0) { _store.Event("brain_failed", null, msg); return -1; }
        tier[key] = id;
        BrainLock(id);
        return id;
    }

    /// <summary>Ask the expensive tier (spawning it on first use). Null when the brain is
    /// off, failed to start, or timed out — callers treat null as "the status quo stands",
    /// because the brain is an improver, never a gate.</summary>
    async Task<JsonElement?> AskBrainHiAsync(string question, string? project = null)
    {
        var id = await EnsureBrainAsync(hi: true, project);
        if (id < 0) return null;
        var gate = BrainLock(id);
        await gate.WaitAsync();
        string? reply;
        try { reply = await _lanes[id].AskAsync(question, 30000); }
        finally { gate.Release(); }
        if (reply is null) { _store.Event("brain_timeout", id, Truncate(question, 120)); return null; }
        try
        {
            var doc = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]);
            return doc.RootElement.Clone();
        }
        catch { _store.Event("brain_failed", id, $"unparseable: {Truncate(reply, 120)}"); return null; }
    }

    /// <summary>Post-hoc review of an auto-created lane — the §4 pattern applied to
    /// judgement: code already acted (lane exists, message delivered), the brain runs
    /// BEHIND and corrects visibly. Silent unless it disagrees (operator's rule #3):
    /// a rename is applied and announced as a receipt with its undo; a ticket is only
    /// ever SUGGESTED, because a wrong claim strands an agent behind the gate.</summary>
    void BrainReview(long laneId, string text, string chosenName, Choice choice)
    {
        if (!_config.Brain) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // THE LANE'S OWN PROJECT ASKS ITS OWN PROJECT'S BRAIN (P5.3). The review names
                // the lane, its siblings and the workspace's repositories, so sending it to
                // another project's session would be asking a manager about work it does not
                // manage. A lane with no recorded project resolves to the first one, which is
                // what every lane was before this phase.
                var reviewProject = _store.LanesAll().FirstOrDefault(l => l.Id == laneId) is Store.LaneRow lr
                    ? RegistrationKey(lr, ProjectPaths()) : _primary;
                var loId = await EnsureBrainAsync(hi: false, reviewProject);
                if (loId < 0) return;
                var lanes = string.Join(", ", _store.LanesAll().Where(l => l.Role == "work" && l.State == "alive").Select(l => l.Title));
                var repos = string.Join(", ", Repositories().Select(r => r.Name));
                var q = $"A lane was just auto-created from operator input.\n" +
                        $"Input: {text}\nChosen name: {chosenName} (derived by code)\nModel policy: {choice.Describe}\n" +
                        $"Existing lanes: [{lanes}]\nRepositories in this workspace: [{repos}]\n" +
                        "Reply ONLY one line of JSON: {\"agree\":true|false,\"confidence\":\"high|medium|low\"," +
                        "\"better_name\":\"<ONE WORD, only if the chosen name is bad>\"," +
                        "\"ticket\":{\"title\":\"<name>\",\"claims\":[\"subtree:<path>\"]} (only if this work should be isolated on a branch)," +
                        "\"reason\":\"<=60 chars\"}";

                var loGate = BrainLock(loId);
                await loGate.WaitAsync();
                string? reply;
                try { reply = await _lanes[loId].AskAsync(q, 25000); }
                finally { loGate.Release(); }
                if (reply is null) { _store.Event("brain_timeout", loId, $"review lane {laneId}"); return; }

                JsonElement v;
                try { v = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]).RootElement.Clone(); }
                catch { _store.Event("brain_failed", loId, $"unparseable: {Truncate(reply, 120)}"); return; }

                // Cheap tier unsure → same question, expensive tier (operator's rule #1).
                var conf = v.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                if (conf == "low")
                {
                    _store.Event("brain_escalated", loId, $"review lane {laneId}");
                    var hiV = await AskBrainHiAsync(q, reviewProject);
                    if (hiV is not null) v = hiV.Value;
                }

                var agree = v.TryGetProperty("agree", out var ag) && ag.ValueKind == JsonValueKind.True;
                var reason = v.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                _store.Event("brain_review", laneId, $"agree={agree} conf={conf} reason={reason}");
                if (agree) return;                                     // silent unless disagreeing

                if (v.TryGetProperty("better_name", out var bn) && bn.ValueKind == JsonValueKind.String &&
                    bn.GetString() is { Length: > 0 } newName && !newName.Contains(' ') &&
                    !newName.Equals(chosenName, StringComparison.OrdinalIgnoreCase))
                {
                    var clean = newName.ToUpperInvariant();
                    _store.LaneTitle(laneId, clean);
                    _store.Event("brain_renamed", laneId, $"{chosenName} → {clean}: {reason}");
                    _store.PaneEvent(laneId, "announcement",
                        $"renamed to {clean} by the dispatcher (was {chosenName}) — undo: dodona lane-rename {laneId} {chosenName}",
                        null, null, acked: true);
                }

                if (v.TryGetProperty("ticket", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                    tk.TryGetProperty("title", out var tt) && tt.GetString() is { Length: > 0 } title)
                {
                    var claims = tk.TryGetProperty("claims", out var cl) && cl.ValueKind == JsonValueKind.Array
                        ? cl.EnumerateArray().Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList()
                        : new List<string?>();
                    var cmd = $"dodona ticket-create --title {title.ToUpperInvariant()}" +
                              string.Concat(claims.Select(c => $" --claim {c}"));
                    _store.Event("brain_suggested_ticket", laneId, cmd);
                    _store.PaneEvent(laneId, "announcement",
                        $"dispatcher: this looks ticket-worthy ({reason}) — {cmd}", null, null);
                }
            }
            catch (Exception ex) { _store.Event("brain_failed", null, ex.Message); }
        });
    }

    /// <summary>
    /// Lane granularity (docs/WORKSPACES-CONCIERGE.md §5, mechanism decided by the operator
    /// 2026-08-18): **a distinct task gets its own lane.** New agent, new context, and lanes
    /// are cheap. An existing lane keeps the input only when it clearly continues that thread.
    ///
    /// THE ERROR ASYMMETRY IS THE WHOLE DESIGN, and it is why this method stopped being
    /// optimistic:
    ///   * A WRONG CONTINUATION IS UNRECOVERABLE. `Say` delivers immediately; a later retarget
    ///     re-sends the text to the right lane, but the wrong agent already received it, may
    ///     already be acting on it, and its warm context is polluted. You cannot unsay a
    ///     sentence to an agent.
    ///   * A WRONG NEW LANE IS FREE. `dodona lane-stop N`, nothing polluted, nothing consumed
    ///     but a process spawn.
    ///
    /// So §4's "deliver instantly, correct behind" no longer holds for input that might be new
    /// work: correcting is exactly what is impossible. Nothing is delivered until the cheap
    /// classifier answers (operator's call, 2026-08-18, on ~1s of latency being the honest
    /// price). Two paths stay instant and model-free because they are free to decide in code:
    /// a `LANE:` prefix, and an unmistakable generic.
    ///
    /// The four verdicts:
    ///   generic   — "stop", "no", "try again". Focused lane, never second-guessed.
    ///   addendum  — continues an existing lane. Reason `direct` (talking to its ongoing work)
    ///               or `tweak` (a small correction to what it just finished). Same
    ///               destination, distinguished because the operator named both and the
    ///               distinction is worth having in the data.
    ///   new-task  — distinct work. Spawn and deliver.
    ///   unclear   — escalate to the expensive tier, then to the operator. Deliver NOTHING.
    /// </summary>
    async Task<string> RouteInput(string rawText)
    {
        // The operator's override is dispatch syntax, not content — strip it before the
        // sentence reaches any agent.
        var (text, ovModel, ovEffort) = Policy.StripOverrides(rawText);

        var work = _store.LanesAll().Where(l => l.Role == "work" && l.State == "alive").ToList();

        // ---- tier 0: an explicit prefix names its target. Code only, instant. ------------
        // `\s+`, not `\s*`: the documented form is `LANE: text`, and requiring the space
        // stops a colon inside the sentence being read as a target. Found by a test whose
        // directive `routekind:` became a LANE TITLED "ROUTEKIND", after which every later
        // `routekind:...` line was silently delivered to it as a tier-0 prefix — and the same
        // shape bites for real with a lane called HTTP and a sentence containing `http://`.
        var prefix = LanePrefix(text);
        if (prefix is not null)
        {
            var lane = work.FirstOrDefault(l => l.Title.Equals(prefix.Value.Target, StringComparison.OrdinalIgnoreCase));
            if (lane is not null && _lanes.TryGetValue(lane.Id, out var rt0))
            {
                rt0.Say(prefix.Value.Body);
                _store.RoutingInsert(text, "prefix", lane.Id, lane.Id, "explicit");
                return $"-> {lane.Title} (tier 0)";
            }
        }

        var live = work.Where(l => _lanes.TryGetValue(l.Id, out var r) && r.Connected).ToList();

        // ---- nothing live: there is nothing to disambiguate, so start the work. ----------
        // A first sentence on an empty project is not an error condition, it is the beginning
        // of the work (§11: act, announce, allow undo).
        if (live.Count == 0)
        {
            var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
            if (id < 0) return msg;
            _store.RoutingInsert(text, "first", id, id, "only");
            return $"-> {msg} (started on {choice.Describe})";
        }

        // ---- who is focused. With no focus, pick rather than refuse (§11). ---------------
        long fid;
        var focused = _store.KvGet("focused_lane");
        if (focused is not null && long.TryParse(focused, out var f0) && live.Any(l => l.Id == f0)) fid = f0;
        else
        {
            var pick = live[^1];                       // the newest lane is the one you just made
            fid = pick.Id;
            _store.KvSet("focused_lane", fid.ToString());
            if (live.Count > 1)
                _store.PaneEvent(fid, "announcement", $"↦ focused {pick.Title} (nothing was focused)", null, null);
        }
        var frt = _lanes[fid];
        var focusedRow = work.First(l => l.Id == fid);

        // ---- tier 0.5: an unmistakable generic. Code, instant, no model. -----------------
        // The operator's rule, unchanged: a generic remark belongs to the focused lane, full
        // stop, no cleverness. Doing the obvious ones here makes the most common interjections
        // free AND keeps them out of the ~1s wait below — "stop" must never be slow.
        if (IsObviousGeneric(text))
        {
            frt.Say(text);
            _store.RoutingInsert(text, "generic", fid, fid, "explicit");
            return $"-> {focusedRow.Title} (generic)";
        }

        // ---- the classifier decides, and we WAIT for it. --------------------------------
        // Ensure, never look up. A lookup that misses is indistinguishable from a lookup that
        // was never going to hit, and for the whole life of this feature it never hit once
        // outside the suites (EnsureRouterAsync carries the incident).
        var routerId = await EnsureRouterAsync();
        if (routerId < 0)
        {
            // No judgement available, so keep the old, well-understood default rather than
            // inventing one. Spawning on every sentence would be worse than this: generics are
            // already handled above, but "make it blue instead" would still become a lane, and
            // a system that cannot tell continuation from new work should not pretend it can.
            // The four-verdict behaviour needs the brain on, which is the default in
            // dodona.json; the suites deliberately run without it.
            frt.Say(text);
            _store.RoutingInsert(text, "focus", null, fid, "no-classifier");
            // SAY SO. A permanent silent downgrade to "whatever is focused" is exactly the
            // quietly-stale state the standing directive forbids: the operator typed for two
            // days into a system whose routing had been off the whole time, and the only
            // evidence was a status-line suffix nobody reads. Once per daemon, in the pane.
            if (!_saidNoClassifier)
            {
                _saidNoClassifier = true;
                _store.Event("routing_unrouted", null, _config.Brain ? "classifier would not start" : "brain disabled in config");
                Announce(_config.Brain
                    ? "[dodona] the input classifier will not start — every sentence is going to the FOCUSED lane until it does. `dodona router-start` to retry."
                    : "[dodona] brain is off in dodona.json — routing is focused-lane only; a distinct task will NOT get its own lane.");
            }
            var stale0 = ovModel is not null || ovEffort is not null
                ? "  (model/effort is set when a lane starts — this one is already running)" : "";
            return $"-> {focusedRow.Title} (focus, no classifier warm){stale0}";
        }

        var verdict = await ClassifyAsync(routerId, text, work, focusedRow);

        // A classifier that timed out or answered nonsense has no opinion. Same reasoning as
        // above: fall back to the known default rather than guessing in either direction.
        if (verdict is null)
        {
            frt.Say(text);
            _store.RoutingInsert(text, "focus", null, fid, "classifier-silent");
            return $"-> {focusedRow.Title} (focus, classifier did not answer)";
        }

        var (kind, target, conf, reason) = verdict.Value;

        // ---- generic: the focused lane, never second-guessed. ---------------------------
        if (kind == "generic")
        {
            frt.Say(text);
            _store.RoutingInsert(text, "generic", fid, fid, conf);
            return $"-> {focusedRow.Title} (generic)";
        }

        // ---- addendum: an existing lane's thread continues. -----------------------------
        if (kind == "addendum" && conf != "low")
        {
            var tLane = work.FirstOrDefault(l => l.Title.Equals(target ?? "", StringComparison.OrdinalIgnoreCase));
            if (tLane is not null && _lanes.TryGetValue(tLane.Id, out var trt))
            {
                trt.Say(text);
                _store.RoutingInsert(text, "addendum", tLane.Id, tLane.Id, conf);
                _store.Event("routed_addendum", tLane.Id, $"{reason}: {Truncate(text, 80)}");
                if (tLane.Id != fid)
                    _store.PaneEvent(tLane.Id, "announcement", $"→ continued here rather than {focusedRow.Title} ({reason})", null, null);
                return $"-> {tLane.Title} (addendum{(reason.Length > 0 ? ", " + reason : "")})";
            }
        }

        // ---- new-task: spawn and deliver. The cheap, undoable side of the asymmetry. -----
        if (kind == "new-task" && conf != "low")
        {
            var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
            if (id < 0) return msg;
            _store.RoutingInsert(text, "new-task", id, id, conf);
            _store.Event("routed_new_task", id, $"conf={conf} reason={reason}");
            return $"-> {msg} (new task, started on {choice.Describe})";
        }

        // ---- unclear, or a shaky guess: the expensive tier, then the operator. ----------
        // NOTHING has been delivered yet, and that is the point. Guessing here is the one
        // mistake that cannot be taken back, so the ladder's top rung is a question.
        var laneList = string.Join("\n", work.Select(l => $"- {l.Title} (lane {l.Id})"));
        // THE FOCUSED LANE'S OWN PROJECT ASKS ITS OWN PROJECT'S MANAGER (Phase 5, handed to
        // Phase 3 as prose). This call site passed the default -- the workspace's FIRST project --
        // while the fact sheet it sends describes the focused lane and its siblings: project B's
        // lanes reasoned about by project A's manager, which is the cross-project confusion the
        // projects work removed everywhere else it could reach. `BrainReview` already resolves the
        // reviewed lane's own registration and this follows that shape exactly.
        //
        // `RegistrationKey` returns "" for a work lane in a folder no project owns, and
        // `BrainProject` turns that back into the first project -- so a workspace with one project
        // is byte-for-byte unchanged, which is the property every phase of this plan is measured
        // against.
        var escalationProject = RegistrationKey(focusedRow, ProjectPaths());
        var hi = await AskBrainHiAsync(
            "Decide where one line of operator input belongs in a multi-agent orchestrator.\n" +
            FactSheet(text, work, focusedRow) +
            "A distinct task should get its OWN new lane — new agent, clean context, and lanes are cheap. " +
            "An existing lane keeps it only when the input clearly continues that lane's thread: either it is " +
            "aimed at work that lane is doing now, or it is a small correction to what that lane just finished.\n" +
            "Reply ONLY one line of JSON: {\"kind\":\"generic|addendum|new-task|unclear\",\"target\":\"<LANE TITLE for addendum>\"," +
            "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars\"}",
            escalationProject);

        string? hKind = null, hTarget = null, hReason = "";
        var hConf = "low";
        if (hi is JsonElement he)
        {
            if (he.TryGetProperty("kind", out var k2)) hKind = k2.GetString();
            if (he.TryGetProperty("target", out var t2)) hTarget = t2.GetString();
            if (he.TryGetProperty("confidence", out var c2)) hConf = c2.GetString() ?? "low";
            if (he.TryGetProperty("reason", out var r2)) hReason = r2.GetString() ?? "";
        }
        _store.Event("classified_escalated", null, $"kind={hKind} target={hTarget} conf={hConf} input={Truncate(text, 80)}");

        if (hConf != "low")
        {
            if (hKind == "new-task")
            {
                var (id, msg, choice) = await SpawnForAsync(text, ovModel, ovEffort);
                if (id < 0) return msg;
                _store.RoutingInsert(text, "new-task", id, id, "escalated");
                _store.Event("routed_new_task", id, $"escalated reason={hReason}");
                return $"-> {msg} (new task, escalated, started on {choice.Describe})";
            }
            var hLane = work.FirstOrDefault(l => l.Title.Equals(hTarget ?? "", StringComparison.OrdinalIgnoreCase));
            if (hKind is "addendum" or "generic" && (hLane is not null || hKind == "generic"))
            {
                var dest = hLane ?? focusedRow;
                if (_lanes.TryGetValue(dest.Id, out var drt))
                {
                    drt.Say(text);
                    _store.RoutingInsert(text, hKind, dest.Id, dest.Id, "escalated");
                    return $"-> {dest.Title} ({hKind}, escalated)";
                }
            }
        }

        // ---- double uncertainty: ask, and hold the sentence. ----------------------------
        // The operator's own policy for ambiguity (§4) was "leave it with the focused lane",
        // but that was written when delivery was already done and the question was only whether
        // to retarget. Here nothing has been said yet, and delivering to the wrong lane is the
        // unrecoverable error — so the honest thing is to hold it and ask. Undoing a wait costs
        // nothing; undoing a polluted context costs the lane.
        var candidates = string.Join(" / ", work.Select(l => l.Title).Take(4));
        var rowId = _store.RoutingInsert(text, "ask", null, null, "unsure");
        _store.Event("routing_clarification", fid, $"decision {rowId}: {Truncate(text, 120)}");
        Announce($"[dodona] not sure whether “{Truncate(text, 45)}” is new work or continues something — " +
                 $"NOT delivered yet. Send it with a lane prefix ({candidates}) to continue one, " +
                 $"or `dodona lane-start --title <NAME>` then say it there for new work.");
        return $"held: not sure if this is new work or a continuation — nothing was delivered. " +
               $"Prefix a lane ({candidates}) to continue, or start a new lane.";
    }

    /// <summary>
    /// Tier 0 of the routing ladder (docs/WORKSPACES-CONCIERGE.md §5): `LANE: text` names its
    /// own target, so it is decided in code, instantly, and never reaches a model. Returns the
    /// named target and the body of the sentence, or null when the text is not of that shape.
    /// (`Body`, not `Rest`: `Rest` is a reserved tuple element name and will not compile.)
    ///
    /// `\s+`, not `\s*`, and that single character is the whole lesson: the documented form is
    /// `LANE: text` WITH a space, and requiring it stops a colon inside an ordinary sentence
    /// being read as a target. It was found by a test whose directive `routekind:` became a
    /// lane TITLED "ROUTEKIND", after which every later `routekind:...` line was silently
    /// delivered to it as a tier-0 prefix. The same shape bites for real with a lane called
    /// HTTP and a sentence containing `http://`.
    ///
    /// Pulled out of RouteInput so it can be checked without a daemon, a store or a lane
    /// (P4.5) -- this is a pure function over a string, and it was only reachable through
    /// eight seconds of process startup.
    /// </summary>
    internal static (string Target, string Body)? LanePrefix(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"^([A-Za-z0-9_-]+):\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>
    /// WHERE A LANE'S PROCESS RUNS. Three rungs, first non-empty wins: a directory that is
    /// AUTHORITATIVE for this particular spawn, then the lane's own recorded cwd
    /// (`lanes.cwd`, schema 8), then the workspace's first project as the last resort.
    ///
    /// THIS DECISION HAS ALREADY BEEN GOT WRONG ONCE, EXPENSIVELY (M5.1). `lane-respawn`
    /// hardcoded the first project and rebuilt the PLAIN-lane prompt, so a resumed TICKET
    /// agent ran in the operator's live working copy while being told "your worktree is the
    /// current working directory; work only there" -- a gated agent, resumed, editing main's
    /// tree. Both call sites now route through here, so the rung ORDER is one thing to read
    /// and one thing to test rather than two similar expressions twelve hundred lines apart.
    ///
    /// WHAT RUNG 1 IS DIFFERS BY CALLER, and that difference is real rather than an oversight,
    /// so it stays at the call site where it can be read:
    ///   * the `lane-respawn` command supplies the open ticket's worktree, because a ticket is
    ///     the authority on where its agent belongs;
    ///   * <see cref="RespawnLaneAsync"/> supplies its `workDir` parameter, which is whatever
    ///     the caller asked for -- and for `lane-respawn` that is the answer this function
    ///     just gave. The second call is a pass-through, which is why the two sites never
    ///     disagreed in practice despite reading differently.
    ///
    /// NO I/O, deliberately: `Directory.Exists` stays at the call sites and a caller passes
    /// null for a rung it has already ruled out. That keeps the ORDER -- the part that was
    /// wrong -- on the ~1 second `unit` loop beside <see cref="IsObviousGeneric"/> and
    /// <see cref="LanePrefix"/>, instead of eight seconds of daemon startup away.
    /// </summary>
    internal static string ResolveLaneCwd(string? authoritative, string? recordedCwd, string firstProject) =>
        authoritative is { Length: > 0 } a ? a
        : recordedCwd is { Length: > 0 } r ? r
        : firstProject;

    /// <summary>
    /// Unmistakable generics — the ones worth deciding in code so they are instant and free.
    ///
    /// Deliberately SHORT and anchored. This list exists to make "stop" fast, not to
    /// second-guess the classifier: anything not obviously one of these goes to the model,
    /// because the cost of a wrong guess here is a polluted lane. It matches the whole input,
    /// so "stop the nightly build from running" is not a generic — it is work.
    /// </summary>
    internal static bool IsObviousGeneric(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^(stop|wait|hold on|no|nope|yes|yep|ok|okay|continue|carry on|go on|go ahead|" +
            @"try again|again|retry|undo|undo that|revert that|never ?mind|cancel|abort|" +
            @"that'?s wrong|wrong|not that|do'?nt|don'?t do that|scrap that)[.!]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// What the classifier is told, as FACTS rather than things to guess at (§2.2: derive in
    /// code what is not really a judgement).
    ///
    /// Two of these facts exist because of specific operator corrections:
    ///   * MID-TURN IS A SIGNAL TOWARD THE LANE, not away from it. "some mid turn comments are
    ///     definately meant for the lane" — talking to a working agent is normal and common, so
    ///     the prompt says so rather than letting the model treat busy as "must be new work".
    ///   * LENGTH IS NOT A SIGNAL AT ALL. An earlier draft treated short input as probably a
    ///     continuation; the operator rejected it: "a short 'add this' on an existing lane might
    ///     mean a new work on that workspace". So no word count is given, and the discriminator
    ///     offered instead is SUBJECT — does the sentence name what this lane is about, or
    ///     something else.
    /// </summary>
    string FactSheet(string text, List<Store.LaneRow> work, Store.LaneRow focusedRow)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Input: ").Append(text).Append('\n');
        sb.Append("Lanes:\n");
        foreach (var l in work)
        {
            var last = _store.Tail(l.Id, 1, readableOnly: true).FirstOrDefault() ?? "";
            var busy = l.Presence.Length > 0 && l.Presence is not ("idle" or "landed" or "system");
            sb.Append($"- {l.Title}: {(busy ? "WORKING NOW" : "idle")}");
            if (l.Id == focusedRow.Id) sb.Append(" [FOCUSED — the operator is looking at this one]");
            if (last.Length > 0) sb.Append($"; last: {Truncate(last, 110)}");
            sb.Append('\n');
        }
        // Referring expressions point at something already under discussion. A fact, not a rule.
        var refs = System.Text.RegularExpressions.Regex.Matches(text, @"\b(that|it|this|instead|also|still|again|those|them)\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                   .Select(x => x.Value.ToLowerInvariant()).Distinct().ToList();
        if (refs.Count > 0)
            sb.Append($"The input refers back with: {string.Join(", ", refs)} — it may be about something already being discussed.\n");
        sb.Append("A lane that is WORKING NOW is a perfectly normal thing to talk to: operators interrupt working " +
                  "agents constantly, and that is usually an addendum, not new work.\n");
        return sb.ToString();
    }

    /// <summary>Ask the warm cheap classifier and WAIT. Null when it has no usable opinion —
    /// every caller treats that as "fall back", never as "guess".</summary>
    async Task<(string Kind, string? Target, string Conf, string Reason)?> ClassifyAsync(
        long routerId, string text, List<Store.LaneRow> work, Store.LaneRow focusedRow)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _routerLock.WaitAsync();
        string? reply;
        try { reply = await _lanes[routerId].AskAsync(FactSheet(text, work, focusedRow), 20000); }
        finally { _routerLock.Release(); }

        if (reply is null) { _store.Event("classifier_timeout", routerId, Truncate(text, 100)); return null; }
        try
        {
            using var d = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]);
            var kind = d.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "unclear" : "unclear";
            var target = d.RootElement.TryGetProperty("target", out var t) ? t.GetString() : null;
            var conf = d.RootElement.TryGetProperty("confidence", out var c) ? c.GetString() ?? "low" : "low";
            var reason = d.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            _store.Event("classified", routerId, $"{sw.ElapsedMilliseconds}ms kind={kind} target={target} confidence={conf} reason={reason} input={Truncate(text, 80)}");
            return (kind, target, conf, reason);
        }
        catch { _store.Event("classifier_failed", routerId, Truncate(reply, 120)); return null; }
    }

    /// <summary>
    /// WHICH PROJECT A NEW LANE OPENS IN (docs/LOCATIONS-PLAN.md Phase 3). The ladder itself is
    /// pure and lives in <see cref="ProjectLadder"/>; this is the I/O half — the registry read,
    /// the liveness read, and the one cheap model call rung 2 is allowed.
    ///
    /// Returns the project, or null when the sentence must be HELD. Null is not an error and not
    /// a fallback: with several projects, no project named in the sentence, and no live lane to
    /// infer from, every guess is a coin toss whose losing side is an agent editing the wrong
    /// repository. That is not undone by a `lane-stop`, so it is the one place this ladder stops
    /// and asks (§5's error asymmetry, one level down from lane choice).
    ///
    /// **A one-project workspace never reaches any of it**: <see cref="ProjectLadder.Decide"/>
    /// answers `only` before the liveness read, before the registry read and before any model,
    /// and this method writes no event for it. Byte-for-byte what the spawn site did before.
    /// </summary>
    async Task<ProjectVerdict> ResolveProjectAsync(string text)
    {
        var projects = ProjectPaths();
        // THE ONE-PROJECT SHORT-CIRCUIT, HERE AS WELL AS INSIDE Decide, and it is not
        // redundant: arguments are evaluated before the call, so passing `ProjectHandles()` and
        // `LiveProjectPaths()` unconditionally would make a one-project workspace pay for a
        // registry read of the alias table and a full pipe-namespace enumeration on every
        // sentence the operator types -- to reach a rung that had already decided. The honest
        // residual cost this phase adds to a one-project workspace is `ProjectPaths()`, one
        // registry read that degrades to `_primary` if the registry will not open, i.e. exactly
        // the old answer.
        if (projects.Count <= 1)
            return ProjectLadder.Decide(projects, Array.Empty<(string, string)>(), Array.Empty<string>(), text);

        var v = ProjectLadder.Decide(projects, ProjectHandles(), LiveProjectPaths(), text);

        if (v.Rung == ProjectLadder.Classify)
        {
            // Rung 2 proper: several projects hold live lanes, so which one this sentence is
            // about is a judgement, and it is the cheap tier's to make. EnsureRouterAsync, not a
            // lookup -- the same rule that cost this project two days of dead routing.
            var routerId = await EnsureRouterAsync();
            var picked = routerId < 0 ? null : await ClassifyProjectAsync(routerId, text, v.Candidates);
            if (picked is not null) v = v with { Rung = ProjectLadder.Live, Project = picked, How = "classified" };
            else
            {
                // No classifier, or it would not choose. SAY SO rather than picking the first
                // candidate: a silent degrade is a bug, and "the first project" is exactly the
                // invisible wrong answer this phase exists to delete.
                _store.Event("project_unclassified", null,
                    routerId < 0 ? $"no classifier; candidates={string.Join(", ", v.Candidates)}"
                                 : $"classifier would not choose; candidates={string.Join(", ", v.Candidates)}");
                v = v with { Rung = ProjectLadder.Ask, Project = null, How = routerId < 0 ? "no-classifier" : "classifier-unsure" };
            }
        }

        // ONE PLACE WRITES THE EVENT, and it is below the classify branch on purpose: that branch
        // used to return early, so a lane placed by the cheap tier -- the only rung that costs
        // quota -- was the one rung with no row saying which project it chose or why. Caught by
        // `workspace:the_classified_rung_records_that_a_model_answered`, which read back the
        // PREVIOUS decision's event and reported `how=sole-live` for a classified one.
        if (v.Rung != ProjectLadder.Only && v.Project is not null)
            _store.Event("project_chosen", null, $"rung={v.Rung} how={v.How} project={v.Project}");
        return v.Rung == ProjectLadder.Ask ? v with { Candidates = ProjectsByRecency() } : v;
    }

    /// <summary>Ask the warm cheap classifier which project, over a CLOSED list. Null when it has
    /// no usable opinion or names something that was not offered — a model that invents a folder
    /// must not be able to place an agent in it, which is why the answer is matched against the
    /// candidates rather than fed to <see cref="TryProject"/> and hoped about.</summary>
    async Task<string?> ClassifyProjectAsync(long routerId, string text, IReadOnlyList<string> candidates)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ProjectQuestionLead).Append('\n');
        sb.Append("Each project is one folder, and each already has an agent working in it. The new " +
                  "work is a DISTINCT task, so it gets its own lane -- the only question is which " +
                  "project's folder it belongs in.\n");
        sb.Append("Projects:\n");
        foreach (var c in candidates) sb.Append("- ").Append(ProjectLadder.Leaf(c)).Append('\n');
        sb.Append("Input: ").Append(text).Append('\n');
        sb.Append("Reply ONLY one line of JSON: {\"project\":\"<one project name above, or none>\"," +
                  "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars\"}\n");
        sb.Append("Say none, or confidence low, if the input does not clearly belong to one of them. " +
                  "The operator is then asked, which is cheap; a lane opened in the wrong project is " +
                  "an agent editing the wrong repository, which is not.");

        await _routerLock.WaitAsync();
        string? reply;
        try { reply = await _lanes[routerId].AskAsync(sb.ToString(), 20000); }
        finally { _routerLock.Release(); }
        if (reply is null) { _store.Event("classifier_timeout", routerId, Truncate(text, 100)); return null; }
        try
        {
            using var d = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]);
            var name = d.RootElement.TryGetProperty("project", out var p) ? p.GetString() : null;
            var conf = d.RootElement.TryGetProperty("confidence", out var c) ? c.GetString() ?? "low" : "low";
            _store.Event("classified_project", routerId, $"project={name} confidence={conf} input={Truncate(text, 80)}");
            if (conf == "low" || name is null or "" or "none") return null;
            // ONE name->project resolver, in ProjectLadder, shared with the operator's own answer
            // to a rung-4 question (P3.A). This was an inline FirstOrDefault; two copies of "does
            // this name mean one of these projects" drift the moment one of them learns something,
            // which is why Concierge.Mentions moved into ProjectLadder as well.
            return ProjectLadder.ByName(candidates, name);
        }
        catch { _store.Event("classifier_failed", routerId, Truncate(reply, 120)); return null; }
    }

    /// <summary>
    /// Spawn a lane for this input and deliver to it — the `new-task` action, and also the
    /// first-lane case. Name derived in code, model/effort from the policy table (a claude
    /// process cannot change model mid-session, so this is decided where the lane is born), and
    /// `BrainReview` corrects the name or suggests a ticket from behind — machinery that already
    /// existed and needed no change.
    ///
    /// **A negative id means NOTHING WAS DELIVERED**, and Phase 3 gave that two meanings rather
    /// than one: the spawn failed, or the project ladder held the sentence. Both are handled the
    /// same way by every caller (`if (id &lt; 0) return msg`), which is why the held case can be
    /// reported here — the alternative was a second return channel through four call sites.
    /// </summary>
    async Task<(long Id, string Msg, Choice Choice)> SpawnForAsync(string text, string? ovModel, string? ovEffort,
                                                                  string? answeredProject = null)
    {
        var name = NameFromText(text);
        var choice = Policy.Resolve(text, _config.Rules, _config.Model, _config.Effort, ovModel, ovEffort);

        // PHASE 3'S ONE LINE. This used to be `_primary` -- the first project, always, with a
        // comment saying that choosing one from a sentence was Phase 3's job. It is.
        //
        // `answeredProject` is the ONE input that skips the ladder, and only ever arrives from
        // `AnswerQuestion` (P3.A): the operator has just told us which project, so re-running a
        // ladder that already said "I do not know" would hold the sentence a second time and
        // discard the answer. It is still validated below like every other rung's answer.
        ProjectVerdict pv;
        if (answeredProject is not null)
        {
            // Recorded here rather than in ResolveProjectAsync, which never sees this path: every
            // rung that places a lane writes one `project_chosen` row saying which evidence
            // decided, and "the operator said so" is evidence like any other. Without it the one
            // rung a person actually answered would be the one rung with no record.
            pv = new ProjectVerdict("answered", answeredProject, "operator", Array.Empty<string>());
            _store.Event("project_chosen", null, $"rung={pv.Rung} how={pv.How} project={pv.Project}");
        }
        else pv = await ResolveProjectAsync(text);

        if (pv.Project is null)
        {
            // Rung 4: HOLD. No lane row, nothing said to any agent -- the same shape the lane
            // ladder's own top rung uses, and the same reason (`held_input_invents_no_lane`).
            var list = pv.Candidates.Count == 0 ? "none" : string.Join(" / ", pv.Candidates.Select(ProjectLadder.Leaf));
            _store.RoutingInsert(text, "ask", null, null, "no-project");
            _store.Event("project_unknown", null, $"how={pv.How} candidates={list} input={Truncate(text, 80)}");
            // ...AND IT OPENS A QUESTION ROW, which is P3.A and is what makes "ask" mean asking
            // somebody. Phase 3 built this rung, Phase 4 built the overlay that renders a
            // `questions` row, and for two days nothing connected them: rung 4 wrote a
            // `routing_decisions` row at tier `ask`, an event and an announcement, and the
            // operator's window never showed a routing question at all. The row goes in the
            // WORKSPACE store (D-L11) -- scope is which store the row is in, and a daemon that
            // needed a live concierge to ask about its own work would be unable to ask in
            // precisely the cases routing matters.
            var qid = AskWhichProject(text, pv.Candidates);
            Announce($"[dodona] not sure which project “{Truncate(text, 45)}” is for — NOT delivered yet. " +
                     $"Projects here: {list}. Answer in the window, or `dodona answer {qid} <project>`; " +
                     $"naming one in the sentence works too, as does " +
                     $"`dodona lane-start --title <NAME> --project <path>`.");
            return (-1, $"held: not sure which project this is for — nothing was delivered. " +
                        $"Answer question {qid} ({list}), name one of them in the sentence, " +
                        $"or start a lane with --project.", choice);
        }
        // Through TryProject, always: a rung's answer is still only a folder until the thing that
        // validates folders has seen it (P2.1). Belt and braces on purpose -- every candidate here
        // came out of `members`, so a refusal can only mean the project was detached between the
        // ladder's read and this one, which is precisely trap T4 arriving on the spawn path.
        if (!TryProject(pv.Project, out var project, out var refusal))
        {
            _store.Event("project_gone_at_spawn", null, $"rung={pv.Rung} {refusal}");
            return (-1, $"error: could not start a lane for this: {refusal}", choice);
        }
        var (newId, msg) = await SpawnAgentLaneAsync(name, project, choice.Model, choice.Effort);
        if (newId < 0) return (-1, $"error: could not start a lane for this: {msg}", choice);

        _store.Event("policy_choice", newId, $"{choice.Model}/{choice.Effort} why={choice.Why} overridden={choice.Overridden} text={text}");
        _store.KvSet("focused_lane", newId.ToString());
        _store.Event("lane_auto_created", newId, $"from input: {text}");
        _store.PaneEvent(newId, "announcement",
            $"started this lane on {choice.Describe} for “{Truncate(text, 45)}” — undo: dodona lane-stop {newId}",
            null, null, acked: true);   // a receipt: it badged the lane the instant it was born, which was a lie
        _lanes[newId].Say(text);
        BrainReview(newId, text, name, choice);   // fire-and-forget: corrects behind, never gates
        return (newId, name, choice);
    }


    /// <summary>
    /// The merge that brought <paramref name="main"/> into <paramref name="branch"/>: returns
    /// that merge commit and its FIRST parent — the branch's state immediately before main
    /// arrived. Empty strings when no such merge exists.
    ///
    /// **This replaced a fork-point calculation that was measured wrong, and the way it was
    /// wrong is worth keeping.** `REVIEW-AND-MERGE-PLAN` §10 says the drop check must diff
    /// against the merge base rather than main's tip, and the reason is sound: once main has
    /// been merged in, main IS an ancestor of the branch, so `merge-base main branch` returns
    /// main's own tip and a branch that reverted main's change looks identical to one that
    /// never saw it. The first implementation therefore recovered a fork point from the
    /// branch's merge commits — and took the OLDEST one, reasoning that a wider window catches
    /// more.
    ///
    /// It caught nothing. `git rev-list --first-parent --merges &lt;branch&gt;` walks the whole
    /// ancestry, and a ticket branch's ancestry CONTAINS MAIN'S OWN MERGE HISTORY — every
    /// previous ticket that landed. So "oldest merge" resolved to an ancient merge on main and
    /// the fork point came out as the repository's **init commit**, identically for every
    /// ticket (measured: `fork=adc8bfb` for tickets 1 through 7). Against init the dropped file
    /// did not exist yet, so the pre-image comparison could never match and the check passed
    /// everything. A check that is blind while looking armed — CLAUDE.md §0.3 exactly.
    ///
    /// So there is no fork point here at all. The reference is **M^1**, the branch tip just
    /// before the merge, which is defined by the merge itself and cannot be confused with
    /// anything in main's history. §10's intent is honoured — the comparison is emphatically
    /// not against main's tip — while the quantity used is one git can hand over exactly.
    /// The NEWEST qualifying merge is the right one: anything reverted before an earlier merge
    /// was brought back in by the later one.
    /// </summary>
    static (string Merge, string PreMerge) MainMergeOnBranch(string workDir, string main, string branch)
    {
        var (lc, list) = Git.Run(workDir, "rev-list", "--first-parent", "--merges", branch);
        if (lc != 0) return ("", "");
        foreach (var raw in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))   // newest first
        {
            var m = raw.Trim();
            if (m.Length == 0) continue;
            var (pc, parents) = Git.Run(workDir, "rev-list", "--parents", "-n", "1", m);
            if (pc != 0) continue;
            var parts = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;             // <self> <p1> <p2>...
            var p1 = parts[1]; var p2 = parts[2];
            // The second parent must be part of main — otherwise this is some other merge the
            // branch carries (including one it inherited from main itself, which is what made
            // the first version of this function useless).
            if (Git.Run(workDir, "merge-base", "--is-ancestor", p2, main).Code != 0) continue;
            // ...and the merge must be the branch's OWN, not one it inherited: an inherited
            // merge is an ancestor of main, and the branch did not perform it.
            if (Git.Run(workDir, "merge-base", "--is-ancestor", m, main).Code == 0) continue;
            return (m, p1);
        }
        return ("", "");
    }

    /// <summary>
    /// Files where main's change has gone missing from the branch (D-R4). A path counts as a
    /// silent drop when all three hold: the merge changed it (so main contributed something
    /// there), and the branch's final version is byte-identical to the PRE-MERGE version, and
    /// therefore main's contribution is simply absent. That is a fact, not a judgement, and it
    /// is the one failure an agent's own report will never mention — the tests still pass,
    /// because nothing references the discarded code.
    ///
    /// A resolution that COMBINES both sides differs from the pre-merge version, so it is not
    /// flagged. That is the common, legitimate case and it must stay quiet.
    /// </summary>
    static List<string> SilentDrops(string workDir, string preMerge, string mergeCommit, string branch)
    {
        var drops = new List<string>();
        var (dc, changed) = Git.Run(workDir, "diff", "--name-only", preMerge, mergeCommit);
        if (dc != 0) return drops;
        foreach (var raw in changed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.Trim();
            if (f.Length == 0) continue;
            // A missing path is a real state, not an error: main may have DELETED the file, and
            // a branch that put its copy back has dropped that deletion too. ShaOrEmpty makes
            // absence a value rather than an exception, which is why it exists (see Git.cs).
            var atPre = Git.ShaOrEmpty(workDir, $"{preMerge}:{f}");
            var atMerge = Git.ShaOrEmpty(workDir, $"{mergeCommit}:{f}");
            var atBranch = Git.ShaOrEmpty(workDir, $"{branch}:{f}");
            if (atMerge != atPre && atBranch == atPre) drops.Add(f);
        }
        return drops;
    }

    /// <summary>One land, in flight or finished (R3.5). The whole of the phase's state, and it
    /// is deliberately in memory: a daemon that restarts forgets a land it did not finish, which
    /// is the correct answer rather than a gap — a persisted "landing" row is exactly the kind of
    /// thing that outlives its reason and goes quietly stale (CLAUDE.md §0.1). The recovery is
    /// re-running `land`, which is idempotent by construction: the trunk moves only in the last
    /// step, so an interrupted land has merged main in (a no-op next time) and nothing else.</summary>
    sealed class LandRun
    {
        public LandRun(long ticket) { Ticket = ticket; StartedUtc = DateTime.UtcNow; }
        public long Ticket { get; }
        public DateTime StartedUtc { get; }
        /// <summary>WRITTEN LAST, and volatile, so a reader that sees `Done` also sees `Ok` and
        /// `Message`. The writer is the land's own task; every reader is the control pipe.</summary>
        public volatile bool Done;
        public bool Ok;
        public string Message = "";
        public void Finish(bool ok, string msg) { Ok = ok; Message = msg; Done = true; }
    }

    /// <summary>Lands by ticket id. A finished entry is kept, so the outcome stays readable after
    /// the caller has gone, and is replaced when the same ticket lands again.</summary>
    readonly ConcurrentDictionary<long, LandRun> _lands = new();

    /// <summary>Everything the expensive half of a land needs, resolved by the cheap half that
    /// still runs on the pipe. Passing it forward rather than re-resolving is not a micro-
    /// optimisation: `RepoOf` and `Config.For` read the registry and the filesystem, and a land
    /// that answered "your repository is fine" and then acted on a different one would be P0.1's
    /// wrong-main incident with a race in front of it.</summary>
    sealed record LandPlan(Store.TicketRow Ticket, string RepoPath, Config Cfg, Store.RepoId TokenId);

    /// <summary>THE LAND IS NOT ON THE CONTROL PIPE ANY MORE (R3.5, decision D-R14). This is what
    /// `case "land"` calls, and it returns in milliseconds.
    ///
    /// **The freeze it removes, measured 2026-08-20.** The pipe is serial — one
    /// `NamedPipeServerStream` instance, `HandleAsync` awaited inline — and the land ran on it. So
    /// for the whole duration of a land's verify the daemon answered *nothing*: no UI, no lane
    /// input, no `say`, no other repository's land. The narrow verify this repo settled on holds
    /// it ~20 s; the full `dev gate` would hold it **4.6 minutes**. That is CLAUDE.md §0.1's
    /// *never hung* on the one operation an operator is certainly watching.
    ///
    /// **The protocol, which is the part that changes for callers.** The cheap gate stays here —
    /// ticket open, repository resolvable, token held, lease alive, and the trunk actually checked
    /// out in the shared checkout — because it costs milliseconds and a caller deserves those
    /// refusals on the spot. Past that point the reply is *landing…*, and the outcome arrives
    /// three ways: an announcement in the ticket's pane, an event in the store, and
    /// `land-status &lt;ticket&gt;`. `dodona land` polls that last one so a shell still gets an
    /// exit code (see `LandCli` in Program.cs) — the daemon is free either way, which is the
    /// whole point.
    ///
    /// **Two constraints this had to preserve, both load-bearing (plan §5).**
    ///
    /// * **The token is held across the WHOLE flow.** Nothing here releases or re-checks it: the
    ///   in-worktree merge and the fast-forward stay inside one task, so no window exists in
    ///   which main can move between them. D-R2's fast-forward-as-an-assertion depends on it, and
    ///   a swap cannot cut a land in half either — `Blockers` already refuses to swap while a
    ///   merge token is held.
    /// * **A failed land still leaves the worktree clean and main untouched.** Unchanged, because
    ///   `LandFlow` is the same code in the same order: every giving-up path aborts its merge and
    ///   returns before the fast-forward. What the split adds is that the failure is now
    ///   *reported* asynchronously, so it has to announce itself — and it does, on every path
    ///   inside `LandFlow`, plus the two this wrapper covers (success, and a throw).
    ///
    /// A second `land` for a ticket already landing is refused rather than run twice. That was
    /// impossible before — the serial pipe made it impossible — and it is the one new race the
    /// split creates, so it is closed here rather than left to be discovered.</summary>
    string LandBegin(long tid, out bool started)
    {
        started = false;
        if (_lands.TryGetValue(tid, out var already) && !already.Done)
            return $"refused: ticket {tid} is already landing ({(int)(DateTime.UtcNow - already.StartedUtc).TotalSeconds}s so far) — dodona land-status {tid}";

        var refusal = LandGate(tid, out var plan);
        if (refusal is not null) return refusal;

        var run = new LandRun(tid);
        _lands[tid] = run;
        started = true;
        _store.Event("land_started", null, $"ticket {tid}");
        _ = Task.Run(() =>
        {
            string msg;
            var ok = false;
            try { msg = LandFlow(plan!, out ok); }
            catch (Exception ex)
            {
                // The pipe used to catch this and turn it into `error: …` on the caller's
                // reply. Nobody is holding that reply now, so an unhandled throw would be a
                // land that simply stopped existing — the silent failure this codebase pays for
                // most (§3's dead routing ladder). It announces, and it says what to do.
                msg = $"error: the land threw — {ex.Message}";
                _store.Event("land_threw", null, $"ticket {tid}: {ex}");
                Announce(plan!.Ticket, $"ticket {tid}'s land threw: {ex.Message} — nothing was lost (the trunk moves only in the last step); re-run dodona land {tid}");
            }
            run.Finish(ok, msg);
            _store.Event(ok ? "land_finished" : "land_refused_async", null, $"ticket {tid}: {msg}");
            // Success is the one outcome LandFlow does not announce in its own words: it writes
            // "agent retired" into the lane's pane and used to return the receipt to a caller
            // that was still waiting. There is no such caller now, so the receipt is announced —
            // which is also the only announcement a ticket with no lane would ever get.
            if (ok) Announce(plan!.Ticket, msg);
        });
        return $"landing ticket {tid} — merge, verify and fast-forward run off the control pipe; the outcome announces itself and dodona land-status {tid} reports it";
    }

    /// <summary>The cheap half: milliseconds, and therefore still answered on the pipe. Returns a
    /// refusal, or null with the plan the expensive half runs on.</summary>
    string? LandGate(long tid, out LandPlan? plan)
    {
        plan = null;
        var t = _store.Ticket(tid);
        if (t is null || t.State != "open") return $"refused: ticket {tid} not open";

        // THE FALLBACK HERE USED TO BE `_primary`, AND IT COULD FAST-FORWARD THE WRONG MAIN.
        // `Repos.ByName(repos, t.Repo)` returns null as soon as the naming rule moves under an
        // open ticket — attach a second project and every "." ticket resolves to nothing — and
        // the land then ran `git merge --ff-only ticket/N` in the FIRST PROJECT'S repository.
        // A ref advance is the one irreversible act in this system, so there is no default for
        // "which repository": it is the recorded one or it is a refusal (P0.1).
        var repo = RepoOf(t);
        if (repo is null)
        {
            _store.Event("land_refused", null, $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
            return $"refused: ticket {tid}'s repository is no longer in this workspace " +
                   $"({(t.RepoPath.Length > 0 ? t.RepoPath : $"'{t.Repo}'")}) — re-attach it or abandon the ticket";
        }
        var repoPath = repo.Path;
        var cfg = Config.For(_primary, repoPath);
        var where = t.Repo == "." ? "project root" : $"repository {t.Repo}";

        var tokenId = TokenIdOf(t);
        var tok = _store.TokenRead(tokenId);
        if (tok.Holder != tid) { _store.Event("land_refused", null, $"ticket {tid}: not holder of {t.Repo} (holder={tok.Holder?.ToString() ?? "none"})"); return $"refused: ticket {tid} does not hold {t.Repo}'s merge token"; }
        if (tok.ExpiresTs is not null && DateTime.Parse(tok.ExpiresTs).ToUniversalTime() < DateTime.UtcNow)
        { _store.Event("land_refused", null, $"ticket {tid}: lease expired"); return "refused: merge-token lease expired; re-request"; }

        // Checked BEFORE the merge and the verify, because those cost minutes and this costs
        // milliseconds — and ANNOUNCED rather than failing quietly (plan §10). It is true
        // while CLAUDE.md §0.0 keeps the operator on main in the shared checkout, so the one
        // way to see this is a state nobody expected, which is exactly when a silent refusal
        // in a daemon log is the wrong place for the sentence.
        var (hc, head) = Git.Run(repoPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (hc != 0 || head != cfg.Main)
        {
            _store.Event("land_refused", null, $"ticket {tid}: {where} has '{head}' checked out, not '{cfg.Main}'");
            Announce(t, $"ticket {tid} cannot land: {where} has '{head}' checked out, not '{cfg.Main}' — check out {cfg.Main} there and re-run dodona land {tid}");
            return $"refused: {where} has '{head}' checked out, not '{cfg.Main}'";
        }

        plan = new LandPlan(t, repoPath, cfg, tokenId);
        return null;
    }

    /// <summary>The land (§7, and `docs/REVIEW-AND-MERGE-PLAN.md` §3): the daemon executes
    /// the one atomic ref advance — but it now does the ordinary developer flow first
    /// (D-R1), in this order and under the merge token throughout:
    ///
    /// <code>
    ///   git merge &lt;main&gt;    IN THE WORKTREE, on the ticket branch
    ///   &lt;verify&gt;             IN THE WORKTREE, on the merged result
    ///   git merge --ff-only  in the shared checkout: now guaranteed
    /// </code>
    ///
    /// **What changed and why.** This used to be `merge --ff-only` and nothing else: when
    /// main had moved it refused with *"rebase &lt;branch&gt; onto &lt;main&gt; and re-verify
    /// first"* — and **nothing in the tree performed that rebase**, so concurrent work
    /// could not land at all. Worse, verify ran AFTER the ref advance, in the repository
    /// that had just changed, so a red verify had already shipped.
    ///
    /// **ff-only is now an ASSERTION rather than a policy (D-R2).** After main has been
    /// merged into the branch, the merge back *is* a fast-forward — measured, not assumed:
    /// git itself reports `Fast-forward` and main's tree comes out byte-identical to the
    /// branch tip that was verified. That identity is the whole reason verifying the
    /// worktree is equivalent to verifying main (`WORK-ISOLATION-PLAN` D-5), and it is why
    /// verify may move ahead of the merge at all. So if ff-only fails *now*, main moved
    /// despite the token — a real fault, and refusing is correct.
    ///
    /// **The ordering is the trap, not the merge** (plan §10). The in-worktree merge must
    /// happen while the token is HELD, which is why it lives here, below the holder check,
    /// and never in `token-request` before the grant: otherwise two lanes both merge main
    /// in, both believe they verified against current main, and the second one's
    /// fast-forward is against a main that moved underneath it.
    ///
    /// **AND IT NO LONGER RUNS ON THE CONTROL PIPE** (R3.5 / D-R14). `LandBegin` answers the
    /// caller; this is what its task runs. See `LandBegin` for the protocol and for the two
    /// constraints the split had to preserve.</summary>
    string LandFlow(LandPlan plan, out bool ok)
    {
        ok = false;
        var (t, repoPath, cfg, tokenId) = plan;
        var tid = t.Id;

        // ---- D-R1 step 1: bring main INTO the branch, in the agent's own worktree --------
        //
        // Measured before this was written (the premise the whole phase rests on): `git merge
        // <main>` inside a linked worktree SUCCEEDS while main is checked out in the shared
        // checkout, leaves the shared checkout's HEAD and main sha untouched, and leaves the
        // worktree clean. Only `checkout` of a branch held elsewhere is refused; merging a ref
        // into the current branch never checks it out.
        var mergeMsg = "already current with " + cfg.Main;
        if (t.Worktree.Length > 0 && Directory.Exists(t.Worktree))
        {
            // A dirty worktree first, because `git merge` refuses one and its complaint does
            // not say what to do about it. NEVER `git stash` here: the stash is repo-global,
            // one shared ref in the common dir, so two lanes stashing interleave one stack and
            // `pop` takes the other lane's work (CLAUDE.md §5.2). Commit to the branch instead.
            var (sc, dirty) = Git.Run(t.Worktree, "status", "--porcelain");
            if (sc == 0 && dirty.Length > 0)
            {
                _store.Event("land_refused", null, $"ticket {tid}: worktree has uncommitted changes");
                Announce(t, $"ticket {tid} cannot land: uncommitted changes in its worktree — commit them to {t.Branch} (never git stash: it is repo-global) and re-run dodona land {tid}");
                return $"refused: ticket {tid}'s worktree has uncommitted changes — commit them to {t.Branch} " +
                       $"(do NOT git stash: the stash is repo-global and another lane's pop would take them) and re-run land";
            }

            var (bmc, bmOut) = Git.Run(t.Worktree, "merge", cfg.Main, "-m", $"merge {cfg.Main} into {t.Branch} before landing ticket {tid}");
            if (bmc != 0)
            {
                // A conflict the daemon must not guess at (D-R3). Code does not resolve —
                // the agent does, and it keeps its context to do it. What code owes here is a
                // CLEAN TREE: a half-merged worktree makes every later check lie, so the abort
                // is not optional and it is not best-effort.
                var (uc, conflicted) = Git.Run(t.Worktree, "diff", "--name-only", "--diff-filter=U");
                var names = uc == 0 && conflicted.Length > 0
                    ? string.Join(", ", conflicted.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                    : "(git named none — see the daemon log)";
                var (ac, aOut) = Git.Run(t.Worktree, "merge", "--abort");
                if (ac != 0) _store.Event("land_merge_abort_failed", null, $"ticket {tid}: {aOut}");
                _store.Event("land_conflict", null, $"ticket {tid}: merging {cfg.Main} into {t.Branch} conflicted in {names}");
                Announce(t, $"ticket {tid}: merging {cfg.Main} in conflicts in {names} — resolve it in {t.Worktree}, commit, then re-run dodona land {tid}");
                return $"refused: merging {cfg.Main} into {t.Branch} conflicts in {names} — the merge was aborted, " +
                       $"so the worktree is clean. Resolve it there, commit, then land again.";
            }
            // "Already up to date." is the common case and costs one git call: main has not
            // moved since the branch was cut, so no merge commit is created and the land is
            // byte-for-byte what it always was.
            mergeMsg = bmOut.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
                ? $"already current with {cfg.Main}"
                : $"merged {cfg.Main} in";
            if (mergeMsg != $"already current with {cfg.Main}")
                _store.Event("land_merged_main", null, $"ticket {tid}: {cfg.Main} -> {t.Branch}");
        }
        else
        {
            // No worktree to merge in. Not fatal — a ticket can outlive its checkout — but it
            // means ff-only below is back to being a policy rather than an assertion, so say so
            // rather than letting the land look like it did the flow.
            mergeMsg = "no worktree: could not merge " + cfg.Main + " in";
            _store.Event("land_no_worktree", null, $"ticket {tid}: {t.Worktree}");
        }

        // ---- D-R4: the SILENT DROP, which is the failure a report will not mention ---------
        //
        // The dangerous resolution is not the messy one. It is the quiet one: the agent
        // resolves by discarding what main brought in, and the tests still pass because
        // nothing references the discarded code. Nobody's judgement is needed for that — it is
        // mechanically detectable — and no report will mention it, which is why code asks.
        {
            var dropDir = t.Worktree.Length > 0 && Directory.Exists(t.Worktree) ? t.Worktree : repoPath;
            var (mergeCommit, preMerge) = MainMergeOnBranch(dropDir, cfg.Main, t.Branch);
            if (mergeCommit.Length == 0)
            {
                // No merge of main on this branch, so main contributed nothing here for the
                // branch to have discarded — there is genuinely nothing to check, which is a
                // different thing from a check that failed to run. Recorded either way, because
                // a check that quietly does nothing is the fail-open this codebase has paid for
                // twice (§3's dead routing ladder, GateHook's BOM).
                _store.Event("land_drop_check_moot", null, $"ticket {tid}: no merge of {cfg.Main} on {t.Branch}, nothing to drop");
            }
            else
            {
                var drops = SilentDrops(dropDir, preMerge, mergeCommit, t.Branch);
                _store.Event("land_drop_check", null,
                    $"ticket {tid}: {drops.Count} drop(s) against pre-merge {preMerge[..Math.Min(8, preMerge.Length)]} (merge {mergeCommit[..Math.Min(8, mergeCommit.Length)]})");
                if (drops.Count > 0)
                {
                    var names = string.Join(", ", drops);
                    _store.Event("land_silent_drop", null, $"ticket {tid}: reverted {cfg.Main}'s change to {names} (pre-merge {preMerge[..Math.Min(8, preMerge.Length)]})");
                    Announce(t, $"ticket {tid} did not land: it reverts {cfg.Main}'s change to {names}. If that resolution was deliberate, re-apply it as an edit on top of {cfg.Main}'s version rather than as the pre-merge file.");
                    return $"refused: {t.Branch} reverts {cfg.Main}'s change to {names} — the branch carries the PRE-MERGE version of " +
                           $"{(drops.Count == 1 ? "that file" : "those files")}, so merging {cfg.Main} in delivered the change and something put it back. " +
                           $"Take {cfg.Main}'s version (or resolve on top of it) and land again.";
                }
            }
        }

        // ---- D-R1 step 2: verify the MERGED RESULT, in the worktree, BEFORE the ref moves --
        //
        // This used to run after `LandCommit`, in the repository that had just changed — so a
        // red verify had already shipped and there was nothing left to refuse
        // (`WORK-ISOLATION-PLAN` D-5). It is exactly equivalent here and strictly safer: the
        // fast-forward below makes main's tree byte-identical to the tip verified here (D-R2).
        var verifyMsg = "no verify steps configured";
        var verifyDir = t.Worktree.Length > 0 && Directory.Exists(t.Worktree) ? t.Worktree : repoPath;
        foreach (var step in cfg.Verify)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = verifyDir };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(step);
            using var p = Process.Start(psi)!;
            var errT = Task.Run(() => p.StandardError.ReadToEnd());
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                _store.Event("verify_red", null, $"ticket {tid} step '{step}': {so}{errT.Result}".Trim());
                // The merge commit STAYS. It is legitimate work the agent will fix on top of,
                // and throwing it away would mean resolving the same conflict again next round.
                // What matters is that main did not move: this returns before the ff-only.
                Announce(t, $"ticket {tid} did not land: verify RED at '{step}' after merging {cfg.Main} in — {cfg.Main} is unchanged. Fix it in {t.Worktree} and re-run dodona land {tid}");
                return $"refused: VERIFY RED at '{step}' ({mergeMsg}) — {cfg.Main} unchanged. " +
                       $"Fix it on {t.Branch} and land again.";
            }
        }
        if (cfg.Verify.Length > 0) { _store.Event("verify_green", null, $"ticket {tid}"); verifyMsg = "verify green"; }

        // ---- D-R1 step 3: the fast-forward, which is now an assertion (D-R2) --------------
        var (mc, mergeOut) = Git.Run(repoPath, "merge", "--ff-only", t.Branch);
        if (mc != 0)
        {
            // Reaching here means main moved WHILE THIS TICKET HELD THE TOKEN — the one thing
            // the token exists to prevent. It is not "the agent needs to rebase" any more, and
            // saying so would send someone to do work that is already done.
            _store.Event("land_not_ff_under_token", null, $"ticket {tid}: {cfg.Main} moved while ticket held the token. {mergeOut}");
            Announce(t, $"ticket {tid} did not land: {cfg.Main} moved while this ticket held the merge token — nothing was merged. Re-run dodona land {tid}");
            return $"refused: not fast-forward AFTER merging {cfg.Main} in — {cfg.Main} moved while ticket {tid} " +
                   $"held the merge token, which the token exists to prevent. Nothing landed; re-run land. {mergeOut}";
        }

        if (!_store.LandCommit(tid, tokenId, out var reason))
        {
            // Merge advanced main but the fence refused in the same instant (lease raced
            // out). Reconcile-from-git heals: branch is an ancestor of main.
            _store.Event("land_inconsistent", null, $"ticket {tid}: {reason}");
            return $"landed on main but store fence refused ({reason}) — run reconcile";
        }

        // Landing retires the agent BEFORE the ground is pulled from under it
        // (docs/LANE-LIFECYCLE.md §3): the prune below deletes the directory the agent is
        // standing in, and an agent left running in a deleted worktree was this system's
        // most confusing possible state. The LANE stays — dormant, visible, its thread
        // intact — because §8 says lanes group sequential work and the next ticket in
        // this area belongs here. The session id is recorded, so a future respawn can
        // resume the context.
        if (t.LaneId is long landedLane)
        {
            if (_lanes.TryGetValue(landedLane, out var lrt))
            {
                lrt.Shutdown();
                _lanes.TryRemove(landedLane, out _);
            }
            _store.LaneState(landedLane, "dormant");
            _store.LanePresence(landedLane, "landed");
            _store.PaneEvent(landedLane, "announcement",
                $"ticket {tid} landed — agent retired, lane keeps this thread", null, null, acked: true);
            _store.Event("lane_dormant", landedLane, $"ticket {tid} landed");
        }

        // Worktree prune — retryable, never silent (§15).
        var (wc, wOut) = Git.Run(repoPath, "worktree", "remove", "--force", t.Worktree);
        if (wc == 0) { Git.Run(repoPath, "branch", "-D", t.Branch); _store.Event("worktree_pruned", null, $"ticket {tid}"); }
        else _store.Event("worktree_prune_failed", null, $"ticket {tid}: {wOut}");

        ok = true;
        // Says what the flow DID, not just that it finished: "merged main in" is the
        // difference between a land that resolved against current main and one that never
        // had to, and the operator reading a receipt cannot tell them apart otherwise.
        return $"landed ticket {tid} on {(t.Repo == "." ? "" : t.Repo + "/")}{cfg.Main}; {mergeMsg}; {verifyMsg}";
    }

    /// <summary>
    /// Deploy the gate for a lane: a PreToolUse hook that asks the daemon whether a write is
    /// allowed. Returns the settings file to hand the agent, or null when this lane gets no gate.
    ///
    /// **IT WRITES NOTHING INTO ANYBODY'S REPOSITORY (D-17).** It used to write
    /// `.claude/settings.local.json` into the ticket worktree plus a block in the repo's shared
    /// `.git/info/exclude`. The operator's challenge is what killed that, and it is correct: a
    /// hook in a project's settings file binds EVERYTHING that runs Claude Code in that folder,
    /// including the operator's own IDE session. Only the process Dodona started should be gated.
    /// So the file lives in workspace state and is passed on the launch line with `--settings`.
    ///
    /// Three hazards died with it, and the first was live:
    ///
    ///  * `File.WriteAllText` on `settings.local.json` is a WHOLE-FILE OVERWRITE. Safe until now
    ///    only by accident -- a ticket worktree is a fresh checkout and the file is untracked, so
    ///    there was never one there to destroy. This phase gates the shared checkout too, where
    ///    that write would have silently wiped the developer's own allowed-commands list with
    ///    nothing in git to restore from.
    ///  * both footprints in a repo that is not the operator's to modify.
    ///  * the stale `dodona-gate.ps1` cleanup, and the generated script whose parse failure it
    ///    existed to sweep up.
    ///
    /// **`--settings` is a PRECEDENCE LAYER, NOT A REPLACEMENT**, which is the property that makes
    /// this safe: command-line settings sit above Local and Project, so the project's own settings
    /// still load, and hook entries MERGE across levels rather than replacing each other -- a
    /// repo's own PreToolUse hooks keep firing alongside this one. Two constraints fall out, both
    /// easy to get wrong and both deliberate here:
    ///
    ///  * THE FILE CONTAINS ONLY THE HOOK. Command-line settings outrank the project on any
    ///    colliding key, so a second key here would silently override what the project chose.
    ///  * NO `--setting-sources` FOR A WORK LANE. `ClaudeArgs` passes `--setting-sources user`
    ///    for utility roles on purpose; doing that to a work lane would cut the project's own
    ///    settings and hooks out of the agent doing the work -- manufacturing exactly the problem
    ///    this decision exists to avoid.
    ///
    /// MEASURED, 2026-08-20, because "the flag exists" is not "the hook fires": a PreToolUse hook
    /// supplied via `--settings <file>` DOES fire under `-p --permission-mode bypassPermissions`,
    /// and its deny is enforced -- the write never happened and the agent was told why. A control
    /// run without the flag wrote the file, so the absence is the refusal and not the model
    /// declining. (The pre-existing measurement was taken against a hook in a PROJECT file, which
    /// is a different route and no longer the one used.)
    ///
    /// AND HOOKS ARE FIXED AT SESSION START, ALSO MEASURED: a two-turn stream-json session kept
    /// firing the hook on turn 2 after it had been REMOVED from the settings file between turns.
    /// So this file is read once, at launch, and rewriting it under a live agent does nothing --
    /// which is why the gate names the LANE and lets the daemon look up the rest. A lane's ticket,
    /// claims and worktree all change during its life; the lane id does not.
    /// </summary>
    string? DeployGate(long laneId, long ticketId = 0, string? worktree = null)
    {
        var exe = Environment.ProcessPath ?? "dodona.exe";
        var cmd = $"\"{exe}\" gate-hook --lane {laneId} --workspace \"{_instanceId}\"" +
                  (ticketId > 0 ? $" --ticket {ticketId}" : "") +
                  (worktree is { Length: > 0 } ? $" --worktree \"{worktree}\"" : "");
        var hookCmd = JsonSerializer.Serialize(cmd);
        var dir = Paths.WorkspaceDir(_instanceId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"gate-lane{laneId}.json");
        // ONE KEY. See the remarks: anything else in here outranks the project's own choice.
        File.WriteAllText(file, $$"""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Edit|Write|MultiEdit|NotebookEdit",
                    "hooks": [
                      {
                        "type": "command",
                        "command": {{hookCmd}}
                      }
                    ]
                  }
                ]
              }
            }
            """);
        return file;
    }

}
