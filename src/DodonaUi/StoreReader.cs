using System.IO;
using Dodona;
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

    /// <summary>Takes the store PATH, not a project root. A workspace is named rather than
    /// located (WORKSPACES-CONCIERGE.md §1), so its store lives under
    /// %LOCALAPPDATA%\Dodona\workspaces\&lt;id&gt;\ and there is no root to append
    /// `.dodona\store.db` to any more. `--attach` still points at a copied file.</summary>
    public StoreReader(string storePath) => _path = storePath;

    bool? _hasCompressed;
    bool? _hasLaneCwd;

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

    /// <summary>Does this store record where each lane RUNS (`lanes.cwd`, schema v8)? Same
    /// shape and same reason as <see cref="HasCompressed"/>: the UI is read-only, cannot
    /// migrate, and `--attach` is pointed at copies of older stores on purpose. Naming a
    /// column that is not there throws into Lanes()' catch and returns NO LANES, which blanks
    /// the whole grid -- a far worse answer than "this store predates the column".</summary>
    bool HasLaneCwd()
    {
        if (_hasLaneCwd is bool known) return known;
        var found = false;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM pragma_table_info('lanes') WHERE name = 'cwd';";
            found = Convert.ToInt64(c.ExecuteScalar() ?? 0L) > 0;
        }
        catch { }
        _hasLaneCwd = found;
        return found;
    }

    /// <summary>`Cwd` is where the lane's process actually runs, and it is what the pane's
    /// project tag is derived from (docs/LOCATIONS-PLAN.md P1.2). Empty for a store that
    /// predates schema v8, which reads as "no project to name" rather than as a defect.</summary>
    public record LaneR(long Id, string Title, string State, string Presence, string Role, string Cwd);

    public List<LaneR> Lanes()
    {
        var list = new List<LaneR>();
        if (!Open()) return list;
        try
        {
            var cwd = HasLaneCwd();
            using var c = _db!.CreateCommand();
            c.CommandText = cwd
                ? "SELECT id, title, state, presence, role, COALESCE(cwd, '') FROM lanes ORDER BY id;"
                : "SELECT id, title, state, presence, role, '' FROM lanes ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new LaneR(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
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

    /// <summary>Highest user_input row id per lane — the pulse trigger: when this moves,
    /// something the operator said just LANDED in that lane, and the pane flashes so the
    /// eye can follow the routing without reading a receipt.</summary>
    public Dictionary<long, long> LastInput()
    {
        var map = new Dictionary<long, long>();
        if (!Open()) return map;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT lane_id, MAX(id) FROM pane_events WHERE kind = 'user_input' GROUP BY lane_id;";
            using var r = c.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(1)) map[r.GetInt64(0)] = r.GetInt64(1);
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
    /// the lane, what came back at the end of a turn, what the system announced, and, since
    /// 2026-08-19, what the agent has been DOING meanwhile: `progress` rows, folded (see
    /// PaneProgress.cs, which carries the measurement and the operator's own words for what the
    /// silent version felt like).
    ///
    /// Mid-turn `agent_line` narration is still absent, and that part of the argument
    /// stands: an agent ends its turn when it needs you, so anything that needs you IS a
    /// result. What did NOT stand was the other half — "what it is doing meanwhile is
    /// already on the presence line". Presence is ONE COLUMN, overwritten by every event,
    /// so it can only ever hold the newest tool; eighteen tool calls in one measured turn
    /// left no trace of the first seventeen and the pane sat silent for minutes. A
    /// `progress` row is that trace, decided in code, no model, one line per run of
    /// same-verb steps — so the §2.2 volume cut is kept while the blind spot is not.
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
            var kinds = v7 ? $"'user_input','result','announcement','error','{PaneProgress.Kind}'"
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
        // The overlay folds nothing — it is the "what actually came over the wire" answer
        // and folding it would be the same lie in a smaller font. The pane folds, because
        // a run of six reads is one fact about the turn, not six.
        if (all) return list;
        return PaneProgress.Fold(list.Select(x => (x.Kind, x.Body)))
                       .Select(x => new LineSnap(x.Kind, x.Body)).ToList();
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

    /// <summary>Which lanes the operator has collapsed. A row, not a UI field, so the choice
    /// survives closing the window and is the same for every window over this workspace.</summary>
    public HashSet<long> CollapsedLanes()
    {
        var set = new HashSet<long>();
        foreach (var part in (Kv("collapsed_lanes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(part.Trim(), out var id)) set.Add(id);
        return set;
    }

    /// <summary>An open question in this workspace, oldest first — the ask overlay's source
    /// (LOCATIONS-PLAN P4.1, D-L4). A ROW, so the ask survives the window closing and every
    /// window over this workspace shows the same one.
    ///
    /// The catch is load-bearing, not defensive habit: the `questions` table is created by the
    /// daemon and this reader is read-only, so a UI launched against a store no daemon has ever
    /// opened — or against an `--attach`ed copy of an older store — names a table that is not
    /// there. Returning EMPTY is the right answer to "is anything being asked"; throwing here
    /// would take the whole ask down, and rule 12 of this phase is that the overlay failing must
    /// never take the window with it (the same reason a corrupt `ui.json` is silently ignored:
    /// the box you would use to complain lives inside the window).</summary>
    public List<QuestionR> OpenQuestions()
    {
        var list = new List<QuestionR>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT id, ts, input, candidates FROM questions WHERE state = 'open' ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new QuestionR(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        catch { /* no questions table (older store, or no daemon has opened it yet): nothing is being asked */ }
        return list;
    }

    /// <summary>Only the four columns an ask RENDERS. `state` is in the WHERE clause and
    /// `kind`/`subject` are the daemon's business — a reader that pulled them would invite a
    /// window that branched on kind, which is how one component becomes two.</summary>
    public record QuestionR(long Id, string Ts, string Input, string Candidates);

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
