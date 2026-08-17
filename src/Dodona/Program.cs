using System.IO.Pipes;
using System.Text.Json;
using Dodona;

// dodona — one binary, two roles, always separate processes:
//   dodona daemon --root <path>       the single writer: store, lanes, tickets, token
//   dodona <command> [--root <path>]  a client over the control pipe

var (cmd, root, opts, pos) = ParseArgs(args);
if (cmd is null) { Help(); return 1; }

string instanceId = Instance.Id(root);
string ctlPipe = Instance.CtlPipe(instanceId);
string uiPipe = Instance.UiPipe(instanceId);

// A missing or malformed argument is a usage mistake, not a crash. Without this a bare
// `dodona lane-stop` threw an unhandled IndexOutOfRange and printed a stack trace at the
// operator — which is also exactly what an agent would see when it gets a command
// slightly wrong, and a stack trace is a terrible thing to try to recover from.
try
{
    return await Dispatch();
}
catch (ArgumentOutOfRangeException) { return Usage(); }
catch (IndexOutOfRangeException) { return Usage(); }
catch (FormatException) { return Fail($"{cmd}: a numeric argument was expected"); }

int Usage() => Fail($"{cmd}: missing an argument — see `dodona` with no arguments for usage");

async Task<int> Dispatch() => cmd switch
{
    "version" => Version(),
    "daemon" => await Daemon.RunAsync(Path.GetFullPath(root), instanceId, ctlPipe, opts.ContainsKey("successor")),
    "lane-start" => Client(new { cmd = "lane-start", title = One("title") ?? "LANE", child = One("child"), model = One("model"), effort = One("effort"), childArgs = Many("child-arg") }),
    "lane-stop" => Client(new { cmd = "lane-stop", lane = long.Parse(pos[0]) }),
    "say" => Client(new { cmd = "say", lane = long.Parse(pos[0]), text = pos[1] }),
    "tail" => Client(new { cmd = "tail", lane = long.Parse(pos[0]), n = pos.Count > 1 ? int.Parse(pos[1]) : 20 }),
    "status" => Client(new { cmd = "status" }),
    "ticket-create" => Client(new { cmd = "ticket-create", title = One("title") ?? "TICKET", mode = One("mode") ?? "on-approval", repo = One("repo"), claims = Many("claim") }),
    "repos" => Client(new { cmd = "repos" }),
    "claim-check" => Client(new { cmd = "claim-check", ticket = long.Parse(pos[0]), path = pos[1] }),
    "claim-extend" => Client(new { cmd = "claim-extend", ticket = long.Parse(pos[0]), claims = Many("claim") }),
    "approve" => Client(new { cmd = "approve", ticket = long.Parse(pos[0]) }),
    "tickets" => Client(new { cmd = "tickets" }),
    "focus" => Client(new { cmd = "focus", lane = long.Parse(pos[0]) }),
    "input" => Client(new { cmd = "input", text = string.Join(" ", pos) }),
    "router-start" => Client(new { cmd = "router-start", child = One("child"), model = One("model"), effort = One("effort") }),
    "ticket-agent" => Client(new { cmd = "ticket-agent", ticket = long.Parse(pos[0]), child = One("child"), model = One("model"), effort = One("effort") }),
    "token-request" => Client(new { cmd = "token-request", ticket = long.Parse(pos[0]), lease = int.Parse(One("lease") ?? "120") }),
    "token-renew" => Client(new { cmd = "token-renew", ticket = long.Parse(pos[0]), lease = int.Parse(One("lease") ?? "120") }),
    "token-release" => Client(new { cmd = "token-release", ticket = long.Parse(pos[0]) }),
    "token-status" => Client(new { cmd = "token-status" }),
    "land" => Client(new { cmd = "land", ticket = long.Parse(pos[0]) }),
    "ack" => Client(new { cmd = "ack", id = long.Parse(pos[0]) }),
    "undo-route" => Client(new { cmd = "undo-route", id = long.Parse(pos[0]) }),
    "ui" => Ui(),
    "policy" => Client(new { cmd = "policy", text = string.Join(" ", pos) }),
    "repo-status" => Client(new { cmd = "repo-status" }),
    "repo-init" => Client(new { cmd = "repo-init", adopt = opts.ContainsKey("adopt") }),
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
        // All three executables land in ONE directory, which is what makes the published
        // folder an application rather than a build output: the UI finds dodona.exe
        // beside itself, and the daemon finds DodonaShim.exe beside itself, with no
        // environment variables anywhere. The shim must also be here because after a swap
        // new lanes are spawned from the new binary's directory (live shims are untouched
        // — they are already running, which is exactly why hot-swap works).
        foreach (var proj in new[] { Path.Combine(project, "src", "Dodona", "Dodona.csproj"),
                                     Path.Combine(project, "src", "DodonaShim", "DodonaShim.csproj"),
                                     Path.Combine(project, "src", "DodonaUi", "DodonaUi.csproj") })
        {
            if (!File.Exists(proj)) return Fail($"not a Dodona source tree: {proj} not found (use --project <dir> or --exe <path>)");
            Console.WriteLine($"building {Path.GetFileNameWithoutExtension(proj)} → {outDir}");
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
            // Build into publish-private obj/bin, NOT the dev tree's: a daemon running
            // from bin\Release locks its image (Windows), and publish kept dying on that
            // lock whenever a test daemon lingered. Publish must never contend with
            // whatever is running — that is its whole reason to exist.
            // Only bin is redirected: obj must stay put (moving BaseIntermediateOutputPath
            // after a restore double-generates AssemblyInfo → CS0579), and the lock that
            // kept killing publish was on bin's apphost, never on obj.
            var scratchBin = Path.Combine(Path.GetTempPath(), "dodona-publish", stamp, Path.GetFileNameWithoutExtension(proj));
            foreach (var a in new[] { "publish", proj, "-c", "Release", "-o", outDir, "--nologo", "-v", "q",
                                      $"-p:BaseOutputPath={scratchBin}\\" })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) return Fail($"build failed ({Path.GetFileName(proj)}) — nothing was published, nothing swapped");
        }
    }

    var newExe = Path.Combine(outDir, "dodona.exe");
    if (!File.Exists(newExe)) return Fail($"published, but {newExe} is missing");
    Shortcut(outDir);

    var targets = opts.ContainsKey("all") ? Instance.LiveCtlPipes() : new List<string> { ctlPipe };
    int worst = 0;
    if (targets.Count == 0) Console.WriteLine($"published {newExe}; no daemon running to swap");
    foreach (var target in targets)
    {
        Console.WriteLine($"— swapping instance on {target}");
        var code = Client(new { cmd = "swap", exe = newExe, mode = One("mode") ?? "ask" }, target);
        worst = Math.Max(worst, code);
    }

    // The UI is its own process and its own build. Swapping only the daemon leaves the
    // operator looking at the old window — which is exactly what "nothing happened" looks
    // like, and is how a published UI change goes unnoticed. Refresh windows too.
    // Deliberately after the daemon: the new UI should come up against the new daemon.
    var newUiExe = Path.Combine(outDir, "DodonaUi.exe");
    if (File.Exists(newUiExe))
    {
        var uiTargets = opts.ContainsKey("all") ? Instance.LiveUiPipes() : new List<string> { uiPipe };
        foreach (var target in uiTargets.Where(t => Instance.LiveUiPipes().Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"— updating UI on {target}");
            worst = Math.Max(worst, Client(new { verb = "update", exe = newUiExe }, target));
        }
    }
    return worst;
}

/// <summary>
/// Keep the desktop shortcut pointing at the newest build. Every publish lands in a new
/// versioned directory (Windows locks a running image, so in-place is impossible) and old
/// ones are garbage-collected — which would leave a fixed shortcut dangling after the
/// first hot swap. So: `--shortcut` creates it once, and after that every publish
/// refreshes it automatically, but only if it already exists. Opt in once, stays correct
/// forever, and no publish ever writes to the desktop uninvited.
/// </summary>
void Shortcut(string outDir)
{
    // Only ever repoint the shortcut at a build in the REAL install location. A publish
    // into an overridden DODONA_BIN_ROOT is a test or an experiment — the acceptance
    // suite publishes into a temp directory and then deletes it, which would leave the
    // desktop icon aimed at nothing.
    var defaultBinRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dodona", "bin");
    if (!Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(defaultBinRoot), StringComparison.OrdinalIgnoreCase))
        return;

    var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Dodona.lnk");
    if (!opts.ContainsKey("shortcut") && !File.Exists(lnk)) return;

    var target = Path.Combine(outDir, "DodonaUi.exe");
    if (!File.Exists(target)) { Console.Error.WriteLine($"note: no DodonaUi.exe in {outDir}; shortcut left alone"); return; }
    try
    {
        // A .lnk is a COM shell object; PowerShell is the shortest honest way to write one.
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnk}');" +
                 $"$s.TargetPath='{target}';$s.WorkingDirectory='{outDir}';" +
                 $"$s.Description='Dodona — multi-agent orchestrator';$s.Save()";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", ps }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(15000);
        Console.WriteLine(p.ExitCode == 0 ? $"desktop shortcut → {target}" : "note: could not write the desktop shortcut");
    }
    catch (Exception ex) { Console.Error.WriteLine($"note: could not write the desktop shortcut: {ex.Message}"); }
}

// The ui verbs (§17) talk to the UI process, not the daemon — the UI testifies about
// what it is actually showing. Same line protocol, different pipe.
int Ui()
{
    if (pos.Count == 0) return Fail("ui verb required: dump | screenshot | pose <name> | overlay <PANE|off> | update <exe> | close");
    return pos[0] switch
    {
        "dump" => Client(new { verb = "dump" }, uiPipe),
        "screenshot" => Client(new { verb = "screenshot", @out = Path.GetFullPath(One("out") ?? "dodona-ui.png"), pane = One("pane") }, uiPipe),
        "pose" => pos.Count > 1 ? Client(new { verb = "pose", name = pos[1] }, uiPipe) : Fail("ui pose <name|live>"),
        "overlay" => pos.Count > 1 ? Client(new { verb = "overlay", pane = pos[1] }, uiPipe) : Fail("ui overlay <PANE|off>"),
        "update" => pos.Count > 1 ? Client(new { verb = "update", exe = Path.GetFullPath(pos[1]) }, uiPipe) : Fail("ui update <DodonaUi.exe>"),
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
    var boolFlags = new HashSet<string> { "json", "successor", "all", "adopt", "shortcut" };

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
      dodona lane-start --title <T> [--model sonnet] [--child <exe> [--child-arg <a>]...]
              no --child means a real claude lane in the project (no ticket, no claim gate)
      dodona say <lane> <text> | tail <lane> [n] | status
    model & effort (§9):
      dodona policy                         (the table: defaults, rules, override syntax)
      dodona policy <text>                  (what that sentence would run as, and why)
              override in any prompt: @opus @max <text>
    project setup:
      dodona repo-status                    (is this folder a repo? what is inside it?)
      dodona repo-init [--adopt]            (--adopt commits the files already there)
      dodona repos                          (the workspace's repositories, and their tokens)
    tickets & claims (§6/§11):
      dodona ticket-create --title <T> --claim <spec>... [--mode on-approval|auto] [--repo <name>]
              a ticket belongs to ONE repository, usually inferred from its claim paths
              spec: path:<file> | new:<file> | subtree:<dir> | symbol:<name>
      dodona claim-check <ticket> <file>   (exit 0 covered / 1 denied)
      dodona claim-extend <ticket> --claim <spec>...
      dodona approve <ticket> | tickets
    merge (§7):
      dodona token-request <ticket> [--lease sec] | token-renew | token-release | token-status
      dodona land <ticket>
    hot swap (§13/§14 — nothing interrupted, no session lost):
      dodona publish [--project <dir>] [--all] [--exe <prebuilt>] [--mode now] [--shortcut]
              --shortcut puts Dodona on the desktop; later publishes keep it current
              swaps live daemons, then refreshes live UIs (they are separate processes)
      dodona swap <new dodona.exe> [--mode now] | swap-answer <now|when-it-lands|hold>
      dodona swaps
    ui (§8/§17 — talks to the DodonaUi process, not the daemon):
      dodona ui dump | ui screenshot [--pane <PANE>] --out <png> | ui pose <name|live>
      dodona ui overlay <PANE|off> | ui update <DodonaUi.exe> | ui close
      dodona ack <pane_event_id> | undo-route <routing_decision_id>
      dodona stop-daemon
    All commands accept --root <path> (default: cwd).
    """);
