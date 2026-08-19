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

/// <summary>Run the daemon, and make sure a daemon that never came up leaves EVIDENCE.
/// A daemon is spawned detached and hidden (by the UI, by Ensure, by a predecessor's
/// handoff), so one that dies before answering its pipe used to vanish without a trace —
/// fourteen broken auto-published builds did exactly that on 2026-08-18, and the only
/// symptom was windows freezing against a pipe nobody was ever going to serve. Every
/// startup failure now lands in one place a person (or an agent) can read after the fact.</summary>
async Task<int> RunDaemonLogged()
{
    try
    {
        var code = await Daemon.RunAsync(WsPrimary(), WsId(), WsName(), CtlPipe(), opts.ContainsKey("successor"));
        if (code != 0) StartupLog($"daemon exited {code}");
        return code;
    }
    catch (WorkspaceUnavailable ex) { StartupLog($"daemon refused: {ex.Message}"); throw; }
    catch (Exception ex) { StartupLog($"daemon crashed: {ex}"); throw; }
}

void StartupLog(string msg)
{
    try
    {
        var dir = Path.Combine(Paths.Home, "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "daemon-start.log"),
            $"{DateTime.UtcNow:o} pid={Environment.ProcessId} build={Ver.Build} exe={Ver.ExePath} args=[{string.Join(" ", args)}] {msg}{Environment.NewLine}");
    }
    catch { /* the log must never be a second way to die */ }
}

async Task<int> Dispatch() => cmd switch
{
    "version" => Version(),
    "daemon" => await RunDaemonLogged(),
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
    "gate-hook" => GateHook(),
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
        // A build output is not an installation (Ver.IsSourceTreeBuildOutput). Checked
        // BEFORE the "starting one" notice, so we never announce a start we will not do.
        var exe = Environment.ProcessPath ?? "dodona.exe";
        if (Ver.IsSourceTreeBuildOutput(exe)) return Fail(Ver.BuildOutputRefusal(exe, "the concierge"));
        Console.Error.WriteLine("no concierge running — starting one");
        try
        {
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

    var stale = 0;
    Console.WriteLine("WHAT  NAME                 DAEMON  WINDOW  LANES  WHERE");
    foreach (var w in all)
    {
        var isLive = live.Contains(Instance.CtlPipe(w.Id), StringComparer.OrdinalIgnoreCase);
        var hasUi = ui.Contains(Instance.UiPipe(w.Id), StringComparer.OrdinalIgnoreCase);
        var reaped = ReapShimInfo(Paths.WorkspaceDir(w.Id));
        if (reaped > 0) stale += reaped;
        var shims = LiveShimPids(Paths.WorkspaceDir(w.Id)).Count;
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
    else
    {
        Console.WriteLine($"\n{running} running. `dodona stop-all` stops the daemons; add --lanes to take the agents down too.");
        // LANES is live processes now, not files. Say when leftovers were cleared, so the
        // number changing between two runs is never a mystery.
        if (stale > 0)
            Console.WriteLine($"cleared {stale} stale shim record(s) for agents that had already exited.");
    }
    return 0;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

/// <summary>Shim pids recorded for a workspace, from its own `shim-lane<N>.json` files —
/// never by process name (CLAUDE.md §4: killing by name once murdered the operator's live
/// session mid-trial).</summary>
/// <summary>Is this pid a LIVE process of the expected kind? The name check is not
/// decoration: pids are reused, and a recycled pid would otherwise resurrect a lane that
/// died days ago.</summary>
static bool PidAlive(int pid, string expectNamePrefix)
{
    if (pid <= 0) return false;
    try
    {
        using var p = System.Diagnostics.Process.GetProcessById(pid);
        return !p.HasExited &&
               p.ProcessName.StartsWith(expectNamePrefix, StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }          // no such process, or it exited between the two calls
}

/// <summary>The shims that are actually RUNNING — which is the only number `ps` was ever
/// supposed to print.
///
/// Found 2026-08-18, by the operator: `dodona ps` said "24 lanes" and `stop-all` said "24
/// lane agent(s) are still up" while exactly SIX processes existed, of which one was doing
/// their work and five were Dodona's own machinery. The count came from
/// <see cref="ShimPids"/>, which reads `shim-lane*.json` FILES and never asks whether the
/// pid in them is alive; eighteen were leftovers from lanes that had already died. That is
/// not a cosmetic bug: the number appears in the sentence offering `stop-all --lanes`, so it
/// pushes someone toward killing real work to clean up files.</summary>
static List<(long Lane, int Shim, int Child)> LiveShimPids(string dir) =>
    ShimPids(dir).Where(t => PidAlive(t.Shim, "DodonaShim")).ToList();

/// <summary>Delete shim-info files whose shim is gone. Dodona's own bookkeeping, never repo
/// content (CLAUDE.md §5), so self-healing on read is right: the standing directive is that
/// nothing is allowed to go quietly stale, and eighteen dead files had accumulated
/// unnoticed. Announced by the caller, never silent.</summary>
static int ReapShimInfo(string dir)
{
    var reaped = 0;
    foreach (var (lane, shim, _) in ShimPids(dir))
    {
        if (PidAlive(shim, "DodonaShim")) continue;
        try { File.Delete(Path.Combine(dir, $"shim-lane{lane}.json")); reaped++; }
        catch { /* in use or already gone: a failed reap must never fail the listing */ }
    }
    return reaped;
}

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

    // ---- daemons this registry does not own are NAMED, not stopped (invariant I6) --------
    // This used to stop them, with a comment admitting they might belong to "another
    // DODONA_HOME". That is not a footnote, it is the whole problem: DODONA_HOME scopes the
    // REGISTRY but not the OS pipe namespace, so every suite's isolated daemons are visible
    // here and were being swept up by a command whose blast radius read as "my stuff".
    //
    // Measured, on this session: a proof script called `stop-all` during cleanup while an
    // acceptance run was going in another shell. It killed that run's daemons; the suite then
    // waited on a pipe that would never answer and sat there for twenty minutes with an empty
    // log. `publish --all` was narrowed to registry scope for exactly this reason and
    // tests/publish-acceptance.ps1 proves it; stop-all was the last machine-wide sweep left.
    //
    // So the default is now the same promise publish makes: a live daemon belonging to no
    // registered workspace is never a target. They are still LISTED, because a cleanup that
    // hides what it can see is its own trap, and `--orphans` stops them when that is genuinely
    // what you want. Naming the flag in the output keeps it a choice you make rather than a
    // wall you hit (CLAUDE.md 0.1).
    var unregistered = live.Where(p => !all.Select(w => Instance.CtlPipe(w.Id))
        .Append(Instance.CtlPipe(Instance.ConciergeId)).Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
    if (unregistered.Count > 0)
    {
        if (opts.ContainsKey("orphans"))
            foreach (var p in unregistered)
            {
                Console.WriteLine($"— stopping unregistered instance on {p} (--orphans)");
                Client(new { cmd = "stop-daemon" }, p);
                stopped++;
            }
        else
        {
            Console.WriteLine($"{unregistered.Count} live daemon(s) belong to no workspace in this registry and were LEFT ALONE:");
            foreach (var p in unregistered) Console.WriteLine($"    {p}");
            Console.WriteLine("  They may be another DODONA_HOME's (a test run, or another agent's session).");
            Console.WriteLine("  `dodona stop-all --orphans` stops them too, if you are sure they are yours.");
        }
    }

    if (!opts.ContainsKey("lanes"))
    {
        // LIVE shims, not shim FILES: this number is printed next to the offer of
        // `--lanes`, so over-counting it argues for killing work that does not exist.
        var leftovers = all.Sum(w => LiveShimPids(Paths.WorkspaceDir(w.Id)).Count);
        Console.WriteLine(stopped == 0 ? "nothing was running" : $"stopped {stopped} daemon(s); lanes keep running");
        if (leftovers > 0)
            Console.WriteLine($"{leftovers} lane agent(s) are still up — they survive their daemon on purpose. " +
                              "`dodona stop-all --lanes` takes them down too.");
        return 0;
    }

    int agents = 0;
    foreach (var w in all)
        foreach (var (lane, shim, child) in LiveShimPids(Paths.WorkspaceDir(w.Id)))
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
    // `commit` is the field that makes this bisectable (P2.6): it names a commit `git log`
    // knows, where `build` is only an image stamp that maps to nothing outside this machine.
    // Both are reported -- they answer different questions (see Ver's class comment).
    if (opts.ContainsKey("json"))
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            build = Ver.Build, schema = Ver.Schema, shimProtocol = Ver.ShimProtocol, exe = Ver.ExePath,
            commit = Ver.Commit, branch = Ver.Branch, mainBaseline = Ver.MainBaseline,
            trial = Ver.IsTrial, dirty = Ver.Dirty, provenance = Ver.Provenance,
        }));
    else
    {
        Console.WriteLine($"dodona build {Ver.Build}");
        Console.WriteLine($"  {Ver.ProvenanceLine}");
        Console.WriteLine($"  store schema   v{Ver.Schema}");
        Console.WriteLine($"  shim protocol  v{Ver.ShimProtocol}");
        Console.WriteLine($"  exe            {Ver.ExePath}");
        Console.WriteLine($"  published to   {Ver.BinRoot}");
    }
    return 0;
}

/// <summary>
/// The claim gate itself (design §6 layer 1), as a COMPILED subcommand rather than a generated
/// PowerShell script.
///
/// WHY THIS IS NOT A .ps1 ANY MORE. A script that fails to parse runs NOTHING: it exits with a
/// parse error and denies nothing, while still being registered and still sitting on disk
/// looking installed. That is not hypothetical -- it happened during Phase 2 to a different
/// hook, where one line of message text ended in a backtick (PowerShell's escape character),
/// which swallowed the terminator of a here-string and killed the whole file. The same mistake
/// in C# is a BUILD failure: loud, immediate, and impossible to ship.
///
/// It is also one process instead of two. The script's entire job was to read stdin, shell out
/// to `dodona claim-check`, and format the refusal -- so every gated edit started PowerShell
/// (~136 ms measured) purely to start dodona.exe. And the suites can now call this directly and
/// assert its answer, which a generated script made awkward.
///
/// FAILS OPEN, deliberately and unchanged (§6): if anything here cannot reach a verdict, the
/// write is allowed and the reason is appended to the bypass log. The merge-time diff backstop
/// catches what slips through, and a broken gate must never brick a lane.
/// </summary>
/// <summary>
/// Record that the gate ALLOWED a write without having actually checked it, and hand back 0.
///
/// EVERY fail-open goes through here, which is the whole point (P4 follow-up, operator's
/// decision 2026-08-19). The gate used to have FOUR silent `return 0`s -- no ticket, unreadable
/// stdin, unparseable stdin, no file_path -- and one logged path. A silent allow is
/// indistinguishable from an allow the claim algebra actually approved, and that is not a
/// theoretical complaint: `m1 gate_denies_outside_claim` went red under load with EMPTY output
/// and an EMPTY bypass log, and three separate hypotheses about the cause were all wrong
/// because there was nothing recorded to read. Whatever happens, something is written down now.
///
/// STILL FAIL-OPEN, deliberately, and that is the operator's call rather than an oversight.
/// Failing closed would block every write in every ticket lane whenever the daemon is briefly
/// unreachable, and a lane has no channel to ask a human for permission (CLAUDE.md section 7) --
/// it would strand mid-task. Layer 2, the merge-time diff backstop, is what refuses anything
/// that slipped through, and it reads the very log this writes.
///
/// TWO DESTINATIONS, because each covers the other's blind spot:
///   * the bypass log in the worktree -- durable, and where the merge backstop already looks;
///   * stderr -- always available even when --worktree was not passed, and it is what makes the
///     event visible to a test and to anyone reading a lane's output. Claude Code takes the
///     hook's DECISION from stdout, so stderr cannot change the verdict.
/// </summary>
int GateAllowedUnchecked(string why, long ticket, string? path)
{
    var line = $"{DateTime.Now:o} gate fail-open: {why}" +
               (ticket > 0 ? $" [ticket {ticket}]" : "") +
               (string.IsNullOrEmpty(path) ? "" : $" path={path}");
    try { Console.Error.WriteLine("dodona gate: " + line); } catch { }
    try
    {
        var wt = One("worktree");
        if (wt is { Length: > 0 } && Directory.Exists(wt))
            File.AppendAllText(Path.Combine(wt, ".dodona-bypass.log"), line + Environment.NewLine);
    }
    catch { /* a log we cannot write must still not block the write */ }
    return 0;
}

int GateHook()
{
    // No ticket means no gate was ever deployed for this lane, so there is nothing to check and
    // nothing to report -- a plain `lane-start` runs ungated by design (CLAUDE.md section 7).
    // This is the one allow that is genuinely not a bypass.
    var ticketArg = One("ticket");
    if (string.IsNullOrEmpty(ticketArg)) return 0;
    if (!long.TryParse(ticketArg, out var ticket))
        return GateAllowedUnchecked($"--ticket '{ticketArg}' is not a number (gate misconfigured)", 0, null);

    string input;
    try { input = Console.In.ReadToEnd(); }
    catch (Exception ex) { return GateAllowedUnchecked($"stdin unreadable: {ex.GetType().Name}", ticket, null); }

    // The tool name is only for the diagnostic, so it is read defensively and never gates.
    string tool = "";
    string? path = null;
    try
    {
        using var doc = JsonDocument.Parse(input);
        if (doc.RootElement.TryGetProperty("tool_name", out var tn)) tool = tn.GetString() ?? "";
        if (doc.RootElement.TryGetProperty("tool_input", out var ti) &&
            ti.TryGetProperty("file_path", out var fp)) path = fp.GetString();
    }
    catch (Exception ex)
    {
        // The measured mystery lands here or in the branch below: under a heavy parallel run the
        // hook produced no verdict and left no trace, and empty-or-truncated stdin is the only
        // remaining explanation (the daemon path logs, and PowerShell was proved to deliver
        // stdin 60/60 under load). The BYTE COUNT and a prefix are recorded so the next
        // occurrence answers the question instead of raising it again.
        var head = input.Length > 120 ? input.Substring(0, 120) + "..." : input;
        return GateAllowedUnchecked(
            $"stdin unparseable as JSON ({input.Length} bytes, {ex.GetType().Name}): {head.Replace("\n", " ").Replace("\r", "")}",
            ticket, null);
    }

    // NotebookEdit carries `notebook_path`, not `file_path`, so it lands here and is allowed --
    // a real hole in layer 1, and now a visible one rather than a silent one.
    if (string.IsNullOrEmpty(path))
        return GateAllowedUnchecked($"no file_path in tool_input (tool='{tool}', {input.Length} bytes)", ticket, null);

    // Client() prints the daemon's reply to stdout, and stdout here IS the hook's verdict --
    // Claude Code parses it as JSON. So it is silenced for the duration, which is exactly what
    // the old script's `> $null 2> $null` was doing.
    int code;
    var saved = Console.Out;
    try
    {
        Console.SetOut(TextWriter.Null);
        code = Client(new { cmd = "claim-check", ticket, path });
    }
    catch (Exception ex) { code = 2; try { Console.Error.WriteLine($"dodona gate: claim-check threw {ex.GetType().Name}"); } catch { } }
    finally { Console.SetOut(saved); }

    if (code == 0) return 0;                                         // covered by the claim
    if (code == 1)
    {
        var ws = One("workspace") ?? "";
        var reason = $"outside ticket {ticket}'s claim: {path}. Stay within claimed paths, or request " +
                     $"an extension: dodona claim-extend {ticket} --claim <spec>" +
                     (ws.Length > 0 ? $" --workspace '{ws}'" : "");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = "deny",
                permissionDecisionReason = reason,
            }
        }));
        return 0;                                                    // the DECISION is the output, not the exit code
    }

    // Anything else: no daemon, a pipe error, a reply we do not understand.
    return GateAllowedUnchecked($"claim-check could not answer (exit {code})", ticket, path);
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
    var repo = Path.GetFullPath(One("project") ?? root);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    string outDir;

    var prebuilt = One("exe");

    // ---- WHAT gets built (RECOVERY-PHASES P2.5, decision D-1) --------------------------
    // `--from <ref|worktree>` is the ONLY way to publish something that is not main, and it
    // is deliberate: automatic publishing follows main only, so a session's uncommitted or
    // half-finished edit can never reach the operator's app on its own. A trial is stamped
    // with non-main provenance, `status` says so out loud, and the next commit to main
    // replaces it.
    //
    // A ref gets a DETACHED WORKTREE of its own, never the live tree: publish must not care
    // what an operator or another session has checked out, and building the live tree is how
    // a publish picks up work that was never meant to ship.
    var from = One("from");
    var fromIsRef = from is not null && !Directory.Exists(from);
    using var fromWt = fromIsRef ? Git.TempWorktree.For(repo, from!, stamp) : Git.TempWorktree.None(repo);
    if (fromIsRef && fromWt.Path is null)
        return Fail($"--from {from}: could not check it out ({fromWt.Error.Trim()}) -- is it a ref or a directory?");

    var project = from is null ? repo : fromIsRef ? fromWt.Path! : Path.GetFullPath(from);

    // ---- provenance: the COMMIT this build is made from -------------------------------
    // Replaces the source-mtime snapshot that used to be taken here. A commit is exact and
    // atomic, where "newest source file" needed a debounce, a stamp file and a persisted
    // guard to behave, and looped 64 times in one afternoon anyway (Ver's class comment
    // carries the numbers). Resolved from the tree being BUILT, so a --from trial reports the
    // trial's commit and a main publish reports main's.
    //
    // "Is this main?" is answered by comparing HEAD to the main ref rather than by a flag --
    // which is what makes the drift watcher's detached worktree of main's SHA come out as a
    // MAIN build rather than a trial, with no special case anywhere: a linked worktree shares
    // the ref store, so `rev-parse main` means the same thing inside it.
    var mainBranch = Config.Load(project).Main;
    // ShaOrEmpty, not Sha: `project` is not guaranteed to be a git repository -- `publish
    // --exe` on a plain folder is a legitimate call, and it does not need provenance at all.
    // Sha THREW here, so that call died with an unhandled InvalidOperationException six lines
    // above the `haveProvenance` test written to handle exactly this.
    var head = Git.ShaOrEmpty(project, "HEAD");
    var mainSha = Git.ShaOrEmpty(project, mainBranch);
    var (bc, bout) = Git.Run(project, "rev-parse", "--abbrev-ref", "HEAD");
    var branch = bc == 0 ? bout.Trim() : "";
    if (branch is "HEAD" or "") branch = head == mainSha && mainSha.Length > 0 ? mainBranch : "detached";
    var (sc, sout) = Git.Run(project, "status", "--porcelain");
    var dirty = sc == 0 && sout.Trim().Length > 0;
    var haveProvenance = head.Length == 40;

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
            // The commit goes INTO the assembly, as FIELDS WE NAMED (Directory.Build.props),
            // so the binary can always answer what it was built from with no side file to lose
            // and no shared string to parse. One plain value per property: the dotnet CLI
            // splits a -p: value on commas, which silently ate a combined stamp on the first
            // attempt. Deliberately not passed when git could not answer -- an unknown
            // provenance must stay unknown rather than become a plausible lie.
            if (haveProvenance)
            {
                psi.ArgumentList.Add($"-p:DodonaCommit={head}");
                psi.ArgumentList.Add($"-p:DodonaMainSha={(mainSha.Length == 40 ? mainSha : head)}");
                psi.ArgumentList.Add($"-p:DodonaDirty={(dirty ? "1" : "0")}");
                // Branch is cosmetic -- "is this main?" is decided by comparing the two SHAs, so
                // a branch name mangled by the CLI's comma handling can never change a decision.
                if (!branch.Contains(',')) psi.ArgumentList.Add($"-p:DodonaBranch={branch}");
            }
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) return Fail($"build failed ({Path.GetFileName(proj)}) — nothing was published, nothing swapped");
        }
    }

    var newExe = Path.Combine(outDir, "dodona.exe");
    if (!File.Exists(newExe)) return Fail($"published, but {newExe} is missing");

    if (prebuilt is null && haveProvenance)
        Console.WriteLine(head == mainSha
            ? $"provenance: {mainBranch}@{Ver.Short(head)}{(dirty ? " +uncommitted-changes" : "")}"
            : $"provenance: TRIAL {branch}@{Ver.Short(head)}{(dirty ? " +uncommitted-changes" : "")} " +
              $"-- the next commit to {mainBranch} ({Ver.Short(mainSha)}) replaces it");
    else if (prebuilt is null)
        Console.WriteLine($"provenance: NONE -- {project} is not a git repository, so this build cannot say what it came from");

    // NO STAMP FILE ANY MORE (P2.4 is a deletion). The provenance is compiled into the
    // assembly above, so there is nothing to write beside the binaries and nothing that can
    // go missing. `--exe <prebuilt>` compiled nothing, so it carries whatever the prebuilt
    // binary already said -- which for a `dev build` image is nothing at all, reported as
    // "build=unknown" rather than guessed at.

    // Verify the build actually RUNS before anything is promoted to it. The shortcut
    // used to be repointed right here, before any swap was attempted — so a build that
    // compiled but could not start became the front door (found live 2026-08-18:
    // fourteen consecutive broken auto-publishes each repointed the shortcut at a
    // binary whose daemon died on startup; every project open froze against it for the
    // rest of the morning). Now: probe first, and move the shortcut only at the end,
    // once a daemon has accepted the build — or, with nothing running, on this probe.
    try
    {
        var vpsi = new System.Diagnostics.ProcessStartInfo(newExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        vpsi.ArgumentList.Add("version");
        vpsi.ArgumentList.Add("--json");
        using var vp = System.Diagnostics.Process.Start(vpsi)!;
        var vout = vp.StandardOutput.ReadToEnd();
        vp.WaitForExit(10000);
        if (vp.ExitCode != 0 || !vout.Contains("\"schema\""))
            return Fail($"built, but {newExe} does not answer `version --json` — nothing promoted, nothing swapped");
    }
    catch (Exception ex) { return Fail($"built, but {newExe} would not start ({ex.Message}) — nothing promoted, nothing swapped"); }

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
            Shortcut(outDir);                           // probe-verified above; nothing running to object
            Console.WriteLine($"published {newExe}");
            Console.Error.WriteLine($"note: nothing was swapped — {ex.Message}");
            Console.Error.WriteLine("      name targets explicitly:  --workspace <name> ... [--concierge]   or  --all");
            return 0;                                   // the build is real; only the swap did not happen
        }
    }

    int worst = 0;
    var accepted = targets.Count == 0;      // probe-verified, and nothing running to object
    if (targets.Count == 0) Console.WriteLine($"published {newExe}; no daemon running to swap");
    foreach (var (label, target) in targets)
    {
        Console.WriteLine($"— swapping {label} on {target}");
        var code = Client(new { cmd = "swap", exe = newExe, mode = One("mode") ?? "ask" }, target);
        if (code == 0) accepted = true;     // handed off, or armed to land — a daemon vouched for it
        worst = Math.Max(worst, code);
    }

    // The front door moves LAST: only onto a build a daemon accepted, or that the probe
    // verified when nothing was running to ask. A publish where every swap failed leaves
    // the shortcut on the last build that provably runs.
    if (accepted) Shortcut(outDir);
    else Console.Error.WriteLine("note: desktop shortcut NOT repointed — no daemon accepted this build; the door still opens the last good one");

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
        // --shell is the front door (WORKSPACES-CONCIERGE.md §4): one window over every
        // awake workspace, boot-to-zero when nothing is. The shortcut used to launch the
        // bare exe, whose no-argument path is the folder PICKER — the pre-workspace way
        // in, folder-first in a workspace-first design. The operator noticed before we
        // did (2026-08-18). Arguments are re-stamped on every publish, so an existing
        // argument-less shortcut heals itself on the next publish.
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnk}');" +
                 $"$s.TargetPath='{target}';$s.Arguments='--shell';$s.WorkingDirectory='{outDir}';" +
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
    if (pos.Count == 0) return Fail("ui verb required: dump | screenshot | pose <name> | overlay <PANE|off> | " +
                                    "type <text> | compose <text> | key <enter|shift+enter> | input-resize <dy|reset> | " +
                                    "workspace <name> | update <exe> | close");
    return pos[0] switch
    {
        "dump" => Client(new { verb = "dump" }, UiPipeName()),
        "screenshot" => Client(new { verb = "screenshot", @out = Path.GetFullPath(One("out") ?? "dodona-ui.png"), pane = One("pane") }, UiPipeName()),
        "pose" => pos.Count > 1 ? Client(new { verb = "pose", name = pos[1] }, UiPipeName()) : Fail("ui pose <name|live>"),
        "type" => pos.Count > 1 ? Client(new { verb = "type", text = string.Join(" ", pos.Skip(1)) }, UiPipeName()) : Fail("ui type <text>"),
        // The multiline box, driven without focus: `compose` types characters and does NOT
        // send, `key` presses the one key that means two things. Together they are how a test
        // (or an agent) writes a two-line prompt the way a person does — `type` alone always
        // submits, so it could never leave two lines sitting in the box.
        "compose" => pos.Count > 1 ? Client(new { verb = "compose", text = string.Join(" ", pos.Skip(1)) }, UiPipeName()) : Fail("ui compose <text>"),
        "key" => pos.Count > 1 ? Client(new { verb = "key", key = pos[1] }, UiPipeName()) : Fail("ui key <enter|shift+enter>"),
        "input-resize" => pos.Count > 1 ? UiResize(pos[1]) : Fail("ui input-resize <dy|reset>"),
        // Give a band the grid — the same code path a click takes, without needing focus.
        "workspace" => pos.Count > 1 ? Client(new { verb = "workspace", workspace = pos[1] }, UiPipeName()) : Fail("ui workspace <name|id>"),
        "overlay" => pos.Count > 1 ? Client(new { verb = "overlay", pane = pos[1] }, UiPipeName()) : Fail("ui overlay <PANE|off>"),
        "update" => pos.Count > 1 ? Client(new { verb = "update", exe = Path.GetFullPath(pos[1]) }, UiPipeName()) : Fail("ui update <DodonaUi.exe>"),
        "close" => Client(new { verb = "close" }, UiPipeName()),
        _ => Fail($"unknown ui verb: {pos[0]}"),
    };
}

// The grip, without a mouse: `dy` pixels taller (negative shorter), `reset` hands the box
// back to fitting its own text — the same ResizeInput a drag and a double-click call.
int UiResize(string arg)
{
    if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase)) return Client(new { verb = "input-resize", reset = true }, UiPipeName());
    if (!double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy))
        return Fail("ui input-resize <dy|reset>");
    return Client(new { verb = "input-resize", dy }, UiPipeName());
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
    var exe = Environment.ProcessPath ?? "dodona.exe";
    // The refusal, before the notice: a build output is not an installation, and a daemon
    // autostarted from one is the invisible holder of the compiler's own output file
    // (Ver.IsSourceTreeBuildOutput carries the incident and the paths deliberately allowed).
    // `dodona daemon` run EXPLICITLY still works from any path — it is autostart that
    // refuses, because autostart is the one nobody sees happen.
    if (Ver.IsSourceTreeBuildOutput(exe)) return Ver.BuildOutputRefusal(exe, "a daemon");
    try
    {
        Console.Error.WriteLine("no daemon for this workspace — starting one");
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
      dodona gate-hook --ticket <n> [--workspace <id>] [--worktree <dir>]
              the claim gate, run BY CLAUDE CODE and not by hand: reads a PreToolUse
              payload on stdin and answers deny/allow. Deployed by DeployGate.
      dodona stop-all [--lanes] [--orphans]
              --orphans also stops live daemons this registry does not own (another
              DODONA_HOME's test run, say). Without it they are listed and left alone.
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
      the box is multiline: Enter sends, Shift+Enter is a new line, and the grip drags it taller
      dodona ui compose <text>              (type WITHOUT sending — characters, no Enter)
      dodona ui key <enter|shift+enter>     (the keystroke itself, through the real handler)
      dodona ui input-resize <dy|reset>     (the resize grip: +px taller, reset = fit the text)
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
