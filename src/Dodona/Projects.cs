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
}
