using FCommerce.Access.Contracts;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Host.Pages.Company.Orders;

/// <summary>Whose orders a customer user sees: the active customer's, only their own with the "own" order scope.</summary>
public sealed record OrderViewer(CustomerDetails Customer, Guid UserId, bool OnlyOwn);

/// <summary>
/// Orders of the current request, only from the session: the buyer placing an order (the active customer, the limit
/// and the delivery addresses of the membership) and who may see or cancel which order (access policies).
/// </summary>
public sealed class OrderAccess(CurrentUser user, CompanyContext company)
{
    /// <summary>The buyer's terms; none when the user may not order for the active customer.</summary>
    public async Task<Maybe<BuyerTerms>> BuyerAsync(CancellationToken cancellationToken = default)
    {
        var session = await user.SessionAsync(cancellationToken);
        var membership = await user.ActiveMembershipAsync(cancellationToken);
        var customer = await company.ActiveCustomerAsync(cancellationToken);
        var candidate = from s in session
                        from m in membership
                        from c in customer
                        select (Terms: Terms(s.UserId, m), Customer: c);
        return await candidate.Match(
            async c => await company.CanAsync(AccessAction.Submit, (code, salesperson) => new Resource.Orders(code, salesperson), c.Customer, cancellationToken)
                ? Maybe.Some(c.Terms)
                : Maybe<BuyerTerms>.None,
            () => Task.FromResult(Maybe<BuyerTerms>.None));
    }

    /// <summary>Who looks at the order list; none when the user may not see the active customer's orders.</summary>
    public async Task<Maybe<OrderViewer>> ViewerAsync(CancellationToken cancellationToken = default)
    {
        var session = await user.SessionAsync(cancellationToken);
        var membership = await user.ActiveMembershipAsync(cancellationToken);
        var candidate = from s in session
                        from m in membership
                        select (s.UserId, OnlyOwn: m.OrderScope != "company");
        var customer = await company.ActiveCustomerAsync(cancellationToken);
        return await (from c in candidate from details in customer select (c.UserId, c.OnlyOwn, Customer: details)).Match(
            async c => await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.Orders(code, salesperson), c.Customer, cancellationToken)
                ? Maybe.Some(new OrderViewer(c.Customer, c.UserId, c.OnlyOwn))
                : Maybe<OrderViewer>.None,
            () => Task.FromResult(Maybe<OrderViewer>.None));
    }

    /// <summary>Whether the user may see (read) or cancel (manage) this order of the active customer.</summary>
    public Task<bool> CanAsync(AccessAction action, OrderViewer viewer, OrderView order, CancellationToken cancellationToken = default) =>
        order.CustomerCode == viewer.Customer.Code
            ? company.CanAsync(action, (code, salesperson) => new Resource.Order(code, salesperson, order.PlacedBy), viewer.Customer, cancellationToken)
            : Task.FromResult(false);

    private static BuyerTerms Terms(Guid userId, MembershipView membership) =>
        new(userId, membership.CustomerCode, membership.OrderLimit, membership.OrderLimitCurrency, membership.DeliveryAddressCodes);
}
