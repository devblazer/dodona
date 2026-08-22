using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Dodona;

/// <summary>A repo or a bare folder attached to a workspace. <see cref="Key"/> is the
/// lowercased canonical path — identity; <see cref="Path"/> keeps the OS's own casing for
/// display. <see cref="IsGit"/> decides whether the exclusivity invariant applies: a repo
/// has a merge token to split, a notes folder does not (§3).</summary>
sealed record Member(string Path, string Key, bool IsGit, string AddedTs);

/// <summary>
/// A named, durable session group (§1). It owns everything a root-anchored instance owns
/// today — the daemon, the store, the brain, the grid, the lanes, the tickets, the merge
/// tokens of its member repos — and it is anchored by a NAME rather than a folder.
/// </summary>
sealed record Workspace(string Id, string Name, string CreatedTs, List<Member> Members, List<string> Aliases)
{
    /// <summary>The primary member: first attached, and the one that stands in wherever
    /// the old code said "the project root" — where a lane spawns by default, which
    /// dodona.json the workspace falls back to, what `repo-status` reports on. For a
    /// one-member workspace it IS the root, which is what makes the degenerate case
    /// identical to today. (Decision recorded in WORKSPACES-CONCIERGE.md §2.1.)</summary>
    public string? Primary => Members.Count > 0 ? Members[0].Path : null;

    public string Label => $"{Name} ({Id})";
}

/// <summary>
/// The workspace registry (docs/WORKSPACES-CONCIERGE.md §1/§3). Names, ids, aliases,
/// members, and repo-to-workspace ownership — the machine-global memory of which
/// workspaces exist and what belongs to them.
///
/// **The invariant this class exists to enforce.** Path-derived identity was never
/// aesthetic: two spellings of one repo hashed to one instance id, one mutex, one merge
/// token, and Instance.cs's own doc comment calls two daemons over one main "exactly the
/// race this system exists to prevent". Named workspaces delete that structural
/// enforcement, so it moves up a level and becomes registry law:
///
///   A GIT REPO BELONGS TO AT MOST ONE WORKSPACE AT A TIME.
///
/// Enforced three ways, deliberately overlapping (CLAUDE.md §0 — enforcement in code beats
/// an instruction, and the strongest form is the one that cannot be skipped):
///   1. a partial UNIQUE INDEX on members(key) WHERE is_git — structurally impossible,
///      even for a future caller that forgets to ask;
///   2. an explicit check in <see cref="Attach"/> that refuses LOUDLY and prints the move
///      command, because reassignment is legitimate and silent double-ownership never is;
///   3. <see cref="RepoConflict"/>, called at ticket-create — the moment a merge token is
///      actually at stake. It catches the one hole the other two cannot: a BARE FOLDER
///      legitimately attached to two workspaces (exempt, harmless) that someone later runs
///      `git init` in. Layer 1 was satisfied when the row was written; only a check at the
///      point of use can notice the ground moved. Same two-layer shape as the claim gate
///      and the merge-time backstop (§6).
///
/// Writes are one transaction each and the partial index is the real arbiter — no
/// check-then-write race survives it. In M2 every writer is a CLI process; from M3 the
/// concierge daemon is the sole writer and these same methods are what it calls.
/// </summary>
sealed class Registry : IDisposable
{
    readonly SqliteConnection _db;

    /// <summary>The live registry: the one machine-wide file under DODONA_HOME. Production
    /// reaches the constructor below through THIS line and no other, which is the property
    /// that stops an overload becoming a second implementation (the `Trees.Locate` shape,
    /// docs/testarch/seams.md).</summary>
    public Registry() : this(Paths.Registry) { }

    /// <summary>The same registry over a NAMED file - seam S5 (docs/testarch/seams.md), and
    /// what it buys is two isolated registries in ONE process. That is not a convenience: it
    /// is what `the_registry_under_dodona_home_is_the_live_one` actually asserts - open the
    /// path the binary reported, in a second connection, and find the workspace the first one
    /// wrote. A `HashSet` stand-in could never answer it, because the thing being asked about
    /// is the file (plan 3.5: never fake `Registry`; the partial unique index is the real
    /// arbiter, and a fake enforces something else).
    ///
    /// Behaviour-neutral: `Path.GetDirectoryName(Paths.Registry)` IS `Paths.ConciergeDir`, so
    /// the parameterless path creates exactly the directory it always did.</summary>
    public Registry(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? Paths.ConciergeDir);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        // Several CLI processes can write in M2 (the concierge is not built until M3), and
        // a suite that attaches members in a loop would otherwise meet SQLITE_BUSY.
        Exec("PRAGMA busy_timeout=5000;");
        Migrate();
    }

    void Migrate()
    {
        long v;
        using (var c = _db.CreateCommand()) { c.CommandText = "PRAGMA user_version;"; v = (long)c.ExecuteScalar()!; }
        if (v < 1)
            Exec("""
                CREATE TABLE workspaces(
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE members(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_id TEXT NOT NULL,
                    path TEXT NOT NULL,                 -- canonical, OS casing (display)
                    key TEXT NOT NULL,                  -- lowercased canonical (identity)
                    is_git INTEGER NOT NULL,
                    added_ts TEXT NOT NULL
                );
                CREATE UNIQUE INDEX members_key ON members(workspace_id, key);
                -- The invariant, made structural. Partial index: bare folders are exempt
                -- (no merge token exists to split, and a shared notes folder in two
                -- workspaces harms nothing), git repos are not.
                CREATE UNIQUE INDEX members_repo_exclusive ON members(key) WHERE is_git = 1;
                CREATE TABLE aliases(
                    alias TEXT PRIMARY KEY,             -- lowercased
                    display TEXT NOT NULL,
                    workspace_id TEXT NOT NULL,
                    created_ts TEXT NOT NULL
                );
                -- Registry history. Same reason the store has one: if a state change
                -- happened with no row naming why, that is a bug (DEBUGGING.md).
                CREATE TABLE events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    workspace_id TEXT,
                    detail TEXT
                );
                PRAGMA user_version = 1;
                """);
        // SCHEMA 2 (docs/LOCATIONS-PLAN.md Phase 3, D-L5): a spoken handle may name a PROJECT
        // and not only a workspace. `members` was already every project ever attached — the
        // router's "memory of every project ever" needed no new table, only a way to record
        // what the operator CALLS one. So `aliases` grows one nullable column instead of a
        // parallel `places` table: fewer owned things is this project's whole failure mode.
        //
        // NULL means what it always meant — the alias names the workspace. A value is the
        // `members.key` of one project inside it, and such an alias resolves BOTH: the
        // workspace at the concierge's rung 1 (asking for a project you can name should wake
        // the workspace that holds it) and the project at the router's rung 3.
        if (v < 2)
            Exec("""
                ALTER TABLE aliases ADD COLUMN member_key TEXT;
                PRAGMA user_version = 2;
                """);
    }

    void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    static string Now() => DateTime.UtcNow.ToString("o");

    public void Event(string kind, string? wsId, string? detail)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO events(ts, kind, workspace_id, detail) VALUES ($ts,$k,$w,$d);";
        c.Parameters.AddWithValue("$ts", Now());
        c.Parameters.AddWithValue("$k", kind);
        c.Parameters.AddWithValue("$w", (object?)wsId ?? DBNull.Value);
        c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ reading

    public List<Workspace> All()
    {
        var rows = new List<(string Id, string Name, string Ts)>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT id, name, created_ts FROM workspaces ORDER BY created_ts, id;";
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return rows.Select(x => new Workspace(x.Id, x.Name, x.Ts, MembersOf(x.Id), AliasesOf(x.Id))).ToList();
    }

    public Workspace? ById(string id)
    {
        var all = All();
        return all.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    List<Member> MembersOf(string wsId)
    {
        var list = new List<Member>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT path, key, is_git, added_ts FROM members WHERE workspace_id = $w ORDER BY id;";
        c.Parameters.AddWithValue("$w", wsId);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new Member(r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, r.GetString(3)));
        return list;
    }

    List<string> AliasesOf(string wsId)
    {
        var list = new List<string>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT display FROM aliases WHERE workspace_id = $w ORDER BY created_ts;";
        c.Parameters.AddWithValue("$w", wsId);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// The spoken handles taught for the PROJECTS of one workspace: `(alias, members.key)`
    /// (schema 2, D-L5). This is the router's rung-3 memory, and it is a READ — the daemon
    /// never writes here (the registry is machine-wide and the concierge owns learning into
    /// it; an operator-explicit `dodona project-alias` is what writes, exactly as
    /// `workspace-create` does).
    ///
    /// Longest first is the caller's job, not this one's: two ladders order by different
    /// things and a helper that pre-sorted would hide which.
    /// </summary>
    public List<(string Alias, string Key)> ProjectHandles(string wsId)
    {
        var list = new List<(string, string)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT display, member_key FROM aliases " +
                        "WHERE workspace_id = $w AND member_key IS NOT NULL ORDER BY created_ts;";
        c.Parameters.AddWithValue("$w", wsId);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// Teach a handle for one PROJECT of this workspace (D-L5). The path must already be a
    /// member: an alias for a folder nobody attached is a memory of somewhere no lane may open,
    /// and refusing here is the same refusal <see cref="Daemon.TryProject"/> makes at the spawn
    /// site — better said now than discovered when a sentence resolves to it.
    /// </summary>
    public bool AddProjectAlias(string id, string projectPath, string alias, out string error)
    {
        error = "";
        var clean = alias.Trim();
        if (clean.Length == 0) { error = "an alias needs text"; return false; }
        var ws = ById(id);
        if (ws is null) { error = $"no workspace {id}"; return false; }
        var key = Instance.Canonical(projectPath).ToLowerInvariant();
        var m = ws.Members.FirstOrDefault(x => x.Key == key);
        if (m is null)
        {
            error = $"{projectPath} is not a project of {ws.Label} " +
                    $"(projects here: {(ws.Members.Count == 0 ? "none" : string.Join(", ", ws.Members.Select(x => x.Path)))})";
            return false;
        }
        var clash = ByNameOrId(clean);
        if (clash is not null && !clash.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        { error = $"\"{clean}\" already resolves to {clash.Label}"; return false; }
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO aliases(alias, display, workspace_id, created_ts, member_key) " +
                        "VALUES ($a,$d,$w,$t,$k) " +
                        "ON CONFLICT(alias) DO UPDATE SET workspace_id = $w, display = $d, member_key = $k;";
        c.Parameters.AddWithValue("$a", clean.ToLowerInvariant());
        c.Parameters.AddWithValue("$d", clean);
        c.Parameters.AddWithValue("$w", id);
        c.Parameters.AddWithValue("$t", Now());
        c.Parameters.AddWithValue("$k", key);
        c.ExecuteNonQuery();
        Event("project_alias_added", id, $"{clean} -> {m.Path}");
        return true;
    }

    /// <summary>Registry rung 1 (§4): exact id, exact name, then alias — code, no model.
    /// This is the steady-state path and it must never cost a token.</summary>
    public Workspace? ByNameOrId(string nameOrId)
    {
        var all = All();
        var want = nameOrId.Trim();
        return all.FirstOrDefault(w => w.Id.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Aliases.Any(a => a.Equals(want, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Which workspace owns this path — the member itself, or the nearest member
    /// ANCESTOR of it. Longest match wins, so a repo attached inside an also-attached
    /// folder resolves to the repo. Ancestor matching is why a client command works from a
    /// subdirectory now: the store used to be found by looking for `&lt;cwd&gt;\.dodona`,
    /// and is now found by asking the registry who owns the path.</summary>
    public (Workspace Ws, Member M)? Owner(string anyPath)
    {
        var key = Instance.Canonical(anyPath).ToLowerInvariant();
        (Workspace, Member)? best = null;
        int bestLen = -1;
        foreach (var w in All())
            foreach (var m in w.Members)
            {
                if (!(key == m.Key || key.StartsWith(m.Key + "\\", StringComparison.Ordinal))) continue;
                if (m.Key.Length <= bestLen) continue;
                bestLen = m.Key.Length;
                best = (w, m);
            }
        return best;
    }

    /// <summary>The exclusivity backstop, asked where a merge token is actually at stake
    /// (layer 3 above). Returns the OTHER workspace holding this repo path, or null.</summary>
    public Workspace? RepoConflict(string repoPath, string myWorkspaceId)
    {
        var key = Instance.Canonical(repoPath).ToLowerInvariant();
        return All().FirstOrDefault(w =>
            !w.Id.Equals(myWorkspaceId, StringComparison.OrdinalIgnoreCase) &&
            w.Members.Any(m => m.Key == key));
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Is this folder a git repository? The same filesystem-only test Repos uses
    /// — a `.git` directory, or a `.git` FILE, which is what a worktree and a submodule
    /// have.</summary>
    public static bool LooksLikeRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// The workspace id: a readable slug plus four hex. Generated, NOT derived from the
    /// name — that is the whole point of §1. Renaming "personal" to "home" re-derives
    /// nothing and orphans nothing, because pipes, the OS mutex and the store directory
    /// key off this id and never off the name.
    ///
    /// The slug half is cosmetic and may go stale after a rename. That is accepted and
    /// worth stating: an id that is merely a readable handle can drift from the display
    /// name, whereas an id that MEANT the name would have to move a store directory to
    /// change one. DEBUGGING.md's "short enough to read in a pipe name" still holds.
    /// </summary>
    public static string NewId(string name, Func<string, bool> taken)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        if (slug.Length == 0) slug = "ws";
        for (int i = 0; i < 100; i++)
        {
            var id = $"{slug}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
            if (!taken(id)) return id;
        }
        // Vanishingly unlikely, but a collision loop that gave up quietly would hand back
        // a duplicate id — which is two daemons over one store.
        throw new InvalidOperationException("could not generate a free workspace id");
    }

    public Workspace Create(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0) throw new ArgumentException("a workspace needs a name");
        var id = NewId(clean, candidate => ById(candidate) is not null || Directory.Exists(Paths.WorkspaceDir(candidate)));
        var ts = Now();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "INSERT INTO workspaces(id, name, created_ts) VALUES ($i,$n,$t);";
            c.Parameters.AddWithValue("$i", id);
            c.Parameters.AddWithValue("$n", clean);
            c.Parameters.AddWithValue("$t", ts);
            c.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Paths.WorkspaceDir(id));
        Event("workspace_created", id, $"name={clean}");
        return new Workspace(id, clean, ts, new List<Member>(), new List<string>());
    }

    public bool Rename(string id, string newName, out string error)
    {
        error = "";
        var clean = newName.Trim();
        if (clean.Length == 0) { error = "a workspace needs a name"; return false; }
        var clash = ByNameOrId(clean);
        if (clash is not null && !clash.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        { error = $"\"{clean}\" is already {clash.Label}"; return false; }
        var was = ById(id)?.Name ?? "?";
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE workspaces SET name = $n WHERE id = $i;";
        c.Parameters.AddWithValue("$n", clean);
        c.Parameters.AddWithValue("$i", id);
        if (c.ExecuteNonQuery() == 0) { error = $"no workspace {id}"; return false; }
        Event("workspace_renamed", id, $"{was} -> {clean}");
        return true;
    }

    /// <summary>Teach the registry a name (§4 rung 4: every clarification the operator
    /// gives becomes an alias, so asking decays toward not asking).</summary>
    public bool AddAlias(string id, string alias, out string error)
    {
        error = "";
        var clean = alias.Trim();
        if (clean.Length == 0) { error = "an alias needs text"; return false; }
        var clash = ByNameOrId(clean);
        if (clash is not null && !clash.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        { error = $"\"{clean}\" already resolves to {clash.Label}"; return false; }
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO aliases(alias, display, workspace_id, created_ts) VALUES ($a,$d,$w,$t) " +
                        "ON CONFLICT(alias) DO UPDATE SET workspace_id = $w, display = $d;";
        c.Parameters.AddWithValue("$a", clean.ToLowerInvariant());
        c.Parameters.AddWithValue("$d", clean);
        c.Parameters.AddWithValue("$w", id);
        c.Parameters.AddWithValue("$t", Now());
        c.ExecuteNonQuery();
        Event("alias_added", id, clean);
        return true;
    }

    /// <summary>
    /// Attach a member. THE enforcement point for repo-exclusivity (§3): a git repo owned
    /// by another workspace is refused loudly and the error carries the move command,
    /// because reassignment is legitimate and silent double-ownership never is.
    /// </summary>
    public bool Attach(string id, string path, out string error)
    {
        error = "";
        var full = Instance.Canonical(path);
        if (!Directory.Exists(full)) { error = $"no such folder: {path}"; return false; }
        var key = full.ToLowerInvariant();
        var isGit = LooksLikeRepo(full);

        var ws = ById(id);
        if (ws is null) { error = $"no workspace {id}"; return false; }
        if (ws.Members.Any(m => m.Key == key)) { error = $"{full} is already a member of {ws.Label}"; return false; }

        if (isGit)
        {
            var other = All().FirstOrDefault(w => !w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                                                  && w.Members.Any(m => m.Key == key));
            if (other is not null)
            {
                error = $"{full} already belongs to workspace \"{other.Name}\" ({other.Id})\n" +
                        "       a repo belongs to at most ONE workspace at a time: landing is a fast-forward of one\n" +
                        "       branch onto one main, and two workspaces over one repo is two merge tokens over one main\n" +
                        $"       move it:  dodona workspace-move --member \"{full}\" --workspace \"{ws.Name}\"";
                Event("attach_refused", id, $"{full} owned by {other.Id}");
                return false;
            }
        }

        try { InsertMember(id, full, key, isGit); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The partial index fired — layer 1 caught what layer 2 raced past.
            error = $"{full} already belongs to another workspace (registry refused: repo exclusivity)";
            Event("attach_refused", id, $"{full} unique-index");
            return false;
        }
        Event("member_attached", id, $"{full} git={isGit}");
        return true;
    }

    void InsertMember(string wsId, string path, string key, bool isGit)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO members(workspace_id, path, key, is_git, added_ts) VALUES ($w,$p,$k,$g,$t);";
        c.Parameters.AddWithValue("$w", wsId);
        c.Parameters.AddWithValue("$p", path);
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$g", isGit ? 1 : 0);
        c.Parameters.AddWithValue("$t", Now());
        c.ExecuteNonQuery();
    }

    /// <summary>Reassignment — the affordance the refusal above points at. One
    /// transaction: the repo is never in two workspaces and never in none.</summary>
    public bool Move(string toId, string path, out string error)
    {
        error = "";
        var full = Instance.Canonical(path);
        var key = full.ToLowerInvariant();
        var to = ById(toId);
        if (to is null) { error = $"no workspace {toId}"; return false; }
        var from = All().FirstOrDefault(w => w.Members.Any(m => m.Key == key));
        if (from is null) { error = $"{full} is not a member of any workspace — attach it instead"; return false; }
        if (from.Id.Equals(toId, StringComparison.OrdinalIgnoreCase)) { error = $"{full} is already in {to.Label}"; return false; }

        var isGit = LooksLikeRepo(full);
        using var tx = _db.BeginTransaction();
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM members WHERE workspace_id = $w AND key = $k;";
            del.Parameters.AddWithValue("$w", from.Id);
            del.Parameters.AddWithValue("$k", key);
            del.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO members(workspace_id, path, key, is_git, added_ts) VALUES ($w,$p,$k,$g,$t);";
            ins.Parameters.AddWithValue("$w", toId);
            ins.Parameters.AddWithValue("$p", full);
            ins.Parameters.AddWithValue("$k", key);
            ins.Parameters.AddWithValue("$g", isGit ? 1 : 0);
            ins.Parameters.AddWithValue("$t", Now());
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        Event("member_moved", toId, $"{full} from {from.Id}");
        return true;
    }

    public bool Detach(string id, string path, out string error)
    {
        error = "";
        var key = Instance.Canonical(path).ToLowerInvariant();
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM members WHERE workspace_id = $w AND key = $k;";
        c.Parameters.AddWithValue("$w", id);
        c.Parameters.AddWithValue("$k", key);
        if (c.ExecuteNonQuery() == 0) { error = $"{path} is not a member of workspace {id}"; return false; }
        Event("member_detached", id, key);
        return true;
    }

    /// <summary>Forget a workspace: the registry rows go, the store directory does NOT.
    /// Nothing in this system deletes a transcript (§12), and "undo a workspace I made by
    /// accident" must never be able to mean "delete six lanes of history".</summary>
    public bool Forget(string id, out string error)
    {
        error = "";
        var ws = ById(id);
        if (ws is null) { error = $"no workspace {id}"; return false; }
        using var tx = _db.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM members WHERE workspace_id = $i;",
                                    "DELETE FROM aliases WHERE workspace_id = $i;",
                                    "DELETE FROM workspaces WHERE id = $i;" })
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            c.Parameters.AddWithValue("$i", id);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        Event("workspace_forgotten", id, $"name={ws.Name} store kept at {Paths.WorkspaceDir(id)}");
        return true;
    }

    public void Dispose() => _db.Dispose();
}
