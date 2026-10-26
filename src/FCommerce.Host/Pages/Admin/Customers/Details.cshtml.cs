using System.Text.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Shared;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Customers;

public sealed class DetailsModel(
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    IQueryHandler<GetAuditLog, IReadOnlyList<AuditEntryView>> auditLog,
    CustomerUsersActions users,
    ApiKeyActions apiKeys,
    AccessControl access,
    CurrentUser user) : PageModel
{
    /// <param name="Actor">None for the system (ERP-driven changes).</param>
    public sealed record AuditLine(DateTimeOffset At, Maybe<string> Actor, string Action, Maybe<string> Email);

    private Maybe<CustomerDetails> customer;

    public bool Found => customer.Match(_ => true, () => false);

    /// <summary>The customer; read only when <see cref="Found"/>.</summary>
    public CustomerDetails Customer => customer.Match(c => c, () => throw new InvalidOperationException("The customer is not loaded."));

    /// <summary>None when the operator may not see the customer's users.</summary>
    public Maybe<CustomerUsersPanel> Users { get; private set; }

    /// <summary>None when the operator may not see the customer's API keys (or the public API is off).</summary>
    public Maybe<ApiKeysPanel> ApiKeys { get; private set; }

    public IReadOnlyList<AuditLine> Audit { get; private set; } = [];

    public Maybe<string> Saved { get; private set; }

    [BindProperty]
    public CustomerUserForm UserForm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string code, CancellationToken cancellationToken) =>
        (await LoadAsync(code, AccessAction.Read, cancellationToken)).ValueOr(Page);

    public Task<IActionResult> OnPostInviteAsync(string code, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.InviteAsync(actor, customer, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostChangeAsync(string code, Guid id, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.ChangeAsync(actor, customer, id, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostRevokeAsync(string code, Guid id, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.RevokeAsync(actor, customer, id, ModelState, cancellationToken), cancellationToken);

    public async Task<IActionResult> OnPostRevokeKeyAsync(string code, Guid id, CancellationToken cancellationToken) =>
        await (await LoadAsync(code, AccessAction.Read, cancellationToken)).Match(
            Task.FromResult,
            async () =>
            {
                if (!await access.CanAsync(AccessAction.Manage, new Resource.ApiKeys(Customer.Code, Company.CompanyContext.Salesperson(Customer)), cancellationToken))
                {
                    return Forbid();
                }

                Saved = await apiKeys.RevokeAsync(user.RequiredUserId, Customer.Code, id, ModelState, cancellationToken);
                return (await LoadAsync(code, AccessAction.Read, cancellationToken)).ValueOr(Page);
            });

    private async Task<IActionResult> ManageAsync(string code, Func<Guid, string, Task<Maybe<string>>> command, CancellationToken cancellationToken) =>
        await (await LoadAsync(code, AccessAction.Manage, cancellationToken)).Match(
            Task.FromResult,
            async () =>
            {
                Saved = await command(user.RequiredUserId, Customer.Code);
                return (await LoadAsync(code, AccessAction.Read, cancellationToken)).ValueOr(Page);
            });

    /// <summary>Loads the page; returns a result only when the page cannot be shown (not found, denied).</summary>
    private async Task<Maybe<IActionResult>> LoadAsync(string code, AccessAction usersAction, CancellationToken cancellationToken) =>
        await (await customers.HandleAsync(new GetCustomer(code), cancellationToken)).ToMaybe().Match(
            found => LoadAsync(found, usersAction, cancellationToken),
            () =>
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.FromResult(Maybe.Some<IActionResult>(Page()));
            });

    private async Task<Maybe<IActionResult>> LoadAsync(CustomerDetails found, AccessAction usersAction, CancellationToken cancellationToken)
    {
        var salesperson = Company.CompanyContext.Salesperson(found);
        if (!await access.CanAsync(AccessAction.Read, new Resource.CustomerProfile(found.Code, salesperson), cancellationToken)
            || !await access.CanAsync(usersAction, new Resource.CustomerUsers(found.Code, salesperson), cancellationToken))
        {
            return Forbid();
        }

        customer = found;
        Users = new CustomerUsersPanel(
            await users.UsersAsync(found.Code, cancellationToken),
            await access.CanAsync(AccessAction.Manage, new Resource.CustomerUsers(found.Code, salesperson), cancellationToken),
            new Dictionary<string, string> { ["code"] = found.Code },
            user.UserId,
            found.Currency,
            found.DeliveryAddresses);
        var keysResource = new Resource.ApiKeys(found.Code, salesperson);
        ApiKeys = await access.CanAsync(AccessAction.Read, keysResource, cancellationToken)
            ? Maybe.Some(new ApiKeysPanel(
                await apiKeys.KeysAsync(found.Code, cancellationToken),
                await access.CanAsync(AccessAction.Manage, keysResource, cancellationToken),
                CanCreate: false,
                [],
                Maybe<CreatedApiKey>.None,
                new Dictionary<string, string> { ["code"] = found.Code },
                user.UserId))
            : Maybe<ApiKeysPanel>.None;
        Audit = [.. (await auditLog.HandleAsync(new GetAuditLog($"customer:{found.Code}"), cancellationToken))
            .Select(entry => new AuditLine(entry.At, entry.Actor.ToMaybe(), entry.Action, entry.Details.ToMaybe().Bind(EmailOf)))];
        return Maybe<IActionResult>.None;
    }

    private static Maybe<string> EmailOf(string details)
    {
        using var document = JsonDocument.Parse(details);
        return document.RootElement.TryGetProperty("email", out var email) ? email.GetString().ToMaybe() : Maybe<string>.None;
    }
}
