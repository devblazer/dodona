namespace DodonaUi;

/// <summary>
/// Seeded poses (§17): put the UI into each visual state on demand, deterministically —
/// fixed strings, fixed counts, no store, no clock. "Screenshot whatever state the app
/// happens to be in" cannot verify a layout change; posing the state can. These double
/// as the fixture set for visual regression later.
/// </summary>
static class Poses
{
    public static readonly string[] Names =
    {
        "full", "badges", "blocked", "feed", "empty-slot", "tray", "overlay", "long",
        // The multi-workspace shell (WORKSPACES-CONCIERGE.md §6). Every new affordance owes
        // a deterministic pose (CLAUDE.md §3), and these are the three states a screenshot
        // could not otherwise reach without two live daemons and a concierge.
        "bands", "merged-feed", "boot-zero",
    };

    static PaneSnap Pane(long id, string title, string presence, int badge = 0, bool blocked = false,
                         bool focused = false, params string[] lines) =>
        new(id, title, "alive", presence, badge, blocked, focused,
            lines.Select(l =>
            {
                var i = l.IndexOf('|');
                return new LineSnap(l[..i], l[(i + 1)..]);
            }).ToList());

    static PaneSnap?[] SixPanes() => new PaneSnap?[]
    {
        Pane(1, "WATER", "idle", 0, false, true,
            "user_input|make the waves taller near the shore",
            "agent_line|Raising amplitude in the shallow band and rebuilding the shader.",
            "result|done: wave amplitude now depth-scaled, verified in sim",
            "user_input|now damp the foam a little",
            "agent_line|Reducing foam persistence from 0.8 to 0.6."),
        Pane(2, "SKYBOX", "write: clouds.hlsl", 0, false, false,
            "user_input|the sunset gradient bands too hard",
            "agent_line|Switching the gradient to a perceptual ramp; touching clouds.hlsl next."),
        Pane(3, "UI", "bash: dotnet test", 0, false, false,
            "user_input|move the minimap to the top right",
            "agent_line|Moved. Running the layout tests before I hand it back.",
            "result|done: minimap anchored top-right, 14 tests green"),
        Pane(4, "AUDIO", "idle", 0, false, false,
            "user_input|the footsteps echo in open fields",
            "agent_line|Reverb zones were inverted — outdoor was using the cave profile.",
            "result|done: reverb zone lookup fixed"),
        Pane(5, "NETCODE", "read: lobby.cs", 0, false, false,
            "user_input|players ghost through doors on high ping",
            "agent_line|Reading the lobby interpolation path to find where rewind loses the door state."),
        Pane(6, "BUILD", "working…", 0, false, false,
            "user_input|get the nightly green again",
            "agent_line|Two failures: one flaky timeout, one real regression in asset packing."),
    };

    /// <summary>Returns the posed snapshot, plus overlay/toast side-channels. Null when
    /// the name is unknown.</summary>
    public static (Snapshot Snap, string? OverlayTitle, ToastView? Toast)? Get(string name)
    {
        switch (name)
        {
            case "full":
                return (new Snapshot(SixPanes(), new(), Feed(3), null), null, null);

            case "badges":
            {
                var s = SixPanes();
                s[1] = s[1]! with { Badge = 1 };
                s[3] = s[3]! with { Badge = 3 };
                s[5] = s[5]! with { Badge = 12 };
                return (new Snapshot(s, new(), Feed(5), null), null, null);
            }
            case "blocked":
            {
                var s = SixPanes();
                s[1] = s[1]! with { Presence = "waiting on you: merge", Blocked = true, Badge = 1 };
                var feed = Feed(2);
                feed.Insert(0, new FeedSnap(901, "SKYBOX", "2026-08-17T12:00:00Z",
                    "waiting on you: merge ticket 7 'sunset gradient' — dodona approve 7", false, false));
                return (new Snapshot(s, new(), feed, null), null,
                        new ToastView { Ts = "2026-08-17T12:00:00Z", Lane = "SKYBOX", Reason = "blocked on you: merge" });
            }
            case "feed":
                return (new Snapshot(SixPanes(), new(), Feed(8), null), null, null);

            case "empty-slot":
            {
                var s = SixPanes();
                s[4] = null;
                s[5] = null;
                return (new Snapshot(s, new(), Feed(2), null), null, null);
            }
            case "tray":
                return (new Snapshot(SixPanes(), new List<string> { "TERRAIN", "SFX" }, Feed(3), null), null, null);

            // The case a pane is actually in most of the time: far more transcript than
            // pixels, and lines longer than a pane is wide. This is the fixture that
            // catches a pane which trims instead of wrapping, or clips instead of
            // scrolling — both of which it did before there was a pose for it.
            case "long":
            {
                var s = SixPanes();
                s[0] = s[0]! with { Lines = LongTranscript() };
                s[1] = s[1]! with { Lines = LongTranscript().Take(9).ToList() };
                return (new Snapshot(s, new(), Feed(4), null), null, null);
            }

            case "overlay":
            {
                var s = SixPanes();
                var ov = s[0]! with
                {
                    Lines = new List<LineSnap>
                    {
                        new("system", "init session=fake-90dd21"),
                        new("user_input", "make the waves taller near the shore"),
                        new("agent_line", "Raising amplitude in the shallow band and rebuilding the shader."),
                        new("wire", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"src/water/waves.hlsl\"}}]}}"),
                        new("result", "done: wave amplitude now depth-scaled, verified in sim"),
                        new("user_input", "now damp the foam a little"),
                        new("agent_line", "Reducing foam persistence from 0.8 to 0.6."),
                    },
                };
                return (new Snapshot(s, new(), Feed(3), ov), "WATER", null);
            }

            // ---- the multi-workspace shell (§6) ------------------------------------------

            // Shape B, posed: the focused workspace keeps the whole grid, two other awake
            // workspaces are bands. This is the fixture that catches bands stealing the
            // grid's height, or a band's chips wrapping into a second row unreadably.
            case "bands":
                return (new Snapshot(SixPanes(), new(), Feed(4), null)
                {
                    FocusedWorkspace = "dodona-dev-3f9a",
                    FocusedWorkspaceName = "dodona-dev",
                    Bands = Bands(),
                }, null, null);

            // The feed as a UNION: rows from three workspaces plus the concierge's own
            // group-scope voice, which belongs to no workspace's column by definition.
            case "merged-feed":
                return (new Snapshot(SixPanes(), new(), MergedFeed(), null)
                {
                    FocusedWorkspace = "dodona-dev-3f9a",
                    FocusedWorkspaceName = "dodona-dev",
                    Bands = Bands(),
                }, null, null);

            // Boot-to-zero: no workspace awake. A REAL state (§4), not an error — the grid is
            // an invitation and the input box is still the front door. Worth a pose precisely
            // because it is the first thing a new operator sees and the easiest to get wrong.
            case "boot-zero":
                return (new Snapshot(new PaneSnap?[6], new(), new List<FeedSnap>
                {
                    new(1, "", "2026-08-18T09:00:00Z",
                        "not sure which workspace “tidy up the shader warnings” is for. dodona-dev / personal, or new?",
                        false, true) { Workspace = "[dodona]", IsConcierge = true },
                }, null)
                {
                    FocusedWorkspace = "", FocusedWorkspaceName = "",
                }, null, null);

            default:
                return null;
        }
    }

    /// <summary>Two other awake workspaces, as bands. `personal` carries an attention badge
    /// so the fixture proves a band can say "you are needed over here" without the grid.</summary>
    static List<BandSnap> Bands() => new()
    {
        new BandSnap("personal-71c4", "personal", true, new List<BandLaneSnap>
        {
            new(1, "GARDEN", "idle", 2, false),
            new(2, "TAXES", "waiting on you: merge", 1, true),
            new(3, "PHOTOS", "read: exif.py", 0, false),
        }, Tray: 0, Badge: 3),
        new BandSnap("work-5e07", "work", true, new List<BandLaneSnap>
        {
            new(4, "INVOICES", "bash: pytest", 0, false),
            new(5, "ROSTER", "idle", 0, false),
        }, Tray: 0, Badge: 0),
    };

    static List<FeedSnap> MergedFeed() => new()
    {
        new(810, "", "2026-08-18T12:06:00Z",
            "“grease the winch” went to work, but it looks like personal. It was already delivered — resend if you meant personal.",
            false, true) { Workspace = "[dodona]", IsConcierge = true },
        new(809, "TAXES", "2026-08-18T12:05:00Z", "waiting on you: merge ticket 12 'Q3 return' — dodona approve 12", false, false) { Workspace = "personal" },
        new(808, "WATER", "2026-08-18T12:04:00Z", "→ retargeted to WATER (classifier, high)", false, false) { Workspace = "dodona-dev" },
        new(807, "INVOICES", "2026-08-18T12:03:00Z", "landed ticket 4 on main; verify green", true, false) { Workspace = "work" },
        new(806, "", "2026-08-18T12:02:00Z",
            "“the beacon gearbox” → personal; learned “gearbox”", true, true) { Workspace = "[dodona]", IsConcierge = true },
        new(805, "GARDEN", "2026-08-18T12:01:00Z", "claim extended: path:beds/rotation.md", false, false) { Workspace = "personal" },
        new(804, "SKYBOX", "2026-08-18T12:00:00Z", "started this lane on opus/high for “the sunset bands too hard”", true, false) { Workspace = "dodona-dev" },
    };

    static List<LineSnap> LongTranscript() => new()
    {
        new("user_input", "the shoreline foam looks wrong when the camera is low — it reads as a flat white band instead of individual crests"),
        new("agent_line", "Looking at it now. The foam mask is generated from wave height alone, so at grazing angles every crest above the threshold merges into one silhouette. Two candidates: fade the mask by view angle, or drive it from height *and* curvature so only breaking crests foam."),
        new("agent_line", "Going with curvature — the angle fade would kill foam in the distance too, and that reads as fog."),
        new("result", "done: foam mask now height×curvature, shoreline crests separate at grazing angles"),
        new("user_input", "better. now it flickers on the third wave from shore"),
        new("agent_line", "That band is exactly where curvature crosses the threshold, so adjacent frames land either side of it. Adding hysteresis: 0.02 of slack between the on and off thresholds."),
        new("result", "done: foam threshold hysteresis added, flicker gone in a 600-frame capture"),
        new("announcement", "claim extended: path:src/water/foam.hlsl"),
        new("user_input", "run the sim tests before you hand it back"),
        new("agent_line", "Running. 41 of 43 green; the two reds are the golden-image tests for the shoreline, which is the thing we just changed on purpose. Re-baselining those two and leaving the rest untouched."),
        new("result", "done: 43 tests green, 2 shoreline goldens re-baselined"),
        new("user_input", "what did the re-baseline actually change"),
        new("agent_line", "Pixel diff is confined to the first three wave bands; mean delta 0.03, max 0.21 at the crests. Nothing moved offshore, which is what you would want if only the mask changed."),
    };

    static List<FeedSnap> Feed(int n)
    {
        // The DODONA rows are the system speaking in its own voice (Daemon.Announce) —
        // the dispatcher lane holds no grid slot, so this is the only fixture coverage
        // for a feed row that has no lane colour to inherit.
        var all = new List<FeedSnap>
        {
            new(801, "WATER",   "2026-08-17T11:55:00Z", "→ retargeted to WATER (classifier, high)", false, false),
            new(802, "UI",      "2026-08-17T11:56:00Z", "ticket 5 approved — merge unblocked", true, false),
            new(803, "DODONA",  "2026-08-17T11:56:30Z", "[dodona] swapped to build 1.0.0+20260817 — 6 lane(s) adopted, nothing interrupted", false, true),
            new(804, "AUDIO",   "2026-08-17T11:57:00Z", "landed ticket 4 on main; verify green", false, false),
            new(805, "SKYBOX",  "2026-08-17T11:58:00Z", "claim extended: path:src/sky/clouds.hlsl", true, false),
            new(806, "NETCODE", "2026-08-17T11:59:00Z", "↩ undone: \"tighten the fog\" retracted", false, false),
            new(807, "DODONA",  "2026-08-17T11:59:30Z", "[dodona] update ready — a lane is mid-turn. swap now / when it lands / hold", false, true),
            new(808, "BUILD",   "2026-08-17T12:00:00Z", "verify RED at 'dotnet test' — blocked ticket 9 created", false, false),
            new(809, "WATER",   "2026-08-17T12:01:00Z", "worktree pruned for ticket 3", true, false),
            new(810, "UI",      "2026-08-17T12:02:00Z", "waiting on you: merge ticket 8 'minimap' — dodona approve 8", false, false),
        };
        return all.Take(n).ToList();
    }
}
