using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.SharedKernel.Optional;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

public sealed class IndexModel(CompanyContext company, Orders.OrderAccess orders) : PageModel
{
    /// <summary>None when the user is signed in but acts for no company.</summary>
    public Maybe<CustomerDetails> Customer { get; private set; }

    public bool HasCompany => Customer.Match(_ => true, () => false);

    public bool CanSeeUsers { get; private set; }

    public bool CanSeeApiKeys { get; private set; }

    public bool CanSeeDocuments { get; private set; }

    public bool CanSeeShoppingLists { get; private set; }

    public bool CanSeeOrders { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await (await company.ActiveCustomerAsync(cancellationToken)).Match(customer => ShowAsync(customer, cancellationToken), () => Task.FromResult<IActionResult>(Page()));

    private async Task<IActionResult> ShowAsync(CustomerDetails customer, CancellationToken cancellationToken)
    {
        if (!await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerProfile(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        Customer = customer;
        CanSeeUsers = await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken);
        CanSeeApiKeys = await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.ApiKeys(code, salesperson), customer, cancellationToken);
        CanSeeDocuments = await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), customer, cancellationToken);
        CanSeeShoppingLists = await company.CanAsync(AccessAction.Read, (code, _) => new Resource.ShoppingLists(code), customer, cancellationToken);
        CanSeeOrders = (await orders.ViewerAsync(cancellationToken)).Match(_ => true, () => false);
        return Page();
    }
}
