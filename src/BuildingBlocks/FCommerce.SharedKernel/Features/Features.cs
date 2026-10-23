using System.Collections.Immutable;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Features;

/// <summary>A platform function that can be absent for a client or switched off by them.</summary>
public sealed record FeatureId
{
    private FeatureId(string value) => Value = value;

    public string Value { get; }

    public static Result<FeatureId, Error> Parse(string? raw) =>
        Feature.All.FirstOrDefault(f => f.Id.Value == raw?.Trim()) is { } feature
            ? feature.Id
            : Error.Validation("feature.unknown", $"'{raw}' is not a known feature.");

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

    /// <summary>The feature catalog. Modules add their features here (one list, so dependencies can be checked).</summary>
    public static ImmutableArray<Feature> All { get; } =
    [
        new(CustomerAccounts, "Customer users can sign in (customer area).", true, []),
        new(MultiCustomerAccounts, "One user can act for several customers (company switcher).", true, [CustomerAccounts]),
        new(CustomerAdminInvitations, "Customer administrators invite and manage their colleagues.", true, [CustomerAccounts]),
        new(ErpContactInvitations, "ERP contacts with portal access are invited automatically.", true, [CustomerAccounts]),
        new(SelfRegistration, "Companies apply for an account; operators decide.", false, [CustomerAccounts]),
        new(Documents, "Customers see invoices and other documents from the ERP.", true, [CustomerAccounts]),
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
