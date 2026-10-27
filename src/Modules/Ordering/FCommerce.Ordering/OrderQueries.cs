using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Documents.Contracts;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.Ordering.Persistence;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Ordering;

/// <summary>Domain values as contract views; every value has its name, none falls back to another.</summary>
internal static class OrderViews
{
    public static string Status(OrderStatus status) =>
        status switch
        {
            OrderStatus.Placed => OrderStatuses.Placed,
            OrderStatus.Accepted => OrderStatuses.Accepted,
            OrderStatus.InProgress => OrderStatuses.InProgress,
            OrderStatus.Completed => OrderStatuses.Completed,
            OrderStatus.Rejected => OrderStatuses.Rejected,
            OrderStatus.Cancelled => OrderStatuses.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "An order status without a name."),
        };

    public static string Step(OrderMilestoneKind kind) =>
        kind switch
        {
            OrderMilestoneKind.Placed => OrderSteps.Placed,
            OrderMilestoneKind.Accepted => OrderSteps.Accepted,
            OrderMilestoneKind.CancellationRequested => OrderSteps.CancellationRequested,
            OrderMilestoneKind.CancellationRefused => OrderSteps.CancellationRefused,
            OrderMilestoneKind.Rejected => OrderSteps.Rejected,
            OrderMilestoneKind.Cancelled => OrderSteps.Cancelled,
            OrderMilestoneKind.Dispatched => OrderSteps.InProgress,
            OrderMilestoneKind.Invoiced => OrderSteps.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "An order step without a name."),
        };

    public static OrderTotalView Total(OrderTotals totals) =>
        new(totals.Currency.Code, totals.Net, [.. totals.Rates.Select(r => new OrderVatView(r.Rate.Percent, r.Net, r.Vat))], totals.Vat, totals.Gross);

    public static OrderLineView Line(OrderLine line) =>
        new(line.Sku.Value, line.Name.Value, line.Unit.Value, line.Quantity.Value, line.UnitNet.Value, line.VatRate.Percent, line.NetAmount, line.Backorder);

    /// <summary>A line of the checkout; one without a price shows zeros (the checkout reports it as an issue).</summary>
    public static OrderLineView Line(CheckoutLine line) =>
        line switch
        {
            CheckoutLine.Priced priced => Line(priced.Line),
            CheckoutLine.Unpriced unpriced => new(
                unpriced.Sku.Value, unpriced.Name.Map(n => n.Value).ValueOr(unpriced.Sku.Value), unpriced.Unit.Map(u => u.Value).ValueOr(string.Empty), unpriced.Quantity.Value, 0m, 0m, 0m, false),
            _ => throw new ArgumentOutOfRangeException(nameof(line), line, "A checkout line of an unknown kind."),
        };

    public static OrderAddressView Address(DeliveryAddress address) =>
        new(address.Code.Value, address.Name.Map(n => n.Value).OrNull(), address.Street.Value, address.PostalCode.Value, address.City.Value, address.Country.Value);

    public static string DocumentType(LinkedDocumentKind kind) =>
        kind switch
        {
            LinkedDocumentKind.Invoice => DocumentTypes.Invoice,
            LinkedDocumentKind.CorrectionInvoice => DocumentTypes.CorrectionInvoice,
            LinkedDocumentKind.DeliveryNote => DocumentTypes.DeliveryNote,
            LinkedDocumentKind.Proforma => DocumentTypes.Proforma,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A document kind without a name."),
        };

    public static Result<LinkedDocumentKind, Error> DocumentKind(string type) =>
        type switch
        {
            DocumentTypes.Invoice => LinkedDocumentKind.Invoice,
            DocumentTypes.CorrectionInvoice => LinkedDocumentKind.CorrectionInvoice,
            DocumentTypes.DeliveryNote => LinkedDocumentKind.DeliveryNote,
            DocumentTypes.Proforma => LinkedDocumentKind.Proforma,
            _ => Error.Validation("document-type.unknown", $"Unknown document type '{type}'."),
        };

    public static OrderView Order(Guid id, OrderState.Placed state, Maybe<string> placedByName)
    {
        var placed = state.Order;
        return new(
            id,
            placed.Number.Value,
            state.ErpNumber.Map(n => n.Value).OrNull(),
            placed.CustomerCode.Value,
            placed.PlacedBy,
            placedByName.OrNull(),
            placed.PlacedAt,
            Status(state.Status),
            [.. state.History.Select(m => new OrderMilestoneView(Step(m.Kind), m.At, m.Reason.Map(r => r.Value).OrNull()))],
            [.. placed.Lines.Select(Line)],
            Total(placed.Totals),
            placed.Details.CustomerReference.Map(r => r.Value).OrNull(),
            placed.Details.Notes.Map(n => n.Value).OrNull(),
            placed.Details.RequestedDate.OrNull(),
            placed.Details.Address.Map(Address).OrNull(),
            [.. state.Documents.Select(d => new LinkedDocumentView(d.DocumentId.Value, DocumentType(d.Kind), d.Number.Value, d.At))],
            state.Progress.CancellationRequest.Map(request => request.At).OrNull(),
            state.Progress is OrderProgress.Cancelled { OnRequest: true },
            ReasonOf(state.Progress).Map(r => r.Value).OrNull());
    }

    /// <summary>Why the order was rejected or cancelled.</summary>
    private static Maybe<Reason> ReasonOf(OrderProgress progress) =>
        progress switch
        {
            OrderProgress.Rejected rejected => rejected.Reason,
            OrderProgress.Cancelled cancelled => cancelled.Reason,
            _ => Maybe<Reason>.None,
        };

    public static OrderExport Export(Guid id, OrderState.Placed state, Maybe<OrderRow> row)
    {
        var placed = state.Order;
        return new(
            id,
            placed.Number.Value,
            placed.CustomerCode.Value,
            placed.PlacedAt,
            new OrderBuyer(placed.PlacedBy, row.Bind(r => r.PlacedByName.ToMaybe()).OrNull(), row.Bind(r => r.PlacedByEmail.ToMaybe()).OrNull()),
            Status(state.Status),
            state.ErpNumber.Map(n => n.Value).OrNull(),
            [.. placed.Lines.Select((l, i) => new OrderExportLine(i + 1, l.Sku.Value, l.Name.Value, l.Unit.Value, l.Quantity.Value, l.UnitNet.Value, l.VatRate.Percent, l.NetAmount, l.PriceList.Value, l.Backorder))],
            Total(placed.Totals),
            placed.Details.CustomerReference.Map(r => r.Value).OrNull(),
            placed.Details.Notes.Map(n => n.Value).OrNull(),
            placed.Details.RequestedDate.OrNull(),
            placed.Details.Address.Map(Address).OrNull());
    }
}

/// <summary>The order from its stream (current right after placing); the buyer's name from the order list or, before it caught up, from Access.</summary>
internal sealed class GetOrderHandler(OrderStore orders, OrderingDbContext db, IQueryHandler<GetUserContact, Result<UserContact, Error>> contacts)
    : IQueryHandler<GetOrder, Result<OrderView, Error>>
{
    public async Task<Result<OrderView, Error>> HandleAsync(GetOrder query, CancellationToken cancellationToken = default)
    {
        if (await orders.LoadAsync(query.OrderId, cancellationToken) is not OrderState.Placed state)
        {
            return Error.NotFound("order.not-found", "The order does not exist.");
        }

        var row = await db.Orders.AsNoTracking().SingleOrNoneAsync(o => o.Id == query.OrderId, cancellationToken);
        var name = await row.Bind(r => r.PlacedByName.ToMaybe()).Match(
            known => Task.FromResult(Maybe.Some(known)),
            async () => (await contacts.HandleAsync(new GetUserContact(state.Order.PlacedBy), cancellationToken)).ToMaybe().Map(c => c.Name));
        return OrderViews.Order(query.OrderId, state, name);
    }
}

internal sealed class GetOrdersHandler(OrderingDbContext db) : IQueryHandler<GetOrders, Result<OrderPage, Error>>
{
    public async Task<Result<OrderPage, Error>> HandleAsync(GetOrders query, CancellationToken cancellationToken = default)
    {
        var status = query.Status.ToMaybe().Map(s => s.Trim()).Where(s => s.Length > 0);
        if (status.Exists(s => !OrderStatuses.All.Contains(s)))
        {
            return Error.Validation("status.unknown", "Unknown order status.") with { Field = "status" };
        }

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, GetOrders.MaxPageSize);
        var orders = db.Orders.AsNoTracking();
        orders = query.CustomerCode.ToMaybe().Match(code => orders.Where(o => o.CustomerCode == code), () => orders);
        orders = query.PlacedBy.ToMaybe().Match(user => orders.Where(o => o.PlacedBy == user), () => orders);
        orders = query.SalespersonEmail.ToMaybe().Map(e => e.Trim().ToLowerInvariant()).Match(email => orders.Where(o => o.SalespersonEmail == email), () => orders);
        orders = status.Match(s => orders.Where(o => o.Status == s), () => orders);
        orders = query.PlacedFrom.ToMaybe().Match(from => orders.Where(o => o.PlacedOn >= from), () => orders);
        orders = query.PlacedTo.ToMaybe().Match(to => orders.Where(o => o.PlacedOn <= to), () => orders);
        orders = query.Number.ToMaybe().Map(n => n.Trim()).Where(n => n.Length > 0)
            .Match(n => orders.Where(o => o.Number == n || o.ErpNumber == n || o.CustomerReference == n), () => orders);

        var total = await orders.CountAsync(cancellationToken);
        var rows = await orders.OrderByDescending(o => o.PlacedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new OrderPage(
            [.. rows.Select(o => new OrderSummaryView(
                o.Id, o.Number, o.ErpNumber, o.CustomerCode, o.CustomerName, o.PlacedBy, o.PlacedByName, o.PlacedAt, o.Status, o.Currency, o.Net, o.Gross, o.LineCount, o.CustomerReference))],
            page,
            pageSize,
            total);
    }
}

internal sealed class GetOrderFeedHandler(OrderStore orders, OrderingDbContext db) : IQueryHandler<GetOrderFeed, OrderFeedPage>
{
    public async Task<OrderFeedPage> HandleAsync(GetOrderFeed query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, GetOrderFeed.MaxLimit);
        var entries = await db.Feed.AsNoTracking().Where(f => f.Position > query.After).OrderBy(f => f.Position).Take(limit).ToListAsync(cancellationToken);
        var ids = entries.Select(e => e.OrderId).Distinct().ToList();
        var rows = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);

        var exported = new List<OrderFeedEntry>();
        foreach (var entry in entries)
        {
            // A feed entry is written only for an order that was placed.
            var state = await orders.LoadAsync(entry.OrderId, cancellationToken) as OrderState.Placed
                ?? throw Stored.Corrupt($"feed entry {entry.Position} names order {entry.OrderId}, which was not placed");
            exported.Add(new OrderFeedEntry(entry.Position, entry.Type, entry.At, OrderViews.Export(entry.OrderId, state, rows.GetValueOrNone(entry.OrderId))));
        }

        return new OrderFeedPage(exported, entries.Count == 0 ? query.After : entries[^1].Position);
    }
}
