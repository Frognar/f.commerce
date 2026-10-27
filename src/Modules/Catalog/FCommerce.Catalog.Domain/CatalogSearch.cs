using System.Text;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Catalog.Domain;

/// <summary>What the search index stores for one product in one language (texts already resolved).</summary>
/// <param name="Categories">The product's category and all its ancestors, so filtering by a category includes subcategories.</param>
/// <param name="Facets">One <see cref="CatalogSearch.FacetToken"/> per attribute value, for filtering and counting.</param>
/// <param name="FacetNames">"code␟name" pairs, to label facets in this language.</param>
public sealed record SearchEntry(
    string Id,
    string Sku,
    Maybe<string> Gtin,
    string Name,
    Maybe<string> Description,
    string Unit,
    Maybe<string> ImageId,
    EquatableArray<string> Categories,
    EquatableArray<string> Facets,
    EquatableArray<string> FacetNames,
    EquatableArray<string> AttributeValues,
    bool Discontinued);

/// <summary>A facet (attribute) with the values found in the current results.</summary>
public sealed record Facet(string Code, string Name, EquatableArray<FacetValue> Values);

public sealed record FacetValue(string Value, int Count, bool Selected);

public static class CatalogSearch
{
    /// <summary>Unit separator: codes are free of control characters, so it cannot occur inside a code.</summary>
    public const char Separator = '\u001f';

    /// <summary>Search engines restrict ids to [A-Za-z0-9_-]; SKUs are free text, so the id is the SKU's UTF-8 in hex.</summary>
    public static string DocumentId(Sku sku) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(sku.Value));

    public static string FacetToken(string code, string value) => $"{code}{Separator}{value}";

    public static Maybe<(string Code, string Value)> ParseFacetToken(string token) =>
        token.IndexOf(Separator, StringComparison.Ordinal) is var at and > 0
            ? (token[..at], token[(at + 1)..])
            : Maybe<(string Code, string Value)>.None;

    public static SearchEntry Entry(Product product, EquatableArray<Category> categoryPath, CultureCode culture, CultureCode fallback)
    {
        string Text(LocalizedText text) => text.ResolveOrAny(culture, fallback);

        var attributes = product.Content.Attributes
            .Select(a => (Code: a.Code.Value, Name: Text(a.Name), Value: Text(a.Value)))
            .Where(a => a.Value.Length > 0)
            .ToList();

        return new SearchEntry(
            DocumentId(product.Sku),
            product.Sku.Value,
            product.Content.Gtin.Map(gtin => gtin.Value),
            Text(product.Content.Name.Text),
            product.Content.Description.IsEmpty ? Maybe<string>.None : Text(product.Content.Description),
            product.Content.Unit.Value,
            product.Content.Images.Select(image => image.Value).FirstOrNone(),
            categoryPath.Select(c => c.Code.Value).ToEquatableArray(),
            attributes.Select(a => FacetToken(a.Code, a.Value)).Distinct().ToEquatableArray(),
            attributes.Select(a => FacetToken(a.Code, a.Name)).Distinct().ToEquatableArray(),
            attributes.Select(a => a.Value).Distinct().ToEquatableArray(),
            product.Status == ProductStatus.Discontinued);
    }

    /// <summary>
    /// Builds facets from value counts. An attribute is shown when it lets the user narrow the results (two or more
    /// values) or when one of its values is selected. Facets are ordered by name, values naturally ("8 mm" before "10 mm").
    /// </summary>
    public static EquatableArray<Facet> Facets(
        IReadOnlyDictionary<string, int> valueCounts,
        IReadOnlyDictionary<string, int> names,
        IReadOnlySet<(string Code, string Value)> selected)
    {
        var labels = names.Keys
            .Choose(ParseFacetToken)
            .GroupBy(n => n.Code)
            .ToDictionary(g => g.Key, g => g.First().Value);

        return valueCounts
            .Choose(pair => ParseFacetToken(pair.Key).Map(token => (token.Code, token.Value, Count: pair.Value)))
            .Concat(selected.Select(s => (s.Code, s.Value, Count: 0)))
            .GroupBy(v => v.Code)
            .Select(attribute => new Facet(
                attribute.Key,
                labels.GetValueOrDefault(attribute.Key, attribute.Key),
                attribute
                    .GroupBy(v => v.Value)
                    .Select(value => new FacetValue(value.Key, value.Max(v => v.Count), selected.Contains((attribute.Key, value.Key))))
                    .OrderBy(v => v.Value, NaturalComparer.Instance)
                    .ToEquatableArray()))
            .Where(f => f.Values.Count >= 2 || f.Values.Any(v => v.Selected))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToEquatableArray();
    }
}

/// <summary>Compares strings with embedded numbers by their numeric value: "M8" &lt; "M10", "8 mm" &lt; "10 mm".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        var (i, j) = (0, 0);
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (startX, startY) = (i, j);
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byNumber = numberX.Length != numberY.Length
                    ? numberX.Length.CompareTo(numberY.Length)
                    : string.CompareOrdinal(numberX, numberY);
                if (byNumber != 0)
                {
                    return byNumber;
                }
            }
            else
            {
                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }
        }

        var byRest = (x.Length - i).CompareTo(y.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(x, y); // total order: "007" and "7" are not equal
    }
}
