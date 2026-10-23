using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Account;

public sealed class IndexModel(
    CurrentUser user,
    ICommandHandler<ChangeProfile, Result<Unit, Error>> changeProfile,
    ICommandHandler<ChangePassword, Result<SignedIn, Error>> changePassword,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string Culture { get; set; } = string.Empty;

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string Confirmation { get; set; } = string.Empty;

    public SessionView Session { get; private set; } = null!;

    public string? Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Session = (await user.SessionAsync(cancellationToken))!;
        Name = Session.Name;
        Culture = Session.Culture;
    }

    public async Task<IActionResult> OnPostProfileAsync(CancellationToken cancellationToken)
    {
        Session = (await user.SessionAsync(cancellationToken))!;
        (await changeProfile.HandleAsync(new ChangeProfile(Session.UserId, Name, Culture), cancellationToken)).Switch(
            _ => Saved = texts["ProfileSaved"],
            error => ModelState.AddErrors(error, texts));
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken cancellationToken)
    {
        Session = (await user.SessionAsync(cancellationToken))!;
        (Name, Culture) = (Session.Name, Session.Culture);
        if (NewPassword != Confirmation)
        {
            ModelState.AddModelError(nameof(Confirmation), texts["PasswordsDiffer"]);
            return Page();
        }

        var result = await changePassword.HandleAsync(new ChangePassword(Session.UserId, CurrentPassword, NewPassword), cancellationToken);
        if (result.Match(_ => false, error => { ModelState.AddErrors(error, texts); return true; }))
        {
            return Page();
        }

        // The stamp changed (other sessions end); this one continues with a fresh cookie.
        var remember = (await HttpContext.AuthenticateAsync()).Properties?.IsPersistent ?? false;
        await result.Match(signedIn => SessionCookie.SignInAsync(HttpContext, signedIn, remember), _ => Task.CompletedTask);
        Saved = texts["PasswordChanged"];
        return Page();
    }
}
