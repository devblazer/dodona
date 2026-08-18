using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The workspace switcher. It lists WORKSPACES — named identities from the registry
/// (WORKSPACES-CONCIERGE.md §1) — and never folders: locations are the router's business,
/// attached by the concierge as work arrives. Picking one wakes its daemon and hands it
/// the grid in the shell, the exact path a band click takes (FocusWorkspace), so the
/// one-window model survives the pick.
///
/// This window used to be the folder picker — recents, Browse…, repo statuses — and
/// opening from it spawned a second old-style window per project. Removed 2026-08-18 on
/// the operator's direction: the app's only user-facing identity is the workspace name;
/// a Browse dialog in the front door contradicted the entire workspace design. Creating
/// a repository, attaching a folder, creating a workspace: all of that is typed, and the
/// router does it (concierge ladder, §5). Do not reintroduce folder UI here.
/// </summary>
public partial class PickerWindow : Window
{
    public sealed class Row
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public required bool Awake { get; init; }
        internal string? Primary { get; init; }
        public string Status => Awake ? "awake" : "asleep";
        public Brush StatusBrush => Awake
            ? new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
    }

    public PickerWindow()
    {
        InitializeComponent();
        _ = RefreshAsync();
    }

    /// <summary>Registry read + one liveness probe per workspace, off the UI thread:
    /// Probe blocks its full 300ms per asleep workspace, and a handful of those would
    /// freeze the window exactly the way Open() used to (2026-08-18's "opening a project
    /// froze").</summary>
    async Task RefreshAsync()
    {
        var rows = await Task.Run(() =>
        {
            using var reg = new Registry();
            return reg.All().Select(w => new Row
            {
                Id = w.Id,
                Name = w.Name,
                Detail = w.Aliases.Count > 0 ? $"also answers to {string.Join(", ", w.Aliases)}" : "",
                Awake = DaemonClient.Probe(w.Id),
                Primary = w.Primary,
            }).OrderByDescending(r => r.Awake).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });
        if (!IsLoaded) return;                      // closed while we probed
        WsList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) WsList.SelectedIndex = 0;
    }

    Row? Selected => WsList.SelectedItem as Row;

    void WsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenBtn.IsEnabled = Selected is not null;

    void WsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is Row row) Open(row);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is Row row) Open(row);
    }

    void Warn(string? text)
    {
        Warning.Text = text ?? "";
        Warning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Wake the workspace and give it the grid. The daemon start runs off the UI
    /// thread (Ensure legitimately takes seconds and blocks 20s when broken); the focus
    /// then RETRIES briefly, because the band only appears once the shell's poller notices
    /// the daemon is awake — a condition, not a person (CLAUDE.md §0.1).</summary>
    async void Open(Row row)
    {
        IsEnabled = false;
        Warn($"Waking {row.Name}…");
        // Primary is only a working directory for the daemon process; the daemon resolves
        // everything else by workspace id. Empty is fine — Ensure falls back to temp.
        var reason = await Task.Run(() => DaemonClient.Ensure(row.Primary ?? "", row.Id));
        if (!IsLoaded) return;                      // closed while we waited
        if (reason is not null) { IsEnabled = true; Warn(reason); return; }

        // One window over N workspaces (§6): reuse whatever MainWindow exists — shell or
        // --root-opened, same class, same bands — and only conjure a shell when there is
        // none at all. Never a second window per workspace; that model is dead.
        var shell = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (shell is null)
        {
            shell = new MainWindow("", Instance.ShellId, "");
            shell.Show();
        }

        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            if (!shell.FocusWorkspace(row.Id).StartsWith("error"))
            {
                shell.Activate();
                Close();
                return;
            }
            await Task.Delay(300);
            if (!IsLoaded) return;                  // closed while we retried
        }
        IsEnabled = true;
        Warn($"{row.Name} is awake but never showed up as a band — check the feed.");
    }
}
