using System.Text.Json;

namespace Dodona;

/// <summary>
/// The asking component's PURE half (docs/LOCATIONS-PLAN.md Phase 4, decision D-L4).
///
/// **A question is a row, and asking is rendering that row.** The UI does not get to invent a
/// dialog — deciding where work goes is the system's job, not a form for the operator to fill
/// in (the comment at MainWindow.SubmitInput, and CLAUDE.md §3.1's "no folder UI, ever").
///
/// **One SHAPE, two stores — and P4.1's "one source" wording is what had to give.** There are
/// two authorities that can need an answer, and they must stay two: <see cref="ConciergeStore"/>
/// asks *which workspace* (group scope), <see cref="Store"/> asks about the work inside one
/// workspace (project scope). Merging them was rejected twice over: a workspace daemon may never
/// read the concierge's store (ConciergeStore's class note — that is what stops the concierge
/// becoming the one queue §12 designed out, and every suite runs daemons with no concierge at
/// all), and the concierge's `questions` has no column for scope, so distinguishing the two
/// there would be a row-shape change to a machine-wide table (D-L11).
///
/// So the two tables carry the SAME seven columns, and SCOPE IS WHICH STORE THE ROW IS IN. That
/// is what keeps D-L4's load-bearing half intact: one component renders both and **one answer
/// verb answers both**, differing only in which control pipe it is addressed to — which the
/// window already does routinely for every other write (§6).
///
/// What lives here is only what is a FUNCTION: turning a `candidates` JSON blob into the
/// choices a person picks from, and matching what they picked back to a choice. No I/O, so it
/// sits on the 1-second `unit` loop (CLAUDE.md §1) rather than behind a window.
///
/// The `candidates` column is `[{id,name,why}]` — written by `Concierge.Ask` before this file
/// existed, and now also by the daemon's repo question. `id` is the value the answer verb
/// takes, `name` is what the operator reads, `why` is the optional one-line reason. Parsing is
/// TOTAL: a malformed blob yields an empty list rather than throwing, because the overlay
/// failing must never take the window with it (the same rule a corrupt `ui.json` follows —
/// the box you would use to complain is inside the window).
/// </summary>
public static class Ask
{
    /// <summary>One thing the operator can pick. <paramref name="Value"/> is what the answer
    /// verb accepts; <paramref name="Label"/> is what a button says.</summary>
    public record Choice(string Value, string Label, string? Why);

    /// <summary>Parse a `questions.candidates` blob. Never throws, and never returns null:
    /// an unreadable blob is an ask with no buttons, which still renders its question text and
    /// still says how to answer it in words.</summary>
    public static List<Choice> Choices(string? candidatesJson)
    {
        var list = new List<Choice>();
        if (string.IsNullOrWhiteSpace(candidatesJson)) return list;
        try
        {
            using var d = JsonDocument.Parse(candidatesJson);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in d.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = Str(el, "id");
                var name = Str(el, "name");
                // Either half alone is enough to render something a person can act on: a
                // candidate with a name and no id is answerable BY that name (the concierge
                // has always accepted `dodona concierge-answer <id> <name>`), and one with an
                // id and no name shows its id.
                if (id is null && name is null) continue;
                list.Add(new Choice(id ?? name!, name ?? id!, Str(el, "why")));
            }
        }
        catch { /* malformed: no buttons, and the question still renders (see the class note) */ }
        return list;

        static string? Str(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s
                ? s : null;
    }

    /// <summary>
    /// What the operator picked, resolved against the choices. Accepts the value OR the label,
    /// case-insensitively, because a check (and a person) naming "lighthouse" should not have
    /// to know whether that is the id or the display name — the concierge's own answer command
    /// has always taken either.
    ///
    /// Returns null when nothing matches, and the caller must then REFUSE rather than guess:
    /// answering the wrong question is the one thing about asking that cannot be undone.
    /// Free-form answers the choices do not cover (`new:NAME`, which `Concierge.Answer` handles)
    /// are deliberately NOT matched here — see <see cref="IsFreeForm"/>.
    /// </summary>
    public static Choice? Match(List<Choice> choices, string? picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return null;
        var p = picked.Trim();
        return choices.FirstOrDefault(c => c.Value.Equals(p, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => c.Label.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An answer the choice list cannot contain and the daemon must still be allowed
    /// to see. `new:NAME` is the concierge's "none of these, make one" — a candidate list can
    /// never enumerate it, so a strict match would make the overlay strictly less capable than
    /// the command line it replaces, which is exactly the kind of divergence D-L4 forbids.</summary>
    public static bool IsFreeForm(string? picked) =>
        picked is not null && picked.TrimStart().StartsWith("new:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The candidates for the repo question (LOCATIONS-PLAN P4.5). Built here rather
    /// than inline in the daemon so the shape the UI parses and the shape the daemon writes are
    /// the same literal, and so `unit` can hold them to it.</summary>
    public static string RepoInitCandidates(string projectLabel) => JsonSerializer.Serialize(new[]
    {
        new { id = "yes", name = $"create a git repo in {projectLabel}", why = "git init, then commit what is already there" },
        new { id = "no",  name = "not now", why = "lanes keep working without git; only tickets need a repo" },
    });

    /// <summary>
    /// The candidates for the ROUTE question — "which project is this sentence for?"
    /// (LOCATIONS-PLAN P3.A). Takes project NAMES, not paths, and that is the whole shape of it:
    ///
    /// * **No paths, ever.** CLAUDE.md §3.1 has no folder UI, and a routing question names
    ///   projects rather than offering somewhere to navigate. `ui-use`'s
    ///   `the_ask_offers_no_filesystem_navigation` asserts that no choice VALUE carries a
    ///   separator or a drive letter, and this must keep satisfying it.
    /// * **Names, so the daemon does the resolving.** The answer comes back as a name and the
    ///   daemon turns it into a project with <c>ProjectLadder.ByName</c> over the projects it
    ///   still has — the same closed-list match <c>ClassifyProjectAsync</c> already makes on the
    ///   cheap tier's answer, so a name that no longer belongs to a project is a refusal rather
    ///   than a folder somebody guessed at. It also means this file needs nothing from
    ///   `ProjectLadder`, which is NOT linked into DodonaUi — the window parses this blob.
    /// * **Two projects whose folders share a leaf name are ambiguous here**, exactly as they
    ///   already are for the classifier rung, which offers leaves and matches leaves back. Same
    ///   limitation, one place, and the fix (if it is ever wanted) belongs in `ProjectLadder`
    ///   where both rungs would get it.
    ///
    /// <paramref name="names"/> arrives in recency order (`Daemon.ProjectsByRecency`), so the
    /// first one says why it is first: an ordering the operator cannot see is an ordering they
    /// have to guess at.
    /// </summary>
    public static string RouteCandidates(IEnumerable<string> names) => JsonSerializer.Serialize(
        names.Select((n, i) => new
        {
            id = n,
            name = n,
            why = i == 0 ? "most recently worked in" : null,
        }).ToArray());

    /// <summary>The `kind` a repo question carries. The UI never reads `kind` — it renders the
    /// seven shared columns and nothing else — but the daemon needs it to know what answering
    /// MEANS, and a string constant is how the two sides agree without either importing the
    /// other's file twice.</summary>
    public const string KindRepoInit = "repo-init";

    /// <summary>The `kind` the router's rung 4 carries (LOCATIONS-PLAN P3.A). Answering one
    /// DELIVERS the held sentence — `questions.subject` holds it whole for exactly that — into a
    /// new lane in the project the operator named. Phase 4 deliberately did not add this
    /// constant: an unused `kind` with no `case` behind it reads as support that is not there,
    /// and for two days rung 4 asked a question nothing could render.</summary>
    public const string KindRoute = "route";

    /// <summary>
    /// The `kind` the APPROVAL ask carries (`docs/REVIEW-AND-MERGE-PLAN.md` R6, D-R11).
    /// `subject` is the ticket id as text, and answering `yes` is the operator's approval —
    /// the one legitimate path to `Store.TicketApprove` besides typing `dodona approve`.
    ///
    /// **D-R10 is the reason this constant is worth a paragraph.** The manager may block and
    /// may never bless, so nothing that is not a person may answer a question of this kind:
    /// there is no timeout that answers it, no default, and no path from `ManagerReview` to
    /// `Daemon.ApproveTicket`. A model as the sole gate on the one irreversible step is *a
    /// prompt providing safety*, which `WORK-ISOLATION-PLAN` §2 forbids however the model is
    /// dressed.
    /// </summary>
    public const string KindLand = "land";

    /// <summary>
    /// The candidates for the approval ask. Two answers, and the VALUES are `yes`/`no` for a
    /// reason beyond brevity: `Daemon.AnswerQuestion` reads a literal `no` as a DECLINATION and
    /// records the row `withdrawn` rather than `answered`, which is the difference between "the
    /// operator said not yet" and "the operator said land it" when somebody reads the row back.
    ///
    /// **No path, no folder, no drive letter** (CLAUDE.md §3.1, and `ui-use`'s
    /// `the_ask_offers_no_filesystem_navigation` asserts it on the choice values): this is a
    /// question about a TICKET, which the system already knows by number, not about a place.
    ///
    /// **"not yet" is a real answer, not a dismissal.** Escape puts the overlay down and leaves
    /// the row open; `no` closes the row and changes nothing else, and the next completed turn
    /// that moves the worktree opens a fresh one. Neither can lose the ticket, which is what
    /// makes declining safe to offer at all.
    /// </summary>
    public static string LandCandidates(long ticket) => JsonSerializer.Serialize(new[]
    {
        new { id = "yes", name = $"approve the merge of ticket {ticket}",
              why = "the agent can then take the merge token and fast-forward main" },
        new { id = "no",  name = "not yet",
              why = "the ticket stays open, the agent keeps working, and you are asked again when the work changes" },
    });
}
