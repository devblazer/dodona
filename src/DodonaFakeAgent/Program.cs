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
//   tool:Name:arg — a tool_use event with `file_path` = arg. EVERY occurrence, in order, so a
//                   turn can be many calls — which is what mid-turn progress rows are about
//                   (one call was enough for presence, a single column; a fold is not).
//   bash:<cmd>    — a tool_use whose input is a `command`, the other shape Progress reads
//   toolfail:<m>  — a FAILED tool_result for the last tool_use, as a `user` event with
//                   is_error: the half of the wire the pane showed nothing of at all
//   think:N       — N `system/thinking_tokens` events, emitted AFTER the tools, the way a
//                   real agent floods the wire while reasoning (93 of one turn's 111 lines)
//   env:NAME      — the result event carries the value of environment variable NAME, or
//                   `(unset)`. This is how a check sees what the SPAWN SITE actually put in
//                   an agent's environment (Phase 0c, DODONA_WORKSPACE).
//   cwd           — the result event carries this process's OWN working directory. How a check
//                   sees WHERE an agent really is, rather than where the store says it was
//                   meant to be (LOCATIONS-PLAN Phase 2).
//   brief         — the result event carries the LANE BRIEFING this turn arrived with, or
//                   `(none)`. The only witness to what was really DELIVERED, which is the
//                   divergence B2 is: the pane records the operator's words, the agent gets the
//                   briefing as well (docs/LANE-BRIEFING-PLAN.md B2).
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
// router (project question, Phase 3): routeproject:N — the Nth project offered, by INDEX
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

    // THE BRIEFING IS STRIPPED BEFORE ANYTHING ELSE READS THE TEXT, and that is what keeps every
    // other check in every suite unchanged: `working on: {text}` and `done: {text}` are what a
    // dozen assertions and the whole compression suite are written against, and a prefix riding
    // in on all of them would have rewritten the meaning of all of them. It is also honest -- a
    // real agent does not echo its own instructions back.
    //
    // `[/DISPATCHER]` must match `Dodona.Briefing.TurnEnd`. There is no shared constant because
    // this project deliberately references nothing; the drift is caught instead by the check that
    // proves the feature (m1 `the_briefing_reaches_a_ticket_agent`), which reads `(none)` and goes
    // red the moment the two disagree. The closing marker is REQUIRED, so `RouteInput`'s misroute
    // retraction -- which is `[DISPATCHER] `-prefixed and has no closing marker -- is left alone.
    const string BriefEnd = "[/DISPATCHER]";
    var brief = "";
    if (text.StartsWith("[DISPATCHER] ", StringComparison.Ordinal))
    {
        var end = text.IndexOf(BriefEnd, StringComparison.Ordinal);
        if (end >= 0)
        {
            brief = text[.. end].Trim();
            text = text[(end + BriefEnd.Length) ..].TrimStart('\r', '\n');
        }
    }

    if (asRouter)
    {
        // TWO QUESTIONS REACH ONE WARM ROUTER (LOCATIONS-PLAN Phase 3). The second is "which
        // PROJECT does a new lane for this open in", asked only when several projects hold live
        // lanes, and it wants its own schema. Recognised by the question's own first line --
        // the same way the concierge's review-behind and brain-hi's escalation are, and for the
        // same reason: a directive in the OPERATOR'S text cannot distinguish them, because both
        // questions carry that same text.
        //   routeproject:N     — pick the Nth project (1-based) out of the list the daemon put
        //                        in the question. AN INDEX, NOT A NAME, and that is the same
        //                        lesson `cxpick:N` carries one level up: a project NAME written
        //                        into the operator's sentence is matched IN CODE by the ladder's
        //                        `named` rung before any model is asked, so a rung-2 test
        //                        written with a name passes at rung 3 having never reached the
        //                        tier at all -- a check proving the opposite of what it claims.
        //   (no directive)     — none/low, so the ladder holds the sentence and asks. That is
        //                        the safe default: a fake agent that said nothing useful must
        //                        never be able to place an agent in a project.
        if (text.Contains("Choose which PROJECT a new lane for this input should open in."))
        {
            string? picked = null;
            var rp = Regex.Match(text, @"routeproject:(\d+)");
            if (rp.Success)
            {
                var offered = Regex.Matches(text, @"(?m)^- (.+)$").Select(m => m.Groups[1].Value.Trim()).ToList();
                var i = int.Parse(rp.Groups[1].Value) - 1;
                if (i >= 0 && i < offered.Count) picked = offered[i];
            }
            Emit(new
            {
                type = "result",
                subtype = "success",
                session_id = sessionId,
                result = JsonSerializer.Serialize(new
                {
                    project = picked ?? "none",
                    confidence = picked is not null && !text.Contains("routeconf:low") ? "high" : "low",
                    reason = "fake router project",
                }),
            });
            continue;
        }
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

        // THE R5 WORK REVIEW (REVIEW-AND-MERGE-PLAN D-R9/D-R12) reaches EITHER tier with its own
        // schema -- the cheap one first, the expensive one only on escalation -- so it is
        // recognised by the question's own first line, the way brain-hi's granularity question
        // and the concierge's review-behind are. A directive in the operator's text could not
        // tell them apart: the completion record quotes the agent's report back verbatim, and
        // that report is where these directives ride in.
        //   mgrverdict:ok|send-back|approve -- the verdict. `approve` is deliberately reachable:
        //       D-R10 says the manager may block and may not bless, so a check has to be able to
        //       ASK for a blessing and watch it grant nothing.
        //   mgrmsg:<TOKEN>  -- the send-back message. ONE token on purpose: a multi-word message
        //       would run into the rest of the question's line and the check could not name what
        //       it expects to find in the lane's pane.
        //   mgrlow          -- confidence low on the cheap tier only, which forces the escalation
        //       (the hi tier is always sure, like `brainlow` above).
        //   mgrneed:<path>  -- R8/D-R23: ask to READ that file. It rides in on the report like
        //       the rest, so it is present on BOTH passes of a review -- which is the point:
        //       the daemon must ignore the second one.
        //   mgrneed2:<path> -- what to ask for ON the details pass, and it is what makes
        //       "bounded and once" PROVABLE rather than merely untimed. A details round that
        //       ran twice would put a second, different name in the review row's `details`; a
        //       check that only waited for a hang could not tell a loop from a slow model.
        if (text.Contains("Review the completed work on a ticket"))
        {
            var mv = Regex.Match(text, @"mgrverdict:([\w-]+)");
            var mm = Regex.Match(text, @"mgrmsg:(\S+)");
            // The DAEMON'S OWN header, which no directive riding in on the report can forge, so
            // this is a true reading of which pass we are on.
            var saw = text.Contains("You asked to see these files");
            var mn = Regex.Match(text, saw ? @"mgrneed2:(\S+)" : @"mgrneed:(\S+)");
            // Proof the CONTENT arrived and not merely the name: the test writes this token
            // inside the file it expects to be granted, and it appears nowhere else in the
            // question -- not in the diffstat, which is names and counts, not in the changed
            // list, and not in the agent's report.
            var tok = Regex.Match(text, @"detailtoken:(\S+)");
            Emit(new
            {
                type = "result",
                subtype = "success",
                session_id = sessionId,
                result = JsonSerializer.Serialize(new
                {
                    verdict = mv.Success ? mv.Groups[1].Value : "ok",
                    confidence = text.Contains("mgrlow") && !isHi ? "low" : "high",
                    // THE NOTE CARRIES THE DIRECTIVE'S OWN TOKEN (R6). `note` is the field
                    // D-R11 calls the point of the whole review -- it is written for the
                    // OPERATOR and R6 renders it in the approval ask -- so a fixed string
                    // here would let a check assert that "a note" arrived while proving
                    // nothing about WHICH review it came from. With the token in it,
                    // `brain:the_managers_write_up_reaches_the_operators_approval_ask` names
                    // exactly the round it expects to be reading.
                    note = $"fake manager on the {(isHi ? "hi" : "lo")} tier" + (mm.Success ? $": {mm.Groups[1].Value}" : "")
                           + (saw && tok.Success ? $" read:{tok.Groups[1].Value}" : ""),
                    message = mm.Success ? mm.Groups[1].Value : "",
                    need = mn.Success ? new[] { mn.Groups[1].Value } : Array.Empty<string>(),
                    needWhy = mn.Success ? "the fake manager was told to ask for this one" : "",
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

    // tool:Name:arg — emit a claude-shaped tool_use event (drives presence derivation AND
    // the mid-turn progress rows, Progress.cs). EVERY occurrence, in order: one call was
    // enough to test presence, which is a single column, but a progress row exists
    // precisely because a turn is MANY calls — the fold, the dedupe of a repeated subject
    // and "the operator can still see the sixteenth step" are all unreachable with one.
    // The id is per-occurrence so a `toolfail:` below can name the tool it answers.
    var toolIds = new List<(string Id, string Name)>();
    foreach (Match tool in Regex.Matches(text, @"tool:(\w+):(\S+)"))
    {
        var id = $"fake-tool-{toolIds.Count + 1}";
        toolIds.Add((id, tool.Groups[1].Value));
        Emit(new
        {
            type = "assistant",
            session_id = sessionId,
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id, name = tool.Groups[1].Value, input = new { file_path = tool.Groups[2].Value } } },
            },
        });
    }

    // bash:<command...> — a tool_use whose input is a `command`, not a `file_path`. The
    // two shapes take different branches in Progress.FromTool (a file is named by its leaf,
    // a command is shown as it will run), and a directive that could only produce one of
    // them left the other testable only by reading the code.
    var bash = Regex.Match(text, @"bash:(.+?)(?:\s+(?:tool|toolfail|say|sleep|ratelimit):|$)");
    if (bash.Success)
    {
        var id = $"fake-tool-{toolIds.Count + 1}";
        toolIds.Add((id, "Bash"));
        Emit(new
        {
            type = "assistant",
            session_id = sessionId,
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id, name = "Bash", input = new { command = bash.Groups[1].Value.Trim() } } },
            },
        });
    }

    // toolfail:<message> — the FAILED tool_result for the last tool_use emitted above,
    // shaped as the wire carries it: a `user` event with is_error. This is the half of the
    // stream the pane never showed at all, so without this directive the only way to see a
    // failing mid-turn tool was to watch a real agent get something wrong.
    var fail = Regex.Match(text, @"toolfail:(.+?)(?:\s+(?:tool|bash|say|sleep|ratelimit):|$)");
    if (fail.Success)
        Emit(new
        {
            type = "user",
            session_id = sessionId,
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "tool_result",
                        tool_use_id = toolIds.Count > 0 ? toolIds[^1].Id : "fake-tool-unknown",
                        is_error = true,
                        content = fail.Groups[1].Value.Trim(),
                    },
                },
            },
        });

    // think:N — emit N `system/thinking_tokens` events, the shape a real agent floods the
    // wire with while it reasons (measured: 93 of one turn's 111 lines). Emitted AFTER the tool
    // events above, which is both the real order (an agent reasons about what it just read)
    // and the one that can be OBSERVED: it makes `thinking…` the last presence before the
    // turn's sleep, where a check can still see whether the daemon left a stale tool there.
    var think = Regex.Match(text, @"think:(\d+)");
    if (think.Success)
        for (var i = 1; i <= int.Parse(think.Groups[1].Value); i++)
            Emit(new
            {
                type = "system",
                subtype = "thinking_tokens",
                estimated_tokens = i * 50,
                estimated_tokens_delta = 50,
                session_id = sessionId,
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

    // env:NAME — the result IS that environment variable's value, or `(unset)`.
    //
    // Not a convenience. The daemon puts three variables into every lane it spawns
    // (DODONA_SHIM_INFO, DODONA_LANE_ROLE and, since Phase 0c, DODONA_WORKSPACE), and until
    // this directive existed no check could see what an agent inside a lane actually
    // inherited — a spawn-site environment was observable only by reading the code that set
    // it. The whole of Phase 0c is about that environment being right: without
    // DODONA_WORKSPACE an agent's own `dodona` command had nothing to resolve by except the
    // folder its process happened to start in, and that fallback CREATED a workspace.
    //
    // Checked BEFORE `say`, because `say env:X` matches both patterns.
    var envq = Regex.Match(text, @"env:(\w+)");
    // cwd — the result IS this process's own working directory.
    //
    // The same reasoning as env:NAME, one level deeper, and it is what makes
    // docs/LOCATIONS-PLAN.md Phase 2 checkable at all. `lanes.cwd` is written BEFORE
    // Process.Start, so a recorded project proves only that the daemon INTENDED a folder — and
    // "the lane looks placed while the process is somewhere else" is precisely this phase's
    // failure mode (trap T1: a prompt naming a folder the process is not in). The daemon sets
    // the SHIM's WorkingDirectory and the shim hands its own cwd to the child, so this is the
    // OS's answer about the agent itself, at the far end of that chain.
    var cwdq = Regex.IsMatch(text.Trim(), @"^cwd$");
    // brief — the result IS the briefing this turn arrived with. Same reasoning as env:NAME one
    // step further out: what the daemon PREFIXED is observable nowhere else, because the pane
    // deliberately does not record it and the wire is inside the shim.
    var briefq = Regex.IsMatch(text.Trim(), @"^brief$");
    var say = Regex.Match(text, @"say\s+(.+)$");
    Emit(new
    {
        type = "result",
        subtype = "success",
        session_id = sessionId,
        result = briefq ? (brief.Length > 0 ? brief : "(none)")
               : cwdq ? Environment.CurrentDirectory
               : envq.Success ? (Environment.GetEnvironmentVariable(envq.Groups[1].Value) ?? "(unset)")
               : say.Success ? say.Groups[1].Value.Trim()
               : $"done: {text}",
    });
}
