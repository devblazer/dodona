using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dodona;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DodonaUi;

public partial class MainWindow : Window
{
    readonly string _root, _instanceId;
    readonly MainVm _vm = new();
    readonly StoreReader _reader;
    readonly Poller _poller;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Which project this window is showing — the picker uses it to raise an
    /// already-open project instead of opening it twice.</summary>
    public string InstanceId => _instanceId;

    public MainWindow(string root, string instanceId, bool successor = false)
    {
        _root = root;
        _instanceId = instanceId;
        InitializeComponent();
        DataContext = _vm;
        _vm.ProjectName = Path.GetFileName(root.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root;
        _vm.ProjectPath = root;
        Title = $"Dodona — {_vm.ProjectName}";

        _reader = new StoreReader(root);
        _poller = new Poller(_reader);
        _ = _poller.RunAsync(_vm, snap => Dispatcher.InvokeAsync(() => ApplySnapshot(snap)).Task, _cts.Token);
        UiPipe.Start(Instance.UiPipe(instanceId), this, successor);
        if (successor) _ = Task.Run(SignalReadyAsync);
        Closed += (_, _) => { _cts.Cancel(); _reader.Dispose(); };
    }

    /// <summary>Tell the outgoing UI we are up (§13). Sent only once the window exists, so
    /// "ready" means the new build actually runs — the incumbent will not stand down for a
    /// binary that cannot open a window.</summary>
    async Task SignalReadyAsync()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Instance.UiHandoffPipe(_instanceId),
                                                    PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(15000);
            var w = new StreamWriter(c) { AutoFlush = true };
            await w.WriteLineAsync($"ready {Ver.Build} (pid {Environment.ProcessId})");
        }
        catch { /* nobody waiting: we were launched normally, not as a successor */ }
    }

    void ApplySnapshot(Snapshot snap)
    {
        // Re-checked HERE, on the UI thread: a poll snapshot already queued when a pose
        // lands must not overwrite the pose (the poller's own check races the dispatcher).
        if (_vm.PoseName is not null) return;
        var newlyBlocked = _vm.Apply(snap);
        // Toast rule (§8): only when the app lacks focus AND a lane is blocked on you.
        // Never for progress — muted toasts help nobody.
        foreach (var lane in newlyBlocked)
        {
            if (!IsActive)
            {
                _vm.RecordToast(lane, "blocked on you: merge");
                FlashWindow(new WindowInteropHelper(this).Handle, true);
            }
        }
    }

    // ------------------------------------------------------------- ui verbs (§17)

    /// <summary>Handle one verb from the UI pipe. Always on the UI thread; returns one
    /// response line.</summary>
    public string HandleVerb(JsonElement e)
    {
        switch (e.GetProperty("verb").GetString())
        {
            case "dump":
                return Dump();
            case "screenshot":
            {
                var path = e.GetProperty("out").GetString()!;
                var pane = e.TryGetProperty("pane", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                return Screenshot(path, pane);
            }
            case "pose":
            {
                var name = e.GetProperty("name").GetString()!;
                return ApplyPose(name);
            }
            case "overlay":
            {
                var pane = e.GetProperty("pane").GetString()!;
                return SetOverlay(pane.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : pane);
            }
            case "update":
                return Update(e.GetProperty("exe").GetString()!);
            case "close":
                Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
                return "closing";
            default:
                return "error: unknown verb";
        }
    }

    /// <summary>The UI testifies (§17): panes, badges, presence, feed, tray, toasts as
    /// JSON — serialized from the live view model, not from the store, because the
    /// question a dump answers is "what is the UI showing".</summary>
    string Dump()
    {
        var dump = new
        {
            window = new { w = (int)Width, h = (int)Height, title = Title, active = IsActive, root = _root },
            pose = _vm.PoseName,
            overlay = _vm.OverlayPane?.Title,
            slots = _vm.Slots.Select(s => s.IsEmpty
                ? (object)new { slot = s.Slot, empty = true }
                : new
                {
                    slot = s.Slot, empty = false, lane = s.LaneId, title = s.Title, color = s.ColorHex,
                    state = s.State, presence = s.Presence, badge = s.Badge, blocked = s.Blocked,
                    focused = s.Focused, repo = s.Repo, lines = s.Lines.Select(l => l.Text).ToList(),
                }).ToList(),
            quota = _vm.QuotaText,
            tray = _vm.Tray.ToList(),
            feed = _vm.Feed.Select(f => new { id = f.Id, lane = f.LaneTitle, body = f.Body, acked = f.Acked }).ToList(),
            toasts = _vm.Toasts.Select(t => new { ts = t.Ts, lane = t.Lane, reason = t.Reason }).ToList(),
            status = _vm.Status,
        };
        return JsonSerializer.Serialize(dump);
    }

    /// <summary>Screenshot the whole grid, or one pane (§17). Rendering is in Shot, shared
    /// with the picker's --shot.</summary>
    string Screenshot(string outPath, string? paneTitle)
    {
        FrameworkElement target = paneTitle is null
            ? Root
            : FindPaneElement(paneTitle) ?? throw new InvalidOperationException($"no pane titled '{paneTitle}' in the grid");
        return Shot.Save(target, outPath);
    }

    FrameworkElement? FindPaneElement(string title)
    {
        for (int i = 0; i < _vm.Slots.Count; i++)
        {
            if (_vm.Slots[i].IsEmpty || !_vm.Slots[i].Title.Equals(title, StringComparison.OrdinalIgnoreCase)) continue;
            return PaneGrid.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
        }
        return null;
    }

    /// <summary>
    /// Swap this window for a newer build (§13). The daemon hot-swaps but the UI could
    /// not: Windows locks a running image, so a published UI sat on disk while the
    /// operator kept looking at the old one — a swapped daemon behind a stale window is
    /// indistinguishable from nothing having happened.
    ///
    /// A UI handoff is far cheaper than the daemon's: this process owns no lanes, no
    /// store writes and no agents. Its only exclusive resource is the ui pipe, so the
    /// whole protocol is "start the new one, wait for it to say it is up, then let go".
    /// Same safety rule as the daemon: if the successor never answers, THIS window stays.
    /// </summary>
    string Update(string exe)
    {
        if (!File.Exists(exe)) return $"error: no such UI binary: {exe}";
        if (string.Equals(Path.GetFullPath(exe), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return $"already running {exe}";

        try
        {
            using var server = new NamedPipeServerStream(Instance.UiHandoffPipe(_instanceId), PipeDirection.InOut, 1,
                                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = _root };
            foreach (var a in new[] { "--root", _root, "--successor" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);

            // Blocking the UI thread here is deliberate: nothing this window could usefully
            // do mid-handoff, and the pipe reply must not race the shutdown below.
            if (!server.WaitForConnectionAsync().Wait(30000))
                return $"error: successor never connected; staying on {Ver.Build}";
            var ready = new StreamReader(server).ReadLine();
            if (ready is null || !ready.StartsWith("ready"))
                return $"error: successor said '{ready}'; staying on {Ver.Build}";

            // Exit only now. The successor is retrying the ui pipe and takes it the
            // moment this process dies (UiPipe.Start awaitPredecessor).
            Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            return $"updated: {Ver.Build} → {ready}";
        }
        catch (Exception ex) { return $"error: update failed ({ex.Message}); staying on {Ver.Build}"; }
    }

    public string ApplyPose(string name)
    {
        if (name.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            _vm.PoseName = null;
            _vm.OverlayPane = null;
            _poller.OverlayTitle = null;
            _poller.Invalidate();
            return "pose live (store polling resumed)";
        }
        var pose = Poses.Get(name);
        if (pose is null) return $"error: unknown pose '{name}' (have: {string.Join(", ", Poses.Names)}, live)";
        var (snap, overlayTitle, toast) = pose.Value;
        _vm.PoseName = name;
        _vm.OverlayPane = null;
        _vm.Toasts.Clear();
        _vm.Apply(snap);
        if (toast is not null) _vm.Toasts.Add(toast);
        if (overlayTitle is not null && snap.Overlay is PaneSnap ov)
            _vm.OverlayPane = PaneView.From(ov, _vm.Slots.First(s => !s.IsEmpty && s.Title == overlayTitle).Slot);
        _vm.Status = $"pose: {name}";
        return $"pose {name} applied";
    }

    string SetOverlay(string? paneTitle)
    {
        if (paneTitle is null)
        {
            _vm.OverlayPane = null;
            _poller.OverlayTitle = null;
            return "overlay off";
        }
        var pane = _vm.Slots.FirstOrDefault(s => !s.IsEmpty && s.Title.Equals(paneTitle, StringComparison.OrdinalIgnoreCase));
        if (pane is null) return $"error: no pane titled '{paneTitle}'";
        _vm.OverlayPane = pane;                    // immediate, with the pane's current lines
        if (_vm.PoseName is null)
        {
            _poller.OverlayTitle = pane.Title;     // next poll deepens it to 120 raw rows
            _poller.Invalidate();
        }
        return $"overlay {pane.Title}";
    }

    // ------------------------------------------------------------- transcript scrolling
    // A snapshot rebuilds all six PaneViews, so the ItemsControl regenerates its
    // containers and every ScrollViewer would snap back to the top four times a second.
    // Position is therefore remembered per SLOT (which is sticky, §8) rather than per
    // element: NaN means "follow the tail", a number means the operator scrolled up and
    // wants to stay there. Reading a lane while it talks has to be possible.

    readonly Dictionary<int, double> _paneScroll = new();
    double _overlayScroll = double.NaN;

    void Transcript_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.DataContext is not PaneView p) return;
        _paneScroll[p.Slot] = Follow(sv, e, _paneScroll.TryGetValue(p.Slot, out var at) ? at : double.NaN);
    }

    void Overlay_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        _overlayScroll = Follow(sv, e, _overlayScroll);
    }

    /// <summary>Content grew (or the container was rebuilt) → put the view back where it
    /// was, or at the end if we were following. Otherwise the operator just scrolled, so
    /// record it — landing at the bottom re-arms following. Returns the new saved offset.</summary>
    static double Follow(ScrollViewer sv, ScrollChangedEventArgs e, double saved)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            sv.ScrollToVerticalOffset(double.IsNaN(saved) ? sv.ScrollableHeight : saved);
            return saved;
        }
        return sv.VerticalOffset >= sv.ScrollableHeight - 1 ? double.NaN : sv.VerticalOffset;
    }

    // ------------------------------------------------------------- interactions
    // The view is dumb: every click is just a daemon pipe message (§17) — which is why
    // tests can inject the message instead of driving the UI.

    void Pane_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p) return;
        if (p.IsEmpty) { StartLane(); e.Handled = true; return; }   // an empty slot is an invitation
        Send(new { cmd = "focus", lane = p.LaneId });
        e.Handled = true;
    }

    void Pane_Overlay(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        SetOverlay(p.Title);
        e.Handled = true;
    }

    void Feed_Ack(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FeedView f) return;
        Send(new { cmd = "ack", id = f.Id });
    }

    void Pane_Close(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        Send(new { cmd = "lane-stop", lane = p.LaneId });
        e.Handled = true;                       // not a pane click; do not also focus it
    }

    void Pane_Wake(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        Send(new { cmd = "lane-respawn", lane = p.LaneId });
        e.Handled = true;
    }

    void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        // Always sends. If there is nowhere to route, the daemon starts a lane and says
        // so — the UI does not get to invent a dialog for that, because deciding where
        // work goes is the system's job, not a form for the operator to fill in.
        InputBox.Clear();
        Send(new { cmd = "input", text });
    }

    void StartLane(string? suggestedName = null, string? firstMessage = null)
    {
        var dlg = new StartLaneWindow(_instanceId, suggestedName, firstMessage) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.StartedLane < 0) return;
        _poller.Invalidate();
        if (firstMessage is { Length: > 0 })
            Send(new { cmd = "say", lane = dlg.StartedLane, text = firstMessage });
    }


    void Input_TextChanged(object sender, TextChangedEventArgs e) =>
        InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.OverlayPane is not null) { SetOverlay(null); e.Handled = true; }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { OpenPicker(); e.Handled = true; }
    }

    /// <summary>Open another project (Ctrl+P). It gets its own window, its own daemon and
    /// its own everything — instances share nothing (§14), so several can be open at
    /// once and this window keeps running untouched.</summary>
    void OpenPicker()
    {
        var existing = Application.Current.Windows.OfType<PickerWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new PickerWindow { Owner = null }.Show();
    }

    void Project_Click(object sender, RoutedEventArgs e) => OpenPicker();

    void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Root) try { DragMove(); } catch { }
    }

    /// <summary>Fire a daemon command off the UI thread; the reply becomes the status line.</summary>
    void Send(object req) => Task.Run(() =>
    {
        var reply = DaemonClient.Send(_instanceId, req);
        Dispatcher.BeginInvoke(() => _vm.Status = reply);
    });

    [DllImport("user32.dll")]
    static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
