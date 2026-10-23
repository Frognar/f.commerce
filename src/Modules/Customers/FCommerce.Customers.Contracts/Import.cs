using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Customers.Contracts;

/// <summary>
/// Upserts customers sent by the ERP connector. Each customer is sent whole (addresses and contacts included) and
/// validated independently; items not newer than the stored version are ignored.
/// </summary>
public sealed record ImportCustomers(IReadOnlyList<CustomerData> Customers) : ICommand<Result<ImportReport, Error>>;

/// <param name="Code">ERP customer code; identifies the customer.</param>
/// <param name="Version">Monotonic version from the ERP (row version or change timestamp).</param>
/// <param name="TaxId">Tax number; a bare 10-digit number is a Polish NIP, others carry their country prefix ("DE123456789").</param>
/// <param name="PriceListCode">The customer's own price list; the platform default applies when absent.</param>
/// <param name="Currency">ISO 4217 currency of the customer's prices and orders; PLN when absent.</param>
/// <param name="Blocked">Blocked in the ERP (e.g. overdue payments): users may sign in but not order.</param>
/// <param name="Active">False closes the customer's access to the platform.</param>
public sealed record CustomerData(
    string Code,
    long Version,
    string Name,
    string? TaxId,
    AddressData BillingAddress,
    IReadOnlyList<DeliveryAddressData>? DeliveryAddresses,
    IReadOnlyList<ContactData>? Contacts,
    SalespersonData? Salesperson,
    string? PriceListCode = null,
    int? PaymentTermDays = null,
    string? Currency = null,
    bool Blocked = false,
    bool Active = true);

/// <param name="Country">ISO 3166-1 alpha-2 code, e.g. "PL".</param>
public sealed record AddressData(string Street, string PostalCode, string City, string Country);

/// <param name="Code">ERP code of the address, unique within the customer.</param>
public sealed record DeliveryAddressData(string Code, string? Name, AddressData Address, bool IsDefault = false);

/// <param name="ExternalId">ERP id of the contact person, unique within the customer.</param>
/// <param name="PortalAccess">The person should get a platform account (an invitation is sent to their e-mail).</param>
/// <param name="AttributeTemplate">Rights for the invitation: "administrator", "buyer" or "accounting"; the platform default when absent.</param>
public sealed record ContactData(
    string ExternalId,
    string Name,
    string? Email,
    string? Phone,
    bool PortalAccess = false,
    string? AttributeTemplate = null);

/// <param name="Email">Links the customer to the seller's operator with this e-mail (salesperson-scoped back-office access).</param>
public sealed record SalespersonData(string Name, string? Email, string? Phone);
