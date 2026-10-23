using FCommerce.Application.EventStore;
using FCommerce.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Infrastructure.Tests.EventStore;

public sealed class MartenEventStoreContractTests(PostgresFixture postgres) : EventStoreContractTests, IAsyncDisposable
{
    private readonly ServiceProvider services = postgres.BuildServices();

    protected override IEventStore Store => services.GetRequiredService<IEventStore>();

    public ValueTask DisposeAsync() => services.DisposeAsync();
}
