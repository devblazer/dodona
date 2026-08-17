using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    public MainWindow(string root, string instanceId)
    {
        _root = root;
        _instanceId = instanceId;
        InitializeComponent();
        DataContext = _vm;
        Title = $"Dodona — {root}";

        _reader = new StoreReader(root);
        _poller = new Poller(_reader);
        _ = _poller.RunAsync(_vm, snap => Dispatcher.InvokeAsync(() => ApplySnapshot(snap)).Task, _cts.Token);
        UiPipe.Start($"dodona-{instanceId}-ui", this);
        Closed += (_, _) => { _cts.Cancel(); _reader.Dispose(); };
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
                    focused = s.Focused, lines = s.Lines,
                }).ToList(),
            tray = _vm.Tray.ToList(),
            feed = _vm.Feed.Select(f => new { id = f.Id, lane = f.LaneTitle, body = f.Body, acked = f.Acked }).ToList(),
            toasts = _vm.Toasts.Select(t => new { ts = t.Ts, lane = t.Lane, reason = t.Reason }).ToList(),
            status = _vm.Status,
        };
        return JsonSerializer.Serialize(dump);
    }

    /// <summary>Self-rendering screenshot (§17): RenderTargetBitmap of our own visual
    /// tree — no window-finding, no occlusion, no DPI drift. 96dpi means pixel == DIP,
    /// so the full window is exactly 1600x900 everywhere.</summary>
    string Screenshot(string outPath, string? paneTitle)
    {
        FrameworkElement target = Root;
        if (paneTitle is not null)
        {
            target = FindPaneElement(paneTitle)
                ?? throw new InvalidOperationException($"no pane titled '{paneTitle}' in the grid");
        }
        target.UpdateLayout();
        int w = (int)Math.Ceiling(target.ActualWidth), h = (int)Math.Ceiling(target.ActualHeight);
        if (w == 0 || h == 0) return "error: target has no size (window not rendered yet?)";
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(target);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var fs = File.Create(outPath);
        enc.Save(fs);
        return $"screenshot {w}x{h} -> {outPath}";
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
            _poller.OverlayTitle = pane.Title;     // next poll deepens it to 40 raw rows
            _poller.Invalidate();
        }
        return $"overlay {pane.Title}";
    }

    // ------------------------------------------------------------- interactions
    // The view is dumb: every click is just a daemon pipe message (§17) — which is why
    // tests can inject the message instead of driving the UI.

    void Pane_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
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

    void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        InputBox.Clear();
        Send(new { cmd = "input", text });
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.OverlayPane is not null) { SetOverlay(null); e.Handled = true; }
    }

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
