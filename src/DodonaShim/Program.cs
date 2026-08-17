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
//
// Usage: DodonaShim <pipeName> <childExe> [childArgs...]
// Env:   DODONA_SHIM_INFO=<path> — writes {shimPid, childPid, pipeName} JSON.

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
    childExited = true;
    newLine.Release();
});

// Serve clients, one at a time, forever.
while (!shutdown.IsCancellationRequested)
{
    var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    try { await server.WaitForConnectionAsync(shutdown.Token); }
    catch (OperationCanceledException) { server.Dispose(); break; }

    var writer = new StreamWriter(server) { AutoFlush = true };
    var reader = new StreamReader(server);
    var conn = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

    try
    {
        writer.WriteLine($"!hello shim={Environment.ProcessId} child={child.Id} " +
                         $"delivered={delivered} buffered={buffer.Count}");
    }
    catch { server.Dispose(); continue; }

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
            delivered++;
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
                try { child.Kill(entireProcessTree: true); } catch { }
                shutdown.Cancel();
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
}

return 0;
