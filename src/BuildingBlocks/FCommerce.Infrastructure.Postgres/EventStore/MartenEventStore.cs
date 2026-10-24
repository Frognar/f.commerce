using FCommerce.Application.EventStore;
using FCommerce.Application.Results;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Results;
using JasperFx;
using JasperFx.Events;
using Marten;
using IEventStore = FCommerce.Application.EventStore.IEventStore;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Postgres.EventStore;

/// <summary>
/// <see cref="IEventStore"/> on Marten. Only Marten's raw event storage is used — no aggregates, projections
/// or daemon — so the rest of the system never depends on Marten and the adapter stays replaceable.
/// </summary>
internal sealed class MartenEventStore(IDocumentStore store, MessageTypeRegistry registry) : IEventStore
{
    internal const string ActorHeader = "actor";

    public Task<Result<long, Error>> AppendAsync(
        StreamId streamId,
        ExpectedVersion expectedVersion,
        IReadOnlyList<object> events,
        EventMetadata metadata,
        CancellationToken cancellationToken = default) =>
        events.Count == 0
            ? Task.FromResult(Result.Fail<long, Error>(EventStoreErrors.NoEvents(streamId)))
            : events
                .Traverse(e => registry.NameOf(e.GetType()).MapError(_ => EventStoreErrors.UnregisteredType(e.GetType())))
                .BindAsync(_ => SaveAsync(streamId, expectedVersion, events, metadata, cancellationToken));

    private async Task<Result<long, Error>> SaveAsync(
        StreamId streamId,
        ExpectedVersion expectedVersion,
        IReadOnlyList<object> events,
        EventMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();
        session.CorrelationId = metadata.CorrelationId.OrNull();
        session.CausationId = metadata.CausationId.OrNull();
        metadata.Actor.Switch(actor => session.SetHeader(ActorHeader, actor), () => { });

        var key = streamId.Value;
        var action = expectedVersion switch
        {
            ExpectedVersion.NoStreamVersion => session.Events.StartStream(key, events),
            // Marten expects the version the stream will have *after* the append.
            ExpectedVersion.ExactVersion exact => session.Events.Append(key, exact.Version + events.Count, events),
            _ => session.Events.Append(key, events),
        };

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsConcurrencyFailure(exception))
        {
            return EventStoreErrors.ConcurrencyConflict(streamId, expectedVersion);
        }

        return action.Version > 0 ? action.Version : action.Events[^1].Version;
    }

    public async Task<IReadOnlyList<RecordedEvent>> ReadStreamAsync(StreamId streamId, CancellationToken cancellationToken = default)
    {
        await using var session = store.QuerySession();
        var events = await session.Events.FetchStreamAsync(streamId.Value, token: cancellationToken);
        return [.. events.Select(ToRecordedEvent)];
    }

    public async Task<IReadOnlyList<RecordedEvent>> ReadAllAsync(long afterPosition, int maxCount, CancellationToken cancellationToken = default)
    {
        await using var session = store.QuerySession();
        var events = await session.Events.QueryAllRawEvents()
            .Where(e => e.Sequence > afterPosition)
            .OrderBy(e => e.Sequence)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
        return [.. events.Select(ToRecordedEvent)];
    }

    private static RecordedEvent ToRecordedEvent(IEvent e)
    {
        // Marten's own bookkeeping (e.g. tombstones filling failed appends) keeps its position in the log.
        var key = e.StreamKey.OrEmpty();
        var streamId = e.Data is Tombstone
            ? StreamId.Foreign(key)
            : StreamId.Parse(key).ValueOr(_ => StreamId.Foreign(key));
        return new RecordedEvent(
            e.Id,
            streamId,
            e.Version,
            e.Sequence,
            e.EventTypeName,
            e.Data,
            e.Timestamp,
            new EventMetadata(e.CorrelationId.ToMaybe(), e.CausationId.ToMaybe(), e.GetHeader(ActorHeader).ToMaybe().Map(actor => actor.ToString() ?? string.Empty)));
    }

    private static bool IsConcurrencyFailure(Exception exception) =>
        exception is ConcurrencyException
            or EventStreamUnexpectedMaxEventIdException
            or ExistingStreamIdCollisionException
            or Marten.Exceptions.ExistingStreamIdCollisionException;
}
