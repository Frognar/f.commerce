using System.Globalization;
using SkiaSharp;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Liberation Sans (SIL Open Font License 1.1, see Fonts/LICENSE-LiberationSans.txt), embedded in the tool so the
/// PDFs look the same on every system (the Linux build of SkiaSharp used here sees no system fonts at all).
/// </summary>
internal sealed class DemoFonts(SKTypeface regular, SKTypeface bold) : IDisposable
{
    public SKTypeface Regular => regular;

    public SKTypeface Bold => bold;

    public static DemoFonts Load() => new(Embedded("LiberationSans-Regular.ttf"), Embedded("LiberationSans-Bold.ttf"));

    public void Dispose()
    {
        regular.Dispose();
        bold.Dispose();
    }

    private static SKTypeface Embedded(string name)
    {
        using var stream = typeof(DemoFonts).Assembly.GetManifestResourceStream($"Fonts.{name}")
            ?? throw new InvalidOperationException($"Font resource '{name}' is missing.");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"Font resource '{name}' cannot be read.");
    }
}

/// <summary>Renders a demo document (invoice, correction, delivery note, proforma) as a one-page A4 PDF in Polish.</summary>
internal static class DemoPdf
{
    private const float Width = 595;
    private const float Height = 842;
    private const float Margin = 48;
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly SKColor Ink = new(0x1F, 0x23, 0x28);
    private static readonly SKColor Muted = new(0x6B, 0x72, 0x80);
    private static readonly SKColor Rule = new(0xD5, 0xD9, 0xE0);
    private static readonly SKColor Accent = new(0x1F, 0x5E, 0xFF);

    private static readonly (string Name, string Address, string TaxId) Seller =
        ("f.commerce Hurtownia Demo Sp. z o.o.", "ul. Przemysłowa 12, 61-001 Poznań", "5250000104");

    public static byte[] Render(DemoDocument document, DemoFonts fonts)
    {
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = document.Data.Number, Creator = "FCommerce.DemoSeeder" }))
        {
            var canvas = pdf.BeginPage(Width, Height);
            Draw(canvas, document, fonts);
            pdf.EndPage();
            pdf.Close();
        }

        return stream.ToArray();
    }

    private static void Draw(SKCanvas canvas, DemoDocument document, DemoFonts fonts)
    {
        var data = document.Data;
        var priced = data.Type != "delivery-note";
        using var regular = new SKFont(fonts.Regular, 9);
        using var small = new SKFont(fonts.Regular, 7.5f);
        using var bold = new SKFont(fonts.Bold, 9);
        using var title = new SKFont(fonts.Bold, 18);
        using var ink = new SKPaint { Color = Ink, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        using var accent = new SKPaint { Color = Accent, IsAntialias = true };
        using var rule = new SKPaint { Color = Rule, StrokeWidth = 0.75f, IsAntialias = true };

        canvas.DrawRect(0, 0, Width, 6, accent);
        Text(canvas, "f.commerce", Margin, 48, title, accent);
        Text(canvas, Title(data.Type), Width - Margin, 40, bold, muted, SKTextAlign.Right);
        Text(canvas, data.Number, Width - Margin, 60, title, ink, SKTextAlign.Right);

        var y = 100f;
        Party(canvas, "Sprzedawca", Seller.Name, Seller.Address, Seller.TaxId, Margin, y, regular, bold, muted, ink);
        var customer = document.Customer;
        var address = customer.BillingAddress;
        Party(canvas, "Nabywca", customer.Name, $"{address.Street}, {address.PostalCode} {address.City}", customer.TaxId, Width / 2, y, regular, bold, muted, ink);

        y = 180;
        var facts = new List<(string, string)> { ("Data wystawienia", Date(data.IssueDate.GetValueOrDefault())) };
        if (data.SaleDate is { } sold)
        {
            facts.Add((data.Type == "delivery-note" ? "Data wydania" : "Data sprzedaży", Date(sold)));
        }

        if (priced && data.DueDate is { } due)
        {
            facts.Add(("Termin płatności", Date(due)));
        }

        if (data.OrderNumber is { } order)
        {
            facts.Add(("Zamówienie", order));
        }

        foreach (var (label, value) in facts)
        {
            Text(canvas, label, Margin, y, regular, muted);
            Text(canvas, value, Margin + 110, y, regular, ink);
            y += 14;
        }

        // Lines
        y += 16;
        var columns = priced
            ? new[] { ("Lp.", Margin, SKTextAlign.Left), ("Nazwa", Margin + 24, SKTextAlign.Left), ("Ilość", 360f, SKTextAlign.Right), ("J.m.", 368f, SKTextAlign.Left), ("Cena netto", 470f, SKTextAlign.Right), ("Wartość netto", Width - Margin, SKTextAlign.Right) }
            : new[] { ("Lp.", Margin, SKTextAlign.Left), ("Nazwa", Margin + 24, SKTextAlign.Left), ("Ilość", 470f, SKTextAlign.Right), ("J.m.", 478f, SKTextAlign.Left) };
        foreach (var (label, x, align) in columns)
        {
            Text(canvas, label, x, y, small, muted, align);
        }

        y += 6;
        canvas.DrawLine(Margin, y, Width - Margin, y, rule);
        foreach (var (line, index) in document.Lines.Select((l, i) => (l, i)))
        {
            y += 16;
            string[] values = priced
                ? [$"{index + 1}", Fit(line.Name, regular, 280), Number(line.Quantity, "0.##"), line.Unit, Number(line.UnitPrice, "N2"), Number(line.Net, "N2")]
                : [$"{index + 1}", Fit(line.Name, regular, 380), Number(line.Quantity, "0.##"), line.Unit];
            for (var c = 0; c < columns.Length; c++)
            {
                Text(canvas, values[c], columns[c].Item2, y, regular, ink, columns[c].Item3);
            }

            canvas.DrawLine(Margin, y + 6, Width - Margin, y + 6, rule);
        }

        if (priced)
        {
            y += 30;
            var net = DemoDocuments.NetTotal(document.Lines);
            var vat = DemoDocuments.Vat(document.Lines);
            var gross = net + vat;
            var paid = data.Paid ?? 0m;
            (string Label, decimal Amount, bool Strong)[] totals =
            [
                ("Razem netto", net, false),
                ("VAT 23%", vat, false),
                ("Razem brutto", gross, true),
                ("Zapłacono", paid, false),
                ("Do zapłaty", Math.Max(gross - paid, 0m), true),
            ];
            foreach (var (label, amount, strong) in totals)
            {
                Text(canvas, label, 400, y, strong ? bold : regular, strong ? ink : muted, SKTextAlign.Right);
                Text(canvas, $"{Number(amount, "N2")} {data.Currency}", Width - Margin, y, strong ? bold : regular, ink, SKTextAlign.Right);
                y += 16;
            }
        }

        Text(canvas, "Dokument demonstracyjny wygenerowany przez FCommerce.DemoSeeder — nie jest dokumentem księgowym.", Margin, Height - 36, small, muted);
    }

    private static void Party(SKCanvas canvas, string role, string name, string address, string? taxId, float x, float y, SKFont regular, SKFont bold, SKPaint muted, SKPaint ink)
    {
        Text(canvas, role, x, y, regular, muted);
        Text(canvas, name, x, y + 16, bold, ink);
        Text(canvas, address, x, y + 30, regular, ink);
        if (taxId is not null)
        {
            Text(canvas, $"NIP {taxId}", x, y + 44, regular, ink);
        }
    }

    private static string Title(string type) =>
        type switch
        {
            "correction-invoice" => "FAKTURA KORYGUJĄCA",
            "delivery-note" => "WYDANIE ZEWNĘTRZNE (WZ)",
            "proforma" => "FAKTURA PROFORMA",
            _ => "FAKTURA VAT",
        };

    /// <summary>Shortens text that does not fit the column.</summary>
    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width)
        {
            return text;
        }

        var shorter = text;
        while (shorter.Length > 1 && font.MeasureText(shorter + "…") > width)
        {
            shorter = shorter[..^1];
        }

        return shorter.TrimEnd() + "…";
    }

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", Polish);

    private static string Number(decimal value, string format) => value.ToString(format, Polish);

    private static void Text(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint, SKTextAlign align = SKTextAlign.Left) =>
        canvas.DrawText(text, x, y, align, font, paint);
}
