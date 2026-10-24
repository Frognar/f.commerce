using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Catalog;

public sealed class IndexModel(
    IQueryHandler<SearchProducts, Result<ProductPage, Error>> search,
    IQueryHandler<GetCategoryTree, Result<IReadOnlyList<CategoryView>, Error>> categories,
    CatalogOffers offers,
    Cart.CartAccess carts,
    SupportedCultures cultures) : PageModel
{
    public const string ResultsId = "results";

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "category")]
    public string? Category { get; set; }

    /// <summary>Selected attribute values as <see cref="FilterValue"/> tokens.</summary>
    [BindProperty(SupportsGet = true, Name = "f")]
    public IReadOnlyList<string> Filters { get; set; } = [];

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public ProductPage Results { get; private set; } = new([], 1, 1, 0, []);

    public IReadOnlyList<CategoryView> Categories { get; private set; } = [];

    /// <summary>Prices and stock of the products on the page, for the signed-in user (or the visitor).</summary>
    public ListingOffers Offers { get; private set; } = ListingOffers.None;

    /// <summary>The user may order for the active customer: products get an "add to cart" form.</summary>
    public bool CanAddToCart { get; private set; }

    /// <summary>URL token of a filter: the escaped code cannot contain '=', so the first '=' separates code and value.</summary>
    public static string FilterValue(string code, string value) => $"{Uri.EscapeDataString(code)}={value}";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var requestCultures = cultures.ForRequest();
        var page = await search.HandleAsync(new SearchProducts(requestCultures, Query, Category, ParseFilters(), PageNumber), cancellationToken);
        var tree = await categories.HandleAsync(new GetCategoryTree(requestCultures), cancellationToken);

        return await (from results in page from categoryTree in tree select (results, categoryTree)).Match<Task<IActionResult>>(
            async loaded =>
            {
                (Results, Categories) = loaded;
                Offers = await offers.ListAsync([.. Results.Items.Select(product => product.Sku)], cancellationToken);
                CanAddToCart = (await carts.OwnerAsync(cancellationToken)).Match(_ => true, () => false);
                return Request.IsHtmxFor(ResultsId) ? Partial("_ProductResults", this) : Page();
            },
            error => Task.FromResult<IActionResult>(BadRequest(error.Message)));
    }

    /// <summary>Sign-in that returns to this page of results (also from an htmx response, which has its own URL).</summary>
    public string SignInHref => Url.CulturePage("/Auth/SignIn") + QueryString.Create("returnUrl", Href(page: Results.Page));

    /// <summary>Link to the catalogue with the current search, optionally changing the category or the page.</summary>
    public string Href(Maybe<string> category = default, Maybe<int> page = default, bool keepCategory = true, bool keepFilters = true)
    {
        var query = new QueryBuilder();
        Query.ToMaybe().Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add("q", text), () => { });
        category.Or(() => Category.ToMaybe().Where(_ => keepCategory)).Switch(code => query.Add("category", code), () => { });
        if (keepFilters && Filters.Count > 0)
        {
            query.Add("f", Filters);
        }

        page.Where(number => number > 1).Switch(number => query.Add("p", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), () => { });

        return Url.CulturePage("/Catalog/Index") + query.ToQueryString();
    }

    private List<AttributeFilter> ParseFilters() =>
        [.. Filters
            .Select(token => token.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .Select(parts => new AttributeFilter(Uri.UnescapeDataString(parts[0]), parts[1]))];
}
