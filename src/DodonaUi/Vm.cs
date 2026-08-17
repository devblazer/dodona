using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace DodonaUi;

// ---------------------------------------------------------------- snapshots (plain data)
// The poller (and the poses) produce these off the UI thread; MainVm.Apply turns them
// into bindable views on the UI thread. Snapshot equality (serialized) gates re-apply.

public record LineSnap(string Kind, string Body);
public record PaneSnap(long LaneId, string Title, string State, string Presence, int Badge,
                       bool Blocked, bool Focused, List<LineSnap> Lines);
public record FeedSnap(long Id, string LaneTitle, string Ts, string Body, bool Acked);
public record Snapshot(PaneSnap?[] Slots, List<string> Tray, List<FeedSnap> Feed, PaneSnap? Overlay);

// ---------------------------------------------------------------- bindable views

public sealed class PaneView
{
    // Colour means the lane, not the state (§8) — fixed palette by (sticky) slot.
    public static readonly string[] Palette = { "#4FC3F7", "#81C784", "#FFB74D", "#BA68C8", "#E57373", "#FFD54F" };

    public int Slot { get; init; }
    public bool IsEmpty { get; init; }
    public long LaneId { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string Presence { get; init; } = "";
    public int Badge { get; init; }
    public bool Blocked { get; init; }
    public bool Focused { get; init; }
    public List<string> Lines { get; init; } = new();

    public string ColorHex => Palette[Slot % Palette.Length];
    public Brush LaneBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
    public string FocusMark => Focused ? "▶ " : "";
    // Blocked-on-you: border highlight + glyph — border, not fill; colour still means the lane (§8).
    public Brush BorderBrushValue => Blocked ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x44));
    public Thickness BorderThicknessValue => Blocked ? new Thickness(2) : new Thickness(1);
    public Visibility GlyphVisibility => Blocked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();
    public Visibility BodyVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public double TitleOpacity => State == "alive" || IsEmpty ? 1.0 : 0.45;

    public static string FormatLine(LineSnap l) => l.Kind switch
    {
        "user_input" => $"you> {l.Body}",
        "agent_line" => $"agent> {l.Body}",
        "result" => $"✓ {l.Body}",
        "announcement" => $"· {l.Body}",
        _ => $"{l.Kind}> {l.Body}",
    };

    public static PaneView From(PaneSnap s, int slot) => new()
    {
        Slot = slot, LaneId = s.LaneId, Title = s.Title, State = s.State, Presence = s.Presence,
        Badge = s.Badge, Blocked = s.Blocked, Focused = s.Focused,
        Lines = s.Lines.Select(FormatLine).ToList(),
    };
}

public sealed class FeedView
{
    public long Id { get; init; }
    public string LaneTitle { get; init; } = "";
    public string Body { get; init; } = "";
    public bool Acked { get; init; }
    public Brush ChipBrush { get; init; } = Brushes.Gray;
    public double RowOpacity => Acked ? 0.45 : 1.0;
    public Visibility AckVisibility => Acked ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ToastView
{
    public string Ts { get; init; } = "";
    public string Lane { get; init; } = "";
    public string Reason { get; init; } = "";
}

// ---------------------------------------------------------------- the view model

public sealed class MainVm : INotifyPropertyChanged
{
    public ObservableCollection<PaneView> Slots { get; } = new();
    public ObservableCollection<FeedView> Feed { get; } = new();
    public ObservableCollection<string> Tray { get; } = new();
    public ObservableCollection<ToastView> Toasts { get; } = new();

    PaneView? _overlayPane;
    public PaneView? OverlayPane { get => _overlayPane; set { _overlayPane = value; Notify(nameof(OverlayPane)); Notify(nameof(OverlayVisible)); } }
    public Visibility OverlayVisible => _overlayPane is null ? Visibility.Collapsed : Visibility.Visible;

    string _status = "";
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }

    string _trayText = "";
    public string TrayText { get => _trayText; set { _trayText = value; Notify(nameof(TrayText)); } }

    /// <summary>Non-null while a pose is applied — the poller stands down (§17).</summary>
    public volatile string? PoseName;

    readonly HashSet<string> _blockedBefore = new();

    public MainVm()
    {
        for (int i = 0; i < 6; i++) Slots.Add(new PaneView { Slot = i, IsEmpty = true });
    }

    /// <summary>Rebuild the whole view from a snapshot (UI thread). Returns lanes that
    /// just transitioned to blocked — the caller decides whether that warrants a toast.</summary>
    public List<string> Apply(Snapshot s)
    {
        Slots.Clear();
        for (int i = 0; i < 6; i++)
            Slots.Add(s.Slots.Length > i && s.Slots[i] is PaneSnap p ? PaneView.From(p, i) : new PaneView { Slot = i, IsEmpty = true });

        var titleToBrush = Slots.Where(x => !x.IsEmpty).ToDictionary(x => x.Title, x => x.LaneBrush, StringComparer.OrdinalIgnoreCase);
        Feed.Clear();
        foreach (var f in s.Feed)
            Feed.Add(new FeedView { Id = f.Id, LaneTitle = f.LaneTitle, Body = f.Body, Acked = f.Acked,
                                    ChipBrush = titleToBrush.TryGetValue(f.LaneTitle, out var b) ? b : Brushes.Gray });

        Tray.Clear();
        foreach (var t in s.Tray) Tray.Add(t);
        TrayText = s.Tray.Count == 0 ? "tray: empty" : $"tray: {string.Join(", ", s.Tray)}";

        if (s.Overlay is PaneSnap ov && OverlayPane is not null)
            OverlayPane = PaneView.From(ov, Slots.FirstOrDefault(x => x.Title.Equals(ov.Title, StringComparison.OrdinalIgnoreCase))?.Slot ?? 0);

        var nowBlocked = Slots.Where(x => x.Blocked).Select(x => x.Title).ToList();
        var fresh = nowBlocked.Where(t => !_blockedBefore.Contains(t)).ToList();
        _blockedBefore.Clear();
        foreach (var t in nowBlocked) _blockedBefore.Add(t);
        return fresh;
    }

    public void RecordToast(string lane, string reason)
    {
        Toasts.Add(new ToastView { Ts = DateTime.UtcNow.ToString("o"), Lane = lane, Reason = reason });
        while (Toasts.Count > 20) Toasts.RemoveAt(0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
