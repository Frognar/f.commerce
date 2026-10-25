using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>Checkout and the order history in the storefront, on the shared host (ordering on).</summary>
public sealed partial class OrderPagesTests(HostFixture host)
{
    private const string Password = "customer password 1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    private async Task PutAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A product priced 10 net, 5 in stock, and a company with two delivery addresses whose administrator, buyer and
    /// accountant accepted their invitations.
    /// </summary>
    private async Task<(string Sku, Browser Admin, Browser Buyer, Browser Accountant)> CompanyAsync(string? salesperson = null)
    {
        var sku = Unique("A");
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Wkręt {sku}" }, null, null, "szt", null, [], [])]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(sku, 1, 10m, 23m)]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([new StockData(sku, 1, 5m, null, null)]));

        var code = Unique("K");
        string[] emails = [UniqueEmail("admin"), UniqueEmail("buyer"), UniqueEmail("accounting")];
        var address = new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, address,
            [new DeliveryAddressData("MAG", "Magazyn", address, IsDefault: true), new DeliveryAddressData("BUD", "Budowa", address with { Street = "ul. Krótka 2" })],
            [
                new ContactData("C1", "Anna Admin", emails[0], null, PortalAccess: true, "administrator"),
                new ContactData("C2", "Piotr Kupiec", emails[1], null, PortalAccess: true, "buyer"),
                new ContactData("C3", "Ewa Księgowa", emails[2], null, PortalAccess: true, "accounting"),
            ],
            salesperson is null ? null : new SalespersonData("Anna Handlowiec", salesperson, null), PriceListCode: list)]));
        var browsers = new List<Browser>();
        foreach (var email in emails)
        {
            var browser = host.NewBrowser();
            await host.AcceptInvitationAsync(browser, email, Password);
            browsers.Add(browser);
        }

        return (sku, browsers[0], browsers[1], browsers[2]);
    }

    private static Task<(HttpStatusCode Status, string Html, string? Location)> QuickAddAsync(Browser browser, string sku, string quantity) =>
        browser.SubmitAsync("/pl/cart", new Dictionary<string, string> { ["sku"] = sku, ["quantity"] = quantity }, "/pl/cart?handler=QuickAdd");

    /// <summary>The checkout form as shown: its hidden fields (order id, cart version and total) plus the buyer's choices.</summary>
    private static async Task<Dictionary<string, string>> CheckoutFormAsync(Browser browser, string address = "BUD")
    {
        var html = (await browser.GetAsync("/pl/cart/checkout")).Html;
        string Hidden(string name) => Regex.Match(html, $"name=\"Form\\.{name}\" value=\"([^\"]*)\"").Groups[1].Value;
        return new Dictionary<string, string>
        {
            ["Form.OrderId"] = Hidden("OrderId"),
            ["Form.CartVersion"] = Hidden("CartVersion"),
            ["Form.ExpectedGross"] = Hidden("ExpectedGross"),
            ["Form.AddressCode"] = address,
            ["Form.CustomerReference"] = "PO-7",
            ["Form.Notes"] = "Proszę o telefon",
        };
    }

    private static Task<(HttpStatusCode Status, string Html, string? Location)> PlaceAsync(Browser browser, Dictionary<string, string> form) =>
        browser.SubmitAsync("/pl/cart/checkout", form);

    private static string OrderNumber(string html) => OrderNumberPattern().Match(html).Value;

    [Fact]
    public async Task A_buyer_places_an_order_from_the_cart_once_and_finds_it_in_the_history()
    {
        var (sku, admin, buyer, accountant) = await CompanyAsync();
        using var _ = admin;
        using var __ = buyer;
        using var ___ = accountant;
        await QuickAddAsync(buyer, sku, "3");

        var cart = (await buyer.GetAsync("/pl/cart")).Html;
        var checkout = (await buyer.GetAsync("/pl/cart/checkout")).Html;
        var form = await CheckoutFormAsync(buyer);
        var (status, _, location) = await PlaceAsync(buyer, form);
        var page = (await buyer.GetAsync(location!)).Html;
        var again = await PlaceAsync(buyer, form);
        var emptied = (await buyer.GetAsync("/pl/cart")).Html;

        Assert.Contains("Przejdź do zamówienia", cart, StringComparison.Ordinal);
        Assert.Contains($"Wkręt {sku}", checkout, StringComparison.Ordinal);
        Assert.Contains("value=\"MAG\" checked=\"checked\"", checkout, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.StartsWith("/pl/company/orders/", location, StringComparison.Ordinal);
        var number = OrderNumber(page);
        Assert.Contains($"Dziękujemy! Zamówienie {number} zostało złożone.", page, StringComparison.Ordinal);
        Assert.Contains("PO-7", page, StringComparison.Ordinal);
        Assert.Contains("ul. Krótka 2", page, StringComparison.Ordinal);
        Assert.Contains("Razem brutto</dt><dd class=\"gross\">36,90 PLN", page, StringComparison.Ordinal);
        Assert.Equal(location, again.Location);
        Assert.Contains("Koszyk jest pusty.", emptied, StringComparison.Ordinal);
        var history = await EventuallyAsync(buyer, "/pl/company/orders", html => html.Contains(number, StringComparison.Ordinal));
        Assert.Contains("Złożone", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cart_changed_after_the_checkout_was_shown_is_not_ordered_unseen()
    {
        var (sku, admin, buyer, accountant) = await CompanyAsync();
        using var _ = admin;
        using var __ = buyer;
        using var ___ = accountant;
        await QuickAddAsync(buyer, sku, "1");
        var form = await CheckoutFormAsync(buyer);
        await QuickAddAsync(buyer, sku, "1");

        var (status, html, _) = await PlaceAsync(buyer, form);
        var placed = await PlaceAsync(buyer, await CheckoutFormAsync(buyer, address: "NONE"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Koszyk lub ceny zmieniły się od chwili wyświetlenia.", html, StringComparison.Ordinal);
        Assert.Contains("Nie możesz zamawiać na ten adres dostawy.", placed.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buyers_see_their_own_orders_the_company_scope_sees_all_and_the_buyer_cancels_or_reorders()
    {
        var (sku, admin, buyer, accountant) = await CompanyAsync();
        using var _ = admin;
        using var __ = buyer;
        using var ___ = accountant;
        await QuickAddAsync(admin, sku, "2");
        var adminOrder = (await PlaceAsync(admin, await CheckoutFormAsync(admin))).Location!;
        await QuickAddAsync(buyer, sku, "1");
        var buyerOrder = (await PlaceAsync(buyer, await CheckoutFormAsync(buyer))).Location!;
        var buyerOrderPath = buyerOrder.Split('?')[0];
        var adminOrderPath = adminOrder.Split('?')[0];

        var foreign = await buyer.GetAsync(adminOrderPath);
        var seenByAdmin = (await admin.GetAsync(buyerOrderPath)).Html;
        var seenByAccountant = (await accountant.GetAsync(buyerOrderPath)).Html;
        var accountantCheckout = await accountant.GetAsync("/pl/cart/checkout");
        var cancelled = await buyer.SubmitAsync(buyerOrderPath, new Dictionary<string, string>(), $"{buyerOrderPath}?handler=Cancel");
        var reordered = await buyer.SubmitAsync(buyerOrderPath, new Dictionary<string, string> { ["replace"] = "false" }, $"{buyerOrderPath}?handler=ToCart");
        var cart = (await buyer.GetAsync("/pl/cart")).Html;

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Contains("Anuluj zamówienie", seenByAdmin, StringComparison.Ordinal);
        Assert.Contains("Piotr Kupiec", seenByAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("Anuluj zamówienie", seenByAccountant, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, ForbiddenOrDenied(accountantCheckout));
        Assert.Contains("Zamówienie zostało anulowane.", cancelled.Html, StringComparison.Ordinal);
        Assert.Contains("Anulowane", cancelled.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Anuluj zamówienie<", cancelled.Html, StringComparison.Ordinal);
        Assert.Contains("Dodano do koszyka", reordered.Html, StringComparison.Ordinal);
        Assert.Contains($"Wkręt {sku}", cart, StringComparison.Ordinal);
    }

    /// <summary>A denied page answers 403 or redirects to the access-denied page.</summary>
    private static HttpStatusCode ForbiddenOrDenied((HttpStatusCode Status, string Html, string? Location) response) =>
        response.Status == HttpStatusCode.Redirect && response.Location?.Contains("access-denied", StringComparison.Ordinal) == true
            ? HttpStatusCode.Forbidden
            : response.Status;

    [Fact]
    public async Task The_order_history_lists_own_orders_or_all_of_the_company()
    {
        var (sku, admin, buyer, accountant) = await CompanyAsync();
        using var _ = admin;
        using var __ = buyer;
        using var ___ = accountant;
        await QuickAddAsync(admin, sku, "2");
        var adminNumber = OrderNumber((await admin.GetAsync((await PlaceAsync(admin, await CheckoutFormAsync(admin))).Location!)).Html);
        await QuickAddAsync(buyer, sku, "1");
        var buyerNumber = OrderNumber((await buyer.GetAsync((await PlaceAsync(buyer, await CheckoutFormAsync(buyer))).Location!)).Html);

        var all = await EventuallyAsync(accountant, "/pl/company/orders", html => html.Contains(adminNumber, StringComparison.Ordinal) && html.Contains(buyerNumber, StringComparison.Ordinal));
        var own = await EventuallyAsync(buyer, "/pl/company/orders", html => html.Contains(buyerNumber, StringComparison.Ordinal));
        var mine = await EventuallyAsync(admin, "/pl/company/orders?mine=true", html => html.Contains(adminNumber, StringComparison.Ordinal));

        Assert.Contains("Tylko moje zamówienia", all, StringComparison.Ordinal);
        Assert.DoesNotContain(adminNumber, own, StringComparison.Ordinal);
        Assert.DoesNotContain("Tylko moje zamówienia", own, StringComparison.Ordinal);
        Assert.Contains(adminNumber, mine, StringComparison.Ordinal);
        Assert.DoesNotContain(buyerNumber, mine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_buyer_and_the_salesperson_get_an_e_mail_with_a_link_and_the_seller_sees_the_order()
    {
        var salesperson = UniqueEmail("anna");
        var (sku, admin, buyer, accountant) = await CompanyAsync(salesperson);
        using var _ = admin;
        using var __ = buyer;
        using var ___ = accountant;
        await QuickAddAsync(buyer, sku, "2");
        var location = (await PlaceAsync(buyer, await CheckoutFormAsync(buyer))).Location!;
        var id = location.Split('?')[0].Split('/')[^1];
        var number = OrderNumber((await buyer.GetAsync(location)).Html);

        var toSalesperson = await host.Mail.WaitForAsync(salesperson, m => m.Subject.Contains(number, StringComparison.Ordinal));
        using var operatorBrowser = await host.AdminAsync();
        var list = await EventuallyAsync(operatorBrowser, $"/pl/admin/orders?number={Uri.EscapeDataString(number)}", html => html.Contains(number, StringComparison.Ordinal));
        var details = (await operatorBrowser.GetAsync($"/pl/admin/orders/{id}")).Html;
        var buyerInBackOffice = await buyer.GetAsync("/pl/admin/orders");

        Assert.Contains($"/pl/admin/orders/{id}", toSalesperson.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sku, toSalesperson.Text, StringComparison.Ordinal);
        Assert.Contains("Piotr Kupiec", list, StringComparison.Ordinal);
        Assert.Contains($"Wkręt {sku}", details, StringComparison.Ordinal);
        Assert.Contains("Zamówienie przyjmuje, odrzuca i anuluje ERP", details, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, buyerInBackOffice.Status);
    }

    private static async Task<string> EventuallyAsync(Browser browser, string url, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var html = (await browser.GetAsync(url)).Html;
            if (condition(html))
            {
                return html;
            }

            Assert.True(DateTime.UtcNow < deadline, "The order list did not catch up.");
            await Task.Delay(100, Token);
        }
    }

    [GeneratedRegex(@"WWW/\d{4}/\d{6}")]
    private static partial Regex OrderNumberPattern();
}
