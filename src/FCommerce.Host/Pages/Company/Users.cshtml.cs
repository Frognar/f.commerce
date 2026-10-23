using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Shared;
using FCommerce.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>The company's users, managed by its administrators (feature "customer-admin-invitations").</summary>
public sealed class UsersModel(CompanyContext company, CustomerUsersActions users, CurrentUser user) : PageModel
{
    public CustomerDetails? Customer { get; private set; }

    public CustomerUsersPanel? Users { get; private set; }

    public string? Saved { get; private set; }

    [BindProperty]
    public CustomerUserForm UserForm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(AccessAction.Read, cancellationToken) ?? Page();

    public Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken) =>
        ManageAsync(customer => users.InviteAsync(user.UserId!.Value, customer, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostChangeAsync(Guid id, CancellationToken cancellationToken) =>
        ManageAsync(customer => users.ChangeAsync(user.UserId!.Value, customer, id, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken cancellationToken) =>
        ManageAsync(customer => users.RevokeAsync(user.UserId!.Value, customer, id, ModelState, cancellationToken), cancellationToken);

    private async Task<IActionResult> ManageAsync(Func<string, Task<string?>> command, CancellationToken cancellationToken)
    {
        if (await LoadAsync(AccessAction.Manage, cancellationToken) is { } denied)
        {
            return denied;
        }

        Saved = await command(Customer!.Code);
        return await LoadAsync(AccessAction.Read, cancellationToken) ?? Page();
    }

    private async Task<IActionResult?> LoadAsync(AccessAction action, CancellationToken cancellationToken)
    {
        var customer = await company.ActiveCustomerAsync(cancellationToken);
        if (customer is null || !await company.CanAsync(action, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        Customer = customer;
        Users = new CustomerUsersPanel(
            await users.UsersAsync(customer.Code, cancellationToken),
            await company.CanAsync(AccessAction.Manage, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken),
            new Dictionary<string, string>(),
            user.UserId);
        return null;
    }
}
