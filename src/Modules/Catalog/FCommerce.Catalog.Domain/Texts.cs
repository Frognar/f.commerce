using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

internal static class Translations
{
    /// <summary>Parses "culture → text" pairs; each invalid culture is reported under its own key.</summary>
    public static Validated<LocalizedText> Parse(IReadOnlyDictionary<string, string>? raw, int maxLength) =>
        (raw ?? new Dictionary<string, string>())
            .Select(pair => ParseEntry(pair.Key, pair.Value, maxLength).AtField(pair.Key))
            .Sequence()
            .Map(entries => LocalizedText.Of(entries.Select(e => new KeyValuePair<CultureCode, string>(e.Culture, e.Text))));

    private static Validated<(CultureCode Culture, string Text)> ParseEntry(string culture, string? text, int maxLength) =>
        CultureCode.Parse(culture)
            .Bind(code => (text?.Trim().Length ?? 0) <= maxLength
                ? Result.Ok<(CultureCode, string), Error>((code, text ?? string.Empty))
                : Error.Validation("text.too-long", $"Text must not exceed {maxLength} characters."))
            .ToValidated();
}

/// <summary>Localized name with at least one translation.</summary>
public sealed record ProductName : IPersistedValue<ProductName, LocalizedText>
{
    public const int MaxLength = 300;

    private ProductName(LocalizedText text) => Text = text;

    public LocalizedText Text { get; }

    public static Validated<ProductName> Parse(IReadOnlyDictionary<string, string>? raw) =>
        NonEmpty(Translations.Parse(raw, MaxLength), "product-name.empty", "Product name needs at least one translation.")
            .Map(text => new ProductName(text));

    internal static Validated<LocalizedText> NonEmpty(Validated<LocalizedText> text, string code, string message) =>
        text.Match(
            value => value.IsEmpty ? Validated.Invalid<LocalizedText>(Error.Validation(code, message)) : Validated.Valid(value),
            errors => text);

    static ProductName IPersistedValue<ProductName, LocalizedText>.Rehydrate(LocalizedText raw) => new(raw);

    LocalizedText IPersistedValue<ProductName, LocalizedText>.Dehydrate() => Text;

    public override string ToString() => Text.ToString();
}

/// <summary>Localized category name with at least one translation.</summary>
public sealed record CategoryName : IPersistedValue<CategoryName, LocalizedText>
{
    public const int MaxLength = 200;

    private CategoryName(LocalizedText text) => Text = text;

    public LocalizedText Text { get; }

    public static Validated<CategoryName> Parse(IReadOnlyDictionary<string, string>? raw) =>
        ProductName.NonEmpty(Translations.Parse(raw, MaxLength), "category-name.empty", "Category name needs at least one translation.")
            .Map(text => new CategoryName(text));

    static CategoryName IPersistedValue<CategoryName, LocalizedText>.Rehydrate(LocalizedText raw) => new(raw);

    LocalizedText IPersistedValue<CategoryName, LocalizedText>.Dehydrate() => Text;

    public override string ToString() => Text.ToString();
}
