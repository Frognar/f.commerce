using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Customers;

/// <summary>Customer list; a salesperson-scoped operator sees only their own customers.</summary>
public sealed class IndexModel(IQueryHandler<SearchCustomers, Result<CustomerPage, Error>> search, CurrentUser user) : PageModel
{
    public const string ResultsId = "customer-results";

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public CustomerPage Results { get; private set; } = new([], 1, 1, 0);

    /// <summary>True when the list is limited to the operator's own customers.</summary>
    public bool OwnCustomersOnly { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var salesperson = await user.SubjectAsync(cancellationToken) is Subject.Operator { Attributes.CustomerScope: CustomerScope.AsSalesperson } op
            ? op.Email.Value
            : null;
        OwnCustomersOnly = salesperson is not null;

        return (await search.HandleAsync(new SearchCustomers(Query, salesperson, PageNumber), cancellationToken)).Match<IActionResult>(
            results =>
            {
                Results = results;
                return Request.IsHtmxFor(ResultsId) ? Partial("_CustomerResults", this) : Page();
            },
            error => BadRequest(error.Message));
    }

    public string Href(int? page = null)
    {
        var query = new QueryBuilder();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            query.Add("q", Query);
        }

        if (page is > 1)
        {
            query.Add("p", page.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Url.CulturePage("/Admin/Customers/Index") + query.ToQueryString();
    }
}
