using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dodona;

// dodona — one binary, two roles, always separate processes:
//   dodona daemon --root <path>       the single writer: store, lanes, tickets, token
//   dodona <command> [--root <path>]  a client over the control pipe

var (cmd, root, opts, pos) = ParseArgs(args);
if (cmd is null) { Help(); return 1; }

string instanceId;
{
    var canonical = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
    instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
}
string ctlPipe = $"dodona-{instanceId}-ctl";
string uiPipe = $"dodona-{instanceId}-ui";

return cmd switch
{
    "version" => Version(),
    "daemon" => await Daemon.RunAsync(Path.GetFullPath(root), instanceId, ctlPipe, opts.ContainsKey("successor")),
    "lane-start" => Client(new { cmd = "lane-start", title = One("title") ?? "LANE", child = One("child"), childArgs = Many("child-arg") }),
    "say" => Client(new { cmd = "say", lane = long.Parse(pos[0]), text = pos[1] }),
    "tail" => Client(new { cmd = "tail", lane = long.Parse(pos[0]), n = pos.Count > 1 ? int.Parse(pos[1]) : 20 }),
    "status" => Client(new { cmd = "status" }),
    "ticket-create" => Client(new { cmd = "ticket-create", title = One("title") ?? "TICKET", mode = One("mode") ?? "on-approval", claims = Many("claim") }),
    "claim-check" => Client(new { cmd = "claim-check", ticket = long.Parse(pos[0]), path = pos[1] }),
    "claim-extend" => Client(new { cmd = "claim-extend", ticket = long.Parse(pos[0]), claims = Many("claim") }),
    "approve" => Client(new { cmd = "approve", ticket = long.Parse(pos[0]) }),
    "tickets" => Client(new { cmd = "tickets" }),
    "focus" => Client(new { cmd = "focus", lane = long.Parse(pos[0]) }),
    "input" => Client(new { cmd = "input", text = string.Join(" ", pos) }),
    "router-start" => Client(new { cmd = "router-start", child = One("child"), model = One("model") ?? "haiku" }),
    "ticket-agent" => Client(new { cmd = "ticket-agent", ticket = long.Parse(pos[0]), child = One("child"), model = One("model") ?? "sonnet" }),
    "token-request" => Client(new { cmd = "token-request", ticket = long.Parse(pos[0]), lease = int.Parse(One("lease") ?? "120") }),
    "token-renew" => Client(new { cmd = "token-renew", ticket = long.Parse(pos[0]), lease = int.Parse(One("lease") ?? "120") }),
    "token-release" => Client(new { cmd = "token-release", ticket = long.Parse(pos[0]) }),
    "token-status" => Client(new { cmd = "token-status" }),
    "land" => Client(new { cmd = "land", ticket = long.Parse(pos[0]) }),
    "ack" => Client(new { cmd = "ack", id = long.Parse(pos[0]) }),
    "undo-route" => Client(new { cmd = "undo-route", id = long.Parse(pos[0]) }),
    "ui" => Ui(),
    "publish" => Publish(),
    "swap" => Client(new { cmd = "swap", exe = Path.GetFullPath(pos[0]), mode = One("mode") ?? "ask" }),
    "swap-answer" => Client(new { cmd = "swap-answer", answer = pos[0] }),
    "swaps" => Client(new { cmd = "swaps" }),
    "stop-daemon" => Client(new { cmd = "stop-daemon" }),
    _ => Fail($"unknown command: {cmd}"),
};

int Version()
{
    if (opts.ContainsKey("json"))
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            build = Ver.Build, schema = Ver.Schema, shimProtocol = Ver.ShimProtocol, exe = Ver.ExePath,
        }));
    else
    {
        Console.WriteLine($"dodona build {Ver.Build}");
        Console.WriteLine($"  store schema   v{Ver.Schema}");
        Console.WriteLine($"  shim protocol  v{Ver.ShimProtocol}");
        Console.WriteLine($"  exe            {Ver.ExePath}");
        Console.WriteLine($"  published to   {Ver.BinRoot}");
    }
    return 0;
}

/// <summary>
/// Publish (§13): build a new binary into a FRESH versioned directory — Windows locks
/// the image of a running exe, so in-place is not an option — then ask the daemon(s) to
/// swap to it. The CLIENT builds, deliberately: a 15-second build inside the daemon's
/// command loop would block every other command, and the whole point is that nothing
/// stalls. --all broadcasts to every running instance (§14: no version pinning, a
/// published build swaps into all of them at once).
/// </summary>
int Publish()
{
    var project = Path.GetFullPath(One("project") ?? root);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    string outDir;

    var prebuilt = One("exe");
    if (prebuilt is not null)
    {
        outDir = Path.GetDirectoryName(Path.GetFullPath(prebuilt))!;
        Console.WriteLine($"publishing prebuilt binary: {prebuilt}");
    }
    else
    {
        outDir = Path.Combine(Ver.BinRoot, stamp);
        // The shim rides along: after a swap, new lanes are spawned from the new
        // binary's directory, so the shim must be there too. Live shims are untouched —
        // they are already running, which is exactly why hot-swap works.
        foreach (var proj in new[] { Path.Combine(project, "src", "Dodona", "Dodona.csproj"),
                                     Path.Combine(project, "src", "DodonaShim", "DodonaShim.csproj") })
        {
            if (!File.Exists(proj)) return Fail($"not a Dodona source tree: {proj} not found (use --project <dir> or --exe <path>)");
            Console.WriteLine($"building {Path.GetFileNameWithoutExtension(proj)} → {outDir}");
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (var a in new[] { "publish", proj, "-c", "Release", "-o", outDir, "--nologo", "-v", "q" })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) return Fail($"build failed ({Path.GetFileName(proj)}) — nothing was published, nothing swapped");
        }
    }

    var newExe = Path.Combine(outDir, "dodona.exe");
    if (!File.Exists(newExe)) return Fail($"published, but {newExe} is missing");

    var targets = opts.ContainsKey("all") ? LiveInstances() : new List<string> { ctlPipe };
    if (targets.Count == 0) { Console.WriteLine($"published {newExe}; no daemon running to swap"); return 0; }

    int worst = 0;
    foreach (var target in targets)
    {
        Console.WriteLine($"— swapping instance on {target}");
        var code = Client(new { cmd = "swap", exe = newExe, mode = One("mode") ?? "ask" }, target);
        worst = Math.Max(worst, code);
    }
    return worst;
}

/// <summary>Every running instance, found the way Windows lets you: the pipe namespace
/// is a directory. No shared registry, no lock file — nothing global (§14).</summary>
static List<string> LiveInstances()
{
    try
    {
        return Directory.GetFiles(@"\\.\pipe\")
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.StartsWith("dodona-") && n.EndsWith("-ctl"))
            .Select(n => n!)
            .Distinct()
            .ToList();
    }
    catch { return new List<string>(); }
}

// The ui verbs (§17) talk to the UI process, not the daemon — the UI testifies about
// what it is actually showing. Same line protocol, different pipe.
int Ui()
{
    if (pos.Count == 0) return Fail("ui verb required: dump | screenshot | pose <name> | overlay <PANE|off> | close");
    return pos[0] switch
    {
        "dump" => Client(new { verb = "dump" }, uiPipe),
        "screenshot" => Client(new { verb = "screenshot", @out = Path.GetFullPath(One("out") ?? "dodona-ui.png"), pane = One("pane") }, uiPipe),
        "pose" => pos.Count > 1 ? Client(new { verb = "pose", name = pos[1] }, uiPipe) : Fail("ui pose <name|live>"),
        "overlay" => pos.Count > 1 ? Client(new { verb = "overlay", pane = pos[1] }, uiPipe) : Fail("ui overlay <PANE|off>"),
        "close" => Client(new { verb = "close" }, uiPipe),
        _ => Fail($"unknown ui verb: {pos[0]}"),
    };
}

// ---------------------------------------------------------------- client role

int Client(object request, string? pipeName = null)
{
    pipeName ??= ctlPipe;
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
    try { pipe.Connect(3000); }
    catch
    {
        pipe.Dispose();
        // Start-on-demand (§13): the registry is a store, never a service — the store is
        // always there and the daemon is summoned. This is also the recovery path from a
        // failed swap or a crash: the next command brings the daemon back, and the shims
        // have been buffering the whole time.
        if (pipeName != ctlPipe || cmd == "stop-daemon" || Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1")
            return Fail(pipeName == ctlPipe ? $"daemon not running for this root (ctl pipe {pipeName})" : $"UI not running for this root (pipe {pipeName})");

        var reborn = Autostart(root);
        if (reborn is not null) return Fail($"could not start a daemon for this root: {reborn}");
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try { pipe.Connect(15000); }
        catch { pipe.Dispose(); return Fail("started a daemon but it never answered its control pipe"); }
    }
    var w = new StreamWriter(pipe) { AutoFlush = true };
    var r = new StreamReader(pipe);
    bool err = false;
    int? exitOverride = null;
    try
    {
        w.WriteLine(JsonSerializer.Serialize(request));
        string? line;
        while ((line = r.ReadLine()) is not null && line != "##end")
        {
            if (line.StartsWith("##exit ")) { exitOverride = int.Parse(line[7..]); continue; }
            Console.WriteLine(line);
            if (line.StartsWith("error:")) err = true;
        }
    }
    catch { err = true; }
    try { pipe.Dispose(); } catch { }   // daemon closes its end first; never flush into it
    return exitOverride ?? (err ? 1 : 0);
}

// ---------------------------------------------------------------- plumbing

/// <summary>Launch a daemon for this root, fully detached: no redirected stdio (a parent
/// that exits would break the pipe under it) and no window. Returns null on success, or
/// the failure reason.</summary>
static string? Autostart(string root)
{
    try
    {
        Console.Error.WriteLine("no daemon for this root — starting one");
        var exe = Environment.ProcessPath ?? "dodona.exe";
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = true,                     // detach: the daemon must outlive this CLI
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetFullPath(root),
        };
        psi.ArgumentList.Add("daemon");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(Path.GetFullPath(root));
        return System.Diagnostics.Process.Start(psi) is null ? "Process.Start returned null" : null;
    }
    catch (Exception ex) { return ex.Message; }
}

static (string? cmd, string root, Dictionary<string, List<string>> opts, List<string> pos) ParseArgs(string[] args)
{
    // Valueless flags must be declared: otherwise `--json` at the end of a line is
    // indistinguishable from a positional argument, and silently becomes one.
    var boolFlags = new HashSet<string> { "json", "successor", "all" };

    string? cmd = null;
    string root = Environment.CurrentDirectory;
    var opts = new Dictionary<string, List<string>>();
    var pos = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; continue; }
        if (args[i].StartsWith("--") && boolFlags.Contains(args[i][2..])) { opts[args[i][2..]] = new List<string> { "true" }; continue; }
        if (args[i].StartsWith("--") && i + 1 < args.Length)
        {
            var key = args[i][2..];
            if (!opts.TryGetValue(key, out var list)) opts[key] = list = new List<string>();
            list.Add(args[++i]);
            continue;
        }
        if (cmd is null) { cmd = args[i]; continue; }
        pos.Add(args[i]);
    }
    return (cmd, root, opts, pos);
}

string? One(string name) => opts.TryGetValue(name, out var l) ? l[0] : null;
List<string> Many(string name) => opts.TryGetValue(name, out var l) ? l : new List<string>();

static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

static void Help() => Console.WriteLine("""
    dodona — multi-agent orchestrator (M4)
      dodona daemon [--root <path>] [--successor]
      dodona version [--json]
    lanes:
      dodona lane-start --title <T> --child <agent exe> [--child-arg <a>]...
      dodona say <lane> <text> | tail <lane> [n] | status
    tickets & claims (§6/§11):
      dodona ticket-create --title <T> --claim <spec>... [--mode on-approval|auto]
              spec: path:<file> | new:<file> | subtree:<dir> | symbol:<name>
      dodona claim-check <ticket> <file>   (exit 0 covered / 1 denied)
      dodona claim-extend <ticket> --claim <spec>...
      dodona approve <ticket> | tickets
    merge (§7):
      dodona token-request <ticket> [--lease sec] | token-renew | token-release | token-status
      dodona land <ticket>
    hot swap (§13/§14 — nothing interrupted, no session lost):
      dodona publish [--project <dir>] [--all] [--exe <prebuilt>] [--mode now]
      dodona swap <new dodona.exe> [--mode now] | swap-answer <now|when-it-lands|hold>
      dodona swaps
    ui (§8/§17 — talks to the DodonaUi process, not the daemon):
      dodona ui dump | ui screenshot [--pane <PANE>] --out <png> | ui pose <name|live>
      dodona ui overlay <PANE|off> | ui close
      dodona ack <pane_event_id> | undo-route <routing_decision_id>
      dodona stop-daemon
    All commands accept --root <path> (default: cwd).
    """);
