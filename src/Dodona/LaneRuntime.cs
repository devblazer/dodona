using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Dodona;

/// <summary>
/// The daemon's view of one lane: a client on the shim's named pipe. The shim owns the
/// agent (spike 2); this class merely drains its `seq<TAB>line` stream into the store
/// and forwards user messages. If the daemon dies, the shim buffers; on reconnect the
/// replay lands here and the store's UNIQUE(lane_id, seq) makes it exactly-once.
///
/// M0 note: seq is per-shim-lifetime. A lane keeps one shim for its whole life in M0;
/// per-connection epochs come with agent replacement (M1+).
/// </summary>
sealed class LaneRuntime
{
    public long Id { get; }
    public string PipeName { get; }
    // ILaneSink, not Store: the concierge runs the same wire over its own tables (§2 -
    // it holds no lanes, no tickets and no merge token, so it must not be handed a store
    // shaped to hold them). See LaneSink.cs for why an interface was cheaper than either
    // a second parser or a schema bump.
    readonly ILaneSink _store;
    StreamWriter? _writer;
    public volatile bool Connected;
    /// <summary>What this shim's wire says it speaks — read from the hello line, checked
    /// before a hot swap (§13). Pre-versioning shims say nothing, which means 1.</summary>
    public int ShimProtocol { get; private set; } = 1;
    TaskCompletionSource<string>? _resultTcs;

    /// <summary>Raised when a turn ends, with the pane row it just wrote (§5). The daemon
    /// hangs selective compression off this: a turn-final message is the one thing that
    /// always reaches the operator, so it is the one thing always worth shortening. Never
    /// awaited from the pump — a compressor must not be able to stall the wire.</summary>
    public Action<long, long, string>? OnResult;      // (laneId, paneEventId, body)

    public LaneRuntime(long id, string pipeName, ILaneSink store)
    {
        Id = id;
        PipeName = pipeName;
        _store = store;
    }

    public async Task<bool> ConnectAndPumpAsync(int attempts = 10)
    {
        for (int i = 0; i < attempts; i++)
        {
            // PipeOptions.Asynchronous is load-bearing: this handle is read by the pump
            // task and written by Say() concurrently, and a synchronous pipe handle
            // serializes the two — a pending read blocks the write forever.
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(500); }
            catch { pipe.Dispose(); await Task.Delay(300); continue; }

            _writer = new StreamWriter(pipe) { AutoFlush = true };
            var reader = new StreamReader(pipe);
            // BOUNDED. `!hello` is the first thing a shim writes, so a connection that stays
            // silent is one the shim has accepted but not promoted -- it is still pumping an
            // earlier client on its other server instance (the shim keeps a spare listener so
            // its pipe name never leaves the namespace, P6.1). An unbounded read here would
            // park reconcile on that connection forever, which is CLAUDE.md 0.1's standing
            // directive broken by the fix for a race. Give up, drop it, and let the retry loop
            // come back; by then the shim has almost always promoted us.
            var helloTask = reader.ReadLineAsync();
            if (await Task.WhenAny(helloTask, Task.Delay(2000)) != helloTask)
            {
                _store.Event("lane_pipe_silent", Id, $"connected to {PipeName} but no !hello in 2s -- the shim is serving someone else; retrying");
                try { pipe.Dispose(); } catch { }
                await Task.Delay(300);
                continue;
            }
            var hello = await helloTask;                       // "!hello proto=… shim=… child=… delivered=… buffered=…"
            var proto = System.Text.RegularExpressions.Regex.Match(hello ?? "", @"proto=(\d+)");
            if (proto.Success) ShimProtocol = int.Parse(proto.Groups[1].Value);
            _store.Event("lane_connected", Id, hello);
            Connected = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null) HandleShimLine(line);
                }
                catch { }
                Connected = false;
                _store.Event("lane_pipe_lost", Id, null);
            });
            return true;
        }
        return false;
    }

    /// <summary>
    /// THE PARSER, and the one seam this pilot slice opens (docs/TEST-ARCHITECTURE-PLAN.md W5).
    /// `internal` rather than `private` for exactly one reason: `Dodona.csproj` grants
    /// `InternalsVisibleTo("Dodona.Tests")`, so a unit test can hand this method a real
    /// recorded `claude` wire line and read what it wrote through a recording
    /// <see cref="ILaneSink"/>. Nothing else changes -- the daemon still reaches it only from
    /// the pump task above, and no production caller exists outside this class.
    /// </summary>
    internal void HandleShimLine(string line)
    {
        int t = line.IndexOf('\t');
        if (t < 0) return;                                     // greeting/noise
        if (!long.TryParse(line.AsSpan(0, t), out var seq)) return;
        var raw = line[(t + 1)..];

        string kind = "wire", body = raw;
        try
        {
            using var d = JsonDocument.Parse(raw);
            var type = d.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            switch (type)
            {
                case "system":
                {
                    // `system` is a bag of subtypes, and treating them all as "init" spammed
                    // one live lane with 985 thinking_tokens rows each reading
                    // "init session=…". Only real init is worth a labelled pane row;
                    // permission_denied is worth a LOUD one (an agent that cannot run its
                    // build is the most-repeated failure in this project's history); the
                    // rest stay raw — wire noise the overlay can still show.
                    var sub = d.RootElement.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    if (sub == "init" && d.RootElement.TryGetProperty("session_id", out var sid))
                    {
                        kind = "system";
                        _store.LaneSession(Id, sid.GetString() ?? "");
                        body = $"init session={sid.GetString()}";
                    }
                    else if (sub == "permission_denied")
                    {
                        kind = "error";
                        var tool = d.RootElement.TryGetProperty("tool_name", out var tn) ? tn.GetString() : "?";
                        var why = d.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : "denied";
                        body = $"permission denied: {tool} — {Truncate(why ?? "", 140)}";
                    }
                    else if (sub == "thinking_tokens")
                    {
                        // PRESENCE MUST NOT LIE WHILE IT WAITS. These arrive in their
                        // hundreds during a long think (93 of one turn's 111 wire lines),
                        // and until now they left presence reading as the last TOOL the
                        // agent ran -- so a tile said `bash: ls -la docs/...` for ninety
                        // seconds of pure reasoning. The pane clock next to it was ticking
                        // (LANE-LIFECYCLE §5), which made a stale label look like a live
                        // one. No pane row: a thought is not a step, and this is the
                        // meaningless volume the operator asked to have dropped.
                        _store.LanePresence(Id, "thinking…");
                    }
                    // else: kind stays "wire", body stays raw
                    break;
                }
                case "assistant":
                    kind = "agent_line";
                    var sb = new StringBuilder();
                    foreach (var c in d.RootElement.GetProperty("message").GetProperty("content").EnumerateArray())
                    {
                        var ct = c.TryGetProperty("type", out var ctp) ? ctp.GetString() : null;
                        if (ct == "text") sb.Append(c.GetProperty("text").GetString());
                        else if (ct == "tool_use")
                        {
                            // Presence is derived from tool events by CODE — no model (§5).
                            var tool = c.TryGetProperty("name", out var tn) ? tn.GetString() ?? "?" : "?";
                            var detail = "";
                            if (c.TryGetProperty("input", out var inp))
                            {
                                if (inp.TryGetProperty("file_path", out var fp)) detail = Path.GetFileName(fp.GetString() ?? "");
                                else if (inp.TryGetProperty("command", out var cm)) detail = Truncate(cm.GetString() ?? "", 40);
                            }
                            _store.LanePresence(Id, $"{tool.ToLowerInvariant()}: {detail}".TrimEnd(':', ' '));

                            // AND A ROW, which presence cannot be. Presence is one column
                            // that every event overwrites, so it can only ever say what is
                            // happening *now* — eighteen tool calls in one measured turn
                            // left two sentences on screen and no trace of the other
                            // sixteen (PaneProgress.cs carries the measurement). The row is the
                            // trace; Progress decides in code whether it is worth one.
                            var seen = c.TryGetProperty("input", out var pin) ? pin : default;
                            var (tier, text) = PaneProgress.FromTool(tool, seen);
                            if (tier != ProgressTier.Noise && text.Length > 0)
                                // seq stays NULL: the seq of this wire line belongs to the
                                // row written below it, and UNIQUE(lane_id, seq) is what
                                // makes shim redelivery exactly-once. A progress row is a
                                // DERIVED row -- it must not compete for that key.
                                _store.PaneEvent(Id, PaneProgress.Kind, text, null, raw);
                            RememberTool(c, tool);
                        }
                    }
                    body = sb.ToString();
                    if (body.Length == 0) return;            // tool-only assistant event: the progress row above is the pane line
                    break;
                case "user":
                    // A tool_result comes back as a `user` event, and a FAILED one is the
                    // most valuable mid-turn line there is: a build that will not compile,
                    // a command with an unbalanced quote, a path that is not there. It used
                    // to fall through to an unread raw `wire` row, so the operator learned
                    // about it -- if at all -- when the turn ended. Successes stay silent:
                    // the tool_use row above already said the step happened, and saying it
                    // twice is the volume §2.2 refuses to spend.
                    ReportFailedResults(d.RootElement);
                    break;
                case "result":
                    kind = "result";
                    body = d.RootElement.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "";
                    _store.LanePresence(Id, "idle");
                    _resultTcs?.TrySetResult(body);
                    break;
                case "rate_limit_event":
                    // The authoritative quota number, pushed unasked (observed live:
                    // five_hour, utilization 0.97). Kept as the latest reading in kv with
                    // its own timestamp — it only arrives when a lane takes a turn, so the
                    // UI must show it as "as of", never imply it is live. No pane row:
                    // quota is ambient state, not lane conversation.
                    if (d.RootElement.TryGetProperty("rate_limit_info", out var rl))
                    {
                        _store.KvSet("rate_limit", JsonSerializer.Serialize(new
                        {
                            observedTs = DateTime.UtcNow.ToString("o"),
                            lane = Id,
                            info = rl,
                        }));
                    }
                    break;
            }
        }
        catch { /* unparseable stays kind=wire, body=raw */ }

        var rowId = _store.PaneEventId(Id, kind, body, seq, raw);

        // The row is already written and the pane can already show it. Compression is a
        // later, optional improvement to a row that is complete without it (§5).
        if (kind == "result" && rowId > 0 && body.Length > 0) OnResult?.Invoke(Id, rowId, body);
    }

    // ------------------------------------------------------- which tool_use id was which
    //
    // A tool_result names only the id of the call it answers, so the id has to be kept to
    // say "bash failed" rather than "something failed". BOUNDED, and small: a turn is
    // dozens of calls, not thousands, and this is a live daemon holding one of these per
    // lane forever. Oldest out first when it fills -- a result always follows its call
    // closely, so an id old enough to evict is an id whose answer already came and went.
    readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    readonly Queue<string> _toolOrder = new();
    const int ToolMemory = 64;

    void RememberTool(JsonElement block, string tool)
    {
        if (!block.TryGetProperty("id", out var idp) || idp.ValueKind != JsonValueKind.String) return;
        var id = idp.GetString();
        if (id is null or "") return;
        lock (_toolNames)
        {
            if (!_toolNames.TryAdd(id, tool)) return;
            _toolOrder.Enqueue(id);
            while (_toolOrder.Count > ToolMemory) _toolNames.Remove(_toolOrder.Dequeue());
        }
    }

    string? ToolNamed(JsonElement block)
    {
        if (!block.TryGetProperty("tool_use_id", out var idp) || idp.ValueKind != JsonValueKind.String) return null;
        var id = idp.GetString();
        if (id is null or "") return null;
        lock (_toolNames) return _toolNames.TryGetValue(id, out var t) ? t : null;
    }

    /// <summary>Write a progress row for every FAILED tool result in a user event. Silent on
    /// success, and silent on our own injected input (which is `text` blocks, not
    /// `tool_result` ones), so this can never echo the operator back at themselves.</summary>
    void ReportFailedResults(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        foreach (var c in content.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            if (!c.TryGetProperty("type", out var tp) || tp.GetString() != "tool_result") continue;
            if (!c.TryGetProperty("is_error", out var err) || err.ValueKind != JsonValueKind.True) continue;
            // `content` is a string on the shapes observed live, but the API allows an
            // array of blocks; take the string when it is one and fall back to the raw
            // text of whatever else it is, rather than saying nothing.
            var text = c.TryGetProperty("content", out var cc)
                ? (cc.ValueKind == JsonValueKind.String ? cc.GetString() : cc.GetRawText())
                : null;
            var line = PaneProgress.FromFailedResult(ToolNamed(c), text);
            if (line is not null) _store.PaneEvent(Id, PaneProgress.Kind, line, null, null);
        }
    }

    /// <summary>THE LANE BRIEFING, REPEATED ON EVERY TURN (docs/LANE-BRIEFING-PLAN.md B2), or
    /// null for a lane that gets none. Prefixed to what is SENT and to nothing else.
    ///
    /// Static for the life of the lane on purpose: the facts in it (own worktree or shared
    /// checkout, which branch, which delivery mode) do not change while the lane runs, and
    /// per-turn plumbing to attach CHANGING facts was considered and rejected (plan §6). It is
    /// set at BOTH `LaneRuntime` construction sites -- the spawn and reconcile's adoption -- for
    /// the reason `HookTurnEnd` carries in full: a daemon restarts on every publish and hot swap,
    /// so anything wired only at spawn simply stops happening for every lane the operator already
    /// had. Fully covered and dead in production is §3's routing ladder exactly.</summary>
    public string? TurnBriefing { get; set; }

    /// <summary>SEND AND RECORD ARE NOT THE SAME STRING, AND THAT IS THE WHOLE OF B2.
    ///
    /// This wrote one string to two destinations: the pane row the operator reads, and the
    /// agent. So a briefing prefixed naively would land in the operator's feed on every message
    /// they ever send -- and in the compressor's input as well, since selective compression reads
    /// panes. What the agent gets is the briefing plus the operator's words; what the pane, the
    /// feed, the `say` event and the compressor get is the operator's words alone.
    ///
    /// Every way into a lane funnels through here -- `input`, `ui type`, `ui compose` + Enter,
    /// dictation, the router's delivery, `AskAsync` -- so there is no call site that can forget,
    /// which is the correction `DaemonClient.Send` needed for start-on-demand (CLAUDE.md §3.1).</summary>
    public void Say(string text)
    {
        if (_writer is null || !Connected) throw new InvalidOperationException($"lane {Id} not connected");
        _store.PaneEvent(Id, "user_input", text, null, null);
        _store.Event("say", Id, text);
        _store.LanePresence(Id, "working…");
        var sent = TurnBriefing is { Length: > 0 } brief ? brief + text : text;
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "text", text = sent } } },
        });
        _writer.WriteLine(msg);
    }

    /// <summary>Tell the shim to take its child down and exit. `##shutdown` is the one
    /// control word the shim's input pump intercepts (spike 2).</summary>
    public void Shutdown()
    {
        try { _writer?.WriteLine("##shutdown"); } catch { }
        Connected = false;
    }

    /// <summary>Tell a shim to go, over its OWN pipe, with no LaneRuntime and no recorded pid
    /// -- the only way to stop an agent whose `shim-lane*.json` was never written or has been
    /// reaped, which is exactly the four-agents-nothing-can-see case in RC2.
    ///
    /// This is the missing half of the orphan story. The daemon used to ABANDON a lane it could
    /// not adopt: it dropped the only reference that could ever stop the process and then spawned
    /// a replacement beside it (measured: `lane_unreachable 20`, `utility_lane_reaped 20`, then
    /// `shim_spawned 25` 160 ms later, while lane 20's shim, child and pipe were all still
    /// alive). A live pipe is a live process you can still reach; reaching it is strictly better
    /// than walking away from it.</summary>
    public static async Task<bool> ShutdownShimAsync(string pipeName, int connectMs = 1500)
    {
        if (pipeName is null or "") return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectMs);
            var w = new StreamWriter(pipe) { AutoFlush = true };
            var r = new StreamReader(pipe);
            // Take the greeting first. The shim writes `!hello` before it reads anything, so a
            // client that never drains it is a client the shim is still talking to. Bounded,
            // because ReadLineAsync has no timeout of its own and a wedged shim must not wedge
            // the daemon's startup with it.
            var hello = r.ReadLineAsync();
            if (await Task.WhenAny(hello, Task.Delay(connectMs)) != hello) return false;
            w.WriteLine("##shutdown");
            await pipe.FlushAsync();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Wait for a lane pipe to actually LEAVE the namespace. The poke above is a
    /// request; this is the confirmation, and it has a deadline because a wait with no
    /// deadline is the standing directive in CLAUDE.md 0.1 violated in a new costume.
    ///
    /// TWO CONSECUTIVE absences, 150 ms apart, and that is the whole subtlety here. A shim's
    /// pipe name blinks out of the namespace for a few milliseconds while the serve loop swaps
    /// server instances (LaneLiveness carries the measurement), so a single absent reading is
    /// exactly as likely to mean "mid-reconnect" as "gone". Believing it here would be the worse
    /// direction of the two: the caller marks the lane dead and starts a replacement beside a
    /// live agent, which is the leak this whole phase exists to close. Two readings that far
    /// apart cannot both land in the same gap.</summary>
    public static async Task<bool> WaitPipeGoneAsync(string pipeName, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!Live(pipeName))
            {
                await Task.Delay(150);
                if (!Live(pipeName)) return true;
            }
            await Task.Delay(100);
        }
        return false;

        static bool Live(string name) =>
            Instance.LiveLanePipes().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One request/response turn: send, await this lane's next result event.
    /// Used for utility lanes (the router). Returns null on timeout.</summary>
    public async Task<string?> AskAsync(string text, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resultTcs = tcs;
        Say(text);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        _resultTcs = null;
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
