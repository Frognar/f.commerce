using System.Net;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;

namespace FCommerce.IntegrationTests;

public sealed class CatalogTests(HostFixture host)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static ProductData Product(string sku, string? category = null, long version = 1, string pl = "Śruba M8x40", string en = "M8x40 bolt") =>
        new(
            sku,
            version,
            new Dictionary<string, string> { ["pl"] = pl, ["en"] = en },
            new Dictionary<string, string> { ["pl"] = "Stal ocynkowana", ["en"] = "Zinc-plated steel" },
            category,
            "szt",
            null,
            ["https://cdn.example.com/bolt.jpg"],
            [new AttributeData("thread", new Dictionary<string, string> { ["pl"] = "Gwint", ["en"] = "Thread" }, new Dictionary<string, string> { ["pl"] = "M8" })]);

    private async Task<ImportReport> ImportAsync(params ProductData[] products)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync("/integration/v1/catalog/products", new ImportProducts(products), Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportReport>(Token))!;
    }

    private async Task ImportCategoriesAsync(params CategoryData[] categories)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync("/integration/v1/catalog/categories", new ImportCategories(categories), Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> FragmentAsync(string url)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Target", "results");
        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(Token);
    }

    [Fact]
    public async Task Integration_api_requires_the_api_key()
    {
        using var client = host.CreateClient();

        using var response = await client.PutAsJsonAsync("/integration/v1/catalog/products", new ImportProducts([]), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_applies_valid_items_and_reports_invalid_ones_with_field_paths()
    {
        var sku = Unique("OK");

        var report = await ImportAsync(Product(sku), Product(Unique("BAD")) with { Unit = "", Name = new Dictionary<string, string> { ["english"] = "x" } });

        Assert.Equal((2, 1), (report.Received, report.Created));
        var rejected = Assert.Single(report.Rejected);
        Assert.Equal(1, rejected.Index);
        Assert.Equal(["name.english", "unit"], rejected.Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Reimporting_is_idempotent_and_older_versions_are_ignored()
    {
        var sku = Unique("IDEM");
        await ImportAsync(Product(sku, version: 5));

        var same = await ImportAsync(Product(sku, version: 6));
        var older = await ImportAsync(Product(sku, version: 4, en: "Changed"));
        var newer = await ImportAsync(Product(sku, version: 7, en: "Changed"));

        Assert.Equal(1, same.Unchanged);
        Assert.Equal(1, older.Stale);
        Assert.Equal(1, newer.Updated);
    }

    [Fact]
    public async Task Search_matches_word_prefixes_without_diacritics_in_any_language()
    {
        var sku = Unique("SRCH");
        await ImportAsync(Product(sku, pl: $"Łożysko kulkowe {sku}", en: $"Ball bearing {sku}"));

        var polish = await FragmentAsync($"/pl/catalog?q=lozy+kulk+{sku}");
        var english = await FragmentAsync($"/en/catalog?q=bearing+{sku}");
        var none = await FragmentAsync($"/pl/catalog?q=wiertlo+{sku}");

        Assert.Contains($"Łożysko kulkowe {sku}", polish, StringComparison.Ordinal);
        Assert.Contains($"Ball bearing {sku}", english, StringComparison.Ordinal);
        Assert.DoesNotContain(sku, none.Replace($"q=wiertlo+{sku}", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Category_filter_includes_subcategories()
    {
        var parent = Unique("cat");
        var child = Unique("sub");
        var other = Unique("oth");
        await ImportCategoriesAsync(
            new CategoryData(child, parent, new Dictionary<string, string> { ["pl"] = "Śruby" }, 1),
            new CategoryData(parent, null, new Dictionary<string, string> { ["pl"] = "Złączne" }, 1),
            new CategoryData(other, null, new Dictionary<string, string> { ["pl"] = "Inne" }, 1));
        var inChild = Unique("CHILD");
        var inOther = Unique("OTHER");
        await ImportAsync(Product(inChild, child), Product(inOther, other));

        var fragment = await FragmentAsync($"/pl/catalog?category={parent}");

        Assert.Contains(inChild, fragment, StringComparison.Ordinal);
        Assert.DoesNotContain(inOther, fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Product_page_is_localized_and_unknown_products_are_not_found()
    {
        var sku = Unique("PAGE");
        await ImportAsync(Product(sku));
        using var client = host.CreateClient();

        var english = await client.GetStringAsync($"/en/products/{sku}", Token);
        using var missing = await client.GetAsync($"/pl/products/{Unique("NONE")}", Token);

        Assert.Contains("<h1>M8x40 bolt</h1>", english, StringComparison.Ordinal);
        Assert.Contains("Zinc-plated steel", english, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"row\">Thread</th><td>M8</td>", english, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Product_page_links_keep_the_culture_and_the_case_sensitive_sku()
    {
        var category = Unique("lnk");
        await ImportCategoriesAsync(new CategoryData(category, null, new Dictionary<string, string> { ["pl"] = "Linki" }, 1));
        var sku = Unique("MiXeD");
        await ImportAsync(Product(sku, category));
        using var client = host.CreateClient();

        var html = await client.GetStringAsync($"/pl/products/{sku}", Token);

        Assert.Contains("<a class=\"brand\" href=\"/pl\">", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/pl/catalog?category={category}\"", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/en/products/{sku}\">EN</a>", html, StringComparison.Ordinal);
        Assert.Contains($"hreflang=\"en\" href=\"/en/products/{sku}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_are_published_through_the_outbox()
    {
        var sku = Unique("EVT");

        await ImportAsync(Product(sku));
        await ImportAsync(Product(sku, version: 2) with { Active = false });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (host.Events.ProductChanges.Count(e => e.Sku == sku) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Token);
        }

        Assert.Equal([new ProductChanged(sku, true), new ProductChanged(sku, false)], host.Events.ProductChanges.Where(e => e.Sku == sku));
    }
}
