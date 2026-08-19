using System.Text;
using System.Text.Json;

namespace Dodona;

/// <summary>
/// What an agent is DOING, mid-turn, in one line per thing — decided by code, no model
/// (§5: presence lines are computed from the structured tool events stream-json already
/// carries, and this is the same rule applied to the transcript instead of the status
/// line).
///
/// WHY THIS EXISTS. Selective compression (§5) cut mid-turn narration out of the pane on a
/// reasonable argument: an agent ends its turn when it needs you, so anything that needs
/// you IS a result, and what it is doing meanwhile is on the presence line. Measured
/// against a real four-minute turn on 2026-08-19, that argument does not survive contact:
/// the operator's lane went 111 wire lines and 18 tool calls with exactly TWO sentences on
/// screen, and 93 of those wire lines were `thinking_tokens`. Presence showed one tool —
/// the current one — because presence is a single column that each event overwrites. So the
/// pane was silent for minutes at a time and the operator's own words for it were "staring
/// at a blank screen hoping something's happening". A cut that leaves nothing in its place
/// is not compression, it is a blind spot.
///
/// THE MIDDLE GROUND, in the operator's own terms (2026-08-19): drop the meaningless,
/// mega-condense the medium, show the high-value abbreviated. That is three tiers:
///
/// - <see cref="ProgressTier.Noise"/> — no pane row at all. Bookkeeping tools whose text
///   would say nothing a human wants (`BashOutput` polling a shell it already started).
/// - <see cref="ProgressTier.Step"/> — one short line, and consecutive same-verb lines FOLD
///   into one at render time: `read 6 files: LaneRuntime.cs, LaneSink.cs, Store.cs +3`.
///   Looking things up is most of what a turn is; it deserves proof-of-life, not six lines.
/// - <see cref="ProgressTier.Act"/> — its own line, always, abbreviated. Something CHANGED
///   or something ran: an edit, a command, a subagent, a tool that failed. These are the
///   lines you would want to have seen before the turn ended badly.
///
/// An UNKNOWN tool is <see cref="ProgressTier.Act"/> on purpose. A new tool nobody has
/// classified yet must appear, not vanish — a silent degrade is a bug (CLAUDE.md §3, the
/// dead routing ladder), and "the pane went quiet again because Claude Code shipped a tool
/// name we do not know" is exactly that bug wearing this component's clothes.
///
/// Everything here is a pure function over a tool name, its input JSON, and a list of
/// rows, so all of it is covered by `dev test unit` in about a second — no daemon, no
/// store, no window. Linked into DodonaUi as SOURCE (see DodonaUi.csproj) for the same
/// reason Ask.cs is: the daemon WRITES these rows and the window FOLDS them, and two
/// implementations of "what does this row mean" would be two answers to one question.
/// </summary>
enum ProgressTier
{
    /// <summary>Not worth a row. Presence still updates; the raw line is still stored.</summary>
    Noise,

    /// <summary>Worth a row, foldable with its neighbours of the same verb.</summary>
    Step,

    /// <summary>Worth its own row, never folded.</summary>
    Act,
}

static class PaneProgress
{
    /// <summary>The pane_events kind these rows carry. Its own kind, not `agent_line`:
    /// the pane filters by kind, `dodona tail` prints it, and the two are different things
    /// — an agent's sentence to you, versus a fact about what it did.</summary>
    public const string Kind = "progress";

    /// <summary>Longest line this component will ever emit. A pane is narrow and a folded
    /// line has to fit beside `you>` and `✓` rows without reflowing the tile.</summary>
    const int MaxLine = 110;

    // ------------------------------------------------------------------ classification

    /// <summary>One `tool_use` block from an assistant event → the tier and the line.
    /// Never throws: a malformed or absent input object degrades to the tool's own name,
    /// which is still true and still visible.</summary>
    public static (ProgressTier Tier, string Text) FromTool(string tool, JsonElement input)
    {
        tool = (tool ?? "").Trim();
        if (tool.Length == 0) return (ProgressTier.Noise, "");

        // MCP tools are `mcp__<server>__<tool>`, and the server name is the part a human
        // recognises ("slack", "atlassian"). Reading is what almost all of them do.
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__", StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length >= 3 ? $"{parts[1]} {parts[2]}" : tool[5..];
            return (ProgressTier.Step, Line("called", name));
        }

        switch (tool)
        {
            // ---- Noise: the agent talking to its own bookkeeping -----------------------
            case "BashOutput":
            case "KillShell":
            case "TaskOutput":
                return (ProgressTier.Noise, "");

            // ---- Step: looking things up ----------------------------------------------
            case "Read":
            case "NotebookRead":
                return (ProgressTier.Step, Line("read", FileName(Str(input, "file_path") ?? Str(input, "notebook_path"))));
            case "Glob":
                return (ProgressTier.Step, Line("glob", Str(input, "pattern")));
            case "Grep":
            {
                var pat = Quote(Str(input, "pattern"));
                var where = Tail(Str(input, "path"));
                return (ProgressTier.Step, Line("grep", where.Length > 0 ? $"{pat} in {where}" : pat));
            }
            case "LS":
                return (ProgressTier.Step, Line("ls", Tail(Str(input, "path"))));
            case "WebFetch":
                return (ProgressTier.Step, Line("fetched", Host(Str(input, "url"))));
            case "WebSearch":
                return (ProgressTier.Step, Line("searched", Quote(Str(input, "query"))));
            case "TodoWrite":
                // The one todo marked in_progress is the agent's own statement of what it
                // is doing right now — the single most useful sentence in the whole
                // stream, and it arrives free. A run of these folds, and identical
                // consecutive subjects collapse, so re-writing the same list costs nothing.
                return (ProgressTier.Step, Line("now", ActiveTodo(input)));

            // ---- Act: something changed, or something ran -----------------------------
            case "Write":
                return (ProgressTier.Act, Line("wrote", FileName(Str(input, "file_path"))));
            case "Edit":
            case "NotebookEdit":
                return (ProgressTier.Act, Line("edited", FileName(Str(input, "file_path") ?? Str(input, "notebook_path"))));
            case "MultiEdit":
            {
                var n = Count(input, "edits");
                var f = FileName(Str(input, "file_path"));
                return (ProgressTier.Act, Line("edited", n > 1 ? $"{f} ({n} edits)" : f));
            }
            case "Bash":
            case "PowerShell":
                // The COMMAND, not the tool's `description` field. A description is the
                // agent's summary of its own intent; the command is what will actually run
                // on the operator's machine, and this pane is the only place they would
                // ever see it before it does.
                return (ProgressTier.Act, Line("$", Collapse(Str(input, "command")), 78));
            case "Task":
                return (ProgressTier.Act, Line("subagent", Str(input, "description") ?? Str(input, "subagent_type")));
            case "SlashCommand":
                return (ProgressTier.Act, Line("ran", "/" + (Str(input, "command") ?? "")));
            case "ExitPlanMode":
                return (ProgressTier.Act, "presented a plan");

            // ---- Unknown: SHOW IT (see the class comment) -----------------------------
            default:
                return (ProgressTier.Act, Line(tool.ToLowerInvariant(), FirstString(input)));
        }
    }

    /// <summary>As <see cref="FromTool"/>, from the input object's JSON text. The daemon has
    /// a live <see cref="JsonElement"/> and uses the other overload; this one exists so the
    /// pure-logic tests can state a case as the JSON the wire actually carries.</summary>
    public static (ProgressTier Tier, string Text) FromToolJson(string tool, string inputJson)
    {
        try
        {
            using var d = JsonDocument.Parse(inputJson);
            return FromTool(tool, d.RootElement);
        }
        catch
        {
            using var empty = JsonDocument.Parse("{}");
            return FromTool(tool, empty.RootElement);
        }
    }

    /// <summary>A FAILED tool result → the line, or null when there is nothing to say.
    ///
    /// This is the half of the stream the pane never had at all, and it is the half that
    /// answers "is it flailing?". A tool_result arrives as a `user` event, which fell
    /// through the wire switch into an unread raw row — so a build that would not compile,
    /// a command with an unbalanced quote, a path that does not exist were all invisible
    /// until the turn ended. `tool` is resolved from the id the assistant used, so the line
    /// can name what failed; unresolved it still reports, unnamed.</summary>
    public static string? FromFailedResult(string? tool, string? content)
    {
        var first = Collapse(content);
        if (first.Length == 0 && (tool ?? "").Length == 0) return null;
        var what = (tool ?? "").Length > 0 ? tool!.ToLowerInvariant() : "tool";
        return Cut($"! {what} failed: {first}", MaxLine);
    }

    // ------------------------------------------------------------------------ folding

    /// <summary>Verbs whose consecutive rows fold into one, and the noun for a folded
    /// count. Only <see cref="ProgressTier.Step"/> verbs are here: an Act row is a thing
    /// that happened and must keep its own line.</summary>
    static readonly Dictionary<string, string> FoldNoun = new(StringComparer.Ordinal)
    {
        ["read"] = "files",
        ["glob"] = "globs",
        ["grep"] = "patterns",
        ["ls"] = "paths",
        ["fetched"] = "pages",
        ["searched"] = "queries",
        ["called"] = "calls",
        ["now"] = "steps",
    };

    /// <summary>The fold key of a body — its verb, when that verb folds; null otherwise
    /// (an Act row, an error row, anything unrecognised). Deriving the key from the text
    /// keeps the store the only state: a reader folds correctly over rows written by an
    /// older build, and over rows it is seeing for the second time.</summary>
    public static string? FoldKey(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var sp = body.IndexOf(' ');
        if (sp <= 0) return null;
        var verb = body[..sp];
        return FoldNoun.ContainsKey(verb) ? verb : null;
    }

    /// <summary>Fold a pane transcript: consecutive progress rows sharing a fold key
    /// become one row, identical subjects collapsing rather than repeating. Every other
    /// row passes through untouched, in order.
    ///
    /// AT RENDER, NOT AT WRITE, and that is the load-bearing choice. The store keeps one
    /// row per tool call — so `seq` dedup stays exactly-once on shim redelivery (a folded
    /// row would have to be rewritten by a replay, which is the one thing UNIQUE(lane_id,
    /// seq) is there to make impossible), the overlay still shows every step, and the fold
    /// is a pure function anyone can change without a migration.</summary>
    public static List<(string Kind, string Body)> Fold(IEnumerable<(string Kind, string Body)> rows)
    {
        var outp = new List<(string Kind, string Body)>();
        string? key = null;
        var subjects = new List<string>();

        void Flush()
        {
            if (key is null) return;
            outp.Add((Kind, Render(key, subjects)));
            key = null;
            subjects.Clear();
        }

        foreach (var (k, body) in rows)
        {
            var fk = k == Kind ? FoldKey(body) : null;
            if (fk is null) { Flush(); outp.Add((k, body)); continue; }
            if (fk != key) Flush();
            key = fk;
            var subject = body[(fk.Length + 1)..].Trim();
            // Distinct, order preserved: an agent re-reading one file or re-writing an
            // unchanged todo list must not inflate the count it is folded into.
            if (subject.Length > 0 && !subjects.Contains(subject, StringComparer.Ordinal)) subjects.Add(subject);
        }
        Flush();
        return outp;
    }

    /// <summary>`read Store.cs` for one, `read 6 files: a, b, c +3` for many. Three
    /// subjects because a pane is narrow and the fourth name is never the one that tells
    /// you what is going on — the count is.</summary>
    static string Render(string verb, List<string> subjects)
    {
        if (subjects.Count == 0) return verb;
        if (subjects.Count == 1) return Cut($"{verb} {subjects[0]}", MaxLine);
        var noun = FoldNoun.TryGetValue(verb, out var n) ? n : "steps";
        var shown = subjects.Take(3);
        var rest = subjects.Count - 3;
        var sb = new StringBuilder($"{verb} {subjects.Count} {noun}: ").AppendJoin(", ", shown);
        if (rest > 0) sb.Append(" +").Append(rest);
        return Cut(sb.ToString(), MaxLine);
    }

    // ------------------------------------------------------------------------ plumbing

    static string Line(string verb, string? subject, int cap = MaxLine)
    {
        subject = (subject ?? "").Trim();
        return subject.Length == 0 ? verb : Cut($"{verb} {subject}", cap);
    }

    static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int Count(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.GetArrayLength() : 0;

    /// <summary>The first string value in the input object — what an unclassified tool gets
    /// to show. Ordered by the property order the caller sent, which is the closest thing
    /// to "the important argument" available without knowing the tool.</summary>
    static string? FirstString(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in o.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                return Collapse(s);
        return null;
    }

    /// <summary>The todo marked in_progress, else the first pending one, else nothing.
    /// `content` is the modern key and `activeForm` the older spelling; accept both, since
    /// a shim can be older than the daemon reading it (§13).</summary>
    static string? ActiveTodo(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array) return null;
        string? firstPending = null;
        foreach (var t in todos.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var text = Str(t, "activeForm") ?? Str(t, "content");
            if (text is null or "") continue;
            var status = Str(t, "status") ?? "";
            if (status == "in_progress") return Collapse(text);
            firstPending ??= Collapse(text);
        }
        return firstPending;
    }

    /// <summary>The leaf of a path. A pane is too narrow for a directory and the file is
    /// what identifies the work; `dodona tail` and the overlay keep the full path.</summary>
    static string? FileName(string? path)
    {
        if (path is null or "") return path;
        var s = path.Replace('\\', '/').TrimEnd('/');
        var i = s.LastIndexOf('/');
        return i < 0 ? s : s[(i + 1)..];
    }

    /// <summary>The last two segments of a directory path — `src/Dodona`, not the whole
    /// absolute path, and not just `Dodona`, which could be anywhere.</summary>
    static string Tail(string? path)
    {
        if (path is null or "") return "";
        var parts = path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Join('/', parts) : string.Join('/', parts[^2..]);
    }

    static string Host(string? url)
    {
        if (url is null or "") return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : Cut(url, 40);
    }

    static string Quote(string? s) => (s ?? "").Length == 0 ? "" : $"\"{Collapse(s)}\"";

    /// <summary>One line, single-spaced. A `command` is routinely multi-line and a tool
    /// result always is, and a pane row is one line by construction — a raw newline in a
    /// body would break the transcript's shape wherever it is rendered.</summary>
    static string Collapse(string? s) =>
        (s ?? "").Length == 0 ? "" : System.Text.RegularExpressions.Regex.Replace(s!, @"\s+", " ").Trim();

    static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)].TrimEnd() + "…";
}
