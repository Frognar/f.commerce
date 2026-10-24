using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Products;

public sealed class DetailsModel(IQueryHandler<GetProduct, Result<ProductDetails, Error>> products, SupportedCultures cultures) : PageModel
{
    private Maybe<ProductDetails> product;

    public bool Found => product.Match(_ => true, () => false);

    /// <summary>The product; read only when <see cref="Found"/>.</summary>
    public ProductDetails Product => product.Match(p => p, () => throw new InvalidOperationException("The product is not loaded."));

    public async Task<IActionResult> OnGetAsync(string sku, CancellationToken cancellationToken) =>
        (await products.HandleAsync(new GetProduct(cultures.ForRequest(), sku), cancellationToken)).Match<IActionResult>(
            found =>
            {
                product = found;
                return Page();
            },
            error =>
            {
                Response.StatusCode = error.Kind is ErrorKind.NotFound or ErrorKind.Validation ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Page();
            });
}
