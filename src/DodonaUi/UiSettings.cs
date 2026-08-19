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

    /// <summary>Two preferences now. <paramref name="Listening"/> is null for "never chose",
    /// which reads as off -- a microphone that armed itself because a file was missing would be
    /// the worst possible default.</summary>
    public sealed record Values(double? InputHeight, bool? Listening);

    public static Values Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Values(null, null);
            var e = JsonDocument.Parse(File.ReadAllText(Path)).RootElement;
            double? height = e.TryGetProperty("inputHeight", out var h) && h.ValueKind == JsonValueKind.Number
                ? h.GetDouble() : null;
            bool? listening = e.TryGetProperty("listening", out var l)
                              && l.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? l.GetBoolean() : null;
            return new Values(height, listening);
        }
        catch { /* a preference is not worth an error dialog */ }
        return new Values(null, null);
    }

    /// <summary>Save the dragged height, or null to forget it (a double-click on the grip
    /// says "go back to the default", which must survive a restart exactly as a drag
    /// does — otherwise refitting the box would silently un-refit itself next launch).</summary>
    public static void SaveInputHeight(double? height) => Save(height, Load().Listening);

    /// <summary>Remember the dictation toggle (D-V2). A toggle that resets itself is a button,
    /// and publish hot-swaps this window, so an unremembered toggle would go silently deaf on
    /// every swap. DODONA_UI_MIC=off outranks this and is checked where the recogniser is
    /// constructed, not here -- a preference must never be able to open a microphone that the
    /// machine has said no to.</summary>
    public static void SaveListening(bool listening) => Save(Load().InputHeight, listening);

    /// <summary>One writer, so a save of either preference cannot drop the other. Writing only
    /// the field that changed is how the box's remembered height would have vanished the first
    /// time the mic was toggled.</summary>
    static void Save(double? height, bool? listening)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            File.WriteAllText(Path, JsonSerializer.Serialize(new { inputHeight = height, listening }));
        }
        catch { /* read-only home, roaming profile mid-sync: still not worth failing over */ }
    }
}
