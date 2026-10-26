using FCommerce.Access.Contracts;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Shared;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>The company's API keys, managed by its administrators (feature "public-api").</summary>
[RequiresFeature("public-api")]
public sealed class ApiKeysModel(CompanyContext company, ApiKeyActions keys, CustomerUsersActions users, CurrentUser user) : PageModel
{
    private Maybe<(CustomerDetails Customer, ApiKeysPanel Panel)> loaded;
    private Maybe<CreatedApiKey> created;

    /// <summary>The active customer; read only after a successful load (the view renders only then).</summary>
    public CustomerDetails Customer => Loaded.Customer;

    public ApiKeysPanel Panel => Loaded.Panel with { Created = created };

    public Maybe<string> Saved { get; private set; }

    private (CustomerDetails Customer, ApiKeysPanel Panel) Loaded =>
        loaded.Match(value => value, () => throw new InvalidOperationException("The API keys are not loaded."));

    [BindProperty]
    public ApiKeyForm KeyForm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(AccessAction.Read, cancellationToken)).ValueOr(Page);

    public Task<IActionResult> OnPostCreateKeyAsync(CancellationToken cancellationToken) =>
        ManageAsync(
            async customer =>
            {
                created = await keys.CreateAsync(user.RequiredUserId, customer, KeyForm, ModelState, cancellationToken);
                if (created.Exists(_ => true))
                {
                    // The key is shown once, in this response only.
                    Response.Headers.CacheControl = "no-store";
                }
            },
            cancellationToken);

    public Task<IActionResult> OnPostRevokeKeyAsync(Guid id, CancellationToken cancellationToken) =>
        ManageAsync(async customer => Saved = await keys.RevokeAsync(user.RequiredUserId, customer, id, ModelState, cancellationToken), cancellationToken);

    private async Task<IActionResult> ManageAsync(Func<string, Task> command, CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Manage, cancellationToken)).Match(
            Task.FromResult,
            async () =>
            {
                await command(Customer.Code);
                return (await LoadAsync(AccessAction.Read, cancellationToken)).ValueOr(Page);
            });

    /// <summary>Loads the active customer and its keys; the result is the denial, none when loaded.</summary>
    private async Task<Maybe<IActionResult>> LoadAsync(AccessAction action, CancellationToken cancellationToken) =>
        await (await company.ActiveCustomerAsync(cancellationToken)).Match(
            customer => LoadAsync(customer, action, cancellationToken),
            () => Task.FromResult(Maybe.Some<IActionResult>(Forbid())));

    private async Task<Maybe<IActionResult>> LoadAsync(CustomerDetails customer, AccessAction action, CancellationToken cancellationToken)
    {
        if (!await company.CanAsync(action, (code, salesperson) => new Resource.ApiKeys(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        var canManage = await company.CanAsync(AccessAction.Manage, (code, salesperson) => new Resource.ApiKeys(code, salesperson), customer, cancellationToken);
        loaded = (customer, new ApiKeysPanel(
            await keys.KeysAsync(customer.Code, cancellationToken),
            canManage,
            canManage,
            [.. (await users.UsersAsync(customer.Code, cancellationToken)).Where(u => u.Status == "Active")],
            Maybe<CreatedApiKey>.None,
            new Dictionary<string, string>(),
            user.UserId));
        return Maybe<IActionResult>.None;
    }
}
