using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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

    private Maybe<SessionView> session;

    /// <summary>The signed-in account; loaded before every handler (the page requires a session).</summary>
    public SessionView Session => session.Match(s => s, () => throw new InvalidOperationException("The session is not loaded."));

    public Maybe<string> Saved { get; private set; }

    /// <summary>Loads the session before any handler, so every request path (including an unknown handler) can render.</summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        session = await user.SessionAsync(HttpContext.RequestAborted);
        if (session.Match(_ => true, () => false))
        {
            await next();
        }
        else
        {
            context.Result = Challenge();
        }
    }

    public void OnGet()
    {
        Name = Session.Name;
        Culture = Session.Culture;
    }

    public async Task<IActionResult> OnPostProfileAsync(CancellationToken cancellationToken)
    {
        (await changeProfile.HandleAsync(new ChangeProfile(Session.UserId, Name, Culture), cancellationToken)).Switch(
            _ => Saved = Maybe.Some<string>(texts["ProfileSaved"]),
            error => ModelState.AddErrors(error, texts));
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken cancellationToken)
    {
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
        var remember = (await HttpContext.AuthenticateAsync()).Properties.ToMaybe().Exists(p => p.IsPersistent);
        await result.Match(signedIn => SessionCookie.SignInAsync(HttpContext, signedIn, remember), _ => Task.CompletedTask);
        Saved = Maybe.Some<string>(texts["PasswordChanged"]);
        return Page();
    }
}
