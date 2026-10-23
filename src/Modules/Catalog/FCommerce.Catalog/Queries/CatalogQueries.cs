using FCommerce.Application.Messaging;
using FCommerce.Application.Search;
using FCommerce.Application.Results;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.Catalog.Search;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Queries;

internal sealed class SearchProductsHandler(ISearchEngine engine, CatalogCultures catalogCultures)
    : IQueryHandler<SearchProducts, Result<ProductPage, Error>>
{
    public const int MaxPageSize = 100;

    public Task<Result<ProductPage, Error>> HandleAsync(SearchProducts query, CancellationToken cancellationToken = default) =>
        Cultures.Parse(query.Cultures).MapAsync(cultures => SearchAsync(query, cultures, cancellationToken));

    private async Task<ProductPage> SearchAsync(SearchProducts query, EquatableArray<CultureCode> cultures, CancellationToken cancellationToken)
    {
        var index = ProductSearchIndex.IndexName(catalogCultures.IndexFor(cultures));
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var selected = (query.Filters ?? []).Select(f => (f.Code, f.Value)).ToHashSet();
        var category = string.IsNullOrWhiteSpace(query.CategoryCode) ? null : query.CategoryCode.Trim();

        var main = await engine.SearchAsync<ProductSearchDocument>(
            index,
            new SearchRequest(query.Text, Filter(category, selected, except: null), [ProductSearchIndex.FacetsField, ProductSearchIndex.FacetNamesField], page, pageSize),
            cancellationToken);

        // Disjunctive facets: a selected attribute's own values are counted without its own filter,
        // so choosing "M8" still shows how many "M10" products there are.
        var counts = new Dictionary<string, int>(FacetCounts(main, ProductSearchIndex.FacetsField).Where(c => !IsSelectedAttribute(c.Key, selected)));
        foreach (var code in selected.Select(s => s.Code).Distinct())
        {
            var others = await engine.SearchAsync<ProductSearchDocument>(
                index,
                new SearchRequest(query.Text, Filter(category, selected, except: code), [ProductSearchIndex.FacetsField], 1, 0),
                cancellationToken);
            foreach (var (token, count) in FacetCounts(others, ProductSearchIndex.FacetsField).Where(c => CatalogSearch.ParseFacetToken(c.Key)?.Code == code))
            {
                counts[token] = count;
            }
        }

        var facets = CatalogSearch.Facets(counts, FacetCounts(main, ProductSearchIndex.FacetNamesField), selected);
        return new ProductPage(
            [.. main.Hits.Select(h => new ProductSummary(h.Sku, h.Name, h.Unit, h.ImageId, h.Categories.Length > 0 ? h.Categories[^1] : null))],
            page,
            pageSize,
            main.TotalHits,
            [.. facets.Select(f => new FacetView(f.Code, f.Name, [.. f.Values.Select(v => new FacetValueView(v.Value, v.Count, v.Selected))]))]);
    }

    private static SearchFilter.All Filter(string? category, IReadOnlySet<(string Code, string Value)> selected, string? except) =>
        new SearchFilter.All(
        [
            .. category is null ? [] : new SearchFilter[] { new SearchFilter.Equal(ProductSearchIndex.CategoriesField, category) },
            .. selected
                .Where(s => s.Code != except)
                .GroupBy(s => s.Code)
                .Select(attribute => new SearchFilter.AnyOf(
                    ProductSearchIndex.FacetsField,
                    attribute.Select(s => CatalogSearch.FacetToken(s.Code, s.Value)).ToEquatableArray())),
        ]);

    private static bool IsSelectedAttribute(string token, IReadOnlySet<(string Code, string Value)> selected) =>
        CatalogSearch.ParseFacetToken(token) is { } parsed && selected.Any(s => s.Code == parsed.Code);

    private static IReadOnlyDictionary<string, int> FacetCounts(SearchResult<ProductSearchDocument> result, string field) =>
        result.Facets.GetValueOrDefault(field) ?? new Dictionary<string, int>();
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
