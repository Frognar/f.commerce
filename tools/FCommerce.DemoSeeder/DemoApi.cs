using System.Net;
using System.Net.Http.Json;
using FCommerce.Cart.Contracts;
using FCommerce.Catalog.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>
/// The whole demo through the public API, as a customer's system would do it, narrated step by step: the key's user
/// browses the catalog, fills the cart, places an order (twice, to show that it is placed once), and the seeder plays
/// the ERP while the order moves to accepted, in progress and completed. Its output is the demo script.
/// </summary>
internal static class DemoApi
{
    private const int Products = 2;

    public static async Task<int> RunAsync(HttpClient erp, string baseUrl, string customerKey)
    {
        using var api = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(1) };
        api.DefaultRequestHeaders.Add("X-Api-Key", customerKey);
        api.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl");

        Step("1. Who is calling", "GET /api/v1/me");
        var me = await GetAsync<Me>(api, "/api/v1/me");
        Console.WriteLine($"   {me.Name} <{me.Email}> acts for {me.CustomerCode} {me.CustomerName}; may order: {me.CanOrder}, sees orders of: {me.OrderScope}");
        if (!me.CanOrder)
        {
            return Fail("This key's user may not order. Issue the key for a buyer or an administrator.");
        }

        Step("2. Browse the catalog (prices and stock of this customer)", "GET /api/v1/catalog/products");
        var catalog = await GetAsync<ProductList>(api, "/api/v1/catalog/products?pageSize=50");
        var offered = catalog.Items.Where(p => p.Price.Status == PriceStatuses.Priced && !p.Discontinued).Take(Products).ToList();
        if (offered.Count == 0)
        {
            return Fail("No product with a price for this customer. Seed the demo data first and use a customer with a price list.");
        }

        foreach (var product in offered)
        {
            Console.WriteLine($"   {product.Sku} {product.Name}: {product.Price.Offer!.Unit.Net:0.00} net / {product.Price.Offer.Unit.Gross:0.00} gross {product.Price.Offer.Currency} per {product.Unit}; stock {product.Stock?.Status ?? "hidden"}");
        }

        Step("3. Fill the cart", "POST /api/v1/cart/items");
        await DeleteAsync(api, "/api/v1/cart");
        var added = await PostAsync<CartResponse>(api, "/api/v1/cart/items", new { items = offered.Select((p, i) => new { sku = p.Sku, quantity = (decimal)(i + 1) * 3 }) });
        Console.WriteLine($"   {added.Cart.Lines.Count} products in the cart, can order: {added.Cart.CanOrder}, issues: [{string.Join(", ", added.Cart.Issues)}]");

        Step("4. Check out (prices are frozen when the order is placed)", "GET /api/v1/checkout");
        var checkout = await GetAsync<CheckoutView>(api, "/api/v1/checkout");
        if (!checkout.CanPlace || checkout.Total is null)
        {
            return Fail($"The cart cannot be ordered: {string.Join(", ", checkout.Issues)}.");
        }

        Console.WriteLine($"   total {checkout.Total.Net:0.00} net, {checkout.Total.Gross:0.00} gross {checkout.Total.Currency}; delivery to {checkout.SuggestedAddressCode ?? "(ERP decides)"}");

        Step("5. Place the order; the Idempotency-Key makes a repeated request return the same order", "POST /api/v1/orders");
        var key = Guid.CreateVersion7();
        var order = await PlaceAsync(api, key, checkout);
        var repeated = await PlaceAsync(api, key, checkout);
        Console.WriteLine($"   placed {order.Number} (status {order.Status}); sent again: {(repeated.Id == order.Id ? "the same order" : "ANOTHER ORDER?!")}");

        await DemoOrders.WaitForFeedAsync(erp, order.Number);
        var steps = new[] { ("accept", OrderStatuses.Accepted, "the ERP takes the order over under its own number"), ("deliver", OrderStatuses.InProgress, "the ERP issues a delivery note"), ("invoice", OrderStatuses.Completed, "the ERP issues the invoice") };
        var number = 6;
        foreach (var (erpStep, status, what) in steps)
        {
            Step($"{number++}. Play the ERP: {what}", $"dotnet run --project tools/FCommerce.DemoSeeder -- {baseUrl} <integration key> orders {erpStep}");
            await DemoOrders.RunAsync(erp, erpStep);
            order = await WaitForStatusAsync(api, order.Id, status);
            Console.WriteLine($"   GET /api/v1/orders/{order.Id} → status {order.Status}{(order.ErpNumber is null ? string.Empty : $", ERP number {order.ErpNumber}")}");
        }

        Step($"{number++}. Documents of the order", "GET /api/v1/documents/{id}/file");
        foreach (var document in order.Documents)
        {
            using var response = await api.GetAsync($"/api/v1/documents/{Uri.EscapeDataString(document.DocumentId)}/file");
            Console.WriteLine($"   {document.Type} {document.Number}: {(response.IsSuccessStatusCode ? $"{(await response.Content.ReadAsByteArrayAsync()).Length} bytes of PDF" : $"{(int)response.StatusCode} (the key's user may not see documents)")}");
        }

        Console.WriteLine($"""

            Done. The same order is in the storefront ({baseUrl}/pl/company/orders), and the sales rep sees it in the back-office
            ({baseUrl}/pl/admin/orders). The API is described at {baseUrl}/openapi/public-v1.json.
            """);
        return 0;
    }

    private static void Step(string title, string request) => Console.WriteLine($"\n{title}\n   > {request}");

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static async Task<OrderView> PlaceAsync(HttpClient api, Guid key, CheckoutView checkout)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                cartVersion = checkout.CartVersion,
                expectedGross = checkout.Total!.Gross,
                customerReference = $"DEMO-{DateTime.Now:MMdd-HHmm}",
                notes = "Placed through the public API by the demo script.",
                addressCode = checkout.SuggestedAddressCode,
            }),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        using var response = await api.SendAsync(request);
        return await ReadAsync<OrderView>(response, [HttpStatusCode.Created, HttpStatusCode.OK]);
    }

    /// <summary>Statuses that come from ERP documents reach the order list a moment later: ask again, gently (the API limits requests per key).</summary>
    private static async Task<OrderView> WaitForStatusAsync(HttpClient api, Guid id, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var order = await GetAsync<OrderView>(api, $"/api/v1/orders/{id}");
            if (order.Status == status || DateTime.UtcNow > deadline)
            {
                return order;
            }

            await Task.Delay(1000);
        }
    }

    private static async Task<T> GetAsync<T>(HttpClient api, string path)
    {
        using var response = await api.GetAsync(path);
        return await ReadAsync<T>(response, [HttpStatusCode.OK]);
    }

    private static async Task<T> PostAsync<T>(HttpClient api, string path, object body)
    {
        using var response = await api.PostAsJsonAsync(path, body);
        return await ReadAsync<T>(response, [HttpStatusCode.OK]);
    }

    private static async Task DeleteAsync(HttpClient api, string path)
    {
        using var response = await api.DeleteAsync(path);
        await ReadAsync<CartResponse>(response, [HttpStatusCode.OK]);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode[] expected)
    {
        if (!expected.Contains(response.StatusCode))
        {
            Console.Error.WriteLine($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri?.PathAndQuery}: {await response.Content.ReadAsStringAsync()}");
            Environment.Exit(1);
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // What the public API answers, as far as the demo reads it.
    private sealed record Me(string Name, string Email, string CustomerCode, string CustomerName, bool CanOrder, string OrderScope);

    private sealed record ProductList(IReadOnlyList<ListedProduct> Items);

    private sealed record ListedProduct(string Sku, string Name, string Unit, bool Discontinued, ProductPrice Price, StockSummary? Stock);

    private sealed record CartResponse(CartView Cart);
}
