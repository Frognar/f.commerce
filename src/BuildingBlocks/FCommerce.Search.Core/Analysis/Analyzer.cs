using FCommerce.Search.Core.Analysis.Hunspell;

namespace FCommerce.Search.Core.Analysis;

/// <summary>A token with the terms it is indexed/searched under: its folded surface form and its dictionary forms.</summary>
public sealed record AnalyzedToken(string Surface, IReadOnlyList<string> Lemmas, int Position);

/// <summary>
/// Language-specific text analysis: tokenize and fold, then reduce each word to its dictionary forms.
/// Indexing and querying use the same analyzer, so their terms always line up.
/// </summary>
public sealed class Analyzer(string language, Func<string, IReadOnlyList<string>> lemmatize)
{
    /// <summary>Bump whenever analysis output changes: stored indexes built with another version are rebuilt.</summary>
    public const int Version = 2;

    public string Language { get; } = language;

    public static Analyzer Polish(HunspellLemmatizer dictionary) =>
        new("pl", word => dictionary.Lemmas(word) is { Count: > 0 } lemmas ? lemmas : [Stemmers.PolishFallback(word)]);

    public static Analyzer English() => new("en", word => [Stemmers.EnglishLight(word)]);

    /// <summary>Folding only; for languages without a stemmer.</summary>
    public static Analyzer Simple(string language) => new(language, word => [word]);

    public IReadOnlyList<AnalyzedToken> Analyze(string? text) =>
        [.. Tokenizer.Tokenize(text).Select(token => new AnalyzedToken(token.Text, lemmatize(token.Text), token.Position))];

    /// <summary>Tokens without linguistic processing, for exact fields (codes, identifiers).</summary>
    public static IReadOnlyList<AnalyzedToken> AnalyzeExact(string? text) =>
        [.. Tokenizer.Tokenize(text).Select(token => new AnalyzedToken(token.Text, [], token.Position))];
}
