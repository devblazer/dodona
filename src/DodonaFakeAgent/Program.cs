using System.Text.Json;
using System.Text.RegularExpressions;

// DodonaFakeAgent — a scripted stand-in for `claude -p --input-format stream-json
// --output-format stream-json`. Speaks just enough of the wire shape (observed in
// spike 1) for the daemon: system/init with a session id, an assistant event per
// turn, a result event per turn.
//
// Directives inside the user text make turns deterministic and schedulable:
//   sleep:N       — wait N seconds mid-turn (a long "thinking" turn to kill daemons under)
//   say <text>    — the result event carries exactly <text>
//
// DODONA_LANE_ROLE=compressor makes it answer in the compressor's fixed JSON schema (§5)
// instead, deterministically shortening whatever it was given. That keeps selective
// compression testable end-to-end with zero model calls, like every other suite.

var sessionId = $"fake-{Guid.NewGuid():N}";
var asCompressor = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "compressor";
// brain / brain-hi: deterministic management judgement, driven by directives embedded in
// whatever text reaches it (the operator input is quoted inside the brain's question):
//   brainname:X   — disagree, better_name X      brainticket:T — suggest ticket T
//   brainlow      — answer with confidence low (forces the escalation path)
// brain-hi also answers the ESCALATED lane-granularity question (§5), driven by:
//   hikind:new-task|addendum|generic   hitarget:LANE   hiconf:low
// With no hikind at all it answers unclear/low, so the ladder walks to its top rung and asks
// the operator -- which is the branch a test most needs to be able to reach.
var asBrain = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE")?.StartsWith("brain") == true;
var brainIsHi = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "brain-hi";
// router: routekind:generic|addendum|new-task|unclear, routetarget:X, routereason:direct|tweak,
// routeconf:low — so the full escalation chain (classifier → brain-hi → ask the operator) runs
// model-free. Default is `unclear`, deliberately: an unclear verdict delivers NOTHING, so a
// fake agent with no directive can never cause a silent wrong delivery inside a test.
var asRouter = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") == "router";
// concierge-lo / concierge-hi: the GROUP-scope ladder (WORKSPACES-CONCIERGE.md §2/§4).
// Directives, embedded in whatever text reaches the tier (the operator's sentence is quoted
// inside the concierge's question), make every rung deterministic:
//   cxpick:N      — the cheap tier picks the Nth workspace from the list it was HANDED
//                   (1-based). Prefer this for rung-2 tests: cxws:NAME spells a workspace
//                   name into the sentence, and rung 1 matches names in code, so the test
//                   would pass at rung 1 and never reach the tier.
//   cxws:NAME     — the cheap tier is confident it is workspace NAME       (rung 2 hit)
//   cxguess:NAME  — the cheap tier offers candidate_name NAME, unconfident (rung 2 miss)
//   cxlow         — answer confidence low, whatever else was asked         (forces escalation)
//   cxfolder:NAME — the EXPENSIVE tier picks fence candidate NAME          (rung 3 hit)
//   cxdisagree:NAME — the review-behind says the workspace was wrong       (§2.3)
// With no directive at all both tiers answer "none"/low, which walks the ladder to rung 4 —
// asking the operator. That is the honest default: a fake agent that guessed would hide the
// one path most worth testing.
var conciergeRole = Environment.GetEnvironmentVariable("DODONA_LANE_ROLE") ?? "";
var asConcierge = conciergeRole.StartsWith("concierge");
var conciergeHi = conciergeRole == "concierge-hi";
var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
void Emit(object o) => stdout.WriteLine(JsonSerializer.Serialize(o));

Emit(new { type = "system", subtype = "init", session_id = sessionId, model = "fake-agent" });

string? line;
while ((line = Console.ReadLine()) is not null)
{
    string text;
    try
    {
        using var d = JsonDocument.Parse(line);
        text = d.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
    catch { continue; }

    if (asRouter)
    {
        // Four verdicts (WORKSPACES-CONCIERGE.md §5): generic | addendum | new-task | unclear.
        //   routekind:new-task     — a distinct task; the daemon spawns a lane and delivers
        //   routekind:addendum + routetarget:LANE  — continues that lane
        //   routereason:tweak      — the addendum reason (direct | tweak)
        //   routeconf:low          — force the escalation path
        // The default is deliberately `unclear`, NOT `generic`: an unclear verdict delivers
        // nothing and asks, so a fake agent that said nothing useful can never cause a silent
        // wrong delivery in a test. The old default of `generic` would have.
        var rk = Regex.Match(text, @"routekind:([\w-]+)");
        var rt2 = Regex.Match(text, @"routetarget:(\w+)");
        var rr = Regex.Match(text, @"routereason:(\w+)");
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                kind = rk.Success ? rk.Groups[1].Value : "unclear",
                target = rt2.Success ? rt2.Groups[1].Value : (string?)null,
                confidence = text.Contains("routeconf:low") ? "low" : "high",
                reason = rr.Success ? rr.Groups[1].Value : "fake router",
                cleaned_text = text,
            }),
        });
        continue;
    }

    if (asConcierge)
    {
        var low = text.Contains("cxlow");
        var disagree = Regex.Match(text, @"cxdisagree:([\w.-]+)");
        // The review-behind (§2.3) asks a different question of the same cheap tier, and it
        // is recognisable by its schema: it wants agree/workspace, not workspace/candidate.
        var isReview = text.Contains("Was that the right WORKSPACE?");
        if (isReview)
        {
            Emit(new
            {
                type = "result",
                subtype = "success",
                session_id = sessionId,
                result = JsonSerializer.Serialize(new
                {
                    agree = !disagree.Success,
                    workspace = disagree.Success ? disagree.Groups[1].Value : null,
                    confidence = low ? "low" : "high",
                    reason = "fake concierge",
                }),
            });
            continue;
        }
        if (conciergeHi)
        {
            var folder = Regex.Match(text, @"cxfolder:([\w.~-]+)");
            Emit(new
            {
                type = "result",
                subtype = "success",
                session_id = sessionId,
                result = JsonSerializer.Serialize(new
                {
                    folder = folder.Success ? folder.Groups[1].Value : "none",
                    confidence = folder.Success && !low ? "high" : "low",
                    reason = "fake concierge hi",
                }),
            });
            continue;
        }
        var ws = Regex.Match(text, @"cxws:([\w.~/-]+)");
        var guess = Regex.Match(text, @"cxguess:([\w.~-]+)");

        // cxpick:N picks the Nth workspace (1-based) out of the list the concierge put in
        // the question, instead of naming it. That is not a convenience: `cxws:lighthouse`
        // spells a workspace NAME into the operator's sentence, and rung 1 matches names in
        // the sentence in code — so a fuzzy-rung test written with cxws: silently passes at
        // rung 1 and never exercises the tier at all. Picking by index keeps every workspace
        // name out of the text, which is also closer to what a real model does: read the
        // list it was handed and choose from it.
        var pick = Regex.Match(text, @"cxpick:(\d+)");
        string? picked = null;
        if (pick.Success)
        {
            var listed = Regex.Match(text, @"Workspaces: \[(.*?)\]");
            if (listed.Success)
            {
                var options = Regex.Matches(listed.Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
                var i = int.Parse(pick.Groups[1].Value) - 1;
                if (i >= 0 && i < options.Count) picked = options[i];
            }
        }

        var answer = picked ?? (ws.Success ? ws.Groups[1].Value : null);
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                workspace = answer ?? "none",
                confidence = answer is not null && !low ? "high" : "low",
                candidate_name = guess.Success ? guess.Groups[1].Value : null,
            }),
        });
        continue;
    }

    if (asBrain)
    {
        var isHi = brainIsHi;

        // The escalated lane-granularity question (§5) reaches brain-hi with the four-verdict
        // schema, not the review schema. Recognised by its own words, the same way the
        // concierge's review question is.
        if (isHi && text.Contains("Decide where one line of operator input belongs"))
        {
            var hk = Regex.Match(text, @"hikind:([\w-]+)");
            var ht = Regex.Match(text, @"hitarget:(\w+)");
            Emit(new
            {
                type = "result",
                subtype = "success",
                session_id = sessionId,
                result = JsonSerializer.Serialize(new
                {
                    kind = hk.Success ? hk.Groups[1].Value : "unclear",
                    target = ht.Success ? ht.Groups[1].Value : (string?)null,
                    // No directive means genuinely unsure, so the ladder reaches its top rung
                    // and asks the operator -- the path most worth testing.
                    confidence = hk.Success && !text.Contains("hiconf:low") ? "high" : "low",
                    reason = "fake brain-hi",
                }),
            });
            continue;
        }

        var name = Regex.Match(text, @"brainname:(\w+)");
        var tick = Regex.Match(text, @"brainticket:(\w+)");
        var low = text.Contains("brainlow") && !isHi;      // the hi tier is always sure
        object? ticket = tick.Success ? new { title = tick.Groups[1].Value, claims = new[] { "subtree:src" } } : null;
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                agree = !(name.Success || tick.Success),
                confidence = low ? "low" : "high",
                better_name = name.Success ? name.Groups[1].Value : null,
                ticket,
                target = Regex.Match(text, @"braintarget:(\w+)") is { Success: true } m2 ? m2.Groups[1].Value : null,
                reason = "fake brain",
            }),
        });
        continue;
    }

    if (asCompressor)
    {
        // Deterministic stand-in for the judgement: first 60 characters, one line, and
        // needs_you only when the text says so — enough shape for a test to assert on.
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        var needs = flat.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase);
        Emit(new
        {
            type = "result",
            subtype = "success",
            session_id = sessionId,
            result = JsonSerializer.Serialize(new
            {
                headline = flat.Length > 60 ? flat[..60].TrimEnd() : flat,
                needs_you = needs,
                options = needs ? new[] { "wait", "override" } : Array.Empty<string>(),
            }),
        });
        continue;
    }

    Emit(new
    {
        type = "assistant",
        session_id = sessionId,
        message = new { role = "assistant", content = new object[] { new { type = "text", text = $"working on: {text}" } } },
    });

    // tool:Name:arg — emit a claude-shaped tool_use event (drives presence derivation)
    var tool = Regex.Match(text, @"tool:(\w+):(\S+)");
    if (tool.Success)
        Emit(new
        {
            type = "assistant",
            session_id = sessionId,
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id = "fake-tool-1", name = tool.Groups[1].Value, input = new { file_path = tool.Groups[2].Value } } },
            },
        });

    // ratelimit:0.42 — emit the CLI's rate_limit_event shape (observed live 2026-08-17),
    // so the quota indicator is testable without a real session ever being consulted.
    var rl = Regex.Match(text, @"ratelimit:([\d.]+)");
    if (rl.Success)
        Emit(new
        {
            type = "rate_limit_event",
            session_id = sessionId,
            rate_limit_info = new
            {
                status = "allowed_warning",
                resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
                rateLimitType = "five_hour",
                utilization = double.Parse(rl.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                isUsingOverage = false,
            },
        });

    var sleep = Regex.Match(text, @"sleep:(\d+)");
    if (sleep.Success) Thread.Sleep(int.Parse(sleep.Groups[1].Value) * 1000);

    var say = Regex.Match(text, @"say\s+(.+)$");
    Emit(new
    {
        type = "result",
        subtype = "success",
        session_id = sessionId,
        result = say.Success ? say.Groups[1].Value.Trim() : $"done: {text}",
    });
}
