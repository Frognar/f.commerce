using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;

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

    public static DeliveryAddressScope Only(EquatableArray<string> codes) => new OnlyAddresses(codes);

    public sealed record AllAddresses : DeliveryAddressScope;

    public sealed record OnlyAddresses(EquatableArray<string> Codes) : DeliveryAddressScope;
}

/// <summary>What a user may do for one customer (attributes of the membership, edited in the UI).</summary>
public sealed record MembershipAttributes(
    bool CanOrder,
    OrderScope OrderScope,
    Maybe<Money> OrderLimit,
    bool CanViewDocuments,
    bool CanManageUsers,
    DeliveryAddressScope DeliveryAddresses)
{
    public static MembershipAttributes Administrator { get; } = new(true, OrderScope.Company, Maybe<Money>.None, true, true, DeliveryAddressScope.All);

    public static MembershipAttributes Buyer { get; } = new(true, OrderScope.Own, Maybe<Money>.None, false, false, DeliveryAddressScope.All);

    public static MembershipAttributes Accounting { get; } = new(false, OrderScope.Company, Maybe<Money>.None, true, false, DeliveryAddressScope.All);

    /// <summary>Named attribute sets offered in the UI; they are only starting points.</summary>
    public static IReadOnlyDictionary<string, MembershipAttributes> Templates { get; } = new Dictionary<string, MembershipAttributes>
    {
        ["administrator"] = Administrator,
        ["buyer"] = Buyer,
        ["accounting"] = Accounting,
    };
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
