using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// The claim algebra (design §6). This is the safety property the whole ticket system rests
/// on: two tickets may run at once exactly when their claims do not intersect, and the gate
/// refuses a write outside the claim it was given. Fifty-three lines of string comparison,
/// with no I/O in it at all -- and until now the only way to exercise any of it was to create
/// a ticket, which needs a workspace, which needs a daemon, which is eight seconds.
/// </summary>
public class ClaimsTests
{
    [Theory]
    [InlineData(@"src\Water\Sim.cs", "src/water/sim.cs")]
    [InlineData("/src/water/", "src/water")]
    [InlineData("  src/water  ", "src/water")]
    public void Normalize_folds_separators_case_and_edges(string input, string expected) =>
        Assert.Equal(expected, Claims.Normalize(input));

    [Theory]
    [InlineData("path:README.md", "path", "readme.md")]
    [InlineData("new:src/X.cs", "newfile", "src/x.cs")]
    [InlineData("subtree:src/Water", "subtree", "src/water")]
    public void Parse_maps_the_four_spellings(string spec, string kind, string value)
    {
        var p = Claims.Parse(spec);
        Assert.NotNull(p);
        Assert.Equal(kind, p!.Value.Kind);
        Assert.Equal(value, p.Value.Value);
    }

    /// <summary>A symbol keeps its case and its spaces -- it is an identifier, not a path.</summary>
    [Fact]
    public void Parse_leaves_a_symbol_alone()
    {
        var p = Claims.Parse("symbol:WaterController");
        Assert.Equal(("symbol", "WaterController"), (p!.Value.Kind, p.Value.Value));
    }

    [Theory]
    [InlineData("")]              // no colon
    [InlineData("path")]          // no colon
    [InlineData(":x")]            // no kind
    [InlineData("path:")]         // no value
    [InlineData("glob:src/**")]   // deliberately not supported (design §6)
    public void Parse_refuses_what_is_not_a_claim(string spec) =>
        Assert.Null(Claims.Parse(spec));

    // ---- Overlap: the concurrency decision --------------------------------------------

    [Fact]
    public void Two_identical_paths_overlap() =>
        Assert.True(Claims.Overlap("path", "src/a.cs", "path", "src/a.cs"));

    [Fact]
    public void Two_different_paths_do_not() =>
        Assert.False(Claims.Overlap("path", "src/a.cs", "path", "src/b.cs"));

    [Fact]
    public void A_subtree_contains_a_path_under_it() =>
        Assert.True(Claims.Overlap("subtree", "src/water", "path", "src/water/sim.cs"));

    /// <summary>Both directions, because Overlap is called with whichever order the caller
    /// happened to have and an asymmetric answer would let two tickets both be granted.</summary>
    [Fact]
    public void Containment_is_symmetric() =>
        Assert.True(Claims.Overlap("path", "src/water/sim.cs", "subtree", "src/water"));

    /// <summary>The prefix trap: "src/waterfall" starts with "src/water" as a STRING but is a
    /// different directory. Without the "/" the algebra would refuse unrelated work.</summary>
    [Fact]
    public void A_sibling_with_a_shared_prefix_does_not_overlap()
    {
        Assert.False(Claims.Overlap("subtree", "src/water", "subtree", "src/waterfall"));
        Assert.False(Claims.Overlap("subtree", "src/water", "path", "src/waterfall/sim.cs"));
    }

    [Fact]
    public void Nested_subtrees_overlap_in_both_directions()
    {
        Assert.True(Claims.Overlap("subtree", "src", "subtree", "src/water"));
        Assert.True(Claims.Overlap("subtree", "src/water", "subtree", "src"));
    }

    /// <summary>Symbols gate against symbols only. A symbol and a path are not comparable --
    /// the same name can exist in files no claim mentions -- so they never block each other.</summary>
    [Fact]
    public void A_symbol_only_collides_with_the_same_symbol()
    {
        Assert.True(Claims.Overlap("symbol", "WaterController", "symbol", "WaterController"));
        Assert.False(Claims.Overlap("symbol", "WaterController", "symbol", "SkyController"));
        Assert.False(Claims.Overlap("symbol", "WaterController", "subtree", "src/water"));
        Assert.False(Claims.Overlap("subtree", "src/water", "symbol", "WaterController"));
    }

    /// <summary>A file that does not exist yet is claimable, and collides with the subtree it
    /// will be born into -- otherwise two tickets could both declare the same new file.</summary>
    [Fact]
    public void A_declared_new_file_behaves_like_a_path()
    {
        Assert.True(Claims.Overlap("newfile", "src/water/new.cs", "subtree", "src/water"));
        Assert.True(Claims.Overlap("newfile", "src/a.cs", "path", "src/a.cs"));
    }

    // ---- Covers: what the claim gate allows a lane to write ---------------------------

    [Fact]
    public void Covers_accepts_the_exact_path_and_the_subtree()
    {
        Assert.True(Claims.Covers("path", "src/a.cs", "src/a.cs"));
        Assert.True(Claims.Covers("subtree", "src/water", "src/water/sim.cs"));
        Assert.True(Claims.Covers("subtree", "src/water", "src/water"));
    }

    [Fact]
    public void Covers_refuses_a_sibling_and_a_shared_prefix()
    {
        Assert.False(Claims.Covers("path", "src/a.cs", "src/b.cs"));
        Assert.False(Claims.Covers("subtree", "src/water", "src/waterfall/sim.cs"));
    }

    /// <summary>Stated as a test because it is a deliberate design decision and looks like a
    /// bug otherwise: a symbol claim gates nothing at the filesystem, so the gate must fall
    /// through to the ticket's path claims rather than granting a write on a name match.</summary>
    [Fact]
    public void Covers_never_grants_a_write_on_a_symbol_claim() =>
        Assert.False(Claims.Covers("symbol", "WaterController", "src/water/sim.cs"));
}

/// <summary>
/// Model/effort policy (design §9): the table decides, the operator overrides, and the
/// override is dispatch syntax that must never reach the agent.
/// </summary>
public class PolicyTests
{
    [Fact]
    public void An_override_is_stripped_and_returned()
    {
        var (text, model, effort) = Policy.StripOverrides("@haiku @low fix the spelling");
        Assert.Equal("fix the spelling", text);
        Assert.Equal("haiku", model);
        Assert.Equal("low", effort);
    }

    /// <summary>An `@` that is not ours belongs to the sentence. Eating it would silently
    /// change what the agent was asked to do.</summary>
    [Fact]
    public void An_unknown_at_token_is_left_for_the_agent()
    {
        var (text, model, effort) = Policy.StripOverrides("@someone please look at this");
        Assert.Equal("@someone please look at this", text);
        Assert.Null(model);
        Assert.Null(effort);
    }

    /// <summary>Overrides only, with nothing after them, must not hand the agent an empty
    /// prompt -- the original text stands.</summary>
    [Fact]
    public void Overrides_alone_do_not_produce_an_empty_prompt()
    {
        var (text, _, _) = Policy.StripOverrides("@haiku");
        Assert.Equal("@haiku", text);
    }

    [Theory]
    [InlineData("fix the spelling in the readme", "haiku", "low")]
    [InlineData("add a unit test for the parser", "sonnet", "medium")]
    [InlineData("redesign the schema", "opus", "max")]
    public void The_default_table_routes_by_kind_of_work(string text, string model, string effort)
    {
        var c = Policy.Resolve(text, Policy.Default, "opus", "high");
        Assert.Equal(model, c.Model);
        Assert.Equal(effort, c.Effort);
        Assert.False(c.Overridden);
    }

    [Fact]
    public void Anything_unmatched_falls_to_the_default()
    {
        var c = Policy.Resolve("make the toolbar collapsible", Policy.Default, "opus", "high");
        Assert.Equal(("opus", "high", "default"), (c.Model, c.Effort, c.Why));
    }

    [Fact]
    public void An_override_beats_the_table_and_says_so()
    {
        var c = Policy.Resolve("redesign the schema", Policy.Default, "opus", "high", "haiku", "low");
        Assert.Equal(("haiku", "low"), (c.Model, c.Effort));
        Assert.True(c.Overridden);
    }

    /// <summary>A project can put its own patterns in dodona.json, and a bad regex there must
    /// degrade to the next rule rather than take routing down with it.</summary>
    [Fact]
    public void A_broken_pattern_in_config_does_not_break_routing()
    {
        var rules = new[]
        {
            new PolicyRule("([unclosed", "haiku", "low", "broken"),
            new PolicyRule(@"\bshader\b", "opus", "max", "graphics"),
        };
        var c = Policy.Resolve("write the water shader", rules, "opus", "high");
        Assert.Equal(("opus", "max", "graphics"), (c.Model, c.Effort, c.Why));
    }
}

/// <summary>
/// Which repository a ticket belongs to (WORKSPACES-CONCIERGE.md §1). The load-bearing
/// property: a ticket lands by fast-forwarding ONE repository, and two fast-forwards cannot
/// be atomic -- so claims spanning two repos must be refused, not guessed at.
/// </summary>
public class ReposForClaimsTests
{
    static RepoRef R(string name) => new(name, $@"C:\ws\{name}", @"C:\ws");
    static readonly RepoRef Root = new(".", @"C:\ws", @"C:\ws");
    static List<(string, string)> C(params string[] specs) =>
        specs.Select(s => { var p = Claims.Parse(s)!.Value; return (p.Kind, p.Value); }).ToList();

    [Fact]
    public void An_empty_workspace_says_what_to_run()
    {
        var (repo, err) = Repos.ForClaims(new List<RepoRef>(), C("path:a.cs"));
        Assert.Null(repo);
        Assert.Contains("repo-init", err);
    }

    [Fact]
    public void Claims_in_one_repo_resolve_to_it()
    {
        var repos = new List<RepoRef> { R("engine"), R("tools") };
        var (repo, err) = Repos.ForClaims(repos, C("subtree:engine/src", "path:engine/readme.md"));
        Assert.Null(err);
        Assert.Equal("engine", repo!.Name);
    }

    /// <summary>The refusal that matters, with its reason in the message: this is the check
    /// that stops a ticket whose landing could only ever be half-atomic.</summary>
    [Fact]
    public void Claims_spanning_two_repos_are_refused_with_the_reason()
    {
        var repos = new List<RepoRef> { R("engine"), R("tools") };
        var (repo, err) = Repos.ForClaims(repos, C("path:engine/a.cs", "path:tools/b.cs"));
        Assert.Null(repo);
        Assert.Contains("span", err);
        Assert.Contains("split it into one ticket per repository", err);
    }

    /// <summary>Symbols name no path, so they follow whatever the path claims decided rather
    /// than counting as homeless.</summary>
    [Fact]
    public void A_symbol_claim_does_not_make_a_ticket_homeless()
    {
        var repos = new List<RepoRef> { R("engine"), R("tools") };
        var (repo, err) = Repos.ForClaims(repos, C("path:engine/a.cs", "symbol:WaterController"));
        Assert.Null(err);
        Assert.Equal("engine", repo!.Name);
    }

    [Fact]
    public void A_claim_in_no_repository_is_named_in_the_error()
    {
        var repos = new List<RepoRef> { R("engine") };
        var (repo, err) = Repos.ForClaims(repos, C("path:nowhere/a.cs"));
        Assert.Null(repo);
        Assert.Contains("nowhere/a.cs", err);
    }

    /// <summary>The single-repo workspace: the root repo swallows every path, which is what
    /// makes ordinary one-repository use need no repo syntax at all.</summary>
    [Fact]
    public void The_root_repo_covers_everything()
    {
        var (repo, err) = Repos.ForClaims(new List<RepoRef> { Root }, C("path:src/a.cs"));
        Assert.Null(err);
        Assert.True(repo!.IsRoot);
    }
}

/// <summary>
/// The two routing decisions made in CODE rather than by a model (WORKSPACES-CONCIERGE.md
/// §5.1). Everything else now WAITS for a verdict, so these two are what keep "stop" instant.
/// </summary>
public class RoutingInCodeTests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("no")]
    [InlineData("try again")]
    [InlineData("never mind")]
    [InlineData("Carry on.")]
    [InlineData("that's wrong")]
    public void An_unmistakable_generic_needs_no_model(string text) =>
        Assert.True(Daemon.IsObviousGeneric(text));

    /// <summary>The whole-input anchor, stated as a test because it is the difference between
    /// "stop" (a generic) and "stop the nightly build from running" (work). Without it, a real
    /// instruction would be delivered as an interjection to whatever lane was focused.</summary>
    [Theory]
    [InlineData("stop the nightly build from running")]
    [InlineData("no idea why the shader is dark")]
    [InlineData("continue the migration in the morning")]
    public void A_generic_word_inside_a_real_sentence_is_work(string text) =>
        Assert.False(Daemon.IsObviousGeneric(text));

    [Fact]
    public void A_lane_prefix_names_its_target_and_keeps_the_rest()
    {
        var p = Daemon.LanePrefix("WATER: make it darker");
        Assert.Equal(("WATER", "make it darker"), (p!.Value.Target, p.Value.Body));
    }

    /// <summary>THE bug this shape has already caused, kept as a test so it cannot come back:
    /// `\s+` and not `\s*`. A test directive `routekind:` once created a lane titled ROUTEKIND,
    /// after which every later `routekind:...` line was silently delivered to it. The same
    /// shape bites for real with a lane called HTTP and a sentence containing `http://`.</summary>
    [Theory]
    [InlineData("routekind:new-task say build the dialog")]
    [InlineData("see http://example.com for the spec")]
    public void A_colon_with_no_space_is_not_a_target(string text) =>
        Assert.Null(Daemon.LanePrefix(text));

    /// <summary>Singleline, so a multi-line paragraph addressed at a lane keeps all of it --
    /// the box is multiline and Shift+Enter is a newline, so this is the common case.</summary>
    [Fact]
    public void A_prefixed_paragraph_keeps_its_newlines()
    {
        var p = Daemon.LanePrefix("WATER: first line\nsecond line");
        Assert.Equal("first line\nsecond line", p!.Value.Body);
    }
}

/// <summary>
/// Path canonicalization (Instance). The registry dedupes MEMBERS by this, so two spellings
/// of one folder must fold together or a workspace can attach the same repo twice -- which is
/// two merge tokens over one main, the race this system exists to prevent (CLAUDE.md §5).
/// </summary>
public class InstanceCanonicalTests
{
    [Fact]
    public void A_trailing_separator_is_not_a_different_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dodona-canon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { Assert.Equal(Instance.Canonical(dir), Instance.Canonical(dir + Path.DirectorySeparatorChar)); }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void A_relative_spelling_folds_to_the_absolute_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dodona-canon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "inner"));
        try
        {
            Assert.Equal(Instance.Canonical(Path.Combine(dir, "inner")),
                         Instance.Canonical(Path.Combine(dir, "inner", "..", "inner")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A path that does not exist still canonicalizes -- `where` is asked about
    /// folders before they are created, and throwing there would make it unusable.</summary>
    [Fact]
    public void A_path_that_does_not_exist_still_answers()
    {
        var p = Path.Combine(Path.GetTempPath(), "dodona-nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.False(string.IsNullOrEmpty(Instance.Canonical(p)));
    }
}
