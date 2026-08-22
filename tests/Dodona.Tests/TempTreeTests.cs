using System;
using System.IO;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// THE CLEANUP THAT WAS FAILING ON EVERY RUN BEHIND A COMMENT SAYING IT WAS FINE (issue #25).
///
/// Four fixtures in this assembly already tried to delete their temp directory inside a
/// `try { } catch { }` excusing the failure. Every one of those deletes failed, every run: one
/// `dev test unit` leaked 25 directories and 1,574 had accumulated since 20 August, in a `%TEMP%`
/// holding 32,861 -- which is where every acceptance suite creates its sandbox. Measured before
/// and after this change: 25 per run, then 0 across two consecutive runs.
///
/// These facts are what stops it going quiet again. Deleting is checked against the two causes
/// that actually defeated it (a pooled SQLite handle; git's read-only objects), and sweeping is
/// checked on what it must take and the three things it must not touch.
/// </summary>
public sealed class TempTreeTests : IDisposable
{
    readonly string _root = TempTree.New("dodona-sweeptest-");

    string Aged(string name, int minutesOld)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddMinutes(-minutesOld));
        return dir;
    }

    /// <summary>CAUSE 1. `Microsoft.Data.Sqlite` pools connections, so `Store.Dispose()` hands the
    /// file back to a pool rather than closing it and a plain recursive delete throws. This is the
    /// one that leaked `dodona-reg-*`, `dodona-lanesink-*` and `dodona-compress-*`.</summary>
    [Fact]
    public void A_directory_holding_a_disposed_store_still_goes()
    {
        var dir = Path.Combine(_root, "with-store");
        Directory.CreateDirectory(dir);
        var store = new Store(Path.Combine(dir, "store.db"));
        store.LaneCreate("SKY");
        store.Dispose();

        Assert.True(TempTree.Delete(dir), "a disposed Store's directory would not delete -- the connection pool still holds it");
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>CAUSE 2. Git writes loose objects READ-ONLY, and `Directory.Delete(recursive: true)`
    /// throws `UnauthorizedAccessException` on the first one it meets.</summary>
    [Fact]
    public void A_directory_holding_a_read_only_file_still_goes()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "read-only")).FullName;
        var f = Path.Combine(dir, "object");
        File.WriteAllText(f, "x");
        File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);

        Assert.True(TempTree.Delete(dir), "a read-only file defeated the delete -- this is what git leaves behind");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Deleting_something_that_is_already_gone_is_a_success()
    {
        Assert.True(TempTree.Delete(Path.Combine(_root, "never-existed")));
    }

    [Fact]
    public void The_sweep_takes_an_owned_directory_that_is_old_enough()
    {
        var stale = Aged("dodona-reg-deadbeef0001", minutesOld: 90);
        var (gone, stuck) = TempTree.Sweep(_root, DateTime.UtcNow.AddMinutes(-30), mine: null);
        Assert.Equal((1, 0), (gone, stuck));
        Assert.False(Directory.Exists(stale));
    }

    /// <summary>THE THREE IT MUST NOT TOUCH, in one fact because the point is that a sweep over a
    /// shared temp directory is only safe if all three hold at once: a YOUNG one may belong to a
    /// test process running right now; an UNOWNED prefix is the tooling's (`dodona-prove` is
    /// `dev prove`'s live worktree cache and `dodona-publish` is a publish in flight); and `mine`
    /// is this process's own home.</summary>
    [Fact]
    public void The_sweep_spares_the_young_the_unowned_and_its_own_home()
    {
        var young = Aged("dodona-reg-deadbeef0002", minutesOld: 2);
        var tooling = Aged("dodona-prove", minutesOld: 600);
        var publish = Aged("dodona-publish", minutesOld: 600);
        var mine = Aged("dodona-unit-home-99999", minutesOld: 600);

        var (gone, stuck) = TempTree.Sweep(_root, DateTime.UtcNow.AddMinutes(-30), mine: "dodona-unit-home-99999");

        Assert.Equal((0, 0), (gone, stuck));
        Assert.True(Directory.Exists(young), "a directory younger than the cutoff was swept");
        Assert.True(Directory.Exists(tooling), "dodona-prove is dev prove's live worktree cache and is not ours to remove");
        Assert.True(Directory.Exists(publish), "dodona-publish can be a publish in flight");
        Assert.True(Directory.Exists(mine), "the sweep took this process's own DODONA_HOME");
    }

    public void Dispose() => TempTree.Delete(_root);
}
