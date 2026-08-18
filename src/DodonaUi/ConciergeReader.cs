using System.IO;
using Microsoft.Data.Sqlite;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The shell's read side over the CONCIERGE's store (WORKSPACES-CONCIERGE.md §2/§6) — the
/// merged-feed spine. Read-only WAL, exactly like <see cref="StoreReader"/>, and for the same
/// reason: group-scope announcements are rows, the UI owns nothing, and a concierge that is
/// not running must never blank the feed.
///
/// This is the shell reading the concierge, which §2 permits; the rule it forbids is a
/// WORKSPACE DAEMON reading this store, because that would make the concierge a thing work
/// waits on rather than a thing that routes sentences.
/// </summary>
sealed class ConciergeReader : IDisposable
{
    readonly string _path;
    SqliteConnection? _db;

    public ConciergeReader() => _path = Paths.ConciergeStore;

    bool Open()
    {
        if (_db is not null) return true;
        if (!File.Exists(_path)) return false;
        try { _db = new SqliteConnection($"Data Source={_path};Mode=ReadOnly"); _db.Open(); return true; }
        catch { _db?.Dispose(); _db = null; return false; }
    }

    public record CxFeedRow(long Id, string Ts, string Body, bool Acked);

    /// <summary>The concierge's own voice, newest first. Acked rows stay visible and grey —
    /// persistence until acked is the point, the same as a workspace's decision feed (§8).</summary>
    public List<CxFeedRow> Feed(int n)
    {
        var list = new List<CxFeedRow>();
        if (!Open()) return list;
        try
        {
            using var c = _db!.CreateCommand();
            c.CommandText = "SELECT id, ts, body, acked FROM feed ORDER BY id DESC LIMIT $n;";
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new CxFeedRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1));
        }
        catch { /* concierge store missing or mid-migration: the feed is just workspaces today */ }
        return list;
    }

    public void Dispose() => _db?.Dispose();
}
