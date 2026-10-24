using FCommerce.Catalog.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Two PLN price lists with 23% VAT: DETAL (retail, most products; meant as the default price list) and HURT-A
/// (wholesale for K-1001, about 70% of the products, fasteners with quantity breaks, some prices with 4 decimal
/// places). A few SKUs have no price, to show "sign in to see prices" and "price on request".
/// </summary>
internal static class DemoPricing
{
    public const string Retail = "DETAL";
    public const string Wholesale = "HURT-A";
    public const decimal VatRate = 23m;

    private static readonly HashSet<string> Fasteners = ["bolts", "nuts", "washers", "anchors"];

    /// <summary>The retail net price of a product; invoices are priced with it too.</summary>
    public static decimal Base(string sku) => 0.30m + (DemoHash.Of(sku) % 2500 / 100m);

    public static IReadOnlyList<PriceListData> Lists(long version) =>
    [
        new(Retail, version, "Cennik detaliczny", "PLN"),
        new(Wholesale, version, "Cennik hurtowy A", "PLN"),
    ];

    public static IReadOnlyList<PriceData> RetailPrices(IEnumerable<ProductData> products, long version) =>
        [.. products
            .Where(p => DemoHash.Of($"{p.Sku}/retail") % 12 != 0)
            .Select(p => new PriceData(p.Sku, version, Base(p.Sku), VatRate))];

    public static IReadOnlyList<PriceData> WholesalePrices(IEnumerable<ProductData> products, long version) =>
        [.. products
            .Where(p => DemoHash.Of($"{p.Sku}/wholesale") % 10 < 7)
            .Select(p =>
            {
                var net = Math.Round(Base(p.Sku) * 0.85m, 2, MidpointRounding.AwayFromZero);
                IReadOnlyList<PriceBreakData> breaks = Fasteners.Contains(p.CategoryCode ?? string.Empty)
                    ? [new(50, Math.Round(net * 0.95m, 2, MidpointRounding.AwayFromZero)), new(200, Math.Round(net * 0.8875m, 4, MidpointRounding.AwayFromZero))]
                    : [];
                return new PriceData(p.Sku, version, net, VatRate, breaks);
            })];
}

/// <summary>A stable hash (FNV-1a), so the demo data is the same on every run.</summary>
internal static class DemoHash
{
    public static ulong Of(string text) => text.Aggregate(1469598103934665603UL, (h, c) => (h ^ c) * 1099511628211UL);
}
