using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Ordering;

/// <summary>The reviewed cart and the delivery addresses of a buyer, read through the cart and customer contracts.</summary>
internal sealed class CheckoutReader(
    IQueryHandler<GetCart, CartView> carts,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
{
    public async Task<(CartSnapshot Cart, AddressBook Addresses)> ReadAsync(BuyerTerms buyer, IReadOnlyList<string> cultures, CancellationToken cancellationToken)
    {
        var limit = buyer.OrderLimit.ToMaybe()
            .Bind(amount => buyer.OrderLimitCurrency.ToMaybe().Map(currency => new CartOrderLimit(amount, currency)));
        var cart = await carts.HandleAsync(new GetCart(new CartOwner(buyer.UserId, buyer.CustomerCode), cultures, limit.OrNull()), cancellationToken);
        var customer = (await customers.HandleAsync(new GetCustomer(buyer.CustomerCode), cancellationToken)).ToMaybe();
        return (Snapshot(cart), Book(customer, buyer.AllowedAddressCodes.ToMaybe()));
    }

    public static CartSnapshot Snapshot(CartView cart) =>
        new(
            cart.Version,
            cart.Complete,
            cart.CanOrder,
            [.. cart.Lines.Select(line => new CartLine(
                line.Sku,
                line.Name.ToMaybe(),
                line.Unit.ToMaybe(),
                line.Quantity,
                line.UnitNet.ToMaybe(),
                line.VatRate.ToMaybe(),
                line.NetAmount.ToMaybe(),
                line.PriceListCode.ToMaybe(),
                line.Issues.Any(issue => issue.Code == "backorder")))],
            [.. cart.Totals.Select(t => new OrderTotals(t.Currency, t.Net, [.. t.Rates.Select(r => new VatTotal(r.Rate, r.Net, r.Vat))], t.Vat, t.Gross))]);

    private static AddressBook Book(Maybe<CustomerDetails> customer, Maybe<IReadOnlyList<string>> allowed) =>
        new(
            customer.Match(
                c => (EquatableArray<DeliveryAddress>)[.. c.DeliveryAddresses.Select(a => new DeliveryAddress(a.Code, a.Name.ToMaybe(), a.Address.Street, a.Address.PostalCode, a.Address.City, a.Address.Country))],
                () => EquatableArray<DeliveryAddress>.Empty),
            customer.Bind(c => c.DeliveryAddresses.FirstOrNone(a => a.IsDefault)).Map(a => a.Code),
            allowed.Where(codes => codes.Count > 0).Map(codes => (EquatableArray<string>)[.. codes]));
}

internal sealed class GetCheckoutHandler(CheckoutReader reader, OrderingClock clock) : IQueryHandler<GetCheckout, CheckoutView>
{
    public async Task<CheckoutView> HandleAsync(GetCheckout query, CancellationToken cancellationToken = default)
    {
        var (cart, addresses) = await reader.ReadAsync(query.Buyer, query.Cultures.OrEmpty(), cancellationToken);
        var noAddress = !addresses.Addresses.IsEmpty && addresses.Permitted.IsEmpty;
        IReadOnlyList<string> issues =
        [
            .. cart.Complete ? [] : new[] { "cart-incomplete" },
            .. cart.Lines.IsEmpty ? ["empty"] : Array.Empty<string>(),
            .. !cart.CanOrder && !cart.Lines.IsEmpty ? ["cannot-order"] : Array.Empty<string>(),
            .. noAddress ? ["no-address-allowed"] : Array.Empty<string>(),
        ];
        return new CheckoutView(
            cart.Version,
            cart.Complete && cart.CanOrder && cart.Totals.Count == 1 && !noAddress,
            issues,
            [.. cart.Lines.Select(line => new OrderLineView(
                line.Sku, line.Name.ValueOr(line.Sku), line.Unit.ValueOr(string.Empty), line.Quantity, line.UnitNet.ValueOr(0m), line.VatRate.ValueOr(0m), line.NetAmount.ValueOr(0m), line.Backorder))],
            cart.Totals.FirstOrNone().Map(OrderViews.Total).OrNull(),
            [.. addresses.Permitted.Select(OrderViews.Address)],
            addresses.Suggested.Map(a => a.Code).OrNull(),
            clock.Today);
    }
}

/// <summary>
/// Read the cart and addresses → validate (pure) → number → append the order (a stream that must not exist yet, so
/// the same form sent twice places one order). The cart is emptied by the <c>OrderPlaced</c> event.
/// </summary>
internal sealed class PlaceOrderHandler(CheckoutReader reader, OrderStore orders, OrderNumberCounter numbers, OrderingClock clock)
    : ICommandHandler<PlaceOrder, Result<OrderPlacement, Error>>
{
    public async Task<Result<OrderPlacement, Error>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken = default) =>
        await (await ExistingAsync(command, cancellationToken)).Match(Task.FromResult, () => PlaceAsync(command, cancellationToken));

    private async Task<Result<OrderPlacement, Error>> PlaceAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        var (cart, addresses) = await reader.ReadAsync(command.Buyer, command.Cultures.OrEmpty(), cancellationToken);
        var form = new CheckoutForm(
            command.CartVersion,
            command.ExpectedGross,
            command.CustomerReference.ToMaybe(),
            command.Notes.ToMaybe(),
            command.RequestedDate.ToMaybe(),
            command.AddressCode.ToMaybe());
        var today = clock.Today;

        return await OrderDraft.Validate(cart, form, addresses, today).ToResult().BindAsync(async order =>
        {
            var number = await numbers.NextAsync(today.Year, cancellationToken);
            var placed = await orders.DecideAsync(
                command.OrderId,
                new OrderCommand.Place(number, command.Buyer.CustomerCode, command.Buyer.UserId, order),
                command.Buyer.UserId.ToString("N"),
                cancellationToken);
            return await placed.Match<Task<Result<OrderPlacement, Error>>>(
                _ => Task.FromResult(Result.Ok<OrderPlacement, Error>(new OrderPlacement(command.OrderId, number, false, Items(order.Lines)))),
                async error => error.Code == "order.exists"
                    ? (await ExistingAsync(command, cancellationToken)).ValueOr(Result.Fail<OrderPlacement, Error>(error))
                    : Result.Fail<OrderPlacement, Error>(error));
        });
    }

    /// <summary>The order placed before under this id; another buyer's id is not theirs to see.</summary>
    private async Task<Maybe<Result<OrderPlacement, Error>>> ExistingAsync(PlaceOrder command, CancellationToken cancellationToken) =>
        (await orders.LoadAsync(command.OrderId, cancellationToken)).Placed.Map(placed =>
            placed.PlacedBy == command.Buyer.UserId && placed.CustomerCode == command.Buyer.CustomerCode
                ? Result.Ok<OrderPlacement, Error>(new OrderPlacement(command.OrderId, placed.Number, true, Items(placed.Lines)))
                : Error.Conflict("order.exists", "This order has already been placed."));

    private static IReadOnlyList<OrderItemData> Items(EquatableArray<OrderLine> lines) =>
        [.. lines.Select(line => new OrderItemData(line.Sku, line.Quantity))];
}

internal sealed class CancelOrderHandler(OrderStore orders) : ICommandHandler<CancelOrder, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(CancelOrder command, CancellationToken cancellationToken = default) =>
        (await orders.DecideAsync(command.OrderId, new OrderCommand.CancelByCustomer(), command.UserId.ToString("N"), cancellationToken)).Map(_ => Unit.Value);
}

internal sealed class AcceptOrderHandler(OrderStore orders) : ICommandHandler<AcceptOrder, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(AcceptOrder command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(command.Number.OrEmpty(), new OrderCommand.Accept(command.ErpNumber.OrEmpty()), cancellationToken);
}

internal sealed class RejectOrderHandler(OrderStore orders) : ICommandHandler<RejectOrder, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(RejectOrder command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(command.Number.OrEmpty(), new OrderCommand.Reject(command.Reason.ToMaybe()), cancellationToken);
}

internal sealed class CancelOrderBySellerHandler(OrderStore orders) : ICommandHandler<CancelOrderBySeller, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(CancelOrderBySeller command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(command.Number.OrEmpty(), new OrderCommand.CancelBySeller(command.Reason.ToMaybe()), cancellationToken);
}
