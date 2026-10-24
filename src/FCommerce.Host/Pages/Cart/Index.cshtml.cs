using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Catalog.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Cart;

/// <summary>
/// The cart of the active customer: lines with current prices, totals, what prevents ordering, quick order. Every
/// change answers htmx with the cart fragment (and the header link out of band), other requests with the page.
/// </summary>
[RequiresFeature("cart")]
public sealed class IndexModel(
    CartAccess carts,
    IQueryHandler<GetCart, CartView> getCart,
    IQueryHandler<SearchProducts, Result<ProductPage, Error>> search,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    ICommandHandler<AddToCart, Result<CartChange, Error>> add,
    ICommandHandler<AddPastedLines, Result<CartChange, Error>> paste,
    ICommandHandler<ChangeCartQuantity, Result<CartChange, Error>> change,
    ICommandHandler<RemoveFromCart, Result<CartChange, Error>> remove,
    ICommandHandler<ClearCart, Result<CartChange, Error>> clear,
    ICommandHandler<SaveCartAsShoppingList, Result<Guid, Error>> saveList,
    SupportedCultures cultures,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public const string ContentId = "cart";
    public const int MaxSuggestions = 8;

    public CartView Cart { get; private set; } = new([], [], [], false, "hidden");

    /// <summary>What the last change did: a message and what was not added.</summary>
    public Maybe<string> Message { get; private set; }

    public IReadOnlyList<CartRejection> Rejected { get; private set; } = [];

    public Maybe<string> Failure { get; private set; }

    public bool QuickOrder { get; private set; }

    /// <summary>The cart can be saved as a shopping list (feature and access).</summary>
    public bool ShoppingLists { get; private set; }

    /// <summary>Pasted text that had unreadable lines, offered again for correcting.</summary>
    public string PastedText { get; private set; } = string.Empty;

    public IReadOnlyList<ProductSummary> Suggestions { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            await LoadAsync(owner, cancellationToken);
            return Page();
        }, cancellationToken);

    /// <summary>Products matching typed text, for the "add by SKU" field.</summary>
    public Task<IActionResult> OnGetSuggestAsync(string? sku, CancellationToken cancellationToken) =>
        WithOwnerAsync(async _ =>
        {
            if (!await QuickOrderOnAsync(cancellationToken))
            {
                return NotFound();
            }

            Suggestions = (sku ?? string.Empty).Trim().Length < 2 ? [] : (await search.HandleAsync(new SearchProducts(cultures.ForRequest(), sku, null, null, 1, MaxSuggestions), cancellationToken))
                .Match(page => page.Items, _ => []);
            return Partial("_CartSuggestions", this);
        }, cancellationToken);

    /// <summary>"Add to cart" from a product list or page; htmx gets a short confirmation, others go back.</summary>
    public Task<IActionResult> OnPostAddAsync(string? sku, string? quantity, string? source, string? returnUrl, CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            var added = await FormNumbers.Decimal(quantity ?? "1", "quantity.invalid", "quantity")
                .BindAsync(q => add.HandleAsync(new AddToCart(owner, [new CartItemData(sku ?? string.Empty, q)], source ?? CartSources.Product), cancellationToken));
            if (Request.IsHtmx())
            {
                return Partial("_AddedToCart", added.Match(
                    change => new AddedToCart(change.Rejected.Count == 0, change.Rejected.Count == 0 ? texts["AddedToCart"] : Rejection(change.Rejected[0]), change.ItemCount),
                    error => new AddedToCart(false, texts.Describe(error), Maybe<int>.None)));
            }

            return LocalRedirect(returnUrl.ToMaybe().Where(Url.IsLocalUrl).ValueOr(() => Url.CulturePage("/Cart/Index")));
        }, cancellationToken);

    public Task<IActionResult> OnPostQuickAddAsync(string? sku, string? quantity, CancellationToken cancellationToken) =>
        ChangeAsync(
            async owner => await FormNumbers.Decimal(quantity ?? "1", "quantity.invalid", "quantity")
                .BindAsync(q => add.HandleAsync(new AddToCart(owner, [new CartItemData(sku ?? string.Empty, q)], CartSources.QuickOrder), cancellationToken)),
            quickOrder: true,
            cancellationToken);

    public Task<IActionResult> OnPostPasteAsync(string? text, CancellationToken cancellationToken) =>
        ChangeAsync(owner => paste.HandleAsync(new AddPastedLines(owner, text ?? string.Empty), cancellationToken), quickOrder: true, cancellationToken, pasted: text ?? string.Empty);

    public Task<IActionResult> OnPostQuantityAsync(string? sku, string? quantity, CancellationToken cancellationToken) =>
        ChangeAsync(
            async owner => await FormNumbers.Decimal(quantity ?? string.Empty, "quantity.invalid", "quantity")
                .BindAsync(q => change.HandleAsync(new ChangeCartQuantity(owner, sku ?? string.Empty, q), cancellationToken)),
            quickOrder: false,
            cancellationToken);

    public Task<IActionResult> OnPostRemoveAsync(string? sku, CancellationToken cancellationToken) =>
        ChangeAsync(owner => remove.HandleAsync(new RemoveFromCart(owner, sku ?? string.Empty), cancellationToken), quickOrder: false, cancellationToken);

    public Task<IActionResult> OnPostClearAsync(CancellationToken cancellationToken) =>
        ChangeAsync(owner => clear.HandleAsync(new ClearCart(owner), cancellationToken), quickOrder: false, cancellationToken);

    /// <summary>Saves the cart as a new shopping list and opens it.</summary>
    public Task<IActionResult> OnPostSaveListAsync(string? name, CancellationToken cancellationToken) =>
        WithOwnerAsync(async owner =>
        {
            var listsOwner = await carts.ShoppingListsOwnerAsync(cancellationToken);
            if (!listsOwner.Exists(o => o == owner))
            {
                return NotFound();
            }

            return await (await saveList.HandleAsync(new SaveCartAsShoppingList(owner, name ?? string.Empty), cancellationToken)).Match<Task<IActionResult>>(
                id => Task.FromResult<IActionResult>(LocalRedirect(Url.CulturePage("/Company/Lists/Details", new { id }))),
                async error =>
                {
                    Failure = texts.Describe(error.Details.IsEmpty ? error : error.Details[0]);
                    await LoadAsync(owner, cancellationToken);
                    return Page();
                });
        }, cancellationToken);

    /// <summary>A rejected line in words: "Wiersz 3 (SR-M8 dużo): …".</summary>
    public string Rejection(CartRejection rejection)
    {
        var reason = texts[$"Error.{rejection.Code}"] is { ResourceNotFound: false } text ? text.Value : rejection.Message;
        return rejection.Line is { } line ? texts["RejectedLine", line, rejection.Text, reason] : texts["RejectedItem", rejection.Text, reason];
    }

    private async Task<IActionResult> ChangeAsync(Func<CartOwner, Task<Result<CartChange, Error>>> apply, bool quickOrder, CancellationToken cancellationToken, string pasted = "") =>
        await WithOwnerAsync(async owner =>
        {
            if (quickOrder && !await QuickOrderOnAsync(cancellationToken))
            {
                return NotFound();
            }

            var changed = await apply(owner);
            changed.Switch(
                result =>
                {
                    Rejected = result.Rejected;
                    Message = result.Added > 0 ? Maybe.Some<string>(texts["AddedItems", result.Added]) : Maybe<string>.None;
                    PastedText = result.Rejected.Count > 0 ? pasted : string.Empty;
                },
                error => Failure = texts.Describe(error.Details.IsEmpty ? error : error.Details[0]));
            await LoadAsync(owner, cancellationToken);
            return Request.IsHtmx() ? Partial("_CartContent", this) : Page();
        }, cancellationToken);

    private async Task<IActionResult> WithOwnerAsync(Func<CartOwner, Task<IActionResult>> action, CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(action, () => Task.FromResult<IActionResult>(Forbid()));

    private async Task LoadAsync(CartOwner owner, CancellationToken cancellationToken)
    {
        Cart = await getCart.HandleAsync(new GetCart(owner, cultures.ForRequest()), cancellationToken);
        QuickOrder = await QuickOrderOnAsync(cancellationToken);
        ShoppingLists = (await carts.ShoppingListsOwnerAsync(cancellationToken)).Match(_ => true, () => false);
    }

    private async Task<bool> QuickOrderOnAsync(CancellationToken cancellationToken) =>
        (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.QuickOrder.Value);
}

/// <summary>The answer to "add to cart" from a product list or page.</summary>
/// <param name="Count">Products in the cart now, for the header link; none when nothing changed.</param>
public sealed record AddedToCart(bool Success, string Message, Maybe<int> Count);
