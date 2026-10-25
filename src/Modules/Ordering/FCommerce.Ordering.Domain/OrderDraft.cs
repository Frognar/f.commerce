using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Ordering.Domain;

/// <summary>A line of the cart as the cart review shows it; the price parts are none when the line is not priced.</summary>
public sealed record CartLine(
    string Sku,
    Maybe<string> Name,
    Maybe<string> Unit,
    decimal Quantity,
    Maybe<decimal> UnitNet,
    Maybe<decimal> VatRate,
    Maybe<decimal> NetAmount,
    Maybe<string> PriceListCode,
    bool Backorder);

/// <summary>The reviewed cart an order is placed from.</summary>
/// <param name="Complete">Every lookup of the review succeeded.</param>
/// <param name="CanOrder">The review found nothing that prevents ordering (prices, stock, limit, blocked customer).</param>
public sealed record CartSnapshot(long Version, bool Complete, bool CanOrder, EquatableArray<CartLine> Lines, EquatableArray<OrderTotals> Totals);

/// <summary>What the buyer sent from the checkout page.</summary>
/// <param name="ExpectedVersion">The version of the cart the buyer saw.</param>
/// <param name="ExpectedGross">The gross total the buyer saw.</param>
public sealed record CheckoutForm(
    long ExpectedVersion,
    decimal ExpectedGross,
    Maybe<string> CustomerReference,
    Maybe<string> Notes,
    Maybe<DateOnly> RequestedDate,
    Maybe<string> AddressCode);

/// <summary>The customer's delivery addresses and the ones the buyer may order to (none: all).</summary>
public sealed record AddressBook(EquatableArray<DeliveryAddress> Addresses, Maybe<string> DefaultCode, Maybe<EquatableArray<string>> Allowed)
{
    public EquatableArray<DeliveryAddress> Permitted =>
        Allowed.Match(codes => (EquatableArray<DeliveryAddress>)[.. Addresses.Where(a => codes.Contains(a.Code))], () => Addresses);

    /// <summary>The address offered first: the customer's default if the buyer may use it, else the first permitted one.</summary>
    public Maybe<DeliveryAddress> Suggested =>
        DefaultCode.Bind(code => Permitted.FirstOrNone(a => a.Code == code)).Or(() => Permitted.FirstOrNone());
}

/// <summary>An order ready to be placed: frozen lines and totals, and the buyer's details.</summary>
public sealed record ValidOrder(EquatableArray<OrderLine> Lines, OrderTotals Totals, OrderDetails Details);

/// <summary>Turning a reviewed cart and the checkout form into an order, or every reason why not.</summary>
public static class OrderDraft
{
    public const int MaxCustomerReference = 50;
    public const int MaxNotes = 1000;
    public const int MaxDaysAhead = 365;

    /// <param name="today">Today in the seller's time zone.</param>
    public static Validated<ValidOrder> Validate(CartSnapshot cart, CheckoutForm form, AddressBook addresses, DateOnly today) =>
        ((Func<(EquatableArray<OrderLine> Lines, OrderTotals Totals), OrderDetails, ValidOrder>)((content, details) => new ValidOrder(content.Lines, content.Totals, details)))
            .Apply(Content(cart, form))
            .Apply(((Func<Maybe<string>, Maybe<string>, Maybe<DateOnly>, Maybe<DeliveryAddress>, OrderDetails>)((reference, notes, date, address) => new OrderDetails(reference, notes, date, address)))
                .Apply(Text(form.CustomerReference, MaxCustomerReference, "order.customer-reference-too-long", "customerReference"))
                .Apply(Text(form.Notes, MaxNotes, "order.notes-too-long", "notes"))
                .Apply(RequestedDate(form.RequestedDate, today))
                .Apply(Address(form.AddressCode, addresses)));

    /// <summary>The cart must be exactly the one the buyer reviewed, fully known, orderable and in one currency.</summary>
    private static Validated<(EquatableArray<OrderLine> Lines, OrderTotals Totals)> Content(CartSnapshot cart, CheckoutForm form)
    {
        if (!cart.Complete)
        {
            return Error.Conflict("order.cart-incomplete", "Prices or stock could not be checked; try again in a moment.");
        }

        if (cart.Lines.IsEmpty || !cart.CanOrder)
        {
            return Error.Conflict("order.cannot-order", "Something in the cart prevents ordering it.");
        }

        if (cart.Totals.Count != 1)
        {
            return Error.Conflict("order.mixed-currencies", "An order has one currency.");
        }

        var totals = cart.Totals[0];
        if (cart.Version != form.ExpectedVersion || totals.Gross != form.ExpectedGross)
        {
            return Error.Conflict("order.cart-changed", "The cart or its prices changed; check them and place the order again.");
        }

        var lines = cart.Lines.Select(Line).ToList();
        return lines.All(line => line.Exists(_ => true))
            ? (lines.Choose(line => line).ToEquatableArray(), totals)
            : Error.Conflict("order.cart-incomplete", "Prices or stock could not be checked; try again in a moment.");
    }

    private static Maybe<OrderLine> Line(CartLine line) =>
        from name in line.Name
        from unit in line.Unit
        from price in line.UnitNet
        from rate in line.VatRate
        from amount in line.NetAmount
        from list in line.PriceListCode
        select new OrderLine(line.Sku, name, unit, line.Quantity, price, rate, amount, list, line.Backorder);

    private static Validated<Maybe<string>> Text(Maybe<string> raw, int max, string code, string field) =>
        raw.Map(text => text.Trim()).Where(text => text.Length > 0).Match(
            text => text.Length > max
                ? Validated.Invalid<Maybe<string>>(Error.Validation(code, $"At most {max} characters.") with { Field = field })
                : Validated.Valid(Maybe.Some(text)),
            () => Validated.Valid(Maybe<string>.None));

    private static Validated<Maybe<DateOnly>> RequestedDate(Maybe<DateOnly> date, DateOnly today) =>
        date.Match(
            value => value < today ? Validated.Invalid<Maybe<DateOnly>>(Error.Validation("order.requested-date-past", "The date cannot be in the past.") with { Field = "requestedDate" })
                : value > today.AddDays(MaxDaysAhead) ? Validated.Invalid<Maybe<DateOnly>>(Error.Validation("order.requested-date-too-far", $"At most {MaxDaysAhead} days ahead.") with { Field = "requestedDate" })
                : Validated.Valid(Maybe.Some(value)),
            () => Validated.Valid(Maybe<DateOnly>.None));

    /// <summary>
    /// Without delivery addresses the ERP decides; otherwise one the buyer may order to, the suggested one when none
    /// was chosen.
    /// </summary>
    private static Validated<Maybe<DeliveryAddress>> Address(Maybe<string> code, AddressBook book)
    {
        if (book.Addresses.IsEmpty)
        {
            return Validated.Valid(Maybe<DeliveryAddress>.None);
        }

        var chosen = code.Map(c => c.Trim()).Where(c => c.Length > 0);
        return chosen.Match(
            c => book.Permitted.FirstOrNone(a => a.Code == c).Match(
                address => Validated.Valid(Maybe.Some(address)),
                () => Validated.Invalid<Maybe<DeliveryAddress>>(Error.Validation("order.address-not-allowed", "You cannot order to this address.") with { Field = "addressCode" })),
            () => book.Suggested.Match(
                address => Validated.Valid(Maybe.Some(address)),
                () => Validated.Invalid<Maybe<DeliveryAddress>>(Error.Validation("order.no-address-allowed", "You may not order to any delivery address of the company.") with { Field = "addressCode" })));
    }
}

/// <summary>Order numbers: "{prefix}/{year}/{sequence}", the sequence restarting every year.</summary>
public static class OrderNumbers
{
    public const int MaxPrefix = 10;

    public static string Format(string prefix, int year, long sequence) => $"{prefix}/{year}/{sequence:D6}";

    /// <summary>A prefix of letters and digits (it starts every order number).</summary>
    public static Result<string, Error> Prefix(string raw) =>
        raw.Trim() is { Length: > 0 and <= MaxPrefix } prefix && prefix.All(char.IsAsciiLetterOrDigit)
            ? prefix
            : Error.Validation("order-number.prefix", $"The order number prefix has 1–{MaxPrefix} letters or digits.");
}
