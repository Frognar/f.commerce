using System.Security.Claims;
using FCommerce.Host.Http;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Host.Api;

/// <summary>
/// The public API of the customers' systems: the storefront's functions for the user a key belongs to, as JSON.
/// Endpoints are thin adapters over the module contracts and ask the same access policies as the pages.
/// </summary>
internal static partial class PublicApiEndpoints
{
    public const string Prefix = "/api/v1";

    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix)
            .RequireFeature("public-api")
            .RequireAuthorization(PublicApiSecurity.Policy)
            .RequireRateLimiting(PublicApiSecurity.RateLimit)
            .WithTags("Public API")
            .WithGroupName(OpenApiDocument);

        api.MapGet("/me", GetMeAsync)
            .WithSummary("The user and customer the key acts for, with the rights of the membership.");
        return app;
    }

    public const string OpenApiDocument = "public-v1";

    private static async Task<IResult> GetMeAsync(CurrentUser user, HttpContext http, CancellationToken cancellationToken)
    {
        var session = await user.SessionAsync(cancellationToken);
        var membership = await user.ActiveMembershipAsync(cancellationToken);
        var companies = await user.CompaniesAsync(cancellationToken);
        return await (from s in session from m in membership select (s, m)).Match(
            found => Task.FromResult(Results.Ok(new ApiAccount(
                found.s.UserId,
                found.s.Email,
                found.s.Name,
                found.m.CustomerCode,
                companies.FirstOrNone(c => c.Code == found.m.CustomerCode).Map(c => c.Name).ValueOr(found.m.CustomerCode),
                http.User.FindFirstValue(ApiKeyAuthenticationHandler.KeyClaim) ?? string.Empty,
                found.m.CanOrder,
                found.m.OrderScope,
                found.m.OrderLimit,
                found.m.OrderLimitCurrency,
                found.m.CanViewDocuments,
                found.m.DeliveryAddressCodes))),
            () => Task.FromResult(Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "api.unauthorized")));
    }
}

/// <summary>Who the key acts as (a wire format: absent values are null in the JSON).</summary>
/// <param name="KeyId">The id of the key used (not the key).</param>
/// <param name="OrderScope">"own" or "company": which orders the user sees.</param>
/// <param name="DeliveryAddressCodes">Null: every delivery address of the customer.</param>
[NullableEdge]
public sealed record ApiAccount(
    Guid UserId,
    string Email,
    string Name,
    string CustomerCode,
    string CustomerName,
    string KeyId,
    bool CanOrder,
    string OrderScope,
    decimal? OrderLimit,
    string? OrderLimitCurrency,
    bool CanViewDocuments,
    IReadOnlyList<string>? DeliveryAddressCodes);
