using FCommerce.Application.EventStore;
using FCommerce.Testing;

namespace FCommerce.Infrastructure.Tests.EventStore;

/// <summary>Keeps the in-memory test double honest: it must behave like the real adapters.</summary>
public sealed class InMemoryEventStoreContractTests : EventStoreContractTests
{
    protected override IEventStore Store { get; } = new InMemoryEventStore(ContractEvents.Registry.NameOf);
}
