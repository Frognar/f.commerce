using System.Globalization;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Orders;

/// <summary>Orders of all customers, newest first; a salesperson-scoped operator sees only their customers' orders.</summary>
[RequiresFeature("ordering")]
public sealed class IndexModel(IQueryHandler<GetOrders, Result<OrderPage, Error>> getOrders, CurrentUser user) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "customer")]
    public string? Customer { get; set; }

    [BindProperty(SupportsGet = true, Name = "number")]
    public string? Number { get; set; }

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public OrderPage Results { get; private set; } = new([], 1, 1, 0);

    /// <summary>True when the list is limited to the operator's own customers.</summary>
    public bool OwnCustomersOnly { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var salesperson = await user.SubjectAsync(cancellationToken) is Subject.Operator { Attributes.CustomerScope: CustomerScope.AsSalesperson } op
            ? op.Email.Value
            : Maybe<string>.None;
        OwnCustomersOnly = salesperson.Match(_ => true, () => false);
        Status = Status.ToMaybe().Where(OrderStatuses.All.Contains).OrNull();
        var customer = Customer.ToMaybe().Map(c => c.Trim()).Where(c => c.Length > 0);

        return (await getOrders.HandleAsync(new GetOrders(customer.OrNull(), null, salesperson.OrNull(), Status, From, To, Number, PageNumber), cancellationToken)).Match<IActionResult>(
            page =>
            {
                Results = page;
                return Page();
            },
            error => BadRequest(error.Message));
    }

    /// <summary>A page of the list with the current filters.</summary>
    public string Href(int page)
    {
        var query = new QueryBuilder();
        Add(query, "status", Status.ToMaybe());
        Add(query, "customer", Customer.ToMaybe());
        Add(query, "number", Number.ToMaybe());
        Add(query, "from", From.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "to", To.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "p", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : Maybe<string>.None);
        return Url.CulturePage("/Admin/Orders/Index") + query.ToQueryString();
    }

    private static void Add(QueryBuilder query, string name, Maybe<string> value) =>
        value.Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add(name, text), () => { });
}
