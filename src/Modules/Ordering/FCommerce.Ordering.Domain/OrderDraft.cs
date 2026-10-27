using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Ordering.Domain;

/// <summary>A line of the reviewed cart: priced, with everything an order line needs, or not (price on request, product gone).</summary>
public abstract record CheckoutLine
{
    private CheckoutLine()
    {
    }

    public abstract Sku Sku { get; }

    public abstract OrderQuantity Quantity { get; }

    public sealed record Priced(Currency Currency, OrderLine Line) : CheckoutLine
    {
        public override Sku Sku => Line.Sku;

        public override OrderQuantity Quantity => Line.Quantity;
    }

    /// <param name="Name">None when the product is no longer in the catalog.</param>
    public sealed record Unpriced(Sku Item, Maybe<LineName> Name, Maybe<UnitCode> Unit, OrderQuantity Amount) : CheckoutLine
    {
        public override Sku Sku => Item;

        public override OrderQuantity Quantity => Amount;
    }
}

/// <summary>What the cart review says about ordering the cart.</summary>
public enum CartReadiness
{
    /// <summary>Some lookup of the review failed: it may be too lenient, so nothing is ordered from it.</summary>
    Incomplete,

    /// <summary>Something in the cart prevents ordering it (prices, stock, the order limit, a blocked customer).</summary>
    NotOrderable,

    Orderable,
}

/// <summary>The reviewed cart an order is placed from.</summary>
public sealed record CartSnapshot(CartVersion Version, CartReadiness Readiness, EquatableArray<CheckoutLine> Lines)
{
    public IEnumerable<CheckoutLine.Priced> PricedLines => Lines.OfType<CheckoutLine.Priced>();

    /// <summary>The value of the priced lines, when they are all in one currency.</summary>
    public Maybe<OrderTotals> Total =>
        PricedLines.Select(line => line.Currency).Distinct().ToList() is [var currency]
            ? OrderTotals.Of(currency, PricedLines.Select(line => line.Line))
            : Maybe<OrderTotals>.None;
}

/// <summary>What the buyer sent from the checkout page.</summary>
/// <param name="ExpectedVersion">The version of the cart the buyer saw.</param>
/// <param name="ExpectedGross">The gross total the buyer saw.</param>
[RawInput]
public sealed record CheckoutForm(
    long ExpectedVersion,
    decimal ExpectedGross,
    Maybe<string> CustomerReference,
    Maybe<string> Notes,
    Maybe<DateOnly> RequestedDate,
    Maybe<string> AddressCode);

/// <summary>The delivery addresses a buyer may order to.</summary>
public abstract record AddressScope
{
    private AddressScope()
    {
    }

    public static AddressScope All { get; } = new AllAddresses();

    /// <summary>Only these; none listed means all of them.</summary>
    public static AddressScope Of(IEnumerable<AddressCode> codes) =>
        codes.Distinct().ToNonEmptyArray().Match<AddressScope>(only => new OnlyAddresses(only), () => All);

    public sealed record AllAddresses : AddressScope;

    public sealed record OnlyAddresses(NonEmptyArray<AddressCode> Codes) : AddressScope;
}

/// <summary>The customer's delivery addresses (unique codes, at most one default among them) and the ones the buyer may order to.</summary>
public sealed record AddressBook
{
    private AddressBook(EquatableArray<DeliveryAddress> addresses, Maybe<AddressCode> defaultCode, AddressScope allowed)
    {
        Addresses = addresses;
        DefaultCode = defaultCode;
        Allowed = allowed;
    }

    public EquatableArray<DeliveryAddress> Addresses { get; }

    /// <summary>The customer's default address; always one of <see cref="Addresses"/>.</summary>
    public Maybe<AddressCode> DefaultCode { get; }

    public AddressScope Allowed { get; }

    public static AddressBook Empty { get; } = new(EquatableArray<DeliveryAddress>.Empty, Maybe<AddressCode>.None, AddressScope.All);

    /// <summary>The addresses in their order (the first of a code wins); the default is the first one marked so.</summary>
    public static AddressBook Of(IEnumerable<(DeliveryAddress Address, bool IsDefault)> addresses, AddressScope allowed)
    {
        var unique = addresses.DistinctBy(a => a.Address.Code).ToList();
        return new(
            [.. unique.Select(a => a.Address)],
            unique.FirstOrNone(a => a.IsDefault).Map(a => a.Address.Code),
            allowed);
    }

    public EquatableArray<DeliveryAddress> Permitted =>
        Allowed switch
        {
            AddressScope.OnlyAddresses only => [.. Addresses.Where(a => only.Codes.Contains(a.Code))],
            _ => Addresses,
        };

    /// <summary>The address offered first: the customer's default if the buyer may use it, else the first permitted one.</summary>
    public Maybe<DeliveryAddress> Suggested =>
        DefaultCode.Bind(code => Permitted.FirstOrNone(a => a.Code == code)).Or(() => Permitted.FirstOrNone());
}

/// <summary>An order ready to be placed: frozen lines in one currency and the buyer's details; only <see cref="OrderDraft.Validate"/> makes one.</summary>
public sealed record ValidOrder
{
    internal ValidOrder(Currency currency, NonEmptyArray<OrderLine> lines, OrderDetails details)
    {
        Currency = currency;
        Lines = lines;
        Details = details;
    }

    public Currency Currency { get; }

    public NonEmptyArray<OrderLine> Lines { get; }

    public OrderDetails Details { get; }

    public OrderTotals Totals => OrderTotals.Of(Currency, Lines);
}

/// <summary>Turning a reviewed cart and the checkout form into an order, or every reason why not.</summary>
public static class OrderDraft
{
    public const int MaxCustomerReference = 50;
    public const int MaxNotes = 1000;
    public const int MaxDaysAhead = 365;

    /// <param name="today">Today in the seller's time zone.</param>
    public static Validated<ValidOrder> Validate(CartSnapshot cart, CheckoutForm form, AddressBook addresses, DateOnly today) =>
        ((Func<(Currency Currency, NonEmptyArray<OrderLine> Lines), OrderDetails, ValidOrder>)((content, details) => new ValidOrder(content.Currency, content.Lines, details)))
            .Apply(Content(cart, form))
            .Apply(((Func<Maybe<BuyerText>, Maybe<BuyerText>, Maybe<DateOnly>, Maybe<DeliveryAddress>, OrderDetails>)((reference, notes, date, address) => new OrderDetails(reference, notes, date, address)))
                .Apply(BuyerText.ParseOptional(form.CustomerReference, MaxCustomerReference, "order.customer-reference-too-long").ToValidated("customerReference"))
                .Apply(BuyerText.ParseOptional(form.Notes, MaxNotes, "order.notes-too-long").ToValidated("notes"))
                .Apply(RequestedDate(form.RequestedDate, today))
                .Apply(Address(form.AddressCode, addresses)));

    /// <summary>The cart must be exactly the one the buyer reviewed, fully known and priced, orderable and in one currency.</summary>
    private static Validated<(Currency Currency, NonEmptyArray<OrderLine> Lines)> Content(CartSnapshot cart, CheckoutForm form)
    {
        if (cart.Readiness == CartReadiness.Incomplete || (cart.Readiness == CartReadiness.Orderable && cart.Lines.Any(line => line is CheckoutLine.Unpriced)))
        {
            return Error.Conflict("order.cart-incomplete", "Prices or stock could not be checked; try again in a moment.");
        }

        var priced = cart.PricedLines.ToList();
        if (priced.Count == 0 || cart.Readiness == CartReadiness.NotOrderable)
        {
            return Error.Conflict("order.cannot-order", "Something in the cart prevents ordering it.");
        }

        if (priced.Select(line => line.Currency).Distinct().Count() != 1)
        {
            return Error.Conflict("order.mixed-currencies", "An order has one currency.");
        }

        var currency = priced[0].Currency;
        var lines = NonEmptyArray.Of(priced[0].Line, [.. priced.Skip(1).Select(line => line.Line)]);
        return cart.Version.Value != form.ExpectedVersion || OrderTotals.Of(currency, lines).Gross != form.ExpectedGross
            ? Error.Conflict("order.cart-changed", "The cart or its prices changed; check them and place the order again.")
            : (currency, lines);
    }

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
            c => book.Permitted.FirstOrNone(a => a.Code.Value == c).Match(
                address => Validated.Valid(Maybe.Some(address)),
                () => Validated.Invalid<Maybe<DeliveryAddress>>(Error.Validation("order.address-not-allowed", "You cannot order to this address.") with { Field = "addressCode" })),
            () => book.Suggested.Match(
                address => Validated.Valid(Maybe.Some(address)),
                () => Validated.Invalid<Maybe<DeliveryAddress>>(Error.Validation("order.no-address-allowed", "You may not order to any delivery address of the company.") with { Field = "addressCode" })));
    }
}

/// <summary>What starts every order number: 1–10 letters or digits.</summary>
public sealed record OrderNumberPrefix
{
    public const int MaxLength = 10;

    private OrderNumberPrefix(string value) => Value = value;

    public string Value { get; }

    public static Result<OrderNumberPrefix, Error> Parse(string raw) =>
        raw.Trim() is { Length: > 0 and <= MaxLength } prefix && prefix.All(char.IsAsciiLetterOrDigit)
            ? new OrderNumberPrefix(prefix)
            : Error.Validation("order-number.prefix", $"The order number prefix has 1–{MaxLength} letters or digits.");

    public override string ToString() => Value;
}

/// <summary>Order numbers: "{prefix}/{year}/{sequence}", the sequence restarting every year.</summary>
public static class OrderNumbers
{
    public static OrderNumber Format(OrderNumberPrefix prefix, int year, long sequence) => OrderNumber.Of($"{prefix.Value}/{year}/{sequence:D6}");
}
