using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Optional;
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

    /// <summary>Highest net value of an order; empty: unlimited.</summary>
    public string OrderLimit { get; set; } = string.Empty;

    /// <summary>The customer's currency, rendered with the form.</summary>
    public string OrderLimitCurrency { get; set; } = string.Empty;

    /// <summary>Delivery addresses the user may order to; none selected: all of them.</summary>
    public List<string> DeliveryAddressCodes { get; set; } = [];

    public Result<MembershipRightsView, Error> Rights() =>
        (string.IsNullOrWhiteSpace(OrderLimit)
            ? Result.Ok<Maybe<decimal>, Error>(Maybe<decimal>.None)
            : FormNumbers.Decimal(OrderLimit, "membership.order-limit-format", "orderLimit").Map(Maybe.Some))
        .Map(limit => new MembershipRightsView(
            CanOrder,
            OrderScope,
            CanViewDocuments,
            CanManageUsers,
            limit.OrNull(),
            limit.Map(_ => OrderLimitCurrency).OrNull(),
            DeliveryAddressCodes.Count == 0 ? null : DeliveryAddressCodes));

    /// <summary>The template's rights (an unknown template gives none, so nothing is granted by mistake).</summary>
    public MembershipRightsView TemplateRights() =>
        MembershipAttributes.Templates.GetValueOrNone(Template).Match(
            attributes => new MembershipRightsView(attributes.CanOrder, attributes.OrderScope == Authorization.Core.OrderScope.Company ? "company" : "own", attributes.CanViewDocuments, attributes.CanManageUsers),
            () => new MembershipRightsView(false, "own", false, false));
}

/// <summary>What the users panel shows; forms post to the current page with <see cref="RouteValues"/>.</summary>
/// <param name="Currency">The customer's currency (of order limits).</param>
/// <param name="DeliveryAddresses">The customer's delivery addresses, to limit where a user may order to.</param>
public sealed record CustomerUsersPanel(
    IReadOnlyList<CustomerUserSummary> Users,
    bool CanManage,
    IDictionary<string, string> RouteValues,
    Maybe<Guid> CurrentUserId,
    string Currency,
    IReadOnlyList<DeliveryAddressView> DeliveryAddresses);

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

    /// <summary>Runs the command; returns the confirmation text, or none after adding the errors to the model state.</summary>
    public async Task<Maybe<string>> InviteAsync(Guid actorId, string customerCode, CustomerUserForm form, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await invite.HandleAsync(new InviteCustomerUser(actorId, customerCode, form.Email, form.Name, form.Culture, form.TemplateRights()), cancellationToken), "UserInvited", modelState);

    public async Task<Maybe<string>> ChangeAsync(Guid actorId, string customerCode, Guid userId, CustomerUserForm form, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await form.Rights().BindAsync(rights => change.HandleAsync(new ChangeMembership(actorId, userId, customerCode, rights), cancellationToken)), "Saved", modelState);

    public async Task<Maybe<string>> RevokeAsync(Guid actorId, string customerCode, Guid userId, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        Outcome(await revoke.HandleAsync(new RevokeMembership(actorId, userId, customerCode), cancellationToken), "AccessRevoked", modelState);

    private Maybe<string> Outcome<T>(Result<T, Error> result, string savedText, ModelStateDictionary modelState)
        where T : notnull =>
        result.Match(
            _ =>
            {
                modelState.Clear();
                return Maybe.Some<string>(texts[savedText]);
            },
            error =>
            {
                modelState.AddErrors(error, texts);
                return Maybe<string>.None;
            });
}
