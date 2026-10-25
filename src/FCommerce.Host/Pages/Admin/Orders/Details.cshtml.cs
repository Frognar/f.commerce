using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Pages.Company;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Orders;

/// <summary>An order as the seller sees it (read only; the ERP confirms, rejects and cancels). Outside the operator's scope it is not found.</summary>
[RequiresFeature("ordering")]
public sealed class DetailsModel(
    IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    AccessControl access) : PageModel
{
    private Maybe<(OrderView Order, Maybe<CustomerDetails> Customer)> loaded;

    public OrderView Order => loaded.Match(l => l.Order, () => throw new InvalidOperationException("The order is not loaded."));

    /// <summary>The customer's name, while the ERP still sends the customer.</summary>
    public string CustomerName => loaded.Bind(l => l.Customer).Map(c => c.Name).ValueOr(Order.CustomerCode);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = (await getOrder.HandleAsync(new GetOrder(id), cancellationToken)).ToMaybe();
        return await order.Match<Task<IActionResult>>(
            async found =>
            {
                var customer = (await customers.HandleAsync(new GetCustomer(found.CustomerCode), cancellationToken)).ToMaybe();
                var salesperson = customer.Bind(CompanyContext.Salesperson);
                if (!await access.CanAsync(AccessAction.Read, new Resource.Order(found.CustomerCode, salesperson, found.PlacedBy), cancellationToken))
                {
                    return NotFound();
                }

                loaded = (found, customer);
                return Page();
            },
            () => Task.FromResult<IActionResult>(NotFound()));
    }
}
