namespace Dodona.Testing;

/// <summary>
/// HOW a test double is kept honest -- the question "what stops this fake drifting away from the
/// thing it stands in for?" answered by a declaration rather than by somebody remembering
/// (docs/TEST-ARCHITECTURE-PLAN.md 3.1, 3.2).
/// </summary>
enum Anchor
{
    /// <summary>
    /// The double and its <see cref="DoubleAttribute.Real"/> share an interface that PRODUCTION
    /// implements more than once, so growing the interface breaks a shipping caller before it
    /// breaks a test.
    ///
    /// The count deliberately EXCLUDES every type carrying <see cref="DoubleAttribute"/> (review
    /// finding 2): `IRecognizer`'s two implementers are `DeepgramRecognizer` and
    /// `FakeRecognizer`, and one of them IS the double -- so a naive "two implementers" rule is
    /// satisfied by the fake counting itself.
    ///
    /// It reaches D1 (shape) and NOTHING ELSE, which is why it is never a sole anchor: see
    /// <see cref="DoubleAttribute.Contract"/>.
    /// </summary>
    Interface,

    /// <summary>
    /// For anything standing in for something OUTSIDE this repository, where no interface and no
    /// in-process contract can help: real recorded bytes, replayed through the REAL parser
    /// (plan 3.4). `DodonaFakeAgent` is anchored this way and carries no attribute at all --
    /// it is a program, not a type (D-T27), and its row lives in double-assemblies.tsv.
    /// </summary>
    Corpus,

    /// <summary>
    /// The deliberately weakest kind: the double is installed by assigning the SAME static
    /// delegate production reads, so there is one landing site and no parallel path. Invalid
    /// without a <see cref="DoubleAttribute.Wire"/>.
    /// </summary>
    Landing,
}

/// <summary>
/// ══ THE DOUBLE LEDGER: a declaration on every test double in this repository ══
///
/// It has NO BEHAVIOUR. Two rungs read it, and they answer two different questions because one
/// instrument cannot answer both (plan 3.2, redesigned after the adversarial review):
///
/// - **Rung 1, POPULATION.** A static text scan of every `src\**\*.cs` and `tests\**\*.cs` inside
///   Repo-Lint (I8, so `dev gate` stays at TEN assertions -- D-T23). It asserts that every
///   `Fake*`/`Recording*` type carries this attribute, that nothing is named `Stub*`/`Mock*`,
///   that every `Wire` resolves to a `wires.tsv` row, and that every project declaring a
///   `[Double]` appears in `tests\ledger\double-assemblies.tsv`.
///
///   **A TEXT SCAN CANNOT MISS AN ASSEMBLY**, and that is the whole reason the population
///   question lives there. The first design asked `Assembly.GetExecutingAssembly().GetTypes()`
///   from `Dodona.Tests`, whose one ProjectReference is `src\Dodona` -- so it enumerated a
///   population containing NONE of the three doubles that already existed, two of which sit in
///   `src\DodonaUi` (a net8.0 project cannot even load it) and one of which is a standalone exe.
///   A mechanism green because it is looking at an empty set is the routing ladder's own failure
///   shape, in the section written to prevent it. The scan also runs on a tree that will not
///   compile, which is what `tools\dev.ps1` exists for and which reflection can never have.
///
/// - **Rung 2, ANCHOR SEMANTICS.** Reflection, per assembly, in that assembly's own test project
///   (`tests\Dodona.Tests\Doubles\DoubleLedgerTests.cs` and
///   `tests\Dodona.Ui.Tests\DoubleLedgerTests.cs`), over an EXPLICIT assembly list and never
///   `GetExecutingAssembly()`. Counting implementers and resolving contracts are semantic
///   questions a text scan answers badly.
///
/// **WHY THIS LIVES IN `src\Dodona` AND IS LINKED INTO `DodonaUi`.** Two of the three existing
/// doubles are in `src\` and always will be: `FakeRecognizer` is *the implementation the suites
/// use*, and `DODONA_UI_MIC=off` returning it is what stops a suite opening the operator's
/// microphone (CLAUDE.md 4's incident with a bill attached). An attribute that only test
/// projects could carry would therefore be an attribute two of the three doubles could not
/// carry. `DodonaUi.csproj` links this file by source, the house's own mechanism, used here for
/// the weakest possible thing -- an attribute with no logic in it.
///
/// **IT IS NOT LINKED INTO `DodonaFakeAgent`** (D-B7, D-T27). That project deliberately
/// references nothing, and anchoring a 545-line exe by reflecting over one of its classes would
/// assert nothing about the wire shapes it emits, which is the only thing anyone cares about.
/// Its anchor is the corpus rung, which is file-based end to end.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
sealed class DoubleAttribute : Attribute
{
    public DoubleAttribute(Anchor anchor, Type real)
    {
        Anchor = anchor;
        Real = real;
    }

    public Anchor Anchor { get; }

    /// <summary>
    /// The thing this double stands in for, as a <c>Type</c> and NEVER a string: a string name
    /// is itself a hand copy, and hand copies are what this whole mechanism exists to stop.
    /// </summary>
    public Type Real { get; }

    /// <summary>
    /// `<suite>:<check>` -- A WIRE THIS DOUBLE DOES NOT REPLACE, and which is therefore still
    /// proved against the real machinery (plan 3.1's second hard rule against D0). Rung 1
    /// resolves it against a `tests\ledger\wires.tsv` row and rung 2 re-checks that a suite still
    /// registers the check.
    ///
    /// **WHAT THIS IS NOT** (plan 3.3.1, review finding 4). It is an anti-rot check on the
    /// register: a double may not stand beside a wire that has been deleted or renamed. It is
    /// NOT proof that the wire still exercises the path the way the operator runs it -- a
    /// surviving name can be green while asserting something strictly weaker, and no name check
    /// can see that. `wires.tsv`'s `owner_body_sha` is the (speed-bump, not proof) answer to the
    /// silent-narrowing half, and it is populated at W7.
    /// </summary>
    public string? Wire { get; set; }

    /// <summary>
    /// The NAME of the abstract contract class holding the facts both this double and
    /// <see cref="Real"/> must satisfy -- one body, two subjects (D2).
    ///
    /// ══ IT IS A STRING, AND THE PLAN SAID `typeof`. THAT WAS NOT BUILDABLE ══
    ///
    /// docs/TEST-ARCHITECTURE-PLAN.md 3.2 writes `Contract = typeof(LaneSinkContract)`. A
    /// contract class holds `[Fact]`s, so it lives in a TEST project; two of the three doubles
    /// live in PRODUCTION assemblies (`src\DodonaUi`), and `src` cannot reference `tests`.
    /// `typeof` is therefore impossible for exactly the doubles the redesign exists to reach,
    /// and it would have compiled only for a double that lived in a test project -- the
    /// population the first design already failed on.
    ///
    /// So it is a name, and the hand copy that creates is CLOSED BY ENFORCEMENT rather than by
    /// care: `Interface_is_never_a_sole_anchor` resolves the name inside the test assembly and
    /// goes RED if it names nothing, if what it names is not abstract, or if it has fewer than
    /// two concrete subclasses (one supplying the double, one the real thing). A name that does
    /// not resolve is a red test, not a stale comment.
    /// </summary>
    public string? Contract { get; set; }

    /// <summary>
    /// One sentence naming a behavioural gap between this double and <see cref="Real"/> that is
    /// KNOWN and not closable in process.
    ///
    /// **It is visibility, not a catch, and must never be sold as one** (plan 3.2). It does not
    /// stop the drift; it makes the drift named, counted and ticketed -- the same discipline
    /// `no-seam-yet` carries (D-T21). Requires <see cref="Issue"/>; rung 1 refuses one without.
    /// Counted in `dev gate`'s ledger reading line, so it cannot hide.
    /// </summary>
    public string? KnownDivergence { get; set; }

    /// <summary>The open tracker issue for <see cref="KnownDivergence"/>. Rung 1 refuses a
    /// divergence with no issue: an untracked gap is a gap nobody will ever close.</summary>
    public int Issue { get; set; }

    /// <summary>
    /// ══ A DECLARATION THE PLAN DID NOT HAVE, AND WITHOUT WHICH IT DOES NOT SHIP ══
    ///
    /// The tracker issue for: *the interface this double shares with <see cref="Real"/> has only
    /// ONE shipping implementation, so `Interface` buys less here than it does elsewhere.*
    ///
    /// **Why this exists.** Plan 3.2 states the `Interface` rule as ">= 2 implementers that do
    /// NOT carry [Double]", says in as many words that `IRecognizer` does not qualify, and then
    /// 3.6 anchors `FakeRecognizer` as `Interface` anyway and calls it *"RED on day one under the
    /// corrected rule, deliberately"*. The two halves of the remedy it offers -- a `Contract` and
    /// a `KnownDivergence` -- are both about BEHAVIOUR (D2) and neither one changes an
    /// implementer count. So as written, the mechanism ships a permanently failing unit test,
    /// which is a gate people learn to ignore: the same disease as a gate that is always green.
    ///
    /// The rule is kept at >= 2 rather than weakened to >= 1, because >= 1 would bless every
    /// interface that exists only as a test seam. Instead the shortfall becomes what this repo
    /// does with every other gap it cannot close: NAMED, COUNTED and TICKETED. Rung 1 refuses a
    /// value that is not a positive issue number, rung 2 prints the surviving implementers BY
    /// NAME so a reader can see whether the second one ships, and `dev gate`'s ledger reading
    /// counts these separately from `KnownDivergence`.
    ///
    /// **It is not a debt to be worked off.** `IRecognizer` has one shipping implementation
    /// because there is one speech engine; inventing a second would be a third fake needing its
    /// own anchor. What the declaration buys is that the weakness cannot be silent.
    /// </summary>
    public int SeamOnlyInterface { get; set; }
}
