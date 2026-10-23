using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace FCommerce.Host.Security;

/// <summary>Slows down password guessing and e-mail flooding: form submissions of the authentication pages per client IP.</summary>
public static class RateLimits
{
    public const string Authentication = "authentication";

    public static IServiceCollection AddAuthenticationRateLimits(this IServiceCollection services, int attemptsPerMinute) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Authentication, http =>
                HttpMethods.IsPost(http.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = attemptsPerMinute, Window = TimeSpan.FromMinutes(1) })
                    : RateLimitPartition.GetNoLimiter("read"));
        });
}
