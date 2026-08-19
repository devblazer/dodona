using System.Windows;
using Dodona;

namespace DodonaUi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? root = null, pose = null, shot = null, workspace = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--root" && i + 1 < e.Args.Length) root = e.Args[++i];
            if (e.Args[i] == "--workspace" && i + 1 < e.Args.Length) workspace = e.Args[++i];
            if (e.Args[i] == "--pose" && i + 1 < e.Args.Length) pose = e.Args[++i];
            if (e.Args[i] == "--shot" && i + 1 < e.Args.Length) shot = e.Args[++i];
        }

        // The shell is THE front door (WORKSPACES-CONCIERGE.md §4): one window over
        // whatever is awake, boot-to-zero when nothing is, and typing is how you leave
        // that state. A bare launch lands here too — the folder picker that used to answer
        // it was removed 2026-08-18 (a workspace is named, not located, so there is no
        // folder question to ask on the way in; the picker survives only as the workspace
        // switcher the header opens). --root/--workspace stay as the direct doors tests
        // and shortcuts use.
        if (e.Args.Contains("--shell") || (root is null && workspace is null))
        {
            var shell = new MainWindow("", Instance.ShellId, "", e.Args.Contains("--successor"));
            if (e.Args.Contains("--test-window")) MakeTestWindow(shell);
            shell.Show();
            if (pose is not null) shell.ApplyPose(pose);
            if (shot is not null) Capture(shell, shot);
            return;
        }

        // --root or --workspace: the direct way into ONE workspace — what tests and
        // shortcuts use. Resolve it exactly the way the CLI does (WORKSPACES-CONCIERGE.md
        // §1): both must land on the same id or the UI would read one store while writing
        // to another's control pipe — which is why the resolver is shared source and not
        // two implementations.
        string wsId, wsLabel, primary;
        try
        {
            using var reg = new Registry();
            var ws = workspace is not null
                ? WorkspaceResolve.ByNameOrId(reg, workspace)
                : WorkspaceResolve.ForPath(reg, root!).Ws;
            if (ws is null)
            {
                MessageBox.Show($"No workspace \"{workspace}\".", "Dodona",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            wsId = ws.Id;
            wsLabel = ws.Name;
            primary = ws.Primary ?? Instance.Canonical(root ?? ".");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the workspace registry:\n{ex.Message}", "Dodona",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // Start-on-demand applies to the UI too (§13) — except with --attach, which is
        // the forensic mode: point a UI at a copied store and look, without summoning
        // anything. --pose implies it (a pose does not read the store at all).
        if (!e.Args.Contains("--attach") && pose is null) DaemonClient.Ensure(primary, wsId);
        // --successor: launched by the UI we are replacing (§13). It still holds the ui
        // pipe for a moment longer, and it is waiting to hear that we came up.
        var main = new MainWindow(primary, wsId, wsLabel, e.Args.Contains("--successor"));
        // --test-window: exists for suites and agent runs, and for one reason — the
        // operator was interrupted by test windows stealing focus while they worked.
        // Off-screen, never activated, not in the taskbar; screenshots, dumps and UIA
        // all still work, and a human never sees it.
        if (e.Args.Contains("--test-window")) MakeTestWindow(main);
        main.Show();
        if (pose is not null) main.ApplyPose(pose);

        // --shot <png>: render this window and exit.
        // Render the WINDOW, not its content: RenderTargetBitmap works in the element's
        // own coordinate space, so capturing a margined child crops it by that margin.
        if (shot is not null) Capture(main, shot);
    }

    /// <summary>--test-window: exists for suites and agent runs, and for one reason — the
    /// operator was interrupted by test windows stealing focus while they worked. Off-screen,
    /// never activated, not in the taskbar; screenshots, dumps, poses and UIA all still work,
    /// and a human never sees it.</summary>
    static void MakeTestWindow(MainWindow w)
    {
        w.IsTestWindow = true;   // so nothing downstream can pop a modal at an off-screen window
        // Qualified inside an Application, where bare `MainWindow` is Application.MainWindow.
        DodonaUi.MainWindow.TestWindow = true;      // survives a UI hot swap
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Left = -4200;
        w.Top = 0;
    }

    /// <summary>--shot &lt;png&gt;: render this window and exit. Render the WINDOW, not its
    /// content: RenderTargetBitmap works in the element's own coordinate space, so capturing
    /// a margined child crops it by that margin.</summary>
    void Capture(Window win, string shot) =>
        win.Dispatcher.BeginInvoke(new Action(() =>
        {
            Shot.Save(win, shot);
            Shutdown();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}
