using System.IO.Pipes;
using System.Text.Json;

namespace Dodona;

/// <summary>
/// THE WRITE GATE'S DECISION, lifted out of <c>Program.cs</c>'s top-level statements and
/// nothing else (docs/testarch/seams.md S11).
///
/// <c>GateHook</c> was a 200-line local function inside <c>Program.&lt;Main&gt;$</c>: it read
/// <c>Console.In</c>, read its own argv through the <c>opts</c> closure, asked the daemon
/// inline, printed the verdict and wrote the trace. Nothing outside that method could reach
/// it, so every question about what the gate DECIDES had to be asked by starting a real
/// subprocess, a real daemon and a real pipe -- while the decision itself is pure over
/// (laneArg, ticketArg, stdin, the tree answer). No behaviour changed when it was cut, and
/// <c>m1</c> staying green is what says so.
///
/// The three things that must stay real are still real, and two of them are ARGUMENTS rather
/// than doubles -- the <c>Trees.Locate</c> shape (Trees.cs:44 + the :77 overload binding the
/// real predicates), where production has exactly one path:
///
///   * <b>stdin</b> arrives as a <c>Func&lt;string&gt;</c> that this function calls INSIDE its
///     own try/catch, because "the read threw" is one of the cases being decided. Production
///     binds <c>Console.In.ReadToEnd</c>. TEST-ARCHITECTURE-PLAN 3.5 says the gate's stdin is
///     never faked, and it is not: wire B1 (<c>m2:the_lane_ends_up_in_a_worktree</c>) still
///     pipes real bytes -- including a real UTF-8 BOM -- into the real exe, which is the only
///     way the 2026-08-19 fail-open was ever reachable;
///   * <b>the tree question</b> arrives as a <c>Func</c> and production binds the real
///     <c>GateAsk</c>, so a fake that drifts cannot be reached from production;
///   * <b>the effects</b> -- the deny JSON on stdout, the fail-open trace on stderr and in the
///     bypass log -- stay in <c>Program.cs</c>, where the <c>opts</c> closure lives. This
///     function returns WHAT TO DO and performs nothing.
///
/// THE PROPERTY THIS TYPE EXISTS TO MAKE CHECKABLE, and the reason the extraction is worth a
/// seam at all: every <see cref="Verdict.Allow"/> it can return is a write it has POSITIVELY
/// placed inside a worktree, or an invocation no gate was ever deployed for. That property has
/// twice been asserted in prose and been false (CLAUDE.md 7; issue #4), and settling it the
/// last time meant a human enumerating every <c>return</c> by hand. It is now
/// <c>unit:Dodona.Tests.GateDecisionTests.every_allow_asked_the_tree_or_had_no_gate_deployed</c>,
/// which enumerates the whole input matrix mechanically and reddens if a new exit skips the
/// tree question. Assert it by enumeration or not at all.
/// </summary>
static class GateDecision
{
    /// <summary>The verdict. There are two, and there is deliberately no third that means
    /// "could not tell": that case is a <see cref="Verdict.Deny"/> with a trace, because layer
    /// 1 refusing on doubt is the whole guarantee (docs/WORK-ISOLATION-PLAN.md 3, P1).</summary>
    internal enum Verdict { Allow, Deny }

    /// <summary>
    /// What the gate decided and the effects that go with it. Three of the fields are effects
    /// rather than the decision, and they are separate from it on purpose -- the trace and the
    /// verdict are independent, and conflating them is how a line that only WRITES a trace can
    /// read as an allow at a glance:
    ///
    ///   * <paramref name="Note"/> -- a plain stderr line that changes no verdict (the
    ///     unreadable <c>--ticket</c> case, which reports a misconfiguration and carries on);
    ///   * <paramref name="Unchecked"/> -- the <c>gate could not check</c> trace. Non-null WITH
    ///     <see cref="Verdict.Deny"/> means "recorded for the trace only; the verdict is the
    ///     refusal". Non-null with <see cref="Verdict.Allow"/> is a real unchecked allow, and
    ///     the three branches that can produce one are unreachable from this function's own
    ///     guards (see <see cref="Decide"/>);
    ///   * <paramref name="Ticket"/> / <paramref name="Path"/> -- what the trace annotates.
    /// </summary>
    internal readonly record struct Outcome(
        Verdict Verdict,
        string? DenyReason,
        string? Unchecked,
        string? Note,
        long Ticket,
        string? Path);

    /// <summary>One line, for a diagnostic that must not break the log format it lands in.</summary>
    internal static string Flatten(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// WHAT THIS HOOK IS FOR, AND WHY IT ASKS EXACTLY ONE QUESTION
    /// (docs/WORK-ISOLATION-PLAN.md section 3, P1; docs/REVIEW-AND-MERGE-PLAN.md D-R5, R3).
    ///
    ///   1. WHICH TREE is this write in? Layer 1: no agent writes into a project outside a
    ///      worktree. Unconditional, model-free, EVERY work lane -- not only ticket lanes.
    ///      Refusing is the DEFAULT, so this one fails CLOSED: an unreadable --lane,
    ///      unparseable stdin, a tool with no path, or a daemon that does not answer all DENY.
    ///
    /// There used to be a second question -- is the write inside the ticket's CLAIM -- and it
    /// is gone by decision (D-R5), not by accident. See the note where it used to be, at the
    /// bottom of this function, which carries the reasoning and the one property a future
    /// second question must not break. In short: the claim question failed OPEN and was only
    /// tolerable because this one runs first and refuses on doubt; with it gone, this hook has
    /// no fail-open path left at all.
    ///
    /// THE THING TO PRESERVE: every <see cref="Verdict.Allow"/> below must be a write this
    /// function has POSITIVELY placed inside a worktree, or a lane it was never deployed for.
    /// If you find yourself adding an allow for a case you could not determine, you are undoing
    /// layer 1, and layer 1 is what stands between an agent and the operator's live checkout.
    /// </summary>
    internal static Outcome Decide(
        string? laneArg,
        string? ticketArg,
        Func<string> readStdin,
        Func<long, string, (int Code, string Reply)> treeCheck)
    {
        _ = long.TryParse(ticketArg ?? "", out var ticket);
        _ = long.TryParse(laneArg ?? "", out var lane);

        // NO ARGUMENTS AT ALL means no gate was ever deployed for this invocation, so there is
        // nothing to check and nothing to report. This is the one allow that is genuinely not a
        // bypass -- and it is not reachable from a work lane: `DeployGate` writes `--lane` for
        // every one of them (`--ticket` is the optional half).
        //
        // ASKED OF THE ARGUMENTS, NOT OF THE PARSED NUMBERS, and that distinction was a live
        // FAIL-OPEN until R3. This read `if (lane <= 0 && ticket <= 0) return 0;` -- and an
        // unparseable `--lane` parses to 0, so `--lane not-a-number` with no `--ticket` took
        // this early return and ALLOWED THE WRITE, silently, before ever reaching the deny two
        // lines below that exists precisely for it. The deny was unreachable in the exact case
        // it was written for. Found by the check re-aimed onto this property in R3
        // (`the_gate_denies_a_lane_argument_it_cannot_read`), which is the argument for
        // re-aiming a check rather than deleting it: the old assertion was retired, and the new
        // one immediately caught something the old one could never have looked at.
        if (laneArg is not { Length: > 0 } && ticketArg is not { Length: > 0 })
            return new Outcome(Verdict.Allow, null, null, null, ticket, null);

        // A LANE ARGUMENT WE CANNOT READ IS NOT AN ALLOW. It means our own deployment wrote
        // something wrong, and under layer 1 guessing "allow" is a write into the live tree.
        if (laneArg is { Length: > 0 } && lane <= 0)
            return Refuse($"dodona gate: --lane '{laneArg}' is not a number, so the gate cannot tell which tree " +
                          "this lane owns. This is a Dodona misconfiguration, not your mistake -- report it; " +
                          "the write is refused rather than allowed unchecked.", ticket);

        // A gate deployed with a ticket but NO lane can no longer ask anything -- the claim
        // question it used to answer is gone (D-R5) and the tree question needs the lane.
        // Before R3 this combination still gated something; now it would be a hook that runs
        // and permits everything, so it refuses and says why. `DeployGate` cannot produce it,
        // which is exactly why reaching here means something is wrong rather than unusual.
        if (lane <= 0)
            return Refuse("dodona gate: deployed with --ticket but no readable --lane, so the gate cannot tell " +
                          "which tree this write is in. This is a Dodona misconfiguration, not your mistake -- " +
                          "report it; the write is refused rather than allowed unchecked.", ticket);

        // AN UNREADABLE `--ticket` IS NOT AN ALLOW EITHER, AND UNTIL 2026-08-21 IT WAS. This
        // line read `return GateAllowedUnchecked(...)`, so `--lane 5 --ticket abc` returned 0
        // with the lane perfectly readable -- the tree question below never ran and the write
        // was permitted wherever it landed. That is R3's hole one argument along: the `--lane`
        // case two blocks up was fixed and this sibling was not, because the ticket number was
        // load-bearing while the CLAIM question existed and stopped being load-bearing the
        // moment D-R5 deleted it. Nothing gates on the ticket now; it annotates a trace line
        // and nothing more.
        //
        // So this says so and CARRIES ON to the question that decides. Not a deny: the lane is
        // readable, the tree is answerable, and refusing a write we can actually adjudicate
        // would be punishing an agent for our own bad argument. Not an unchecked allow either
        // -- that writes `gate could not check` into the bypass log, and nothing is being let
        // through here.
        string? note = null;
        if (ticketArg is { Length: > 0 } && ticket <= 0)
            note = $"dodona gate: --ticket '{ticketArg}' is not a number (Dodona " +
                   "misconfiguration, not your mistake -- report it). The tree check still " +
                   "runs and still decides this write; only the trace loses the ticket number.";

        // THE THREE `lane <= 0` BRANCHES BELOW ARE UNREACHABLE FROM HERE, and they are kept
        // rather than deleted because each is a genuine unchecked allow if it ever becomes
        // reachable again -- which is exactly what a future edit to the guards above would do.
        // They are the reason this function reads as though it still has fail-opens; it does
        // not, and `every_allow_asked_the_tree_or_had_no_gate_deployed` is what says so
        // mechanically rather than by a reader's enumeration (issue #4 cost two of those).
        string input;
        try { input = readStdin(); }
        catch (Exception ex)
        {
            return lane > 0
                ? Refuse($"dodona gate: could not read the tool payload ({ex.GetType().Name}), so the gate cannot " +
                         "tell which tree this write is in. Refused rather than allowed unchecked. Retry the write.",
                         ticket, note)
                : new Outcome(Verdict.Allow, null, $"stdin unreadable: {ex.GetType().Name}", note, ticket, null);
        }

        // A BOM IS NOT CORRUPTION, AND IT WAS FAILING THIS GATE OPEN (2026-08-19).
        //
        // `Console.In` hands back a leading U+FEFF as an ordinary character, and
        // JsonDocument.Parse refuses a document that does not start with `{`. So stdin arriving
        // as EF BB BF 7B ... took the catch below, logged `JsonReaderException`, and ALLOWED
        // the write unchecked -- the claim gate, layer 1 of the safety model (section 6), not
        // enforcing at all. Measured: m1's `gate_allows_inside_claim` and
        // `gate_denies_outside_claim` were red on `main` at d43dffb, deterministically, with a
        // mojibaked BOM before `{"tool_input":` in the bypass log.
        //
        // Windows writes BOMs everywhere -- PS 5.1's `>` and `Out-File` default to UTF-8-with-
        // BOM in this environment (CLAUDE.md 0.2 has three separate incidents from that habit)
        // -- so any producer piping a file into this hook can hand us one. Stripping it is not
        // leniency about malformed input; it is reading the encoding the platform actually
        // emits, and the alternative is a gate that silently does nothing.
        input = input.TrimStart('\uFEFF').Trim();      // spelled as an escape on purpose: an
        // invisible literal here would be the same class of trap as the rest of CLAUDE.md 0.2

        // The tool name is only for the diagnostic, so it is read defensively and never gates.
        string tool = "";
        string? path = null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.TryGetProperty("tool_name", out var tn)) tool = tn.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("tool_input", out var ti))
            {
                // NOTEBOOKEDIT CARRIES `notebook_path`, NOT `file_path`, and reading only the
                // latter was a named hole in layer 1 -- an allow with a log line. It is a write
                // to a file like any other, so it is read here and gated like any other.
                if (ti.TryGetProperty("file_path", out var fp)) path = fp.GetString();
                else if (ti.TryGetProperty("notebook_path", out var np)) path = np.GetString();
            }
        }
        catch (Exception ex)
        {
            // The measured mystery lands here or in the branch below: under a heavy parallel
            // run the hook produced no verdict and left no trace, and empty-or-truncated stdin
            // is the only remaining explanation (the daemon path logs, and PowerShell was
            // proved to deliver stdin 60/60 under load). The BYTE COUNT and a prefix are
            // recorded so the next occurrence answers the question instead of raising it again.
            var head = input.Length > 120 ? input.Substring(0, 120) + "..." : input;
            var why = $"stdin unparseable as JSON ({input.Length} bytes, {ex.GetType().Name}): {Flatten(head)}";
            if (lane <= 0) return new Outcome(Verdict.Allow, null, why, note, ticket, null);
            return new Outcome(
                Verdict.Deny,
                "dodona gate: the tool payload did not parse, so the gate cannot tell which tree this " +
                "write is in. Refused rather than allowed unchecked (see .dodona-bypass.log). Retry the write.",
                why,          // for the trace only; the verdict is the refusal beside it
                note, ticket, null);
        }

        if (string.IsNullOrEmpty(path))
        {
            var why = $"no file_path or notebook_path in tool_input (tool='{tool}', {input.Length} bytes)";
            if (lane <= 0) return new Outcome(Verdict.Allow, null, why, note, ticket, null);
            return new Outcome(
                Verdict.Deny,
                $"dodona gate: '{tool}' carried no file path the gate could read, so it cannot tell which " +
                "tree the write lands in. Refused rather than allowed unchecked.",
                why,          // for the trace only; the verdict is the refusal beside it
                note, ticket, null);
        }

        // ---- question 1: the TREE. Fails CLOSED, deliberately. ----
        //
        // There is nothing behind THIS one: the shared checkout is the operator's live tree and
        // other lanes are working in it, so "we could not tell" has to mean no. A refused write
        // is visible, recoverable and retryable; an allowed one is none of those, and
        // CLAUDE.md 0.3 is largely a list of what invisible costs. A down daemon is already a
        // degraded state -- the shim is buffering this lane's output into it -- so refusing
        // there costs a message, not work.
        if (lane > 0)
        {
            var (code, reply) = treeCheck(lane, path!);
            if (code == 1)
                return Refuse(reply.Length > 0 ? reply : $"denied: {path} is in the shared checkout, not a worktree.",
                              ticket, note);
            if (code != 0)
                return new Outcome(
                    Verdict.Deny,
                    $"dodona gate: could not verify which tree {path} is in (the daemon did not answer: " +
                    $"{(reply.Length > 0 ? Flatten(reply) : "no reply")}). Refused rather than allowed " +
                    "unchecked -- a write into the shared checkout cannot be undone for the other lanes " +
                    "in it. Retry the write.",
                    $"tree-check could not answer (exit {code}): {Flatten(reply)}",
                    note, ticket, path);
        }

        // ---- there is no question 2 any more, and its absence is the decision ----
        //
        // This hook used to ask a second question: is the write inside the TICKET'S CLAIM? It
        // is gone (`REVIEW-AND-MERGE-PLAN.md` D-R5, R3), and deleting it is not a relaxation of
        // the safety model -- it is the removal of a refusal that was never protecting anything
        // by the time layer 1 existed.
        //
        // The operator's reasoning, which is the decision: *"You give the sheriff to agents
        // about to work on the same file. That's often the case, very often the case. And if
        // that is problematic in some way, it's the manager's job to say something about it."*
        // The write was always inside the agent's OWN PRIVATE CHECKOUT, where it harms nobody;
        // refusing it was refusing an agent permission to do the work it had been given. It
        // also stranded the promoted lane of layer 2 on its SECOND file, because promotion
        // seeds a claim from the one path that happened to be denied first.
        //
        // And the claim never solved the problem it was standing in for: two agents with
        // entirely disjoint claims still both failed `--ff-only` as soon as one landed, because
        // main moved under the other. What makes concurrent work land is R1's merge flow, not
        // prediction.
        //
        // WHAT REMAINS IS THE PART THAT WAS DOING THE WORK. Question 1 above -- the TREE -- is
        // layer 1, it is unconditional, it covers EVERY work lane rather than only ticket
        // lanes, and it FAILS CLOSED: unreadable arguments, unparseable stdin, a missing path,
        // a daemon that does not answer all deny.
        //
        // The ordering argument the old comment here made is spent rather than deleted, and it
        // is worth knowing why it existed: the claim question failed OPEN, so it was only
        // tolerable because the tree question ran FIRST and refused on doubt -- bounding every
        // claim fail-open to a write inside a worktree. With the claim question gone there is
        // no fail-open left in this hook at all. Every path above either allows a write it has
        // positively placed inside a worktree, or denies. That is the property to preserve if
        // anyone adds a second question here again: A NEW QUESTION MUST NOT REINTRODUCE A
        // FAIL-OPEN, because there is no longer an ordering argument to excuse one.
        //
        // AND THAT SENTENCE WAS WRITTEN BEFORE IT WAS TRUE -- wrong by exactly one line for a
        // day. The unreadable-`--ticket` branch above still returned an unchecked allow, with
        // the lane readable and the tree answerable. Fixed 2026-08-21 while settling issue #4,
        // which existed because a line in CLAUDE.md disagreed with the comment here; the
        // disagreement turned out to be the CODE's fault, not the doc's. The lesson is the one
        // CLAUDE.md 0.3 already carries: a property asserted in a comment is not enforcement.
        // It is enforced now -- `every_allow_asked_the_tree_or_had_no_gate_deployed` enumerates
        // every exit of this function, which is what the last two settlements did by hand.
        //
        // `claim-check` itself still exists as a daemon command -- it is a useful read, and
        // `workspace`'s drift check uses it -- but nothing gates on it.
        return new Outcome(Verdict.Allow, null, null, note, ticket, path);
    }

    /// <summary>A refusal with no trace beside it: the gate could not adjudicate and said so,
    /// and nothing was let through, so there is nothing for a trace to record.</summary>
    static Outcome Refuse(string reason, long ticket, string? note = null) =>
        new(Verdict.Deny, reason, null, note, ticket, null);
}
