using System.IO;
using System.Text.Json;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The handful of things the WINDOW remembers, and nothing else. Today that is one number:
/// how tall the operator dragged the dispatcher box.
///
/// Why a file and not the store: the store is per-workspace, and this window may be the
/// shell — which spans workspaces and, booted to zero, has no store to read at all. So it
/// lives beside the workspaces under DODONA_HOME (§5: Dodona's state is never repo content,
/// and DODONA_HOME is what stops a suite littering the operator's real settings).
///
/// It is a preference, never data: every read failure is a silent fall back to the default.
/// A corrupt ui.json must not be able to stop the window opening — the box would be the one
/// thing you could not use to say so.
/// </summary>
static class UiSettings
{
    public static string Path => System.IO.Path.Combine(Paths.Home, "ui.json");

    public sealed record Values(double? InputHeight);

    public static Values Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Values(null);
            var e = JsonDocument.Parse(File.ReadAllText(Path)).RootElement;
            if (e.TryGetProperty("inputHeight", out var h) && h.ValueKind == JsonValueKind.Number)
                return new Values(h.GetDouble());
        }
        catch { /* a preference is not worth an error dialog */ }
        return new Values(null);
    }

    /// <summary>Save the dragged height, or null to forget it (a double-click on the grip
    /// says "go back to the default", which must survive a restart exactly as a drag
    /// does — otherwise refitting the box would silently un-refit itself next launch).</summary>
    public static void SaveInputHeight(double? height)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            File.WriteAllText(Path, JsonSerializer.Serialize(new { inputHeight = height }));
        }
        catch { /* read-only home, roaming profile mid-sync: still not worth failing over */ }
    }
}
