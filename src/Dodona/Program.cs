using System.IO.Pipes;
using System.Text.Json;
using Dodona;

// dodona — one binary, two roles, always separate processes:
//   dodona daemon --root <path>       the single writer: store, lanes, tickets, token
//   dodona <command> [--root <path>]  a client over the control pipe

var (cmd, root, opts, pos) = ParseArgs(args);
if (cmd is null) { Help(); return 1; }

// ---------------------------------------------------------------- who am I talking to
// Identity is the WORKSPACE (docs/WORKSPACES-CONCIERGE.md §1), not a hash of --root. Two
// ways in, and they are deliberately different:
//   --workspace <name|id|alias>  addresses a workspace directly; never creates one, because
//                                naming one that does not exist is a typo.
//   --root <path>  (the default) asks the registry who owns that path — and if nobody does,
//                                MIGRATES or CREATES a workspace for it, which is what makes
//                                every pre-workspace project and every existing acceptance
//                                suite keep working untouched.
//
// **Resolved LAZILY, and that is load-bearing.** Doing it eagerly meant any command at all
// created-or-migrated a workspace for whatever the cwd happened to be, purely as a side
// effect of being run there — `dodona swaps`, `dodona ui dump --shell`, `dodona publish`.
// Worse, when migration was legitimately refused (a pre-workspace daemon still holding the
// store), commands that never needed a workspace failed with it. Found live: `publish` run
// from a source tree whose own daemon was running could not publish, because resolving a
// workspace it did not need refused first. Nothing resolves now until something asks.
Workspace? wsCache = null;

Workspace Ws()
{
    if (wsCache is not null) return wsCache;
    using var reg = new Registry();
    if (One("workspace") is { Length: > 0 } named)
    {
        var found = WorkspaceResolve.ByNameOrId(reg, named);
        if (found is null)
        {
            var have = reg.All();
            throw new WorkspaceUnavailable(
                $"no workspace \"{named}\"" +
                (have.Count > 0 ? $" — have: {string.Join(", ", have.Select(x => x.Name))}" : " — none exist yet") +
                "\n       make one:  dodona workspace-create --name <NAME> --member <path>");
        }
        return wsCache = found;
    }
    var resolved = WorkspaceResolve.ForPath(reg, root);
    // Announced, not silent (§11): a workspace appearing, or a store moving out of a project
    // folder, is exactly the kind of thing an operator must be able to see afterwards.
    if (resolved.Note is not null) Console.Error.WriteLine($"dodona: {resolved.Note}");
    return wsCache = resolved.Ws;
}

string WsId() => Ws().Id;
string WsName() => Ws().Name;
string WsPrimary() => Ws().Primary ?? Path.GetFullPath(root);
string CtlPipe() => Instance.CtlPipe(WsId());

// `--shell` addresses the one-window shell, which belongs to no workspace (§4/§6): it shows
// every awake workspace, so it cannot borrow the ui pipe of one of them.
string UiPipeName() => opts.ContainsKey("shell") ? Instance.UiPipe(Instance.ShellId) : Instance.UiPipe(WsId());

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
// A workspace that cannot be resolved is a usage problem too, not a crash — and its message
// already says what to do about it (rename, move, or stop the daemon holding the store).
catch (WorkspaceUnavailable ex) { return Fail(ex.Message); }

int Usage() => Fail($"{cmd}: missing an argument — see `dodona` with no arguments for usage");

async Task<int> Dispatch() => cmd switch
{
    "version" => Version(),
    "daemon" => await Daemon.RunAsync(WsPrimary(), WsId(), WsName(), CtlPipe(), opts.ContainsKey("successor")),
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
    "ps" => Ps(),
    "stop-all" => StopAll(),
    // ---- the concierge (§2): one per machine, its own store, its own ctl pipe. It answers
    // exactly one question — which workspace — and holds no lanes, no claims, no tokens.
    "concierge" => await Concierge.RunAsync(),
    "concierge-status" => Cx(new { cmd = "status" }),
    "concierge-resolve" => Cx(new { cmd = "resolve", text = string.Join(" ", pos), from = One("from") }),
    // Prompt-first (§4): resolve, WAKE the workspace if it is asleep, and hand the sentence to
    // ITS dispatcher. This is what the shell's input box calls when several workspaces are on
    // screen. Needs no entry in a skip list: workspace resolution is lazy, so a command that
    // never asks for a workspace never resolves one.
    "route" => Cx(new { cmd = "route", text = string.Join(" ", pos), from = One("from") }),
    "concierge-feed" => Cx(new { cmd = "feed", n = int.TryParse(One("n"), out var fn) ? fn : 30 }),
    "concierge-ack" => Cx(new { cmd = "ack", id = long.Parse(pos[0]) }),
    "concierge-questions" => Cx(new { cmd = "questions" }),
    "concierge-answer" => Cx(new { cmd = "answer", id = long.Parse(pos[0]), answer = string.Join(" ", pos.Skip(1)) }),
    "concierge-review" => Cx(new { cmd = "review", text = string.Join(" ", pos), workspace = One("workspace-id") ?? One("from") }),
    "concierge-focus" => Cx(new { cmd = "focus", workspace = One("workspace-id") ?? (pos.Count > 0 ? pos[0] : "") }),
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
    // Collapse a lane's tile to a one-line strip, or expand it again. A view choice the
    // operator makes; the grid itself grows on its own (§8 as revised).
    "lane-collapse" => Client(new { cmd = "lane-collapse", lane = long.Parse(pos[0]), collapsed = true }),
    "lane-expand" => Client(new { cmd = "lane-collapse", lane = long.Parse(pos[0]), collapsed = false }),
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
            worst = Math.Max(worst, AttachOne(reg, WsId(), path));
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
    // Ws() BEFORE the registry opens, so the name we report is the one before a rename.
    var was = Ws();
    using var reg = new Registry();
    var err = op(reg, was.Id);
    if (err is not null) return Fail($"error: {err}");
    Console.WriteLine($"{verb}: workspace {was.Name} ({was.Id})");
    return 0;
}

/// <summary>Where this workspace's state actually lives. Exists because the answer stopped
/// being obvious the moment a store left the project folder: DEBUGGING.md's whole first
/// section was a table of `&lt;root&gt;\.dodona\...` paths, and an acceptance suite that
/// wants to read a store must be able to ask rather than reconstruct.</summary>
int Where()
{
    var ws = Ws();
    var storeDir = Paths.WorkspaceDir(ws.Id);
    if (opts.ContainsKey("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id = ws.Id, name = ws.Name, live = Instance.IsLive(ws.Id),
            dir = storeDir, store = Paths.Store(ws.Id), primary = ws.Primary ?? "",
            ctlPipe = Instance.CtlPipe(ws.Id), uiPipe = Instance.UiPipe(ws.Id), registry = Paths.Registry,
            members = ws.Members.Select(m => m.Path).ToList(),
        }));
        return 0;
    }
    Console.WriteLine($"workspace  {ws.Name} ({ws.Id}){(Instance.IsLive(ws.Id) ? "  [daemon running]" : "")}");
    Console.WriteLine($"  store    {Paths.Store(ws.Id)}");
    Console.WriteLine($"  dir      {storeDir}       (shim-lane<N>.json live here too)");
    Console.WriteLine($"  primary  {ws.Primary ?? "(no members)"}");
    Console.WriteLine($"  ctl pipe {Instance.CtlPipe(ws.Id)}");
    Console.WriteLine($"  registry {Paths.Registry}");
    return 0;
}

/// <summary>
/// Everything Dodona has running on this machine, in one place.
///
/// This exists because of a real surprise: the operator closed their window, believed
/// nothing was running, and a daemon plus seventeen lane shims had been up for hours. That
/// was not a bug — **a daemon deliberately outlives its UI window** (§13: the window is the
/// disposable half, agents survive it, which is the whole point of the shim). But nothing
/// anywhere would tell you so. "Is anything running?" had no answer short of Task Manager,
/// and Task Manager cannot tell a test's processes from your own.
///
/// Reads liveness off the OS pipe namespace, the same way publish and the picker do (§14 —
/// nothing global, no lock file, liveness is observed rather than stored), and crosses it
/// with the registry so every live thing gets a name instead of a hex id.
/// </summary>
int Ps()
{
    var live = Instance.LiveCtlPipes();
    var ui = Instance.LiveUiPipes();
    var rows = new List<object>();
    int running = 0;

    List<Workspace> all;
    try { using var reg = new Registry(); all = reg.All(); }
    catch { all = new List<Workspace>(); }

    Console.WriteLine("WHAT  NAME                 DAEMON  WINDOW  LANES  WHERE");
    foreach (var w in all)
    {
        var isLive = live.Contains(Instance.CtlPipe(w.Id), StringComparer.OrdinalIgnoreCase);
        var hasUi = ui.Contains(Instance.UiPipe(w.Id), StringComparer.OrdinalIgnoreCase);
        var shims = ShimPids(Paths.WorkspaceDir(w.Id)).Count;
        if (!isLive && !hasUi && shims == 0) continue;      // asleep and idle: not "running"
        running++;
        Console.WriteLine($"ws    {Trim(w.Name, 20),-20} {(isLive ? "yes   " : "no    ")}  " +
                          $"{(hasUi ? "yes   " : "no    ")}  {shims,-5}  {w.Primary ?? "(no members)"}");
        rows.Add(new { kind = "workspace", id = w.Id, name = w.Name, daemon = isLive, window = hasUi, lanes = shims });
    }

    if (live.Contains(Instance.CtlPipe(Instance.ConciergeId), StringComparer.OrdinalIgnoreCase))
    {
        running++;
        Console.WriteLine($"cx    {Trim("concierge", 20),-20} yes     -       -      {Paths.ConciergeDir}");
        rows.Add(new { kind = "concierge", id = Instance.ConciergeId, name = "concierge", daemon = true, window = false, lanes = 0 });
    }
    if (ui.Contains(Instance.UiPipe(Instance.ShellId), StringComparer.OrdinalIgnoreCase))
    {
        running++;
        Console.WriteLine($"shell {Trim("(all workspaces)", 20),-20} -       yes     -      one window over N workspaces");
        rows.Add(new { kind = "shell", id = Instance.ShellId, name = "shell", daemon = false, window = true, lanes = 0 });
    }

    // A live ctl pipe that matches no workspace in THIS registry: a pre-workspace instance,
    // or one belonging to another DODONA_HOME. Named honestly rather than hidden — it is
    // exactly the thing that was running unnoticed, and it is NOT a `--all` publish target.
    var accounted = all.Select(w => Instance.CtlPipe(w.Id))
        .Append(Instance.CtlPipe(Instance.ConciergeId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var orphan in live.Where(p => !accounted.Contains(p)))
    {
        running++;
        Console.WriteLine($"?     {Trim(orphan, 20),-20} yes     ?       ?      unregistered — pre-workspace, or another DODONA_HOME");
        rows.Add(new { kind = "unregistered", id = orphan, name = orphan, daemon = true, window = false, lanes = 0 });
    }

    if (opts.ContainsKey("json")) { Console.WriteLine(JsonSerializer.Serialize(rows)); return 0; }
    if (running == 0) Console.WriteLine("(nothing running)");
    else Console.WriteLine($"\n{running} running. `dodona stop-all` stops the daemons; add --lanes to take the agents down too.");
    return 0;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

/// <summary>Shim pids recorded for a workspace, from its own `shim-lane<N>.json` files —
/// never by process name (CLAUDE.md §4: killing by name once murdered the operator's live
/// session mid-trial).</summary>
static List<(long Lane, int Shim, int Child)> ShimPids(string dir)
{
    var list = new List<(long, int, int)>();
    try
    {
        foreach (var f in Directory.EnumerateFiles(dir, "shim-lane*.json"))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(f));
                var lane = long.TryParse(Path.GetFileNameWithoutExtension(f)["shim-lane".Length..], out var n) ? n : 0;
                int Pid(string k) => d.RootElement.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
                list.Add((lane, Pid("shimPid"), Pid("childPid")));
            }
            catch { /* half-written or stale: skip it rather than fail the listing */ }
        }
    }
    catch (DirectoryNotFoundException) { }
    return list;
}

/// <summary>
/// Stop everything Dodona is running, gracefully.
///
/// Daemons first, over their own control pipes, so each writes its own `daemon_stop` row —
/// a shutdown with no reason in the causal chain is the thing DEBUGGING.md calls a bug.
/// Lanes are left running by default, because that is what `stop-daemon` has always meant
/// and what the shim exists for: agents survive their daemon and are re-adopted. `--lanes`
/// takes them down too, resolved by recorded pid, which is the only safe way (§4).
/// </summary>
int StopAll()
{
    var live = Instance.LiveCtlPipes();
    List<Workspace> all;
    try { using var reg = new Registry(); all = reg.All(); }
    catch { all = new List<Workspace>(); }

    int stopped = 0;
    foreach (var w in all.Where(w => live.Contains(Instance.CtlPipe(w.Id), StringComparer.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"— stopping workspace {w.Name} ({w.Id})");
        Client(new { cmd = "stop-daemon" }, Instance.CtlPipe(w.Id));
        stopped++;
    }
    if (live.Contains(Instance.CtlPipe(Instance.ConciergeId), StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("— stopping the concierge");
        Client(new { cmd = "stop" }, Instance.CtlPipe(Instance.ConciergeId));
        stopped++;
    }

    var unregistered = live.Where(p => !all.Select(w => Instance.CtlPipe(w.Id))
        .Append(Instance.CtlPipe(Instance.ConciergeId)).Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
    foreach (var p in unregistered)
    {
        // Still stopped — it is running on this machine and the operator asked for quiet —
        // but named, so "I stopped something I could not identify" is never silent.
        Console.WriteLine($"— stopping unregistered instance on {p} (pre-workspace, or another DODONA_HOME)");
        Client(new { cmd = "stop-daemon" }, p);
        stopped++;
    }

    if (!opts.ContainsKey("lanes"))
    {
        var leftovers = all.Sum(w => ShimPids(Paths.WorkspaceDir(w.Id)).Count);
        Console.WriteLine(stopped == 0 ? "nothing was running" : $"stopped {stopped} daemon(s); lanes keep running");
        if (leftovers > 0)
            Console.WriteLine($"{leftovers} lane agent(s) are still up — they survive their daemon on purpose. " +
                              "`dodona stop-all --lanes` takes them down too.");
        return 0;
    }

    int agents = 0;
    foreach (var w in all)
        foreach (var (lane, shim, child) in ShimPids(Paths.WorkspaceDir(w.Id)))
            foreach (var pid in new[] { shim, child })
            {
                if (pid <= 0) continue;
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(pid);
                    // A recorded pid can be recycled by the OS onto something unrelated, and
                    // killing that would be exactly the machine-wide damage resolving-by-pid
                    // exists to avoid. Check what it actually is first.
                    var name = proc.ProcessName;
                    if (name is not ("DodonaShim" or "claude" or "node" or "DodonaFakeAgent"))
                    {
                        Console.WriteLine($"  skipped pid {pid} (lane {lane} of {w.Name}) — now '{name}', not ours");
                        continue;
                    }
                    proc.Kill(entireProcessTree: true);
                    agents++;
                }
                catch { /* already gone */ }
            }
    Console.WriteLine($"stopped {stopped} daemon(s) and {agents} agent process(es)");
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

    // ---- who gets the swap (WORKSPACES-CONCIERGE.md §7) --------------------------------
    // `--all` used to broadcast to every ctl pipe on the machine, which ORCHESTRATOR-REVIEW
    // already flagged as untestable: a suite exercising it would hot-swap the operator's live
    // instances. So targeting is now explicit and the suite can finally exist.
    //
    //   --workspace <name> ...   these workspaces (repeatable)
    //   --concierge              the concierge too (it has no workspace of its own)
    //   --all                    every live workspace daemon in the REGISTRY, plus the
    //                            concierge — still "everything", but everything Dodona
    //                            knows it owns, resolved by id rather than by scraping a
    //                            pipe name off the OS
    //   (none)                   the workspace owning --project, or the cwd
    //
    // Any lingering pipe that belongs to no registered workspace is left alone. That is the
    // whole difference: a stale or foreign `dodona-*-ctl` is no longer a swap target.
    var targets = new List<(string Label, string Pipe)>();
    var named = Many("workspace");
    if (opts.ContainsKey("all"))
    {
        using var reg = new Registry();
        foreach (var w in reg.All().Where(w => Instance.IsLive(w.Id)))
            targets.Add(($"{w.Name} ({w.Id})", Instance.CtlPipe(w.Id)));
        if (Instance.IsLive(Instance.ConciergeId)) targets.Add(("concierge", Instance.CtlPipe(Instance.ConciergeId)));
    }
    else if (named.Count > 0)
    {
        using var reg = new Registry();
        foreach (var n in named)
        {
            var w = WorkspaceResolve.ByNameOrId(reg, n);
            if (w is null) { Console.Error.WriteLine($"error: no workspace \"{n}\" to publish to"); return 2; }
            targets.Add(($"{w.Name} ({w.Id})", Instance.CtlPipe(w.Id)));
        }
        if (opts.ContainsKey("concierge") && Instance.IsLive(Instance.ConciergeId))
            targets.Add(("concierge", Instance.CtlPipe(Instance.ConciergeId)));
    }
    else
    {
        // The workspace owning what we just built. Resolved HERE and not earlier, so a
        // publish never migrates a store as a side effect of being run in a source tree
        // (found live: the tree's own pre-workspace daemon was holding it, and publish
        // refused before it had built anything).
        try { targets.Add(($"{WsName()} ({WsId()})", CtlPipe())); }
        catch (WorkspaceUnavailable ex)
        {
            Console.WriteLine($"published {newExe}");
            Console.Error.WriteLine($"note: nothing was swapped — {ex.Message}");
            Console.Error.WriteLine("      name targets explicitly:  --workspace <name> ... [--concierge]   or  --all");
            return 0;                                   // the build is real; only the swap did not happen
        }
    }

    int worst = 0;
    if (targets.Count == 0) Console.WriteLine($"published {newExe}; no daemon running to swap");
    foreach (var (label, target) in targets)
    {
        Console.WriteLine($"— swapping {label} on {target}");
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
        // Scoped the same way, and the SHELL's window is always a candidate: it is one
        // window over N workspaces, so it belongs to no workspace's pipe and would otherwise
        // never be refreshed at all (§6).
        var uiCandidates = new List<string>();
        if (opts.ContainsKey("all"))
        {
            using var reg = new Registry();
            foreach (var w in reg.All()) uiCandidates.Add(Instance.UiPipe(w.Id));
        }
        else
            foreach (var (_, pipe) in targets)
                uiCandidates.Add(pipe.Replace("-ctl", "-ui"));
        uiCandidates.Add(Instance.UiPipe(Instance.ShellId));

        var liveUi = Instance.LiveUiPipes();
        foreach (var target in uiCandidates.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(t => liveUi.Contains(t, StringComparer.OrdinalIgnoreCase)))
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
        "dump" => Client(new { verb = "dump" }, UiPipeName()),
        "screenshot" => Client(new { verb = "screenshot", @out = Path.GetFullPath(One("out") ?? "dodona-ui.png"), pane = One("pane") }, UiPipeName()),
        "pose" => pos.Count > 1 ? Client(new { verb = "pose", name = pos[1] }, UiPipeName()) : Fail("ui pose <name|live>"),
        "type" => pos.Count > 1 ? Client(new { verb = "type", text = string.Join(" ", pos.Skip(1)) }, UiPipeName()) : Fail("ui type <text>"),
        // Give a band the grid — the same code path a click takes, without needing focus.
        "workspace" => pos.Count > 1 ? Client(new { verb = "workspace", workspace = pos[1] }, UiPipeName()) : Fail("ui workspace <name|id>"),
        "overlay" => pos.Count > 1 ? Client(new { verb = "overlay", pane = pos[1] }, UiPipeName()) : Fail("ui overlay <PANE|off>"),
        "update" => pos.Count > 1 ? Client(new { verb = "update", exe = Path.GetFullPath(pos[1]) }, UiPipeName()) : Fail("ui update <DodonaUi.exe>"),
        "close" => Client(new { verb = "close" }, UiPipeName()),
        _ => Fail($"unknown ui verb: {pos[0]}"),
    };
}

// ---------------------------------------------------------------- client role

int Client(object request, string? pipeName = null)
{
    pipeName ??= CtlPipe();
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
    try { pipe.Connect(3000); }
    catch
    {
        pipe.Dispose();
        // Start-on-demand (§13): the registry is a store, never a service — the store is
        // always there and the daemon is summoned. This is also the recovery path from a
        // failed swap or a crash: the next command brings the daemon back, and the shims
        // have been buffering the whole time.
        var isWorkspaceCtl = wsCache is not null && pipeName == Instance.CtlPipe(wsCache.Id);
        if (!isWorkspaceCtl || cmd == "stop-daemon" || Environment.GetEnvironmentVariable("DODONA_NO_AUTOSTART") == "1")
            return Fail(
                isWorkspaceCtl ? $"daemon not running for workspace {wsCache!.Name} (ctl pipe {pipeName})"
                : pipeName == Instance.CtlPipe(Concierge.Id) ? $"concierge not running (ctl pipe {pipeName})"
                : pipeName.EndsWith("-ui") ? $"no Dodona UI on pipe {pipeName}"
                : $"nothing listening on pipe {pipeName}");

        var reborn = Autostart(WsId(), WsPrimary());
        if (reborn is not null) return Fail($"could not start a daemon for workspace {WsName()}: {reborn}");
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
    var boolFlags = new HashSet<string> { "json", "successor", "all", "adopt", "shortcut", "hi", "bulk", "shell", "concierge", "lanes" };

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
      dodona ps [--json]                    (EVERYTHING running on this machine, named)
      dodona stop-all [--lanes]             (stop the daemons; --lanes stops the agents too)
              a daemon deliberately outlives its window, so "I closed the app" does NOT
              mean nothing is running — `ps` is how you find out
    the concierge (one per machine; answers only "which workspace"):
      dodona concierge                      (run it; any concierge-* command starts one)
      dodona concierge-status               (tiers, the search fence, open questions)
      dodona concierge-resolve <text>       (walk the ladder, print the verdict as JSON)
      dodona route <text>                   (resolve, WAKE that workspace, deliver to it)
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
      dodona publish [--project <dir>] [--exe <prebuilt>] [--mode now] [--shortcut]
              [--workspace <name>]...  swap exactly these  [--concierge]  and the concierge
              [--all]                  every LIVE registered workspace, plus the concierge
              (neither)                the workspace that owns --project / the cwd
              --shortcut puts Dodona on the desktop; later publishes keep it current
              swaps daemons, then refreshes live UIs including the shell (separate processes)
      dodona swap <new dodona.exe> [--mode now] | swap-answer <now|when-it-lands|hold>
      dodona swaps
    lanes & the brain (§3):
      dodona lane-rename <lane> <TITLE> | lane-respawn <lane> | lane-stop <lane>
      dodona lane-collapse <lane> | lane-expand <lane>
              the grid GROWS with the work; you collapse what you are not dealing with
      dodona brain-start [--hi]             (warm the dispatcher brain; hi = expensive tier)
    ui (§8/§17 — talks to the DodonaUi process, not the daemon):
      dodona ui type <text>                 (submit through the same path as Enter — no focus)
      DodonaUi.exe --test-window            (off-screen, never activates: for tests/agents)
      dodona ui dump | ui screenshot [--pane <PANE>] --out <png> | ui pose <name|live>
      dodona ui workspace <name|id>         (give a band the grid — the same path a click takes)
      DodonaUi.exe --shell                  (one window over every awake workspace; boots to zero)
      add --shell to any ui verb to address that window instead of one workspace's
      dodona ui overlay <PANE|off> | ui update <DodonaUi.exe> | ui close
      dodona ack <pane_event_id> | undo-route <routing_decision_id>
      dodona stop-daemon
    Every command takes --workspace <name|id|alias>, or --root <path> (default: cwd) to
    address the workspace that owns that path — an unowned path gets a workspace made for
    it, named after the folder, with that folder as its sole member.
    """);
