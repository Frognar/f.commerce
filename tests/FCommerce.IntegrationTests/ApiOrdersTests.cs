using System.Net;
using System.Net.Http.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Host.Api;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>The public API's order path: cart, checkout, placing an order once, the history and cancelling.</summary>
public sealed class ApiOrdersTests(HostFixture host)
{
    private readonly ApiSupport support = new(host);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StringContent NoBody => new(string.Empty);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Token))!;
    }

    private static async Task<ApiCartChange> AddAsync(HttpClient client, string sku, decimal quantity)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/cart/items", new ApiCartItems([new CartItemData(sku, quantity)]), Token);
        return await ReadAsync<ApiCartChange>(response);
    }

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient client, Guid key, CheckoutView checkout, string? address = "BUD", string reference = "PO-7")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new ApiPlaceOrder(checkout.CartVersion, checkout.Total!.Gross, reference, "Please call", null, address)),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request, Token);
    }

    private static async Task<CheckoutView> CheckoutAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CheckoutView>("/api/v1/checkout", Token))!;

    [Fact]
    public async Task A_cart_is_filled_ordered_once_and_found_in_the_history()
    {
        var company = await support.CompanyAsync();
        var (buyerKey, _) = await support.KeyAsync(company, company.BuyerId);
        var (adminKey, _) = await support.KeyAsync(company, company.AdminId);
        using var buyer = support.ApiClient(buyerKey);
        using var admin = support.ApiClient(adminKey);

        var added = await AddAsync(buyer, company.Sku, 3);
        var checkout = await CheckoutAsync(buyer);
        var orderKey = Guid.CreateVersion7();
        using var created = await PlaceAsync(buyer, orderKey, checkout);
        var order = await ReadAsync<OrderView>(created, HttpStatusCode.Created);
        using var again = await PlaceAsync(buyer, orderKey, checkout);
        var repeated = await ReadAsync<OrderView>(again);
        var cart = await buyer.GetFromJsonAsync<CartView>("/api/v1/cart", Token);
        var asAdmin = await admin.GetFromJsonAsync<OrderView>($"/api/v1/orders/{order.Id}", Token);

        Assert.Equal((1, true), (added.Cart.Lines.Count, added.Cart.CanOrder));
        Assert.True(checkout.CanPlace);
        Assert.Equal(("MAG", 36.90m), (checkout.SuggestedAddressCode, checkout.Total!.Gross));
        Assert.Equal(["BUD", "MAG"], checkout.Addresses.Select(a => a.Code).Order());
        Assert.Equal($"/api/v1/orders/{order.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal((OrderStatuses.Placed, "PO-7", "Please call", company.Code, company.BuyerId), (order.Status, order.CustomerReference, order.Notes, order.CustomerCode, order.PlacedBy));
        Assert.Equal("ul. Krótka 2", order.DeliveryAddress!.Street);
        Assert.Equal(36.90m, order.Total.Gross);
        Assert.Equal(order.Number, repeated.Number);
        Assert.Empty(cart!.Lines);
        var item = await ApiSupport.EventuallyAsync(() => buyer.GetFromJsonAsync<OrderPage>("/api/v1/orders", Token), page => page!.Items.Count > 0);
        Assert.Equal(order.Number, Assert.Single(item!.Items).Number);
        Assert.Equal(order.Number, asAdmin!.Number);
        Assert.Equal(1, item.TotalCount);
    }

    [Fact]
    public async Task A_cart_changed_since_the_checkout_is_not_ordered_unseen()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        using var client = support.ApiClient(key);
        await AddAsync(client, company.Sku, 1);
        var checkout = await CheckoutAsync(client);
        await AddAsync(client, company.Sku, 1);

        using var response = await PlaceAsync(client, Guid.CreateVersion7(), checkout);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("order.cart-changed", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_needs_an_idempotency_key_a_body_and_an_allowed_address()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        using var client = support.ApiClient(key);
        await AddAsync(client, company.Sku, 1);
        var checkout = await CheckoutAsync(client);

        using var withoutKey = await client.PostAsJsonAsync("/api/v1/orders", new ApiPlaceOrder(checkout.CartVersion, checkout.Total!.Gross, null, null, null, null), Token);
        using var emptyBody = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders");
        emptyBody.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var noBody = await client.SendAsync(emptyBody, Token);
        using var badAddress = await PlaceAsync(client, Guid.CreateVersion7(), checkout, address: "NOWHERE");

        Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
        Assert.Contains("api.idempotency-key", await withoutKey.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Contains("order.address-not-allowed", await badAddress.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cart_items_are_changed_removed_and_unknown_products_are_reported()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        using var client = support.ApiClient(key);

        using var mixed = await client.PostAsJsonAsync("/api/v1/cart/items", new ApiCartItems([new CartItemData(company.Sku, 2), new CartItemData("NO-SUCH-SKU", 1)]), Token);
        var change = await ReadAsync<ApiCartChange>(mixed);
        using var quantity = await client.PutAsJsonAsync($"/api/v1/cart/items/{company.Sku}", new ApiCartQuantity(4), Token);
        var changed = await ReadAsync<ApiCartChange>(quantity);
        using var removed = await client.DeleteAsync($"/api/v1/cart/items/{company.Sku}", Token);
        var empty = await ReadAsync<ApiCartChange>(removed);
        await AddAsync(client, company.Sku, 1);
        using var cleared = await client.DeleteAsync("/api/v1/cart", Token);

        Assert.Equal("product.unknown", Assert.Single(change.Change.Rejected).Code);
        Assert.Equal(2, Assert.Single(change.Cart.Lines).Quantity);
        Assert.Equal(4, Assert.Single(changed.Cart.Lines).Quantity);
        Assert.Empty(empty.Cart.Lines);
        Assert.Empty((await ReadAsync<ApiCartChange>(cleared)).Cart.Lines);
    }

    [Fact]
    public async Task The_buyer_asks_to_cancel_and_the_erp_decides()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        using var client = support.ApiClient(key);
        await AddAsync(client, company.Sku, 1);
        using var first = await PlaceAsync(client, Guid.CreateVersion7(), await CheckoutAsync(client));
        var order = await ReadAsync<OrderView>(first, HttpStatusCode.Created);
        await AddAsync(client, company.Sku, 1);
        using var second = await PlaceAsync(client, Guid.CreateVersion7(), await CheckoutAsync(client));
        var rejected = await ReadAsync<OrderView>(second, HttpStatusCode.Created);

        using var request = await client.PostAsync($"/api/v1/orders/{order.Id}/cancellation", NoBody, Token);
        using var again = await client.PostAsync($"/api/v1/orders/{order.Id}/cancellation", NoBody, Token);
        using (var erp = host.CreateIntegrationClient())
        {
            // The ERP finds the order by number in the read model, which catches up asynchronously.
            var status = await ApiSupport.EventuallyAsync(
                async () =>
                {
                    using var rejection = await erp.PutAsJsonAsync($"/integration/v1/orders/{Uri.EscapeDataString(rejected.Number)}/rejection", new OrderReasonData("Brak"), Token);
                    return rejection.StatusCode;
                },
                code => code != HttpStatusCode.NotFound);
            Assert.Equal(HttpStatusCode.NoContent, status);
        }

        using var tooLate = await client.PostAsync($"/api/v1/orders/{rejected.Id}/cancellation", NoBody, Token);

        var requested = await ReadAsync<OrderView>(request, HttpStatusCode.Accepted);
        Assert.Equal(OrderStatuses.Placed, requested.Status);
        Assert.NotNull(requested.CancellationRequestedAt);
        Assert.Equal(requested.CancellationRequestedAt, (await ReadAsync<OrderView>(again, HttpStatusCode.Accepted)).CancellationRequestedAt);
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Contains("order.rejected", await tooLate.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_of_other_companies_and_other_buyers_do_not_exist_for_a_key()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        var (adminKey, _) = await support.KeyAsync(company, company.AdminId);
        var (buyerKey, _) = await support.KeyAsync(company, company.BuyerId);
        var (otherKey, _) = await support.KeyAsync(other, other.AdminId);
        using var admin = support.ApiClient(adminKey);
        using var buyer = support.ApiClient(buyerKey);
        using var foreign = support.ApiClient(otherKey);
        await AddAsync(admin, company.Sku, 1);
        using var placed = await PlaceAsync(admin, Guid.CreateVersion7(), await CheckoutAsync(admin));
        var order = await ReadAsync<OrderView>(placed, HttpStatusCode.Created);

        using var asBuyer = await buyer.GetAsync($"/api/v1/orders/{order.Id}", Token);
        using var cancelAsBuyer = await buyer.PostAsync($"/api/v1/orders/{order.Id}/cancellation", NoBody, Token);
        using var asForeign = await foreign.GetAsync($"/api/v1/orders/{order.Id}", Token);
        using var cancelAsForeign = await foreign.PostAsync($"/api/v1/orders/{order.Id}/cancellation", NoBody, Token);
        using var unknown = await admin.GetAsync($"/api/v1/orders/{Guid.NewGuid()}", Token);
        var buyersList = await ApiSupport.EventuallyAsync(() => buyer.GetFromJsonAsync<OrderPage>("/api/v1/orders", Token), _ => true);

        Assert.Equal(HttpStatusCode.NotFound, asBuyer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancelAsBuyer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asForeign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancelAsForeign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(buyersList!.Items);
        using var stillThere = await admin.PostAsync($"/api/v1/orders/{order.Id}/cancellation", NoBody, Token);
        Assert.Equal(HttpStatusCode.Accepted, stillThere.StatusCode);
    }

    [Fact]
    public async Task A_key_of_a_user_who_may_not_order_has_no_cart_but_reads_the_orders_of_the_company()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.AccountantId);
        using var client = support.ApiClient(key);

        using var cart = await client.GetAsync("/api/v1/cart", Token);
        using var add = await client.PostAsJsonAsync("/api/v1/cart/items", new ApiCartItems([new CartItemData(company.Sku, 1)]), Token);
        using var checkout = await client.GetAsync("/api/v1/checkout", Token);
        using var place = await client.PostAsync("/api/v1/orders", null, Token);
        using var orders = await client.GetAsync("/api/v1/orders", Token);

        Assert.Equal(HttpStatusCode.Forbidden, cart.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, place.StatusCode); // no key or body to place, nothing to place with
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
    }

    [Fact]
    public async Task The_order_limit_of_the_keys_user_applies()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeMembership, Result<Unit, Error>>>()
                .HandleAsync(new ChangeMembership(company.AdminId, company.BuyerId, company.Code, new MembershipRightsView(true, "own", false, false, 5m, "PLN")), Token))
                .Match(_ => 0, e => throw new InvalidOperationException(e.Code));
        }

        using var client = support.ApiClient(key);
        var added = await AddAsync(client, company.Sku, 1);
        var me = await client.GetFromJsonAsync<ApiAccount>("/api/v1/me", Token);
        var checkout = await CheckoutAsync(client);
        using var placed = await PlaceAsync(client, Guid.CreateVersion7(), checkout with { Total = checkout.Total });

        Assert.Contains("over-limit", added.Cart.Issues);
        Assert.Equal(5m, me!.OrderLimit);
        Assert.False(checkout.CanPlace);
        Assert.Equal(HttpStatusCode.Conflict, placed.StatusCode);
    }
}
