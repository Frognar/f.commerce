namespace FCommerce.Search.Core.Analysis;

/// <summary>A word of the input, already folded, with its position (for future phrase/proximity scoring).</summary>
public readonly record struct Token(string Text, int Position);

/// <summary>
/// Splits folded text into runs of letters and digits; everything else separates tokens.
/// "SR-M8x40, Ø10" → "sr", "m8x40", "o10".
/// </summary>
public static class Tokenizer
{
    public const int MaxTokenLength = 64;

    public static IReadOnlyList<Token> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var folded = Folding.Fold(text);
        var tokens = new List<Token>();
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var isWordChar = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (!isWordChar && start >= 0)
            {
                var length = Math.Min(i - start, MaxTokenLength);
                tokens.Add(new Token(folded.Substring(start, length), tokens.Count));
                start = -1;
            }
        }

        return tokens;
    }
}
