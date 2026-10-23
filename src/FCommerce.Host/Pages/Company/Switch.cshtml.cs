using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>Chooses the customer to act for (company switcher in the header).</summary>
public sealed class SwitchModel(CurrentUser user) : PageModel
{
    public static readonly TimeSpan Remembered = TimeSpan.FromDays(365);

    public IActionResult OnGet() => LocalRedirect(Url.CulturePage("/Company/Index")!);

    public async Task<IActionResult> OnPostAsync(string? code, string? returnUrl, CancellationToken cancellationToken)
    {
        var session = await user.SessionAsync(cancellationToken);
        if (session is null || !session.Memberships.Any(m => m.CustomerCode == code))
        {
            return Forbid();
        }

        Response.Cookies.Append(CurrentUser.CustomerCookie, code!, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = Remembered,
        });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : Url.CulturePage("/Company/Index")!);
    }
}
