namespace FCommerce.Search.Core.Analysis;

/// <summary>
/// Rule-based stemming for words a dictionary does not know (trade names, jargon, English words in Polish
/// texts). Conservative: words with digits are never stemmed and a stem keeps at least three letters.
/// </summary>
public static class Stemmers
{
    // Folded Polish inflectional endings, longest first.
    private static readonly string[] PolishEndings =
    [
        "owie", "ami", "ach", "owi", "ego", "emu", "ymi", "imi", "ych", "ich", "iem", "om", "ow", "ej", "ym", "im", "em",
        "y", "i", "a", "e", "u", "o",
    ];

    public static string PolishFallback(string folded)
    {
        if (folded.Length < 5 || folded.Any(char.IsDigit))
        {
            return folded;
        }

        foreach (var ending in PolishEndings)
        {
            if (folded.EndsWith(ending, StringComparison.Ordinal) && folded.Length - ending.Length >= 3)
            {
                return folded[..^ending.Length];
            }
        }

        return folded;
    }

    /// <summary>English plural/possessive stripping: "bolts" → "bolt", "boxes" → "box", "batteries" → "battery".</summary>
    public static string EnglishLight(string folded)
    {
        if (folded.Length < 4 || folded.Any(char.IsDigit))
        {
            return folded;
        }

        return folded switch
        {
            _ when folded.EndsWith("ies", StringComparison.Ordinal) && folded.Length > 4 => folded[..^3] + "y",
            _ when folded.EndsWith("sses", StringComparison.Ordinal) => folded[..^2],
            _ when folded.EndsWith("xes", StringComparison.Ordinal) || folded.EndsWith("zes", StringComparison.Ordinal)
                || folded.EndsWith("ches", StringComparison.Ordinal) || folded.EndsWith("shes", StringComparison.Ordinal) => folded[..^2],
            _ when folded.EndsWith('s') && !folded.EndsWith("ss", StringComparison.Ordinal)
                && !folded.EndsWith("us", StringComparison.Ordinal) && !folded.EndsWith("is", StringComparison.Ordinal) => folded[..^1],
            _ => folded,
        };
    }
}
