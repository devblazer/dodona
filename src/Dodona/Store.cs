using System.Linq;
using Microsoft.Data.Sqlite;

namespace Dodona;

/// <summary>
/// The registry (design §12). One writer — the daemon — behind one connection and one
/// lock. WAL + synchronous=FULL. Every state change is one transaction: claim-check and
/// claim-insert atomically (§6), token grant and land atomically (§7). pane_events
/// dedupes shim redelivery via UNIQUE(lane_id, seq).
/// </summary>
sealed class Store : IDisposable, ILaneSink
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    /// <summary>Where this store was copied to before a migration ran, or null when nothing
    /// migrated. The daemon announces it, because the ONLY undo for a half-applied migration
    /// is that file — and the swap path has backed up since §14 was revised while a plain
    /// COLD START (stop-daemon, then start a newer build) migrated with no copy at all. Same
    /// migration, same irreversibility, no backup: the gap was invisible because the loud
    /// path was the one that had been thought about.</summary>
    public string? PreMigrationBackup { get; private set; }
    /// <summary>Why the pre-migration copy could not be made. Non-null means the migration
    /// ran anyway and is not undoable — announced, never silently swallowed, and never a
    /// reason to refuse to start (CLAUDE.md §0.1: nothing parks behind a human).</summary>
    public string? PreMigrationBackupError { get; private set; }
    /// <summary>The schema this store was on when it was opened. 0 for a brand-new file.</summary>
    public long SchemaAtOpen { get; private set; }

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = "PRAGMA user_version;"; SchemaAtOpen = (long)c.ExecuteScalar()!; }
        // A store that is ABOUT to be migrated gets copied first, and the copy is announced
        // rather than merely written: the daemon refuses to hot-swap DOWN across a schema
        // version (Daemon.SwapDecision), so a migration is a one-way door for the operator
        // unless they know where the key is. SchemaAtOpen == 0 is a new file, which has
        // nothing to lose.
        if (SchemaAtOpen > 0 && SchemaAtOpen < Ver.Schema)
        {
            var bak = $"{path}.pre-v{SchemaAtOpen}";
            try { Backup(bak); PreMigrationBackup = bak; }
            catch (Exception ex) { PreMigrationBackupError = ex.Message; }
        }
        Migrate();
    }

    /// <summary>Copy the live store to <paramref name="destPath"/> with SQLite's online
    /// backup — consistent mid-write, WAL included, no file-copy races. Exists so a
    /// schema-migrating swap can proceed instead of parking (§14 revised): the backup IS
    /// the keystroke-undo a half-applied migration otherwise lacks. Overwrites any
    /// previous backup at the same path — the latest pre-migration state is the one an
    /// undo wants.</summary>
    public void Backup(string destPath)
    {
        lock (_lock)
        {
            using var dest = new SqliteConnection($"Data Source={destPath}");
            dest.Open();
            _db.BackupDatabase(dest);
        }
    }

    void Migrate()
    {
        long v;
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = "PRAGMA user_version;"; v = (long)c.ExecuteScalar()!; }
        if (v < 1)
        {
            Exec("""
                CREATE TABLE lanes(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'alive',
                    pipe_name TEXT NOT NULL DEFAULT '',
                    session_id TEXT,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE pane_events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lane_id INTEGER NOT NULL,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    body TEXT NOT NULL,
                    seq INTEGER,
                    raw TEXT,
                    UNIQUE(lane_id, seq)
                );
                CREATE TABLE events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    lane_id INTEGER,
                    detail TEXT
                );
                PRAGMA user_version = 1;
                """);
        }
        if (v < 2)
        {
            Exec("""
                CREATE TABLE tickets(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lane_id INTEGER,
                    title TEXT NOT NULL,
                    branch TEXT NOT NULL DEFAULT '',
                    worktree TEXT NOT NULL DEFAULT '',
                    state TEXT NOT NULL DEFAULT 'open',            -- open | landed | abandoned
                    merge_mode TEXT NOT NULL DEFAULT 'on-approval',
                    approved INTEGER NOT NULL DEFAULT 0,
                    created_ts TEXT NOT NULL,
                    landed_ts TEXT
                );
                CREATE TABLE claims(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ticket_id INTEGER NOT NULL,
                    kind TEXT NOT NULL,                            -- path | newfile | subtree | symbol
                    value TEXT NOT NULL,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE merge_token(
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    holder_ticket INTEGER,
                    generation INTEGER NOT NULL DEFAULT 0,
                    granted_ts TEXT,
                    expires_ts TEXT,
                    main_sha TEXT
                );
                INSERT INTO merge_token(id, generation) VALUES (1, 0);
                CREATE TABLE token_queue(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ticket_id INTEGER NOT NULL UNIQUE,
                    enqueued_ts TEXT NOT NULL
                );
                PRAGMA user_version = 2;
                """);
        }
        if (v < 3)
        {
            Exec("""
                ALTER TABLE lanes ADD COLUMN presence TEXT NOT NULL DEFAULT '';
                ALTER TABLE lanes ADD COLUMN role TEXT NOT NULL DEFAULT 'work';   -- work | router | dispatcher
                CREATE TABLE routing_decisions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    input TEXT NOT NULL,
                    tier TEXT NOT NULL,                 -- prefix | focus | classifier
                    target_lane INTEGER,                -- the classifier's opinion
                    delivered_lane INTEGER,             -- where it actually went (final)
                    confidence TEXT,
                    retargeted INTEGER NOT NULL DEFAULT 0,
                    undone INTEGER NOT NULL DEFAULT 0   -- the undo keystroke is labeled data (§4)
                );
                CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 3;
                """);
        }
        if (v < 4)
        {
            // M3: the decision feed persists until acked (§8) — acked is state, not
            // deletion; the pane replay must still show the row.
            Exec("""
                ALTER TABLE pane_events ADD COLUMN acked INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 4;
                """);
        }
        if (v < 5)
        {
            // M4: a swap that cannot be seamless is a decision with three answers, and
            // "when it lands" must survive a daemon restart — so it is a row (§14).
            Exec("""
                CREATE TABLE swaps(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    requested_ts TEXT NOT NULL,
                    exe TEXT NOT NULL,
                    build TEXT NOT NULL,
                    schema_version INTEGER NOT NULL,
                    shim_protocol INTEGER NOT NULL,
                    blocker TEXT,                       -- NULL once nothing is in the way
                    mode TEXT NOT NULL,                 -- ask | now | when-it-lands | hold
                    state TEXT NOT NULL,                -- pending | armed | held | swapped | failed | superseded
                    decided_ts TEXT,
                    note TEXT
                );
                PRAGMA user_version = 5;
                """);
        }
        if (v < 6)
        {
            // A workspace holds several repositories. The merge token becomes per
            // repository: a ticket landing in `engine` must not queue behind one landing
            // in `tools` — different mains, no possible conflict, no reason to serialize.
            // Existing single-repo stores migrate to the repo named ".", the workspace
            // root itself, so nothing about them changes.
            Exec("""
                ALTER TABLE tickets ADD COLUMN repo TEXT NOT NULL DEFAULT '.';
                ALTER TABLE token_queue ADD COLUMN repo TEXT NOT NULL DEFAULT '.';
                CREATE TABLE merge_token_v6(
                    repo TEXT PRIMARY KEY,
                    holder_ticket INTEGER,
                    generation INTEGER NOT NULL DEFAULT 0,
                    granted_ts TEXT,
                    expires_ts TEXT,
                    main_sha TEXT
                );
                INSERT INTO merge_token_v6(repo, holder_ticket, generation, granted_ts, expires_ts, main_sha)
                    SELECT '.', holder_ticket, generation, granted_ts, expires_ts, main_sha FROM merge_token WHERE id = 1;
                DROP TABLE merge_token;
                ALTER TABLE merge_token_v6 RENAME TO merge_token;
                PRAGMA user_version = 6;
                """);
        }
        if (v < 7)
        {
            // Selective compression (§5). The short readable version is a SECOND column,
            // never an overwrite: `body` stays the agent's own words and `raw` stays the
            // wire line, so "raw one keystroke away" remains literally true and a bad
            // compression is a cosmetic problem rather than a lost message. NULL means
            // "not compressed" — either it was not eligible, or the compressor never
            // answered, and both render as the full text.
            Exec("""
                ALTER TABLE pane_events ADD COLUMN compressed TEXT;
                PRAGMA user_version = 7;
                """);
        }
        if (v < 8)
        {
            // A lane now records WHERE it runs (M5.1). It never did, and `_primary` was
            // hardcoded at both spawn and respawn — so every lane in a workspace shared the
            // operator's live tree, and a respawned TICKET lane came back editing main's
            // working copy while its own system prompt still told it "your worktree is the
            // current working directory". Empty means "the workspace primary", which is
            // exactly what every pre-M5 row was.
            Exec("""
                ALTER TABLE lanes ADD COLUMN cwd TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 8;
                """);
        }
        if (v < 9)
        {
            // A repository is identified by its PATH, not by its display name (P0.1/P0.2).
            // v6 keyed the merge token on `repo`, the workspace-relative display name — and
            // that name is recomputed from the registry and the filesystem on every call,
            // by a rule that CHANGES WITH PROJECT COUNT: one project that is a repo is
            // named ".", and attaching a second renames that same repository to its leaf.
            // `tickets.repo` is written once and never updated, so after an attach the
            // pre-existing ticket asked for token "." while a new one asked for token
            // "proj" — two rows over one `main`, both landing in the same folder. Two
            // agents could each be told "granted" and both fast-forward. `leaf~2` is
            // recyclable on top of that, so a later-attached repo could inherit another
            // repo's open tickets and its token outright.
            //
            // COLLATE NOCASE on every key column is not tidiness (P0.4): repo names and
            // paths come from live disk casing while SQLite `=` and PRIMARY KEY are
            // binary-collated, so `Engine` and `engine` were two tokens over one main by
            // exactly the same mechanism, reached by renaming a folder.
            //
            // The path itself cannot be computed HERE — it needs the registry's member list
            // and a filesystem walk, and the store deliberately knows about neither. So
            // every carried-over row is keyed by a `#unresolved:<name>` placeholder that is
            // unique (the old name was a PRIMARY KEY), can never collide with a canonical
            // absolute path, and is visibly unfinished. StampRepoPaths resolves them at
            // daemon start, where repo discovery lives, and MERGES rows that turn out to be
            // the same repository — which is the live two-token defect being repaired, out
            // loud, rather than merely prevented from recurring.
            Exec("""
                ALTER TABLE tickets ADD COLUMN repo_path TEXT NOT NULL DEFAULT '' COLLATE NOCASE;
                ALTER TABLE token_queue ADD COLUMN repo_path TEXT NOT NULL DEFAULT '' COLLATE NOCASE;
                UPDATE token_queue SET repo_path = '#unresolved:' || repo;
                CREATE TABLE merge_token_v9(
                    repo_path TEXT PRIMARY KEY COLLATE NOCASE,
                    repo TEXT NOT NULL DEFAULT '',
                    holder_ticket INTEGER,
                    generation INTEGER NOT NULL DEFAULT 0,
                    granted_ts TEXT,
                    expires_ts TEXT,
                    main_sha TEXT
                );
                INSERT INTO merge_token_v9(repo_path, repo, holder_ticket, generation, granted_ts, expires_ts, main_sha)
                    SELECT '#unresolved:' || repo, repo, holder_ticket, generation, granted_ts, expires_ts, main_sha FROM merge_token;
                DROP TABLE merge_token;
                ALTER TABLE merge_token_v9 RENAME TO merge_token;
                PRAGMA user_version = 9;
                """);
        }

        if (v < 10)
        {
            // WHICH PROJECT A LANE IS FOR (P5.1, decision D-L8: "track brains, do not count
            // them"). `cwd` (schema 8) says where a lane's process RUNS. This says which
            // project it is FOR, and the two are deliberately different for a management lane:
            // a brain runs in the neutral directory on purpose (P5.8 -- a brain inside a
            // project loads that project's CLAUDE.md and skills, i.e. a manager that can end
            // up running `/ship`), so its cwd can never say which project it serves.
            //
            // WITHOUT THIS COLUMN THERE IS NOTHING TO ASK BUT A COUNT, and the count was
            // wrong. Reconcile kept ONE `keep` lane id per utility role and shut every other
            // alive lane of that role down as "a duplicate left by a fixed leak" -- so two
            // projects meant a healthy brain killed on every daemon start (including every
            // auto-publish swap), ANNOUNCED AS A REPAIR. A count cannot tell "two brains
            // because two projects" from "two brains because of a bug".
            //
            // So validity becomes a REGISTRATION: a brain is valid iff a row says it should
            // exist for (role, project), and "surplus" becomes an unmatched registration
            // rather than an arithmetic result. Nothing healthy is ever killed again.
            //
            // Empty means "not resolved yet", which is what every pre-v10 row is.
            // StampLaneProjects fills them in at daemon start, where the registry lives --
            // the store deliberately knows nothing about workspace membership, exactly as with
            // schema 9's `#unresolved:` repo paths.
            //
            // COLLATE NOCASE for schema 9's reason, restated because it bit twice: paths come
            // from live disk casing while SQLite `=` is binary-collated, so `C:\Proj` and
            // `c:\proj` would be two registrations over one project -- i.e. two brains where
            // there should be one, reached by nothing more than a folder rename.
            Exec("""
                ALTER TABLE lanes ADD COLUMN project TEXT NOT NULL DEFAULT '' COLLATE NOCASE;
                PRAGMA user_version = 10;
                """);
        }

        // ---- questions: a workspace-scope ask, as a row (LOCATIONS-PLAN P4.1/P4.5) ----------
        //
        // Deliberately NOT behind a `Ver.Schema` bump, and that is a decision rather than an
        // oversight. `Ver.Schema` exists for ONE purpose: the daemon refuses to hot-swap DOWN
        // across it (Daemon.cs:1249), because an older binary must not take over a store whose
        // rows it would misread. This table is purely additive — no older binary names it, no
        // older code path reads it — so there is nothing for that refusal to protect, while a
        // bump would have cost a version number in a wave where Phase 5 has already been
        // assigned v10 for `lanes.project` (the plan's P5.1 note: "two changes would have
        // collided on one version number"). IF NOT EXISTS makes it self-healing in both
        // directions: a store made by a newer binary and reopened by an older one is unharmed,
        // and the next newer open re-creates whatever is missing.
        //
        // The seven columns before `kind` are byte-identical to ConciergeStore's `questions`,
        // and that identity is the whole of D-L4: ONE row shape means one component renders
        // both scopes and one answer verb answers both. `kind`/`subject` are appended, not
        // interleaved, and the UI never reads them — they tell the DAEMON what answering means
        // (run repo-init on which project), which is not a rendering concern.
        Exec("""
            CREATE TABLE IF NOT EXISTS questions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                input TEXT NOT NULL,
                candidates TEXT NOT NULL,           -- JSON [{id,name,why}] — Ask.Choices parses it
                state TEXT NOT NULL,                -- open | answered | withdrawn
                answer TEXT,
                answered_ts TEXT,
                kind TEXT NOT NULL DEFAULT '',      -- what answering DOES (Ask.KindRepoInit)
                subject TEXT NOT NULL DEFAULT ''    -- what it acts on (a project path)
            );
            """);
    }

    // ------------------------------------------------- lane registration (P5.1)

    /// <summary>What <see cref="StampLaneProjects"/> did. Every list is a line a person may
    /// need to read: a lane whose project could not be resolved is ANNOUNCED, and an
    /// ASSUMED one is announced too, because an assumption that decides whether an agent is
    /// reaped must not be invisible (CLAUDE.md §0.1 — a silent degrade is a bug).</summary>
    public sealed record LaneStampReport(List<string> Stamped, List<string> Assumed, List<string> Unresolved);

    /// <summary>
    /// Fill in <c>project</c> for lanes written before schema 10, using a resolver that knows
    /// the workspace's projects (the daemon owns membership; the store must not).
    ///
    /// Idempotent and safe on every start: it touches only rows that are still unstamped and
    /// not dead, so on an already-migrated store it costs one scan.
    ///
    /// WHY THIS IS NOT OPTIONAL. Reaping is keyed on this column, so a pre-v10 brain left
    /// blank would look like a registration for no project — and the first daemon on the new
    /// schema would reap the operator's live brain as unregistered. Which is the exact bug
    /// Phase 5 exists to remove, reintroduced by the migration that removes it. The resolver
    /// therefore always returns SOMETHING for a management lane, and says whether it knew or
    /// assumed.
    ///
    /// Dead rows are left blank on purpose: nothing keys on them, they can be numerous, and
    /// rewriting a workspace's whole history to stamp lanes that ended months ago is a
    /// migration cost paid for no reader.
    /// </summary>
    public LaneStampReport StampLaneProjects(Func<LaneRow, (string? Project, bool Assumed)> resolve)
    {
        var report = new LaneStampReport(new List<string>(), new List<string>(), new List<string>());
        var pending = LanesAll().Where(l => l.Project.Length == 0 && l.State != "dead").ToList();
        if (pending.Count == 0) return report;
        foreach (var l in pending)
        {
            var (project, assumed) = resolve(l);
            if (project is null or "")
            {
                report.Unresolved.Add($"lane {l.Id} ({l.Title}, role={l.Role}): no project owns cwd '{(l.Cwd.Length > 0 ? l.Cwd : "-")}'");
                continue;
            }
            LaneProject(l.Id, project);
            (assumed ? report.Assumed : report.Stamped).Add($"lane {l.Id} ({l.Title}, role={l.Role}) -> {project}");
        }
        return report;
    }

    // ------------------------------------------------------- questions (P4.1)

    /// <summary>The seven columns a question renders from. Same record shape as
    /// <c>ConciergeStore.QuestionRow</c> plus what the daemon needs to ACT on an answer, and
    /// the duplication is deliberate: the two stores are separate authorities (§2 forbids a
    /// workspace daemon reading the concierge's), so sharing a type would be the first step
    /// towards sharing a connection.</summary>
    public record QuestionRow(long Id, string Ts, string Input, string Candidates, string State,
                              string? Answer, string Kind, string Subject);

    const string QuestionCols = "id, ts, input, candidates, state, answer, kind, subject";

    static QuestionRow ReadQuestion(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7));

    /// <summary>Open a question. Returns its id — which is what the answer command takes, and
    /// what the announcement must carry so the operator can answer it from anywhere.</summary>
    public long QuestionOpen(string input, string candidatesJson, string kind = "", string subject = "")
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO questions(ts, input, candidates, state, kind, subject) " +
                            "VALUES ($ts,$i,$c,'open',$k,$s);";
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", input);
            c.Parameters.AddWithValue("$c", candidatesJson);
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$s", subject);
            c.ExecuteNonQuery();
            // A SEPARATE command for the id: `INSERT …; SELECT last_insert_rowid();` in one
            // Microsoft.Data.Sqlite command returns nothing without NextResult() (CLAUDE.md §0.2).
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(q.ExecuteScalar()!);
        }
    }

    public QuestionRow? Question(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {QuestionCols} FROM questions WHERE id = $i;";
            c.Parameters.AddWithValue("$i", id);
            using var r = c.ExecuteReader();
            return r.Read() ? ReadQuestion(r) : null;
        }
    }

    /// <summary>Every unanswered question, oldest first — oldest because the operator should be
    /// asked in the order they created the uncertainty, not last-in-first-out.</summary>
    public List<QuestionRow> OpenQuestions()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {QuestionCols} FROM questions WHERE state = 'open' ORDER BY id;";
            var list = new List<QuestionRow>();
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(ReadQuestion(r));
            return list;
        }
    }

    /// <summary>Record the answer. Guarded on `state = 'open'`, so answering twice is a
    /// refusal rather than a second action — the concierge's `QuestionAnswer` has the same
    /// guard and `concierge:answering_twice_is_refused` is the check that pins it.</summary>
    public bool QuestionAnswer(long id, string answer, string state = "answered")
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE questions SET state = $st, answer = $a, answered_ts = $ts " +
                            "WHERE id = $i AND state = 'open';";
            c.Parameters.AddWithValue("$st", state);
            c.Parameters.AddWithValue("$a", answer);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", id);
            return c.ExecuteNonQuery() > 0;
        }
    }

    // ------------------------------------------------------- repo identity (P0.3)

    /// <summary>What <see cref="StampRepoPaths"/> did, so the daemon can announce it. Every
    /// list is a line a person may need to read: a row that could not be resolved is
    /// ANNOUNCED, never silently dropped and never silently left to resolve to whatever the
    /// name happens to mean now.</summary>
    public sealed record StampReport(List<string> Stamped, List<string> Unresolved, List<string> Merged);

    /// <summary>
    /// Fill in <c>repo_path</c> for rows written before schema 9, using a resolver that knows
    /// the workspace's repositories (the daemon owns discovery; the store must not).
    ///
    /// Idempotent, and safe to run on every start: it only touches rows that are still
    /// unstamped, so it costs one indexed scan on an already-migrated store.
    ///
    /// The MERGE is the point. Two token rows whose names resolve to one path are the live
    /// two-token defect materialised, so they are folded into one: the highest generation
    /// wins (a fencing counter must never go backwards) and a holder is preserved rather
    /// than dropped — losing a holder would hand the token to a second agent, which is the
    /// thing being fixed. Both rows holding at once is the incident itself; it is reported
    /// with both ticket numbers.
    /// </summary>
    public StampReport StampRepoPaths(Func<string, string?> resolve)
    {
        var report = new StampReport(new List<string>(), new List<string>(), new List<string>());
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();

            // -- tickets
            var pending = new List<(long Id, string Repo)>();
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT id, repo FROM tickets WHERE repo_path = '';";
                using var r = c.ExecuteReader();
                while (r.Read()) pending.Add((r.GetInt64(0), r.GetString(1)));
            }
            foreach (var (id, name) in pending)
            {
                var path = resolve(name);
                if (path is null) { report.Unresolved.Add($"ticket {id}: repo '{name}' resolves to no repository in this workspace"); continue; }
                using var u = _db.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE tickets SET repo_path = $p WHERE id = $id;";
                u.Parameters.AddWithValue("$p", path);
                u.Parameters.AddWithValue("$id", id);
                u.ExecuteNonQuery();
                report.Stamped.Add($"ticket {id}: '{name}' -> {path}");
            }

            // -- merge tokens (the two-token repair)
            var placeholders = new List<(string Key, string Repo, long? Holder, long Gen, string? Granted)>();
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT repo_path, repo, holder_ticket, generation, granted_ts FROM merge_token WHERE repo_path LIKE '#unresolved:%';";
                using var r = c.ExecuteReader();
                while (r.Read())
                    placeholders.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                                      r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)));
            }
            foreach (var p in placeholders)
            {
                var path = resolve(p.Repo);
                if (path is null) { report.Unresolved.Add($"merge token '{p.Repo}' resolves to no repository in this workspace — it keeps its own row and cannot be granted"); continue; }

                (long? Holder, long Gen, string? Granted)? existing = null;
                using (var c = _db.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "SELECT holder_ticket, generation, granted_ts FROM merge_token WHERE repo_path = $p;";
                    c.Parameters.AddWithValue("$p", path);
                    using var r = c.ExecuteReader();
                    if (r.Read()) existing = (r.IsDBNull(0) ? null : r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2));
                }

                if (existing is null)
                {
                    using var u = _db.CreateCommand();
                    u.Transaction = tx;
                    u.CommandText = "UPDATE merge_token SET repo_path = $p WHERE repo_path = $k;";
                    u.Parameters.AddWithValue("$p", path);
                    u.Parameters.AddWithValue("$k", p.Key);
                    u.ExecuteNonQuery();
                    report.Stamped.Add($"merge token '{p.Repo}' -> {path}");
                    continue;
                }

                var e = existing.Value;
                long? holder = e.Holder ?? p.Holder;
                if (e.Holder is not null && p.Holder is not null)
                {
                    // Both rows held. This is the race, on disk. Keep the later grant and say so.
                    var pLater = p.Granted is not null && (e.Granted is null || DateTime.Parse(p.Granted) > DateTime.Parse(e.Granted));
                    holder = pLater ? p.Holder : e.Holder;
                    report.Merged.Add($"TWO MERGE TOKENS over {path}: tickets {e.Holder} and {p.Holder} both held one — kept {holder}");
                }
                else report.Merged.Add($"merge token '{p.Repo}' was a second row over {path} — folded into one");

                using (var u = _db.CreateCommand())
                {
                    u.Transaction = tx;
                    u.CommandText = "UPDATE merge_token SET holder_ticket = $h, generation = MAX(generation, $g) WHERE repo_path = $p;";
                    u.Parameters.AddWithValue("$h", (object?)holder ?? DBNull.Value);
                    u.Parameters.AddWithValue("$g", p.Gen);
                    u.Parameters.AddWithValue("$p", path);
                    u.ExecuteNonQuery();
                }
                using (var d = _db.CreateCommand())
                {
                    d.Transaction = tx;
                    d.CommandText = "DELETE FROM merge_token WHERE repo_path = $k;";
                    d.Parameters.AddWithValue("$k", p.Key);
                    d.ExecuteNonQuery();
                }
            }

            // -- the queue follows the token it queues for
            var queued = new List<(long Ticket, string Repo)>();
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT ticket_id, repo FROM token_queue WHERE repo_path LIKE '#unresolved:%';";
                using var r = c.ExecuteReader();
                while (r.Read()) queued.Add((r.GetInt64(0), r.GetString(1)));
            }
            foreach (var (ticket, name) in queued)
            {
                var path = resolve(name);
                if (path is null) { report.Unresolved.Add($"queued ticket {ticket}: repo '{name}' resolves to no repository"); continue; }
                using var u = _db.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE token_queue SET repo_path = $p WHERE ticket_id = $t;";
                u.Parameters.AddWithValue("$p", path);
                u.Parameters.AddWithValue("$t", ticket);
                u.ExecuteNonQuery();
            }

            tx.Commit();
        }
        return report;
    }

    static string Now() => DateTime.UtcNow.ToString("o");

    void Exec(string sql)
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }

    // ------------------------------------------------------------- events & lanes

    /// <summary>The most recent detail this lane recorded for an event kind, or null.
    ///
    /// Reads what was WRITTEN rather than what the config would say now -- which is the difference
    /// promotion needs (WORK-ISOLATION-PLAN P2): a lane must come back as the same agent binary it
    /// was running, not as whatever `dodona.json` currently names.</summary>
    public string? LastEventDetail(string kind, long laneId)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT detail FROM events WHERE kind = $k AND lane_id = $l ORDER BY id DESC LIMIT 1;";
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$l", laneId);
            return c.ExecuteScalar() as string;
        }
    }

    /// <summary>Has this lane ever recorded an event of this kind whose detail matches? Written
    /// for D-9's undo: `lane-stop` may only abandon a ticket the lane was PROMOTED into, never one
    /// the operator created deliberately -- and the promotion event is the only record of the
    /// difference. `like` is a SQL LIKE pattern; pass "%" for "any".</summary>
    public bool HasEvent(string kind, long laneId, string like)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM events WHERE kind = $k AND lane_id = $l AND detail LIKE $d;";
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$d", like);
            return Convert.ToInt64(c.ExecuteScalar() ?? 0L) > 0;
        }
    }

    public void Event(string kind, long? laneId, string? detail)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO events(ts, kind, lane_id, detail) VALUES ($ts, $k, $l, $d);";
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$l", (object?)laneId ?? DBNull.Value);
            c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    public long LaneCreate(string title)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO lanes(title, created_ts) VALUES ($t, $ts); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$ts", Now());
            return (long)c.ExecuteScalar()!;
        }
    }

    public void LanePipe(long id, string pipe) => Set("UPDATE lanes SET pipe_name = $v WHERE id = $id;", id, pipe);
    public void LaneState(long id, string state) => Set("UPDATE lanes SET state = $v WHERE id = $id;", id, state);
    public void LaneSession(long id, string session) => Set("UPDATE lanes SET session_id = $v WHERE id = $id;", id, session);
    public void LanePresence(long id, string presence) => Set("UPDATE lanes SET presence = $v WHERE id = $id;", id, presence);
    public void LaneRole(long id, string role) => Set("UPDATE lanes SET role = $v WHERE id = $id;", id, role);
    /// <summary>Where this lane's child process runs. Recorded so a RESPAWN lands in the
    /// same place the original did — a ticket lane must come back in its worktree, not in
    /// the operator's live tree (M5.1).</summary>
    public void LaneCwd(long id, string cwd) => Set("UPDATE lanes SET cwd = $v WHERE id = $id;", id, cwd);
    /// <summary>Which project this lane is FOR — the registration a brain's validity is
    /// decided by (P5.1, D-L8). Not the same as <see cref="LaneCwd"/>: a brain is scoped to a
    /// project while running in the neutral directory (P5.8).</summary>
    public void LaneProject(long id, string project) => Set("UPDATE lanes SET project = $v WHERE id = $id;", id, project);
    public void LaneTitle(long id, string title) => Set("UPDATE lanes SET title = $v WHERE id = $id;", id, title);

    public void KvSet(string key, string value)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO kv(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Which lanes the operator has collapsed (ORCHESTRATOR-DESIGN §8 as revised: the grid
    /// grows with the work instead of capping at six, and the operator collapses what they are
    /// not dealing with).
    ///
    /// It lives in the STORE rather than in the window, for the m3 reason that decided
    /// everything else: the UI owns nothing. A view choice this deliberate should survive
    /// closing the window — and it has to be the same answer for every window looking at this
    /// workspace, which a per-process field could never be.
    ///
    /// Deliberately NOT a `lanes` column: collapse says nothing about the lane's life, only
    /// about how much room you want it to take right now. Nothing reads it for authority.
    /// </summary>
    public HashSet<long> CollapsedLanes()
    {
        var set = new HashSet<long>();
        foreach (var part in (KvGet("collapsed_lanes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(part.Trim(), out var id)) set.Add(id);
        return set;
    }

    public void LaneCollapsed(long id, bool collapsed)
    {
        var set = CollapsedLanes();
        if (collapsed) set.Add(id); else set.Remove(id);
        KvSet("collapsed_lanes", string.Join(",", set.OrderBy(x => x)));
    }

    public string? KvGet(string key)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT value FROM kv WHERE key = $k;";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public long RoutingInsert(string input, string tier, long? target, long? delivered, string? confidence)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                INSERT INTO routing_decisions(ts, input, tier, target_lane, delivered_lane, confidence)
                VALUES ($ts, $i, $t, $tl, $dl, $c); SELECT last_insert_rowid();
                """;
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", input);
            c.Parameters.AddWithValue("$t", tier);
            c.Parameters.AddWithValue("$tl", (object?)target ?? DBNull.Value);
            c.Parameters.AddWithValue("$dl", (object?)delivered ?? DBNull.Value);
            c.Parameters.AddWithValue("$c", (object?)confidence ?? DBNull.Value);
            return (long)c.ExecuteScalar()!;
        }
    }

    public void RoutingRetarget(long id, long targetLane, string confidence)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE routing_decisions SET tier = 'classifier', target_lane = $t, delivered_lane = $t, confidence = $c, retargeted = 1 WHERE id = $id;";
            c.Parameters.AddWithValue("$t", targetLane);
            c.Parameters.AddWithValue("$c", confidence);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    void Set(string sql, long id, string value)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$v", value);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public record LaneRow(long Id, string Title, string State, string Pipe, string? Session, string Presence, string Role,
                          string Cwd, string Project);

    public List<LaneRow> LanesAll()
    {
        lock (_lock)
        {
            var list = new List<LaneRow>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, title, state, pipe_name, session_id, presence, role, cwd, project FROM lanes ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new LaneRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                     r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6),
                                     r.GetString(7), r.GetString(8)));
            return list;
        }
    }

    /// <summary>`acked: true` writes a receipt — visible in the pane and the feed but
    /// never counted as attention. A badge must mean "you are needed", not "something
    /// happened" (docs/LANE-LIFECYCLE.md §4); a receipt for an act the system just did on
    /// your behalf is the latter.</summary>
    public bool PaneEvent(long laneId, string kind, string body, long? seq, string? raw, bool acked = false) =>
        PaneEventId(laneId, kind, body, seq, raw, acked) > 0;

    /// <summary>As PaneEvent, but returns the new row's id — 0 when the row was a
    /// duplicate seq and therefore ignored. The compressor needs the id: the row is
    /// written the instant it arrives and the short version is filled in later, so the
    /// pane never waits on a model (§5).</summary>
    public long PaneEventId(long laneId, string kind, string body, long? seq, string? raw, bool acked = false)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO pane_events(lane_id, ts, kind, body, seq, raw, acked) VALUES ($l, $ts, $k, $b, $s, $r, $a);";
            c.Parameters.AddWithValue("$a", acked ? 1 : 0);
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$b", body);
            c.Parameters.AddWithValue("$s", (object?)seq ?? DBNull.Value);
            c.Parameters.AddWithValue("$r", (object?)raw ?? DBNull.Value);
            if (c.ExecuteNonQuery() <= 0) return 0;      // duplicate seq: the shim redelivered

            // A SEPARATE statement on purpose. Appending `SELECT last_insert_rowid()` to
            // the insert reads naturally and returns nothing: Microsoft.Data.Sqlite leaves
            // the reader on the first statement, so Read() is false on the INSERT and the
            // SELECT is never reached without NextResult(). That silently returned "no row
            // id", which silently disabled compression — no error anywhere.
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(q.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Fill in a row's short readable version. Never touches `body`.</summary>
    public bool PaneCompressed(long paneEventId, string compressed)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE pane_events SET compressed = $c WHERE id = $id;";
            c.Parameters.AddWithValue("$c", compressed);
            c.Parameters.AddWithValue("$id", paneEventId);
            return c.ExecuteNonQuery() > 0;
        }
    }

    public bool PaneAck(long paneEventId)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE pane_events SET acked = 1 WHERE id = $id AND kind = 'announcement' AND acked = 0;";
            c.Parameters.AddWithValue("$id", paneEventId);
            return c.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Mark a routing decision undone (§4: the undo keystroke is labeled data)
    /// and return where it was delivered so the daemon can send a retraction.</summary>
    public (long? DeliveredLane, string Input)? RoutingUndo(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                UPDATE routing_decisions SET undone = 1 WHERE id = $id AND undone = 0
                RETURNING delivered_lane, input;
                """;
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return (r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1));
        }
    }

    /// <summary>The last n pane rows, formatted for a person: `dodona tail`, and the
    /// dispatcher's fact sheet.
    ///
    /// <paramref name="readableOnly"/> drops the raw `wire` rows, and it exists because
    /// the fact sheet was quietly feeding them to a model. `Tail(id, 1)` is how §4's
    /// FactSheet says "last: …" about a lane, and the newest row of a thinking agent is a
    /// `thinking_tokens` JSON blob — so the brain was being asked to judge a lane's subject
    /// from 110 characters of `{"type":"system","subtype":"thinking_to`. Not a crash, not
    /// visible anywhere, just a worse routing decision than the store could support.</summary>
    public List<string> Tail(long laneId, int n, bool readableOnly = false)
    {
        lock (_lock)
        {
            var rows = new List<string>();
            using var c = _db.CreateCommand();
            c.CommandText = readableOnly ? """
                SELECT ts, kind, body FROM (
                    SELECT id, ts, kind, body FROM pane_events
                    WHERE lane_id = $l AND kind NOT IN ('wire', 'system') ORDER BY id DESC LIMIT $n
                ) ORDER BY id;
                """ : """
                SELECT ts, kind, body FROM (
                    SELECT id, ts, kind, body FROM pane_events WHERE lane_id = $l ORDER BY id DESC LIMIT $n
                ) ORDER BY id;
                """;
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add($"{r.GetString(0)}  {r.GetString(1),-10}  {r.GetString(2)}");
            return rows;
        }
    }

    // ------------------------------------------------------------- tickets & claims

    /// <summary><paramref name="Repo"/> is the DISPLAY name, frozen at creation — the claims
    /// in the claims table were written workspace-relative to it, so it is the ticket's claim
    /// namespace and must not move underneath an open ticket. <paramref name="RepoPath"/> is
    /// the IDENTITY: which repository this is, whatever it is called today (P0.1). Empty only
    /// for a row written before schema 9 whose name no longer resolves to anything.</summary>
    public record TicketRow(long Id, long? LaneId, string Title, string Branch, string Worktree,
                            string State, string MergeMode, bool Approved, string Repo, string RepoPath);

    /// <summary>Insert the ticket and its claims in ONE transaction (§6, §12), and REPORT any
    /// overlap with other open tickets rather than refusing it.
    ///
    /// **The refusal is gone (`REVIEW-AND-MERGE-PLAN.md` D-R5, R3), and the returned list is
    /// now information.** Two tickets over one path used to be rejected here — "no overlap →
    /// inserted; overlap → nothing inserted". The operator's decision retired it: *two agents
    /// about to work on the same file is often the case, very often the case*, and files are
    /// not the unit of work — a feature spans files and features overlap. What genuinely
    /// matters about an overlap is duplicated **effort**, which is a judgement for the manager
    /// to raise, not a lock for the store to hold.
    ///
    /// The `Conflicts` list is still computed and still returned, because it is exactly the
    /// derived signal D-R7 wants shown to a reviewer. Callers announce it; nobody gates on it.
    /// `Id` is therefore always a real id now — a caller checking `Id &lt; 0` for "overlap" is
    /// reading a condition that can no longer happen.</summary>
    public (long Id, List<string> Conflicts) TicketCreate(long? laneId, string title, string mode, string repo,
                                                          string repoPath, List<(string Kind, string Value)> claims)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            // Scoped by the SAME pair that is about to be written to the row: the canonical
            // path is the identity a conflict is judged against, the display name is the
            // prefix the claim values carry (Phase 0b).
            var conflicts = FindConflicts(tx, new RepoId(repoPath, repo), claims, excludeTicket: null);

            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO tickets(lane_id, title, merge_mode, repo, repo_path, created_ts) VALUES ($l, $t, $m, $r, $rp, $ts); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$l", (object?)laneId ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$r", repo);
            c.Parameters.AddWithValue("$rp", repoPath);
            c.Parameters.AddWithValue("$ts", Now());
            var id = (long)c.ExecuteScalar()!;
            InsertClaims(tx, id, claims);
            tx.Commit();
            return (id, conflicts);
        }
    }

    public List<string> ClaimExtend(long ticketId, List<(string Kind, string Value)> claims)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            // The ticket's own repository is READ HERE, inside the transaction, rather than
            // passed in by the daemon: a caller able to pass the wrong repository is a caller
            // able to scope a conflict search to the wrong place, and the row already holds
            // both halves — repo_path is the identity, repo is the frozen display name the
            // existing claim values were written against. A missing row leaves the pair empty,
            // which means UNSCOPED, which refuses more rather than less.
            var repo = TxRepoId(tx, ticketId);
            var conflicts = FindConflicts(tx, repo, claims, excludeTicket: ticketId);
            // A FOURTH REFUSAL, NOT LISTED IN D-R5, RETIRED HERE BECAUSE THE OTHER THREE MAKE IT
            // INCOHERENT (R3, announced rather than done quietly). D-R5's table names three: the
            // write gate, `ticket-create`, and the `token-request` backstop. This is the same
            // refusal reached from a fourth direction -- and leaving it would mean a claim you
            // may freely CREATE over another ticket's path is one you may not EXTEND onto, so
            // the identical end state would be permitted or refused depending on which command
            // you happened to use. It also breaks in practice the moment `ticket-create` stops
            // refusing: overlapping tickets now exist, so every wide extension hits one.
            //
            // The principle D-R5 actually settles is that declared claims are not locks
            // (Appendix A: `claim-extend` exists "for anyone who wants to annotate a ticket by
            // hand" -- annotation, not locking). So the overlap is returned and the claims are
            // inserted; the caller reports rather than refuses. A BAD SPEC still refuses, in the
            // daemon, and that is a different thing: it is unparseable input, not an overlap.
            InsertClaims(tx, ticketId, claims);
            tx.Commit();
            return conflicts;
        }
    }

    RepoId TxRepoId(SqliteTransaction tx, long ticketId)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT repo_path, repo FROM tickets WHERE id = $id;";
        c.Parameters.AddWithValue("$id", ticketId);
        using var r = c.ExecuteReader();
        return r.Read() ? new RepoId(r.GetString(0), r.GetString(1)) : new RepoId("", "");
    }

    /// <summary>Which open tickets' claims intersect these (§6) — asked PER REPOSITORY since
    /// Phase 0b. <paramref name="repo"/> is the asking ticket's identity/display-name pair;
    /// <c>Claims.Overlap(Held, Held)</c> carries the whole argument for why the comparison
    /// cannot be made on the raw stored values, in both directions.</summary>
    List<string> FindConflicts(SqliteTransaction tx, RepoId repo,
                               List<(string Kind, string Value)> claims, long? excludeTicket)
    {
        var conflicts = new List<string>();
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = """
            SELECT cl.kind, cl.value, t.id, t.title, t.repo_path, t.repo FROM claims cl
            JOIN tickets t ON t.id = cl.ticket_id
            WHERE t.state = 'open' AND ($x IS NULL OR t.id != $x);
            """;
        c.Parameters.AddWithValue("$x", (object?)excludeTicket ?? DBNull.Value);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var (ek, ev, tid, ttitle) = (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3));
            var held = new Claims.Held(r.GetString(4), r.GetString(5), ek, ev);
            foreach (var (k, v) in claims)
                if (Claims.Overlap(new Claims.Held(repo.Path, repo.Name, k, v), held))
                    conflicts.Add($"{Claims.Spec(k, v)} overlaps {Claims.Spec(ek, ev)} held by ticket {tid} ({ttitle})");
        }
        return conflicts;
    }

    void InsertClaims(SqliteTransaction tx, long ticketId, List<(string Kind, string Value)> claims)
    {
        foreach (var (k, v) in claims)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO claims(ticket_id, kind, value, created_ts) VALUES ($t, $k, $v, $ts);";
            c.Parameters.AddWithValue("$t", ticketId);
            c.Parameters.AddWithValue("$k", k);
            c.Parameters.AddWithValue("$v", v);
            c.Parameters.AddWithValue("$ts", Now());
            c.ExecuteNonQuery();
        }
    }

    public void TicketSetLane(long id, long laneId)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE tickets SET lane_id = $l WHERE id = $id;";
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void TicketSetGit(long id, string branch, string worktree)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE tickets SET branch = $b, worktree = $w WHERE id = $id;";
            c.Parameters.AddWithValue("$b", branch);
            c.Parameters.AddWithValue("$w", worktree);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void TicketState(long id, string state) => Set("UPDATE tickets SET state = $v WHERE id = $id;", id, state);
    public void TicketApprove(long id) => Set("UPDATE tickets SET approved = 1 WHERE id = $id AND $v = $v;", id, "1");

    const string TicketCols = "id, lane_id, title, branch, worktree, state, merge_mode, approved, repo, repo_path";

    static TicketRow ReadTicket(SqliteDataReader r) =>
        new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1, r.GetString(8), r.GetString(9));

    public TicketRow? Ticket(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {TicketCols} FROM tickets WHERE id = $id;";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            return r.Read() ? ReadTicket(r) : null;
        }
    }

    public List<TicketRow> Tickets()
    {
        lock (_lock)
        {
            var list = new List<TicketRow>();
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {TicketCols} FROM tickets ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(ReadTicket(r));
            return list;
        }
    }

    public List<(string Kind, string Value)> TicketClaims(long id)
    {
        lock (_lock)
        {
            var list = new List<(string, string)>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT kind, value FROM claims WHERE ticket_id = $id;";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    // ------------------------------------------------------------- merge token (§7)

    /// <summary>
    /// How every token operation names a repository (P0.2). <paramref name="Path"/> is the
    /// KEY — the canonical path, from <c>Repos.Key</c> — and <paramref name="Name"/> is only
    /// ever shown to a person.
    ///
    /// They are one parameter rather than two because keying the token on the display name is
    /// precisely the defect being fixed: the name is recomputed on every call by a rule that
    /// changes with project count, so "." and "proj" were two token rows over one `main`.
    /// A call site that has only a name now cannot compile.
    /// </summary>
    public readonly record struct RepoId(string Path, string Name);

    public record TokenRow(string RepoPath, string Repo, long? Holder, long Generation, string? ExpiresTs, string? MainSha);

    public TokenRow TokenRead(RepoId repo)
    {
        lock (_lock) { return ReadToken(null, repo); }
    }

    /// <summary>Every repository's token — what `token-status` shows in a workspace.</summary>
    public List<TokenRow> TokensAll()
    {
        lock (_lock)
        {
            var list = new List<TokenRow>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT repo_path, repo, holder_ticket, generation, expires_ts, main_sha FROM merge_token ORDER BY repo;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TokenRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3),
                                      r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    TokenRow ReadToken(SqliteTransaction? tx, RepoId repo)
    {
        // Rows appear on first use: a repository that has never been landed in has no
        // token row, which is indistinguishable from a free one.
        //
        // The display name is refreshed on every read, deliberately: the row's identity is
        // its path, so the name is free to follow whatever the repository is called today
        // without ever splitting the row in two. A TICKET's name is the opposite — frozen,
        // because its claims were written relative to it.
        using (var ins = _db.CreateCommand())
        {
            if (tx is not null) ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO merge_token(repo_path, repo, generation) VALUES ($p, $r, 0);
                UPDATE merge_token SET repo = $r WHERE repo_path = $p AND repo <> $r;
                """;
            ins.Parameters.AddWithValue("$p", repo.Path);
            ins.Parameters.AddWithValue("$r", repo.Name);
            ins.ExecuteNonQuery();
        }
        using var c = _db.CreateCommand();
        if (tx is not null) c.Transaction = tx;
        c.CommandText = "SELECT holder_ticket, generation, expires_ts, main_sha FROM merge_token WHERE repo_path = $p;";
        c.Parameters.AddWithValue("$p", repo.Path);
        using var r2 = c.ExecuteReader();
        r2.Read();
        return new TokenRow(repo.Path, repo.Name, r2.IsDBNull(0) ? null : r2.GetInt64(0), r2.GetInt64(1),
                            r2.IsDBNull(2) ? null : r2.GetString(2), r2.IsDBNull(3) ? null : r2.GetString(3));
    }

    static bool Expired(TokenRow t) => t.ExpiresTs is not null && DateTime.Parse(t.ExpiresTs).ToUniversalTime() < DateTime.UtcNow;

    /// <summary>Request the merge token. Lease + FIFO in one transaction. An expired
    /// holder is reclaimed here — a crashed holder cannot wedge the queue (§7, §12).</summary>
    public (string Status, long Generation, int Position) TokenRequest(long ticketId, RepoId repo, int leaseSec, Func<string> mainSha)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);

            if (t.Holder is not null && Expired(t))
            {
                TxSet(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE repo_path = $p;", repo.Path);
                TxEvent(tx, "token_expired_reclaimed", $"{repo.Name}: was ticket {t.Holder}");
                t = t with { Holder = null };
            }

            if (t.Holder == ticketId)
            {
                tx.Commit();
                return ("granted", t.Generation, 0);
            }

            // ensure enqueued — the queue is per repository, like the token
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT OR IGNORE INTO token_queue(ticket_id, repo, repo_path, enqueued_ts) VALUES ($t, $r, $p, $ts);";
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$r", repo.Name);
                c.Parameters.AddWithValue("$p", repo.Path);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }

            long head;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT ticket_id FROM token_queue WHERE repo_path = $p ORDER BY id LIMIT 1;";
                c.Parameters.AddWithValue("$p", repo.Path);
                head = (long)c.ExecuteScalar()!;
            }

            if (t.Holder is null && head == ticketId)
            {
                var sha = mainSha();
                using var c = _db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = """
                    DELETE FROM token_queue WHERE ticket_id = $t;
                    UPDATE merge_token SET holder_ticket = $t, generation = generation + 1,
                        granted_ts = $ts, expires_ts = $exp, main_sha = $sha WHERE repo_path = $p;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$p", repo.Path);
                c.Parameters.AddWithValue("$ts", Now());
                c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
                c.Parameters.AddWithValue("$sha", sha);
                c.ExecuteNonQuery();
                var gen = ReadToken(tx, repo).Generation;
                TxEvent(tx, "token_granted", $"ticket {ticketId} repo {repo.Name} gen {gen} main {sha[..8]} lease {leaseSec}s");
                tx.Commit();
                return ("granted", gen, 0);
            }

            int pos;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = """
                    SELECT COUNT(*) FROM token_queue
                    WHERE repo_path = $p AND id <= (SELECT id FROM token_queue WHERE ticket_id = $t);
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$p", repo.Path);
                pos = Convert.ToInt32(c.ExecuteScalar());
            }
            TxEvent(tx, "token_queued", $"ticket {ticketId} repo {repo.Name} position {pos}");
            tx.Commit();
            return ("queued", t.Generation, pos);
        }
    }

    public bool TokenRenew(long ticketId, RepoId repo, int leaseSec)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder != ticketId || Expired(t)) { tx.Rollback(); return false; }
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE merge_token SET expires_ts = $exp WHERE repo_path = $p;";
            c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
            c.Parameters.AddWithValue("$p", repo.Path);
            c.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public void TokenRelease(long ticketId, RepoId repo)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder == ticketId)
            {
                TxSet(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE repo_path = $p;", repo.Path);
                TxEvent(tx, "token_released", $"ticket {ticketId} repo {repo.Name}");
            }
            tx.Commit();
        }
    }

    /// <summary>The land fence + commit, one transaction (§7): holder identity and lease
    /// re-checked HERE, in the same transaction that records the land and frees the
    /// claims. Returns false if the fence refuses.</summary>
    public bool LandCommit(long ticketId, RepoId repo, out string reason)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder != ticketId) { reason = $"token holder for {repo.Name} is {(t.Holder?.ToString() ?? "nobody")}, not ticket {ticketId}"; tx.Rollback(); return false; }
            if (Expired(t)) { reason = "lease expired"; tx.Rollback(); return false; }

            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = """
                    UPDATE tickets SET state = 'landed', landed_ts = $ts WHERE id = $t;
                    DELETE FROM claims WHERE ticket_id = $t;
                    DELETE FROM token_queue WHERE ticket_id = $t;
                    UPDATE merge_token SET holder_ticket = NULL WHERE repo_path = $p;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$p", repo.Path);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }
            TxEvent(tx, "landed", $"ticket {ticketId} repo {repo.Name} gen {t.Generation}; claims released");
            tx.Commit();
            reason = "";
            return true;
        }
    }

    // ------------------------------------------------------------- swaps (§13/§14)

    public record SwapRow(long Id, string Exe, string Build, int Schema, int ShimProtocol,
                          string? Blocker, string Mode, string State);

    public long SwapCreate(string exe, string build, int schema, int shimProto, string? blocker, string mode, string state)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            // One live proposal at a time: a newer build supersedes an older parked one.
            TxExec(tx, "UPDATE swaps SET state = 'superseded' WHERE state IN ('pending','armed','held');");
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = """
                INSERT INTO swaps(requested_ts, exe, build, schema_version, shim_protocol, blocker, mode, state)
                VALUES ($ts, $e, $b, $sc, $sp, $bl, $m, $st); SELECT last_insert_rowid();
                """;
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$e", exe);
            c.Parameters.AddWithValue("$b", build);
            c.Parameters.AddWithValue("$sc", schema);
            c.Parameters.AddWithValue("$sp", shimProto);
            c.Parameters.AddWithValue("$bl", (object?)blocker ?? DBNull.Value);
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$st", state);
            var id = (long)c.ExecuteScalar()!;
            tx.Commit();
            return id;
        }
    }

    public SwapRow? SwapLive()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                SELECT id, exe, build, schema_version, shim_protocol, blocker, mode, state
                FROM swaps WHERE state IN ('pending','armed','held') ORDER BY id DESC LIMIT 1;
                """;
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new SwapRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                               r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7));
        }
    }

    public void SwapSet(long id, string mode, string state, string? note = null)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE swaps SET mode = $m, state = $st, decided_ts = $ts, note = COALESCE($n, note) WHERE id = $id;";
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$st", state);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public List<string> SwapsAll()
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, requested_ts, build, state, mode, COALESCE(blocker,'-') FROM swaps ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add($"swap {r.GetInt64(0)}  {r.GetString(1)}  build={r.GetString(2)}  state={r.GetString(3)}  mode={r.GetString(4)}  blocker={r.GetString(5)}");
            return list;
        }
    }

    void TxExec(SqliteTransaction tx, string sql)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>One statement inside a transaction, bound to one repo-path parameter (<c>$p</c>).</summary>
    void TxSet(SqliteTransaction tx, string sql, string repoPath)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.Parameters.AddWithValue("$p", repoPath);
        c.ExecuteNonQuery();
    }

    void TxEvent(SqliteTransaction tx, string kind, string detail)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT INTO events(ts, kind, lane_id, detail) VALUES ($ts, $k, NULL, $d);";
        c.Parameters.AddWithValue("$ts", Now());
        c.Parameters.AddWithValue("$k", kind);
        c.Parameters.AddWithValue("$d", detail);
        c.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
