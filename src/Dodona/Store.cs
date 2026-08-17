using Microsoft.Data.Sqlite;

namespace Dodona;

/// <summary>
/// The registry (design §12). One writer — the daemon — behind one connection and one
/// lock. WAL + synchronous=FULL. Every state change is one transaction; pane_events
/// dedupes shim redelivery via UNIQUE(lane_id, seq), which is what makes the shim's
/// at-least-once delivery exactly-once in the store.
/// </summary>
sealed class Store : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    public Store(string path)
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
        {
            Exec("""
                CREATE TABLE lanes(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'alive',   -- alive | unreachable | dead
                    pipe_name TEXT NOT NULL DEFAULT '',
                    session_id TEXT,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE pane_events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lane_id INTEGER NOT NULL,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,                    -- user_input | agent_line | result | system | wire
                    body TEXT NOT NULL,
                    seq INTEGER,                           -- shim seq for agent lines; NULL for local rows
                    raw TEXT,                              -- the raw wire line, for debugging (§12)
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
    }

    static string Now() => DateTime.UtcNow.ToString("o");

    void Exec(string sql)
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
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

    public List<(long Id, string Title, string State, string Pipe, string? Session)> LanesAll()
    {
        lock (_lock)
        {
            var list = new List<(long, string, string, string, string?)>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, title, state, pipe_name, session_id FROM lanes ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    /// <summary>Insert a pane event. Rows with a seq dedupe on (lane_id, seq): shim
    /// redelivery after a daemon death lands exactly once. Returns true if inserted.</summary>
    public bool PaneEvent(long laneId, string kind, string body, long? seq, string? raw)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO pane_events(lane_id, ts, kind, body, seq, raw) VALUES ($l, $ts, $k, $b, $s, $r);";
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$b", body);
            c.Parameters.AddWithValue("$s", (object?)seq ?? DBNull.Value);
            c.Parameters.AddWithValue("$r", (object?)raw ?? DBNull.Value);
            return c.ExecuteNonQuery() > 0;
        }
    }

    public List<string> Tail(long laneId, int n)
    {
        lock (_lock)
        {
            var rows = new List<string>();
            using var c = _db.CreateCommand();
            c.CommandText = """
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

    public void Dispose() => _db.Dispose();
}
