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

    /// <summary>
    /// Make sure a concierge is running before talking to it — start-on-demand, exactly as for
    /// a workspace daemon (§13: the store is always there, the process is summoned).
    ///
    /// It matters most in the state where nothing else is running at all: boot-to-zero
    /// (§4) is a window with no workspace awake, and typing is how the operator gets out of
    /// it. An input box that needed a concierge someone had remembered to start would make
    /// that state a dead end — which is precisely the failure `ui-use` exists to catch.
    /// </summary>
    public static string? EnsureConcierge(int timeoutMs = 20000)
    {
        if (Probe(Instance.ConciergeId)) return null;
        var exe = DodonaExe();
        if (exe is null) return "cannot find dodona.exe (set DODONA_EXE, or run from a published folder)";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,                 // detached: it must outlive this UI
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                // Neutral cwd: the concierge is a manager and must load no project's
                // CLAUDE.md or skills (commit 19dad3d). It has no project anyway.
                WorkingDirectory = Paths.NeutralCwd(),
            };
            psi.ArgumentList.Add("concierge");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { return $"could not start the concierge: {ex.Message}"; }

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Probe(Instance.ConciergeId)) return null;
            Thread.Sleep(200);
        }
        return "started a concierge but it never answered its control pipe";
    }

    /// <summary>Is this workspace's daemon answering its control pipe? Public because the
    /// workspace switcher shows awake/asleep per row — same probe, same 300ms bound.</summary>
    public static bool Probe(string instanceId)
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
