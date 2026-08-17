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

var sessionId = $"fake-{Guid.NewGuid():N}";
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
