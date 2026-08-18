using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Dodona;

/// <summary>
/// The concierge's config (docs/WORKSPACES-CONCIERGE.md §2): its own file beside its own
/// store, because it belongs to no workspace and so no workspace's `dodona.json` can serve
/// it. Same pattern as `dodona.json`, one level up.
/// </summary>
sealed record ConciergeConfig(
    string Agent = "claude",
    // Two tiers, and the cheap one does the steady-state work (§2.2 / CLAUDE.md §0.1 quota
    // discipline). The expensive tier exists to be reached rarely.
    string LoModel = "haiku", string LoEffort = "low",
    string HiModel = "opus", string HiEffort = "high",
    // Rung 3's fence, beyond the parents of registered members (Fence.Roots).
    string[]? SearchRoots = null,
    // Off ⇒ rungs 2 and 3 are skipped and an unresolved sentence goes straight to rung 4.
    // A concierge with no models is still useful: rung 1 is code and is the steady state.
    bool Models = true)
{
    public string[] Roots => SearchRoots ?? Array.Empty<string>();

    public static ConciergeConfig Load()
    {
        var path = Paths.ConciergeConfig;
        if (!File.Exists(path)) return new ConciergeConfig();
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            var e = d.RootElement;
            string Str(string k, string fb) =>
                e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? fb : fb;
            string[]? roots = null;
            if (e.TryGetProperty("searchRoots", out var sr) && sr.ValueKind == JsonValueKind.Array)
                roots = sr.EnumerateArray().Select(x => x.GetString()!).Where(x => x is { Length: > 0 }).ToArray();
            return new ConciergeConfig(
                Str("agent", "claude"),
                Str("loModel", "haiku"), Str("loEffort", "low"),
                Str("hiModel", "opus"), Str("hiEffort", "high"),
                roots,
                !(e.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.False));
        }
        catch { return new ConciergeConfig(); }
    }
}

/// <summary>What the ladder decided, and by which rung.</summary>
sealed record Verdict(string Rung, string? WorkspaceId, string? WorkspaceName, string Confidence,
                      bool Created = false, long? QuestionId = null, string? Note = null);

/// <summary>
/// The concierge (docs/WORKSPACES-CONCIERGE.md §2). One per machine.
///
/// **It is daemon-natured, not view-natured**, and that is an argument rather than a
/// preference: it holds pending operator questions and routing state that must survive a
/// window close, and m3 doctrine says the UI owns nothing — so it cannot live in the UI
/// process. It runs its management models in a neutral cwd with `--setting-sources user`,
/// exactly like the workspace brain, for the commit-19dad3d reason: a manager that reads a
/// worker's orders is how a classifier ends up running /ship.
///
/// **It owns exactly three things**, and the cap is the design:
///   1. the registry — as the thing that RESOLVES and LEARNS from it (every clarification
///      becomes an alias, so asking decays toward not asking);
///   2. the group-scope routing ladder, at full depth — cheap → expensive → ask the
///      operator. Its question is narrow: *which workspace, and how confident*. Lane choice,
///      naming, tickets and claims stay with the per-workspace brain (§8 rejected one merged
///      manager: two managers wearing one trench coat);
///   3. a review-behind net for group-misses — the per-workspace brain can catch
///      wrong-lane-within-workspace but structurally cannot catch wrong-workspace, because
///      it does not know other workspaces exist (§14).
///
/// **It holds no lanes, no claims, no merge tokens, and no workspace daemon ever reads its
/// store.** The moment it coordinates work rather than routing sentences it becomes the
/// persistent-coordinator serialization point §12 designed out.
///
/// Every model call sits behind the same fake-agent seam the router uses, and rung 3 is made
/// deterministic by the fence (Fence.cs), so the whole ladder is testable with zero model
/// calls (§17).
/// </summary>
sealed class Concierge
{
    /// <summary>A fixed id, not a generated slug: there is exactly one per machine, and its
    /// pipe name has to be discoverable by a client that has read nothing. A workspace slug
    /// is always `&lt;name&gt;-&lt;4 hex&gt;`, so `concierge` can never collide with one.</summary>
    public const string Id = "concierge";

    readonly ConciergeStore _store;
    readonly SemaphoreSlim _loLock = new(1, 1), _hiLock = new(1, 1);
    readonly Dictionary<long, LaneRuntime> _tiers = new();
    ConciergeConfig _config;

    Concierge(ConciergeStore store)
    {
        _store = store;
        _config = ConciergeConfig.Load();
    }

    public static async Task<int> RunAsync()
    {
        // One concierge per machine, enforced at the OS — the same guard the daemon uses,
        // for the same reason: two of these would be two registries' worth of opinions and
        // two sets of pending questions.
        using var mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{Id}", out bool createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("a concierge is already running on this machine");
            return 3;
        }
        using var store = new ConciergeStore(Paths.ConciergeStore);
        return await new Concierge(store).LoopAsync();
    }

    async Task<int> LoopAsync()
    {
        var ctl = Instance.CtlPipe(Id);
        _store.Event("concierge_start", null, $"pid={Environment.ProcessId} build={Ver.Build} store={Paths.ConciergeStore}");
        Console.WriteLine($"dodona concierge: ctl pipe {ctl}, pid {Environment.ProcessId}, build {Ver.Build}");

        // Reconcile the management tiers the same way the daemon reconciles lanes: the rows
        // are the claim, the pipe is the proof. A concierge that restarted while a tier's
        // shim is still up adopts it rather than spawning a second one.
        foreach (var t in _store.Tiers().Where(t => t.State == "alive" && t.Pipe.Length > 0))
        {
            var rt = new LaneRuntime(t.Id, t.Pipe, _store);
            if (await rt.ConnectAndPumpAsync(attempts: 3)) _tiers[t.Id] = rt;
            else _store.TierState(t.Id, "unreachable");
        }
        _store.Event("concierge_reconciled", null, $"tiers={_tiers.Count}");

        bool stopping = false;
        while (!stopping)
        {
            var server = new NamedPipeServerStream(ctl, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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

        _store.Event("concierge_stop", null, "graceful");
        return 0;
    }

    async Task<bool> HandleAsync(string req, StreamWriter w)
    {
        using var d = JsonDocument.Parse(req);
        var e = d.RootElement;
        switch (e.GetProperty("cmd").GetString())
        {
            case "status":
            {
                using var reg = new Registry();
                w.WriteLine($"concierge pid={Environment.ProcessId} build={Ver.Build} store={Paths.ConciergeStore}");
                w.WriteLine($"models={_config.Models} lo={_config.LoModel}/{_config.LoEffort} hi={_config.HiModel}/{_config.HiEffort} agent={_config.Agent}");
                foreach (var t in _store.Tiers())
                    w.WriteLine($"tier {t.Id} {t.Name,-6} state={t.State} connected={_tiers.ContainsKey(t.Id) && _tiers[t.Id].Connected} session={t.Session ?? "-"}");
                var roots = Fence.Roots(reg, _config.Roots);
                w.WriteLine($"fence: {(roots.Count == 0 ? "(empty — no members registered yet)" : string.Join(", ", roots))}");
                foreach (var ws in reg.All())
                    w.WriteLine($"workspace {ws.Name,-16} {ws.Id,-20} live={Instance.IsLive(ws.Id)} members={ws.Members.Count}");
                foreach (var q in _store.OpenQuestions())
                    w.WriteLine($"question {q.Id} open: {Truncate(q.Input, 60)}");
                break;
            }

            case "resolve":
            {
                var v = await ResolveAsync(e.GetProperty("text").GetString()!, Opt(e, "from"));
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    rung = v.Rung, workspace = v.WorkspaceId, name = v.WorkspaceName,
                    confidence = v.Confidence, created = v.Created, question = v.QuestionId, note = v.Note,
                }));
                break;
            }

            case "questions":
                foreach (var q in _store.OpenQuestions())
                    w.WriteLine($"{q.Id}\t{q.Input}\t{q.Candidates}");
                break;

            case "answer":
            {
                foreach (var line in Answer(e.GetProperty("id").GetInt64(), e.GetProperty("answer").GetString()!))
                    w.WriteLine(line);
                break;
            }

            case "feed":
            {
                var n = e.TryGetProperty("n", out var nn) && nn.ValueKind == JsonValueKind.Number ? nn.GetInt32() : 30;
                foreach (var f in _store.Feed(n))
                    w.WriteLine($"{f.Id}\t{f.Ts}\t{(f.Acked ? "acked" : "open ")}\t{f.Body}");
                break;
            }

            case "ack":
                w.WriteLine(_store.FeedAck(e.GetProperty("id").GetInt64()) ? "acked" : "error: no such feed row");
                break;

            case "review":
                // Fire-and-forget by contract (§2.3): the delivery already happened, and a
                // review that could delay it would defeat the point of delivering optimistically.
                ReviewBehind(e.GetProperty("text").GetString()!, e.GetProperty("workspace").GetString()!);
                w.WriteLine("reviewing behind");
                break;

            case "stop":
                w.WriteLine("stopping");
                foreach (var t in _tiers.Values) t.Shutdown();
                return true;
        }
        return false;
    }

    static string? Opt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // =====================================================================================
    // The ladder (§2.2 + §4). Cheapest first, and the cheap rungs are code so the steady
    // state costs nothing at all.
    //
    // §2.2 names the tiers (cheap → expensive → ask) and §4 names the rungs (exact → fuzzy
    // → bounded discovery → ask-and-teach). They are the same ladder seen from two angles —
    // both answer "which workspace, and how confident", both escalate in the same order — so
    // this is one implementation rather than two that could disagree. Recorded as a decision
    // in the doc's §2.1.
    // =====================================================================================

    async Task<Verdict> ResolveAsync(string text, string? from)
    {
        var sw = Stopwatch.StartNew();
        using var reg = new Registry();
        var all = reg.All();

        Verdict Done(Verdict v)
        {
            _store.ResolutionInsert(text, v.Rung, v.WorkspaceId, v.Confidence, v.Created, sw.ElapsedMilliseconds);
            _store.Event("resolved", null, $"rung={v.Rung} ws={v.WorkspaceName ?? "-"} conf={v.Confidence} input={Truncate(text, 80)}");
            return v;
        }

        // ---- rung 0: an explicit path. Explicit information NEVER triggers a search (§4):
        // the operator already said where, and rediscovering it with a model call would be
        // spending money to learn something we were told.
        if (Fence.ExplicitPath(text) is string path)
        {
            if (reg.Owner(path) is { } owner)
                return Done(new Verdict("path", owner.Ws.Id, owner.Ws.Name, "explicit"));
            // Not owned → attach it outright, as its own workspace. Reversible (§11), and
            // announced, because a workspace appearing is exactly the kind of thing the
            // operator must be able to see afterwards.
            try
            {
                var made = WorkspaceResolve.ForPath(reg, path);
                Announce($"[dodona] new workspace “{made.Ws.Name}” for {path} — undo: dodona workspace-forget --workspace {made.Ws.Id}");
                return Done(new Verdict("path", made.Ws.Id, made.Ws.Name, "explicit", Created: true, Note: made.Note));
            }
            catch (Exception ex) { return Done(new Verdict("path", null, null, "refused", Note: ex.Message)); }
        }

        // ---- rung 1: exact name or alias, in code, no model. THE STEADY-STATE PATH, and it
        // must never cost a token. Longest name first so "work-ui" beats "work".
        foreach (var ws in all.OrderByDescending(x => x.Name.Length))
            foreach (var handle in new[] { ws.Name }.Concat(ws.Aliases))
                if (Mentions(text, handle))
                    return Done(new Verdict("registry", ws.Id, ws.Name, "explicit"));

        // ---- rung 1b: there is only one workspace, so there is no group-scope question to
        // answer. A single-workspace machine — which is every machine until the operator
        // makes a second one — never reaches a model here at all (CLAUDE.md §0.1: quota is
        // the scarce resource).
        if (all.Count == 1) return Done(new Verdict("only", all[0].Id, all[0].Name, "sole"));

        // Optimistic focus: with several workspaces and no name, the one the operator is
        // looking at is the honest default. It is delivered immediately and the review-behind
        // (§2.3) is what catches a wrong-workspace guess — the same act/announce/undo shape
        // the per-workspace router uses one level down.
        var focus = from ?? _store.KvGet("focused_workspace");
        var focused = focus is null ? null : all.FirstOrDefault(x => x.Id.Equals(focus, StringComparison.OrdinalIgnoreCase));

        if (!_config.Models)
        {
            // No models configured: skip straight to asking rather than guessing. Rung 1 is
            // still code and still works, which is why this is a usable configuration.
            if (focused is not null) return Done(new Verdict("focus", focused.Id, focused.Name, "no-models"));
            return Done(Ask(text, all, "the concierge has no models configured"));
        }

        // ---- rung 2: fuzzy, on the CHEAP tier. Voice-typed "blazing some of the trumpets"
        // against registry names and recents.
        var recents = _store.Resolutions(12).Where(r => r.WorkspaceId is not null)
            .Select(r => r.WorkspaceId!).Distinct()
            .Select(id => all.FirstOrDefault(x => x.Id == id)?.Name).Where(n => n is not null).Take(5).ToList();
        var names = string.Join(", ", all.Select(x => $"\"{x.Name}\""));
        var lo = await AskTierAsync(TierLoQuestion(text, names, recents!, focused?.Name), hi: false);

        if (lo is JsonElement loV)
        {
            var conf = Str(loV, "confidence") ?? "low";
            var target = Str(loV, "workspace");
            var hit = target is null ? null : all.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (hit is not null && conf != "low")
            {
                // Confident → act, and announce with an undo (§11 applied to workspace wake).
                if (focused is null || hit.Id != focused.Id)
                    Announce($"[dodona] “{Truncate(text, 40)}” → workspace {hit.Name} (fuzzy match, {conf})");
                return Done(new Verdict("fuzzy", hit.Id, hit.Name, conf));
            }
        }

        // ---- rung 3: bounded discovery, on the EXPENSIVE tier, inside the fence. The one
        // narrow exception to "management brains never run tools" — a classifier with a
        // flashlight (Fence.cs). It falls to rung 4 fast, and the fence NEVER widens itself.
        var candidateName = lo is JsonElement lv ? Str(lv, "candidate_name") : null;
        var roots = Fence.Roots(reg, _config.Roots);
        var candidates = roots.Count > 0 ? Fence.Enumerate(roots) : new List<Candidate>();
        if (candidates.Count > 0)
        {
            var hi = await AskTierAsync(TierHiQuestion(text, candidateName, candidates), hi: true);
            if (hi is JsonElement hiV && Str(hiV, "confidence") is string hc && hc != "low" &&
                Str(hiV, "folder") is { Length: > 0 } folder && folder != "none")
            {
                var pick = candidates.FirstOrDefault(c =>
                    c.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (pick is not null)
                {
                    if (reg.Owner(pick.Path) is { } already)
                        return Done(new Verdict("discovery", already.Ws.Id, already.Ws.Name, hc));
                    try
                    {
                        var made = WorkspaceResolve.ForPath(reg, pick.Path);
                        Announce($"[dodona] found {pick.Path} inside the search fence — new workspace “{made.Ws.Name}”. " +
                                 $"undo: dodona workspace-forget --workspace {made.Ws.Id}");
                        return Done(new Verdict("discovery", made.Ws.Id, made.Ws.Name, hc, Created: true));
                    }
                    catch (Exception ex) { _store.Event("discovery_attach_failed", null, ex.Message); }
                }
            }
            _store.Event("discovery_miss", null, $"fence={candidates.Count} candidates, input={Truncate(text, 60)}");
        }

        // ---- rung 4: ask, with a guess, and TEACH. Double uncertainty lands in the merged
        // feed carrying its best candidates; the answer becomes an alias, so rung 4 decays
        // toward rung 1 with use.
        return Done(Ask(text, all, candidateName is null ? null : $"it sounded like “{candidateName}”"));
    }

    /// <summary>Does the text name this workspace? Word-bounded for plain names so "work"
    /// does not match "network"; a substring match for names carrying punctuation, where a
    /// word boundary is not meaningful.</summary>
    static bool Mentions(string text, string handle)
    {
        if (handle.Length < 2) return false;
        var esc = System.Text.RegularExpressions.Regex.Escape(handle);
        var pattern = handle.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            ? $@"(?<![\w-]){esc}(?![\w-])"
            : esc;
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    Verdict Ask(string text, List<Workspace> all, string? hint)
    {
        // Best candidates, cheapest ordering available without another model call: whatever
        // was resolved to most recently, then the rest.
        var recent = _store.Resolutions(20).Where(r => r.WorkspaceId is not null).Select(r => r.WorkspaceId!).Distinct().ToList();
        var ordered = all.OrderBy(x => recent.IndexOf(x.Id) is var i && i < 0 ? int.MaxValue : i).Take(4).ToList();
        var candidates = JsonSerializer.Serialize(ordered.Select(x => new { id = x.Id, name = x.Name }));
        var qid = _store.QuestionOpen(text, candidates);
        var choices = string.Join(" / ", ordered.Select(x => x.Name));
        Announce($"[dodona] not sure which workspace “{Truncate(text, 45)}” is for{(hint is null ? "" : $" — {hint}")}. " +
                 $"{choices}, or new? answer: dodona concierge-answer {qid} <name|new:NAME>", qid);
        _store.Event("group_clarification", null, $"question {qid}: {Truncate(text, 80)}");
        return new Verdict("ask", null, null, "unsure", QuestionId: qid);
    }

    /// <summary>
    /// Answer a rung-4 question — and TEACH the registry, which is the half that matters
    /// (§4): every clarification becomes an alias, so the same sentence resolves at rung 1
    /// next time and asking decays with use.
    /// </summary>
    List<string> Answer(long id, string answer)
    {
        var lines = new List<string>();
        var q = _store.Question(id);
        if (q is null) { lines.Add($"error: no question {id}"); return lines; }
        if (q.State != "open") { lines.Add($"error: question {id} is already {q.State}"); return lines; }

        using var reg = new Registry();
        Workspace? target;
        if (answer.StartsWith("new:", StringComparison.OrdinalIgnoreCase))
        {
            var name = answer[4..].Trim();
            if (name.Length == 0) { lines.Add("error: new:<NAME> needs a name"); return lines; }
            if (reg.ByNameOrId(name) is Workspace clash) { lines.Add($"error: \"{name}\" is already {clash.Label}"); return lines; }
            target = reg.Create(name);
            lines.Add($"created workspace {target.Name} ({target.Id}) — attach a folder with: dodona workspace-attach --member <path> --workspace {target.Id}");
        }
        else
        {
            target = reg.ByNameOrId(answer);
            if (target is null) { lines.Add($"error: no workspace \"{answer}\" (use new:<NAME> to make one)"); return lines; }
        }

        _store.QuestionAnswer(id, target.Id);

        // The teaching step. The alias is the DISTINGUISHING WORD from the question, not the
        // whole sentence — an alias of "do the thing on blazing trumpets please" would never
        // match anything again, which would make rung 4 permanent instead of decaying.
        var taught = Teach(reg, target, q.Input);
        if (taught is not null) lines.Add($"learned: \"{taught}\" now resolves to {target.Name} at rung 1");

        _store.Event("question_answered", null, $"question {id} -> {target.Id} taught={taught ?? "-"}");
        Announce($"[dodona] “{Truncate(q.Input, 40)}” → {target.Name}" +
                 (taught is null ? "" : $"; learned “{taught}”"));
        lines.Add($"answered: {target.Name} ({target.Id})");
        // The held sentence is delivered by the caller (M5 wires the input box through
        // here); the concierge's job ends at "which workspace".
        lines.Add($"deliver: dodona input \"{q.Input}\" --workspace {target.Id}");
        return lines;
    }

    /// <summary>
    /// Turn a clarified sentence into a reusable alias: the longest word that is not already
    /// a known workspace name and not a stop word. Code, not a model — the same reasoning as
    /// `NameFromText` in the daemon (§2.2: derive in code what is not really a judgement).
    /// Returns the alias taught, or null when the sentence offered nothing distinctive.
    /// </summary>
    static string? Teach(Registry reg, Workspace target, string input)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","that","this","with","some","please","its","make","fix","add","let","can","you",
            "should","would","could","need","want","get","put","use","there","then","when","where","what","how",
            "why","from","into","about","over","under","out","doing","does","done","work","working","thing",
        };
        var word = System.Text.RegularExpressions.Regex.Matches(input, @"[A-Za-z][A-Za-z0-9_-]{3,}")
            .Select(m => m.Value)
            .Where(v => !stop.Contains(v))
            .Where(v => reg.ByNameOrId(v) is null)
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
        if (word is null) return null;
        return reg.AddAlias(target.Id, word, out _) ? word : null;
    }

    /// <summary>
    /// The review-behind net (§2.3). Optimistic focused-workspace delivery skips the ladder
    /// entirely; the per-workspace brain can catch wrong-lane-within-workspace but
    /// **structurally cannot catch wrong-workspace**, because it does not know other
    /// workspaces exist (§14). So the cheap tier reviews behind — fire-and-forget, silent on
    /// agreement, the BrainReview pattern one level up.
    ///
    /// It never retracts and never re-delivers. You cannot unsay a sentence to an agent
    /// (§5's error asymmetry), so all a group-scope miss can honestly do is TELL the
    /// operator, in the merged feed, with the command to move it.
    /// </summary>
    void ReviewBehind(string text, string deliveredTo)
    {
        if (!_config.Models) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var reg = new Registry();
                var all = reg.All();
                if (all.Count < 2) return;                      // nothing to be wrong about
                var delivered = all.FirstOrDefault(x => x.Id.Equals(deliveredTo, StringComparison.OrdinalIgnoreCase));
                if (delivered is null) return;

                var names = string.Join(", ", all.Select(x => $"\"{x.Name}\""));
                var v = await AskTierAsync(
                    "A sentence from the operator was just delivered to a workspace, optimistically, without asking you first.\n" +
                    $"Sentence: {text}\nDelivered to: \"{delivered.Name}\"\nAll workspaces: [{names}]\n" +
                    "Was that the right WORKSPACE? You are not judging which lane or which task — only which workspace.\n" +
                    "Reply ONLY one line of JSON: {\"agree\":true|false,\"workspace\":\"<NAME if you disagree>\"," +
                    "\"confidence\":\"high|medium|low\",\"reason\":\"<=60 chars\"}", hi: false);
                if (v is not JsonElement r) return;

                var agree = r.TryGetProperty("agree", out var ag) && ag.ValueKind == JsonValueKind.True;
                var conf = Str(r, "confidence") ?? "low";
                _store.Event("review_behind", null, $"agree={agree} conf={conf} delivered={delivered.Name}");
                if (agree || conf == "low") return;             // silent unless confidently disagreeing

                var better = Str(r, "workspace");
                var pick = better is null ? null : all.FirstOrDefault(x => x.Name.Equals(better, StringComparison.OrdinalIgnoreCase));
                if (pick is null || pick.Id == delivered.Id) return;

                var reason = Str(r, "reason") ?? "";
                _store.Event("group_misroute", null, $"{delivered.Name} -> {pick.Name}: {reason}");
                // No retraction: the agent in `delivered` already has the sentence. Say so
                // plainly and hand over the command, rather than pretending it can be undone.
                Announce($"[dodona] “{Truncate(text, 40)}” went to {delivered.Name}, but it looks like {pick.Name}" +
                         $"{(reason.Length > 0 ? $" ({reason})" : "")}. It was already delivered — resend if you meant {pick.Name}: " +
                         $"dodona input \"{Truncate(text, 60)}\" --workspace {pick.Id}");
            }
            catch (Exception ex) { _store.Event("review_failed", null, ex.Message); }
        });
    }

    static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // ------------------------------------------------------------------ the merged feed (§6)

    /// <summary>The system's own voice at GROUP scope. It belongs to no workspace's column by
    /// definition, so it lives in the concierge's own feed table and the shell merges it in
    /// (§6). Never an exception, never a silent decision: everything the ladder does above
    /// rung 1 says so here.</summary>
    void Announce(string body, long? questionId = null)
    {
        _store.Announce(body, questionId);
        _store.Event("announced", null, Truncate(body, 120));
    }

    // ------------------------------------------------------------------ the two tiers

    string TierLoQuestion(string text, string names, List<string> recents, string? focused) =>
        $"Which WORKSPACE does this operator input belong to?\n" +
        $"Workspaces: [{names}]\n" +
        (recents.Count > 0 ? $"Recently used: [{string.Join(", ", recents)}]\n" : "") +
        (focused is not null ? $"Currently focused: \"{focused}\"\n" : "") +
        $"Input: {text}\n" +
        "The input may name a workspace loosely or by a mangled dictation of its name. It may also " +
        "name somewhere you have never heard of, in which case say so with candidate_name.\n" +
        "Reply ONLY one line of JSON, no prose, no markdown: " +
        "{\"workspace\":\"<NAME or none>\",\"confidence\":\"high|medium|low\"," +
        "\"candidate_name\":\"<the name it seems to be reaching for, if not a known workspace>\"}";

    string TierHiQuestion(string text, string? candidateName, List<Candidate> candidates) =>
        "An operator input names a place to work that is not a known workspace. Below is every folder " +
        "inside the configured search fence — this list is ALL you may consider; there is nothing else to look at " +
        "and no way to look further.\n" +
        $"Input: {text}\n" +
        (candidateName is not null ? $"The cheap tier thought it sounded like: \"{candidateName}\"\n" : "") +
        "Folders:\n" + string.Join("\n", candidates.Select(c => $"- {c.Name}{(c.IsGit ? " (git repo)" : "")} at {c.Path}")) + "\n" +
        "Which folder is the input about? Answer none unless it is a clear match — a wrong guess makes a " +
        "workspace the operator did not want.\n" +
        "Reply ONLY one line of JSON: {\"folder\":\"<name or full path, or none>\",\"confidence\":\"high|medium|low\"," +
        "\"reason\":\"<=60 chars\"}";

    /// <summary>Ask one tier, spawning it on first use. Null when models are off, the tier
    /// failed to start, it timed out, or it answered something unparseable — every caller
    /// treats null as "this rung had no opinion" and moves down the ladder, because the
    /// ladder must degrade rather than stall.</summary>
    async Task<JsonElement?> AskTierAsync(string question, bool hi)
    {
        var id = await EnsureTierAsync(hi);
        if (id < 0) return null;
        var gate = hi ? _hiLock : _loLock;
        await gate.WaitAsync();
        string? reply;
        try { reply = await _tiers[id].AskAsync(question, hi ? 30000 : 20000); }
        finally { gate.Release(); }
        if (reply is null) { _store.Event("tier_timeout", id, Truncate(question, 100)); return null; }
        try
        {
            var json = reply[reply.IndexOf('{')..(reply.LastIndexOf('}') + 1)];
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch { _store.Event("tier_unparseable", id, Truncate(reply, 120)); return null; }
    }

    async Task<long> EnsureTierAsync(bool hi)
    {
        if (!_config.Models) return -1;
        var id = hi ? ConciergeStore.TierHi : ConciergeStore.TierLo;
        if (_tiers.TryGetValue(id, out var live) && live.Connected) return id;

        var name = hi ? "hi" : "lo";
        var role = hi ? "concierge-hi" : "concierge-lo";
        var sys =
            "You are Dodona's CONCIERGE. You answer exactly one kind of question: which WORKSPACE a sentence " +
            "belongs to. A workspace is a named group of folders the operator works in — \"work\", \"personal\", " +
            "\"dodona-dev\". You do NOT decide which lane, which task, whether something deserves a ticket, or " +
            "what any of it should be called: those belong to the workspace's own dispatcher, one level down. " +
            "You never read or write code, never run tools, and never do work yourself. " +
            "Answer ONLY in the single-line JSON schema each request specifies: no prose, no markdown, no code fences. " +
            "State your confidence honestly — saying low is how a hard question reaches someone with more budget than you.";

        var model = hi ? _config.HiModel : _config.LoModel;
        var effort = hi ? _config.HiEffort : _config.LoEffort;
        var args = IsClaude(_config.Agent) ? ClaudeArgs(model, effort, sys) : new List<string>();

        var pipe = $"dodona-{Id}-tier{id}";
        _store.TierUpsert(id, name, pipe);

        var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                      ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
        // Neutral cwd, always: the concierge is a manager and must never load a project's
        // CLAUDE.md or skills (commit 19dad3d — a manager reading a worker's orders is how a
        // classifier ends up running /ship). It has no project anyway, which is the point.
        var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = Paths.NeutralCwd() };
        psi.ArgumentList.Add(pipe);
        psi.ArgumentList.Add(_config.Agent);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DODONA_SHIM_INFO"] = Path.Combine(Paths.ConciergeDir, $"shim-tier{id}.json");
        psi.Environment["DODONA_LANE_ROLE"] = role;
        try { Process.Start(psi); }
        catch (Exception ex) { _store.Event("tier_spawn_failed", id, ex.Message); return -1; }
        _store.Event("tier_spawned", id, $"pipe={pipe} child={_config.Agent} model={model}/{effort}");

        var rt = new LaneRuntime(id, pipe, _store);
        if (!await rt.ConnectAndPumpAsync(attempts: 20))
        {
            _store.TierState(id, "unreachable");
            _store.Event("tier_unreachable", id, "shim pipe never answered");
            return -1;
        }
        _tiers[id] = rt;
        return id;
    }

    static bool IsClaude(string child) =>
        child.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase) ||
        child.EndsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>The concierge's tiers are utility roles: `--setting-sources user` so they
    /// read no project settings, no permission mode, and no allowlist — they have nothing
    /// to run.</summary>
    static List<string> ClaudeArgs(string model, string effort, string systemPrompt)
    {
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                                      "--verbose", "--model", model };
        if (!string.IsNullOrWhiteSpace(effort)) { args.Add("--effort"); args.Add(effort); }
        args.Add("--setting-sources");
        args.Add("user");
        args.Add("--append-system-prompt");
        args.Add(systemPrompt);
        return args;
    }
}
