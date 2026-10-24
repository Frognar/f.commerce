using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using SignInCommand = FCommerce.Access.Contracts.SignIn;

namespace FCommerce.Host.Pages.Auth;

[EnableRateLimiting(RateLimits.Authentication)]
public sealed class SignInModel(ICommandHandler<SignInCommand, Result<SignedIn, Error>> signIn, IStringLocalizer<SharedResource> texts) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool Remember { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public Maybe<string> Failure { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken) =>
        await (await signIn.HandleAsync(new SignInCommand(Email, Password), cancellationToken)).Match<Task<IActionResult>>(
            async signedIn =>
            {
                await SessionCookie.SignInAsync(HttpContext, signedIn, Remember);
                return LocalRedirect(ReturnUrl.ToMaybe().Where(Url.IsLocalUrl).ValueOr(() => Url.CulturePage("/Index")));
            },
            error =>
            {
                Failure = texts.Describe(error);
                Password = string.Empty;
                return Task.FromResult<IActionResult>(Page());
            });
}
