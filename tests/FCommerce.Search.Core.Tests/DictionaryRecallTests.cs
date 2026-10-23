using System.Text.RegularExpressions;
using FCommerce.Search.Core.Analysis;

namespace FCommerce.Search.Core.Tests;

/// <summary>
/// Property check over the real dictionary: forms generated from the original (unfolded) affix rules, then folded
/// as a buyer might type them, must lead back to their root. Guards the folding of rules and conditions.
/// </summary>
public sealed class DictionaryRecallTests
{
    private sealed record Rule(char Flag, string Strip, string Append, Regex Condition);

    [Fact]
    public void Every_generated_form_of_sampled_roots_is_reduced_to_its_root()
    {
        var rules = PolishDictionary.AffixText.Split('\n')
            .Select(line => line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 5 && f[0] == "SFX")
            .Select(f => new Rule(f[1][0], f[2] == "0" ? "" : f[2], f[3] == "0" ? "" : f[3], new Regex($"{(f[4] == "." ? "" : f[4])}$", RegexOptions.CultureInvariant)))
            .GroupBy(r => r.Flag)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sampled = PolishDictionary.DictionaryText.Split('\n').Skip(1)
            .Where((_, i) => i % 25 == 0)
            .Select(line => line.Trim().Split('/'))
            .Where(entry => entry.Length == 2 && entry[0].Length > 2);

        var checkedForms = 0;
        var misses = new List<string>();
        foreach (var entry in sampled)
        {
            var (root, flags) = (entry[0], entry[1]);
            foreach (var rule in flags.Where(rules.ContainsKey).SelectMany(flag => rules[flag]))
            {
                if (!rule.Condition.IsMatch(root) || !root.EndsWith(rule.Strip, StringComparison.Ordinal))
                {
                    continue;
                }

                var form = root[..^rule.Strip.Length] + rule.Append;
                checkedForms++;
                if (!PolishDictionary.Instance.Lemmas(Folding.Fold(form)).Contains(Folding.Fold(root)))
                {
                    misses.Add($"{form} → {root}");
                }
            }
        }

        Assert.True(checkedForms > 50_000, $"Only {checkedForms} forms checked.");
        Assert.True(misses.Count == 0, $"{misses.Count} of {checkedForms} forms not reduced, e.g. {string.Join(", ", misses.Take(10))}");
    }
}
