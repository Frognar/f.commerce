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

/// <summary>A net unit price that applies from a quantity.</summary>
public sealed record PriceBreak(decimal FromQuantity, decimal Net);

/// <summary>
/// The net unit price from one unit (<see cref="Base"/>) and lower prices from larger quantities (<see cref="Breaks"/>:
/// above 1, strictly ascending). Created only through validation.
/// </summary>
public sealed record PriceTiers : IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>
{
    private PriceTiers(decimal @base, EquatableArray<PriceBreak> breaks)
    {
        Base = @base;
        Breaks = breaks;
    }

    public decimal Base { get; }

    public EquatableArray<PriceBreak> Breaks { get; }

    /// <summary>Every tier, the base one (from 1) first.</summary>
    public EquatableArray<PriceBreak> All => [new PriceBreak(1, Base), .. Breaks];

    /// <summary>The tier for a quantity: the last one it reaches; below 1 the base price.</summary>
    public PriceBreak For(decimal quantity) => All.Last(tier => tier.FromQuantity <= quantity || tier.FromQuantity == 1);

    public static Validated<PriceTiers> Validate(decimal net, IReadOnlyList<PriceBreak> breaks) =>
        ((Func<decimal, IReadOnlyList<PriceBreak>, PriceTiers>)((price, valid) => new PriceTiers(price, [.. valid])))
            .Apply(Prices.ParseNet(net).ToValidated("netPrice"))
            .Apply(ValidateBreaks(breaks));

    private static Validated<IReadOnlyList<PriceBreak>> ValidateBreaks(IReadOnlyList<PriceBreak> breaks) =>
        breaks.Count > Prices.MaxBreaks
            ? Validated.Invalid<IReadOnlyList<PriceBreak>>(Error.Validation("breaks.too-many", $"At most {Prices.MaxBreaks} price breaks.") with { Field = "breaks" })
            : breaks
                .Traverse(ValidateBreak)
                .AtField("breaks")
                .Ensure(
                    valid => valid.Zip(valid.Skip(1)).All(pair => pair.First.FromQuantity < pair.Second.FromQuantity),
                    _ => Error.Validation("breaks.not-ascending", "Price breaks must have strictly ascending quantities.") with { Field = "breaks" });

    private static Validated<PriceBreak> ValidateBreak(PriceBreak tier) =>
        ((Func<decimal, decimal, PriceBreak>)((from, net) => new PriceBreak(from, net)))
            .Apply(Prices.ParseQuantity(tier.FromQuantity)
                .Ensure(quantity => quantity > 1, _ => Error.Validation("break.from-quantity", "A price break applies from more than one unit."))
                .ToValidated("fromQuantity"))
            .Apply(Prices.ParseNet(tier.Net).ToValidated("netPrice"));

    static PriceTiers IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>.Rehydrate(EquatableArray<PriceBreak> raw) =>
        new(raw[0].Net, [.. raw.Skip(1)]);

    EquatableArray<PriceBreak> IPersistedValue<PriceTiers, EquatableArray<PriceBreak>>.Dehydrate() => All;
}

public sealed record PriceListContent(string Name, Currency Currency, bool Active);

/// <summary>A price list header from the ERP; its prices are imported separately.</summary>
public sealed record PriceList(PriceListCode Code, SourceVersion Version, PriceListContent Content);

/// <param name="Active">False withdraws the price (the default price list applies again).</param>
public sealed record PriceContent(PriceTiers Tiers, VatRate Vat, bool Active);

/// <summary>The price of one SKU in one price list.</summary>
public sealed record Price(string Sku, SourceVersion Version, PriceContent Content);

[RawInput]
public sealed record UnvalidatedPriceList(string Code, long Version, string Name, string Currency, bool Active);

/// <summary>A price as received from the ERP (missing required texts arrive as empty).</summary>
[RawInput]
public sealed record UnvalidatedPrice(string Sku, long Version, decimal NetPrice, decimal VatRate, IReadOnlyList<PriceBreak> Breaks, bool Active);

public static class Prices
{
    public const int MaxSku = 64;
    public const int MaxName = 200;
    public const int MaxBreaks = 20;
    public const int NetDecimals = 4;
    public const int QuantityDecimals = 3;

    /// <summary>Bounds that fit the storage columns (numeric(15,4) prices, numeric(15,3) quantities).</summary>
    public const decimal MaxNet = 99_999_999_999.9999m;
    public const decimal MaxQuantity = 999_999_999_999.999m;

    public static Validated<PriceList> Validate(UnvalidatedPriceList input) =>
        ((Func<PriceListCode, SourceVersion, string, Currency, PriceList>)((code, version, name, currency) =>
            new PriceList(code, version, new PriceListContent(name, currency, input.Active))))
            .Apply(PriceListCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(SourceCodes.Parse(input.Name, MaxName, "name", "Name").ToValidated("name"))
            .Apply(Currency.Parse(input.Currency.Trim()).ToValidated("currency"));

    public static Validated<Price> Validate(UnvalidatedPrice input) =>
        ((Func<string, SourceVersion, PriceTiers, VatRate, Price>)((sku, version, tiers, vat) =>
            new Price(sku, version, new PriceContent(tiers, vat, input.Active))))
            .Apply(SourceCodes.Parse(input.Sku, MaxSku, "sku", "SKU").ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(PriceTiers.Validate(input.NetPrice, input.Breaks))
            .Apply(VatRate.Parse(input.VatRate).ToValidated("vatRate"));

    public static Change<PriceList> Reconcile(Maybe<PriceList> stored, PriceList incoming) =>
        ImportBatch.Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content);

    public static Change<Price> Reconcile(Maybe<Price> stored, Price incoming) =>
        ImportBatch.Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content);

    public static Result<decimal, Error> ParseNet(decimal net) =>
        net switch
        {
            <= 0 => Error.Validation("net-price.not-positive", "The net price must be greater than zero."),
            > MaxNet => Error.Validation("net-price.too-large", "The net price is too large."),
            _ when decimal.Round(net, NetDecimals) != net => Error.Validation("net-price.precision", $"Net prices have at most {NetDecimals} decimal places."),
            _ => net,
        };

    public static Result<decimal, Error> ParseQuantity(decimal quantity) =>
        quantity switch
        {
            < 0 => Error.Validation("quantity.negative", "The quantity must not be negative."),
            > MaxQuantity => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(quantity, QuantityDecimals) != quantity => Error.Validation("quantity.precision", $"Quantities have at most {QuantityDecimals} decimal places."),
            _ => quantity,
        };

    /// <summary>
    /// Gross unit price: net plus VAT, rounded half away from zero to the decimal places the net price uses, at least
    /// two (10.01 at 23% → 12.31; 0.0123 at 23% → 0.0151).
    /// </summary>
    public static decimal GrossUnit(decimal net, VatRate vat) =>
        Math.Round(net * (100 + vat.Percent) / 100, Math.Max(2, DecimalPlaces(net)), MidpointRounding.AwayFromZero);

    private static int DecimalPlaces(decimal value) =>
        Enumerable.Range(0, 28).First(places => decimal.Round(value, places) == value);
}

/// <summary>The value of one priced line.</summary>
public sealed record QuotedValue(Currency Currency, VatRate Vat, decimal UnitNet, decimal UnitGross, decimal NetAmount);

public sealed record RateTotal(VatRate Vat, decimal Net, decimal VatAmount);

public sealed record CurrencyTotal(Currency Currency, EquatableArray<RateTotal> Rates, decimal Net, decimal VatAmount, decimal Gross);

/// <summary>Values of lines and totals, the way an invoice computes them.</summary>
public static class Quotes
{
    /// <summary>The unit price of the tier for the quantity; the line value is net × quantity rounded to 2 places.</summary>
    public static QuotedValue Line(Currency currency, PriceContent price, decimal quantity)
    {
        var unit = price.Tiers.For(quantity).Net;
        return new QuotedValue(currency, price.Vat, unit, Prices.GrossUnit(unit, price.Vat), Round(unit * quantity));
    }

    /// <summary>
    /// Per currency (alphabetically): VAT of each rate from the sum of its net values (rounded half away from zero),
    /// gross = net + VAT. Rates in ascending order.
    /// </summary>
    public static EquatableArray<CurrencyTotal> Total(IEnumerable<QuotedValue> lines) =>
        [.. lines
            .GroupBy(line => line.Currency)
            .OrderBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(currency =>
            {
                EquatableArray<RateTotal> rates = [.. currency
                    .GroupBy(line => line.Vat)
                    .OrderBy(rate => rate.Key.Percent)
                    .Select(rate =>
                    {
                        var net = rate.Sum(line => line.NetAmount);
                        return new RateTotal(rate.Key, net, Round(net * rate.Key.Percent / 100));
                    })];
                var net = rates.Sum(r => r.Net);
                var vat = rates.Sum(r => r.VatAmount);
                return new CurrencyTotal(currency.Key, rates, net, vat, net + vat);
            })];

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
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
