using FCommerce.Application.Messaging;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Inventory.Contracts;
using FCommerce.Inventory.Persistence;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Inventory;

public sealed class InventoryOptions
{
    /// <summary>IANA time zone of the seller: deliveries before today there are no longer shown.</summary>
    public string TimeZone { get; set; } = "Europe/Warsaw";
}

/// <summary>Warehouses and stock from the ERP. Reads how stock is shown from the platform settings (requires the Settings module).</summary>
public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services, InventoryOptions options)
    {
        services.AddModuleDbContext<InventoryDbContext>(InventoryDbContext.Schema);
        services.AddSingleton(options);
        services.AddSingleton<InventoryClock>();

        services.AddScoped<ICommandHandler<ImportWarehouses, Result<ImportReport, Error>>, ImportWarehousesHandler>();
        services.AddScoped<ICommandHandler<ImportStock, Result<ImportReport, Error>>, ImportStockHandler>();
        services.AddScoped<IQueryHandler<GetStockSummaries, Result<StockOverview, Error>>, GetStockSummariesHandler>();
        services.AddScoped<IQueryHandler<GetProductStock, Result<ProductStock, Error>>, GetProductStockHandler>();
        return services;
    }
}
