using FCommerce.Catalog.Domain;

namespace FCommerce.Catalog.Search;

/// <summary>Storage shape of a <see cref="SearchEntry"/> in the search engine (plain JSON-friendly types).</summary>
internal sealed record ProductSearchDocument(
    string Id,
    string Sku,
    string? Gtin,
    string Name,
    string? Description,
    string Unit,
    string? ImageId,
    string[] Categories,
    string[] Facets,
    string[] FacetNames,
    string[] AttributeValues)
{
    public static ProductSearchDocument From(SearchEntry entry) =>
        new(
            entry.Id,
            entry.Sku,
            entry.Gtin,
            entry.Name,
            entry.Description,
            entry.Unit,
            entry.ImageId,
            [.. entry.Categories],
            [.. entry.Facets],
            [.. entry.FacetNames],
            [.. entry.AttributeValues]);
}
