using FCommerce.Application.Messaging;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.Ordering.Persistence;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Ordering;

/// <summary>Domain values as contract views.</summary>
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
            _ => OrderStatuses.Cancelled,
        };

    public static OrderTotalView Total(OrderTotals totals) =>
        new(totals.Currency, totals.Net, [.. totals.Rates.Select(r => new OrderVatView(r.Rate, r.Net, r.Vat))], totals.Vat, totals.Gross);

    public static OrderAddressView Address(DeliveryAddress address) =>
        new(address.Code, address.Name.OrNull(), address.Street, address.PostalCode, address.City, address.Country);

    public static string DocumentType(LinkedDocumentKind kind) =>
        kind switch
        {
            LinkedDocumentKind.Invoice => "invoice",
            LinkedDocumentKind.CorrectionInvoice => "correction-invoice",
            LinkedDocumentKind.DeliveryNote => "delivery-note",
            LinkedDocumentKind.Proforma => "proforma",
            _ => "other",
        };

    public static LinkedDocumentKind DocumentKind(string type) =>
        type switch
        {
            "invoice" => LinkedDocumentKind.Invoice,
            "correction-invoice" => LinkedDocumentKind.CorrectionInvoice,
            "delivery-note" => LinkedDocumentKind.DeliveryNote,
            "proforma" => LinkedDocumentKind.Proforma,
            _ => LinkedDocumentKind.Other,
        };

    public static OrderView Order(Guid id, OrderEvent.OrderPlaced placed, OrderState state, Maybe<OrderRow> row) =>
        new(
            id,
            placed.Number,
            state.ErpNumber.OrNull(),
            placed.CustomerCode,
            placed.PlacedBy,
            row.Map(r => r.PlacedByName).Where(name => name.Length > 0).OrNull(),
            placed.PlacedAt,
            Status(state.Status),
            [.. state.Milestones.Select(m => new OrderMilestoneView(Status(m.Status), m.At))],
            [.. placed.Lines.Select(l => new OrderLineView(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitNet, l.VatRate, l.NetAmount, l.Backorder))],
            Total(placed.Totals),
            placed.Details.CustomerReference.OrNull(),
            placed.Details.Notes.OrNull(),
            placed.Details.RequestedDate.OrNull(),
            placed.Details.Address.Map(Address).OrNull(),
            [.. state.Documents.Select(d => new LinkedDocumentView(d.DocumentId, DocumentType(d.Kind), d.Number, d.At))],
            state.Cancellation.Map(c => c.By == CancelledBy.Customer ? "customer" : "seller").OrNull(),
            state.Rejection.Bind(r => r.Reason).Or(() => state.Cancellation.Bind(c => c.Reason)).OrNull());

    public static OrderExport Export(Guid id, OrderEvent.OrderPlaced placed, OrderState state, Maybe<OrderRow> row) =>
        new(
            id,
            placed.Number,
            placed.CustomerCode,
            placed.PlacedAt,
            new OrderBuyer(placed.PlacedBy, row.Map(r => r.PlacedByName).ValueOr(string.Empty), row.Map(r => r.PlacedByEmail).ValueOr(string.Empty)),
            Status(state.Status),
            state.ErpNumber.OrNull(),
            [.. placed.Lines.Select((l, i) => new OrderExportLine(i + 1, l.Sku, l.Name, l.Unit, l.Quantity, l.UnitNet, l.VatRate, l.NetAmount, l.PriceListCode, l.Backorder))],
            Total(placed.Totals),
            placed.Details.CustomerReference.OrNull(),
            placed.Details.Notes.OrNull(),
            placed.Details.RequestedDate.OrNull(),
            placed.Details.Address.Map(Address).OrNull());
}

internal sealed class GetOrderHandler(OrderStore orders, OrderingDbContext db) : IQueryHandler<GetOrder, Result<OrderView, Error>>
{
    public async Task<Result<OrderView, Error>> HandleAsync(GetOrder query, CancellationToken cancellationToken = default)
    {
        var state = await orders.LoadAsync(query.OrderId, cancellationToken);
        var row = await db.Orders.AsNoTracking().SingleOrNoneAsync(o => o.Id == query.OrderId, cancellationToken);
        return state.Placed
            .Map(placed => OrderViews.Order(query.OrderId, placed, state, row))
            .ToResult(() => Error.NotFound("order.not-found", "The order does not exist."));
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
            var state = await orders.LoadAsync(entry.OrderId, cancellationToken);
            exported.AddRange(state.Placed
                .Map(placed => new OrderFeedEntry(entry.Position, entry.Type, entry.At, OrderViews.Export(entry.OrderId, placed, state, rows.GetValueOrNone(entry.OrderId))))
                .AsEnumerable());
        }

        return new OrderFeedPage(exported, entries.Count == 0 ? query.After : entries[^1].Position);
    }
}
