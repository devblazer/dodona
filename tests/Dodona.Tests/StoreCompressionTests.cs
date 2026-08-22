using System.IO;
using Dodona;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// ══ THE FIRST DISK-TOUCHING UNIT FIXTURE IN THIS REPOSITORY (plan W5, falsifier 4) ══
///
/// A real <see cref="Store"/> on a real temp file. Plan 3.5 is explicit that `Store` is never
/// faked, because *"the properties ARE the transactions"* -- and the property below is a
/// property of one `UPDATE` statement, so a stand-in would be asserting about itself.
///
/// It needs NO SEAM AT ALL, which is why the pilot took it: `Store(string path)` is public on an
/// internal class and `Dodona.csproj` already grants `InternalsVisibleTo("Dodona.Tests")`. Its
/// acceptance ancestor needed a daemon, a compressor pool, two fake agents and a live window to
/// reach the same `UPDATE`.
///
/// COST, and it is a reading the plan asked for at the kill switch rather than after it: W4
/// measured a real `Store` at **56 ms per case** against the operator's one-to-two second unit
/// budget. There is ONE case here, and `LaneSinkContract` adds four more. The bound W4 recorded
/// is roughly 60 more store cases before the budget goes -- so a bulk slice wants a SHARED
/// fixture (`IClassFixture`), not this shape repeated.
/// </summary>
public class StoreCompressionTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dodona-compress-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _path;
    readonly Store _store;

    public StoreCompressionTests()
    {
        _path = Path.Combine(_dir, "store.db");
        _store = new Store(_path);
    }

    /// <summary>
    /// MOVED from `compression:raw_body_is_never_overwritten` (`compression-acceptance.ps1`),
    /// which read `SELECT length(body)` off the store after a turn had been compressed and
    /// demanded it still equal the agent's own paragraph.
    ///
    /// THE WHOLE OF SELECTIVE COMPRESSION RESTS ON THIS. Compression is an improvement to a row
    /// that was already complete and already on screen: the pane renders
    /// `COALESCE(compressed, body)`, and the overlay is the raw truth. If the short version could
    /// overwrite the long one, every failure of the compressor -- a bad model turn, a truncation,
    /// a pool that died -- would destroy the agent's own words instead of falling back to them.
    /// `Store.PaneCompressed`'s own comment is one line long and says *"Never touches `body`"*;
    /// this is that comment as enforcement.
    ///
    /// Read back with SQL against the file the real `Store` wrote, exactly as the acceptance
    /// check did -- `Store` exposes no reader for `body` + `compressed`, and inventing one for a
    /// test would be widening the design's API because a test wanted in.
    /// </summary>
    [Fact]
    public void raw_body_is_never_overwritten()
    {
        var lane = _store.LaneCreate("SKY");
        const string longBody =
            "the shoreline foam looked wrong at grazing angles because the mask came from wave height alone, " +
            "so every crest above the threshold merged into one flat white band; it now uses height times " +
            "curvature and only breaking crests foam";
        const string shortVersion = "foam mask now uses height times curvature";

        var rowId = _store.PaneEventId(lane, "result", longBody, 1, "{}");
        Assert.True(rowId > 0);
        Assert.True(_store.PaneCompressed(rowId, shortVersion));

        var (body, compressed) = ReadRow(rowId);
        Assert.Equal(longBody, body);
        Assert.Equal(shortVersion, compressed);
        // Said as the acceptance check said it, because the check's failure detail was a LENGTH
        // and a reader comparing the two records wants the same number in both.
        Assert.Equal(longBody.Length, body.Length);
    }

    (string Body, string? Compressed) ReadRow(long id)
    {
        using var db = new SqliteConnection($"Data Source={_path}");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT body, compressed FROM pane_events WHERE id = $id;";
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        Assert.True(r.Read(), "no pane_events row with id " + id);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a WAL twin still held: the OS's problem, not a failing test */ }
    }
}
