using FCommerce.Catalog.Domain;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Catalog.Search;

/// <summary>Storage shape of a <see cref="SearchEntry"/> in the search engine (plain JSON-friendly types).</summary>
[NullableEdge]
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
            entry.Gtin.OrNull(),
            entry.Name,
            entry.Description.OrNull(),
            entry.Unit,
            entry.ImageId.OrNull(),
            [.. entry.Categories],
            [.. entry.Facets],
            [.. entry.FacetNames],
            [.. entry.AttributeValues]);
}
