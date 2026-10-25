using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

public sealed class DocumentsTests(HostFixture host)
{
    private const string Password = "customer password 1";

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    private static byte[] Pdf(string text) => System.Text.Encoding.ASCII.GetBytes($"%PDF-1.7\n% {text}\n%%EOF\n");

    /// <summary>A customer with one person (rights from <paramref name="template"/>) who accepted the invitation and is signed in.</summary>
    private async Task<(string Code, Browser Browser)> CustomerUserAsync(string template)
    {
        var code = Unique("K");
        var email = UniqueEmail(template);
        using (var client = host.CreateIntegrationClient())
        {
            var customer = new CustomerData(code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null,
                [new ContactData("C1", "Jan Kowalski", email, null, PortalAccess: true, template)], null);
            using var response = await client.PutAsJsonAsync("/integration/v1/customers", new ImportCustomers([customer]), Token);
            response.EnsureSuccessStatusCode();
        }

        var browser = host.NewBrowser();
        await host.AcceptInvitationAsync(browser, email, Password);
        return (code, browser);
    }

    private async Task<ImportReport> ImportAsync(params DocumentData[] documents)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync("/integration/v1/documents", new ImportDocuments(documents), Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportReport>(Token))!;
    }

    private async Task<HttpStatusCode> UploadAsync(string id, byte[] content, string? apiKey = HostFixture.IntegrationApiKey)
    {
        using var client = host.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var response = await client.PutAsync($"/integration/v1/documents/{Uri.EscapeDataString(id)}/file", body, Token);
        return response.StatusCode;
    }

    /// <summary>Number group separators (non-breaking spaces, possibly encoded) as plain spaces.</summary>
    private static string Spaced(string html) =>
        html.Replace("&#xA0;", " ", StringComparison.Ordinal).Replace("&#x202F;", " ", StringComparison.Ordinal).Replace('\u00a0', ' ').Replace('\u202f', ' ');

    private static DocumentData Invoice(string id, string customer, string number, int dueInDays, decimal paid = 0m) =>
        new(id, 1, customer, "invoice", number, Today.AddDays(-30), Today.AddDays(-30), Today.AddDays(dueInDays), "PLN", 1000m, 1230m, paid, "ZAM/1");

    [Fact]
    public async Task A_customer_sees_its_documents_with_what_is_due_and_downloads_the_pdf()
    {
        var (code, browser) = await CustomerUserAsync("accounting");
        using var _ = browser;
        var overdue = Unique("FV") + "/1"; // ERP ids may contain "/"
        var pdf = Pdf(overdue);

        var report = await ImportAsync(
            Invoice(overdue, code, "FV/2026/10/001", dueInDays: -10, paid: 230m),
            Invoice(Unique("FV"), code, "FV/2026/10/002", dueInDays: 14),
            Invoice(Unique("FV"), code, "FV/2026/10/003", dueInDays: -10, paid: 1230m),
            new DocumentData(Unique("WZ"), 1, code, "delivery-note", "WZ/2026/10/001", Today.AddDays(-31)));
        var stored = await UploadAsync(overdue, pdf);
        var page = await browser.GetAsync("/pl/company/documents");
        var overdueOnly = await browser.GetAsync("/pl/company/documents?status=overdue");
        var company = await browser.GetAsync("/pl/company");
        using var download = await browser.Client.GetAsync($"/pl/company/documents?handler=File&id={Uri.EscapeDataString(overdue)}", Token);

        Assert.Equal((4, 0), (report.Created, report.Rejected.Count));
        Assert.Equal(HttpStatusCode.NoContent, stored);
        Assert.Contains("Dokumentów: 4", page.Html, StringComparison.Ordinal);
        Assert.Contains("2 230,00 PLN", Spaced(page.Html), StringComparison.Ordinal); // 1000 + 1230 due
        Assert.Contains("w tym po terminie: 1 000,00 PLN (1 dok.)", Spaced(page.Html), StringComparison.Ordinal);
        Assert.Contains("Dokumentów: 1", overdueOnly.Html, StringComparison.Ordinal);
        Assert.Contains("FV/2026/10/001", overdueOnly.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"/pl/company/documents\"", company.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("FV-2026-10-001.pdf", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(pdf, await download.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task Documents_of_another_company_or_without_the_right_stay_closed()
    {
        var (_, accountant) = await CustomerUserAsync("accounting");
        var (otherCode, buyer) = await CustomerUserAsync("buyer");
        using var a = accountant;
        using var b = buyer;
        var foreign = Unique("FV");
        await ImportAsync(Invoice(foreign, otherCode, "FV/OBCA/1", dueInDays: 5));
        await UploadAsync(foreign, Pdf(foreign));

        var stolen = await accountant.GetAsync($"/pl/company/documents?handler=File&id={foreign}");
        var list = await accountant.GetAsync("/pl/company/documents");
        var buyerList = await buyer.GetAsync("/pl/company/documents");
        var buyerFile = await buyer.GetAsync($"/pl/company/documents?handler=File&id={foreign}");
        var buyerCompany = await buyer.GetAsync("/pl/company");
        var anonymous = await host.NewBrowser().GetAsync("/pl/company/documents");

        Assert.Equal(HttpStatusCode.NotFound, stolen.Status);
        Assert.DoesNotContain("FV/OBCA/1", list.Html, StringComparison.Ordinal);
        Assert.Equal("/pl/access-denied", buyerList.Location);
        Assert.Equal("/pl/access-denied", buyerFile.Location);
        Assert.DoesNotContain("/pl/company/documents", buyerCompany.Html, StringComparison.Ordinal);
        Assert.StartsWith("/pl/sign-in", anonymous.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_documents_are_published_with_their_orders_and_found_by_id()
    {
        var customer = Unique("K");
        var id = Unique("WZ");
        var note = new DocumentData(id, 1, customer, "delivery-note", $"WZ/{id}", Today, OrderNumber: "ZAM/9", OrderNumbers: ["WWW/2026/000001", "ZAM/9"]);

        await ImportAsync(note);
        await ImportAsync(note);
        await ImportAsync(note with { Version = 2 });
        await ImportAsync(note with { Version = 3, OrderNumbers = ["WWW/2026/000002"] });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (host.Events.DocumentChanges.Count(e => e.Id == id) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The document events were not delivered.");
            await Task.Delay(50, Token);
        }

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetOrderDocuments, IReadOnlyList<DocumentSummary>>>();
        var found = await query.HandleAsync(new GetOrderDocuments(customer, [id, "NONE"]), Token);
        var otherCustomer = await query.HandleAsync(new GetOrderDocuments(Unique("K"), [id]), Token);

        Assert.Equal(
            [(customer, "delivery-note", "ZAM/9|WWW/2026/000001"), (customer, "delivery-note", "ZAM/9|WWW/2026/000002")],
            host.Events.DocumentChanges.Where(e => e.Id == id).Select(e => (e.CustomerCode, e.Type, string.Join("|", e.OrderNumbers))));
        var summary = Assert.Single(found);
        Assert.Equal(("ZAM/9, WWW/2026/000002", "ZAM/9|WWW/2026/000002"), (summary.OrderNumber, string.Join("|", summary.OrderNumbers!)));
        Assert.Empty(otherCustomer);
    }

    [Fact]
    public async Task The_integration_api_reports_invalid_documents_and_files()
    {
        var code = Unique("K");
        var known = Unique("FV");
        var report = await ImportAsync(
            Invoice(known, code, "FV/1", dueInDays: 5),
            new DocumentData(Unique("FV"), 1, code, "receipt", "X/1", null, Currency: "PLN", Net: 1m, Gross: null));

        var rejected = Assert.Single(report.Rejected);
        Assert.Equal(1, rejected.Index);
        Assert.Equal(["type", "issueDate", "gross"], rejected.Errors.Select(e => e.Field));
        Assert.Equal(HttpStatusCode.NotFound, await UploadAsync(Unique("FV"), Pdf("unknown")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(known, "<html></html>"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, await UploadAsync(known, Pdf("no key"), apiKey: null));
        Assert.Equal(1, (await ImportAsync(Invoice(known, code, "FV/1", dueInDays: 5))).Stale);
    }
}
