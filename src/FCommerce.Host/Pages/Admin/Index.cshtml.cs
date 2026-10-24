using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin;

public sealed class IndexModel(AccessControl access, IQueryHandler<GetRegistrationRequests, IReadOnlyList<RegistrationSummary>> registrations) : PageModel
{
    public bool CanManageOperators { get; private set; }

    public bool CanManageSettings { get; private set; }

    /// <summary>Pending registration requests; none when the operator does not decide them (or the feature is off).</summary>
    public Maybe<int> PendingRegistrations { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        CanManageOperators = await access.CanAsync(AccessAction.Manage, new Resource.Operators(), cancellationToken);
        CanManageSettings = await access.CanAsync(AccessAction.Manage, new Resource.PlatformSettings(), cancellationToken);
        PendingRegistrations = await access.CanAsync(AccessAction.Decide, new Resource.Registrations(), cancellationToken)
            ? (await registrations.HandleAsync(new GetRegistrationRequests("pending"), cancellationToken)).Count
            : Maybe<int>.None;
    }
}
