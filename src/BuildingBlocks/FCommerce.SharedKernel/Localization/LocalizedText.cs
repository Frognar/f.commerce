using System.Collections.Immutable;
using FCommerce.SharedKernel.Persistence;

namespace FCommerce.SharedKernel.Localization;

/// <summary>Text with translations per culture. Immutable, compared by value.</summary>
public sealed class LocalizedText : IEquatable<LocalizedText>, IPersistedValue<LocalizedText, IReadOnlyDictionary<string, string>>
{
    private LocalizedText(ImmutableSortedDictionary<CultureCode, string> translations) => Translations = translations;

    public static LocalizedText Empty { get; } = new(ImmutableSortedDictionary.Create<CultureCode, string>(CultureCodeComparer.Instance));

    public ImmutableSortedDictionary<CultureCode, string> Translations { get; }

    public bool IsEmpty => Translations.IsEmpty;

    public static LocalizedText Of(params ReadOnlySpan<(CultureCode Culture, string Text)> translations)
    {
        var text = Empty;
        foreach (var (culture, value) in translations)
        {
            text = text.With(culture, value);
        }

        return text;
    }

    public static LocalizedText Of(IEnumerable<KeyValuePair<CultureCode, string>> translations) =>
        translations.Aggregate(Empty, (text, pair) => text.With(pair.Key, pair.Value));

    /// <summary>Returns a copy with the translation set; blank text removes it.</summary>
    public LocalizedText With(CultureCode culture, string text) =>
        string.IsNullOrWhiteSpace(text)
            ? new(Translations.Remove(culture))
            : new(Translations.SetItem(culture, text.Trim()));

    /// <summary>
    /// Resolves the best translation: exact culture → its neutral language → any regional variant of
    /// that language → each fallback culture in order (with the same rules). Null when nothing matches.
    /// </summary>
    public string? Resolve(CultureCode culture, params ReadOnlySpan<CultureCode> fallbacks)
    {
        var resolved = ResolveSingle(culture);
        foreach (var fallback in fallbacks)
        {
            resolved ??= ResolveSingle(fallback);
        }

        return resolved;
    }

    private string? ResolveSingle(CultureCode culture)
    {
        if (Translations.TryGetValue(culture, out var exact))
        {
            return exact;
        }

        if (Translations.TryGetValue(culture.Language, out var neutral))
        {
            return neutral;
        }

        var language = culture.Language;
        return Translations.FirstOrDefault(t => t.Key.Language == language).Value;
    }

    public bool Equals(LocalizedText? other) =>
        other is not null && Translations.Count == other.Translations.Count
        && Translations.All(t => other.Translations.TryGetValue(t.Key, out var value) && value == t.Value);

    public override bool Equals(object? obj) => Equals(obj as LocalizedText);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (culture, text) in Translations)
        {
            hash.Add(culture);
            hash.Add(text);
        }

        return hash.ToHashCode();
    }

    static LocalizedText IPersistedValue<LocalizedText, IReadOnlyDictionary<string, string>>.Rehydrate(IReadOnlyDictionary<string, string> raw) =>
        new(raw.ToImmutableSortedDictionary(pair => Rehydrate<CultureCode, string>(pair.Key), pair => pair.Value, CultureCodeComparer.Instance));

    IReadOnlyDictionary<string, string> IPersistedValue<LocalizedText, IReadOnlyDictionary<string, string>>.Dehydrate() =>
        Translations.ToDictionary(pair => pair.Key.Value, pair => pair.Value);

    private static T Rehydrate<T, TRaw>(TRaw raw)
        where T : IPersistedValue<T, TRaw> =>
        T.Rehydrate(raw);

    public override string ToString() => string.Join(", ", Translations.Select(t => $"{t.Key}: {t.Value}"));

    private sealed class CultureCodeComparer : IComparer<CultureCode>
    {
        public static readonly CultureCodeComparer Instance = new();

        public int Compare(CultureCode? x, CultureCode? y) => string.CompareOrdinal(x?.Value, y?.Value);
    }
}
