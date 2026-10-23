using System.Collections.Frozen;

namespace FCommerce.Search.Core.Analysis.Hunspell;

/// <summary>
/// Reduces inflected words to their dictionary forms using a Hunspell dictionary (.aff + .dic), without expanding
/// every form: an affix is removed according to a rule and the resulting root must be in the dictionary with that
/// rule's flag. The whole dictionary is folded (see <see cref="Folding"/>), so words typed without diacritics are
/// recognized too: "wiertel", "wierteł" and "wiertłami" all lead to "wiertlo".
/// </summary>
public sealed class HunspellLemmatizer
{
    private readonly FrozenDictionary<string, string> flagsByRoot;
    private readonly FrozenDictionary<string, AffixRule[]> suffixesByAppend;
    private readonly AffixRule[] prefixes;
    private readonly int longestSuffix;

    private HunspellLemmatizer(FrozenDictionary<string, string> flagsByRoot, IReadOnlyList<AffixRule> rules)
    {
        this.flagsByRoot = flagsByRoot;
        suffixesByAppend = rules.Where(r => !r.IsPrefix).GroupBy(r => r.Append).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        prefixes = [.. rules.Where(r => r.IsPrefix)];
        longestSuffix = suffixesByAppend.Keys.Select(k => k.Length).DefaultIfEmpty(0).Max();
    }

    public int RootCount => flagsByRoot.Count;

    /// <summary>Builds the lemmatizer from the text of a Hunspell .aff and .dic file (already decoded).</summary>
    public static HunspellLemmatizer Parse(string affix, string dictionary)
    {
        var lettersByFolding = AffixRule.GroupByFolding(dictionary.ToHashSet());
        var rules = new List<AffixRule>();
        var crossProduct = new Dictionary<(string Kind, char Flag), bool>();
        foreach (var line in affix.Split('\n'))
        {
            var fields = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] is not ("SFX" or "PFX") || fields[1].Length != 1)
            {
                continue;
            }

            var key = (fields[0], fields[1][0]);
            if (fields.Length == 4)
            {
                crossProduct[key] = fields[2] == "Y"; // header: SFX a Y 16
                continue;
            }

            rules.Add(new AffixRule(
                key.Item2,
                IsPrefix: fields[0] == "PFX",
                crossProduct.GetValueOrDefault(key),
                Strip: fields[2] == "0" ? string.Empty : Folding.Fold(fields[2]),
                Append: fields[3] == "0" ? string.Empty : Folding.Fold(fields[3]),
                AffixRule.ParseCondition(fields[4], lettersByFolding)));
        }

        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in dictionary.Split('\n').Skip(1))
        {
            var entry = line.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var root = Folding.Fold(slash < 0 ? entry : entry[..slash]);
            var flags = slash < 0 ? string.Empty : entry[(slash + 1)..];
            // Folding merges homographs (e.g. "kat"/"kąt"); a root keeps the union of their flags.
            roots[root] = roots.TryGetValue(root, out var existing) ? string.Concat(existing, flags) : flags;
        }

        return new HunspellLemmatizer(roots.ToFrozenDictionary(StringComparer.Ordinal), rules);
    }

    public bool IsKnown(string foldedWord) => Lemmas(foldedWord).Count > 0;

    /// <summary>Dictionary forms of a folded word; empty when the word is unknown.</summary>
    public IReadOnlyList<string> Lemmas(string foldedWord)
    {
        var lemmas = new List<string>();
        if (flagsByRoot.ContainsKey(foldedWord))
        {
            lemmas.Add(foldedWord);
        }

        AddSuffixRoots(foldedWord, requiredFlag: null, lemmas);

        foreach (var prefix in prefixes)
        {
            if (!foldedWord.StartsWith(prefix.Append, StringComparison.Ordinal) || foldedWord.Length == prefix.Append.Length)
            {
                continue;
            }

            var root = prefix.Strip + foldedWord[prefix.Append.Length..];
            if (!prefix.MatchesRoot(root))
            {
                continue;
            }

            // The prefix stays in the lemma: a negating prefix makes a different word for search purposes
            // ("nierdzewny" — stainless — must not match "rdzewny" — rusting).
            if (HasFlag(root, prefix.Flag))
            {
                Add(lemmas, prefix.Append + root[prefix.Strip.Length..]);
            }

            if (prefix.CrossProduct)
            {
                var prefixed = new List<string>();
                AddSuffixRoots(root, requiredFlag: prefix.Flag, prefixed);
                foreach (var lemma in prefixed)
                {
                    Add(lemmas, prefix.Append + lemma[prefix.Strip.Length..]);
                }
            }
        }

        return lemmas;
    }

    private void AddSuffixRoots(string word, char? requiredFlag, List<string> lemmas)
    {
        for (var length = 0; length <= Math.Min(longestSuffix, word.Length - 1); length++)
        {
            if (!suffixesByAppend.TryGetValue(word[^length..], out var rules))
            {
                continue;
            }

            var stem = word[..^length];
            foreach (var rule in rules)
            {
                if (requiredFlag is not null && !rule.CrossProduct)
                {
                    continue;
                }

                var root = stem + rule.Strip;
                if (rule.MatchesRoot(root) && HasFlag(root, rule.Flag) && (requiredFlag is null || HasFlag(root, requiredFlag.Value)))
                {
                    Add(lemmas, root);
                }
            }
        }
    }

    private bool HasFlag(string root, char flag) =>
        flagsByRoot.TryGetValue(root, out var flags) && flags.Contains(flag, StringComparison.Ordinal);

    private static void Add(List<string> lemmas, string lemma)
    {
        if (!lemmas.Contains(lemma))
        {
            lemmas.Add(lemma);
        }
    }
}
