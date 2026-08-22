using System.IO;
using System.Text.Json;
using Dodona;
using Dodona.Testing.Ledger;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// ══ THE CORPUS RUNG: THE ONLY THING IN THIS REPOSITORY THAT REACHES D3 ══
///
/// Plan 3.1's third drift axis is *"`claude` moved; the double is faithful to yesterday"*, and its
/// row in that table reads **nothing automatic**. No interface, no contract and no compiler can
/// see it, because the thing that moved is outside this repository. The only instrument is REAL
/// RECORDED BYTES replayed through the REAL parser, and that is what this class is.
///
/// `tests\assets\wire\real\wire.jsonl` is `spikes\spike1-output\wire.jsonl` byte-for-byte -- a
/// tracked, real, 20-line `claude -p --output-format stream-json` transcript that costs no quota
/// because it is already in git. **Its BOM is kept deliberately.** Line 1 carries one, and a
/// leading U+FEFF handed back as an ordinary character is the exact pair that made the claim gate
/// fail open on every run while looking green (CLAUDE.md 3, the GateHook incident). A fixture that
/// quietly stripped it would be a fixture that could never reproduce the bug.
///
/// ══ WHAT IT ASSERTS, AND WHY THAT AND NOT SOMETHING STRONGER ══
///
/// `MANIFEST.json` records, per `(type, subtype, content-block-type)` shape, the `pane_events`
/// kind the real parser produced when the recording was replayed -- or `(no pane row)` where it
/// deliberately produced none. So changing a `case` in `LaneRuntime.HandleShimLine` reddens this,
/// AT A LINE NUMBER (plan 3.3's own row for it).
///
/// It is deliberately NOT a snapshot of every field. A snapshot goes red for a whitespace change
/// and teaches people to re-bless it, which is the same disease as a gate that is always green.
/// The classification is the decision; the rest is rendering.
///
/// ══ THE DEBT THIS FILE DECLARES RATHER THAN HIDES ══
///
/// The seed witnesses SIX shapes. `HandleShimLine` handles about ten. `MANIFEST.json`'s
/// `unwitnessed[]` names each gap with a reason from a closed vocabulary and an open issue
/// (**#18**), and <see cref="Every_unwitnessed_shape_is_declared_with_a_reason_and_an_issue"/>
/// refuses one without. That is plan 3.4's rule applied to itself: a mechanism whose failure mode
/// is *"edit a JSON list and be green"* is a convention wearing enforcement clothes.
/// </summary>
public class WireCorpusTests
{
    static string AssetDir => Path.Combine(CheckLedger.RepoRoot, "tests", "assets", "wire");
    static string CorpusPath => Path.Combine(AssetDir, "real", "wire.jsonl");

    static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetDir, "MANIFEST.json")));

    /// <summary>
    /// The tuple the parser switches on. `content-block-type` is the set of distinct
    /// `message.content[].type` values, joined -- one real `assistant` message is thinking-only
    /// and the next is text-only, sharing a `message.id`, which is a thing plan 3.4 records that
    /// a hand-written fake would never have produced.
    /// </summary>
    static string ShapeOf(string line)
    {
        using var d = JsonDocument.Parse(line);
        var root = d.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var sub = root.TryGetProperty("subtype", out var s) ? s.GetString() ?? "" : "";
        var blocks = new List<string>();
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var c in content.EnumerateArray())
                if (c.TryGetProperty("type", out var ct) && ct.GetString() is { } ctv && !blocks.Contains(ctv))
                    blocks.Add(ctv);
        return blocks.Count == 0 && sub.Length == 0 ? type
             : blocks.Count == 0 ? type + "/" + sub
             : type + "/" + sub + "/" + string.Join("+", blocks);
    }

    /// <summary>Read with `utf-8-sig` semantics ON PURPOSE, because that is what the SHIM does:
    /// the daemon receives one line at a time off a pipe and the BOM belongs to the first of
    /// them. Kept here so the fixture's own reader can never be the thing that hides it.</summary>
    static string[] Lines() => File.ReadAllLines(CorpusPath)
                                   .Select(l => l.TrimStart('﻿'))
                                   .Where(l => l.Trim().Length > 0)
                                   .ToArray();

    [Fact]
    public void The_corpus_is_present_and_keeps_its_BOM()
    {
        Assert.True(File.Exists(CorpusPath), "no corpus at " + CorpusPath + " -- a missing artefact must never read as an empty one");
        var raw = File.ReadAllBytes(CorpusPath);
        Assert.True(raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "the recording's UTF-8 BOM was stripped. It is kept deliberately: a leading U+FEFF read back as an ordinary " +
            "character is the GateHook fail-open incident's own artefact, and a fixture without it cannot reproduce it.");
        Assert.Equal(20, Lines().Length);
    }

    /// <summary>
    /// EVERY recorded line, through the REAL parser, classified as the manifest records. This is
    /// the check plan 3.3 names for *"changed a `case` in `HandleShimLine`"*, and the failure
    /// message carries the line number because a corpus failure is otherwise unreadable.
    /// </summary>
    [Fact]
    public void every_recorded_line_classifies_as_recorded()
    {
        using var manifest = Manifest();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var expectedLines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in manifest.RootElement.GetProperty("witnessed").EnumerateArray())
        {
            expected[w.GetProperty("shape").GetString()!] = w.GetProperty("classifies_as").GetString()!;
            expectedLines[w.GetProperty("shape").GetString()!] = w.GetProperty("lines").GetInt32();
        }
        Assert.NotEmpty(expected);

        var sink = new RecordingLaneSink();
        var lane = sink.NewLane("SPIKE");
        var rt = new LaneRuntime(lane, "dodona-corpus", sink);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = Lines();
        var problems = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var shape = ShapeOf(lines[i]);
            seen[shape] = seen.TryGetValue(shape, out var n) ? n + 1 : 1;

            var before = sink.Panes.Count;
            rt.HandleShimLine((i + 1) + "\t" + lines[i]);
            var wrote = sink.Panes.Count - before;
            var got = wrote == 0 ? "(no pane row)" : sink.Panes[^1].Kind;

            if (!expected.TryGetValue(shape, out var want))
            {
                problems.Add($"wire.jsonl:{i + 1} shape '{shape}' is in no MANIFEST.json witnessed[] row -- " +
                             "the recording grew a shape nobody declared");
                continue;
            }
            if (got != want)
                problems.Add($"wire.jsonl:{i + 1} shape '{shape}' classified as '{got}', MANIFEST.json records '{want}' -- " +
                             "a case in LaneRuntime.HandleShimLine moved, or the manifest is stale");
        }

        foreach (var (shape, want) in expectedLines)
        {
            var got = seen.TryGetValue(shape, out var n) ? n : 0;
            if (got != want)
                problems.Add($"MANIFEST.json says shape '{shape}' appears on {want} line(s); the recording has {got}");
        }

        Assert.True(problems.Count == 0, string.Join("\r\n", problems));
    }

    /// <summary>
    /// The parser's SIDE EFFECTS on real bytes, not only its classification. These are the three
    /// things the recording can actually witness happening, and each is a different method on
    /// <c>ILaneSink</c> -- so this is also the only place the recording exercises the interface
    /// <see cref="RecordingLaneSink"/> is anchored to.
    /// </summary>
    [Fact]
    public void The_real_transcript_drives_session_presence_and_quota()
    {
        var sink = new RecordingLaneSink();
        var lane = sink.NewLane("SPIKE");
        var rt = new LaneRuntime(lane, "dodona-corpus", sink);
        var lines = Lines();
        for (var i = 0; i < lines.Length; i++) rt.HandleShimLine((i + 1) + "\t" + lines[i]);

        Assert.Equal("8e6542aa-9e76-4464-a4af-683458a731a5", sink.SessionOf(lane));
        // 11 thinking_tokens lines, and every one of them must say `thinking`, never leave the
        // tile reading as the last TOOL the agent ran. A tile that said `bash: ls -la docs/...`
        // for ninety seconds of pure reasoning is why that branch exists.
        Assert.Equal(11, sink.Presences.Count(p => p.Presence == "thinking…"));
        // The turn ended twice in this transcript, and presence must be `idle` at the end of each.
        Assert.Equal("idle", sink.PresenceOf(lane));
        Assert.True(sink.Kv.ContainsKey("rate_limit"),
            "the recorded rate_limit_event did not reach kv -- the quota reading is the one number pushed unasked");
    }

    /// <summary>
    /// Plan 3.4's rule applied to the escape hatch itself. `unwitnessed[]` is the one place this
    /// job lets somebody declare a gap instead of closing it, so the declaration is constrained
    /// the way `no-seam-yet` is: a reason from a CLOSED vocabulary, and an open tracker issue.
    /// Without that, the workflow is *"emit a shape, declare it unwitnessed, ship"*.
    /// </summary>
    [Fact]
    public void Every_unwitnessed_shape_is_declared_with_a_reason_and_an_issue()
    {
        var vocabulary = new[] { "not-in-seed", "costs-quota", "unreachable-offline" };
        using var manifest = Manifest();
        var bad = new List<string>();
        var rows = manifest.RootElement.GetProperty("unwitnessed").EnumerateArray().ToArray();
        Assert.NotEmpty(rows);      // an empty list would be this mechanism looking at nothing

        foreach (var u in rows)
        {
            var shape = u.TryGetProperty("shape", out var s) ? s.GetString() ?? "" : "";
            var reason = u.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var issue = u.TryGetProperty("issue", out var i) && i.TryGetInt32(out var n) ? n : 0;
            if (shape.Length == 0) bad.Add("an unwitnessed[] row names no shape");
            if (!vocabulary.Contains(reason))
                bad.Add($"unwitnessed shape '{shape}' gives reason '{reason}', outside the closed vocabulary [{string.Join(" ", vocabulary)}]");
            if (issue <= 0)
                bad.Add($"unwitnessed shape '{shape}' carries no issue number -- an untracked gap is one nobody will ever close");
        }

        // And a shape may not be in both lists: that is the accounting going quietly wrong.
        var witnessed = manifest.RootElement.GetProperty("witnessed").EnumerateArray()
                                .Select(w => w.GetProperty("shape").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var u in rows)
            if (witnessed.Contains(u.GetProperty("shape").GetString()))
                bad.Add($"shape '{u.GetProperty("shape").GetString()}' is declared BOTH witnessed and unwitnessed");

        Assert.True(bad.Count == 0, string.Join("\r\n", bad));
    }

    /// <summary>
    /// Provenance is a READING, not a staleness assertion (D-T13). A test that reddened when the
    /// corpus was older than N days would fail for a non-defect, on a date, and redden every
    /// historical commit under bisect -- teaching people to re-run instead of read. What is
    /// asserted is that the provenance is PRESENT and well formed: a recording nobody can date is
    /// a recording nobody can decide about.
    /// </summary>
    [Fact]
    public void The_corpus_says_where_it_came_from()
    {
        using var manifest = Manifest();
        var p = manifest.RootElement.GetProperty("provenance");
        foreach (var key in new[] { "recorded", "committed", "source", "model" })
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty(key).GetString()),
                "MANIFEST.json provenance." + key + " is empty");
        Assert.Equal(Lines().Length, p.GetProperty("lines").GetInt32());
    }
}
