using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Products;

public sealed class DetailsModel(IQueryHandler<GetProduct, Result<ProductDetails, Error>> products, SupportedCultures cultures) : PageModel
{
    public ProductDetails? Product { get; private set; }

    public async Task<IActionResult> OnGetAsync(string sku, CancellationToken cancellationToken) =>
        (await products.HandleAsync(new GetProduct(cultures.ForRequest(), sku), cancellationToken)).Match<IActionResult>(
            product =>
            {
                Product = product;
                return Page();
            },
            error =>
            {
                Response.StatusCode = error.Kind is ErrorKind.NotFound or ErrorKind.Validation ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Page();
            });
}
