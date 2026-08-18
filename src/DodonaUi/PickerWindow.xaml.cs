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
        public string Status => !Entry.Exists ? "gone"
            : Entry.IsLive ? "running"
            : Entry.HoldsRepos ? "holds repos"
            : !Entry.IsGitRepo ? "no repo"
            : Entry.HasStore ? "idle" : "new";
        public Brush StatusBrush => Status switch
        {
            "running" => new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
            "idle" => new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)),
            "new" => new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            "holds repos" => new SolidColorBrush(Color.FromRgb(0xBA, 0x68, 0xC8)),
            "no repo" => new SolidColorBrush(Color.FromRgb(0xE0, 0xA9, 0x6D)),   // a note, not an error
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
        // Offered when there is no repo to use — but never inside someone else's repo,
        // where a nested one would be a mess to undo and never what was meant.
        InitBtn.Visibility = row is not null && row.Entry.Exists
            && (!RepoScan.IsRepoRoot(row.Path) || !RepoScan.LooksCommitted(row.Path))
            && RepoScan.EnclosingRepo(row.Path) is null
            ? Visibility.Visible : Visibility.Collapsed;
        Warn(row is null ? null : Describe(row.Path));
    }

    /// <summary>Say what this folder is and what will happen — never bar the door. Git is
    /// needed when a ticket cuts a branch and a worktree, which may be much later than
    /// now, and a workspace whose repos live one level down is a normal shape.</summary>
    static string? Describe(string path)
    {
        if (!Directory.Exists(path)) return "That folder no longer exists.";
        if (RepoScan.IsRepoRoot(path))
            return RepoScan.LooksCommitted(path) ? null
                : "This repository has no commits yet, so there is no branch to work from. Create repository… makes the first one.";

        if (RepoScan.EnclosingRepo(path) is string outer)
            return $"This is inside the repository {outer}. Open that as the project instead — " +
                   "claims, branches and the merge queue all belong to the repository, not to a folder within it.";

        var nested = RepoScan.FindNested(path);
        if (nested.Count > 0)
            return $"A workspace: not a repository itself, but it contains {nested.Count} " +
                   $"({string.Join(", ", nested.Take(4).Select(r => Path.GetRelativePath(path, r)))}" +
                   $"{(nested.Count > 4 ? ", …" : "")}). Open it to run lanes across the whole workspace now; " +
                   "tickets branch a repository, so today they need one of those opened as the project.";

        return "No git repository here yet — that is fine. Open it and start working; " +
               "git is needed when a ticket cuts a branch, and Create repository… does that whenever you like.";
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

    /// <summary>Create the repository the selected folder is missing. The daemon does the
    /// git — git is the daemon's business (§12) — so this starts one first.</summary>
    void Init_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row || !row.Entry.Exists) return;
        var root = Instance.Canonical(row.Path);
        var hasFiles = RepoScan.HasContent(root);

        var question = hasFiles
            ? $"Create a git repository in\n{root}\n\nThe files already there will be committed as the initial commit, " +
              "and .dodona/ will be added to .gitignore.\n\nChoose No to create the repository with an empty first " +
              "commit instead, leaving those files untracked."
            : $"Create a git repository in\n{root}\n\nIt will start on 'main' with an empty initial commit, " +
              "and .dodona/ will be added to .gitignore.";

        var answer = MessageBox.Show(question, "Dodona",
            hasFiles ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        var adopt = hasFiles && answer == MessageBoxResult.Yes;

        IsEnabled = false;
        Warn("Creating the repository…");
        var ws = ResolveWorkspace(root);
        if (ws is null) { IsEnabled = true; return; }
        var reason = DaemonClient.Ensure(ws.Primary ?? root, ws.Id);
        var result = reason ?? DaemonClient.Send(ws.Id, new { cmd = "repo-init", adopt });
        IsEnabled = true;
        Warn(result);
        Refresh();
    }

    /// <summary>The folder the operator picked, turned into the workspace that owns it —
    /// creating one (named after the folder, that folder as sole member) if nobody does.
    /// This is the same resolver the CLI and the daemon use; picking a folder is now a way
    /// of NAMING a workspace rather than of locating one (WORKSPACES-CONCIERGE.md §1).</summary>
    Workspace? ResolveWorkspace(string root)
    {
        try
        {
            using var reg = new Registry();
            return WorkspaceResolve.ForPath(reg, root).Ws;
        }
        catch (Exception ex) { Warn(ex.Message); return null; }
    }

    void Open(string path)
    {
        if (!Directory.Exists(path)) { Warn("That folder does not exist."); return; }
        var root = Instance.Canonical(path);
        var ws = ResolveWorkspace(root);
        if (ws is null) return;

        // Already open? Show that window instead of racing it for the UI pipe.
        if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.InstanceId == ws.Id) is MainWindow open)
        {
            open.Activate();
            Close();
            return;
        }

        IsEnabled = false;
        Warn($"Starting {ws.Name}…");
        var reason = DaemonClient.Ensure(ws.Primary ?? root, ws.Id);
        IsEnabled = true;
        if (reason is not null) { Warn(reason); return; }

        ProjectStore.Touch(root);
        new MainWindow(ws.Primary ?? root, ws.Id, ws.Name).Show();
        Close();
    }
}
