using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Pages.Company;

/// <summary>The customer area always works on the session's active customer; the code never comes from the URL.</summary>
public sealed class CompanyContext(CurrentUser user, AccessControl access, IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
{
    /// <summary>The active customer, or null when the user acts for none (or it is gone from the ERP).</summary>
    public async Task<CustomerDetails?> ActiveCustomerAsync(CancellationToken cancellationToken) =>
        await user.ActiveMembershipAsync(cancellationToken) is { } membership
            ? (await customers.HandleAsync(new GetCustomer(membership.CustomerCode), cancellationToken)).Match(c => c, _ => (CustomerDetails?)null)
            : null;

    public Task<bool> CanAsync(AccessAction action, Func<string, EmailAddress?, Resource> resource, CustomerDetails customer, CancellationToken cancellationToken) =>
        access.CanAsync(action, resource(customer.Code, Salesperson(customer)), cancellationToken);

    private static EmailAddress? Salesperson(CustomerDetails customer) =>
        customer.Salesperson?.Email is { } email ? EmailAddress.Parse(email).Match(e => e, _ => (EmailAddress?)null) : null;
}
