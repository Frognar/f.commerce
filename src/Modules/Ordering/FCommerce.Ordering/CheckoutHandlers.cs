using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Ordering;

/// <summary>
/// The reviewed cart and the delivery addresses of a buyer, read through the cart and customer contracts. The other
/// modules validated what they send; a value that does not parse here is a broken contract, not a buyer's mistake.
/// </summary>
internal sealed class CheckoutReader(
    IQueryHandler<GetCart, CartView> carts,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
{
    public async Task<(CartSnapshot Cart, AddressBook Addresses)> ReadAsync(BuyerTerms buyer, IReadOnlyList<string> cultures, CancellationToken cancellationToken)
    {
        var cart = Snapshot(await carts.HandleAsync(new GetCart(new CartOwner(buyer.UserId, buyer.CustomerCode), cultures, Limit(buyer).OrNull()), cancellationToken));
        var customer = (await customers.HandleAsync(new GetCustomer(buyer.CustomerCode), cancellationToken)).ToMaybe();
        return customer.Match(
            details => (cart, Book(details, buyer.AllowedAddressCodes.OrEmpty())),
            () => (cart with { Readiness = CartReadiness.Incomplete }, AddressBook.Empty));
    }

    /// <summary>No limit, or one with its currency: a limit without a currency must not turn into no limit.</summary>
    private static Maybe<CartOrderLimit> Limit(BuyerTerms buyer) =>
        (buyer.OrderLimit, buyer.OrderLimitCurrency) switch
        {
            (null, null) => Maybe<CartOrderLimit>.None,
            ({ } amount, { } currency) => new CartOrderLimit(amount, currency),
            _ => throw new InvalidOperationException("The buyer's order limit needs both its amount and its currency."),
        };

    public static CartSnapshot Snapshot(CartView cart) =>
        new(
            Contract(CartVersion.Parse(cart.Version), "cart version"),
            !cart.Complete ? CartReadiness.Incomplete : cart.CanOrder ? CartReadiness.Orderable : CartReadiness.NotOrderable,
            [.. cart.Lines.Select(Line)]);

    /// <summary>A line with a price and everything an order line needs is priced; any other line is not.</summary>
    private static CheckoutLine Line(CartLineView line)
    {
        var sku = Contract(Sku.Parse(line.Sku), "cart SKU");
        var quantity = Contract(OrderQuantity.Parse(line.Quantity), "cart quantity");
        var name = line.Name.ToMaybe().Map(raw => Contract(LineName.Parse(raw), "product name"));
        var unit = line.Unit.ToMaybe().Map(raw => Contract(UnitCode.Parse(raw), "unit"));
        var priced =
            from productName in name
            from unitCode in unit
            from currency in line.Currency.ToMaybe()
            from price in line.UnitNet.ToMaybe()
            from rate in line.VatRate.ToMaybe()
            from list in line.PriceListCode.ToMaybe()
            select (CheckoutLine)new CheckoutLine.Priced(
                Contract(Currency.Parse(currency), "currency"),
                new OrderLine(
                    sku,
                    productName,
                    unitCode,
                    quantity,
                    Contract(NetPrice.Parse(price), "net price"),
                    Contract(VatRate.Parse(rate), "VAT rate"),
                    Contract(PriceListCode.Parse(list), "price list"),
                    line.Issues.Any(issue => issue.Code == CartLineIssues.Backorder)));
        return priced.ValueOr(() => new CheckoutLine.Unpriced(sku, name, unit, quantity));
    }

    /// <summary>The customer's addresses; without the customer the cart is not complete, so nothing is ordered to a guess.</summary>
    private static AddressBook Book(CustomerDetails customer, IReadOnlyList<string> allowed) =>
        AddressBook.Of(
            customer.DeliveryAddresses.Select(a => (Address(a), a.IsDefault)),
            AddressScope.Of(allowed.Where(code => code.Trim().Length > 0).Select(code => Contract(AddressCode.Parse(code), "allowed address code"))));

    private static DeliveryAddress Address(DeliveryAddressView address)
    {
        var country = Contract(CountryCode.Parse(address.Address.Country), "country");
        return new DeliveryAddress(
            Contract(AddressCode.Parse(address.Code), "address code"),
            address.Name.ToMaybe().Map(name => Contract(AddressText.Parse(name), "address name")),
            Contract(AddressText.Parse(address.Address.Street), "street"),
            Contract(PostalCode.Parse(address.Address.PostalCode, country), "postal code"),
            Contract(AddressText.Parse(address.Address.City), "city"),
            country);
    }

    private static T Contract<T>(Result<T, Error> value, string what)
        where T : notnull =>
        value.Match(valid => valid, error => throw new InvalidOperationException($"The checkout read an invalid {what}: {error.Message}"));
}

internal sealed class GetCheckoutHandler(CheckoutReader reader, OrderingClock clock) : IQueryHandler<GetCheckout, CheckoutView>
{
    public async Task<CheckoutView> HandleAsync(GetCheckout query, CancellationToken cancellationToken = default)
    {
        var (cart, addresses) = await reader.ReadAsync(query.Buyer, query.Cultures.OrEmpty(), cancellationToken);
        IReadOnlyList<string> issues = Issues(cart, addresses);
        return new CheckoutView(
            cart.Version.Value,
            issues.Count == 0,
            issues,
            [.. cart.Lines.Select(OrderViews.Line)],
            cart.Total.Map(OrderViews.Total).OrNull(),
            [.. addresses.Permitted.Select(OrderViews.Address)],
            addresses.Suggested.Map(a => a.Code.Value).OrNull(),
            clock.Today);
    }

    /// <summary>What prevents placing the order, in the order <see cref="OrderDraft.Validate"/> checks it.</summary>
    private static IReadOnlyList<string> Issues(CartSnapshot cart, AddressBook addresses) =>
    [
        .. cart.Readiness == CartReadiness.Incomplete || (cart.Readiness == CartReadiness.Orderable && cart.Lines.Any(line => line is CheckoutLine.Unpriced))
            ? ["cart-incomplete"] : Array.Empty<string>(),
        .. cart.Lines.IsEmpty ? ["empty"] : Array.Empty<string>(),
        .. cart.Readiness == CartReadiness.NotOrderable && !cart.Lines.IsEmpty ? ["cannot-order"] : Array.Empty<string>(),
        .. cart.PricedLines.Select(line => line.Currency).Distinct().Count() > 1 ? ["mixed-currencies"] : Array.Empty<string>(),
        .. !addresses.Addresses.IsEmpty && addresses.Permitted.IsEmpty ? ["no-address-allowed"] : Array.Empty<string>(),
    ];
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

        return await CustomerCode.Parse(command.Buyer.CustomerCode.OrEmpty())
            .Bind(customer => OrderDraft.Validate(cart, form, addresses, today).ToResult().Map(order => (Customer: customer, Order: order)))
            .BindAsync(async valid =>
            {
                var number = await numbers.NextAsync(today.Year, cancellationToken);
                var placed = await orders.DecideAsync(
                    command.OrderId,
                    new OrderCommand.Place(number, valid.Customer, command.Buyer.UserId, valid.Order),
                    command.Buyer.UserId.ToString("N"),
                    cancellationToken);
                return await placed.Match<Task<Result<OrderPlacement, Error>>>(
                    _ => Task.FromResult(Result.Ok<OrderPlacement, Error>(new OrderPlacement(command.OrderId, number.Value, false, Items(valid.Order.Lines)))),
                    async error => error.Code == "order.exists"
                        ? (await ExistingAsync(command, cancellationToken)).ValueOr(Result.Fail<OrderPlacement, Error>(error))
                        : Result.Fail<OrderPlacement, Error>(error));
            });
    }

    /// <summary>The order placed before under this id; another buyer's id is not theirs to see.</summary>
    private async Task<Maybe<Result<OrderPlacement, Error>>> ExistingAsync(PlaceOrder command, CancellationToken cancellationToken) =>
        await orders.LoadAsync(command.OrderId, cancellationToken) is OrderState.Placed { Order: var placed }
            ? placed.PlacedBy == command.Buyer.UserId && placed.CustomerCode.Value == command.Buyer.CustomerCode
                ? Result.Ok<OrderPlacement, Error>(new OrderPlacement(command.OrderId, placed.Number.Value, true, Items(placed.Lines)))
                : Error.Conflict("order.exists", "This order has already been placed.")
            : Maybe<Result<OrderPlacement, Error>>.None;

    private static IReadOnlyList<OrderItemData> Items(NonEmptyArray<OrderLine> lines) =>
        [.. lines.Select(line => new OrderItemData(line.Sku.Value, line.Quantity.Value))];
}

/// <summary>The buyer asks; the request goes to the ERP through the order feed.</summary>
internal sealed class RequestOrderCancellationHandler(OrderStore orders) : ICommandHandler<RequestOrderCancellation, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RequestOrderCancellation command, CancellationToken cancellationToken = default) =>
        (await orders.DecideAsync(command.OrderId, new OrderCommand.RequestCancellation(command.UserId), command.UserId.ToString("N"), cancellationToken)).Map(_ => Unit.Value);
}

internal sealed class AcceptOrderHandler(OrderStore orders) : ICommandHandler<AcceptOrder, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(AcceptOrder command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(
            command.Number.OrEmpty(),
            ErpOrderNumber.Parse(command.ErpNumber.OrEmpty()).Map(number => (OrderCommand)new OrderCommand.Accept(number)),
            cancellationToken);
}

internal sealed class RejectOrderHandler(OrderStore orders) : ICommandHandler<RejectOrder, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(RejectOrder command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(
            command.Number.OrEmpty(),
            Reason.ParseOptional(command.Reason.ToMaybe()).Map(reason => (OrderCommand)new OrderCommand.Reject(reason)),
            cancellationToken);
}

internal sealed class CancelOrderHandler(OrderStore orders) : ICommandHandler<CancelOrder, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(CancelOrder command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(
            command.Number.OrEmpty(),
            Reason.ParseOptional(command.Reason.ToMaybe()).Map(reason => (OrderCommand)new OrderCommand.Cancel(reason)),
            cancellationToken);
}

internal sealed class RefuseOrderCancellationHandler(OrderStore orders) : ICommandHandler<RefuseOrderCancellation, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(RefuseOrderCancellation command, CancellationToken cancellationToken = default) =>
        orders.DecideByNumberAsync(
            command.Number.OrEmpty(),
            Reason.ParseOptional(command.Reason.ToMaybe()).Map(reason => (OrderCommand)new OrderCommand.RefuseCancellation(reason)),
            cancellationToken);
}
