using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Ordering.Domain;

/// <summary>A line of an order with the price frozen when it was placed.</summary>
/// <param name="PriceList">The price list the line was priced from.</param>
/// <param name="Backorder">More than was on hand when ordered: the rest is delivered later.</param>
public sealed record OrderLine(Sku Sku, LineName Name, UnitCode Unit, OrderQuantity Quantity, NetPrice UnitNet, VatRate VatRate, PriceListCode PriceList, bool Backorder)
{
    /// <summary>Net value of the line: price × quantity, rounded half away from zero to 2 places (as pricing quotes it).</summary>
    public decimal NetAmount => Amounts.Round(UnitNet.Value * Quantity.Value);
}

/// <summary>The net value of the lines with one VAT rate and the VAT on it.</summary>
public sealed record VatTotal
{
    internal VatTotal(VatRate rate, decimal net)
    {
        Rate = rate;
        Net = net;
    }

    public VatRate Rate { get; }

    public decimal Net { get; }

    /// <summary>VAT of the summed net value, rounded half away from zero (as an invoice computes it).</summary>
    public decimal Vat => Amounts.Round(Net * Rate.Percent / 100);
}

/// <summary>The value of an order in its one currency, computed from its lines: VAT per rate (ascending), gross = net + VAT.</summary>
public sealed record OrderTotals
{
    private OrderTotals(Currency currency, EquatableArray<VatTotal> rates)
    {
        Currency = currency;
        Rates = rates;
    }

    public Currency Currency { get; }

    public EquatableArray<VatTotal> Rates { get; }

    public decimal Net => Rates.Sum(rate => rate.Net);

    public decimal Vat => Rates.Sum(rate => rate.Vat);

    public decimal Gross => Net + Vat;

    public static OrderTotals Of(Currency currency, IEnumerable<OrderLine> lines) =>
        new(currency, [.. lines.GroupBy(line => line.VatRate).OrderBy(rate => rate.Key.Percent).Select(rate => new VatTotal(rate.Key, rate.Sum(line => line.NetAmount)))]);
}

internal static class Amounts
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A delivery address of the customer, copied from the ERP data when the order was placed.</summary>
public sealed record DeliveryAddress(AddressCode Code, Maybe<AddressText> Name, AddressText Street, SharedKernel.Contact.PostalCode PostalCode, AddressText City, SharedKernel.Contact.CountryCode Country);

/// <summary>What the buyer said about the order at checkout.</summary>
/// <param name="CustomerReference">The buyer's own order number.</param>
/// <param name="RequestedDate">When the buyer would like the goods.</param>
/// <param name="Address">None when the customer has no delivery addresses (the ERP decides).</param>
public sealed record OrderDetails(Maybe<BuyerText> CustomerReference, Maybe<BuyerText> Notes, Maybe<DateOnly> RequestedDate, Maybe<DeliveryAddress> Address);

/// <summary>What kind of ERP document names the order; delivery notes and invoices move it on.</summary>
public enum LinkedDocumentKind
{
    Invoice,
    CorrectionInvoice,
    DeliveryNote,
    Proforma,
}

/// <summary>What happened to an order. Persisted: change only additively, or add a new version.</summary>
public abstract record OrderEvent
{
    private OrderEvent()
    {
    }

    public sealed record OrderPlaced(
        OrderNumber Number,
        CustomerCode CustomerCode,
        Guid PlacedBy,
        DateTimeOffset PlacedAt,
        Currency Currency,
        NonEmptyArray<OrderLine> Lines,
        OrderDetails Details) : OrderEvent
    {
        public OrderTotals Totals => OrderTotals.Of(Currency, Lines);
    }

    /// <summary>The ERP took the order over: by a confirmation under its own number, or by issuing a document for it.</summary>
    public sealed record OrderAccepted(Maybe<ErpOrderNumber> ErpNumber, DateTimeOffset At) : OrderEvent;

    /// <summary>The ERP confirmed under its own number an order it had already taken over by a document.</summary>
    public sealed record ErpNumberAssigned(ErpOrderNumber ErpNumber, DateTimeOffset At) : OrderEvent;

    public sealed record OrderRejected(Maybe<Reason> Reason, DateTimeOffset At) : OrderEvent;

    /// <summary>The buyer asked to cancel the order; the ERP decides.</summary>
    public sealed record CancellationRequested(Guid By, DateTimeOffset At) : OrderEvent;

    /// <summary>The ERP keeps the order the buyer asked to cancel.</summary>
    public sealed record CancellationRefused(Maybe<Reason> Reason, DateTimeOffset At) : OrderEvent;

    /// <summary>The ERP cancelled the order.</summary>
    /// <param name="OnRequest">While the buyer's request to cancel it was open (otherwise of the seller's own accord).</param>
    public sealed record OrderCancelled(Maybe<Reason> Reason, bool OnRequest, DateTimeOffset At) : OrderEvent;

    /// <summary>An ERP document names the order.</summary>
    public sealed record DocumentLinked(DocumentId DocumentId, LinkedDocumentKind Kind, DocumentNumber Number, DateTimeOffset At) : OrderEvent;

    /// <summary>The first delivery note of the order was issued: the goods are on their way.</summary>
    public sealed record OrderDispatched(DocumentId DeliveryNote, DateTimeOffset At) : OrderEvent;

    /// <summary>The first invoice of the order was issued: the order is completed.</summary>
    public sealed record OrderInvoiced(DocumentId Invoice, DateTimeOffset At) : OrderEvent;
}

public abstract record OrderCommand
{
    private OrderCommand()
    {
    }

    /// <summary>Records a validated order (see <see cref="OrderDraft.Validate"/>).</summary>
    public sealed record Place(OrderNumber Number, CustomerCode CustomerCode, Guid PlacedBy, ValidOrder Order) : OrderCommand;

    /// <summary>The ERP confirms the order under its own number.</summary>
    public sealed record Accept(ErpOrderNumber ErpNumber) : OrderCommand;

    /// <summary>The ERP refuses an order it has not taken over.</summary>
    public sealed record Reject(Maybe<Reason> Reason) : OrderCommand;

    /// <summary>The buyer asks to cancel the order.</summary>
    public sealed record RequestCancellation(Guid By) : OrderCommand;

    /// <summary>The ERP cancels the order.</summary>
    public sealed record Cancel(Maybe<Reason> Reason) : OrderCommand;

    /// <summary>The ERP keeps an order the buyer asked to cancel.</summary>
    public sealed record RefuseCancellation(Maybe<Reason> Reason) : OrderCommand;

    public sealed record LinkDocument(DocumentId DocumentId, LinkedDocumentKind Kind, DocumentNumber Number) : OrderCommand;
}

public enum OrderStatus
{
    /// <summary>Placed on the platform, not yet taken over by the ERP.</summary>
    Placed,

    /// <summary>The ERP took the order over (confirmed it or issued a document for it).</summary>
    Accepted,

    /// <summary>A delivery note was issued.</summary>
    InProgress,

    /// <summary>An invoice was issued.</summary>
    Completed,
    Rejected,
    Cancelled,
}

/// <summary>When and under which number (none when it took the order over by a document) the ERP took the order over.</summary>
public sealed record Acceptance(Maybe<ErpOrderNumber> ErpNumber, DateTimeOffset At);

/// <summary>The buyer's open request to cancel the order.</summary>
public sealed record CancellationRequest(Guid By, DateTimeOffset At);

/// <summary>
/// How far an order got. Each step holds only what exists at that step: an acceptance from the ERP once taken over, an
/// open cancellation request only while the ERP can still cancel.
/// </summary>
public abstract record OrderProgress
{
    private OrderProgress()
    {
    }

    public abstract OrderStatus Status { get; }

    public virtual Maybe<Acceptance> Acceptance => Maybe<Acceptance>.None;

    /// <summary>The buyer's request the ERP has not decided yet.</summary>
    public virtual Maybe<CancellationRequest> CancellationRequest => Maybe<CancellationRequest>.None;

    /// <summary>The ERP may still cancel the order (it is neither decided nor invoiced).</summary>
    public virtual bool Cancellable => false;

    public sealed record AwaitingErp(Maybe<CancellationRequest> Request) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.Placed;

        public override Maybe<CancellationRequest> CancellationRequest => Request;

        public override bool Cancellable => true;
    }

    public sealed record Accepted(Acceptance By, Maybe<CancellationRequest> Request) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.Accepted;

        public override Maybe<Acceptance> Acceptance => By;

        public override Maybe<CancellationRequest> CancellationRequest => Request;

        public override bool Cancellable => true;
    }

    public sealed record Dispatched(Acceptance By, DateTimeOffset At, Maybe<CancellationRequest> Request) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.InProgress;

        public override Maybe<Acceptance> Acceptance => By;

        public override Maybe<CancellationRequest> CancellationRequest => Request;

        public override bool Cancellable => true;
    }

    public sealed record Invoiced(Acceptance By, DateTimeOffset At) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.Completed;

        public override Maybe<Acceptance> Acceptance => By;
    }

    public sealed record Rejected(Maybe<Reason> Reason, DateTimeOffset At) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.Rejected;
    }

    /// <param name="Before">The ERP's acceptance, when it had taken the order over before cancelling it.</param>
    /// <param name="OnRequest">Cancelled on the buyer's request.</param>
    public sealed record Cancelled(Maybe<Reason> Reason, DateTimeOffset At, Maybe<Acceptance> Before, bool OnRequest) : OrderProgress
    {
        public override OrderStatus Status => OrderStatus.Cancelled;

        public override Maybe<Acceptance> Acceptance => Before;
    }
}

public sealed record LinkedDocument(DocumentId DocumentId, LinkedDocumentKind Kind, DocumentNumber Number, DateTimeOffset At);

public enum OrderMilestoneKind
{
    Placed,
    Accepted,
    CancellationRequested,
    CancellationRefused,
    Rejected,
    Cancelled,
    Dispatched,
    Invoiced,
}

/// <summary>A moment in the life of an order, for its history on the order page.</summary>
/// <param name="Reason">Why the ERP rejected or cancelled the order, or refused to cancel it.</param>
public sealed record OrderMilestone(OrderMilestoneKind Kind, DateTimeOffset At, Maybe<Reason> Reason);

/// <summary>An order rebuilt from its events: not placed yet, or placed with its progress, documents and history.</summary>
public abstract record OrderState
{
    private OrderState()
    {
    }

    public static OrderState Initial { get; } = new NotPlaced();

    public sealed record NotPlaced : OrderState;

    /// <summary>Built only by <see cref="Orders.Evolve"/>, so progress, documents and history always agree.</summary>
    public sealed record Placed : OrderState
    {
        internal Placed(OrderEvent.OrderPlaced order, OrderProgress progress, EquatableArray<LinkedDocument> documents, EquatableArray<OrderMilestone> history)
        {
            Order = order;
            Progress = progress;
            Documents = documents;
            History = history;
        }

        public OrderEvent.OrderPlaced Order { get; }

        public OrderProgress Progress { get; }

        public EquatableArray<LinkedDocument> Documents { get; }

        /// <summary>The steps of the order in the order they happened.</summary>
        public EquatableArray<OrderMilestone> History { get; }

        public OrderStatus Status => Progress.Status;

        public Maybe<ErpOrderNumber> ErpNumber => Progress.Acceptance.Bind(acceptance => acceptance.ErpNumber);

        internal Placed With(OrderProgress progress, Maybe<OrderMilestone> milestone) =>
            new(Order, progress, Documents, [.. History, .. milestone.AsEnumerable()]);

        internal Placed With(LinkedDocument document) => new(Order, Progress, Documents.Add(document), History);
    }
}

/// <summary>An order as a pure decider: commands become events, events build the state.</summary>
public static class Orders
{
    public static Decider<OrderCommand, OrderState, OrderEvent> Decider { get; } = new(Decide, Evolve, OrderState.Initial);

    public static Result<EquatableArray<OrderEvent>, Error> Decide(OrderCommand command, OrderState state, DecisionContext context) =>
        (command, state) switch
        {
            (OrderCommand.Place place, OrderState.NotPlaced) => Events(new OrderEvent.OrderPlaced(
                place.Number, place.CustomerCode, place.PlacedBy, context.Now, place.Order.Currency, place.Order.Lines, place.Order.Details)),
            (OrderCommand.Place, _) => Error.Conflict("order.exists", "This order has already been placed."),
            (_, OrderState.Placed placed) => Decide(command, placed, context.Now),
            _ => Error.NotFound("order.not-found", "The order does not exist."),
        };

    public static OrderState Evolve(OrderState state, OrderEvent @event) =>
        (state, @event) switch
        {
            (OrderState.NotPlaced, OrderEvent.OrderPlaced placed) => new OrderState.Placed(
                placed,
                new OrderProgress.AwaitingErp(Maybe<CancellationRequest>.None),
                EquatableArray<LinkedDocument>.Empty,
                [new OrderMilestone(OrderMilestoneKind.Placed, placed.PlacedAt, Maybe<Reason>.None)]),
            (OrderState.Placed placed, OrderEvent.DocumentLinked linked) => placed.With(new LinkedDocument(linked.DocumentId, linked.Kind, linked.Number, linked.At)),
            (OrderState.Placed placed, not OrderEvent.OrderPlaced) => Advance(placed, @event),
            _ => state,
        };

    private static Result<EquatableArray<OrderEvent>, Error> Decide(OrderCommand command, OrderState.Placed placed, DateTimeOffset now) =>
        command switch
        {
            OrderCommand.Accept accept => Accept(placed.Progress, accept.ErpNumber, now),
            OrderCommand.Reject reject => Reject(placed.Progress, reject.Reason, now),
            OrderCommand.RequestCancellation request => RequestCancellation(placed.Progress, request.By, now),
            OrderCommand.Cancel cancel => Cancel(placed.Progress, cancel.Reason, now),
            OrderCommand.RefuseCancellation refuse => placed.Progress.CancellationRequest.Match(
                _ => Events(new OrderEvent.CancellationRefused(refuse.Reason, now)),
                () => Error.Conflict("order.no-cancellation-request", "Nobody asked to cancel this order.")),
            OrderCommand.LinkDocument link => Link(placed, link, now),
            _ => Error.Validation("order.unknown-command", "Unknown order command."),
        };

    /// <summary>The ERP confirms an order only once; the same confirmation again changes nothing.</summary>
    private static Result<EquatableArray<OrderEvent>, Error> Accept(OrderProgress progress, ErpOrderNumber number, DateTimeOffset now) =>
        progress switch
        {
            OrderProgress.Rejected => Error.Conflict("order.rejected", "The order was rejected."),
            OrderProgress.Cancelled => Error.Conflict("order.cancelled", "The order was cancelled."),
            OrderProgress.AwaitingErp => Events(new OrderEvent.OrderAccepted(number, now)),
            _ => progress.Acceptance.Bind(acceptance => acceptance.ErpNumber).Match(
                known => known == number
                    ? Result.Ok<EquatableArray<OrderEvent>, Error>(EquatableArray<OrderEvent>.Empty)
                    : Error.Conflict("order.already-accepted", $"The order was accepted as {known}."),
                () => Events(new OrderEvent.ErpNumberAssigned(number, now))),
        };

    private static Result<EquatableArray<OrderEvent>, Error> Reject(OrderProgress progress, Maybe<Reason> reason, DateTimeOffset now) =>
        progress switch
        {
            OrderProgress.AwaitingErp => Events(new OrderEvent.OrderRejected(reason, now)),
            OrderProgress.Rejected => EquatableArray<OrderEvent>.Empty,
            OrderProgress.Cancelled => Error.Conflict("order.cancelled", "The order was cancelled."),
            _ => Error.Conflict("order.already-accepted", "An accepted order is cancelled, not rejected."),
        };

    /// <summary>The buyer may ask while the ERP can still cancel; asking again changes nothing.</summary>
    private static Result<EquatableArray<OrderEvent>, Error> RequestCancellation(OrderProgress progress, Guid by, DateTimeOffset now) =>
        progress switch
        {
            { Cancellable: true } => progress.CancellationRequest.Match(
                _ => EquatableArray<OrderEvent>.Empty,
                () => Events(new OrderEvent.CancellationRequested(by, now))),
            OrderProgress.Cancelled => EquatableArray<OrderEvent>.Empty,
            OrderProgress.Rejected => Error.Conflict("order.rejected", "The order was rejected."),
            _ => Error.Conflict("order.completed", "An invoiced order cannot be cancelled."),
        };

    private static Result<EquatableArray<OrderEvent>, Error> Cancel(OrderProgress progress, Maybe<Reason> reason, DateTimeOffset now) =>
        progress switch
        {
            { Cancellable: true } => Events(new OrderEvent.OrderCancelled(reason, progress.CancellationRequest.Exists(_ => true), now)),
            OrderProgress.Cancelled => EquatableArray<OrderEvent>.Empty,
            OrderProgress.Rejected => Error.Conflict("order.rejected", "The order was rejected."),
            _ => Error.Conflict("order.completed", "An invoiced order cannot be cancelled."),
        };

    /// <summary>
    /// A document is linked once. Documents are facts of the ERP, so they move the order on even after it was rejected
    /// or cancelled (the ERP's later fact wins): the first delivery note dispatches it, the first invoice completes it,
    /// and any document means the ERP took it over.
    /// </summary>
    private static Result<EquatableArray<OrderEvent>, Error> Link(OrderState.Placed placed, OrderCommand.LinkDocument link, DateTimeOffset now)
    {
        if (placed.Documents.Any(document => document.DocumentId == link.DocumentId))
        {
            return EquatableArray<OrderEvent>.Empty;
        }

        var progress = placed.Progress;
        var accepting = progress.Acceptance.Match<IEnumerable<OrderEvent>>(_ => [], () => [new OrderEvent.OrderAccepted(Maybe<ErpOrderNumber>.None, now)]);
        IEnumerable<OrderEvent> following = link.Kind switch
        {
            LinkedDocumentKind.Invoice when progress is not OrderProgress.Invoiced => [.. accepting, new OrderEvent.OrderInvoiced(link.DocumentId, now)],
            LinkedDocumentKind.DeliveryNote when progress is not (OrderProgress.Dispatched or OrderProgress.Invoiced) => [.. accepting, new OrderEvent.OrderDispatched(link.DocumentId, now)],
            _ when progress is OrderProgress.AwaitingErp => accepting,
            _ => [],
        };
        return Events([new OrderEvent.DocumentLinked(link.DocumentId, link.Kind, link.Number, now), .. following]);
    }

    /// <summary>The next step for an event; an event that does not fit the step (Decide never emits one) changes nothing.</summary>
    private static OrderState.Placed Advance(OrderState.Placed placed, OrderEvent @event)
    {
        var before = placed.Progress;
        var after = (@event, before) switch
        {
            (OrderEvent.OrderAccepted accepted, OrderProgress.AwaitingErp awaiting) => new OrderProgress.Accepted(new Acceptance(accepted.ErpNumber, accepted.At), awaiting.Request),
            (OrderEvent.OrderAccepted accepted, OrderProgress.Rejected or OrderProgress.Cancelled) => new OrderProgress.Accepted(new Acceptance(accepted.ErpNumber, accepted.At), Maybe<CancellationRequest>.None),
            (OrderEvent.ErpNumberAssigned assigned, _) => WithErpNumber(before, assigned.ErpNumber),
            (OrderEvent.OrderRejected rejected, OrderProgress.AwaitingErp) => new OrderProgress.Rejected(rejected.Reason, rejected.At),
            (OrderEvent.CancellationRequested requested, { Cancellable: true }) => WithRequest(before, new CancellationRequest(requested.By, requested.At)),
            (OrderEvent.CancellationRefused, { Cancellable: true }) => WithRequest(before, Maybe<CancellationRequest>.None),
            (OrderEvent.OrderCancelled cancelled, { Cancellable: true }) => new OrderProgress.Cancelled(cancelled.Reason, cancelled.At, before.Acceptance, cancelled.OnRequest),
            (OrderEvent.OrderDispatched dispatched, OrderProgress.Accepted accepted) => new OrderProgress.Dispatched(accepted.By, dispatched.At, accepted.Request),
            (OrderEvent.OrderDispatched dispatched, OrderProgress.Cancelled cancelled) => cancelled.Before.Match<OrderProgress>(
                acceptance => new OrderProgress.Dispatched(acceptance, dispatched.At, Maybe<CancellationRequest>.None),
                () => before),
            (OrderEvent.OrderInvoiced invoiced, not OrderProgress.Invoiced) => before.Acceptance.Match<OrderProgress>(
                acceptance => new OrderProgress.Invoiced(acceptance, invoiced.At),
                () => before),
            _ => before,
        };
        return after == before ? placed : placed.With(after, Milestone(@event));
    }

    private static OrderProgress WithErpNumber(OrderProgress progress, ErpOrderNumber number) =>
        progress switch
        {
            OrderProgress.Accepted accepted => accepted with { By = accepted.By with { ErpNumber = number } },
            OrderProgress.Dispatched dispatched => dispatched with { By = dispatched.By with { ErpNumber = number } },
            OrderProgress.Invoiced invoiced => invoiced with { By = invoiced.By with { ErpNumber = number } },
            _ => progress,
        };

    private static OrderProgress WithRequest(OrderProgress progress, Maybe<CancellationRequest> request) =>
        progress switch
        {
            OrderProgress.AwaitingErp awaiting => awaiting with { Request = request },
            OrderProgress.Accepted accepted => accepted with { Request = request },
            OrderProgress.Dispatched dispatched => dispatched with { Request = request },
            _ => progress,
        };

    private static Maybe<OrderMilestone> Milestone(OrderEvent @event) =>
        @event switch
        {
            OrderEvent.OrderAccepted e => new OrderMilestone(OrderMilestoneKind.Accepted, e.At, Maybe<Reason>.None),
            OrderEvent.OrderRejected e => new OrderMilestone(OrderMilestoneKind.Rejected, e.At, e.Reason),
            OrderEvent.CancellationRequested e => new OrderMilestone(OrderMilestoneKind.CancellationRequested, e.At, Maybe<Reason>.None),
            OrderEvent.CancellationRefused e => new OrderMilestone(OrderMilestoneKind.CancellationRefused, e.At, e.Reason),
            OrderEvent.OrderCancelled e => new OrderMilestone(OrderMilestoneKind.Cancelled, e.At, e.Reason),
            OrderEvent.OrderDispatched e => new OrderMilestone(OrderMilestoneKind.Dispatched, e.At, Maybe<Reason>.None),
            OrderEvent.OrderInvoiced e => new OrderMilestone(OrderMilestoneKind.Invoiced, e.At, Maybe<Reason>.None),
            _ => Maybe<OrderMilestone>.None,
        };

    private static Result<EquatableArray<OrderEvent>, Error> Events(params ReadOnlySpan<OrderEvent> events) => EquatableArray.Create(events);
}
