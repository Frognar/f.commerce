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

/// <summary>Who sees a warehouse's stock: buyers, only the seller (internal), or nobody (withdrawn).</summary>
public enum WarehouseVisibility
{
    Public,
    Internal,
    Withdrawn,
}

/// <summary>A text of the ERP for stock (a SKU, a warehouse name): trimmed, bounded, no control characters.</summary>
public sealed record StockText : IPersistedValue<StockText, string>
{
    private StockText(string value) => Value = value;

    public string Value { get; }

    public static Result<StockText, Error> Parse(string raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.Parse(raw, maxLength, errorPrefix, label).Map(value => new StockText(value));

    static StockText IPersistedValue<StockText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<StockText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>On hand as the ERP reports it: at most 3 decimal places, bounded; may be negative (corrections in progress).</summary>
public sealed record StockQuantity : IPersistedValue<StockQuantity, decimal>
{
    public const int Decimals = 3;

    /// <summary>Bounds that fit the storage columns (numeric(15,3)).</summary>
    public const decimal Max = 999_999_999_999.999m;

    private StockQuantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<StockQuantity, Error> Parse(decimal raw) =>
        raw switch
        {
            > Max or < -Max => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(raw, Decimals) != raw => Error.Validation("quantity.precision", $"Quantities have at most {Decimals} decimal places."),
            _ => new StockQuantity(raw),
        };

    static StockQuantity IPersistedValue<StockQuantity, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<StockQuantity, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A quantity of stock that is zero or more: what buyers may see, what a delivery brings, a low-stock threshold.</summary>
public sealed record Amount : IPersistedValue<Amount, decimal>
{
    private Amount(decimal value) => Value = value;

    public static Amount Zero { get; } = new(0m);

    public decimal Value { get; }

    public static Result<Amount, Error> Parse(decimal raw, string code = "quantity.negative") =>
        StockQuantity.Parse(raw).Bind(quantity => quantity.Value < 0
            ? Error.Validation(code, "The quantity must not be negative.")
            : Result.Ok<Amount, Error>(new Amount(quantity.Value)));

    /// <summary>Stock on hand as buyers may learn it: negative stock is none.</summary>
    public static Amount Of(StockQuantity quantity) => quantity.Value > 0 ? new Amount(quantity.Value) : Zero;

    internal Amount Plus(Amount other) => new(Value + other.Value);

    static bool IPersistedValue<Amount, decimal>.IsWellFormed(decimal raw) => raw >= 0;

    static Amount IPersistedValue<Amount, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<Amount, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record WarehouseContent(StockText Name, WarehouseVisibility Visibility, int SortOrder);

public sealed record Warehouse(WarehouseCode Code, SourceVersion Version, WarehouseContent Content);

/// <param name="Quantity">How much the delivery brings (more than zero), when the ERP knows it.</param>
public sealed record Delivery(DateOnly Date, Maybe<Amount> Quantity);

public sealed record StockContent(StockQuantity Quantity, Maybe<Delivery> NextDelivery);

/// <summary>Stock of one SKU in one warehouse.</summary>
public sealed record StockEntry(StockText Sku, SourceVersion Version, StockContent Content);

/// <param name="Visible">Shown to buyers (of an active warehouse).</param>
/// <param name="Active">False withdraws the warehouse (its stock is not shown).</param>
[RawInput]
public sealed record UnvalidatedWarehouse(string Code, long Version, string Name, bool Visible, int SortOrder, bool Active);

[RawInput]
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
public sealed record StockPolicy(StockDisplayMode Mode, Amount LowThreshold);

/// <summary>What buyers may learn about stock today: never below zero, no deliveries in the past.</summary>
public sealed record StockFigure
{
    internal StockFigure(Amount quantity, Maybe<Delivery> nextDelivery)
    {
        Quantity = quantity;
        NextDelivery = nextDelivery;
    }

    public static StockFigure Empty { get; } = new(Amount.Zero, Maybe<Delivery>.None);

    public Amount Quantity { get; }

    public Maybe<Delivery> NextDelivery { get; }
}

/// <summary>A figure as shown: quantities only in the exact display; the status always follows the quantity.</summary>
public abstract record StockPresentation
{
    private StockPresentation(StockStatus status) => Status = status;

    public StockStatus Status { get; }

    public sealed record Levels : StockPresentation
    {
        internal Levels(StockStatus status, Maybe<DateOnly> nextDelivery)
            : base(status) => NextDelivery = nextDelivery;

        public Maybe<DateOnly> NextDelivery { get; }
    }

    public sealed record Exact : StockPresentation
    {
        internal Exact(StockStatus status, Amount quantity, Maybe<Delivery> nextDelivery)
            : base(status)
        {
            Quantity = quantity;
            NextDelivery = nextDelivery;
        }

        public Amount Quantity { get; }

        public Maybe<Delivery> NextDelivery { get; }
    }
}

public static class Stock
{
    public const int MaxSku = 64;
    public const int MaxName = 200;

    public static Validated<Warehouse> Validate(UnvalidatedWarehouse input) =>
        ((Func<WarehouseCode, SourceVersion, StockText, Warehouse>)((code, version, name) =>
            new Warehouse(code, version, new WarehouseContent(name, VisibilityOf(input.Visible, input.Active), input.SortOrder))))
            .Apply(WarehouseCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(StockText.Parse(input.Name, MaxName, "name", "Name").ToValidated("name"));

    /// <summary>A withdrawn warehouse is withdrawn, shown or not.</summary>
    public static WarehouseVisibility VisibilityOf(bool visible, bool active) =>
        !active ? WarehouseVisibility.Withdrawn : visible ? WarehouseVisibility.Public : WarehouseVisibility.Internal;

    public static Validated<StockEntry> Validate(UnvalidatedStock input) =>
        ((Func<StockText, SourceVersion, StockQuantity, Maybe<Delivery>, StockEntry>)((sku, version, quantity, delivery) =>
            new StockEntry(sku, version, new StockContent(quantity, delivery))))
            .Apply(StockText.Parse(input.Sku, MaxSku, "sku", "SKU").ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(StockQuantity.Parse(input.Quantity).ToValidated("quantity"))
            .Apply(ValidateDelivery(input.NextDeliveryDate, input.NextDeliveryQuantity));

    public static Change<Warehouse> Reconcile(Maybe<Warehouse> stored, Warehouse incoming) =>
        ImportBatch.Reconcile(stored, incoming, w => w.Version, (a, b) => a.Content == b.Content);

    public static Change<StockEntry> Reconcile(Maybe<StockEntry> stored, StockEntry incoming) =>
        ImportBatch.Reconcile(stored, incoming, s => s.Version, (a, b) => a.Content == b.Content);

    /// <summary>Negative stock (ERP corrections in progress) is shown as zero; a delivery before today is skipped.</summary>
    public static StockFigure Figure(StockContent content, DateOnly today) =>
        new(Amount.Of(content.Quantity), content.NextDelivery.Where(delivery => delivery.Date >= today));

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
                thatDay.All(d => d.Quantity.Exists(_ => true)) ? Maybe.Some(thatDay.Choose(d => d.Quantity).Aggregate(Amount.Zero, (sum, q) => sum.Plus(q))) : Maybe<Amount>.None);
        });
        return new StockFigure(all.Aggregate(Amount.Zero, (sum, f) => sum.Plus(f.Quantity)), next);
    }

    /// <summary>Nothing at zero, low up to the threshold, available above it.</summary>
    public static StockStatus StatusOf(Amount quantity, Amount lowThreshold) =>
        quantity.Value switch
        {
            <= 0 => StockStatus.None,
            var value when value <= lowThreshold.Value => StockStatus.Low,
            _ => StockStatus.Available,
        };

    /// <summary>The figure as buyers see it; the levels display leaves out every quantity.</summary>
    public static StockPresentation Present(StockFigure figure, StockPolicy policy) =>
        policy.Mode == StockDisplayMode.Exact
            ? new StockPresentation.Exact(StatusOf(figure.Quantity, policy.LowThreshold), figure.Quantity, figure.NextDelivery)
            : new StockPresentation.Levels(StatusOf(figure.Quantity, policy.LowThreshold), figure.NextDelivery.Map(delivery => delivery.Date));

    private static Validated<Maybe<Delivery>> ValidateDelivery(Maybe<DateOnly> date, Maybe<decimal> quantity) =>
        date.Match(
            day => quantity
                .Match(amount => ParseDeliveryQuantity(amount).Map(Maybe.Some), () => Result.Ok<Maybe<Amount>, Error>(Maybe<Amount>.None))
                .Map(amount => Maybe.Some(new Delivery(day, amount)))
                .ToValidated("nextDeliveryQuantity"),
            () => quantity.Match(
                _ => Validated.Invalid<Maybe<Delivery>>(Error.Validation("next-delivery.quantity-without-date", "A delivery quantity needs the delivery date.") with { Field = "nextDeliveryDate" }),
                () => Validated.Valid(Maybe<Delivery>.None)));

    private static Result<Amount, Error> ParseDeliveryQuantity(decimal quantity) =>
        quantity <= 0
            ? Error.Validation("next-delivery.quantity-not-positive", "The delivery quantity must be greater than zero.")
            : Amount.Parse(quantity);
}
