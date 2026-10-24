using System.Net;
using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Inventory.Contracts;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>On the shared host: stock levels are on and shown as levels (the default settings).</summary>
public sealed class InventoryTests(HostFixture host)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private async Task<(HttpStatusCode Status, ImportReport? Report)> PutAsync<T>(string path, T body, bool withKey = true)
    {
        using var client = withKey ? host.CreateIntegrationClient() : host.CreateClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ImportReport>(Token) : null);
    }

    private Task<(HttpStatusCode Status, ImportReport? Report)> ImportWarehousesAsync(params WarehouseData[] warehouses) =>
        PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses(warehouses));

    private Task<(HttpStatusCode Status, ImportReport? Report)> ImportStockAsync(string warehouse, params StockData[] stock) =>
        PutAsync($"/integration/v1/inventory/warehouses/{Uri.EscapeDataString(warehouse)}/stock", new StockBatch(stock));

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<Result<TResult, Error>>
        where TResult : notnull
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, Result<TResult, Error>>>();
        return (await handler.HandleAsync(query, Token)).Match(result => result, error => throw new InvalidOperationException(error.Message));
    }

    [Fact]
    public async Task Warehouses_and_stock_are_imported_with_reports()
    {
        var warehouse = Unique("MAG");
        var sku = Unique("SKU");

        var warehouses = await ImportWarehousesAsync(new WarehouseData(warehouse, 1, "Magazyn"), new WarehouseData("", 1, "Bez kodu"));
        var stock = await ImportStockAsync(
            warehouse,
            new StockData(sku, 100, 12.5m, Today.AddDays(3), 40m),
            new StockData(Unique("SKU"), 100, 1m, null, 5m));
        var again = await ImportStockAsync(warehouse, new StockData(sku, 101, 12.5m, Today.AddDays(3), 40m));
        var older = await ImportStockAsync(warehouse, new StockData(sku, 99, 1m));

        Assert.Equal((HttpStatusCode.OK, 1, 1), (warehouses.Status, warehouses.Report!.Created, warehouses.Report.Rejected.Count));
        Assert.Equal((1, "next-delivery.quantity-without-date"), (stock.Report!.Created, stock.Report.Rejected[0].Errors[0].Code));
        Assert.Equal(1, again.Report!.Unchanged);
        Assert.Equal(1, older.Report!.Stale);
    }

    [Fact]
    public async Task Stock_of_an_unknown_warehouse_too_large_batches_and_missing_keys_are_refused()
    {
        var unknown = await ImportStockAsync(Unique("NOPE"), new StockData("X", 1, 1m));
        var tooLarge = await ImportWarehousesAsync([.. Enumerable.Range(0, 1001).Select(i => new WarehouseData($"W{i}", 1, "Magazyn"))]);
        var withoutKey = await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([]), withKey: false);

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.Status);
    }

    [Fact]
    public async Task Summaries_add_up_visible_warehouses_as_levels_without_quantities()
    {
        var (first, second, hidden) = (Unique("A"), Unique("B"), Unique("H"));
        var (low, plenty, none) = (Unique("SKU"), Unique("SKU"), Unique("SKU"));
        await ImportWarehousesAsync(new WarehouseData(first, 1, "Pierwszy"), new WarehouseData(second, 1, "Drugi"), new WarehouseData(hidden, 1, "Serwis", Visible: false));
        await ImportStockAsync(first, new StockData(low, 1, 4m), new StockData(plenty, 1, 6m), new StockData(none, 1, -2m, Today.AddDays(5), 30m));
        await ImportStockAsync(second, new StockData(low, 1, 3m), new StockData(plenty, 1, 6m));
        await ImportStockAsync(hidden, new StockData(low, 1, 500m));

        var overview = await QueryAsync<GetStockSummaries, StockOverview>(new GetStockSummaries([low, plenty, none, Unique("SKU")]));

        Assert.Equal(StockDisplays.Levels, overview.Display);
        Assert.Equal(
            [StockStatuses.Low, StockStatuses.Available, StockStatuses.None, StockStatuses.None],
            overview.Items.Select(i => i.Status));
        Assert.All(overview.Items, i => Assert.Null(i.Quantity));
        Assert.Equal(new DeliveryView(Today.AddDays(5), null), overview.Items[2].NextDelivery);
    }

    [Fact]
    public async Task A_product_shows_every_visible_warehouse_in_order()
    {
        var (late, early, hidden) = (Unique("Z"), Unique("A"), Unique("H"));
        var sku = Unique("SKU");
        await ImportWarehousesAsync(
            new WarehouseData(late, 1, "Poznań", SortOrder: 2),
            new WarehouseData(early, 1, "Wrocław", SortOrder: 1),
            new WarehouseData(hidden, 1, "Serwis", Visible: false));
        await ImportStockAsync(late, new StockData(sku, 1, 50m, Today.AddDays(-1), 10m));
        await ImportStockAsync(hidden, new StockData(sku, 1, 9m));

        var stock = await QueryAsync<GetProductStock, ProductStock>(new GetProductStock(sku));

        var ours = stock.Warehouses.Where(w => w.Code == late || w.Code == early).ToList();
        Assert.Equal([(early, StockStatuses.None), (late, StockStatuses.Available)], ours.Select(w => (w.Code, w.Status)));
        Assert.DoesNotContain(stock.Warehouses, w => w.Code == hidden);
        Assert.Null(ours[1].NextDelivery); // yesterday's delivery is not shown
        Assert.Equal(StockStatuses.Available, stock.Total!.Status);
    }

    [Fact]
    public async Task Availability_for_ordering_sums_visible_warehouses_whatever_the_display()
    {
        var (visible, hidden) = (Unique("V"), Unique("H"));
        var (sku, empty) = (Unique("SKU"), Unique("SKU"));
        await ImportWarehousesAsync(new WarehouseData(visible, 1, "Widoczny"), new WarehouseData(hidden, 1, "Ukryty", Visible: false));
        await ImportStockAsync(visible, new StockData(sku, 1, 7.5m), new StockData(empty, 1, -3m, Today.AddDays(4), 10m));
        await ImportStockAsync(hidden, new StockData(sku, 1, 100m));

        var availability = await QueryAsync<GetAvailability, IReadOnlyList<Availability>>(new GetAvailability([sku, empty, Unique("SKU")]));

        Assert.Equal([new Availability(sku, 7.5m, null), new Availability(empty, 0m, Today.AddDays(4))], availability.Take(2));
        Assert.Equal(0m, availability[2].Available);
    }
}
