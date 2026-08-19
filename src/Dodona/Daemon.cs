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
                     bool Brain = true, string BrainModel = "haiku", string BrainEffort = "low")
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
            Str("brainModel", "haiku"), Str("brainEffort", "low"));
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
    readonly Dictionary<long, LaneRuntime> _lanes = new();
    readonly SemaphoreSlim _routerLock = new(1, 1);   // one classification at a time on the warm session
    readonly SemaphoreSlim _brainLoLock = new(1, 1), _brainHiLock = new(1, 1);
    long _brainLo = -1, _brainHi = -1;                // lane ids of the two brain tiers
    long _routerLo = -1;                              // lane id of the warm input classifier
    bool _saidNoClassifier;                           // the fallback announces ONCE per daemon
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
        foreach (var l in _store.LanesAll().Where(l => l.State == "alive" && l.Role != "dispatcher"))
        {
            // The stored pipe name, hoisted once. `LaneRow.Pipe` is declared non-nullable, but
            // testing it for emptiness below is enough for the compiler to treat every earlier
            // read of it as a maybe -- and a new CS8604 in a tree that builds with zero warnings
            // is a real signal, not noise to wave through.
            var lanePipe = l.Pipe ?? "";
            var rt = new LaneRuntime(l.Id, lanePipe, _store);
            HookCompression(rt, l.Role);
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
            if (_lanes.ContainsKey(l.Id))
            {
                if (l.Role == "brain") _brainLo = l.Id;
                else if (l.Role == "brain-hi") _brainHi = l.Id;
                // ...and the ROUTER, for exactly the same reason. It was added later than the
                // brain and inherited none of the brain's lessons: adopt it or every start
                // spawns another one, retire the surplus or a store accumulates them.
                else if (l.Role == "router") _routerLo = l.Id;
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

        // Retire brain lanes the old bug already leaked. Adopting one is what stops NEW ones
        // appearing; this clears the ones a store has accumulated, so an existing instance
        // heals itself on the next start instead of needing the operator to go and count
        // processes. Utility roles only — a work lane is never retired behind the operator's
        // back (LANE-LIFECYCLE §2: no eviction, and a parked lane is often deliberate).
        foreach (var role in new[] { "brain", "brain-hi", "router" })
        {
            var keep = role switch { "brain" => _brainLo, "brain-hi" => _brainHi, _ => _routerLo };
            var surplus = _store.LanesAll()
                .Where(l => l.Role == role && l.State == "alive" && l.Id != keep)
                .ToList();
            foreach (var l in surplus)
            {
                if (_lanes.TryGetValue(l.Id, out var rt)) { rt.Shutdown(); _lanes.Remove(l.Id); }
                _store.LaneState(l.Id, "dead");
                _store.Event("brain_surplus_retired", l.Id, $"role={role}; kept lane {keep}");
            }
            if (surplus.Count > 0)
                Announce($"[dodona] retired {surplus.Count} duplicate {role.ToUpperInvariant()} lane(s) left by a fixed leak — one per daemon start; kept lane {keep}");
        }

        // Re-deploy the claim gate into every open ticket's worktree. Gate files are
        // deployment, and deployment rots: each script hard-codes the exe that wrote it,
        // and GcOldBuilds deletes old build directories — so an adopted worktree's gate
        // ends up invoking a binary that no longer exists and silently fails OPEN (found
        // live 2026-08-18: every lane older than the running build had lost enforcement
        // layer 1, with nothing but a bypass log to show for it). Two file writes per
        // ticket buys a gate that always points at the build actually running.
        try
        {
            foreach (var t in _store.Tickets().Where(t => t.State == "open" && t.Worktree.Length > 0 && Directory.Exists(t.Worktree)))
            {
                // RESOLVED BY PATH, AND NEVER SKIPPED (P0.1). This read `Repos.ByName(repos,
                // t.Repo)` and `continue`d on null — so the moment a second project was
                // attached and the repository stopped being called ".", every pre-existing
                // ticket lost gate redeployment silently, GcOldBuilds deleted the exe its
                // stale gate invoked, and enforcement layer 1 failed OPEN. A `continue` here
                // is the fail-open, so there is not one any more: a repository that has left
                // the workspace still gets its gate rewritten from the recorded path, because
                // a gate is a restriction and deploying one can only ever be safer.
                var repo = RepoOf(t)
                        ?? (t.RepoPath.Length > 0 ? new RepoRef(t.Repo, t.RepoPath, t.RepoPath) : null);
                if (repo is null)
                {
                    _store.Event("gate_redeploy_failed", null, $"ticket {t.Id}: repo '{t.Repo}' resolves to nothing and no path was recorded");
                    Announce($"[dodona] ticket {t.Id}'s claim gate could not be redeployed: repo '{t.Repo}' resolves to nothing — that agent is UNGATED until it is fixed");
                    continue;
                }
                try { DeployGate(t.Worktree, t.Id, repo); _store.Event("gate_redeployed", null, $"ticket {t.Id}: {t.Worktree}"); }
                catch (Exception ex) { _store.Event("gate_redeploy_failed", null, $"ticket {t.Id}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { _store.Event("gate_redeploy_failed", null, ex.Message); }

        // A leak this quiet needs to be visible in the chain, not just absent.
        _store.Event("reconcile_done", null,
            $"connected={_lanes.Count} brain={(_brainLo > 0 ? _brainLo.ToString() : "-")} " +
            $"brain-hi={(_brainHi > 0 ? _brainHi.ToString() : "-")} compressors={_compressorLocks.Count}");
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

                // No --child means the real thing. A lane with no ticket has no claim and
                // therefore no gate — it is plain Claude Code in the workspace, which is
                // fine for one lane and is why isolated work wants a ticket instead. T7: this
                // phase lets that ungated agent open in a SECOND repository. Not a regression
                // -- an expansion of a surface that was already ungated.
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
                    if (row.Session is { Length: > 0 } sess && !sess.StartsWith("fake-"))
                    { args2.Add("--resume"); args2.Add(sess); }
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
                    _lanes.Remove(lane);
                }
                _store.LaneState(lane, "dead");
                if (_store.KvGet("focused_lane") == lane.ToString()) _store.KvSet("focused_lane", "");
                _store.Event("lane_stopped", lane, "operator");
                w.WriteLine($"stopped lane {lane}");
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
                    w.WriteLine($"lane {l.Id}  {l.Title,-10}  role={l.Role,-6}  state={l.State}  connected={connected}  presence={l.Presence,-16}  session={l.Session ?? "-"}" +
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
                var lo = await EnsureBrainAsync(hi: false);
                var wantHi = e.TryGetProperty("hi", out var bh) && bh.ValueKind == JsonValueKind.True;
                var hi2 = wantHi ? await EnsureBrainAsync(hi: true) : -2;
                w.WriteLine($"brain: cheap tier lane {(lo > 0 ? lo.ToString() : "FAILED")}" +
                            (wantHi ? $", expensive tier lane {(hi2 > 0 ? hi2.ToString() : "FAILED")}" : ""));
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
                if (claims.Count == 0) { w.WriteLine("error: at least one --claim required"); break; }

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
                // Attach-time enforcement and the partial unique index both cover the
                // ordinary case; neither can cover a BARE FOLDER legitimately attached to
                // two workspaces (exempt, harmless) that someone later ran `git init` in.
                // Only a check at the point of use notices the ground moved — the same
                // reasoning that puts a diff backstop behind the claim gate (§6).
                try
                {
                    using var reg = new Registry();
                    if (reg.RepoConflict(repo.Path, _instanceId) is Workspace other)
                    {
                        _store.Event("ticket_repo_not_exclusive", null, $"'{title}': {repo.Path} also in {other.Id}");
                        w.WriteLine($"error: {repo.Path} also belongs to workspace \"{other.Name}\" ({other.Id})");
                        w.WriteLine("       a repo belongs to at most ONE workspace at a time — two workspaces over one");
                        w.WriteLine("       repo is two merge tokens over one main, the race this system exists to prevent");
                        w.WriteLine($"       move it:  dodona workspace-move --member \"{repo.Path}\" --workspace \"{_wsName}\"");
                        w.WriteLine("##exit 1");
                        break;
                    }
                }
                catch (Exception ex) { _store.Event("registry_unreadable", null, ex.Message); }

                var repoCfg = Config.For(_primary, repo.Path);
                if (!Git.HasCommit(repo.Path))
                {
                    w.WriteLine($"error: {repo.Name} is a git repository with no commits, so there is no '{repoCfg.Main}' to branch from");
                    w.WriteLine("       run `dodona repo-init` to make the first commit");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Both: the display name the claims were written relative to, and the canonical
                // path that says WHICH repository this is whatever it gets called later (P0.1).
                var (id, conflicts) = _store.TicketCreate(null, title, mode, repo.Name, Repos.Key(repo.Path), claims);
                if (id < 0)
                {
                    _store.Event("claim_conflict", null, $"'{title}': {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"conflict: {cf}");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Branch names are workspace-unique because ticket ids are. The worktree
                // lives beside the MEMBER holding this repository — worktrees are the one
                // piece of state that deliberately did NOT move into workspace territory
                // (WORKSPACES-CONCIERGE.md §1: they are volume- and path-sensitive, and
                // moving them buys nothing). For a one-member workspace this is the exact
                // path it has always been.
                var branch = $"ticket/{id}";
                var wt = Path.Combine(Paths.Worktrees(repo.MemberPath), $"t{id}");
                var (code, output) = Git.Run(repo.Path, "worktree", "add", "-b", branch, wt, repoCfg.Main);
                if (code != 0)
                {
                    _store.TicketState(id, "abandoned");
                    _store.Event("ticket_git_failed", null, $"ticket {id} repo {repo.Name}: {output}");
                    w.WriteLine($"error: worktree add failed in {repo.Name}: {output}");
                    break;
                }
                _store.TicketSetGit(id, branch, wt);
                DeployGate(wt, id, repo);
                _store.Event("ticket_created", null, $"ticket {id} '{title}' repo {repo.Name} branch {branch} claims [{string.Join(", ", specs)}]");
                // A single-repo project never sees the word "repo": there is only one, and
                // naming it would be noise in the ordinary case.
                w.WriteLine($"ticket {id}{RepoTag(repo.Name)} branch {branch} worktree {wt}");
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

                var conflicts = _store.ClaimExtend(tid, claims);
                if (conflicts.Count > 0)
                {
                    _store.Event("claim_conflict", null, $"extend ticket {tid}: {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"conflict: {cf}");
                    w.WriteLine("##exit 1");
                }
                else
                {
                    _store.Event("claim_extended", null, $"ticket {tid} += [{string.Join(", ", specs)}]");
                    w.WriteLine($"extended ticket {tid}");
                }
                break;
            }
            case "approve":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                _store.TicketApprove(tid);
                _store.Event("ticket_approved", null, $"ticket {tid}");
                // Unblock the lane: presence back to idle, receipt in the pane.
                if (_store.Ticket(tid)?.LaneId is long alid)
                {
                    _store.LanePresence(alid, "idle");
                    _store.PaneEvent(alid, "announcement", $"ticket {tid} approved — merge unblocked", null, null);
                }
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
                var (dc, diff) = Git.Run(reqPath, "diff", "--name-only", $"{reqCfg.Main}...{t.Branch}");
                if (dc == 0 && diff.Length > 0)
                {
                    var ticketClaims = _store.TicketClaims(tid);
                    var outside = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => Claims.Normalize(reqPrefix + f))   // git speaks repo-relative; claims are workspace-relative
                        .Where(f => !ticketClaims.Any(cl => Claims.Covers(cl.Kind, cl.Value, f)))
                        .ToList();
                    if (outside.Count > 0)
                    {
                        _store.Event("claim_backstop_refused", null, $"ticket {tid} touched outside claim: {string.Join(", ", outside)}");
                        w.WriteLine($"refused: branch touches paths outside ticket {tid}'s claim: {string.Join(", ", outside)}");
                        w.WriteLine($"         extend the claim (dodona claim-extend) or revert those changes");
                        w.WriteLine("##exit 1");
                        break;
                    }
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
            case "land":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                w.WriteLine(LandOp(tid, out var landOk));
                if (!landOk) w.WriteLine("##exit 1");
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
                foreach (var line in AnswerQuestion(e.GetProperty("id").GetInt64(),
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
                    if (_lanes.TryGetValue(l.Id, out var grt)) { grt.Shutdown(); _lanes.Remove(l.Id); }
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
                w.WriteLine("stopping (lanes keep running)");
                return true;
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
            try { Directory.Delete(dir, recursive: true); _store.Event("binary_gc", null, dir); }
            catch (Exception ex) { _store.Event("binary_gc_skipped", null, $"{dir}: {ex.Message}"); }
        }
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
    List<string> AnswerQuestion(long id, string answer)
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

        // `withdrawn`, not `answered`, for a declined question: the two are different facts and
        // a later "why is there no repo" wants to know which one happened.
        var declined = picked.Value.Equals("no", StringComparison.OrdinalIgnoreCase);
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
            // A kind with no case here answers the ROW and does nothing else, which is the right
            // default for a question that was only ever "tell me which one" — the caller reads the
            // answer off the row. The next one to arrive is routing's rung 4 (LOCATIONS-PLAN P3.A):
            // it needs a case, because delivering the held sentence to the chosen project is
            // `SpawnForAsync`, which belongs to Phase 3.
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
    async Task<(long Id, string Msg)> SpawnLaneAsync(string title, string role, string workDir, string child, List<string> childArgs)
    {
        var id = _store.LaneCreate(title);
        _store.LaneRole(id, role);
        return await AttachShimAsync(id, title, role, workDir, child, childArgs);
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

    async Task<(long Id, string Msg)> AttachShimAsync(long id, string title, string role, string workDir, string child, List<string> childArgs)
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
        HookCompression(rt, role);
        if (await rt.ConnectAndPumpAsync(attempts: 20))
        {
            _lanes[id] = rt;
            _store.Event("lane_started", id, $"{title} role={role}");
            return (id, $"lane {id} title {title} role {role} pipe {pipe}");
        }
        _store.LaneState(id, "unreachable");
        return (-1, $"error: lane {id} shim pipe never answered");
    }

    // ------------------------------------------------------------- selective compression (§5)

    /// <summary>Only WORK lanes get their turn-finals compressed. A compressor whose own
    /// result was compressed would ask itself to summarise its summary, forever.</summary>
    void HookCompression(LaneRuntime rt, string role)
    {
        if (role == "work") rt.OnResult = CompressResult;
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
        if (!await ClearOfLivePredecessorsAsync("compressor"))
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
        if (!await ClearOfLivePredecessorsAsync("router")) return -1;
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
    async Task<bool> ClearOfLivePredecessorsAsync(params string[] roles)
    {
        var candidates = _store.LanesAll()
            .Where(l => roles.Contains(l.Role) && l.State is "alive" or "unreachable")
            .Where(l => !(_lanes.TryGetValue(l.Id, out var rt) && rt.Connected))
            .Where(l => l.Pipe is { Length: > 0 })
            .ToList();
        if (candidates.Count == 0) return true;

        var live = LaneLiveness.Live(_instanceId, Paths.WorkspaceDir(_instanceId));
        var clear = true;
        foreach (var l in candidates.Where(l => live.Contains(l.Id)))
        {
            // ONCE PER LANE. This runs from EnsureRouterAsync, which is on the path of every
            // routed sentence the operator types -- a poke plus a wait on each of them would be
            // seconds of latency per keystroke-to-lane, paid forever, for a message the shim has
            // already declined once.
            if (!_shutdownAsked.Add(l.Id)) { clear = false; continue; }
            var told = await LaneRuntime.ShutdownShimAsync(l.Pipe!);
            var gone = told && await LaneRuntime.WaitPipeGoneAsync(l.Pipe!);
            _lanes.Remove(l.Id);           // whatever we had, it is not usable
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

    /// <summary>Lanes already told to go. See the loop above for why asking twice is not free.</summary>
    readonly HashSet<long> _shutdownAsked = new();

    /// <summary>The middle rung of the escalation ladder: management judgement between
    /// code-that-checks-facts and the operator-who-decides-intent. Two warm sessions —
    /// cheap for the everyday calls, expensive only when the cheap one says it is not
    /// sure (operator's rule). It is deliberately kept AWAY from code: neutral cwd, no
    /// project CLAUDE.md, no skills, no tools it could run — its whole world is the
    /// management question in front of it.</summary>
    async Task<long> EnsureBrainAsync(bool hi)
    {
        var current = hi ? _brainHi : _brainLo;
        if (current > 0 && _lanes.TryGetValue(current, out var live) && live.Connected) return current;
        if (!_config.Brain) return -1;
        if (!await ClearOfLivePredecessorsAsync(hi ? "brain-hi" : "brain")) return -1;

        var sys = "You are Dodona's dispatcher brain. You make MANAGEMENT decisions for a multi-agent " +
                  "orchestrator: what a piece of work should be called, which lane an input belongs to, whether work " +
                  "deserves its own ticket and which paths that ticket should claim. You never read or write code, " +
                  "never run tools, and never do the work yourself — you are the coordinator's judgement, not a worker. " +
                  "Answer ONLY in the single-line JSON schema each request specifies: no prose, no markdown, no code fences. " +
                  "State your confidence honestly — saying low is how hard questions reach someone with more budget than you.";
        var model = hi ? _config.Model : _config.BrainModel;
        var effort = hi ? _config.Effort : _config.BrainEffort;
        var args = IsClaude(_config.Agent) ? ClaudeArgs(_config, model, effort, sys, acceptEdits: false, utility: true) : new List<string>();
        var (id, msg) = await SpawnLaneAsync(hi ? "BRAIN-HI" : "BRAIN", hi ? "brain-hi" : "brain", NeutralCwd(), _config.Agent, args);
        if (id < 0) { _store.Event("brain_failed", null, msg); return -1; }
        if (hi) _brainHi = id; else _brainLo = id;
        return id;
    }

    /// <summary>Ask the expensive tier (spawning it on first use). Null when the brain is
    /// off, failed to start, or timed out — callers treat null as "the status quo stands",
    /// because the brain is an improver, never a gate.</summary>
    async Task<JsonElement?> AskBrainHiAsync(string question)
    {
        var id = await EnsureBrainAsync(hi: true);
        if (id < 0) return null;
        await _brainHiLock.WaitAsync();
        string? reply;
        try { reply = await _lanes[id].AskAsync(question, 30000); }
        finally { _brainHiLock.Release(); }
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
                var loId = await EnsureBrainAsync(hi: false);
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

                await _brainLoLock.WaitAsync();
                string? reply;
                try { reply = await _lanes[loId].AskAsync(q, 25000); }
                finally { _brainLoLock.Release(); }
                if (reply is null) { _store.Event("brain_timeout", loId, $"review lane {laneId}"); return; }

                JsonElement v;
                try { v = JsonDocument.Parse(reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)]).RootElement.Clone(); }
                catch { _store.Event("brain_failed", loId, $"unparseable: {Truncate(reply, 120)}"); return; }

                // Cheap tier unsure → same question, expensive tier (operator's rule #1).
                var conf = v.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "low" : "low";
                if (conf == "low")
                {
                    _store.Event("brain_escalated", loId, $"review lane {laneId}");
                    var hiV = await AskBrainHiAsync(q);
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
        var hi = await AskBrainHiAsync(
            "Decide where one line of operator input belongs in a multi-agent orchestrator.\n" +
            FactSheet(text, work, focusedRow) +
            "A distinct task should get its OWN new lane — new agent, clean context, and lanes are cheap. " +
            "An existing lane keeps it only when the input clearly continues that lane's thread: either it is " +
            "aimed at work that lane is doing now, or it is a small correction to what that lane just finished.\n" +
            "Reply ONLY one line of JSON: {\"kind\":\"generic|addendum|new-task|unclear\",\"target\":\"<LANE TITLE for addendum>\"," +
            "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars\"}");

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
            var last = _store.Tail(l.Id, 1).FirstOrDefault() ?? "";
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
            return candidates.FirstOrDefault(x =>
                ProjectLadder.Leaf(x).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                x.Equals(name, StringComparison.OrdinalIgnoreCase));
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
    async Task<(long Id, string Msg, Choice Choice)> SpawnForAsync(string text, string? ovModel, string? ovEffort)
    {
        var name = NameFromText(text);
        var choice = Policy.Resolve(text, _config.Rules, _config.Model, _config.Effort, ovModel, ovEffort);

        // PHASE 3'S ONE LINE. This used to be `_primary` -- the first project, always, with a
        // comment saying that choosing one from a sentence was Phase 3's job. It is.
        var pv = await ResolveProjectAsync(text);
        if (pv.Project is null)
        {
            // Rung 4: HOLD. No lane row, nothing said to any agent -- the same shape the lane
            // ladder's own top rung uses, and the same reason (`held_input_invents_no_lane`).
            var list = pv.Candidates.Count == 0 ? "none" : string.Join(" / ", pv.Candidates.Select(ProjectLadder.Leaf));
            _store.RoutingInsert(text, "ask", null, null, "no-project");
            _store.Event("project_unknown", null, $"how={pv.How} candidates={list} input={Truncate(text, 80)}");
            Announce($"[dodona] not sure which project “{Truncate(text, 45)}” is for — NOT delivered yet. " +
                     $"Projects here: {list}. Name one in the sentence, or " +
                     $"`dodona lane-start --title <NAME> --project <path>` and say it there.");
            return (-1, $"held: not sure which project this is for — nothing was delivered. " +
                        $"Name one of {list} in the sentence, or start a lane with --project.", choice);
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


    /// <summary>The land (§7): the daemon executes the one atomic ref advance. The agent
    /// already rebased and verified in its own worktree; ff-only IS the freshness check —
    /// a branch that does not contain current main cannot land.</summary>
    string LandOp(long tid, out bool ok)
    {
        ok = false;
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

        var (hc, head) = Git.Run(repoPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (hc != 0 || head != cfg.Main) return $"refused: {where} has '{head}' checked out, not '{cfg.Main}'";

        var (mc, mergeOut) = Git.Run(repoPath, "merge", "--ff-only", t.Branch);
        if (mc != 0)
        {
            _store.Event("land_refused", null, $"ticket {tid}: ff-only failed — rebase needed. {mergeOut}");
            return $"refused: not fast-forward — rebase {t.Branch} onto {cfg.Main} and re-verify first. {mergeOut}";
        }

        if (!_store.LandCommit(tid, tokenId, out var reason))
        {
            // Merge advanced main but the fence refused in the same instant (lease raced
            // out). Reconcile-from-git heals: branch is an ancestor of main.
            _store.Event("land_inconsistent", null, $"ticket {tid}: {reason}");
            return $"landed on main but store fence refused ({reason}) — run reconcile";
        }

        // Post-land verify (§10): the daemon — code, not a model — runs the configured
        // steps, in the repository that just changed.
        var verifyMsg = "no verify steps configured";
        foreach (var step in cfg.Verify)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repoPath };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(step);
            using var p = Process.Start(psi)!;
            var errT = Task.Run(() => p.StandardError.ReadToEnd());
            var so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                _store.Event("verify_red", null, $"ticket {tid} step '{step}': {so}{errT.Result}".Trim());
                verifyMsg = $"VERIFY RED at '{step}'";
                goto verified;
            }
        }
        if (cfg.Verify.Length > 0) { _store.Event("verify_green", null, $"ticket {tid}"); verifyMsg = "verify green"; }
        verified:

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
                _lanes.Remove(landedLane);
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
        return $"landed ticket {tid} on {(t.Repo == "." ? "" : t.Repo + "/")}{cfg.Main}; {verifyMsg}";
    }

    /// <summary>Deploy the claim gate (§6 enforcement layer 1) into a ticket's worktree:
    /// a PreToolUse hook that asks the daemon whether the write is covered. Fails OPEN
    /// (logged) — the merge-time backstop catches what slips; a broken gate must not
    /// brick the lane.</summary>
    void DeployGate(string worktree, long ticketId, RepoRef repo)
    {
        // The gate files are deployment, not repo content: register them in the repo's
        // shared info/exclude (applies to every worktree) so `git add -A` by an agent
        // can never commit them — a ticket-1 gate landing on main conflicts with every
        // other ticket's gate on rebase. (Found by the M1 acceptance test.)
        // The exclude file belongs to the TICKET'S repository, not the workspace.
        //
        // The gate lives in settings.LOCAL.json, and that is the whole answer to "what
        // about a repo with its own tracked .claude/": Claude Code merges local settings
        // over project settings, so the repo's tracked settings.json is never touched,
        // never shows as modified in the worktree, and its own hooks keep running
        // alongside the gate. Writing settings.json here used to OVERWRITE the tracked
        // file in the working copy — info/exclude does not untrack anything, so the
        // agent saw a dirty file it did not change and the repo lost its hooks.
        var exclude = Path.Combine(repo.Path, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        var marker = "# dodona-gate deployment files";
        if (!File.Exists(exclude) || !File.ReadAllText(exclude).Contains(marker))
            File.AppendAllText(exclude, $"\n{marker}\n.claude/settings.local.json\ndodona-gate.ps1\n.dodona-bypass.log\n");

        // The hook is dodona.exe itself (`gate-hook`), not a generated PowerShell script. A
        // .ps1 that fails to parse runs NOTHING -- it denies nothing while still being
        // registered and still looking installed, which is a live failure this project has
        // already paid for. The same mistake in C# cannot be shipped, because it does not
        // compile. It is also one process instead of two: the script's whole job was to read
        // stdin, shell out to this same binary, and format the refusal.
        var exe = Environment.ProcessPath ?? "dodona.exe";
        var hookCmd = JsonSerializer.Serialize($"\"{exe}\" gate-hook --ticket {ticketId} " +
                                               $"--workspace \"{_instanceId}\" --worktree \"{worktree}\"");
        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        File.WriteAllText(Path.Combine(worktree, ".claude", "settings.local.json"), $$"""
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

        // A worktree adopted from an older build may still carry the generated script. Remove
        // it, so there is exactly one gate and nobody debugs the one that is no longer wired.
        try
        {
            var stale = Path.Combine(worktree, "dodona-gate.ps1");
            if (File.Exists(stale)) File.Delete(stale);
        }
        catch { /* a leftover file is untidy, never fatal */ }
    }
}
