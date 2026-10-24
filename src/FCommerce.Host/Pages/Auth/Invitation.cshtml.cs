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

namespace FCommerce.Host.Pages.Auth;

[EnableRateLimiting(RateLimits.Authentication)]
public sealed class InvitationModel(
    IQueryHandler<GetInvitation, Result<InvitationView, Error>> invitations,
    ICommandHandler<AcceptInvitation, Result<SignedIn, Error>> accept,
    IQueryHandler<GetSession, Result<SessionView, Error>> sessions,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Confirmation { get; set; } = string.Empty;

    private Maybe<InvitationView> invitation;

    /// <summary>False for an invalid or expired link.</summary>
    public bool Valid => invitation.Match(_ => true, () => false);

    /// <summary>The invitation; read only when <see cref="Valid"/>.</summary>
    public InvitationView Invitation => invitation.Match(i => i, () => throw new InvalidOperationException("The invitation is not loaded."));

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        invitation = (await invitations.HandleAsync(new GetInvitation(Token), cancellationToken)).ToMaybe();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await OnGetAsync(cancellationToken);
        if (!Valid)
        {
            return Page();
        }

        if (Password != Confirmation)
        {
            ModelState.AddModelError(nameof(Confirmation), texts["PasswordsDiffer"]);
            return Page();
        }

        return await (await accept.HandleAsync(new AcceptInvitation(Token, Password), cancellationToken)).Match<Task<IActionResult>>(
            async signedIn =>
            {
                await SessionCookie.SignInAsync(HttpContext, signedIn, remember: false);
                var session = (await sessions.HandleAsync(new GetSession(signedIn.UserId, signedIn.SecurityStamp), cancellationToken)).ToMaybe();
                var landing = session.Match(
                    s => s.Operator is not null ? "/Admin/Index" : s.Memberships.Count > 0 ? "/Company/Index" : "/Index",
                    () => "/Index");
                return LocalRedirect(Url.CulturePage(landing));
            },
            error =>
            {
                ModelState.AddErrors(error, texts);
                return Task.FromResult<IActionResult>(Page());
            });
    }
}
