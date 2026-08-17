using System.IO;
using System.Text.Json;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The recent-projects list — the ONE piece of state that is per-user rather than
/// per-project. That is allowed precisely because it is not system state: it holds no
/// lanes, no claims, no queue, and nothing reads it for authority (§14's "no shared
/// mutable state" is about the registry, and the registry is still one store per root).
/// If this file is deleted the only loss is convenience: you browse for the folder again.
/// </summary>
public sealed record ProjectEntry(string Path, string LastOpened)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public bool Exists => Directory.Exists(Path);
    public bool IsGitRepo => RepoScan.IsRepoRoot(Path);
    public bool IsWorkspace => !IsGitRepo && RepoScan.FindNested(Path).Count > 0;
    public bool HasStore => File.Exists(System.IO.Path.Combine(Path, ".dodona", "store.db"));
    public bool IsLive => Instance.IsLive(Instance.Id(Path));
}

static class ProjectStore
{
    static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dodona");
    static string File_ => System.IO.Path.Combine(Dir, "projects.json");

    public static List<ProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(File_)) return new List<ProjectEntry>();
            return JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllText(File_)) ?? new List<ProjectEntry>();
        }
        catch { return new List<ProjectEntry>(); }
    }

    static void Save(List<ProjectEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a lost recents list is a cosmetic failure; never block opening a project */ }
    }

    /// <summary>Record an opened project, most recent first. Paths are canonicalized so
    /// the same repo reached by two spellings is one entry, not two.</summary>
    public static void Touch(string path)
    {
        var canonical = Instance.Canonical(path);
        var list = Load().Where(e => !Instance.Canonical(e.Path).Equals(canonical, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, new ProjectEntry(canonical, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        Save(list.Take(20).ToList());
    }

    public static void Forget(string path)
    {
        var canonical = Instance.Canonical(path);
        Save(Load().Where(e => !Instance.Canonical(e.Path).Equals(canonical, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}
