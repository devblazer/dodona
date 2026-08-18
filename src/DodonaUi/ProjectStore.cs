using System.IO;
using System.Text.Json;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The recent-projects list — the ONE piece of state that is per-user rather than
/// per-project. That is allowed precisely because it is not system state: it holds no
/// lanes, no claims, no queue, and nothing reads it for authority (§14's "no shared
/// mutable state" is about a workspace's store, and that is still one store per workspace).
/// If this file is deleted the only loss is convenience: you browse for the folder again.
/// </summary>
public sealed record ProjectEntry(string Path, string LastOpened)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public bool Exists => Directory.Exists(Path);
    public bool IsGitRepo => RepoScan.IsRepoRoot(Path);
    public bool HoldsRepos => !IsGitRepo && RepoScan.FindNested(Path).Count > 0;

    /// <summary>Which workspace owns this folder, if any — filled in by
    /// <see cref="ProjectStore.Load"/> from ONE registry read. Deliberately not a property
    /// that resolves on access: the picker binds several of these per row and would
    /// otherwise open a SQLite connection per row per refresh.</summary>
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }

    public bool HasStore => WorkspaceId is { } id && File.Exists(Paths.Store(id));
    public bool IsLive => WorkspaceId is { } id && Instance.IsLive(id);
}

static class ProjectStore
{
    static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dodona");
    static string File_ => System.IO.Path.Combine(Dir, "projects.json");

    public static List<ProjectEntry> Load()
    {
        List<ProjectEntry> list;
        try
        {
            if (!File.Exists(File_)) return new List<ProjectEntry>();
            list = JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllText(File_)) ?? new List<ProjectEntry>();
        }
        catch { return new List<ProjectEntry>(); }

        // One registry read for the whole list: a recent folder's store, liveness and
        // status now all hang off which WORKSPACE owns it, and that answer is not on disk
        // beside the folder any more. A registry that will not open leaves every row
        // workspace-less, which renders as "new" — honest, and never an empty picker.
        try
        {
            using var reg = new Registry();
            foreach (var e in list)
                if (reg.Owner(e.Path) is { } owner)
                {
                    e.WorkspaceId = owner.Ws.Id;
                    e.WorkspaceName = owner.Ws.Name;
                }
        }
        catch { }
        return list;
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
