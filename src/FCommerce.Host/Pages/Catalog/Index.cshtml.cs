using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Catalog;

public sealed class IndexModel(
    IQueryHandler<SearchProducts, Result<ProductPage, Error>> search,
    IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>> categories,
    SupportedCultures cultures) : PageModel
{
    public const string ResultsId = "results";

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "category")]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public ProductPage Results { get; private set; } = new([], 1, 1, 0);

    public IReadOnlyList<CategoryView> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var requestCultures = cultures.ForRequest();
        var page = await search.HandleAsync(new SearchProducts(requestCultures, Query, Category, PageNumber), cancellationToken);
        var tree = await categories.HandleAsync(new GetCategoryTree(requestCultures), cancellationToken);

        return (from results in page from categoryTree in tree select (results, categoryTree)).Match<IActionResult>(
            loaded =>
            {
                (Results, Categories) = loaded;
                return Request.IsHtmxFor(ResultsId) ? Partial("_ProductResults", this) : Page();
            },
            error => BadRequest(error.Message));
    }

    /// <summary>Route values for a link that keeps the current filters but changes some of them.</summary>
    public Dictionary<string, string?> Link(string? category = null, int? page = null, bool keepCategory = true) =>
        new()
        {
            ["q"] = string.IsNullOrWhiteSpace(Query) ? null : Query,
            ["category"] = category ?? (keepCategory ? Category : null),
            ["p"] = page is > 1 ? page.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        };
}
