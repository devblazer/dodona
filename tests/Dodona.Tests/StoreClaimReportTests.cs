using System.Collections.Generic;
using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// WHAT AN OVERLAPPING CLAIM IS FOR, NOW THAT IT REFUSES NOTHING.
///
/// `REVIEW-AND-MERGE-PLAN` D-R5 / R3 retired the refusal on 2026-08-20, on the operator's own
/// words: two agents about to work on the same file is *"often the case, very often the case,
/// and if that is problematic it's the manager's job to say something about it."* What survived
/// is the REPORT — D-R7's derived ownership signal, computed by `Store.FindConflicts` inside the
/// same transaction that inserts the claims and handed to the caller to say out loud.
///
/// So the report is now the entire content of an overlap, and a conflict search that quietly
/// finds nothing is invisible: nothing exits non-zero, nothing is refused, and two agents are
/// told they are alone in a file they are not alone in. `tests/mutants/s-store-01.patch` is that
/// defect, and the three checks below are what notice it.
///
/// THESE ARE `Store` TESTS AND NOT `Claims` TESTS, and the distinction is why they are worth
/// moving rather than deleting. <see cref="ClaimsTests"/> already covers the algebra —
/// `Overlap`, `Covers`, `Normalize`, the whole-tree empty value, the shared-prefix trap — with
/// constructed arguments. What is asked here is the thing above it: that the SQL scan really
/// finds the open tickets, really scopes them by repository, and really reaches the algebra with
/// both claims in each other's terms. Under s-store-01 the algebra is untouched and every one of
/// `ClaimsTests` stays green.
/// </summary>
public class StoreClaimReportTests
{
    static List<(string, string)> Spec(params string[] specs) =>
        specs.Select(s => Claims.Parse(s)!.Value).Select(p => (p.Kind, p.Value)).ToList();

    /// <summary>
    /// MOVED from `m1:the_overlap_is_reported_and_names_the_holder`, which read
    /// `ticket-create`'s reply off the control pipe and demanded it carry `overlap:` and
    /// `ticket 1`.
    ///
    /// The old check asserted two things and so does this one: that a report was produced at
    /// all, and that it NAMES THE HOLDER. The second half is the one that matters and the one
    /// that is easy to lose — a count of overlaps ("3 open claims intersect") is useless to the
    /// manager D-R7 wrote it for, who has to go and look at those tickets. The daemon's literal
    /// `overlap: ` prefix stays where it is written (`Daemon.cs:1454`), riding the wire check
    /// that survives; what is asked here is that there is something for it to prefix.
    ///
    /// ALSO PINNED, because it was in the old check's fixture and would otherwise be lost: the
    /// ticket IS created. D-R5's whole change is that an overlap reports instead of refusing,
    /// and a `TicketCreate` that returned a real id only when nothing overlapped would satisfy
    /// every other assertion in this class.
    /// </summary>
    [Fact]
    public void the_overlap_is_reported_and_names_the_holder()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("overlap-named");
        var (water, _) = s.TicketCreate(null, "WATER", "on-approval", repo.Name, repo.Path,
                                        Spec("subtree:src/water"));

        var (second, conflicts) = s.TicketCreate(null, "WATER2", "on-approval", repo.Name, repo.Path,
                                                 Spec("path:src/water/sim.cs"));

        Assert.True(second > 0);                       // D-R5: reported, never refused
        var report = Assert.Single(conflicts);
        Assert.Contains($"ticket {water}", report);    // the holder, by id
        Assert.Contains("WATER", report);              // and by title, so a person can find it
        Assert.Contains("path:src/water/sim.cs", report);
        Assert.Contains("subtree:src/water", report);
    }

    /// <summary>
    /// MOVED from `m1:the_wide_extension_still_names_what_it_overlaps`.
    ///
    /// `claim-extend` had a refusal of its own, and `Store.ClaimExtend`'s comment carries the
    /// argument for why it went with D-R5's three: leaving it would mean a claim you may freely
    /// CREATE over another ticket's path is one you may not EXTEND onto, so the identical end
    /// state is permitted or refused depending on which command you happened to type. The
    /// extension therefore succeeds and the overlap is said — and this is the half that says it.
    ///
    /// `excludeTicket` is the trap underneath, and it is why this cannot be asked of `Claims`:
    /// the search must skip the extending ticket's OWN claims or every widening reports itself
    /// as a conflict with itself.
    /// </summary>
    [Fact]
    public void the_wide_extension_still_names_what_it_overlaps()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("wide-extension");
        var (water, _) = s.TicketCreate(null, "WATER", "on-approval", repo.Name, repo.Path,
                                        Spec("subtree:src/water"));
        var (next, _) = s.TicketCreate(null, "WATER-NEXT", "on-approval", repo.Name, repo.Path,
                                       Spec("subtree:src/sky"));

        var conflicts = s.ClaimExtend(next, Spec("subtree:/"));

        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, c => Assert.DoesNotContain($"ticket {next} ", c));   // never itself
        Assert.Contains(conflicts, c => c.Contains($"ticket {water}"));
    }

    /// <summary>
    /// MOVED from `m1:the_whole_tree_claim_is_created_and_its_overlap_reported`, which carried
    /// its own history: it was re-aimed from `the_whole_tree_claim_conflicts_with_an_open_claim`
    /// when D-R5 turned the refusal into a report, and the suite's comment says the OVERLAP is
    /// what it was always really about.
    ///
    /// THE BUG IT WAS WRITTEN FOR. `subtree:/` normalises to the EMPTY string, and every branch
    /// of the algebra then answered no — `Overlap`'s `a == b || a.StartsWith(b + "/")` cannot
    /// match an empty `a`. So a claim that READS "the whole tree" overlapped nothing: an
    /// exclusive lock over everything that let every other ticket walk straight past it, which
    /// is CLAUDE.md §0.3's enforcement-that-is-switched-off-while-looking-armed.
    ///
    /// <see cref="ClaimsTests.The_whole_tree_overlaps_every_path_claim"/> pins the algebra. What
    /// is pinned HERE is that the empty value survives the round trip through the store — it is
    /// written into `claims.value` as an empty string, read back out, and reaches `Claims.Held`
    /// still meaning "the whole tree" rather than "no value at all". That round trip is the one
    /// place an empty string is most likely to be quietly dropped, and no `Claims` test can see
    /// it.
    /// </summary>
    [Fact]
    public void the_whole_tree_claim_is_created_and_its_overlap_reported()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("whole-tree");
        var (held, _) = s.TicketCreate(null, "WATER-NEXT", "on-approval", repo.Name, repo.Path,
                                       Spec("subtree:src/water"));

        var (whole, conflicts) = s.TicketCreate(null, "WHOLE", "on-approval", repo.Name, repo.Path,
                                                Spec("subtree:/"));

        Assert.True(whole > 0);
        Assert.Contains(conflicts, c => c.Contains($"ticket {held}"));
        // and it really was stored as the whole tree, not as a literal "/"
        Assert.Contains(s.TicketClaims(whole), c => c.Kind == "subtree" && c.Value.Length == 0);
    }
}
