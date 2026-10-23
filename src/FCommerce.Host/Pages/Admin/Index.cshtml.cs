using FCommerce.Authorization.Core;
using FCommerce.Host.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin;

public sealed class IndexModel(AccessControl access) : PageModel
{
    public bool CanManageOperators { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        CanManageOperators = await access.CanAsync(AccessAction.Manage, new Resource.Operators(), cancellationToken);
}
