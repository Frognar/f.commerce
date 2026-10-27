using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Ordering.Domain;

/// <summary>The platform's number of an order ("WWW/2026/000123", see <see cref="OrderNumbers"/>).</summary>
public sealed record OrderNumber : IPersistedValue<OrderNumber, string>
{
    public const int MaxLength = 32;

    private OrderNumber(string value) => Value = value;

    public string Value { get; }

    /// <summary>A number as someone typed or sent it (to look an order up).</summary>
    public static Result<OrderNumber, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "order-number", "Order number").Map(value => new OrderNumber(value));

    internal static OrderNumber Of(string formatted) => new(formatted);

    static OrderNumber IPersistedValue<OrderNumber, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<OrderNumber, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The number the ERP gave the order when it took it over.</summary>
public sealed record ErpOrderNumber : IPersistedValue<ErpOrderNumber, string>
{
    public const int MaxLength = 64;

    private ErpOrderNumber(string value) => Value = value;

    public string Value { get; }

    public static Result<ErpOrderNumber, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "erp-number", "ERP order number").Map(value => new ErpOrderNumber(value));

    static ErpOrderNumber IPersistedValue<ErpOrderNumber, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ErpOrderNumber, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The ERP code of the customer an order is placed for.</summary>
public sealed record CustomerCode : IPersistedValue<CustomerCode, string>
{
    public const int MaxLength = 64;

    private CustomerCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CustomerCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "customer-code", "Customer code").Map(value => new CustomerCode(value));

    static CustomerCode IPersistedValue<CustomerCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CustomerCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

public sealed record Sku : IPersistedValue<Sku, string>
{
    public const int MaxLength = 64;

    private Sku(string value) => Value = value;

    public string Value { get; }

    public static Result<Sku, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "sku", "SKU").Map(value => new Sku(value));

    static Sku IPersistedValue<Sku, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Sku, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The product name on an order line, as the catalog showed it when the order was placed.</summary>
public sealed record LineName : IPersistedValue<LineName, string>
{
    public const int MaxLength = 300;

    private LineName(string value) => Value = value;

    public string Value { get; }

    public static Result<LineName, Error> Parse(string raw) =>
        raw.Trim() is { Length: > 0 and <= MaxLength } name
            ? new LineName(name)
            : Error.Validation("line-name.invalid", $"A line name has 1–{MaxLength} characters.");

    static LineName IPersistedValue<LineName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<LineName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>Unit of measure of an order line ("szt", "kg").</summary>
public sealed record UnitCode : IPersistedValue<UnitCode, string>
{
    public const int MaxLength = 16;

    private UnitCode(string value) => Value = value;

    public string Value { get; }

    public static Result<UnitCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "unit", "Unit").Map(value => new UnitCode(value));

    static UnitCode IPersistedValue<UnitCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<UnitCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The ERP price list a line was priced from.</summary>
public sealed record PriceListCode : IPersistedValue<PriceListCode, string>
{
    public const int MaxLength = 64;

    private PriceListCode(string value) => Value = value;

    public string Value { get; }

    public static Result<PriceListCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "price-list-code", "Price list code").Map(value => new PriceListCode(value));

    static PriceListCode IPersistedValue<PriceListCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PriceListCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>How much of a product is ordered: more than zero, at most 3 decimal places, bounded like a cart line.</summary>
public sealed record OrderQuantity : IPersistedValue<OrderQuantity, decimal>
{
    public const int Decimals = 3;
    public const decimal Max = 1_000_000m;

    private OrderQuantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<OrderQuantity, Error> Parse(decimal raw) =>
        raw switch
        {
            <= 0 => Error.Validation("quantity.not-positive", "The quantity must be greater than zero."),
            > Max => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(raw, Decimals) != raw => Error.Validation("quantity.precision", $"Quantities have at most {Decimals} decimal places."),
            _ => new OrderQuantity(raw),
        };

    static OrderQuantity IPersistedValue<OrderQuantity, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<OrderQuantity, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A net unit price: more than zero, at most 4 decimal places, bounded like a price list's prices.</summary>
public sealed record NetPrice : IPersistedValue<NetPrice, decimal>
{
    public const int Decimals = 4;
    public const decimal Max = 99_999_999_999.9999m;

    private NetPrice(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<NetPrice, Error> Parse(decimal raw) =>
        raw switch
        {
            <= 0 => Error.Validation("net-price.not-positive", "The net price must be greater than zero."),
            > Max => Error.Validation("net-price.too-large", "The net price is too large."),
            _ when decimal.Round(raw, Decimals) != raw => Error.Validation("net-price.precision", $"Net prices have at most {Decimals} decimal places."),
            _ => new NetPrice(raw),
        };

    static NetPrice IPersistedValue<NetPrice, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<NetPrice, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>VAT rate in percent: 0–100 with at most two decimal places.</summary>
public sealed record VatRate : IPersistedValue<VatRate, decimal>
{
    private VatRate(decimal percent) => Percent = percent;

    public decimal Percent { get; }

    public static Result<VatRate, Error> Parse(decimal percent) =>
        percent switch
        {
            < 0 or > 100 => Error.Validation("vat-rate.out-of-range", "The VAT rate must be between 0 and 100 percent."),
            _ when decimal.Round(percent, 2) != percent => Error.Validation("vat-rate.precision", "The VAT rate has at most two decimal places."),
            _ => new VatRate(percent),
        };

    static VatRate IPersistedValue<VatRate, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<VatRate, decimal>.Dehydrate() => Percent;

    public override string ToString() => $"{Percent:0.##}%";
}

/// <summary>Why the ERP rejected or cancelled an order, or refused to cancel it: trimmed, 1–500 characters.</summary>
public sealed record Reason : IPersistedValue<Reason, string>
{
    public const int MaxLength = 500;

    private Reason(string value) => Value = value;

    public string Value { get; }

    /// <summary>An optional reason: blank is none.</summary>
    public static Result<Maybe<Reason>, Error> ParseOptional(Maybe<string> raw) =>
        raw.Map(text => text.Trim()).Where(text => text.Length > 0).Match(
            text => text.Length > MaxLength
                ? Error.Validation("order.reason-too-long", $"A reason has at most {MaxLength} characters.") with { Field = "reason" }
                : Result.Ok<Maybe<Reason>, Error>(new Reason(text)),
            () => Result.Ok<Maybe<Reason>, Error>(Maybe<Reason>.None));

    static Reason IPersistedValue<Reason, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Reason, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A free text of the buyer on an order (their own order number, notes for the seller), trimmed and bounded.</summary>
public sealed record BuyerText : IPersistedValue<BuyerText, string>
{
    private BuyerText(string value) => Value = value;

    public string Value { get; }

    /// <summary>Blank is none; longer than <paramref name="maxLength"/> is an error with <paramref name="code"/>.</summary>
    public static Result<Maybe<BuyerText>, Error> ParseOptional(Maybe<string> raw, int maxLength, string code) =>
        raw.Map(text => text.Trim()).Where(text => text.Length > 0).Match(
            text => text.Length > maxLength
                ? Error.Validation(code, $"At most {maxLength} characters.")
                : Result.Ok<Maybe<BuyerText>, Error>(new BuyerText(text)),
            () => Result.Ok<Maybe<BuyerText>, Error>(Maybe<BuyerText>.None));

    static BuyerText IPersistedValue<BuyerText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<BuyerText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The code of one of the customer's delivery addresses.</summary>
public sealed record AddressCode : IPersistedValue<AddressCode, string>
{
    public const int MaxLength = 64;

    private AddressCode(string value) => Value = value;

    public string Value { get; }

    public static Result<AddressCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "address-code", "Address code").Map(value => new AddressCode(value));

    static AddressCode IPersistedValue<AddressCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<AddressCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A part of a delivery address (name, street, city) as the ERP keeps it.</summary>
public sealed record AddressText : IPersistedValue<AddressText, string>
{
    public const int MaxLength = 300;

    private AddressText(string value) => Value = value;

    public string Value { get; }

    public static Result<AddressText, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "address", "Address").Map(value => new AddressText(value));

    static AddressText IPersistedValue<AddressText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<AddressText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The id of an ERP document (as the documents module knows it).</summary>
public sealed record DocumentId : IPersistedValue<DocumentId, string>
{
    public const int MaxLength = 64;

    private DocumentId(string value) => Value = value;

    public string Value { get; }

    public static Result<DocumentId, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "document-id", "Document id").Map(value => new DocumentId(value));

    static DocumentId IPersistedValue<DocumentId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<DocumentId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The number printed on an ERP document.</summary>
public sealed record DocumentNumber : IPersistedValue<DocumentNumber, string>
{
    public const int MaxLength = 64;

    private DocumentNumber(string value) => Value = value;

    public string Value { get; }

    public static Result<DocumentNumber, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "document-number", "Document number").Map(value => new DocumentNumber(value));

    static DocumentNumber IPersistedValue<DocumentNumber, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<DocumentNumber, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The version of a cart as the checkout read it (the buyer sends it back with the order).</summary>
public sealed record CartVersion
{
    private CartVersion(long value) => Value = value;

    public long Value { get; }

    public static Result<CartVersion, Error> Parse(long raw) =>
        raw >= 0 ? new CartVersion(raw) : Error.Validation("cart-version.negative", "A cart version is not negative.");
}
