using System.Globalization;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>A separate host: these tests change the default price list and the stock display of the installation.</summary>
public sealed class CommerceHostFixture : HostFixture;

public sealed partial class StorefrontOffersTests(CommerceHostFixture host) : IClassFixture<CommerceHostFixture>
{
    private const string Password = "customer password 1";
    private static readonly SemaphoreSlim Sequential = new(1, 1);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static readonly string[] DefaultFeatures =
        ["customer-accounts", "multi-customer-accounts", "customer-admin-invitations", "erp-contact-invitations", "documents", "public-prices", "stock-levels"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    /// <summary>A word no other test uses, so search results can be asserted exactly.</summary>
    private static string Marker() => string.Concat(Guid.NewGuid().ToString("N").Where(char.IsAsciiLetter).Take(8).Select(c => (char)('k' + (c - 'a'))));

    private static string PolishDate(DateOnly date) => date.ToString("d", CultureInfo.GetCultureInfo("pl-PL"));

    /// <summary>The settings form with the given commerce settings (everything else at its default).</summary>
    private static List<KeyValuePair<string, string>> Settings(
        long version, string priceList = "", bool publicPrices = true, string stockDisplay = "Levels", bool stockLevels = true)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new("Form.Version", version.ToString(CultureInfo.InvariantCulture)),
            new("Form.CatalogVisibility", "Public"),
            new("Form.StoreName", "f.commerce"),
            new("Form.AccentColor", "#1f5eff"),
            new("Form.DefaultContactTemplate", "buyer"),
            new("Form.DefaultPriceListCode", priceList),
            new("Form.StockDisplay", stockDisplay),
            new("Form.LowStockThreshold", "10"),
        ];
        fields.AddRange(DefaultFeatures
            .Where(f => (f != "public-prices" || publicPrices) && (f != "stock-levels" || stockLevels))
            .Select(f => new KeyValuePair<string, string>("Form.EnabledFeatures", f)));
        return fields;
    }

    private static async Task SaveSettingsAsync(Browser admin, string priceList = "", bool publicPrices = true, string stockDisplay = "Levels", bool stockLevels = true)
    {
        var version = long.Parse(VersionField().Match((await admin.GetAsync("/pl/admin/settings")).Html).Groups[1].Value, CultureInfo.InvariantCulture);
        var (_, html, _) = await admin.SubmitAsync("/pl/admin/settings", Settings(version, priceList, publicPrices, stockDisplay, stockLevels));
        Assert.Contains("Zapisano zmiany.", html, StringComparison.Ordinal);
    }

    /// <summary>Settings are global: tests of this class change them one at a time and restore the defaults.</summary>
    private async Task WithSettingsAsync(Func<Browser, Task> test)
    {
        await Sequential.WaitAsync(Token);
        using var admin = await host.AdminAsync();
        try
        {
            await test(admin);
        }
        finally
        {
            await SaveSettingsAsync(admin);
            Sequential.Release();
        }
    }

    private async Task PutAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        response.EnsureSuccessStatusCode();
    }

    private Task ProductsAsync(params string[] skus) =>
        PutAsync("/integration/v1/catalog/products", new ImportProducts([.. skus.Select(sku => new ProductData(
            sku,
            1,
            new Dictionary<string, string> { ["pl"] = $"Śruba {sku}", ["en"] = $"Bolt {sku}" },
            null,
            null,
            "szt",
            null,
            [],
            []))]));

    private Task ProductAsync(string sku, string name) =>
        PutAsync("/integration/v1/catalog/products", new ImportProducts([new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = name, ["en"] = name }, null, null, "szt", null, [], [])]));

    /// <summary>A price list with prices of the given SKUs (net; VAT 23%).</summary>
    private async Task<string> PriceListAsync(string currency, params (string Sku, decimal Net)[] prices)
    {
        var code = Unique("C");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(code, 1, $"Cennik {code}", currency)]));
        await PutAsync($"/integration/v1/pricing/price-lists/{code}/prices", new PriceBatch([.. prices.Select(p => new PriceData(p.Sku, 1, p.Net, 23m))]));
        return code;
    }

    /// <summary>A customer (with the price list) whose contact accepted the invitation and is signed in.</summary>
    private async Task<(string Code, Browser Browser)> CustomerUserAsync(string? priceList, string? email = null)
    {
        var code = Unique("K");
        email ??= UniqueEmail("buyer");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null,
            [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, "buyer")], null, PriceListCode: priceList)]));
        var browser = host.NewBrowser();
        await host.AcceptInvitationAsync(browser, email, Password);
        return (code, browser);
    }

    private static async Task<string> ProductPageAsync(Browser browser, string sku) => (await browser.GetAsync($"/pl/products/{sku}")).Html;

    [Fact]
    public Task Without_a_default_list_visitors_sign_in_and_customers_see_their_own_prices_or_on_request() =>
        WithSettingsAsync(async _ =>
        {
            var sku = Unique("SKU");
            await ProductsAsync(sku);
            var own = await PriceListAsync("PLN", (sku, 10.01m));
            var (_, withList) = await CustomerUserAsync(own);
            var (_, withoutList) = await CustomerUserAsync(null);
            using var a = withList;
            using var b = withoutList;
            using var visitor = host.NewBrowser();

            var anonymous = await ProductPageAsync(visitor, sku);
            var priced = await ProductPageAsync(withList, sku);
            var onRequest = await ProductPageAsync(withoutList, sku);

            Assert.Contains("Zaloguj się, aby zobaczyć ceny", anonymous, StringComparison.Ordinal);
            Assert.Contains($"href=\"/pl/sign-in?returnUrl=%2Fpl%2Fproducts%2F{sku}\"", anonymous, StringComparison.Ordinal);
            Assert.Contains("10,01 PLN <small>netto / szt</small>", priced, StringComparison.Ordinal);
            Assert.Contains("12,31 PLN brutto (VAT 23%)", priced, StringComparison.Ordinal);
            Assert.Contains("Cena na zapytanie", onRequest, StringComparison.Ordinal);
        });

    [Fact]
    public Task The_default_list_prices_visitors_and_fills_gaps_of_own_lists() =>
        WithSettingsAsync(async admin =>
        {
            var (both, onlyDefault, onlyOwn) = (Unique("SKU"), Unique("SKU"), Unique("SKU"));
            await ProductsAsync(both, onlyDefault, onlyOwn);
            var retail = await PriceListAsync("PLN", (both, 10m), (onlyDefault, 20m));
            var wholesale = await PriceListAsync("PLN", (both, 8m), (onlyOwn, 5m));
            var export = await PriceListAsync("EUR", (both, 3m));
            var (_, customer) = await CustomerUserAsync(wholesale);
            var (_, foreign) = await CustomerUserAsync(export);
            using var c = customer;
            using var f = foreign;
            using var visitor = host.NewBrowser();
            await SaveSettingsAsync(admin, priceList: retail);

            Assert.Contains("10,00 PLN", await ProductPageAsync(visitor, both), StringComparison.Ordinal);
            Assert.Contains("Zaloguj się, aby zobaczyć ceny", await ProductPageAsync(visitor, onlyOwn), StringComparison.Ordinal);
            Assert.Contains("8,00 PLN", await ProductPageAsync(customer, both), StringComparison.Ordinal);
            Assert.Contains("20,00 PLN", await ProductPageAsync(customer, onlyDefault), StringComparison.Ordinal);
            Assert.Contains("5,00 PLN", await ProductPageAsync(customer, onlyOwn), StringComparison.Ordinal);
            Assert.Contains("3,00 EUR", await ProductPageAsync(foreign, both), StringComparison.Ordinal);
            Assert.Contains("Cena na zapytanie", await ProductPageAsync(foreign, onlyDefault), StringComparison.Ordinal); // no PLN fallback

            await SaveSettingsAsync(admin, priceList: retail, publicPrices: false);

            Assert.Contains("Zaloguj się, aby zobaczyć ceny", await ProductPageAsync(visitor, both), StringComparison.Ordinal);
            Assert.Contains("20,00 PLN", await ProductPageAsync(customer, onlyDefault), StringComparison.Ordinal);
        });

    [Fact]
    public Task Prices_of_another_company_cannot_be_asked_for_and_switching_companies_changes_them() =>
        WithSettingsAsync(async _ =>
        {
            var sku = Unique("SKU");
            await ProductsAsync(sku);
            var mine = await PriceListAsync("PLN", (sku, 11m));
            var theirs = await PriceListAsync("PLN", (sku, 7m));
            var email = UniqueEmail("multi");
            var (_, browser) = await CustomerUserAsync(mine, email);
            using var b = browser;
            var (other, otherBrowser) = await CustomerUserAsync(theirs);
            otherBrowser.Dispose();

            var tampered = await browser.GetAsync($"/pl/products/{sku}?customer={other}&priceList={theirs}");
            using var forged = new HttpRequestMessage(HttpMethod.Get, $"/pl/products/{sku}");
            forged.Headers.Add("Cookie", $"fc.customer={other}");
            using var forgedResponse = await browser.Client.SendAsync(forged, Token);
            var forgedCookie = await forgedResponse.Content.ReadAsStringAsync(Token);

            var second = Unique("K");
            await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
                second, 1, $"Druga {second}", null, new AddressData("ul. Krótka 2", "00-950", "Warszawa", "PL"), null,
                [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, "buyer")], null, PriceListCode: theirs)]));
            await WaitUntilAsync(async () => (await browser.GetAsync("/pl/company")).Html.Contains(second, StringComparison.Ordinal));
            await browser.SubmitAsync("/pl/company", new Dictionary<string, string> { ["code"] = second, ["returnUrl"] = "/pl/company" }, "/pl/company/switch");
            var switched = await ProductPageAsync(browser, sku);

            Assert.Contains("11,00 PLN", tampered.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("7,00 PLN", tampered.Html, StringComparison.Ordinal);
            Assert.Contains("11,00 PLN", forgedCookie, StringComparison.Ordinal);
            Assert.DoesNotContain("7,00 PLN", forgedCookie, StringComparison.Ordinal);
            Assert.Contains("7,00 PLN", switched, StringComparison.Ordinal);
        });

    [Fact]
    public Task The_product_page_shows_quantity_prices_and_stock_per_warehouse_as_configured() =>
        WithSettingsAsync(async admin =>
        {
            var sku = Unique("SKU");
            await ProductsAsync(sku);
            var list = Unique("C");
            await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Hurt", "PLN")]));
            await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(sku, 1, 10m, 23m, [new PriceBreakData(50, 9.5m), new PriceBreakData(200, 9.1234m)])]));
            var (near, far, hidden) = (Unique("W"), Unique("W"), Unique("W"));
            await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([
                new WarehouseData(near, 1, $"Wrocław {near}", SortOrder: 1), new WarehouseData(far, 1, $"Poznań {far}", SortOrder: 2), new WarehouseData(hidden, 1, "Serwis", Visible: false)]));
            await PutAsync($"/integration/v1/inventory/warehouses/{near}/stock", new StockBatch([new StockData(sku, 1, 4m)]));
            await PutAsync($"/integration/v1/inventory/warehouses/{far}/stock", new StockBatch([new StockData(sku, 1, -1m, Today.AddDays(5), 30m)]));
            await PutAsync($"/integration/v1/inventory/warehouses/{hidden}/stock", new StockBatch([new StockData(sku, 1, 900m)]));
            await SaveSettingsAsync(admin, priceList: list);
            using var visitor = host.NewBrowser();

            var levels = await ProductPageAsync(visitor, sku);
            await SaveSettingsAsync(admin, priceList: list, stockDisplay: "Exact");
            var exact = await ProductPageAsync(visitor, sku);
            await SaveSettingsAsync(admin, priceList: list, stockLevels: false);
            var hiddenStock = await ProductPageAsync(visitor, sku);

            Assert.Matches("<td>50 szt</td>\\s*<td class=\"number\">9,50 PLN</td>\\s*<td class=\"number\">11,69 PLN</td>", levels);
            Assert.Matches("<td>200 szt</td>\\s*<td class=\"number\">9,1234 PLN</td>\\s*<td class=\"number\">11,2218 PLN</td>", levels);
            Assert.Matches($"Wrocław {near}</th>\\s*<td><span class=\"stock stock-low\">\\s*Mało", levels);
            Assert.Matches($"Poznań {far}</th>\\s*<td><span class=\"stock stock-none\">\\s*Brak\\s*</span>\\s*<span class=\"stock-delivery\">Dostawa {Regex.Escape(PolishDate(Today.AddDays(5)))}</span>", levels);
            Assert.DoesNotContain("Serwis", levels, StringComparison.Ordinal);
            Assert.Contains("4 szt (mało)", exact, StringComparison.Ordinal);
            Assert.Contains($"Dostawa {PolishDate(Today.AddDays(5))}: 30 szt", exact, StringComparison.Ordinal);
            Assert.DoesNotContain("Magazyny", hiddenStock, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"stock ", hiddenStock, StringComparison.Ordinal);
        });

    [Fact]
    public Task The_catalog_list_shows_prices_and_stock_also_in_htmx_responses() =>
        WithSettingsAsync(async admin =>
        {
            var marker = Marker();
            var (cheap, unpriced) = (Unique("SKU"), Unique("SKU"));
            await ProductAsync(cheap, $"Nakrętka {marker} M8");
            await ProductAsync(unpriced, $"Podkładka {marker} M8");
            var list = Unique("C");
            await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Detal", "PLN")]));
            await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(cheap, 1, 0.0123m, 23m, [new PriceBreakData(1000, 0.01m)])]));
            var warehouse = Unique("W");
            await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
            await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([new StockData(cheap, 1, 5000m)]));
            await SaveSettingsAsync(admin, priceList: list);

            var fragment = await EventuallyAsync($"/pl/catalog?q={marker}", html => html.Contains(cheap, StringComparison.Ordinal) && html.Contains(unpriced, StringComparison.Ordinal));
            using var visitor = host.NewBrowser();
            var page = (await visitor.GetAsync($"/pl/catalog?q={marker}")).Html;

            Assert.Contains("0,0123 PLN <small>netto / szt</small>", fragment, StringComparison.Ordinal);
            Assert.Contains("0,0151 PLN brutto", fragment, StringComparison.Ordinal);
            Assert.Contains("taniej od 1 000 szt", Spaced(fragment), StringComparison.Ordinal);
            Assert.Contains("stock stock-available", fragment, StringComparison.Ordinal);
            Assert.Contains("Zaloguj się, aby zobaczyć ceny", fragment, StringComparison.Ordinal); // the product without a price
            Assert.Contains($"returnUrl=%2Fpl%2Fcatalog%3Fq%3D{marker}", fragment, StringComparison.Ordinal);
            Assert.Contains("0,0123 PLN", page, StringComparison.Ordinal);
        });

    /// <summary>Number group separators (non-breaking spaces, possibly encoded) as plain spaces.</summary>
    private static string Spaced(string html) =>
        html.Replace("&#xA0;", " ", StringComparison.Ordinal).Replace("&#x202F;", " ", StringComparison.Ordinal).Replace(' ', ' ').Replace(' ', ' ');

    private async Task<string> EventuallyAsync(string url, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string html;
        do
        {
            using var client = host.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("HX-Request", "true");
            request.Headers.Add("HX-Target", "results");
            using var response = await client.SendAsync(request, Token);
            html = await response.Content.ReadAsStringAsync(Token);
            if (condition(html))
            {
                return html;
            }

            await Task.Delay(100, Token);
        }
        while (DateTime.UtcNow < deadline);

        return html;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100, Token);
        }
    }

    [GeneratedRegex("name=\"Form.Version\" value=\"(\\d+)\"")]
    private static partial Regex VersionField();
}
