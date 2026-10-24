using System.Net;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>Products of an invoice or delivery note into the cart, on the shared host (features on by default).</summary>
public sealed class DocumentToCartTests(HostFixture host)
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

    private async Task<(string Code, Browser Browser)> CustomerUserAsync(string template)
    {
        var code = Unique("K");
        var email = UniqueEmail(template);
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null,
            [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, template)], null)]));
        var browser = host.NewBrowser();
        await host.AcceptInvitationAsync(browser, email, Password);
        return (code, browser);
    }

    private static DocumentData Invoice(string id, string customer, params DocumentLineData[] lines) =>
        new(id, 1, customer, "invoice", $"FV/{id}", Today.AddDays(-3), Today.AddDays(-3), Today.AddDays(11), "PLN", 100m, 123m, 0m, null, lines);

    private static Task<(HttpStatusCode Status, string Html, string? Location)> ToCartAsync(Browser browser, string id, bool replace) =>
        browser.SubmitAsync(
            "/pl/company/documents",
            new Dictionary<string, string> { ["replace"] = replace ? "true" : "false" },
            $"/pl/company/documents?handler=ToCart&id={Uri.EscapeDataString(id)}");

    [Fact]
    public async Task The_products_of_an_invoice_go_to_the_cart_added_or_replacing_it()
    {
        var (a, b, c) = (Unique("A"), Unique("B"), Unique("C"));
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([.. new[] { a, b, c }.Select(sku => new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Kołek {sku}" }, null, null, "szt", null, [], []))]));
        var (code, admin) = await CustomerUserAsync("administrator");
        using var _ = admin;
        var invoice = Unique("FV") + "/1";
        var empty = Unique("FV");
        await PutAsync("/integration/v1/documents", new ImportDocuments([
            Invoice(invoice, code, new(1, a, "Kołek A", 10, "szt", 1m, 10m), new(2, b, "Kołek B", 2.5m, "szt"), new(3, null, "Transport", 1, "usł."), new(4, a, "Kołek A", 5, "szt")),
            Invoice(empty, code)]));
        await admin.SubmitAsync("/pl/cart", new Dictionary<string, string> { ["text"] = $"{c} 1" }, "/pl/cart?handler=Paste");

        var list = (await admin.GetAsync("/pl/company/documents")).Html;
        var added = await ToCartAsync(admin, invoice, replace: false);
        var afterAdd = (await admin.GetAsync("/pl/cart")).Html;
        var replaced = await ToCartAsync(admin, invoice, replace: true);
        var afterReplace = (await admin.GetAsync("/pl/cart")).Html;

        Assert.Contains($"id={Uri.EscapeDataString(invoice)}", list, StringComparison.Ordinal);
        Assert.DoesNotContain($"id={empty}", list, StringComparison.Ordinal); // no lines, nothing to order
        Assert.Contains("Dodano do koszyka pozycji: 2. W koszyku jest teraz 3 produktów.", added.Html, StringComparison.Ordinal);
        Assert.Contains("pozycji bez indeksu (usługi, opisy): 1", added.Html, StringComparison.Ordinal);
        Assert.Contains("value=\"15\"", afterAdd, StringComparison.Ordinal);
        Assert.Contains($"Kołek {c}", afterAdd, StringComparison.Ordinal);
        Assert.Contains("W koszyku jest teraz 2 produktów.", replaced.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"Kołek {c}", afterReplace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_of_another_company_cannot_be_sent_and_accountants_have_no_cart()
    {
        var (otherCode, other) = await CustomerUserAsync("buyer");
        other.Dispose();
        var foreign = Unique("FV");
        await PutAsync("/integration/v1/documents", new ImportDocuments([Invoice(foreign, otherCode, new DocumentLineData(1, Unique("A"), "x", 1, "szt"))]));
        var (_, admin) = await CustomerUserAsync("administrator");
        var (accountantCode, accountant) = await CustomerUserAsync("accounting");
        using var a1 = admin;
        using var a2 = accountant;
        var own = Unique("FV");
        await PutAsync("/integration/v1/documents", new ImportDocuments([Invoice(own, accountantCode, new DocumentLineData(1, Unique("A"), "x", 1, "szt"))]));

        var stolen = await ToCartAsync(admin, foreign, replace: false);
        var accountantList = (await accountant.GetAsync("/pl/company/documents")).Html;

        Assert.Contains("Nie ma takiego dokumentu.", stolen.Html, StringComparison.Ordinal);
        Assert.Contains($"FV/{own}", accountantList, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ToCart", accountantList, StringComparison.Ordinal);
    }
}
