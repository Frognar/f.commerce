using FCommerce.SharedKernel.Imports;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.DemoSeeder;
using FCommerce.Documents.Contracts;
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

// Customers: contacts with portal access get invitations (Mailpit), so they choose their passwords there.
await SendAsync("/integration/v1/customers", new ImportCustomers(DemoCustomers.All), DemoCustomers.All.Count);

var documents = DemoDocuments.For(DemoCustomers.All, DemoCatalog.Products, DateOnly.FromDateTime(DateTime.Now));
await SendAsync("/integration/v1/documents", new ImportDocuments([.. documents.Select(d => d.Data)]), documents.Count);
Console.Write("uploading document files ");
using (var fonts = DemoFonts.Load())
{
    foreach (var document in documents)
    {
        await PutFileAsync(document.Data.Id, DemoPdf.Render(document, fonts));
        Console.Write('.');
    }
}

Console.WriteLine();

Console.WriteLine($"""

    Demo data is in. E-mails (invitations, password resets) arrive in Mailpit: http://localhost:8025
      Back-office:  {baseUrl}/pl/admin as the operator from Access:BootstrapAdminEmail (invited on the host's first start).
                    Invite {DemoCustomers.Salesperson} in Operators with the "salesperson" rights to see only her customers.
      Customers:    open the invitation e-mails to choose passwords, then sign in at {baseUrl}/pl/sign-in
    """);
foreach (var customer in DemoCustomers.All)
{
    var people = customer.Contacts?.Where(c => c.PortalAccess).Select(c => $"{c.Email} ({c.AttributeTemplate})").ToList() ?? [];
    var note = customer.Blocked ? " [blocked in the ERP]" : string.Empty;
    Console.WriteLine($"      {customer.Code} {customer.Name}{note}: {(people.Count == 0 ? "nobody on the platform yet" : string.Join(", ", people))}");
}

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

async Task PutFileAsync(string documentId, byte[] pdf)
{
    using var content = new ByteArrayContent(pdf);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    using var response = await client.PutAsync($"/integration/v1/documents/{Uri.EscapeDataString(documentId)}/file", content);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"document file {documentId}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Environment.Exit(1);
    }
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
