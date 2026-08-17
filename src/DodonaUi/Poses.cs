namespace DodonaUi;

/// <summary>
/// Seeded poses (§17): put the UI into each visual state on demand, deterministically —
/// fixed strings, fixed counts, no store, no clock. "Screenshot whatever state the app
/// happens to be in" cannot verify a layout change; posing the state can. These double
/// as the fixture set for visual regression later.
/// </summary>
static class Poses
{
    public static readonly string[] Names = { "full", "badges", "blocked", "feed", "empty-slot", "tray", "overlay" };

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
                    "waiting on you: merge ticket 7 'sunset gradient' — dodona approve 7", false));
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
            default:
                return null;
        }
    }

    static List<FeedSnap> Feed(int n)
    {
        var all = new List<FeedSnap>
        {
            new(801, "WATER",   "2026-08-17T11:55:00Z", "→ retargeted to WATER (classifier, high)", false),
            new(802, "UI",      "2026-08-17T11:56:00Z", "ticket 5 approved — merge unblocked", true),
            new(803, "AUDIO",   "2026-08-17T11:57:00Z", "landed ticket 4 on main; verify green", false),
            new(804, "SKYBOX",  "2026-08-17T11:58:00Z", "claim extended: path:src/sky/clouds.hlsl", true),
            new(805, "NETCODE", "2026-08-17T11:59:00Z", "↩ undone: \"tighten the fog\" retracted", false),
            new(806, "BUILD",   "2026-08-17T12:00:00Z", "verify RED at 'dotnet test' — blocked ticket 9 created", false),
            new(807, "WATER",   "2026-08-17T12:01:00Z", "worktree pruned for ticket 3", true),
            new(808, "UI",      "2026-08-17T12:02:00Z", "waiting on you: merge ticket 8 'minimap' — dodona approve 8", false),
        };
        return all.Take(n).ToList();
    }
}
