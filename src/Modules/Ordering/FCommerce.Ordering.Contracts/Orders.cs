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
/// <param name="CanPlace">No issue: the order can be placed.</param>
/// <param name="Issues">
/// What prevents placing the order: "cart-incomplete" (prices or stock could not be checked), "empty", "cannot-order"
/// (see the cart's issues and lines), "mixed-currencies", "no-address-allowed".
/// </param>
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

/// <summary>
/// The buyer asks to cancel an order (the host checks who may); the ERP decides, see <see cref="CancelOrder"/> and
/// <see cref="RefuseOrderCancellation"/>. Asking again changes nothing.
/// </summary>
public sealed record RequestOrderCancellation(Guid OrderId, Guid UserId) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP took the order over under its own number; the same confirmation again changes nothing.</summary>
public sealed record AcceptOrder(string Number, string ErpNumber) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP refuses an order it has not accepted.</summary>
public sealed record RejectOrder(string Number, string? Reason) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP cancels an order that is not invoiced yet (on the buyer's request or of its own accord).</summary>
public sealed record CancelOrder(string Number, string? Reason) : ICommand<Result<Unit, Error>>;

/// <summary>The ERP keeps an order the buyer asked to cancel.</summary>
public sealed record RefuseOrderCancellation(string Number, string? Reason) : ICommand<Result<Unit, Error>>;

/// <summary>The body of the ERP's confirmation of an order (integration API).</summary>
public sealed record OrderAcceptanceData(string ErpNumber);

/// <summary>The body of the ERP's rejection or cancellation of an order (integration API); the reason is optional.</summary>
public sealed record OrderReasonData(string? Reason);

/// <summary>One order with its frozen lines, history and linked documents.</summary>
public sealed record GetOrder(Guid OrderId) : IQuery<Result<OrderView, Error>>;

/// <param name="Status">See <see cref="OrderStatuses"/>.</param>
/// <param name="PlacedByName">The name of the buyer, once known to the order list.</param>
/// <param name="History">The steps of the order (see <see cref="OrderSteps"/>) in the order they happened.</param>
/// <param name="CancellationRequestedAt">When the buyer asked to cancel the order, while the ERP has not decided.</param>
/// <param name="CancelledOnRequest">A cancelled order was cancelled on the buyer's request.</param>
/// <param name="Reason">Why the order was rejected or cancelled (see the status), if the ERP said so.</param>
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
    DateTimeOffset? CancellationRequestedAt,
    bool CancelledOnRequest,
    string? Reason);

/// <param name="Step">See <see cref="OrderSteps"/>.</param>
/// <param name="Reason">Why the ERP rejected or cancelled the order or kept it, if it said so.</param>
public sealed record OrderMilestoneView(string Step, DateTimeOffset At, string? Reason);

/// <param name="Backorder">More than was on hand when ordered: delivered later.</param>
public sealed record OrderLineView(string Sku, string Name, string Unit, decimal Quantity, decimal UnitNet, decimal VatRate, decimal NetAmount, bool Backorder);

public sealed record OrderTotalView(string Currency, decimal Net, IReadOnlyList<OrderVatView> Rates, decimal Vat, decimal Gross);

public sealed record OrderVatView(decimal Rate, decimal Net, decimal Vat);

public sealed record OrderAddressView(string Code, string? Name, string Street, string PostalCode, string City, string Country);

/// <param name="Type">"invoice", "correction-invoice", "delivery-note" or "proforma".</param>
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

/// <param name="CustomerName">None while the customer is not known to the order list.</param>
/// <param name="PlacedByName">None while the buyer is not known to the order list.</param>
public sealed record OrderSummaryView(
    Guid Id,
    string Number,
    string? ErpNumber,
    string CustomerCode,
    string? CustomerName,
    Guid PlacedBy,
    string? PlacedByName,
    DateTimeOffset PlacedAt,
    string Status,
    string Currency,
    decimal Net,
    decimal Gross,
    int LineCount,
    string? CustomerReference);

/// <summary>
/// What the ERP takes over: placed orders and the buyers' requests to cancel them, in the order they happened, after
/// the position the ERP has already processed (0 for the beginning).
/// </summary>
public sealed record GetOrderFeed(long After, int Limit = 100) : IQuery<OrderFeedPage>
{
    public const int MaxLimit = 500;
}

/// <param name="LastPosition">The position to ask after next time (the same as asked when nothing is new).</param>
public sealed record OrderFeedPage(IReadOnlyList<OrderFeedEntry> Entries, long LastPosition);

/// <param name="Type">See <see cref="OrderFeedTypes"/>.</param>
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

/// <param name="Name">None when the buyer's account is no longer active (as their e-mail).</param>
public sealed record OrderBuyer(Guid UserId, string? Name, string? Email);

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

/// <summary>The steps in the history of an order: the statuses it went through and the buyer's cancellation requests.</summary>
public static class OrderSteps
{
    public const string Placed = OrderStatuses.Placed;
    public const string Accepted = OrderStatuses.Accepted;
    public const string CancellationRequested = "cancellation-requested";
    public const string CancellationRefused = "cancellation-refused";
    public const string Rejected = OrderStatuses.Rejected;
    public const string Cancelled = OrderStatuses.Cancelled;
    public const string InProgress = OrderStatuses.InProgress;
    public const string Completed = OrderStatuses.Completed;
}

public static class OrderFeedTypes
{
    public const string Placed = "placed";

    /// <summary>The buyer asked to cancel the order: the ERP confirms (cancellation) or refuses (cancellation-refusal).</summary>
    public const string CancellationRequested = "cancellation-requested";
}

/// <summary>
/// An order was placed; the cart takes the items out. Like every order event below it is published after the order
/// event is stored (the event store is the outbox), at least once.
/// </summary>
public sealed record OrderPlaced(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, DateTimeOffset PlacedAt, IReadOnlyList<OrderItemData> Items) : IIntegrationEvent;

/// <summary>The ERP took the order over: under its own number, or (number none) by issuing a document for it.</summary>
public sealed record OrderAccepted(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string? ErpNumber, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The ERP refused the order.</summary>
public sealed record OrderRejected(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string? Reason, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The buyer asked to cancel the order; the ERP decides.</summary>
public sealed record OrderCancellationRequested(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, Guid RequestedBy, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The ERP keeps an order the buyer asked to cancel.</summary>
public sealed record OrderCancellationRefused(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string? Reason, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The ERP cancelled the order.</summary>
/// <param name="OnRequest">On the buyer's request (otherwise of the seller's own accord).</param>
public sealed record OrderCancelled(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string? Reason, bool OnRequest, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The first delivery note of the order was issued: the goods are on their way.</summary>
public sealed record OrderDispatched(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string DeliveryNoteId, DateTimeOffset At) : IIntegrationEvent;

/// <summary>The first invoice of the order was issued: the order is completed.</summary>
public sealed record OrderInvoiced(Guid OrderId, string Number, string CustomerCode, Guid PlacedBy, string InvoiceId, DateTimeOffset At) : IIntegrationEvent;
