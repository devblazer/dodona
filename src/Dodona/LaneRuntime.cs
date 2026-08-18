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
            var hello = await reader.ReadLineAsync();          // "!hello proto=… shim=… child=… delivered=… buffered=…"
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

    void HandleShimLine(string line)
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
                        }
                    }
                    body = sb.ToString();
                    if (body.Length == 0) return;            // tool-only assistant event: presence updated, no pane line
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

    public void Say(string text)
    {
        if (_writer is null || !Connected) throw new InvalidOperationException($"lane {Id} not connected");
        _store.PaneEvent(Id, "user_input", text, null, null);
        _store.Event("say", Id, text);
        _store.LanePresence(Id, "working…");
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "text", text } } },
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
