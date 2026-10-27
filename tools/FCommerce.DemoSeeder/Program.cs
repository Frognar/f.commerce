using FCommerce.SharedKernel.Imports;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.DemoSeeder;
using FCommerce.Documents.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.Media.Contracts;
using FCommerce.SharedKernel.Contracts;

// Usage: dotnet run --project tools/FCommerce.DemoSeeder [-- <host url> <integration api key> [orders [cancellations|accept|deliver|invoice] | api <customer api key>]]
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5180";
var apiKey = args.Length > 1 ? args[1] : "dev-integration-key";

using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

// "orders": the ERP's part for orders placed in the demo (cancel on request, confirm, deliver, invoice); every step by default.
if (args.Length > 2 && args[2] == "orders")
{
    var step = args.Length > 3 ? args[3] : "all";
    if (step != "all" && !DemoOrders.Steps.Contains(step))
    {
        Console.Error.WriteLine($"Unknown step '{step}': use {string.Join(", ", DemoOrders.Steps)} or nothing for all of them.");
        return 1;
    }

    return await DemoOrders.RunAsync(client, step);
}

// "api": the whole path through the public API with a customer's key (create one in the customer area), the ERP played here.
if (args.Length > 2 && args[2] == "api")
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine($"Usage: ... {baseUrl} <integration api key> api <customer api key> (create the key at {baseUrl}/pl/company/api-keys as a company administrator).");
        return 1;
    }

    return await DemoApi.RunAsync(client, baseUrl, args[3]);
}

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

// Prices and stock; the version is the day of seeding, so seeding again on a later day refreshes them.
var today = DateOnly.FromDateTime(DateTime.Now);
await SendAsync("/integration/v1/pricing/price-lists", new ImportPriceLists(DemoPricing.Lists(today.DayNumber)), 2);
var retail = DemoPricing.RetailPrices(DemoCatalog.Products, today.DayNumber);
await SendAsync($"/integration/v1/pricing/price-lists/{DemoPricing.Retail}/prices", new PriceBatch(retail), retail.Count);
var wholesale = DemoPricing.WholesalePrices(DemoCatalog.Products, today.DayNumber);
await SendAsync($"/integration/v1/pricing/price-lists/{DemoPricing.Wholesale}/prices", new PriceBatch(wholesale), wholesale.Count);
await SendAsync("/integration/v1/inventory/warehouses", new ImportWarehouses(DemoInventory.Warehouses(today.DayNumber)), 3);
foreach (var (warehouse, stock) in DemoInventory.Stock(DemoCatalog.Products, today.DayNumber, today))
{
    await SendAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch(stock), stock.Count);
}

// Customers: contacts with portal access get invitations (Mailpit), so they choose their passwords there.
await SendAsync("/integration/v1/customers", new ImportCustomers(DemoCustomers.All), DemoCustomers.All.Count);

var documents = DemoDocuments.For(DemoCustomers.All, DemoCatalog.Products, today);
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
      Prices:       choose {DemoPricing.Retail} as the default price list in {baseUrl}/pl/admin/settings (the integration
                    API does not change settings); until then visitors are asked to sign in. K-1001 buys from {DemoPricing.Wholesale}.
      Customers:    open the invitation e-mails to choose passwords, then sign in at {baseUrl}/pl/sign-in
      Orders:       place an order as a buyer (company administrators set order limits and delivery addresses in
                    {baseUrl}/pl/company/users), then play the ERP: dotnet run --project tools/FCommerce.DemoSeeder --
                    {baseUrl} <api key> orders [cancellations|accept|deliver|invoice] (every step without one). A buyer only asks
                    to cancel an order; the "cancellations" step plays the ERP confirming those requests.
      Public API:   as a company administrator create a key at {baseUrl}/pl/company/api-keys (for yourself: an administrator also sees documents), then run the whole
                    path through the API: dotnet run --project tools/FCommerce.DemoSeeder -- {baseUrl} <integration key> api <customer key>
                    (catalog → cart → order → the ERP confirms, delivers, invoices → documents). Docs: {baseUrl}/openapi/public-v1.json
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
