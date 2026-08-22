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
                     int MaxBrains = 6,
                     // WHICH LIFECYCLE THIS REPOSITORY'S OWN PROCESS OWNS (M5-DELIVERY-PLAN §3,
                     // which is the authority for the field and the ceremony around it;
                     // REVIEW-AND-MERGE-PLAN §7 and D-R28). Two values and one spelling of each:
                     // "local-merge" (the default, today's behaviour) and "pr".
                     string Delivery = "local-merge")
{
    public PolicyRule[] Rules => Policy ?? Dodona.Policy.Default;

    /// <summary>Is this a repository whose own CLAUDE.md and skills own the merge?
    ///
    /// `local-merge` is everything Dodona has always done: it names the branch, holds the merge
    /// token, performs the ff-only land and prunes. `pr` means the project owns all of that —
    /// Dodona never merges, never grants a merge token and never deletes a branch. It supplies
    /// the isolation and gets out of the way, and the forge's merge button is the human gate
    /// (REVIEW-AND-MERGE-PLAN §7; D-R10 is untouched, and this puts the manager further from a
    /// yes rather than closer to one).
    ///
    /// THE FOLD IS DELIBERATELY ASYMMETRIC, AND WHICH WAY IT LEANS IS THE WHOLE SAFETY ARGUMENT.
    /// Only the absent key and the exact word `local-merge` permit merging; every other value —
    /// `"PR"`, a typo, `true`, an empty string — reads as `pr`. Wrong in that direction refuses a
    /// land: loud, and entirely recoverable by fixing one word. Wrong in the other direction
    /// advances a ref in a repository whose owner said not to, which is the single irreversible
    /// act in this system (P0.1's reasoning, one field over). Nothing existing can break on the
    /// strict reading, because no repository carries this key at all yet.</summary>
    public bool IsPr => DeliveryIsPr(Delivery);

    /// <summary>The fold, separately so it can be tested without a file on disk.</summary>
    public static bool DeliveryIsPr(string? delivery) =>
        !string.Equals((delivery ?? "local-merge").Trim(), "local-merge", StringComparison.OrdinalIgnoreCase);

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

        // Read RAW, and `ToString()` rather than a fallback for a non-string, so that
        // `"delivery": true` cannot come out as "local-merge" by way of Str()'s default. The
        // fold lives in `IsPr`, with the reasoning for its direction.
        var delivery = d.RootElement.TryGetProperty("delivery", out var dv)
            ? (dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : dv.ToString())
            : "local-merge";

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
            Num("maxBrains", 6), delivery);
    }
}

sealed partial class Daemon
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

    /// <summary>
    /// INTERNAL FOR THE TEST ASSEMBLY, AND FOR NOTHING ELSE (seam S3,
    /// `docs/TEST-ARCHITECTURE-PLAN.md` §4/W8). `RunAsync` is still the only way a daemon is
    /// started for real: it takes the `Global\dodona-&lt;id&gt;` mutex, opens the store itself and
    /// does not return until stop. What this keyword buys is a `Daemon` over a temp-file
    /// `Store` with no mutex, no pipe server and no `RunAsync`, which is the only thing
    /// standing between the 45-case command surface and the ~1 second `unit` loop
    /// (`docs/testarch/seams.md` F2). It changes no behaviour: `Dodona.csproj` already grants
    /// `InternalsVisibleTo("Dodona.Tests")` and the type itself is already internal.
    /// </summary>
    internal Daemon(string primary, string wsId, string wsName, string ctlPipe, Store store)
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
    /// in which case this process exits and the predecessor stays up, unharmed.
    ///
    /// Internal because the concierge's successor performs the identical dance on its own
    /// handoff pipe (issue #9). It is already parameterised by instance id and touches no
    /// daemon state, so sharing it beat writing a second one that could drift.</summary>
    internal static async Task<int> HandshakeAsSuccessorAsync(string instanceId)
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
            // AND THE SECOND OF THE TWO WIRING SITES (B2). A daemon restarts on every publish
            // and hot swap, so a briefing wired only at the spawn would be absent from every
            // lane the operator already had -- which is the failure mode `HookTurnEnd` is
            // directly above this line to avoid, in a second costume.
            rt.TurnBriefing = BriefingFor(l.Id, l.Role, l.Cwd ?? "");
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

}
