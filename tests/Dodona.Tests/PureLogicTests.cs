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
}
