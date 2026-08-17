using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

// Spike 2 daemon stand-in.
//   A <pipe> <shimExe> <claudeExe> <markerPath> — "daemon A": spawns the shim,
//     plants a fact (turn 1), fires a slow turn 2, writes the marker, then keeps
//     pumping until the orchestrator murders it.
//   B <pipe> — "daemon B": connects to a shim it never spawned, drains the
//     backlog produced while nobody was listening, then asks for the fact.

var so = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
Console.SetOut(so);

const string MAGIC = "CINNABAR-77";
string mode = args[0], pipeName = args[1];

static string UserMsg(string text) => JsonSerializer.Serialize(new
{
    type = "user",
    message = new { role = "user", content = new object[] { new { type = "text", text } } },
});

static (long Seq, string Raw)? Parse(string line)
{
    int i = line.IndexOf('\t');
    if (i < 0) return null;
    return (long.Parse(line[..i]), line[(i + 1)..]);
}

static bool IsResult(string raw, out string text)
{
    text = "";
    if (!raw.Contains("\"type\":\"result\"")) return false;
    try
    {
        using var d = JsonDocument.Parse(raw);
        if (d.RootElement.GetProperty("type").GetString() != "result") return false;
        if (d.RootElement.TryGetProperty("result", out var r)) text = r.GetString() ?? "";
        return true;
    }
    catch { return false; }
}

if (mode == "A")
{
    string shimExe = args[2], claudeExe = args[3], marker = args[4];

    var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
    foreach (var a in new[] { pipeName, claudeExe, "-p", "--input-format", "stream-json",
                              "--output-format", "stream-json", "--verbose", "--model", "haiku" })
        psi.ArgumentList.Add(a);
    var shim = Process.Start(psi)!;
    Console.WriteLine($"SPAWNED-SHIM {shim.Id}");

    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
    pipe.Connect(15000);
    using var w = new StreamWriter(pipe) { AutoFlush = true };
    using var r = new StreamReader(pipe);
    Console.WriteLine("HELLO " + r.ReadLine());

    w.WriteLine(UserMsg($"Remember this exactly: the magic word is {MAGIC}. Acknowledge in five words or fewer."));
    string? line;
    while ((line = r.ReadLine()) is not null)
    {
        var p = Parse(line);
        if (p is null) continue;
        Console.WriteLine($"RECV {p.Value.Seq}");
        if (IsResult(p.Value.Raw, out var t)) { Console.WriteLine($"TURN1-RESULT {t}"); break; }
    }

    w.WriteLine(UserMsg("Write a 150-word story about a lighthouse keeper."));
    Console.WriteLine("SENT-TURN2");
    File.WriteAllText(marker, "sent-turn2");

    while ((line = r.ReadLine()) is not null)       // pump until killed
    {
        var p = Parse(line);
        if (p is not null) Console.WriteLine($"RECV {p.Value.Seq}");
    }
    return 0;
}

if (mode == "B")
{
    // No `using` on the pipe: after ##shutdown the shim closes its end first, and
    // a StreamWriter flush-on-dispose against a closed pipe throws. Teardown is guarded.
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
    pipe.Connect(15000);
    var w = new StreamWriter(pipe) { AutoFlush = true };
    var r = new StreamReader(pipe);
    Console.WriteLine("HELLO " + r.ReadLine());

    string? line;
    bool gotTurn2 = false;
    while (!gotTurn2 && (line = r.ReadLine()) is not null)
    {
        var p = Parse(line);
        if (p is null) continue;
        Console.WriteLine($"RECV {p.Value.Seq}");
        if (IsResult(p.Value.Raw, out _))
        {
            gotTurn2 = true;
            Console.WriteLine("B-GOT-TURN2-RESULT true");
        }
    }

    w.WriteLine(UserMsg("What is the magic word I told you earlier? Reply with only the word."));
    bool fact = false;
    string reply = "";
    while ((line = r.ReadLine()) is not null)
    {
        var p = Parse(line);
        if (p is null) continue;
        Console.WriteLine($"RECV {p.Value.Seq}");
        if (IsResult(p.Value.Raw, out var t)) { reply = t; fact = t.Contains(MAGIC); break; }
    }
    Console.WriteLine($"B-TURN3 {reply}");
    Console.WriteLine($"B-FACT {(fact ? "PASS" : "FAIL")}");
    try { w.WriteLine("##shutdown"); } catch { }
    try { pipe.Dispose(); } catch { }
    return fact ? 0 : 1;
}

return 2;
