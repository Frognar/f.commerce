using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Contracts;

/// <summary>A user's access to one customer, as primitives.</summary>
/// <param name="OrderScope">"own" or "company": which orders the user sees.</param>
/// <param name="OrderLimit">Highest order value, in <paramref name="OrderLimitCurrency"/> (enforced with ordering).</param>
/// <param name="DeliveryAddressCodes">Null: every delivery address of the customer.</param>
public sealed record MembershipView(
    string CustomerCode,
    bool CanOrder,
    string OrderScope,
    decimal? OrderLimit,
    string? OrderLimitCurrency,
    bool CanViewDocuments,
    bool CanManageUsers,
    IReadOnlyList<string>? DeliveryAddressCodes);

/// <summary>The rights edited in the UI ("own"/"company" order scope).</summary>
public sealed record MembershipRightsView(bool CanOrder, string OrderScope, bool CanViewDocuments, bool CanManageUsers);

/// <summary>
/// Invites a person to act for a customer: a new account gets an invitation link, an existing one the access (and an
/// e-mail about it). The actor is an operator or an administrator of that customer.
/// </summary>
public sealed record InviteCustomerUser(Guid ActorId, string CustomerCode, string Email, string Name, string Culture, MembershipRightsView Rights)
    : ICommand<Result<Guid, Error>>;

/// <summary>Changes a member's rights (the last administrator of a customer keeps the right to manage users).</summary>
public sealed record ChangeMembership(Guid ActorId, Guid UserId, string CustomerCode, MembershipRightsView Rights) : ICommand<Result<Unit, Error>>;

/// <summary>Removes a person's access to a customer (their account stays).</summary>
public sealed record RevokeMembership(Guid ActorId, Guid UserId, string CustomerCode) : ICommand<Result<Unit, Error>>;

public sealed record GetCustomerUsers(string CustomerCode) : IQuery<IReadOnlyList<CustomerUserSummary>>;

/// <param name="Status">"Invited", "Active" or "Disabled".</param>
/// <param name="Source">"Erp", "Operator", "CustomerAdmin" or "Registration".</param>
public sealed record CustomerUserSummary(Guid UserId, string Email, string Name, string Status, string Source, MembershipRightsView Rights);

/// <summary>Security-relevant changes concerning a subject ("customer:{code}", "account:{id}"), newest first.</summary>
public sealed record GetAuditLog(string Subject, int Limit = 50) : IQuery<IReadOnlyList<AuditEntryView>>;

/// <param name="Actor">Name of who did it; null for the system (ERP, bootstrap).</param>
/// <param name="Details">JSON details, as recorded.</param>
public sealed record AuditEntryView(DateTimeOffset At, string? Actor, string Action, string? Details);
