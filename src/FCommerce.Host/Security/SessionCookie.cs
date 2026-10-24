using System.Globalization;
using System.Security.Claims;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FCommerce.Host.Security;

/// <summary>The session cookie: what it holds, how it is validated on every request and where it redirects.</summary>
internal static class SessionCookie
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan RememberMeLifetime = TimeSpan.FromDays(30);

    public static void Configure(CookieAuthenticationOptions options, bool requireHttps)
    {
        options.Cookie.Name = "fc.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = Lifetime;
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = ValidateAsync;
        options.Events.OnRedirectToLogin = context => Redirect(context, "sign-in", withReturnUrl: true);
        options.Events.OnRedirectToAccessDenied = context => Redirect(context, "access-denied", withReturnUrl: false);
    }

    /// <summary>Signs the user in; the security stamp in the cookie ties the session to the account's current credentials.</summary>
    public static Task SignInAsync(HttpContext http, SignedIn signedIn, bool remember)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, signedIn.UserId.ToString()), new Claim(CurrentUser.StampClaim, signedIn.SecurityStamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = remember, ExpiresUtc = remember ? DateTimeOffset.UtcNow + RememberMeLifetime : null });
    }

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    /// <summary>Ends the session when the account is disabled or its stamp changed (password change, revoked rights).</summary>
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var valid = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && principal?.FindFirstValue(CurrentUser.StampClaim) is { } stamp
            && await IsValidAsync(context.HttpContext, userId, stamp);

        if (!valid)
        {
            context.RejectPrincipal();
            await SignOutAsync(context.HttpContext);
        }
    }

    private static async Task<bool> IsValidAsync(HttpContext http, Guid userId, string stamp)
    {
        var sessions = http.RequestServices.GetRequiredService<IQueryHandler<GetSession, Result<SessionView, Error>>>();
        var session = (await sessions.HandleAsync(new GetSession(userId, stamp), http.RequestAborted)).ToMaybe();
        http.Items[CurrentUser.SessionItem] = session;
        return session.Match(_ => true, () => false);
    }

    private static Task Redirect(RedirectContext<CookieAuthenticationOptions> context, string page, bool withReturnUrl)
    {
        var request = context.Request;
        var target = $"/{CultureInfo.CurrentUICulture.Name}/{page}";
        if (withReturnUrl)
        {
            target += $"?returnUrl={Uri.EscapeDataString(request.PathBase + request.Path + request.QueryString)}";
        }

        if (request.Headers["HX-Request"] == "true")
        {
            context.Response.Headers["HX-Redirect"] = target; // htmx: navigate the whole page
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            context.Response.Redirect(target);
        }

        return Task.CompletedTask;
    }
}
