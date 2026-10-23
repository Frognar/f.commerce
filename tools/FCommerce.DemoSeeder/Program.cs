using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.DemoSeeder;

// Usage: dotnet run --project tools/FCommerce.DemoSeeder [-- <host url> <integration api key>]
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5180";
var apiKey = args.Length > 1 ? args[1] : "dev-integration-key";

using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

await SendAsync("/integration/v1/catalog/categories", new ImportCategories(DemoCatalog.Categories), DemoCatalog.Categories.Count);
await SendAsync("/integration/v1/catalog/products", new ImportProducts(DemoCatalog.Products), DemoCatalog.Products.Count);
return 0;

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
