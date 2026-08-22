using System;
using Dodona;

namespace Dodona.Tests;

/// <summary>
/// A real <see cref="Store"/> with no disk, and a clock a test can move.
///
/// WHY `:memory:` RATHER THAN A TEMP FILE. Plan §3.5 forbids faking `Store` by name, because
/// *"the properties ARE the transactions"* — `LandCommit` re-checks holder identity and lease
/// expiry INSIDE the transaction that lands the ticket, frees the claims and withdraws the
/// approval question, and a stand-in would be asserting about itself. So the store here is the
/// real class, the real SQLite engine, the real migration ladder and the real multi-statement
/// commands. The only thing that is absent is the disk.
///
/// That absence is what makes a bulk slice affordable. W4 measured a real `Store` on a temp
/// FILE at **56 ms per case** and recorded the honest ceiling: roughly 60 more such cases
/// before the operator's one-to-two-second unit budget goes (`tests/ledger/README.md`, W4,
/// *"falsifier 4, measured"*). `S-STORE` opens one per case, so on a file it would have spent a
/// third of the whole budget on `CreateDirectory` and `fsync`. Seam S2 (`docs/testarch/seams.md`)
/// exists for exactly this and names exactly this population: *"migration ladder, token FIFO,
/// claim conflicts, question upsert — no disk."*
///
/// WHAT `:memory:` DOES NOT GIVE YOU, said here so nobody discovers it the expensive way:
/// `StoreReader.Open()` needs a FILE and a second connection (`seams.md` §5, a measured
/// constraint rather than a preference), so nothing about the UI read path can be asked here.
/// Neither can durability, WAL behaviour, or the pre-migration backup — `SchemaAtOpen` is 0 for
/// a fresh in-memory database, so the copy that guards a migration never runs.
///
/// A STORE PER CASE, NOT A SHARED ONE, AND THAT IS A MEASUREMENT. `IClassFixture` is the right
/// answer above roughly 30 disk-touching cases; the reading that decides it is `dev test unit`'s
/// own printed seconds, warm, and it did not move here (see this slice's commit message). A
/// shared store would buy nothing and cost the property every case below depends on: each one
/// starts from an empty schema, so no test can be made to pass or fail by the order xunit
/// happens to run it in.
/// </summary>
static class Mem
{
    /// <summary>A fresh empty store on the real clock.</summary>
    internal static Store Store() => new Store(":memory:");

    /// <summary>A fresh empty store whose clock is the returned dial. Move the dial instead of
    /// sleeping: `m1`'s lease pair carried this suite's only real `Start-Sleep -Seconds 2`, and
    /// CLAUDE.md §1 is explicit that a fixed sleep is a guess about the slowest machine that
    /// ever ran it, paid in full on every machine since.</summary>
    internal static (Store Store, Clock Clock) OnAClock()
    {
        var clock = new Clock();
        return (new Store(":memory:", () => clock.UtcNow), clock);
    }

    internal sealed class Clock
    {
        internal DateTime UtcNow { get; private set; } = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        internal void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>A repository identity of this test's own. Every case uses a distinct path, and
    /// that is load-bearing rather than tidy: `Store.FindConflicts` scans EVERY open ticket in
    /// the store and `Claims.Overlap(Held, Held)` refuses to compare two claims from different
    /// repositories, and the merge token is keyed on `repo_path`. Distinct paths are therefore
    /// what makes two cases in one store — or one case rerun — unable to see each other.</summary>
    internal static Store.RepoId Repo(string name) => new Store.RepoId($@"c:\ws\{name}", ".");

    /// <summary>A `main` sha for the token grant to record. `TokenRequest` writes `sha[..8]`
    /// into its event detail, so a short one throws rather than failing an assertion.</summary>
    internal static string Sha(string seed) => seed.PadRight(40, '0')[..40];
}
