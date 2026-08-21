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
    /// that state a dead end — which is precisely the failure the `ui-*` suites exist to
    /// catch (`ui-shell` owns boot-to-zero; `ui-use` was split into four on 2026-08-21).
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

    /// <summary>Which start-on-demand applies to this id — the concierge has its own process and
    /// its own pipe, a workspace daemon is addressed by id and wants its primary folder as a cwd.
    /// Returns null when something is now listening, or a reason to show the user.
    ///
    /// The SHELL id owns no daemon (Instance.ShellId is a window identity, not a workspace), so it
    /// is refused rather than summoned — starting "the shell daemon" would create a process nothing
    /// could ever talk to.</summary>
    static string? EnsureOwner(string instanceId)
    {
        if (instanceId == Instance.ConciergeId) return EnsureConcierge();
        if (instanceId.Length == 0 || instanceId == Instance.ShellId)
            return "no workspace has the grid — type to start one";

        var primary = "";
        try
        {
            using var reg = new Registry();
            primary = WorkspaceResolve.ByNameOrId(reg, instanceId)?.Primary ?? "";
        }
        catch { /* registry unreadable: Ensure falls back to a temp cwd and still starts one */ }
        return Ensure(primary, instanceId);
    }

    /// <summary>
    /// START-ON-DEMAND IS PART OF SENDING (§13), and that is enforcement replacing a rule every
    /// call site had to remember — the same correction the routing ladder needed (CLAUDE.md §3:
    /// "ensure at the point of use, never look up").
    ///
    /// THE INCIDENT (2026-08-19). Two of the three write paths ensured first — <see cref="AnswerAsk"/>
    /// through <see cref="Ensure"/>, the input box's concierge branch through <see cref="EnsureConcierge"/>
    /// — and <see cref="MainWindow.Send"/>, which carries EVERY lane click and the one-workspace
    /// branch of the input box, did not. So against a workspace whose daemon had exited, the window
    /// still rendered every lane as `alive` (the store reader is read-only and needs no daemon) and
    /// answered the first thing a person did with the literal words **"daemon not running"**.
    /// Measured: `ui type` into a shell window with one workspace and no daemon returned
    /// `status: daemon not running`, tiles intact. Closing the app is DESIGNED to leave nothing
    /// running (CLAUDE.md §3.1), so this was the state the operator's machine sat in every morning.
    ///
    /// It survived because no check ever ran the UI against a dead daemon, and because none of the
    /// five lane actions had a `ui` verb at all — they were unreachable, not merely untested.
    /// Both are fixed alongside this (`ui lane`, and m3's revives_* checks -- this line said
    /// ui-use's, and those checks have always been m3's).
    ///
    /// Consequence: this method can no longer report "daemon not running" as an outcome. Every
    /// failure it returns now names something a person can act on.
    /// </summary>
    public static string Send(string instanceId, object request)
    {
        var pipe = new NamedPipeClientStream(".", Instance.CtlPipe(instanceId), PipeDirection.InOut);
        try { pipe.Connect(2000); }
        catch
        {
            pipe.Dispose();
            // Asleep, not broken. Summon whoever owns this id and try once more; Ensure/
            // EnsureConcierge each probe first, so a daemon that came up in the meantime costs
            // one extra connect and nothing else.
            var reason = EnsureOwner(instanceId);
            if (reason is not null) return reason;
            pipe = new NamedPipeClientStream(".", Instance.CtlPipe(instanceId), PipeDirection.InOut);
            try { pipe.Connect(2000); }
            catch { pipe.Dispose(); return "started a daemon but it never answered its control pipe"; }
        }
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
