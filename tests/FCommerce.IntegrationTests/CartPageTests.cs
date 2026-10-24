using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>The cart in the storefront, on the shared host (default settings: cart and quick order on, stock as levels).</summary>
public sealed partial class CartPageTests(HostFixture host)
{
    private const string Password = "customer password 1";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    private async Task PutAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Three products (the last discontinued) priced in a new price list, 5 of each in stock.</summary>
    private async Task<(string List, string[] Skus)> ProductsAsync()
    {
        string[] skus = [Unique("A"), Unique("B"), Unique("C")];
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([.. skus.Select((sku, i) => new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Wkręt {sku}" }, null, null, "szt", null, [], [], Discontinued: i == 2))]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([.. skus.Take(2).Select(sku => new PriceData(sku, 1, 10m, 23m))]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([.. skus.Select(sku => new StockData(sku, 1, 5m, Today.AddDays(4), 20m))]));
        return (list, skus);
    }

    /// <summary>A customer with the price list whose person (rights of the template) accepted the invitation.</summary>
    private async Task<(string Code, Browser Browser)> CustomerUserAsync(string? priceList, string template = "buyer", string? email = null)
    {
        var code = Unique("K");
        email ??= UniqueEmail(template);
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null,
            [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, template)], null, PriceListCode: priceList)]));
        var browser = host.NewBrowser();
        await host.AcceptInvitationAsync(browser, email, Password);
        return (code, browser);
    }

    /// <summary>Posts like htmx does: the antiforgery token from the page in a header.</summary>
    private static async Task<(HttpStatusCode Status, string Html)> HtmxPostAsync(Browser browser, string pageUrl, string action, Dictionary<string, string> fields)
    {
        var html = (await browser.GetAsync(pageUrl)).Html;
        var token = FormToken().Match(html) is { Success: true } form ? form.Groups[1].Value : HeaderToken().Match(html).Groups[1].Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))) };
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await browser.Client.SendAsync(request, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static Task<(HttpStatusCode Status, string Html)> AddAsync(Browser browser, string sku, string quantity = "1") =>
        HtmxPostAsync(browser, $"/pl/products/{sku}", "/pl/cart?handler=Add", new() { ["sku"] = sku, ["quantity"] = quantity, ["source"] = "product", ["returnUrl"] = $"/pl/products/{sku}" });

    private static Task<(HttpStatusCode Status, string Html)> CartPostAsync(Browser browser, string handler, Dictionary<string, string> fields) =>
        HtmxPostAsync(browser, "/pl/cart", $"/pl/cart?handler={handler}", fields);

    [Fact]
    public async Task A_buyer_adds_products_and_sees_prices_totals_and_the_count_in_the_header()
    {
        var (list, skus) = await ProductsAsync();
        var (_, buyer) = await CustomerUserAsync(list);
        using var _ = buyer;

        var product = (await buyer.GetAsync($"/pl/products/{skus[0]}")).Html;
        var added = await AddAsync(buyer, skus[0], "2,5");
        var again = await AddAsync(buyer, skus[1]);
        var cart = (await buyer.GetAsync("/pl/cart")).Html;

        Assert.Contains("class=\"add-to-cart\"", product, StringComparison.Ordinal);
        Assert.Contains("Dodano do koszyka.", added.Html, StringComparison.Ordinal);
        Assert.Matches("id=\"cart-link\"[^>]*hx-swap-oob=\"true\"", added.Html);
        Assert.Matches("class=\"cart-count\"[^>]*>2</span>", again.Html);
        Assert.Contains($"Wkręt {skus[0]}", cart, StringComparison.Ordinal);
        Assert.Contains("value=\"2,5\"", cart, StringComparison.Ordinal);
        Assert.Contains("25,00 PLN", cart, StringComparison.Ordinal);
        Assert.Contains("Razem netto</dt><dd>35,00 PLN", cart, StringComparison.Ordinal);
        Assert.Contains("VAT 23%</dt><dd>8,05 PLN", cart, StringComparison.Ordinal);
        Assert.Contains("Razem brutto</dt><dd class=\"gross\">43,05 PLN", cart, StringComparison.Ordinal);
        Assert.Contains("Koszyk jest gotowy do zamówienia.", cart, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lines_are_changed_removed_and_the_cart_emptied_with_htmx_fragments()
    {
        var (list, skus) = await ProductsAsync();
        var (_, buyer) = await CustomerUserAsync(list);
        using var _ = buyer;
        await AddAsync(buyer, skus[0]);
        await AddAsync(buyer, skus[1]);

        var changed = await CartPostAsync(buyer, "Quantity", new() { ["sku"] = skus[0], ["quantity"] = "8" });
        var wrong = await CartPostAsync(buyer, "Quantity", new() { ["sku"] = skus[0], ["quantity"] = "dużo" });
        var removed = await CartPostAsync(buyer, "Remove", new() { ["sku"] = skus[1] });
        var cleared = await CartPostAsync(buyer, "Clear", []);

        Assert.StartsWith("<div id=\"cart\"", changed.Html.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("Brak pełnej ilości — realizacja po dostawie", changed.Html, StringComparison.Ordinal);
        Assert.Contains("Podaj ilość liczbą.", wrong.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"Wkręt {skus[1]}", removed.Html, StringComparison.Ordinal);
        Assert.Matches("class=\"cart-count\"[^>]*>1</span>", removed.Html);
        Assert.Contains("Koszyk jest pusty.", cleared.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocking_lines_are_explained()
    {
        var (list, skus) = await ProductsAsync();
        var (_, buyer) = await CustomerUserAsync(list);
        using var _ = buyer;

        await AddAsync(buyer, skus[2], "9");
        var cart = (await buyer.GetAsync("/pl/cart")).Html;

        Assert.Contains("Towar wycofany — ilość przekracza dostępny zapas.", cart, StringComparison.Ordinal);
        Assert.Contains("Brak ceny dla Twojej firmy", cart, StringComparison.Ordinal);
        Assert.Contains("Zamówienia nie da się złożyć, dopóki w koszyku są oznaczone pozycje.", cart, StringComparison.Ordinal);
        Assert.DoesNotContain("gotowy do zamówienia", cart, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quick_order_adds_by_sku_with_suggestions_and_pasted_lines_with_a_report()
    {
        var (list, skus) = await ProductsAsync();
        var (_, buyer) = await CustomerUserAsync(list);
        using var _ = buyer;

        var quick = await CartPostAsync(buyer, "QuickAdd", new() { ["sku"] = skus[0], ["quantity"] = "3" });
        var pasted = await CartPostAsync(buyer, "Paste", new() { ["text"] = $"{skus[1]}\t2\nNIEMA-{skus[0]} 1\n{skus[0]};dużo" });
        var suggestions = string.Empty;
        await WaitUntilAsync(async () =>
            (suggestions = (await buyer.GetAsync($"/pl/cart?handler=Suggest&sku={Uri.EscapeDataString(skus[0])}")).Html).Contains($"<option value=\"{skus[0]}\">", StringComparison.Ordinal));

        Assert.Contains("Dodano do koszyka pozycji: 1.", quick.Html, StringComparison.Ordinal);
        Assert.Contains("Nie dodano:", pasted.Html, StringComparison.Ordinal);
        Assert.Contains($"wiersz 3 ({skus[0]};dużo): niepoprawna ilość", pasted.Html, StringComparison.Ordinal);
        Assert.Contains($"NIEMA-{skus[0]}: nie ma takiego produktu w katalogu", pasted.Html, StringComparison.Ordinal);
        Assert.Contains($"{skus[0]};dużo</textarea>", pasted.Html, StringComparison.Ordinal);
        Assert.Matches("class=\"cart-count\"[^>]*>2</span>", pasted.Html);
        Assert.Contains($"Wkręt {skus[0]} (szt)</option>", suggestions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task People_without_the_right_to_order_and_visitors_have_no_cart()
    {
        var (list, skus) = await ProductsAsync();
        var (_, accountant) = await CustomerUserAsync(list, template: "accounting");
        using var _ = accountant;
        using var visitor = host.NewBrowser();

        var product = (await accountant.GetAsync($"/pl/products/{skus[0]}")).Html;
        var cart = await accountant.GetAsync("/pl/cart");
        var add = await AddAsync(accountant, skus[0]);
        var anonymous = await visitor.GetAsync("/pl/cart");

        Assert.DoesNotContain("class=\"add-to-cart\"", product, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"cart-link\"", product, StringComparison.Ordinal);
        Assert.Equal("/pl/access-denied", AccessDeniedPath(cart.Location));
        Assert.NotEqual(HttpStatusCode.OK, add.Status);
        Assert.StartsWith("/pl/sign-in", anonymous.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_company_has_its_own_cart_and_a_forged_company_cookie_changes_nothing()
    {
        var (list, skus) = await ProductsAsync();
        var email = UniqueEmail("multi");
        var (first, buyer) = await CustomerUserAsync(list, email: email);
        using var _ = buyer;
        var (stranger, strangerBrowser) = await CustomerUserAsync(list);
        using var __ = strangerBrowser;
        await AddAsync(strangerBrowser, skus[1]);
        await AddAsync(buyer, skus[0]);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/pl/cart");
        forged.Headers.Add("Cookie", $"fc.customer={stranger}");
        using var forgedResponse = await buyer.Client.SendAsync(forged, Token);
        var forgedCart = await forgedResponse.Content.ReadAsStringAsync(Token);

        var second = Unique("K");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            second, 1, $"Druga {second}", null, new AddressData("ul. Krótka 2", "00-950", "Warszawa", "PL"), null,
            [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, "buyer")], null, PriceListCode: list)]));
        await WaitUntilAsync(async () => (await buyer.GetAsync("/pl/company")).Html.Contains(second, StringComparison.Ordinal));
        await buyer.SubmitAsync("/pl/company", new Dictionary<string, string> { ["code"] = second, ["returnUrl"] = "/pl/cart" }, "/pl/company/switch");
        var secondCart = (await buyer.GetAsync("/pl/cart")).Html;
        await buyer.SubmitAsync("/pl/company", new Dictionary<string, string> { ["code"] = first, ["returnUrl"] = "/pl/cart" }, "/pl/company/switch");
        var firstCart = (await buyer.GetAsync("/pl/cart")).Html;

        Assert.Contains($"Wkręt {skus[0]}", forgedCart, StringComparison.Ordinal);
        Assert.DoesNotContain($"Wkręt {skus[1]}", forgedCart, StringComparison.Ordinal);
        Assert.Contains("Koszyk jest pusty.", secondCart, StringComparison.Ordinal);
        Assert.Contains($"Wkręt {skus[0]}", firstCart, StringComparison.Ordinal);
    }

    private static string? AccessDeniedPath(string? location) => location?.Split('?')[0];

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100, Token);
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();

    [GeneratedRegex("\"RequestVerificationToken\": \"([^\"]+)\"")]
    private static partial Regex HeaderToken();
}
