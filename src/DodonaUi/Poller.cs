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

    /// <summary>Presence, made honest about time (docs/LANE-LIFECYCLE.md §5). A static
    /// `working…` looks identical whether the agent is thinking hard or wedged; a clock
    /// that moves proves the pipeline AND the UI are alive. Elapsed is bucketed to 10s so
    /// the snapshot JSON changes on that cadence and the poller re-renders it — precision
    /// past that is noise. Long silence is reported as `quiet`, a neutral state: big
    /// thinks and slow builds are legitimate, so it informs rather than accuses.</summary>
    static string Liveness(string presence, string state, DateTime? lastSeen, DateTime now)
    {
        if (state == "unreachable") return "unreachable";
        if (state == "dormant") return presence is { Length: > 0 } ? presence : "landed";
        var busy = presence.Length > 0 && presence != "idle" && presence != "landed" && presence != "system"
                   && !presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase);
        if (!busy || lastSeen is null) return presence;

        var s = (long)(now - lastSeen.Value).TotalSeconds;
        if (s < 10) return presence;
        if (s >= 300) return $"quiet {s / 60}m ({presence})";
        s -= s % 10;
        return $"{presence} {(s >= 60 ? $"{s / 60}m{s % 60:00}s" : $"{s}s")}";
    }

    public Snapshot Build()
    {
        var lanes = _reader.Lanes();
        var badges = _reader.Badges();
        var lastSeen = _reader.LastActivity();
        var lastInput = _reader.LastInput();
        var ticketRepos = _reader.TicketRepoByLane();
        // Repo tags only say anything when the workspace has more than one repo in play —
        // a single-repo project must never see the word (same rule as everywhere else).
        var multiRepo = ticketRepos.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                        || ticketRepos.Values.Any(v => v != ".");
        var now = DateTime.UtcNow;
        var focusedLane = long.TryParse(_reader.Kv("focused_lane"), out var f) ? f : -1;

        // Sticky slot assignment: first-seen work lane takes the lowest free slot, keeps it
        // for as long as it lives, and never moves when a neighbour goes (§8).
        //
        // A lane the operator STOPPED leaves the grid and frees its slot — undo has to
        // look like undo, and with six slots the grid cannot be a graveyard. Its rows are
        // untouched in the store, so `tail` and the overlay still have the whole
        // transcript. A lane that went UNREACHABLE stays visible on purpose: that one is
        // a problem to notice, not a decision you made.
        var slots = new PaneSnap?[6];
        var tray = new List<string>();
        var shown = lanes.Where(l => l.Role == "work" && l.State != "dead").ToList();
        foreach (var goneId in _slotOf.Keys.Where(id => shown.All(l => l.Id != id)).ToList())
            _slotOf.Remove(goneId);

        foreach (var l in shown.OrderBy(l => l.Id))
        {
            if (!_slotOf.TryGetValue(l.Id, out var slot))
            {
                var taken = _slotOf.Values.ToHashSet();
                slot = Enumerable.Range(0, 6).FirstOrDefault(i => !taken.Contains(i), -1);
                if (slot < 0) { tray.Add(l.Title); continue; }   // grid capped at six (§8)
                _slotOf[l.Id] = slot;
            }
            slots[slot] = new PaneSnap(l.Id, l.Title, l.State,
                Liveness(l.Presence, l.State, lastSeen.TryGetValue(l.Id, out var seen) ? seen : null, now),
                badges.GetValueOrDefault(l.Id),
                l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase),
                l.Id == focusedLane,
                // 12 was what fitted a pane that could not scroll. It can now, so a pane
                // carries real scrollback — bounded, because this whole snapshot is
                // serialized and compared every 250ms.
                _reader.Tail(l.Id, 40))
            {
                Repo = multiRepo && ticketRepos.TryGetValue(l.Id, out var rp) && rp != "." ? rp : "",
                LastInputId = lastInput.GetValueOrDefault(l.Id),
            };
        }

        var laneTitle = lanes.ToDictionary(l => l.Id, l => l.Title);
        // By role, not by title: "the system speaking in its own voice" is what the
        // dispatcher role MEANS, and matching the string "DODONA" would only work until
        // someone renamed it.
        var laneRole = lanes.ToDictionary(l => l.Id, l => l.Role);
        var feed = _reader.Feed(30)
            .Select(x => new FeedSnap(x.Id, laneTitle.GetValueOrDefault(x.LaneId, $"lane {x.LaneId}"), x.Ts, x.Body,
                                      x.Acked, laneRole.GetValueOrDefault(x.LaneId) == "dispatcher"))
            .ToList();

        PaneSnap? overlay = null;
        if (OverlayTitle is string ot)
        {
            var l = lanes.FirstOrDefault(x => x.Title.Equals(ot, StringComparison.OrdinalIgnoreCase));
            if (l is not null)
                overlay = new PaneSnap(l.Id, l.Title, l.State, l.Presence, badges.GetValueOrDefault(l.Id),
                    l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase),
                    l.Id == focusedLane, _reader.Tail(l.Id, 120, all: true));
        }

        return new Snapshot(slots, tray, feed, overlay) { Quota = QuotaLine(now) };
    }

    /// <summary>The 5-hour-window line for the dispatcher column. The CLI pushes
    /// `rate_limit_event` down every lane's wire unasked; the daemon keeps the latest in
    /// kv. It only updates when a lane takes a turn, so it always carries its age — a
    /// stale number presented as live is how a fleet dies at 4pm with no warning (§2.6).</summary>
    string? QuotaLine(DateTime now)
    {
        var raw = _reader.Kv("rate_limit");
        if (raw is null) return null;
        try
        {
            using var d = JsonDocument.Parse(raw);
            var info = d.RootElement.GetProperty("info");
            if (info.TryGetProperty("rateLimitType", out var rt) && rt.GetString() != "five_hour") return null;
            var util = info.TryGetProperty("utilization", out var u) ? u.GetDouble() : -1;
            if (util < 0) return null;

            var line = $"5h window {util * 100:0}%";
            if (info.TryGetProperty("resetsAt", out var ra) && ra.TryGetInt64(out var epoch))
                line += $" · resets {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm}";
            if (info.TryGetProperty("isUsingOverage", out var ov) && ov.ValueKind == JsonValueKind.True)
                line += " · OVERAGE";
            if (d.RootElement.TryGetProperty("observedTs", out var ot) &&
                DateTime.TryParse(ot.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen))
            {
                var age = (long)(now - seen.ToUniversalTime()).TotalMinutes;
                if (age >= 2) line += $" · as of {age}m ago";
            }
            return line;
        }
        catch { return null; }
    }
}
