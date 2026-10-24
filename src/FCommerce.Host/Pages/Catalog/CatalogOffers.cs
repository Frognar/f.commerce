using FCommerce.Application.Messaging;
using FCommerce.Host.Security;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Pages.Catalog;

/// <summary>Prices and stock of products on a list, by SKU.</summary>
public sealed record ListingOffers(IReadOnlyDictionary<string, ProductPrice> Prices, StockOverview Stock)
{
    public static ListingOffers None { get; } = new(new Dictionary<string, ProductPrice>(), new StockOverview(StockDisplays.Hidden, []));

    public Maybe<ProductPrice> PriceOf(string sku) => Prices.GetValueOrNone(sku);

    public Maybe<StockBadge> StockOf(string sku, string unit) =>
        Stock.Items.FirstOrNone(item => item.Sku == sku).Map(item => StockBadge.From(Stock.Display, item, unit));
}

/// <summary>Price and stock of one product.</summary>
public sealed record ProductOffer(ProductPrice Price, ProductStock Stock);

/// <summary>What the price partial shows.</summary>
/// <param name="SignInHref">Sign-in link that returns to the current page.</param>
/// <param name="Detailed">The product page shows VAT and every tier; lists show the unit price and the first break.</param>
public sealed record PriceTag(ProductPrice Price, string Unit, string SignInHref, bool Detailed);

/// <summary>What the stock partial shows (from a summary or a warehouse).</summary>
public sealed record StockBadge(string Display, string Status, Maybe<decimal> Quantity, Maybe<DeliveryView> NextDelivery, string Unit)
{
    public static StockBadge From(string display, StockSummary stock, string unit) =>
        new(display, stock.Status, stock.Quantity.ToMaybe(), stock.NextDelivery.ToMaybe(), unit);

    public static StockBadge From(string display, WarehouseStock stock, string unit) =>
        new(display, stock.Status, stock.Quantity.ToMaybe(), stock.NextDelivery.ToMaybe(), unit);
}

/// <summary>
/// Prices and stock for catalog pages. The viewer always comes from the session (<see cref="CurrentUser"/>), never
/// from the request, so a page cannot be asked for another customer's prices.
/// </summary>
public sealed class CatalogOffers(
    CurrentUser user,
    IQueryHandler<GetPrices, Result<IReadOnlyList<ProductPrice>, Error>> prices,
    IQueryHandler<GetStockSummaries, Result<StockOverview, Error>> stockSummaries,
    IQueryHandler<GetProductStock, Result<ProductStock, Error>> productStock)
{
    /// <summary>One price query (quantity 1) and one stock query for the products of a list.</summary>
    public async Task<ListingOffers> ListAsync(IReadOnlyList<string> skus, CancellationToken cancellationToken)
    {
        if (skus.Count == 0)
        {
            return ListingOffers.None;
        }

        var viewer = await user.PriceViewerAsync(cancellationToken);
        var priced = await prices.HandleAsync(new GetPrices(viewer, [.. skus.Select(sku => new PriceLine(sku))]), cancellationToken);
        var stock = await stockSummaries.HandleAsync(new GetStockSummaries(skus), cancellationToken);
        return new ListingOffers(
            priced.Match(list => list.DistinctBy(p => p.Sku).ToDictionary(p => p.Sku), _ => []),
            stock.Match(overview => overview, _ => ListingOffers.None.Stock));
    }

    /// <summary>The price (with every tier) and the stock per warehouse of one product; none for an invalid SKU.</summary>
    public async Task<Maybe<ProductOffer>> ProductAsync(string sku, CancellationToken cancellationToken)
    {
        var viewer = await user.PriceViewerAsync(cancellationToken);
        var priced = await prices.HandleAsync(new GetPrices(viewer, [new PriceLine(sku)]), cancellationToken);
        var stock = await productStock.HandleAsync(new GetProductStock(sku), cancellationToken);
        return (from list in priced from price in list.FirstOrNone().ToResult(() => Error.NotFound("price.none", "No price line.")) from s in stock select new ProductOffer(price, s))
            .ToMaybe();
    }
}
