using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;
using FCommerce.Testing;

namespace FCommerce.Infrastructure.Tests.EventStore;

public sealed record ThingCreated(Guid ThingId, string Name);

public sealed record ThingRenamed(string Name);

/// <summary>Exercises serialization of every shared-kernel value type an event may carry.</summary>
/// <summary>Never registered: appending it must fail.</summary>
public sealed record UnregisteredThing(string Name);

public sealed record ThingDescribed(LocalizedText Description, EquatableArray<string> Tags, Money Price, CultureCode Culture);

public static class ContractEvents
{
    public static readonly MessageTypeRegistration[] Registrations =
    [
        new(typeof(ThingCreated), "contract.thing-created.v1"),
        new(typeof(ThingRenamed), "contract.thing-renamed.v1"),
        new(typeof(ThingDescribed), "contract.thing-described.v1"),
    ];

    public static readonly MessageTypeRegistry Registry = MessageTypeRegistry.Create(Registrations).ShouldSucceed();
}
