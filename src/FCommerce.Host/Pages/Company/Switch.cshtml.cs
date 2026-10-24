using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>Chooses the customer to act for (company switcher in the header).</summary>
public sealed class SwitchModel(CurrentUser user) : PageModel
{
    public static readonly TimeSpan Remembered = TimeSpan.FromDays(365);

    public IActionResult OnGet() => LocalRedirect(Url.CulturePage("/Company/Index"));

    public async Task<IActionResult> OnPostAsync(string? code, string? returnUrl, CancellationToken cancellationToken)
    {
        var chosen = (await user.SessionAsync(cancellationToken))
            .Bind(session => session.Memberships.FirstOrNone(m => m.CustomerCode == code))
            .Map(membership => membership.CustomerCode);
        return chosen.Match<IActionResult>(customerCode => Switch(customerCode, returnUrl.ToMaybe()), Forbid);
    }

    private LocalRedirectResult Switch(string customerCode, Maybe<string> returnUrl)
    {
        Response.Cookies.Append(CurrentUser.CustomerCookie, customerCode, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = Remembered,
        });
        return LocalRedirect(returnUrl.Where(Url.IsLocalUrl).ValueOr(() => Url.CulturePage("/Company/Index")));
    }
}
