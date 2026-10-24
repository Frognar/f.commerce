using System.Globalization;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;

namespace FCommerce.DemoSeeder;

internal sealed record DemoLine(string Sku, string Name, string Unit, decimal Quantity, decimal UnitPrice)
{
    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public DocumentLineData Data(int number) => new(number, Sku, Name, Quantity, Unit, UnitPrice, Net);
}

/// <summary>A document's metadata for the ERP import and what its PDF shows.</summary>
/// <remarks>Every line is taxed at 23% VAT.</remarks>
internal sealed record DemoDocument(DocumentData Data, CustomerData Customer, IReadOnlyList<DemoLine> Lines)
{
    public const decimal VatRate = 0.23m;
}

/// <summary>
/// Invoices with delivery notes, a correction and a proforma for the customers that use the platform. Dates are
/// relative to the day of seeding, so the demo always has documents that are due, overdue and paid; the version is
/// that day, so seeding again on a later day refreshes them.
/// </summary>
internal static class DemoDocuments
{
    private const int InvoicesPerCustomer = 8;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<DemoDocument> For(IReadOnlyList<CustomerData> customers, IReadOnlyList<ProductData> products, DateOnly today)
    {
        var documents = new List<DemoDocument>();
        foreach (var (customer, index) in customers.Where(c => c.Contacts?.Any(p => p.PortalAccess) == true).Select((c, i) => (c, i)))
        {
            var term = customer.PaymentTermDays ?? 14;
            var series = (index + 1) * 100;
            for (var i = 0; i < InvoicesPerCustomer; i++)
            {
                var issued = today.AddDays(-((i * 11) + 2 + (index * 3)));
                var lines = Lines(products, $"{customer.Code}/{i}");
                var gross = Gross(lines);
                var due = issued.AddDays(term);
                var paid = i switch
                {
                    3 => 0m, // overdue
                    4 => Math.Round(gross / 2, 2), // partly paid, overdue
                    _ when due < today => gross,
                    _ => 0m, // not yet due
                };
                var number = series + InvoicesPerCustomer - i;
                var order = $"ZAM/{Year(issued)}/{number}";
                documents.Add(Invoice($"DEMO-FV-{customer.Code}-{i}", customer, "invoice", $"FV/{Year(issued)}/{number}", issued, due, lines, paid, order, today));
                documents.Add(new DemoDocument(
                    new DocumentData($"DEMO-WZ-{customer.Code}-{i}", today.DayNumber, customer.Code, "delivery-note", $"WZ/{Year(issued)}/{number}", issued, issued, OrderNumber: order, Lines: Lines(lines)),
                    customer,
                    lines));

                if (i == 5)
                {
                    // One item of the invoice returned: the correction lowers the amount.
                    var returned = lines[0] with { Quantity = -Math.Max(1, Math.Round(lines[0].Quantity / 5)) };
                    var corrected = issued.AddDays(5);
                    documents.Add(Invoice($"DEMO-FK-{customer.Code}-{i}", customer, "correction-invoice", $"FK/{Year(corrected)}/{number}", corrected, corrected.AddDays(term), [returned], 0m, order, today));
                }
            }

            var proforma = today.AddDays(-1);
            documents.Add(Invoice($"DEMO-PRO-{customer.Code}", customer, "proforma", $"PRO/{Year(proforma)}/{series + 1}", proforma, proforma.AddDays(7), Lines(products, $"{customer.Code}/proforma"), 0m, null, today));
        }

        return documents;
    }

    public static decimal NetTotal(IEnumerable<DemoLine> lines) => lines.Sum(l => l.Net);

    public static decimal Vat(IEnumerable<DemoLine> lines) => Math.Round(NetTotal(lines) * DemoDocument.VatRate, 2, MidpointRounding.AwayFromZero);

    public static decimal Gross(IReadOnlyList<DemoLine> lines) => NetTotal(lines) + Vat(lines);

    private static DemoDocument Invoice(string id, CustomerData customer, string type, string number, DateOnly issued, DateOnly due, IReadOnlyList<DemoLine> lines, decimal paid, string? order, DateOnly today) =>
        new(
            new DocumentData(id, today.DayNumber, customer.Code, type, number, issued, issued, due, "PLN", NetTotal(lines), Gross(lines), paid, order, Lines(lines)),
            customer,
            lines);

    private static IReadOnlyList<DocumentLineData> Lines(IReadOnlyList<DemoLine> lines) => [.. lines.Select((line, index) => line.Data(index + 1))];

    /// <summary>Two to four catalog products in deterministic quantities, at retail prices.</summary>
    private static List<DemoLine> Lines(IReadOnlyList<ProductData> products, string seed)
    {
        var hash = DemoHash.Of(seed);
        return [.. Enumerable.Range(0, 2 + (int)(hash % 3))
            .Select(n => products[(int)(((hash / 7) + ((ulong)n * 37)) % (ulong)products.Count)])
            .DistinctBy(p => p.Sku)
            .Select((product, n) => new DemoLine(
                product.Sku,
                product.Name["pl"],
                product.Unit,
                10 * (1 + (decimal)((hash >> (n * 5)) % 20)),
                DemoPricing.Base(product.Sku)))];
    }

    private static string Year(DateOnly date) => date.Year.ToString(Invariant);
}
