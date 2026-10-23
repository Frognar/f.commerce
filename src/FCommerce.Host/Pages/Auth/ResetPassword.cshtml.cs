using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Auth;

[EnableRateLimiting(RateLimits.Authentication)]
public sealed class ResetPasswordModel(ICommandHandler<ResetPassword, Result<Unit, Error>> reset, IStringLocalizer<SharedResource> texts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Confirmation { get; set; } = string.Empty;

    public bool Done { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Password != Confirmation)
        {
            ModelState.AddModelError(nameof(Confirmation), texts["PasswordsDiffer"]);
            return Page();
        }

        (await reset.HandleAsync(new ResetPassword(Token, Password), cancellationToken)).Switch(
            _ => Done = true,
            error => ModelState.AddErrors(error, texts));
        return Page();
    }
}
