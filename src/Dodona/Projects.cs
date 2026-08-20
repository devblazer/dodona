using System.IO;                 // explicit: this file also compiles into the WPF project,
using System.Linq;               // whose implicit usings are narrower than the console one's

namespace Dodona;

/// <summary>
/// WHERE A LANE IS, as a decision rather than a lookup — and as something a person can see.
///
/// A **project** is one folder: a `members` row (docs/GLOSSARY.md). A lane's own folder is
/// `lanes.cwd` (schema 8). Those two facts had, between them, no observable surface in the
/// entire product: the `shim_spawned` event detail was the only place a lane's directory
/// ever appeared, no suite read `lanes.cwd` at all, and neither `status` nor `ui dump`
/// mentioned a project. So a lane opening in the WRONG project was invisible to a check and
/// invisible to the operator alike — which is the whole reason docs/LOCATIONS-PLAN.md
/// Phase 1 exists and blocks Phases 2 to 5.
///
/// Both members here are PURE (no I/O), on purpose, so they live on the ~1 second `unit`
/// loop beside <see cref="Daemon.IsObviousGeneric"/> and <see cref="Daemon.LanePrefix"/>.
/// That is not tidiness: <see cref="Field"/> encodes the one-project rule, and a rule about
/// what a one-project workspace must NOT start printing has to be cheap to re-check on
/// every edit, or it gets discovered by an operator instead.
///
/// It compiles into the UI as a linked source file (the same treatment as
/// <see cref="Instance"/> and <see cref="Repos"/>, and for the same reason, §13): the window
/// must reach the SAME answer the daemon does. Two implementations of "which project is this
/// lane in" is two answers to the question this phase exists to make answerable.
/// </summary>
static class Projects
{
    /// <summary>
    /// Which project a path is in, or null when no project owns it. Returns the project
    /// string as it was passed in, so display keeps the registry's casing.
    ///
    /// Longest ancestor wins — the same rule <see cref="Registry.Owner"/> uses, and for the
    /// same reason: a repo attached inside an also-attached folder must resolve to the repo
    /// and not to its container. Ancestor matching is not an edge case here, it is the
    /// ORDINARY answer: a ticket lane lives at `&lt;project&gt;\.dodona\wt\tN`
    /// (<see cref="Paths.Worktrees"/>), so every ticket lane in the product resolves by
    /// ancestor rather than by equality.
    ///
    /// Case is folded because paths come from live disk casing while string `==` and SQLite
    /// `=` are both binary-collated — the same drift docs/LOCATIONS-PLAN.md Phase 0 records
    /// for repo names, and a recorded `lanes.cwd` is exposed to exactly it.
    ///
    /// **No canonicalization**, deliberately: <see cref="Instance.Canonical"/> opens a file
    /// handle, and this has to stay pure. Both sides already come out of the registry or out
    /// of a path built from it, so they are canonical before they get here. A caller holding
    /// a path from somewhere else must canonicalize it itself.
    ///
    /// An empty path answers null rather than matching the first project: "this lane has no
    /// recorded directory" and "this lane is in the first project" are different facts, and
    /// collapsing them is how `_primary` stayed invisible for as long as it did.
    /// </summary>
    public static string? Of(IEnumerable<string> projects, string? anyPath)
    {
        if (string.IsNullOrWhiteSpace(anyPath)) return null;
        var key = anyPath!.TrimEnd('\\', '/').ToLowerInvariant();
        string? best = null;
        var bestLen = -1;
        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var pk = p.TrimEnd('\\', '/').ToLowerInvariant();
            if (!(key == pk || key.StartsWith(pk + "\\", StringComparison.Ordinal))) continue;
            if (pk.Length <= bestLen) continue;
            bestLen = pk.Length;
            best = p;
        }
        return best;
    }

    /// <summary>
    /// What `status` and a pane actually SAY about a lane's project — or null for "say
    /// nothing", which is a real answer and the one that keeps the one-project case
    /// byte-for-byte identical.
    ///
    /// Three values, and the third is the point of the whole phase:
    ///   * the project path        — this lane is in that project
    ///   * `neutral`               — the lane is in <see cref="Paths.NeutralDir"/>, which is
    ///                               where management roles BELONG (a router or brain started
    ///                               inside a project loads that project's CLAUDE.md and
    ///                               skills, i.e. a classifier that can run /ship — commit
    ///                               19dad3d, and LOCATIONS-PLAN T3/P5.8 keep it that way)
    ///   * `none (cwd=…)`          — no project owns this folder and it is not neutral. This
    ///                               is Phase 2's T4 made visible: `workspace-detach` and
    ///                               `-move` touch no lane row, so a lane's recorded cwd can
    ///                               outlive its project while the FOLDER still exists — it
    ///                               just belongs to another workspace now, and respawn's
    ///                               only test is `Directory.Exists`.
    ///
    /// AND THEN IT COMPARES WHAT THE LANE IS AGAINST WHAT A LANE OF THIS ROLE SHOULD BE, and
    /// prints only on disagreement. That is what makes the field free:
    ///
    ///   * a WORK lane in a workspace with ONE project, sitting in it → nothing to say. There
    ///     is exactly one answer, so the field would be noise, and a one-project workspace
    ///     must not start reporting a new field at all: that byte-for-byte property is what
    ///     the entire workspace migration rested on and is the thing this plan is most likely
    ///     to break (LOCATIONS-PLAN, "Order").
    ///   * a MANAGEMENT lane (any role but `work`) in the neutral directory → nothing to say.
    ///     It is where it is supposed to be. Conversely a brain that somehow ended up INSIDE
    ///     a project prints its project, in a one-project workspace too — the omission rule
    ///     is per role, so it cannot hide the T3 defect it would otherwise mask.
    ///
    /// The role string is the lane's `role` column: `work`, `router`, `brain`, `compressor`,
    /// `dispatcher`.
    /// </summary>
    public static string? Field(string? role, string? cwd, IReadOnlyList<string> projects, string neutralDir)
    {
        // NO RECORDED DIRECTORY MEANS NO PROCESS, WHICH MEANS NOTHING TO SAY -- not `none`.
        // Found by m3 going red on real output: the `DODONA` dispatcher lane is a UI row and
        // nothing else (GLOSSARY: "`dispatcher` also names a lane role that is only a UI row"),
        // so it has no cwd and was being reported as `none (cwd=-)` in a one-project workspace.
        // That is the field answering a question nobody asked, which is the exact noise the
        // omission rules below exist to avoid.
        //
        // It hides nothing: AttachShimAsync writes `lanes.cwd` BEFORE Process.Start, so even a
        // spawn that threw has a recorded directory. An empty one can therefore only mean the
        // row never went through a spawn at all -- a dispatcher row, or a lane older than the
        // schema 8 column.
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var isNeutral = Of(new[] { neutralDir }, cwd) is not null;
        var proj = Of(projects, cwd);

        var value = isNeutral ? "neutral"
                  : proj ?? $"none (cwd={(cwd is { Length: > 0 } ? cwd : "-")})";

        var expected = role == "work"
            ? (projects.Count == 1 && proj is not null ? proj : null)
            : (isNeutral ? "neutral" : null);

        return value == expected ? null : value;
    }

    /// <summary>
    /// May a lane legitimately run here? True for a folder a project owns, and true for the
    /// neutral directory (where management roles BELONG, T3/P5.8). False is the T4 state:
    /// `workspace-detach` and `workspace-move` touch no lane row, so a recorded `lanes.cwd`
    /// can outlive its project while the FOLDER still exists — it belongs to another
    /// workspace now, and respawn's only test was <c>Directory.Exists</c>, which passes.
    ///
    /// Pure, like everything else here, so the rule that decides whether an agent may be
    /// started in a folder is re-checkable in a second rather than eight seconds of daemon
    /// away. The empty case answers TRUE deliberately: "this lane never recorded a directory"
    /// is a lane older than schema 8 or a dispatcher row, and <see cref="Daemon.ResolveLaneCwd"/>
    /// falls back to the first project for exactly those — refusing them would strand a row
    /// that has done nothing wrong.
    /// </summary>
    public static bool IsOwned(IEnumerable<string> projects, string? cwd, string neutralDir) =>
        string.IsNullOrWhiteSpace(cwd)
        || Of(new[] { neutralDir }, cwd) is not null
        || Of(projects, cwd) is not null;

    // ------------------------------------------- rung 2's evidence, and D-L6 made unnarrowable

    /// <summary>
    /// THE DISTINCT PROJECTS THAT HOLD A LIVE LANE — the evidence rung 2 of the project ladder
    /// decides on (docs/LOCATIONS-PLAN.md Phase 3), as a pure function over three liveness
    /// answers rather than one read.
    ///
    /// **This is pure for one specific reason, and it is not tidiness.** Phase 3's named trap
    /// (D-L6) is that "is something already live in this project?" must never be one
    /// instantaneous pipe read: a shim's pipe name BLINKS OUT of the namespace while its serve
    /// loop disposes one `NamedPipeServerStream` and constructs the next — measured on this
    /// machine, 8 reads out of 192 over 1.5 s saw no pipe while the shim was demonstrably alive
    /// and instantly connectable — and the gap is *synchronised*: every shim in a workspace
    /// disconnects the instant its daemon exits, and the next daemon's reconcile runs
    /// milliseconds later. A single read there once declared four to seven live lanes dead per
    /// restart.
    ///
    /// A suite cannot construct that window: it would have to hold the OS pipe namespace still.
    /// So the part that CAN be got wrong — *which answers count* — is lifted out of the daemon
    /// and onto the ~1 second `unit` loop, where narrowing it back to one source is a red check
    /// rather than a plausible-looking simplification. The three answers, and a lane counts if
    /// ANY of them says yes:
    ///
    ///   * <paramref name="byPipe"/>        — the pipe namespace. Cannot go stale; the only
    ///                                        answer that sees a lane whose `shim-lane*.json`
    ///                                        was never written or has been reaped. It blinks.
    ///   * <paramref name="byRecord"/>      — a recorded shim pid that is a live `DodonaShim`.
    ///                                        Does not blink; blind to a recordless orphan.
    ///   * <paramref name="byConnection"/>  — this daemon is holding an open handle to that pipe
    ///                                        right now, which is proof no read can contradict.
    ///
    /// Every consequence of a false negative here is worse than a stale rung: it demotes a free,
    /// correct rung 2 into a question the operator did not need to be asked — or, with two
    /// projects live and one wrongly dropped, into the WRONG project answered confidently.
    ///
    /// Work lanes only, and only projects this workspace still owns: a lane whose recorded
    /// folder outlived its project (Phase 2 trap T4) must not drag a new lane into a folder that
    /// now belongs to somebody else.
    /// </summary>
    public static List<string> Live(
        IReadOnlyList<string> projects,
        IEnumerable<(long Id, string Role, string State, string Cwd)> lanes,
        IEnumerable<long> byPipe,
        IEnumerable<long> byRecord,
        IEnumerable<long> byConnection)
    {
        var live = new HashSet<long>(byPipe);
        foreach (var id in byRecord) live.Add(id);
        foreach (var id in byConnection) live.Add(id);

        var result = new List<string>();
        foreach (var l in lanes)
        {
            if (l.Role != "work" || l.State != "alive") continue;
            if (!live.Contains(l.Id)) continue;
            if (Of(projects, l.Cwd) is string p &&
                !result.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase))) result.Add(p);
        }
        return result;
    }

    // -------------------------------------------------- P5.6: which project a MANAGER is for

    /// <summary>
    /// The roles whose project is a SCOPE rather than a location — a manager is a per-project
    /// scope and not a process (docs/GLOSSARY.md, decision D-L1), so these lanes are scoped to
    /// a project while running in the neutral directory (P5.8). `dispatcher` is not here: it is
    /// only a UI row and has no process at all.
    /// </summary>
    public static bool IsManagementRole(string? role) =>
        role is "brain" or "brain-hi" or "router" or "compressor";

    /// <summary>
    /// What `status` SAYS about the project a management lane is scoped to — or null for "say
    /// nothing", which is what keeps the one-project case byte-for-byte identical.
    ///
    /// This is the second half of <see cref="Field"/> and it exists because the first half
    /// cannot answer the question. `Field` reads `lanes.cwd`, and a brain's cwd is the neutral
    /// directory on purpose, so `Field` correctly says nothing about it — which left "which
    /// project is this brain for" with no surface anywhere a person looks. With one brain per
    /// workspace that was fine; with one per project it is the whole fact.
    ///
    /// Three answers:
    ///   * null              — nothing to say: this is not a management lane, or the workspace
    ///                         has one project (exactly one answer, so the field is noise and a
    ///                         one-project workspace must not start printing a new one), or the
    ///                         lane has no registration at all (a row that never spawned).
    ///   * the project path  — this manager is for that project.
    ///   * `gone (…)`        — the lane is registered to a project this workspace no longer has.
    ///                         That is P5.5's state made visible rather than merely acted on:
    ///                         a brain whose project was detached is reaped, and a person
    ///                         reading `status` between the detach and the reap should see why.
    ///
    /// Pure, no I/O, so the rule about what a one-project workspace must NOT print is
    /// re-checkable on the ~1 second `unit` loop rather than discovered by an operator.
    /// </summary>
    public static string? ScopeField(string? role, string? project, IReadOnlyList<string> projects)
    {
        if (!IsManagementRole(role)) return null;
        if (string.IsNullOrWhiteSpace(project)) return null;
        // ONE PROJECT MEANS ONE ANSWER. The operator's own machine is a one-project workspace
        // and every phase of docs/LOCATIONS-PLAN.md leaves that case unchanged; printing a
        // field whose value is never in doubt is the noise the omission rules exist to avoid.
        if (projects.Count <= 1) return null;
        return Of(projects, project) is not null ? project : $"gone ({project})";
    }

    // ------------------------------------------------------------------ T1: one folder, said once

    /// <summary>The lead-in of the sentence a plain lane's system prompt uses to name its
    /// folder. Public so <see cref="Daemon.LaneSystemPrompt"/> WRITES the sentence with it and
    /// <see cref="Named"/> READS it back with it — one definition, so the two cannot drift.</summary>
    public const string DirLead = "Your working directory is ";
    /// <summary>The tail. `—` is an em dash written as an escape on purpose: this file is
    /// BOM-less, and a literal keeps working here but invites the same literal into a `.ps1`,
    /// where a BOM-less non-ASCII byte is read as ANSI and matches nothing (CLAUDE.md §0.2).</summary>
    public const string DirTail = " \u2014 work there.";

    /// <summary>The sentence, built once.</summary>
    public static string DirSentence(string workDir) => DirLead + workDir + DirTail;

    /// <summary>The folder a system prompt NAMES, or null when it names none (a ticket prompt
    /// says "your worktree is the current working directory" and names nothing, which is the
    /// honest answer for it).</summary>
    public static string? Named(string? systemPrompt)
    {
        if (systemPrompt is null) return null;
        var i = systemPrompt.IndexOf(DirLead, StringComparison.Ordinal);
        if (i < 0) return null;
        var from = i + DirLead.Length;
        var j = systemPrompt.IndexOf(DirTail, from, StringComparison.Ordinal);
        return j < 0 ? null : systemPrompt[from..j];
    }

    /// <summary>The value of a flag in a spawn's argv, or null. Used to read back what was
    /// really passed rather than what the code that built it believed it passed.</summary>
    public static string? ArgValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal)) return args[i + 1];
        return null;
    }

    /// <summary>The `--resume` pair for a session, or nothing.
    ///
    /// ONE SPELLING, because there are now two callers and they must not disagree: `lane-respawn`
    /// and layer 2's promotion (docs/WORK-ISOLATION-PLAN.md P2). Promotion is only cheap BECAUSE
    /// the session survives -- `--resume` rebuilds the context the agent already has, so moving a
    /// lane into its own checkout costs nothing the operator can perceive. A promotion that lost
    /// the conversation would be worse than the refusal it replaces.
    ///
    /// A `fake-` session is the acceptance suites' stand-in agent (section 17) and is not resumable
    /// by claude, so it is excluded -- and that exclusion is the whole reason this is a function
    /// rather than two inline conditions: it is a rule about which sessions are real.</summary>
    public static List<string> ResumeArgs(string? session) =>
        session is { Length: > 0 } s && !s.StartsWith("fake-", StringComparison.Ordinal)
            ? new List<string> { "--resume", s }
            : new List<string>();

    /// <summary>
    /// TRAP T1, ENFORCED (docs/LOCATIONS-PLAN.md Phase 2): the prompt must name the folder the
    /// process is actually started in. Returns null when they agree, or the refusal to print.
    ///
    /// This is the most dangerous item in the phase and it is not hypothetical — it has already
    /// happened once (M5.1: `lane-respawn` set the worktree as the working directory and rebuilt
    /// the PLAIN-lane prompt, so a resumed ticket agent was told the live tree and ran in the
    /// worktree). Two literals eleven hundred lines apart, and **it compiles clean**: an agent
    /// told a folder it is not in will `cd` there, or reason about the wrong `CLAUDE.md`, or
    /// write a report naming a tree it never touched. Nothing in a type system catches that,
    /// and no acceptance suite could see it either — the prompt goes into an argv nobody reads
    /// back.
    ///
    /// So the check happens where BOTH facts are finally in one place: at the spawn, over the
    /// real argv and the real working directory. Paths are compared with trailing separators
    /// and case folded, because one side comes from the registry and the other from a
    /// `ProcessStartInfo` (CLAUDE.md §0.2 — SQLite `=` and string `==` are both binary-collated
    /// while live disk casing is not).
    /// </summary>
    public static string? PromptDirMismatch(IReadOnlyList<string> args, string workDir)
    {
        var named = Named(ArgValue(args, "--append-system-prompt"));
        if (named is null) return null;
        static string Fold(string p) => p.TrimEnd('\\', '/').ToLowerInvariant();
        return Fold(named) == Fold(workDir) ? null
            : $"the system prompt names '{named}' but the process would start in '{workDir}'";
    }
}
