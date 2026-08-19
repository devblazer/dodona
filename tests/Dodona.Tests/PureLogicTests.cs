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

    // ---- the whole tree: `subtree:/` was a claim that blocked nobody (Phase 0b) ---------
    //
    // MEASURED against HEAD before it was changed, by writing these tests first and running
    // them: `subtree:/` normalizes to the EMPTY string, and every branch of the algebra then
    // answered no. Overlap's `a == b || a.StartsWith(b + "/")` cannot match an empty `a`, and
    // Covers' `relPath.StartsWith(value + "/")` went looking for a leading "/". So a claim
    // that READS "the whole tree" overlapped nothing and covered nothing -- an exclusive lock
    // over everything that let every other ticket straight past it, and a gate that then
    // denied its own holder every write. The shape of enforcement that is switched off while
    // looking armed (CLAUDE.md §0.3).
    //
    // It is now the whole tree rather than a parse error, for two reasons. The empty value is
    // needed INTERNALLY anyway -- reducing `subtree:proj` to repo-relative terms inside repo
    // `proj` yields exactly "" -- so the algebra has to answer this correctly whatever the
    // parser does. And in a one-repo workspace, where the claim prefix is empty, `subtree:/`
    // is the ONLY spelling of "I am refactoring the whole repository, claim all of it":
    // refusing it would leave a legitimate and maximally-safe claim inexpressible.

    [Theory]
    [InlineData("subtree:/")]
    [InlineData("subtree:.")]
    [InlineData("subtree:./")]
    public void The_whole_tree_parses_to_an_empty_subtree_value(string spec) =>
        Assert.Equal(("subtree", ""), Claims.Parse(spec)!.Value);

    /// <summary>"./x" and "x" are one file, and the algebra is string comparison -- so if the
    /// leading "./" survived normalization they would not overlap.</summary>
    [Fact]
    public void A_leading_dot_slash_is_the_same_place() =>
        Assert.True(Claims.Overlap("path", Claims.Normalize("./src/a.cs"), "path", Claims.Normalize("src/a.cs")));

    [Fact]
    public void The_whole_tree_overlaps_every_path_claim()
    {
        Assert.True(Claims.Overlap("subtree", "", "subtree", "src/water"));
        Assert.True(Claims.Overlap("subtree", "src/water", "subtree", ""));
        Assert.True(Claims.Overlap("subtree", "", "path", "readme.md"));
        Assert.True(Claims.Overlap("path", "readme.md", "subtree", ""));
        Assert.True(Claims.Overlap("subtree", "", "newfile", "src/x.cs"));
        Assert.True(Claims.Overlap("subtree", "", "subtree", ""));
    }

    /// <summary>...but not a symbol, and that is not an oversight: a symbol names an
    /// identifier, not a path, so no subtree contains one. Kept consistent with
    /// <see cref="A_symbol_only_collides_with_the_same_symbol"/> rather than special-cased,
    /// because "the widest possible path claim" and "a name" are still not comparable.</summary>
    [Fact]
    public void The_whole_tree_does_not_overlap_a_symbol()
    {
        Assert.False(Claims.Overlap("subtree", "", "symbol", "Config"));
        Assert.False(Claims.Overlap("symbol", "Config", "subtree", ""));
    }

    /// <summary>The other half, and the one that makes the claim usable rather than merely
    /// blocking: the gate must grant its holder a write anywhere. Against HEAD this denied
    /// every path, so a ticket holding the whole tree could not touch a single file in it.</summary>
    [Fact]
    public void The_whole_tree_covers_every_path()
    {
        Assert.True(Claims.Covers("subtree", "", "readme.md"));
        Assert.True(Claims.Covers("subtree", "", "src/water/sim.cs"));
    }

    /// <summary>An empty value is the whole tree for a SUBTREE and nonsense for everything
    /// else: `path:/` names no file and `symbol:   ` names no identifier. Both used to parse,
    /// and a `path:` claim with an empty value overlapped only other empty ones and covered
    /// nothing -- so it created a ticket that held a claim on nothing while reporting success,
    /// which is P0.5's silently-dropped-spec failure reached from the other side. Refused by
    /// name at the door instead. A truncated `subtree:` (nothing at all after the colon) stays
    /// refused too: "/" and "." are deliberate spellings, an empty tail is a typo.</summary>
    [Theory]
    [InlineData("path:/")]
    [InlineData("new:/")]
    [InlineData("path:.")]
    [InlineData("path:./")]
    [InlineData("symbol:   ")]
    [InlineData("subtree:")]
    public void An_empty_value_is_refused_for_every_kind_but_a_subtree(string spec) =>
        Assert.Null(Claims.Parse(spec));
}

/// <summary>
/// Claim scoping (Phase 0b): the same claim string means different things in different
/// repositories, and the same folder is spelled differently in one repository over time. Both
/// follow from Phase 0's finding that a repository's DISPLAY NAME is not stable while an open
/// ticket's claims are frozen against it — so <c>Store.FindConflicts</c> cannot compare raw
/// values, and <c>Claims.Overlap(Held, Held)</c> is where it stopped.
///
/// The bias is stated once here because every case below is an instance of it: a false
/// positive refuses work that would have been fine and can be argued with; a false negative
/// puts two agents in one file and says nothing. So every uncertainty falls through to the
/// old unscoped comparison.
/// </summary>
public class ClaimScopeTests
{
    // Two repositories in one workspace, as Repos.Discover names them once a second project
    // is attached: identity is the path, the display name is what claims are prefixed with.
    const string EngineKey = @"c:\ws\engine";
    const string ToolsKey = @"c:\ws\tools";
    static Claims.Held Engine(string kind, string v) => new(EngineKey, "engine", kind, v);
    static Claims.Held Tools(string kind, string v) => new(ToolsKey, "tools", kind, v);
    static Claims.Held Root(string kind, string v) => new(@"c:\ws\solo", ".", kind, v);

    // ---- THE POINT OF THE PHASE: the drift that nothing detected ----------------------
    //
    // A repository alone in its workspace is named "." (empty claim prefix); attaching a
    // second project renames it to its folder leaf, and every ticket already open keeps the
    // name it was born with. So one folder acquires two spellings, and until this existed the
    // claim algebra saw two unrelated strings. `workspace-acceptance`'s drift fixture carried
    // a comment recording exactly this hole: "after the rename it CANNOT be seen to [overlap]
    // ... (that half is Phase 0b's problem)".
    [Fact]
    public void One_folder_under_two_names_in_one_repository_still_overlaps()
    {
        var born_before_the_attach = new Claims.Held(EngineKey, ".", "subtree", "src/one");
        var born_after_it = Engine("subtree", "engine/src/one");
        Assert.True(Claims.Overlap(born_before_the_attach, born_after_it));
        Assert.True(Claims.Overlap(born_after_it, born_before_the_attach));
        // ...and the raw comparison the code used to make says no, which is the incident.
        Assert.False(Claims.Overlap("subtree", "src/one", "subtree", "engine/src/one"));
    }

    [Fact]
    public void A_path_and_the_subtree_it_is_in_still_overlap_across_a_rename()
    {
        var before = new Claims.Held(EngineKey, ".", "subtree", "src/one");
        Assert.True(Claims.Overlap(before, Engine("path", "engine/src/one/a.cs")));
        Assert.False(Claims.Overlap(before, Engine("path", "engine/src/two/b.cs")));
    }

    /// <summary>The repository's own root, claimed by name, is the whole of it — and that is
    /// the same value `subtree:/` produces in a one-repository workspace.</summary>
    [Fact]
    public void Claiming_a_repository_by_name_claims_all_of_it() =>
        Assert.True(Claims.Overlap(Engine("subtree", "engine"), Engine("path", "engine/src/deep/a.cs")));

    // ---- the false positive this removes ----------------------------------------------
    //
    // A symbol carries no path (Claims.Parse leaves it alone), ForClaims skips it, and Overlap
    // compared bare equality — so `symbol:Config` held in `engine` refused `symbol:Config` in
    // `tools`, where it is a different file in a different repository that no agent in the
    // first can even reach. A symbol is scoped to its ticket's repository because that is
    // where it lives: a ticket's claims cannot leave the repository it lands in (ticket-create
    // and claim-extend both refuse it), so there is no shared resource to protect.
    [Fact]
    public void The_same_symbol_in_two_repositories_does_not_collide() =>
        Assert.False(Claims.Overlap(Engine("symbol", "Config"), Tools("symbol", "Config")));

    /// <summary>...and the detection that matters is untouched. THIS is the check that would
    /// catch a "scoping" that simply stopped comparing: if narrowing had been done by deleting
    /// the symbol rule rather than by placing it, this goes green while the one above does too.</summary>
    [Fact]
    public void The_same_symbol_in_ONE_repository_still_collides() =>
        Assert.True(Claims.Overlap(Engine("symbol", "Config"), Engine("symbol", "Config")));

    [Fact]
    public void Two_repositories_paths_do_not_collide_even_spelled_identically()
    {
        // Only reachable for a pre-P0.6 row, since ForClaims/CheckClaims refuse a claim that
        // is not inside its ticket's repository — but the scoping must be right regardless.
        Assert.False(Claims.Overlap(Engine("path", "engine/src/main.cs"), Tools("path", "tools/src/main.cs")));
    }

    // ---- where it deliberately refuses too much --------------------------------------

    /// <summary>A ticket with no recorded repository (pre-schema-9, and its display name no
    /// longer resolves to anything) cannot be placed, so it is compared against everything the
    /// old way. It is one ticket in one workspace; over-refusing is a nuisance, and the
    /// alternative is letting an unplaceable claim collide with nothing at all.</summary>
    [Fact]
    public void An_unknown_repository_falls_back_to_the_unscoped_comparison()
    {
        var stranded = new Claims.Held("", ".", "subtree", "src/one");
        Assert.True(Claims.Overlap(stranded, new Claims.Held(EngineKey, ".", "path", "src/one/a.cs")));
        Assert.True(Claims.Overlap(new Claims.Held(EngineKey, ".", "path", "src/one/a.cs"), stranded));
    }

    /// <summary>A claim that does not live in the repository its ticket lands in — which
    /// `--repo tools --claim path:engine/sim.cs` created for as long as P0.6 was open — cannot
    /// be reduced to repo-relative terms without lying about it. So it keeps the workspace-wide
    /// comparison, and a legitimately-created claim over the same file is still refused.
    /// Scoping it to `tools` would have made this pair invisible, which is the one outcome
    /// worse than a red check.</summary>
    [Fact]
    public void A_claim_outside_its_own_repository_is_still_compared_workspace_wide()
    {
        var mis_attributed = Tools("path", "engine/src/main.cs");
        Assert.True(Claims.Overlap(mis_attributed, Engine("path", "engine/src/main.cs")));
        Assert.True(Claims.Overlap(Engine("path", "engine/src/main.cs"), mis_attributed));
    }

    /// <summary>The whole WORKSPACE (an empty subtree value in a repository whose prefix is
    /// not empty) is wider than any one repository, so it is not narrowed to one.</summary>
    [Fact]
    public void A_workspace_wide_subtree_in_a_prefixed_repository_is_not_narrowed() =>
        Assert.True(Claims.Overlap(Engine("subtree", ""), Tools("path", "tools/src/main.cs")));

    // ---- THE INVARIANT: a one-repository workspace is byte-for-byte unchanged ---------
    //
    // The operator's own machine is a single-project workspace, so this is not theoretical.
    // There the repository is named "." for every ticket, the claim prefix is empty, and every
    // ticket shares one key — so the scoped comparison is the unscoped one, exactly.
    [Theory]
    [InlineData("subtree", "src/water", "path", "src/water/sim.cs")]
    [InlineData("subtree", "src/water", "subtree", "src/waterfall")]
    [InlineData("path", "src/a.cs", "path", "src/a.cs")]
    [InlineData("path", "src/a.cs", "path", "src/b.cs")]
    [InlineData("symbol", "Config", "symbol", "Config")]
    [InlineData("symbol", "Config", "symbol", "Other")]
    [InlineData("newfile", "src/water/new.cs", "subtree", "src/water")]
    [InlineData("subtree", "", "path", "readme.md")]
    public void In_a_single_repository_workspace_scoping_changes_nothing(string ka, string a, string kb, string b) =>
        Assert.Equal(Claims.Overlap(ka, a, kb, b), Claims.Overlap(Root(ka, a), Root(kb, b)));

    /// <summary>The prefix rule has one definition now, and `RepoRef.ClaimPrefix` calls it.
    /// Both spellings of "the repository is the workspace" must produce no prefix, or every
    /// claim ever written in a one-project workspace stops matching itself.</summary>
    [Theory]
    [InlineData(".", "")]
    [InlineData("", "")]
    [InlineData("engine", "engine/")]
    [InlineData("work/engine", "work/engine/")]
    [InlineData("twin~2", "twin~2/")]
    public void Prefix_is_empty_only_for_the_root_repository(string name, string prefix) =>
        Assert.Equal(prefix, Claims.Prefix(name));
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
/// Repo IDENTITY, as opposed to repo naming (P0.1). A display name is recomputed by
/// `Repos.Discover` on every call and the rule changes with project count — one project that is
/// a repo is named ".", and attaching a second renames that same repository to its leaf. Keying
/// anything durable on that name meant two merge-token rows over one `main`, which is the double
/// fast-forward this whole system exists to prevent. So identity is the canonical path.
///
/// These are pure: `Instance.Canonical` falls back to `Path.GetFullPath` for a path that does
/// not exist, so no filesystem fixture is needed.
/// </summary>
public class ReposIdentityTests
{
    static RepoRef R(string name, string path) => new(name, path, @"C:\ws");

    /// <summary>The whole defect in one assertion: the SAME folder under the two names the
    /// naming rule gives it before and after an attach must be one key.</summary>
    [Fact]
    public void The_same_folder_under_two_names_has_one_key()
    {
        var before = R(".", @"C:\ws\proj");
        var after = R("proj", @"C:\ws\proj");
        Assert.Equal(Repos.Key(before.Path), Repos.Key(after.Path));
    }

    /// <summary>Repo names and paths come from live disk casing while SQLite `=` and PRIMARY KEY
    /// are binary-collated (P0.4) — so a rename that only changes case must not become a second
    /// identity. The store's key columns are COLLATE NOCASE for the same reason.</summary>
    [Fact]
    public void Case_is_not_a_different_repository()
    {
        var repos = new List<RepoRef> { R("engine", @"C:\ws\Engine") };
        Assert.NotNull(Repos.ByPath(repos, @"c:\ws\engine"));
    }

    [Fact]
    public void A_path_no_longer_in_the_workspace_resolves_to_nothing()
    {
        var repos = new List<RepoRef> { R("twin", @"C:\ws\p1\twin"), R("twin~2", @"C:\ws\p3\twin") };
        // The `leaf~2` recycling route: the NAME still resolves, to a stranger. The path does not.
        Assert.NotNull(Repos.ByName(repos, "twin~2"));
        Assert.Null(Repos.ByPath(repos, @"C:\ws\p2\twin"));
    }

    [Fact]
    public void An_empty_recorded_path_is_not_a_match_for_anything()
    {
        var repos = new List<RepoRef> { R("engine", @"C:\ws\engine") };
        Assert.Null(Repos.ByPath(repos, ""));
    }

    /// <summary>`--repo X` used to skip claim validation entirely (P0.6), so a ticket could be
    /// created in `tools` holding a claim over `engine`.</summary>
    [Fact]
    public void A_claim_in_another_repo_is_refused_for_the_named_one()
    {
        var repos = new List<RepoRef> { R("engine", @"C:\ws\engine"), R("tools", @"C:\ws\tools") };
        var err = Repos.CheckClaims(repos, repos[1], new List<(string, string)> { ("path", "engine/sim.cs") });
        Assert.NotNull(err);
        Assert.Contains("engine", err);
        Assert.Contains("tools", err);
    }

    [Fact]
    public void Claims_in_the_named_repo_are_accepted()
    {
        var repos = new List<RepoRef> { R("engine", @"C:\ws\engine"), R("tools", @"C:\ws\tools") };
        Assert.Null(Repos.CheckClaims(repos, repos[1], new List<(string, string)> { ("subtree", "tools/src") }));
    }

    /// <summary>Symbols name no path, so they name no repository — skipped here exactly as
    /// ForClaims skips them. Narrowing them is Phase 0b's business, not this one's.</summary>
    [Fact]
    public void A_symbol_claim_is_not_held_to_the_named_repo()
    {
        var repos = new List<RepoRef> { R("engine", @"C:\ws\engine"), R("tools", @"C:\ws\tools") };
        Assert.Null(Repos.CheckClaims(repos, repos[1], new List<(string, string)> { ("symbol", "Config") }));
    }

    /// <summary>THE ONE-PROJECT CASE IS UNCHANGED, and that is the property the whole workspace
    /// migration rested on: the root repo swallows every path, so a single-repo workspace can
    /// never see this refusal.</summary>
    [Fact]
    public void One_repository_can_never_produce_a_mismatch()
    {
        var root = new List<RepoRef> { new(".", @"C:\ws", @"C:\ws") };
        Assert.Null(Repos.CheckClaims(root, root[0],
            new List<(string, string)> { ("path", "src/a.cs"), ("subtree", "anything/at/all") }));
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
/// <summary>
/// WHERE A LANE'S PROCESS RUNS -- the rung order in <see cref="Daemon.ResolveLaneCwd"/>.
///
/// This decision has already been got wrong once and it was expensive (M5.1): `lane-respawn`
/// hardcoded the workspace's first project and rebuilt the PLAIN-lane prompt, so a resumed
/// TICKET agent ran in the operator's live working copy while being told "your worktree is the
/// current working directory; work only there". A gated agent, resumed, editing main's tree.
///
/// It was fixed in two places at once, eleven hundred lines apart, and the fix was invisible to
/// everything except two acceptance checks that parse an event's detail STRING
/// (m3 `respawned_ticket_lane_returns_to_its_worktree`) -- eight seconds of daemon startup to
/// exercise three string comparisons. That is why the order lives on this loop now: it is the
/// part that was wrong, and it costs a millisecond to hold.
/// </summary>
public class LaneCwdPrecedenceTests
{
    const string Worktree = @"C:\proj\.dodona\wt\t1";
    const string Recorded = @"C:\other";
    const string First = @"C:\proj";

    /// <summary>THE M5.1 INCIDENT, as one assertion. If this ever reverses, a resumed ticket
    /// agent goes back to editing the operator's live tree while its prompt claims otherwise.
    /// m3:186-187 is the same fact through eight seconds of daemon.</summary>
    [Fact]
    public void A_ticket_worktree_wins_over_the_recorded_cwd() =>
        Assert.Equal(Worktree, Daemon.ResolveLaneCwd(Worktree, Recorded, First));

    /// <summary>The plain-lane case: schema 8 recorded where this lane runs, so a respawn goes
    /// back THERE and not to the first project (which is what it used to do).</summary>
    [Fact]
    public void A_recorded_cwd_wins_over_the_first_project() =>
        Assert.Equal(Recorded, Daemon.ResolveLaneCwd(null, Recorded, First));

    /// <summary>Only a lane older than the `lanes.cwd` column reaches this rung. It is a
    /// fallback, not a default -- LOCATIONS-PLAN Phase 2 removes the last spawn sites that
    /// still pass the first project deliberately.</summary>
    [Fact]
    public void The_first_project_is_the_last_resort() =>
        Assert.Equal(First, Daemon.ResolveLaneCwd(null, null, First));

    /// <summary>An EMPTY string is not a directory, and both rungs above must skip it rather
    /// than start an agent in "". The store returns "" and not null for an unset cwd
    /// (`LaneRow.Cwd` is non-nullable), so this is the shape the real caller passes.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_rung_is_skipped_not_used(string? empty)
    {
        Assert.Equal(Recorded, Daemon.ResolveLaneCwd(empty, Recorded, First));
        Assert.Equal(First, Daemon.ResolveLaneCwd(empty, empty, First));
    }
}

/// <summary>
/// WHICH PROJECT a lane is in, and WHAT IS SAID ABOUT IT (<see cref="Projects"/>) -- the two
/// halves of docs/LOCATIONS-PLAN.md P1.2.
///
/// A project is one folder, a `members` row (docs/GLOSSARY.md). Before this, `lanes.cwd` had no
/// observable surface anywhere a person looks -- not `status`, not `ui dump`, only a
/// `shim_spawned` event detail string that two checks in the whole tree parse. So a lane opening
/// in the WRONG project was invisible to the operator and to every check but those two, which is
/// why Phase 1 blocks Phases 2 to 5.
///
/// <see cref="Projects.Field"/> is on this loop for a specific reason: it encodes the rule that
/// a ONE-project workspace must report NOTHING new. That byte-for-byte property is what the
/// whole workspace migration rested on and is the thing this plan is most likely to break, and a
/// rule like that has to be re-checkable in a second or it will be checked once.
/// </summary>
public class ProjectResolutionTests
{
    const string Alpha = @"C:\ws\alpha";
    const string Beta = @"C:\ws\beta";
    const string Neutral = @"C:\home\neutral";
    static readonly string[] One = { Alpha };
    static readonly string[] Two = { Alpha, Beta };

    // ---------------------------------------------------------------- Of: the ancestor match

    [Fact]
    public void A_lane_sitting_in_a_project_resolves_to_it() =>
        Assert.Equal(Beta, Projects.Of(Two, Beta));

    /// <summary>Ancestor matching is the ORDINARY answer, not an edge case: every ticket lane in
    /// the product lives at `&lt;project&gt;\.dodona\wt\tN` (Paths.Worktrees).</summary>
    [Fact]
    public void A_ticket_worktree_resolves_to_the_project_that_holds_it() =>
        Assert.Equal(Alpha, Projects.Of(Two, Alpha + @"\.dodona\wt\t3"));

    /// <summary>Longest ancestor wins -- the same rule Registry.Owner uses. A repo attached
    /// inside an also-attached folder must resolve to the repo, not to its container, or every
    /// lane in it would be reported against the wrong project.</summary>
    [Fact]
    public void The_longest_ancestor_wins()
    {
        var nested = new[] { Alpha, Alpha + @"\inner" };
        Assert.Equal(Alpha + @"\inner", Projects.Of(nested, Alpha + @"\inner\src\a.cs"));
    }

    /// <summary>A PREFIX IS NOT AN ANCESTOR. `C:\ws\alpha` must not swallow `C:\ws\alpha-two`
    /// -- a startswith without the separator is the classic form of this bug, and it would
    /// report every lane of one project against a neighbour whose name it happens to begin
    /// with.</summary>
    [Fact]
    public void A_sibling_sharing_a_name_prefix_is_not_inside_it() =>
        Assert.Null(Projects.Of(Two, Alpha + "-two" + @"\src"));

    /// <summary>Paths come from live disk casing while string and SQLite comparison are both
    /// binary-collated -- the same drift LOCATIONS-PLAN Phase 0 records for repo NAMES, and a
    /// recorded cwd is exposed to exactly it.</summary>
    [Fact]
    public void Case_does_not_decide_which_project() =>
        Assert.Equal(Alpha, Projects.Of(Two, Alpha.ToUpperInvariant() + @"\SRC"));

    /// <summary>A trailing separator is not a different folder. `Paths.Worktrees` and hand-typed
    /// `--root` values both produce these.</summary>
    [Fact]
    public void A_trailing_separator_is_not_a_different_folder() =>
        Assert.Equal(Alpha, Projects.Of(Two, Alpha + @"\"));

    /// <summary>"This lane has no recorded directory" and "this lane is in the first project"
    /// are DIFFERENT FACTS. Collapsing them is how `_primary` stayed invisible for as long as it
    /// did, so an empty path answers nothing rather than the first entry.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_lane_with_no_recorded_directory_is_in_no_project(string? empty) =>
        Assert.Null(Projects.Of(Two, empty));

    [Fact]
    public void A_folder_no_project_owns_resolves_to_nothing() =>
        Assert.Null(Projects.Of(Two, @"C:\somewhere\else"));

    // ---------------------------------------------------------------- Field: what is SAID

    /// <summary>THE ONE-PROJECT RULE, and the reason this is a unit test rather than only an
    /// acceptance check. A workspace with one project has exactly one possible answer, so the
    /// field would be noise -- and more than noise, it would change what `status` and `ui dump`
    /// report for the configuration eleven of the twelve suites use. Every phase of
    /// LOCATIONS-PLAN has to leave that case byte-for-byte identical.</summary>
    [Fact]
    public void A_one_project_workspace_says_nothing_about_projects() =>
        Assert.Null(Projects.Field("work", Alpha + @"\src", One, Neutral));

    [Fact]
    public void A_work_lane_in_a_two_project_workspace_names_its_project() =>
        Assert.Equal(Beta, Projects.Field("work", Beta + @"\src", Two, Neutral));

    /// <summary>Two projects and a TICKET lane: the tag names the project, not the worktree
    /// inside it. A person reading a tile wants to know which project, and the worktree path is
    /// one `dodona status` away.</summary>
    [Fact]
    public void A_ticket_lane_is_reported_against_its_project() =>
        Assert.Equal(Alpha, Projects.Field("work", Alpha + @"\.dodona\wt\t1", Two, Neutral));

    /// <summary>Management roles BELONG outside every project and must not be reported as a
    /// problem for being there. A router or brain started inside a project loads that project's
    /// CLAUDE.md and skills -- i.e. a classifier that can run /ship (commit 19dad3d; the
    /// operator: "that could be disastrous"), which is why LOCATIONS-PLAN T3 and P5.8 keep them
    /// neutral.</summary>
    [Theory]
    [InlineData("brain")]
    [InlineData("router")]
    [InlineData("compressor")]
    public void A_management_lane_in_the_neutral_directory_says_nothing(string role) =>
        Assert.Null(Projects.Field(role, Neutral, new[] { Alpha, Beta }, Neutral));

    /// <summary>...AND THE OMISSION IS PER ROLE, so it cannot hide the inverse defect. A brain
    /// that ended up INSIDE a project is named even in a one-project workspace, where the
    /// one-project rule would otherwise have silenced it.</summary>
    [Fact]
    public void A_brain_that_ended_up_inside_a_project_is_named_anyway() =>
        Assert.Equal(Alpha, Projects.Field("brain", Alpha, One, Neutral));

    /// <summary>PHASE 2's T4, MADE VISIBLE. `workspace-detach` and `workspace-move` touch no
    /// lane row, and respawn's only test is `Directory.Exists` -- so a lane's recorded cwd
    /// outlives its project while the folder still exists, it just belongs to another workspace
    /// now. A one-project workspace is NOT exempt: `none` is a defect, and the byte-for-byte
    /// rule protects a quiet field, never a broken one.</summary>
    [Fact]
    public void A_lane_whose_project_is_gone_says_none_and_carries_its_cwd()
    {
        var f = Projects.Field("work", Beta + @"\src", One, Neutral);
        Assert.NotNull(f);
        Assert.StartsWith("none ", f);
        Assert.Contains(Beta, f);
    }

    /// <summary>A LANE WITH NO RECORDED DIRECTORY has no process, so there is nothing to say --
    /// and `none` would be a lie about a defect. Found by m3 going red on real output: the
    /// `DODONA` dispatcher lane is a UI row and nothing else, so it has no cwd, and it was being
    /// reported as `none (cwd=-)` in a ONE-project workspace -- breaking the byte-for-byte rule
    /// on a row that is not even a process. AttachShimAsync writes the column before
    /// Process.Start, so an empty cwd can only mean "never spawned".</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("work")]
    public void A_lane_that_never_started_a_process_says_nothing(string? role)
    {
        Assert.Null(Projects.Field(role, "", Two, Neutral));
        Assert.Null(Projects.Field(role, null, Two, Neutral));
    }

    /// <summary>A WORK lane in the neutral directory is not normal and says so. Nobody spawns
    /// one there today; LOCATIONS-PLAN Phase 2 moves every work spawn site, and this is the rung
    /// that would catch one landing in management territory.</summary>
    [Fact]
    public void A_work_lane_in_the_neutral_directory_is_named_as_neutral() =>
        Assert.Equal("neutral", Projects.Field("work", Neutral, One, Neutral));

    // ------------------------------------------------- ScopeField: which project a MANAGER is for
    //
    // The second half of the same question, and it needs its own answer because Field CANNOT give
    // one: Field reads `lanes.cwd`, and a brain's cwd is the neutral directory on purpose (P5.8 --
    // a manager inside a project loads that project's CLAUDE.md and skills, i.e. a judgement agent
    // that can end up running `/ship`). So with a brain per project (P5.6), "which project is this
    // brain for" had no surface anywhere a person or a check could read.

    /// <summary>A manager in a two-project workspace names its project. This is the whole new
    /// fact: two brains, and nothing else in the product can tell them apart.</summary>
    [Fact]
    public void A_manager_in_a_multi_project_workspace_names_the_project_it_is_for() =>
        Assert.Equal(Beta, Projects.ScopeField("brain", Beta, Two));

    /// <summary>THE BYTE-FOR-BYTE RULE, and the reason this function is on the 1-second loop at
    /// all. Every phase of docs/LOCATIONS-PLAN.md leaves the one-project case identical -- the
    /// operator's own machine is a one-project workspace -- so with one project there is exactly
    /// one possible answer and the field would be pure noise.</summary>
    [Theory]
    [InlineData("brain")]
    [InlineData("brain-hi")]
    [InlineData("router")]
    [InlineData("compressor")]
    public void A_one_project_workspace_says_nothing_about_scope(string role) =>
        Assert.Null(Projects.ScopeField(role, Alpha, One));

    /// <summary>A WORK lane's scope is never printed here: its folder IS its project, so
    /// <see cref="Projects.Field"/> already answers it and two fields saying one thing is how a
    /// reader learns to stop reading either.</summary>
    [Theory]
    [InlineData("work")]
    [InlineData("dispatcher")]
    [InlineData(null)]
    public void A_work_lane_has_no_scope_field(string? role) =>
        Assert.Null(Projects.ScopeField(role, Beta, Two));

    /// <summary>A row with no registration says nothing rather than `none`: "this lane was never
    /// scoped" and "this lane is scoped to a project that left" are different facts, and the
    /// second one has its own answer below.</summary>
    [Fact]
    public void An_unregistered_manager_says_nothing()
    {
        Assert.Null(Projects.ScopeField("brain", "", Two));
        Assert.Null(Projects.ScopeField("brain", null, Two));
    }

    /// <summary>P5.5 MADE VISIBLE. A brain registered to a project the workspace no longer has is
    /// reaped -- but between the detach and the reap a person reading `status` must be able to see
    /// WHY, or the disappearance is the silent kind this codebase keeps paying for.</summary>
    [Fact]
    public void A_manager_registered_to_a_departed_project_is_named_as_gone() =>
        Assert.Equal($"gone ({Beta})", Projects.ScopeField("brain", Beta, new[] { Alpha, @"C:\ws\gamma" }));

    /// <summary>Case is folded, because one side comes from a `ProcessStartInfo` or a command line
    /// and the other from the registry, while `==` is binary-collated. `C:\WS\Beta` reading as a
    /// departed project would reap a healthy brain over a folder rename.</summary>
    [Fact]
    public void Scope_is_matched_case_insensitively() =>
        Assert.Equal(@"C:\WS\BETA", Projects.ScopeField("brain", @"C:\WS\BETA", Two));
}

/// <summary>
/// A LANE MAY ONLY OPEN WHERE A PROJECT OWNS (docs/LOCATIONS-PLAN.md Phase 2, traps T1 and T4).
///
/// Three rules live here, and the reason all three are pure functions is that none of them has an
/// acceptance-testable surface on its own:
///
///   * <see cref="Projects.IsOwned"/> is the T4 answer. `workspace-detach` and `workspace-move`
///     touch no lane row, and respawn's only test was `Directory.Exists` -- which PASSES, because
///     the folder is still there; it just belongs to another workspace now.
///   * <see cref="Projects.PromptDirMismatch"/> is T1, ENFORCED. The prompt says "your working
///     directory is X" and a separate line sets the real one; change one and not the other and the
///     agent is told a folder it is not in. **It compiles clean**, it has already happened once
///     (M5.1), and no acceptance suite can see it, because the prompt lives in an argv nothing
///     reads back. So the detector had better be right, and it is checked in a millisecond.
///   * <see cref="Daemon.ClaudeArgs"/> is T2's last link. The event a suite reads reports the
///     CONFIG it resolved; this is what pins that the config's permission mode actually reaches
///     the process's argv -- and it can only be checked here, because `IsClaude` is false for the
///     suites' fake agent, so no suite ever builds a claude argv at all.
/// </summary>
public class LaneProjectGuardTests
{
    const string Alpha = @"C:\ws\alpha";
    const string Beta = @"C:\ws\beta";
    const string Neutral = @"C:\home\neutral";
    static readonly string[] Two = { Alpha, Beta };

    [Fact]
    public void A_folder_a_project_owns_is_owned()
    {
        Assert.True(Projects.IsOwned(Two, Beta, Neutral));
        Assert.True(Projects.IsOwned(Two, Beta + @"\src\a.cs", Neutral));
    }

    /// <summary>THE T4 STATE. The folder still exists -- that is exactly why `Directory.Exists`
    /// could not catch this -- and it is no longer ours.</summary>
    [Fact]
    public void A_folder_that_left_the_workspace_is_not_owned() =>
        Assert.False(Projects.IsOwned(new[] { Alpha }, Beta + @"\src", Neutral));

    /// <summary>Management roles belong in the neutral directory and must not be refused there.
    /// A brain or router started inside a project loads that project's CLAUDE.md and skills --
    /// a classifier that can run /ship (T3, P5.8).</summary>
    [Fact]
    public void The_neutral_directory_is_always_owned() =>
        Assert.True(Projects.IsOwned(Two, Neutral + @"\x", Neutral));

    /// <summary>No recorded directory is not a refusal: a dispatcher row is a UI row with no
    /// process, and a lane older than schema 8 never wrote the column. ResolveLaneCwd falls back
    /// to the first project for both, so refusing them would strand a row that did nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_lane_with_no_recorded_directory_is_not_refused(string? cwd) =>
        Assert.True(Projects.IsOwned(Two, cwd, Neutral));

    // ---------------------------------------------------------------- T1

    /// <summary>The prompt a plain lane really gets, parsed back out of the argv it really goes
    /// into. If <see cref="Projects.DirSentence"/> and <see cref="Projects.Named"/> ever stop
    /// agreeing, the T1 detector silently detects nothing -- an enforcement that quietly stops
    /// enforcing, which is the failure this project keeps paying for.</summary>
    [Fact]
    public void The_prompt_names_the_directory_it_was_built_with() =>
        Assert.Equal(Beta, Projects.Named(Daemon.LaneSystemPrompt("BETA", Beta)));

    [Fact]
    public void A_matching_prompt_and_working_directory_pass()
    {
        var args = Daemon.ClaudeArgs(new Config("main", Array.Empty<string>()), "opus", "high",
                                     Daemon.LaneSystemPrompt("BETA", Beta), acceptEdits: true);
        Assert.Null(Projects.PromptDirMismatch(args, Beta));
        // Trailing separator and casing differ between a registry path and a ProcessStartInfo,
        // and both are binary-collated by `==`. A false refusal here would block every spawn.
        Assert.Null(Projects.PromptDirMismatch(args, Beta.ToUpperInvariant() + @"\"));
    }

    /// <summary>THE M5.1 INCIDENT, as one assertion: the prompt says one folder, the process
    /// starts in another. This is the case that used to compile, run, and mislead an agent.</summary>
    [Fact]
    public void A_prompt_naming_a_different_folder_is_caught()
    {
        var args = Daemon.ClaudeArgs(new Config("main", Array.Empty<string>()), "opus", "high",
                                     Daemon.LaneSystemPrompt("BETA", Beta), acceptEdits: true);
        var m = Projects.PromptDirMismatch(args, Alpha);
        Assert.NotNull(m);
        Assert.Contains(Beta, m);
        Assert.Contains(Alpha, m);
    }

    /// <summary>A TICKET prompt names no folder ("your worktree is the current working
    /// directory"), and neither does a fake agent's empty argv. Both must pass rather than be
    /// refused for having nothing to compare.</summary>
    [Fact]
    public void A_prompt_that_names_no_folder_is_not_a_mismatch()
    {
        Assert.Null(Projects.PromptDirMismatch(Array.Empty<string>(), Alpha));
        Assert.Null(Projects.PromptDirMismatch(
            new[] { "--append-system-prompt", "You are a lane agent working ticket 3. Your worktree is the current working directory." },
            Alpha));
    }

    // ---------------------------------------------------------------- T2

    /// <summary>The permission mode in the argv is THIS config's, not the daemon's field. This
    /// method read `_config` directly until Phase 2 -- the workspace's FIRST project -- so a lane
    /// in project B ran with project A's leash, and a repo deliberately kept on one lost it
    /// (CLAUDE.md §7: that leash is the only thing a project gets to ask for).</summary>
    [Fact]
    public void A_lanes_argv_carries_its_own_configs_permission_mode()
    {
        var leashed = new Config("main", Array.Empty<string>(), PermissionMode: "acceptEdits",
                                 AllowedTools: new[] { "Bash(dotnet build:*)" });
        var args = Daemon.ClaudeArgs(leashed, "opus", "high", "sys", acceptEdits: true);
        Assert.Equal("acceptEdits", Projects.ArgValue(args, "--permission-mode"));
        Assert.Equal("Bash(dotnet build:*)", Projects.ArgValue(args, "--allowedTools"));
    }

    /// <summary>A utility role (router, brain, compressor) asks for no edit permission at all, so
    /// no permission mode reaches its argv however its config is written.</summary>
    [Fact]
    public void A_utility_lane_gets_no_permission_mode()
    {
        var args = Daemon.ClaudeArgs(new Config("main", Array.Empty<string>(), PermissionMode: "acceptEdits"),
                                     "haiku", "low", "sys", acceptEdits: false, utility: true);
        Assert.Null(Projects.ArgValue(args, "--permission-mode"));
        Assert.Equal("user", Projects.ArgValue(args, "--setting-sources"));
    }
}

// =========================================================================================
// PHASE 3: WHICH PROJECT A TYPED SENTENCE MEANS (docs/LOCATIONS-PLAN.md Phase 3)
//
// The whole ladder is pure, which is the point of it being here: the one-project answer below
// is the property the entire workspace migration rested on and the one this plan is most
// likely to break, and it must be re-checkable in a second rather than eight seconds of
// daemon startup away.
//
// `dev prove unit:<check>` refuses by design (a HEAD that does not contain a new function
// cannot fail a test of it), so the teeth for this phase are the `workspace` and `concierge`
// acceptance checks. These pin the DECISIONS so a later simplification cannot pass quietly.
// =========================================================================================

public class ProjectLadderTests
{
    const string Alpha = @"C:\ws\alpha";
    const string Beta = @"C:\ws\project-zed";
    static readonly string[] One = { Alpha };
    static readonly string[] Two = { Alpha, Beta };
    static readonly (string Alias, string Key)[] NoHandles = System.Array.Empty<(string, string)>();
    static readonly string[] Nothing = System.Array.Empty<string>();

    /// <summary>THE PROPERTY THIS PHASE IS MOST LIKELY TO BREAK. One project is one answer, so
    /// nothing is named, nothing is classified and nothing is asked -- and the rung says `only`
    /// so the daemon knows not to write an event either. The operator's own machine is a
    /// one-project workspace.</summary>
    [Fact]
    public void One_project_is_answered_before_anything_else_happens()
    {
        var v = ProjectLadder.Decide(One, NoHandles, Nothing, "something completely unrelated");
        Assert.Equal(ProjectLadder.Only, v.Rung);
        Assert.Equal(Alpha, v.Project);
    }

    /// <summary>...and it holds even when a live lane and a taught handle would both point
    /// elsewhere. The short-circuit is first for a reason: a one-project workspace must not be
    /// able to reach a rung at all.</summary>
    [Fact]
    public void One_project_wins_over_every_other_rung()
    {
        var v = ProjectLadder.Decide(One, new[] { ("zed", Beta.ToLowerInvariant()) }, new[] { Beta }, "on zed, fix it");
        Assert.Equal(ProjectLadder.Only, v.Rung);
        Assert.Equal(Alpha, v.Project);
    }

    /// <summary>Rung 3, the cheapest of the real rungs: the sentence names the folder.</summary>
    [Fact]
    public void A_named_project_is_decided_in_code()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, Nothing, "in project-zed, rename the header");
        Assert.Equal(ProjectLadder.Named, v.Rung);
        Assert.Equal(Beta, v.Project);
        Assert.Equal("leaf", v.How);
    }

    /// <summary>A taught handle (`aliases.member_key`, registry schema 2) is the half `members`
    /// could not carry: `members` is already every project ever attached, but nothing recorded
    /// what the operator CALLS one.</summary>
    [Fact]
    public void A_taught_handle_names_its_project()
    {
        var v = ProjectLadder.Decide(Two, new[] { ("lamp", Beta.ToLowerInvariant()) }, Nothing, "the lamp needs repainting");
        Assert.Equal(ProjectLadder.Named, v.Rung);
        Assert.Equal(Beta, v.Project);
        Assert.Equal("alias", v.How);
    }

    /// <summary>The normalised form the plan names outright: `project zed` for `project-zed`.
    /// Voice typing and ordinary English both put spaces where a folder name has hyphens, and a
    /// memory that only matched the literal folder name would miss how people actually talk.</summary>
    [Fact]
    public void The_folder_name_said_as_words_still_names_it()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, Nothing, "start on project zed please");
        Assert.Equal(ProjectLadder.Named, v.Rung);
        Assert.Equal(Beta, v.Project);
        Assert.Equal("spoken", v.How);
    }

    /// <summary>A NAME BEATS THE CLASSIFIER, and this is the check that pins it. The concierge's
    /// own ladder already applies the rule one level up: explicit information never triggers a
    /// search. Consulting the model first would let "fix project-zed's header" open a lane in
    /// alpha because alpha happens to be busy -- a confident wrong answer, made instantly, in a
    /// case the operator had already answered for free.</summary>
    [Fact]
    public void A_named_project_is_not_overruled_by_a_busy_one()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, new[] { Alpha, Beta }, "in project-zed, rename the header");
        Assert.Equal(ProjectLadder.Named, v.Rung);
        Assert.Equal(Beta, v.Project);
    }

    /// <summary>Rung 2 with only ONE project holding live lanes needs no model: there is nothing
    /// to choose between. Free, and recorded as `sole-live` so the data says which evidence
    /// answered.</summary>
    [Fact]
    public void One_live_project_needs_no_model()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, new[] { Beta }, "make the header taller");
        Assert.Equal(ProjectLadder.Live, v.Rung);
        Assert.Equal(Beta, v.Project);
        Assert.Equal("sole-live", v.How);
    }

    /// <summary>Rung 2 proper: several projects are live, so which one is a judgement, and the
    /// verdict hands the caller the CLOSED list to ask about. Project is null -- nothing may be
    /// spawned on this verdict.</summary>
    [Fact]
    public void Several_live_projects_go_to_the_cheap_tier()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, new[] { Alpha, Beta }, "make the header taller");
        Assert.Equal(ProjectLadder.Classify, v.Rung);
        Assert.Null(v.Project);
        Assert.Equal(new[] { Alpha, Beta }, v.Candidates);
    }

    /// <summary>A LIVE LANE IN A PROJECT THIS WORKSPACE NO LONGER OWNS IS NOT EVIDENCE (Phase 2
    /// trap T4). `workspace-detach` and `-move` touch no lane row, so a recorded folder can
    /// outlive its project while the FOLDER still exists -- it belongs to another workspace now.
    /// Counting it would drag a brand-new agent into somebody else's repository.</summary>
    [Fact]
    public void A_live_lane_outside_every_project_is_not_a_candidate()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, new[] { @"C:\elsewhere\gone" }, "make the header taller");
        Assert.Equal(ProjectLadder.Ask, v.Rung);
    }

    /// <summary>Rung 4, and it is a real answer rather than a failure. Several projects, no name
    /// in the sentence, nothing live to infer from: every guess is a coin toss whose losing side
    /// is an agent editing the wrong repository, which one `lane-stop` does not undo. So the
    /// sentence is held and the candidates come back for the question.</summary>
    [Fact]
    public void Nothing_to_go_on_asks_rather_than_guessing()
    {
        var v = ProjectLadder.Decide(Two, NoHandles, Nothing, "make the header taller");
        Assert.Equal(ProjectLadder.Ask, v.Rung);
        Assert.Null(v.Project);
        Assert.Equal(new[] { Alpha, Beta }, v.Candidates);
    }

    /// <summary>A word boundary, not a substring: "network" is not "work". The concierge's rung 1
    /// has always had this rule and Phase 3 shares the one matcher, so a project called `work`
    /// cannot swallow every sentence about networks.</summary>
    [Fact]
    public void A_project_name_does_not_match_inside_a_longer_word()
    {
        var projects = new[] { @"C:\ws\work", @"C:\ws\other" };
        Assert.Null(ProjectLadder.NameMatch(projects, NoHandles, "trace the network timeouts"));
    }

    /// <summary>Longest name first, the same reason the concierge orders workspaces that way:
    /// `work-ui` must beat `work`, or the longer name can never be used.</summary>
    [Fact]
    public void The_longer_project_name_wins()
    {
        var projects = new[] { @"C:\ws\work", @"C:\ws\work-ui" };
        var hit = ProjectLadder.NameMatch(projects, NoHandles, "tidy up work-ui");
        Assert.Equal(@"C:\ws\work-ui", hit!.Value.Project);
    }

    /// <summary>A single-word handle is not also a "spoken" match -- Mentions already answered
    /// it. A rung that can only ever repeat the previous one lies about which evidence was
    /// used, and `how=` is data the operator's routing table is meant to be tunable from.</summary>
    [Fact]
    public void A_single_word_handle_is_never_a_spoken_match() =>
        Assert.False(ProjectLadder.Spoken("start on alpha", "alpha"));

    /// <summary>P3.A: an answer arriving from outside this process — the cheap tier's reply, or the
    /// operator's own answer to a rung-4 question — becomes a project only through the closed list.
    /// One resolver for both, because two copies of "does this name mean one of these projects"
    /// drift the moment one of them learns something.</summary>
    [Fact]
    public void A_project_name_resolves_back_to_its_project_by_leaf_or_by_path()
    {
        Assert.Equal(Beta, ProjectLadder.ByName(Two, "project-zed"));
        Assert.Equal(Beta, ProjectLadder.ByName(Two, "PROJECT-ZED"));
        Assert.Equal(Beta, ProjectLadder.ByName(Two, @"c:\WS\project-zed\"));
        Assert.Equal(Alpha, ProjectLadder.ByName(Two, "  alpha  "));
    }

    /// <summary>
    /// A name the list does not contain is null, and the caller must REFUSE. Two cases, both real:
    /// a model that invented a folder must not be able to place an agent in it, and a question
    /// answered after its project was detached must not deliver work into somebody else's tree
    /// (trap T4 on the answer path).
    /// </summary>
    [Theory]
    [InlineData("atlantis")]
    [InlineData("alph")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_name_no_project_answers_to_resolves_to_nothing(string? name) =>
        Assert.Null(ProjectLadder.ByName(Two, name));
}

public class LiveProjectEvidenceTests
{
    const string Alpha = @"C:\ws\alpha";
    const string Beta = @"C:\ws\beta";
    static readonly string[] Two = { Alpha, Beta };
    static readonly long[] Nothing = System.Array.Empty<long>();

    static (long, string, string, string)[] Lanes() => new[]
    {
        (1L, "work", "alive", Alpha),
        (2L, "work", "alive", Beta),
        (3L, "router", "alive", @"C:\home\neutral"),
        (4L, "work", "dead", Beta),
    };

    /// <summary>THE D-L6 CHECK, AND THE REASON THIS FUNCTION IS PURE AT ALL. A lane visible only
    /// by its RECORDED SHIM PID -- its pipe name absent from the namespace this instant -- is
    /// live. That window is real and measured: 8 reads of 192 over 1.5 s saw no pipe while the
    /// shim was alive and instantly connectable, and it is synchronised with a daemon restart,
    /// where a single read once declared four to seven lanes dead at once.
    ///
    /// No acceptance suite can construct that window -- it would have to hold the OS pipe
    /// namespace still -- so this is where narrowing the union back to "just read the pipes"
    /// goes red instead of looking like a tidy-up.</summary>
    [Fact]
    public void A_lane_alive_only_by_its_shim_record_still_counts() =>
        Assert.Equal(new[] { Beta }, Projects.Live(Two, Lanes(), Nothing, new[] { 2L }, Nothing));

    /// <summary>...and the other direction: a lane with no shim record at all is still seen by
    /// its pipe. On 2026-08-18 four live agents were invisible to `dodona ps` for exactly this
    /// reason, three of them running out of the compiler's own output directory.</summary>
    [Fact]
    public void A_lane_with_no_record_is_still_seen_by_its_pipe() =>
        Assert.Equal(new[] { Alpha }, Projects.Live(Two, Lanes(), new[] { 1L }, Nothing, Nothing));

    /// <summary>The third answer, which only the daemon has: it is holding an open handle to that
    /// pipe right now. No read can contradict that.</summary>
    [Fact]
    public void A_connected_runtime_is_evidence_on_its_own() =>
        Assert.Equal(new[] { Alpha }, Projects.Live(Two, Lanes(), Nothing, Nothing, new[] { 1L }));

    /// <summary>Distinct projects, not lanes: three lanes in one project is one candidate, or
    /// rung 2 would go to a model to choose between a project and itself.</summary>
    [Fact]
    public void The_answer_is_projects_not_lanes()
    {
        var many = new[] { (1L, "work", "alive", Alpha), (5L, "work", "alive", Alpha + @"\src") };
        Assert.Equal(new[] { Alpha }, Projects.Live(Two, many, new[] { 1L, 5L }, Nothing, Nothing));
    }

    /// <summary>A management lane is not work, and a dead row is not live. Either counted would
    /// make rung 2 answer with a project nobody is working in -- and the router itself lives in
    /// the neutral directory, which belongs to no project at all.</summary>
    [Fact]
    public void Management_lanes_and_dead_rows_are_not_evidence() =>
        Assert.Empty(Projects.Live(Two, Lanes(), new[] { 3L, 4L }, new[] { 3L, 4L }, Nothing));
}

/// <summary>
/// The asking component's pure half (docs/LOCATIONS-PLAN.md Phase 4, decision D-L4).
///
/// These are the only part of asking that is a FUNCTION, and they belong on the 1-second `unit`
/// loop for a specific reason: the daemon WRITES `questions.candidates` and the window PARSES
/// it, and the whole of D-L4 is that there is one component over one row. A parser that
/// disagreed with the writer would put the divergence in the one place no acceptance check
/// looks — inside a string.
///
/// `dev prove unit:&lt;check&gt;` REFUSES by design (`Do-Prove`): a pure function HEAD does not
/// contain cannot be failed by HEAD, so the acceptance checks in `ui-use` are what carry the
/// proof for this phase and these carry the algebra.
/// </summary>
public class AskTests
{
    [Fact]
    public void The_daemons_own_candidates_parse_back_to_the_choices_it_meant()
    {
        var choices = Ask.Choices(Ask.RepoInitCandidates("shaders"));
        Assert.Equal(new[] { "yes", "no" }, choices.Select(c => c.Value));
        Assert.Contains("shaders", choices[0].Label);
        Assert.False(string.IsNullOrEmpty(choices[1].Why));
    }

    /// <summary>The concierge's own shape, written by `Concierge.Ask` since before this file
    /// existed: `[{id,name}]` with no `why`. One parser has to read both, or the overlay would
    /// render group-scope questions with no buttons.</summary>
    [Fact]
    public void The_concierges_candidate_shape_parses_too()
    {
        var choices = Ask.Choices("""[{"id":"lighthouse-71c4","name":"lighthouse"},{"id":"work-5e07","name":"work"}]""");
        Assert.Equal(2, choices.Count);
        Assert.Equal("lighthouse-71c4", choices[0].Value);
        Assert.Equal("lighthouse", choices[0].Label);
        Assert.Null(choices[0].Why);
    }

    /// <summary>Rule 12 of this phase: a malformed question row must not take the window with
    /// it, the same reasoning that makes a corrupt `ui.json` silently ignored — the box you
    /// would use to complain lives inside the window. Every one of these is a blob the overlay
    /// must survive, and none of them may throw.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"yes\"}")]          // an object, not an array
    [InlineData("[1,2,3]")]                    // an array of the wrong thing
    [InlineData("[{}]")]                       // an object with neither half
    [InlineData("[{\"id\":\"\",\"name\":\"\"}]")]
    public void A_malformed_candidates_blob_is_no_choices_and_never_an_exception(string? blob)
    {
        Assert.Empty(Ask.Choices(blob));
    }

    /// <summary>Either half alone is answerable: a candidate with a name and no id is answered BY
    /// the name (`dodona concierge-answer &lt;id&gt; &lt;name&gt;` has always accepted that).</summary>
    [Fact]
    public void Either_half_alone_still_renders_something_answerable()
    {
        Assert.Equal("work", Ask.Choices("""[{"name":"work"}]""").Single().Value);
        Assert.Equal("w-1", Ask.Choices("""[{"id":"w-1"}]""").Single().Label);
    }

    [Fact]
    public void An_answer_matches_by_value_or_by_label_case_insensitively()
    {
        var choices = Ask.Choices(Ask.RepoInitCandidates("shaders"));
        Assert.Equal("yes", Ask.Match(choices, "YES")!.Value);
        Assert.Equal("no", Ask.Match(choices, "Not Now")!.Value);
    }

    /// <summary>Asking exists because guessing was wrong. A near-miss answer must therefore be
    /// refused rather than resolved — the one moment the operator actually told us the truth is
    /// the worst possible moment to start inferring.</summary>
    [Theory]
    [InlineData("maybe")]
    [InlineData("y")]
    [InlineData("")]
    [InlineData(null)]
    public void An_answer_the_question_never_offered_matches_nothing(string? picked)
    {
        Assert.Null(Ask.Match(Ask.Choices(Ask.RepoInitCandidates("shaders")), picked));
    }

    /// <summary>`new:NAME` is the concierge's "none of these, make one". A candidate list can
    /// never enumerate it, so a strict match would make the overlay strictly less capable than
    /// the command line it replaces — which is exactly the divergence D-L4 forbids.</summary>
    [Fact]
    public void New_name_is_free_form_and_passes_through()
    {
        Assert.True(Ask.IsFreeForm("new:harbour"));
        Assert.True(Ask.IsFreeForm("  NEW:harbour"));
        Assert.False(Ask.IsFreeForm("harbour"));
        Assert.False(Ask.IsFreeForm(null));
    }

    /// <summary>P3.A: the routing question's own candidates. Same round trip as the repo
    /// question's — written by the daemon, parsed by the window, held to it here.</summary>
    [Fact]
    public void A_route_questions_candidates_parse_back_to_the_projects_it_offered()
    {
        var choices = Ask.Choices(Ask.RouteCandidates(new[] { "engine", "tools" }));
        Assert.Equal(new[] { "engine", "tools" }, choices.Select(c => c.Value));
        Assert.Equal(new[] { "engine", "tools" }, choices.Select(c => c.Label));
        // Recency order is the daemon's (ProjectsByRecency), and the first one says why it is
        // first: an ordering the operator cannot see is one they have to guess at.
        Assert.False(string.IsNullOrEmpty(choices[0].Why));
        Assert.Null(choices[1].Why);
    }

    /// <summary>
    /// CLAUDE.md §3.1, and the reason this takes NAMES rather than paths: a routing question names
    /// projects, never somewhere to navigate. `ui-use`'s `the_ask_offers_no_filesystem_navigation`
    /// asserts the same property on the rendered choices; this is the source it renders.
    /// </summary>
    [Fact]
    public void A_route_question_carries_no_path_anywhere_in_it()
    {
        var blob = Ask.RouteCandidates(new[] { "engine", "project-zed" });
        Assert.DoesNotContain(@"\", blob);
        Assert.DoesNotContain("/", blob);
        Assert.DoesNotContain(":\\", blob);
    }

    /// <summary>A workspace whose projects were all detached between the ask and the render is a
    /// question with no buttons, not a crash — the same totality every other blob gets.</summary>
    [Fact]
    public void A_route_question_with_no_candidates_is_no_choices_and_never_an_exception()
    {
        Assert.Empty(Ask.Choices(Ask.RouteCandidates(Array.Empty<string>())));
    }
}
