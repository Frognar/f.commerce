using FCommerce.Catalog.Contracts;
using FCommerce.Inventory.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Warehouses in Wrocław and Poznań (visible to buyers) and a service warehouse (hidden). Stock is deterministic per
/// SKU: about 15% out of stock with a delivery in 3–14 days, about 20% low, some products only in one warehouse.
/// </summary>
internal static class DemoInventory
{
    public const string Wroclaw = "WRO";
    public const string Poznan = "POZ";
    public const string Service = "SERWIS";

    public static IReadOnlyList<WarehouseData> Warehouses(long version) =>
    [
        new(Wroclaw, version, "Wrocław", Visible: true, SortOrder: 1),
        new(Poznan, version, "Poznań", Visible: true, SortOrder: 2),
        new(Service, version, "Serwis", Visible: false, SortOrder: 3),
    ];

    /// <summary>Stock per warehouse code; <paramref name="version"/> is the moment of the snapshot (as an ERP would send).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<StockData>> Stock(IEnumerable<ProductData> products, long version, DateOnly today)
    {
        var wroclaw = new List<StockData>();
        var poznan = new List<StockData>();
        var service = new List<StockData>();
        foreach (var product in products)
        {
            var hash = DemoHash.Of($"{product.Sku}/stock");
            var bucket = hash % 100;
            if (bucket < 15)
            {
                var delivery = today.AddDays(3 + (int)(hash / 100 % 12));
                wroclaw.Add(new StockData(product.Sku, version, 0, delivery, 50 + (hash / 7 % 10 * 25)));
                poznan.Add(new StockData(product.Sku, version, 0));
            }
            else if (bucket < 35)
            {
                wroclaw.Add(new StockData(product.Sku, version, 1 + (hash / 100 % 5)));
                poznan.Add(new StockData(product.Sku, version, hash / 1000 % 4));
            }
            else
            {
                wroclaw.Add(new StockData(product.Sku, version, 20 + (hash / 100 % 480)));
                if (bucket % 3 != 0)
                {
                    poznan.Add(new StockData(product.Sku, version, 10 + (hash / 1000 % 290), bucket % 4 == 0 ? today.AddDays(7) : null, bucket % 4 == 0 ? 100 : null));
                }
            }

            if (bucket % 10 == 0)
            {
                service.Add(new StockData(product.Sku, version, 5));
            }
        }

        return new Dictionary<string, IReadOnlyList<StockData>> { [Wroclaw] = wroclaw, [Poznan] = poznan, [Service] = service };
    }
}
