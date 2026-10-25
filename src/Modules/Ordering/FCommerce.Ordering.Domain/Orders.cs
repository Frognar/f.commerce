using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Ordering.Domain;

/// <summary>A line of an order with the price frozen when it was placed.</summary>
/// <param name="NetAmount">Net value of the line (rounded to 2 places), as quoted.</param>
/// <param name="Backorder">More than was on hand when ordered: the rest is delivered later.</param>
public sealed record OrderLine(
    string Sku,
    string Name,
    string Unit,
    decimal Quantity,
    decimal UnitNet,
    decimal VatRate,
    decimal NetAmount,
    string PriceListCode,
    bool Backorder);

public sealed record VatTotal(decimal Rate, decimal Net, decimal Vat);

/// <summary>The value of an order in its one currency, with VAT per rate (as quoted).</summary>
public sealed record OrderTotals(string Currency, decimal Net, EquatableArray<VatTotal> Rates, decimal Vat, decimal Gross);

/// <summary>A delivery address of the customer, copied from the ERP data when the order was placed.</summary>
public sealed record DeliveryAddress(string Code, Maybe<string> Name, string Street, string PostalCode, string City, string Country);

/// <summary>What the buyer said about the order at checkout.</summary>
/// <param name="CustomerReference">The buyer's own order number.</param>
/// <param name="RequestedDate">When the buyer would like the goods.</param>
/// <param name="Address">None when the customer has no delivery addresses (the ERP decides).</param>
public sealed record OrderDetails(Maybe<string> CustomerReference, Maybe<string> Notes, Maybe<DateOnly> RequestedDate, Maybe<DeliveryAddress> Address);

public enum CancelledBy
{
    Customer,
    Seller,
}

/// <summary>What kind of ERP document names the order; only delivery notes and invoices move its status.</summary>
public enum LinkedDocumentKind
{
    Invoice,
    CorrectionInvoice,
    DeliveryNote,
    Proforma,
    Other,
}

public enum OrderStatus
{
    /// <summary>Placed on the platform, not yet taken over by the ERP.</summary>
    Placed,

    /// <summary>The ERP took the order over (confirmed it or issued documents for it).</summary>
    Accepted,

    /// <summary>A delivery note was issued.</summary>
    InProgress,

    /// <summary>An invoice was issued.</summary>
    Completed,
    Rejected,
    Cancelled,
}

/// <summary>What happened to an order. Persisted: change only additively, or add a new version.</summary>
public abstract record OrderEvent
{
    private OrderEvent()
    {
    }

    public sealed record OrderPlaced(
        string Number,
        string CustomerCode,
        Guid PlacedBy,
        DateTimeOffset PlacedAt,
        EquatableArray<OrderLine> Lines,
        OrderTotals Totals,
        OrderDetails Details) : OrderEvent;

    /// <summary>The ERP took the order over under its own number.</summary>
    public sealed record OrderAccepted(string ErpNumber, DateTimeOffset At) : OrderEvent;

    public sealed record OrderRejected(Maybe<string> Reason, DateTimeOffset At) : OrderEvent;

    public sealed record OrderCancelled(CancelledBy By, Maybe<string> Reason, DateTimeOffset At) : OrderEvent;

    /// <summary>An ERP document names the order.</summary>
    public sealed record DocumentLinked(string DocumentId, LinkedDocumentKind Kind, string Number, DateTimeOffset At) : OrderEvent;
}

public abstract record OrderCommand
{
    private OrderCommand()
    {
    }

    /// <summary>Records a validated order (see <see cref="OrderDraft.Validate"/>).</summary>
    public sealed record Place(string Number, string CustomerCode, Guid PlacedBy, ValidOrder Order) : OrderCommand;

    public sealed record Accept(string ErpNumber) : OrderCommand;

    public sealed record Reject(Maybe<string> Reason) : OrderCommand;

    public sealed record CancelByCustomer : OrderCommand;

    public sealed record CancelBySeller(Maybe<string> Reason) : OrderCommand;

    public sealed record LinkDocument(string DocumentId, LinkedDocumentKind Kind, string Number) : OrderCommand;
}

public sealed record LinkedDocument(string DocumentId, LinkedDocumentKind Kind, string Number, DateTimeOffset At);

/// <summary>A moment in the life of an order, for its history on the order page.</summary>
public sealed record OrderMilestone(OrderStatus Status, DateTimeOffset At);

/// <summary>An order rebuilt from its events; <see cref="Placed"/> is none before the order exists.</summary>
public sealed record OrderState(
    Maybe<OrderEvent.OrderPlaced> Placed,
    Maybe<string> ErpNumber,
    Maybe<OrderEvent.OrderRejected> Rejection,
    Maybe<OrderEvent.OrderCancelled> Cancellation,
    EquatableArray<LinkedDocument> Documents)
{
    public static OrderState Initial { get; } = new(
        Maybe<OrderEvent.OrderPlaced>.None, Maybe<string>.None, Maybe<OrderEvent.OrderRejected>.None, Maybe<OrderEvent.OrderCancelled>.None, EquatableArray<LinkedDocument>.Empty);

    /// <summary>
    /// Rejected and cancelled are final; otherwise the furthest step the ERP documents show: an invoice completes the
    /// order, a delivery note puts it in progress, any document or a confirmation means it was accepted.
    /// </summary>
    public OrderStatus Status =>
        Rejection.Exists(_ => true) ? OrderStatus.Rejected
        : Cancellation.Exists(_ => true) ? OrderStatus.Cancelled
        : Documents.Any(d => d.Kind == LinkedDocumentKind.Invoice) ? OrderStatus.Completed
        : Documents.Any(d => d.Kind == LinkedDocumentKind.DeliveryNote) ? OrderStatus.InProgress
        : ErpNumber.Exists(_ => true) || !Documents.IsEmpty ? OrderStatus.Accepted
        : OrderStatus.Placed;

    /// <summary>When the order reached each status it went through, in order.</summary>
    public EquatableArray<OrderMilestone> Milestones =>
        Placed.Match(
            placed => (EquatableArray<OrderMilestone>)
            [
                new OrderMilestone(OrderStatus.Placed, placed.PlacedAt),
                .. Accepted.AsEnumerable().Select(at => new OrderMilestone(OrderStatus.Accepted, at)),
                .. Documents.FirstOrNone(d => d.Kind == LinkedDocumentKind.DeliveryNote).AsEnumerable().Select(d => new OrderMilestone(OrderStatus.InProgress, d.At)),
                .. Documents.FirstOrNone(d => d.Kind == LinkedDocumentKind.Invoice).AsEnumerable().Select(d => new OrderMilestone(OrderStatus.Completed, d.At)),
                .. Rejection.AsEnumerable().Select(r => new OrderMilestone(OrderStatus.Rejected, r.At)),
                .. Cancellation.AsEnumerable().Select(c => new OrderMilestone(OrderStatus.Cancelled, c.At)),
            ],
            () => EquatableArray<OrderMilestone>.Empty);

    /// <summary>When the ERP took the order over: its confirmation, or else its first document.</summary>
    public Maybe<DateTimeOffset> Accepted { get; init; }
}

/// <summary>An order as a pure decider: commands become events, events build the state.</summary>
public static class Orders
{
    public const int MaxErpNumber = 64;
    public const int MaxReason = 500;

    public static Decider<OrderCommand, OrderState, OrderEvent> Decider { get; } = new(Decide, Evolve, OrderState.Initial);

    public static Result<EquatableArray<OrderEvent>, Error> Decide(OrderCommand command, OrderState state, DecisionContext context) =>
        (command, state.Placed.Exists(_ => true)) switch
        {
            (OrderCommand.Place place, false) => One(new OrderEvent.OrderPlaced(
                place.Number, place.CustomerCode, place.PlacedBy, context.Now, place.Order.Lines, place.Order.Totals, place.Order.Details)),
            (OrderCommand.Place, true) => Error.Conflict("order.exists", "This order has already been placed."),
            (_, false) => Error.NotFound("order.not-found", "The order does not exist."),
            (OrderCommand.Accept accept, true) => ErpNumber(accept.ErpNumber).Bind(number => Accept(state, number, context)),
            (OrderCommand.Reject reject, true) => Reason(reject.Reason).Bind(reason => Reject(state, reason, context)),
            (OrderCommand.CancelByCustomer, true) => CancelByCustomer(state, context),
            (OrderCommand.CancelBySeller cancel, true) => Reason(cancel.Reason).Bind(reason => CancelBySeller(state, reason, context)),
            (OrderCommand.LinkDocument link, true) => state.Documents.Any(d => d.DocumentId == link.DocumentId)
                ? EquatableArray<OrderEvent>.Empty
                : One(new OrderEvent.DocumentLinked(link.DocumentId, link.Kind, link.Number, context.Now)),
            _ => Error.Validation("order.unknown-command", "Unknown order command."),
        };

    public static OrderState Evolve(OrderState state, OrderEvent @event) =>
        @event switch
        {
            OrderEvent.OrderPlaced placed => state with { Placed = placed },
            OrderEvent.OrderAccepted accepted => state with { ErpNumber = accepted.ErpNumber, Accepted = state.Accepted.Or(accepted.At) },
            OrderEvent.OrderRejected rejected => state with { Rejection = rejected },
            OrderEvent.OrderCancelled cancelled => state with { Cancellation = cancelled },
            OrderEvent.DocumentLinked linked => state with
            {
                Documents = state.Documents.Add(new LinkedDocument(linked.DocumentId, linked.Kind, linked.Number, linked.At)),
                Accepted = state.Accepted.Or(linked.At),
            },
            _ => state,
        };

    public static Result<string, Error> ErpNumber(string raw) => SourceCodes.Parse(raw, MaxErpNumber, "erp-number", "ERP order number");

    /// <summary>An optional reason: blank is none, at most 500 characters.</summary>
    public static Result<Maybe<string>, Error> Reason(Maybe<string> raw) =>
        raw.Map(text => text.Trim()).Where(text => text.Length > 0).Match(
            text => text.Length > MaxReason
                ? Error.Validation("order.reason-too-long", $"A reason has at most {MaxReason} characters.") with { Field = "reason" }
                : Result.Ok<Maybe<string>, Error>(text),
            () => Result.Ok<Maybe<string>, Error>(Maybe<string>.None));

    /// <summary>The ERP confirms an order only once; the same confirmation again changes nothing.</summary>
    private static Result<EquatableArray<OrderEvent>, Error> Accept(OrderState state, string number, DecisionContext context) =>
        state.Status switch
        {
            OrderStatus.Rejected => Error.Conflict("order.rejected", "The order was rejected."),
            OrderStatus.Cancelled => Error.Conflict("order.cancelled", "The order was cancelled."),
            _ => state.ErpNumber.Match(
                known => known == number
                    ? Result.Ok<EquatableArray<OrderEvent>, Error>(EquatableArray<OrderEvent>.Empty)
                    : Error.Conflict("order.already-accepted", $"The order was accepted as {known}."),
                () => One(new OrderEvent.OrderAccepted(number, context.Now))),
        };

    private static Result<EquatableArray<OrderEvent>, Error> Reject(OrderState state, Maybe<string> reason, DecisionContext context) =>
        state.Status switch
        {
            OrderStatus.Placed => One(new OrderEvent.OrderRejected(reason, context.Now)),
            OrderStatus.Rejected => EquatableArray<OrderEvent>.Empty,
            OrderStatus.Cancelled => Error.Conflict("order.cancelled", "The order was cancelled."),
            _ => Error.Conflict("order.already-accepted", "An accepted order is cancelled, not rejected."),
        };

    /// <summary>The buyer may cancel only while the ERP has not taken the order over.</summary>
    private static Result<EquatableArray<OrderEvent>, Error> CancelByCustomer(OrderState state, DecisionContext context) =>
        state.Status switch
        {
            OrderStatus.Placed => One(new OrderEvent.OrderCancelled(CancelledBy.Customer, Maybe<string>.None, context.Now)),
            OrderStatus.Cancelled => EquatableArray<OrderEvent>.Empty,
            _ => Error.Conflict("order.not-cancellable", "The seller has already taken the order over; contact them to cancel it."),
        };

    private static Result<EquatableArray<OrderEvent>, Error> CancelBySeller(OrderState state, Maybe<string> reason, DecisionContext context) =>
        state.Status switch
        {
            OrderStatus.Placed or OrderStatus.Accepted or OrderStatus.InProgress => One(new OrderEvent.OrderCancelled(CancelledBy.Seller, reason, context.Now)),
            OrderStatus.Cancelled => EquatableArray<OrderEvent>.Empty,
            OrderStatus.Rejected => Error.Conflict("order.rejected", "The order was rejected."),
            _ => Error.Conflict("order.completed", "An invoiced order cannot be cancelled."),
        };

    private static Result<EquatableArray<OrderEvent>, Error> One(OrderEvent @event) => (EquatableArray<OrderEvent>)[@event];
}
