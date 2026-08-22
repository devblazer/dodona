using System.Text.Json;
using Dodona;

namespace DodonaUi;

/// <summary>
/// Reads the store every 250ms, builds a Snapshot, hands it to the window when it changed.
///
/// **The grid grows with the work.** ORCHESTRATOR-DESIGN §8 originally capped it at six with a
/// seventh lane queueing in a tray, and called the cap a feature: it stops you starting nine
/// things and tracking none. The operator has superseded that (2026-08-18): they favour a new
/// lane per distinct task, so a fixed cap now fights the routing policy rather than protecting
/// them from it. The layout divides itself as lanes arrive, and the operator collapses what
/// they are not dealing with.
///
/// What §8 was actually protecting is kept, because it was right: **stable order.** Lanes are
/// ordered by creation and never reshuffle, so "SKYBOX is the second tile" stays true all
/// session and your eye does not re-read titles on every glance. Growth adds at the end; a
/// lane that dies leaves the order of its neighbours alone. Colour still means the lane.
///
/// This also fixes a drift §8 itself forbade. It said "an active-but-invisible lane would
/// defeat the cap and could be blocked on you with no visible signal; forbidden" — and yet a
/// seventh live lane appeared only as a NAME in the tray, agent running, badge unseeable. Now
/// every live lane has a tile: expanded, or a one-line strip you can click. The tray goes back
/// to meaning only what §8 said it meant — lanes that have not started.
///
/// Router, compressor, brain and dispatcher lanes never occupy the grid.
/// </summary>
sealed class Poller
{
    readonly StoreReader _reader;
    string _lastJson = "";
    public volatile string? OverlayTitle;               // set by the window; poller fills lines

    /// <summary>This workspace's projects, newest registry answer, set by <see cref="Shell"/>
    /// on every tick — the same treatment <see cref="OverlayTitle"/> gets, and for the same
    /// reason: the poller reads the STORE, and a project list is registry state, which only
    /// the Shell already has open. Re-set every tick rather than at construction because a
    /// project attached while the window is up must appear without a restart (the doctrine
    /// Daemon.Members() states one level down).
    ///
    /// Empty until the first Refresh, and empty is safe: Projects.Field then reports every
    /// work lane as `none (cwd=…)` for one 250 ms tick at most, and never the wrong project.
    /// Volatile array reference rather than a List, so the 250 ms read never sees a
    /// half-populated collection.</summary>
    public volatile string[] ProjectPaths = Array.Empty<string>();

    public Poller(StoreReader reader) => _reader = reader;

    /// <summary>The 250ms loop moved up to <see cref="Shell"/> when the window became one
    /// view over N workspaces (WORKSPACES-CONCIERGE.md §6): there is one window, so there is
    /// one tick and one change-gate over the MERGED snapshot. A per-workspace loop would
    /// re-render the whole window N times a tick, and two of them disagreeing about whether
    /// anything changed is how a pose gets overwritten.</summary>
    public void Invalidate() => _lastJson = "";

    internal string LastJson { get => _lastJson; set => _lastJson = value; }

    /// <summary>Presence, made honest about time (docs/LANE-LIFECYCLE.md §5). A static
    /// `working…` looks identical whether the agent is thinking hard or wedged; a clock
    /// that moves proves the pipeline AND the UI are alive. Elapsed is bucketed to 10s so
    /// the snapshot JSON changes on that cadence and the poller re-renders it — precision
    /// past that is noise. Long silence is reported as `quiet`, a neutral state: big
    /// thinks and slow builds are legitimate, so it informs rather than accuses.</summary>
    internal static string Liveness(string presence, string state, DateTime? lastSeen, DateTime now)
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

        // Ordered by creation, one tile per live work lane, no fixed count and no empty
        // placeholders. Order is what must be stable, not position in a fixed array: a lane
        // the operator STOPPED disappears and its neighbours keep their relative order, so
        // undo looks like undo and the grid never becomes a graveyard. Its rows are untouched
        // in the store, so `tail` and the overlay still hold the whole transcript. A lane that
        // went UNREACHABLE stays visible on purpose: that one is a problem to notice, not a
        // decision you made.
        // Projects are captured ONCE per snapshot: the field is volatile and the Shell writes
        // it from another thread, so re-reading it per lane could describe two different
        // workspaces in one grid.
        var projects = ProjectPaths;
        var neutral = Paths.NeutralDir;
        var collapsed = _reader.CollapsedLanes();
        var tray = new List<string>();
        var shown = lanes.Where(l => l.Role == "work" && l.State != "dead").OrderBy(l => l.Id).ToList();

        var slots = shown.Select(l => new PaneSnap(l.Id, l.Title, l.State,
                Liveness(l.Presence, l.State, lastSeen.TryGetValue(l.Id, out var seen) ? seen : null, now),
                badges.GetValueOrDefault(l.Id),
                l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase),
                l.Id == focusedLane,
                // 12 was what fitted a pane that could not scroll. It can now, so a pane
                // carries real scrollback — bounded, because this whole snapshot is
                // serialized and compared every 250ms. A collapsed tile shows no transcript
                // at all, so it costs nothing to read and nothing to serialize.
                collapsed.Contains(l.Id) ? new List<LineSnap>() : _reader.Tail(l.Id, 40))
            {
                Repo = multiRepo && ticketRepos.TryGetValue(l.Id, out var rp) && rp != "." ? rp : "",
                // WHICH PROJECT (P1.2). Never computed here: the daemon answers `status` with
                // the same function over the same three inputs, so the window and the CLI
                // cannot disagree about where a lane is.
                Project = Projects.Field(l.Role, l.Cwd, projects, neutral) ?? "",
                LastInputId = lastInput.GetValueOrDefault(l.Id),
                Collapsed = collapsed.Contains(l.Id),
            })
            .Cast<PaneSnap?>()
            .ToArray();

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

    /// <summary>The convenience overload that binds the real reader — the Trees.Locate
    /// pattern (Trees.cs:44 + :77, and docs/testarch/seams.md S4). Production has exactly ONE
    /// path through the decision below; a test supplies the kv bytes instead of a store, which
    /// is what keeps the rendering out of a live window without faking StoreReader.</summary>
    string? QuotaLine(DateTime now) => QuotaLine(_reader.Kv("rate_limit"), now);

    /// <summary>The 5-hour-window line for the dispatcher column. The CLI pushes
    /// `rate_limit_event` down every lane's wire unasked; the daemon keeps the latest in
    /// kv. It only updates when a lane takes a turn, so it always carries its age — a
    /// stale number presented as live is how a fleet dies at 4pm with no warning (§2.6).</summary>
    internal static string? QuotaLine(string? raw, DateTime now)
    {
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
