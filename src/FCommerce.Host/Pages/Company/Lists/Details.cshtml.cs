using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Cart;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Company.Lists;

/// <summary>One shopping list of the owner: products with quantities, renaming, sending to the cart, deleting.</summary>
[RequiresFeature("shopping-lists")]
public sealed class DetailsModel(
    CartAccess carts,
    IQueryHandler<GetShoppingList, Result<ShoppingListView, Error>> getList,
    ICommandHandler<RenameShoppingList, Result<Unit, Error>> rename,
    ICommandHandler<ChangeShoppingListItem, Result<Unit, Error>> changeItem,
    ICommandHandler<ShoppingListToCart, Result<CartChange, Error>> toCart,
    ICommandHandler<DeleteShoppingList, Result<Unit, Error>> delete,
    SupportedCultures cultures,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    private Maybe<ShoppingListView> list;

    /// <summary>The list; read only when the page renders (handlers answer not found otherwise).</summary>
    public ShoppingListView List => list.Match(l => l, () => throw new InvalidOperationException("The list is not loaded."));

    public ListOutcome Outcome { get; private set; } = ListOutcome.None;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        WithListAsync(id, _ => Task.FromResult(ListOutcome.None), cancellationToken);

    public Task<IActionResult> OnPostRenameAsync(Guid id, string? name, CancellationToken cancellationToken) =>
        WithListAsync(id, async owner => Done(await rename.HandleAsync(new RenameShoppingList(owner, id, name ?? string.Empty), cancellationToken), "ShoppingListRenamed"), cancellationToken);

    public Task<IActionResult> OnPostQuantityAsync(Guid id, string? sku, string? quantity, CancellationToken cancellationToken) =>
        WithListAsync(id, async owner => Done(
            await FormNumbers.Decimal(quantity ?? string.Empty, "quantity.invalid", "quantity")
                .BindAsync(q => changeItem.HandleAsync(new ChangeShoppingListItem(owner, id, sku ?? string.Empty, q), cancellationToken)),
            "ShoppingListChanged"), cancellationToken);

    public Task<IActionResult> OnPostToCartAsync(Guid id, bool replace, CancellationToken cancellationToken) =>
        WithListAsync(id, async owner => ListOutcome.Of(await toCart.HandleAsync(new ShoppingListToCart(owner, id, replace), cancellationToken), texts), cancellationToken);

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await (await carts.ShoppingListsOwnerAsync(cancellationToken)).Match(
            async owner => (await delete.HandleAsync(new DeleteShoppingList(owner, id), cancellationToken)).Match<IActionResult>(
                _ => LocalRedirect(Url.CulturePage("/Company/Lists/Index")),
                _ => NotFound()),
            () => Task.FromResult<IActionResult>(Forbid()));

    private ListOutcome Done(Result<Unit, Error> result, string message) =>
        result.Match(_ => ListOutcome.Done(texts[message]), error => ListOutcome.Failed(texts.Describe(error.Details.IsEmpty ? error : error.Details[0])));

    /// <summary>Runs the action for the owner, then loads the list; another owner's or a missing list is not found.</summary>
    private async Task<IActionResult> WithListAsync(Guid id, Func<CartOwner, Task<ListOutcome>> action, CancellationToken cancellationToken) =>
        await (await carts.ShoppingListsOwnerAsync(cancellationToken)).Match(
            async owner =>
            {
                Outcome = await action(owner);
                list = (await getList.HandleAsync(new GetShoppingList(owner, id, cultures.ForRequest()), cancellationToken)).ToMaybe();
                return list.Match<IActionResult>(_ => Page(), NotFound);
            },
            () => Task.FromResult<IActionResult>(Forbid()));
}
