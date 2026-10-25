using System.Net;
using System.Net.Http.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

public sealed class MembershipsTests(HostFixture host)
{
    private const string Password = "customer password 1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    private static CustomerData Customer(string code, string name, long version = 1, params ContactData[] contacts) =>
        new(
            code,
            version,
            name,
            null,
            new AddressData("ul. Piotrkowska 100", "90-001", "Łódź", "PL"),
            null,
            contacts,
            new SalespersonData("Anna Handlowiec", "anna@seller.test", null),
            Active: true);

    private static ContactData Portal(string email, string? template = null, string id = "C1") =>
        new(id, "Jan Kowalski", email, null, PortalAccess: true, template);

    private async Task ImportAsync(params CustomerData[] customers)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync("/integration/v1/customers", new ImportCustomers(customers), Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>A customer whose ERP contact accepted the invitation, signed in (the browser is theirs).</summary>
    private async Task<(string Code, Browser Browser)> CustomerUserAsync(string email, string template = "administrator", string? name = null)
    {
        var code = Unique("K");
        await ImportAsync(Customer(code, name ?? $"Firma {code}", 1, Portal(email, template)));
        var browser = host.NewBrowser();
        var (_, location) = await host.AcceptInvitationAsync(browser, email, Password);
        Assert.Equal("/pl/company", location);
        return (code, browser);
    }

    private static async Task<string> UserIdAsync(Browser browser, string email)
    {
        var html = (await browser.GetAsync("/pl/company/users")).Html;
        var at = html.IndexOf(email, StringComparison.Ordinal);
        var marker = "form=\"member-"; // the row's inputs belong to its change form, after the e-mail cell
        var start = html.IndexOf(marker, at, StringComparison.Ordinal) + marker.Length;
        return Guid.ParseExact(html.Substring(start, 32), "N").ToString();
    }

    private static Dictionary<string, string> Rights(bool canOrder = true, string scope = "own", bool documents = false, bool manageUsers = false) =>
        new()
        {
            ["UserForm.CanOrder"] = canOrder ? "true" : "false",
            ["UserForm.OrderScope"] = scope,
            ["UserForm.CanViewDocuments"] = documents ? "true" : "false",
            ["UserForm.CanManageUsers"] = manageUsers ? "true" : "false",
        };

    [Fact]
    public async Task An_erp_contact_with_portal_access_is_invited_and_lands_in_the_company_area()
    {
        var email = UniqueEmail("erp");
        var name = $"Hurtownia {Unique("X")}";

        var (_, browser) = await CustomerUserAsync(email, name: name);
        using var _ = browser;
        var invitation = await host.Mail.WaitForAsync(email);
        var company = await browser.GetAsync("/pl/company");

        Assert.Contains(name, invitation.Text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, company.Status);
        Assert.Contains($"<h1>{name}</h1>", company.Html, StringComparison.Ordinal);
        Assert.Contains("Użytkownicy firmy", company.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_portal_access_in_the_erp_revokes_only_erp_access()
    {
        var email = UniqueEmail("erp");
        var colleague = UniqueEmail("manual");
        var (code, admin) = await CustomerUserAsync(email);
        using var _ = admin;
        await admin.SubmitAsync("/pl/company/users", new Dictionary<string, string>
        {
            ["UserForm.Email"] = colleague, ["UserForm.Name"] = "Ewa Nowak", ["UserForm.Culture"] = "pl", ["UserForm.Template"] = "administrator",
        }, "/pl/company/users?handler=Invite");

        await ImportAsync(Customer(code, $"Firma {code}", 2));
        await WaitUntilAsync(async () => (await admin.GetAsync("/pl/company")).Html.Contains("nie jest powiązane", StringComparison.Ordinal));

        using var operatorView = await host.AdminAsync();
        var details = (await operatorView.GetAsync($"/pl/admin/customers/{code}")).Html;
        Assert.DoesNotContain(email, details.Split("Dziennik zmian")[0], StringComparison.Ordinal);
        Assert.Contains(colleague, details, StringComparison.Ordinal);
        Assert.Contains("Odebrano dostęp", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_company_administrator_invites_colleagues_who_cannot_manage_users_themselves()
    {
        var colleague = UniqueEmail("buyer");
        var (_, admin) = await CustomerUserAsync(UniqueEmail("admin"));
        using var _ = admin;

        var (_, html, _) = await admin.SubmitAsync("/pl/company/users", new Dictionary<string, string>
        {
            ["UserForm.Email"] = colleague, ["UserForm.Name"] = "Ewa Nowak", ["UserForm.Culture"] = "pl", ["UserForm.Template"] = "buyer",
        }, "/pl/company/users?handler=Invite");
        using var buyer = host.NewBrowser();
        await host.AcceptInvitationAsync(buyer, colleague, Password);
        var users = await buyer.GetAsync("/pl/company/users");
        var company = await buyer.GetAsync("/pl/company");

        Assert.Contains("Zaproszenie wysłane.", html, StringComparison.Ordinal);
        Assert.Equal("/pl/access-denied", users.Location);
        Assert.DoesNotContain("Użytkownicy firmy", company.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_sets_an_order_limit_and_the_delivery_addresses_of_a_colleague()
    {
        var email = UniqueEmail("admin");
        var buyer = UniqueEmail("buyer");
        var code = Unique("K");
        var address = new AddressData("ul. Magazynowa 5", "90-002", "Łódź", "PL");
        await ImportAsync(Customer(code, $"Firma {code}", 1, Portal(email, "administrator"), Portal(buyer, "buyer", "C2")) with
        {
            DeliveryAddresses = [new DeliveryAddressData("MAG", "Magazyn", address, IsDefault: true), new DeliveryAddressData("BUD", "Budowa", address)],
        });
        using var admin = host.NewBrowser();
        await host.AcceptInvitationAsync(admin, email, Password);
        var id = await UserIdAsync(admin, buyer);

        var invalid = await admin.SubmitAsync("/pl/company/users", new Dictionary<string, string>(Rights()) { ["UserForm.OrderLimit"] = "dużo" }, $"/pl/company/users?handler=Change&id={id}");
        var saved = await admin.SubmitAsync(
            "/pl/company/users",
            new Dictionary<string, string>(Rights()) { ["UserForm.OrderLimit"] = "2500,50", ["UserForm.OrderLimitCurrency"] = "PLN", ["UserForm.DeliveryAddressCodes"] = "BUD" },
            $"/pl/company/users?handler=Change&id={id}");
        var page = (await admin.GetAsync("/pl/company/users")).Html;

        Assert.Contains("Limit zamówienia musi być liczbą.", invalid.Html, StringComparison.Ordinal);
        Assert.Contains("Zapisano", saved.Html, StringComparison.Ordinal);
        Assert.Contains("value=\"2500.5\"", page, StringComparison.Ordinal);
        Assert.Contains("<option value=\"BUD\" selected=\"selected\">", page, StringComparison.Ordinal);
        Assert.Contains("<option value=\"MAG\">", page, StringComparison.Ordinal);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var contacts = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetUserContact, Result<UserContact, Error>>>();
        var adminId = Guid.Parse(await UserIdAsync(admin, email));
        Assert.Equal(new UserContact(adminId, email, "Jan Kowalski", "pl"), (await contacts.HandleAsync(new GetUserContact(adminId), Token)).Match(c => c, e => throw new InvalidOperationException(e.Code)));
        Assert.Equal("access.user-not-found", (await contacts.HandleAsync(new GetUserContact(Guid.Parse(id)), Token)).Match(_ => "found", e => e.Code));
    }

    [Fact]
    public async Task The_last_administrator_keeps_the_right_to_manage_users()
    {
        var email = UniqueEmail("admin");
        var (_, admin) = await CustomerUserAsync(email);
        using var _ = admin;
        var id = await UserIdAsync(admin, email);

        var (_, html, _) = await admin.SubmitAsync("/pl/company/users", Rights(manageUsers: false), $"/pl/company/users?handler=Change&id={id}");
        var revoke = await admin.SubmitAsync("/pl/company/users", new Dictionary<string, string>(), $"/pl/company/users?handler=Revoke&id={id}");

        Assert.Contains("co najmniej jedną osobę zarządzającą", html, StringComparison.Ordinal);
        Assert.Contains("co najmniej jedną osobę zarządzającą", revoke.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_person_acting_for_two_companies_switches_between_them()
    {
        var email = UniqueEmail("multi");
        var (first, browser) = await CustomerUserAsync(email, name: $"Pierwsza {Unique("X")}");
        using var _ = browser;
        var second = Unique("K");
        var secondName = $"Druga {Unique("X")}";

        await ImportAsync(Customer(second, secondName, 1, Portal(email, "buyer")));
        var notice = await host.Mail.WaitForAsync(email, m => m.Subject.Contains(secondName, StringComparison.Ordinal));
        await WaitUntilAsync(async () => (await browser.GetAsync("/pl/company")).Html.Contains(secondName, StringComparison.Ordinal));
        var switched = await browser.SubmitAsync("/pl/company", new Dictionary<string, string> { ["code"] = second, ["returnUrl"] = "/pl/company" }, "/pl/company/switch");
        var company = await browser.GetAsync("/pl/company");
        var foreign = await browser.SubmitAsync("/pl/company", new Dictionary<string, string> { ["code"] = Unique("K") }, "/pl/company/switch");

        Assert.Contains("/pl/sign-in", notice.Text, StringComparison.Ordinal);
        Assert.Equal("/pl/company", switched.Location);
        Assert.Contains($"<h1>{secondName}</h1>", company.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Użytkownicy firmy", company.Html, StringComparison.Ordinal); // a buyer there
        Assert.Equal("/pl/access-denied", foreign.Location);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task An_operator_invites_users_from_the_back_office_and_an_inactive_customer_closes_the_area()
    {
        var code = Unique("K");
        var email = UniqueEmail("invited");
        await ImportAsync(Customer(code, $"Firma {code}"));
        using var admin = await host.AdminAsync();

        var (_, html, _) = await admin.SubmitAsync($"/pl/admin/customers/{code}", new Dictionary<string, string>
        {
            ["UserForm.Email"] = email, ["UserForm.Name"] = "Jan Kowalski", ["UserForm.Culture"] = "pl", ["UserForm.Template"] = "administrator",
        }, $"/pl/admin/customers/{code}?handler=Invite");
        using var user = host.NewBrowser();
        var (_, location) = await host.AcceptInvitationAsync(user, email, Password);
        var before = await user.GetAsync("/pl/company");
        await ImportAsync(Customer(code, $"Firma {code}", 2) with { Active = false });
        var after = await user.GetAsync("/pl/company");

        Assert.Contains("Zaproszenie wysłane.", html, StringComparison.Ordinal);
        Assert.Contains("Nadano dostęp", html, StringComparison.Ordinal);
        Assert.Equal("/pl/company", location);
        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.Equal("/pl/access-denied", after.Location);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100, Token);
        }
    }
}
