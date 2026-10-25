using System.Net;
using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>An installation whose ERP takes orders another way: the order feed is not available.</summary>
public sealed class NoOrderFeedHostFixture : HostFixture
{
    protected override IWebHostBuilder Configure(IWebHostBuilder host) =>
        Feature.All
            .Where(f => f.Id != Feature.OrderFeed)
            .Select((f, index) => (f.Id.Value, index))
            .Aggregate(host, (builder, f) => builder.UseSetting($"Features:Available:{f.index}", f.Value));
}

/// <summary>The ERP's side of orders through the integration API: the feed, confirmations and documents naming orders.</summary>
public sealed class OrderFeedApiTests(HostFixture host, NoOrderFeedHostFixture noFeed) : IClassFixture<NoOrderFeedHostFixture>
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private async Task PutAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpStatusCode> PutStatusAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        return response.StatusCode;
    }

    private async Task<OrderFeedPage> FeedAsync(long after, HostFixture? on = null)
    {
        using var client = (on ?? host).CreateIntegrationClient();
        return (await client.GetFromJsonAsync<OrderFeedPage>($"/integration/v1/orders/feed?after={after}&limit=500", Token))!;
    }

    private static string Path(string number, string action) => $"/integration/v1/orders/{Uri.EscapeDataString(number)}/{action}";

    /// <summary>A buyer of a new customer orders one product (placed through the module, as the checkout does).</summary>
    private async Task<(OrderPlacement Order, string Customer)> OrderAsync(decimal quantity = 2)
    {
        var sku = Unique("A");
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([new ProductData(sku, 1, new Dictionary<string, string> { ["pl"] = "Śruba" }, null, null, "szt", null, [], [])]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(sku, 1, 4m, 23m)]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([new StockData(sku, 1, 1m, null, null)]));
        var customer = Unique("K");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            customer, 1, $"Firma {customer}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null, [], null, PriceListCode: list)]));

        var buyer = new BuyerTerms(Guid.NewGuid(), customer, null, null, null);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        (await services.GetRequiredService<ICommandHandler<AddToCart, Result<CartChange, Error>>>()
            .HandleAsync(new AddToCart(new CartOwner(buyer.UserId, customer), [new CartItemData(sku, quantity)], CartSources.Product), Token)).Match(_ => 0, e => throw new InvalidOperationException(e.Code));
        var checkout = await services.GetRequiredService<IQueryHandler<GetCheckout, CheckoutView>>().HandleAsync(new GetCheckout(buyer, ["pl"]), Token);
        var placed = await services.GetRequiredService<ICommandHandler<PlaceOrder, Result<OrderPlacement, Error>>>()
            .HandleAsync(new PlaceOrder(Guid.CreateVersion7(), buyer, ["pl"], checkout.CartVersion, checkout.Total!.Gross, "PO-9", "Pilne", null, null), Token);
        return (placed.Match(o => o, e => throw new InvalidOperationException(e.Code)), customer);
    }

    private async Task<OrderFeedEntry> FedAsync(Guid orderId, string type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var entry = (await FeedAsync(0)).Entries.FirstOrDefault(e => e.Order.Id == orderId && e.Type == type);
            if (entry is not null)
            {
                return entry;
            }

            Assert.True(DateTime.UtcNow < deadline, "The feed did not catch up.");
            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task The_erp_reads_placed_orders_from_the_feed_after_its_last_position()
    {
        var (order, customer) = await OrderAsync();

        var entry = await FedAsync(order.OrderId, "placed");
        var later = await FeedAsync(entry.Position);
        using var anonymous = host.CreateClient();
        using var withoutKey = await anonymous.GetAsync("/integration/v1/orders/feed", Token);

        Assert.Equal((order.Number, customer, OrderStatuses.Placed, "PO-9", "Pilne"), (entry.Order.Number, entry.Order.CustomerCode, entry.Order.Status, entry.Order.CustomerReference, entry.Order.Notes));
        var line = Assert.Single(entry.Order.Lines);
        Assert.Equal((1, "Śruba", 2m, 4m, 8m, true), (line.Number, line.Name, line.Quantity, line.UnitNet, line.NetAmount, line.Backorder));
        Assert.Equal((8m, 9.84m), (entry.Order.Total.Net, entry.Order.Total.Gross));
        Assert.DoesNotContain(later.Entries, e => e.Order.Id == order.OrderId);
        Assert.True(later.LastPosition >= entry.Position);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
    }

    [Fact]
    public async Task Confirmations_change_the_order_once_and_conflicts_are_reported()
    {
        var (accepted, _) = await OrderAsync();
        var (rejected, _) = await OrderAsync();
        await FedAsync(accepted.OrderId, "placed");
        await FedAsync(rejected.OrderId, "placed");

        Assert.Equal(HttpStatusCode.NoContent, await PutStatusAsync(Path(accepted.Number, "acceptance"), new OrderAcceptanceData("ZAM/2026/77")));
        Assert.Equal(HttpStatusCode.NoContent, await PutStatusAsync(Path(accepted.Number, "acceptance"), new OrderAcceptanceData("ZAM/2026/77")));
        Assert.Equal(HttpStatusCode.Conflict, await PutStatusAsync(Path(accepted.Number, "acceptance"), new OrderAcceptanceData("ZAM/2026/78")));
        Assert.Equal(HttpStatusCode.Conflict, await PutStatusAsync(Path(accepted.Number, "rejection"), new OrderReasonData("Brak towaru")));
        Assert.Equal(HttpStatusCode.BadRequest, await PutStatusAsync(Path(accepted.Number, "acceptance"), new OrderAcceptanceData(" ")));
        Assert.Equal(HttpStatusCode.NoContent, await PutStatusAsync(Path(rejected.Number, "rejection"), new OrderReasonData("Brak towaru")));
        Assert.Equal(HttpStatusCode.Conflict, await PutStatusAsync(Path(rejected.Number, "cancellation"), new OrderReasonData(null)));
        Assert.Equal(HttpStatusCode.NotFound, await PutStatusAsync(Path("WWW/1900/000001", "acceptance"), new OrderAcceptanceData("ZAM/1")));
        Assert.Equal(HttpStatusCode.NoContent, await PutStatusAsync(Path(accepted.Number, "cancellation"), new OrderReasonData("Klient zrezygnował")));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetOrder, Result<OrderView, Error>>>();
        var first = (await orders.HandleAsync(new GetOrder(accepted.OrderId), Token)).Match(o => o, e => throw new InvalidOperationException(e.Code));
        var second = (await orders.HandleAsync(new GetOrder(rejected.OrderId), Token)).Match(o => o, e => throw new InvalidOperationException(e.Code));
        Assert.Equal((OrderStatuses.Cancelled, "seller", "Klient zrezygnował", "ZAM/2026/77"), (first.Status, first.CancelledBy, first.Reason, first.ErpNumber));
        Assert.Equal((OrderStatuses.Rejected, "Brak towaru"), (second.Status, second.Reason));
    }

    [Fact]
    public async Task An_order_the_buyer_cancelled_appears_in_the_feed_and_cannot_be_accepted()
    {
        var (order, _) = await OrderAsync();
        await FedAsync(order.OrderId, "placed");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var placedBy = (await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetOrder, Result<OrderView, Error>>>().HandleAsync(new GetOrder(order.OrderId), Token))
                .Match(o => o.PlacedBy, e => throw new InvalidOperationException(e.Code));
            (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, Result<Unit, Error>>>().HandleAsync(new CancelOrder(order.OrderId, placedBy), Token))
                .Match(_ => 0, e => throw new InvalidOperationException(e.Code));
        }

        var cancelled = await FedAsync(order.OrderId, "cancelled");

        Assert.Equal(OrderStatuses.Cancelled, cancelled.Order.Status);
        Assert.Equal(HttpStatusCode.Conflict, await PutStatusAsync(Path(order.Number, "acceptance"), new OrderAcceptanceData("ZAM/1")));
    }

    [Fact]
    public async Task A_document_with_the_erp_number_completes_the_order()
    {
        var (order, customer) = await OrderAsync();
        await FedAsync(order.OrderId, "placed");
        await PutAsync(Path(order.Number, "acceptance"), new OrderAcceptanceData($"ZAM-{order.Number}"));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var invoice = Unique("FV");
        var imported = false;
        while (true)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var view = (await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetOrders, Result<OrderPage, Error>>>()
                .HandleAsync(new GetOrders(customer), Token)).Match(p => p.Items.Single(), e => throw new InvalidOperationException(e.Code));
            if (view.Status == OrderStatuses.Completed)
            {
                break;
            }

            // The document is matched by the ERP number once the order list knows it.
            if (!imported && view.ErpNumber is not null)
            {
                await PutAsync("/integration/v1/documents", new ImportDocuments([
                    new DocumentData(invoice, 1, customer, "invoice", $"FV/{invoice}", Today, Currency: "PLN", Net: 8m, Gross: 9.84m, OrderNumber: $"ZAM-{order.Number}")]));
                imported = true;
            }

            Assert.True(DateTime.UtcNow < deadline, "The invoice did not complete the order.");
            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task Without_the_order_feed_feature_the_order_endpoints_are_not_found()
    {
        using var client = noFeed.CreateIntegrationClient();

        using var feed = await client.GetAsync("/integration/v1/orders/feed", Token);
        using var acceptance = await client.PutAsJsonAsync(Path("WWW/2026/000001", "acceptance"), new OrderAcceptanceData("ZAM/1"), Token);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (feed.StatusCode, acceptance.StatusCode));
    }
}
