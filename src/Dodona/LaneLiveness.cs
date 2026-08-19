using System.Text.Json;

namespace Dodona;

/// <summary>
/// Which lanes exist RIGHT NOW — asked of the operating system, twice, because each answer is
/// blind exactly where the other one sees (RECOVERY-PHASES P3.1, docs/INVESTIGATION-2026-08-18.md
/// RC2).
///
/// **Answer 1: the pipe namespace** (<see cref="Instance.LiveLanes()"/>). It cannot go stale — the
/// OS deletes the name when the last handle closes — and it is the ONLY answer that can see a lane
/// whose `shim-lane*.json` was never written or has been reaped. On 2026-08-18 four live agents
/// were invisible to `dodona ps` and unreachable by `stop-all --lanes` for exactly that reason,
/// three of them running out of the compiler's own output directory, blocking every build.
///
/// **Answer 2: the recorded shim pid, checked against the process table.** This is not belt and
/// braces, it is load-bearing, and it was measured: **a shim's pipe name BLINKS OUT between
/// clients.** The serve loop disposes its `NamedPipeServerStream` and constructs the next one, and
/// in that window the name is simply not in the namespace. Probed directly on this machine —
/// 8 reads out of 192 over 1.5 s saw no pipe while the shim was demonstrably alive and instantly
/// connectable. That window is not rare in practice, it is *synchronised*: when a daemon exits,
/// every one of its shims disconnects at the same instant, and the next daemon's reconcile runs
/// milliseconds later. A single instantaneous read there declared four to seven live lanes "gone"
/// per restart, orphaning every agent in the workspace. Caught in brain-acceptance, which noticed
/// that a restart had stopped adopting anything.
///
/// The union is right in both directions. A record whose pid is dead contributes nothing (that
/// over-count is what made `ps` say "24 lanes" with six processes alive), and a lane with no
/// record at all is still seen by its pipe.
/// </summary>
static class LaneLiveness
{
    /// <summary>Is this pid a LIVE process of the expected kind? The name check is not decoration:
    /// pids are reused, and a recycled pid would otherwise resurrect a lane that died days
    /// ago.</summary>
    public static bool PidAlive(int pid, string expectNamePrefix)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited &&
                   p.ProcessName.StartsWith(expectNamePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }          // no such process, or it exited between the two calls
    }

    /// <summary>Every `shim-lane*.json` in a state directory, parsed. A RECORD, not a fact about
    /// what is running — see <see cref="LiveRecords"/> for that, and never count these.</summary>
    public static List<(long Lane, int Shim, int Child)> Records(string dir)
    {
        var list = new List<(long, int, int)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "shim-lane*.json"))
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(f));
                    var lane = long.TryParse(Path.GetFileNameWithoutExtension(f)["shim-lane".Length..], out var n) ? n : 0;
                    int Pid(string k) => d.RootElement.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
                    list.Add((lane, Pid("shimPid"), Pid("childPid")));
                }
                catch { /* half-written or stale: skip it rather than fail the listing */ }
            }
        }
        catch (DirectoryNotFoundException) { }
        return list;
    }

    /// <summary>Records whose shim process is actually running. This is the PID LOOKUP FOR KILLING
    /// (and the second liveness answer above) — it is deliberately not the count anywhere.</summary>
    public static List<(long Lane, int Shim, int Child)> LiveRecords(string dir) =>
        Records(dir).Where(t => PidAlive(t.Shim, "DodonaShim")).ToList();

    /// <summary>The lanes of one instance that exist right now: live pipe OR live shim process.
    /// Read the class comment before narrowing this to one of the two — the second one is there
    /// because the first was measured wrong, not to be safe.</summary>
    public static HashSet<long> Live(string instanceId, string stateDir, int settleMs = 0)
    {
        var live = new HashSet<long>(Instance.LiveLanes(instanceId));
        foreach (var (lane, _, _) in LiveRecords(stateDir)) live.Add(lane);
        // A second sample, for the one caller that must not MISS a lane: `stop-all --lanes`, where
        // a lane skipped is a process left running that no later command can find. A recordless
        // orphan is visible only by its pipe, and that pipe is exactly what blinks -- so for that
        // lane a single sample is the whole evidence base. Two samples a quarter-second apart
        // cannot both land in the same few-millisecond gap.
        if (settleMs > 0)
        {
            Thread.Sleep(settleMs);
            foreach (var lane in Instance.LiveLanes(instanceId)) live.Add(lane);
            foreach (var (lane, _, _) in LiveRecords(stateDir)) live.Add(lane);
        }
        return live;
    }

    /// <summary>Delete shim-info files whose shim is gone. Dodona's own bookkeeping, never repo
    /// content (CLAUDE.md §5), so self-healing on read is right: the standing directive is that
    /// nothing is allowed to go quietly stale, and eighteen dead files had accumulated unnoticed.
    /// Announced by the caller, never silent.
    ///
    /// A shim now deletes its own record on every exit it controls, so this mostly catches what a
    /// -Force kill left behind — which is precisely the case no `finally` can reach.</summary>
    public static int Reap(string dir)
    {
        var reaped = 0;
        foreach (var (lane, shim, _) in Records(dir))
        {
            if (PidAlive(shim, "DodonaShim")) continue;
            try { File.Delete(Path.Combine(dir, $"shim-lane{lane}.json")); reaped++; }
            catch { /* in use or already gone: a failed reap must never fail the listing */ }
        }
        return reaped;
    }
}
