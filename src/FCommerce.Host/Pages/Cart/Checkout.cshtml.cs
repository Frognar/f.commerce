using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Company.Orders;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Cart;

/// <summary>The order form of the checkout page (all optional except what the cart review sent along).</summary>
[NullableEdge]
public sealed class CheckoutForm
{
    /// <summary>Chosen when the page is shown, so sending the form twice places one order.</summary>
    public Guid OrderId { get; set; }

    public long CartVersion { get; set; }

    /// <summary>The gross total shown, invariant culture.</summary>
    public string ExpectedGross { get; set; } = string.Empty;

    public string? CustomerReference { get; set; }

    public string? Notes { get; set; }

    public DateOnly? RequestedDate { get; set; }

    public string? AddressCode { get; set; }
}

/// <summary>
/// Checkout: the reviewed cart with frozen prices to confirm, the delivery address, the buyer's reference, notes and
/// a requested date. Placing the order takes its items out of the cart and opens the order.
/// </summary>
[RequiresFeature("ordering")]
public sealed class CheckoutModel(
    OrderAccess orders,
    CartAccess carts,
    IQueryHandler<GetCheckout, CheckoutView> getCheckout,
    ICommandHandler<PlaceOrder, Result<OrderPlacement, Error>> place,
    ICommandHandler<RemoveOrderedItems, Result<CartChange, Error>> removeOrdered,
    SupportedCultures cultures,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    [BindProperty]
    public CheckoutForm Form { get; set; } = new();

    public CheckoutView Checkout { get; private set; } = new(0, false, [], [], null, [], null, DateOnly.MinValue);

    /// <summary>The order was not placed: why (the cart changed, a limit, …).</summary>
    public Maybe<string> Failure { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await (await orders.BuyerAsync(cancellationToken)).Match<Task<IActionResult>>(
            async buyer =>
            {
                await LoadAsync(buyer, keepChoices: false, cancellationToken);
                return Page();
            },
            () => Task.FromResult<IActionResult>(Forbid()));

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken) =>
        await (await orders.BuyerAsync(cancellationToken)).Match(buyer => PlaceAsync(buyer, cancellationToken), () => Task.FromResult<IActionResult>(Forbid()));

    private async Task<IActionResult> PlaceAsync(BuyerTerms buyer, CancellationToken cancellationToken)
    {
        var placed = await FormNumbers.Decimal(Form.ExpectedGross, "order.cart-changed", string.Empty)
            .BindAsync(gross => place.HandleAsync(
                new PlaceOrder(Form.OrderId, buyer, cultures.ForRequest(), Form.CartVersion, gross, Form.CustomerReference, Form.Notes, Form.RequestedDate, Form.AddressCode),
                cancellationToken));
        return await placed.Match<Task<IActionResult>>(
            async order =>
            {
                await (await carts.OwnerAsync(cancellationToken)).Match(
                    owner => removeOrdered.HandleAsync(new RemoveOrderedItems(owner, order.OrderId, [.. order.Items.Select(i => new CartItemData(i.Sku, i.Quantity))]), cancellationToken),
                    () => Task.FromResult(Result.Ok<CartChange, Error>(new CartChange(0, 0, []))));
                return LocalRedirect(Url.CulturePage("/Company/Orders/Details", new { id = order.OrderId, placed = true }));
            },
            async error =>
            {
                var single = error.Details.IsEmpty ? [error] : error.Details.AsImmutableArray().AsEnumerable();
                var formErrors = single.Where(e => e.Field.Exists(_ => true)).ToList();
                formErrors.ForEach(e => ModelState.AddModelError($"Form.{Field(e.Field.ValueOr(string.Empty))}", texts.Describe(e)));
                Failure = single.FirstOrNone(e => !e.Field.Exists(_ => true)).Map(texts.Describe);
                await LoadAsync(buyer, keepChoices: true, cancellationToken);
                return Page();
            });
    }

    /// <summary>Error fields of the order ("customerReference") as form fields ("CustomerReference").</summary>
    private static string Field(string field) => field.Length == 0 ? field : char.ToUpperInvariant(field[0]) + field[1..];

    /// <summary>The reviewed cart; a new order id and the cart version and total shown, the buyer's choices kept after a failure.</summary>
    private async Task LoadAsync(BuyerTerms buyer, bool keepChoices, CancellationToken cancellationToken)
    {
        Checkout = await getCheckout.HandleAsync(new GetCheckout(buyer, cultures.ForRequest()), cancellationToken);
        Form.CartVersion = Checkout.CartVersion;
        Form.ExpectedGross = Checkout.Total?.Gross.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        if (!keepChoices || Form.OrderId == Guid.Empty)
        {
            Form.OrderId = Guid.CreateVersion7();
            Form.AddressCode = Checkout.SuggestedAddressCode;
        }

        ModelState.Remove("Form.CartVersion");
        ModelState.Remove("Form.ExpectedGross");
        ModelState.Remove("Form.OrderId");
    }
}
