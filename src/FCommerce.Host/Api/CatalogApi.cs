using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Media;
using FCommerce.Host.Pages.Catalog;
using FCommerce.Host.Security;
using FCommerce.Inventory.Contracts;
using FCommerce.Media.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace FCommerce.Host.Api;

internal static partial class PublicApiEndpoints
{
    private static void MapCatalog(RouteGroupBuilder api)
    {
        var catalog = api.MapGroup("/catalog");
        catalog.MapGet("/products", SearchProductsAsync)
            .WithSummary("Searches the catalog; every product comes with the price and stock of the key's customer. Texts follow Accept-Language.");
        catalog.MapGet("/products/{sku}", GetProductAsync)
            .WithSummary("One product with its attributes, images, every price tier and the stock per warehouse.");
        catalog.MapGet("/categories", GetCategoriesAsync)
            .WithSummary("The category tree.");
    }

    private static async Task<IResult> SearchProductsAsync(
        [AsParameters] ProductSearch search,
        IQueryHandler<SearchProducts, Result<ProductPage, Error>> products,
        CatalogOffers offers,
        AccessControl access,
        SupportedCultures cultures,
        CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(AccessAction.Read, new Resource.Catalog(), cancellationToken))
        {
            return ApiResults.Forbidden();
        }

        var page = await products.HandleAsync(
            new SearchProducts(cultures.ForRequest(), search.Query, search.Category, [], Math.Max(search.Page, 1), search.PageSize > 0 ? search.PageSize : 24),
            cancellationToken);
        return await page.Match(
            async found =>
            {
                var priced = await offers.ListAsync([.. found.Items.Select(p => p.Sku)], cancellationToken);
                return Results.Ok(new ApiProductPage(
                    [.. found.Items.Select(p => new ApiProduct(
                        p.Sku,
                        p.Name,
                        p.Unit,
                        p.ImageId.ToMaybe().Map(id => MediaEndpoints.ImageUrl(id, ImageVariantNames.Medium)).OrNull(),
                        p.CategoryCode,
                        p.Discontinued,
                        priced.PriceOf(p.Sku).OrNull() ?? new ProductPrice(p.Sku, 1, PriceStatuses.OnRequest, null),
                        priced.Stock.Items.FirstOrDefault(s => s.Sku == p.Sku)))],
                    found.Page,
                    found.PageSize,
                    found.TotalCount,
                    found.PageCount,
                    found.Facets,
                    priced.Stock.Display));
            },
            error => Task.FromResult(error.ToProblem()));
    }

    private static async Task<IResult> GetProductAsync(
        string sku,
        IQueryHandler<GetProduct, Result<ProductDetails, Error>> products,
        CatalogOffers offers,
        AccessControl access,
        SupportedCultures cultures,
        CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(AccessAction.Read, new Resource.Catalog(), cancellationToken))
        {
            return ApiResults.Forbidden();
        }

        var product = await products.HandleAsync(new GetProduct(cultures.ForRequest(), sku), cancellationToken);
        return await product.Match(
            async details =>
            {
                var offer = await offers.ProductAsync(sku, cancellationToken);
                return Results.Ok(new ApiProductDetails(
                    details.Sku,
                    details.Name,
                    details.Description,
                    details.Unit,
                    details.Gtin,
                    [.. details.ImageIds.Select(id => MediaEndpoints.ImageUrl(id, ImageVariantNames.Large))],
                    details.Attributes,
                    details.CategoryPath,
                    details.Discontinued,
                    offer.Map(o => o.Price).OrNull() ?? new ProductPrice(sku, 1, PriceStatuses.OnRequest, null),
                    offer.Map(o => o.Stock).OrNull()));
            },
            error => Task.FromResult(error.ToProblem()));
    }

    private static async Task<IResult> GetCategoriesAsync(
        IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>> categories,
        AccessControl access,
        SupportedCultures cultures,
        CancellationToken cancellationToken) =>
        await access.CanAsync(AccessAction.Read, new Resource.Catalog(), cancellationToken)
            ? (await categories.HandleAsync(new GetCategoryTree(cultures.ForRequest()), cancellationToken)).ToHttpResult(tree => Results.Ok(tree))
            : ApiResults.Forbidden();
}

/// <summary>Query string of the product search.</summary>
/// <param name="Query">Words to search for; none lists the catalog.</param>
/// <param name="Category">A category code: its products and those of its subcategories.</param>
/// <param name="PageSize">1–100 (24 when absent).</param>
[NullableEdge]
internal sealed record ProductSearch(
    [FromQuery(Name = "q")] string? Query,
    [FromQuery(Name = "category")] string? Category,
    [FromQuery(Name = "page")] int Page = 1,
    [FromQuery(Name = "pageSize")] int PageSize = 0);

/// <summary>A product on a list, with what the key's customer pays and what is in stock.</summary>
/// <param name="ImageUrl">Relative to the host; none for a product without an image.</param>
/// <param name="Stock">None when stock is hidden or the product is unknown to the warehouses.</param>
[NullableEdge]
public sealed record ApiProduct(
    string Sku,
    string Name,
    string Unit,
    string? ImageUrl,
    string? CategoryCode,
    bool Discontinued,
    ProductPrice Price,
    StockSummary? Stock);

/// <param name="StockDisplay">How stock is shown: "hidden", "levels" or "exact".</param>
[NullableEdge]
public sealed record ApiProductPage(
    IReadOnlyList<ApiProduct> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int PageCount,
    IReadOnlyList<FacetView> Facets,
    string StockDisplay);

/// <summary>A product with every price tier of the key's customer and the stock per warehouse.</summary>
[NullableEdge]
public sealed record ApiProductDetails(
    string Sku,
    string Name,
    string? Description,
    string Unit,
    string? Gtin,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<ProductAttributeView> Attributes,
    IReadOnlyList<CategoryReference> CategoryPath,
    bool Discontinued,
    ProductPrice Price,
    ProductStock? Stock);
