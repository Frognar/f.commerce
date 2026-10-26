using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FCommerce.Host.Api;

/// <summary>
/// Authenticates the requests of the public API by the key in the X-Api-Key header. A key acts as its user for its
/// customer: the principal carries what a signed-in session carries (user, security stamp) and the customer, so access
/// control and the modules treat API requests like requests of the same person in the storefront.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICommandHandler<AuthenticateApiKey, Result<ApiKeyIdentity, Error>> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>The id of the key used for the request.</summary>
    public const string KeyClaim = "fc:api-key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[HeaderName].ToString();
        if (key.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await keys.HandleAsync(new AuthenticateApiKey(key), Context.RequestAborted);
        return identity.Match(
            found => AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(Identity(found)), SchemeName)),
            error => AuthenticateResult.Fail(error.Code));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        return Problem(StatusCodes.Status401Unauthorized, "api.unauthorized", $"A valid {HeaderName} header is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problem(StatusCodes.Status403Forbidden, "api.forbidden", "This key may not do that.");

    private Task Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = title }).ExecuteAsync(Context);

    private static ClaimsIdentity Identity(ApiKeyIdentity key) =>
        new(
            [
                new Claim(ClaimTypes.NameIdentifier, key.UserId.ToString()),
                new Claim(CurrentUser.StampClaim, key.SecurityStamp),
                new Claim(CurrentUser.CustomerClaim, key.CustomerCode),
                new Claim(KeyClaim, key.KeyId.ToString()),
            ],
            SchemeName);
}

public static class PublicApiSecurity
{
    /// <summary>The authorization policy of the public API: a valid key (and nothing else, no cookie).</summary>
    public const string Policy = "public-api";

    /// <summary>The rate limit of the public API per key.</summary>
    public const string RateLimit = "public-api";

    public static IServiceCollection AddPublicApiSecurity(this IServiceCollection services, int requestsPerMinutePerKey, int requestsPerMinutePerAddress)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
        services.AddRateLimiter(options =>
        {
            // Every request to the API counts against its address (so wrong keys cannot be tried without limit), then against its key.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                IsApi(http)
                    ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => Window(requestsPerMinutePerAddress))
                    : RateLimitPartition.GetNoLimiter("not-api"));
            options.AddPolicy(RateLimit, http =>
                RateLimitPartition.GetFixedWindowLimiter(KeyPartition(http), _ => Window(requestsPerMinutePerKey)));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (IsApi(context.HttpContext))
                {
                    await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "api.rate-limited", detail: "Too many requests. Try again later.",
                        extensions: new Dictionary<string, object?> { ["code"] = "api.rate-limited" }).ExecuteAsync(context.HttpContext);
                }
            };
        });
        return services;
    }

    private static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api/v1");

    private static FixedWindowRateLimiterOptions Window(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };

    /// <summary>The public identifier of the key sent ("fck_{id}_…"), or the address when there is none.</summary>
    private static string KeyPartition(HttpContext http)
    {
        var parts = http.Request.Headers[ApiKeyAuthenticationHandler.HeaderName].ToString().Split('_', 3);
        return parts is ["fck", { Length: > 0 and <= 32 } id, _] ? $"key:{id}" : $"ip:{http.Connection.RemoteIpAddress}";
    }
}
