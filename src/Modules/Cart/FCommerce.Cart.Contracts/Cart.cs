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
}

/// <summary>How many products are in the cart (for the header).</summary>
public sealed record GetCartSize(CartOwner Owner) : IQuery<int>;

/// <summary>
/// The cart with current prices (the owner's customer prices, as pricing resolves them), availability and what
/// prevents ordering it. Texts in the first matching culture of <c>Cultures</c>.
/// </summary>
public sealed record GetCart(CartOwner Owner, IReadOnlyList<string> Cultures) : IQuery<CartView>;

/// <param name="StockDisplay">"hidden", "levels" or "exact": quantities of stock are given only in "exact".</param>
/// <param name="Issues">Cart issues: "empty", "customer-blocked".</param>
/// <param name="CanOrder">Nothing prevents placing an order from the cart.</param>
public sealed record CartView(
    IReadOnlyList<CartLineView> Lines,
    IReadOnlyList<CartTotalView> Totals,
    IReadOnlyList<string> Issues,
    bool CanOrder,
    string StockDisplay);

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
    bool Blocking);

/// <param name="Code">"unavailable", "no-price", "discontinued-shortage" (blocks) or "backorder" (delivered later).</param>
/// <param name="Available">What is on hand; only when the stock display is "exact".</param>
/// <param name="NextDelivery">For a backorder, when the next delivery is expected.</param>
public sealed record CartLineIssueView(string Code, bool Blocks, decimal? Available, DateOnly? NextDelivery);

public sealed record CartTotalView(string Currency, decimal Net, IReadOnlyList<CartVatView> Rates, decimal Vat, decimal Gross);

public sealed record CartVatView(decimal Rate, decimal Net, decimal Vat);
