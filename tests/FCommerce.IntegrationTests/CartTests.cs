using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>The cart module through its contracts, with carts stored as event streams in PostgreSQL (Marten).</summary>
public sealed class CartTests(HostFixture host)
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

    private async Task<TResult> SendAsync<TCommand, TResult>(TCommand command)
        where TCommand : ICommand<Result<TResult, Error>>
        where TResult : notnull
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, Result<TResult, Error>>>();
        return (await handler.HandleAsync(command, Token)).Match(result => result, error => throw new InvalidOperationException($"{error.Code}: {error.Message}"));
    }

    private Task<CartChange> ChangeAsync<TCommand>(TCommand command)
        where TCommand : ICommand<Result<CartChange, Error>> =>
        SendAsync<TCommand, CartChange>(command);

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
        where TResult : notnull
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, Token);
    }

    /// <summary>Products (one discontinued), a price list priced for all but the last, a warehouse with 5 of each, a customer.</summary>
    private async Task<(CartOwner Owner, string[] Skus)> ShopAsync(bool blocked = false)
    {
        string[] skus = [Unique("A"), Unique("B"), Unique("C"), Unique("D")];
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([.. skus.Select((sku, i) => new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Produkt {i}" }, null, null, "szt", null, [], [], Discontinued: i == 2))]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([
            new PriceData(skus[0], 1, 10m, 23m, [new PriceBreakData(10, 9m)]), new PriceData(skus[1], 1, 2.5m, 8m), new PriceData(skus[2], 1, 1m, 23m)]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([.. skus.Select(sku => new StockData(sku, 1, 5m, Today.AddDays(6), 50m))]));
        var customer = Unique("K");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            customer, 1, $"Firma {customer}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null, [], null, PriceListCode: list, Blocked: blocked)]));
        return (new CartOwner(Guid.NewGuid(), customer), skus);
    }

    [Fact]
    public async Task Items_are_added_merged_changed_and_removed_and_unknown_skus_are_reported()
    {
        var (owner, skus) = await ShopAsync();
        var unknown = Unique("NONE");

        var added = await ChangeAsync(new AddToCart(owner, [new(skus[0], 2), new(unknown, 1), new(skus[1], 0)], CartSources.Product));
        var more = await ChangeAsync(new AddToCart(owner, [new(skus[0], 3), new(skus[1], 1)], CartSources.Catalog));
        await ChangeAsync(new ChangeCartQuantity(owner, skus[1], 4));
        var removed = await ChangeAsync(new RemoveFromCart(owner, skus[1]));
        var cart = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));

        Assert.Equal((1, 1), (added.ItemCount, added.Added));
        Assert.Equal([(unknown, "product.unknown"), ($"{skus[1]} 0", "quantity.not-positive")], added.Rejected.Select(r => (r.Text, r.Code)).OrderBy(r => r.Code));
        Assert.Equal(2, more.ItemCount);
        Assert.Equal(1, removed.ItemCount);
        Assert.Equal([(skus[0], 5m)], cart.Lines.Select(l => (l.Sku, l.Quantity)));
        Assert.Equal(1, await QueryAsync<GetCartSize, int>(new GetCartSize(owner)));
    }

    [Fact]
    public async Task The_cart_shows_current_prices_totals_and_what_prevents_ordering()
    {
        var (owner, skus) = await ShopAsync();
        await ChangeAsync(new AddToCart(owner, [new(skus[0], 10), new(skus[1], 2), new(skus[2], 3)], CartSources.Product));

        var orderable = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));
        await ChangeAsync(new AddToCart(owner, [new(skus[2], 3), new(skus[3], 1)], CartSources.Product));
        var blocked = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));

        Assert.True(orderable.CanOrder);
        Assert.Equal(StockDisplays.Levels, orderable.StockDisplay);
        Assert.Equal([("Produkt 0", 9m, 90m), ("Produkt 1", 2.5m, 5m), ("Produkt 2", 1m, 3m)], orderable.Lines.Select(l => (l.Name!, l.UnitNet!.Value, l.NetAmount!.Value)));
        var backorder = Assert.Single(orderable.Lines[0].Issues);
        Assert.Equal(("backorder", false, null, Today.AddDays(6)), (backorder.Code, backorder.Blocks, backorder.Available, backorder.NextDelivery));
        var total = Assert.Single(orderable.Totals);
        Assert.Equal([new CartVatView(8m, 5m, 0.40m), new CartVatView(23m, 93m, 21.39m)], total.Rates);
        Assert.Equal((98m, 21.79m, 119.79m), (total.Net, total.Vat, total.Gross));

        Assert.False(blocked.CanOrder);
        Assert.Equal(["discontinued-shortage"], blocked.Lines[2].Issues.Select(i => i.Code));
        Assert.True(blocked.Lines[2].Blocking);
        Assert.Equal((PriceStatuses.OnRequest, "no-price"), (blocked.Lines[3].PriceStatus, blocked.Lines[3].Issues[0].Code));
    }

    [Fact]
    public async Task A_blocked_customer_cannot_order_and_an_empty_cart_neither()
    {
        var (owner, skus) = await ShopAsync(blocked: true);

        var empty = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));
        await ChangeAsync(new AddToCart(owner, [new(skus[1], 1)], CartSources.Product));
        var cart = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));

        Assert.Equal(["empty", "customer-blocked"], empty.Issues);
        Assert.Equal(["customer-blocked"], cart.Issues);
        Assert.False(cart.CanOrder);
    }

    [Fact]
    public async Task Pasted_lines_replacing_and_clearing()
    {
        var (owner, skus) = await ShopAsync();

        var pasted = await ChangeAsync(new AddPastedLines(owner, $"{skus[0]}\t3\n{skus[1]};1,5\nNIEZNANY 1\n{skus[0]} dużo"));
        var replaced = await ChangeAsync(new ReplaceCart(owner, [new(skus[3], 2)], CartSources.ShoppingList));
        var afterReplace = await QueryAsync<GetCart, CartView>(new GetCart(owner, ["pl"]));
        var cleared = await ChangeAsync(new ClearCart(owner));

        Assert.Equal((2, 2), (pasted.ItemCount, pasted.Added));
        Assert.Equal([(4, "paste.quantity"), ((int?)null, "product.unknown")], pasted.Rejected.Select(r => (r.Line, r.Code)));
        Assert.Equal(1, replaced.ItemCount);
        Assert.Equal([(skus[3], 2m)], afterReplace.Lines.Select(l => (l.Sku, l.Quantity)));
        Assert.Equal(0, cleared.ItemCount);
    }

    [Fact]
    public async Task Each_user_has_a_cart_per_customer()
    {
        var (owner, skus) = await ShopAsync();
        var otherCustomer = owner with { CustomerCode = Unique("K") };
        var otherUser = owner with { UserId = Guid.NewGuid() };

        await ChangeAsync(new AddToCart(owner, [new(skus[0], 1)], CartSources.Product));

        Assert.Equal(1, await QueryAsync<GetCartSize, int>(new GetCartSize(owner)));
        Assert.Equal(0, await QueryAsync<GetCartSize, int>(new GetCartSize(otherCustomer)));
        Assert.Equal(0, await QueryAsync<GetCartSize, int>(new GetCartSize(otherUser)));
    }
}
