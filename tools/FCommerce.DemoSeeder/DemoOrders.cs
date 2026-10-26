using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Documents.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Plays the ERP for orders placed in the demo: reads the order feed, confirms the orders under an ERP number, then
/// issues a delivery note (naming the ERP number) and an invoice (naming the platform number) with their PDFs. Each
/// step can run alone, so the demo can show an order accepted, in progress and completed. Running it again changes
/// nothing for orders already handled.
/// </summary>
internal static class DemoOrders
{
    public static readonly string[] Steps = ["accept", "deliver", "invoice"];

    public static async Task<int> RunAsync(HttpClient client, string step)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var orders = await OpenOrdersAsync(client);
        Console.WriteLine($"orders to handle: {orders.Count}");
        using var fonts = DemoFonts.Load();
        foreach (var order in orders)
        {
            var erpNumber = ErpNumber(order);
            if (step is "all" or "accept")
            {
                await AcceptAsync(client, order, erpNumber);
            }

            var customer = DemoCustomers.All.FirstOrDefault(c => c.Code == order.CustomerCode);
            if (customer is null)
            {
                Console.WriteLine($"  {order.Number}: customer {order.CustomerCode} is not a demo customer, no documents");
                continue;
            }

            var lines = order.Lines.Select(l => new DemoLine(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitNet)).ToList();
            if (step is "all" or "deliver")
            {
                var note = new DemoDocument(
                    new DocumentData($"DEMO-WZ-{erpNumber}", today.DayNumber, customer.Code, "delivery-note", $"WZ/{erpNumber}", today, today,
                        OrderNumber: erpNumber, Lines: [.. lines.Select((line, i) => line.Data(i + 1))]),
                    customer,
                    lines);
                await DocumentAsync(client, note, fonts);
            }

            if (step is "all" or "invoice")
            {
                var invoice = new DemoDocument(
                    new DocumentData($"DEMO-FV-{erpNumber}", today.DayNumber, customer.Code, "invoice", $"FV/{erpNumber}", today, today, today.AddDays(customer.PaymentTermDays ?? 14),
                        "PLN", DemoDocuments.NetTotal(lines), DemoDocuments.Gross(lines), 0m, order.Number, [.. lines.Select((line, i) => line.Data(i + 1))]),
                    customer,
                    lines);
                await DocumentAsync(client, invoice, fonts);
            }

            Console.WriteLine($"  {order.Number} → {erpNumber}: {step}");
        }

        return 0;
    }

    /// <summary>The feed catches up with a new order a moment after it was placed: waits until the ERP can see it.</summary>
    public static async Task WaitForFeedAsync(HttpClient client, string number)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(await OpenOrdersAsync(client)).Any(order => order.Number == number))
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.Error.WriteLine($"{number} did not reach the order feed.");
                Environment.Exit(1);
            }

            await Task.Delay(500);
        }
    }

    /// <summary>Placed orders from the whole feed that were not cancelled or rejected since (handling one again changes nothing).</summary>
    private static async Task<IReadOnlyList<OrderExport>> OpenOrdersAsync(HttpClient client)
    {
        var latest = new Dictionary<Guid, OrderFeedEntry>();
        var after = 0L;
        while (true)
        {
            var page = (await client.GetFromJsonAsync<OrderFeedPage>($"/integration/v1/orders/feed?after={after}&limit={GetOrderFeed.MaxLimit}"))!;
            foreach (var entry in page.Entries)
            {
                latest[entry.Order.Id] = entry;
            }

            if (page.Entries.Count == 0)
            {
                break;
            }

            after = page.LastPosition;
        }

        return [.. latest.Values
            .Where(e => e.Type == "placed" && e.Order.Status is not (OrderStatuses.Cancelled or OrderStatuses.Rejected))
            .Select(e => e.Order)];
    }

    /// <summary>"WWW/2026/000042" → "ZAM/2026/5042": the ERP's own number of the order.</summary>
    private static string ErpNumber(OrderExport order) =>
        order.Number.Split('/') is [_, var year, var sequence] && long.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? $"ZAM/{year}/{5000 + n}"
            : $"ZAM/{order.Id:N}"[..20];

    private static async Task AcceptAsync(HttpClient client, OrderExport order, string erpNumber)
    {
        using var response = await client.PutAsJsonAsync($"/integration/v1/orders/{Uri.EscapeDataString(order.Number)}/acceptance", new OrderAcceptanceData(erpNumber));
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.Conflict))
        {
            Console.Error.WriteLine($"acceptance of {order.Number}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            Environment.Exit(1);
        }
    }

    private static async Task DocumentAsync(HttpClient client, DemoDocument document, DemoFonts fonts)
    {
        using (var response = await client.PutAsJsonAsync("/integration/v1/documents", new ImportDocuments([document.Data])))
        {
            var report = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ImportReport>() : null;
            if (report is null || report.Rejected.Count > 0)
            {
                Console.Error.WriteLine($"document {document.Data.Id}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                Environment.Exit(1);
            }
        }

        using var content = new ByteArrayContent(DemoPdf.Render(document, fonts));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var file = await client.PutAsync($"/integration/v1/documents/{Uri.EscapeDataString(document.Data.Id)}/file", content);
        if (!file.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"document file {document.Data.Id}: {(int)file.StatusCode} {await file.Content.ReadAsStringAsync()}");
            Environment.Exit(1);
        }
    }
}
