using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Inventory.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Inventory.Persistence;

/// <summary>
/// A warehouse. Its visibility is two columns (for the stock queries): a withdrawn warehouse is not visible. Mapping
/// back does not check the rules of the values again (they were checked when written).
/// </summary>
internal sealed class WarehouseRow
{
    public required string Code { get; init; }

    public long Version { get; set; }

    public required string Name { get; set; }

    public bool Visible { get; set; }

    public int SortOrder { get; set; }

    public bool Active { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static WarehouseRow From(Warehouse warehouse, DateTimeOffset now)
    {
        var row = new WarehouseRow { Code = warehouse.Code.Value, Name = string.Empty };
        row.Apply(warehouse, now);
        return row;
    }

    public void Apply(Warehouse warehouse, DateTimeOffset now)
    {
        Version = warehouse.Version.Value;
        Name = warehouse.Content.Name.Value;
        (Visible, Active) = warehouse.Content.Visibility switch
        {
            WarehouseVisibility.Public => (true, true),
            WarehouseVisibility.Internal => (false, true),
            WarehouseVisibility.Withdrawn => (false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(warehouse), warehouse.Content.Visibility, "A warehouse visibility without a stored form."),
        };
        SortOrder = warehouse.Content.SortOrder;
        UpdatedAt = now;
    }

    public Warehouse ToDomain() =>
        new(
            Persisted.Rehydrate<WarehouseCode, string>(Code),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new WarehouseContent(
                Persisted.Rehydrate<StockText, string>(Name),
                (Visible, Active) switch
                {
                    (true, true) => WarehouseVisibility.Public,
                    (false, true) => WarehouseVisibility.Internal,
                    (false, false) => WarehouseVisibility.Withdrawn,
                    _ => throw Stored.Corrupt($"warehouse {Code} is visible but withdrawn"),
                },
                SortOrder));
}

/// <summary>Stock of a SKU in a warehouse; a delivery quantity only with its date.</summary>
internal sealed class StockRow
{
    public required string WarehouseCode { get; init; }

    public required string Sku { get; init; }

    public long Version { get; set; }

    public decimal Quantity { get; set; }

    public DateOnly? NextDeliveryDate { get; set; }

    public decimal? NextDeliveryQuantity { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static StockRow From(WarehouseCode warehouse, StockEntry entry, DateTimeOffset now)
    {
        var row = new StockRow { WarehouseCode = warehouse.Value, Sku = entry.Sku.Value };
        row.Apply(entry, now);
        return row;
    }

    public void Apply(StockEntry entry, DateTimeOffset now)
    {
        Version = entry.Version.Value;
        Quantity = entry.Content.Quantity.Value;
        NextDeliveryDate = entry.Content.NextDelivery.Map(d => d.Date).OrNull();
        NextDeliveryQuantity = entry.Content.NextDelivery.Bind(d => d.Quantity).Map(q => q.Value).OrNull();
        UpdatedAt = now;
    }

    public StockEntry ToDomain() => new(Persisted.Rehydrate<StockText, string>(Sku), Persisted.Rehydrate<SourceVersion, long>(Version), Content());

    public StockContent Content() =>
        new(
            Persisted.Rehydrate<StockQuantity, decimal>(Quantity),
            (NextDeliveryDate, NextDeliveryQuantity) switch
            {
                (null, null) => Maybe<Delivery>.None,
                ({ } date, var quantity) => new Delivery(date, quantity.ToMaybe().Map(Persisted.Rehydrate<Amount, decimal>)),
                _ => throw Stored.Corrupt($"stock of {Sku} in {WarehouseCode} has a delivery quantity without its date"),
            });
}
