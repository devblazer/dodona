using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dodona;

// dodona — one binary, two roles, always separate processes:
//   dodona daemon --root <path>       the single writer: store, lanes, control pipe
//   dodona <command> [--root <path>]  a client over the control pipe
//
// Everything is scoped to the canonical project root (design §14): instance id, store
// path, pipe namespace. A named mutex makes a second daemon on the same root refuse
// to start no matter how it spelled the path.

var (cmd, root, rest) = ParseArgs(args);
if (cmd is null) { Help(); return 1; }

string instanceId;
{
    var canonical = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
    instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
}
string ctlPipe = $"dodona-{instanceId}-ctl";

return cmd switch
{
    "daemon" => await DaemonRun(),
    "lane-start" => Client(new { cmd = "lane-start", title = Opt("--title") ?? "LANE", child = Opt("--child"), childArgs = OptRest() }),
    "say" => Client(new { cmd = "say", lane = long.Parse(rest[0]), text = rest[1] }),
    "tail" => Client(new { cmd = "tail", lane = long.Parse(rest[0]), n = rest.Count > 1 ? int.Parse(rest[1]) : 20 }),
    "status" => Client(new { cmd = "status" }),
    "stop-daemon" => Client(new { cmd = "stop-daemon" }),
    _ => Fail($"unknown command: {cmd}"),
};

// ---------------------------------------------------------------- daemon role

async Task<int> DaemonRun()
{
    using var mutex = new Mutex(initiallyOwned: true, $"Global\\dodona-{instanceId}", out bool createdNew);
    if (!createdNew)
    {
        Console.Error.WriteLine($"another daemon already owns this root (instance {instanceId})");
        return 3;
    }

    using var store = new Store(Path.Combine(root, ".dodona", "store.db"));
    store.Event("daemon_start", null, $"pid={Environment.ProcessId} root={root}");
    Console.WriteLine($"dodona daemon: instance {instanceId}, ctl pipe {ctlPipe}, pid {Environment.ProcessId}");

    // Reconcile (design §12): rows are the claim; the pipe is the proof.
    var lanes = new Dictionary<long, LaneRuntime>();
    foreach (var l in store.LanesAll().Where(l => l.State == "alive"))
    {
        var rt = new LaneRuntime(l.Id, l.Pipe, store);
        if (await rt.ConnectAndPumpAsync(attempts: 3)) lanes[l.Id] = rt;
        else { store.LaneState(l.Id, "unreachable"); store.Event("lane_unreachable", l.Id, "reconcile: pipe did not answer"); }
    }
    store.Event("reconcile_done", null, $"connected={lanes.Count}");

    // No `using` on pipe streams anywhere near a peer that may close first (spike 2's
    // teardown lesson): StreamWriter flush-on-dispose against a closed pipe throws, and
    // an unhandled exception on Windows parks the process under WerFault.
    bool stopping = false;
    while (!stopping)
    {
        var server = new NamedPipeServerStream(ctlPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        var r = new StreamReader(server);
        var w = new StreamWriter(server) { AutoFlush = true };
        try
        {
            var req = await r.ReadLineAsync();
            if (req is not null)
            {
                try { stopping = await HandleAsync(req, w, store, lanes); }
                catch (Exception ex) { w.WriteLine($"error: {ex.Message}"); }
                w.WriteLine("##end");
            }
        }
        catch { /* client vanished mid-conversation; next */ }
        try { server.Disconnect(); } catch { }
        try { server.Dispose(); } catch { }
    }

    store.Event("daemon_stop", null, "graceful; lanes keep running");
    return 0;
}

async Task<bool> HandleAsync(string req, StreamWriter w, Store store, Dictionary<long, LaneRuntime> lanes)
{
    using var d = JsonDocument.Parse(req);
    var e = d.RootElement;
    switch (e.GetProperty("cmd").GetString())
    {
        case "lane-start":
        {
            var title = e.GetProperty("title").GetString()!;
            var child = e.TryGetProperty("child", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
            if (child is null) { w.WriteLine("error: --child <agent exe> is required in M0"); break; }
            var childArgs = e.TryGetProperty("childArgs", out var ca) && ca.ValueKind == JsonValueKind.Array
                ? ca.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();

            var id = store.LaneCreate(title);
            var pipe = $"dodona-{instanceId}-lane{id}";
            store.LanePipe(id, pipe);

            var shimExe = Environment.GetEnvironmentVariable("DODONA_SHIM")
                          ?? Path.Combine(AppContext.BaseDirectory, "DodonaShim.exe");
            var psi = new ProcessStartInfo(shimExe) { UseShellExecute = false, WorkingDirectory = root };
            psi.ArgumentList.Add(pipe);
            psi.ArgumentList.Add(child);
            foreach (var a in childArgs) psi.ArgumentList.Add(a);
            psi.Environment["DODONA_SHIM_INFO"] = Path.Combine(root, ".dodona", $"shim-lane{id}.json");
            Process.Start(psi);
            store.Event("shim_spawned", id, $"pipe={pipe} child={child}");

            var rt = new LaneRuntime(id, pipe, store);
            if (await rt.ConnectAndPumpAsync())
            {
                lanes[id] = rt;
                store.Event("lane_started", id, title);
                w.WriteLine($"lane {id} title {title} pipe {pipe}");
            }
            else
            {
                store.LaneState(id, "unreachable");
                w.WriteLine($"error: lane {id} shim pipe never answered");
            }
            break;
        }
        case "say":
        {
            var lane = e.GetProperty("lane").GetInt64();
            var text = e.GetProperty("text").GetString()!;
            if (!lanes.TryGetValue(lane, out var rt)) { w.WriteLine($"error: lane {lane} not connected"); break; }
            rt.Say(text);
            w.WriteLine($"-> lane {lane}");
            break;
        }
        case "tail":
        {
            var lane = e.GetProperty("lane").GetInt64();
            var n = e.GetProperty("n").GetInt32();
            foreach (var row in store.Tail(lane, n)) w.WriteLine(row);
            break;
        }
        case "status":
        {
            foreach (var l in store.LanesAll())
            {
                var connected = lanes.TryGetValue(l.Id, out var rt) && rt.Connected;
                w.WriteLine($"lane {l.Id}  {l.Title,-10}  state={l.State}  connected={connected}  session={l.Session ?? "-"}");
            }
            break;
        }
        case "stop-daemon":
            w.WriteLine("stopping (lanes keep running)");
            return true;
    }
    return false;
}

// ---------------------------------------------------------------- client role

int Client(object request)
{
    var pipe = new NamedPipeClientStream(".", ctlPipe, PipeDirection.InOut);
    try { pipe.Connect(3000); }
    catch { return Fail($"daemon not running for this root (ctl pipe {ctlPipe})"); }
    var w = new StreamWriter(pipe) { AutoFlush = true };
    var r = new StreamReader(pipe);
    bool err = false;
    try
    {
        w.WriteLine(JsonSerializer.Serialize(request));
        string? line;
        while ((line = r.ReadLine()) is not null && line != "##end")
        {
            Console.WriteLine(line);
            if (line.StartsWith("error:")) err = true;
        }
    }
    catch { err = true; }
    try { pipe.Dispose(); } catch { }   // daemon closes its end first; never flush into it
    return err ? 1 : 0;
}

// ---------------------------------------------------------------- plumbing

static (string? cmd, string root, List<string> rest) ParseArgs(string[] args)
{
    string? cmd = null;
    string root = Environment.CurrentDirectory;
    var rest = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; continue; }
        if (cmd is null) { cmd = args[i]; continue; }
        rest.Add(args[i]);
    }
    return (cmd, root, rest);
}

string? Opt(string name)
{
    var i = rest.IndexOf(name);
    return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null;
}

List<string> OptRest()
{
    var i = rest.IndexOf("--");
    return i >= 0 ? rest.Skip(i + 1).ToList() : new List<string>();
}

static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

static void Help() => Console.WriteLine("""
    dodona — multi-agent orchestrator (M0 walking skeleton)
      dodona daemon [--root <path>]
      dodona lane-start --title <T> --child <agent exe> [-- <child args...>] [--root <path>]
      dodona say <lane> <text> [--root <path>]
      dodona tail <lane> [n] [--root <path>]
      dodona status [--root <path>]
      dodona stop-daemon [--root <path>]
    """);
