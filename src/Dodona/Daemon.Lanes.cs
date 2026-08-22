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
    /// own, the honest instruction is: do not touch which branch is checked out, at all.
    ///
    /// **THAT PARAGRAPH NOW LIVES IN <see cref="Briefing"/>, AND SO DOES THE TICKET LANE'S COPY
    /// OF IT** (docs/LANE-BRIEFING-PLAN.md B1). It was written here and nowhere else, so the lane
    /// that actually HAS a branch and a worktree -- the ticket lane -- carried no git rules at
    /// all. One builder, consumed by both prompts and by the per-turn briefing, is what stops the
    /// two drifting again; every divergence between them was a defect.</summary>
    /// <summary>...and the folder sentence is built by <see cref="Projects.DirSentence"/>, which
    /// <see cref="Projects.PromptDirMismatch"/> also reads back — so the prompt's idea of where
    /// the agent is and the process's actual working directory are checked against each other at
    /// every spawn (trap T1). One definition, written in one place and parsed in one place.</summary>
    internal static string LaneSystemPrompt(string title, string workDir) =>
        $"You are the agent for lane \"{title}\", operated by the Dodona orchestrator.\n" +
        Briefing.Plain(workDir);

    /// <summary>The framing for a TICKET lane. Factored out of `ticket-agent` because respawn
    /// needs the identical text (M5.1) and was rebuilding the plain-lane prompt instead.
    ///
    /// **THE CLAIM SENTENCE IS GONE, AND DELETING IT IS FINISHING R3 RATHER THAN CHANGING
    /// POLICY** (docs/LANE-BRIEFING-PLAN.md D-B1). It said a PreToolUse gate denies writes
    /// outside the declared claim and to ask for an extension if refused. `REVIEW-AND-MERGE-PLAN`
    /// R3 / D-R5 retired that refusal on 2026-08-20, and `claim-extend`'s with it, so the
    /// sentence invited an agent to believe a boundary that is not there and to ask for an
    /// extension to a thing that no longer refuses anything. CLAUDE.md 7: do not describe a
    /// ticket lane as bounded to its claim. Claims survive as an annotation and as a derived
    /// signal (D-R7) -- read by the REVIEWER out of the completion record, which is why nothing
    /// is lost by the agent not being told, and why `claims` is no longer a parameter here.</summary>
    internal static string TicketSystemPrompt(long tid, string title, string branch, bool pr) =>
        $"You are a lane agent operated by the Dodona orchestrator, working ticket {tid}: \"{title}\".\n" +
        Briefing.Ticket(tid, branch, pr);

    /// <summary>WHICH TICKET A LANE IS WORKING, RESOLVED BY WORKING DIRECTORY rather than by the
    /// lane link, and extracted so the write gate and the briefing cannot answer it differently.
    ///
    /// Not a preference: `ticket-agent` calls `TicketSetLane` AFTER the spawn returns, so at
    /// spawn time the link does not exist yet, and matching on it silently produced a ticket lane
    /// with no `--ticket` -- m1's two gate checks red. A ticket lane's cwd IS its worktree (pinned
    /// by `m3:186-187` and `LaneCwdPrecedenceTests`), so the directory answers it with no ordering
    /// to get wrong. The lane link is still consulted, for a respawn whose recorded cwd drifted.</summary>
    Store.TicketRow? TicketOfLane(long laneId, string workDir) =>
        _store.Tickets().FirstOrDefault(t => t.State == "open" &&
            (t.LaneId == laneId ||
             (t.Worktree.Length > 0 && workDir.Length > 0 && Paths.SamePath(t.Worktree, workDir))));

    /// <summary>The per-turn briefing for one lane, or null when the lane gets none (B2).
    ///
    /// **WORK LANES ONLY, and the test is the same `role != "work"` `HookTurnEnd` uses**, for the
    /// same reason rather than a parallel one: a router, brain or compressor session touches no
    /// git, holds no ticket and runs in the neutral directory, so a block on every one of its
    /// turns would be tokens spent for ever on a question whose answer is always "none of this
    /// applies to you" (CLAUDE.md §0.1, quota).</summary>
    string? BriefingFor(long laneId, string role, string workDir)
    {
        if (role != "work") return null;
        var t = TicketOfLane(laneId, workDir);
        return Briefing.Turn(t is null
            ? Briefing.Plain(workDir)
            : Briefing.Ticket(t.Id, t.Branch, TicketIsPr(t)));
    }

    /// <summary>Is this ticket's REPOSITORY a `delivery: pr` one (R7 / D-R28)? Keyed on the
    /// repository and not on the project, because every other pr refusal in this file is
    /// (`land`, `token-request`, the approval question) -- and a briefing that disagreed with
    /// the refusals would be worse than no briefing. Same three-rung path resolution
    /// `LandFlow` uses, so a ticket whose repo name no longer resolves still gets an answer.</summary>
    bool TicketIsPr(Store.TicketRow t) =>
        Config.For(_primary, RepoOf(t)?.Path ?? (t.RepoPath.Length > 0 ? t.RepoPath : _primary)).IsPr;

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

    /// <summary>
    /// HOW A SHIM IS LAUNCHED, AS TWO REPLACEABLE PROBES — the seam
    /// `docs/testarch/survey-daemon.md` blocker 8 asks for, and the `Trees.Locate` shape
    /// (`Trees.cs:44` + `:77`): the real filesystem and the real launcher are bound HERE, once,
    /// so production has exactly one path and no call site chooses.
    ///
    /// **What it unblocks.** `a_missing_shim_is_named_not_guessed` and
    /// `a_failed_spawn_leaves_no_lane_claiming_alive` are a string and a state transition over
    /// one boolean, and today the only way to produce a spawn that fails is to point
    /// `DODONA_SHIM` at a nonexistent path and start a whole extra real daemon
    /// (`m0-acceptance.ps1:335` explains why it cannot even reuse one).
    ///
    /// **A FIELD RATHER THAN A PARAMETER, DELIBERATELY.** <see cref="AttachShimAsync"/> is
    /// private and is reached from the outside only through <see cref="HandleAsync"/>'s
    /// `lane-start`, which is the seam a test drives (S3); a defaulted parameter would be
    /// unreachable from there and would therefore be a seam in name only. `docs/TEST-ARCHITECTURE-PLAN.md`
    /// §3.1 names a field as a landing site for exactly this reason — what matters is that the
    /// double replaces the thing production reads, and this IS the thing production reads.
    /// </summary>
    internal Func<string, bool> ShimBinaryExists = File.Exists;

    /// <summary>The launcher itself. See <see cref="ShimBinaryExists"/>; the return value is
    /// discarded here exactly as `Process.Start(psi)` was — the daemon has never held the
    /// `Process`, it waits for the shim's PIPE to answer.</summary>
    internal Action<ProcessStartInfo> StartShim = psi => Process.Start(psi);

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
            var t = TicketOfLane(id, workDir);
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
        if (!ShimBinaryExists(shimExe))
        {
            _store.LaneState(id, "unreachable");
            var missing = $"shim binary not found: {shimExe}" +
                          (Environment.GetEnvironmentVariable("DODONA_SHIM") is null
                              ? " (looked beside this daemon; a published build has it there)"
                              : " (DODONA_SHIM points at it)");
            _store.Event("shim_spawn_failed", id, missing);
            return (-1, $"error: lane {id} not started -- {missing}");
        }
        try { StartShim(psi); }
        catch (Exception ex)
        {
            _store.LaneState(id, "unreachable");
            _store.Event("shim_spawn_failed", id, $"{ex.GetType().Name}: {ex.Message} (shim={shimExe})");
            return (-1, $"error: lane {id} not started -- could not launch {shimExe}: {ex.Message}");
        }
        _store.Event("shim_spawned", id, $"pipe={pipe} child={child} cwd={workDir}");

        var rt = new LaneRuntime(id, pipe, _store);
        HookTurnEnd(rt, role);
        rt.TurnBriefing = BriefingFor(id, role, workDir);
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

}
