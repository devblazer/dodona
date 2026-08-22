namespace Dodona;

/// <summary>
/// Everything <see cref="LaneRuntime"/> needs to write, and nothing else.
///
/// It exists so the concierge (docs/WORKSPACES-CONCIERGE.md §2) can run management model
/// sessions over the SAME shim wire the workspace daemon uses — the stream-json parsing,
/// the presence derivation, the exactly-once seq dedup, the turn-final detection — without
/// owning a workspace <see cref="Store"/>. The concierge belongs to no workspace, so it has
/// no lanes, no tickets, no claims and no merge token, and it must not be handed a store
/// shaped to hold them.
///
/// The alternative was to give the concierge a full workspace store and let it keep its own
/// rows in tables it would never use, which would also have meant bumping the store schema
/// for every workspace — and a schema bump is the one thing that makes a swap not seamless
/// (§14). Six method signatures were the cheaper answer.
///
/// Sharing the wire MACHINERY is not sharing AUTHORITY. §2's cap on the concierge —
/// registry, routing, resolution, nothing else — is about what it may decide, and this
/// interface is deliberately too narrow to decide anything.
/// </summary>
interface ILaneSink
{
    void Event(string kind, long? laneId, string? detail);
    bool PaneEvent(long laneId, string kind, string body, long? seq, string? raw, bool acked = false);
    long PaneEventId(long laneId, string kind, string body, long? seq, string? raw, bool acked = false);
    /// <summary>
    /// WHERE THIS LANE'S seq NUMBERING STARTS, FOR THE SHIM THAT JUST SAID HELLO.
    ///
    /// A shim numbers its lines by the index of its own output buffer (`DodonaShim/Program.cs`:
    /// "every child stdout line; seq == index"), so a NEW shim process starts again at 0 — while
    /// the store dedupes with `UNIQUE(lane_id, seq)` + `INSERT OR IGNORE`. Every line a respawned
    /// agent emitted therefore collided with the previous agent's rows and was silently thrown
    /// away, its own `system init` included. `state`, `presence` and `session` are not seq-keyed
    /// and kept updating, so the lane rendered alive and healthy while being permanently mute.
    /// Measured 2026-08-22: `lane-respawn`, one of the five lane-tile actions, and the
    /// wake-after-a-night path with it. `LaneRuntime`'s own header had named the gap since M0 —
    /// "per-connection epochs come with agent replacement (M1+)" — and it was never built.
    ///
    /// <paramref name="shimKey"/> identifies the shim LIFETIME (`shim:child` pids from the hello),
    /// and that is the whole distinction this has to make: the SAME shim reconnecting after the
    /// daemon died is a replay that must still dedupe exactly-once, so it must get the SAME base
    /// back — across a daemon restart, which is why the answer is persisted rather than held in
    /// memory. A DIFFERENT shim gets a base above every seq already stored, so nothing can collide.
    ///
    /// An empty <paramref name="shimKey"/> means the hello named no shim, which no shim in this
    /// codebase does. It returns whatever base is already recorded and rebases nothing — today's
    /// behaviour exactly, because rebasing on an unidentifiable connection would turn a replay
    /// into duplicate rows, and duplicated output is a worse failure than the one being fixed.
    /// </summary>
    long SeqBase(long laneId, string shimKey);
    void LaneSession(long id, string session);
    void LanePresence(long id, string presence);
    void KvSet(string key, string value);
}
