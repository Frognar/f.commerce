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
/// The buyer's name and e-mail and the customer's name are read once, when the order first appears.
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
            var state = await orders.LoadAsync(orderId, cancellationToken);
            foreach (var placed in state.Placed.AsEnumerable())
            {
                var row = await (await db.Orders.SingleOrNoneAsync(o => o.Id == orderId, cancellationToken))
                    .Match(Task.FromResult, () => NewRowAsync(orderId, placed, cancellationToken));
                row.Status = OrderViews.Status(state.Status);
                row.ErpNumber = state.ErpNumber.OrNull();
                row.UpdatedAt = clock.Now;
                await FeedAsync(recordedEvent, orderId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<OrderRow> NewRowAsync(Guid orderId, OrderEvent.OrderPlaced placed, CancellationToken cancellationToken)
    {
        var buyer = (await contacts.HandleAsync(new GetUserContact(placed.PlacedBy), cancellationToken)).ToMaybe();
        var customer = (await customers.HandleAsync(new GetCustomer(placed.CustomerCode), cancellationToken)).ToMaybe();
        var row = new OrderRow
        {
            Id = orderId,
            Number = placed.Number,
            CustomerCode = placed.CustomerCode,
            CustomerName = customer.Map(c => c.Name).ValueOr(placed.CustomerCode),
            SalespersonEmail = customer.Bind(c => c.Salesperson.ToMaybe()).Bind(s => s.Email.ToMaybe()).Map(e => e.ToLowerInvariant()).OrNull(),
            PlacedBy = placed.PlacedBy,
            PlacedByName = buyer.Map(b => b.Name).ValueOr(string.Empty),
            PlacedByEmail = buyer.Map(b => b.Email).ValueOr(string.Empty),
            PlacedAt = placed.PlacedAt,
            PlacedOn = clock.DayOf(placed.PlacedAt),
            Status = OrderStatuses.Placed,
            Currency = placed.Totals.Currency,
            Net = placed.Totals.Net,
            Gross = placed.Totals.Gross,
            LineCount = placed.Lines.Count,
            CustomerReference = placed.Details.CustomerReference.OrNull(),
        };
        db.Orders.Add(row);
        return row;
    }

    /// <summary>The ERP takes over placed orders and the ones the buyer cancelled.</summary>
    private async Task FeedAsync(RecordedEvent recordedEvent, Guid orderId, CancellationToken cancellationToken)
    {
        var type = recordedEvent.Data switch
        {
            OrderEvent.OrderPlaced => Maybe.Some("placed"),
            OrderEvent.OrderCancelled { By: CancelledBy.Customer } => Maybe.Some("cancelled"),
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
/// Publishes the public events of orders. The event store is the outbox: an integration event follows a stored
/// order event, at least once.
/// </summary>
internal sealed class OrderIntegrationEvents(OrderStore orders, IIntegrationEventPublisher publisher) : IEventSubscription
{
    public string Name => "ordering.integration-events";

    public bool Accepts(RecordedEvent recordedEvent) =>
        recordedEvent.StreamId.Category == OrderStream.Category
        && recordedEvent.Data is OrderEvent.OrderPlaced or OrderEvent.OrderAccepted or OrderEvent.OrderRejected or OrderEvent.OrderCancelled;

    public async Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken)
    {
        foreach (var orderId in OrderStream.OrderId(recordedEvent.StreamId).AsEnumerable())
        {
            var placed = recordedEvent.Data is OrderEvent.OrderPlaced own
                ? Maybe.Some(own)
                : (await orders.LoadAsync(orderId, cancellationToken)).Placed;
            foreach (var integrationEvent in placed.Bind(order => Translate(orderId, order, recordedEvent.Data)).AsEnumerable())
            {
                await publisher.PublishAsync(integrationEvent, cancellationToken);
            }
        }
    }

    private static Maybe<IIntegrationEvent> Translate(Guid orderId, OrderEvent.OrderPlaced order, object data) =>
        data switch
        {
            OrderEvent.OrderPlaced => new OrderPlaced(orderId, order.Number, order.CustomerCode, order.PlacedBy, order.PlacedAt, [.. order.Lines.Select(l => new OrderItemData(l.Sku, l.Quantity))]),
            OrderEvent.OrderAccepted accepted => Changed(orderId, order, OrderStatuses.Accepted, Maybe<string>.None, Maybe<string>.None, accepted.At),
            OrderEvent.OrderRejected rejected => Changed(orderId, order, OrderStatuses.Rejected, Maybe<string>.None, rejected.Reason, rejected.At),
            OrderEvent.OrderCancelled cancelled => Changed(orderId, order, OrderStatuses.Cancelled, cancelled.By == CancelledBy.Customer ? "customer" : "seller", cancelled.Reason, cancelled.At),
            _ => Maybe<IIntegrationEvent>.None,
        };

    private static Maybe<IIntegrationEvent> Changed(Guid orderId, OrderEvent.OrderPlaced order, string status, Maybe<string> by, Maybe<string> reason, DateTimeOffset at) =>
        new OrderStatusChanged(orderId, order.Number, order.CustomerCode, order.PlacedBy, status, by.OrNull(), reason.OrNull(), at);
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

        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerCode == integrationEvent.CustomerCode && (numbers.Contains(o.Number) || (o.ErpNumber != null && numbers.Contains(o.ErpNumber))))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var linked = await orders.DecideAsync(
                id,
                new OrderCommand.LinkDocument(integrationEvent.Id, OrderViews.DocumentKind(integrationEvent.Type), integrationEvent.Number),
                "erp",
                cancellationToken);
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
