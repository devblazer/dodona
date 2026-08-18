using System.IO.Pipes;
using System.Text.Json;
using Dodona;

// dodona — one binary, two roles, always separate processes:
//   dodona daemon --root <path>       the single writer: store, lanes, tickets, token
//   dodona <command> [--root <path>]  a client over the control pipe

var (cmd, root, opts, pos) = ParseArgs(args);
if (cmd is null) { Help(); return 1; }

// ---------------------------------------------------------------- who am I talking to
// Identity is the WORKSPACE (docs/WORKSPACES-CONCIERGE.md §1), not a hash of --root.
// Two ways in, and they are deliberately different:
//   --workspace <name|id|alias>  addresses a workspace directly; never creates one,
//                                because naming one that does not exist is a typo.
//   --root <path>  (the default)  asks the registry who owns that path — and if nobody
//                                does, migrates or creates a workspace for it, which is
//                                what makes every pre-workspace project and every
//                                existing acceptance suite keep working untouched.
// Registry-free commands (`version`, and the workspace verbs that manage the registry
// themselves) are resolved lazily so a broken registry never stops you inspecting a binary.
// Concierge commands address the machine-global concierge (§2), never a workspace, so they
// skip workspace resolution entirely — asking "which workspace is this" of the component
// that belongs to none is the category error §2's authority cap exists to prevent.
var conciergeCmds = new HashSet<string>
{
    "concierge", "concierge-status", "concierge-resolve", "concierge-feed",
    "concierge-ack", "concierge-questions", "concierge-answer", "concierge-review",
    "concierge-stop",
};

string instanceId = "", wsName = "", primary = Path.GetFullPath(root);
if (cmd is not ("version" or "workspaces" or "workspace-create") && !conciergeCmds.Contains(cmd))
{
    try
    {
        using var reg0 = new Registry();
        Workspace? ws;
        if (One("workspace") is { Length: > 0 } named)
        {
            ws = WorkspaceResolve.ByNameOrId(reg0, named);
            if (ws is null)
            {
                var have = reg0.All();
                Console.Error.WriteLine($"no workspace \"{named}\"" +
                    (have.Count > 0 ? $" — have: {string.Join(", ", have.Select(x => x.Name))}" : " — none exist yet"));
                Console.Error.WriteLine("       make one:  dodona workspace-create --name <NAME> --member <path>");
                return 2;
            }
        }
        else
        {
            var resolved = WorkspaceResolve.ForPath(reg0, root);
            ws = resolved.Ws;
            // Announced, not silent (§11): a workspace appearing, or a store moving out of
            // a project folder, is exactly the kind of thing an operator must be able to
            // see in the scrollback afterwards.
            if (resolved.Note is not null) Console.Error.WriteLine($"dodona: {resolved.Note}");
        }
        instanceId = ws.Id;
        wsName = ws.Name;
        primary = ws.Primary ?? primary;
    }
    catch (Exception ex) { return Fail($"workspace registry: {ex.Message}"); }
}

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
    "daemon" => await Daemon.RunAsync(primary, instanceId, wsName, ctlPipe, opts.ContainsKey("successor")),
    // ---- workspaces (WORKSPACES-CONCIERGE.md §1). Answered in THIS process, deliberately,
    // even now that a concierge exists: the concierge owns the registry as the thing that
    // RESOLVES and LEARNS from it (§2.1), while the file itself stays safe for several
    // writers — the partial unique index is the arbiter, not a process. A registry you
    // cannot edit because a daemon will not start is worse than one two processes can edit
    // safely, and it is the same reasoning that keeps registry READS direct everywhere.
    "workspaces" => WorkspaceList(),
    "workspace-create" => WorkspaceCreate(),
    "workspace-attach" => WorkspaceAttach(),
    "workspace-detach" => WorkspaceEdit((r, id) => r.Detach(id, RequireMember(), out var e) ? null : e, "detached"),
    "workspace-move" => WorkspaceEdit((r, id) => r.Move(id, RequireMember(), out var e) ? null : e, "moved"),
    "workspace-rename" => WorkspaceEdit((r, id) => r.Rename(id, pos[0], out var e) ? null : e, "renamed"),
    "workspace-alias" => WorkspaceEdit((r, id) => r.AddAlias(id, pos[0], out var e) ? null : e, "aliased"),
    "workspace-forget" => WorkspaceEdit((r, id) => r.Forget(id, out var e) ? null : e, "forgotten"),
    "where" => Where(),
    // ---- the concierge (§2): one per machine, its own store, its own ctl pipe. It answers
    // exactly one question — which workspace — and holds no lanes, no claims, no tokens.
    "concierge" => await Concierge.RunAsync(),
    "concierge-status" => Cx(new { cmd = "status" }),
    "concierge-resolve" => Cx(new { cmd = "resolve", text = string.Join(" ", pos), from = One("from") }),
    "concierge-feed" => Cx(new { cmd = "feed", n = int.TryParse(One("n"), out var fn) ? fn : 30 }),
    "concierge-ack" => Cx(new { cmd = "ack", id = long.Parse(pos[0]) }),
    "concierge-questions" => Cx(new { cmd = "questions" }),
    "concierge-answer" => Cx(new { cmd = "answer", id = long.Parse(pos[0]), answer = string.Join(" ", pos.Skip(1)) }),
    "concierge-review" => Cx(new { cmd = "review", text = string.Join(" ", pos), workspace = One("workspace-id") ?? One("from") }),
    "concierge-stop" => Cx(new { cmd = "stop" }),
    "lane-start" => Client(new { cmd = "lane-start", title = One("title") ?? "LANE", child = One("child"), model = One("model"), effort = One("effort"), childArgs = Many("child-arg") }),
    "lane-stop" => Client(new { cmd = "lane-stop", lane = long.Parse(pos[0]) }),
    "lane-respawn" => Client(new { cmd = "lane-respawn", lane = long.Parse(pos[0]) }),
    "lane-rename" => Client(new { cmd = "lane-rename", lane = long.Parse(pos[0]), title = pos[1] }),
    "brain-start" => Client(new { cmd = "brain-start", hi = opts.ContainsKey("hi") }),
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
    "compressor-start" => Client(new { cmd = "compressor-start", child = One("child"), model = One("model"), effort = One("effort"),
                                       count = int.TryParse(One("count"), out var cpn) ? cpn : (int?)null }),
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

// ---------------------------------------------------------------- the concierge (§2)

/// <summary>
/// A client on the concierge's control pipe, with start-on-demand — the same doctrine as the
/// daemon (§13: the store is always there, the process is summoned). Deliberately NOT the
/// generic <see cref="Client"/>: that one summons a *workspace* daemon and reports failures
/// in workspace terms, and the concierge belongs to no workspace.
///
/// The concierge is also **not hot-swapped**, and that is a decision rather than an omission.
/// The M4 handoff exists to protect an agent mid-turn from being interrupted; the concierge
/// holds no work agents, no lanes, no claims and no merge tokens. Its only state that must
/// survive is rows (pending questions, resolutions, feed), and rows survive anything. So a
/// publish stops it and the next command revives it — losing at most one in-flight
/// classification, which every rung of the ladder already treats as "no opinion".
/// </summary>
int Cx(object request)
{
    var pipe = Instance.CtlPipe(Concierge.Id);
    if (cmd == "concierge-stop" || Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1")
        return Client(request, pipe);
    if (!Instance.IsLive(Concierge.Id))
    {
        Console.Error.WriteLine("no concierge running — starting one");
        try
        {
            var exe = Environment.ProcessPath ?? "dodona.exe";
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,                     // detach: it must outlive this CLI
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Paths.NeutralCwd(),      // it has no project, and must load none
            };
            psi.ArgumentList.Add("concierge");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { return Fail($"could not start the concierge: {ex.Message}"); }

        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && !Instance.IsLive(Concierge.Id)) Thread.Sleep(200);
        if (!Instance.IsLive(Concierge.Id)) return Fail("started a concierge but it never answered its control pipe");
    }
    return Client(request, pipe);
}

// ---------------------------------------------------------------- workspaces (§1)

int WorkspaceList()
{
    using var reg = new Registry();
    var all = reg.All();
    if (opts.ContainsKey("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(all.Select(w => new
        {
            id = w.Id, name = w.Name, live = Instance.IsLive(w.Id), store = Paths.Store(w.Id),
            aliases = w.Aliases,
            members = w.Members.Select(m => new { path = m.Path, git = m.IsGit }).ToList(),
        })));
        return 0;
    }
    if (all.Count == 0)
    {
        Console.WriteLine("no workspaces yet.");
        Console.WriteLine("  dodona workspace-create --name work --member C:\\repos\\thing");
        Console.WriteLine("  (or just run any command with --root <path>: a workspace is made for it)");
        return 0;
    }
    foreach (var w in all)
    {
        Console.WriteLine($"{(Instance.IsLive(w.Id) ? "*" : " ")} {w.Name}  ({w.Id})" +
                          (w.Aliases.Count > 0 ? $"  aka {string.Join(", ", w.Aliases)}" : ""));
        foreach (var m in w.Members) Console.WriteLine($"      {(m.IsGit ? "repo  " : "folder")} {m.Path}");
        if (w.Members.Count == 0) Console.WriteLine("      (no members yet — dodona workspace-attach --member <path>)");
    }
    Console.WriteLine("\n* = a daemon is running for it");
    return 0;
}

int WorkspaceCreate()
{
    var name = One("name") ?? (pos.Count > 0 ? pos[0] : null);
    if (name is null) return Fail("workspace-create --name <NAME> [--member <path>]... [--bulk]");
    using var reg = new Registry();
    if (reg.ByNameOrId(name) is Workspace clash) return Fail($"\"{name}\" already resolves to {clash.Label}");
    var ws = reg.Create(name);
    Console.WriteLine($"workspace \"{ws.Name}\" ({ws.Id})  store {Paths.Store(ws.Id)}");
    int worst = 0;
    foreach (var m in Many("member"))
        foreach (var path in opts.ContainsKey("bulk") ? WorkspaceResolve.BulkCandidates(m) : new List<string> { m })
            worst = Math.Max(worst, AttachOne(reg, ws.Id, path));
    Console.WriteLine($"undo: dodona workspace-forget --workspace {ws.Id}   (the store directory is kept)");
    return worst;
}

int WorkspaceAttach()
{
    var members = Many("member");
    if (members.Count == 0) return Fail("workspace-attach --member <path>... [--bulk]   (--bulk expands a folder into its repos)");
    using var reg = new Registry();
    int worst = 0;
    foreach (var m in members)
        foreach (var path in opts.ContainsKey("bulk") ? WorkspaceResolve.BulkCandidates(m) : new List<string> { m })
            worst = Math.Max(worst, AttachOne(reg, instanceId, path));
    return worst;
}

int AttachOne(Registry reg, string wsId, string path)
{
    if (reg.Attach(wsId, path, out var err)) { Console.WriteLine($"  + {Instance.Canonical(path)}"); return 0; }
    Console.Error.WriteLine($"error: {err}");
    return 2;
}

string RequireMember() => One("member") ?? (pos.Count > 0 ? pos[0] : throw new ArgumentOutOfRangeException(nameof(opts), "--member <path> required"));

int WorkspaceEdit(Func<Registry, string, string?> op, string verb)
{
    using var reg = new Registry();
    var err = op(reg, instanceId);
    if (err is not null) return Fail($"error: {err}");
    Console.WriteLine($"{verb}: workspace {wsName} ({instanceId})");
    return 0;
}

/// <summary>Where this workspace's state actually lives. Exists because the answer stopped
/// being obvious the moment a store left the project folder: DEBUGGING.md's whole first
/// section was a table of `&lt;root&gt;\.dodona\...` paths, and an acceptance suite that
/// wants to read a store must be able to ask rather than reconstruct.</summary>
int Where()
{
    var storeDir = Paths.WorkspaceDir(instanceId);
    if (opts.ContainsKey("json"))
    {
        using var reg = new Registry();
        var ws = reg.ById(instanceId);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id = instanceId, name = wsName, live = Instance.IsLive(instanceId),
            dir = storeDir, store = Paths.Store(instanceId), primary,
            ctlPipe, uiPipe, registry = Paths.Registry,
            members = ws?.Members.Select(m => m.Path).ToList() ?? new List<string>(),
        }));
        return 0;
    }
    Console.WriteLine($"workspace  {wsName} ({instanceId}){(Instance.IsLive(instanceId) ? "  [daemon running]" : "")}");
    Console.WriteLine($"  store    {Paths.Store(instanceId)}");
    Console.WriteLine($"  dir      {storeDir}       (shim-lane<N>.json live here too)");
    Console.WriteLine($"  primary  {primary}");
    Console.WriteLine($"  ctl pipe {ctlPipe}");
    Console.WriteLine($"  registry {Paths.Registry}");
    return 0;
}

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
        "type" => pos.Count > 1 ? Client(new { verb = "type", text = string.Join(" ", pos.Skip(1)) }, uiPipe) : Fail("ui type <text>"),
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
            return Fail(
                pipeName == ctlPipe ? $"daemon not running for this root (ctl pipe {pipeName})"
                : pipeName == Instance.CtlPipe(Concierge.Id) ? $"concierge not running (ctl pipe {pipeName})"
                : $"UI not running for this root (pipe {pipeName})");

        var reborn = Autostart(instanceId, primary);
        if (reborn is not null) return Fail($"could not start a daemon for workspace {wsName}: {reborn}");
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

/// <summary>Launch a daemon for this workspace, fully detached: no redirected stdio (a
/// parent that exits would break the pipe under it) and no window. Addressed by workspace
/// id, not by path — the registry has already resolved who this is, and re-resolving in
/// the child is a second chance to disagree. Returns null on success, or the reason.</summary>
static string? Autostart(string wsId, string primary)
{
    try
    {
        Console.Error.WriteLine("no daemon for this workspace — starting one");
        var exe = Environment.ProcessPath ?? "dodona.exe";
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = true,                     // detach: the daemon must outlive this CLI
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = Directory.Exists(primary) ? primary : Path.GetTempPath(),
        };
        psi.ArgumentList.Add("daemon");
        psi.ArgumentList.Add("--workspace");
        psi.ArgumentList.Add(wsId);
        return System.Diagnostics.Process.Start(psi) is null ? "Process.Start returned null" : null;
    }
    catch (Exception ex) { return ex.Message; }
}

static (string? cmd, string root, Dictionary<string, List<string>> opts, List<string> pos) ParseArgs(string[] args)
{
    // Valueless flags must be declared: otherwise `--json` at the end of a line is
    // indistinguishable from a positional argument, and silently becomes one.
    var boolFlags = new HashSet<string> { "json", "successor", "all", "adopt", "shortcut", "hi", "bulk" };

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
    dodona — multi-agent orchestrator (M4 + workspaces)
      dodona daemon [--workspace <name>] [--root <path>] [--successor]
      dodona version [--json]
    workspaces (a named, durable session group over N folders):
      dodona workspaces [--json]            (all of them; * = a daemon is running)
      dodona workspace-create --name <NAME> [--member <path>]... [--bulk]
              --bulk expands a folder into the repositories under it
      dodona workspace-attach --member <path>... [--bulk]
      dodona workspace-detach --member <path> | workspace-move --member <path>
              a REPO belongs to at most one workspace; move is how you reassign it
      dodona workspace-rename <NAME> | workspace-alias <name> | workspace-forget
      dodona where [--json]                 (store, dir, pipes — state left the project folder)
    the concierge (one per machine; answers only "which workspace"):
      dodona concierge                      (run it; any concierge-* command starts one)
      dodona concierge-status               (tiers, the search fence, open questions)
      dodona concierge-resolve <text>       (walk the ladder, print the verdict as JSON)
      dodona concierge-feed | concierge-ack <id>
      dodona concierge-questions | concierge-answer <id> <name|new:NAME>
              answering TEACHES an alias, so the next one resolves for free
      dodona concierge-review <text> --workspace-id <id> | concierge-stop
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
    lanes & the brain (§3):
      dodona lane-rename <lane> <TITLE> | lane-respawn <lane> | lane-stop <lane>
      dodona brain-start [--hi]             (warm the dispatcher brain; hi = expensive tier)
    ui (§8/§17 — talks to the DodonaUi process, not the daemon):
      dodona ui type <text>                 (submit through the same path as Enter — no focus)
      DodonaUi.exe --test-window            (off-screen, never activates: for tests/agents)
      dodona ui dump | ui screenshot [--pane <PANE>] --out <png> | ui pose <name|live>
      dodona ui overlay <PANE|off> | ui update <DodonaUi.exe> | ui close
      dodona ack <pane_event_id> | undo-route <routing_decision_id>
      dodona stop-daemon
    Every command takes --workspace <name|id|alias>, or --root <path> (default: cwd) to
    address the workspace that owns that path — an unowned path gets a workspace made for
    it, named after the folder, with that folder as its sole member.
    """);
