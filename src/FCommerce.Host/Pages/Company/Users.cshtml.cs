using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Shared;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>The company's users, managed by its administrators (feature "customer-admin-invitations").</summary>
public sealed class UsersModel(CompanyContext company, CustomerUsersActions users, CurrentUser user) : PageModel
{
    private Maybe<(CustomerDetails Customer, CustomerUsersPanel Users)> loaded;

    /// <summary>The active customer; read only after a successful load (the view renders only then).</summary>
    public CustomerDetails Customer => Loaded.Customer;

    public CustomerUsersPanel Users => Loaded.Users;

    public Maybe<string> Saved { get; private set; }

    private (CustomerDetails Customer, CustomerUsersPanel Users) Loaded =>
        loaded.Match(value => value, () => throw new InvalidOperationException("The company users are not loaded."));

    [BindProperty]
    public CustomerUserForm UserForm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(AccessAction.Read, cancellationToken)).ValueOr(Page);

    public Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken) =>
        ManageAsync(customer => users.InviteAsync(user.RequiredUserId, customer, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostChangeAsync(Guid id, CancellationToken cancellationToken) =>
        ManageAsync(customer => users.ChangeAsync(user.RequiredUserId, customer, id, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken cancellationToken) =>
        ManageAsync(customer => users.RevokeAsync(user.RequiredUserId, customer, id, ModelState, cancellationToken), cancellationToken);

    private async Task<IActionResult> ManageAsync(Func<string, Task<Maybe<string>>> command, CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Manage, cancellationToken)).Match(
            Task.FromResult,
            async () =>
            {
                Saved = await command(Customer.Code);
                return (await LoadAsync(AccessAction.Read, cancellationToken)).ValueOr(Page);
            });

    /// <summary>Loads the active customer and its users; the result is the denial, none when loaded.</summary>
    private async Task<Maybe<IActionResult>> LoadAsync(AccessAction action, CancellationToken cancellationToken) =>
        await (await company.ActiveCustomerAsync(cancellationToken)).Match(
            customer => LoadAsync(customer, action, cancellationToken),
            () => Task.FromResult(Maybe.Some<IActionResult>(Forbid())));

    private async Task<Maybe<IActionResult>> LoadAsync(CustomerDetails customer, AccessAction action, CancellationToken cancellationToken)
    {
        if (!await company.CanAsync(action, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        loaded = (customer, new CustomerUsersPanel(
            await users.UsersAsync(customer.Code, cancellationToken),
            await company.CanAsync(AccessAction.Manage, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken),
            new Dictionary<string, string>(),
            user.UserId));
        return Maybe<IActionResult>.None;
    }
}
