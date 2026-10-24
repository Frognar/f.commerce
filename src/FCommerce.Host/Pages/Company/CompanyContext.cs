using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Pages.Company;

/// <summary>The customer area always works on the session's active customer; the code never comes from the URL.</summary>
public sealed class CompanyContext(CurrentUser user, AccessControl access, IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
{
    /// <summary>The active customer; none when the user acts for none (or it is gone from the ERP).</summary>
    public async Task<Maybe<CustomerDetails>> ActiveCustomerAsync(CancellationToken cancellationToken) =>
        await (await user.ActiveMembershipAsync(cancellationToken)).Match(
            async membership => (await customers.HandleAsync(new GetCustomer(membership.CustomerCode), cancellationToken)).ToMaybe(),
            () => Task.FromResult(Maybe<CustomerDetails>.None));

    public Task<bool> CanAsync(AccessAction action, Func<string, Maybe<EmailAddress>, Resource> resource, CustomerDetails customer, CancellationToken cancellationToken) =>
        access.CanAsync(action, resource(customer.Code, Salesperson(customer)), cancellationToken);

    /// <summary>The customer's salesperson as the access policies see it (an operator's scope).</summary>
    public static Maybe<EmailAddress> Salesperson(CustomerDetails customer) =>
        customer.Salesperson.ToMaybe().Bind(s => s.Email.ToMaybe()).Bind(email => EmailAddress.Parse(email).ToMaybe());
}
