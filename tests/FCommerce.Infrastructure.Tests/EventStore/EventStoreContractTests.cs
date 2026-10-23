using FCommerce.Application.EventStore;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Results;
using FCommerce.Testing;

namespace FCommerce.Infrastructure.Tests.EventStore;

/// <summary>
/// Behaviour every <see cref="IEventStore"/> adapter must have. A new adapter (e.g. SQL Server) is done
/// when a subclass of this passes. Tests share one database, so each uses its own stream ids.
/// </summary>
public abstract class EventStoreContractTests
{
    protected abstract IEventStore Store { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StreamId NewStream() => Values.NewStream("thing");

    [Fact]
    public async Task Appends_to_new_stream_and_reads_it_back_in_order()
    {
        var stream = NewStream();
        var id = Guid.CreateVersion7();

        var appended = await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingCreated(id, "a"), new ThingRenamed("b")], EventMetadata.None, Token);

        Assert.Equal(2, appended.ShouldSucceed());
        var events = await Store.ReadStreamAsync(stream, Token);
        Assert.Equal([new ThingCreated(id, "a"), new ThingRenamed("b")], events.Select(e => e.Data));
        Assert.Equal([1L, 2L], events.Select(e => e.StreamVersion));
        Assert.All(events, e => Assert.Equal(stream, e.StreamId));
        Assert.Equal(["contract.thing-created.v1", "contract.thing-renamed.v1"], events.Select(e => e.EventType));
        Assert.True(events[0].GlobalPosition < events[1].GlobalPosition);
    }

    [Fact]
    public async Task Reading_a_missing_stream_returns_no_events()
    {
        Assert.Empty(await Store.ReadStreamAsync(NewStream(), Token));
    }

    [Fact]
    public async Task Stores_metadata_with_each_event()
    {
        var stream = NewStream();
        var metadata = new EventMetadata("corr-1", "cause-1", "user-1");

        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("x")], metadata, Token);

        Assert.Equal(metadata, (await Store.ReadStreamAsync(stream, Token)).Single().Metadata);
    }

    [Fact]
    public async Task Round_trips_shared_kernel_value_types()
    {
        var stream = NewStream();
        var described = new ThingDescribed(
            LocalizedText.Of((Values.Culture("pl"), "Śruba"), (Values.Culture("en-GB"), "Bolt")),
            EquatableArray.Create("steel", "m8"),
            new Money(12.34m, Values.Currency("PLN")),
            Values.Culture("en-GB"));

        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [described], EventMetadata.None, Token);

        Assert.Equal(described, (await Store.ReadStreamAsync(stream, Token)).Single().Data);
    }

    [Fact]
    public async Task Rejects_no_stream_expectation_when_stream_exists()
    {
        var stream = NewStream();
        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("a")], EventMetadata.None, Token);

        var second = await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("b")], EventMetadata.None, Token);

        AssertConflict(second);
        Assert.Single(await Store.ReadStreamAsync(stream, Token));
    }

    [Fact]
    public async Task Appends_when_exact_version_matches()
    {
        var stream = NewStream();
        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("a"), new ThingRenamed("b")], EventMetadata.None, Token);

        var appended = await Store.AppendAsync(stream, ExpectedVersion.Exact(2), [new ThingRenamed("c")], EventMetadata.None, Token);

        Assert.Equal(3, appended.ShouldSucceed());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Rejects_stale_or_future_exact_version(long expected)
    {
        var stream = NewStream();
        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("a"), new ThingRenamed("b")], EventMetadata.None, Token);

        var appended = await Store.AppendAsync(stream, ExpectedVersion.Exact(expected), [new ThingRenamed("c")], EventMetadata.None, Token);

        AssertConflict(appended);
        Assert.Equal(2, (await Store.ReadStreamAsync(stream, Token)).Count);
    }

    [Fact]
    public async Task Rejects_exact_version_for_missing_stream()
    {
        var appended = await Store.AppendAsync(NewStream(), ExpectedVersion.Exact(1), [new ThingRenamed("a")], EventMetadata.None, Token);

        AssertConflict(appended);
    }

    [Fact]
    public async Task Any_version_appends_to_new_and_existing_streams()
    {
        var stream = NewStream();

        var first = await Store.AppendAsync(stream, ExpectedVersion.Any, [new ThingRenamed("a")], EventMetadata.None, Token);
        var second = await Store.AppendAsync(stream, ExpectedVersion.Any, [new ThingRenamed("b")], EventMetadata.None, Token);

        Assert.Equal(1, first.ShouldSucceed());
        Assert.Equal(2, second.ShouldSucceed());
    }

    [Fact]
    public async Task Exactly_one_of_concurrent_appends_with_the_same_expectation_wins()
    {
        var stream = NewStream();
        await Store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("start")], EventMetadata.None, Token);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Store.AppendAsync(stream, ExpectedVersion.Exact(1), [new ThingRenamed($"writer-{i}")], EventMetadata.None, Token)));

        Assert.Single(results, Succeeded);
        Assert.All(results.Where(r => !Succeeded(r)), AssertConflict);
        Assert.Equal(2, (await Store.ReadStreamAsync(stream, Token)).Count);
    }

    [Fact]
    public async Task Reads_global_log_after_a_position_in_order_and_paged()
    {
        var before = await LastGlobalPositionAsync();
        var streamA = NewStream();
        var streamB = NewStream();
        await Store.AppendAsync(streamA, ExpectedVersion.NoStream, [new ThingRenamed("a1")], EventMetadata.None, Token);
        await Store.AppendAsync(streamB, ExpectedVersion.NoStream, [new ThingRenamed("b1")], EventMetadata.None, Token);
        await Store.AppendAsync(streamA, ExpectedVersion.Exact(1), [new ThingRenamed("a2")], EventMetadata.None, Token);

        var ours = (await Store.ReadAllAsync(before, 1000, Token))
            .Where(e => e.StreamId == streamA || e.StreamId == streamB)
            .ToList();

        Assert.Equal(["a1", "b1", "a2"], ours.Select(e => ((ThingRenamed)e.Data).Name));
        Assert.True(ours.Zip(ours.Skip(1)).All(p => p.First.GlobalPosition < p.Second.GlobalPosition));

        var firstPage = await Store.ReadAllAsync(before, 1, Token);
        Assert.Single(firstPage);
        Assert.True(firstPage[0].GlobalPosition > before);
    }

    private async Task<long> LastGlobalPositionAsync()
    {
        var position = 0L;
        while (true)
        {
            var page = await Store.ReadAllAsync(position, 1000, Token);
            if (page.Count == 0)
            {
                return position;
            }

            position = page[^1].GlobalPosition;
        }
    }

    [Fact]
    public async Task Rejects_an_empty_batch()
    {
        var stream = NewStream();

        var error = await Store.AppendAsync(stream, ExpectedVersion.Any, [], EventMetadata.None, Token).ShouldFail();

        Assert.Equal(EventStoreErrors.NoEventsCode, error.Code);
        Assert.Empty(await Store.ReadStreamAsync(stream, Token));
    }

    [Fact]
    public async Task Rejects_event_types_without_a_registered_name_and_stores_nothing()
    {
        var stream = NewStream();

        var error = await Store.AppendAsync(stream, ExpectedVersion.Any, [new ThingRenamed("ok"), new UnregisteredThing("x")], EventMetadata.None, Token).ShouldFail();

        Assert.Equal(EventStoreErrors.UnregisteredTypeCode, error.Code);
        Assert.Empty(await Store.ReadStreamAsync(stream, Token));
    }

    private static bool Succeeded(Result<long, Error> result) => result.Match(_ => true, _ => false);

    private static void AssertConflict(Result<long, Error> result)
    {
        var error = result.ShouldFail();
        Assert.Equal(EventStoreErrors.ConcurrencyConflictCode, error.Code);
        Assert.Equal(ErrorKind.Conflict, error.Kind);
    }
}
