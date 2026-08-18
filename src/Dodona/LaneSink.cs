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
    void LaneSession(long id, string session);
    void LanePresence(long id, string presence);
    void KvSet(string key, string value);
}
