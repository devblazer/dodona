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

    bool? _hasCompressed;

    bool Open()
    {
        if (_db is not null) return true;
        if (!File.Exists(_path)) return false;
        try { _db = new SqliteConnection($"Data Source={_path};Mode=ReadOnly"); _db.Open(); return true; }
        catch { _db?.Dispose(); _db = null; return false; }
    }

    /// <summary>Does this store know about compression (schema v7)? The UI is read-only
    /// and cannot migrate, and `--attach` is pointed at copies of older stores on purpose
    /// — asking for a column that is not there would throw into the catch below and blank
    /// every pane, which is a much worse answer than "this store predates compression".</summary>
    bool HasCompressed()
    {
        if (_hasCompressed is bool known) return known;
        var found = false;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM pragma_table_info('pane_events') WHERE name = 'compressed';";
            found = Convert.ToInt64(c.ExecuteScalar() ?? 0L) > 0;
        }
        catch { }
        _hasCompressed = found;
        return found;
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
    /// progress never badges) — and DEFERRED while the lane is mid-turn
    /// (docs/LANE-LIFECYCLE.md §4): a badge that appears while the agent is still working
    /// says "something happened" when it must say "you are needed". The rows are written
    /// immediately either way; when the turn ends presence flips to idle and everything
    /// deferred flushes into the count at once. Blocked-on-you is never deferred — waiting
    /// on a merge approval is true the instant it happens.</summary>
    public Dictionary<long, int> Badges()
    {
        var map = new Dictionary<long, int>();
        if (!Open()) return map;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = """
                SELECT p.lane_id, COUNT(*) FROM pane_events p JOIN lanes l ON l.id = p.lane_id
                WHERE p.kind = 'announcement' AND p.acked = 0
                  AND (l.presence IN ('idle', 'landed', 'system', '')
                       OR l.presence LIKE 'waiting on you%'
                       OR l.state != 'alive'
                       OR p.body LIKE '%waiting on you%')
                GROUP BY p.lane_id;
                """;
            using var r = c.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetInt32(1);
        }
        catch { }
        return map;
    }

    /// <summary>When each lane last said anything on the wire — the liveness input: a
    /// static `working…` cannot tell thinking from wedged, a moving clock can.</summary>
    public Dictionary<long, DateTime> LastActivity()
    {
        var map = new Dictionary<long, DateTime>();
        if (!Open()) return map;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT lane_id, MAX(ts) FROM pane_events GROUP BY lane_id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1) && DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                    map[r.GetInt64(0)] = ts.ToUniversalTime();
        }
        catch { }
        return map;
    }

    /// <summary>Open tickets' repo per lane — the pane subtitle in a multi-repo workspace.</summary>
    public Dictionary<long, string> TicketRepoByLane()
    {
        var map = new Dictionary<long, string>();
        if (!Open()) return map;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT lane_id, repo FROM tickets WHERE state = 'open' AND lane_id IS NOT NULL;";
            using var r = c.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Last n pane lines. Two different questions, two different queries (§5, §12):
    ///
    /// The PANE answers "what has happened in this lane" — so it carries what was said to
    /// the lane, what came back at the end of a turn, and what the system announced, each
    /// in its shortest true form (`compressed` when the compressor got to it, the agent's
    /// own words when it did not). Mid-turn `agent_line` narration is deliberately absent:
    /// an agent ends its turn when it needs you, so anything that needs you IS a result,
    /// and what it is doing meanwhile is already on the presence line — derived in code,
    /// no model, no volume (§2.2). That is the 5–10× cut.
    ///
    /// The OVERLAY (all=true) answers "what actually came over the wire", so it filters
    /// nothing and compresses nothing — raw one keystroke away (§12).
    /// </summary>
    public List<LineSnap> Tail(long laneId, int n, bool all = false)
    {
        var list = new List<LineSnap>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            // Against a store that predates compression, behave EXACTLY as before —
            // mid-turn narration included. Dropping it there would remove the running
            // commentary and put nothing in its place, which is a worse pane than the
            // verbose one it replaced. Filtering is only earned once there is a shorter
            // truth to show instead.
            var v7 = HasCompressed();
            var shown = v7 ? "COALESCE(compressed, body)" : "body";
            // 'error' is a lane saying it is stuck (e.g. permission denied on its build
            // command) — the one mid-turn thing that must NOT wait for the turn to end.
            var kinds = v7 ? "'user_input','result','announcement','error'"
                           : "'user_input','agent_line','result','announcement','error'";
            c.CommandText = all
                ? "SELECT kind, body FROM (SELECT id, kind, body FROM pane_events WHERE lane_id = $l ORDER BY id DESC LIMIT $n) ORDER BY id;"
                : $"SELECT kind, body FROM (SELECT id, kind, {shown} AS body FROM pane_events WHERE lane_id = $l AND kind IN ({kinds}) ORDER BY id DESC LIMIT $n) ORDER BY id;";
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
