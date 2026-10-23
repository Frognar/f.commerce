using FCommerce.SharedKernel.Imports;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.Media.Contracts;
using SkiaSharp;

namespace FCommerce.IntegrationTests;

public sealed class CatalogTests(HostFixture host)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    /// <summary>A word no other test uses, so search results can be asserted exactly.</summary>
    private static string Marker() => string.Concat(Guid.NewGuid().ToString("N").Where(char.IsAsciiLetter).Take(8).Select(c => (char)('k' + (c - 'a'))));

    private static ProductData Product(string sku, string? category = null, long version = 1, string pl = "Śruba M8x40", string en = "M8x40 bolt", string thread = "M8", IReadOnlyList<string>? images = null) =>
        new(
            sku,
            version,
            new Dictionary<string, string> { ["pl"] = pl, ["en"] = en },
            new Dictionary<string, string> { ["pl"] = "Stal ocynkowana", ["en"] = "Zinc-plated steel" },
            category,
            "szt",
            null,
            images ?? [],
            [new AttributeData("thread", new Dictionary<string, string> { ["pl"] = "Gwint", ["en"] = "Thread" }, new Dictionary<string, string> { ["pl"] = thread })]);

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

    private async Task<(HttpStatusCode Status, UploadedImage? Image)> UploadAsync(byte[] content, string contentType = "image/png")
    {
        using var client = host.CreateIntegrationClient();
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await client.PostAsync("/integration/v1/media/images", body, Token);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<UploadedImage>(Token) : null);
    }

    private static byte[] Png(SKColor colour, int width = 800, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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

    /// <summary>Indexing is asynchronous (outbox → search engine): polls the fragment until the condition holds.</summary>
    private async Task<string> EventuallyAsync(string url, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string html;
        do
        {
            html = await FragmentAsync(url);
            if (condition(html))
            {
                return html;
            }

            await Task.Delay(100, Token);
        }
        while (DateTime.UtcNow < deadline);

        return html;
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
        var report = await ImportAsync(Product(Unique("OK")), Product(Unique("BAD")) with { Unit = "", Name = new Dictionary<string, string> { ["english"] = "x" } });

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
    public async Task Search_ignores_diacritics_tolerates_typos_and_requires_every_word()
    {
        var marker = Marker();
        await ImportAsync(Product(Unique("SRCH"), pl: $"Łożysko kulkowe {marker}", en: $"Ball bearing {marker}"));

        var polish = await EventuallyAsync($"/pl/catalog?q=lozysko+{marker}", html => html.Contains(marker, StringComparison.Ordinal));
        var typo = await FragmentAsync($"/pl/catalog?q=kulkowr+{marker}");
        var english = await EventuallyAsync($"/en/catalog?q=bearing+{marker}", html => html.Contains(marker, StringComparison.Ordinal));
        var notAllWords = await FragmentAsync($"/pl/catalog?q=wiertlo+{marker}");

        Assert.Contains($"Łożysko kulkowe {marker}", polish, StringComparison.Ordinal);
        Assert.Contains($"Łożysko kulkowe {marker}", typo, StringComparison.Ordinal);
        Assert.Contains($"Ball bearing {marker}", english, StringComparison.Ordinal);
        Assert.DoesNotContain("product-card", notAllWords, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wiertło do betonu", "wierteł do betonu")]
    [InlineData("Wiertło do betonu", "wiertłami do betonu")]
    [InlineData("Kołek rozporowy", "kolkow rozporowych")]
    [InlineData("Śruba nierdzewna", "srub nierdzewnych")]
    public async Task Search_understands_polish_inflection(string name, string query)
    {
        var marker = Marker();
        await ImportAsync(Product(Unique("INFL"), pl: $"{name} {marker}"));

        var found = await EventuallyAsync($"/pl/catalog?q={Uri.EscapeDataString(query)}+{marker}", html => html.Contains(marker, StringComparison.Ordinal));

        Assert.Contains($"{name} {marker}", found, StringComparison.Ordinal);
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

        var fragment = await EventuallyAsync($"/pl/catalog?category={parent}", html => html.Contains(inChild, StringComparison.Ordinal));

        Assert.Contains(inChild, fragment, StringComparison.Ordinal);
        Assert.DoesNotContain(inOther, fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Facets_narrow_results_and_keep_counts_of_other_values()
    {
        var category = Unique("fac");
        await ImportCategoriesAsync(new CategoryData(category, null, new Dictionary<string, string> { ["pl"] = "Fasety" }, 1));
        var m8 = Unique("M8");
        var m10 = Unique("M10");
        await ImportAsync(Product(m8, category, thread: "M8"), Product(m10, category, thread: "M10"), Product(Unique("M10B"), category, thread: "M10"));

        var all = await EventuallyAsync($"/pl/catalog?category={category}", html => html.Contains("Wyniki: 3", StringComparison.Ordinal));
        var onlyM8 = await FragmentAsync($"/pl/catalog?category={category}&f=thread%3DM8");

        Assert.Contains("<legend>Gwint</legend>", all, StringComparison.Ordinal);
        Assert.Contains("Wyniki: 1", onlyM8, StringComparison.Ordinal);
        Assert.Contains(m8, onlyM8, StringComparison.Ordinal);
        Assert.DoesNotContain(m10, onlyM8, StringComparison.Ordinal);
        Assert.Contains("value=\"thread=M8\" checked", onlyM8, StringComparison.Ordinal);
        Assert.Contains("<span>M10</span> <span class=\"muted\">(2)</span>", onlyM8, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deactivated_products_leave_the_search_results()
    {
        var marker = Marker();
        var sku = Unique("GONE");
        await ImportAsync(Product(sku, pl: $"Produkt {marker}"));
        await EventuallyAsync($"/pl/catalog?q={marker}", html => html.Contains(sku, StringComparison.Ordinal));

        await ImportAsync(Product(sku, version: 2, pl: $"Produkt {marker}") with { Active = false });
        var after = await EventuallyAsync($"/pl/catalog?q={marker}", html => !html.Contains(sku, StringComparison.Ordinal));

        Assert.DoesNotContain(sku, after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploading_the_same_image_twice_returns_the_same_id()
    {
        var content = Png(new SKColor(10, 120, (byte)Random.Shared.Next(256)));

        var first = await UploadAsync(content);
        var second = await UploadAsync(content);
        var invalid = await UploadAsync("not an image"u8.ToArray());

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Image!.Id, second.Image!.Id);
        Assert.Equal((800, 600), (first.Image.Width, first.Image.Height));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
    }

    [Fact]
    public async Task Images_are_served_as_cacheable_webp_variants()
    {
        var (_, image) = await UploadAsync(Png(SKColors.OrangeRed, 2000, 1000));
        using var client = host.CreateClient();

        using var thumb = await client.GetAsync($"/media/images/{image!.Id}/thumb", Token);
        using var unknown = await client.GetAsync($"/media/images/{image.Id}/huge", Token);
        var bytes = await thumb.Content.ReadAsByteArrayAsync(Token);
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes));

        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", thumb.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal((240, 120), (codec.Info.Width, codec.Info.Height));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Products_must_reference_uploaded_images()
    {
        var (_, image) = await UploadAsync(Png(SKColors.Teal));

        var report = await ImportAsync(Product(Unique("IMG"), images: [image!.Id, new string('f', 64)]));

        var rejected = Assert.Single(report.Rejected);
        Assert.Equal("imageIds[1]", Assert.Single(rejected.Errors).Field);
    }

    [Fact]
    public async Task Product_page_shows_a_gallery_of_all_images()
    {
        var images = new List<string>();
        foreach (var colour in new[] { SKColors.Red, SKColors.Green, SKColors.Blue })
        {
            images.Add((await UploadAsync(Png(colour.WithAlpha((byte)Random.Shared.Next(200, 256))))).Image!.Id);
        }

        var sku = Unique("GAL");
        await ImportAsync(Product(sku, images: images));
        using var client = host.CreateClient();

        var html = await client.GetStringAsync($"/pl/products/{sku}", Token);

        Assert.Contains("data-gallery", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"/media/images/{images[0]}/medium\"", html, StringComparison.Ordinal);
        Assert.All(images, id => Assert.Contains($"src=\"/media/images/{id}/thumb\"", html, StringComparison.Ordinal));
        Assert.Contains($"data-large=\"/media/images/{images[2]}/large\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Product_without_images_shows_a_placeholder()
    {
        var sku = Unique("NOIMG");
        await ImportAsync(Product(sku));
        using var client = host.CreateClient();

        var html = await client.GetStringAsync($"/pl/products/{sku}", Token);

        Assert.Contains("src=\"/img/placeholder.svg\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-gallery", html, StringComparison.Ordinal);
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
