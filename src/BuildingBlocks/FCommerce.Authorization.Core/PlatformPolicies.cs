using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Authorization.Core;

/// <summary>
/// Every access rule of the platform, in one place. Guard policies only deny (feature off, other customer, inactive
/// customer, outside an operator's scope); grant policies permit specific subject/resource/action combinations.
/// Anything not explicitly permitted is denied.
/// </summary>
public static class PlatformPolicies
{
    public static IReadOnlyList<Policy> All { get; } =
    [
        FeatureGate,
        CustomerIsolation,
        InactiveCustomer,
        OperatorScope,
        CatalogAccess,
        OwnAccount,
        CustomerProfile,
        CustomerUsers,
        CustomerDocuments,
        Cart,
        Orders,
        ApiKeys,
        BackOffice,
        Registrations,
    ];

    public static Decision Decide(AccessRequest request) => PolicyCombining.DenyOverrides(All, request);

    /// <summary>Resources of a switched-off feature are denied to everybody.</summary>
    private static PolicyResult FeatureGate(AccessRequest request) =>
        RequiredFeature(request)
            .Where(feature => !request.Environment.FeaturesOn.Contains(feature))
            .Match(feature => PolicyResult.Deny($"feature-off:{feature}"), () => PolicyResult.NotApplicable);

    private static Maybe<FeatureId> RequiredFeature(AccessRequest request) =>
        (request.Resource, request.Subject) switch
        {
            (Resource.CustomerDocuments, _) => Feature.Documents,
            (Resource.Cart, _) => Feature.Cart,
            (Resource.ShoppingLists, _) => Feature.ShoppingLists,
            (Resource.Orders or Resource.Order, _) => Feature.Ordering,
            (Resource.ApiKeys, _) => Feature.PublicApi,
            (Resource.Registrations, _) => Feature.SelfRegistration,
            (Resource.CustomerUsers, Subject.CustomerUser) when request.Action == AccessAction.Manage => Feature.CustomerAdminInvitations,
            (Resource.CustomerProfile or Resource.CustomerUsers, Subject.CustomerUser) => Feature.CustomerAccounts,
            _ => Maybe<FeatureId>.None,
        };

    /// <summary>A customer user acts only within the active customer of the session.</summary>
    private static PolicyResult CustomerIsolation(AccessRequest request) =>
        request.Subject is Subject.CustomerUser user && request.Resource.Owner.Exists(owner => owner.Code != user.ActiveCustomer)
            ? PolicyResult.Deny("other-customer")
            : PolicyResult.NotApplicable;

    private static PolicyResult InactiveCustomer(AccessRequest request) =>
        request.Subject is Subject.CustomerUser && request.Resource.Owner.Exists(_ => request.Environment.ActiveCustomer != ActiveCustomerStatus.Active)
            ? PolicyResult.Deny("customer-inactive")
            : PolicyResult.NotApplicable;

    /// <summary>A salesperson-scoped operator handles only customers they are the salesperson of.</summary>
    private static PolicyResult OperatorScope(AccessRequest request) =>
        request.Subject is Subject.Operator { Attributes.CustomerScope: CustomerScope.AsSalesperson } op
        && request.Resource.Owner.Exists(owner => !owner.Salesperson.Exists(salesperson => salesperson == op.Email))
            ? PolicyResult.Deny("outside-operator-scope")
            : PolicyResult.NotApplicable;

    private static PolicyResult CatalogAccess(AccessRequest request) =>
        request is { Resource: Resource.Catalog, Action: AccessAction.Read }
            ? PolicyResult.PermitIf(
                request.Environment.CatalogVisibility == CatalogVisibility.Public || request.Subject is not Subject.AnonymousSubject,
                "sign-in-required")
            : PolicyResult.NotApplicable;

    private static PolicyResult OwnAccount(AccessRequest request) =>
        request.Resource is Resource.OwnAccount && request.Action is AccessAction.Read or AccessAction.Manage
            ? PolicyResult.PermitIf(request.Subject is not Subject.AnonymousSubject, "sign-in-required")
            : PolicyResult.NotApplicable;

    private static PolicyResult CustomerProfile(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.CustomerProfile, AccessAction.Read, Subject.CustomerUser or Subject.Operator) => PolicyResult.Permit,
            _ => PolicyResult.NotApplicable,
        };

    private static PolicyResult CustomerUsers(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.CustomerUsers, AccessAction.Read or AccessAction.Manage, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanManageUsers, "cannot-manage-users"),
            (Resource.CustomerUsers, AccessAction.Read or AccessAction.Manage, Subject.Operator) => PolicyResult.Permit,
            _ => PolicyResult.NotApplicable,
        };

    private static PolicyResult CustomerDocuments(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.CustomerDocuments, AccessAction.Read or AccessAction.Download, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanViewDocuments, "cannot-view-documents"),
            (Resource.CustomerDocuments, AccessAction.Read or AccessAction.Download, Subject.Operator) => PolicyResult.Permit,
            _ => PolicyResult.NotApplicable,
        };

    /// <summary>Only people who may order for the customer have a cart and shopping lists; operators order nothing.</summary>
    private static PolicyResult Cart(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.Cart or Resource.ShoppingLists, AccessAction.Read or AccessAction.Manage, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanOrder, "cannot-order"),
            _ => PolicyResult.NotApplicable,
        };

    /// <summary>
    /// Buyers place orders and see their own; the company order scope shows every order of the customer. Only the
    /// person who placed an order, or someone who sees all orders, may cancel it. Operators read orders.
    /// </summary>
    private static PolicyResult Orders(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.Orders, AccessAction.Submit, Subject.CustomerUser user) => PolicyResult.PermitIf(user.Attributes.CanOrder, "cannot-order"),
            (Resource.Orders, AccessAction.Read, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanOrder || user.Attributes.OrderScope == OrderScope.Company, "cannot-see-orders"),
            (Resource.Order order, AccessAction.Read, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(order.PlacedBy == user.UserId || user.Attributes.OrderScope == OrderScope.Company, "not-own-order"),
            (Resource.Order order, AccessAction.Manage, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanOrder && (order.PlacedBy == user.UserId || user.Attributes.OrderScope == OrderScope.Company), "cannot-manage-order"),
            (Resource.Orders or Resource.Order, AccessAction.Read, Subject.Operator) => PolicyResult.Permit,
            _ => PolicyResult.NotApplicable,
        };

    /// <summary>Keys are an integration right: only administrators of the customer manage them; operators read and revoke.</summary>
    private static PolicyResult ApiKeys(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.ApiKeys, AccessAction.Read or AccessAction.Manage, Subject.CustomerUser user) =>
                PolicyResult.PermitIf(user.Attributes.CanManageUsers, "cannot-manage-api-keys"),
            (Resource.ApiKeys, AccessAction.Read or AccessAction.Manage, Subject.Operator) => PolicyResult.Permit,
            _ => PolicyResult.NotApplicable,
        };

    private static PolicyResult BackOffice(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.Customers, AccessAction.Read, Subject.Operator) => PolicyResult.Permit,
            (Resource.PlatformSettings, AccessAction.Read or AccessAction.Manage, Subject.Operator op) =>
                PolicyResult.PermitIf(op.Attributes.CanManageSettings, "cannot-manage-settings"),
            (Resource.Operators, AccessAction.Read or AccessAction.Manage, Subject.Operator op) =>
                PolicyResult.PermitIf(op.Attributes.CanManageOperators, "cannot-manage-operators"),
            _ => PolicyResult.NotApplicable,
        };

    private static PolicyResult Registrations(AccessRequest request) =>
        (request.Resource, request.Action, request.Subject) switch
        {
            (Resource.Registrations, AccessAction.Submit, Subject.AnonymousSubject or Subject.CustomerUser) => PolicyResult.Permit,
            (Resource.Registrations, AccessAction.Read or AccessAction.Decide, Subject.Operator op) =>
                PolicyResult.PermitIf(op.Attributes.CanDecideRegistrations, "cannot-decide-registrations"),
            _ => PolicyResult.NotApplicable,
        };
}
