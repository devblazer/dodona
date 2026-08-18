using Microsoft.Data.Sqlite;

namespace Dodona;

/// <summary>
/// The concierge's own store (docs/WORKSPACES-CONCIERGE.md §2). Separate from every
/// workspace store and separate from the registry, with its own `user_version` — it is not
/// a workspace, so it must not carry a workspace's schema, and bumping `Ver.Schema` for
/// tables no workspace would ever use would make every ordinary swap non-seamless (§14).
///
/// **No workspace daemon ever reads this.** That is §2's hard rule, and it is what keeps
/// the concierge from becoming the persistent-coordinator serialization point §12 designed
/// out: the moment a workspace daemon needs an answer from here to do its work, the
/// machine has one queue again.
///
/// What is here is what must survive a window close, which is the whole argument for the
/// concierge being daemon-natured rather than living in the UI (m3 doctrine: the UI owns
/// nothing):
///
///   questions   — rung 4 asked the operator something and is waiting. A pending question
///                 that evaporated when a window closed would make asking worse than
///                 guessing.
///   resolutions — every group-scope decision and its rung. Free labeled data for tuning,
///                 the same reason `routing_decisions` exists one level down (§4).
///   feed        — the merged-feed spine (§6). Group-scope announcements belong to no
///                 workspace's column by definition, so they need a home of their own.
///   tiers/wire  — the two management model sessions and their transcript. Rows so a
///                 restart can reconcile a shim that is still running, exactly as the
///                 daemon's reconcile does.
///
/// It implements <see cref="ILaneSink"/> so <see cref="LaneRuntime"/> — the shim wire, the
/// stream-json parsing, exactly-once seq dedup — is reused rather than reimplemented. That
/// is shared machinery, not shared authority: see LaneSink.cs.
/// </summary>
sealed class ConciergeStore : IDisposable, ILaneSink
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    public const long TierLo = 1, TierHi = 2;      // fixed ids ⇒ stable pipe names across restarts

    public ConciergeStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        Migrate();
    }

    void Migrate()
    {
        long v;
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = "PRAGMA user_version;"; v = (long)c.ExecuteScalar()!; }
        if (v < 1)
            Exec("""
                CREATE TABLE tiers(
                    id INTEGER PRIMARY KEY,             -- 1 = cheap, 2 = expensive
                    name TEXT NOT NULL,
                    pipe_name TEXT NOT NULL DEFAULT '',
                    session_id TEXT,
                    state TEXT NOT NULL DEFAULT 'alive',
                    presence TEXT NOT NULL DEFAULT '',
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE wire(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tier_id INTEGER NOT NULL,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    body TEXT NOT NULL,
                    seq INTEGER,
                    raw TEXT,
                    UNIQUE(tier_id, seq)
                );
                CREATE TABLE resolutions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    input TEXT NOT NULL,
                    rung TEXT NOT NULL,                 -- path|registry|fuzzy|discovery|ask|focus|only
                    workspace_id TEXT,
                    confidence TEXT,
                    created INTEGER NOT NULL DEFAULT 0, -- a workspace was created for it
                    undone INTEGER NOT NULL DEFAULT 0,
                    latency_ms INTEGER
                );
                CREATE TABLE questions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    input TEXT NOT NULL,
                    candidates TEXT NOT NULL,           -- JSON [{id,name,why}]
                    state TEXT NOT NULL,                -- open | answered | withdrawn
                    answer TEXT,
                    answered_ts TEXT
                );
                CREATE TABLE feed(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    body TEXT NOT NULL,
                    acked INTEGER NOT NULL DEFAULT 0,
                    question_id INTEGER
                );
                CREATE TABLE events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    tier_id INTEGER,
                    detail TEXT
                );
                CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 1;
                """);
    }

    static string Now() => DateTime.UtcNow.ToString("o");

    void Exec(string sql)
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }

    T Read<T>(string sql, Action<SqliteCommand>? bind, Func<SqliteCommand, T> run)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            bind?.Invoke(c);
            return run(c);
        }
    }

    // ------------------------------------------------------------------ ILaneSink

    public void Event(string kind, long? tierId, string? detail) => Read(
        "INSERT INTO events(ts, kind, tier_id, detail) VALUES ($ts,$k,$t,$d);",
        c =>
        {
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$t", (object?)tierId ?? DBNull.Value);
            c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        },
        c => c.ExecuteNonQuery());

    public bool PaneEvent(long tierId, string kind, string body, long? seq, string? raw, bool acked = false) =>
        PaneEventId(tierId, kind, body, seq, raw, acked) > 0;

    public long PaneEventId(long tierId, string kind, string body, long? seq, string? raw, bool acked = false) => Read(
        "INSERT OR IGNORE INTO wire(tier_id, ts, kind, body, seq, raw) VALUES ($t,$ts,$k,$b,$s,$r); SELECT changes();",
        c =>
        {
            c.Parameters.AddWithValue("$t", tierId);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$b", body);
            c.Parameters.AddWithValue("$s", (object?)seq ?? DBNull.Value);
            c.Parameters.AddWithValue("$r", (object?)raw ?? DBNull.Value);
        },
        c => Convert.ToInt64(c.ExecuteScalar() ?? 0L));

    public void LaneSession(long id, string session) =>
        Set("UPDATE tiers SET session_id = $v WHERE id = $id;", id, session);

    public void LanePresence(long id, string presence) =>
        Set("UPDATE tiers SET presence = $v WHERE id = $id;", id, presence);

    public void KvSet(string key, string value) => Read(
        "INSERT INTO kv(key, value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value = $v;",
        c => { c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value); },
        c => c.ExecuteNonQuery());

    public string? KvGet(string key) => Read(
        "SELECT value FROM kv WHERE key = $k;",
        c => c.Parameters.AddWithValue("$k", key),
        c => c.ExecuteScalar() as string);

    void Set(string sql, long id, string value) => Read(sql,
        c => { c.Parameters.AddWithValue("$v", value); c.Parameters.AddWithValue("$id", id); },
        c => c.ExecuteNonQuery());

    // ------------------------------------------------------------------ tiers

    public record TierRow(long Id, string Name, string Pipe, string? Session, string State, string Presence);

    public List<TierRow> Tiers() => Read(
        "SELECT id, name, pipe_name, session_id, state, presence FROM tiers ORDER BY id;", null,
        c =>
        {
            var list = new List<TierRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TierRow(r.GetInt64(0), r.GetString(1), r.GetString(2),
                                     r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5)));
            return list;
        });

    public void TierUpsert(long id, string name, string pipe) => Read(
        """
        INSERT INTO tiers(id, name, pipe_name, created_ts) VALUES ($i,$n,$p,$ts)
        ON CONFLICT(id) DO UPDATE SET name = $n, pipe_name = $p, state = 'alive';
        """,
        c =>
        {
            c.Parameters.AddWithValue("$i", id);
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$p", pipe);
            c.Parameters.AddWithValue("$ts", Now());
        },
        c => c.ExecuteNonQuery());

    public void TierState(long id, string state) => Set("UPDATE tiers SET state = $v WHERE id = $id;", id, state);

    // ------------------------------------------------------------------ resolutions

    public long ResolutionInsert(string input, string rung, string? wsId, string? confidence, bool created, long latencyMs) => Read(
        """
        INSERT INTO resolutions(ts, input, rung, workspace_id, confidence, created, latency_ms)
        VALUES ($ts,$i,$r,$w,$c,$cr,$l);
        SELECT last_insert_rowid();
        """,
        c =>
        {
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", input);
            c.Parameters.AddWithValue("$r", rung);
            c.Parameters.AddWithValue("$w", (object?)wsId ?? DBNull.Value);
            c.Parameters.AddWithValue("$c", (object?)confidence ?? DBNull.Value);
            c.Parameters.AddWithValue("$cr", created ? 1 : 0);
            c.Parameters.AddWithValue("$l", latencyMs);
        },
        c => Convert.ToInt64(c.ExecuteScalar()!));

    public record ResolutionRow(long Id, string Ts, string Input, string Rung, string? WorkspaceId, string? Confidence, bool Created, bool Undone);

    public List<ResolutionRow> Resolutions(int n) => Read(
        "SELECT id, ts, input, rung, workspace_id, confidence, created, undone FROM resolutions ORDER BY id DESC LIMIT $n;",
        c => c.Parameters.AddWithValue("$n", n),
        c =>
        {
            var list = new List<ResolutionRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ResolutionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                           r.IsDBNull(4) ? null : r.GetString(4),
                                           r.IsDBNull(5) ? null : r.GetString(5),
                                           r.GetInt64(6) == 1, r.GetInt64(7) == 1));
            return list;
        });

    // ------------------------------------------------------------------ questions (rung 4)

    public long QuestionOpen(string input, string candidatesJson) => Read(
        "INSERT INTO questions(ts, input, candidates, state) VALUES ($ts,$i,$c,'open'); SELECT last_insert_rowid();",
        c =>
        {
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", input);
            c.Parameters.AddWithValue("$c", candidatesJson);
        },
        c => Convert.ToInt64(c.ExecuteScalar()!));

    public record QuestionRow(long Id, string Ts, string Input, string Candidates, string State, string? Answer);

    public QuestionRow? Question(long id) => Read(
        "SELECT id, ts, input, candidates, state, answer FROM questions WHERE id = $i;",
        c => c.Parameters.AddWithValue("$i", id),
        c =>
        {
            using var r = c.ExecuteReader();
            return r.Read()
                ? new QuestionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                                  r.IsDBNull(5) ? null : r.GetString(5))
                : null;
        });

    public List<QuestionRow> OpenQuestions() => Read(
        "SELECT id, ts, input, candidates, state, answer FROM questions WHERE state = 'open' ORDER BY id;", null,
        c =>
        {
            var list = new List<QuestionRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new QuestionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                                         r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        });

    public bool QuestionAnswer(long id, string answer) => Read(
        "UPDATE questions SET state = 'answered', answer = $a, answered_ts = $ts WHERE id = $i AND state = 'open';",
        c =>
        {
            c.Parameters.AddWithValue("$a", answer);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", id);
        },
        c => c.ExecuteNonQuery() > 0);

    // ------------------------------------------------------------------ the merged-feed spine (§6)

    public long Announce(string body, long? questionId = null) => Read(
        "INSERT INTO feed(ts, body, question_id) VALUES ($ts,$b,$q); SELECT last_insert_rowid();",
        c =>
        {
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$b", body);
            c.Parameters.AddWithValue("$q", (object?)questionId ?? DBNull.Value);
        },
        c => Convert.ToInt64(c.ExecuteScalar()!));

    public record FeedRow(long Id, string Ts, string Body, bool Acked, long? QuestionId);

    public List<FeedRow> Feed(int n) => Read(
        "SELECT id, ts, body, acked, question_id FROM feed ORDER BY id DESC LIMIT $n;",
        c => c.Parameters.AddWithValue("$n", n),
        c =>
        {
            var list = new List<FeedRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new FeedRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1,
                                     r.IsDBNull(4) ? null : r.GetInt64(4)));
            return list;
        });

    public bool FeedAck(long id) => Read(
        "UPDATE feed SET acked = 1 WHERE id = $i;",
        c => c.Parameters.AddWithValue("$i", id),
        c => c.ExecuteNonQuery() > 0);

    public void Dispose() => _db.Dispose();
}
