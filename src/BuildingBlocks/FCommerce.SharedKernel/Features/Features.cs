using System.Collections.Immutable;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Features;

/// <summary>A platform function that can be absent for a client or switched off by them.</summary>
public sealed record FeatureId
{
    private FeatureId(string value) => Value = value;

    public string Value { get; }

    public static Result<FeatureId, Error> Parse(string raw) =>
        Feature.All.Select(f => f.Id).FirstOrNone(id => id.Value == raw.Trim())
            .ToResult(() => Error.Validation("feature.unknown", $"'{raw}' is not a known feature."));

    internal static FeatureId Define(string value) => new(value);

    public override string ToString() => Value;
}

/// <param name="RequiresAll">Features that must be on for this one to work.</param>
public sealed record Feature(FeatureId Id, string Description, bool EnabledByDefault, ImmutableArray<FeatureId> RequiresAll)
{
    public static readonly FeatureId CustomerAccounts = FeatureId.Define("customer-accounts");
    public static readonly FeatureId MultiCustomerAccounts = FeatureId.Define("multi-customer-accounts");
    public static readonly FeatureId CustomerAdminInvitations = FeatureId.Define("customer-admin-invitations");
    public static readonly FeatureId ErpContactInvitations = FeatureId.Define("erp-contact-invitations");
    public static readonly FeatureId SelfRegistration = FeatureId.Define("self-registration");
    public static readonly FeatureId Documents = FeatureId.Define("documents");
    public static readonly FeatureId PublicPrices = FeatureId.Define("public-prices");
    public static readonly FeatureId StockLevels = FeatureId.Define("stock-levels");
    public static readonly FeatureId Cart = FeatureId.Define("cart");
    public static readonly FeatureId QuickOrder = FeatureId.Define("quick-order");
    public static readonly FeatureId ShoppingLists = FeatureId.Define("shopping-lists");
    public static readonly FeatureId ReorderFromDocuments = FeatureId.Define("reorder-from-documents");
    public static readonly FeatureId Ordering = FeatureId.Define("ordering");
    public static readonly FeatureId ReorderFromOrders = FeatureId.Define("reorder-from-orders");
    public static readonly FeatureId OrderFeed = FeatureId.Define("order-feed");

    /// <summary>The feature catalog. Modules add their features here (one list, so dependencies can be checked).</summary>
    public static ImmutableArray<Feature> All { get; } =
    [
        new(CustomerAccounts, "Customer users can sign in (customer area).", true, []),
        new(MultiCustomerAccounts, "One user can act for several customers (company switcher).", true, [CustomerAccounts]),
        new(CustomerAdminInvitations, "Customer administrators invite and manage their colleagues.", true, [CustomerAccounts]),
        new(ErpContactInvitations, "ERP contacts with portal access are invited automatically.", true, [CustomerAccounts]),
        new(SelfRegistration, "Companies apply for an account; operators decide.", false, [CustomerAccounts]),
        new(Documents, "Customers see invoices and other documents from the ERP.", true, [CustomerAccounts]),
        new(PublicPrices, "Visitors who are not signed in see the default price list.", true, []),
        new(StockLevels, "The storefront shows stock levels and upcoming deliveries.", true, []),
        new(Cart, "Customer users with the right to order collect products in a cart.", true, [CustomerAccounts]),
        new(QuickOrder, "Products are added to the cart by SKU or by pasting a list.", true, [Cart]),
        new(ShoppingLists, "Carts are saved as shopping lists and added back to the cart.", true, [Cart]),
        new(ReorderFromDocuments, "Lines of an invoice or delivery note are added to the cart.", true, [Cart, Documents]),
        new(Ordering, "Orders are placed from the cart; customers see their order history.", true, [Cart]),
        new(ReorderFromOrders, "Lines of an earlier order are added to the cart.", true, [Ordering]),
        new(OrderFeed, "The ERP takes orders from a feed of the integration API and confirms them.", true, [Ordering]),
    ];
}

/// <summary>
/// Which features are on: <see cref="Available"/> is set per client instance when deploying (what was bought/built),
/// <see cref="Enabled"/> is switched by the seller in the back-office, within what is available.
/// </summary>
public sealed record FeatureSet(ImmutableHashSet<FeatureId> Available, ImmutableHashSet<FeatureId> Enabled)
{
    public static FeatureSet AllAvailable(IEnumerable<FeatureId> enabled) =>
        new([.. Feature.All.Select(f => f.Id)], [.. enabled]);

    /// <summary>On = available, enabled, and every required feature is on too.</summary>
    public bool IsOn(FeatureId feature) =>
        Available.Contains(feature)
        && Enabled.Contains(feature)
        && Feature.All.Single(f => f.Id == feature).RequiresAll.All(IsOn);

    public ImmutableHashSet<FeatureId> On => [.. Feature.All.Select(f => f.Id).Where(IsOn)];

    /// <summary>Validates a change of the enabled features: only available ones, with their requirements enabled.</summary>
    public Result<FeatureSet, Error> WithEnabled(IEnumerable<FeatureId> enabled)
    {
        var requested = enabled.ToImmutableHashSet();
        var unavailable = requested.Where(f => !Available.Contains(f)).ToList();
        if (unavailable.Count > 0)
        {
            return Error.Validation("feature.unavailable", $"Not available in this installation: {string.Join(", ", unavailable)}.");
        }

        var missing = Feature.All
            .Where(f => requested.Contains(f.Id))
            .SelectMany(f => f.RequiresAll.Where(r => !requested.Contains(r)).Select(r => $"{f.Id} requires {r}"))
            .ToList();
        return missing.Count > 0
            ? Error.Validation("feature.missing-requirement", string.Join("; ", missing))
            : this with { Enabled = requested };
    }
}
