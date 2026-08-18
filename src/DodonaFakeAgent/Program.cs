using System.Text.Json;
using System.Text.RegularExpressions;

// DodonaFakeAgent — a scripted stand-in for `claude -p --input-format stream-json
// --output-format stream-json`. Speaks just enough of the wire shape (observed in
// spike 1) for the daemon: system/init with a session id, an assistant event per
// turn, a result event per turn.
//
// Directives inside the user text make turns deterministic and schedulable:
//   sleep:N       — wait N seconds mid-turn (a long "thinking" turn to kill daemons under)
//   say <text>    — the result event carries exactly <text>
//
// DODONA_LANE_ROLE=compressor makes it answer in the compressor's fixed JSON schema (§5)
// instead, deterministically shortening whatever it was given. That keeps selective
// compression testable end-to-end with zero model calls, like every other suite.

var sessionId = $"fake-{Guid.NewGuid():N}";
var asCompressor = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "compressor";
// brain / brain-hi: deterministic management judgement, driven by directives embedded in
// whatever text reaches it (the operator input is quoted inside the brain's question):
//   brainname:X   — disagree, better_name X      brainticket:T — suggest ticket T
//   brainlow      — answer with confidence low (forces the escalation path)
var asBrain = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE")?.StartsWith("brain") == true;
// router: routekind:generic|specific|unclear, routetarget:X, routeconf:low — so the full
// escalation chain (classifier → brain-hi → operator clarification) runs model-free.
var asRouter = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "router";
var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
void Emit(object o) => stdout.WriteLine(JsonSerializer.Serialize(o));

Emit(new { type = "system", subtype = "init", session_id = sessionId, model = "fake-agent" });

string? line;
while ((line = Console.ReadLine()) is not null)
{
    string text;
    try
    {
        using var d = JsonDocument.Parse(line);
        text = d.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
    catch { continue; }

    if (asRouter)
    {
        var rk = Regex.Match(text, @"routekind:(\w+)");
        var rt2 = Regex.Match(text, @"routetarget:(\w+)");
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                kind = rk.Success ? rk.Groups[1].Value : "generic",
                target = rt2.Success ? rt2.Groups[1].Value : "none",
                confidence = text.Contains("routeconf:low") ? "low" : "high",
                cleaned_text = text,
            }),
        });
        continue;
    }

    if (asBrain)
    {
        var isHi = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "brain-hi";
        var name = Regex.Match(text, @"brainname:(\w+)");
        var tick = Regex.Match(text, @"brainticket:(\w+)");
        var low = text.Contains("brainlow") && !isHi;      // the hi tier is always sure
        object? ticket = tick.Success ? new { title = tick.Groups[1].Value, claims = new[] { "subtree:src" } } : null;
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                agree = !(name.Success || tick.Success),
                confidence = low ? "low" : "high",
                better_name = name.Success ? name.Groups[1].Value : null,
                ticket,
                target = Regex.Match(text, @"braintarget:(\w+)") is { Success: true } m2 ? m2.Groups[1].Value : null,
                reason = "fake brain",
            }),
        });
        continue;
    }

    if (asCompressor)
    {
        // Deterministic stand-in for the judgement: first 60 characters, one line, and
        // needs_you only when the text says so — enough shape for a test to assert on.
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        var needs = flat.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase);
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                headline = flat.Length > 60 ? flat[..60].TrimEnd() : flat,
                needs_you = needs,
                options = needs ? new[] { "wait", "override" } : Array.Empty<string>(),
            }),
        });
        continue;
    }

    Emit(new
    {
        type = "assistant",
        session_id = sessionId,
        message = new { role = "assistant", content = new object[] { new { type = "text", text = $"working on: {text}" } } },
    });

    // tool:Name:arg — emit a claude-shaped tool_use event (drives presence derivation)
    var tool = Regex.Match(text, @"tool:(\w+):(\S+)");
    if (tool.Success)
        Emit(new
        {
            type = "assistant",
            session_id = sessionId,
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id = "fake-tool-1", name = tool.Groups[1].Value, input = new { file_path = tool.Groups[2].Value } } },
            },
        });

    // ratelimit:0.42 — emit the CLI's rate_limit_event shape (observed live 2026-08-17),
    // so the quota indicator is testable without a real session ever being consulted.
    var rl = Regex.Match(text, @"ratelimit:([\d.]+)");
    if (rl.Success)
        Emit(new
        {
            type = "rate_limit_event",
            session_id = sessionId,
            rate_limit_info = new
            {
                status = "allowed_warning",
                resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
                rateLimitType = "five_hour",
                utilization = double.Parse(rl.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                isUsingOverage = false,
            },
        });

    var sleep = Regex.Match(text, @"sleep:(\d+)");
    if (sleep.Success) Thread.Sleep(int.Parse(sleep.Groups[1].Value) * 1000);

    var say = Regex.Match(text, @"say\s+(.+)$");
    Emit(new
    {
        type = "result",
        subtype = "success",
        session_id = sessionId,
        result = say.Success ? say.Groups[1].Value.Trim() : $"done: {text}",
    });
}
