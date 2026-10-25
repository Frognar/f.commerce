using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Ordering.Contracts;

/// <summary>
/// Who orders and within which terms. The host builds it only from the session: the signed-in user, their active
/// customer and the attributes of that membership.
/// </summary>
/// <param name="OrderLimit">Highest net value of an order, in <paramref name="OrderLimitCurrency"/>; null: unlimited.</param>
/// <param name="AllowedAddressCodes">Delivery addresses the buyer may order to; null: all of the customer's.</param>
public sealed record BuyerTerms(Guid UserId, string CustomerCode, decimal? OrderLimit, string? OrderLimitCurrency, IReadOnlyList<string>? AllowedAddressCodes);

/// <summary>What the checkout page shows: the reviewed cart, the delivery addresses, whether the order can be placed.</summary>
public sealed record GetCheckout(BuyerTerms Buyer, IReadOnlyList<string> Cultures) : IQuery<CheckoutView>;

/// <param name="CartVersion">Sent back with the order, so a cart changed meanwhile is not ordered unseen.</param>
/// <param name="Issues">"cart-incomplete", "empty", "customer-blocked", "mixed-currencies", "over-limit", "line-issues", "no-address-allowed".</param>
/// <param name="Today">The earliest requested date (today in the seller's time zone).</param>
public sealed record CheckoutView(
    long CartVersion,
    bool CanPlace,
    IReadOnlyList<string> Issues,
    IReadOnlyList<OrderLineView> Lines,
    OrderTotalView? Total,
    IReadOnlyList<OrderAddressView> Addresses,
    string? SuggestedAddressCode,
    DateOnly Today);

/// <summary>
/// Places an order from the buyer's cart, with the prices of this moment. <paramref name="OrderId"/> is generated when
/// the checkout page is shown, so sending the form twice places one order.
/// </summary>
/// <param name="CartVersion">The version and the gross total the buyer saw; a different cart is not ordered.</param>
public sealed record PlaceOrder(
    Guid OrderId,
    BuyerTerms Buyer,
    IReadOnlyList<string> Cultures,
    long CartVersion,
    decimal ExpectedGross,
    string? CustomerReference,
    string? Notes,
    DateOnly? RequestedDate,
    string? AddressCode) : ICommand<Result<OrderPlacement, Error>>;

/// <param name="AlreadyPlaced">The same order had been placed before (the form was sent again).</param>
/// <param name="Items">What was ordered, to take out of the cart.</param>
public sealed record OrderPlacement(Guid OrderId, string Number, bool AlreadyPlaced, IReadOnlyList<OrderItemData> Items);

public sealed record OrderItemData(string Sku, decimal Quantity);

/// <summary>The buyer cancels an order the ERP has not taken over yet (the host checks who may).</summary>
public sealed record CancelOrder(Guid OrderId, Guid UserId) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP took the order over under its own number; the same confirmation again changes nothing.</summary>
public sealed record AcceptOrder(string Number, string ErpNumber) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP refuses an order it has not accepted.</summary>
public sealed record RejectOrder(string Number, string? Reason) : ICommand<Result<Unit, Error>>;

/// <summary>The seller cancels an order (before it is invoiced).</summary>
public sealed record CancelOrderBySeller(string Number, string? Reason) : ICommand<Result<Unit, Error>>;

/// <summary>One order with its frozen lines, history and linked documents.</summary>
public sealed record GetOrder(Guid OrderId) : IQuery<Result<OrderView, Error>>;

/// <param name="Status">See <see cref="OrderStatuses"/>.</param>
/// <param name="PlacedByName">The name of the buyer, once known to the order list.</param>
/// <param name="CancelledBy">"customer" or "seller" for a cancelled order.</param>
/// <param name="Reason">Why the order was rejected or cancelled, if the ERP said so.</param>
public sealed record OrderView(
    Guid Id,
    string Number,
    string? ErpNumber,
    string CustomerCode,
    Guid PlacedBy,
    string? PlacedByName,
    DateTimeOffset PlacedAt,
    string Status,
    IReadOnlyList<OrderMilestoneView> History,
    IReadOnlyList<OrderLineView> Lines,
    OrderTotalView Total,
    string? CustomerReference,
    string? Notes,
    DateOnly? RequestedDate,
    OrderAddressView? DeliveryAddress,
    IReadOnlyList<LinkedDocumentView> Documents,
    string? CancelledBy,
    string? Reason);

public sealed record OrderMilestoneView(string Status, DateTimeOffset At);

/// <param name="Backorder">More than was on hand when ordered: delivered later.</param>
public sealed record OrderLineView(string Sku, string Name, string Unit, decimal Quantity, decimal UnitNet, decimal VatRate, decimal NetAmount, bool Backorder);

public sealed record OrderTotalView(string Currency, decimal Net, IReadOnlyList<OrderVatView> Rates, decimal Vat, decimal Gross);

public sealed record OrderVatView(decimal Rate, decimal Net, decimal Vat);

public sealed record OrderAddressView(string Code, string? Name, string Street, string PostalCode, string City, string Country);

/// <param name="Type">"invoice", "correction-invoice", "delivery-note", "proforma" or "other".</param>
public sealed record LinkedDocumentView(string DocumentId, string Type, string Number, DateTimeOffset At);

/// <summary>A page of orders, newest first, narrowed by any of the filters.</summary>
/// <param name="PlacedBy">Only the orders of this buyer (the "own" order scope).</param>
/// <param name="SalespersonEmail">Only customers of this salesperson (operators with that scope).</param>
/// <param name="Number">A platform or ERP order number, or the buyer's reference (exact).</param>
public sealed record GetOrders(
    string? CustomerCode = null,
    Guid? PlacedBy = null,
    string? SalespersonEmail = null,
    string? Status = null,
    DateOnly? PlacedFrom = null,
    DateOnly? PlacedTo = null,
    string? Number = null,
    int Page = 1,
    int PageSize = 20) : IQuery<Result<OrderPage, Error>>
{
    public const int MaxPageSize = 100;
}

public sealed record OrderPage(IReadOnlyList<OrderSummaryView> Items, int Page, int PageSize, int TotalCount)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record OrderSummaryView(
    Guid Id,
    string Number,
    string? ErpNumber,
    string CustomerCode,
    string CustomerName,
    Guid PlacedBy,
    string PlacedByName,
    DateTimeOffset PlacedAt,
    string Status,
    string Currency,
    decimal Net,
    decimal Gross,
    int LineCount,
    string? CustomerReference);

/// <summary>
/// What the ERP takes over: placed orders and orders the buyer cancelled, in the order they happened, after the
/// position the ERP has already processed (0 for the beginning).
/// </summary>
public sealed record GetOrderFeed(long After, int Limit = 100) : IQuery<OrderFeedPage>
{
    public const int MaxLimit = 500;
}

/// <param name="LastPosition">The position to ask after next time (the same as asked when nothing is new).</param>
public sealed record OrderFeedPage(IReadOnlyList<OrderFeedEntry> Entries, long LastPosition);

/// <param name="Type">"placed" or "cancelled" (by the buyer).</param>
public sealed record OrderFeedEntry(long Position, string Type, DateTimeOffset At, OrderExport Order);

/// <summary>An order as the ERP needs it.</summary>
public sealed record OrderExport(
    Guid Id,
    string Number,
    string CustomerCode,
    DateTimeOffset PlacedAt,
    OrderBuyer PlacedBy,
    string Status,
    string? ErpNumber,
    IReadOnlyList<OrderExportLine> Lines,
    OrderTotalView Total,
    string? CustomerReference,
    string? Notes,
    DateOnly? RequestedDate,
    OrderAddressView? DeliveryAddress);

public sealed record OrderBuyer(Guid UserId, string Name, string Email);

public sealed record OrderExportLine(int Number, string Sku, string Name, string Unit, decimal Quantity, decimal UnitNet, decimal VatRate, decimal NetAmount, string PriceListCode, bool Backorder);

public static class OrderStatuses
{
    public const string Placed = "placed";
    public const string Accepted = "accepted";
    public const string InProgress = "in-progress";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Placed, Accepted, InProgress, Completed, Rejected, Cancelled];
}

/// <summary>
/// An order was placed; the cart takes the items out. Published after the order event is stored (the event store is
/// the outbox), at least once.
/// </summary>
public sealed record OrderPlaced(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, DateTimeOffset PlacedAt, IReadOnlyList<OrderItemData> Items) : IIntegrationEvent;

/// <summary>
/// An order was accepted, rejected or cancelled (by the ERP, the seller or the buyer). Published like
/// <see cref="OrderPlaced"/>.
/// </summary>
/// <param name="CancelledBy">"customer" or "seller" for a cancellation.</param>
public sealed record OrderStatusChanged(
    Guid OrderId,
    string Number,
    string CustomerCode,
    Guid PlacedBy,
    string Status,
    string? CancelledBy,
    string? Reason,
    DateTimeOffset At) : IIntegrationEvent;
