using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;

namespace DodonaUi;

/// <summary>
/// The UI's own pipe (§17): `dodona ui dump|screenshot|pose|overlay|close` land here.
/// Same one-exchange line protocol as the daemon's control pipe. Verbs execute on the
/// UI thread — they read and mutate the visual tree.
/// </summary>
static class UiPipe
{
    public static void Start(string pipeName, MainWindow win) => Task.Run(async () =>
    {
        while (true)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch
            {
                // Another UI already owns this root — one grid per store (§14).
                await win.Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show($"Another Dodona UI is already running for this root.\n(pipe {pipeName})",
                        "Dodona", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Application.Current.Shutdown(3);
                });
                return;
            }

            try
            {
                await server.WaitForConnectionAsync();
                var r = new StreamReader(server);
                var w = new StreamWriter(server) { AutoFlush = true };
                var req = await r.ReadLineAsync();
                if (req is not null)
                {
                    string resp;
                    try
                    {
                        using var d = JsonDocument.Parse(req);
                        var e = d.RootElement.Clone();
                        resp = await win.Dispatcher.InvokeAsync(() =>
                        {
                            try { return win.HandleVerb(e); }
                            catch (Exception ex) { return $"error: {ex.Message}"; }
                        });
                    }
                    catch (Exception ex) { resp = $"error: {ex.Message}"; }
                    w.WriteLine(resp);
                    w.WriteLine("##end");
                }
            }
            catch { /* client vanished mid-conversation */ }
            try { server.Disconnect(); } catch { }
            try { server.Dispose(); } catch { }
        }
    });
}
