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
    readonly Store _store;
    StreamWriter? _writer;
    public volatile bool Connected;

    public LaneRuntime(long id, string pipeName, Store store)
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
            var hello = await reader.ReadLineAsync();          // "!hello shim=… child=… delivered=… buffered=…"
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
                    kind = "system";
                    if (d.RootElement.TryGetProperty("session_id", out var sid))
                    {
                        _store.LaneSession(Id, sid.GetString() ?? "");
                        body = $"init session={sid.GetString()}";
                    }
                    break;
                case "assistant":
                    kind = "agent_line";
                    var sb = new StringBuilder();
                    foreach (var c in d.RootElement.GetProperty("message").GetProperty("content").EnumerateArray())
                        if (c.TryGetProperty("type", out var ct) && ct.GetString() == "text")
                            sb.Append(c.GetProperty("text").GetString());
                    body = sb.ToString();
                    break;
                case "result":
                    kind = "result";
                    body = d.RootElement.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "";
                    break;
            }
        }
        catch { /* unparseable stays kind=wire, body=raw */ }

        _store.PaneEvent(Id, kind, body, seq, raw);
    }

    public void Say(string text)
    {
        if (_writer is null || !Connected) throw new InvalidOperationException($"lane {Id} not connected");
        _store.PaneEvent(Id, "user_input", text, null, null);
        _store.Event("say", Id, text);
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "text", text } } },
        });
        _writer.WriteLine(msg);
    }
}
