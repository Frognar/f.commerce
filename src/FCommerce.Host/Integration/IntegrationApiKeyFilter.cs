using System.Security.Cryptography;
using System.Text;

namespace FCommerce.Host.Integration;

/// <summary>
/// Guards the ERP integration API with a shared key from configuration ("Integration:ApiKey", header X-Api-Key).
/// Without a configured key the API is closed. Interim until per-client API keys exist.
/// </summary>
internal sealed class IntegrationApiKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["Integration:ApiKey"];
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        return string.IsNullOrEmpty(expected) || !FixedTimeEquals(expected, provided)
            ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "integration.unauthorized", detail: $"A valid {HeaderName} header is required.")
            : await next(context);
    }

    private static bool FixedTimeEquals(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
}
