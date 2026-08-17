using System.Text.Json;

namespace DodonaUi;

/// <summary>
/// Reads the store every 250ms, builds a Snapshot, hands it to the window when it
/// changed. Slots are sticky (§8): a lane keeps its position for the UI's lifetime even
/// after it dies — the slot stays, empty-looking or reused, but never reflows. Work
/// lanes past six go to the tray; router/dispatcher lanes never occupy the grid.
/// </summary>
sealed class Poller
{
    readonly StoreReader _reader;
    readonly Dictionary<long, int> _slotOf = new();     // laneId -> sticky slot
    string _lastJson = "";
    public volatile string? OverlayTitle;               // set by the window; poller fills lines

    public Poller(StoreReader reader) => _reader = reader;

    public async Task RunAsync(MainVm vm, Func<Snapshot, Task> apply, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (vm.PoseName is null)
                {
                    var snap = Build();
                    var json = JsonSerializer.Serialize(snap);
                    if (json != _lastJson)
                    {
                        _lastJson = json;
                        await apply(snap);
                    }
                }
            }
            catch { /* store mid-migration or daemon restarting: next tick */ }
            try { await Task.Delay(250, ct); } catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Force re-apply on the next tick (used when leaving a pose).</summary>
    public void Invalidate() => _lastJson = "";

    public Snapshot Build()
    {
        var lanes = _reader.Lanes();
        var badges = _reader.Badges();
        var focusedLane = long.TryParse(_reader.Kv("focused_lane"), out var f) ? f : -1;

        // Sticky slot assignment: first-seen work lane takes the lowest free slot, keeps it.
        var slots = new PaneSnap?[6];
        var tray = new List<string>();
        foreach (var l in lanes.Where(l => l.Role == "work").OrderBy(l => l.Id))
        {
            if (!_slotOf.TryGetValue(l.Id, out var slot))
            {
                var taken = _slotOf.Values.ToHashSet();
                slot = Enumerable.Range(0, 6).FirstOrDefault(i => !taken.Contains(i), -1);
                if (slot < 0) { tray.Add(l.Title); continue; }   // grid capped at six (§8)
                _slotOf[l.Id] = slot;
            }
            slots[slot] = new PaneSnap(l.Id, l.Title, l.State, l.Presence,
                badges.GetValueOrDefault(l.Id),
                l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase),
                l.Id == focusedLane,
                _reader.Tail(l.Id, 12));
        }

        var laneTitle = lanes.ToDictionary(l => l.Id, l => l.Title);
        var feed = _reader.Feed(30)
            .Select(x => new FeedSnap(x.Id, laneTitle.GetValueOrDefault(x.LaneId, $"lane {x.LaneId}"), x.Ts, x.Body, x.Acked))
            .ToList();

        PaneSnap? overlay = null;
        if (OverlayTitle is string ot)
        {
            var l = lanes.FirstOrDefault(x => x.Title.Equals(ot, StringComparison.OrdinalIgnoreCase));
            if (l is not null)
                overlay = new PaneSnap(l.Id, l.Title, l.State, l.Presence, badges.GetValueOrDefault(l.Id),
                    l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase),
                    l.Id == focusedLane, _reader.Tail(l.Id, 40, all: true));
        }

        return new Snapshot(slots, tray, feed, overlay);
    }
}
