using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Auth;

/// <summary>Signing out is a POST (with antiforgery), so a link on another site cannot sign people out.</summary>
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect(Url.CulturePage("/Index"));

    public async Task<IActionResult> OnPostAsync()
    {
        await SessionCookie.SignOutAsync(HttpContext);
        return LocalRedirect(Url.CulturePage("/Index"));
    }
}
