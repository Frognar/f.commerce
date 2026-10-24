using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Inventory.Domain;

/// <summary>Warehouse code from the ERP.</summary>
public sealed record WarehouseCode : IPersistedValue<WarehouseCode, string>
{
    public const int MaxLength = 64;

    private WarehouseCode(string value) => Value = value;

    public string Value { get; }

    public static Result<WarehouseCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "warehouse-code", "Warehouse code").Map(value => new WarehouseCode(value));

    static WarehouseCode IPersistedValue<WarehouseCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<WarehouseCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <param name="Visible">Shown to buyers; hidden warehouses are internal.</param>
/// <param name="Active">False withdraws the warehouse (its stock is not shown).</param>
public sealed record WarehouseContent(string Name, bool Visible, int SortOrder, bool Active);

public sealed record Warehouse(WarehouseCode Code, SourceVersion Version, WarehouseContent Content);

/// <param name="Quantity">How much the delivery brings, when the ERP knows it.</param>
public sealed record Delivery(DateOnly Date, Maybe<decimal> Quantity);

/// <param name="Quantity">On hand as the ERP reports it (may be negative).</param>
public sealed record StockContent(decimal Quantity, Maybe<Delivery> NextDelivery);

/// <summary>Stock of one SKU in one warehouse.</summary>
public sealed record StockEntry(string Sku, SourceVersion Version, StockContent Content);

public sealed record UnvalidatedWarehouse(string Code, long Version, string Name, bool Visible, int SortOrder, bool Active);

public sealed record UnvalidatedStock(string Sku, long Version, decimal Quantity, Maybe<DateOnly> NextDeliveryDate, Maybe<decimal> NextDeliveryQuantity);

public enum StockStatus
{
    None,
    Low,
    Available,
}

public enum StockDisplayMode
{
    /// <summary>Available / low / out of stock, without quantities.</summary>
    Levels,
    Exact,
}

/// <summary>How the seller shows stock (from the platform settings).</summary>
/// <param name="LowThreshold">Quantities above zero and up to this are "low".</param>
public sealed record StockPolicy(StockDisplayMode Mode, decimal LowThreshold);

/// <summary>What buyers may learn about stock today: never below zero, no deliveries in the past.</summary>
public sealed record StockFigure(decimal Quantity, Maybe<Delivery> NextDelivery)
{
    public static StockFigure Empty { get; } = new(0, Maybe<Delivery>.None);
}

/// <summary>A figure as shown: quantities only in the exact display.</summary>
public sealed record StockPresentation(StockStatus Status, Maybe<decimal> Quantity, Maybe<Delivery> NextDelivery);

public static class Stock
{
    public const int MaxSku = 64;
    public const int MaxName = 200;
    public const int QuantityDecimals = 3;

    /// <summary>Bounds that fit the storage columns (numeric(15,3)).</summary>
    public const decimal MaxQuantity = 999_999_999_999.999m;

    public static Validated<Warehouse> Validate(UnvalidatedWarehouse input) =>
        ((Func<WarehouseCode, SourceVersion, string, Warehouse>)((code, version, name) =>
            new Warehouse(code, version, new WarehouseContent(name, input.Visible, input.SortOrder, input.Active))))
            .Apply(WarehouseCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(SourceCodes.Parse(input.Name, MaxName, "name", "Name").ToValidated("name"));

    public static Validated<StockEntry> Validate(UnvalidatedStock input) =>
        ((Func<string, SourceVersion, decimal, Maybe<Delivery>, StockEntry>)((sku, version, quantity, delivery) =>
            new StockEntry(sku, version, new StockContent(quantity, delivery))))
            .Apply(SourceCodes.Parse(input.Sku, MaxSku, "sku", "SKU").ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(ParseQuantity(input.Quantity).ToValidated("quantity"))
            .Apply(ValidateDelivery(input.NextDeliveryDate, input.NextDeliveryQuantity));

    public static Change<Warehouse> Reconcile(Maybe<Warehouse> stored, Warehouse incoming) =>
        ImportBatch.Reconcile(stored, incoming, w => w.Version, (a, b) => a.Content == b.Content);

    public static Change<StockEntry> Reconcile(Maybe<StockEntry> stored, StockEntry incoming) =>
        ImportBatch.Reconcile(stored, incoming, s => s.Version, (a, b) => a.Content == b.Content);

    /// <summary>Negative stock (ERP corrections in progress) is shown as zero; a delivery before today is skipped.</summary>
    public static StockFigure Figure(StockContent content, DateOnly today) =>
        new(Math.Max(content.Quantity, 0), content.NextDelivery.Where(delivery => delivery.Date >= today));

    /// <summary>
    /// The sum over warehouses; the next delivery is the earliest one, with the quantities of that day summed (known
    /// only when every delivery of that day has its quantity).
    /// </summary>
    public static StockFigure Total(IEnumerable<StockFigure> figures)
    {
        var all = figures.ToList();
        var deliveries = all.Choose(f => f.NextDelivery).ToList();
        var next = deliveries.Select(d => d.Date).Order().FirstOrNone().Map(date =>
        {
            var thatDay = deliveries.Where(d => d.Date == date).ToList();
            return new Delivery(
                date,
                thatDay.All(d => d.Quantity.Exists(_ => true)) ? Maybe.Some(thatDay.Sum(d => d.Quantity.ValueOr(0))) : Maybe<decimal>.None);
        });
        return new StockFigure(all.Sum(f => f.Quantity), next);
    }

    /// <summary>Nothing at zero, low up to the threshold, available above it.</summary>
    public static StockStatus StatusOf(decimal quantity, decimal lowThreshold) =>
        quantity switch
        {
            <= 0 => StockStatus.None,
            _ when quantity <= lowThreshold => StockStatus.Low,
            _ => StockStatus.Available,
        };

    /// <summary>The figure as buyers see it; the levels display leaves out every quantity.</summary>
    public static StockPresentation Present(StockFigure figure, StockPolicy policy) =>
        policy.Mode == StockDisplayMode.Exact
            ? new StockPresentation(StatusOf(figure.Quantity, policy.LowThreshold), figure.Quantity, figure.NextDelivery)
            : new StockPresentation(
                StatusOf(figure.Quantity, policy.LowThreshold),
                Maybe<decimal>.None,
                figure.NextDelivery.Map(delivery => delivery with { Quantity = Maybe<decimal>.None }));

    private static Result<decimal, Error> ParseQuantity(decimal quantity) =>
        quantity switch
        {
            > MaxQuantity or < -MaxQuantity => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(quantity, QuantityDecimals) != quantity => Error.Validation("quantity.precision", $"Quantities have at most {QuantityDecimals} decimal places."),
            _ => quantity,
        };

    private static Validated<Maybe<Delivery>> ValidateDelivery(Maybe<DateOnly> date, Maybe<decimal> quantity) =>
        date.Match(
            day => quantity
                .Match(amount => ParseDeliveryQuantity(amount).Map(Maybe.Some), () => Result.Ok<Maybe<decimal>, Error>(Maybe<decimal>.None))
                .Map(amount => Maybe.Some(new Delivery(day, amount)))
                .ToValidated("nextDeliveryQuantity"),
            () => quantity.Match(
                _ => Validated.Invalid<Maybe<Delivery>>(Error.Validation("next-delivery.quantity-without-date", "A delivery quantity needs the delivery date.") with { Field = "nextDeliveryDate" }),
                () => Validated.Valid(Maybe<Delivery>.None)));

    private static Result<decimal, Error> ParseDeliveryQuantity(decimal quantity) =>
        quantity <= 0
            ? Error.Validation("next-delivery.quantity-not-positive", "The delivery quantity must be greater than zero.")
            : ParseQuantity(quantity);
}
