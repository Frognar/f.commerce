using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Pricing.Contracts;

/// <summary>Upserts price lists (headers) sent by the ERP connector; items are validated independently.</summary>
public sealed record ImportPriceLists(IReadOnlyList<PriceListData> PriceLists) : ICommand<Result<ImportReport, Error>>;

/// <param name="Code">ERP code of the price list; identifies it (customers refer to it).</param>
/// <param name="Version">Monotonic version from the ERP (row version or change timestamp).</param>
/// <param name="Currency">ISO 4217 code: every price of the list is in it.</param>
/// <param name="Active">False withdraws the list: it is treated as if it did not exist.</param>
public sealed record PriceListData(string Code, long Version, string Name, string Currency, bool Active = true);

/// <summary>
/// Upserts prices of one imported price list (keyed by SKU); a price list that has not been imported is not found.
/// Prices of SKUs the catalog does not know (yet) are accepted.
/// </summary>
public sealed record ImportPrices(string PriceListCode, IReadOnlyList<PriceData> Prices) : ICommand<Result<ImportReport, Error>>;

/// <summary>The body of a price import; the price list comes from the URL.</summary>
public sealed record PriceBatch(IReadOnlyList<PriceData> Prices);

/// <param name="NetPrice">Net unit price from one unit, at most 4 decimal places.</param>
/// <param name="VatRate">VAT in percent (0–100, at most 2 decimal places), e.g. 23.</param>
/// <param name="Breaks">Lower prices from larger quantities (above 1, ascending, at most 20).</param>
/// <param name="Active">False withdraws the price: the default price list applies again.</param>
public sealed record PriceData(string Sku, long Version, decimal NetPrice, decimal VatRate, IReadOnlyList<PriceBreakData>? Breaks = null, bool Active = true);

/// <param name="FromQuantity">The price applies from this quantity (at most 3 decimal places).</param>
public sealed record PriceBreakData(decimal FromQuantity, decimal NetPrice);

/// <summary>
/// Who is looking at prices. The host builds it only from the session, never from the request: the customer is the
/// one the signed-in user currently acts for.
/// </summary>
/// <param name="CustomerCode">The customer whose price list applies; none for operators and users without a company.</param>
public sealed record PriceViewer(bool SignedIn, string? CustomerCode)
{
    public static PriceViewer Anonymous { get; } = new(false, null);
}

/// <param name="Quantity">The quantity to price (the tier); below 1 means the price from one unit.</param>
public sealed record PriceLine(string Sku, decimal Quantity = 1);

/// <summary>Prices of products for a viewer, in the order of the lines (at most <see cref="MaxLines"/>).</summary>
public sealed record GetPrices(PriceViewer Viewer, IReadOnlyList<PriceLine> Lines) : IQuery<Result<IReadOnlyList<ProductPrice>, Error>>
{
    public const int MaxLines = 500;
}

/// <param name="Status">See <see cref="PriceStatuses"/>.</param>
/// <param name="Offer">The price, when <see cref="Status"/> is "priced".</param>
public sealed record ProductPrice(string Sku, decimal Quantity, string Status, OfferedPrice? Offer);

/// <param name="Unit">The unit price for the requested quantity.</param>
/// <param name="Tiers">Every tier of the price, from one unit up.</param>
public sealed record OfferedPrice(string PriceListCode, string Currency, decimal VatRate, PriceTierView Unit, IReadOnlyList<PriceTierView> Tiers);

/// <param name="Gross">Net plus VAT, rounded half away from zero to the decimal places of the net price (at least 2).</param>
public sealed record PriceTierView(decimal FromQuantity, decimal Net, decimal Gross);

public static class PriceStatuses
{
    public const string Priced = "priced";

    /// <summary>Not signed in: prices may appear after signing in.</summary>
    public const string SignInRequired = "sign-in-required";

    /// <summary>Signed in, but no price list prices the product.</summary>
    public const string OnRequest = "on-request";
}

/// <summary>
/// Values of lines for a viewer (cart, order): the unit price of the tier for each quantity, the net value of each
/// line and totals per currency with VAT per rate. Lines without a price have no value and are left out of totals.
/// </summary>
public sealed record QuoteLines(PriceViewer Viewer, IReadOnlyList<PriceLine> Lines) : IQuery<Result<Quote, Error>>
{
    public const int MaxLines = 500;
}

public sealed record Quote(IReadOnlyList<QuotedLine> Lines, IReadOnlyList<QuoteTotal> Totals);

/// <param name="Status">See <see cref="PriceStatuses"/>; the price fields are set only when priced.</param>
/// <param name="NetAmount">Unit net price × quantity, rounded half away from zero to 2 decimal places.</param>
public sealed record QuotedLine(
    string Sku,
    decimal Quantity,
    string Status,
    string? PriceListCode,
    string? Currency,
    decimal? VatRate,
    decimal? UnitNet,
    decimal? UnitGross,
    decimal? NetAmount);

/// <summary>Totals of one currency: VAT is calculated per rate from the sum of net values of that rate.</summary>
public sealed record QuoteTotal(string Currency, decimal Net, IReadOnlyList<VatAmount> Rates, decimal Vat, decimal Gross);

public sealed record VatAmount(decimal Rate, decimal Net, decimal Vat);

/// <summary>Imported price lists by code (for choosing the default one in the back-office).</summary>
public sealed record GetPriceLists : IQuery<IReadOnlyList<PriceListSummary>>;

public sealed record PriceListSummary(string Code, string Name, string Currency, bool Active);
