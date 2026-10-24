using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>Shopping lists in the customer area, on the shared host (the feature is on by default).</summary>
public sealed partial class ShoppingListTests(HostFixture host)
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

    private Task ProductsAsync(params string[] skus) =>
        PutAsync("/integration/v1/catalog/products", new ImportProducts([.. skus.Select(sku => new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Kotwa {sku}" }, null, null, "szt", null, [], []))]));

    private async Task<(string List, Browser Browser)> BuyerAsync(string template = "buyer")
    {
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        var code = Unique("K");
        var email = UniqueEmail(template);
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null,
            [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, template)], null, PriceListCode: list)]));
        var browser = host.NewBrowser();
        await host.AcceptInvitationAsync(browser, email, Password);
        return (list, browser);
    }

    private static Task<(HttpStatusCode Status, string Html, string? Location)> CartAsync(Browser browser, string handler, Dictionary<string, string> fields) =>
        browser.SubmitAsync("/pl/cart", fields, $"/pl/cart?handler={handler}");

    private static async Task<Guid> SaveCartAsync(Browser browser, string name)
    {
        var (_, _, location) = await CartAsync(browser, "SaveList", new() { ["name"] = name });
        return Guid.Parse(ListLocation().Match(location ?? string.Empty).Groups[1].Value);
    }

    [Fact]
    public async Task The_cart_is_saved_as_a_list_and_the_list_goes_back_to_the_cart_added_or_replacing_it()
    {
        var (a, b, c) = (Unique("A"), Unique("B"), Unique("C"));
        await ProductsAsync(a, b, c);
        var (_, buyer) = await BuyerAsync();
        using var _ = buyer;
        await CartAsync(buyer, "Paste", new() { ["text"] = $"{a} 2\n{b} 1" });

        var id = await SaveCartAsync(buyer, "Budowa Wola");
        var duplicate = await CartAsync(buyer, "SaveList", new() { ["name"] = "budowa wola" });
        await CartAsync(buyer, "Clear", []);
        await CartAsync(buyer, "Paste", new() { ["text"] = $"{c} 1\n{a} 1" });
        var added = await buyer.SubmitAsync("/pl/company/lists", new Dictionary<string, string> { ["replace"] = "false" }, $"/pl/company/lists/{id}?handler=ToCart");
        var afterAdd = (await buyer.GetAsync("/pl/cart")).Html;
        var replaced = await buyer.SubmitAsync("/pl/company/lists", new Dictionary<string, string> { ["replace"] = "true" }, $"/pl/company/lists/{id}?handler=ToCart");
        var afterReplace = (await buyer.GetAsync("/pl/cart")).Html;
        var lists = (await buyer.GetAsync("/pl/company/lists")).Html;

        Assert.Contains("Masz już listę o tej nazwie.", duplicate.Html, StringComparison.Ordinal);
        Assert.Contains("Dodano do koszyka pozycji: 2. W koszyku jest teraz 3 produktów.", added.Html, StringComparison.Ordinal);
        Assert.Contains("value=\"3\"", afterAdd, StringComparison.Ordinal); // 1 + 2 of A
        Assert.Contains("W koszyku jest teraz 2 produktów.", replaced.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"Kotwa {c}", afterReplace, StringComparison.Ordinal);
        Assert.Contains($"href=\"/pl/company/lists/{id}\">Budowa Wola</a>", lists, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_is_edited_renamed_and_deleted_and_withdrawn_products_are_reported()
    {
        var (a, b) = (Unique("A"), Unique("B"));
        await ProductsAsync(a, b);
        var (_, buyer) = await BuyerAsync();
        using var _ = buyer;
        await CartAsync(buyer, "Paste", new() { ["text"] = $"{a} 2\n{b} 1" });
        var id = await SaveCartAsync(buyer, "Zapas");
        var page = $"/pl/company/lists/{id}";

        var changed = await buyer.SubmitAsync(page, new Dictionary<string, string> { ["sku"] = a, ["quantity"] = "7,5" }, $"{page}?handler=Quantity");
        var renamed = await buyer.SubmitAsync(page, new Dictionary<string, string> { ["name"] = "Zapas na zimę" }, $"{page}?handler=Rename");
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([new ProductData(b, 2, new Dictionary<string, string> { ["pl"] = "x" }, null, null, "szt", null, [], [], Active: false)]));
        var withdrawn = (await buyer.GetAsync(page)).Html;
        var toCart = await buyer.SubmitAsync(page, new Dictionary<string, string> { ["replace"] = "true" }, $"/pl/company/lists/{id}?handler=ToCart");
        var deleted = await buyer.SubmitAsync(page, new Dictionary<string, string>(), $"{page}?handler=Delete");
        var gone = await buyer.GetAsync(page);

        Assert.Contains("value=\"7,5\"", changed.Html, StringComparison.Ordinal);
        Assert.Contains("<h1>Zapas na zimę</h1>", renamed.Html, StringComparison.Ordinal);
        Assert.Contains("Produkt nie jest już dostępny w katalogu", withdrawn, StringComparison.Ordinal);
        Assert.Contains($"{b}: nie ma takiego produktu w katalogu", toCart.Html, StringComparison.Ordinal);
        Assert.Equal("/pl/company/lists", deleted.Location);
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task Lists_of_other_people_are_not_found_and_people_who_cannot_order_have_none()
    {
        var a = Unique("A");
        await ProductsAsync(a);
        var (_, owner) = await BuyerAsync();
        var (_, other) = await BuyerAsync();
        var (_, accountant) = await BuyerAsync("accounting");
        using var o1 = owner;
        using var o2 = other;
        using var o3 = accountant;
        await CartAsync(owner, "Paste", new() { ["text"] = $"{a} 1" });
        var name = Unique("Lista");
        var id = await SaveCartAsync(owner, name);

        var foreign = await other.GetAsync($"/pl/company/lists/{id}");
        var foreignToCart = await other.SubmitAsync("/pl/company/lists", new Dictionary<string, string> { ["replace"] = "true" }, $"/pl/company/lists?handler=ToCart&id={id}");
        var foreignDetailsToCart = await other.SubmitAsync("/pl/company/lists", new Dictionary<string, string> { ["replace"] = "true" }, $"/pl/company/lists/{id}?handler=ToCart");
        var otherLists = (await other.GetAsync("/pl/company/lists")).Html;
        var forbidden = await accountant.GetAsync("/pl/company/lists");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Contains("Nie ma takiej listy zakupowej.", foreignToCart.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, foreignDetailsToCart.Status);
        Assert.DoesNotContain(name, otherLists, StringComparison.Ordinal);
        Assert.StartsWith("/pl/access-denied", forbidden.Location, StringComparison.Ordinal);
    }

    [GeneratedRegex("/pl/company/lists/([0-9a-f-]{36})")]
    private static partial Regex ListLocation();
}
