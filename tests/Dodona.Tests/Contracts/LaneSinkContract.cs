using System.IO;
using Dodona;
using Xunit;

namespace Dodona.Testing.Ledger;

/// <summary>
/// ══ ONE BODY, TWO SUBJECTS (plan 3.2, D2 -- behavioural drift) ══
///
/// <see cref="Anchor.Interface"/> reaches SHAPE drift and nothing else: a double that satisfies
/// <c>ILaneSink</c> and DECIDES DIFFERENTLY is invisible to the compiler. So every `Interface`
/// anchor carries a contract, and this is <see cref="RecordingLaneSink"/>'s. The facts below run
/// unchanged against the recording sink and against a REAL <see cref="Store"/> on a real
/// temp file -- no daemon, no pipe, no process, and the real subject is the one production ships.
///
/// ══ THE CASE THIS EXISTS FOR, NAMED IN THE PLAN BEFORE IT WAS WRITTEN ══
///
/// Plan 3.3: *"wrote the naive recording sink (`append; return list.Count`) -> `LaneSinkContract`'s
/// dedup case"*. `Store.PaneEventId` is `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)` and returns
/// **0** for a repeat (`Store.cs`, the `ExecuteNonQuery() &lt;= 0` line and the comment beside it:
/// *"duplicate seq: the shim redelivered"*). That return value is not a detail -- it is the whole
/// of the shim's exactly-once replay, and a sink that handed back a fresh id every time would let
/// a redelivery test pass while production silently doubled every line the shim replayed.
///
/// The NULL-seq half is the same property from the other side. SQLite treats NULLs as distinct in
/// a unique index, and `LaneRuntime`'s `tool_use` branch writes its derived progress row with
/// `seq: null` ON PURPOSE, so that it never competes for the key. A double that deduped on null
/// would drop every progress row after the first.
/// </summary>
public abstract class LaneSinkContract
{
    /// <summary>The subject. One per test instance -- xunit builds a fresh instance per fact, so
    /// no two facts can see each other's rows.</summary>
    private protected abstract ILaneSink Sink { get; }

    /// <summary>A lane id the subject will accept. Not on <c>ILaneSink</c> (the parser never
    /// creates a lane), so each subject supplies its own.</summary>
    protected abstract long NewLane(string title);

    protected abstract string? SessionOf(long lane);
    protected abstract string PresenceOf(long lane);

    [Fact]
    public void A_pane_row_gets_an_id_and_a_repeated_seq_is_ignored()
    {
        var lane = NewLane("SKY");
        var first = Sink.PaneEventId(lane, "agent_line", "hello", 7, "{}");
        Assert.True(first > 0, "a new (lane, seq) must produce a row id, got " + first);

        var repeat = Sink.PaneEventId(lane, "agent_line", "hello again", 7, "{}");
        Assert.Equal(0, repeat);
    }

    [Fact]
    public void A_null_seq_never_dedups()
    {
        var lane = NewLane("SKY");
        var a = Sink.PaneEventId(lane, PaneProgress.Kind, "read a.cs", null, "{}");
        var b = Sink.PaneEventId(lane, PaneProgress.Kind, "read b.cs", null, "{}");
        Assert.True(a > 0 && b > 0 && a != b,
            "two null-seq rows must both land with distinct ids, got " + a + " and " + b);
    }

    /// <summary>
    /// A REPLACEMENT AGENT'S FIRST LINE IS NOT THE DEAD ONE'S FIRST LINE.
    ///
    /// The dedup case above is what makes shim replay exactly-once, and it is also what silently
    /// ate every line a respawned agent wrote: a new shim numbers from its own buffer index, so
    /// it opens at 0, and `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)` read that as a redelivery.
    /// Measured 2026-08-22 through `m3:wake_revives_the_lane` — a lane rendering alive, connected
    /// and permanently mute, its `system init` row missing too.
    ///
    /// Three rungs, and the middle one is the reason this cannot just be "always rebase": the
    /// SAME shim reconnecting after the daemon died is the replay the dedup exists for, and it
    /// must get the same base back or every buffered line doubles.
    /// </summary>
    [Fact]
    public void A_new_shim_numbers_above_the_dead_ones_rows_and_a_reconnect_does_not()
    {
        var lane = NewLane("WATER");
        var first = Sink.SeqBase(lane, "100:200");
        Assert.Equal(0, first);
        Assert.True(Sink.PaneEventId(lane, "system", "init", first + 0, "{}") > 0);
        Assert.True(Sink.PaneEventId(lane, "result", "water ready", first + 1, "{}") > 0);

        // The SAME shim reconnecting: same base, so its replay still dedupes to nothing.
        Assert.Equal(first, Sink.SeqBase(lane, "100:200"));
        Assert.Equal(0, Sink.PaneEventId(lane, "result", "water ready", first + 1, "{}"));

        // A REPLACEMENT shim: above everything stored, so its own seq 0 lands.
        var second = Sink.SeqBase(lane, "300:400");
        Assert.True(second > 1, $"a new shim must start above seq 1, got {second}");
        Assert.True(Sink.PaneEventId(lane, "system", "init", second + 0, "{}") > 0,
            "the replacement agent's first line was dropped as a duplicate -- this is the bug");
        Assert.True(Sink.PaneEventId(lane, "result", "woke up fine", second + 1, "{}") > 0);

        // A hello naming no shim rebases nothing: duplicates are worse than the bug (ILaneSink).
        Assert.Equal(second, Sink.SeqBase(lane, ""));
    }

    /// <summary>NULL seqs are the daemon's own rows -- announcements, user_input -- and they must
    /// not push a replacement shim's base, or the two numbering schemes start interfering.</summary>
    [Fact]
    public void A_null_seq_row_does_not_move_the_next_shims_base()
    {
        var lane = NewLane("SKY");
        var first = Sink.SeqBase(lane, "1:2");
        Assert.True(Sink.PaneEventId(lane, "result", "hello", first + 0, "{}") > 0);
        for (var i = 0; i < 5; i++) Assert.True(Sink.PaneEventId(lane, "announcement", $"a{i}", null, null) > 0);
        Assert.Equal(1, Sink.SeqBase(lane, "3:4"));
    }

    [Fact]
    public void PaneEvent_agrees_with_PaneEventId()
    {
        var lane = NewLane("SKY");
        Assert.True(Sink.PaneEvent(lane, "result", "done", 1, "{}"));
        Assert.False(Sink.PaneEvent(lane, "result", "done twice", 1, "{}"));
    }

    [Fact]
    public void The_session_and_presence_last_written_are_what_stands()
    {
        var lane = NewLane("SKY");
        Sink.LaneSession(lane, "fake-1111");
        Sink.LaneSession(lane, "fake-2222");
        Assert.Equal("fake-2222", SessionOf(lane));

        Sink.LanePresence(lane, "thinking...");
        Sink.LanePresence(lane, "idle");
        Assert.Equal("idle", PresenceOf(lane));
    }
}

/// <summary>The contract over the double. Named for the CONTRACT and not `Fake*`/`Recording*`:
/// rung 1 refuses an unanchored type with that name anywhere in the repo, and it was right to
/// (W4 caught `FakeRecognizerContract` the same way).</summary>
public sealed class LaneSinkContractOverTheRecordingSink : LaneSinkContract
{
    readonly RecordingLaneSink _sink = new();
    private protected override ILaneSink Sink => _sink;
    protected override long NewLane(string title) => _sink.NewLane(title);
    protected override string? SessionOf(long lane) => _sink.SessionOf(lane);
    protected override string PresenceOf(long lane) => _sink.PresenceOf(lane);
}

/// <summary>
/// The contract over the thing production ships. A REAL <see cref="Store"/>, on a real file, in a
/// throwaway directory -- the schema, the unique index and `INSERT OR IGNORE` are the subject, and
/// plan 3.5 is explicit that `Store` is never faked because *"the properties ARE the transactions"*.
///
/// MEASURED COST (W4's falsifier-4 reading, `tests\ledger\README.md`): ~56 ms per case for a real
/// `Store`. Four cases here, so ~0.2 s of the operator's one-to-two second unit budget, and it is
/// spent on the only subject that can prove the double is not lying.
/// </summary>
public sealed class LaneSinkContractOverARealStore : LaneSinkContract, IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dodona-lanesink-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Store _store;

    public LaneSinkContractOverARealStore() => _store = new Store(Path.Combine(_dir, "store.db"));

    private protected override ILaneSink Sink => _store;
    protected override long NewLane(string title) => _store.LaneCreate(title);
    protected override string? SessionOf(long lane) => _store.LanesAll().First(l => l.Id == lane).Session;
    protected override string PresenceOf(long lane) => _store.LanesAll().First(l => l.Id == lane).Presence;

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a WAL twin still held: the temp dir is the OS's problem, not a test failure */ }
    }
}
