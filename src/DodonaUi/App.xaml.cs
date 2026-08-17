using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace DodonaUi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string root = Environment.CurrentDirectory;
        string? pose = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--root" && i + 1 < e.Args.Length) root = e.Args[++i];
            if (e.Args[i] == "--pose" && i + 1 < e.Args.Length) pose = e.Args[++i];
        }
        root = System.IO.Path.GetFullPath(root);

        // Same instance-id derivation as the daemon (§14): pipes scope to the root.
        var canonical = root.TrimEnd('\\', '/').ToLowerInvariant();
        var instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();

        var win = new MainWindow(root, instanceId);
        MainWindow = win;
        win.Show();
        if (pose is not null) win.ApplyPose(pose);
    }
}
