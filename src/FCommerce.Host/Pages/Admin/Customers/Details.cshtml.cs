using System.Text.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Shared;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Customers;

public sealed class DetailsModel(
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    IQueryHandler<GetAuditLog, IReadOnlyList<AuditEntryView>> auditLog,
    CustomerUsersActions users,
    AccessControl access,
    CurrentUser user) : PageModel
{
    public sealed record AuditLine(DateTimeOffset At, string? Actor, string Action, string? Email);

    public CustomerDetails? Customer { get; private set; }

    /// <summary>Null when the operator may not see the customer's users.</summary>
    public CustomerUsersPanel? Users { get; private set; }

    public IReadOnlyList<AuditLine> Audit { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public CustomerUserForm UserForm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string code, CancellationToken cancellationToken) =>
        await LoadAsync(code, AccessAction.Read, cancellationToken) ?? Page();

    public Task<IActionResult> OnPostInviteAsync(string code, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.InviteAsync(actor, customer, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostChangeAsync(string code, Guid id, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.ChangeAsync(actor, customer, id, UserForm, ModelState, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostRevokeAsync(string code, Guid id, CancellationToken cancellationToken) =>
        ManageAsync(code, (actor, customer) => users.RevokeAsync(actor, customer, id, ModelState, cancellationToken), cancellationToken);

    private async Task<IActionResult> ManageAsync(string code, Func<Guid, string, Task<string?>> command, CancellationToken cancellationToken)
    {
        if (await LoadAsync(code, AccessAction.Manage, cancellationToken) is { } denied)
        {
            return denied;
        }

        Saved = await command(user.UserId!.Value, Customer!.Code);
        return await LoadAsync(code, AccessAction.Read, cancellationToken) ?? Page();
    }

    /// <summary>Loads the page; returns a result only when the page cannot be shown (not found, denied).</summary>
    private async Task<IActionResult?> LoadAsync(string code, AccessAction usersAction, CancellationToken cancellationToken)
    {
        var found = (await customers.HandleAsync(new GetCustomer(code), cancellationToken)).Match(c => c, _ => (CustomerDetails?)null);
        if (found is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        var salesperson = found.Salesperson?.Email is { } email ? EmailAddress.Parse(email).Match(e => e, _ => (EmailAddress?)null) : null;
        if (!await access.CanAsync(AccessAction.Read, new Resource.CustomerProfile(found.Code, salesperson), cancellationToken)
            || !await access.CanAsync(usersAction, new Resource.CustomerUsers(found.Code, salesperson), cancellationToken))
        {
            return Forbid();
        }

        Customer = found;
        Users = new CustomerUsersPanel(
            await users.UsersAsync(found.Code, cancellationToken),
            await access.CanAsync(AccessAction.Manage, new Resource.CustomerUsers(found.Code, salesperson), cancellationToken),
            new Dictionary<string, string> { ["code"] = found.Code },
            user.UserId);
        Audit = [.. (await auditLog.HandleAsync(new GetAuditLog($"customer:{found.Code}"), cancellationToken)).Select(entry => new AuditLine(entry.At, entry.Actor, entry.Action, EmailOf(entry.Details)))];
        return null;
    }

    private static string? EmailOf(string? details)
    {
        if (details is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(details);
        return document.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
    }
}
