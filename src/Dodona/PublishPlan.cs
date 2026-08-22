namespace Dodona;

/// <summary>
/// The two decisions `dodona publish` makes that are NOT about processes: <b>who gets the
/// swap</b>, and <b>what each target's reply means</b>.
///
/// Both used to be inline in one 90-line stretch of <c>Program.Publish()</c> -- the block that
/// built <c>targets</c> and the loop that called <c>Client</c> and folded verdicts, `accepted`
/// and the exit code in one pass. That is why every check about them had to start three
/// daemons, a concierge and a registry to ask a question that is pure over
/// (flags, registry rows, liveness) and over (exit code, reply lines). This type is that
/// extraction and nothing else: no behaviour changed when it was cut, and the acceptance suite
/// staying green is what says so.
///
/// The two things it does NOT do are the two things that must stay real:
///
///   * <b>liveness</b> is an injected <c>Func&lt;string,bool&gt;</c>, because the honest answer
///     is a read of the <c>\\.\pipe\</c> namespace and that namespace BLINKS (CLAUDE.md 0.2:
///     8 of 192 reads over 1.5 s saw no pipe while the shim was alive). A faked namespace has
///     no blink;
///   * <b>the registry</b> arrives as rows plus a resolver and is never reimplemented. Repo
///     exclusivity is a partial unique index, and "a HashSet is a different enforcement
///     mechanism passing a test written about the index" (TEST-ARCHITECTURE-PLAN 3.5).
///
/// That is the <c>Trees.Locate</c> shape (Trees.cs:44 + :77): injected arguments rather than
/// doubles, with production binding exactly one set of them.
/// </summary>
static class PublishPlan
{
    /// <summary>One publish target: the label printed for it, and the ctl pipe spoken to.</summary>
    internal readonly record struct Target(string Label, string Pipe);

    /// <summary>A registry row as target resolution sees it. Deliberately two fields: nothing
    /// about who gets a swap depends on members, aliases or creation time, and a resolution
    /// that COULD read them is a resolution somebody has to reason about.</summary>
    internal readonly record struct Ws(string Id, string Name);

    /// <summary>What one target's reply MEANT, and the exit code that follows from it.
    /// <see cref="Code"/> is not always the code the wire returned: silence is promoted to 1
    /// here, which is the whole of issue #9's fix.</summary>
    internal readonly record struct Verdict(string Text, int Code)
    {
        /// <summary>This target is still running whatever it was already running.</summary>
        internal bool StillOld => Code != 0;
    }

    /// <summary>
    /// WHO GETS THE SWAP (docs/WORKSPACES-CONCIERGE.md 7).
    ///
    /// <c>--all</c> used to broadcast to every ctl pipe on the machine, which made the whole
    /// thing untestable: a suite exercising it would hot-swap the operator's live instances.
    /// Targeting is resolved from the REGISTRY instead, by id, so a lingering or foreign
    /// <c>dodona-*-ctl</c> is not a target however live it is -- <paramref name="isLive"/>
    /// saying yes about an id that is in no row of <paramref name="registered"/> can never
    /// produce one. That is the safety property, and it is a property of THIS function.
    ///
    ///   --all                  every live workspace in the registry, plus the concierge
    ///   --workspace n ...      exactly these, plus the concierge with --concierge
    ///   (neither)              <paramref name="owning"/> -- the workspace that owns what we
    ///                          built, IF it is running
    ///
    /// <c>--all</c> wins over <c>--workspace</c>, as it always did.
    ///
    /// THE DEFAULT TARGET IS LIVENESS-FILTERED, and it was not until issue #13. A bare
    /// <c>dodona publish</c> against a sleeping workspace added it as a target anyway, and
    /// <c>Client</c> then SUMMONED a daemon on the OLD build purely to have something to hand a
    /// swap to -- a publish that started the very thing it was about to replace, plus its
    /// warm-up's four model processes. <c>--all</c> and <c>--workspace</c> never did (neither
    /// resolves the owning workspace, so <c>wsCache</c> stayed null and the summon branch was
    /// unreachable), which is exactly why the argument-less form -- the one a person types -- was
    /// the only one that could do it. A name typed explicitly still becomes a target whatever its
    /// state: being told that the workspace you NAMED is not running is the answer to that
    /// question. Nothing running means no targets, which publish already reports as
    /// "no daemon running to swap" and treats as accepted.
    ///
    /// <paramref name="owning"/> is a delegate and not a value because resolving it can THROW
    /// (<see cref="WorkspaceUnavailable"/>), and because it must not be resolved at all when a
    /// target was named: a publish must never migrate a store as a side effect of being run in
    /// a source tree. The throw is deliberately not caught here -- the caller's answer to it is
    /// to publish anyway and say nothing was swapped, which is a decision about the build
    /// rather than about targeting.
    /// </summary>
    /// <returns>the targets, or a refusal already spelled the way stderr wants it plus the exit
    /// code that goes with it. A refusal returns NO targets: the early return precedes the swap
    /// loop, so nothing is ever swapped before a bad name is refused.</returns>
    internal static (List<Target> Targets, string? Error, int ExitCode) Resolve(
        bool all,
        IReadOnlyList<string> named,
        bool concierge,
        IReadOnlyList<Ws> registered,
        Func<string, Ws?> byNameOrId,
        Func<string, bool> isLive,
        string conciergeId,
        Func<Ws> owning)
    {
        var targets = new List<Target>();
        if (all)
        {
            foreach (var w in registered)
                if (isLive(w.Id)) targets.Add(For(w));
            if (isLive(conciergeId)) targets.Add(Concierge(conciergeId));
        }
        else if (named.Count > 0)
        {
            foreach (var n in named)
            {
                if (byNameOrId(n) is not { } w)
                    return (new List<Target>(), $"error: no workspace \"{n}\" to publish to", 2);
                targets.Add(For(w));
            }
            if (concierge && isLive(conciergeId)) targets.Add(Concierge(conciergeId));
        }
        else
        {
            // `owning()` is still CALLED when nothing is live -- resolving it is what decides
            // whether there is a workspace here at all, and its WorkspaceUnavailable throw is the
            // caller's "published, nothing swapped" path. Only the TARGET is conditional.
            var w = owning();
            if (isLive(w.Id)) targets.Add(For(w));
        }
        return (targets, null, 0);
    }

    /// <summary>The label a workspace is printed and reported under. Name AND id, because the
    /// name is what a person typed and the id is what the pipe is called.</summary>
    internal static Target For(Ws w) => new($"{w.Name} ({w.Id})", Instance.CtlPipe(w.Id));

    /// <summary>The concierge has no workspace of its own, so it is labelled by what it is.</summary>
    internal static Target Concierge(string conciergeId) => new("concierge", Instance.CtlPipe(conciergeId));

    /// <summary>
    /// WHAT A TARGET'S REPLY MEANT -- REPORT OUTCOMES, NOT INTENTIONS (issue #9).
    ///
    /// Publish used to print `swapping &lt;label&gt;` before the call and nothing after it. That
    /// line is a statement of INTENT, and it was the only thing publish ever said about a
    /// target: a non-zero code raised the worst code without naming who failed, and ONE
    /// successful target was enough to move the desktop shortcut. So a publish in which the
    /// concierge did absolutely nothing looked entirely successful. It did nothing for two
    /// days -- measured on the operator's machine 2026-08-21 at f346b76, the workspace daemon
    /// on build 20260821-105924 and the concierge still on 20260819-212126.
    ///
    /// The third verdict is the one that matters and is the reason this is a function.
    /// <b>ANSWERED NOTHING.</b> A daemon that does not recognise a command falls out of its
    /// switch, writes no line, and <c>Client</c> reports 0 -- indistinguishable from success at
    /// the wire. Both dispatchers have a <c>default:</c> now, and this does not depend on that:
    /// it catches the next silent no-op whatever produces it, including an OLDER build on the
    /// far end that will never learn a <c>default</c>.
    ///
    /// `armed` counts as taken, exactly as it always did: a daemon read the binary, judged it
    /// and committed to it, which is what the shortcut needs to know.
    ///
    /// The em dashes are written as \\u2014 escapes so this file stays ASCII -- a non-ASCII byte
    /// in a BOM-less file is CLAUDE.md 0.2's oldest trap, and these strings must come out
    /// byte-identical to what the suite already matches on.
    /// </summary>
    internal static Verdict Judge(int code, IReadOnlyList<string> reply)
    {
        if (code != 0) return new Verdict($"DID NOT SWAP (exit {code})", code);
        // Silence. Not success, and not exit 0 either.
        if (reply.Count == 0) return new Verdict("ANSWERED NOTHING \u2014 it did not take this build", 1);
        if (reply.Any(l => l.StartsWith("armed:"))) return new Verdict("armed \u2014 it takes this build when its blocker clears", 0);
        return new Verdict("took this build", 0);
    }
}
