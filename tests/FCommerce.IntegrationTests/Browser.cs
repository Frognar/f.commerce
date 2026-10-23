using System.Net;
using System.Text.RegularExpressions;

namespace FCommerce.IntegrationTests;

/// <summary>
/// A cookie-keeping client that submits forms like a browser: it reads the antiforgery token from the page that
/// contains the form and posts the fields with it.
/// </summary>
public sealed partial class Browser(HttpClient client) : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public HttpClient Client => client;

    public async Task<(HttpStatusCode Status, string Html, string? Location)> GetAsync(string url)
    {
        using var response = await client.GetAsync(url, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token), response.Headers.Location?.OriginalString);
    }

    /// <summary>Loads <paramref name="pageUrl"/>, then posts <paramref name="fields"/> to <paramref name="action"/> (default: the page).</summary>
    public async Task<(HttpStatusCode Status, string Html, string? Location)> SubmitAsync(string pageUrl, IDictionary<string, string> fields, string? action = null)
    {
        var (_, html, _) = await GetAsync(pageUrl);
        var token = AntiforgeryField().Match(html) is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"No antiforgery token on {pageUrl}.");
        using var content = new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token)));
        using var response = await client.PostAsync(action ?? pageUrl, content, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token), response.Headers.Location?.OriginalString);
    }

    public void Dispose() => client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
