using System.Diagnostics;

namespace Dodona;

/// <summary>Thin git runner. Git is the truth for git (design §12) — the store only
/// caches what these calls report.</summary>
static class Git
{
    public static (int Code, string Out) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = Task.Run(() => p.StandardError.ReadToEnd());
        var so = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var se = errTask.Result;
        return (p.ExitCode, (so + se).Trim());
    }

    /// <summary>HEAD sha of a ref; throws if the ref does not resolve.</summary>
    public static string Sha(string repo, string @ref)
    {
        var (code, output) = Run(repo, "rev-parse", @ref);
        if (code != 0) throw new InvalidOperationException($"rev-parse {@ref}: {output}");
        return output;
    }
}
