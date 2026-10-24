using FCommerce.SharedKernel.Imports;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Import;
using FCommerce.Catalog.Persistence;
using FCommerce.Catalog.Queries;
using FCommerce.Catalog.Search;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Catalog;

public static class CatalogModule
{
    /// <summary>
    /// Requires an <see cref="Application.Search.ISearchEngine"/> and the Media module's <c>FindStoredImages</c> query.
    /// Invalid options stop the host on startup.
    /// </summary>
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, Action<CatalogOptions> configure)
    {
        var options = new CatalogOptions();
        configure(options);
        services.AddSingleton(CatalogCultures.From(options).Match(
            cultures => cultures,
            error => throw new InvalidOperationException($"Catalog options: {error.Message}")));

        services.AddModuleDbContext<CatalogDbContext>(CatalogDbContext.Schema);
        services.AddOutbox<CatalogDbContext>();
        services.AddMessageType<ProductChanged>("catalog.product-changed.v1");
        services.AddMessageType<CategoriesChanged>("catalog.categories-changed.v1");

        services.AddScoped<ProductSearchIndex>();
        services.AddIntegrationEventHandler<ProductChanged, IndexChangedProduct>();
        services.AddIntegrationEventHandler<CategoriesChanged, ReindexOnCategoriesChanged>();
        services.AddHostedService<SearchIndexInitializer>();

        services.AddScoped<ICommandHandler<ImportProducts, Result<ImportReport, Error>>, ImportProductsHandler>();
        services.AddScoped<ICommandHandler<ImportCategories, Result<ImportReport, Error>>, ImportCategoriesHandler>();
        services.AddScoped<IQueryHandler<SearchProducts, Result<ProductPage, Error>>, SearchProductsHandler>();
        services.AddScoped<IQueryHandler<GetProduct, Result<ProductDetails, Error>>, GetProductHandler>();
        services.AddScoped<IQueryHandler<GetProductsBySku, Result<IReadOnlyList<ProductSummary>, Error>>, GetProductsBySkuHandler>();
        services.AddScoped<IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>>, GetCategoryTreeHandler>();
        return services;
    }
}
