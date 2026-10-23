using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace FCommerce.Host.Pages.Auth;

[EnableRateLimiting(RateLimits.Authentication)]
public sealed class ForgotPasswordModel(ICommandHandler<RequestPasswordReset, Result<Unit, Error>> requestReset) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public bool Sent { get; private set; }

    /// <summary>The same answer whether or not the account exists.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await requestReset.HandleAsync(new RequestPasswordReset(Email), cancellationToken);
        Sent = true;
        return Page();
    }
}
