using System.Net;
using System.Net.Http.Json;
using FCommerce.Customers.Contracts;
using FCommerce.SharedKernel.Imports;

namespace FCommerce.IntegrationTests;

public sealed class CustomersTests(HostFixture host)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    /// <summary>A word no other test uses, so search results can be asserted exactly.</summary>
    private static string Marker() => string.Concat(Guid.NewGuid().ToString("N").Where(char.IsAsciiLetter).Take(8).Select(c => (char)('k' + (c - 'a'))));

    private static CustomerData Customer(
        string code,
        long version = 1,
        string name = "Hurtownia Śruba sp. z o.o.",
        string? salespersonEmail = null,
        string city = "Łódź",
        IReadOnlyList<ContactData>? contacts = null,
        bool active = true) =>
        new(
            code,
            version,
            name,
            "526-104-08-28",
            new AddressData("ul. Piotrkowska 100", "90-001", city, "PL"),
            [new DeliveryAddressData("MAG", "Magazyn", new AddressData("ul. Składowa 5", "61131", "Poznań", "PL"), IsDefault: true)],
            contacts ?? [new ContactData("C1", "Jan Kowalski", "jan@sruba.example.pl", "+48 600 100 200", PortalAccess: true, "administrator")],
            new SalespersonData("Anna Handlowiec", salespersonEmail, null),
            PriceListCode: "HURT",
            PaymentTermDays: 30,
            Active: active);

    private async Task<ImportReport> ImportAsync(params CustomerData[] customers)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync("/integration/v1/customers", new ImportCustomers(customers), Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportReport>(Token))!;
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> find)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (find() is { } found)
            {
                return found;
            }

            await Task.Delay(50, Token);
        }

        throw new TimeoutException("The expected event was not published.");
    }

    [Fact]
    public async Task Import_applies_valid_customers_and_reports_invalid_ones_with_paths()
    {
        var code = Unique("K");
        var bad = Customer(Unique("K")) with
        {
            TaxId = "5261040827",
            BillingAddress = new AddressData("ul. Piotrkowska 100", "9-0001", "Łódź", "PL"),
            Contacts = [new ContactData("C1", "Jan", null, null, PortalAccess: true)],
        };

        var report = await ImportAsync(Customer(code), bad);
        var stale = await ImportAsync(Customer(code, name: "Starsza wersja"));

        Assert.Equal((2, 1, 1), (report.Received, report.Created, report.Rejected.Count));
        Assert.Equal(["taxId", "billingAddress.postalCode", "contacts[0].email"], report.Rejected[0].Errors.Select(e => e.Field));
        Assert.Equal(1, stale.Stale);
    }

    [Fact]
    public async Task Changes_are_published_and_portal_contacts_only_when_they_change()
    {
        var code = Unique("K");

        await ImportAsync(Customer(code));
        await ImportAsync(Customer(code, version: 2) with { Blocked = true });
        await ImportAsync(Customer(code, version: 3, contacts: [new ContactData("C1", "Jan Kowalski", "jan@sruba.example.pl", null, PortalAccess: true, "buyer")]));

        await EventuallyAsync(() => host.Events.CustomerChanges.Count(e => e.Code == code) >= 3 ? host.Events : null);
        Assert.Equal(
            [false, true, false],
            host.Events.CustomerChanges.Where(e => e.Code == code).Select(e => e.Blocked));
        var portal = host.Events.PortalContactChanges.Where(e => e.CustomerCode == code).ToList();
        Assert.Equal(["administrator", "buyer"], portal.Select(e => Assert.Single(e.Contacts).AttributeTemplate));
        Assert.Equal("jan@sruba.example.pl", portal[0].Contacts[0].Email);
    }

    [Fact]
    public async Task Back_office_searches_customers_by_folded_name_city_and_tax_number()
    {
        var marker = Marker();
        var code = Unique("K");
        await ImportAsync(Customer(code, name: $"Żelazna {marker} sp. j."));
        using var admin = await host.AdminAsync();

        var byName = await admin.GetAsync($"/pl/admin/customers?q=zelazna+{marker.ToUpperInvariant()}");
        var byCityAndMarker = await admin.GetAsync($"/pl/admin/customers?q=lodz+{marker}");
        var byTaxId = await admin.GetAsync($"/pl/admin/customers?q=526-104-08-28+{marker}");
        var none = await admin.GetAsync($"/pl/admin/customers?q=warszawa+{marker}");

        Assert.Contains($"href=\"/pl/admin/customers/{code}\"", byName.Html, StringComparison.Ordinal);
        Assert.Contains(code, byCityAndMarker.Html, StringComparison.Ordinal);
        Assert.Contains(code, byTaxId.Html, StringComparison.Ordinal);
        Assert.Contains("Znaleziono: 0", none.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_details_show_erp_data_and_unknown_codes_are_not_found()
    {
        var code = Unique("K/01");
        await ImportAsync(Customer(code));
        using var admin = await host.AdminAsync();

        var details = await admin.GetAsync($"/pl/admin/customers/{code}");
        var unknown = await admin.GetAsync($"/pl/admin/customers/{Unique("NONE")}");

        Assert.Equal(HttpStatusCode.OK, details.Status);
        Assert.Contains("Hurtownia Śruba sp. z o.o.", details.Html, StringComparison.Ordinal);
        Assert.Contains("PL5261040828", details.Html, StringComparison.Ordinal);
        Assert.Contains("61-131 Poznań", details.Html, StringComparison.Ordinal);
        Assert.Contains("Administrator firmy", details.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
    }

    [Fact]
    public async Task A_salesperson_sees_and_opens_only_their_own_customers()
    {
        using var admin = await host.AdminAsync();
        var email = UniqueEmail("sales");
        var marker = Marker();
        var own = Unique("K");
        var other = Unique("K");
        await ImportAsync(
            Customer(own, name: $"Własny {marker}", salespersonEmail: email.ToUpperInvariant()),
            Customer(other, name: $"Obcy {marker}", salespersonEmail: "someone-else@test.example.pl"));
        await admin.SubmitAsync(
            "/pl/admin/operators",
            new Dictionary<string, string>
            {
                ["Form.Email"] = email,
                ["Form.Name"] = "Anna Handlowiec",
                ["Form.Culture"] = "pl",
                ["Form.CanDecideRegistrations"] = "true",
                ["Form.CustomerScope"] = "as-salesperson",
            },
            "/pl/admin/operators?handler=Invite");
        using var salesperson = host.NewBrowser();
        await host.AcceptInvitationAsync(salesperson, email, "salesperson password 1");

        var list = await salesperson.GetAsync($"/pl/admin/customers?q={marker}");
        var ownDetails = await salesperson.GetAsync($"/pl/admin/customers/{own}");
        var otherDetails = await salesperson.GetAsync($"/pl/admin/customers/{other}");
        var adminList = await admin.GetAsync($"/pl/admin/customers?q={marker}");

        Assert.Contains(own, list.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(other, list.Html, StringComparison.Ordinal);
        Assert.Contains("Widzisz tylko kontrahentów", list.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ownDetails.Status);
        Assert.Equal("/pl/access-denied", otherDetails.Location);
        Assert.Contains(other, adminList.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_pages_require_the_back_office()
    {
        using var anonymous = host.NewBrowser();

        var (status, _, location) = await anonymous.GetAsync("/pl/admin/customers");

        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.StartsWith("/pl/sign-in?returnUrl=", location, StringComparison.Ordinal);
    }
}
