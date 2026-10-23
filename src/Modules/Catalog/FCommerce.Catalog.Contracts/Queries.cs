using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Contracts;

// Texts are resolved for the first matching culture in Cultures (preference order, e.g. ["en", "pl"]).

/// <param name="Filters">Selected attribute values: values of one attribute are alternatives (OR), attributes combine with AND.</param>
public sealed record SearchProducts(
    IReadOnlyList<string> Cultures,
    string? Text,
    string? CategoryCode,
    IReadOnlyList<AttributeFilter>? Filters = null,
    int Page = 1,
    int PageSize = 24) : IQuery<Result<ProductPage, Error>>;

public sealed record AttributeFilter(string Code, string Value);

/// <param name="Facets">Attributes that can narrow the results, with value counts.</param>
public sealed record ProductPage(IReadOnlyList<ProductSummary> Items, int Page, int PageSize, int TotalCount, IReadOnlyList<FacetView> Facets)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record FacetView(string Code, string Name, IReadOnlyList<FacetValueView> Values);

public sealed record FacetValueView(string Value, int Count, bool Selected);

public sealed record ProductSummary(string Sku, string Name, string Unit, string? ImageId, string? CategoryCode);

/// <summary>Active product details; not found for unknown or inactive products.</summary>
public sealed record GetProduct(IReadOnlyList<string> Cultures, string Sku) : IQuery<Result<ProductDetails, Error>>;

public sealed record ProductDetails(
    string Sku,
    string Name,
    string? Description,
    string Unit,
    string? Gtin,
    IReadOnlyList<string> ImageIds,
    IReadOnlyList<ProductAttributeView> Attributes,
    IReadOnlyList<CategoryReference> CategoryPath);

public sealed record ProductAttributeView(string Code, string Name, string Value);

public sealed record CategoryReference(string Code, string Name);

public sealed record GetCategoryTree(IReadOnlyList<string> Cultures) : IQuery<Result<IReadOnlyList<CategoryView>, Error>>;

public sealed record CategoryView(string Code, string Name, IReadOnlyList<CategoryView> Children);
