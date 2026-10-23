using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Contracts;

// Texts are resolved for the first matching culture in Cultures (preference order, e.g. ["en", "pl"]).

public sealed record SearchProducts(
    IReadOnlyList<string> Cultures,
    string? Text,
    string? CategoryCode,
    int Page = 1,
    int PageSize = 24) : IQuery<Result<ProductPage, Error>>;

public sealed record ProductPage(IReadOnlyList<ProductSummary> Items, int Page, int PageSize, int TotalCount)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record ProductSummary(string Sku, string Name, string Unit, string? ImageUrl, string? CategoryCode);

/// <summary>Active product details; not found for unknown or inactive products.</summary>
public sealed record GetProduct(IReadOnlyList<string> Cultures, string Sku) : IQuery<Result<ProductDetails, Error>>;

public sealed record ProductDetails(
    string Sku,
    string Name,
    string? Description,
    string Unit,
    string? Gtin,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<ProductAttributeView> Attributes,
    IReadOnlyList<CategoryReference> CategoryPath);

public sealed record ProductAttributeView(string Code, string Name, string Value);

public sealed record CategoryReference(string Code, string Name);

public sealed record GetCategoryTree(IReadOnlyList<string> Cultures) : IQuery<Result<IReadOnlyList<CategoryView>, Error>>;

public sealed record CategoryView(string Code, string Name, IReadOnlyList<CategoryView> Children);
