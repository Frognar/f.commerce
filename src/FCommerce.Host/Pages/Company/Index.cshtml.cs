using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

public sealed class IndexModel(CompanyContext company) : PageModel
{
    public CustomerDetails? Customer { get; private set; }

    public bool CanSeeUsers { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var customer = await company.ActiveCustomerAsync(cancellationToken);
        if (customer is null)
        {
            return Page(); // signed in, but acting for no company
        }

        if (!await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerProfile(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        Customer = customer;
        CanSeeUsers = await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerUsers(code, salesperson), customer, cancellationToken);
        return Page();
    }
}
