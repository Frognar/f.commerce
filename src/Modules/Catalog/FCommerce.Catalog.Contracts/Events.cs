using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Catalog.Contracts;

/// <summary>A product was created, changed or (de)activated. Published as "catalog.product-changed.v1".</summary>
public sealed record ProductChanged(string Sku, bool Active) : IIntegrationEvent;
