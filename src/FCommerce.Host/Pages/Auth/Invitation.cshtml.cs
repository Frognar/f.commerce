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

    public InvitationView? Invitation { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Invitation = (await invitations.HandleAsync(new GetInvitation(Token), cancellationToken)).Match(i => (InvitationView?)i, _ => null);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await OnGetAsync(cancellationToken);
        if (Invitation is null)
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
                var session = (await sessions.HandleAsync(new GetSession(signedIn.UserId, signedIn.SecurityStamp), cancellationToken)).Match(s => (SessionView?)s, _ => null);
                var landing = session switch
                {
                    { Operator: not null } => "/Admin/Index",
                    { Memberships.Count: > 0 } => "/Company/Index",
                    _ => "/Index",
                };
                return LocalRedirect(Url.CulturePage(landing)!);
            },
            error =>
            {
                ModelState.AddErrors(error, texts);
                return Task.FromResult<IActionResult>(Page());
            });
    }
}
