using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Authorization.Core;

/// <summary>Which orders a customer user sees.</summary>
public enum OrderScope
{
    Own,
    Company,
}

/// <summary>Which delivery addresses a customer user may order to.</summary>
public abstract record DeliveryAddressScope
{
    private DeliveryAddressScope()
    {
    }

    public static DeliveryAddressScope All { get; } = new AllAddresses();

    /// <summary>Only these codes (trimmed, without repeats); none listed means all of them.</summary>
    public static DeliveryAddressScope Of(IEnumerable<string> codes) =>
        codes.Select(code => code.Trim()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToNonEmptyArray()
            .Match<DeliveryAddressScope>(only => new OnlyAddresses(only), () => All);

    public sealed record AllAddresses : DeliveryAddressScope;

    public sealed record OnlyAddresses(NonEmptyArray<string> Codes) : DeliveryAddressScope;
}

/// <summary>The highest net value of an order a user may place: more than zero, at most 2 decimal places, bounded.</summary>
public sealed record OrderLimit : IPersistedValue<OrderLimit, Money>
{
    public const decimal Max = 1_000_000_000m;

    private OrderLimit(Money value) => Value = value;

    public Money Value { get; }

    public static Result<OrderLimit, Error> Parse(decimal amount, string currency) =>
        amount switch
        {
            <= 0 => Error.Validation("membership.order-limit-not-positive", "The order limit must be greater than zero.") with { Field = "orderLimit" },
            > Max => Error.Validation("membership.order-limit-too-large", "The order limit is too large.") with { Field = "orderLimit" },
            _ when decimal.Round(amount, 2) != amount => Error.Validation("membership.order-limit-precision", "The order limit has at most 2 decimal places.") with { Field = "orderLimit" },
            _ => Currency.Parse(currency).Map(c => new OrderLimit(new Money(amount, c))).MapError(error => error with { Field = "orderLimitCurrency" }),
        };

    static OrderLimit IPersistedValue<OrderLimit, Money>.Rehydrate(Money raw) => new(raw);

    Money IPersistedValue<OrderLimit, Money>.Dehydrate() => Value;

    public override string ToString() => Value.ToString();
}

/// <summary>Whether a user may order for the customer; a limit and allowed addresses exist only for those who may.</summary>
public abstract record OrderingRight
{
    private OrderingRight()
    {
    }

    public static OrderingRight NotAllowed { get; } = new Denied();

    public static OrderingRight Unlimited { get; } = new Allowed(Maybe<OrderLimit>.None, DeliveryAddressScope.All);

    public sealed record Denied : OrderingRight;

    /// <param name="Limit">None: any value.</param>
    public sealed record Allowed(Maybe<OrderLimit> Limit, DeliveryAddressScope Addresses) : OrderingRight;
}

/// <summary>What a user may do for one customer (attributes of the membership, edited in the UI).</summary>
/// <param name="OrderScope">Which orders the user sees (also without the right to order).</param>
public sealed record MembershipAttributes(
    OrderingRight Ordering,
    OrderScope OrderScope,
    bool CanViewDocuments,
    bool CanManageUsers)
{
    public static MembershipAttributes Administrator { get; } = new(OrderingRight.Unlimited, OrderScope.Company, true, true);

    public static MembershipAttributes Buyer { get; } = new(OrderingRight.Unlimited, OrderScope.Own, false, false);

    public static MembershipAttributes Accounting { get; } = new(OrderingRight.NotAllowed, OrderScope.Company, true, false);

    /// <summary>Named attribute sets offered in the UI; they are only starting points.</summary>
    public static IReadOnlyDictionary<string, MembershipAttributes> Templates { get; } = new Dictionary<string, MembershipAttributes>
    {
        ["administrator"] = Administrator,
        ["buyer"] = Buyer,
        ["accounting"] = Accounting,
    };

    public bool CanOrder => Ordering is OrderingRight.Allowed;

    public Maybe<OrderLimit> OrderLimit => Ordering is OrderingRight.Allowed allowed ? allowed.Limit : Maybe<OrderLimit>.None;

    public DeliveryAddressScope DeliveryAddresses => Ordering is OrderingRight.Allowed allowed ? allowed.Addresses : DeliveryAddressScope.All;

    /// <summary>The attributes of a template; an unknown name is an error, never another template.</summary>
    public static Result<MembershipAttributes, Error> Template(string name) =>
        Templates.GetValueOrNone(name.Trim()).ToResult(() =>
            Error.Validation("membership.template-unknown", $"Unknown attribute template '{name}'; expected one of: {string.Join(", ", Templates.Keys)}."));
}

/// <summary>Which customers an operator handles.</summary>
public enum CustomerScope
{
    All,

    /// <summary>Only customers whose salesperson e-mail is the operator's e-mail.</summary>
    AsSalesperson,
}

/// <summary>What a seller's staff member may do in the back-office.</summary>
public sealed record OperatorAttributes(
    bool CanManageSettings,
    bool CanManageOperators,
    bool CanDecideRegistrations,
    CustomerScope CustomerScope)
{
    public static OperatorAttributes PlatformAdministrator { get; } = new(true, true, true, CustomerScope.All);

    public static OperatorAttributes Salesperson { get; } = new(false, false, true, CustomerScope.AsSalesperson);

    public static IReadOnlyDictionary<string, OperatorAttributes> Templates { get; } = new Dictionary<string, OperatorAttributes>
    {
        ["platform-administrator"] = PlatformAdministrator,
        ["salesperson"] = Salesperson,
    };
}
