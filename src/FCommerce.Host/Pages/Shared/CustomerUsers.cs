using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Shared;

/// <summary>The invite and change forms of a customer's users (back-office and customer area).</summary>
public sealed class CustomerUserForm
{
    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Culture { get; set; } = string.Empty;

    /// <summary>Rights template for an invitation: "administrator", "buyer" or "accounting".</summary>
    public string Template { get; set; } = "buyer";

    public bool CanOrder { get; set; }

    public string OrderScope { get; set; } = "own";

    public bool CanViewDocuments { get; set; }

    public bool CanManageUsers { get; set; }

    public MembershipRightsView Rights() => new(CanOrder, OrderScope, CanViewDocuments, CanManageUsers);

    /// <summary>The template's rights (an unknown template gives none, so nothing is granted by mistake).</summary>
    public MembershipRightsView TemplateRights() =>
        MembershipAttributes.Templates.TryGetValue(Template, out var attributes)
            ? new MembershipRightsView(attributes.CanOrder, attributes.OrderScope == Authorization.Core.OrderScope.Company ? "company" : "own", attributes.CanViewDocuments, attributes.CanManageUsers)
            : new MembershipRightsView(false, "own", false, false);
}

/// <summary>What the users panel shows; forms post to the current page with <see cref="RouteValues"/>.</summary>
public sealed record CustomerUsersPanel(IReadOnlyList<CustomerUserSummary> Users, bool CanManage, IDictionary<string, string> RouteValues, Guid? CurrentUserId);

/// <summary>Runs the users panel commands for the pages that host it.</summary>
public sealed class CustomerUsersActions(
    ICommandHandler<InviteCustomerUser, Result<Guid, Error>> invite,
    ICommandHandler<ChangeMembership, Result<Unit, Error>> change,
    ICommandHandler<RevokeMembership, Result<Unit, Error>> revoke,
    IQueryHandler<GetCustomerUsers, IReadOnlyList<CustomerUserSummary>> users,
    IStringLocalizer<SharedResource> texts)
{
    public Task<IReadOnlyList<CustomerUserSummary>> UsersAsync(string customerCode, CancellationToken cancellationToken) =>
        users.HandleAsync(new GetCustomerUsers(customerCode), cancellationToken);

    /// <summary>Runs the command; returns the confirmation text, or null after adding the errors to the model state.</summary>
    public async Task<string?> InviteAsync(Guid actorId, string customerCode, CustomerUserForm form, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await invite.HandleAsync(new InviteCustomerUser(actorId, customerCode, form.Email, form.Name, form.Culture, form.TemplateRights()), cancellationToken), "UserInvited", modelState);

    public async Task<string?> ChangeAsync(Guid actorId, string customerCode, Guid userId, CustomerUserForm form, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await change.HandleAsync(new ChangeMembership(actorId, userId, customerCode, form.Rights()), cancellationToken), "Saved", modelState);

    public async Task<string?> RevokeAsync(Guid actorId, string customerCode, Guid userId, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await revoke.HandleAsync(new RevokeMembership(actorId, userId, customerCode), cancellationToken), "AccessRevoked", modelState);

    private string? Outcome<T>(Result<T, Error> result, string savedText, ModelStateDictionary modelState) =>
        result.Match(
            _ =>
            {
                modelState.Clear();
                return (string?)texts[savedText];
            },
            error =>
            {
                modelState.AddErrors(error, texts);
                return null;
            });
}
