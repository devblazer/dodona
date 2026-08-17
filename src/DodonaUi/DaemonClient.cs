using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace DodonaUi;

/// <summary>
/// The UI's write side: everything the user does in the grid becomes one exchange on
/// the daemon's control pipe — the same pipe the CLI uses, so a click and a test's
/// injected message are literally the same thing (§17). The UI never writes the store.
/// </summary>
static class DaemonClient
{
    public static string Send(string instanceId, object request)
    {
        var pipe = new NamedPipeClientStream(".", $"dodona-{instanceId}-ctl", PipeDirection.InOut);
        try { pipe.Connect(2000); }
        catch { return "daemon not running"; }
        try
        {
            var w = new StreamWriter(pipe) { AutoFlush = true };
            var r = new StreamReader(pipe);
            w.WriteLine(JsonSerializer.Serialize(request));
            var lines = new List<string>();
            string? line;
            while ((line = r.ReadLine()) is not null && line != "##end")
                if (!line.StartsWith("##")) lines.Add(line);
            return string.Join(" | ", lines);
        }
        catch (Exception ex) { return $"daemon error: {ex.Message}"; }
        finally { try { pipe.Dispose(); } catch { } }   // daemon closes first; never flush into it
    }
}
