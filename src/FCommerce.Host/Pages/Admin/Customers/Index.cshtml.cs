using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
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
            : Maybe<string>.None;
        OwnCustomersOnly = salesperson.Match(_ => true, () => false);

        return (await search.HandleAsync(new SearchCustomers(Query, salesperson.OrNull(), PageNumber), cancellationToken)).Match<IActionResult>(
            results =>
            {
                Results = results;
                return Request.IsHtmxFor(ResultsId) ? Partial("_CustomerResults", this) : Page();
            },
            error => BadRequest(error.Message));
    }

    /// <summary>A page of the list with the current search.</summary>
    public string Href(int page)
    {
        var query = new QueryBuilder();
        Query.ToMaybe().Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add("q", text), () => { });
        if (page > 1)
        {
            query.Add("p", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Url.CulturePage("/Admin/Customers/Index") + query.ToQueryString();
    }
}
