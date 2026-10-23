using FCommerce.Application.Search;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Search;

/// <summary>Keeps the search engine in sync with the catalog: one index per culture, active products only.</summary>
internal sealed class ProductSearchIndex(ISearchEngine engine, CatalogDbContext db, CatalogCultures cultures)
{
    private const int BatchSize = 500;

    public const string CategoriesField = "categories";
    public const string FacetsField = "facets";
    public const string FacetNamesField = "facetNames";

    public static string IndexName(CultureCode culture) => $"catalog-products-{culture.Value.ToLowerInvariant()}";

    public async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        foreach (var culture in cultures.All)
        {
            await engine.ConfigureIndexAsync(
                new SearchIndexDefinition(
                    IndexName(culture),
                    PrimaryKey: "id",
                    SearchableFields: ["sku", "gtin", "name", "attributeValues", "description"],
                    FilterableFields: [CategoriesField, FacetsField, FacetNamesField],
                    ExactFields: ["sku", "gtin"],
                    Language: culture.Value),
                cancellationToken);
        }
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken)
    {
        foreach (var culture in cultures.All)
        {
            if (await engine.CountAsync(IndexName(culture), cancellationToken) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Re-indexes the given products: active ones are upserted, inactive or unknown ones removed.</summary>
    public async Task IndexAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var rows = await db.Products.AsNoTracking().Where(p => skus.Contains(p.Sku)).ToListAsync(cancellationToken);
        var products = rows.Select(r => r.ToDomain()).ToList();
        var tree = await CategoryTreeAsync(cancellationToken);

        var removed = skus.Except(products.Where(p => p.Active).Select(p => p.Sku.Value))
            .Select(sku => Sku.Parse(sku).Match(s => (string?)CatalogSearch.DocumentId(s), _ => null))
            .OfType<string>()
            .ToList();

        foreach (var culture in cultures.All)
        {
            var entries = products.Where(p => p.Active).Select(p => ProductSearchDocument.From(Entry(p, tree, culture))).ToList();
            await engine.UpsertAsync(IndexName(culture), entries, cancellationToken);
            await engine.DeleteAsync(IndexName(culture), removed, cancellationToken);
        }
    }

    /// <summary>Indexes every product again, e.g. after the category tree changed or the index was lost.</summary>
    public async Task ReindexAllAsync(CancellationToken cancellationToken)
    {
        for (var offset = 0; ; offset += BatchSize)
        {
            var batch = await db.Products.AsNoTracking()
                .OrderBy(p => p.Sku)
                .Select(p => p.Sku)
                .Skip(offset)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            await IndexAsync(batch, cancellationToken);
        }
    }

    private SearchEntry Entry(Product product, EquatableArray<CategoryNode> tree, CultureCode culture) =>
        CatalogSearch.Entry(
            product,
            product.Content.Category is { } category ? CategoryTree.PathTo(tree, category) : EquatableArray<Category>.Empty,
            culture,
            cultures.Default);

    private async Task<EquatableArray<CategoryNode>> CategoryTreeAsync(CancellationToken cancellationToken) =>
        CategoryTree.Build((await db.Categories.AsNoTracking().ToListAsync(cancellationToken)).Select(c => c.ToDomain()));
}
