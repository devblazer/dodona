using Dodona;
using Dodona.Testing;

namespace Dodona.Testing.Ledger;

/// <summary>
/// ══ THE FLAGSHIP DOUBLE (docs/TEST-ARCHITECTURE-PLAN.md 3.6, built at W5) ══
///
/// Everything <see cref="LaneRuntime"/> writes, kept in memory so a test can read it back.
/// It exists so that the shim wire parser -- 130 lines of `switch` over `claude`'s
/// stream-json, the single richest piece of pure decision-making in the daemon -- can be
/// exercised without a daemon, a shim, a named pipe, a child process or a SQLite file.
///
/// ══ WHY THIS ONE CANNOT DRIFT BY CONSTRUCTION ══
///
/// <c>ILaneSink</c> has TWO shipping implementers -- <see cref="Store"/> and
/// <c>ConciergeStore</c> -- and neither of them is this class. So a seventh method on the
/// interface breaks a running system before it breaks a test, and the `Interface` anchor's
/// claim ("the compiler catches shape drift") is true here on its own merits rather than by
/// declaration. Plan 3.6 calls it the only one of five doubles that manages that, and it is the
/// reason the pilot slice was aimed at this seam and not at a bigger prize.
///
/// Shape is not behaviour, which is why `Interface` is never a sole anchor: `LaneSinkContract`
/// runs one body against this class AND against a real temp-file <see cref="Store"/>. The case
/// that matters there is the one plan 3.3 names in advance -- the naive recording sink
/// (`append; return list.Count`) is WRONG, because the real `PaneEventId` is
/// `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)` and returns **0** for a redelivered seq. That
/// dedup is the mechanism the shim's exactly-once replay rests on, so a double that quietly
/// returned a fresh id every time would make a test about redelivery pass while production lost
/// the property.
///
/// ══ THE WIRE IT DOES NOT REPLACE ══
///
/// `m0:orphaned_result_landed` (wire A1). This class replaces the STORE behind the parser; it
/// replaces nothing about a daemon dying mid-turn, a shim buffering into a dead pipe, and a
/// replacement daemon draining it exactly once. That stays real, and rung 2 refuses to let the
/// row it names be deleted or renamed out from under this declaration.
/// </summary>
[Double(Anchor.Interface, typeof(Store),
        Wire = "m0:orphaned_result_landed",
        Contract = "LaneSinkContract")]
sealed class RecordingLaneSink : ILaneSink
{
    /// <summary>One `pane_events` row, in the order the parser wrote it.</summary>
    internal sealed record PaneRow(long Id, long LaneId, string Kind, string Body, long? Seq, string? Raw, bool Acked);

    public List<PaneRow> Panes { get; } = new();
    public List<(string Kind, long? LaneId, string? Detail)> Events { get; } = new();
    /// <summary>Every presence written, in order. A LIST and not a column, because presence is
    /// the one field production overwrites on every event -- a test about a turn's SHAPE has to
    /// be able to see the sequence, while a test about what the tile SAYS reads the last one.</summary>
    public List<(long LaneId, string Presence)> Presences { get; } = new();
    public Dictionary<long, string> Sessions { get; } = new();
    public Dictionary<string, string> Kv { get; } = new(StringComparer.Ordinal);

    long _nextId;
    long _nextLane;
    readonly HashSet<(long, long)> _seen = new();

    /// <summary>Mirrors <c>Store.LaneCreate</c> for setup only -- it is NOT on
    /// <see cref="ILaneSink"/>, and the parser never calls it. The contract needs both subjects
    /// to be able to produce a lane id.</summary>
    public long NewLane(string title)
    {
        var id = ++_nextLane;
        Sessions.Remove(id);
        Presences.RemoveAll(p => p.LaneId == id);
        return id;
    }

    public void Event(string kind, long? laneId, string? detail) => Events.Add((kind, laneId, detail));

    public bool PaneEvent(long laneId, string kind, string body, long? seq, string? raw, bool acked = false) =>
        PaneEventId(laneId, kind, body, seq, raw, acked) > 0;

    /// <summary>
    /// `INSERT OR IGNORE` on `UNIQUE(lane_id, seq)`, in memory. A NULL seq never collides --
    /// SQLite treats NULLs as distinct in a unique index, and the parser depends on it: a
    /// derived progress row is written with `seq: null` precisely so it does not compete for the
    /// key that makes shim redelivery exactly-once (`LaneRuntime.cs`, the `tool_use` branch).
    /// </summary>
    public long PaneEventId(long laneId, string kind, string body, long? seq, string? raw, bool acked = false)
    {
        if (seq is not null && !_seen.Add((laneId, seq.Value))) return 0;
        var id = ++_nextId;
        Panes.Add(new PaneRow(id, laneId, kind, body, seq, raw, acked));
        return id;
    }

    /// <summary>`Store.SeqBase`, in memory, and the same three rungs in the same order: a known
    /// shim key gets its base back (a reconnect's replay must still dedupe), an empty key rebases
    /// nothing, and anything else starts above every seq already recorded for this lane. NULL
    /// seqs are skipped exactly as SQLite's MAX does — they were never the shim's numbering.</summary>
    public long SeqBase(long laneId, string shimKey)
    {
        var kShim = $"seqshim:{laneId}";
        var kBase = $"seqbase:{laneId}";
        var recorded = Kv.TryGetValue(kBase, out var kept) && long.TryParse(kept, out var v) ? v : 0L;
        if (shimKey.Length == 0 || (Kv.TryGetValue(kShim, out var prev) && prev == shimKey)) return recorded;
        long max = -1;
        foreach (var p in Panes) if (p.LaneId == laneId && p.Seq is { } s && s > max) max = s;
        var next = max + 1;
        Kv[kShim] = shimKey;
        Kv[kBase] = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return next;
    }

    public void LaneSession(long id, string session) => Sessions[id] = session;

    public void LanePresence(long id, string presence) => Presences.Add((id, presence));

    public void KvSet(string key, string value) => Kv[key] = value;

    // ---- readback helpers the tests and the contract use -------------------------------

    public string? SessionOf(long lane) => Sessions.TryGetValue(lane, out var s) ? s : null;

    public string PresenceOf(long lane)
    {
        for (var i = Presences.Count - 1; i >= 0; i--) if (Presences[i].LaneId == lane) return Presences[i].Presence;
        return "";
    }

    public IEnumerable<PaneRow> OfKind(string kind) => Panes.Where(p => p.Kind == kind);
}
