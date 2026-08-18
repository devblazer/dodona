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

        // Nothing named → ask which one (the normal way in). --root or --workspace is the
        // direct way in: what tests and shortcuts use, and it skips the picker entirely.
        Window win;
        if (root is null && workspace is null)
        {
            win = new PickerWindow();
            win.Show();
        }
        else
        {
            // Resolve to a WORKSPACE exactly the way the CLI does (WORKSPACES-CONCIERGE.md
            // §1). Both must land on the same id or the UI would read one store while
            // writing to another's control pipe — which is why the resolver is shared
            // source and not two implementations.
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
            if (e.Args.Contains("--test-window"))
            {
                // Qualified: bare `MainWindow` inside an Application resolves to
                // Application.MainWindow (a Window), not to our type.
                DodonaUi.MainWindow.TestWindow = true;  // survives a UI hot swap
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.ShowActivated = false;
                main.ShowInTaskbar = false;
                main.Left = -4200;
                main.Top = 0;
            }
            win = main;
            main.Show();
            if (pose is not null) main.ApplyPose(pose);
        }

        // --shot <png>: render this window and exit. Works on any window, including the
        // picker, which has no control pipe of its own.
        // Render the WINDOW, not its content: RenderTargetBitmap works in the element's
        // own coordinate space, so capturing a margined child crops it by that margin.
        if (shot is not null)
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                Shot.Save(win, shot);
                Shutdown();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
