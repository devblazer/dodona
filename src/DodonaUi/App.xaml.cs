using System.Windows;
using Dodona;

namespace DodonaUi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? root = null, pose = null, shot = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--root" && i + 1 < e.Args.Length) root = e.Args[++i];
            if (e.Args[i] == "--pose" && i + 1 < e.Args.Length) pose = e.Args[++i];
            if (e.Args[i] == "--shot" && i + 1 < e.Args.Length) shot = e.Args[++i];
        }

        // No project named → ask which one (the normal way in). A --root is the direct
        // way in: it is what tests and shortcuts use, and it skips the picker entirely.
        Window win;
        if (root is null)
        {
            win = new PickerWindow();
            win.Show();
        }
        else
        {
            var canonical = Instance.Canonical(root);
            var id = Instance.Id(canonical);
            // Start-on-demand applies to the UI too (§13) — except with --attach, which is
            // the forensic mode: point a UI at a copied store and look, without summoning
            // anything. --pose implies it (a pose does not read the store at all).
            if (!e.Args.Contains("--attach") && pose is null) DaemonClient.Ensure(canonical, id);
            // --successor: launched by the UI we are replacing (§13). It still holds the ui
            // pipe for a moment longer, and it is waiting to hear that we came up.
            var main = new MainWindow(canonical, id, e.Args.Contains("--successor"));
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
