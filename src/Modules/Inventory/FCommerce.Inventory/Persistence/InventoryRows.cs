using FCommerce.Infrastructure.Serialization;
using FCommerce.Inventory.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Inventory.Persistence;

/// <summary>A warehouse. Mapping back to the domain trusts stored data (it was validated when written).</summary>
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
        Name = warehouse.Content.Name;
        Visible = warehouse.Content.Visible;
        SortOrder = warehouse.Content.SortOrder;
        Active = warehouse.Content.Active;
        UpdatedAt = now;
    }

    public Warehouse ToDomain() =>
        new(
            Persisted.Rehydrate<WarehouseCode, string>(Code),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new WarehouseContent(Name, Visible, SortOrder, Active));
}

/// <summary>Stock of a SKU in a warehouse.</summary>
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
        var row = new StockRow { WarehouseCode = warehouse.Value, Sku = entry.Sku };
        row.Apply(entry, now);
        return row;
    }

    public void Apply(StockEntry entry, DateTimeOffset now)
    {
        Version = entry.Version.Value;
        Quantity = entry.Content.Quantity;
        NextDeliveryDate = entry.Content.NextDelivery.Map(d => d.Date).OrNull();
        NextDeliveryQuantity = entry.Content.NextDelivery.Bind(d => d.Quantity).OrNull();
        UpdatedAt = now;
    }

    public StockEntry ToDomain() => new(Sku, Persisted.Rehydrate<SourceVersion, long>(Version), Content());

    public StockContent Content() =>
        new(Quantity, NextDeliveryDate.ToMaybe().Map(date => new Delivery(date, NextDeliveryQuantity.ToMaybe())));
}
