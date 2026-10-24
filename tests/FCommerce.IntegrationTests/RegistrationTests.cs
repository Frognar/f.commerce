using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Customers.Contracts;

namespace FCommerce.IntegrationTests;

/// <summary>A separate host with self-registration switched on and an operator mailbox set.</summary>
public sealed partial class RegistrationHostFixture : HostFixture
{
    public const string OperatorMailbox = "operators@seller.test";
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool enabled;

    /// <summary>A browser of the administrator; the first call switches self-registration on.</summary>
    public async Task<Browser> RegistrationAdminAsync()
    {
        var admin = await AdminAsync();
        await gate.WaitAsync();
        try
        {
            if (!enabled)
            {
                var version = VersionField().Match((await admin.GetAsync("/pl/admin/settings")).Html).Groups[1].Value;
                List<KeyValuePair<string, string>> form =
                [
                    new("Form.Version", version),
                    new("Form.CatalogVisibility", "Public"),
                    new("Form.StoreName", "f.commerce"),
                    new("Form.AccentColor", "#1f5eff"),
                    new("Form.OperatorNotifications", OperatorMailbox),
                    new("Form.DefaultContactTemplate", "buyer"),
                    .. new[] { "customer-accounts", "multi-customer-accounts", "customer-admin-invitations", "erp-contact-invitations", "documents", "self-registration", "public-prices", "stock-levels" }
                        .Select(f => new KeyValuePair<string, string>("Form.EnabledFeatures", f)),
                ];
                var (_, html, _) = await admin.SubmitAsync("/pl/admin/settings", form);
                Assert.Contains("Zapisano zmiany.", html, StringComparison.Ordinal);
                enabled = true;
            }
        }
        finally
        {
            gate.Release();
        }

        return admin;
    }

    [GeneratedRegex("name=\"Form.Version\" value=\"(\\d+)\"")]
    private static partial Regex VersionField();
}

public sealed partial class RegistrationTests(RegistrationHostFixture host, HostFixture shared) : IClassFixture<RegistrationHostFixture>
{
    private const string ValidNip = "5261040828";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    private static Dictionary<string, string> Form(string email, string company, string taxId = ValidNip, bool terms = true) =>
        new()
        {
            ["Form.CompanyName"] = company,
            ["Form.TaxId"] = taxId,
            ["Form.Street"] = "ul. Długa 1",
            ["Form.PostalCode"] = "00-950",
            ["Form.City"] = "Warszawa",
            ["Form.Country"] = "PL",
            ["Form.PersonName"] = "Jan Kowalski",
            ["Form.Email"] = email,
            ["Form.AcceptedTerms"] = terms ? "true" : "false",
        };

    private async Task<string> SubmitAsync(string email, string company)
    {
        using var visitor = host.NewBrowser();
        var (_, html, _) = await visitor.SubmitAsync("/pl/register", Form(email, company));
        Assert.Contains("Wniosek został przyjęty", html, StringComparison.Ordinal);
        var notice = await host.Mail.WaitForAsync(RegistrationHostFixture.OperatorMailbox, m => m.Text.Contains(company, StringComparison.Ordinal));
        return RequestLink().Match(notice.Text).Groups["id"].Value;
    }

    private async Task ImportCustomerAsync(string code, string taxId = ValidNip)
    {
        using var client = host.CreateIntegrationClient();
        var customer = new CustomerData(code, 1, $"Firma {code}", taxId, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null, null, null);
        using var response = await client.PutAsJsonAsync("/integration/v1/customers", new ImportCustomers([customer]), Token);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Without_the_feature_registration_does_not_exist()
    {
        using var visitor = shared.NewBrowser();
        using var admin = await shared.AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/pl/register")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/pl/admin/registrations")).Status);
        Assert.DoesNotContain("/pl/register", (await visitor.GetAsync("/pl/sign-in")).Html, StringComparison.Ordinal);
        Assert.DoesNotContain("/pl/admin/registrations", (await admin.GetAsync("/pl/admin")).Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_applicant_is_invited_and_acts_for_the_chosen_customer()
    {
        using var admin = await host.RegistrationAdminAsync();
        var code = Unique("K");
        var email = UniqueEmail("applicant");
        var company = $"Nowa Firma {Unique("X")}";
        await ImportCustomerAsync(code);

        var id = await SubmitAsync(email, company);
        var details = await admin.GetAsync($"/pl/admin/registrations/{id}");
        var (_, approved, _) = await admin.SubmitAsync($"/pl/admin/registrations/{id}", new Dictionary<string, string>
        {
            ["CustomerCode"] = code, ["Template"] = "administrator",
        }, $"/pl/admin/registrations/{id}?handler=Approve");
        using var applicant = host.NewBrowser();
        var (_, location) = await host.AcceptInvitationAsync(applicant, email, "applicant password 1");
        var area = await applicant.GetAsync("/pl/company");
        var users = await admin.GetAsync($"/pl/admin/customers/{code}");

        Assert.Contains("href=\"/pl/register\"", (await host.NewBrowser().GetAsync("/pl/sign-in")).Html, StringComparison.Ordinal);
        Assert.Contains(company, details.Html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/pl/admin/customers/{code}\"", details.Html, StringComparison.Ordinal); // tax id match
        Assert.Contains("Wniosek zatwierdzony", approved, StringComparison.Ordinal);
        Assert.Equal("/pl/company", location);
        Assert.Contains($"<h1>Firma {code}</h1>", area.Html, StringComparison.Ordinal);
        Assert.Contains("Rejestracja", users.Html, StringComparison.Ordinal); // membership source
    }

    [Fact]
    public async Task A_rejected_applicant_gets_the_reason_and_the_request_cannot_be_decided_again()
    {
        using var admin = await host.RegistrationAdminAsync();
        var email = UniqueEmail("rejected");
        var id = await SubmitAsync(email, $"Odrzucona {Unique("X")}");

        var (_, rejected, _) = await admin.SubmitAsync($"/pl/admin/registrations/{id}", new Dictionary<string, string> { ["Reason"] = "Sprzedajemy tylko hurtowniom." }, $"/pl/admin/registrations/{id}?handler=Reject");
        var mail = await host.Mail.WaitForAsync(email);
        var (_, again, _) = await admin.SubmitAsync($"/pl/admin/registrations/{id}", new Dictionary<string, string> { ["Reason"] = "Ponownie" }, $"/pl/admin/registrations/{id}?handler=Reject");

        Assert.Contains("Wniosek odrzucony.", rejected, StringComparison.Ordinal);
        Assert.Contains("Powód: Sprzedajemy tylko hurtowniom.", mail.Text, StringComparison.Ordinal);
        Assert.Contains("Sprzedajemy tylko hurtowniom.", again, StringComparison.Ordinal); // shown as the decision
        Assert.DoesNotContain("Wniosek odrzucony.", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_forms_are_explained_and_bots_are_ignored()
    {
        using var _ = await host.RegistrationAdminAsync();
        using var visitor = host.NewBrowser();
        var botCompany = $"Bot {Unique("X")}";

        var (_, invalid, _) = await visitor.SubmitAsync("/pl/register", Form(UniqueEmail("x"), "Firma", taxId: "5261040827", terms: false));
        var bot = Form(UniqueEmail("bot"), botCompany);
        bot["Form.Website"] = "http://spam.example";
        var (_, botAnswer, _) = await visitor.SubmitAsync("/pl/register", bot);
        using var admin = await host.AdminAsync();
        var pending = await admin.GetAsync("/pl/admin/registrations");

        Assert.Contains("Nieprawidłowy NIP", invalid, StringComparison.Ordinal);
        Assert.Contains("Zaakceptuj warunki", invalid, StringComparison.Ordinal);
        Assert.Contains("Wniosek został przyjęty", botAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain(botCompany, pending.Html, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"/admin/registrations/(?<id>[0-9a-f-]{36})")]
    private static partial Regex RequestLink();
}
