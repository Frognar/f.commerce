using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Pricing.Domain;

/// <summary>Price list code from the ERP.</summary>
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

/// <summary>A SKU priced in a price list.</summary>
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

/// <summary>The name of a price list.</summary>
public sealed record PriceListName : IPersistedValue<PriceListName, string>
{
    public const int MaxLength = 200;

    private PriceListName(string value) => Value = value;

    public string Value { get; }

    public static Result<PriceListName, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "name", "Name").Map(value => new PriceListName(value));

    static PriceListName IPersistedValue<PriceListName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PriceListName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A net unit price: more than zero, at most 4 decimal places, bounded by the storage column (numeric(15,4)).</summary>
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

    static bool IPersistedValue<NetPrice, decimal>.IsWellFormed(decimal raw) => raw > 0;

    static NetPrice IPersistedValue<NetPrice, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<NetPrice, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A quantity to price: zero or more (below one unit means one unit), at most 3 decimal places, bounded.</summary>
public sealed record PriceQuantity : IPersistedValue<PriceQuantity, decimal>
{
    public const int Decimals = 3;
    public const decimal Max = 999_999_999_999.999m;

    private PriceQuantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static PriceQuantity One { get; } = new(1m);

    public static Result<PriceQuantity, Error> Parse(decimal raw) =>
        raw switch
        {
            < 0 => Error.Validation("quantity.negative", "The quantity must not be negative."),
            > Max => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(raw, Decimals) != raw => Error.Validation("quantity.precision", $"Quantities have at most {Decimals} decimal places."),
            _ => new PriceQuantity(raw),
        };

    static bool IPersistedValue<PriceQuantity, decimal>.IsWellFormed(decimal raw) => raw >= 0;

    static PriceQuantity IPersistedValue<PriceQuantity, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<PriceQuantity, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A net unit price that applies from a quantity.</summary>
public sealed record PriceBreak(PriceQuantity FromQuantity, NetPrice Net);

/// <summary>A price break as received from the ERP.</summary>
[RawInput]
public sealed record UnvalidatedPriceBreak(decimal FromQuantity, decimal NetPrice);

/// <summary>
/// The net unit price from one unit (<see cref="Base"/>) and lower prices from larger quantities (<see cref="Breaks"/>:
/// above 1, strictly ascending). Created only through validation.
/// </summary>
public sealed record PriceTiers : IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>
{
    private PriceTiers(NetPrice @base, EquatableArray<PriceBreak> breaks)
    {
        Base = @base;
        Breaks = breaks;
    }

    public NetPrice Base { get; }

    public EquatableArray<PriceBreak> Breaks { get; }

    /// <summary>Every tier, the base one (from 1) first.</summary>
    public EquatableArray<PriceBreak> All => [new PriceBreak(PriceQuantity.One, Base), .. Breaks];

    /// <summary>The tier for a quantity: the last one it reaches; below 1 the base price.</summary>
    public PriceBreak For(PriceQuantity quantity) => All.Last(tier => tier.FromQuantity.Value <= quantity.Value || tier.FromQuantity == PriceQuantity.One);

    public static Validated<PriceTiers> Validate(decimal net, IReadOnlyList<UnvalidatedPriceBreak> breaks) =>
        ((Func<NetPrice, IReadOnlyList<PriceBreak>, PriceTiers>)((price, valid) => new PriceTiers(price, [.. valid])))
            .Apply(NetPrice.Parse(net).ToValidated("netPrice"))
            .Apply(ValidateBreaks(breaks));

    private static bool Ascending(IEnumerable<PriceBreak> tiers) =>
        tiers.Zip(tiers.Skip(1)).All(pair => pair.First.FromQuantity.Value < pair.Second.FromQuantity.Value);

    private static Validated<IReadOnlyList<PriceBreak>> ValidateBreaks(IReadOnlyList<UnvalidatedPriceBreak> breaks) =>
        breaks.Count > Prices.MaxBreaks
            ? Validated.Invalid<IReadOnlyList<PriceBreak>>(Error.Validation("breaks.too-many", $"At most {Prices.MaxBreaks} price breaks.") with { Field = "breaks" })
            : breaks
                .Traverse(ValidateBreak)
                .AtField("breaks")
                .Ensure(
                    valid => Ascending(valid),
                    _ => Error.Validation("breaks.not-ascending", "Price breaks must have strictly ascending quantities.") with { Field = "breaks" });

    private static Validated<PriceBreak> ValidateBreak(UnvalidatedPriceBreak tier) =>
        ((Func<PriceQuantity, NetPrice, PriceBreak>)((from, net) => new PriceBreak(from, net)))
            .Apply(PriceQuantity.Parse(tier.FromQuantity)
                .Ensure(quantity => quantity.Value > 1, _ => Error.Validation("break.from-quantity", "A price break applies from more than one unit."))
                .ToValidated("fromQuantity"))
            .Apply(NetPrice.Parse(tier.NetPrice).ToValidated("netPrice"));

    /// <summary>The stored tiers: the base one from 1 first, then strictly ascending breaks.</summary>
    static bool IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>.IsWellFormed(EquatableArray<PriceBreak> raw) =>
        raw is [var first, ..] && first.FromQuantity == PriceQuantity.One && Ascending(raw);

    static PriceTiers IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>.Rehydrate(EquatableArray<PriceBreak> raw) =>
        new(raw[0].Net, [.. raw.Skip(1)]);

    EquatableArray<PriceBreak> IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>.Dehydrate() => All;
}

public sealed record PriceListContent(PriceListName Name, Currency Currency, bool Active);

/// <summary>A price list header from the ERP; its prices are imported separately.</summary>
public sealed record PriceList(PriceListCode Code, SourceVersion Version, PriceListContent Content);

/// <param name="Active">False withdraws the price (the default price list applies again).</param>
public sealed record PriceContent(PriceTiers Tiers, VatRate Vat, bool Active);

/// <summary>The price of one SKU in one price list.</summary>
public sealed record Price(Sku Sku, SourceVersion Version, PriceContent Content);

[RawInput]
public sealed record UnvalidatedPriceList(string Code, long Version, string Name, string Currency, bool Active);

/// <summary>A price as received from the ERP (missing required texts arrive as empty).</summary>
[RawInput]
public sealed record UnvalidatedPrice(string Sku, long Version, decimal NetPrice, decimal VatRate, IReadOnlyList<UnvalidatedPriceBreak> Breaks, bool Active);

public static class Prices
{
    public const int MaxBreaks = 20;


    public static Validated<PriceList> Validate(UnvalidatedPriceList input) =>
        ((Func<PriceListCode, SourceVersion, PriceListName, Currency, PriceList>)((code, version, name, currency) =>
            new PriceList(code, version, new PriceListContent(name, currency, input.Active))))
            .Apply(PriceListCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(PriceListName.Parse(input.Name).ToValidated("name"))
            .Apply(Currency.Parse(input.Currency.Trim()).ToValidated("currency"));

    public static Validated<Price> Validate(UnvalidatedPrice input) =>
        ((Func<Sku, SourceVersion, PriceTiers, VatRate, Price>)((sku, version, tiers, vat) =>
            new Price(sku, version, new PriceContent(tiers, vat, input.Active))))
            .Apply(Sku.Parse(input.Sku).ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(PriceTiers.Validate(input.NetPrice, input.Breaks))
            .Apply(VatRate.Parse(input.VatRate).ToValidated("vatRate"));

    public static Change<PriceList> Reconcile(Maybe<PriceList> stored, PriceList incoming) =>
        ImportBatch.Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content);

    /// <summary>
    /// A price list keeps its currency: its prices were imported in it, so another currency would re-denominate every
    /// one of them. The ERP sends a new price list instead.
    /// </summary>
    public static EquatableArray<Error> KeepsCurrency(Maybe<PriceList> stored, PriceList incoming) =>
        stored.Exists(list => list.Content.Currency != incoming.Content.Currency && incoming.Version > list.Version)
            ? [Error.Validation("price-list.currency-changed", $"The price list is in {stored.Map(l => l.Content.Currency.Code).ValueOr("")}; a price list keeps its currency.") with { Field = "currency" }]
            : EquatableArray<Error>.Empty;

    public static Change<Price> Reconcile(Maybe<Price> stored, Price incoming) =>
        ImportBatch.Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content);

    /// <summary>
    /// Gross unit price: net plus VAT, rounded half away from zero to the decimal places the net price uses, at least
    /// two (10.01 at 23% → 12.31; 0.0123 at 23% → 0.0151).
    /// </summary>
    public static decimal GrossUnit(NetPrice net, VatRate vat) =>
        Math.Round(net.Value * (100 + vat.Percent) / 100, Math.Max(2, DecimalPlaces(net.Value)), MidpointRounding.AwayFromZero);

    private static int DecimalPlaces(decimal value) =>
        Enumerable.Range(0, 28).First(places => decimal.Round(value, places) == value);
}

/// <summary>The value of one priced line: the unit price of the tier for the quantity, the line value net × quantity rounded to 2 places.</summary>
public sealed record QuotedValue(Currency Currency, VatRate Vat, NetPrice UnitNet, PriceQuantity Quantity)
{
    public decimal UnitGross => Prices.GrossUnit(UnitNet, Vat);

    public decimal NetAmount => Quotes.Round(UnitNet.Value * Quantity.Value);
}

/// <summary>The net value of the lines with one VAT rate, and the VAT of it.</summary>
public sealed record RateTotal
{
    internal RateTotal(VatRate vat, decimal net)
    {
        Vat = vat;
        Net = net;
    }

    public VatRate Vat { get; }

    public decimal Net { get; }

    /// <summary>VAT of the summed net value, rounded half away from zero.</summary>
    public decimal VatAmount => Quotes.Round(Net * Vat.Percent / 100);
}

/// <summary>The value of the lines in one currency: VAT per rate (ascending), gross = net + VAT.</summary>
public sealed record CurrencyTotal
{
    internal CurrencyTotal(Currency currency, EquatableArray<RateTotal> rates)
    {
        Currency = currency;
        Rates = rates;
    }

    public Currency Currency { get; }

    public EquatableArray<RateTotal> Rates { get; }

    public decimal Net => Rates.Sum(rate => rate.Net);

    public decimal VatAmount => Rates.Sum(rate => rate.VatAmount);

    public decimal Gross => Net + VatAmount;
}

/// <summary>Values of lines and totals, the way an invoice computes them.</summary>
public static class Quotes
{
    /// <summary>The unit price of the tier for the quantity.</summary>
    public static QuotedValue Line(Currency currency, PriceContent price, PriceQuantity quantity) =>
        new(currency, price.Vat, price.Tiers.For(quantity).Net, quantity);

    /// <summary>Per currency (alphabetically): VAT of each rate from the sum of its net values. Rates in ascending order.</summary>
    public static EquatableArray<CurrencyTotal> Total(IEnumerable<QuotedValue> lines) =>
        [.. lines
            .GroupBy(line => line.Currency)
            .OrderBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(currency => new CurrencyTotal(
                currency.Key,
                [.. currency
                    .GroupBy(line => line.Vat)
                    .OrderBy(rate => rate.Key.Percent)
                    .Select(rate => new RateTotal(rate.Key, rate.Sum(line => line.NetAmount)))]))];

    internal static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>An active price list a viewer may be priced from.</summary>
public sealed record ActivePriceList(PriceListCode Code, Currency Currency);

/// <summary>Who looks at prices, as far as pricing is concerned.</summary>
public abstract record PricingViewer
{
    private PricingViewer()
    {
    }

    public sealed record Anonymous : PricingViewer;

    /// <param name="OwnList">The active price list of the customer the user acts for; none without one.</param>
    public sealed record SignedIn(Maybe<ActivePriceList> OwnList) : PricingViewer;
}

/// <summary>The seller's settings that decide prices.</summary>
/// <param name="DefaultList">The default price list when set and active.</param>
/// <param name="PublicPrices">Visitors who are not signed in see the default price list ("public-prices" feature).</param>
public sealed record PricingPolicy(Maybe<ActivePriceList> DefaultList, bool PublicPrices);

/// <summary>What a customer brings to pricing.</summary>
public sealed record CustomerTerms(bool Active, Maybe<PriceListCode> PriceList);

public abstract record PriceOutcome
{
    private PriceOutcome()
    {
    }

    public sealed record Priced(ActivePriceList List, PriceContent Price) : PriceOutcome;

    /// <summary>Not signed in; signing in may show a price.</summary>
    public sealed record SignInRequired : PriceOutcome;

    /// <summary>Signed in, but no source prices the product.</summary>
    public sealed record OnRequest : PriceOutcome;
}

/// <summary>Which price a viewer sees: the rules of visibility and fallback in one place.</summary>
public static class PriceResolution
{
    /// <summary>
    /// The pricing viewer of a session: an inactive customer's price list is ignored, and a price list that is not
    /// active (or not imported) is as if the customer had none.
    /// </summary>
    public static PricingViewer Viewer(bool signedIn, Maybe<CustomerTerms> customer, Func<PriceListCode, Maybe<ActivePriceList>> activeList) =>
        signedIn
            ? new PricingViewer.SignedIn(customer.Where(c => c.Active).Bind(c => c.PriceList).Bind(activeList))
            : new PricingViewer.Anonymous();

    /// <summary>
    /// Price lists to look in, in order: the customer's own, then the default one, but only in the same currency
    /// (prices on a page and in a cart are never mixed). Visitors see the default list only with public prices on.
    /// </summary>
    public static EquatableArray<ActivePriceList> Sources(PricingViewer viewer, PricingPolicy policy) =>
        viewer switch
        {
            PricingViewer.SignedIn { OwnList: var own } => own.Match<EquatableArray<ActivePriceList>>(
                list => [list, .. policy.DefaultList.Where(d => d.Currency == list.Currency && d.Code != list.Code).AsEnumerable()],
                () => [.. policy.DefaultList.AsEnumerable()]),
            _ when policy.PublicPrices => [.. policy.DefaultList.AsEnumerable()],
            _ => [],
        };

    /// <summary>The first source with an active price for the SKU; otherwise sign in (visitors) or on request.</summary>
    public static PriceOutcome Resolve(PricingViewer viewer, EquatableArray<ActivePriceList> sources, Func<PriceListCode, Maybe<PriceContent>> priceIn) =>
        sources
            .Choose(list => priceIn(list.Code).Where(price => price.Active).Map(price => (PriceOutcome)new PriceOutcome.Priced(list, price)))
            .FirstOrNone()
            .ValueOr(() => viewer is PricingViewer.SignedIn ? new PriceOutcome.OnRequest() : new PriceOutcome.SignInRequired());
}
