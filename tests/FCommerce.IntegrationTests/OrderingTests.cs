using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>The ordering module through its contracts: orders as event streams, the order list and feed as projections.</summary>
public sealed class OrderingTests(HostFixture host)
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

    private async Task<Result<TResult, Error>> SendAsync<TCommand, TResult>(TCommand command)
        where TCommand : ICommand<Result<TResult, Error>>
        where TResult : notnull
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, Result<TResult, Error>>>().HandleAsync(command, Token);
    }

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
        where TResult : notnull
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, Token);
    }

    private static T Ok<T>(Result<T, Error> result)
        where T : notnull =>
        result.Match(value => value, error => throw new InvalidOperationException($"{error.Code}: {error.Message}"));

    private static string Code<T>(Result<T, Error> result)
        where T : notnull =>
        result.Match(_ => "ok", error => error.Details.IsEmpty ? error.Code : string.Join(",", error.Details.Select(d => d.Code)));

    /// <summary>Two products priced 10 and 2.50 net (23%), 5 of each in stock, a customer with two delivery addresses.</summary>
    private async Task<(BuyerTerms Buyer, string[] Skus)> ShopAsync(IReadOnlyList<string>? allowedAddresses = null, decimal? limit = null)
    {
        string[] skus = [Unique("A"), Unique("B")];
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([.. skus.Select((sku, i) => new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Produkt {i}" }, null, null, "szt", null, [], []))]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(skus[0], 1, 10m, 23m), new PriceData(skus[1], 1, 2.5m, 23m)]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([.. skus.Select(sku => new StockData(sku, 1, 5m, null, null))]));
        var customer = Unique("K");
        var address = new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            customer, 1, $"Firma {customer}", null, address,
            [new DeliveryAddressData("MAG", "Magazyn", address, IsDefault: true), new DeliveryAddressData("BUD", "Budowa", address with { Street = "ul. Krótka 2" })],
            [], new SalespersonData("Anna Handlowiec", "Anna@Seller.test", null), PriceListCode: list)]));
        return (new BuyerTerms(Guid.NewGuid(), customer, limit, limit is null ? null : "PLN", allowedAddresses), skus);
    }

    private async Task AddAsync(BuyerTerms buyer, params (string Sku, decimal Quantity)[] items) =>
        Ok(await SendAsync<AddToCart, CartChange>(new AddToCart(new CartOwner(buyer.UserId, buyer.CustomerCode), [.. items.Select(i => new CartItemData(i.Sku, i.Quantity))], CartSources.Product)));

    /// <summary>Places the order like the checkout page: then takes the items out of the cart at once (the event would do it later).</summary>
    private async Task<Result<OrderPlacement, Error>> PlaceAsync(BuyerTerms buyer, Guid? id = null, string? address = null, long? version = null, decimal? gross = null, bool takeOut = true)
    {
        var checkout = await QueryAsync<GetCheckout, CheckoutView>(new GetCheckout(buyer, ["pl"]));
        var placed = await SendAsync<PlaceOrder, OrderPlacement>(new PlaceOrder(
            id ?? Guid.CreateVersion7(), buyer, ["pl"], version ?? checkout.CartVersion, gross ?? checkout.Total?.Gross ?? 0m, "PO-1", "Uwagi", Today.AddDays(2), address));
        foreach (var order in placed.Match(o => [o], _ => Array.Empty<OrderPlacement>()).Where(_ => takeOut))
        {
            Ok(await SendAsync<RemoveOrderedItems, CartChange>(new RemoveOrderedItems(
                new CartOwner(buyer.UserId, buyer.CustomerCode), order.OrderId, [.. order.Items.Select(i => new CartItemData(i.Sku, i.Quantity))])));
        }

        return placed;
    }

    private async Task<OrderSummaryView> ListedAsync(BuyerTerms buyer, Guid orderId, Func<OrderSummaryView, bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var page = Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(CustomerCode: buyer.CustomerCode)));
            var found = page.Items.FirstOrDefault(o => o.Id == orderId);
            if (found is not null && (until?.Invoke(found) ?? true))
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "The order list did not catch up.");
            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task An_order_is_placed_from_the_reviewed_cart_once_and_listed_and_fed_to_the_erp()
    {
        var (buyer, skus) = await ShopAsync();
        await AddAsync(buyer, (skus[0], 2), (skus[1], 4));
        var id = Guid.CreateVersion7();

        var checkout = await QueryAsync<GetCheckout, CheckoutView>(new GetCheckout(buyer, ["pl"]));
        var placed = Ok(await PlaceAsync(buyer, id, "BUD"));
        var again = Ok(await PlaceAsync(buyer, id, "BUD"));
        var order = Ok(await QueryAsync<GetOrder, Result<OrderView, Error>>(new GetOrder(id)));
        var listed = await ListedAsync(buyer, id);

        Assert.True(checkout.CanPlace);
        Assert.Equal(["MAG", "BUD"], checkout.Addresses.Select(a => a.Code));
        Assert.Equal("MAG", checkout.SuggestedAddressCode);
        Assert.Matches($"^WWW/{Today.Year}/\\d{{6}}$", placed.Number);
        Assert.Equal((false, true, placed.Number), (placed.AlreadyPlaced, again.AlreadyPlaced, again.Number));
        Assert.Equal([(skus[0], 2m), (skus[1], 4m)], placed.Items.Select(i => (i.Sku, i.Quantity)));
        Assert.Equal((OrderStatuses.Placed, "PO-1", "Uwagi", "BUD"), (order.Status, order.CustomerReference, order.Notes, order.DeliveryAddress?.Code));
        Assert.Equal([("Produkt 0", 10m, 20m), ("Produkt 1", 2.5m, 10m)], order.Lines.Select(l => (l.Name, l.UnitNet, l.NetAmount)));
        Assert.Equal(("PLN", 30m, 36.9m), (order.Total.Currency, order.Total.Net, order.Total.Gross));
        Assert.Equal((placed.Number, 2, 36.9m, $"Firma {buyer.CustomerCode}"), (listed.Number, listed.LineCount, listed.Gross, listed.CustomerName));
        Assert.Contains(id, await SalespersonOrdersAsync("anna@seller.test"));

        var feed = await QueryAsync<GetOrderFeed, OrderFeedPage>(new GetOrderFeed(0, GetOrderFeed.MaxLimit));
        var entry = Assert.Single(feed.Entries, e => e.Order.Id == id);
        Assert.Equal(("placed", placed.Number, 2), (entry.Type, entry.Order.Number, entry.Order.Lines.Count));
        Assert.DoesNotContain((await QueryAsync<GetOrderFeed, OrderFeedPage>(new GetOrderFeed(feed.LastPosition))).Entries, e => e.Order.Id == id);
    }

    private async Task<IReadOnlyList<Guid>> SalespersonOrdersAsync(string email) =>
        [.. Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(SalespersonEmail: email, PageSize: GetOrders.MaxPageSize))).Items.Select(o => o.Id)];

    [Fact]
    public async Task A_changed_cart_a_limit_or_an_address_the_buyer_may_not_use_stops_the_order()
    {
        var (buyer, skus) = await ShopAsync(allowedAddresses: ["BUD"], limit: 25m);
        await AddAsync(buyer, (skus[0], 2));
        var seen = await QueryAsync<GetCheckout, CheckoutView>(new GetCheckout(buyer, ["pl"]));
        await AddAsync(buyer, (skus[1], 1));

        var changed = await PlaceAsync(buyer, version: seen.CartVersion, gross: seen.Total!.Gross);
        var wrongAddress = await PlaceAsync(buyer, address: "MAG");
        await AddAsync(buyer, (skus[1], 2));
        var overLimit = await QueryAsync<GetCheckout, CheckoutView>(new GetCheckout(buyer, ["pl"]));

        Assert.Equal(["BUD"], seen.Addresses.Select(a => a.Code));
        Assert.Equal("order.cart-changed", Code(changed));
        Assert.Equal("order.address-not-allowed", Code(wrongAddress));
        Assert.Equal((false, "cannot-order"), (overLimit.CanPlace, Assert.Single(overLimit.Issues)));
        Assert.Equal("order.cannot-order", Code(await PlaceAsync(buyer)));
    }

    [Fact]
    public async Task The_erp_accepts_rejects_and_cancels_by_number_and_decides_on_the_buyers_requests_to_cancel()
    {
        var (buyer, skus) = await ShopAsync();
        await AddAsync(buyer, (skus[0], 1));
        var first = Ok(await PlaceAsync(buyer));
        await AddAsync(buyer, (skus[0], 1));
        var second = Ok(await PlaceAsync(buyer));
        await ListedAsync(buyer, second.OrderId);

        Assert.Equal("ok", Code(await SendAsync<AcceptOrder, Unit>(new AcceptOrder(first.Number, "ZAM/77"))));
        Assert.Equal("ok", Code(await SendAsync<AcceptOrder, Unit>(new AcceptOrder(first.Number, "ZAM/77"))));
        Assert.Equal("order.already-accepted", Code(await SendAsync<RejectOrder, Unit>(new RejectOrder(first.Number, "Brak"))));
        Assert.Equal("ok", Code(await SendAsync<RequestOrderCancellation, Unit>(new RequestOrderCancellation(first.OrderId, buyer.UserId))));
        Assert.Equal("ok", Code(await SendAsync<RequestOrderCancellation, Unit>(new RequestOrderCancellation(second.OrderId, buyer.UserId))));
        Assert.Equal("ok", Code(await SendAsync<CancelOrder, Unit>(new CancelOrder(second.Number, null))));
        Assert.Equal("order.cancelled", Code(await SendAsync<AcceptOrder, Unit>(new AcceptOrder(second.Number, "ZAM/78"))));
        Assert.Equal("order.not-found", Code(await SendAsync<AcceptOrder, Unit>(new AcceptOrder("WWW/1900/000001", "ZAM/1"))));
        Assert.Equal("ok", Code(await SendAsync<RefuseOrderCancellation, Unit>(new RefuseOrderCancellation(first.Number, "Towar wysłany"))));
        Assert.Equal("order.no-cancellation-request", Code(await SendAsync<RefuseOrderCancellation, Unit>(new RefuseOrderCancellation(first.Number, null))));
        Assert.Equal("ok", Code(await SendAsync<CancelOrder, Unit>(new CancelOrder(first.Number, "Klient zrezygnował"))));

        var kept = await ListedAsync(buyer, first.OrderId, o => o.Status == OrderStatuses.Cancelled);
        var firstView = Ok(await QueryAsync<GetOrder, Result<OrderView, Error>>(new GetOrder(first.OrderId)));
        var secondView = Ok(await QueryAsync<GetOrder, Result<OrderView, Error>>(new GetOrder(second.OrderId)));
        var feed = await QueryAsync<GetOrderFeed, OrderFeedPage>(new GetOrderFeed(0, GetOrderFeed.MaxLimit));

        Assert.Equal("ZAM/77", kept.ErpNumber);
        Assert.Equal((OrderStatuses.Cancelled, false, "Klient zrezygnował"), (firstView.Status, firstView.CancelledOnRequest, firstView.Reason));
        Assert.Equal(
            [OrderSteps.Placed, OrderSteps.Accepted, OrderSteps.CancellationRequested, OrderSteps.CancellationRefused, OrderSteps.Cancelled],
            firstView.History.Select(h => h.Step));
        Assert.Equal((OrderStatuses.Cancelled, true, (DateTimeOffset?)null), (secondView.Status, secondView.CancelledOnRequest, secondView.CancellationRequestedAt));
        Assert.Equal([OrderFeedTypes.Placed, OrderFeedTypes.CancellationRequested], feed.Entries.Where(e => e.Order.Id == second.OrderId).Select(e => e.Type));
        Assert.Equal(
            [("accepted", true), ("requested", true), ("requested", false), ("cancelled-on-request", false), ("refused", true), ("cancelled", true)],
            await OrderEventsAsync(first.OrderId, second.OrderId, 6));
    }

    /// <summary>The published events of two orders: (what happened, whether to the first).</summary>
    private async Task<IReadOnlyList<(string, bool)>> OrderEventsAsync(Guid first, Guid second, int count)
    {
        static (Guid Order, string What) Of(IIntegrationEvent e) =>
            e switch
            {
                OrderAccepted a => (a.OrderId, "accepted"),
                OrderRejected r => (r.OrderId, "rejected"),
                OrderCancellationRequested r => (r.OrderId, "requested"),
                OrderCancellationRefused r => (r.OrderId, "refused"),
                OrderCancelled c => (c.OrderId, c.OnRequest ? "cancelled-on-request" : "cancelled"),
                OrderDispatched d => (d.OrderId, "dispatched"),
                OrderInvoiced i => (i.OrderId, "invoiced"),
                _ => (Guid.Empty, "other"),
            };

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (host.Events.OrderEvents.Select(Of).Count(e => e.Order == first || e.Order == second) < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "The order events were not published.");
            await Task.Delay(50, Token);
        }

        return [.. host.Events.OrderEvents.Select(Of).Where(e => e.Order == first || e.Order == second).Select(e => (e.What, e.Order == first))];
    }

    [Fact]
    public async Task Documents_naming_an_order_move_its_status()
    {
        var (buyer, skus) = await ShopAsync();
        await AddAsync(buyer, (skus[0], 1));
        var first = Ok(await PlaceAsync(buyer));
        await AddAsync(buyer, (skus[1], 1));
        var second = Ok(await PlaceAsync(buyer));
        await ListedAsync(buyer, second.OrderId);
        Ok(await SendAsync<AcceptOrder, Unit>(new AcceptOrder(second.Number, $"ZAM/{second.Number}")));
        await ListedAsync(buyer, second.OrderId, o => o.ErpNumber is not null);

        var note = Unique("WZ");
        var invoice = Unique("FV");
        await PutAsync("/integration/v1/documents", new ImportDocuments([
            new DocumentData(note, 1, buyer.CustomerCode, "delivery-note", $"WZ/{note}", Today, OrderNumbers: [first.Number, $"ZAM/{second.Number}", "ZAM/SPOZA"]),
            new DocumentData(invoice, 1, buyer.CustomerCode, "invoice", $"FV/{invoice}", Today, Currency: "PLN", Net: 10m, Gross: 12.3m, OrderNumber: first.Number),
            new DocumentData(Unique("WZ"), 1, Unique("K"), "delivery-note", "WZ/inna-firma", Today, OrderNumber: second.Number),
        ]));

        var completed = await ListedAsync(buyer, first.OrderId, o => o.Status == OrderStatuses.Completed);
        var inProgress = await ListedAsync(buyer, second.OrderId, o => o.Status == OrderStatuses.InProgress);
        var order = Ok(await QueryAsync<GetOrder, Result<OrderView, Error>>(new GetOrder(first.OrderId)));

        Assert.Equal((OrderStatuses.Completed, OrderStatuses.InProgress), (completed.Status, inProgress.Status));
        Assert.Equal(new[] { note, invoice }.Order(StringComparer.Ordinal), order.Documents.Select(d => d.DocumentId).Order(StringComparer.Ordinal));
        Assert.Equal([OrderSteps.Placed, OrderSteps.Accepted, OrderSteps.InProgress, OrderSteps.Completed], order.History.Select(h => h.Step));
    }

    [Fact]
    public async Task The_order_list_filters_by_buyer_status_and_number()
    {
        var (buyer, skus) = await ShopAsync();
        var colleague = buyer with { UserId = Guid.NewGuid() };
        await AddAsync(buyer, (skus[0], 1));
        var own = Ok(await PlaceAsync(buyer));
        await AddAsync(colleague, (skus[0], 1));
        var theirs = Ok(await PlaceAsync(colleague));
        await ListedAsync(buyer, theirs.OrderId);
        await ListedAsync(buyer, own.OrderId);

        var mine = Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(buyer.CustomerCode, buyer.UserId)));
        var byNumber = Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(buyer.CustomerCode, Number: theirs.Number)));
        var byReference = Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(buyer.CustomerCode, Number: "PO-1")));
        var accepted = Ok(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(buyer.CustomerCode, Status: OrderStatuses.Accepted)));

        Assert.Equal([own.OrderId], mine.Items.Select(o => o.Id));
        Assert.Equal([theirs.OrderId], byNumber.Items.Select(o => o.Id));
        Assert.Equal(2, byReference.TotalCount);
        Assert.Empty(accepted.Items);
        Assert.Equal("status.unknown", Code(await QueryAsync<GetOrders, Result<OrderPage, Error>>(new GetOrders(Status: "lost"))));
    }

    [Fact]
    public async Task A_placed_order_takes_its_items_out_of_the_cart_through_its_event()
    {
        var (buyer, skus) = await ShopAsync();
        var owner = new CartOwner(buyer.UserId, buyer.CustomerCode);
        await AddAsync(buyer, (skus[0], 2));
        Ok(await PlaceAsync(buyer, takeOut: false));
        await AddAsync(buyer, (skus[1], 1));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]))).Lines.Any(l => l.Sku == skus[0]))
        {
            Assert.True(DateTime.UtcNow < deadline, "The cart was not emptied.");
            await Task.Delay(50, Token);
        }

        Assert.Equal([(skus[1], 1m)], (await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]))).Lines.Select(l => (l.Sku, l.Quantity)));
    }
}
