using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The way in. Dodona is an application you run, not a command you type at a project —
/// so the first thing it does is ask which project. Everything below the pick is
/// unchanged: one canonical root, one daemon, one store, one merge queue (§14). Opening
/// a second project opens a second window with its own daemon; that is the whole of
/// multi-project support, and it works because instances share nothing.
/// </summary>
public partial class PickerWindow : Window
{
    public sealed class Row
    {
        public required ProjectEntry Entry { get; init; }
        public string Name => Entry.Name;
        public string Path => Entry.Path;
        public string Detail => Entry.Exists ? $"{Entry.Path}    ·    opened {Entry.LastOpened}" : $"{Entry.Path}    ·    MISSING";
        public string Status => !Entry.Exists ? "gone" : Entry.IsLive ? "running" : !Entry.IsGitRepo ? "not git" : Entry.HasStore ? "idle" : "new";
        public Brush StatusBrush => Status switch
        {
            "running" => new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
            "idle" => new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)),
            "new" => new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            _ => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
        };
        public Brush NameBrush => Entry.Exists ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
    }

    public PickerWindow()
    {
        InitializeComponent();
        Refresh();
    }

    void Refresh()
    {
        var rows = ProjectStore.Load().Select(e => new Row { Entry = e }).ToList();
        Recents.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) Recents.SelectedIndex = 0;
    }

    Row? Selected => Recents.SelectedItem as Row;

    void Recents_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Selected;
        OpenBtn.IsEnabled = row is not null && row.Entry.Exists;
        ForgetBtn.IsEnabled = row is not null;
        Warn(row is null ? null
            : !row.Entry.Exists ? "That folder no longer exists."
            : !row.Entry.IsGitRepo ? "Not a git repository. Dodona needs git — tickets are branches and lanes are worktrees."
            : null);
    }

    void Warn(string? text)
    {
        Warning.Text = text ?? "";
        Warning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a project root" };
        if (dlg.ShowDialog(this) != true) return;
        Open(dlg.FolderName);
    }

    void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        ProjectStore.Forget(row.Path);
        Refresh();
    }

    void Recents_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is Row row && row.Entry.Exists) Open(row.Path);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is Row row) Open(row.Path);
    }

    void Open(string path)
    {
        if (!Directory.Exists(path)) { Warn("That folder does not exist."); return; }
        var root = Instance.Canonical(path);
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            Warn($"{root} is not a git repository. Run `git init` there first — Dodona's tickets are branches and its lanes are worktrees.");
            return;
        }

        // Already open? Show that window instead of racing it for the UI pipe.
        var id = Instance.Id(root);
        if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.InstanceId == id) is MainWindow open)
        {
            open.Activate();
            Close();
            return;
        }

        IsEnabled = false;
        Warn($"Starting {Path.GetFileName(root)}…");
        var reason = DaemonClient.Ensure(root, id);
        IsEnabled = true;
        if (reason is not null) { Warn(reason); return; }

        ProjectStore.Touch(root);
        new MainWindow(root, id).Show();
        Close();
    }
}
