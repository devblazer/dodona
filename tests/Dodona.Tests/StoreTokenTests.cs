using System;
using System.Collections.Generic;
using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// THE MERGE TOKEN: one lease, one holder, one queue, and the fence that re-checks all three
/// inside the transaction that lands the ticket.
///
/// This is the population plan §3.5 names first when it says `Store` is never faked: *"the
/// properties ARE the transactions — `LandCommit` re-checks holder identity and lease expiry
/// INSIDE the tx that lands the ticket, frees the claims and withdraws the `land` question, in
/// one multi-statement command."* Every case below runs that real SQL against a real SQLite
/// engine. What is absent is the daemon, the pipe, git, and — for the two lease cases — the two
/// real seconds `m1` used to sleep.
///
/// THE CLOCK IS A SEAM, NOT A FAKE. `Store(path, utcNow)` defaults to the real clock, so
/// production has exactly one path (`Trees.Locate`'s shape, `Trees.cs:44` + `:77`). What it
/// replaces is `m1-acceptance.ps1`'s `Start-Sleep -Seconds 2`, which CLAUDE.md §1 describes
/// exactly: *"a guess about the slowest machine that ever ran it, paid in full on every machine
/// since, while the condition it is waiting for is already written down one line below."*
///
/// WHAT THESE CASES DO NOT COVER, said out loud because finding it is what re-aimed this slice:
/// `Daemon.LandGate` (`Daemon.cs:6475`) carries its OWN copy of the expiry arithmetic and
/// refuses ahead of `LandCommit`, so `m1:expired_lease_cannot_land` never reaches the fence
/// below. Proved rather than argued — `tests/mutants/s-store-05.patch` switches `Store.Expired`
/// off entirely and that acceptance check comes back VACUOUS. The duplication is a suspect this
/// slice reports and does not fix; the check stays in `m1` over its own copy.
/// </summary>
public class StoreTokenTests
{
    static List<(string, string)> Spec(params string[] specs) =>
        specs.Select(s => Claims.Parse(s)!.Value).Select(p => (p.Kind, p.Value)).ToList();

    static long Ticket(Store s, Store.RepoId repo, string title) =>
        s.TicketCreate(null, title, "on-approval", repo.Name, repo.Path, Spec("subtree:src/" + title.ToLowerInvariant())).Id;

    /// <summary>
    /// MOVED from `m1:approved_token_granted`, which ran `dodona approve 1` and then
    /// `dodona token-request 1` and demanded the reply say `granted ticket 1`.
    ///
    /// The gate that reads `Approved` lives in the daemon (`Daemon.cs:1757`) and stays there
    /// with the wire; what moves is the pair of store facts underneath it — that `TicketApprove`
    /// really marks the row, and that a request against a free token really returns a GRANT.
    ///
    /// THE WORD IS THE WHOLE POINT, and that is why `tests/mutants/s-store-02.patch` changes
    /// only the word. Every caller above `TokenRequest` branches on the status string, so a
    /// grant that reports itself as a queue is a ticket that holds the token and does not know
    /// it: it waits for a handoff that already happened while every other ticket in that
    /// repository is fenced behind its lease. Asserted with the holder row beside it, so the
    /// converse defect — a status that says "granted" over a token nobody was given — is caught
    /// too.
    /// </summary>
    [Fact]
    public void approved_token_granted()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("granted");
        var t = Ticket(s, repo, "WATER");

        Assert.False(s.Ticket(t)!.Approved);
        s.TicketApprove(t);
        Assert.True(s.Ticket(t)!.Approved);

        var (status, gen, position) = s.TokenRequest(t, repo, 120, () => Mem.Sha("a"));

        Assert.Equal("granted", status);
        Assert.Equal(0, position);
        Assert.Equal(t, s.TokenRead(repo).Holder);
        Assert.True(gen > 0);
    }

    /// <summary>
    /// MOVED from `m1:second_ticket_queued`.
    ///
    /// This is the serialization the whole design exists for: one repository's `main` is
    /// advanced by one ticket at a time, and the second asker WAITS rather than being refused or
    /// — far worse — granted. `tests/mutants/s-store-04.patch` deletes half of one condition
    /// (`t.Holder is null && head == ticketId` becomes `head == ticketId`) and the second ticket
    /// takes the token out from under the live holder. That is two agents holding one main,
    /// reached by removing four words, with nothing raising an error anywhere.
    ///
    /// The holder assertion is not decoration: a "queued" status returned while the token
    /// silently changed hands would satisfy the old acceptance check, which matched the word
    /// alone.
    /// </summary>
    [Fact]
    public void second_ticket_queued()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("queued");
        var first = Ticket(s, repo, "WATER");
        var second = Ticket(s, repo, "SKY");
        s.TicketApprove(first);
        s.TicketApprove(second);
        Assert.Equal("granted", s.TokenRequest(first, repo, 120, () => Mem.Sha("a")).Status);

        var (status, _, position) = s.TokenRequest(second, repo, 120, () => Mem.Sha("b"));

        Assert.Equal("queued", status);
        Assert.Equal(1, position);
        Assert.Equal(first, s.TokenRead(repo).Holder);   // and it did NOT change hands
    }

    /// <summary>
    /// MOVED from `m1:queued_ticket_now_granted`, the FIFO handoff.
    ///
    /// Release-on-land is one statement inside `LandCommit`'s multi-statement transaction, and
    /// it is the one that lets the next ticket move. `tests/mutants/s-store-03.patch` gives that
    /// one `UPDATE` an `AND 0`: the land still succeeds in every other respect — ticket marked
    /// landed, claims freed, queue row dropped, approval question withdrawn — and the repository
    /// is wedged forever by a holder that has already gone, with nothing reporting an error.
    ///
    /// Asserted through `LandCommit` rather than `TokenRelease` on purpose: the acceptance
    /// ancestor got here by LANDING ticket 1, and the release the operator actually depends on
    /// is the one bound into the landing transaction. `TokenRelease` is a different door.
    /// </summary>
    [Fact]
    public void queued_ticket_now_granted()
    {
        using var s = Mem.Store();
        var repo = Mem.Repo("handoff");
        var first = Ticket(s, repo, "WATER");
        var second = Ticket(s, repo, "SKY");
        s.TicketApprove(first);
        s.TicketApprove(second);
        s.TokenRequest(first, repo, 120, () => Mem.Sha("a"));
        Assert.Equal("queued", s.TokenRequest(second, repo, 120, () => Mem.Sha("b")).Status);

        Assert.True(s.LandCommit(first, repo, out var reason), reason);

        Assert.Equal("granted", s.TokenRequest(second, repo, 120, () => Mem.Sha("b")).Status);
        Assert.Equal(second, s.TokenRead(repo).Holder);
    }

    /// <summary>
    /// MOVED from `m1:regrant_after_expiry_lands`, which slept two real seconds past a
    /// one-second lease and then re-requested.
    ///
    /// A CRASHED HOLDER MUST NOT WEDGE A REPOSITORY. That is the whole of what the lease buys:
    /// the token is reclaimed by the next asker rather than by anybody noticing, so the failure
    /// mode of a lane that died mid-land is a delay and not a dead repository.
    ///
    /// `tests/mutants/s-store-06.patch` is the defect a reclaim actually has — two copies of one
    /// fact, one updated. The `UPDATE` clears `holder_ticket` in the transaction and the local
    /// row is not re-read, so the requester matches its own stale id, returns "granted"
    /// immediately, and never re-stamps the lease. The ticket is told it holds a token whose
    /// lease expired before it asked, and the fence inside `LandCommit` refuses it. Forever,
    /// quietly, on every retry.
    ///
    /// So this asserts the LAND, not merely the word "granted" — the acceptance ancestor did the
    /// same, and under that defect a status-only assertion would have stayed green.
    /// </summary>
    [Fact]
    public void regrant_after_expiry_lands()
    {
        var (store, clock) = Mem.OnAClock();
        using var s = store;
        var repo = Mem.Repo("regrant");
        var t = Ticket(s, repo, "EXPIRY");
        s.TicketApprove(t);
        Assert.Equal("granted", s.TokenRequest(t, repo, 1, () => Mem.Sha("a")).Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(s.LandCommit(t, repo, out var refused));
        Assert.Contains("expired", refused);

        Assert.Equal("granted", s.TokenRequest(t, repo, 120, () => Mem.Sha("a")).Status);

        Assert.True(s.LandCommit(t, repo, out var reason), reason);
        Assert.Equal("landed", s.Ticket(t)!.State);
    }

    /// <summary>
    /// ADDED, not moved (`tests/ledger/added.tsv`), and the reason it is added is a finding.
    ///
    /// `m1:expired_lease_cannot_land` looks like the check that covers this. It is not.
    /// `Daemon.LandGate` (`Daemon.cs:6475`) carries a SECOND copy of the expiry arithmetic —
    /// `DateTime.Parse(tok.ExpiresTs).ToUniversalTime() &lt; DateTime.UtcNow` — and refuses
    /// before `LandFlow` ever reaches the store. Measured rather than reasoned:
    /// `tests/mutants/s-store-05.patch` makes `Store.Expired` return false unconditionally, and
    /// `dev prove` reports that acceptance check **VACUOUS -- PASS**. Expiry switched off in the
    /// transaction that lands tickets, and the check named after it did not notice.
    ///
    /// This is the fence that actually holds. The daemon's copy is a cheap gate on the way in;
    /// `LandCommit`'s runs inside the transaction, which is what decides the outcome when a
    /// stale holder and a reclaiming requester arrive together. Nothing in this repository
    /// asserted it, so it is asserted here — CLAUDE.md §0's strongest form, since a duplicated
    /// predicate with only one of its copies under test is a drift nobody would see.
    ///
    /// The duplication itself is reported as a suspect and is NOT fixed here: a migration commit
    /// that also changes behaviour is unreviewable (plan §5.4).
    /// </summary>
    [Fact]
    public void the_land_fence_refuses_an_expired_lease()
    {
        var (store, clock) = Mem.OnAClock();
        using var s = store;
        var repo = Mem.Repo("fence");
        var t = Ticket(s, repo, "EXPIRY");
        s.TicketApprove(t);
        s.TokenRequest(t, repo, 60, () => Mem.Sha("a"));

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False(s.LandCommit(t, repo, out var reason));
        Assert.Contains("expired", reason);
        Assert.Equal("open", s.Ticket(t)!.State);        // and nothing landed
        Assert.NotEmpty(s.TicketClaims(t));              // and no claim was freed
    }
}
