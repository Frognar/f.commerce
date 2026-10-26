using System.Net;
using System.Net.Http.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Api;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>An installation without the public API.</summary>
public sealed class NoPublicApiHostFixture : HostFixture
{
    protected override IWebHostBuilder Configure(IWebHostBuilder host) =>
        Feature.All
            .Where(f => f.Id != Feature.PublicApi)
            .Select((f, index) => (f.Id.Value, index))
            .Aggregate(host, (builder, f) => builder.UseSetting($"Features:Available:{f.index}", f.Value));
}

/// <summary>An installation with a tiny rate limit of the public API.</summary>
public sealed class RateLimitedApiHostFixture : HostFixture
{
    protected override IWebHostBuilder Configure(IWebHostBuilder host) => host.UseSetting("Api:RequestsPerMinute", "3");
}

/// <summary>Keys as credentials: whose requests they are, and when they stop working.</summary>
public sealed class ApiKeyTests(HostFixture host, NoPublicApiHostFixture noApi, RateLimitedApiHostFixture limited)
    : IClassFixture<NoPublicApiHostFixture>, IClassFixture<RateLimitedApiHostFixture>
{
    private readonly ApiSupport support = new(host);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<ApiAccount> MeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ApiAccount>("/api/v1/me", Token))!;

    [Fact]
    public async Task A_key_acts_as_its_user_for_its_customer_with_the_rights_of_the_membership()
    {
        var company = await support.CompanyAsync();
        var (buyerKey, _) = await support.KeyAsync(company, company.BuyerId);
        var (adminKey, _) = await support.KeyAsync(company, company.AdminId);

        using var buyer = support.ApiClient(buyerKey);
        using var admin = support.ApiClient(adminKey);
        var asBuyer = await MeAsync(buyer);
        var asAdmin = await MeAsync(admin);

        Assert.Equal((company.BuyerId, company.BuyerEmail, company.Code, true, "own", false), (asBuyer.UserId, asBuyer.Email, asBuyer.CustomerCode, asBuyer.CanOrder, asBuyer.OrderScope, asBuyer.CanViewDocuments));
        Assert.Equal((company.AdminId, "company", true), (asAdmin.UserId, asAdmin.OrderScope, asAdmin.CanViewDocuments));
        Assert.Equal($"Firma {company.Code}", asBuyer.CustomerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("fck_0123456789ab_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Requests_without_a_valid_key_are_unauthorized(string? key)
    {
        using var client = support.ApiClient(key);

        using var response = await client.GetAsync("/api/v1/me", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("api.unauthorized", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_secret_of_an_existing_key_is_the_same_unauthorized()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company);
        using var client = support.ApiClient(key[..^3] + "AAA");

        using var response = await client.GetAsync("/api/v1/me", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_at_once()
    {
        var company = await support.CompanyAsync();
        var (key, id) = await support.KeyAsync(company);
        using var client = support.ApiClient(key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Token)).StatusCode);

        await support.RevokeKeyAsync(company, id);
        await support.RevokeKeyAsync(company, id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
        Assert.Equal("revoked", Assert.Single(await support.KeysAsync(company)).Status);
    }

    [Fact]
    public async Task A_key_of_another_customer_cannot_be_revoked_through_this_one()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        var (key, id) = await support.KeyAsync(other);
        await using var scope = host.Factory.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<RevokeApiKey, Result<Unit, Error>>>()
            .HandleAsync(new RevokeApiKey(company.AdminId, company.Code, id), Token);

        Assert.Equal("api-key.not-found", result.Match(_ => string.Empty, e => e.Code));
        using var client = support.ApiClient(key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
    }

    [Fact]
    public async Task A_key_with_a_validity_expires()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, validForDays: 30);
        using var client = support.ApiClient(key);

        var summary = Assert.Single(await support.KeysAsync(company));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
        Assert.Equal("active", summary.Status);
        Assert.InRange((summary.ExpiresAt!.Value - summary.CreatedAt).TotalDays, 29.99, 30.01);
    }

    [Fact]
    public async Task A_new_password_ends_the_keys_of_the_user()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        var (adminKey, _) = await support.KeyAsync(company, company.AdminId);
        using var client = support.ApiClient(key);
        using var admin = support.ApiClient(adminKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Token)).StatusCode);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangePassword, Result<SignedIn, Error>>>()
                .HandleAsync(new ChangePassword(company.BuyerId, ApiSupport.Password, "another password 12"), Token)).Match(_ => 0, e => throw new InvalidOperationException(e.Code));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me", Token)).StatusCode);
    }

    [Fact]
    public async Task A_user_who_lost_the_customer_loses_the_key()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, company.BuyerId);
        using var client = support.ApiClient(key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Token)).StatusCode);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICommandHandler<RevokeMembership, Result<Unit, Error>>>()
                .HandleAsync(new RevokeMembership(company.AdminId, company.BuyerId, company.Code), Token)).Match(_ => 0, e => throw new InvalidOperationException(e.Code));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Token)).StatusCode);
    }

    [Fact]
    public async Task A_key_can_be_issued_only_to_a_member_of_the_customer()
    {
        var company = await support.CompanyAsync();
        var other = await support.CompanyAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateApiKey, Result<CreatedApiKey, Error>>>()
            .HandleAsync(new CreateApiKey(company.AdminId, other.BuyerId, company.Code, "Foreign", null), Token);

        Assert.Equal("api-key.no-access", result.Match(_ => string.Empty, e => e.Code));
    }

    [Fact]
    public async Task The_last_use_and_the_creation_are_recorded()
    {
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company, name: "ERP of Firma");
        using var client = support.ApiClient(key);
        Assert.Null(Assert.Single(await support.KeysAsync(company)).LastUsedAt);

        await client.GetAsync("/api/v1/me", Token);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetAuditLog, IReadOnlyList<AuditEntryView>>>()
            .HandleAsync(new GetAuditLog($"customer:{company.Code}"), Token);

        var summary = Assert.Single(await support.KeysAsync(company));
        Assert.NotNull(summary.LastUsedAt);
        Assert.Equal("ERP of Firma", summary.Name);
        Assert.Equal("Piotr Kupiec", summary.OwnerName);
        Assert.Contains(audit, e => e.Action == "api-key.created" && e.Details!.Contains("ERP of Firma", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signed_in_session_cookie_is_not_an_api_credential()
    {
        var company = await support.CompanyAsync();
        using var browser = host.NewBrowser();
        await HostFixture.SignInAsync(browser, company.BuyerEmail, ApiSupport.Password);

        var (status, _, _) = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task The_api_does_not_exist_when_the_feature_is_not_available()
    {
        using var client = noApi.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "fck_0123456789ab_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        using var response = await client.GetAsync("/api/v1/me", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Requests_over_the_limit_of_a_key_wait()
    {
        var support = new ApiSupport(limited);
        var company = await support.CompanyAsync();
        var (key, _) = await support.KeyAsync(company);
        var (other, _) = await support.KeyAsync(company, company.AdminId);
        using var client = support.ApiClient(key);
        using var otherClient = support.ApiClient(other);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
        {
            last?.Dispose();
            last = await client.GetAsync("/api/v1/me", Token);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.True(last!.Headers.RetryAfter is not null);
        Assert.Contains("api.rate-limited", await last.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/v1/me", Token)).StatusCode);
        last.Dispose();
    }
}
