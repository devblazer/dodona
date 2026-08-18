using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The UI's write side: everything the user does in the grid becomes one exchange on
/// the daemon's control pipe — the same pipe the CLI uses, so a click and a test's
/// injected message are literally the same thing (§17). The UI never writes the store.
/// </summary>
static class DaemonClient
{
    /// <summary>Where the daemon binary is. In a published install all three executables
    /// sit in one directory, so this is simply "next to me" — which is also why publish
    /// puts the shim and the UI there too. DODONA_EXE overrides for dev runs, where each
    /// project builds to its own bin folder.</summary>
    public static string? DodonaExe()
    {
        var env = Environment.GetEnvironmentVariable("DODONA_EXE");
        if (env is not null && File.Exists(env)) return env;
        var beside = Path.Combine(AppContext.BaseDirectory, "dodona.exe");
        if (File.Exists(beside)) return beside;
        // Dev-tree fallback: ...\src\DodonaUi\bin\Release\net8.0-windows -> ...\src\Dodona\bin\...
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Dodona\bin\Release\net8.0\dodona.exe"));
        return File.Exists(dev) ? dev : null;
    }

    /// <summary>Make sure a daemon owns this workspace, starting one if not (§13:
    /// start-on-demand — the store is always there, the daemon is summoned). Addressed by
    /// workspace id: the UI has already resolved who this is, and letting the child
    /// re-resolve from a path is a second chance to disagree. Returns null on success or a
    /// reason to show the user.</summary>
    public static string? Ensure(string primary, string wsId, int timeoutMs = 20000)
    {
        if (Probe(wsId)) return null;

        var exe = DodonaExe();
        if (exe is null) return "cannot find dodona.exe (set DODONA_EXE, or run from a published folder)";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,                 // detached: the daemon must outlive this UI
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Directory.Exists(primary) ? primary : Path.GetTempPath(),
            };
            psi.ArgumentList.Add("daemon");
            psi.ArgumentList.Add("--workspace");
            psi.ArgumentList.Add(wsId);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { return $"could not start the daemon: {ex.Message}"; }

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Probe(wsId)) return null;
            Thread.Sleep(200);
        }
        return "started a daemon but it never answered its control pipe";
    }

    static bool Probe(string instanceId)
    {
        var pipe = new NamedPipeClientStream(".", Instance.CtlPipe(instanceId), PipeDirection.InOut);
        try { pipe.Connect(300); return true; }
        catch { return false; }
        finally { try { pipe.Dispose(); } catch { } }
    }

    public static string Send(string instanceId, object request)
    {
        var pipe = new NamedPipeClientStream(".", Instance.CtlPipe(instanceId), PipeDirection.InOut);
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
