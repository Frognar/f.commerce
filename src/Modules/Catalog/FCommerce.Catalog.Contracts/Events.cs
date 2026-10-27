using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Catalog.Contracts;

/// <summary>A product was created, changed or (de)activated. Published as "catalog.product-changed.v1".</summary>
/// <param name="Active">Offered to buyers: the status is not <see cref="ProductStatuses.Inactive"/>.</param>
/// <param name="Status">One of <see cref="ProductStatuses"/> (added later: absent in older events, read as active).</param>
public sealed record ProductChanged(string Sku, bool Active, string Status = ProductStatuses.Active) : IIntegrationEvent;

/// <summary>Values of <see cref="ProductChanged.Status"/>.</summary>
public static class ProductStatuses
{
    public const string Active = "active";

    /// <summary>Offered, but no longer replenished: sold only while stock lasts.</summary>
    public const string Discontinued = "discontinued";

    public const string Inactive = "inactive";
}
