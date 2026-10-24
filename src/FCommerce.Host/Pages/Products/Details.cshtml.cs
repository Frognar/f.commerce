using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Catalog;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Products;

public sealed class DetailsModel(IQueryHandler<GetProduct, Result<ProductDetails, Error>> products, CatalogOffers offers, SupportedCultures cultures) : PageModel
{
    private Maybe<ProductDetails> product;

    public bool Found => product.Match(_ => true, () => false);

    /// <summary>The product; read only when <see cref="Found"/>.</summary>
    public ProductDetails Product => product.Match(p => p, () => throw new InvalidOperationException("The product is not loaded."));

    /// <summary>Price and stock for the user (or the visitor); none when they could not be read.</summary>
    public Maybe<ProductOffer> Offer { get; private set; }

    /// <summary>Sign-in that returns to this product.</summary>
    public string SignInHref => Url.CulturePage("/Auth/SignIn") + QueryString.Create("returnUrl", Request.Path + Request.QueryString);

    public async Task<IActionResult> OnGetAsync(string sku, CancellationToken cancellationToken) =>
        await (await products.HandleAsync(new GetProduct(cultures.ForRequest(), sku), cancellationToken)).Match<Task<IActionResult>>(
            async found =>
            {
                product = found;
                Offer = await offers.ProductAsync(found.Sku, cancellationToken);
                return Page();
            },
            error =>
            {
                Response.StatusCode = error.Kind is ErrorKind.NotFound or ErrorKind.Validation ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Task.FromResult<IActionResult>(Page());
            });
}
