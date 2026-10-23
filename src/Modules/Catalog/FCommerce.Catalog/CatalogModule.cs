using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Import;
using FCommerce.Catalog.Persistence;
using FCommerce.Catalog.Queries;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CatalogDbContext>(CatalogDbContext.Schema);
        services.AddOutbox<CatalogDbContext>();
        services.AddMessageType<ProductChanged>("catalog.product-changed.v1");

        services.AddScoped<ICommandHandler<ImportProducts, Result<ImportReport, Error>>, ImportProductsHandler>();
        services.AddScoped<ICommandHandler<ImportCategories, Result<ImportReport, Error>>, ImportCategoriesHandler>();
        services.AddScoped<IQueryHandler<SearchProducts, Result<ProductPage, Error>>, SearchProductsHandler>();
        services.AddScoped<IQueryHandler<GetProduct, Result<ProductDetails, Error>>, GetProductHandler>();
        services.AddScoped<IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>>, GetCategoryTreeHandler>();
        return services;
    }
}
