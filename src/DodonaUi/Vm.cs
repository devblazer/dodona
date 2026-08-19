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

    /// <summary>WHICH PROJECT this lane is in — a project being one folder, a `members`
    /// row (docs/GLOSSARY.md). Exactly what <see cref="Dodona.Projects.Field"/> returns: a
    /// project path, `neutral`, or `none (cwd=…)`; and "" for "there is nothing to say",
    /// which is the case for every lane of a ONE-project workspace and is what keeps that
    /// workspace's dump reporting what it always did (LOCATIONS-PLAN P1.2). Same rule the
    /// <see cref="Repo"/> tag above already follows, for the same reason.</summary>
    public string Project { get; init; } = "";

    /// <summary>Highest user_input row id — moves when a routed message lands here.</summary>
    public long LastInputId { get; init; }

    /// <summary>The operator has collapsed this tile to a one-line strip. A view choice they
    /// made, never one the system made for them: nothing is stopped, demoted or hidden from the
    /// feed, and one click brings it back (LANE-LIFECYCLE §2 — slot-pressure eviction stays
    /// rejected; this is the operator's hand, not the system reclaiming space).</summary>
    public bool Collapsed { get; init; }
}
public record FeedSnap(long Id, string LaneTitle, string Ts, string Body, bool Acked, bool IsSystem)
{
    /// <summary>Which workspace this row came from — the merged feed's own axis (§6). Empty
    /// when only one workspace is open, so a single-workspace operator never sees a chip
    /// answering a question they did not ask. `[dodona]` for the concierge's own rows: a
    /// group-scope clarification belongs to no workspace's column by definition.</summary>
    public string Workspace { get; init; } = "";

    /// <summary>Set on concierge rows only. The feed's ack button routes to the concierge's
    /// pipe rather than a workspace daemon's — writes stay pipe-addressed (§6), and the
    /// concierge is not a seventh workspace any more than Dodona is a seventh lane.</summary>
    public bool IsConcierge { get; init; }
}

/// <summary>One lane on a band: a chip, its attention badge, and nothing else. A band is
/// "the tray idiom at workspace scale" (§6) — awareness, not a pane.</summary>
public record BandLaneSnap(long LaneId, string Title, string Presence, int Badge, bool Blocked);

/// <summary>
/// One awake workspace that is NOT holding the grid (§6, shape B). A compact row of lane
/// chips with attention badges, so both lives stay visible at once without halving every
/// pane — which is what the rejected single-merged-grid and tabs-only shapes each got wrong
/// in opposite directions (§8).
///
/// **A band is a VIEW, never an eviction.** No lane is demoted, trayed or stopped by being
/// in one, and `LANE-LIFECYCLE.md` §2 (slot-pressure eviction rejected) stands untouched:
/// the six-slot cap, `focused_lane` and the dispatcher lane all remain per-workspace
/// concepts inside each store.
/// </summary>
public record BandSnap(string WorkspaceId, string Name, bool Live, List<BandLaneSnap> Lanes, int Tray, int Badge);

public record Snapshot(PaneSnap?[] Slots, List<string> Tray, List<FeedSnap> Feed, PaneSnap? Overlay)
{
    /// <summary>The 5-hour-window line, or null when no reading has ever arrived.</summary>
    public string? Quota { get; init; }

    /// <summary>Every awake workspace that is not holding the grid (§6).</summary>
    public List<BandSnap> Bands { get; init; } = new();

    /// <summary>Which workspace holds the grid, and what it is called. Empty on boot-to-zero
    /// — a window with no workspace awake, just feed and input, which §4 calls out as a real
    /// state rather than an error to be avoided.</summary>
    public string FocusedWorkspace { get; init; } = "";
    public string FocusedWorkspaceName { get; init; } = "";

    /// <summary>True when nothing is awake at all: the grid is replaced by an invitation, and
    /// the input box is still the front door (§4 — clicking is for looking, typing is for
    /// starting).</summary>
    public bool BootToZero => FocusedWorkspace.Length == 0;
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
    /// <summary>See <see cref="PaneSnap.Project"/>. Rendered as a tag beside the title, so a
    /// lane in the wrong project is visible to a PERSON and not only to a check — which is
    /// the whole point of LOCATIONS-PLAN Phase 1.</summary>
    public string Project { get; init; } = "";
    /// <summary>True for ~1.5s after a routed message lands here — the eye follows the
    /// routing without reading a receipt (operator: "a lane pulse so I can see where it
    /// routed"). Rendered as a brief border in the LANE's own colour; blocked's white
    /// border always wins.</summary>
    public bool Pulsing { get; init; }

    /// <summary>Collapsed to a chip by the operator. Nothing about the lane changed — it is
    /// alive, it is still in the feed, and its badge still shows on the chip.</summary>
    public bool Collapsed { get; init; }

    public string ColorHex => Palette[Slot % Palette.Length];
    /// <summary>Collapse/expand glyph. Pointing down means "there is more here".</summary>
    public string CollapseGlyph => Collapsed ? "▸" : "▾";
    public string CollapseTip => Collapsed ? "Expand this lane" : "Collapse to a chip — nothing stops";
    public Brush LaneBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
    public string FocusMark => Focused ? "▶ " : "";
    public Visibility RepoVisibility => Repo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A tile is narrow, so the tag shows the project's LEAF folder — `alpha`, not
    /// forty characters of path. `ui dump` keeps the unabbreviated value, because a dump
    /// testifies and a check needs a value it cannot mistake for another project's.
    /// `none (cwd=…)` becomes a short shout rather than a clipped path: the operator needs
    /// to SEE it, and the cwd itself is one `dodona status` away.</summary>
    public string ProjectLabel =>
        Project.Length == 0 ? ""
        : Project.StartsWith("none ", StringComparison.Ordinal) ? "no project"
        : Project.Contains('\\') ? Project[(Project.LastIndexOf('\\') + 1)..]
        : Project;
    public Visibility ProjectVisibility => Project.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    // The lane's controls: close (retire it) always; wake only when there is something to
    // wake. Buttons, because the feed saying "undo: dodona lane-stop 3" at a GUI user was
    // this project's original sin.
    public Visibility CloseVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WakeVisibility => !IsEmpty && State is "dormant" or "unreachable" ? Visibility.Visible : Visibility.Collapsed;
    // Blocked-on-you: border highlight + glyph — border, not fill; colour still means the lane (§8).
    public Brush BorderBrushValue => Blocked ? Brushes.White
        : Pulsing ? LaneBrush
        : new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x44));
    public Thickness BorderThicknessValue => Blocked || Pulsing ? new Thickness(2) : new Thickness(1);
    public Visibility GlyphVisibility => Blocked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();
    public Visibility BodyVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public double TitleOpacity => State == "alive" || IsEmpty ? 1.0 : 0.45;

    public static PaneView From(PaneSnap s, int slot, bool pulsing = false) => new()
    {
        Slot = slot, LaneId = s.LaneId, Title = s.Title, State = s.State, Presence = s.Presence,
        Badge = s.Badge, Blocked = s.Blocked, Focused = s.Focused, Repo = s.Repo, Project = s.Project, Pulsing = pulsing,
        Collapsed = s.Collapsed,
        Lines = s.Lines.Select(LineView.From).ToList(),
    };
}

/// <summary>One lane chip on a band. Title plus a badge; deliberately not a pane.</summary>
public sealed class BandLaneView
{
    public long LaneId { get; init; }
    public string Title { get; init; } = "";
    public int Badge { get; init; }
    public bool Blocked { get; init; }
    public string BadgeText => Badge > 9 ? "9+" : Badge.ToString();
    public Visibility BadgeVisibility => Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush ChipBrush => Blocked ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x79));
    public string ToolTipText => $"{Title}{(Blocked ? " — waiting on you" : "")}";
}

/// <summary>
/// One awake-but-not-focused workspace, as a compact row (§6). Clicking it swaps which
/// workspace holds the grid — the only thing a band does, because it is a view choice and
/// nothing more.
/// </summary>
public sealed class BandView
{
    /// <summary>Workspaces get their own palette, distinct from the six LANE colours. A
    /// workspace is not a seventh lane, and reusing the lane palette would say it was —
    /// the same reasoning that gives Dodona's own feed rows the trunk colour and a round
    /// chip rather than a hue from the six.</summary>
    public static readonly string[] Palette = { "#7EA6C9", "#C99E7E", "#9EC97E", "#C97EA6", "#A67EC9", "#7EC9C9" };

    public string WorkspaceId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Live { get; init; }
    public int Tray { get; init; }
    public int Badge { get; init; }
    public List<BandLaneView> Lanes { get; init; } = new();

    public int Index { get; init; }
    public string ColorHex => Palette[Index % Palette.Length];
    public Brush WorkspaceBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
    public Visibility BadgeVisibility => Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();
    /// <summary>An awake workspace with no work lanes still gets a band — it is awake, and
    /// hiding it would make "which of my lives are running" unanswerable.</summary>
    public string Summary => Lanes.Count == 0
        ? "no lanes"
        : $"{Lanes.Count} lane{(Lanes.Count == 1 ? "" : "s")}" + (Tray > 0 ? $" · {Tray} in tray" : "");
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

    /// <summary>Which workspace this row came from (§6). Empty in a single-workspace window,
    /// where the axis carries no information and a chip would be noise.</summary>
    public string Workspace { get; init; } = "";
    public Brush WorkspaceBrush { get; init; } = Brushes.Gray;
    public Visibility WorkspaceVisibility => Workspace.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A concierge row: acking it must reach the CONCIERGE's pipe, not a workspace
    /// daemon's. Writes stay pipe-addressed (§6) and the ids come from different tables.</summary>
    public bool IsConcierge { get; init; }

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
    /// <summary>The expanded tiles, in creation order. No fixed count and no empty
    /// placeholders: the grid divides itself as lanes arrive (§8 as revised by the operator,
    /// 2026-08-18). Named `Slots` still because that is what `ui dump` calls it, and what the
    /// UI testifies to must not change shape just because the layout got better (§17).</summary>
    public ObservableCollection<PaneView> Slots { get; } = new();

    /// <summary>Lanes the operator collapsed, as chips. Wrapped rather than one row each, so
    /// twenty of them cost two lines instead of eating the grid they were collapsed to
    /// protect. The operator asked for no scrolling anywhere: panes shrink, and crowding is
    /// the cue to collapse more.</summary>
    public ObservableCollection<PaneView> CollapsedLanes { get; } = new();

    public Visibility CollapsedVisibility => CollapsedLanes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// How many columns the expanded grid divides into. Grows with the work, and deliberately
    /// prefers WIDE over tall: a transcript is vertical, so a pane that loses height stops
    /// showing conversation faster than one that loses width stops showing lines.
    ///
    /// No scrolling and no cap — the operator's call. Past a dozen the tiles are genuinely
    /// small, and that is the signal to collapse some, not for the system to start hiding
    /// work (LANE-LIFECYCLE §2).
    /// </summary>
    public int GridColumns => Slots.Count switch
    {
        0 or 1 => 1,
        2 => 2,
        3 or 4 => 2,
        <= 6 => 3,
        <= 9 => 3,
        <= 12 => 4,
        <= 16 => 4,
        <= 20 => 5,
        _ => 6,
    };

    public ObservableCollection<FeedView> Feed { get; } = new();
    public ObservableCollection<string> Tray { get; } = new();
    public ObservableCollection<ToastView> Toasts { get; } = new();

    /// <summary>Every awake workspace not holding the grid (§6). Empty in the ordinary
    /// single-workspace case, which is why the band strip collapses to nothing there.</summary>
    public ObservableCollection<BandView> Bands { get; } = new();

    public Visibility BandsVisibility => Bands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    string _focusedWorkspace = "", _focusedWorkspaceName = "";
    public string FocusedWorkspace { get => _focusedWorkspace; private set { _focusedWorkspace = value; Notify(nameof(FocusedWorkspace)); } }
    public string FocusedWorkspaceName { get => _focusedWorkspaceName; private set { _focusedWorkspaceName = value; Notify(nameof(FocusedWorkspaceName)); Notify(nameof(WorkspaceLabel)); } }

    /// <summary>What the header says. `FocusedWorkspaceName` is the honest value a dump
    /// reports (empty on boot-to-zero); this is the same thing made readable, because a bare
    /// "▾" in the corner looks like a bug rather than a state.</summary>
    public string WorkspaceLabel => _focusedWorkspaceName.Length > 0 ? _focusedWorkspaceName : "no workspace";

    bool _bootToZero;
    /// <summary>No workspace awake at all — a real state, not an error (§4). The grid is
    /// replaced by an invitation and the input box still works, because typing is the front
    /// door whether anything is running or not.</summary>
    public bool BootToZero { get => _bootToZero; private set { _bootToZero = value; Notify(nameof(BootToZero)); Notify(nameof(GridVisibility)); Notify(nameof(ZeroVisibility)); } }

    /// <summary>
    /// The grid shows whenever there is anything to show. Deliberately NOT keyed to
    /// `BootToZero`: that was the first version, and it hid the grid in every POSE, because a
    /// pose sets panes but names no workspace. Worse, `poses_render_distinct` still passed —
    /// the screenshots differed because the FEED differed — so a completely blank grid shipped
    /// behind a green check. A window holding tiles must render them whatever else is true.
    /// </summary>
    public Visibility GridVisibility => Slots.Count > 0 || CollapsedLanes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The zero state is for a window with nothing to show AND no workspace awake —
    /// both halves, so it can never appear over a grid that has lanes in it.</summary>
    public Visibility ZeroVisibility => _bootToZero && Slots.Count == 0 && CollapsedLanes.Count == 0
        ? Visibility.Visible : Visibility.Collapsed;

    PaneView? _overlayPane;
    public PaneView? OverlayPane { get => _overlayPane; set { _overlayPane = value; Notify(nameof(OverlayPane)); Notify(nameof(OverlayVisible)); } }
    public Visibility OverlayVisible => _overlayPane is null ? Visibility.Collapsed : Visibility.Visible;

    string _status = "";
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }

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

    // pulse bookkeeping: laneId -> (last user_input id seen, glowing until)
    readonly Dictionary<long, long> _lastInputSeen = new();
    readonly Dictionary<long, DateTime> _pulseUntil = new();
    bool _firstApply = true;

    /// <summary>Any pane still glowing? The window's tick uses this to know when a
    /// re-render is owed to let a pulse fade out.</summary>
    public bool AnyPulseActive => _pulseUntil.Values.Any(u => u > DateTime.UtcNow);

    public MainVm()
    {
        for (int i = 0; i < 6; i++) Slots.Add(new PaneView { Slot = i, IsEmpty = true });
    }

    /// <summary>Rebuild the whole view from a snapshot (UI thread). Returns lanes that
    /// just transitioned to blocked — the caller decides whether that warrants a toast.</summary>
    public List<string> Apply(Snapshot s)
    {
        // Pulse detection: a lane whose latest user_input id moved (or which just
        // appeared) glows for 1.5s in its own colour, so the eye can follow where a
        // routed message actually went. The very first apply is a replay of history,
        // not an arrival — nothing pulses. Poses never pulse: they are deterministic
        // fixtures, and a border that appears for 1.5s after `ui pose` is noise in
        // screenshots that exist to be compared.
        var now = DateTime.UtcNow;
        if (PoseName is null)
        {
            foreach (var p in s.Slots.OfType<PaneSnap>())
            {
                var known = _lastInputSeen.TryGetValue(p.LaneId, out var prev);
                if (!_firstApply && (!known || p.LastInputId > prev))
                    _pulseUntil[p.LaneId] = now.AddSeconds(1.5);
                _lastInputSeen[p.LaneId] = p.LastInputId;
            }
            _firstApply = false;
        }
        else _pulseUntil.Clear();

        // One tile per live lane, in order. Expanded ones go to the grid; collapsed ones
        // become chips. The palette index is the lane's POSITION IN ORDER, which is stable for
        // as long as its neighbours live — that is what §8's sticky slots were protecting, and
        // it survives without a fixed count.
        Slots.Clear();
        CollapsedLanes.Clear();
        var live = s.Slots.OfType<PaneSnap>().ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var p = live[i];
            var view = PaneView.From(p, i, pulsing: _pulseUntil.TryGetValue(p.LaneId, out var u) && u > now);
            if (p.Collapsed) CollapsedLanes.Add(view); else Slots.Add(view);
        }
        Notify(nameof(GridColumns));
        Notify(nameof(CollapsedVisibility));
        Notify(nameof(GridVisibility));
        Notify(nameof(ZeroVisibility));

        // Bands first: the feed's workspace chips borrow their colours, so a row from
        // "personal" is the same hue in the feed as the band it came from.
        FocusedWorkspace = s.FocusedWorkspace;
        FocusedWorkspaceName = s.FocusedWorkspaceName;
        BootToZero = s.BootToZero;

        Bands.Clear();
        for (int i = 0; i < s.Bands.Count; i++)
        {
            var b = s.Bands[i];
            Bands.Add(new BandView
            {
                WorkspaceId = b.WorkspaceId, Name = b.Name, Live = b.Live, Tray = b.Tray, Badge = b.Badge,
                // +1 so the FOCUSED workspace owns palette slot 0 — its own colour stays
                // stable as bands come and go, which matters because the operator's eye uses
                // it to tell "which life am I looking at".
                Index = i + 1,
                Lanes = b.Lanes.Select(l => new BandLaneView
                { LaneId = l.LaneId, Title = l.Title, Badge = l.Badge, Blocked = l.Blocked }).ToList(),
            });
        }
        Notify(nameof(BandsVisibility));

        var wsBrush = Bands.ToDictionary(b => b.Name, b => b.WorkspaceBrush, StringComparer.OrdinalIgnoreCase);
        var focusedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BandView.Palette[0]));

        var titleToBrush = Slots.Where(x => !x.IsEmpty).ToDictionary(x => x.Title, x => x.LaneBrush, StringComparer.OrdinalIgnoreCase);
        Feed.Clear();
        foreach (var f in s.Feed)
            Feed.Add(new FeedView
            {
                Id = f.Id, LaneTitle = f.LaneTitle, Body = f.Body, Acked = f.Acked, IsSystem = f.IsSystem,
                Workspace = f.Workspace, IsConcierge = f.IsConcierge,
                // The dispatcher lane holds no grid slot, so it has no slot colour to look
                // up — before this it fell through to grey, which made the system's own
                // voice the least identifiable thing in the feed.
                ChipBrush = f.IsSystem || f.IsConcierge ? Ink.System
                          : titleToBrush.TryGetValue(f.LaneTitle, out var b) ? b
                          : Brushes.Gray,
                // The workspace chip is a SECOND axis, and it has to be a different one:
                // lane colour already means which lane, so which workspace gets its own
                // palette rather than a second reading of the same six hues.
                WorkspaceBrush = f.IsConcierge ? Ink.System
                               : wsBrush.TryGetValue(f.Workspace, out var wb) ? wb
                               : focusedBrush,
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
