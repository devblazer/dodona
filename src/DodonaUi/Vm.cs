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
                       bool Blocked, bool Focused, List<LineSnap> Lines)
{
    /// <summary>Which repository this lane's open ticket lands in — "" in a single-repo
    /// project, which must never see the word.</summary>
    public string Repo { get; init; } = "";
}
public record FeedSnap(long Id, string LaneTitle, string Ts, string Body, bool Acked, bool IsSystem);
public record Snapshot(PaneSnap?[] Slots, List<string> Tray, List<FeedSnap> Feed, PaneSnap? Overlay)
{
    /// <summary>The 5-hour-window line, or null when no reading has ever arrived.</summary>
    public string? Quota { get; init; }
}

// ---------------------------------------------------------------- bindable views

/// <summary>
/// Who is speaking, as colour. A pane is a conversation between two parties plus a
/// machine, and telling them apart should not require reading the prefix. Brushes are
/// frozen and shared: a pane rebuilds on every poll, so one set for the app, never one
/// per row per tick.
/// </summary>
static class Ink
{
    static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // accent (prefix + the left rule) · body text · row wash. The washes are deliberately
    // near-black: they group a turn without turning the pane into a colour chart.
    static readonly Brush YouAccent   = B("#FF7FB0F0"), YouBody   = B("#FFE6ECF4"), YouRow   = B("#FF1E2634");
    static readonly Brush AgentAccent = B("#FFB69CF0"), AgentBody = B("#FFCCD3DB"), AgentRow = B("#FF25252F");
    static readonly Brush DoneAccent  = B("#FF7FC98A"), DoneBody  = B("#FFAAD0AF"), DoneRow  = B("#FF1D2721");
    static readonly Brush NoteAccent  = B("#FFE0A94E"), NoteBody  = B("#FFD9C39A"), NoteRow  = B("#FF2A2419");
    static readonly Brush BadAccent   = B("#FFE07A7A"), BadBody   = B("#FFD9A8A8"), BadRow   = B("#FF2E1F20");
    static readonly Brush DimAccent   = B("#FF5C636D"), DimBody   = B("#FF8B929C"), DimRow   = B("#FF212429");

    // Prose in a proportional face, machine text in mono — the transcript is mostly
    // sentences, and sentences do not read well in a 12px terminal font at pane width.
    static readonly FontFamily Prose = new("Segoe UI"), Mono = new("Consolas");

    public static Brush Accent(string kind) => kind switch
    {
        "user_input" => YouAccent, "agent_line" => AgentAccent, "result" => DoneAccent,
        "announcement" => NoteAccent, "error" => BadAccent, _ => DimAccent,
    };

    public static Brush Body(string kind) => kind switch
    {
        "user_input" => YouBody, "agent_line" => AgentBody, "result" => DoneBody,
        "announcement" => NoteBody, "error" => BadBody, _ => DimBody,
    };

    public static Brush Row(string kind) => kind switch
    {
        "user_input" => YouRow, "agent_line" => AgentRow, "result" => DoneRow,
        "announcement" => NoteRow, "error" => BadRow, _ => DimRow,
    };

    public static FontFamily Face(string kind) => kind is "wire" or "system" ? Mono : Prose;

    /// <summary>Dodona's own voice. The icon is an oak: six lane colours as leaves on one
    /// trunk, and this is that trunk (brightened to read on the dark plate). The system is
    /// deliberately NOT one of the six — it is what they grow from, and a feed row from
    /// DODONA should never look like a seventh lane.</summary>
    public static readonly Brush System = B("#FFC79A63");
}

/// <summary>One transcript row, kept as (kind, prefix, body) rather than a formatted
/// string so the view can colour the speaker.</summary>
public sealed class LineView
{
    public string Kind { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Body { get; init; } = "";

    /// <summary>Prefix + body — byte-for-byte what the pane rendered as a single string
    /// before it had colour. `ui dump` still emits this: colour is a view concern, and
    /// what the UI testifies to must not change shape because it got prettier (§17).</summary>
    public string Text => Prefix + Body;

    public Brush AccentBrush => Ink.Accent(Kind);
    public Brush BodyBrush => Ink.Body(Kind);
    public Brush RowBrush => Ink.Row(Kind);
    public FontFamily FaceValue => Ink.Face(Kind);

    public static LineView From(LineSnap l) => new()
    {
        Kind = l.Kind,
        Body = l.Body,
        Prefix = l.Kind switch
        {
            "user_input" => "you> ",
            "agent_line" => "agent> ",
            "result" => "✓ ",
            "announcement" => "· ",
            _ => $"{l.Kind}> ",
        },
    };

    public override string ToString() => Text;
}

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
    public List<LineView> Lines { get; init; } = new();

    public string Repo { get; init; } = "";

    public string ColorHex => Palette[Slot % Palette.Length];
    public Brush LaneBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
    public string FocusMark => Focused ? "▶ " : "";
    public Visibility RepoVisibility => Repo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    // The lane's controls: close (retire it) always; wake only when there is something to
    // wake. Buttons, because the feed saying "undo: dodona lane-stop 3" at a GUI user was
    // this project's original sin.
    public Visibility CloseVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WakeVisibility => !IsEmpty && State is "dormant" or "unreachable" ? Visibility.Visible : Visibility.Collapsed;
    // Blocked-on-you: border highlight + glyph — border, not fill; colour still means the lane (§8).
    public Brush BorderBrushValue => Blocked ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x44));
    public Thickness BorderThicknessValue => Blocked ? new Thickness(2) : new Thickness(1);
    public Visibility GlyphVisibility => Blocked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();
    public Visibility BodyVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public double TitleOpacity => State == "alive" || IsEmpty ? 1.0 : 0.45;

    public static PaneView From(PaneSnap s, int slot) => new()
    {
        Slot = slot, LaneId = s.LaneId, Title = s.Title, State = s.State, Presence = s.Presence,
        Badge = s.Badge, Blocked = s.Blocked, Focused = s.Focused, Repo = s.Repo,
        Lines = s.Lines.Select(LineView.From).ToList(),
    };
}

public sealed class FeedView
{
    public long Id { get; init; }
    public string LaneTitle { get; init; } = "";
    public string Body { get; init; } = "";
    public bool Acked { get; init; }
    public bool IsSystem { get; init; }
    public Brush ChipBrush { get; init; } = Brushes.Gray;
    public double RowOpacity => Acked ? 0.45 : 1.0;
    public Visibility AckVisibility => Acked ? Visibility.Collapsed : Visibility.Visible;

    // Every feed row is an announcement, so colouring by kind would say nothing here —
    // the axis that matters is WHICH LANE, and it was carried by an 8px chip alone. The
    // name now carries it too, so the feed is scannable by lane without reading a word.
    // Dodona's own rows are round rather than square: the system is not a seventh lane,
    // and shape says so where a hue could still be mistaken for one.
    public double ChipRadius => IsSystem ? 5 : 2;
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

    string _projectName = "";
    public string ProjectName { get => _projectName; set { _projectName = value; Notify(nameof(ProjectName)); } }

    string _projectPath = "";
    public string ProjectPath { get => _projectPath; set { _projectPath = value; Notify(nameof(ProjectPath)); } }

    string _trayText = "";
    public string TrayText { get => _trayText; set { _trayText = value; Notify(nameof(TrayText)); } }

    string _quotaText = "";
    public string QuotaText { get => _quotaText; set { _quotaText = value; Notify(nameof(QuotaText)); Notify(nameof(QuotaBrush)); } }
    /// <summary>Amber past the CLI's own warning threshold — the UI follows the CLI's
    /// escalation rather than inventing colour bands.</summary>
    public Brush QuotaBrush => System.Text.RegularExpressions.Regex.Match(_quotaText, @"(\d+)%") is { Success: true } m
        && int.Parse(m.Groups[1].Value) >= 90
            ? new SolidColorBrush(Color.FromRgb(0xE0, 0xA9, 0x4E))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));

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
            Feed.Add(new FeedView
            {
                Id = f.Id, LaneTitle = f.LaneTitle, Body = f.Body, Acked = f.Acked, IsSystem = f.IsSystem,
                // The dispatcher lane holds no grid slot, so it has no slot colour to look
                // up — before this it fell through to grey, which made the system's own
                // voice the least identifiable thing in the feed.
                ChipBrush = f.IsSystem ? Ink.System
                          : titleToBrush.TryGetValue(f.LaneTitle, out var b) ? b
                          : Brushes.Gray,
            });

        Tray.Clear();
        foreach (var t in s.Tray) Tray.Add(t);
        TrayText = s.Tray.Count == 0 ? "tray: empty" : $"tray: {string.Join(", ", s.Tray)}";
        QuotaText = s.Quota ?? "";

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
