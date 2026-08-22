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
    /// THE ENTIRE DAEMON COMMAND SURFACE — 45 `case` labels — and its only dependencies are a
    /// JSON string in and a `StreamWriter` out. The pipe server that calls it is the nine lines
    /// directly above.
    ///
    /// INTERNAL FOR THE TEST ASSEMBLY (seam S3, `docs/TEST-ARCHITECTURE-PLAN.md` §4/W8, and
    /// D-T11 in that plan's §3.5). A `StreamWriter` over a `MemoryStream` substitutes for the
    /// pipe **with no fake at all** — it is this handler with nine lines of pipe server not
    /// present, which is why the plan deletes that transport rather than doubling it: *a
    /// transport you can leave out beats a transport you fake.* One keyword, no refactor, no
    /// interface, and nothing about what any command DOES is changed by it.
    /// </summary>
    internal async Task<bool> HandleAsync(string req, StreamWriter w)
    {
        using var d = JsonDocument.Parse(req);
        var e = d.RootElement;
        switch (e.GetProperty("cmd").GetString())
        {
            // ---------------- lanes (M0) ----------------
            case "lane-start":
            {
                var title = e.GetProperty("title").GetString()!;
                var child = e.TryGetProperty("child", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
                var childArgs = e.TryGetProperty("childArgs", out var ca) && ca.ValueKind == JsonValueKind.Array
                    ? ca.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();

                // WHICH PROJECT (P2.1). `--project` names one; nothing named is the first
                // project, which is byte-for-byte what this site did before. Resolved BEFORE the
                // lane row is created, so a refusal leaves no row behind -- a half-born lane
                // pointing at a folder we just refused is worse than the refusal.
                if (!TryProject(One(e, "project"), out var laneProject, out var laneRefusal))
                { w.WriteLine(laneRefusal!); w.WriteLine("##exit 1"); break; }

                // No --child means the real thing. A lane with no ticket has no CLAIM -- but it is
                // no longer UNGATED, and this comment used to say it was: layer 1 (P1 of
                // docs/WORK-ISOLATION-PLAN.md) deploys the write gate to every work lane in
                // `AttachShimAsync`, so a plain lane can read anywhere and still cannot write
                // into a project outside a worktree. What a ticket adds is the claim, which
                // bounds it against OTHER lanes; what layer 1 adds is that the shared checkout
                // is nobody's workspace. The T7 expansion noted here is closed by that.
                var lcfg = ConfigForProject(laneProject);
                if (child is null)
                {
                    w.WriteLine((await SpawnAgentLaneAsync(title, laneProject, Pick(e, "model", lcfg.Model), Pick(e, "effort", lcfg.Effort))).Msg);
                    break;
                }
                // A --child lane is configured by its project too, and records it for the same
                // reason -- `--child` chooses the BINARY, not the permissions. It is also the only
                // spawn a suite can drive model-free, so without this the T2 fix would have no
                // observable surface at all in any acceptance suite (IsClaude is false for the
                // fake agent, so no claude argv is ever built for it to be read back from).
                var lr = await SpawnLaneAsync(title, "work", laneProject, child, childArgs);
                if (lr.Id > 0) RecordLaneConfig(lr.Id, laneProject, lcfg, childArgs);
                w.WriteLine(lr.Msg);
                break;
            }
            case "say":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var text = e.GetProperty("text").GetString()!;
                if (!_lanes.TryGetValue(lane, out var rt)) { w.WriteLine($"error: lane {lane} not connected"); break; }
                rt.Say(text);
                w.WriteLine($"-> lane {lane}");
                break;
            }
            case "lane-rename":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var title = e.GetProperty("title").GetString()!.Trim().ToUpperInvariant();
                if (title.Length == 0 || title.Contains(' ')) { w.WriteLine("error: one word"); break; }
                var old = _store.LanesAll().FirstOrDefault(l => l.Id == lane)?.Title;
                if (old is null) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneTitle(lane, title);
                _store.Event("lane_renamed", lane, $"{old} → {title} (operator)");
                _store.PaneEvent(lane, "announcement", $"renamed to {title} (was {old})", null, null, acked: true);
                w.WriteLine($"renamed lane {lane}: {old} → {title}");
                break;
            }
            case "lane-respawn":
            {
                // Agents are fungible; the lane is the thread (§11). A dormant lane (its
                // ticket landed) or an unreachable one (its shim died) comes back as a
                // fresh process resuming the recorded session — spike 1 measured that
                // `--resume` restores full context with the same id and no fork. This is
                // what makes retiring agents cheap enough to do automatically.
                var lane = e.GetProperty("lane").GetInt64();
                var row = _store.LanesAll().FirstOrDefault(l => l.Id == lane);
                if (row is null) { w.WriteLine($"error: no lane {lane}"); break; }
                if (_lanes.TryGetValue(lane, out var lrt2) && lrt2.Connected) { w.WriteLine($"lane {lane} is already connected"); break; }

                var args2 = new List<string>();

                // WHERE it comes back, and WHAT it is told it is, were both wrong (M5.1).
                // Respawn hardcoded `_primary` and always rebuilt the PLAIN-lane prompt, so a
                // resumed TICKET agent ran in the operator's live working copy while being
                // told "your worktree is the current working directory; work only there" — a
                // gated agent, resumed, editing main's tree. The ticket is the authority on
                // both answers; the recorded cwd covers every other kind of lane.
                var t2 = _store.Tickets().FirstOrDefault(t => t.LaneId == lane && t.State == "open");

                // RE-HOMING (P2.6): `--project` is the operator's answer to the refusal below.
                // It is validated exactly like a fresh spawn, so re-homing cannot land a lane
                // somewhere a fresh one could not have opened.
                if (!TryProject(One(e, "project"), out var reProject, out var reRefusal))
                { w.WriteLine(reRefusal!); w.WriteLine("##exit 1"); break; }
                var reHomed = One(e, "project") is not null;
                // A TICKET lane cannot be re-homed: its claim gate is deployed into its worktree
                // and its prompt says "work only there", so moving the process out of the
                // worktree is precisely the M5.1 incident performed on purpose.
                if (reHomed && t2 is not null)
                {
                    w.WriteLine($"refused: lane {lane} works ticket {t2.Id}, so its directory is that ticket's worktree " +
                                "-- a ticket lane cannot be re-homed. Land or abandon the ticket instead.");
                    w.WriteLine("##exit 1");
                    break;
                }

                // The rung ORDER lives in ResolveLaneCwd, on the unit loop (P1.3). What stays
                // here is the I/O: a directory that has been deleted is not a candidate, and
                // ruling it out is this site's business, not a pure function's.
                var cwd2 = ResolveLaneCwd(
                    reHomed ? reProject
                    : t2?.Worktree is { Length: > 0 } twt && Directory.Exists(twt) ? twt : null,
                    row.Cwd is { Length: > 0 } rcwd && Directory.Exists(rcwd) ? rcwd : null,
                    _primary);

                // TRAP T4, REFUSED RATHER THAN RE-OPENED. `workspace-detach` and
                // `workspace-move` change nothing about a lane row, and the only test this site
                // ever applied was `Directory.Exists` -- which PASSES, because the folder is
                // still there; it just belongs to another workspace now. Respawning into it puts
                // an ungated agent (T7) into somebody else's repository, holding somebody else's
                // merge token's tree. Re-homing to the first project instead would be worse in
                // the other direction: an agent whose entire conversation is about project B,
                // silently editing project A. So: refuse, and name the two commands that
                // un-stick it (CLAUDE.md §0.1 -- a wait or a refusal must name the condition).
                if (!Projects.IsOwned(ProjectPaths(), cwd2, NeutralCwd()))
                {
                    w.WriteLine($"refused: lane {lane}'s directory {cwd2} belongs to no project of workspace {_wsName} " +
                                "-- it was detached or moved while this lane existed. " +
                                $"Bring it back with `dodona workspace-attach --member {cwd2}`, " +
                                $"or re-home the lane with `dodona lane-respawn {lane} --project <project>`.");
                    _store.Event("lane_respawn_refused", lane, $"cwd={cwd2} owned=no");
                    w.WriteLine("##exit 1");
                    break;
                }

                // T2 again: a respawned lane is configured by the project it is going back INTO,
                // not by the workspace's first one. This path read `_config` and so could hand a
                // lane in project B project A's permission mode on every respawn.
                var cfg2 = ConfigForProject(Projects.Of(ProjectPaths(), cwd2) ?? _primary);
                var child2 = cfg2.Agent;
                if (IsClaude(child2))
                {
                    var sys2 = t2 is null
                        ? LaneSystemPrompt(row.Title, cwd2)
                        : TicketSystemPrompt(t2.Id, t2.Title, t2.Branch, TicketIsPr(t2));
                    args2 = ClaudeArgs(cfg2, cfg2.Model, cfg2.Effort, sys2, acceptEdits: true);
                    args2.AddRange(Projects.ResumeArgs(row.Session));
                }
                // The pipe name is deterministic per lane, and the old shim is gone —
                // the name is free to reclaim, which is the whole point of never keying
                // anything to pids (§13).
                var (rid, rmsg) = await RespawnLaneAsync(row.Id, row.Title, args2, child2, cwd2);
                if (rid > 0)
                {
                    _store.LaneState(lane, "alive");
                    _store.LanePresence(lane, "idle");
                    _store.PaneEvent(lane, "announcement",
                        row.Session is { Length: > 0 } ? "agent respawned — session resumed, context intact" : "agent respawned — fresh session", null, null, acked: true);
                    _store.Event("lane_respawned", lane, $"session={row.Session ?? "-"}");
                }
                w.WriteLine(rmsg);
                break;
            }
            case "lane-stop":
            {
                // The undo for an auto-started lane. The shim owns the agent, so stopping
                // is a message to the shim, not a kill: it takes the child down with it
                // and the pane's rows stay exactly where they are (§12 — nothing is
                // deleted, the lane is simply no longer alive).
                var lane = e.GetProperty("lane").GetInt64();
                if (_lanes.TryGetValue(lane, out var srt))
                {
                    srt.Shutdown();
                    _lanes.TryRemove(lane, out _);
                }
                _store.LaneState(lane, "dead");
                if (_store.KvGet("focused_lane") == lane.ToString()) _store.KvSet("focused_lane", "");
                _store.Event("lane_stopped", lane, "operator");
                w.WriteLine($"stopped lane {lane}");

                // D-9: THE UNDO LINE HAS TO BE TRUE. Promotion announces `dodona lane-stop <n>` as
                // the way to undo it, so stopping a lane that was PROMOTED must actually undo the
                // promotion: abandon the ticket, prune the worktree, delete the branch, release the
                // claims (the conflict query only sees `state='open'`, so the state change is the
                // release). An announcement offering an undo that does not undo is worse than no
                // undo at all.
                //
                // ONLY A PROMOTED TICKET, and the distinction is the whole care here. A ticket the
                // operator created deliberately with `ticket-create` is THEIR work; deleting its
                // branch because a lane was stopped would be section 11's "nothing is deleted"
                // violated on their behalf. The promotion event is the only record of which is
                // which, which is why `Store.HasEvent` exists.
                var stopped = _store.Tickets().FirstOrDefault(t => t.State == "open" && t.LaneId == lane);
                if (stopped is not null && _store.HasEvent("lane_promoted", lane, $"ticket {stopped.Id} %"))
                    foreach (var line in AbandonTicket(stopped, $"lane {lane} stopped")) w.WriteLine(line);
                break;
            }
            case "tail":
                foreach (var row in _store.Tail(e.GetProperty("lane").GetInt64(), e.GetProperty("n").GetInt32()))
                    w.WriteLine(row);
                break;
            case "status":
                w.WriteLine($"daemon pid={Environment.ProcessId} build={Ver.Build} schema={Ver.Schema} exe={Ver.ExePath}");
                w.WriteLine($"workspace {_wsName} ({_instanceId})  store={Paths.Store(_instanceId)}");
                w.WriteLine($"members: {string.Join(", ", Members().Select(m => m.Path))}");
                w.WriteLine($"lanes: model={_config.Model} effort={(_config.Effort is { Length: > 0 } ? _config.Effort : "cli default")}  " +
                            // The LANE, not only its config. Printing `router: model=haiku` for a
                            // classifier that had never once been created is how a dead routing
                            // ladder looked healthy for two days.
                            $"router: {(_routerLo > 0 && _lanes.TryGetValue(_routerLo, out var rrt) && rrt.Connected ? $"lane {_routerLo}" : "NOT RUNNING")} " +
                            $"model={_config.RouterModel} effort={(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}  " +
                            $"agent={_config.Agent}");
                // WHICH PROJECT EACH LANE IS IN (P1.2). Until this line `lanes.cwd` had no
                // surface anywhere a person looks: not here, not in `ui dump`, only the
                // `shim_spawned` event detail -- so a lane opening in the wrong project was
                // invisible to the operator and to every check but two. Projects are read ONCE
                // for the whole listing rather than per lane: Members() re-reads the registry
                // on every call, and a status line is not worth N registry opens.
                //
                // Projects.Field returns null for "say nothing", which is what keeps a
                // one-project workspace's output byte-for-byte what it has always been --
                // read its doc comment before changing the shape of this line.
                var projects = Members().Select(m => m.Path).ToList();
                foreach (var l in _store.LanesAll())
                {
                    var connected = _lanes.TryGetValue(l.Id, out var rt) && rt.Connected;
                    var proj = Projects.Field(l.Role, l.Cwd, projects, Paths.NeutralDir);
                    // WHICH PROJECT A MANAGER IS FOR (P5.6). `project=` above reads `lanes.cwd`
                    // and a brain's cwd is the neutral directory on purpose (P5.8), so it is
                    // silent about a brain by design -- which left "one brain per project" with
                    // no surface a person could read at all. Null for "say nothing", including
                    // for every one-project workspace, which is what keeps that output identical.
                    //
                    // IT GOES BEFORE `project=` AND MUST STAY THERE: tests/_workspace.ps1's
                    // Get-StatusProject anchors on `project=(.+?)\s*$`, so a field appended after
                    // it would be captured as part of the project path and five checks in two
                    // suites would start comparing a path against a path-plus-a-field.
                    var scope = Projects.ScopeField(l.Role, l.Project, projects);
                    w.WriteLine($"lane {l.Id}  {l.Title,-10}  role={l.Role,-6}  state={l.State}  connected={connected}  presence={l.Presence,-16}  session={l.Session ?? "-"}" +
                                (scope is null ? "" : $"  scope={scope}") +
                                (proj is null ? "" : $"  project={proj}"));
                }
                break;

            // ---------------- routing (M2, §4) ----------------
            case "focus":
            {
                var lane = e.GetProperty("lane").GetInt64();
                _store.KvSet("focused_lane", lane.ToString());
                w.WriteLine($"focused lane {lane}");
                break;
            }
            case "lane-collapse":
            {
                // A view choice, but a durable one, so it goes through the daemon like every
                // other write (m3: the UI owns nothing). Collapsing NEVER touches the lane's
                // life — no agent stops, no slot frees, nothing is demoted. It only says how
                // much room you want it to take, which is why LANE-LIFECYCLE §2's rejection of
                // slot-pressure eviction is untouched by it: this is the operator's hand, not
                // the system reclaiming space.
                var lane = e.GetProperty("lane").GetInt64();
                var on = !e.TryGetProperty("collapsed", out var cv) || cv.ValueKind != JsonValueKind.False;
                if (_store.LanesAll().All(l => l.Id != lane)) { w.WriteLine($"error: no lane {lane}"); break; }
                _store.LaneCollapsed(lane, on);
                _store.Event(on ? "lane_collapsed" : "lane_expanded", lane, "operator");
                w.WriteLine($"{(on ? "collapsed" : "expanded")} lane {lane}");
                break;
            }
            case "input":
            {
                var text = e.GetProperty("text").GetString()!;
                w.WriteLine(await RouteInput(text));
                break;
            }
            case "router-start":
            {
                // By hand: for suites (which set DODONA_NO_AUTOSTART and own every lifetime
                // themselves) and for restarting the classifier after a config change. The
                // ordinary path is EnsureRouterAsync, which creates it at the point of use.
                var child = e.TryGetProperty("child", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()! : _config.Agent;
                w.WriteLine((await SpawnRouterAsync(child, Pick(e, "model", _config.RouterModel), Pick(e, "effort", _config.RouterEffort))).Msg);
                break;
            }
            case "brain-start":
            {
                // For suites (NO_AUTOSTART skips the warm-at-start) and for restarting a
                // brain by hand after changing its config.
                // P5.3: WHICH project's brain. `BrainProject` resolves a subfolder up to its
                // project and falls back to the first one, so the no-argument call is exactly
                // what it always was.
                var bProject = BrainProject(One(e, "project"));
                var lo = await EnsureBrainAsync(hi: false, bProject);
                var wantHi = e.TryGetProperty("hi", out var bh) && bh.ValueKind == JsonValueKind.True;
                var hi2 = wantHi ? await EnsureBrainAsync(hi: true, bProject) : -2;
                // THE REASON IT FAILED, NOT JUST "FAILED" (CLAUDE.md §0.1 -- a silent degrade is
                // a bug, and "FAILED" with no cause is the same thing wearing a word). The cap is
                // the answer a caller can act on, so it is the one named here.
                var capped = BrainLaneCount() >= Math.Max(1, _config.MaxBrains);
                var why = capped ? $"CAPPED (maxBrains={_config.MaxBrains})" : "FAILED";
                w.WriteLine($"brain for {bProject}: cheap tier lane {(lo > 0 ? lo.ToString() : why)}" +
                            (wantHi ? $", expensive tier lane {(hi2 > 0 ? hi2.ToString() : why)}" : ""));
                break;
            }
            case "compressor-start":
            {
                var child = e.TryGetProperty("child", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString()! : _config.Agent;
                var model = Pick(e, "model", _config.CompressorModel);
                var effort = Pick(e, "effort", _config.CompressorEffort);
                // Asked for by hand with the pool configured off, "how many" still has an
                // obvious answer — two, the smallest number that is not a serialization point.
                var count = e.TryGetProperty("count", out var cn) && cn.ValueKind == JsonValueKind.Number ? cn.GetInt32()
                          : _config.Compressors > 0 ? _config.Compressors : 2;
                w.WriteLine(await StartCompressorsAsync(child, model, effort, count));
                break;
            }
            case "ticket-agent":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }
                // T2 FOR A GATED LANE (P2.3). This read `_config` -- the first project's -- while
                // spawning into a worktree that may belong to a different project entirely, and a
                // ticket lane has been able to do that since multi-repo landed. `ConfigFor` was
                // already the right answer here and was already used two lines away for `Main`;
                // the permission mode simply never went through it.
                var tcfg = ConfigFor(t.Repo);
                var child = e.TryGetProperty("child", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString()! : tcfg.Agent;
                var model = Pick(e, "model", tcfg.Model);
                var effort = Pick(e, "effort", tcfg.Effort);
                // The lane-agent framing (§5, spike 3): declare the [DISPATCHER] channel or
                // the model treats mid-turn instructions as a prompt-injection attempt.
                // `tcfg` IS the ticket repository's config (ConfigFor takes a repo name), so it
                // is the right answer for the delivery line as well as for the permission mode.
                var sys = TicketSystemPrompt(tid, t.Title, t.Branch, tcfg.IsPr);
                var args = IsClaude(child) ? ClaudeArgs(tcfg, model, effort, sys, acceptEdits: true) : new List<string>();
                var (laneId, msg) = await SpawnLaneAsync(t.Title, "work", t.Worktree, child, args);
                // Link ticket ↔ lane: "waiting on you: merge" (§8) needs a pane to land in.
                if (laneId > 0)
                {
                    _store.TicketSetLane(tid, laneId);
                    RecordLaneConfig(laneId, Projects.Of(ProjectPaths(), t.Worktree) ?? _primary, tcfg, args);
                }
                w.WriteLine(msg);
                break;
            }

            // ---------------- tickets & claims (M1, §6/§11) ----------------
            case "ticket-create":
            {
                var title = e.GetProperty("title").GetString()!;
                var mode = e.TryGetProperty("mode", out var m) ? m.GetString()! : "on-approval";
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                var claims = new List<(string, string)>();
                foreach (var s in specs)
                {
                    var parsed = Claims.Parse(s);
                    if (parsed is null) { w.WriteLine($"error: bad claim spec '{s}' (use path:|new:|subtree:|symbol:)"); return false; }
                    claims.Add(parsed.Value);
                }
                // A TICKET WITH NO CLAIM IS LEGAL NOW (`REVIEW-AND-MERGE-PLAN.md` D-R5, R3).
                // This used to refuse with "at least one --claim required", which made sense
                // while a claim was a lock: a ticket holding nothing bounded nothing. It is not
                // a lock any more, so requiring one is requiring ceremony — and it is ceremony a
                // spoken sentence cannot supply, which is precisely why layer 2's promotion had
                // to invent a seed claim from whichever path happened to be denied first. That
                // seed is what stranded a promoted agent on its second file. Claims stay
                // available as a deliberate annotation (`--claim`, `claim-extend`); what a
                // branch actually touched is `git diff`, which needs nothing from anybody.
                if (specs.Count > 0 && claims.Count == 0) { w.WriteLine("error: no usable claim in the given specs"); break; }

                // Git is needed HERE — at the first branch and worktree — not at the door.
                // A project can be opened, and lanes can run in it, long before it has a
                // repo; refusing to open would be refusing too early (and for too long).
                var repos = Repositories();
                RepoRef? repo;
                if (e.TryGetProperty("repo", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString() is string rname && rname.Length > 0)
                {
                    repo = Repos.ByName(repos, rname);
                    if (repo is null)
                    {
                        w.WriteLine($"error: no repository '{rname}' in this workspace" +
                                    (repos.Count > 0 ? $" (have: {string.Join(", ", repos.Select(r => r.Name))})" : ""));
                        w.WriteLine("##exit 1");
                        break;
                    }
                    // NAMING THE REPO USED TO SKIP CLAIM VALIDATION ENTIRELY (P0.6). The
                    // inference branch below has always refused claims that span repositories,
                    // because a ticket lands by fast-forwarding ONE main — but `--repo X` went
                    // straight past it, so `--repo tools --claim path:engine/sim.cs` created a
                    // ticket in `tools` holding a claim over `engine`. Everything downstream
                    // then disagreed about which repository it was talking about: the gate
                    // prefixed the claim with `tools/`, the merge backstop diffed `tools`, and
                    // the land fast-forwarded `tools` while the agent edited `engine`.
                    var mismatch = Repos.CheckClaims(repos, repo, claims);
                    if (mismatch is not null)
                    {
                        _store.Event("ticket_repo_unresolved", null, $"'{title}': {mismatch}");
                        w.WriteLine($"error: {mismatch}");
                        w.WriteLine("##exit 1");
                        break;
                    }
                }
                else
                {
                    // Claims are workspace-relative paths, so they already say which
                    // repository this ticket is for — no extra syntax needed.
                    var (inferred, err) = Repos.ForClaims(repos, claims);
                    if (inferred is null)
                    {
                        _store.Event("ticket_repo_unresolved", null, $"'{title}': {err}");
                        w.WriteLine($"error: {err}");
                        // P4.5: ASK, do not instruct. This used to print "(lanes work without
                        // git; only tickets need a repository)" and leave the operator to go and
                        // type `dodona repo-init` — a GUI telling a person to use the CLI, which
                        // is this project's original sin (the same reasoning that turned "undo:
                        // dodona lane-stop 3" in the feed into a button). The refusal still
                        // stands and still costs nothing; what is new is that the missing repo
                        // becomes a QUESTION ROW, which the ask overlay renders and one verb
                        // answers — and which survives the window closing, because a pending
                        // question that evaporated would make asking worse than guessing
                        // (ConciergeStore's class note).
                        if (repos.Count == 0 && !Git.IsRepo(_primary))
                            foreach (var line in AskForRepo(_primary, title)) w.WriteLine($"       {line}");
                        w.WriteLine("##exit 1");
                        break;
                    }
                    repo = inferred;
                }

                // Repo-exclusivity, layer 3 (Registry's doc comment): asked HERE because
                // here is where a merge token first comes into existence for this repo.
                // MATERIALISED BY `MakeTicket`, which layer 2's promotion calls too (P2). It used
                // to be inline here and nowhere else; two implementations of "make a ticket" would
                // drift on exactly the checks that matter -- repo exclusivity and claim conflict.
                var made = MakeTicket(repo, title, mode, claims, specs);
                if (made.Error is not null)
                {
                    foreach (var line in made.Error.Split('\n')) w.WriteLine(line);
                    if (made.Exit1) w.WriteLine("##exit 1");
                    break;
                }
                // A single-repo project never sees the word "repo": there is only one, and
                // naming it would be noise in the ordinary case.
                w.WriteLine($"ticket {made.Id}{RepoTag(repo.Name)} branch {made.Branch} worktree {made.Worktree}");
                // AN OVERLAP IS REPORTED AFTER THE TICKET, NOT INSTEAD OF IT (D-R5, R3). This
                // block used to print `conflict:` lines and exit 1 without creating anything.
                // The ticket is created now and the overlap is said out loud, in that order, so
                // the line a script reads first is still the one that names the ticket.
                if (made.Conflicts.Count > 0)
                {
                    w.WriteLine($"note: overlaps {made.Conflicts.Count} open claim(s) — two agents on one file is");
                    w.WriteLine("      ordinary; duplicated effort is the manager's to raise (D-R5):");
                    foreach (var cf in made.Conflicts) w.WriteLine($"      overlap: {cf}");
                }
                break;
            }
            // ---------------- layer 1: which TREE a write is in (WORK-ISOLATION-PLAN section 3) ----
            //
            // Unconditional, model-free, and asked of EVERY work lane rather than only ticket
            // lanes: no agent writes into a project outside a worktree. The operator's named
            // failure was real work started in the shared checkout, where `.githooks/pre-commit`
            // then refuses the commit -- so today's default destination for load-bearing work is
            // a tree that cannot deliver it, and nothing stopped an agent editing it.
            //
            // IT APPLIES TO TICKET LANES TOO, and that is not belt-and-braces. `claim-check`
            // resolves an absolute path through its repository and project rungs, so a ticket
            // agent writing the ABSOLUTE path of a file its claim covers -- in the operator's
            // live checkout rather than its own worktree -- resolves to the same claim-relative
            // string and is ALLOWED. Found by reading the rungs while implementing this phase;
            // it has been reachable since multi-repo landed. The tree question has to be asked
            // first, and the claim question second.
            case "tree-check":
            {
                var lane = e.GetProperty("lane").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var row = _store.LanesAll().FirstOrDefault(l => l.Id == lane);
                // A relative `file_path` is relative to the AGENT'S working directory, which is
                // the lane's own recorded cwd -- not this daemon's, and not the first project's.
                var baseDir = row?.Cwd is { Length: > 0 } lc && Directory.Exists(lc) ? lc : _primary;
                var full = Path.GetFullPath(path, baseDir);
                var where = Trees.Locate(full, ProjectPaths());
                if (Trees.Allowed(where)) { w.WriteLine($"tree-ok: {where.ToString().ToLowerInvariant()} {full}"); break; }

                // LAYER 2: THE REFUSAL IS A PROMOTION, NOT A WALL (P2).
                //
                // A plain work lane that tried to write here needed a checkout of its own, so it
                // gets one: ticket, worktree, gate, and the same session carried in. Nothing has
                // been written yet, which is the entire reason layer 1 sits at the write attempt
                // rather than at the commit -- afterwards the edits would be in the wrong tree and
                // there is no safe way to move them (`git stash` is repo-global, so two lanes
                // stashing interleave one stack; CLAUDE.md 5.2).
                //
                // Three lanes do NOT get promoted, each for its own reason:
                //   * one that already works a ticket -- it HAS a worktree and should be writing
                //     there. Promoting again would give one lane two, and this is the hole P1
                //     found in `claim-check` (an absolute path inside its own claim), so the
                //     message names the worktree it already owns.
                //   * a management lane -- it runs in the neutral directory and writes nothing.
                //   * a path in no repository of this workspace -- there is nothing to branch.
                var openTicket = _store.Tickets().FirstOrDefault(t => t.State == "open" && t.LaneId == lane);
                var rr = RepoRelOf(full);
                if (row is not null && row.Role == "work" && openTicket is null && rr is not null)
                {
                    var (pmsg, move) = PromoteLane(row, rr.Value.Repo, rr.Value.Rel, full);
                    _store.Event("tree_check_denied", lane, $"{full} -> promotion");
                    w.WriteLine(pmsg);
                    w.WriteLine("##exit 1");
                    // AFTER the reply is on the wire, never before: the move respawns this lane,
                    // and the process it kills is the one currently waiting for this answer.
                    move?.Invoke();
                    break;
                }

                // D-13: A REFUSAL NAMES THE HOLDER. "outside your claim" sends the reader
                // hunting (CLAUDE.md 0.3); the holder is a store read and therefore free.
                var holder = ClaimHolder(full);
                var msg = openTicket is not null
                    ? $"denied: {full} is in the SHARED CHECKOUT, not a worktree. You already have one for " +
                      $"ticket {openTicket.Id}: write this file under {openTicket.Worktree} instead. Editing the " +
                      "shared checkout would put your work in the tree your operator and every other lane are " +
                      "using, and its pre-commit hook refuses commits from there, so it could not be delivered."
                    : $"denied: {full} is in the SHARED CHECKOUT, not a worktree" +
                      (holder is null ? "" : $" -- that path is held by {holder}") +
                      ". The shared checkout is a source of truth, not a workspace: other lanes and " +
                      "your operator are in it, and its pre-commit hook refuses commits from it, so work " +
                      "done here cannot be delivered. Work that changes files needs a ticket worktree of " +
                      "its own.";
                _store.Event("tree_check_denied", lane, $"{full} holder={holder ?? "-"} ticket={openTicket?.Id.ToString() ?? "-"}");
                w.WriteLine(msg);
                w.WriteLine("##exit 1");
                break;
            }
            case "claim-check":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var path = e.GetProperty("path").GetString()!;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }

                // Claims are workspace-relative, but the agent writes inside a worktree of
                // one repository — so a path resolved against the worktree must be put
                // back into workspace terms before it can be matched. For a single-repo
                // project the prefix is empty and this is exactly the old behaviour.
                var ticketRepo = RepoOf(t);
                var prefix = ticketRepo?.ClaimPrefix ?? "";
                var full = Path.GetFullPath(path, t.Worktree).Replace('\\', '/');

                // TRAP T6, FIXED (docs/LOCATIONS-PLAN.md P2.5). The two bases here were the
                // ticket's worktree and THE FIRST PROJECT -- so a write anywhere in a second
                // project resolved to neither, and the gate denied it with "outside the worktree
                // and the project root" while the agent was writing inside a repository the
                // workspace owns. That was already broken before this phase; Phase 2 is what
                // makes it NORMAL, so the latent hole starts firing on every lane that opens
                // outside the first project.
                //
                // The rungs, longest base first so a repo nested under another wins:
                //   1. the ticket's own WORKTREE, carrying the ticket's recorded claim prefix.
                //      First, always: `m3:186-187` and `LaneCwdPrecedenceTests` both pin that a
                //      ticket lane's folder is its worktree, and a worktree lives INSIDE its
                //      project, so without this rung the project rung would swallow it and hand
                //      back `.dodona/wt/t1/...` -- a path no claim can ever cover.
                //   2. any REPOSITORY of the workspace, prefixed with ITS claim name. Claims are
                //      workspace-relative and a repo's name IS its workspace-relative path, so
                //      this is the general form of what rung 3 did by hand.
                //   3. any PROJECT, unprefixed -- kept only because it is exactly what the old
                //      `_primary` base was, and dropping it would change the ordinary
                //      single-project message for a path inside the project but outside every
                //      repo. It cannot produce a false COVER in a multi-project workspace: the
                //      bare relative form it yields can only match a claim with no repo prefix,
                //      and `Repos.Discover` prefixes every repo name the moment a second project
                //      is attached, so no such claim can exist there.
                string? rel = null;
                var bases = new List<(string Dir, string Prefix)> { (t.Worktree, prefix) };
                bases.AddRange(Repositories().OrderByDescending(r => r.Path.Length).Select(r => (r.Path, r.ClaimPrefix)));
                bases.AddRange(ProjectPaths().OrderByDescending(p => p.Length).Select(p => (p, "")));
                foreach (var (baseDir, basePrefix) in bases)
                {
                    if (baseDir.Length == 0) continue;
                    var b = Path.GetFullPath(baseDir).Replace('\\', '/').TrimEnd('/') + "/";
                    if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = basePrefix + full[b.Length..];
                        break;
                    }
                }
                if (rel is null)
                {
                    w.WriteLine($"denied: {path} is outside the worktree and every project of workspace {_wsName}");
                    w.WriteLine("##exit 1");
                    break;
                }
                rel = Claims.Normalize(rel);
                var claims = _store.TicketClaims(tid);
                if (claims.Any(cl => Claims.Covers(cl.Kind, cl.Value, rel)))
                    w.WriteLine($"covered: {rel}");
                else
                {
                    w.WriteLine($"denied: {rel} not covered by ticket {tid} claims [{string.Join(", ", claims.Select(c => $"{c.Kind}:{c.Value}"))}]");
                    w.WriteLine("##exit 1");
                }
                break;
            }
            case "claim-extend":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var specs = e.GetProperty("claims").EnumerateArray().Select(x => x.GetString()!).ToList();
                // THIS USED TO BE `.Where(p => p is not null)` AND NOTHING ELSE (P0.5): every
                // spec it could not parse was dropped in silence and the reply was still
                // "extended ticket N" with exit 0 — so `--claim src/water` (no `path:`) widened
                // nothing while telling the agent it had. All of them unparseable meant an
                // empty list, an insert of nothing, and a success message. `ticket-create` has
                // always refused a bad spec by name; this now does too.
                var claims = new List<(string, string)>();
                foreach (var s in specs)
                {
                    var parsed = Claims.Parse(s);
                    if (parsed is null) { w.WriteLine($"error: bad claim spec '{s}' (use path:|new:|subtree:|symbol:)"); w.WriteLine("##exit 1"); return false; }
                    claims.Add(parsed.Value);
                }
                if (claims.Count == 0) { w.WriteLine("error: at least one --claim required"); w.WriteLine("##exit 1"); break; }

                // AND IT HAD NO REPOSITORY AT ALL. `Store.ClaimExtend` takes a ticket id, so an
                // extension could widen an open ticket into a DIFFERENT repository than the one
                // it lands in — the same hole P0.6 leaves in `ticket-create --repo`, reached
                // from the other side. A ticket's repo is fetched here and the new claims are
                // held to it.
                var xt = _store.Ticket(tid);
                if (xt is null || xt.State != "open") { w.WriteLine($"error: ticket {tid} not open"); w.WriteLine("##exit 1"); break; }
                var xRepo = RepoOf(xt);
                if (xRepo is null)
                {
                    w.WriteLine($"error: ticket {tid}'s repository is no longer in this workspace ({(xt.RepoPath.Length > 0 ? xt.RepoPath : $"'{xt.Repo}'")})");
                    w.WriteLine("##exit 1");
                    break;
                }
                var xMismatch = Repos.CheckClaims(Repositories(), xRepo, claims);
                if (xMismatch is not null)
                {
                    _store.Event("claim_extend_refused", null, $"ticket {tid}: {xMismatch}");
                    w.WriteLine($"error: {xMismatch}");
                    w.WriteLine("##exit 1");
                    break;
                }

                // EXTENDED, THEN THE OVERLAP REPORTED -- in that order, and it used to be
                // instead-of rather than after (D-R5, R3; see Store.ClaimExtend for why this
                // fourth refusal had to go with the three D-R5 names).
                var conflicts = _store.ClaimExtend(tid, claims);
                _store.Event("claim_extended", null, $"ticket {tid} += [{string.Join(", ", specs)}]");
                w.WriteLine($"extended ticket {tid}");
                if (conflicts.Count > 0)
                {
                    _store.Event("claim_overlap", null, $"extend ticket {tid}: {string.Join(" | ", conflicts)}");
                    foreach (var cf in conflicts) w.WriteLine($"      overlap: {cf}");
                }
                break;
            }
            case "approve":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                // ONE implementation, shared with the approval ask (R6) — see `ApproveTicket`
                // for why its caller list is the load-bearing part of D-R10.
                ApproveTicket(tid, "dodona approve");
                w.WriteLine($"approved ticket {tid}");
                break;
            }
            case "ack":
            {
                var id = e.GetProperty("id").GetInt64();
                w.WriteLine(_store.PaneAck(id) ? $"acked {id}" : $"error: {id} is not an unacked announcement");
                break;
            }
            case "undo-route":
            {
                var id = e.GetProperty("id").GetInt64();
                var undone = _store.RoutingUndo(id);
                if (undone is null) { w.WriteLine($"error: routing decision {id} not found or already undone"); break; }
                var (dl, input) = undone.Value;
                // Retraction to the lane that consumed the misroute — [DISPATCHER] framing
                // so the agent treats it as operator-authentic (spike 3).
                if (dl is long dlid && _lanes.TryGetValue(dlid, out var drt) && drt.Connected)
                {
                    drt.Say($"[DISPATCHER] Disregard this earlier message, it was routed to you by mistake: \"{input}\". Do not act on it; if you already started, stop and undo.");
                    _store.PaneEvent(dlid, "announcement", $"↩ undone: \"{Truncate(input, 60)}\" retracted", null, null);
                }
                _store.Event("route_undone", dl, $"decision {id}: {input}");
                w.WriteLine($"undone routing decision {id}");
                break;
            }
            case "tickets":
            {
                var multi = _store.Tickets().Any(t => t.Repo != ".");
                foreach (var t in _store.Tickets())
                    w.WriteLine($"ticket {t.Id}  {t.Title,-12}  {(multi ? $"repo={t.Repo,-10}  " : "")}state={t.State}  mode={t.MergeMode}  approved={t.Approved}  branch={t.Branch}");
                break;
            }
            case "repos":
            {
                var found = Repositories();
                if (found.Count == 0)
                {
                    w.WriteLine($"no git repository in {_primary}");
                    w.WriteLine("run `dodona repo-init` to make this folder one (lanes work meanwhile; only tickets need git)");
                    break;
                }
                foreach (var r in found)
                {
                    var cfg = Config.For(_primary, r.Path);
                    var key = Repos.Key(r.Path);
                    var tok = _store.TokenRead(new Store.RepoId(key, r.Name));
                    // Counted by identity, not by name: a ticket created before this repository
                    // was renamed (or re-prefixed by an attach) is still one of its tickets.
                    var open = _store.Tickets().Count(t => t.State == "open" &&
                        (t.RepoPath.Length > 0 ? t.RepoPath.Equals(key, StringComparison.OrdinalIgnoreCase)
                                               : t.Repo.Equals(r.Name, StringComparison.OrdinalIgnoreCase)));
                    // `delivery=pr` is printed only when it is true: it changes what half of
                    // this line MEANS (a token nobody can hold, a land nobody will run), so a
                    // reading that did not mention it would be the misleading one.
                    w.WriteLine($"{r.Name,-14} main={cfg.Main,-8} open-tickets={open}  token={(tok.Holder?.ToString() ?? "free"),-6} verify={cfg.Verify.Length} step(s){(cfg.IsPr ? "  delivery=pr" : "")}  {r.Path}");
                }
                break;
            }

            // ---------------- merge token & land (M1, §7) ----------------
            case "token-request":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var t = _store.Ticket(tid);
                if (t is null || t.State != "open") { w.WriteLine($"error: ticket {tid} not open"); break; }

                // R7 / D-R28: A `delivery: pr` REPOSITORY HAS NO MERGE TOKEN TO GIVE. Dodona does
                // not merge there, so the token would be serialising access to an operation that
                // never happens — and granting one is not harmless bookkeeping: a holder plus a
                // lease fences every other ticket in that repository for nothing.
                //
                // AHEAD OF THE APPROVAL GATE ON PURPOSE. Otherwise an `on-approval` ticket is sent
                // to `dodona approve` first, to unlock a token it can never be given — a refusal
                // that instructs you to do something useless, which is the shape this codebase has
                // paid for repeatedly. The repository is resolved here as a READ; the refusal for
                // an UNRESOLVABLE one stays below where it was, so no existing refusal changes
                // order.
                //
                // DENY WITH A REWRITE, NEVER A WALL (M5-DELIVERY-PLAN §1): the project's own skill
                // is mid-flow when this fires, and a bare refusal strands it.
                if (RepoOf(t) is { } prRepo && Config.For(_primary, prRepo.Path).IsPr)
                {
                    _store.Event("token_refused_pr_mode", null, $"ticket {tid}: {prRepo.Name} is delivery: pr");
                    w.WriteLine($"refused: {(t.Repo == "." ? "this repository" : t.Repo)} is delivery: pr — Dodona does not merge here, so there is no merge token");
                    w.WriteLine($"         push {(t.Branch.Length > 0 ? t.Branch : "your branch")} and open a PR; the forge's merge button is the gate");
                    w.WriteLine($"         what the work did is already assembled: dodona ticket-record {tid}");
                    w.WriteLine("##exit 1");
                    break;
                }

                if (t.MergeMode == "on-approval" && !t.Approved)
                {
                    _store.Event("token_refused_unapproved", null, $"ticket {tid}");
                    // Blocked-on-you is categorically distinct (§8): presence flips to
                    // "waiting on you" and the announcement lands in the pane AND the feed.
                    if (t.LaneId is long blid)
                    {
                        _store.LanePresence(blid, "waiting on you: merge");
                        _store.PaneEvent(blid, "announcement", $"waiting on you: merge ticket {tid} '{t.Title}' — dodona approve {tid}", null, null);
                    }
                    w.WriteLine($"refused: ticket {tid} is merge:on-approval and not approved");
                    // A REFUSAL THAT ASKS, NOT ONE THAT INSTRUCTS (`WORK-ISOLATION-PLAN` P5, which
                    // R6 absorbs; the same correction P4.5 made to `ticket-create`). The primary
                    // moment is COMPLETION — the record is what carries the manager's write-up, and
                    // asking then means the operator is not waiting for an agent to bump into a
                    // wall first — but this moment is unmistakable and costs milliseconds, and it
                    // covers the ticket whose record was impossible (no worktree) or has not
                    // happened yet. `QuestionUpsert` makes the second raise a no-op on the first's
                    // row, so a person is never asked the same thing twice.
                    // IN ITS OWN TRY, like the other two call sites and for a sharper reason: this
                    // one is ON THE SERIAL CONTROL PIPE, so an exception here would turn a refusal
                    // into an unhandled throw in the handler every other command is queued behind.
                    // The refusal itself is already written above and stands whatever happens next.
                    long qid = 0;
                    try { qid = AskToLand(t, t.LaneId ?? 0, null); }
                    catch (Exception ex) { _store.Event("land_ask_failed", null, $"ticket {tid}: at token-request: {ex.Message}"); }
                    if (qid > 0)
                        w.WriteLine($"         answer it in the window, or: dodona answer {qid} yes");
                    w.WriteLine("##exit 1");
                    break;
                }

                // Merge-time backstop (§6 layer 2): diff the branch against its merge
                // base; any touched path outside the claim refuses the token. This
                // catches everything the fail-open hook gate cannot see.
                // REFUSED, NOT SUBSTITUTED. `reqRepo?.Path ?? _primary` meant a ticket whose
                // repository had left the workspace got its branch diffed against the FIRST
                // project's main — a diff of two unrelated histories, which is either every
                // file or none, and either way the backstop stopped answering the question it
                // was asked. There is no safe default for "which repository is this", so the
                // token is refused and the reason names the path that was recorded.
                var reqRepo = RepoOf(t);
                if (reqRepo is null)
                {
                    _store.Event("token_refused_no_repo", null, $"ticket {tid}: repo '{t.Repo}' ({t.RepoPath}) is not in this workspace");
                    w.WriteLine($"refused: ticket {tid}'s repository is no longer in this workspace ({(t.RepoPath.Length > 0 ? t.RepoPath : $"'{t.Repo}'")})");
                    w.WriteLine("         re-attach it (dodona workspace-attach --member <path>) or abandon the ticket");
                    w.WriteLine("##exit 1");
                    break;
                }
                var reqPath = reqRepo.Path;
                var reqPrefix = reqRepo.ClaimPrefix;
                var reqCfg = Config.For(_primary, reqPath);

                // WHAT THE BRANCH TOUCHED IS RECORDED, NOT JUDGED (D-R5/D-R7, R3).
                //
                // This block used to REFUSE the token when the diff touched a path outside the
                // ticket's declared claim. It was asking whether reality matched a prediction,
                // and with the prediction retired the question has no content left: the paths
                // came out of `git diff`, the claim came out of whatever the agent or the
                // promotion happened to declare up front, and a mismatch means the declaration
                // was incomplete rather than that anything is wrong.
                //
                // It also actively blocked R1's flow. The diff is taken from the merge base, so
                // once an agent has merged main into its branch itself -- D-R3's path, and the
                // only way a silent drop can exist -- the base IS main's tip and every file the
                // branch touched relative to main reads as "outside the claim". R2's own fixture
                // could not obtain a token while this refusal lived.
                //
                // The DIFF ITSELF IS KEPT, because it is the derived ownership signal D-R7 asks
                // for: a fact, needing no ceremony from the agent, that cannot go stale. It is
                // recorded for the manager to read (R4/R5) and it gates nothing.
                var (dc, diff) = Git.Run(reqPath, "diff", "--name-only", $"{reqCfg.Main}...{t.Branch}");
                if (dc == 0 && diff.Length > 0)
                    _store.Event("branch_touched", null,
                                 BranchTouchedDetail(tid, diff, reqPrefix, _store.TicketClaims(tid)));

                var (status, gen, pos) = _store.TokenRequest(tid, TokenIdOf(t), lease, () => Git.Sha(reqPath, reqCfg.Main));
                w.WriteLine(status == "granted"
                    ? $"granted ticket {tid} generation {gen}{RepoTag(t.Repo)}"
                    : $"queued ticket {tid} position {pos}{RepoTag(t.Repo)}");
                break;
            }
            case "token-renew":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var lease = e.TryGetProperty("lease", out var ls) ? ls.GetInt32() : 120;
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                if (_store.TokenRenew(tid, TokenIdOf(rt), lease)) w.WriteLine($"renewed ticket {tid}");
                else { w.WriteLine($"refused: ticket {tid} is not the live holder"); w.WriteLine("##exit 1"); }
                break;
            }
            case "token-release":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                var rt = _store.Ticket(tid);
                if (rt is null) { w.WriteLine($"error: no ticket {tid}"); break; }
                _store.TokenRelease(tid, TokenIdOf(rt));
                w.WriteLine("released");
                break;
            }
            case "token-status":
            {
                // One token per repository: they land in parallel, so they report in
                // parallel too.
                var tokens = _store.TokensAll();
                if (tokens.Count == 0)
                {
                    // Nothing has ever been landed here. Materialise the one for the repository
                    // this workspace has, so the reading is a reading rather than a blank.
                    var only = Repositories().FirstOrDefault();
                    tokens = new List<Store.TokenRow> { _store.TokenRead(
                        only is null ? new Store.RepoId("#unresolved:.", ".") : new Store.RepoId(Repos.Key(only.Path), only.Name)) };
                }
                var manyRepos = tokens.Any(x => x.Repo != ".");
                foreach (var tok in tokens)
                    w.WriteLine($"{(manyRepos ? $"repo={tok.Repo,-12} " : "")}holder={(tok.Holder?.ToString() ?? "none")} generation={tok.Generation} expires={tok.ExpiresTs ?? "-"} main={(tok.MainSha is { Length: >= 8 } s ? s[..8] : "-")}");
                break;
            }
            // R3.5 / D-R14: THIS HANDLER NO LONGER PERFORMS THE LAND. It answers in
            // milliseconds — the cheap gate, then `landing…` — and the merge, the verify and the
            // fast-forward run on their own task. See LandBegin for why, and for the two
            // constraints that survive the change.
            case "land":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                w.WriteLine(LandBegin(tid, out var landStarted));
                if (!landStarted) w.WriteLine("##exit 1");
                break;
            }
            // The other half of the protocol: where the outcome is read from. A land also
            // ANNOUNCES its outcome (into the ticket's pane, or the dispatcher's), so nothing
            // depends on anyone polling — this exists so `dodona land` can still hand a shell an
            // exit code, and so a person can ask.
            //
            // IT MUST NEVER SUMMON A DAEMON, and the client end enforces that (CLAUDE.md §3.2's
            // incident: a summoned daemon runs its warm-up and spawns four model-backed
            // processes). A poll that woke a daemon to be told "no land here" would be that
            // incident on a 250 ms timer.
            // R4: read the ticket's completion record (D-R8). A READ ONLY -- it assembles
            // nothing, because assembly is triggered by a turn ending and a command that built
            // one on demand would be a second, differently-timed producer of the same artifact
            // (the "two implementations of make-a-ticket drift on exactly the checks that
            // matter" lesson, from `MakeTicket`).
            //
            // It exists because R6 is the surface a person will actually read this through, and
            // until then an affordance no verb can reach is where the next defect lives
            // (CLAUDE.md §3.1). It is also how `m1` reads a record without hand-rolling SQL.
            // NEVER SUMMONS a daemon -- see the no-summon list in Program.cs, and §3.2.
            case "ticket-record":
            {
                var rtid = e.GetProperty("ticket").GetInt64();
                var rec = _store.LastTicketEvent(rtid, "completion_record");
                if (rec is null)
                {
                    // Says WHICH nothing this is. A ticket that has never finished a turn, one
                    // whose worktree could not be read, and one whose lane never had the trigger
                    // wired all look identical from the outside, and the last of those is the
                    // failure mode this phase was warned about -- so the reasons are named and
                    // the events that carry them are named too.
                    var why = _store.LastTicketEvent(rtid, "completion_record_impossible", "completion_record_failed");
                    w.WriteLine($"no record for ticket {rtid}" +
                                (why is not null ? $" -- last attempt: {why.Value.Kind} {why.Value.Detail}" : ""));
                    if (why is null)
                        w.WriteLine("       (a record is written when a turn ENDS on the ticket's lane and the worktree has " +
                                    "changed since the last one; `dodona tickets` shows whether the ticket has a lane at all)");
                    w.WriteLine("##exit 1");
                    break;
                }
                var braceAt = rec.Value.Detail.IndexOf('{');
                w.WriteLine(braceAt < 0 ? rec.Value.Detail : rec.Value.Detail[braceAt..]);
                break;
            }
            case "land-status":
            {
                var tid = e.GetProperty("ticket").GetInt64();
                if (!_lands.TryGetValue(tid, out var run))
                {
                    // Deliberately NOT an error about the ticket: this daemon simply has no land
                    // for it. In-flight lands are in memory only, so a restart forgets them —
                    // which is correct (nothing can go stale) and has to be said out loud.
                    w.WriteLine($"state=none");
                    w.WriteLine($"no land in flight for ticket {tid} in this daemon — `dodona tickets` says whether it landed, and a daemon restart forgets lands it did not finish");
                    w.WriteLine("##exit 1");
                    break;
                }
                if (run.Done)
                {
                    w.WriteLine($"state=done ok={(run.Ok ? 1 : 0)}");
                    w.WriteLine(run.Message);
                    if (!run.Ok) w.WriteLine("##exit 1");
                }
                else w.WriteLine($"state=running elapsed={(int)(DateTime.UtcNow - run.StartedUtc).TotalSeconds}s");
                break;
            }
            case "policy":
            {
                // Inspectable without spawning anything: ask what a sentence would get.
                var probe = e.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString()! : "";
                if (probe.Length > 0)
                {
                    var (clean, om, oe) = Policy.StripOverrides(probe);
                    var c = Policy.Resolve(clean, _config.Rules, _config.Model, _config.Effort, om, oe);
                    w.WriteLine($"{c.Model} {(c.Effort is { Length: > 0 } ? c.Effort : "-")}  {c.Describe}");
                    if (clean != probe.Trim()) w.WriteLine($"prompt: {clean}");
                    break;
                }
                w.WriteLine($"default    {_config.Model,-8} {_config.Effort}");
                w.WriteLine($"router     {_config.RouterModel,-8} {(_config.RouterEffort is { Length: > 0 } ? _config.RouterEffort : "cli default")}");
                foreach (var r in _config.Rules)
                    w.WriteLine($"rule       {r.Model,-8} {r.Effort,-7} {(r.Why is { Length: > 0 } ? r.Why : "-"),-12} {r.When}");
                w.WriteLine("override   @opus @max <text>   (model and effort are fixed when a lane starts)");
                break;
            }
            case "repo-status":
            {
                // What the picker (and anyone else) needs to know before offering a fix -- ABOUT
                // THE PROJECT IT WAS ASKED ABOUT (trap T5, P2.4).
                if (!TryCommandProject(e, out var statProj, out var statRefusal))
                { w.WriteLine(statRefusal!); w.WriteLine("##exit 1"); break; }
                var statCfg = ConfigForProject(statProj);
                var isRepo = Git.IsRepo(statProj);
                var nested = isRepo ? new List<string>() : Git.FindRepos(statProj);
                var entries = Directory.Exists(statProj)
                    ? Directory.EnumerateFileSystemEntries(statProj).Where(p => Path.GetFileName(p) is not ".dodona" and not ".git").Take(1).Count()
                    : 0;
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    root = statProj,
                    isRepo,
                    hasCommit = isRepo && Git.HasCommit(statProj),
                    empty = entries == 0,
                    nested = nested.Select(r => Path.GetRelativePath(statProj, r)).ToList(),
                    main = statCfg.Main,
                }));
                break;
            }
            case "repo-init":
            {
                // TRAP T5, FIXED (P2.4). This acted on the FIRST project unconditionally, so an
                // agent working in project B that ran `dodona repo-init` ran `git init` in
                // project A. Silently: every line it printed named A, and an agent that had
                // never seen A's path had no way to notice. `git init` in the wrong folder is
                // not reversible by anything Dodona knows how to do.
                if (!TryCommandProject(e, out var initProj, out var initRefusal))
                { w.WriteLine(initRefusal!); w.WriteLine("##exit 1"); break; }
                var adopt = e.TryGetProperty("adopt", out var ad) && ad.ValueKind == JsonValueKind.True;
                RepoInitOp(initProj, adopt, w);
                break;
            }
            case "questions":
            {
                // The workspace's own open questions, in the same tab-separated shape the
                // concierge's `questions` command prints (Concierge.cs:198). One shape, because
                // the ask overlay and this command are two renderings of one row (D-L4) and a
                // second format would be a second thing to keep in step.
                foreach (var q in _store.OpenQuestions())
                    w.WriteLine($"{q.Id}\t{q.Input}\t{q.Candidates}");
                break;
            }
            case "answer":
            {
                foreach (var line in await AnswerQuestion(e.GetProperty("id").GetInt64(),
                                                          e.GetProperty("answer").GetString() ?? ""))
                    w.WriteLine(line);
                break;
            }
            case "project-gone":
            {
                // P2.6 / trap T4: `workspace-detach` and `workspace-move` are REGISTRY edits made
                // by the CLI, and they touched no lane row at all -- so a live agent kept working
                // in a folder this workspace no longer owns, and `lane-respawn` would have put a
                // fresh one there too (its only test was `Directory.Exists`, which passes: the
                // folder is still there, it just belongs elsewhere now).
                //
                // The CLI sends this ONLY when the daemon is already live -- it must never summon
                // one, because summoning runs the warm-up and a registry edit that starts four
                // haiku processes is the §3.2 incident wearing a different hat.
                var gonePath = Instance.Canonical(e.GetProperty("project").GetString()!);
                var stopped = new List<long>();
                // P5.5 FIRST: THE MANAGERS THIS PROJECT HAD, whose cwd can never name it.
                // `project-gone` matched on `lanes.cwd`, and a brain's cwd is the neutral
                // directory (P5.8) -- so a brain for a departing project was invisible to this
                // handler, invisible to reconcile's old count-and-kill loop (which only ever
                // asked "how many of this role"), and would have sat there answering questions
                // about a project the workspace no longer has until its 30-minute lease ran out.
                // The obvious source of the next leak, and a lifecycle event that did not exist.
                //
                // A manager is fungible infrastructure with no transcript anyone reads, so its
                // row is retired rather than left visible the way a work lane's is.
                var goneManagers = new List<long>();
                // Read the project list ONCE, not per lane: Members() re-opens the registry on
                // every call, and this is a loop over every row in the store.
                var goneProjects = ProjectPaths();
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || !Projects.IsManagementRole(l.Role)) continue;
                    if (!string.Equals(RegistrationKey(l, goneProjects), gonePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_lanes.TryGetValue(l.Id, out var mrt)) { mrt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _brainLocks.TryRemove(l.Id, out _);
                    if (l.Role == "brain") _brainLo.TryRemove(gonePath, out _);
                    if (l.Role == "brain-hi") _brainHi.TryRemove(gonePath, out _);
                    if (l.Role == "router" && _routerLo == l.Id) _routerLo = -1;
                    _store.LaneState(l.Id, "dead");
                    _store.Event("brain_unregistered", l.Id, $"role={l.Role}: project {gonePath} left this workspace");
                    goneManagers.Add(l.Id);
                }
                if (goneManagers.Count > 0)
                    Announce($"[dodona] {gonePath} left this workspace: stopped {goneManagers.Count} management agent(s) that were for it " +
                             $"({string.Join(", ", goneManagers)})");
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || l.Cwd is not { Length: > 0 }) continue;
                    if (Projects.Of(new[] { gonePath }, l.Cwd) is null) continue;
                    // The AGENT goes; the lane ROW and its whole transcript stay (§12 -- nothing
                    // here deletes history). `lane-respawn --project <p>` is the way back, and
                    // the refusal in that handler names it.
                    // Ask the SHIM to go, over its own pipe -- it takes the child tree with it and
                    // exits cleanly, which needs no pid bookkeeping (CLAUDE.md §4). A lane this
                    // daemon never connected to still has a recorded pipe, and a shim that has
                    // been buffering for a predecessor is exactly the case worth covering.
                    if (_lanes.TryGetValue(l.Id, out var grt)) { grt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _store.LaneState(l.Id, "unreachable");
                    _store.Event("lane_project_detached", l.Id, $"project={gonePath} cwd={l.Cwd}");
                    _store.PaneEvent(l.Id, "announcement",
                        $"this project left the workspace, so the agent was stopped -- re-home with `dodona lane-respawn {l.Id} --project <project>`",
                        null, null, acked: true);
                    stopped.Add(l.Id);
                }
                if (stopped.Count > 0)
                    Announce($"[dodona] {gonePath} left this workspace: stopped {stopped.Count} lane(s) that were working in it ({string.Join(", ", stopped)})");
                w.WriteLine($"project {gonePath}: stopped {stopped.Count} lane(s)");
                break;
            }

            // ---------------- hot swap (M4, §13/§14) ----------------
            case "swap":
            {
                var exe = e.GetProperty("exe").GetString()!;
                var mode = e.TryGetProperty("mode", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString()! : "ask";
                var (handedOff, lines) = await ConsiderSwapAsync(exe, mode);
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swap-answer":
            {
                var answer = e.GetProperty("answer").GetString()!;
                var live = _store.SwapLive();
                if (live is null) { w.WriteLine("error: no update is waiting on an answer"); break; }
                switch (answer)
                {
                    case "now":
                    {
                        // The explicit override: swap even though something is in the way.
                        var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "now");
                        foreach (var l in lines) w.WriteLine(l);
                        return handedOff;
                    }
                    case "when-it-lands":
                        _store.SwapSet(live.Id, "when-it-lands", "armed");
                        _store.Event("swap_armed", null, $"swap {live.Id} build {live.Build}: {live.Blocker}");
                        Announce($"[dodona] update {live.Build} armed — swapping the instant this clears: {live.Blocker}");
                        w.WriteLine($"armed: swap {live.Id} fires the instant the blocker clears ({live.Blocker})");
                        break;
                    case "hold":
                        _store.SwapSet(live.Id, "hold", "held");
                        _store.Event("swap_held", null, $"swap {live.Id} build {live.Build}");
                        Announce($"[dodona] update {live.Build} held — say `dodona swap-answer now` when you want it");
                        w.WriteLine($"held: swap {live.Id} parked until you say so");
                        break;
                    default:
                        w.WriteLine("error: answer must be now | when-it-lands | hold");
                        break;
                }
                break;
            }
            case "swap-fire":
            {
                // The armed swap's condition cleared; the ticker woke us through our own
                // control pipe so this lands on the loop thread like any other command.
                var live = _store.SwapLive();
                if (live is null || live.State != "armed") { w.WriteLine("no armed swap"); break; }
                var (handedOff, lines) = await ConsiderSwapAsync(live.Exe, "armed");
                foreach (var l in lines) w.WriteLine(l);
                return handedOff;
            }
            case "swaps":
                foreach (var row in _store.SwapsAll()) w.WriteLine(row);
                w.WriteLine($"running: build {Ver.Build} schema {Ver.Schema} shim-protocol {Ver.ShimProtocol} exe {Ver.ExePath}");
                // The COMMIT, so what is running can be checked against `git log` and
                // bisected (P2.6). The build stamp above maps to nothing off this machine.
                w.WriteLine($"  {Ver.ProvenanceLine}");
                break;

            case "stop-daemon":
            {
                // A STOP CAN NOW ARRIVE DURING A LAND, which R3.5 made possible: the pipe used to
                // be held for the whole land, so this command physically could not be delivered
                // until it finished. Losing the land's task with the process is recoverable —
                // main only moves in the very last step, and re-running `land` re-merges (a
                // no-op), re-verifies and re-fast-forwards under the token the ticket still
                // holds. What is NOT acceptable is that happening silently (CLAUDE.md §0.1's
                // quietly-stale), so it is announced, recorded, and said on this reply.
                //
                // A hot SWAP needs no equivalent: `Blockers` already refuses to swap while any
                // merge token is held, and R3.5's first load-bearing constraint is that the token
                // is held across the whole land. So an in-flight land arms the swap rather than
                // being cut in half by it.
                foreach (var inflight in _lands.Values.Where(x => !x.Done).ToList())
                {
                    _store.Event("land_interrupted", null, $"ticket {inflight.Ticket}: daemon stopped mid-land");
                    if (_store.Ticket(inflight.Ticket) is Store.TicketRow it)
                        Announce(it, $"ticket {inflight.Ticket}'s land was interrupted by a daemon stop — nothing was lost (the trunk only moves in the last step): re-run dodona land {inflight.Ticket}");
                    w.WriteLine($"warning: ticket {inflight.Ticket} was mid-land — nothing was lost, re-run dodona land {inflight.Ticket}");
                }
                w.WriteLine("stopping (lanes keep running)");
                return true;
            }

            case "workspace-forgotten":
            {
                // P2.7, HANDED TO PHASE 5 BY PHASE 2 ON PURPOSE. `Registry.Forget` deletes every
                // `members` row in one transaction, and unlike `workspace-detach` it was wired to
                // nothing -- so forgetting a live workspace left agents working in folders the
                // registry no longer records, exactly the trap-T4 state Phase 2 closed for
                // detach. It was deferred because forget also orphans the DAEMON, which is a
                // lifecycle call and belongs beside this phase's reaping rather than bolted onto
                // detach.
                //
                // WHY THE DAEMON MUST GO TOO, and it is not tidiness: `publish --all` resolves
                // its swap targets by id FROM THE REGISTRY, so a daemon whose workspace has been
                // forgotten can never be hot-swapped again. It becomes an un-updatable process
                // holding agents nothing lists -- the shape of every orphan incident in this
                // codebase.
                //
                // AND IT IS REVERSIBLE, which is what makes acting rather than asking correct
                // (CLAUDE.md §0.1): forget keeps the store directory, so re-creating a workspace
                // over the same folder wakes it with every transcript intact. The announcement
                // says so.
                //
                // Every project is gone by definition, so every lane is stranded. Work lanes keep
                // their rows and their transcripts (§12); managers are retired, being fungible
                // infrastructure nobody reads.
                var forgottenLanes = new List<long>();
                foreach (var l in _store.LanesAll())
                {
                    if (l.State == "dead" || l.Role == "dispatcher") continue;
                    if (_lanes.TryGetValue(l.Id, out var frt)) { frt.Shutdown(); _lanes.TryRemove(l.Id, out _); }
                    else if (l.Pipe is { Length: > 0 }) await LaneRuntime.ShutdownShimAsync(l.Pipe);
                    _brainLocks.TryRemove(l.Id, out _);
                    if (Projects.IsManagementRole(l.Role))
                    {
                        _store.LaneState(l.Id, "dead");
                        _store.Event("brain_unregistered", l.Id, $"role={l.Role}: workspace {_wsName} was forgotten");
                    }
                    else
                    {
                        _store.LaneState(l.Id, "unreachable");
                        _store.Event("lane_project_detached", l.Id, $"workspace {_wsName} was forgotten; project={(l.Project.Length > 0 ? l.Project : l.Cwd)}");
                        _store.PaneEvent(l.Id, "announcement",
                            "this workspace was forgotten, so the agent was stopped -- the transcript is kept; re-create the workspace to resume",
                            null, null, acked: true);
                    }
                    forgottenLanes.Add(l.Id);
                }
                _brainLo.Clear();
                _brainHi.Clear();
                _routerLo = -1;
                _store.Event("workspace_forgotten", null,
                    $"stopped {forgottenLanes.Count} lane(s) and this daemon; store kept at {Paths.Store(_instanceId)}");
                Announce($"[dodona] workspace {_wsName} was forgotten: stopped {forgottenLanes.Count} agent(s) and this daemon. " +
                         $"Nothing was deleted -- the store is still at {Paths.Store(_instanceId)}, so re-creating the workspace brings it all back.");
                w.WriteLine($"workspace {_wsName} forgotten: stopped {forgottenLanes.Count} lane(s), stopping this daemon");
                return true;
            }

            // A COMMAND THIS BUILD DOES NOT KNOW IS AN ERROR, NOT A NO-OP (issue #9). This
            // switch had no default for its whole life, and neither did the concierge's — where
            // it cost two days: `publish --all` sent `swap`, the concierge understood ten
            // commands and that was not one of them, and the reply was silence, which every
            // caller reads as success. The same hole is here, one dispatcher over, and it is
            // reachable the same way: a newer client speaking to an older daemon across a
            // partial swap. Say so, and let it raise publish's exit code.
            default:
                w.WriteLine($"error: this daemon (build {Ver.Build}) does not understand \"{e.GetProperty("cmd").GetString()}\"");
                break;
        }
        return false;
    }

}
