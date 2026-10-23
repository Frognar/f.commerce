using FCommerce.Application.EventStore;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Results;
using FCommerce.Testing;

namespace FCommerce.Application.Tests;

public sealed class EventSourcingTests
{
    private static readonly DecisionContext Context = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly StreamId Stream = Values.Stream("counter", "1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stores_decided_events_and_returns_the_evolved_state()
    {
        var store = new InMemoryEventStore();

        var first = await store.DecideAsync(Stream, Counter.Decider, new Increment(2), Context, EventMetadata.None, Token).ShouldSucceed();
        var second = await store.DecideAsync(Stream, Counter.Decider, new Increment(3), Context, EventMetadata.None, Token).ShouldSucceed();

        Assert.Equal(new Decision<int, Incremented>(5, 2, EquatableArray.Create(new Incremented(3))), second);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, (await store.ReadStreamAsync(Stream, Token)).Count);
    }

    [Fact]
    public async Task Returns_the_decider_error_and_stores_nothing()
    {
        var store = new InMemoryEventStore();

        var error = await store.DecideAsync(Stream, Counter.Decider, new Increment(-1), Context, EventMetadata.None, Token).ShouldFail();

        Assert.Equal("counter.negative", error.Code);
        Assert.Empty(await store.ReadStreamAsync(Stream, Token));
    }

    [Fact]
    public async Task No_events_means_no_append()
    {
        var store = new InMemoryEventStore();

        var decision = await store.DecideAsync(Stream, Counter.Decider, new Increment(0), Context, EventMetadata.None, Token).ShouldSucceed();

        Assert.Equal(new Decision<int, Incremented>(0, 0, EquatableArray<Incremented>.Empty), decision);
        Assert.Empty(await store.ReadStreamAsync(Stream, Token));
    }

    [Fact]
    public async Task Retries_with_fresh_state_after_a_concurrency_conflict()
    {
        var inner = new InMemoryEventStore();
        var store = new InterferingStore(inner, interferences: 1);

        var decision = await store.DecideAsync(Stream, Counter.Decider, new Increment(1), Context, EventMetadata.None, Token).ShouldSucceed();

        Assert.Equal(11, decision.State); // the concurrent +10 is included, not overwritten
        Assert.Equal(2, decision.Version);
    }

    [Fact]
    public async Task Gives_up_after_repeated_conflicts()
    {
        var store = new InterferingStore(new InMemoryEventStore(), interferences: int.MaxValue);

        var error = await store.DecideAsync(Stream, Counter.Decider, new Increment(1), Context, EventMetadata.None, Token).ShouldFail();

        Assert.Equal(EventStoreErrors.ConcurrencyConflictCode, error.Code);
    }

    private sealed record Increment(int By);

    private sealed record Incremented(int By);

    private static class Counter
    {
        public static readonly Decider<Increment, int, Incremented> Decider = new(
            Decide: (command, _, _) => command.By switch
            {
                < 0 => Error.Validation("counter.negative", "Cannot decrement."),
                0 => EquatableArray<Incremented>.Empty,
                _ => EquatableArray.Create(new Incremented(command.By)),
            },
            Evolve: (state, e) => state + e.By,
            Initial: 0);
    }

    /// <summary>Simulates another writer that appends just before each of our appends.</summary>
    private sealed class InterferingStore(IEventStore inner, int interferences) : IEventStore
    {
        private int remaining = interferences;

        public async Task<Result<long, Error>> AppendAsync(StreamId streamId, ExpectedVersion expectedVersion, IReadOnlyList<object> events, EventMetadata metadata, CancellationToken cancellationToken = default)
        {
            if (remaining-- > 0)
            {
                await inner.AppendAsync(streamId, ExpectedVersion.Any, [new Incremented(10)], metadata, cancellationToken);
            }

            return await inner.AppendAsync(streamId, expectedVersion, events, metadata, cancellationToken);
        }

        public Task<IReadOnlyList<RecordedEvent>> ReadStreamAsync(StreamId streamId, CancellationToken cancellationToken = default) =>
            inner.ReadStreamAsync(streamId, cancellationToken);

        public Task<IReadOnlyList<RecordedEvent>> ReadAllAsync(long afterPosition, int maxCount, CancellationToken cancellationToken = default) =>
            inner.ReadAllAsync(afterPosition, maxCount, cancellationToken);
    }
}
