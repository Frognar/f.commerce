namespace FCommerce.Search.Core.Analysis.Hunspell;

/// <summary>One character position of an affix condition: a set of characters, possibly negated ("[^ie]").</summary>
internal sealed record ConditionPart(string Characters, bool Negated, bool Any)
{
    public bool Matches(char c) => Any || (Characters.Contains(c, StringComparison.Ordinal) != Negated);
}

/// <summary>
/// A Hunspell affix rule: a root with <see cref="Flag"/> becomes a word by removing <see cref="Strip"/> from the
/// root's end (start for prefixes) and adding <see cref="Append"/>, provided the root matches the condition.
/// </summary>
internal sealed record AffixRule(char Flag, bool IsPrefix, bool CrossProduct, string Strip, string Append, IReadOnlyList<ConditionPart> Condition)
{
    /// <summary>Whether the condition matches the end (suffix) or the start (prefix) of the root.</summary>
    public bool MatchesRoot(string root)
    {
        if (root.Length < Condition.Count)
        {
            return false;
        }

        var offset = IsPrefix ? 0 : root.Length - Condition.Count;
        for (var i = 0; i < Condition.Count; i++)
        {
            if (!Condition[i].Matches(root[offset + i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses "[^i]e" / "." / "[km]anie" into parts, folding every character. In a negated class a folded letter is
    /// excluded only if every letter folding to it was excluded: "[^ś]" must not become "[^s]" (it would reject
    /// "opaska" → "opasek") and "[^l]" must not reject "ł" ("cwał" → "cwały"). This over-generates slightly
    /// ("[^o]" no longer excludes "o", since "ó" was allowed), which is harmless for search.
    /// </summary>
    public static IReadOnlyList<ConditionPart> ParseCondition(string condition, ILookup<string, char> lettersByFolding)
    {
        if (condition is "." or "")
        {
            return [];
        }

        var parts = new List<ConditionPart>();
        for (var i = 0; i < condition.Length; i++)
        {
            if (condition[i] == '[')
            {
                var end = condition.IndexOf(']', i);
                var negated = condition[i + 1] == '^';
                var characters = condition[(i + (negated ? 2 : 1))..end];
                var folded = negated ? FoldExcluded(characters, lettersByFolding) : Folding.Fold(characters);
                parts.Add(new ConditionPart(folded, negated, Any: false));
                i = end;
            }
            else if (condition[i] == '.')
            {
                parts.Add(new ConditionPart(string.Empty, false, Any: true));
            }
            else
            {
                parts.Add(new ConditionPart(Folding.Fold(condition[i].ToString()), false, Any: false));
            }
        }

        return parts;
    }

    /// <summary>The dictionary's letters grouped by what they fold to: 'l' → "lł", 's' → "sś".</summary>
    public static ILookup<string, char> GroupByFolding(IEnumerable<char> alphabet) =>
        alphabet.Where(char.IsLetter).Select(char.ToLowerInvariant).Distinct().ToLookup(c => Folding.Fold(c.ToString()));

    private static string FoldExcluded(string excluded, ILookup<string, char> lettersByFolding) =>
        string.Concat(excluded
            .Select(c => Folding.Fold(c.ToString()))
            .Distinct()
            .Where(folded => lettersByFolding[folded].All(excluded.Contains)));
}
