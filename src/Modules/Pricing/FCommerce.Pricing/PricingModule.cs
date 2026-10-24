using FCommerce.Application.Messaging;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Pricing.Contracts;
using FCommerce.Pricing.Persistence;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Pricing;

/// <summary>
/// Price lists and prices from the ERP, and which price a viewer sees. Reads platform settings and customers through
/// their contracts (requires the Settings and Customers modules).
/// </summary>
public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<PricingDbContext>(PricingDbContext.Schema);

        services.AddScoped<ICommandHandler<ImportPriceLists, Result<ImportReport, Error>>, ImportPriceListsHandler>();
        services.AddScoped<ICommandHandler<ImportPrices, Result<ImportReport, Error>>, ImportPricesHandler>();
        services.AddScoped<PriceResolver>();
        services.AddScoped<IQueryHandler<GetPrices, Result<IReadOnlyList<ProductPrice>, Error>>, GetPricesHandler>();
        services.AddScoped<IQueryHandler<QuoteLines, Result<Quote, Error>>, QuoteLinesHandler>();
        services.AddScoped<IQueryHandler<GetPriceLists, IReadOnlyList<PriceListSummary>>, GetPriceListsHandler>();
        return services;
    }
}
