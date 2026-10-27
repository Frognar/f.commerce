using FCommerce.Access.Contracts;
using FCommerce.Application.EventStore;
using FCommerce.Application.Messaging;
using FCommerce.Application.Subscriptions;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.Ordering.Persistence;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Ordering;

/// <summary>
/// Keeps the order list and the ERP feed up to date from order events. Every event rewrites the order's row from
/// its whole stream, so handling an event again changes nothing; feed entries are keyed by the event's position.
/// The buyer's name and e-mail and the customer's name are read when the order first appears, and again by later
/// events of the order while they could not be read.
/// </summary>
internal sealed class OrderListProjection(
    OrderStore orders,
    OrderingDbContext db,
    OrderingClock clock,
    IQueryHandler<GetUserContact, Result<UserContact, Error>> contacts,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers) : IEventSubscription
{
    public string Name => "ordering.order-list";

    public bool Accepts(RecordedEvent recordedEvent) => recordedEvent.StreamId.Category == OrderStream.Category;

    public async Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken)
    {
        foreach (var orderId in OrderStream.OrderId(recordedEvent.StreamId).AsEnumerable())
        {
            // An order stream starts with OrderPlaced: every command on a missing order is refused.
            var state = await orders.LoadAsync(orderId, cancellationToken) as OrderState.Placed
                ?? throw Stored.Corrupt($"order stream {orderId} has events but no OrderPlaced");
            var row = await (await db.Orders.SingleOrNoneAsync(o => o.Id == orderId, cancellationToken))
                .Match(Task.FromResult, () => Task.FromResult(NewRow(orderId, state)));
            await NamesAsync(row, state.Order, cancellationToken);
            row.Status = OrderViews.Status(state.Status);
            row.ErpNumber = state.ErpNumber.Map(n => n.Value).OrNull();
            row.UpdatedAt = clock.Now;
            await FeedAsync(recordedEvent, orderId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private OrderRow NewRow(Guid orderId, OrderState.Placed state)
    {
        var placed = state.Order;
        var totals = placed.Totals;
        var row = new OrderRow
        {
            Id = orderId,
            Number = placed.Number.Value,
            CustomerCode = placed.CustomerCode.Value,
            PlacedBy = placed.PlacedBy,
            PlacedAt = placed.PlacedAt,
            PlacedOn = clock.DayOf(placed.PlacedAt),
            Status = OrderViews.Status(state.Status),
            Currency = totals.Currency.Code,
            Net = totals.Net,
            Gross = totals.Gross,
            LineCount = placed.Lines.Count,
            CustomerReference = placed.Details.CustomerReference.Map(r => r.Value).OrNull(),
        };
        db.Orders.Add(row);
        return row;
    }

    /// <summary>Names stay unknown (null) while Access or Customers cannot tell them, and are read again next time.</summary>
    private async Task NamesAsync(OrderRow row, OrderEvent.OrderPlaced placed, CancellationToken cancellationToken)
    {
        if (row.PlacedByName is null)
        {
            foreach (var buyer in (await contacts.HandleAsync(new GetUserContact(placed.PlacedBy), cancellationToken)).ToMaybe().AsEnumerable())
            {
                (row.PlacedByName, row.PlacedByEmail) = (buyer.Name, buyer.Email);
            }
        }

        if (row.CustomerName is null)
        {
            foreach (var customer in (await customers.HandleAsync(new GetCustomer(placed.CustomerCode.Value), cancellationToken)).ToMaybe().AsEnumerable())
            {
                row.CustomerName = customer.Name;
                row.SalespersonEmail = customer.Salesperson.ToMaybe().Bind(s => s.Email.ToMaybe()).Map(e => e.ToLowerInvariant()).OrNull();
            }
        }
    }

    /// <summary>The ERP takes over placed orders and the buyers' requests to cancel them.</summary>
    private async Task FeedAsync(RecordedEvent recordedEvent, Guid orderId, CancellationToken cancellationToken)
    {
        var type = recordedEvent.Data switch
        {
            OrderEvent.OrderPlaced => Maybe.Some(OrderFeedTypes.Placed),
            OrderEvent.CancellationRequested => Maybe.Some(OrderFeedTypes.CancellationRequested),
            _ => Maybe<string>.None,
        };
        foreach (var entry in type.AsEnumerable())
        {
            if (!await db.Feed.AnyAsync(f => f.Position == recordedEvent.GlobalPosition, cancellationToken))
            {
                db.Feed.Add(new OrderFeedRow { Position = recordedEvent.GlobalPosition, OrderId = orderId, Type = entry, At = recordedEvent.RecordedAt });
            }
        }
    }
}

/// <summary>
/// Publishes what happened to orders, one public event per business event of the stream. The event store is the
/// outbox: an integration event follows a stored order event, at least once.
/// </summary>
internal sealed class OrderIntegrationEvents(OrderStore orders, IIntegrationEventPublisher publisher) : IEventSubscription
{
    public string Name => "ordering.integration-events";

    public bool Accepts(RecordedEvent recordedEvent) =>
        recordedEvent.StreamId.Category == OrderStream.Category
        && recordedEvent.Data is OrderEvent.OrderPlaced or OrderEvent.OrderAccepted or OrderEvent.OrderRejected
            or OrderEvent.CancellationRequested or OrderEvent.CancellationRefused or OrderEvent.OrderCancelled
            or OrderEvent.OrderDispatched or OrderEvent.OrderInvoiced;

    public async Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken)
    {
        foreach (var orderId in OrderStream.OrderId(recordedEvent.StreamId).AsEnumerable())
        {
            var placed = recordedEvent.Data as OrderEvent.OrderPlaced
                ?? (await orders.LoadAsync(orderId, cancellationToken) as OrderState.Placed)?.Order
                ?? throw Stored.Corrupt($"order stream {orderId} has events but no OrderPlaced");
            await publisher.PublishAsync(Translate(orderId, placed, (OrderEvent)recordedEvent.Data), cancellationToken);
        }
    }

    private static IIntegrationEvent Translate(Guid id, OrderEvent.OrderPlaced order, OrderEvent data)
    {
        var (number, customer, buyer) = (order.Number.Value, order.CustomerCode.Value, order.PlacedBy);
        return data switch
        {
            OrderEvent.OrderPlaced => new OrderPlaced(id, number, customer, buyer, order.PlacedAt, [.. order.Lines.Select(l => new OrderItemData(l.Sku.Value, l.Quantity.Value))]),
            OrderEvent.OrderAccepted e => new OrderAccepted(id, number, customer, buyer, e.ErpNumber.Map(n => n.Value).OrNull(), e.At),
            OrderEvent.OrderRejected e => new OrderRejected(id, number, customer, buyer, e.Reason.Map(r => r.Value).OrNull(), e.At),
            OrderEvent.CancellationRequested e => new OrderCancellationRequested(id, number, customer, buyer, e.By, e.At),
            OrderEvent.CancellationRefused e => new OrderCancellationRefused(id, number, customer, buyer, e.Reason.Map(r => r.Value).OrNull(), e.At),
            OrderEvent.OrderCancelled e => new OrderCancelled(id, number, customer, buyer, e.Reason.Map(r => r.Value).OrNull(), e.OnRequest, e.At),
            OrderEvent.OrderDispatched e => new OrderDispatched(id, number, customer, buyer, e.DeliveryNote.Value, e.At),
            OrderEvent.OrderInvoiced e => new OrderInvoiced(id, number, customer, buyer, e.Invoice.Value, e.At),
            _ => throw new ArgumentOutOfRangeException(nameof(data), data, "Not a public order event."),
        };
    }
}

/// <summary>
/// An ERP document names orders by the platform or the ERP number: each order of that customer gets the document
/// linked (once; numbers of orders placed outside the platform are ignored).
/// </summary>
internal sealed class LinkDocumentToOrders(OrderStore orders, OrderingDbContext db) : IIntegrationEventHandler<DocumentChanged>
{
    public async Task HandleAsync(DocumentChanged integrationEvent, CancellationToken cancellationToken = default)
    {
        var numbers = integrationEvent.OrderNumbers.OrEmpty().ToList();
        if (numbers.Count == 0)
        {
            return;
        }

        // Documents validated what it publishes: a value that does not parse is a broken contract.
        var link = (from id in DocumentId.Parse(integrationEvent.Id.OrEmpty())
                    from kind in OrderViews.DocumentKind(integrationEvent.Type.OrEmpty())
                    from number in DocumentNumber.Parse(integrationEvent.Number.OrEmpty())
                    select new OrderCommand.LinkDocument(id, kind, number))
            .Match(command => command, error => throw new InvalidOperationException($"Document {integrationEvent.Id} cannot be linked: {error.Message}"));

        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerCode == integrationEvent.CustomerCode && (numbers.Contains(o.Number) || (o.ErpNumber != null && numbers.Contains(o.ErpNumber))))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var linked = await orders.DecideAsync(id, link, "erp", cancellationToken);
            linked.Switch(_ => { }, error => throw new InvalidOperationException($"Linking document {integrationEvent.Id} to order {id} failed: {error.Code}"));
        }
    }
}

/// <summary>Operators see the orders of their customers: the order list follows the customer's salesperson.</summary>
internal sealed class FollowCustomerSalesperson(OrderingDbContext db) : IIntegrationEventHandler<CustomerChanged>
{
    public async Task HandleAsync(CustomerChanged integrationEvent, CancellationToken cancellationToken = default)
    {
        var email = integrationEvent.SalespersonEmail.ToMaybe().Map(e => e.Trim().ToLowerInvariant()).OrNull();
        await db.Orders
            .Where(o => o.CustomerCode == integrationEvent.Code && o.SalespersonEmail != email)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.SalespersonEmail, email), cancellationToken);
    }
}
