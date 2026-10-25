using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Cart.Domain;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart;

/// <summary>
/// Load the cart → products, quote, availability, customer and settings through their contracts → pure review →
/// view. Prices are always current: the cart holds only SKUs and quantities. A failed lookup does not fail the view
/// (the cart page still shows), but marks it incomplete so no order is placed from it.
/// </summary>
internal sealed class GetCartHandler(
    CartStore carts,
    IQueryHandler<GetProductsBySku, Result<IReadOnlyList<ProductSummary>, Error>> products,
    IQueryHandler<QuoteLines, Result<Quote, Error>> quotes,
    IQueryHandler<GetAvailability, Result<IReadOnlyList<Availability>, Error>> availability,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IQueryHandler<GetCart, CartView>
{
    public async Task<CartView> HandleAsync(GetCart query, CancellationToken cancellationToken = default)
    {
        var (cart, version) = await carts.LoadWithVersionAsync(query.Owner, cancellationToken);
        var skus = cart.Items.Select(item => item.Sku).ToList();
        var platform = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        var display = !platform.IsOn(Feature.StockLevels.Value) ? StockDisplays.Hidden
            : platform.StockDisplay == "Exact" ? StockDisplays.Exact
            : StockDisplays.Levels;

        var offered = skus.Count == 0
            ? Result.Ok<IReadOnlyList<ProductSummary>, Error>([])
            : await products.HandleAsync(new GetProductsBySku(query.Cultures.OrEmpty(), skus), cancellationToken);
        var quote = skus.Count == 0
            ? Result.Ok<Quote, Error>(new Quote([], []))
            : await quotes.HandleAsync(new QuoteLines(new PriceViewer(true, query.Owner.CustomerCode), [.. cart.Items.Select(i => new PriceLine(i.Sku, i.Quantity))]), cancellationToken);
        var stock = skus.Count == 0
            ? Result.Ok<IReadOnlyList<Availability>, Error>([])
            : await availability.HandleAsync(new GetAvailability(skus), cancellationToken);
        var customer = await customers.HandleAsync(new GetCustomer(query.Owner.CustomerCode), cancellationToken);
        var complete = offered.Match(_ => true, _ => false) && quote.Match(_ => true, _ => false)
            && stock.Match(_ => true, _ => false) && customer.Match(_ => true, _ => false);

        var productsBySku = offered.Match(found => found.ToDictionary(p => p.Sku), _ => []);
        var quoted = quote.Match(q => q, _ => new Quote([], []));
        var priced = quoted.Lines.Where(l => l.Status == PriceStatuses.Priced).ToDictionary(l => l.Sku);
        var stockBySku = stock.Match(found => found.ToDictionary(a => a.Sku), _ => []);
        var limit = query.OrderLimit.ToMaybe().Bind(l => Currency.Parse(l.Currency.OrEmpty()).ToMaybe().Map(currency => new Money(l.Amount, currency)));

        var review = CartReview.Review(
            cart,
            sku => productsBySku.GetValueOrNone(sku).Map(p => new ProductFacts(p.Discontinued)),
            priced.ContainsKey,
            sku => stockBySku.GetValueOrNone(sku).Match(a => new StockFacts(a.Available, a.NextDelivery.ToMaybe()), () => StockFacts.None),
            [.. quoted.Totals.Choose(t => Currency.Parse(t.Currency.OrEmpty()).ToMaybe().Map(currency => new Money(t.Net, currency)))],
            new OrderTerms(customer.Match(c => c.Blocked, _ => false), limit));

        return new CartView(
            [.. review.Lines.Select(line => Line(line, productsBySku.GetValueOrNone(line.Item.Sku), priced.GetValueOrNone(line.Item.Sku), display))],
            [.. quoted.Totals.Select(t => new CartTotalView(t.Currency, t.Net, [.. t.Rates.Select(r => new CartVatView(r.Rate, r.Net, r.Vat))], t.Vat, t.Gross))],
            [.. review.Issues.Select(IssueCode)],
            review.CanOrder,
            display,
            version,
            complete,
            limit.Map(l => new CartOrderLimit(l.Amount, l.Currency.Code)).OrNull());
    }

    private static CartLineView Line(LineReview line, Maybe<ProductSummary> product, Maybe<QuotedLine> price, string display) =>
        new(
            line.Item.Sku,
            product.Map(p => p.Name).OrNull(),
            product.Map(p => p.Unit).OrNull(),
            product.Bind(p => p.ImageId.ToMaybe()).OrNull(),
            product.Exists(p => p.Discontinued),
            line.Item.Quantity,
            price.Match(_ => PriceStatuses.Priced, () => PriceStatuses.OnRequest),
            price.Bind(p => p.Currency.ToMaybe()).OrNull(),
            price.Bind(p => p.VatRate.ToMaybe()).OrNull(),
            price.Bind(p => p.UnitNet.ToMaybe()).OrNull(),
            price.Bind(p => p.UnitGross.ToMaybe()).OrNull(),
            price.Bind(p => p.NetAmount.ToMaybe()).OrNull(),
            [.. line.Issues.Select(issue => Issue(issue, display))],
            line.Blocks);

    /// <summary>Quantities on hand only in the exact stock display.</summary>
    private static CartLineIssueView Issue(LineIssue issue, string display)
    {
        Maybe<decimal> Shown(decimal available) => display == StockDisplays.Exact ? available : Maybe<decimal>.None;
        return issue switch
        {
            LineIssue.Unavailable => new CartLineIssueView("unavailable", true, null, null),
            LineIssue.NoPrice => new CartLineIssueView("no-price", true, null, null),
            LineIssue.DiscontinuedShortage shortage => new CartLineIssueView("discontinued-shortage", true, Shown(shortage.Available).OrNull(), null),
            LineIssue.Backorder backorder => new CartLineIssueView("backorder", false, Shown(backorder.Available).OrNull(), backorder.NextDelivery.OrNull()),
            _ => new CartLineIssueView("unknown", true, null, null),
        };
    }

    private static string IssueCode(CartIssue issue) =>
        issue switch
        {
            CartIssue.Empty => "empty",
            CartIssue.CustomerBlocked => "customer-blocked",
            CartIssue.MixedCurrencies => "mixed-currencies",
            CartIssue.OverLimit => "over-limit",
            _ => "unknown",
        };
}
