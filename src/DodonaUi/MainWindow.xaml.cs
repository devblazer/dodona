using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dodona;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // Thumb's DragDeltaEventArgs — the resize grip
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DodonaUi;

public partial class MainWindow : Window
{
    readonly string _root, _instanceId;
    readonly MainVm _vm = new();
    // One shell over N workspaces (WORKSPACES-CONCIERGE.md §6). _instanceId stays the
    // workspace this window was OPENED for -- its ui pipe, and the daemon a click defaults
    // to; the shell tracks which workspace currently holds the grid, which the operator can
    // change by clicking a band without any of this window's identity moving.
    readonly Shell _shell;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Which workspace this window is showing — the picker uses it to raise an
    /// already-open workspace instead of opening it twice.</summary>
    public string InstanceId => _instanceId;

    /// <summary>This process was started with --test-window. Carried across a UI hot swap
    /// (§13) because losing it would turn an invisible test window into a visible one that
    /// activates — test windows stealing the operator's keyboard mid-work is the complaint
    /// --test-window exists to answer, and a swap is no reason to reintroduce it.</summary>
    public static bool TestWindow;

    public MainWindow(string primary, string instanceId, string workspaceName, bool successor = false)
    {
        _root = primary;
        _instanceId = instanceId;
        InitializeComponent();
        DataContext = _vm;
        // The workspace's NAME, not a folder label. A workspace is named rather than
        // located (WORKSPACES-CONCIERGE.md §1) — no path appears anywhere in this window's
        // chrome, not even a tooltip: locations belong to the router, and the last two
        // path-shaped leftovers here were exactly what let the old folder interface
        // survive the workspace redesign (removed 2026-08-18).
        Title = workspaceName.Length > 0 ? $"Dodona — {workspaceName}" : "Dodona";

        // ShellId is a sentinel for "opened over no particular workspace" (§4), not a
        // workspace: handing it to the Shell made it open a StoreReader over a store that
        // does not exist and then treat that phantom as focused, so boot-to-zero never
        // triggered and a real workspace waking up could never take the grid.
        _shell = new Shell(instanceId == Instance.ShellId ? "" : instanceId, workspaceName);
        _ = _shell.RunAsync(_vm, snap => Dispatcher.InvokeAsync(() => ApplySnapshot(snap)).Task, _cts.Token);
        UiPipe.Start(Instance.UiPipe(instanceId), this, successor);
        if (successor) _ = Task.Run(SignalReadyAsync);
        // The poller only re-applies when the STORE changes, but a pulse must fade on its
        // own clock — this tick re-renders the last snapshot while any pane still glows,
        // and goes back to sleep the moment none do.
        var pulseTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        var pulseFading = false;                       // one FINAL repaint after the last pulse
        pulseTick.Tick += (_, _) =>                    // expires, or the glow never turns off
        {
            if (_vm.PoseName is not null || _lastSnap is null) return;
            if (_vm.AnyPulseActive) { pulseFading = true; _vm.Apply(_lastSnap); }
            else if (pulseFading) { pulseFading = false; _vm.Apply(_lastSnap); }
        };
        pulseTick.Start();

        Loaded += (_, _) => { RestoreInputHeight(); RestoreListening(); };
        Closed += (_, _) => { _cts.Cancel(); _shell.Dispose(); };
    }

    /// <summary>Tell the outgoing UI we are up (§13). Sent only once the window exists, so
    /// "ready" means the new build actually runs — the incumbent will not stand down for a
    /// binary that cannot open a window.</summary>
    async Task SignalReadyAsync()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Instance.UiHandoffPipe(_instanceId),
                                                    PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(15000);
            var w = new StreamWriter(c) { AutoFlush = true };
            await w.WriteLineAsync($"ready {Ver.Build} (pid {Environment.ProcessId})");
        }
        catch { /* nobody waiting: we were launched normally, not as a successor */ }
    }

    Snapshot? _lastSnap;

    void ApplySnapshot(Snapshot snap)
    {
        // Re-checked HERE, on the UI thread: a poll snapshot already queued when a pose
        // lands must not overwrite the pose (the poller's own check races the dispatcher).
        if (_vm.PoseName is not null) return;
        _lastSnap = snap;
        var newlyBlocked = _vm.Apply(snap);
        // The title follows whichever workspace holds the grid: in the shell that changes
        // when the operator clicks a band, and a window titled after a workspace it stopped
        // showing is worse than one titled after nothing.
        var want = snap.FocusedWorkspaceName.Length > 0 ? $"Dodona — {snap.FocusedWorkspaceName}" : "Dodona";
        if (Title != want) Title = want;
        // Toast rule (§8): only when the app lacks focus AND a lane is blocked on you.
        // Never for progress — muted toasts help nobody.
        foreach (var lane in newlyBlocked)
        {
            if (!IsActive)
            {
                _vm.RecordToast(lane, "blocked on you: merge");
                FlashWindow(new WindowInteropHelper(this).Handle, true);
            }
        }
    }

    // ------------------------------------------------------------- ui verbs (§17)

    /// <summary>Handle one verb from the UI pipe. Always on the UI thread; returns one
    /// response line.</summary>
    public string HandleVerb(JsonElement e)
    {
        switch (e.GetProperty("verb").GetString())
        {
            case "dump":
                return Dump();
            case "screenshot":
            {
                var path = e.GetProperty("out").GetString()!;
                var pane = e.TryGetProperty("pane", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                return Screenshot(path, pane);
            }
            case "pose":
            {
                var name = e.GetProperty("name").GetString()!;
                return ApplyPose(name);
            }
            case "overlay":
            {
                var pane = e.GetProperty("pane").GetString()!;
                return SetOverlay(pane.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : pane);
            }
            case "update":
                return Update(e.GetProperty("exe").GetString()!);
            case "type":
            {
                InputBox.Text = e.GetProperty("text").GetString() ?? "";
                SubmitInput();
                return "typed";
            }
            // The three verbs the multiline box needs, each landing in the SAME method the
            // mouse or keyboard lands in: `compose` types without sending, `key` is Enter or
            // Shift+Enter, `input-resize` is the grip. A parallel test-only path would prove
            // nothing about the affordance a person touches.
            case "compose":
                ComposeInput(e.GetProperty("text").GetString() ?? "");
                return "composed";
            case "key":
            {
                var k = (e.GetProperty("key").GetString() ?? "").Replace(" ", "").ToLowerInvariant();
                return k switch
                {
                    "enter" or "return" => InputKey(false),
                    "shift+enter" or "shift+return" => InputKey(true),
                    // Escape lands in Window_KeyDown's own body, not a copy of it: putting the ask
                    // down and closing the transcript overlay are one decision made in one place,
                    // and a test that drove a parallel path would prove nothing about the key.
                    "escape" or "esc" => EscapePressed(),
                    _ => $"error: unknown key '{k}' (enter | shift+enter | escape)",
                };
            }
            // Pick an answer to the ask — the SAME method the button's Click handler calls
            // (LOCATIONS-PLAN P4.3). The whole point of Phase 4 is that the live overlay and the
            // headless dump are two renderings of one component with ONE answer path; a verb that
            // talked to the daemon by itself would have built the second system.
            // The five lane actions, without a mouse and without focus — each lands in the method
            // the click lands in (LaneAction). They had no verb until 2026-08-19, which is why the
            // defect LaneAction's note describes went two builds unseen.
            case "lane":
                return LaneAction(e.GetProperty("action").GetString() ?? "",
                                  e.GetProperty("lane").GetInt64());
            case "answer":
                return AnswerAsk(e.GetProperty("answer").GetString() ?? "");
            case "input-resize":
            {
                var reset = e.TryGetProperty("reset", out var rs) && rs.ValueKind == JsonValueKind.True;
                var h = ResizeInput(reset ? null : e.GetProperty("dy").GetDouble());
                return $"input height {h:0}";
            }
            // Clicking a band, without a mouse and without focus. Same reasoning as `type`:
            // it goes through EXACTLY the code path a click takes (FocusWorkspace), so a test
            // drives the real affordance rather than a parallel one — and a band is a Border,
            // which UIA can find but cannot invoke.
            case "workspace":
                return FocusWorkspace(e.GetProperty("workspace").GetString() ?? "");
            // Dictation, focus-free, landing where a click and a real utterance land
            // (docs/VOICE-INPUT-PLAN.md §5). `listen` is the mic button; `heard` is a recognition
            // result through the REAL OnHeard splice, which is why it is not gated behind a test
            // flag — a check drives the affordance rather than a rehearsal of it.
            case "listen":
                return SetListening(e.GetProperty("state").GetString() ?? "");
            case "heard":
            {
                var heard = e.GetProperty("text").GetString() ?? "";
                var final = !(e.TryGetProperty("partial", out var pf) && pf.ValueKind == JsonValueKind.True);
                // The epoch a check can override, so the submit race is drivable end to end and
                // not only in a unit test. Default is "the operator is still speaking".
                var epoch = e.TryGetProperty("epoch", out var ep) && ep.ValueKind == JsonValueKind.Number
                    ? ep.GetInt64() : _submitEpoch;
                return OnHeard(new Dictation.Heard(heard, final, epoch));
            }
            case "close":
                Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
                return "closing";
            default:
                return "error: unknown verb";
        }
    }

    /// <summary>The UI testifies (§17): panes, badges, presence, feed, tray, toasts as
    /// JSON — serialized from the live view model, not from the store, because the
    /// question a dump answers is "what is the UI showing".</summary>
    string Dump()
    {
        var dump = new
        {
            window = new { w = (int)Width, h = (int)Height, title = Title, active = IsActive, root = _root },
            pose = _vm.PoseName,
            overlay = _vm.OverlayPane?.Title,
            // The overlay's LINES, not just its title. The overlay is the one component that
            // promises "raw, one keystroke away" (§12) — unfiltered kinds, uncompressed
            // bodies, and since 2026-08-19 the unfolded progress steps — and until this key
            // existed no check could see any of it. A promise nothing can look at is where the
            // next defect lives (§3.1, the second instance of that rule); the pane's own
            // `lines` have been dumpable since M3 and this is the same testimony for the view
            // beside it. Null when nothing is maximized, like `overlay` above.
            overlayLines = _vm.OverlayPane?.Lines.Select(l => l.Text).ToList(),
            // THE ASK (LOCATIONS-PLAN P4.2). `ui dump` had NO field for a dialog, which is why
            // PickerWindow and StartLaneWindow are entirely untested — this key is what makes
            // asking checkable at all, and it is the headless half of the one component: the
            // overlay above renders these exact values.
            //
            // `null` when nothing is being asked, which is the ordinary state and the only state a
            // ONE-project workspace ever has (there is nothing to ask, so no overlay may appear).
            // `shown` is false while the operator has put it down with Escape — the ROW is still
            // open and this still reports it, because "is anything being asked" and "is it on
            // screen" are two honest questions and conflating them is how a dismissal starts
            // looking like an answer.
            ask = _vm.Ask is null ? null : (object)new
            {
                id = _vm.Ask.Id,
                scope = _vm.Ask.Scope,
                scopeLabel = _vm.Ask.ScopeLabel,
                question = _vm.Ask.Question,
                shown = _vm.AskVisible == Visibility.Visible,
                dismissed = _vm.AskDismissed,
                choices = _vm.Ask.Choices.Select(c => new { value = c.Value, label = c.Label, why = c.Why }).ToList(),
            },
            // The workspace dimension §6 asks for. `workspace` is which one holds the grid,
            // `bands` is every other awake one, and `bootToZero` is the real state of having
            // none awake at all -- so a dump can answer "which of my lives is on screen"
            // without a screenshot.
            workspace = _vm.FocusedWorkspace,
            workspaceName = _vm.FocusedWorkspaceName,
            bootToZero = _vm.BootToZero,
            bands = _vm.Bands.Select(b => new
            {
                workspace = b.WorkspaceId, name = b.Name, live = b.Live, badge = b.Badge, color = b.ColorHex,
                lanes = b.Lanes.Select(l => new { lane = l.LaneId, title = l.Title, badge = l.Badge, blocked = l.Blocked }).ToList(),
            }).ToList(),
            slots = _vm.Slots.Select(s => s.IsEmpty
                ? (object)new { slot = s.Slot, empty = true }
                : new
                {
                    slot = s.Slot, empty = false, lane = s.LaneId, title = s.Title, color = s.ColorHex,
                    state = s.State, presence = s.Presence, badge = s.Badge, blocked = s.Blocked,
                    // `project` is the unabbreviated Projects.Field value (a project path,
                    // `neutral`, or `none (cwd=...)`), and "" for "nothing to say" -- which is
                    // every lane of a ONE-project workspace, so that workspace's dump reports
                    // exactly what it always did (LOCATIONS-PLAN P1.2, and the same rule `repo`
                    // beside it already follows). The TILE shows the leaf folder; a dump does
                    // not abbreviate, because a check must not be able to confuse two projects.
                    focused = s.Focused, repo = s.Repo, project = s.Project, pulsing = s.Pulsing, collapsed = false,
                    lines = s.Lines.Select(l => l.Text).ToList(),
                }).ToList(),
            // Collapsed lanes are reported separately AND as part of `slots` shape-compatible
            // rows, because the question "what is on screen" has two honest halves now: the
            // tiles, and the chips. `columns` says how the grid divided itself.
            columns = _vm.GridColumns,
            collapsedLanes = _vm.CollapsedLanes.Select(s => new
            {
                lane = s.LaneId, title = s.Title, presence = s.Presence, badge = s.Badge,
                blocked = s.Blocked, focused = s.Focused, collapsed = true,
            }).ToList(),
            quota = _vm.QuotaText,
            tray = _vm.Tray.ToList(),
            // `lines` and the feed's existing keys keep their shape: what the UI testifies to
            // must not change because it got a new dimension (§17). `workspace` is added.
            feed = _vm.Feed.Select(f => new
            { id = f.Id, lane = f.LaneTitle, body = f.Body, acked = f.Acked, workspace = f.Workspace, concierge = f.IsConcierge }).ToList(),
            toasts = _vm.Toasts.Select(t => new { ts = t.Ts, lane = t.Lane, reason = t.Reason }).ToList(),
            status = _vm.Status,
            // The dispatcher box testifies too, now that it has more than one state to be in:
            // `lines` is LOGICAL lines (Shift+Enter presses + 1, not wrapped display rows),
            // `sized` says the operator overruled the auto-fit with the grip.
            input = new
            {
                text = InputBox.Text,
                lines = InputBox.Text.Split('\n').Length,
                height = (int)Math.Round(InputBox.ActualHeight),
                sized = !double.IsNaN(InputBox.Height),
                hint = InputHint.Visibility == Visibility.Visible,
                // `fit` is the default (MinLines, measured); `remembered` is what is on disk,
                // so a test can prove the size outlived the window rather than the process.
                fit = (int)Math.Round(_inputFit),
                remembered = UiSettings.Load().InputHeight,
                // How much of `text` is the UNSETTLED dictation tail, and where it starts
                // (D-E17). The tail lives in the box now, so a check that wants committed text
                // only has to be able to subtract it -- which is the determinism D-V6 was
                // protecting, kept without pushing the words out of the box.
                pending = PendingLength,
                pendingAt = _pendingAt,
            },
            // Dictation testifies too (docs/VOICE-INPUT-PLAN.md §5). `says` is the SAME sentence
            // the indicator shows, from the same function, so a check that reads the dump is
            // reading what is on screen. `partial` carries the unsettled tail that D-V6 keeps
            // out of input.text, and `dropped` counts submit-race casualties so a silent degrade
            // cannot hide here.
            listen = new
            {
                state = _listen.ToString().ToLowerInvariant(),
                engine = _mic?.Engine ?? "none",
                says = Dictation.Describe(_listen, _listenReason),
                partial = _partial,
                error = _listenReason,
                dropped = _dropped,
                remembered = UiSettings.Load().Listening,
            },
        };
        return JsonSerializer.Serialize(dump);
    }

    /// <summary>Screenshot the whole grid, or one pane (§17). Rendering is in Shot, shared
    /// with the picker's --shot.</summary>
    string Screenshot(string outPath, string? paneTitle)
    {
        FrameworkElement target = paneTitle is null
            ? Root
            : FindPaneElement(paneTitle) ?? throw new InvalidOperationException($"no pane titled '{paneTitle}' in the grid");
        return Shot.Save(target, outPath);
    }

    FrameworkElement? FindPaneElement(string title)
    {
        for (int i = 0; i < _vm.Slots.Count; i++)
        {
            if (_vm.Slots[i].IsEmpty || !_vm.Slots[i].Title.Equals(title, StringComparison.OrdinalIgnoreCase)) continue;
            return PaneGrid.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
        }
        return null;
    }

    /// <summary>
    /// Swap this window for a newer build (§13). The daemon hot-swaps but the UI could
    /// not: Windows locks a running image, so a published UI sat on disk while the
    /// operator kept looking at the old one — a swapped daemon behind a stale window is
    /// indistinguishable from nothing having happened.
    ///
    /// A UI handoff is far cheaper than the daemon's: this process owns no lanes, no
    /// store writes and no agents. Its only exclusive resource is the ui pipe, so the
    /// whole protocol is "start the new one, wait for it to say it is up, then let go".
    /// Same safety rule as the daemon: if the successor never answers, THIS window stays.
    /// </summary>
    string Update(string exe)
    {
        if (!File.Exists(exe)) return $"error: no such UI binary: {exe}";
        if (string.Equals(Path.GetFullPath(exe), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return $"already running {exe}";

        try
        {
            using var server = new NamedPipeServerStream(Instance.UiHandoffPipe(_instanceId), PipeDirection.InOut, 1,
                                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            // Addressed by workspace id: the successor must come up over the same store,
            // and re-resolving a path in the child is a second chance to disagree.
            // EXCEPT the shell, whose id is a sentinel and not a registry row: respawning
            // it as `--workspace <sentinel>` made the successor die at the registry lookup
            // ("No workspace"), never signal ready, and the incumbent kept the pipe — so
            // every publish while the shell window was open silently left the operator
            // looking at the old UI, which reads as "nothing was ever built" (2026-08-18).
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            { UseShellExecute = false, WorkingDirectory = Directory.Exists(_root) ? _root : Path.GetTempPath() };
            if (_instanceId == Instance.ShellId) psi.ArgumentList.Add("--shell");
            else { psi.ArgumentList.Add("--workspace"); psi.ArgumentList.Add(_instanceId); }
            psi.ArgumentList.Add("--successor");
            if (TestWindow) psi.ArgumentList.Add("--test-window");
            using var p = System.Diagnostics.Process.Start(psi);

            // Blocking the UI thread here is deliberate: nothing this window could usefully
            // do mid-handoff, and the pipe reply must not race the shutdown below.
            if (!server.WaitForConnectionAsync().Wait(30000))
                return $"error: successor never connected; staying on {Ver.Build}";
            var ready = new StreamReader(server).ReadLine();
            if (ready is null || !ready.StartsWith("ready"))
                return $"error: successor said '{ready}'; staying on {Ver.Build}";

            // Exit only now. The successor is retrying the ui pipe and takes it the
            // moment this process dies (UiPipe.Start awaitPredecessor).
            Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            return $"updated: {Ver.Build} → {ready}";
        }
        catch (Exception ex) { return $"error: update failed ({ex.Message}); staying on {Ver.Build}"; }
    }

    /// <summary>Set by --test-window. A test window must never produce a MODAL: it renders
    /// off-screen precisely so it cannot steal the keyboard, and a MessageBox would both steal it
    /// and block until somebody clicked — in an automated capture, forever. Anything that would
    /// have been a dialog goes to stderr instead, and the exit code carries the meaning.</summary>
    public bool IsTestWindow { get; set; }

    public string ApplyPose(string name)
    {
        if (name.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            _vm.PoseName = null;
            _vm.OverlayPane = null;
            // A posed ask is a fixture, not a question: leaving it up after `pose live` would put
            // a decision on screen that no row anywhere is waiting for. The next tick sets the
            // real one, if there is one.
            _vm.Ask = null;
            // A posed indicator is a fixture too: leaving it up after `pose live` would claim a
            // microphone is open when none is. Back to what the toggle actually says.
            if (_mic is null) { _listen = Dictation.ListenState.Off; _listenReason = null; }
            _partial = "";
            UpdateListenUi();
            if (_shell.FocusedPoller is Poller fp) fp.OverlayTitle = null;
            _shell.Invalidate();
            return "pose live (store polling resumed)";
        }
        // The dictation indicator is window state rather than snapshot state, so it is posed
        // here rather than in Poses.cs. `full` underneath, because the failure a screenshot
        // catches is an indicator that collides with the grip or the box, not one in isolation.
        if (name.Equals("listening", StringComparison.OrdinalIgnoreCase))
        {
            var listenPose = Poses.Get("full")!.Value;
            _vm.PoseName = name;
            _vm.OverlayPane = null;
            _vm.Toasts.Clear();
            _vm.Apply(listenPose.Item1);
            _listen = Dictation.ListenState.Listening;
            _listenReason = null;
            _partial = "run the suites for";
            // The tail goes in the BOX now (D-E17), so the fixture has to put it there too --
            // otherwise the one screenshot that exists for reviewing dictation would show the
            // old layout and nobody would see the change they were reviewing.
            ShowPending(_partial);
            UpdateListenUi();
            _vm.Status = $"pose: {name}";
            return $"pose {name} applied";
        }

        var pose = Poses.Get(name);
        if (pose is null) return $"error: unknown pose '{name}' (have: {string.Join(", ", Poses.Names)}, live)";
        var (snap, overlayTitle, toast) = pose.Value;
        _vm.PoseName = name;
        _vm.OverlayPane = null;
        _vm.Toasts.Clear();
        _vm.Apply(snap);
        if (toast is not null) _vm.Toasts.Add(toast);
        if (overlayTitle is not null && snap.Overlay is PaneSnap ov)
            _vm.OverlayPane = PaneView.From(ov, _vm.Slots.First(s => !s.IsEmpty && s.Title == overlayTitle).Slot);
        _vm.Status = $"pose: {name}";
        return $"pose {name} applied";
    }

    string SetOverlay(string? paneTitle)
    {
        if (paneTitle is null)
        {
            _vm.OverlayPane = null;
            if (_shell.FocusedPoller is Poller fp0) fp0.OverlayTitle = null;
            return "overlay off";
        }
        var pane = _vm.Slots.FirstOrDefault(s => !s.IsEmpty && s.Title.Equals(paneTitle, StringComparison.OrdinalIgnoreCase));
        if (pane is null) return $"error: no pane titled '{paneTitle}'";
        _vm.OverlayPane = pane;                    // immediate, with the pane's current lines
        if (_vm.PoseName is null && _shell.FocusedPoller is Poller fp1)
        {
            fp1.OverlayTitle = pane.Title;         // next poll deepens it to 120 raw rows
            _shell.Invalidate();
        }
        return $"overlay {pane.Title}";
    }

    // ------------------------------------------------------------- transcript scrolling
    // A snapshot rebuilds all six PaneViews, so the ItemsControl regenerates its
    // containers and every ScrollViewer would snap back to the top four times a second.
    // Position is therefore remembered per SLOT (which is sticky, §8) rather than per
    // element: NaN means "follow the tail", a number means the operator scrolled up and
    // wants to stay there. Reading a lane while it talks has to be possible.

    readonly Dictionary<int, double> _paneScroll = new();
    double _overlayScroll = double.NaN;

    void Transcript_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.DataContext is not PaneView p) return;
        _paneScroll[p.Slot] = Follow(sv, e, _paneScroll.TryGetValue(p.Slot, out var at) ? at : double.NaN);
    }

    void Overlay_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        _overlayScroll = Follow(sv, e, _overlayScroll);
    }

    /// <summary>Content grew (or the container was rebuilt) → put the view back where it
    /// was, or at the end if we were following. Otherwise the operator just scrolled, so
    /// record it — landing at the bottom re-arms following. Returns the new saved offset.</summary>
    static double Follow(ScrollViewer sv, ScrollChangedEventArgs e, double saved)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            sv.ScrollToVerticalOffset(double.IsNaN(saved) ? sv.ScrollableHeight : saved);
            return saved;
        }
        return sv.VerticalOffset >= sv.ScrollableHeight - 1 ? double.NaN : sv.VerticalOffset;
    }

    // ------------------------------------------------------------- interactions
    // The view is dumb: every click is just a daemon pipe message (§17) — which is why
    // tests can inject the message instead of driving the UI.

    /// <summary>
    /// THE LANE ACTION PATH — the one a click and `dodona ui lane <action> <n>` share, for the same
    /// reason <see cref="AnswerAsk"/> is one method (D-L4: only pixels may diverge).
    ///
    /// It exists because these five actions had NO verb at all, so no acceptance check could reach
    /// them — and that is where a real defect lived undetected: every one of them went through
    /// <see cref="Send"/>, which did not start a sleeping daemon, so against the state the operator's
    /// machine is in every morning (window open, nothing running — CLAUDE.md §3.1) each click
    /// answered "daemon not running". Untestable and broken are not a coincidence here; the first
    /// caused the second.
    /// </summary>
    public string LaneAction(string action, long lane)
    {
        object req = action switch
        {
            "focus"    => new { cmd = "focus", lane },
            "stop"     => new { cmd = "lane-stop", lane },
            "respawn"  => new { cmd = "lane-respawn", lane },
            "collapse" => new { cmd = "lane-collapse", lane, collapsed = true },
            "expand"   => new { cmd = "lane-collapse", lane, collapsed = false },
            _ => null!,
        };
        if (req is null) return $"error: '{action}' is not one of: focus / stop / respawn / collapse / expand";
        Send(req);
        return $"{action} {lane}";
    }

    void Pane_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p) return;
        if (p.IsEmpty) { StartLane(); e.Handled = true; return; }   // an empty slot is an invitation
        LaneAction("focus", p.LaneId);
        e.Handled = true;
    }

    void Pane_Overlay(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        SetOverlay(p.Title);
        e.Handled = true;
    }

    void Feed_Ack(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FeedView f) return;
        // The merged feed carries rows from several stores, and their ids are only unique
        // within one. Ack has to go back to the store the row came from, or it would clear
        // an unrelated row that happens to share a number (§6: writes stay pipe-addressed).
        if (f.IsConcierge) SendConcierge(new { cmd = "ack", id = f.Id });
        else Send(new { cmd = "ack", id = f.Id });
    }

    void Pane_Close(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        LaneAction("stop", p.LaneId);
        e.Handled = true;                       // not a pane click; do not also focus it
    }

    /// <summary>Collapse a tile to a chip, or expand it again — a store write like every other
    /// click (m3: the UI owns nothing), so the choice survives closing the window and every
    /// window over this workspace agrees. Nothing about the lane's life changes.</summary>
    void Pane_Collapse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        LaneAction(p.Collapsed ? "expand" : "collapse", p.LaneId);
        e.Handled = true;                       // not a pane click; do not also focus it
    }

    /// <summary>Clicking a collapsed chip expands it. The one thing a chip does.</summary>
    void Collapsed_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p) return;
        LaneAction("expand", p.LaneId);
        e.Handled = true;
    }

    void Pane_Wake(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaneView p || p.IsEmpty) return;
        LaneAction("respawn", p.LaneId);
        e.Handled = true;
    }

    /// <summary>PREVIEWKeyDown, and that is not a style preference: with
    /// AcceptsReturn="True" the TextBox's own class handler consumes Enter to insert the
    /// newline BEFORE any instance KeyDown handler runs, so the box goes quietly deaf and
    /// Enter stops sending. That exact trap already cost this project a round (CLAUDE.md
    /// §0.2) — a lane rewrote this box, could not build it, and shipped the bug.</summary>
    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        InputKey((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0);
        // Handled either way. Letting a Shift+Enter through after InputKey already
        // inserted the newline would insert a SECOND one.
        e.Handled = true;
    }

    /// <summary>The two things Enter can mean, in one place so the `ui key` verb drives
    /// byte-for-byte the code a keystroke does (§17 — the same reasoning as `ui type`).
    /// Shift+Enter is a newline because a prompt is often a paragraph; bare Enter sends,
    /// because that is the muscle memory the box already had.</summary>
    public string InputKey(bool shift)
    {
        if (!shift) { SubmitInput(); return "submitted"; }
        var at = InputBox.SelectionStart;
        InputBox.SelectedText = "\n";      // replaces a selection, exactly like a typed character
        InputBox.CaretIndex = at + 1;
        // Past the auto-grow cap the box scrolls, and a caret you cannot see is a box you
        // are typing into blind.
        var line = InputBox.GetLineIndexFromCharacterIndex(InputBox.CaretIndex);
        if (line >= 0) InputBox.ScrollToLine(line);
        return "newline";
    }

    /// <summary>Typing characters with no Enter — what a person does between keystrokes.
    /// `ui type` cannot stand in for this because `type` submits, so no test could ever
    /// get two lines INTO the box to prove Shift+Enter left them there.</summary>
    public void ComposeInput(string text)
    {
        var at = InputBox.SelectionStart;
        InputBox.SelectedText = text;
        InputBox.CaretIndex = at + text.Length;
    }

    // ══ dictation: speech puts words in the box, and has nowhere to send them from ═════
    //
    // docs/VOICE-INPUT-PLAN.md Phase A. The operator's constraint, verbatim: "Send will still
    // need an enter." OnHeard below calls ComposeInput and InputKey; it does NOT reference
    // SubmitInput, and it could not usefully be made to — Dictation.Decide cannot return a value
    // that means send (Dictation's class note). `ui-use:spoken_send_words_do_not_submit` speaks
    // the word at a live window and demands the feed did not move.

    IRecognizer? _mic;
    Dictation.ListenState _listen = Dictation.ListenState.Off;
    string? _listenReason;
    string _partial = "";
    int _dropped;

    /// <summary>Bumped by <see cref="SubmitInput"/>. A result recognised under an older epoch is
    /// the tail of a sentence already sent, and is dropped rather than opening the next message
    /// with it (§4's submit race).</summary>
    long _submitEpoch;

    /// <summary>
    /// THE ONE LANDING SITE. The real engine and the fake raise into this method, and so does
    /// `dodona ui heard` — the same reasoning as `ui type` calling <see cref="SubmitInput"/>
    /// rather than synthesising a keystroke (§17). A fake feeding a parallel path would prove
    /// nothing about the real one.
    ///
    /// Read what is not here: no SubmitInput, and no branch that could reach one. The words
    /// "enter", "send", "submit" and "go" arrive as ordinary text and land in the box like any
    /// others, because <c>DictationAct</c> has nowhere else to put them.
    /// </summary>
    public string OnHeard(Dictation.Heard h)
    {
        var d = Dictation.Decide(h, _submitEpoch);
        switch (d.Act)
        {
            // Never into InputBox.Text (D-V6): unsettled hypotheses rewrite themselves, and in
            // the box they would make `ui dump`'s input.text non-deterministic — every existing
            // input check would go intermittent.
            // ══ THE UNSETTLED TAIL NOW RENDERS IN THE BOX, AT THE CARET (D-E17 reverses D-V6) ══
            //
            // The operator, after using it: *"while I'm recording, the word should appear in the
            // text box from wherever the cursor was left. Currently it's kinda showing above the
            // text box, which was weird."* They are right, and D-V6's reasoning does not survive
            // the complaint: it kept partials out of `InputBox.Text` because they would make
            // `ui dump`'s input.text non-deterministic and every input check intermittent. That is
            // a TESTABILITY argument being spent against the thing the feature is for — watching
            // your words appear as you say them — which is the wrong way round, and it is also what
            // Claude Code's own extension does (VOICE-INPUT-PLAN §6.2 called this the one open UI
            // question; this closes it).
            //
            // Determinism is kept a different way: the pending range is TRACKED, so it can be
            // removed exactly rather than guessed at, and `dump.input.pending` says how many
            // characters of the box are unsettled. A check can therefore still assert on committed
            // text alone, which is what D-V6 actually needed.
            case Dictation.DictationAct.Partial:
                _partial = d.Text;
                ShowPending(d.Text);
                break;

            case Dictation.DictationAct.Insert:
            {
                _partial = "";
                ClearPending();                // the settled text replaces the tail, never joins it
                var (insert, caret) = Dictation.Splice(InputBox.Text, InputBox.SelectionStart,
                                                       InputBox.SelectionLength, d.Text);
                ComposeInput(insert);          // the method a typed character lands in
                InputBox.CaretIndex = caret;
                break;
            }

            // Through InputKey(shift: true) — the same method Shift+Enter uses — rather than a
            // "\n" written into Text, so a dictated newline and a typed one scroll the caret
            // identically.
            case Dictation.DictationAct.Newline:
                _partial = "";
                ClearPending();
                InputKey(shift: true);
                break;
            case Dictation.DictationAct.Paragraph:
                _partial = "";
                ClearPending();
                InputKey(shift: true);
                InputKey(shift: true);
                break;

            // Counted, not silent. A dropped tail is the one behaviour here a person would find
            // baffling, and a silent degrade is a bug (§0.1) — `ui dump`'s listen.dropped is where
            // it shows. Status is deliberately left alone: it carries the daemon's words, and a
            // dictation aside overwriting them would be a second silent degrade.
            case Dictation.DictationAct.Drop:
                _dropped++;
                ClearPending();
                break;
        }
        UpdateListenUi();
        return d.Act.ToString().ToLowerInvariant();
    }

    /// <summary>Where the unsettled tail sits in <c>InputBox.Text</c>, and how long it is.
    /// -1 means nothing is pending. Tracked rather than searched for: the tail can be any text,
    /// including text identical to something the operator typed, so "find and remove it" would
    /// eventually delete the wrong copy.</summary>
    int _pendingAt = -1;
    int _pendingLen;

    /// <summary>How many characters of the box are NOT yet settled — `dump.input.pending`. This is
    /// what lets a check still reason about committed text after D-E17 put the tail in the box:
    /// `text` minus the last `pending` characters at `pendingAt` is what a person has actually
    /// finished saying.</summary>
    public int PendingLength => _pendingAt < 0 ? 0 : _pendingLen;

    /// <summary>
    /// Show the unsettled tail at the caret, replacing whatever tail was there before.
    ///
    /// It goes through <see cref="Dictation.Splice"/> exactly as a settled result does, so the
    /// pending text sits where the final text will and the words do not visibly jump when the
    /// phrase settles.
    /// </summary>
    void ShowPending(string text)
    {
        ClearPending();
        if (text.Length == 0) return;
        var at = InputBox.SelectionStart;
        var (insert, caret) = Dictation.Splice(InputBox.Text, at, InputBox.SelectionLength, text);
        _pendingAt = at;
        _pendingLen = insert.Length;
        InputBox.SelectedText = insert;
        InputBox.CaretIndex = caret;
    }

    /// <summary>Remove the unsettled tail and put the caret back where it began. Bounds-checked
    /// against the live text because the operator can edit the box mid-utterance — they are
    /// allowed to, and a stale range must never be able to delete their typing.</summary>
    void ClearPending()
    {
        if (_pendingAt < 0) return;
        var at = _pendingAt;
        var len = _pendingLen;
        _pendingAt = -1;
        _pendingLen = 0;
        if (at < 0 || len <= 0 || at + len > InputBox.Text.Length) return;
        InputBox.Text = InputBox.Text.Remove(at, len);
        InputBox.CaretIndex = Math.Min(at, InputBox.Text.Length);
    }

    /// <summary>
    /// The mic button and `ui listen`, in ONE method (§3.1: an affordance no verb can reach is
    /// where the next defect will live — the five lane actions shipped that defect twice).
    /// </summary>
    public string SetListening(string what)
    {
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "on": ArmMic(); break;
            case "off": DisarmMic(); break;
            // A toggle, not a button (the operator's first invariant). Error counts as ON for
            // the purpose of toggling: the thing to do with a mic that failed is turn it off.
            case "toggle":
                if (_listen == Dictation.ListenState.Off) ArmMic(); else DisarmMic();
                break;
            default:
                return $"error: unknown listen state '{what}' (on | off | toggle)";
        }
        UpdateListenUi();
        UiSettings.SaveListening(_listen != Dictation.ListenState.Off);
        return $"listen {_listen.ToString().ToLowerInvariant()}";
    }

    /// <summary>Arming never throws and never blocks. A failure arrives as a REASON and the box
    /// keeps typing normally — a dead recogniser may make the box deaf, but must never be able
    /// to make it unusable, since the box is the one thing you would use to report the problem
    /// (the argument UiSettings already makes about a corrupt ui.json).</summary>
    void ArmMic()
    {
        DisarmMic();                                  // fresh each time: re-arming an existing
        _listenReason = null;                         // instance would double every subscription
        _listen = Dictation.ListenState.Starting;

        // The engine stamps an utterance at its START, so it needs to read the CURRENT submit
        // epoch at that moment — hence a delegate rather than a value (the race, §4).
        _mic = Recognizers.Create(() => _submitEpoch, out var why);
        if (why is not null)
        {
            _listen = Dictation.ListenState.Error;
            _listenReason = why;
            return;
        }

        // Which arming attempt this is. A Ready or Failed arriving from a recogniser the window
        // has already disarmed and replaced belongs to nobody, and applying it would resurrect an
        // indicator for a dead engine — the submit race in a second costume.
        var epoch = ++_armEpoch;

        // The engine's thread is not the UI thread; OnHeard touches controls, so it marshals.
        _mic.Heard += h => Dispatcher.InvokeAsync(() => OnHeard(h));
        _mic.Failed += r => Dispatcher.InvokeAsync(() =>
        {
            if (epoch != _armEpoch) return;
            _listen = Dictation.ListenState.Error;
            _listenReason = r;
            UpdateListenUi();
        });

        // ══ STARTING IS NOW A STATE THAT CAN BE SAT IN, AND THAT IS WHY THIS CHANGED ══
        //
        // Until the cloud engine landed, Start() was synchronous: SAPI had either failed or begun
        // by the time it returned, so this method promoted Starting to Listening on the next line
        // and §7's "Starting must have a deadline" was satisfied by a state that could not linger.
        //
        // A socket connect is not synchronous, and spike E1 found something worse: THIS
        // ENDPOINT'S UPGRADE SUCCEEDS WITH NO CREDENTIAL AT ALL and refuses one frame later. So
        // "Start() returned" no longer implies "we are hearing", and promoting here would leave
        // the indicator reading `listening` at a rejected credential — deaf, silent, and looking
        // healthy, which is the precise failure §5 exists to prevent.
        //
        // So the window HOLDS Starting and lets the engine say which way it went. It needs no
        // timer of its own because the engine's contract guarantees exactly one of Ready/Failed
        // always arrives — DeepgramRecognizer.ConnectTimeoutMs is the thing that un-sticks it,
        // which is §0.1's standing directive satisfied by a condition rather than by a person.
        _mic.Ready += () => Dispatcher.InvokeAsync(() =>
        {
            // A Ready from a recogniser this window has already disarmed and replaced must not
            // resurrect the indicator. Same shape as the submit-race guard, one layer out.
            if (epoch != _armEpoch) return;
            if (_listen == Dictation.ListenState.Starting) _listen = Dictation.ListenState.Listening;
            UpdateListenUi();
        });

        _mic.Start();

        // The fake raises Ready synchronously inside Start(), so a suite is in `listening` by the
        // time this returns and all 268 existing checks read exactly what they read before.
        if (_listen == Dictation.ListenState.Starting) ArmStartingDeadline(epoch);
    }

    /// <summary>
    /// **STARTING CANNOT BE SAT IN, AND THIS IS THE WINDOW'S OWN GUARANTEE OF THAT.**
    ///
    /// `DeepgramRecognizer` has its own connect deadline, so on the shipped path this timer
    /// normally never fires. It exists anyway, one layer out, because the invariant belongs to the
    /// WINDOW rather than to any one engine: §7 asked for a deadline on `Starting` and Phase A got
    /// away without one only because SAPI's `Start()` was synchronous. An engine that forgot to
    /// raise either event — a future one, a wrongly-built one, the `hang` fixture — would
    /// otherwise leave the indicator reading `starting` forever, which is §0.1's standing
    /// directive violated exactly: a wait whose un-sticking condition is a person noticing.
    ///
    /// So the guarantee does not depend on the engine keeping its half of the contract. That is
    /// the same reasoning that put start-on-demand inside `DaemonClient.Send` rather than at each
    /// call site (§3.1): a rule a caller can forget is a rule that gets forgotten.
    /// </summary>
    void ArmStartingDeadline(long epoch)
    {
        // A margin over the engine's own deadline, so on the real path the engine reports the
        // specific reason and this only catches an engine that said nothing at all.
        var ms = DeepgramRecognizer.ConnectTimeoutMs + 2000;
        var timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (epoch != _armEpoch) return;                       // disarmed, or re-armed since
            if (_listen != Dictation.ListenState.Starting) return; // the engine answered: nothing to do
            _listen = Dictation.ListenState.Error;
            _listenReason = Dodona.SpeechStream.NoAnswerWords;
            UpdateListenUi();
        };
        timer.Start();
    }

    /// <summary>Which arming attempt is current. A late Ready or Failed from a recogniser that has
    /// since been disarmed belongs to nobody, and applying it would be the submit race in a second
    /// costume.</summary>
    long _armEpoch;

    void DisarmMic()
    {
        // Invalidate any in-flight Ready/Failed from the engine being torn down. Without this, a
        // connect that answers after the operator switched the toggle off would put the indicator
        // back to `listening` for a recogniser that no longer exists.
        _armEpoch++;

        // ══ THE UNSETTLED TAIL MUST NOT OUTLIVE THE MICROPHONE AS UNTRACKED TEXT ══
        //
        // D-E17 put the tail in the box, which makes this necessary and it is not obvious. Leaving
        // it here on disarm would leave the WORDS in InputBox.Text while `_pendingAt` still pointed
        // at them — so the next time dictation started, ShowPending's ClearPending would delete a
        // range the operator had by then been looking at for minutes. A DELAYED, mysterious
        // deletion is strictly worse than an immediate one.
        //
        // So the tail is removed here, and the engine's flush (DeepgramRecognizer's cancellation
        // path) re-delivers the same words a moment later as SETTLED text. The operator's own
        // observation is what makes that safe: because the tail is now visible as it arrives, they
        // can see their words are in the box, so the momentary removal-and-recommit reads as the
        // text settling rather than as a loss. The fake holds nothing, so for every suite this is
        // simply "an unsettled hypothesis that never settled does not persist".
        ClearPending();
        try { _mic?.Stop(); _mic?.Dispose(); } catch { /* stopping must not be able to fail */ }
        _mic = null;
        _partial = "";
        _listen = Dictation.ListenState.Off;
        _listenReason = null;
    }

    /// <summary>Restore the remembered toggle (D-V2). A toggle that resets itself is a button,
    /// which is not what was asked for; and publish hot-swaps this window (§2), so an
    /// unremembered toggle would go silently deaf on every swap — the "quietly outdated" failure
    /// the standing directive names. DODONA_UI_MIC=off still outranks it, in Recognizers.Create.</summary>
    void RestoreListening()
    {
        if (UiSettings.Load().Listening == true) ArmMic();
        UpdateListenUi();
    }

    /// <summary>The indicator and `ui dump` are two renderings of ONE sentence
    /// (Dictation.Describe), so the screen and the dump cannot disagree about what is
    /// happening. Three states, and the third is the point: on-and-deaf must never look like
    /// on (§5).</summary>
    void UpdateListenUi()
    {
        var words = Dictation.Describe(_listen, _listenReason);
        // JUST THE STATE. The unsettled tail used to be appended here, which is what the operator
        // called weird -- and once D-E17 put the tail in the box at the caret, echoing it here as
        // well showed the same words twice, in two places, one of them the place they had just
        // asked for it not to be. The indicator says WHERE THE MICROPHONE IS; the box says what it
        // heard. `dump.listen.partial` still carries the tail for checks, so nothing lost coverage.
        ListenStatus.Text = words;
        ListenStatus.Visibility = words.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var colour = _listen switch
        {
            Dictation.ListenState.Listening or Dictation.ListenState.Starting => Color.FromRgb(0x6F, 0xA8, 0xDC),
            // The amber this window already uses for "needs your attention", not a new red: a
            // failed mic is a thing to notice, not an emergency.
            Dictation.ListenState.Error => Color.FromRgb(0xE0, 0xA9, 0x4E),
            _ => Color.FromRgb(0x5A, 0x60, 0x69),
        };
        var brush = new SolidColorBrush(colour);
        // The WORDS take the state's colour too, not just the glyph. Caught by looking at the
        // capture rather than by any check: with `Foreground` left at the XAML's blue, an error
        // read "no microphone" in the same calm blue as "listening", and the only thing saying
        // anything was wrong was a 9-pixel glyph. That is one shade away from the failure this
        // feature is most likely to ship -- on and deaf looking like on (§5).
        ListenStatus.Foreground = brush;
        MicGlyph.Stroke = brush;
        // Outline when off, filled when armed — readable at a glance without reading the words.
        MicGlyph.Fill = _listen == Dictation.ListenState.Off ? Brushes.Transparent : brush;
        MicButton.ToolTip = words.Length == 0 ? "Dictation — click to listen" : $"Dictation: {words}";
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        SetListening("toggle");
        e.Handled = true;
    }

    // Auto-grow stops here; a deliberate drag may go further, because the operator asking
    // for more room has said something the default cannot know.
    const double InputAutoCap = 200;

    // The DEFAULT height, read off the real layout rather than guessed: MinHeight="64" in
    // the XAML, which is three lines at this font plus padding and border. Three lines, not
    // one — a box that opens as a one-line sliver invites one-line prompts, and the point of
    // the multiline box is that a prompt is often a paragraph. MinHeight and not MinLines="3":
    // WPF ignores MinLines/MaxLines once TextWrapping is on, and `ui dump` caught it doing
    // exactly that (fit=28, one line, with MinLines set). MinHeight is only a floor, so the
    // box still grows past it as lines arrive. It doubles as the floor for a drag — an
    // explicit Height under it would clip the text instead of shrinking the box.
    double _inputFit;

    /// <summary>Restore the size the operator last set. Runs on Loaded, once the box has
    /// been measured — so the default is read off the real layout instead of a font
    /// calculation, and the remembered height lands in the same layout pass (no visible
    /// jump). It deliberately survives a publish hot-swap as well: the successor window reads
    /// the same file, and a box that silently reverted to default on every swap is exactly
    /// the "quietly outdated" failure the standing directive forbids.</summary>
    void RestoreInputHeight()
    {
        _inputFit = InputBox.ActualHeight;
        if (UiSettings.Load().InputHeight is double h) SetInputHeight(h, remember: false);
    }

    void InputGrip_DragDelta(object sender, DragDeltaEventArgs e) => ResizeInput(-e.VerticalChange, remember: false);

    /// <summary>One save per drag, at the end: persisting every DragDelta would rewrite the
    /// file a hundred times while the mouse moves.</summary>
    void InputGrip_DragCompleted(object sender, DragCompletedEventArgs e) =>
        UiSettings.SaveInputHeight(double.IsNaN(InputBox.Height) ? null : InputBox.Height);

    void InputGrip_Reset(object sender, MouseButtonEventArgs e)
    {
        ResizeInput(null);
        e.Handled = true;
    }

    /// <summary>Resize the dispatcher box: <paramref name="dy"/> pixels taller (negative
    /// shorter), or null to hand it back to fitting its own text. Dragging UP grows it,
    /// which is why the grip negates.</summary>
    public double ResizeInput(double? dy, bool remember = true)
    {
        if (dy is null) return SetInputHeight(null, remember);
        var from = double.IsNaN(InputBox.Height) ? InputBox.ActualHeight : InputBox.Height;
        return SetInputHeight(from + dy.Value, remember);
    }

    /// <summary>The one place the box's height is set — and the one place it is remembered,
    /// so a drag, the `ui input-resize` verb and a restore cannot drift apart. The feed
    /// absorbs the change (its row is the only star-sized one) and the cap keeps something of
    /// it on screen: a box that could eat the whole window would be a way to lose the work
    /// you were watching.</summary>
    double SetInputHeight(double? height, bool remember)
    {
        if (height is null)
        {
            InputBox.Height = double.NaN;             // back to fitting the text, floored by MinLines
            InputBox.MaxHeight = InputAutoCap;
        }
        else
        {
            var floor = Math.Max(24, _inputFit);
            var want = Math.Max(floor, Math.Min(height.Value, Math.Max(floor, ActualHeight * 0.6)));
            // The drag overrules the auto cap — otherwise the grip stops dead at 200px with
            // nothing to explain why. It never overrules the window.
            InputBox.MaxHeight = Math.Max(InputAutoCap, want);
            InputBox.Height = want;
        }
        if (remember) UiSettings.SaveInputHeight(double.IsNaN(InputBox.Height) ? null : InputBox.Height);
        InputBox.UpdateLayout();
        return InputBox.ActualHeight;
    }

    /// <summary>The one path from the box to the daemon — Enter and the `ui type` verb
    /// both land here, so a test drives byte-for-byte the same code a keystroke does
    /// WITHOUT needing keyboard focus (SendKeys required focusing the window, which
    /// stole the operator's keyboard mid-work — the reason the verb exists).</summary>
    public void SubmitInput()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        // THE SUBMIT RACE (§4). Everything the recogniser was in the middle of hearing belongs
        // to THIS message. Anything delivered after this point carries an older epoch and is
        // dropped, instead of opening the next message with the tail of the last one.
        _submitEpoch++;
        _partial = "";

        // Always sends. If there is nowhere to route, the daemon starts a lane and says
        // so — the UI does not get to invent a dialog for that, because deciding where
        // work goes is the system's job, not a form for the operator to fill in.
        InputBox.Clear();

        // Prompt-first, and the front door is one box (WORKSPACES-CONCIERGE.md §4). WHICH
        // door it opens depends on whether there is a group-scope question to answer at all:
        //
        //   one workspace on screen → straight to its daemon. There is nothing to
        //     disambiguate (the concierge's own rung 1b), so involving the concierge would
        //     add a hop, a possible failure and a possible model call to buy nothing — and it
        //     would mean the input box stopped working whenever the concierge would not start.
        //
        //   several, or none awake  → through the concierge, which resolves the workspace,
        //     WAKES it if asleep, creates one if the sentence names somewhere new, and only
        //     then hands the whole sentence to that workspace's own dispatcher. Boot-to-zero
        //     is the case this exists for: typing is how you start when nothing is running.
        if (_vm.Bands.Count == 0 && !_vm.BootToZero) Send(new { cmd = "input", text });
        else SendConcierge(new { cmd = "route", text, from = _vm.FocusedWorkspace });
    }

    void StartLane(string? suggestedName = null, string? firstMessage = null)
    {
        // The lane starts in the workspace holding the GRID, not the one this window was
        // opened for: an empty slot the operator just clicked is in front of them.
        var dlg = new StartLaneWindow(GridWorkspace, suggestedName, firstMessage) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.StartedLane < 0) return;
        _shell.Invalidate();
        if (firstMessage is { Length: > 0 })
            Send(new { cmd = "say", lane = dlg.StartedLane, text = firstMessage });
    }


    void Input_TextChanged(object sender, TextChangedEventArgs e) =>
        InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EscapePressed(); e.Handled = true; }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { OpenPicker(); e.Handled = true; }
    }

    /// <summary>What Escape means, in one place — the key handler and `ui key escape` both land
    /// here. The ask goes down FIRST because it is on top and it is the thing the operator most
    /// wants out of the way; the row stays open, so nothing is lost and it comes back on the next
    /// tick if they want it (MainVm.DismissAsk carries the reasoning).</summary>
    string EscapePressed()
    {
        if (_vm.AskVisible == Visibility.Visible) { _vm.DismissAsk(); return "ask put down"; }
        if (_vm.OverlayPane is not null) { SetOverlay(null); return "overlay off"; }
        return "nothing to close";
    }

    /// <summary>
    /// THE ANSWER PATH — the one both render modes share (LOCATIONS-PLAN P4.3, D-L4).
    ///
    /// A button click and `dodona ui answer <choice>` both arrive here, and here sends the SAME
    /// daemon command the CLI's `dodona answer` / `dodona concierge-answer` sends. That is what
    /// makes "only pixels diverge" a fact about the code: there is no second place that could
    /// answer a question differently, and no surface that is answering a question the other
    /// cannot.
    ///
    /// Pipe-addressed by the question's SCOPE (§6): a workspace question goes to that workspace's
    /// control pipe, a group-scope one to the concierge's. Different ids come from different
    /// tables, so guessing here would answer question 3 in the wrong store.
    ///
    /// A choice the question does not offer is REFUSED rather than sent. `new:NAME` is the one
    /// exception and it is passed through, because the concierge accepts it and a candidate list
    /// can never enumerate it — an overlay strictly less capable than the command line it replaces
    /// is the divergence D-L4 exists to prevent.
    /// </summary>
    public string AnswerAsk(string choice)
    {
        if (_vm.Ask is not AskView ask) return "error: nothing is being asked";
        var value = choice.Trim();
        if (value.Length == 0) return "error: ui answer <choice>";
        if (!Dodona.Ask.IsFreeForm(value))
        {
            var match = ask.Choices.FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                     ?? ask.Choices.FirstOrDefault(c => c.Label.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return $"error: '{value}' is not one of: {string.Join(" / ", ask.Choices.Select(c => c.Value))}";
            value = match.Value;
        }

        var req = new { cmd = "answer", id = ask.Id, answer = value };
        var scope = ask.Scope;
        // Off the UI thread, exactly like Send: a pipe round trip on the dispatcher thread would
        // freeze the window while a daemon ran `git init`.
        Task.Run(() =>
        {
            // A question outlives the process that asked it — that is the whole argument for it
            // being a row (ConciergeStore's class note). So whichever daemon owns it may well be
            // gone by the time the operator answers, and answering must bring it back rather than
            // fail. Same start-on-demand doctrine the input box already follows (§13).
            var reason = scope == Instance.ConciergeId
                ? DaemonClient.EnsureConcierge()
                : DaemonClient.Ensure(_root, scope);
            var reply = reason ?? DaemonClient.Send(scope, req);
            Dispatcher.BeginInvoke(() => _vm.Status = reply);
        });
        return $"answered {ask.Id}: {value}";
    }

    /// <summary>A click on a choice. One line, because everything it could get wrong lives in
    /// <see cref="AnswerAsk"/> where the verb can reach it too.</summary>
    void AskChoice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AskChoiceView c) return;
        AnswerAsk(c.Value);
        e.Handled = true;
    }

    /// <summary>The workspace switcher (Ctrl+P, or the header's name). Workspace NAMES
    /// only — picking one wakes it and hands it the grid in THIS window through
    /// FocusWorkspace, the same path a band click takes. The one-window model holds: this
    /// never spawns a second window per workspace. (It used to be the folder picker and
    /// used to do exactly that; removed 2026-08-18.)</summary>
    void OpenPicker()
    {
        var existing = Application.Current.Windows.OfType<PickerWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new PickerWindow { Owner = null }.Show();
    }

    void Workspace_Click(object sender, RoutedEventArgs e) => OpenPicker();

    void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Root) try { DragMove(); } catch { }
    }

    /// <summary>Which workspace a click acts on: the one holding the grid. Falls back to the
    /// workspace this window was opened for, which is what boot-to-zero leaves us with.</summary>
    string GridWorkspace => _vm.FocusedWorkspace.Length > 0 ? _vm.FocusedWorkspace : _instanceId;

    /// <summary>Fire a daemon command off the UI thread; the reply becomes the status line.
    /// Writes stay pipe-addressed (§6): one click, one daemon's control pipe, never a
    /// broadcast -- the shell is a bigger view, not a new authority.</summary>
    void Send(object req) => Task.Run(() =>
    {
        var reply = DaemonClient.Send(GridWorkspace, req);
        Dispatcher.BeginInvoke(() => _vm.Status = reply);
    });

    /// <summary>Talk to the CONCIERGE rather than a workspace daemon — group-scope rows and
    /// the input box's front door (§2/§6). A different pipe, deliberately: the concierge
    /// belongs to no workspace and its row ids come from its own tables.</summary>
    void SendConcierge(object req) => Task.Run(() =>
    {
        // Start-on-demand, or boot-to-zero would be a dead end: with nothing awake there is
        // no concierge either, and typing is the only way out of that state (§4).
        var reason = DaemonClient.EnsureConcierge();
        var reply = reason ?? DaemonClient.Send(Instance.ConciergeId, req);
        Dispatcher.BeginInvoke(() => _vm.Status = reply);
    });

    /// <summary>Click a band: swap which workspace holds the grid. A VIEW choice and nothing
    /// more -- no store is written, no lane is moved, nothing is evicted (§6, and
    /// LANE-LIFECYCLE §2 stands). The concierge is told, fire-and-forget, so its optimistic
    /// delivery agrees with what the operator is actually looking at.</summary>
    void Band_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BandView b) return;
        FocusWorkspace(b.WorkspaceId);
        e.Handled = true;
    }

    /// <summary>The one path from a band to the grid — a click and the `ui workspace` verb
    /// both land here. Accepts an id or a name, because a test naming "personal" should not
    /// have to know its slug.</summary>
    public string FocusWorkspace(string nameOrId)
    {
        var band = _vm.Bands.FirstOrDefault(b =>
            b.WorkspaceId.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
            b.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
        var id = band?.WorkspaceId ?? nameOrId;
        if (!_shell.Focus(id)) return $"error: workspace '{nameOrId}' is not on screen";
        _shell.Invalidate();
        var label = band?.Name ?? id;
        _vm.Status = $"workspace {label}";
        // The concierge is told, fire-and-forget: swapping the grid is a VIEW choice and the
        // window does not wait on anything, but the ladder's optimistic delivery uses
        // focused_workspace, and delivering to one the operator stopped looking at is exactly
        // the wrong-workspace error the review-behind then has to catch (§2.3).
        SendConcierge(new { cmd = "focus", workspace = id });
        return $"workspace {label}";
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
