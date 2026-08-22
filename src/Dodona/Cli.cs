using System.Text.Json;

namespace Dodona;

/// <summary>
/// The command line, as a real type rather than a local function inside
/// <c>Program.&lt;Main&gt;$</c> (docs/testarch/seams.md S11, the other half of the gate seam).
///
/// It was already <c>static</c> and already pure -- argv in, a five-tuple out -- and it was still
/// unreachable from a test, because a top-level local function is compiled into the synthesised
/// entry point and nothing can name it. That is the whole of the seam: the body below is the
/// previous one, moved, and <c>Program.cs</c> line 9 now calls it.
///
/// The reason it is worth a seam at all is the incident in its own first comment: `orphans` was
/// missing from <c>boolFlags</c>, so <c>stop-all --orphans</c> did nothing whatsoever, and the
/// only way anyone found out was by running it. A declaration list that a test can read is a
/// declaration list a test can hold to the commands that use it.
/// </summary>
static class Cli
{
    // `rootSource` is not a nicety: `root` is EITHER a typed `--root` OR the folder this process
    // was started in, and until this was returned alongside it nothing downstream could tell the
    // two apart — so `WorkspaceResolve.ForPath` created a workspace for either one, which is how an
    // agent's `dodona tickets` invented a workspace named after a folder and moved a store into it
    // (plan D-L9, Phase 0c). Carry the provenance; do not try to re-derive it from the string.
    internal static (string? cmd, string root, PathSource rootSource, Dictionary<string, List<string>> opts, List<string> pos) ParseArgs(string[] args)
    {
        // Valueless flags must be declared: otherwise `--json` at the end of a line is
        // indistinguishable from a positional argument, and silently becomes one.
        // `orphans` was MISSING here, so `stop-all --orphans` did nothing at all: a `--flag` that is
        // not in this set only registers when another argument follows it, and as the last word on
        // the line it fell through to the positional list instead. So the escape hatch 69e8003
        // added -- the one the LEFT ALONE message tells you to use -- has never once worked. Found
        // by running it (`stop-all --lanes --orphans` named two ghost lanes, then left them);
        // reading the code that prints the message would never have shown it.
        var boolFlags = new HashSet<string> { "json", "successor", "all", "adopt", "shortcut", "hi", "bulk", "shell", "concierge", "lanes", "orphans", "partial", "no-wait" };

        string? cmd = null;
        string root = Environment.CurrentDirectory;
        var rootSource = PathSource.Inherited;
        var opts = new Dictionary<string, List<string>>();
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; rootSource = PathSource.Named; continue; }
            if (args[i].StartsWith("--") && boolFlags.Contains(args[i][2..])) { opts[args[i][2..]] = new List<string> { "true" }; continue; }
            if (args[i].StartsWith("--") && i + 1 < args.Length)
            {
                var key = args[i][2..];
                if (!opts.TryGetValue(key, out var list)) opts[key] = list = new List<string>();
                list.Add(args[++i]);
                continue;
            }
            if (cmd is null) { cmd = args[i]; continue; }
            pos.Add(args[i]);
        }
        // `--adopt` is what turns a NAMED path into an EXPLICIT one — the difference between
        // asking about a folder and taking it on (issue #12; see PathSource). Resolved after the
        // loop on purpose: flags may be written in any order, and `--root <p> --adopt` and
        // `--adopt --root <p>` must mean the same thing.
        //
        // The flag is shared with `repo-init --adopt`, deliberately rather than by accident: it
        // means the same thing in both places — take this on rather than refusing — and it can
        // only ever matter for a root no workspace owns, which `repo-init` never has anyway.
        if (rootSource == PathSource.Named && opts.ContainsKey("adopt")) rootSource = PathSource.Explicit;
        return (cmd, root, rootSource, opts, pos);
    }
}
