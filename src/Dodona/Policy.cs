using System.Text.RegularExpressions;

namespace Dodona;

/// <summary>
/// Which model and how much effort a piece of work gets (§9's levers, made policy).
///
/// A table, evaluated in code: instant, deterministic, and readable — you can see why it
/// chose what it chose without asking a model why a model chose something. Rules are
/// tried in order and the first match wins, so the table reads top-to-bottom as
/// "cheapest recognisable work first, then routine, then the expensive judgement calls",
/// and anything unrecognised falls through to the project's default.
///
/// The operator is not a tier at the top of this ladder — they are an override that can
/// land anywhere (§3): `@opus @max rewrite the merge fence` wins over every rule, and the
/// tokens are stripped before the agent ever sees the sentence.
/// </summary>
sealed record PolicyRule(string When, string Model, string Effort, string Why);

sealed record Choice(string Model, string Effort, string Why, bool Overridden)
{
    public string Describe => $"{Model}/{(Effort is { Length: > 0 } ? Effort : "default")}" +
                              (Overridden ? " (you said so)" : Why is { Length: > 0 } ? $" ({Why})" : "");
}

static class Policy
{
    static readonly HashSet<string> Models = new(StringComparer.OrdinalIgnoreCase)
        { "opus", "sonnet", "haiku", "fable" };
    static readonly HashSet<string> Efforts = new(StringComparer.OrdinalIgnoreCase)
        { "low", "medium", "high", "xhigh", "max" };

    /// <summary>The table Dodona ships with when a project says nothing. It encodes §9
    /// directly — mechanical work runs cheap and low, design-tier work runs expensive and
    /// high — and is deliberately short, because a long table nobody can predict is worse
    /// than a default everybody can.</summary>
    public static readonly PolicyRule[] Default =
    {
        new(@"\b(typo|spelling|rename|format|reformat|lint|whitespace|comment|comments|changelog|bump|version bump)\b",
            "haiku", "low", "mechanical"),
        new(@"\b(test|tests|unit test|coverage|flaky|assert)\b",
            "sonnet", "medium", "tests"),
        new(@"\b(design|architect|architecture|refactor|redesign|migrat\w*|schema|concurren\w*|race|deadlock|security|threat|protocol|invariant)\b",
            "opus", "max", "design-tier"),
    };

    /// <summary>Pull `@model` / `@effort` tokens off the front of a prompt. Returns the
    /// remaining text so the agent is never handed the operator's dispatch syntax.</summary>
    public static (string Text, string? Model, string? Effort) StripOverrides(string input)
    {
        string? model = null, effort = null;
        var text = input.TrimStart();
        while (text.StartsWith('@'))
        {
            var end = text.IndexOfAny(new[] { ' ', '\t', '\n' });
            var token = (end < 0 ? text[1..] : text[1..end]).Trim();
            if (Models.Contains(token)) model = token.ToLowerInvariant();
            else if (Efforts.Contains(token)) effort = token.ToLowerInvariant();
            else break;                                   // not ours: leave it for the agent
            text = end < 0 ? "" : text[(end + 1)..].TrimStart();
        }
        return (text.Length > 0 ? text : input.Trim(), model, effort);
    }

    /// <summary>What this text should run as. Overrides beat rules; rules beat defaults.</summary>
    public static Choice Resolve(string text, IReadOnlyList<PolicyRule> rules, string defaultModel, string defaultEffort,
                                 string? overrideModel = null, string? overrideEffort = null)
    {
        if (overrideModel is not null || overrideEffort is not null)
            return new Choice(overrideModel ?? defaultModel, overrideEffort ?? defaultEffort, "", true);

        foreach (var r in rules)
        {
            Match m;
            try { m = Regex.Match(text, r.When, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { continue; }       // a bad pattern in config must not break routing
            if (m.Success) return new Choice(r.Model, r.Effort, r.Why is { Length: > 0 } ? r.Why : m.Value, false);
        }
        return new Choice(defaultModel, defaultEffort, "default", false);
    }
}
