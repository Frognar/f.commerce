using System.Globalization;
using System.Text;

namespace FCommerce.Catalog.Domain;

/// <summary>
/// Language-neutral text normalization for catalog search: lowercase, no diacritics ("Śruba" → "sruba"),
/// letters and digits only. The same function prepares stored documents and queries, so they always match.
/// </summary>
public static class SearchText
{
    public const int MaxTokens = 8;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = char.ToLowerInvariant(c) switch
            {
                'ł' => 'l',
                'ø' => 'o',
                'ß' => 's',
                var other => other,
            };

            if (char.IsLetterOrDigit(mapped))
            {
                builder.Append(mapped);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Distinct normalized words of a query, longest first (the most selective), at most <see cref="MaxTokens"/>.</summary>
    public static IReadOnlyList<string> Tokens(string? query) =>
        [.. Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().OrderByDescending(t => t.Length).Take(MaxTokens)];

    /// <summary>The searchable text of a product: identifiers, names, attribute values in every language.</summary>
    public static string Document(Product product)
    {
        var parts = new[] { product.Sku.Value, product.Content.Gtin?.Value }
            .Concat(product.Content.Name.Text.Translations.Values)
            .Concat(product.Content.Attributes.SelectMany(a => a.Value.Translations.Values));
        return $" {Normalize(string.Join(' ', parts))} ";
    }
}
