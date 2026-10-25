using System.Globalization;
using FCommerce.Application.Messaging;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company.Orders;

/// <summary>The order history of the active customer: own orders, or all of them with the company order scope.</summary>
[RequiresFeature("ordering")]
public sealed class IndexModel(OrderAccess orders, IQueryHandler<GetOrders, Result<OrderPage, Error>> getOrders) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "number")]
    public string? Number { get; set; }

    /// <summary>Only the user's own orders (always so with the "own" order scope).</summary>
    [BindProperty(SupportsGet = true, Name = "mine")]
    public bool Mine { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    /// <summary>The user sees the orders of the whole company, so may narrow them to their own.</summary>
    public bool SeesCompany { get; private set; }

    public OrderPage Results { get; private set; } = new([], 1, 1, 0);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await (await orders.ViewerAsync(cancellationToken)).Match(viewer => ListAsync(viewer, cancellationToken), () => Task.FromResult<IActionResult>(Forbid()));

    private async Task<IActionResult> ListAsync(OrderViewer viewer, CancellationToken cancellationToken)
    {
        // Unknown filter values (edited URLs) are ignored rather than reported.
        Status = Status.ToMaybe().Where(OrderStatuses.All.Contains).OrNull();
        SeesCompany = !viewer.OnlyOwn;
        var placedBy = viewer.OnlyOwn || Mine ? viewer.UserId : Maybe<Guid>.None;
        return (await getOrders.HandleAsync(new GetOrders(viewer.Customer.Code, placedBy.OrNull(), null, Status, From, To, Number, PageNumber), cancellationToken))
            .Match<IActionResult>(
                page =>
                {
                    Results = page;
                    return Page();
                },
                error => BadRequest(error.Message));
    }

    /// <summary>A page of this list with the current filters.</summary>
    public string Href(int page)
    {
        var query = new QueryBuilder();
        Add(query, "status", Status.ToMaybe());
        Add(query, "from", From.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "to", To.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "number", Number.ToMaybe());
        Add(query, "mine", Mine ? "true" : Maybe<string>.None);
        Add(query, "p", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : Maybe<string>.None);
        return Url.CulturePage("/Company/Orders/Index") + query.ToQueryString();
    }

    private static void Add(QueryBuilder query, string name, Maybe<string> value) =>
        value.Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add(name, text), () => { });
}
