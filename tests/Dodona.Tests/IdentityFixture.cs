using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Dodona;

namespace Dodona.Tests;

/// <summary>
/// THE UNIT ASSEMBLY GETS ITS OWN DODONA_HOME, AND IT IS NOT OPTIONAL.
///
/// CLAUDE.md section 5: *"Every suite must set it ... or a test run litters the operator's real
/// workspace list -- and a test of the repo-exclusivity refusal could refuse one of their real
/// repos."* Every `.ps1` suite already does, through `tests/_workspace.ps1`. The unit project
/// never had to, because until S-IDENTITY nothing in it touched `Paths`. `Registry.Create` does:
/// it calls `Directory.CreateDirectory(Paths.WorkspaceDir(id))`, so without this the first
/// registry test would write directories into `%LOCALAPPDATA%\Dodona\workspaces\`.
///
/// A `[ModuleInitializer]` rather than a fixture, deliberately. `Paths.Home` reads the
/// environment on every call and xunit runs test COLLECTIONS in parallel, so an env var set
/// inside one collection is a value another collection can observe half-set. A module
/// initializer runs once, before any type in this assembly is touched, so there is no window at
/// all -- the same reasoning as *ensure at the point of use, never look up* (CLAUDE.md section 3).
///
/// It defers to an existing `DODONA_HOME` so a suite that already isolated itself keeps its own.
/// </summary>
static class UnitHome
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        if (Environment.GetEnvironmentVariable("DODONA_HOME") is { Length: > 0 }) return;
        var home = Path.Combine(Path.GetTempPath(), "dodona-unit-home-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("DODONA_HOME", home);
        // AND IT HAS TO BE TAKEN AWAY AGAIN (issue #25). This is created once per test PROCESS and
        // nothing ever removed it -- 59 were on the machine when it was measured. There is no
        // fixture to hang a Dispose off, so the process exit is the only hook there is; `TempTree`'s
        // sweep is the backstop for the run that is killed before it fires.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TempTree.Delete(home);
    }
}

/// <summary>
/// A directory tree that exists only as a set of strings, handed to `Repos` through seam S10's
/// injected probes (`docs/testarch/seams.md`; the `Trees.Locate` shape).
///
/// THIS IS NOT A DOUBLE AND MUST NOT BECOME ONE -- and it is named `TreeSpec` rather than
/// `FakeTree` for that reason, after dev lint's double-ledger rung 1 refused the first name on
/// sight ("a test double by its NAME and carries no [Double(...)]"). The refusal was right about
/// the name and would have been wrong about the thing, which is exactly why the rung keys on
/// names: it forces the question. Plan 3.6 puts injected `Func&lt;string,bool&gt;`
/// probes in the "not doubles -- arguments" row: there is no interface, nothing implements
/// anything, and production binds `Directory.Exists` / `File.Exists` / `Repos.ListDirs` in the
/// convenience overload, so there is exactly ONE path through the code being tested. What is
/// absent is the disk, not the logic.
///
/// A repository is marked by a `.git` DIRECTORY, which is what an ordinary checkout has;
/// <see cref="RepoWithGitFile"/> marks one with a `.git` FILE, which is what a worktree and a
/// submodule have, because `LooksLikeRepo` accepts both and a tree that only ever exercised one
/// arm would let the other rot.
/// </summary>
sealed class TreeSpec
{
    readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public TreeSpec Folder(string path) { AddDir(path); return this; }

    public TreeSpec Repo(string path) { AddDir(path); AddDir(Path.Combine(path, ".git")); return this; }

    public TreeSpec RepoWithGitFile(string path)
    {
        AddDir(path);
        _files.Add(Norm(Path.Combine(path, ".git")));
        return this;
    }

    static string Norm(string p) => Path.TrimEndingDirectorySeparator(p);

    void AddDir(string path)
    {
        var p = Norm(path);
        // Ancestors are implied, exactly as they are on a real filesystem: a tree that had to
        // be declared parent-first would make a mis-declared fixture look like a real answer.
        while (p.Length > 3)
        {
            if (!_dirs.Add(p)) break;
            p = Path.GetDirectoryName(p) ?? "";
            if (p.Length == 0) break;
        }
    }

    public bool DirExists(string path) => _dirs.Contains(Norm(path));

    public bool FileExists(string path) => _files.Contains(Norm(path));

    public string[] ListDirs(string path)
    {
        var p = Norm(path);
        return _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), p, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    }

    public List<RepoRef> Under(string root, string prefix = "", int maxDepth = 2) =>
        Repos.Under(root, prefix, maxDepth, DirExists, FileExists, ListDirs);

    public List<RepoRef> Discover(params string[] memberPaths) =>
        Repos.Discover(memberPaths.Select(m => new Member(m, m.ToLowerInvariant(), true, "t")).ToList(),
                       2, DirExists, FileExists, ListDirs);

    public static string[] Names(IEnumerable<RepoRef> repos) => repos.Select(r => r.Name).ToArray();
}

/// <summary>
/// A REAL <see cref="Registry"/> over a temp file, reached through seam S5.
///
/// Plan 3.5 forbids faking this one by name, and gives the reason: repo-exclusivity is a partial
/// `UNIQUE(members.key) WHERE is_git` index that the class comment calls *"the real arbiter"*, so
/// a `HashSet` stand-in would be a DIFFERENT ENFORCEMENT MECHANISM passing a test written about
/// the index. Everything below therefore runs the real SQLite, the real migration ladder and the
/// real transactions; the only thing that moved is which file.
///
/// The folders are real too, and cheaply so: `Registry.Attach` asks `Directory.Exists` and
/// `Registry.LooksLikeRepo` asks for a `.git`, so a "repository" here is two `mkdir` calls and no
/// `git` process at all. The workspace suite pays a real `git init` plus a commit for the same
/// fixture.
/// </summary>
sealed class TempReg : IDisposable
{
    public string Root { get; }

    public TempReg()
    {
        Root = TempTree.New("dodona-reg-");
    }

    /// <summary>A plain folder: no merge token to split, so exclusivity does not apply to it.</summary>
    public string Folder(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>A folder that <see cref="Registry.LooksLikeRepo"/> answers yes about.</summary>
    public string GitFolder(string name)
    {
        var p = Folder(name);
        Directory.CreateDirectory(Path.Combine(p, ".git"));
        return p;
    }

    public Registry Open() => new Registry(Path.Combine(Root, "registry.db"));

    /// <summary>The identity the registry stores a member under, so a test compares the same
    /// thing the product compares rather than a hand-spelled path. `%TEMP%` is a short-name
    /// candidate on Windows and `Instance.Canonical` resolves it; a literal comparison would be
    /// green on one machine and red on another.</summary>
    public static string Key(string path) => Instance.Canonical(path).ToLowerInvariant();

    /// <summary>`Registry` holds a SQLite connection, and `Microsoft.Data.Sqlite` POOLS it -- so
    /// disposing the registry did not close `registry.db` and this delete failed on every run,
    /// silently, behind a comment saying that was fine (issue #25). `TempTree.Delete` clears the
    /// pool first, which is what actually releases the handle.</summary>
    public void Dispose() => TempTree.Delete(Root);
}
