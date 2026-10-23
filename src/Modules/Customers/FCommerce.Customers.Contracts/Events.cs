using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Customers.Contracts;

/// <summary>A customer was created or changed (also (de)activated or (un)blocked). Published as "customers.customer-changed.v1".</summary>
public sealed record CustomerChanged(string Code, bool Active, bool Blocked, string? SalespersonEmail) : IIntegrationEvent;

/// <summary>
/// The contacts with platform access changed (all of them are listed, in ERP order). Published as
/// "customers.portal-contacts-changed.v1"; the access module invites new people.
/// </summary>
public sealed record PortalContactsChanged(string CustomerCode, IReadOnlyList<PortalContact> Contacts) : IIntegrationEvent;

/// <param name="AttributeTemplate">Null means the platform default template.</param>
public sealed record PortalContact(string ExternalId, string Name, string Email, string? AttributeTemplate);
