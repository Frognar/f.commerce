using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FCommerce.Host.Api;

namespace FCommerce.IntegrationTests;

/// <summary>The pages where administrators (and the seller) manage a company's API keys.</summary>
public sealed partial class ApiKeyPagesTests(HostFixture host, NoPublicApiHostFixture noApi) : IClassFixture<NoPublicApiHostFixture>
{
    private readonly ApiSupport support = new(host);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> NewKey(Guid owner, string name = "Hurtownia ERP", string days = "90") =>
        new() { ["KeyForm.Name"] = name, ["KeyForm.OwnerId"] = owner.ToString(), ["KeyForm.ValidForDays"] = days };

    [Fact]
    public async Task An_administrator_creates_a_key_that_is_shown_once_and_acts_as_the_chosen_user()
    {
        var company = await support.CompanyAsync();
        using var admin = await support.BrowserAsync(company.AdminEmail);

        var (status, created, _) = await admin.SubmitAsync("/pl/company/api-keys", NewKey(company.BuyerId), "/pl/company/api-keys?handler=CreateKey");
        var key = KeyPattern().Match(created).Value;
        var listing = (await admin.GetAsync("/pl/company/api-keys")).Html;
        using var client = support.ApiClient(key);
        var me = await client.GetFromJsonAsync<ApiAccount>("/api/v1/me", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEmpty(key);
        Assert.Contains("Klucz został utworzony", created, StringComparison.Ordinal);
        Assert.Equal(company.BuyerId, me!.UserId);
        Assert.Contains("Hurtownia ERP", listing, StringComparison.Ordinal);
        Assert.Contains("Piotr Kupiec", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(key, listing, StringComparison.Ordinal);
        Assert.Contains($"fck_{key.Split('_')[1]}_…", listing, StringComparison.Ordinal);
        Assert.Contains("Aktywny", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_needs_a_name_and_one_of_the_offered_validity_periods()
    {
        var company = await support.CompanyAsync();
        using var admin = await support.BrowserAsync(company.AdminEmail);

        var unnamed = await admin.SubmitAsync("/pl/company/api-keys", NewKey(company.BuyerId, name: "  "), "/pl/company/api-keys?handler=CreateKey");
        var odd = await admin.SubmitAsync("/pl/company/api-keys", NewKey(company.BuyerId, days: "7"), "/pl/company/api-keys?handler=CreateKey");

        Assert.Contains("Nadaj kluczowi nazwę.", unnamed.Html, StringComparison.Ordinal);
        Assert.Contains("Wybierz jeden z proponowanych okresów ważności.", odd.Html, StringComparison.Ordinal);
        Assert.Empty(await support.KeysAsync(company));
    }

    [Fact]
    public async Task A_key_cannot_be_issued_to_a_user_of_another_company()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        using var admin = await support.BrowserAsync(company.AdminEmail);

        var (_, html, _) = await admin.SubmitAsync("/pl/company/api-keys", NewKey(other.BuyerId), "/pl/company/api-keys?handler=CreateKey");

        Assert.Contains("Ten użytkownik nie ma dostępu do firmy.", html, StringComparison.Ordinal);
        Assert.Empty(await support.KeysAsync(company));
        Assert.Empty(await support.KeysAsync(other));
    }

    [Fact]
    public async Task Users_without_the_right_to_manage_users_have_no_keys_page()
    {
        var company = await support.CompanyAsync();
        using var buyer = await support.BrowserAsync(company.BuyerEmail);

        var page = await buyer.GetAsync("/pl/company/api-keys");
        var company_ = (await buyer.GetAsync("/pl/company")).Html;

        Assert.Equal("/pl/access-denied", page.Location);
        Assert.DoesNotContain("/company/api-keys", company_, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_revokes_a_key_and_only_a_key_of_their_own_company()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        var (key, id) = await support.KeyAsync(company);
        var (foreignKey, foreignId) = await support.KeyAsync(other);
        using var admin = await support.BrowserAsync(company.AdminEmail);

        var foreign = await admin.SubmitAsync("/pl/company/api-keys", new Dictionary<string, string>(), $"/pl/company/api-keys?handler=RevokeKey&id={foreignId}");
        using var foreignClient = support.ApiClient(foreignKey);
        var stillWorks = await foreignClient.GetAsync("/api/v1/me", Token);
        var own = await admin.SubmitAsync("/pl/company/api-keys", new Dictionary<string, string>(), $"/pl/company/api-keys?handler=RevokeKey&id={id}");
        using var client = support.ApiClient(key);
        var revoked = await client.GetAsync("/api/v1/me", Token);

        Assert.Contains("Nie ma takiego klucza.", foreign.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
        Assert.Contains("Klucz został unieważniony.", own.Html, StringComparison.Ordinal);
        Assert.Contains("Unieważniony", own.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task The_seller_sees_and_revokes_the_keys_of_a_customer_and_the_changes_are_logged()
    {
        var company = await support.CompanyAsync();
        var (key, id) = await support.KeyAsync(company, name: "Integracja ERP");
        using var seller = await host.AdminAsync();

        var before = (await seller.GetAsync($"/pl/admin/customers/{company.Code}")).Html;
        var (_, after, _) = await seller.SubmitAsync($"/pl/admin/customers/{company.Code}", new Dictionary<string, string>(), $"/pl/admin/customers/{company.Code}?handler=RevokeKey&id={id}");
        using var client = support.ApiClient(key);

        Assert.Contains("Integracja ERP", before, StringComparison.Ordinal);
        Assert.Contains("Utworzono klucz API", before, StringComparison.Ordinal);
        Assert.Contains("Klucz został unieważniony.", after, StringComparison.Ordinal);
        Assert.Contains("Unieważniono klucz API", after, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
    }

    [Fact]
    public async Task The_keys_page_does_not_exist_when_the_api_is_not_available()
    {
        using var client = noApi.CreateClient();

        using var response = await client.GetAsync("/pl/company/api-keys", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [GeneratedRegex("fck_[0-9a-f]{12}_[A-Za-z0-9_-]{43}")]
    private static partial Regex KeyPattern();
}
