using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

// DodonaShim — owns one claude process's stdio and exposes it over a named pipe
// (design doc §13). Daemons come and go; the shim and its child do not.
//
//   - Spawns the child with redirected stdio. No job objects, no inherited
//     handles tying anyone's lifetime to anyone else's: the shim outlives the
//     process that launched it, and the child outlives a dead daemon.
//   - Buffers every child stdout line, in order, forever (the daemon persists
//     them; the shim's buffer only needs to cover daemon downtime).
//   - Serves one client at a time on the pipe. On (re)connect it greets with
//     `!hello`, then replays every line not yet successfully written to a
//     client. Delivery is at-least-once; each line is prefixed `seq<TAB>` so
//     the client dedupes. Zero loss is the invariant; duplicates are cheap.
//   - Client → shim lines are forwarded verbatim to child stdin, except
//     `##shutdown`, which kills the child tree and exits the shim.
//   - IT DIES ON ITS OWN, three ways, because a wrapper is not allowed to outlive its
//     reason (RECOVERY-PHASES Phase 3):
//       1. `##shutdown` from a client - kills the child tree and exits.
//       2. THE CHILD EXITED and the buffer is fully delivered - nothing left to wrap and
//          nothing left to hand over, so there is no reason to be running. Before this the
//          flag was computed and then never read, and a shim whose agent had died ran
//          forever, still answering `!hello`, and was re-adopted and routed to.
//       3. THE LEASE expired - no client has been connected for DODONA_SHIM_LEASE_SEC.
//          That closes the orphan class even when the daemon is killed with -Force, which
//          (2) alone cannot: a live child with nobody to deliver to has no reason either.
//     Measured 2026-08-18 on the operator's machine: eleven live lane pipes, ten of them
//     agents nobody could reach, three running out of the compiler's own output directory -
//     which is how they blocked every build invisibly for eighteen hours.
//
// Usage: DodonaShim <pipeName> <childExe> [childArgs...]
// Env:   DODONA_SHIM_INFO=<path> — writes {shimPid, childPid, pipeName} JSON.
//        DODONA_SHIM_LEASE_SEC=<n> - the lease above. Default 1800 (30 minutes): long
//        enough that a hot swap, a crashed daemon and a deliberate `stop-all` (which
//        leaves lanes running ON PURPOSE) all still get their agents back, short enough
//        that "forever" is not a duration this process knows. The lane ROW survives
//        either way and `lane-respawn` resumes the session, so the loss is bounded and
//        recoverable; an immortal process is neither.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DodonaShim <pipeName> <childExe> [childArgs...]");
    return 2;
}
string pipeName = args[0];

var psi = new ProcessStartInfo(args[1])
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    WorkingDirectory = Environment.CurrentDirectory,
    // Claude speaks UTF-8; .NET's default for redirected stdio is the OEM codepage, so
    // without these every em dash the model types is stored as mojibake ("ΓÇö").
    // Found by dogfooding: the first real lane's transcript came back garbled.
    StandardOutputEncoding = System.Text.Encoding.UTF8,
    StandardErrorEncoding = System.Text.Encoding.UTF8,
    StandardInputEncoding = new System.Text.UTF8Encoding(false),   // no BOM into the child
};
for (int i = 2; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
var child = Process.Start(psi)!;

var infoPath = Environment.GetEnvironmentVariable("DODONA_SHIM_INFO");
if (infoPath is not null)
    File.WriteAllText(infoPath, JsonSerializer.Serialize(
        new { shimPid = Environment.ProcessId, childPid = child.Id, pipeName }));

var buffer = new List<string>();      // every child stdout line; seq == index
long delivered = 0;                   // advanced only after a successful pipe write
var gate = new object();
var newLine = new SemaphoreSlim(0);
bool childExited = false;
var shutdown = new CancellationTokenSource();

// Last moment a client was connected. A shim exists to hand its child's output to a daemon;
// with no daemon ever coming back it is holding a conversation with nobody (P3.3).
var lastContact = DateTime.UtcNow;
var clientsHere = 0;              // Interlocked: >0 means contact is happening NOW

// EVERY EXIT SAYS WHY, IN A FILE. A process that vanishes without a reason is the other half of
// the bug this phase is about -- the only evidence of the eleven orphans on 2026-08-18 was a
// pipe listing somebody happened to run.
//
// stderr is NOT enough, and finding that out cost a debugging round: a shim inherits the
// daemon's stderr handle, so the moment the daemon exits those writes go nowhere. Every
// interesting exit happens exactly then -- after the daemon is gone -- so the one reason worth
// having was the one reason that could never be captured. The log lives beside the shim-info
// record, in Dodona's own state directory (CLAUDE.md 5), and is appended to, never rewritten:
// one short line per shim, which is what makes a run of orphans readable after the fact.
var exitLog = infoPath is null ? null : Path.Combine(Path.GetDirectoryName(infoPath)!, "shim-exits.log");
var exitReason = "unknown";
void Note(string what)
{
    var line = $"{DateTime.Now:HH:mm:ss} {pipeName} shim={Environment.ProcessId} child={child.Id} {what}";
    // BOTH writes are guarded. stderr is an inherited handle whose owner is usually already
    // dead, and a diagnostic that can throw is worse than no diagnostic -- it would take the
    // process down on the one path that exists to explain why the process went down.
    try { Console.Error.WriteLine($"DodonaShim: {line}"); } catch { }
    if (exitLog is null) return;
    // Every shim in a workspace appends to one file, and they exit together when their daemon
    // does -- so a plain single attempt loses exactly the lines that matter most, the burst.
    // Three tries, then give up silently: this is a diagnostic, and a diagnostic that delays a
    // process's exit is a diagnostic that changed the thing it was measuring.
    for (int i = 0; i < 3; i++)
    {
        try { File.AppendAllText(exitLog, line + Environment.NewLine); return; }
        catch { Thread.Sleep(20); }
    }
}
void Finish(string why)
{
    if (shutdown.IsCancellationRequested) return;
    exitReason = why;
    Note($"exiting -- {why}");
    shutdown.Cancel();
}

// DRAINED, not merely dead. The child's last turn is usually its most important one and it
// lives in this buffer until a client has actually taken it -- `delivered` only advances after
// a successful pipe write, which is the m0 guarantee (kill the daemon mid-turn; the result
// still lands). Exiting on child death alone would throw away exactly that.
void FinishIfDrained()
{
    bool done; int n;
    lock (gate) { done = childExited && delivered >= buffer.Count; n = buffer.Count; }
    if (done) Finish($"child {child.Id} exited and all {n} buffered line(s) were delivered");
}

// Drain stderr so the child can never block on a full pipe.
_ = Task.Run(async () =>
{
    while (await child.StandardError.ReadLineAsync() is not null) { }
});

// Child stdout → buffer.
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await child.StandardOutput.ReadLineAsync()) is not null)
    {
        lock (gate) buffer.Add(line);
        newLine.Release();
    }
    lock (gate) childExited = true;
    newLine.Release();
    FinishIfDrained();          // P3.2: nothing to wrap. If a client still owes us a drain,
});                             // pumpOut finishes the job and calls this again.

// THE LEASE (P3.3). Names the condition that un-sticks it rather than a person, which is the
// standing directive in CLAUDE.md 0.1: a wait whose only exit is somebody noticing is the bug
// this whole phase is about. `##shutdown` from a dead daemon never arrives, so it cannot be
// the only door.
var leaseSec = int.TryParse(Environment.GetEnvironmentVariable("DODONA_SHIM_LEASE_SEC"), out var ls) && ls > 0
    ? ls : 1800;
// Poll fast enough that a short lease (the suites set seconds) is still honoured promptly, and
// no faster than a second for the real one.
var leasePollMs = Math.Max(200, Math.Min(1000, leaseSec * 1000 / 4));
_ = Task.Run(async () =>
{
    while (!shutdown.IsCancellationRequested)
    {
        try { await Task.Delay(leasePollMs, shutdown.Token); } catch { return; }
        if (Volatile.Read(ref clientsHere) > 0) { lastContact = DateTime.UtcNow; continue; }
        if ((DateTime.UtcNow - lastContact).TotalSeconds < leaseSec) continue;
        // stderr, which the daemon inherits, so this lands in the daemon log a suite already
        // captures. A process that vanishes without saying why is the other half of this bug.
        Finish($"lease expired -- no client connected for {leaseSec}s, so there is nothing left to deliver to");
        try { child.Kill(entireProcessTree: true); } catch { }
        return;
    }
});

Note("up");

// Serve clients, one at a time, forever.
//
// AND THE NAME NEVER LEAVES THE NAMESPACE (P6.1). This used to construct one server instance,
// serve it, dispose it, and construct the next -- and in that gap `\\.\pipe\` did not contain
// this lane at all. Measured: 8 of 192 reads over 1.5 s saw nothing while this process was alive
// and instantly connectable. Worse, the gap is SYNCHRONISED: every shim in a workspace
// disconnects the instant its daemon exits, and the next daemon's reconcile runs milliseconds
// later, so a single read there declared four to seven live lanes "gone" per restart.
//
// Two instances, and the successor is created BEFORE the current one is torn down, so there is
// always a listener holding the name. maxNumberOfServerInstances is 2 for exactly that and no
// more: only one is ever PUMPED, so the one-client-at-a-time contract (§13) is unchanged. A
// second client can now connect and sit unpumped instead of failing to connect -- which is why
// LaneRuntime bounds its wait for `!hello` rather than blocking on a connection it owns but the
// shim has not promoted.
const int MaxServers = 2;
NamedPipeServerStream NewServer() => new(pipeName, PipeDirection.InOut, MaxServers,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
var server = NewServer();
try
{
while (!shutdown.IsCancellationRequested)
{
    // Already connected when a spare got taken while we were busy: WaitForConnectionAsync would
    // throw InvalidOperationException on it, so ask first.
    if (!server.IsConnected)
    {
        try { await server.WaitForConnectionAsync(shutdown.Token); }
        catch (OperationCanceledException) { server.Dispose(); break; }
    }
    var next = NewServer();          // holds the name while this connection is served and closed

    var writer = new StreamWriter(server) { AutoFlush = true };
    var reader = new StreamReader(server);
    var conn = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    Interlocked.Increment(ref clientsHere);           // the lease is satisfied while this is >0

    try
    {
        // proto= is the daemon's compatibility check before a hot swap (§13): a successor
        // that speaks a different protocol would orphan every live shim, so it must be
        // able to see what these ones speak. Absent means pre-versioning, i.e. proto 1.
        writer.WriteLine($"!hello proto=1 shim={Environment.ProcessId} child={child.Id} " +
                         $"delivered={delivered} buffered={buffer.Count}");
    }
    // The decrement is not tidiness: a `continue` that skipped it would leave clientsHere
    // permanently above zero, and the lease would then never expire again -- the guard against
    // immortal shims, made immortal by its own bookkeeping.
    catch { Interlocked.Decrement(ref clientsHere); lastContact = DateTime.UtcNow; server.Dispose(); server = next; continue; }

    var pumpOut = Task.Run(() =>
    {
        while (!conn.IsCancellationRequested)
        {
            string? next = null;
            lock (gate) { if (delivered < buffer.Count) next = buffer[(int)delivered]; }
            if (next is null)
            {
                if (childExited && !newLine.Wait(500)) continue;
                else if (!childExited) newLine.Wait(500);
                continue;
            }
            writer.WriteLine($"{delivered}\t{next}");   // throws on broken pipe → not advanced
            // Under the gate, so FinishIfDrained cannot read a half-updated pair and decide
            // the buffer is drained one line early -- that would drop the final result.
            lock (gate) delivered++;
            FinishIfDrained();                          // P3.2: was that the last one?
        }
    });

    var pumpIn = Task.Run(async () =>
    {
        while (!conn.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;                    // client disconnected
            if (line == "##shutdown")
            {
                Finish("##shutdown from a client");
                try { child.Kill(entireProcessTree: true); } catch { }
                break;
            }
            child.StandardInput.WriteLine(line);
            child.StandardInput.Flush();
        }
    });

    try { await Task.WhenAny(pumpOut, pumpIn); } catch { }
    conn.Cancel();
    server.Dispose();                                   // unblocks whichever pump is stuck
    try { await Task.WhenAll(pumpOut, pumpIn); } catch { }
    Interlocked.Decrement(ref clientsHere);
    lastContact = DateTime.UtcNow;                      // the lease runs from the LAST goodbye
    server = next;                                      // the name was never unheld
    FinishIfDrained();          // the client may have been the one that took the final line
}
server.Dispose();

// The record dies with the process that wrote it. Nothing in the tree used to delete a
// shim-info file on ANY exit path -- not lane-stop, not stop-daemon, not a hot swap, not a
// crash -- so the set was monotonic for the life of a workspace, and the eighteen "leftovers"
// in the 24-lanes incident were simply every lane that workspace had ever spawned. `dodona ps`
// still reaps stale files, because a -Force kill reaches no finally; this just means there is
// usually nothing left to reap.
}
catch (Exception ex)
{
    // A wrapper that dies of an unhandled exception dies SILENTLY: its stderr belongs to a
    // daemon that has usually exited, so there is no crash report anywhere. That is how a whole
    // debugging round went on the question "who killed these shims?" when the answer was "they
    // threw". Never again without a line.
    Note($"CRASHED: {ex.GetType().Name}: {ex.Message}");
    exitReason = "crashed";
}
Note($"gone ({exitReason})");
if (infoPath is not null) { try { File.Delete(infoPath); } catch { } }
return 0;
