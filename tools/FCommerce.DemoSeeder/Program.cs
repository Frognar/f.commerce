using FCommerce.SharedKernel.Imports;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.DemoSeeder;
using FCommerce.Media.Contracts;

// Usage: dotnet run --project tools/FCommerce.DemoSeeder [-- <host url> <integration api key>]
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5180";
var apiKey = args.Length > 1 ? args[1] : "dev-integration-key";

using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

await SendAsync("/integration/v1/catalog/categories", new ImportCategories(DemoCatalog.Categories), DemoCatalog.Categories.Count);

Console.Write("uploading images ");
var products = new List<ProductData>();
foreach (var product in DemoCatalog.Products)
{
    var ids = new List<string>();
    foreach (var image in DemoImages.For(product.Sku, product.CategoryCode ?? string.Empty))
    {
        ids.Add(await UploadAsync(image));
    }

    products.Add(product with { ImageIds = ids });
    Console.Write('.');
}

Console.WriteLine();
await SendAsync("/integration/v1/catalog/products", new ImportProducts(products), products.Count);
return 0;

async Task<string> UploadAsync(byte[] image)
{
    using var content = new ByteArrayContent(image);
    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
    using var response = await client.PostAsync("/integration/v1/media/images", content);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"image upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Environment.Exit(1);
    }

    return (await response.Content.ReadFromJsonAsync<UploadedImage>())!.Id;
}

async Task SendAsync<T>(string path, T body, int count)
{
    using var response = await client.PutAsJsonAsync(path, body);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Environment.Exit(1);
    }

    var report = await response.Content.ReadFromJsonAsync<ImportReport>();
    Console.WriteLine($"{path}: sent {count}, created {report!.Created}, updated {report.Updated}, unchanged {report.Unchanged}, stale {report.Stale}, rejected {report.Rejected.Count}");
    foreach (var rejected in report.Rejected)
    {
        Console.WriteLine($"  #{rejected.Index} {rejected.Key}: {string.Join("; ", rejected.Errors.Select(e => $"{e.Field} {e.Code}"))}");
    }
}
