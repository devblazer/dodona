using System.Reflection;

namespace Dodona.Testing.Ledger;

/// <summary>
/// RUNG 2 of the double ledger (docs/TEST-ARCHITECTURE-PLAN.md 3.2): the SEMANTIC questions,
/// answered by reflection over an EXPLICIT list of assemblies.
///
/// ══ ONE BODY, COMPILED TWICE, AND THAT IS THE POINT ══
///
/// This file is `Compile Include`-linked into tests\Dodona.Ui.Tests as well as living in
/// tests\Dodona.Tests, because there must be one reflection class PER DOUBLE-BEARING ASSEMBLY
/// and the two test projects cannot reference each other -- Dodona.Tests is net8.0 and
/// DodonaUi is net8.0-windows, which is the whole reason the second project exists. The
/// alternative was two hand copies of one algorithm, which is the failure this plan is about.
///
/// Linking also makes `typeof(DoubleAttribute)` resolve to the copy each assembly actually
/// carries: `Dodona` compiles src\Dodona\Testing\DoubleAttribute.cs, `DodonaUi` links the same
/// file, so there are two distinct CLR types with one name. Compiled into each test project
/// beside its own subject, each half asks about the attribute its own subject carries.
///
/// ══ NEVER GetExecutingAssembly() ══
///
/// The first design's fatal flaw. `Assembly.GetExecutingAssembly().GetTypes()` from Dodona.Tests
/// enumerates a population that cannot contain FakeRecognizer, Poses or DodonaFakeAgent, and it
/// goes GREEN because the set is empty. Every caller here passes the assemblies it means, and
/// <see cref="DoubleLedgerAssertions"/> cross-checks that list against
/// tests\ledger\double-assemblies.tsv in both directions -- so an assembly this project is
/// DECLARED to cover but does not load is a red test, not a silent pass.
/// </summary>
static class DoubleLedger
{
    internal sealed class Entry
    {
        public Type Type = typeof(object);
        public Anchor Anchor;
        public Type Real = typeof(object);
        public string? Wire;
        public string? Contract;
        public string? KnownDivergence;
        public int Issue;
        public int SeamOnlyInterface;

        public override string ToString() => Type.FullName + " [" + Type.Assembly.GetName().Name + "]";
    }

    /// <summary>Every type in <paramref name="assemblies"/>, tolerating a partial load. A WPF
    /// assembly can throw <c>ReflectionTypeLoadException</c> for a type whose dependency is not
    /// present, and losing the whole population to one bad type would be the empty-set failure
    /// arriving by a different road.</summary>
    public static IReadOnlyList<Type> Types(params Assembly[] assemblies)
    {
        var all = new List<Type>();
        foreach (var a in assemblies)
        {
            try { all.AddRange(a.GetTypes()); }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var t in ex.Types) if (t is not null) all.Add(t);
            }
        }
        return all;
    }

    public static bool IsDouble(Type t) => t.GetCustomAttribute<DoubleAttribute>() is not null;

    public static IReadOnlyList<Entry> In(params Assembly[] assemblies)
    {
        var found = new List<Entry>();
        foreach (var t in Types(assemblies))
        {
            var a = t.GetCustomAttribute<DoubleAttribute>();
            if (a is null) continue;
            found.Add(new Entry
            {
                Type = t,
                Anchor = a.Anchor,
                Real = a.Real,
                Wire = a.Wire,
                Contract = a.Contract,
                KnownDivergence = a.KnownDivergence,
                Issue = a.Issue,
                SeamOnlyInterface = a.SeamOnlyInterface,
            });
        }
        return found;
    }

    /// <summary>
    /// Concrete types implementing <paramref name="iface"/> that DO NOT themselves carry
    /// <c>[Double]</c>.
    ///
    /// THE EXCLUSION IS THE WHOLE FIX (review finding 2). `IRecognizer`'s two implementers are
    /// `DeepgramRecognizer` and `FakeRecognizer`, and one of them IS the double -- so a plain
    /// "two implementers" rule is satisfied by the fake counting itself, and any future
    /// `FakeX : IX` dropped into `src\` would anchor itself the same way.
    /// </summary>
    public static IReadOnlyList<Type> ShippingImplementers(Type iface, params Assembly[] assemblies)
    {
        var survivors = new List<Type>();
        foreach (var t in Types(assemblies))
        {
            if (t.IsInterface || t.IsAbstract) continue;
            if (!iface.IsAssignableFrom(t)) continue;
            if (IsDouble(t)) continue;
            survivors.Add(t);
        }
        return survivors;
    }

    /// <summary>
    /// The interfaces a double and its <c>Real</c> both implement AND WHICH THESE ASSEMBLIES
    /// DECLARE -- the candidates an <see cref="Anchor.Interface"/> anchor can rest on.
    ///
    /// ══ THE FRAMEWORK FILTER IS NOT TIDINESS. WITHOUT IT THE RULE SELF-SATISFIES ══
    ///
    /// MEASURED, on this mechanism's first run against the real tree: `FakeRecognizer` and
    /// `DeepgramRecognizer` share `IRecognizer` AND `IDisposable`. `IDisposable` has dozens of
    /// shipping implementers in any assembly, so the strongest shared interface came out as
    /// `IDisposable` and `Every_Interface_anchor_has_two_shipping_implementers` went GREEN over a
    /// double whose real interface has exactly ONE shipping implementation.
    ///
    /// That is review finding 2's failure wearing a different hat -- a count satisfied by
    /// something other than the thing being claimed. The anchor's claim is *"the compiler catches
    /// shape drift, because production implements this interface twice"*, and nobody grows
    /// `IDisposable`. So only interfaces declared in the assemblies under test are candidates.
    /// </summary>
    public static IReadOnlyList<Type> SharedInterfaces(Entry e, params Assembly[] loaded)
    {
        var ours = new HashSet<Assembly>(loaded);
        var mine = new HashSet<Type>(e.Type.GetInterfaces());
        var shared = new List<Type>();
        foreach (var i in e.Real.GetInterfaces())
            if (mine.Contains(i) && ours.Contains(i.Assembly)) shared.Add(i);
        return shared;
    }

    public static string Names(IEnumerable<Type> types)
    {
        var n = types.Select(t => t.FullName ?? t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return n.Length == 0 ? "(none)" : string.Join(", ", n);
    }
}
