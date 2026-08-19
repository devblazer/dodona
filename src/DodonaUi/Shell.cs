using Dodona;

namespace DodonaUi;

/// <summary>
/// One window over N workspaces (WORKSPACES-CONCIERGE.md §6).
///
/// m3 doctrine is what makes this cheap: the UI owns nothing and a pane is a replay of store
/// rows, so a multi-workspace window is **N read-only readers and N pipes with a
/// `(workspace, lane)` key** — a bigger view, not a new authority. Nothing here writes; every
/// click still resolves to exactly one daemon's control pipe, and concierge messages go to the
/// concierge's.
///
/// Chosen shape (**B**; §8 records what lost): the focused workspace gets the full 3×2 grid,
/// every other awake workspace renders as a compact band of lane chips. Simultaneous
/// awareness without halving every pane.
///
/// **Per-store semantics do not blur.** The six-slot cap, `focused_lane` and the dispatcher
/// lane stay per-workspace concepts, because each workspace has its own Poller with its own
/// sticky slot map — this class never merges grids, and a band never evicts a lane
/// (LANE-LIFECYCLE §2 stands).
/// </summary>
sealed class Shell : IDisposable
{
    /// <summary>One workspace the shell is showing: its own reader, its own poller, its own
    /// sticky slots. Nothing is shared between them — §14's "no shared mutable state" holds
    /// on the read side too.</summary>
    sealed class Open : IDisposable
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required StoreReader Reader { get; init; }
        public required Poller Poller { get; init; }
        public void Dispose() => Reader.Dispose();
    }

    readonly Dictionary<string, Open> _open = new(StringComparer.OrdinalIgnoreCase);
    readonly ConciergeReader _concierge = new();
    readonly object _lock = new();
    string _focused = "";

    public Shell(string focusedWorkspaceId, string focusedWorkspaceName)
    {
        if (focusedWorkspaceId.Length > 0)
        {
            Add(focusedWorkspaceId, focusedWorkspaceName);
            _focused = focusedWorkspaceId;
        }
    }

    public string Focused => _focused;

    public string FocusedName
    {
        get { lock (_lock) return _open.TryGetValue(_focused, out var o) ? o.Name : ""; }
    }

    /// <summary>The focused workspace's poller — the one whose overlay the window drives.</summary>
    public Poller? FocusedPoller
    {
        get { lock (_lock) return _open.TryGetValue(_focused, out var o) ? o.Poller : null; }
    }

    void Add(string id, string name)
    {
        lock (_lock)
        {
            if (_open.ContainsKey(id)) return;
            var reader = new StoreReader(Paths.Store(id));
            _open[id] = new Open { Id = id, Name = name, Reader = reader, Poller = new Poller(reader) };
        }
    }

    /// <summary>
    /// Which workspaces belong on screen: every AWAKE one (a live ctl pipe), plus whichever is
    /// focused even if its daemon has since died — a workspace you are looking at must not
    /// vanish underneath you because its daemon exited.
    ///
    /// Re-read every tick rather than at startup, because a workspace waking up somewhere else
    /// (a lane started from the CLI, or the concierge waking one on a prompt) has to appear as
    /// a band without the operator doing anything.
    /// </summary>
    public void Refresh()
    {
        List<Workspace> all;
        try
        {
            using var reg = new Registry();
            all = reg.All();
        }
        catch { return; }                                  // registry busy: keep the last view

        var live = all.Where(w => Instance.IsLive(w.Id)).ToList();
        lock (_lock)
        {
            foreach (var w in live) Add(w.Id, w.Name);

            // Drop what is neither awake nor focused. Its rows are untouched on disk; this is
            // a view shrinking, which is the only thing a view is allowed to do.
            foreach (var gone in _open.Keys
                         .Where(id => !id.Equals(_focused, StringComparison.OrdinalIgnoreCase)
                                      && !live.Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                _open[gone].Dispose();
                _open.Remove(gone);
            }

            // Names can be renamed out from under us (name is display, id is identity, §1).
            foreach (var w in all)
                if (_open.TryGetValue(w.Id, out var o) && o.Name != w.Name)
                    _open[w.Id] = new Open { Id = o.Id, Name = w.Name, Reader = o.Reader, Poller = o.Poller };

            // Each poller gets its workspace's PROJECTS, so a pane can say which one its lane
            // is in (docs/LOCATIONS-PLAN.md P1.2). Here rather than in Add(), because Refresh
            // already holds the registry open every tick and a project attached mid-session has
            // to show up without a restart. The Poller reference survives the rename above, so
            // this reads _open after it.
            foreach (var w in all)
                if (_open.TryGetValue(w.Id, out var op))
                    op.Poller.ProjectPaths = w.Members.Select(m => m.Path).ToArray();

            // Boot-to-zero, or the focused workspace disappeared from the registry: adopt an
            // awake one if there is one, otherwise fall to the real zero state (§4).
            if (_focused.Length == 0 || !_open.ContainsKey(_focused))
                _focused = live.Count > 0 ? live[0].Id : "";
        }
    }

    /// <summary>Swap which workspace holds the grid — the only thing clicking a band does. A
    /// view choice: no store is written, no lane is moved, nothing is evicted (§6).</summary>
    public bool Focus(string workspaceId)
    {
        lock (_lock)
        {
            if (!_open.ContainsKey(workspaceId)) return false;
            _focused = workspaceId;
            return true;
        }
    }

    public List<(string Id, string Name, bool Focused)> Showing()
    {
        lock (_lock)
            return _open.Values
                .OrderBy(o => o.Id.Equals(_focused, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => (o.Id, o.Name, o.Id.Equals(_focused, StringComparison.OrdinalIgnoreCase)))
                .ToList();
    }

    /// <summary>
    /// Build the whole window's snapshot: the focused workspace's grid verbatim (so everything
    /// m3 asserts about panes, slots, badges and the overlay is unchanged), plus a band per
    /// other awake workspace, plus one merged feed.
    /// </summary>
    public Snapshot Build()
    {
        Refresh();

        List<Open> others;
        Open? focused;
        lock (_lock)
        {
            focused = _open.TryGetValue(_focused, out var f) ? f : null;
            others = _open.Values
                .Where(o => !o.Id.Equals(_focused, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Boot-to-zero: nothing awake. Six empty slots, no bands, and the feed still shows
        // whatever the concierge has been saying — which is exactly what the operator needs
        // in order to type their way out of it (§4).
        var baseSnap = focused?.Poller.Build()
                       ?? new Snapshot(new PaneSnap?[6], new List<string>(), new List<FeedSnap>(), null);

        var bands = others.Select(o =>
        {
            var lanes = o.Reader.Lanes().Where(l => l.Role == "work" && l.State != "dead").ToList();
            var badges = o.Reader.Badges();
            return new BandSnap(
                o.Id, o.Name, Instance.IsLive(o.Id),
                lanes.Select(l => new BandLaneSnap(
                    l.Id, l.Title, l.Presence, badges.GetValueOrDefault(l.Id),
                    l.Presence.StartsWith("waiting on you", StringComparison.OrdinalIgnoreCase))).ToList(),
                // A band shows chips for every work lane, so "tray" here means only what the
                // GRID would have trayed — a band is not a grid and has no six-slot cap.
                Tray: 0,
                Badge: lanes.Sum(l => badges.GetValueOrDefault(l.Id)));
        }).ToList();

        // ---- the merged feed (§6): a read-only union, newest first, with a workspace chip
        // per row. The workspace label is left EMPTY when only one workspace is showing: the
        // axis carries no information there, and a chip answering a question nobody asked is
        // exactly the kind of noise a single-project operator should never meet.
        var multi = others.Count > 0;
        var merged = new List<(string Ts, FeedSnap Row)>();
        if (focused is not null)
            foreach (var f in baseSnap.Feed)
                merged.Add((f.Ts, f with { Workspace = multi ? focused.Name : "" }));
        foreach (var o in others)
        {
            var laneTitle = o.Reader.Lanes().ToDictionary(l => l.Id, l => l.Title);
            var laneRole = o.Reader.Lanes().ToDictionary(l => l.Id, l => l.Role);
            foreach (var x in o.Reader.Feed(15))
                merged.Add((x.Ts, new FeedSnap(x.Id, laneTitle.GetValueOrDefault(x.LaneId, $"lane {x.LaneId}"),
                                               x.Ts, x.Body, x.Acked, laneRole.GetValueOrDefault(x.LaneId) == "dispatcher")
                { Workspace = o.Name }));
        }
        // Group-scope clarifications belong to NO workspace's column by definition (§6), so
        // they carry the system's own voice and ack to the concierge's pipe.
        foreach (var c in _concierge.Feed(15))
        {
            // The `[dodona]` workspace chip IS the scope marker, so the lane title is empty
            // and the body's own "[dodona] " prefix is dropped: title + chip + prefix would
            // print the same word three times in one row. The store keeps the full text —
            // `dodona concierge-feed` still shows it verbatim.
            var body = c.Body.StartsWith("[dodona] ", StringComparison.Ordinal) ? c.Body[9..] : c.Body;
            merged.Add((c.Ts, new FeedSnap(c.Id, "", c.Ts, body, c.Acked, IsSystem: true)
            { Workspace = "[dodona]", IsConcierge = true }));
        }

        var feed = merged.OrderByDescending(m => m.Ts, StringComparer.Ordinal).Take(30).Select(m => m.Row).ToList();

        return baseSnap with
        {
            Feed = feed,
            Bands = bands,
            FocusedWorkspace = focused?.Id ?? "",
            FocusedWorkspaceName = focused?.Name ?? "",
            Ask = OpenAsk(focused),
        };
    }

    /// <summary>
    /// The one question to render, or null (LOCATIONS-PLAN Phase 4). **Two stores, one row
    /// shape** — the FOCUSED workspace's own open questions first, then the concierge's. They
    /// must stay two stores: a workspace daemon may never read the concierge's (§2), and the
    /// concierge's `questions` has no column for scope, so scope IS which store the row is in
    /// (D-L11 and the plan's correction to P4.1). This method is the "normalised at the edge"
    /// part; everything downstream sees one shape and one answer path.
    ///
    /// **Focused workspace first** because a question about the work in front of you is more
    /// urgent than one about which life a sentence belonged to, and because a group-scope
    /// question has by definition already been navigated past — the operator is looking at a
    /// workspace. Oldest-first within each store: they are asked in the order the uncertainty
    /// was created.
    ///
    /// **Exactly ONE at a time.** A stack of overlays is a queue of modals, and the affordance
    /// this replaces was a single row in the feed. Answering one reveals the next on the
    /// following tick, which is a rhythm rather than a pile.
    ///
    /// **A banded workspace's question is deliberately NOT shown.** It would put a decision
    /// about a workspace the operator is not looking at on top of the one they are — and the
    /// band already carries its badge, which is how a workspace says "you are needed here" (§6).
    /// </summary>
    AskSnap? OpenAsk(Open? focused)
    {
        try
        {
            if (focused is not null && focused.Reader.OpenQuestions().FirstOrDefault() is { } q)
                return Build(focused.Id, focused.Name, q);
            if (_concierge.OpenQuestions().FirstOrDefault() is { } cq)
                // `[dodona]` is the label the merged feed already gives the concierge's own voice:
                // a question about WHICH workspace belongs to no workspace's column by definition.
                return Build(Instance.ConciergeId, "[dodona]", cq);
        }
        catch { /* a store mid-migration or a busy reader: nothing is being asked THIS tick */ }
        return null;

        static AskSnap Build(string scope, string label, StoreReader.QuestionR q) => new(
            scope, label, q.Id, q.Input,
            Dodona.Ask.Choices(q.Candidates)
                .Select(c => new AskChoiceSnap(c.Value, c.Label, c.Why)).ToList());
    }

    string _lastJson = "";

    /// <summary>Force a re-apply on the next tick (used when leaving a pose).</summary>
    public void Invalidate() => _lastJson = "";

    /// <summary>
    /// The window's one tick. Build the merged snapshot, and hand it over only when it
    /// changed — one loop and one change-gate for one window, because N per-workspace loops
    /// would each re-render the whole window and could disagree about whether anything moved.
    /// </summary>
    public async Task RunAsync(MainVm vm, Func<Snapshot, Task> apply, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (vm.PoseName is null)
                {
                    var snap = Build();
                    var json = System.Text.Json.JsonSerializer.Serialize(snap);
                    if (json != _lastJson)
                    {
                        _lastJson = json;
                        await apply(snap);
                    }
                }
            }
            catch { /* a store mid-migration, a daemon restarting, a busy registry: next tick */ }
            try { await Task.Delay(250, ct); } catch (TaskCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var o in _open.Values) o.Dispose();
            _open.Clear();
        }
        _concierge.Dispose();
    }
}
