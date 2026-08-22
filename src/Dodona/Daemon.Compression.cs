using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Dodona;

// Part of Daemon, split out of a 6,791-line file (issue #23). Same class, same
// behaviour -- only the file boundary is new.
sealed partial class Daemon
{
    /// <summary>
    /// The compressor pool (§5). Warm sessions, so a turn lands on ~1s of latency instead
    /// of a cold start, and a POOL of them rather than one: a single compressor
    /// accumulating six lanes' turn-finals is exactly the unbounded serialization point
    /// §3 forbids the dispatcher to be. Cheap model, low effort — shortening a paragraph
    /// is not where judgement compounds (§9's ladder), and it runs 5–10× more often than
    /// anything else in the system.
    /// </summary>
    async Task<string> StartCompressorsAsync(string child, string model, string effort, int count)
    {
        count = Math.Clamp(count, 1, 4);
        // A schema, not an instruction to be brief: "be concise" is advice a model may
        // decline, a character cap on a named field is not (§4/§5).
        var sys = "You are Dodona's pane compressor. You will be given one agent's turn-final message. " +
                  "Reply with ONLY one line of JSON, no prose, no markdown, no code fence: " +
                  "{\"headline\":\"<=90 characters\",\"needs_you\":true|false,\"options\":[\"<a few words>\"]} " +
                  "headline is what the operator must know, written for someone glancing at one pane of six: " +
                  "past tense for work that happened, imperative for what is wanted. No preamble, no markdown, " +
                  "never mention 'the user', never restate the question. " +
                  "needs_you is true only when the work cannot continue without a human decision. " +
                  "options lists those choices, at most three, and is [] whenever needs_you is false.";
        var args = IsClaude(child) ? ClaudeArgs(_config, model, effort, sys, acceptEdits: false, utility: true) : new List<string>();

        var alive = _store.LanesAll().Count(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id));
        if (alive >= count) return $"compressor pool already warm ({alive})";
        // P3.5 applies to the POOL too, and the plan named only the brain and the router. The
        // arithmetic above counts a compressor as present only if it is ADOPTED, so a pool member
        // whose pipe is live but unreachable is invisible here and gets a replacement started
        // beside it -- one leaked `claude -p` per restart, the brain leak in a third costume.
        // Closing the class rather than the two instances the plan happened to list.
        if (!await ClearOfLivePredecessorsAsync(null, "compressor"))
            return "compressor pool left as it is: a previous pool member is still holding its pipe " +
                   "(`dodona ps` shows it; `dodona stop-all --lanes` clears it)";
        var started = new List<long>();
        for (int i = alive; i < count; i++)
        {
            var (id, msg) = await SpawnLaneAsync($"COMPRESS{i + 1}", "compressor", NeutralCwd(), child, args);
            if (id < 0) return started.Count > 0
                ? $"compressor pool partially up: {started.Count} warm, then: {msg}"
                : $"error: {msg}";
            _compressorLocks[id] = new SemaphoreSlim(1, 1);
            started.Add(id);
        }
        return $"compressor pool warm: {alive + started.Count} session(s) on {model}" +
               (effort is { Length: > 0 } ? $"/{effort}" : "") + $" — lanes {string.Join(", ", started)}";
    }

    /// <summary>
    /// A turn ended (§5). The row is already in the store and already on screen; this only
    /// ever fills in a shorter rendering of it, so every failure path below simply leaves
    /// the operator reading the agent's own words — which is the current behaviour, and
    /// therefore a safe floor. Nothing here is ever awaited by the wire pump.
    /// </summary>
    void CompressResult(long laneId, long paneEventId, string body)
    {
        if (!Compression.WorthCompressing(body)) return;

        var pool = _store.LanesAll()
            .Where(l => l.Role == "compressor" && l.State == "alive" && _lanes.ContainsKey(l.Id))
            .ToList();
        if (pool.Count == 0) return;                  // no pool warm: the full text stands

        var pick = pool[(int)((uint)Interlocked.Increment(ref _compressorNext) % pool.Count)];
        if (!_compressorLocks.TryGetValue(pick.Id, out var gate)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? reply;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await gate.WaitAsync();
                try { reply = await _lanes[pick.Id].AskAsync(body, 25000); }
                finally { gate.Release(); }
                if (reply is null) { _store.Event("compressor_timeout", pick.Id, $"lane={laneId} row={paneEventId}"); return; }

                var open = reply.IndexOf('{');
                var close = reply.LastIndexOf('}');
                if (open < 0 || close <= open) { _store.Event("compressor_failed", pick.Id, $"no json in reply: {Truncate(reply, 120)}"); return; }
                using var d = JsonDocument.Parse(reply[open..(close + 1)]);
                var headline = d.RootElement.TryGetProperty("headline", out var h) ? h.GetString() ?? "" : "";
                if (headline.Trim().Length == 0) { _store.Event("compressor_failed", pick.Id, "empty headline"); return; }

                var needsYou = d.RootElement.TryGetProperty("needs_you", out var ny) && ny.ValueKind == JsonValueKind.True;
                var options = new List<string>();
                if (d.RootElement.TryGetProperty("options", out var op) && op.ValueKind == JsonValueKind.Array)
                    options.AddRange(op.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3));

                var text = Compression.Render(headline, needsYou, options);

                _store.PaneCompressed(paneEventId, text);
                _store.Event("compressed", pick.Id,
                    $"{sw.ElapsedMilliseconds}ms lane={laneId} row={paneEventId} {body.Length}->{text.Length} chars needs_you={needsYou}");
            }
            catch (Exception ex) { _store.Event("compressor_failed", pick.Id, ex.Message); }
        });
    }

    /// <summary>
    /// The two decisions <see cref="CompressResult"/> makes that are not I/O — lifted out so
    /// they can be asked on the ~1 second `unit` loop instead of behind a warm pool of two real
    /// `claude -p` processes (`docs/testarch/survey-daemon.md` blocker 2, which names both by
    /// these names). Nothing about WHICH compressor is picked, when it is asked, or what happens
    /// to its answer moved: this is the length test and the rendering, and both are byte-for-byte
    /// what they were inline.
    /// </summary>
    internal static class Compression
    {
        /// <summary>Already the length a compressor would produce: spending a model call there
        /// would be exactly the no-judgment volume §2.2 says not to buy.</summary>
        internal static bool WorthCompressing(string body) => body.Length > 120 || body.Contains('\n');

        /// <summary>
        /// The fixed shape from §5. The lane's name is NOT repeated here the way the design
        /// sketch shows it: in a pane the row already sits under that lane's own coloured
        /// header, and in the feed the title is already the first thing on the row. Printing it
        /// a third time is noise, not structure.
        /// </summary>
        internal static string Render(string headline, bool needsYou, IReadOnlyList<string> options)
        {
            var flat = headline.Trim().ReplaceLineEndings(" ");
            // A model that already opened with the word would otherwise render
            // "BLOCKED — BLOCKED ..." — the prefix is structure, so it is added exactly
            // once and never echoed.
            if (flat.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase))
                flat = flat[7..].TrimStart(' ', ':', '-', '—');
            var text = new StringBuilder();
            if (needsYou) text.Append("BLOCKED — ");
            text.Append(Truncate(flat, 90));
            if (needsYou && options.Count > 0) text.Append("\n   options: ").Append(string.Join(" / ", options));
            return text.ToString();
        }
    }

    // ------------------------------------------------- the completion record (R4, D-R8/D-R13)

    /// <summary>One lock per TICKET, held across "read the last record, decide, write the next
    /// one". `OnResult` fires on a lane's wire-pump thread, so two turns of one ticket can arrive
    /// concurrently -- and without this both would read the same previous digest, both would
    /// decide the worktree had changed, and D-R13's whole point (one record, not one per turn)
    /// would fail exactly when a lane is busiest. Concurrent for the same reason `_lanes` had to
    /// become concurrent in R3.5: it is written from background threads while the control pipe
    /// reads the store beside it.</summary>
    readonly ConcurrentDictionary<long, object> _recordLocks = new();

}
