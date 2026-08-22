using System.IO;
using System.Linq;
using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// THE NAME IS DISPLAY; THE ID IS IDENTITY (docs/WORKSPACES-CONCIERGE.md section 1).
///
/// Path-derived identity used to make "two merge tokens over one main" structurally impossible:
/// two spellings of a repo hashed to one id, one mutex, one token. Named workspaces deleted that,
/// so the invariant moved up a level and became registry law -- and the half that keeps it honest
/// is that a RENAME re-derives nothing. Pipes, the OS mutex and the store directory key off the
/// generated id and never off the name, so renaming "personal" to "home" moves no file and
/// orphans no daemon.
///
/// These all ran through the real CLI and a real `where --json` in `workspace-acceptance.ps1`.
/// The answers are `Registry` rows and `Paths` / `Instance` derivations, and seam S5 is what puts
/// the real registry on a temp file instead of a daemon.
/// </summary>
public class RegistryIdentityTests
{
    /// <summary>A rename changes the display name and NOTHING else. The id is what the store
    /// directory, the control pipe and the OS mutex are all keyed on.</summary>
    [Fact]
    public void rename_keeps_the_id()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("rival");
        Assert.True(reg.Rename(ws.Id, "renamed-rival", out var err), err);
        var after = reg.ById(ws.Id);
        Assert.NotNull(after);
        Assert.Equal(ws.Id, after!.Id);
        Assert.Equal("renamed-rival", after.Name);
    }

    /// <summary>...so the store does not move. `Paths.Store` takes the id, which is the whole
    /// design: an id that MEANT the name would have to relocate a SQLite database to change one.</summary>
    [Fact]
    public void rename_keeps_the_store_path()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("rival");
        var before = Paths.Store(ws.Id);
        Assert.True(reg.Rename(ws.Id, "renamed-rival", out var err), err);
        var after = reg.ById(ws.Id);
        Assert.NotNull(after);
        Assert.Equal(before, Paths.Store(after!.Id));
    }

    /// <summary>...and neither does the control pipe, which is the reason this matters at run
    /// time rather than on disk: a live daemon is reachable at `dodona-&lt;id&gt;-ctl`, and a rename
    /// that moved that name would strand every client mid-session.</summary>
    [Fact]
    public void rename_keeps_the_ctl_pipe()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("rival");
        var before = Instance.CtlPipe(ws.Id);
        Assert.True(reg.Rename(ws.Id, "renamed-rival", out var err), err);
        var after = reg.ById(ws.Id);
        Assert.NotNull(after);
        Assert.Equal(before, Instance.CtlPipe(after!.Id));
    }

    /// <summary>The new name resolves -- to the SAME workspace. Rung 1 of the concierge ladder
    /// (exact id, exact name, then alias) and it must never cost a token.</summary>
    [Fact]
    public void new_name_resolves()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("rival");
        reg.Rename(ws.Id, "renamed-rival", out _);
        Assert.Equal(ws.Id, reg.ByNameOrId("renamed-rival")?.Id);
    }

    /// <summary>REASSIGNMENT IS LEGITIMATE -- it is what the exclusivity refusal points at -- and
    /// it is atomic: after a move the repo is in exactly one workspace, never two and never
    /// none. Both halves are asserted, because a `Move` that only inserted would leave two
    /// owners and a `Move` that only deleted would leave none, and "it moved" is true of
    /// neither.</summary>
    [Fact]
    public void move_reassigns_the_repo()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var repo = t.GitFolder("solo");
        var owner = reg.Create("owner");
        var rival = reg.Create("rival");
        reg.Attach(owner.Id, repo, out _);

        Assert.True(reg.Move(rival.Id, repo, out var err), err);
        var owners = reg.All().Where(w => w.Members.Any(m => m.Key == TempReg.Key(repo))).ToList();
        Assert.Single(owners);
        Assert.Equal("rival", owners[0].Name);
    }

    /// <summary>Forget deletes the REGISTRY ROWS.</summary>
    [Fact]
    public void forget_removes_the_registry_row()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("twin");
        Assert.True(reg.Forget(ws.Id, out var err), err);
        Assert.Null(reg.ById(ws.Id));
        Assert.DoesNotContain(reg.All(), w => w.Id == ws.Id);
    }

    /// <summary>...and the store directory does NOT go. Nothing in this system deletes a
    /// transcript (design section 12), and "undo a workspace I made by accident" must never be
    /// able to mean "delete six lanes of history".</summary>
    [Fact]
    public void forget_keeps_the_store_directory()
    {
        using var t = new TempReg();
        using var reg = t.Open();
        var ws = reg.Create("twin");
        var dir = Paths.WorkspaceDir(ws.Id);
        Assert.True(Directory.Exists(dir));
        Assert.True(reg.Forget(ws.Id, out var err), err);
        Assert.True(Directory.Exists(dir));
    }

    /// <summary>The workspace id is a readable SLUG plus four hex -- GENERATED, not derived from
    /// the name and not a hash of a path. That is the whole point of section 1: a derived id
    /// would have to change when the thing it was derived from changed, which is how a rename
    /// becomes a store migration.</summary>
    [Fact]
    public void identity_is_a_slug_not_a_path_hash()
    {
        var id = Registry.NewId("dodona-ws-1a2b3c4d", _ => false);
        Assert.Matches("^[a-z0-9-]+-[0-9a-f]{4}$", id);
        Assert.Contains("dodona-ws", id);
    }
}
