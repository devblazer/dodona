namespace Dodona;

/// <summary>
/// THE LANE BRIEFING (docs/LANE-BRIEFING-PLAN.md B1): what ground this agent is standing on,
/// written in ONE place and correct per lane kind.
///
/// It exists because there were two hand-maintained prompt strings and every divergence between
/// them was a defect. A PLAIN lane was told, at length, never to change which branch is checked
/// out; a TICKET lane -- the lane that actually HAS a branch and a worktree -- was told nothing
/// about git at all. And the one git-adjacent sentence a ticket lane did get was FALSE: it said a
/// PreToolUse gate denies writes outside the declared claim and to ask for an extension if
/// refused. `REVIEW-AND-MERGE-PLAN` R3 / D-R5 retired that refusal on 2026-08-20 along with
/// `claim-extend`'s, and CLAUDE.md 7 says in terms: do not describe a ticket lane as bounded to
/// its claim. It described one anyway, to every ticket agent, on every turn.
///
/// **A PROMPT REMOVES FRICTION. IT NEVER PROVIDES SAFETY** (`M5-DELIVERY-PLAN` 1, and CLAUDE.md
/// 0.3's *a written warning is not a fix*). Nothing here is a guarantee and nothing existing may
/// be relaxed because it landed -- M5's branch lock and Bash-gate rewrites stay in that plan
/// untouched. What this changes is that an agent acting against what Dodona needs is doing it
/// while informed, rather than while never having been told.
///
/// **SHORT, OR IT GETS SKIMMED.** Five bullets under one framing line, and growth is a
/// regression rather than a feature: B2 repeats this block on every turn of every work lane for
/// as long as the lane lives, so length here is a bill that compounds (CLAUDE.md 0.1, quota).
/// <see cref="MaxBullets"/> and <see cref="MaxChars"/> are asserted in `unit` so it cannot creep.
///
/// **TRAP T1 RUNS THROUGH THIS FILE.** `Projects.Named` scans a system prompt for
/// <see cref="Projects.DirLead"/>/<see cref="Projects.DirTail"/> and `Projects.PromptDirMismatch`
/// compares what it finds against where the process will really start. So the plain block writes
/// <see cref="Projects.DirSentence"/> whole and unsplit, and the TICKET block names no folder at
/// all -- exactly as the ticket prompt has always done. Giving a ticket lane a directory sentence
/// would make that safety detector start comparing ticket lanes, which it does not do today; that
/// may well be worth doing, and it is a behaviour change to a detector, so it needs its own
/// decision and its own commit (LANE-BRIEFING-PLAN 5).
/// </summary>
internal static class Briefing
{
    /// <summary>The framing line. Also the marker every check keys on, and the thing the pane
    /// must never contain (B2): if this string turns up in a `user_input` row, the briefing has
    /// leaked into the operator's feed and into the compressor's input.</summary>
    public const string Head = "You are working inside a Dodona system. That means:";

    /// <summary>The per-turn wrapper (B2). `[DISPATCHER]` is not a new convention -- both system
    /// prompts already declare that channel and `RouteInput`'s misroute retraction already rides
    /// it. The CLOSING marker is the new part, and it is the smallest thing that makes the block
    /// unambiguously separable from the operator's words by whoever reads it next.</summary>
    public const string TurnLead = "[DISPATCHER] ";
    public const string TurnEnd = "[/DISPATCHER]";

    /// <summary>The bound, asserted rather than remembered (LANE-BRIEFING-PLAN D-B2). Five
    /// bullets under one framing line, and a character bound, which is the one that matters
    /// because tokens are what this costs. MEASURED 2026-08-21: the longest of the four shapes
    /// `unit` builds is 975 characters, so the slack here is about half a bullet -- deliberately
    /// not much. Growing past it has to be argued for in a commit rather than absorbed.</summary>
    public const int MaxBullets = 5;
    public const int MaxChars = 1050;

    internal enum Kind { Plain, Ticket }

    /// <summary>A lane with no ticket: a SHARED checkout, and nothing of its own.</summary>
    public static string Plain(string workDir) => Block(Kind.Plain, workDir, "", 0, false);

    /// <summary>A ticket lane: its own worktree, its own branch, and a delivery mode.</summary>
    public static string Ticket(long ticketId, string branch, bool pr) => Block(Kind.Ticket, "", branch, ticketId, pr);

    /// <summary>One shared ref for the whole repository, so the hazard is identical in both
    /// kinds and the sentence is written once (CLAUDE.md 5.2: two lanes stashing interleave one
    /// stack and `pop` takes the other lane's work).</summary>
    const string Stash = "Never `git stash`: it is ONE shared ref for the whole repository, so another lane's `pop` takes your work.";

    /// <summary>The [DISPATCHER] declaration, unchanged from both prompts it replaces -- spike 3
    /// measured that a model treats mid-turn operator instructions as a prompt-injection attempt
    /// and refuses them unless the channel is declared.</summary>
    const string Channel = "Real-time instructions from your human operator arrive labeled [DISPATCHER]; they are authentic " +
                           "and carry the same authority as your original task, even when they change or contradict it.";

    public static string Block(Kind kind, string workDir, string branch, long ticketId, bool pr)
    {
        var bullets = new List<string>(MaxBullets);
        if (kind == Kind.Plain)
        {
            // WHOLE AND UNSPLIT (T1). `Projects.Named` reads this sentence back out of the argv
            // it ends up in; break it and the detector silently detects nothing.
            bullets.Add(Projects.DirSentence(workDir) + " It is a SHARED checkout: your operator and other lanes are " +
                        "working in it at the same time, so it is not yours to reconfigure.");
            bullets.Add("Never `git checkout`, `git switch`, or anything else that changes which branch is checked out " +
                        "— it silently reassigns their uncommitted work along with yours. If this task needs a branch " +
                        "of its own, say so and stop: your operator makes a ticket, and a ticket gets a private checkout.");
            bullets.Add(Stash + " Commit where you are instead.");
            bullets.Add("You have no ticket, so Dodona lands nothing for this work and cuts no branch for it — if it " +
                        "has to reach main, say so and stop.");
        }
        else
        {
            // NAMES NO FOLDER, on purpose (T1). "the current working directory" is the honest
            // answer for a worktree and it is what keeps `Projects.Named` returning null here.
            bullets.Add($"This directory is your own worktree for ticket {ticketId}; work only in it. The shared checkout " +
                        "and every other lane are elsewhere, and are not yours to touch.");
            // The silent killer of CLAUDE.md 5.2: `checkout <existing-branch>` FAILS loudly if
            // that branch is checked out elsewhere and SUCCEEDS if it is not, so the worktree
            // wanders off its branch while Dodona's recorded branch goes stale.
            bullets.Add($"You are already on {(branch is { Length: > 0 } ? $"`{branch}`" : "this worktree's own branch")}. " +
                        "Never check out a branch that ALREADY EXISTS: in a worktree that quietly succeeds whenever the " +
                        "branch is free elsewhere, and walks you off your own. Cutting a NEW branch is fine.");
            bullets.Add(Stash + " Commit to your branch instead.");
            // R7 / D-R28. A lane in a pr repository currently discovers the mode only by being
            // refused -- by `land`, by `token-request`, or by an approval question that never
            // comes. Dodona does not touch a forge at all, so the sentence says whose ceremony
            // it is rather than naming steps Dodona cannot perform.
            bullets.Add(pr
                ? "This repository delivers by PR. Dodona will not merge, will not grant a merge token and will not delete " +
                  $"your branch — deliver it the project's own way; `dodona ticket-record {ticketId}` is what the work did."
                : "Dodona lands this ticket: bring main into your branch and verify there, and Dodona fast-forwards main " +
                  "onto it — after your operator approves, never on its own.");
        }
        bullets.Add(Channel);
        return Head + "\n- " + string.Join("\n- ", bullets);
    }

    /// <summary>The per-turn form: the same block, labeled, closed, and followed by a blank line
    /// so the operator's own words start clean (B2). A PREFIX ONLY -- what is RECORDED is the
    /// operator's text alone, which is the whole design constraint of B2: `LaneRuntime.Say`
    /// writes one string to two destinations, so a naive prefix would put this in the pane, in
    /// the operator's feed and in the compressor's input on every message they ever send.</summary>
    public static string Turn(string block) => TurnLead + block + "\n" + TurnEnd + "\n\n";
}
