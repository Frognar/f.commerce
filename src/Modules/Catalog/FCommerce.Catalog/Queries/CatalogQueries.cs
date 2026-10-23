using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Queries;

internal sealed class SearchProductsHandler(CatalogDbContext db) : IQueryHandler<SearchProducts, Result<ProductPage, Error>>
{
    public const int MaxPageSize = 100;

    public Task<Result<ProductPage, Error>> HandleAsync(SearchProducts query, CancellationToken cancellationToken = default) =>
        Cultures.Parse(query.Cultures).MapAsync(cultures => SearchAsync(query, cultures, cancellationToken));

    private async Task<ProductPage> SearchAsync(SearchProducts query, EquatableArray<CultureCode> cultures, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var products = db.Products.AsNoTracking().Where(p => p.Active);

        if (!string.IsNullOrWhiteSpace(query.CategoryCode))
        {
            var codes = await CategoryScopeAsync(query.CategoryCode, cancellationToken);
            products = products.Where(p => p.CategoryCode != null && codes.Contains(p.CategoryCode));
        }

        // Word-prefix match on the normalized document: "sru m8" finds "Śruba M8x40".
        foreach (var token in SearchText.Tokens(query.Text))
        {
            var prefix = " " + token;
            products = products.Where(p => p.SearchDocument.Contains(prefix));
        }

        var total = await products.CountAsync(cancellationToken);
        var rows = await products
            .OrderBy(p => p.Sku)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new ProductPage([.. rows.Select(r => Summarize(r.ToDomain(), cultures))], page, pageSize, total);
    }

    private async Task<List<string>> CategoryScopeAsync(string rawCode, CancellationToken cancellationToken)
    {
        var tree = CategoryTree.Build((await db.Categories.AsNoTracking().ToListAsync(cancellationToken)).Select(c => c.ToDomain()));
        return CategoryCode.Parse(rawCode).Match(
            code => CategoryTree.SelfAndDescendants(tree, code).Select(c => c.Value).ToList(),
            _ => []);
    }

    private static ProductSummary Summarize(Product product, EquatableArray<CultureCode> cultures) =>
        new(
            product.Sku.Value,
            product.Content.Name.Text.Resolve(cultures),
            product.Content.Unit.Value,
            product.Content.Images.IsEmpty ? null : product.Content.Images[0].Value,
            product.Content.Category?.Value);
}

internal sealed class GetProductHandler(CatalogDbContext db) : IQueryHandler<GetProduct, Result<ProductDetails, Error>>
{
    public Task<Result<ProductDetails, Error>> HandleAsync(GetProduct query, CancellationToken cancellationToken = default) =>
        (from cultures in Cultures.Parse(query.Cultures)
         from sku in Sku.Parse(query.Sku)
         select (cultures, sku))
        .BindAsync(input => FindAsync(input.sku, input.cultures, cancellationToken));

    private async Task<Result<ProductDetails, Error>> FindAsync(Sku sku, EquatableArray<CultureCode> cultures, CancellationToken cancellationToken)
    {
        var row = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Sku == sku.Value && p.Active, cancellationToken);
        if (row is null)
        {
            return Error.NotFound("product.not-found", $"Product '{sku}' does not exist.");
        }

        var product = row.ToDomain();
        var path = product.Content.Category is { } category
            ? CategoryTree.PathTo(CategoryTree.Build((await db.Categories.AsNoTracking().ToListAsync(cancellationToken)).Select(c => c.ToDomain())), category)
            : EquatableArray<Category>.Empty;

        return new ProductDetails(
            product.Sku.Value,
            product.Content.Name.Text.Resolve(cultures),
            product.Content.Description.IsEmpty ? null : product.Content.Description.Resolve(cultures),
            product.Content.Unit.Value,
            product.Content.Gtin?.Value,
            [.. product.Content.Images.Select(i => i.Value)],
            [.. product.Content.Attributes.Select(a => new ProductAttributeView(a.Code.Value, a.Name.Resolve(cultures), a.Value.Resolve(cultures)))],
            [.. path.Select(c => new CategoryReference(c.Code.Value, c.Content.Name.Text.Resolve(cultures)))]);
    }
}

internal sealed class GetCategoryTreeHandler(CatalogDbContext db) : IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>>
{
    public Task<Result<IReadOnlyList<CategoryView>, Error>> HandleAsync(GetCategoryTree query, CancellationToken cancellationToken = default) =>
        Cultures.Parse(query.Cultures).MapAsync(async cultures =>
        {
            var categories = await db.Categories.AsNoTracking().ToListAsync(cancellationToken);
            return (IReadOnlyList<CategoryView>)[.. CategoryTree.Build(categories.Select(c => c.ToDomain())).Select(n => View(n, cultures))];
        });

    private static CategoryView View(CategoryNode node, EquatableArray<CultureCode> cultures) =>
        new(node.Category.Code.Value, node.Category.Content.Name.Text.Resolve(cultures), [.. node.Children.Select(c => View(c, cultures))]);
}
