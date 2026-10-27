using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Contracts;

/// <summary>
/// Whose cart: a user acting for a customer. The host builds it only from the session (the active customer of the
/// signed-in user), never from the request.
/// </summary>
public sealed record CartOwner(Guid UserId, string CustomerCode);

/// <param name="Quantity">More than zero, at most 3 decimal places.</param>
public sealed record CartItemData(string Sku, decimal Quantity);

/// <summary>Adds items (quantities add up); SKUs the catalog does not offer are rejected and reported.</summary>
/// <param name="Source">See <see cref="CartSources"/>.</param>
public sealed record AddToCart(CartOwner Owner, IReadOnlyList<CartItemData> Items, string Source) : ICommand<Result<CartChange, Error>>;

/// <summary>Empties the cart and adds the items (unknown SKUs are rejected and reported).</summary>
public sealed record ReplaceCart(CartOwner Owner, IReadOnlyList<CartItemData> Items, string Source) : ICommand<Result<CartChange, Error>>;

/// <summary>Adds pasted lines ("SKU quantity" per line); unreadable lines and unknown SKUs are reported.</summary>
public sealed record AddPastedLines(CartOwner Owner, string Text) : ICommand<Result<CartChange, Error>>;

/// <summary>Sets the quantity of an item in the cart; zero removes it.</summary>
public sealed record ChangeCartQuantity(CartOwner Owner, string Sku, decimal Quantity) : ICommand<Result<CartChange, Error>>;

public sealed record RemoveFromCart(CartOwner Owner, string Sku) : ICommand<Result<CartChange, Error>>;

public sealed record ClearCart(CartOwner Owner) : ICommand<Result<CartChange, Error>>;

/// <summary>
/// Takes the quantities of a placed order out of the cart; what was added meanwhile stays. Idempotent per order.
/// </summary>
public sealed record RemoveOrderedItems(CartOwner Owner, Guid OrderId, IReadOnlyList<CartItemData> Items) : ICommand<Result<CartChange, Error>>;

/// <param name="ItemCount">Products in the cart after the change.</param>
/// <param name="Added">Items that were added (after merging the same SKU).</param>
/// <param name="Rejected">Lines or SKUs that were not added, and why.</param>
public sealed record CartChange(int ItemCount, int Added, IReadOnlyList<CartRejection> Rejected);

/// <param name="Line">The line number of pasted text, if the rejection is about one.</param>
/// <param name="Code">"product.unknown", "paste.format", "paste.quantity", a quantity code, …</param>
public sealed record CartRejection(int? Line, string Text, string Code, string Message);

public static class CartSources
{
    public const string Product = "product";
    public const string Catalog = "catalog";
    public const string QuickOrder = "quick-order";
    public const string Paste = "paste";
    public const string ShoppingList = "shopping-list";
    public const string Document = "document";
    public const string Order = "order";
    public const string Api = "api";
}

/// <summary>How many products are in the cart (for the header).</summary>
public sealed record GetCartSize(CartOwner Owner) : IQuery<int>;

/// <summary>
/// The cart with current prices (the owner's customer prices, as pricing resolves them), availability and what
/// prevents ordering it. Texts in the first matching culture of <c>Cultures</c>.
/// </summary>
/// <param name="OrderLimit">The owner's order limit (from the session, like the owner); null when unlimited.</param>
public sealed record GetCart(CartOwner Owner, IReadOnlyList<string> Cultures, CartOrderLimit? OrderLimit = null) : IQuery<CartView>;

/// <summary>The highest net value of an order, in a currency.</summary>
public sealed record CartOrderLimit(decimal Amount, string Currency);

/// <param name="StockDisplay">"hidden", "levels" or "exact": quantities of stock are given only in "exact".</param>
/// <param name="Issues">Cart issues: "empty", "customer-blocked", "mixed-currencies", "over-limit".</param>
/// <param name="CanOrder">Nothing prevents placing an order from the cart.</param>
/// <param name="Version">Version of the cart: changes with every change of its items.</param>
/// <param name="Complete">
/// Products, prices, stock and the customer were all read; when false the review may be too lenient, so an order
/// must not be placed from this view.
/// </param>
/// <param name="OrderLimit">The limit the review checked against.</param>
public sealed record CartView(
    IReadOnlyList<CartLineView> Lines,
    IReadOnlyList<CartTotalView> Totals,
    IReadOnlyList<string> Issues,
    bool CanOrder,
    string StockDisplay,
    long Version,
    bool Complete,
    CartOrderLimit? OrderLimit);

/// <param name="Name">None when the product is no longer in the catalog.</param>
/// <param name="PriceStatus">"priced", "on-request" (no price for this customer).</param>
/// <param name="Issues">See <see cref="CartLineIssueView"/>.</param>
/// <param name="Blocking">An issue of the line prevents ordering.</param>
public sealed record CartLineView(
    string Sku,
    string? Name,
    string? Unit,
    string? ImageId,
    bool Discontinued,
    decimal Quantity,
    string PriceStatus,
    string? Currency,
    decimal? VatRate,
    decimal? UnitNet,
    decimal? UnitGross,
    decimal? NetAmount,
    IReadOnlyList<CartLineIssueView> Issues,
    bool Blocking,
    string? PriceListCode = null);

/// <param name="Code">See <see cref="CartLineIssues"/>.</param>
/// <param name="Available">What is on hand; only when the stock display is "exact".</param>
/// <param name="NextDelivery">For a backorder, when the next delivery is expected.</param>
public sealed record CartLineIssueView(string Code, bool Blocks, decimal? Available, DateOnly? NextDelivery);

public static class CartLineIssues
{
    /// <summary>The product is no longer in the catalog (blocks).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>No price for this buyer (blocks).</summary>
    public const string NoPrice = "no-price";

    /// <summary>Discontinued and more than is on hand (blocks).</summary>
    public const string DiscontinuedShortage = "discontinued-shortage";

    /// <summary>More than is on hand: the rest is delivered later (does not block).</summary>
    public const string Backorder = "backorder";
}

public sealed record CartTotalView(string Currency, decimal Net, IReadOnlyList<CartVatView> Rates, decimal Vat, decimal Gross);

public sealed record CartVatView(decimal Rate, decimal Net, decimal Vat);
