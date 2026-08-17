using System.IO;
using Microsoft.Data.Sqlite;

namespace DodonaUi;

/// <summary>
/// The UI's read side (§12: everything the UI shows is a row). A read-only connection
/// over the daemon's WAL store — readers never block the single writer. Every method
/// degrades to empty when the store is missing or mid-migration: a UI launched before
/// its daemon just shows empty slots until rows appear.
/// </summary>
sealed class StoreReader : IDisposable
{
    readonly string _path;
    SqliteConnection? _db;

    public StoreReader(string root) => _path = Path.Combine(root, ".dodona", "store.db");

    bool Open()
    {
        if (_db is not null) return true;
        if (!File.Exists(_path)) return false;
        try { _db = new SqliteConnection($"Data Source={_path};Mode=ReadOnly"); _db.Open(); return true; }
        catch { _db?.Dispose(); _db = null; return false; }
    }

    public record LaneR(long Id, string Title, string State, string Presence, string Role);

    public List<LaneR> Lanes()
    {
        var list = new List<LaneR>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT id, title, state, presence, role FROM lanes ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new LaneR(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        catch { }
        return list;
    }

    /// <summary>Unacked announcement count per lane — the only thing that badges (§8:
    /// progress never badges).</summary>
    public Dictionary<long, int> Badges()
    {
        var map = new Dictionary<long, int>();
        if (!Open()) return map;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT lane_id, COUNT(*) FROM pane_events WHERE kind = 'announcement' AND acked = 0 GROUP BY lane_id;";
            using var r = c.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetInt32(1);
        }
        catch { }
        return map;
    }

    /// <summary>Last n pane lines. The pane replay filters wire noise; the overlay
    /// (all=true) shows everything — raw one keystroke away (§12).</summary>
    public List<LineSnap> Tail(long laneId, int n, bool all = false)
    {
        var list = new List<LineSnap>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = all
                ? "SELECT kind, body FROM (SELECT id, kind, body FROM pane_events WHERE lane_id = $l ORDER BY id DESC LIMIT $n) ORDER BY id;"
                : "SELECT kind, body FROM (SELECT id, kind, body FROM pane_events WHERE lane_id = $l AND kind IN ('user_input','agent_line','result','announcement') ORDER BY id DESC LIMIT $n) ORDER BY id;";
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new LineSnap(r.GetString(0), r.GetString(1)));
        }
        catch { }
        return list;
    }

    public record FeedR(long Id, long LaneId, string Ts, string Body, bool Acked);

    /// <summary>The decision feed (§8): announcements across all lanes, newest first,
    /// acked rows still visible (greyed) — persistence until acked is the point.</summary>
    public List<FeedR> Feed(int n)
    {
        var list = new List<FeedR>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT id, lane_id, ts, body, acked FROM pane_events WHERE kind = 'announcement' ORDER BY id DESC LIMIT $n;";
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new FeedR(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1));
        }
        catch { }
        return list;
    }

    public string? Kv(string key)
    {
        if (!Open()) return null;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT value FROM kv WHERE key = $k;";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    public void Dispose() => _db?.Dispose();
}
