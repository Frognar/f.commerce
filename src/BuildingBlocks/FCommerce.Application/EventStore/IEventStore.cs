using FCommerce.SharedKernel.Results;

namespace FCommerce.Application.EventStore;

/// <summary>
/// Port for the event store. Every implementation must pass
/// the shared contract tests in FCommerce.Infrastructure.Tests.
/// Expected failures are returned as <see cref="Error"/> values; exceptions mean infrastructure failure.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Appends events atomically and returns the new stream version. Fails with
    /// <see cref="EventStoreErrors.ConcurrencyConflictCode"/> when the expectation is not met,
    /// <see cref="EventStoreErrors.NoEventsCode"/> for an empty batch and
    /// <see cref="EventStoreErrors.UnregisteredTypeCode"/> for an event type without a registered name.
    /// </summary>
    Task<Result<long, Error>> AppendAsync(
        StreamId streamId,
        ExpectedVersion expectedVersion,
        IReadOnlyList<object> events,
        EventMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>All events of the stream in order; empty when the stream does not exist.</summary>
    Task<IReadOnlyList<RecordedEvent>> ReadStreamAsync(StreamId streamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="maxCount"/> events from the store-wide log with <see cref="RecordedEvent.GlobalPosition"/>
    /// greater than <paramref name="afterPosition"/>, ordered by position. Positions may contain gaps.
    /// </summary>
    Task<IReadOnlyList<RecordedEvent>> ReadAllAsync(long afterPosition, int maxCount, CancellationToken cancellationToken = default);
}

public static class EventStoreErrors
{
    public const string ConcurrencyConflictCode = "event-store.concurrency-conflict";
    public const string NoEventsCode = "event-store.no-events";
    public const string UnregisteredTypeCode = "event-store.unregistered-type";

    public static Error ConcurrencyConflict(StreamId streamId, ExpectedVersion expected) =>
        Error.Conflict(ConcurrencyConflictCode, $"Stream '{streamId}' is not at the expected version ({expected}).");

    public static Error NoEvents(StreamId streamId) =>
        Error.Validation(NoEventsCode, $"Nothing to append to stream '{streamId}'.");

    public static Error UnregisteredType(Type type) =>
        Error.Validation(UnregisteredTypeCode, $"Event type '{type.FullName}' has no registered name.");

    public static bool IsConcurrencyConflict(this Error error) => error.Code == ConcurrencyConflictCode;
}
