using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog;

internal static class Cultures
{
    /// <summary>Preference-ordered cultures of a query; at least one is required.</summary>
    public static Result<EquatableArray<CultureCode>, Error> Parse(IReadOnlyList<string>? raw) =>
        raw is null or { Count: 0 }
            ? Error.Validation("cultures.empty", "At least one culture is required.")
            : raw.Traverse(CultureCode.Parse).Map(codes => codes.ToEquatableArray());

    /// <summary>Best translation, or any translation when none of the preferred cultures has one.</summary>
    public static string Resolve(this LocalizedText text, EquatableArray<CultureCode> cultures) =>
        text.Resolve(cultures[0], [.. cultures.Skip(1)])
        ?? text.Translations.Values.FirstOrDefault()
        ?? string.Empty;
}
