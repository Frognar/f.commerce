using System.Text.Json;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Pricing.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;

namespace FCommerce.Pricing.Persistence;

/// <summary>A price list header. Mapping back to the domain does not check the rules of the values again (they were checked when written).</summary>
internal sealed class PriceListRow
{
    public required string Code { get; init; }

    public long Version { get; set; }

    public required string Name { get; set; }

    public required string Currency { get; set; }

    public bool Active { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static PriceListRow From(PriceList list, DateTimeOffset now)
    {
        var row = new PriceListRow { Code = list.Code.Value, Name = string.Empty, Currency = string.Empty };
        row.Apply(list, now);
        return row;
    }

    public void Apply(PriceList list, DateTimeOffset now)
    {
        Version = list.Version.Value;
        Name = list.Content.Name.Value;
        Currency = list.Content.Currency.Code;
        Active = list.Content.Active;
        UpdatedAt = now;
    }

    public PriceList ToDomain() =>
        new(
            Persisted.Rehydrate<PriceListCode, string>(Code),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new PriceListContent(Persisted.Rehydrate<PriceListName, string>(Name), Persisted.Rehydrate<Currency, string>(Currency), Active));

    public ActivePriceList ToActive() =>
        new(Persisted.Rehydrate<PriceListCode, string>(Code), Persisted.Rehydrate<Currency, string>(Currency));
}

/// <summary>The price of a SKU in a price list; the base price is a column, the breaks above it are JSON (never null, ascending).</summary>
internal sealed class PriceRow
{
    public required string PriceListCode { get; init; }

    public required string Sku { get; init; }

    public long Version { get; set; }

    public decimal NetPrice { get; set; }

    public decimal VatRate { get; set; }

    /// <summary>JSON array of the breaks above the base price ("[]" without any).</summary>
    public required string Breaks { get; set; }

    public bool Active { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static PriceRow From(PriceListCode list, Price price, DateTimeOffset now)
    {
        var row = new PriceRow { PriceListCode = list.Value, Sku = price.Sku.Value, Breaks = "[]" };
        row.Apply(price, now);
        return row;
    }

    public void Apply(Price price, DateTimeOffset now)
    {
        Version = price.Version.Value;
        NetPrice = price.Content.Tiers.Base.Value;
        VatRate = price.Content.Vat.Percent;
        Breaks = JsonSerializer.Serialize(price.Content.Tiers.Breaks, PersistenceJson.Options);
        Active = price.Content.Active;
        UpdatedAt = now;
    }

    public Price ToDomain() => new(Persisted.Rehydrate<Domain.Sku, string>(Sku), Persisted.Rehydrate<SourceVersion, long>(Version), Content());

    public PriceContent Content()
    {
        var breaks = JsonSerializer.Deserialize<EquatableArray<PriceBreak>>(Stored.Required(Breaks, "price breaks"), PersistenceJson.Options);
        return new PriceContent(
            Persisted.Rehydrate<PriceTiers, EquatableArray<PriceBreak>>([new PriceBreak(PriceQuantity.One, Persisted.Rehydrate<NetPrice, decimal>(NetPrice)), .. breaks]),
            Persisted.Rehydrate<VatRate, decimal>(VatRate),
            Active);
    }
}
