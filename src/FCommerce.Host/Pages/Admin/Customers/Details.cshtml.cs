using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Customers;

public sealed class DetailsModel(IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers, AccessControl access) : PageModel
{
    public CustomerDetails? Customer { get; private set; }

    public async Task<IActionResult> OnGetAsync(string code, CancellationToken cancellationToken)
    {
        var found = (await customers.HandleAsync(new GetCustomer(code), cancellationToken)).Match(c => c, _ => (CustomerDetails?)null);
        if (found is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        var salesperson = found.Salesperson?.Email is { } email ? EmailAddress.Parse(email).Match(e => e, _ => (EmailAddress?)null) : null;
        if (!await access.CanAsync(AccessAction.Read, new Resource.CustomerProfile(found.Code, salesperson), cancellationToken))
        {
            return Forbid();
        }

        Customer = found;
        return Page();
    }
}
