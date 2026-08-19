using System.Linq;
using System.Text.RegularExpressions;

namespace Dodona;

/// <summary>What the project ladder decided, and by which rung. <see cref="Project"/> is null
/// exactly when the caller must NOT spawn: <c>classify</c> (ask the cheap tier, using
/// <see cref="Candidates"/>) and <c>ask</c> (hold the sentence and ask the operator).</summary>
sealed record ProjectVerdict(string Rung, string? Project, string How, IReadOnlyList<string> Candidates);

/// <summary>
/// WHICH PROJECT A TYPED SENTENCE MEANS (docs/LOCATIONS-PLAN.md Phase 3) — the operator's
/// order of precedence, minus the rung that is not about projects at all.
///
/// Their list is: (1) a comment for an existing lane, (2) a new lane in a project an open lane
/// is already in, (3) a new lane in a project we have a memory of, (4) we do not know — ask.
/// Rung 1 is the four-verdict lane ladder in <see cref="Daemon.RouteInput"/> and was already
/// built; by the time anything here runs, that ladder has already said "this is new work". So
/// this decides only the SECOND question, and only for a lane that is about to be born.
///
/// **Pure, and deliberately so.** No I/O, no registry, no model, no daemon: the caller hands in
/// the projects, the spoken handles, and which projects currently hold live lanes. That keeps
/// the whole decision on the ~1 second `unit` loop beside <see cref="Daemon.IsObviousGeneric"/>,
/// <see cref="Daemon.LanePrefix"/> and <see cref="Projects.Of"/> — and this is a decision that
/// has to be cheap to re-check, because the one-project answer below is the property the entire
/// workspace migration rested on.
///
/// **The one-project case is answered first and answers `only`.** A workspace with one project
/// has no question to answer, so nothing is classified, nothing is asked, no event is written
/// and no model is touched. The operator's own machine is a one-project workspace and its
/// behaviour must be byte-for-byte what it was before this phase existed.
///
/// **`named` runs before `classify`, and that is not the operator's list reordered.** It is the
/// same rule the concierge's own ladder already applies one level up (Concierge.ResolveAsync
/// rung 0/1 before rung 2): *explicit information never triggers a search*. A sentence that
/// names a project is the operator telling us where, and it is decidable in code for free —
/// whereas rung 2's question is "which of these open projects did you mean", which is only worth
/// asking a model when the sentence itself did not say. The two rungs agree wherever they both
/// have an answer; where they disagree, the named one is right. Consulting the classifier first
/// would let "fix the header on <b>" go to a lane in A because A happens to be busy, which is
/// the unrecoverable error this phase exists to avoid making instantly.
///
/// **`ask` is a real answer.** With several projects, no name in the sentence and no live lane
/// to infer from, there is no honest guess: a lane opened in the wrong project is an agent
/// editing the wrong repository, and unlike a wrong lane that is not undone by one
/// `lane-stop`. So the sentence is held (§5's error asymmetry, applied one level down) and the
/// candidates come back ordered so the caller can offer them.
/// </summary>
static class ProjectLadder
{
    /// <summary>One project: there is no question. Free, and byte-for-byte today's answer.</summary>
    public const string Only = "only";
    /// <summary>The sentence NAMES a project: leaf, alias, or spoken form. Code, free.</summary>
    public const string Named = "named";
    /// <summary>A project with a live lane in it, and only one such project. Code, free.</summary>
    public const string Live = "live";
    /// <summary>Several projects hold live lanes: one cheap call, over <see cref="ProjectVerdict.Candidates"/>.</summary>
    public const string Classify = "classify";
    /// <summary>We do not know. Hold the sentence and ask (Phase 4 renders it).</summary>
    public const string Ask = "ask";

    static readonly IReadOnlyList<string> None = Array.Empty<string>();

    /// <summary>
    /// The ladder. <paramref name="liveProjects"/> must come from a liveness answer that is not
    /// one instantaneous pipe read (D-L6, <see cref="LaneLiveness"/>) — a shim's pipe name
    /// blinks out of the namespace while its server swaps instances, and 8 reads in 192 over
    /// 1.5 s saw nothing while the shim was alive. A single read here would silently demote a
    /// perfectly good rung 2 to an unnecessary question, or worse, to a different project.
    /// </summary>
    public static ProjectVerdict Decide(
        IReadOnlyList<string> projects,
        IReadOnlyList<(string Alias, string Key)> handles,
        IReadOnlyList<string> liveProjects,
        string text)
    {
        // No projects at all: there is nothing to choose and nothing to guess. The caller
        // refuses through Daemon.TryProject rather than spawning into a folder nobody owns.
        if (projects.Count == 0) return new ProjectVerdict(Ask, null, "no-projects", None);

        // THE ONE-PROJECT CASE, FIRST AND FREE. Before any name matching, any liveness read and
        // any model: one project is one answer, so this phase must be invisible here.
        if (projects.Count == 1) return new ProjectVerdict(Only, projects[0], "sole", None);

        if (NameMatch(projects, handles, text) is (string named, string how))
            return new ProjectVerdict(Named, named, how, None);

        // Rung 2. Only projects this workspace still owns count: a lane whose recorded folder
        // outlived its project (trap T4) must not drag a new lane into a folder that now
        // belongs to somebody else.
        var live = projects.Where(p => liveProjects.Any(l => Same(l, p))).ToList();
        if (live.Count == 1) return new ProjectVerdict(Live, live[0], "sole-live", None);
        if (live.Count > 1) return new ProjectVerdict(Classify, null, "live-lanes", live);

        // Rung 4. Candidates ordered by recency of use, which the caller derives from its own
        // lane rows — see Daemon.ProjectsByRecency for why that is not a registry column.
        return new ProjectVerdict(Ask, null, "no-name-no-live-lane", projects);
    }

    /// <summary>
    /// Rung 3, entirely in code: does the sentence NAME one of these projects? Three ways, in
    /// order, and the order is cheapest-and-most-certain first:
    ///
    ///   * <b>leaf</b>   — the project folder's own name, `engine`, appears in the sentence.
    ///   * <b>alias</b>  — a spoken handle taught for that project (`aliases.member_key`,
    ///                     registry schema 2). This is the half `members` could not carry:
    ///                     `members` already IS every project ever attached, but nothing said
    ///                     what the operator CALLS one (D-L5).
    ///   * <b>spoken</b>  — the leaf's words, said as words: `project zed` for `project-zed`.
    ///                     Voice typing and ordinary English both put spaces where a folder name
    ///                     has hyphens, dots or underscores, and a memory that only matched the
    ///                     literal folder name would miss the way people actually talk.
    ///
    /// Longest name first, for the reason the concierge's rung 1 does it: `work-ui` must beat
    /// `work`, or a workspace with both can never be addressed by the longer name.
    /// </summary>
    public static (string Project, string How)? NameMatch(
        IReadOnlyList<string> projects,
        IReadOnlyList<(string Alias, string Key)> handles,
        string text)
    {
        foreach (var p in projects.OrderByDescending(p => Leaf(p).Length))
            if (Mentions(text, Leaf(p))) return (p, "leaf");

        foreach (var h in handles.OrderByDescending(h => h.Alias.Length))
        {
            if (!Mentions(text, h.Alias)) continue;
            var owner = projects.FirstOrDefault(p => Key(p) == h.Key);
            if (owner is not null) return (owner, "alias");
        }

        foreach (var p in projects.OrderByDescending(p => Leaf(p).Length))
            if (Spoken(text, Leaf(p))) return (p, "spoken");

        foreach (var h in handles.OrderByDescending(h => h.Alias.Length))
        {
            if (!Spoken(text, h.Alias)) continue;
            var owner = projects.FirstOrDefault(p => Key(p) == h.Key);
            if (owner is not null) return (owner, "alias");
        }

        return null;
    }

    /// <summary>The folder's own name — what a person says when they mean this project.</summary>
    public static string Leaf(string projectPath) =>
        projectPath.TrimEnd('\\', '/') is var t && t.Length > 0
            ? t[(t.LastIndexOfAny(new[] { '\\', '/' }) + 1)..]
            : projectPath;

    /// <summary>A project's identity, lowercased and separator-trimmed — the same key
    /// `members.key` holds, so a handle taught against one spelling is found by the other
    /// (SQLite `=` and string `==` are both binary-collated while live disk casing is not).</summary>
    public static string Key(string projectPath) => projectPath.TrimEnd('\\', '/').ToLowerInvariant();

    static bool Same(string a, string b) => Key(a) == Key(b);

    /// <summary>
    /// Does the text name this handle? Word-bounded for plain handles so "work" does not match
    /// "network"; a substring match for handles carrying punctuation, where a word boundary is
    /// not meaningful.
    ///
    /// This is the concierge's rung-1 matcher, moved here rather than copied: both ladders are
    /// answering "did the operator say this name", and two implementations of that would drift
    /// the moment one of them learned something (Concierge.Mentions now calls this one).
    /// </summary>
    public static bool Mentions(string text, string handle)
    {
        if (handle.Length < 2) return false;
        var esc = Regex.Escape(handle);
        var pattern = handle.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            ? $@"(?<![\w-]){esc}(?![\w-])"
            : esc;
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The handle said as WORDS: `project zed` for `project-zed`. Splits the handle on the
    /// separators a folder name uses and requires those words consecutively in the text, with
    /// any of the same separators (or spaces) between them.
    ///
    /// A single-word handle returns false rather than duplicating <see cref="Mentions"/> — the
    /// two are asked in order and a rung that can only ever repeat the previous one is a rung
    /// that lies about which evidence was used.
    /// </summary>
    public static bool Spoken(string text, string handle)
    {
        var words = handle.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;
        var pattern = @"(?<![\w-])" + string.Join(@"[\s\-_.]+", words.Select(Regex.Escape)) + @"(?![\w-])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }
}
