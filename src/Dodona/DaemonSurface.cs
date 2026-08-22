namespace Dodona;

/// <summary>
/// WHICH COMMANDS ARE WORTH STARTING A DAEMON FOR -- declared once, per WIRE COMMAND, for both
/// dispatchers. Issue #13.
///
/// The rule this table applies, and the only one: <b>a command that only REPORTS never starts
/// anything; a command that CHANGES or DELIVERS something summons.</b> Not "does the handler
/// write" -- `repos` and `token-status` materialise a merge_token row and are still readings by
/// name, and `ack` flips one bit and is still an act.
///
/// WHY A TABLE AND NOT A LONGER `or` CHAIN. This was four literals in `Client`:
/// <c>cmd is "stop-daemon" or "status" or "land-status" or "ticket-record"</c> -- right about
/// those four since the day it was written and silent about the other sixty. Summoning a
/// workspace daemon runs its warm-up: the router, the brain and the compressor pool, FOUR real
/// `claude -p --model haiku` processes in this repo. So `dodona policy`, whose own handler
/// comment says "Inspectable without spawning anything", spawned four model agents to print a
/// static table. On 2026-08-19 a session ran a health check twice against a machine the operator
/// believed was idle, left a daemon and five model lanes, and then spent two hours diagnosing
/// its own leftovers as machine contention (CLAUDE.md 3.2, which is that incident).
///
/// A longer hand-maintained list is the same bug with more entries, so completeness is ENFORCED
/// rather than remembered: `dev lint` reads the `case` labels out of `Daemon.Commands.cs` and
/// `Concierge.cs` and fails if either dispatcher knows a command this table does not, or this
/// table names one no dispatcher has. A new command cannot be added without answering the
/// question -- which is the property the literal never had.
///
/// KEYED ON THE WIRE COMMAND, NOT THE CLI VERB, and that is the second half of the fix. The old
/// test read the outer `cmd` variable -- the word the operator typed -- while the daemon acts on
/// the `cmd` field of the request. They differ: `dodona land`'s poll sends `land-status` (which
/// needed a `neverSummon` parameter threaded through `Client` to compensate, now deleted),
/// `dodona lane-expand` sends `lane-collapse`, and `dodona publish` sends `swap`. That last one
/// is why a bare `publish` against a sleeping workspace woke a daemon ON THE OLD BUILD purely to
/// have something to hand a swap to.
///
/// FAILS CLOSED. An undeclared command starts nothing and says so -- the safe direction, because
/// the cost of not starting is one refusal a person can read, and the cost of starting is four
/// model processes nobody asked for. `Declared` is pure, so `unit` can ask it every question
/// without a daemon, a pipe or a store.
/// </summary>
static class DaemonSurface
{
    internal enum Surface { Workspace, Concierge }

    internal enum Summon
    {
        /// <summary>Reports only. If nothing is running, say so -- do not start one to answer.</summary>
        Never,
        /// <summary>Changes or delivers something. Bringing the daemon back is what the caller wants.</summary>
        Always,
    }

    // Read a row as a sentence: `["policy"] = Ask` -- policy only asks a question.
    const Summon Ask = Summon.Never;
    const Summon Act = Summon.Always;

    /// <summary>Every `case` label in <c>Daemon.HandleAsync</c>. Enforced by `dev lint`.</summary>
    static readonly Dictionary<string, Summon> Ws = new(StringComparer.Ordinal)
    {
        // ---- readings. Every one of these had "starts two to four model helpers to answer a
        // question" as its behaviour until issue #13; the six worst are named in that ticket.
        ["status"] = Ask,          // reports ASLEEP rather than waking the workspace (the 2026-08-19 incident)
        ["policy"] = Ask,          // a static config table. The clearest instance in the ticket
        ["swaps"] = Ask,           // pure read; never on the old list, and nobody noticed
        ["repo-status"] = Ask,     // git rev-parse plus a directory walk
        ["claim-check"] = Ask,     // pure read
        ["questions"] = Ask,       // pure read
        ["tickets"] = Ask,         // pure read; CLAUDE.md 3.2 listed it as deliberately summoning
        ["repos"] = Ask,           // a reading BY NAME. It materialises a merge_token row on the way --
        ["token-status"] = Ask,    // ...as does this one. A write, but only ever while a daemon is already up
        ["land-status"] = Ask,     // a POLL. `LandCli` needed neverSummon:true for it; the wire cmd carries it now
        ["ticket-record"] = Ask,   // what a manager (R5) and any script polls, repeatedly
        ["tail"] = Ask,            // 20 pane rows out of SQLite. CLAUDE.md 3.2 justified summoning here with
                                   // "the shims have been buffering" -- true of `say`/`input`, where the caller
                                   // is DELIVERING. Reading is reading: a sleeping workspace answers ASLEEP,
                                   // and the buffer lands the moment anything wakes it.

        // ---- stopping and swapping. There is nothing to stop or swap if nothing is running, so
        // summoning first would be starting a process in order to talk to it about ending.
        ["stop-daemon"] = Ask,
        ["swap"] = Ask,            // `publish`'s wire cmd -- waking a daemon on the OLD build to hand it a new one
        ["swap-answer"] = Ask,     // answers a question only a live daemon can have asked

        // ---- the write gate's tree question, from a PreToolUse hook on EVERY agent write.
        // `GateAsk` also sets DODONA_NO_AUTOSTART=1: two independent guards on the one path where
        // summoning would cost four model processes per edit, and it fails closed regardless.
        ["tree-check"] = Ask,

        // ---- sent only to a daemon already known live (`TellIfLive`, `Daemon.Swap`), so these
        // never reach the summon branch at all. Declared anyway because the enforcement IS
        // completeness: an unreachable row is cheap, and an omission is how this ticket happened.
        ["project-gone"] = Ask,
        ["workspace-forgotten"] = Ask,
        ["swap-fire"] = Ask,

        // ---- acts. Bringing the daemon back is what the caller wants, and for the delivery
        // verbs the shims have been buffering the whole time (CLAUDE.md 3.2).
        ["say"] = Act,
        ["input"] = Act,
        ["lane-start"] = Act,
        ["lane-stop"] = Act,
        ["lane-respawn"] = Act,
        ["lane-rename"] = Act,
        ["lane-collapse"] = Act,   // `dodona lane-expand` sends this too
        ["focus"] = Act,
        ["router-start"] = Act,
        ["brain-start"] = Act,
        ["compressor-start"] = Act,
        ["ticket-agent"] = Act,
        ["ticket-create"] = Act,
        ["claim-extend"] = Act,
        ["approve"] = Act,
        ["ack"] = Act,             // one UPDATE, and the ticket is right that four helpers to flip a bit is
                                   // absurd -- but refusing a WRITE on a sleeping workspace breaks a working
                                   // command, and the line this table draws is report/act, not cheap/expensive.
        ["undo-route"] = Act,
        ["answer"] = Act,
        ["token-request"] = Act,
        ["token-renew"] = Act,
        ["token-release"] = Act,
        ["land"] = Act,
        ["repo-init"] = Act,
    };

    /// <summary>Every `case` label in <c>Concierge.HandleAsync</c>. Enforced by `dev lint`.
    /// Starting a concierge costs one process and no model lanes -- lower stakes than a workspace
    /// daemon, and the same rule, because "it is only a little machine the operator did not ask
    /// for" is how the first list stayed short.</summary>
    static readonly Dictionary<string, Summon> Cx = new(StringComparer.Ordinal)
    {
        ["status"] = Ask,
        ["feed"] = Ask,
        ["questions"] = Ask,
        ["resolve"] = Ask,         // DEBUGGING.md sells it as "walk the ladder and print the verdict as JSON"
        ["stop"] = Ask,            // the one exemption the old code had
        ["swap"] = Ask,            // nothing to swap if nothing is running
        ["route"] = Act,           // resolves, WAKES the workspace and delivers the sentence -- the whole point
        ["answer"] = Act,
        ["ack"] = Act,
        ["focus"] = Act,
        ["review"] = Act,
    };

    /// <summary>What this surface declares about <paramref name="wire"/>, or null if it declares
    /// nothing -- which the caller must treat as "start nothing" and SAY so. Pure: no store, no
    /// pipe, no process.</summary>
    internal static Summon? Declared(Surface surface, string? wire) =>
        wire is not null && (surface == Surface.Workspace ? Ws : Cx).TryGetValue(wire, out var s) ? s : null;

    /// <summary>The line to print when nothing declared this command. Names the two files a person
    /// has to reconcile, because an enforcement that leaves you with no next step is a new way to
    /// be stuck (CLAUDE.md 0.1).</summary>
    internal static string Undeclared(Surface surface, string? wire) =>
        $"dodona: \"{wire}\" is not declared in DaemonSurface.{(surface == Surface.Workspace ? "Ws" : "Cx")} -- " +
        "starting nothing, because an undeclared command fails closed. Declare it beside its `case` label in " +
        $"{(surface == Surface.Workspace ? "Daemon.Commands.cs" : "Concierge.cs")}; `dev lint` asserts the two agree.";

    /// <summary>The command names a surface declares -- for the checks that read the table rather
    /// than start a process.</summary>
    internal static IReadOnlyCollection<string> Names(Surface surface) =>
        (surface == Surface.Workspace ? Ws : Cx).Keys;
}
