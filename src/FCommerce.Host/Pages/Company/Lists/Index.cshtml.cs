using FCommerce.Application.Messaging;
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

/// <summary>The shopping lists of the signed-in user for the active customer; lists go to the cart (added or replacing it).</summary>
[RequiresFeature("shopping-lists")]
public sealed class IndexModel(
    CartAccess carts,
    IQueryHandler<GetShoppingLists, IReadOnlyList<ShoppingListSummary>> getLists,
    ICommandHandler<ShoppingListToCart, Result<CartChange, Error>> toCart,
    ICommandHandler<DeleteShoppingList, Result<Unit, Error>> delete,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public IReadOnlyList<ShoppingListSummary> Lists { get; private set; } = [];

    public ListOutcome Outcome { get; private set; } = ListOutcome.None;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            Lists = await getLists.HandleAsync(new GetShoppingLists(owner), cancellationToken);
            return Page();
        }, cancellationToken);

    public Task<IActionResult> OnPostToCartAsync(Guid id, bool replace, CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            Outcome = ListOutcome.Of(await toCart.HandleAsync(new ShoppingListToCart(owner, id, replace), cancellationToken), texts);
            Lists = await getLists.HandleAsync(new GetShoppingLists(owner), cancellationToken);
            return Page();
        }, cancellationToken);

    public Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            var deleted = await delete.HandleAsync(new DeleteShoppingList(owner, id), cancellationToken);
            Outcome = deleted.Match(_ => ListOutcome.Done(texts["ShoppingListDeleted"]), error => ListOutcome.Failed(texts.Describe(error)));
            Lists = await getLists.HandleAsync(new GetShoppingLists(owner), cancellationToken);
            return Page();
        }, cancellationToken);

    private async Task<IActionResult> WithOwnerAsync(Func<CartOwner, Task<IActionResult>> action, CancellationToken cancellationToken) =>
        await (await carts.ShoppingListsOwnerAsync(cancellationToken)).Match(action, () => Task.FromResult<IActionResult>(Forbid()));
}

/// <summary>What happened to a list: a message, what did not go to the cart, or a failure.</summary>
/// <param name="ToCart">The list went to the cart: offer a link to it.</param>
public sealed record ListOutcome(Maybe<string> Message, Maybe<string> Failure, IReadOnlyList<string> NotAdded, bool ToCart)
{
    public static ListOutcome None { get; } = new(Maybe<string>.None, Maybe<string>.None, [], false);

    public static ListOutcome Done(string message) => None with { Message = message };

    public static ListOutcome Failed(string failure) => None with { Failure = failure };

    /// <summary>The result of sending a list or a document to the cart.</summary>
    public static ListOutcome Of(Result<CartChange, Error> change, IStringLocalizer<SharedResource> texts) =>
        change.Match(
            result => new ListOutcome(
                Maybe.Some<string>(texts["ListAddedToCart", result.Added, result.ItemCount]),
                Maybe<string>.None,
                [.. result.Rejected.Select(r => texts["RejectedItem", r.Text, texts[$"Error.{r.Code}"] is { ResourceNotFound: false } text ? text.Value : r.Message].Value)],
                ToCart: true),
            error => Failed(texts.Describe(error.Details.IsEmpty ? error : error.Details[0])));
}
