using System.Net;
using System.Net.Http.Headers;

namespace FCommerce.IntegrationTests;

public sealed class WebShellTests(HostFixture host)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-GB,en;q=0.9", "/en")]
    [InlineData("pl-PL", "/pl")]
    [InlineData("de-DE", "/pl")] // unsupported → default culture
    [InlineData("de;q=1, en;q=0.5", "/en")]
    public async Task Root_redirects_to_the_preferred_supported_culture(string acceptLanguage, string expected)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/pl", "lang=\"pl\"", "Zamawiaj szybciej")]
    [InlineData("/en", "lang=\"en\"", "Order faster")]
    public async Task Renders_pages_in_the_culture_from_the_url(string path, string langAttribute, string heading)
    {
        using var client = host.CreateClient();

        var html = await client.GetStringAsync(path, Token);

        Assert.Contains(langAttribute, html, StringComparison.Ordinal);
        Assert.Contains(heading, html, StringComparison.Ordinal);
        Assert.Contains("hreflang=\"pl\" href=\"/pl\"", html, StringComparison.Ordinal);
        Assert.Contains("hreflang=\"en\" href=\"/en\"", html, StringComparison.Ordinal);
        Assert.Contains($"hx-get=\"{path}?handler=Status\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{path}/catalog\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_culture_is_not_found()
    {
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/fr", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_fragment_reaches_the_event_store_and_is_localized()
    {
        using var client = host.CreateClient();

        var fragment = await client.GetStringAsync("/pl?handler=Status", Token);

        Assert.StartsWith("<p class=\"status-ok\">System działa", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_page_is_culture_free()
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/error");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Something went wrong", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/openapi/v1.json")]
    public async Task Operational_endpoints_respond(string path)
    {
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
