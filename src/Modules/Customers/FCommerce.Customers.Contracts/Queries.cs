using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Customers.Contracts;

/// <summary>Customer details; not found for unknown codes (inactive customers are returned, marked).</summary>
public sealed record GetCustomer(string Code) : IQuery<Result<CustomerDetails, Error>>;

/// <summary>Summaries of the given customers (unknown codes are skipped), e.g. for a user's customer switcher.</summary>
public sealed record GetCustomers(IReadOnlyList<string> Codes) : IQuery<IReadOnlyList<CustomerSummary>>;

/// <summary>
/// Back-office customer list: text matches code, name, tax id and city; <paramref name="SalespersonEmail"/> limits
/// the list to one salesperson's customers.
/// </summary>
public sealed record SearchCustomers(string? Text, string? SalespersonEmail, int Page = 1, int PageSize = 25)
    : IQuery<Result<CustomerPage, Error>>;

/// <summary>Customers with this tax id (e.g. to match a registration request to an existing customer).</summary>
public sealed record FindCustomersByTaxId(string TaxId) : IQuery<Result<IReadOnlyList<CustomerSummary>, Error>>;

public sealed record CustomerPage(IReadOnlyList<CustomerSummary> Items, int Page, int PageSize, int TotalCount)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record CustomerSummary(
    string Code,
    string Name,
    string? TaxId,
    string City,
    string? SalespersonName,
    string? SalespersonEmail,
    bool Active,
    bool Blocked);

public sealed record CustomerDetails(
    string Code,
    string Name,
    string? TaxId,
    AddressView BillingAddress,
    IReadOnlyList<DeliveryAddressView> DeliveryAddresses,
    IReadOnlyList<ContactView> Contacts,
    SalespersonView? Salesperson,
    string? PriceListCode,
    int? PaymentTermDays,
    string Currency,
    bool Blocked,
    bool Active,
    DateTimeOffset UpdatedAt);

public sealed record AddressView(string Street, string PostalCode, string City, string Country);

public sealed record DeliveryAddressView(string Code, string? Name, AddressView Address, bool IsDefault);

public sealed record ContactView(string ExternalId, string Name, string? Email, string? Phone, bool PortalAccess, string? AttributeTemplate);

public sealed record SalespersonView(string Name, string? Email, string? Phone);
