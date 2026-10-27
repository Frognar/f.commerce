using FCommerce.Application.Messaging;
using FCommerce.Application.Search;
using FCommerce.Application.Results;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.Catalog.Search;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
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
        var selected = query.Filters.OrEmpty().Select(f => (Code: f.Code.OrEmpty(), Value: f.Value.OrEmpty())).ToHashSet();
        var category = query.CategoryCode.ToMaybe().Where(code => !string.IsNullOrWhiteSpace(code)).Map(code => code.Trim());
        var text = query.Text.ToMaybe();

        var main = await engine.SearchAsync<ProductSearchDocument>(
            index,
            new SearchRequest(text, Filter(category, selected, except: Maybe<string>.None), [ProductSearchIndex.FacetsField, ProductSearchIndex.FacetNamesField], page, pageSize),
            cancellationToken);

        // Disjunctive facets: a selected attribute's own values are counted without its own filter,
        // so choosing "M8" still shows how many "M10" products there are.
        var counts = new Dictionary<string, int>(FacetCounts(main, ProductSearchIndex.FacetsField).Where(c => !IsSelectedAttribute(c.Key, selected)));
        foreach (var code in selected.Select(s => s.Code).Distinct())
        {
            var others = await engine.SearchAsync<ProductSearchDocument>(
                index,
                new SearchRequest(text, Filter(category, selected, except: code), [ProductSearchIndex.FacetsField], 1, 0),
                cancellationToken);
            foreach (var (token, count) in FacetCounts(others, ProductSearchIndex.FacetsField).Where(c => CatalogSearch.ParseFacetToken(c.Key).Exists(t => t.Code == code)))
            {
                counts[token] = count;
            }
        }

        var facets = CatalogSearch.Facets(counts, FacetCounts(main, ProductSearchIndex.FacetNamesField), selected);
        return new ProductPage(
            [.. main.Hits.Select(h => new ProductSummary(h.Sku, h.Name, h.Unit, h.ImageId, h.Categories.LastOrDefault(), h.Discontinued))],
            page,
            pageSize,
            main.TotalHits,
            [.. facets.Select(f => new FacetView(f.Code, f.Name, [.. f.Values.Select(v => new FacetValueView(v.Value, v.Count, v.Selected))]))]);
    }

    private static SearchFilter.All Filter(Maybe<string> category, IReadOnlySet<(string Code, string Value)> selected, Maybe<string> except) =>
        new SearchFilter.All(
        [
            .. category.AsEnumerable().Select(code => new SearchFilter.Equal(ProductSearchIndex.CategoriesField, code)),
            .. selected
                .Where(s => !except.Exists(code => code == s.Code))
                .GroupBy(s => s.Code)
                .Select(attribute => new SearchFilter.AnyOf(
                    ProductSearchIndex.FacetsField,
                    attribute.Select(s => CatalogSearch.FacetToken(s.Code, s.Value)).ToEquatableArray())),
        ]);

    private static bool IsSelectedAttribute(string token, IReadOnlySet<(string Code, string Value)> selected) =>
        CatalogSearch.ParseFacetToken(token).Exists(parsed => selected.Any(s => s.Code == parsed.Code));

    private static IReadOnlyDictionary<string, int> FacetCounts(SearchResult<ProductSearchDocument> result, string field) =>
        result.Facets.GetValueOrNone(field).ValueOr(() => new Dictionary<string, int>());
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
        var row = await db.Products.AsNoTracking().SingleOrNoneAsync(p => p.Sku == sku.Value && p.Active, cancellationToken);
        return await row.Match(
            async stored => Result.Ok<ProductDetails, Error>(await DetailsAsync(stored.ToDomain(), cultures, cancellationToken)),
            () => Task.FromResult(Result.Fail<ProductDetails, Error>(Error.NotFound("product.not-found", $"Product '{sku}' does not exist."))));
    }

    private async Task<ProductDetails> DetailsAsync(Product product, EquatableArray<CultureCode> cultures, CancellationToken cancellationToken)
    {
        var path = await product.Content.Category.Match(
            async category => CategoryTree.PathTo(CategoryTree.Build((await db.Categories.AsNoTracking().ToListAsync(cancellationToken)).Select(c => c.ToDomain())), category),
            () => Task.FromResult(EquatableArray<Category>.Empty));

        return new ProductDetails(
            product.Sku.Value,
            product.Content.Name.Text.Resolve(cultures),
            product.Content.Description.IsEmpty ? null : product.Content.Description.Resolve(cultures),
            product.Content.Unit.Value,
            product.Content.Gtin.Map(gtin => gtin.Value).OrNull(),
            [.. product.Content.Images.Select(i => i.Value)],
            [.. product.Content.Attributes.Select(a => new ProductAttributeView(a.Code.Value, a.Name.Resolve(cultures), a.Value.Resolve(cultures)))],
            [.. path.Select(c => new CategoryReference(c.Code.Value, c.Content.Name.Text.Resolve(cultures)))],
            product.Status == ProductStatus.Discontinued);
    }
}

internal sealed class GetProductsBySkuHandler(CatalogDbContext db) : IQueryHandler<GetProductsBySku, Result<IReadOnlyList<ProductSummary>, Error>>
{
    public Task<Result<IReadOnlyList<ProductSummary>, Error>> HandleAsync(GetProductsBySku query, CancellationToken cancellationToken = default) =>
        (from cultures in Cultures.Parse(query.Cultures)
         from skus in Skus(query.Skus.OrEmpty())
         select (cultures, skus))
        .MapAsync(async input =>
        {
            var wanted = input.skus.Select(s => s.Value).Distinct().ToList();
            var found = (await db.Products.AsNoTracking().Where(p => wanted.Contains(p.Sku) && p.Active).ToListAsync(cancellationToken))
                .ToDictionary(p => p.Sku, p => p.ToDomain());
            return (IReadOnlyList<ProductSummary>)[.. input.skus.Select(s => s.Value).Distinct().Choose(sku => found.GetValueOrNone(sku)).Select(p => Summary(p, input.cultures))];
        });

    private static Result<IReadOnlyList<Sku>, Error> Skus(IReadOnlyList<string> skus) =>
        skus.Count > GetProductsBySku.MaxSkus
            ? Error.Validation("products.too-many-skus", $"At most {GetProductsBySku.MaxSkus} SKUs per query.")
            : skus.Traverse(sku => Sku.Parse(sku.OrEmpty()));

    private static ProductSummary Summary(Product product, EquatableArray<CultureCode> cultures) =>
        new(
            product.Sku.Value,
            product.Content.Name.Text.Resolve(cultures),
            product.Content.Unit.Value,
            product.Content.Images.Select(i => i.Value).FirstOrNone().OrNull(),
            product.Content.Category.Map(c => c.Value).OrNull(),
            product.Status == ProductStatus.Discontinued);
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
