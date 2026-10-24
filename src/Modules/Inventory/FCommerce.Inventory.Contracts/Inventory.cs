using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Inventory.Contracts;

/// <summary>Upserts warehouses sent by the ERP connector; items are validated independently.</summary>
public sealed record ImportWarehouses(IReadOnlyList<WarehouseData> Warehouses) : ICommand<Result<ImportReport, Error>>;

/// <param name="Code">ERP code of the warehouse; identifies it.</param>
/// <param name="Version">Monotonic version from the ERP (row version or change timestamp).</param>
/// <param name="Visible">Whether buyers see the warehouse (and its stock); hidden ones are internal (e.g. service).</param>
/// <param name="SortOrder">Order of warehouses on the product page (ascending, then by name).</param>
/// <param name="Active">False withdraws the warehouse: its stock is not shown.</param>
public sealed record WarehouseData(string Code, long Version, string Name, bool Visible = true, int SortOrder = 0, bool Active = true);

/// <summary>
/// Upserts stock of one imported warehouse, keyed by SKU; a warehouse that has not been imported is not found.
/// Stock is never deleted: the ERP sends zero.
/// </summary>
public sealed record ImportStock(string WarehouseCode, IReadOnlyList<StockData> Stock) : ICommand<Result<ImportReport, Error>>;

/// <summary>The body of a stock import; the warehouse comes from the URL.</summary>
public sealed record StockBatch(IReadOnlyList<StockData> Stock);

/// <param name="Version">Monotonic version from the ERP (e.g. the change timestamp in ticks): older updates are ignored.</param>
/// <param name="Quantity">On hand, at most 3 decimal places; negative quantities are accepted and shown as zero.</param>
/// <param name="NextDeliveryDate">The next expected delivery, if any.</param>
/// <param name="NextDeliveryQuantity">How much the next delivery brings (requires the date).</param>
public sealed record StockData(string Sku, long Version, decimal Quantity, DateOnly? NextDeliveryDate = null, decimal? NextDeliveryQuantity = null);

/// <summary>Stock of products summed over the visible warehouses, in the order of the SKUs (at most <see cref="MaxSkus"/>).</summary>
public sealed record GetStockSummaries(IReadOnlyList<string> Skus) : IQuery<Result<StockOverview, Error>>
{
    public const int MaxSkus = 500;
}

/// <summary>
/// What can be ordered from stock: the sum over visible warehouses (never below zero) and the next delivery, whatever
/// the stock display settings. For ordering rules (cart, orders), not for showing quantities to buyers.
/// </summary>
public sealed record GetAvailability(IReadOnlyList<string> Skus) : IQuery<Result<IReadOnlyList<Availability>, Error>>
{
    public const int MaxSkus = 500;
}

/// <param name="NextDelivery">The earliest delivery from today on, if any.</param>
public sealed record Availability(string Sku, decimal Available, DateOnly? NextDelivery);

/// <param name="Display">See <see cref="StockDisplays"/>; "hidden" has no items.</param>
public sealed record StockOverview(string Display, IReadOnlyList<StockSummary> Items);

/// <summary>Stock of one product: the total and every visible warehouse.</summary>
public sealed record GetProductStock(string Sku) : IQuery<Result<ProductStock, Error>>;

/// <param name="Total">The sum over the visible warehouses; none when stock is hidden.</param>
/// <param name="Warehouses">Visible warehouses in their order, including those without stock of the product.</param>
public sealed record ProductStock(string Display, StockSummary? Total, IReadOnlyList<WarehouseStock> Warehouses);

/// <param name="Status">See <see cref="StockStatuses"/>.</param>
/// <param name="Quantity">Only when the display is "exact".</param>
public sealed record StockSummary(string Sku, string Status, decimal? Quantity, DeliveryView? NextDelivery);

public sealed record WarehouseStock(string Code, string Name, string Status, decimal? Quantity, DeliveryView? NextDelivery);

/// <param name="Quantity">Only when the display is "exact" and the ERP sent it.</param>
public sealed record DeliveryView(DateOnly Date, decimal? Quantity);

public static class StockDisplays
{
    /// <summary>The "stock-levels" feature is off: no stock information anywhere.</summary>
    public const string Hidden = "hidden";

    /// <summary>Available / low / out of stock, without quantities.</summary>
    public const string Levels = "levels";

    public const string Exact = "exact";
}

public static class StockStatuses
{
    public const string Available = "available";
    public const string Low = "low";
    public const string None = "none";
}
