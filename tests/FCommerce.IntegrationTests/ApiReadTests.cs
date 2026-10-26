using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Api;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>The public API's read side: catalog with the key's prices and stock, documents, and the API description.</summary>
public sealed class ApiReadTests(HostFixture host)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly ApiSupport support = new(host);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<ApiProductPage?> SearchAsync(HttpClient client, string query, string? language = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/catalog/products?q={Uri.EscapeDataString(query)}");
        if (language is not null)
        {
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        }

        return SendAsync<ApiProductPage>(client, request);
    }

    private static async Task<T?> SendAsync<T>(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Token);
    }

    private static async Task<ApiProductPage> FoundAsync(HttpClient client, string sku, string? language = null) =>
        (await ApiSupport.EventuallyAsync(() => SearchAsync(client, sku, language), page => page!.Items.Any(p => p.Sku == sku), "The product was not found."))!;

    [Fact]
    public async Task The_catalog_comes_with_the_price_and_stock_of_the_keys_customer()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company);
        using var client = support.ApiClient(key);

        var page = await FoundAsync(client, company.Sku);
        var product = page.Items.Single(p => p.Sku == company.Sku);

        Assert.Equal($"Wkręt {company.Sku}", product.Name);
        Assert.Equal(("priced", "PLN", 10m, 12.30m), (product.Price.Status, product.Price.Offer!.Currency, product.Price.Offer.Unit.Net, product.Price.Offer.Unit.Gross));
        Assert.Equal(company.PriceList, product.Price.Offer.PriceListCode);
        Assert.NotNull(product.Stock);
        Assert.Equal(1, page.Page);
        Assert.Equal("levels", page.StockDisplay, ignoreCase: true);
    }

    [Fact]
    public async Task Texts_follow_the_accept_language_header()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company);
        using var client = support.ApiClient(key);

        var english = await FoundAsync(client, company.Sku, "en");
        var polish = await FoundAsync(client, company.Sku, "pl");

        Assert.Equal($"Screw {company.Sku}", english.Items.Single(p => p.Sku == company.Sku).Name);
        Assert.Equal($"Wkręt {company.Sku}", polish.Items.Single(p => p.Sku == company.Sku).Name);
    }

    [Fact]
    public async Task Two_customers_get_their_own_prices_for_the_same_product()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        await support.PutAsync($"/integration/v1/pricing/price-lists/{other.PriceList}/prices", new PriceBatch([new PriceData(company.Sku, 1, 7m, 23m, [new PriceBreakData(10m, 6m)])]));
        var (key, _) = await support.KeyAsync(company);
        var (otherKey, _) = await support.KeyAsync(other);
        using var client = support.ApiClient(key);
        using var otherClient = support.ApiClient(otherKey);
        await FoundAsync(client, company.Sku);

        var mine = await client.GetFromJsonAsync<ApiProductDetails>($"/api/v1/catalog/products/{company.Sku}", Token);
        var theirs = await otherClient.GetFromJsonAsync<ApiProductDetails>($"/api/v1/catalog/products/{company.Sku}", Token);

        Assert.Equal(10m, mine!.Price.Offer!.Unit.Net);
        Assert.Equal(7m, theirs!.Price.Offer!.Unit.Net);
        Assert.Equal([(1m, 7m), (10m, 6m)], theirs.Price.Offer.Tiers.Select(t => (t.FromQuantity, t.Net)));
    }

    [Fact]
    public async Task A_product_has_its_details_stock_per_warehouse_and_unknown_ones_are_not_found()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company);
        using var client = support.ApiClient(key);
        await FoundAsync(client, company.Sku);

        var details = await client.GetFromJsonAsync<ApiProductDetails>($"/api/v1/catalog/products/{company.Sku}", Token);
        using var unknown = await client.GetAsync("/api/v1/catalog/products/NO-SUCH-SKU", Token);
        using var categories = await client.GetAsync("/api/v1/catalog/categories", Token);

        Assert.Equal((company.Sku, "szt"), (details!.Sku, details.Unit));
        Assert.NotEmpty(details.Stock!.Warehouses);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("application/problem+json", unknown.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
    }

    [Fact]
    public async Task Documents_are_listed_and_downloaded_for_users_who_may_see_them()
    {
        var company = await support.CompanyAsync();
        var id = ApiSupport.Unique("FV") + "/1";
        var foreign = (await support.CompanyAsync());
        var foreignId = ApiSupport.Unique("FV");
        await support.PutAsync("/integration/v1/documents", new ImportDocuments(
        [
            new DocumentData(id, 1, company.Code, "invoice", "FV/2026/10/001", Today.AddDays(-30), Today.AddDays(-30), Today.AddDays(-10), "PLN", 1000m, 1230m, 230m, "ZAM/1"),
            new DocumentData(ApiSupport.Unique("WZ"), 1, company.Code, "delivery-note", "WZ/2026/10/001", Today.AddDays(-31)),
            new DocumentData(foreignId, 1, foreign.Code, "invoice", "FV/2026/10/777", Today.AddDays(-1), Today.AddDays(-1), Today.AddDays(30), "PLN", 10m, 12.3m, 0m, null),
        ]));
        using (var upload = host.CreateIntegrationClient())
        {
            using var body = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n% test\n%%EOF\n"));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            using var stored = await upload.PutAsync($"/integration/v1/documents/{Uri.EscapeDataString(id)}/file", body, Token);
            stored.EnsureSuccessStatusCode();
        }

        var (adminKey, _) = await support.KeyAsync(company, company.AdminId);
        var (buyerKey, _) = await support.KeyAsync(company, company.BuyerId);
        using var admin = support.ApiClient(adminKey);
        using var buyer = support.ApiClient(buyerKey);

        var all = await admin.GetFromJsonAsync<DocumentPage>("/api/v1/documents", Token);
        var invoices = await admin.GetFromJsonAsync<DocumentPage>("/api/v1/documents?type=invoice&status=overdue", Token);
        using var file = await admin.GetAsync($"/api/v1/documents/{Uri.EscapeDataString(id)}/file", Token);
        using var foreignFile = await admin.GetAsync($"/api/v1/documents/{Uri.EscapeDataString(foreignId)}/file", Token);
        var receivables = await admin.GetFromJsonAsync<ReceivableView[]>("/api/v1/receivables", Token);
        using var badType = await admin.GetAsync("/api/v1/documents?type=receipt", Token);
        using var buyerList = await buyer.GetAsync("/api/v1/documents", Token);
        using var buyerFile = await buyer.GetAsync($"/api/v1/documents/{Uri.EscapeDataString(id)}/file", Token);

        Assert.Equal(2, all!.TotalCount);
        Assert.DoesNotContain(all.Items, d => d.Number == "FV/2026/10/777");
        Assert.Equal("FV/2026/10/001", Assert.Single(invoices!.Items).Number);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", await file.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, foreignFile.StatusCode);
        Assert.Equal(1000m, Assert.Single(receivables!).Outstanding);
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, buyerList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, buyerFile.StatusCode);
    }

    [Fact]
    public async Task The_api_is_described_in_its_own_openapi_document()
    {
        using var client = host.CreateClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/public-v1.json", Token)).RootElement;
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("/api/v1/catalog/products", paths);
        Assert.Contains("/api/v1/me", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("/integration", StringComparison.Ordinal));
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("ApiKey", out _));
        var integration = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", Token)).RootElement;
        Assert.DoesNotContain(integration.GetProperty("paths").EnumerateObject(), p => p.Name.StartsWith("/api/v1", StringComparison.Ordinal));
    }
}
