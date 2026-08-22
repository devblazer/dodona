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
            var lane = work.FirstOrDefault(l => TitleMatches(l.Title, prefix.Value.Target));
            if (lane is not null && _lanes.TryGetValue(lane.Id, out var rt0))
            {
                rt0.Say(prefix.Value.Body);
                _store.RoutingInsert(text, "prefix", lane.Id, lane.Id, "explicit");
                return Tier0Verdict(lane.Title);
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
        var focus = FocusPick(_store.KvGet("focused_lane"), live.Select(l => l.Id).ToList());
        long fid = focus.Id;
        if (focus.Picked)
        {
            var pick = live.First(l => l.Id == fid);
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
            return GenericVerdict(focusedRow.Title);
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
                var notice = UnroutedNotice(_config.Brain);
                _store.Event("routing_unrouted", null, notice.Detail);
                Announce(notice.Announcement);
            }
            return NoClassifierVerdict(focusedRow.Title, ovModel, ovEffort);
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
            return GenericVerdict(focusedRow.Title);
        }

        // ---- addendum: an existing lane's thread continues. -----------------------------
        if (kind == "addendum" && conf != "low")
        {
            var tLane = work.FirstOrDefault(l => TitleMatches(l.Title, target));
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
            var hLane = work.FirstOrDefault(l => TitleMatches(l.Title, hTarget));
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

    // ---------------------------------------------------------------------------------------
    // THE RUNGS BELOW `LanePrefix` AND `IsObviousGeneric`, AND THE OTHER SENTENCES THE DAEMON
    // ASSEMBLES BY HAND.
    //
    // `docs/testarch/survey-daemon.md` blocker 3, verbatim: *"Verdict strings are produced
    // inside `RouteInput`, not by a function."* `LanePrefix` and `IsObviousGeneric` were pulled
    // out for exactly this reason at P4.5 (their comments say so) and the rungs BELOW them were
    // not, so five m2 checks — `tier0_prefix_routes`, `focus_routes_optimistically`,
    // `stale_focus_falls_back_to_a_live_lane`, `unrouted_fallback_is_announced`,
    // `routing_rows_recorded` — still need a real daemon, a real store and two real lanes to
    // read one sentence back.
    //
    // NONE OF THIS DECIDES ANYTHING NEW. Each one is the expression that was inline, moved
    // whole; what was I/O at the call site is still I/O at the call site. That is the whole
    // contract of a seam commit (`docs/TEST-ARCHITECTURE-PLAN.md` §9.3, commit A).
    // ---------------------------------------------------------------------------------------

    /// <summary>How a routing target names a lane: by TITLE, case-insensitively, at all three
    /// sites that do it (tier 0, the classifier's addendum, the escalated addendum). A null
    /// target from a model that answered without one matches nothing, which is what the
    /// `target ?? ""` at those sites has always meant.</summary>
    internal static bool TitleMatches(string laneTitle, string? target) =>
        laneTitle.Equals(target ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verdict `RouteInput` returns when a `LANE:` prefix found its lane.</summary>
    internal static string Tier0Verdict(string laneTitle) => $"-> {laneTitle} (tier 0)";

    /// <summary>The verdict for a generic — the focused lane, whether it was decided in code
    /// (tier 0.5) or by the classifier. One string, because it is one outcome.</summary>
    internal static string GenericVerdict(string laneTitle) => $"-> {laneTitle} (generic)";

    /// <summary>
    /// WHO IS FOCUSED, over the recorded `focused_lane` and the lanes that are actually live.
    /// With no focus, PICK rather than refuse (§11): the newest lane is the one you just made.
    /// `Picked` is true exactly when the caller must write the kv back and say so in the pane —
    /// i.e. when the recorded lane is missing, unparseable, or no longer live, which is what
    /// `stale_focus_falls_back_to_a_live_lane` is about. Callers only reach this with at least
    /// one live lane; the empty case answers 0 rather than throwing, because a decision function
    /// that throws is a worse diagnosis than one that returns.
    /// </summary>
    internal static (long Id, bool Picked) FocusPick(string? focusedKv, IReadOnlyList<long> liveIds)
    {
        if (focusedKv is not null && long.TryParse(focusedKv, out var f0) && liveIds.Contains(f0))
            return (f0, false);
        return liveIds.Count == 0 ? (0, true) : (liveIds[^1], true);
    }

    /// <summary>
    /// WHAT THE DAEMON SAYS WHEN THERE IS NO CLASSIFIER — an event detail and one announcement,
    /// once per daemon. A permanent silent downgrade to "whatever is focused" is exactly the
    /// quietly-stale state CLAUDE.md §0.1 forbids: the operator typed for two days into a system
    /// whose routing had been off the whole time, and the only evidence was a status-line suffix
    /// nobody reads. `unrouted_fallback_is_announced` is the check, and E8 in
    /// `docs/TEST-ARCHITECTURE-PLAN.md` §2.3 is why it is one of the thirteen never skipped —
    /// keeping the detector and dropping the alarm is how those two days happened.
    /// </summary>
    internal static (string Detail, string Announcement) UnroutedNotice(bool brainEnabled) =>
        brainEnabled
            ? ("classifier would not start",
               "[dodona] the input classifier will not start — every sentence is going to the FOCUSED lane until it does. `dodona router-start` to retry.")
            : ("brain disabled in config",
               "[dodona] brain is off in dodona.json — routing is focused-lane only; a distinct task will NOT get its own lane.");

    /// <summary>The verdict for the no-classifier fallback, carrying the stale-override note: a
    /// model/effort override is applied when a lane STARTS, and this sentence went to one that
    /// is already running, so saying nothing would let the operator believe it took.</summary>
    internal static string NoClassifierVerdict(string laneTitle, string? ovModel, string? ovEffort) =>
        $"-> {laneTitle} (focus, no classifier warm)" +
        (ovModel is not null || ovEffort is not null
            ? "  (model/effort is set when a lane starts — this one is already running)" : "");

    /// <summary>
    /// WHAT A BRANCH TOUCHED, RECORDED AND NOT JUDGED (D-R5/D-R7, R3) — the `branch_touched`
    /// detail, assembled out of `git diff --name-only` and the ticket's declared claims. It was
    /// written inline in the `token-request` case, so four m2 checks needed a live daemon and a
    /// real git repository to read a string built from two lists
    /// (`docs/testarch/survey-daemon.md` blocker 4).
    ///
    /// The caller keeps the `dc == 0 &amp;&amp; diff.Length > 0` guard, so this is only ever
    /// asked about a diff that produced output — an empty `touched` after normalisation still
    /// renders "touched 0 path(s): ", exactly as it did inline.
    /// </summary>
    internal static string BranchTouchedDetail(long ticketId, string diff, string claimPrefix,
                                               IReadOnlyList<(string Kind, string Value)> claims)
    {
        var touched = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Claims.Normalize(claimPrefix + f.Trim()))   // git speaks repo-relative; claims are workspace-relative
            .Where(f => f.Length > 0)
            .ToList();
        var undeclared = touched.Where(f => !claims.Any(cl => Claims.Covers(cl.Kind, cl.Value, f))).ToList();
        return $"ticket {ticketId} touched {touched.Count} path(s): {string.Join(", ", touched)}" +
               (undeclared.Count > 0 ? $" | undeclared: {string.Join(", ", undeclared)}" : "");
    }

    /// <summary>The words of the repo-init question, and the leaf its choices are built from —
    /// see <see cref="AskForRepo"/> for the idempotency, which is a store question and stays
    /// there. A project path whose leaf is empty answers with the path, because a question that
    /// names nothing is worse than a question that names too much.</summary>
    internal static (string Leaf, string Text) RepoInitAsk(string project, string forWhat)
    {
        var leaf = Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (leaf.Length == 0) leaf = project;
        return (leaf, $"{leaf} has no git repo, so \"{forWhat}\" cannot become a ticket. Create one?");
    }

    /// <summary>The words of the held-sentence question. The SUBJECT column keeps the sentence
    /// whole because answering delivers it; this is the line a person reads, so this is the one
    /// that gets shortened (<see cref="AskWhichProject"/>).</summary>
    internal static string WhichProjectAskText(string text) =>
        $"Which project is “{Truncate(text, 60)}” for?";

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


}
