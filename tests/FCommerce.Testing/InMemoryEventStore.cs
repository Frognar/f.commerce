using FCommerce.Application.EventStore;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Testing;

/// <summary>
/// Reference implementation of <see cref="IEventStore"/> for fast tests. Passes the same contract tests
/// as the real adapters, so tests using it observe the same semantics.
/// </summary>
public sealed class InMemoryEventStore(Func<Type, Result<string, Error>>? nameOf = null, TimeProvider? time = null) : IEventStore
{
    private readonly Lock gate = new();
    private readonly List<RecordedEvent> log = [];
    private readonly Func<Type, Result<string, Error>> nameOf = nameOf ?? (type => type.Name);
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public Task<Result<long, Error>> AppendAsync(
        StreamId streamId,
        ExpectedVersion expectedVersion,
        IReadOnlyList<object> events,
        EventMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return Task.FromResult(Result.Fail<long, Error>(EventStoreErrors.NoEvents(streamId)));
        }

        var names = events.Traverse(e => nameOf(e.GetType()).MapError(_ => EventStoreErrors.UnregisteredType(e.GetType())));
        return Task.FromResult(names.Bind(typeNames => Append(streamId, expectedVersion, events, typeNames, metadata)));
    }

    private Result<long, Error> Append(StreamId streamId, ExpectedVersion expectedVersion, IReadOnlyList<object> events, IReadOnlyList<string> typeNames, EventMetadata metadata)
    {
        lock (gate)
        {
            var current = log.Count(e => e.StreamId == streamId);
            var matches = expectedVersion switch
            {
                ExpectedVersion.NoStreamVersion => current == 0,
                ExpectedVersion.ExactVersion exact => current == exact.Version,
                _ => true,
            };

            if (!matches)
            {
                return EventStoreErrors.ConcurrencyConflict(streamId, expectedVersion);
            }

            var now = time.GetUtcNow();
            foreach (var (data, typeName) in events.Zip(typeNames))
            {
                current++;
                log.Add(new RecordedEvent(Guid.CreateVersion7(), streamId, current, log.Count + 1, typeName, data, now, metadata));
            }

            return current;
        }
    }

    public Task<IReadOnlyList<RecordedEvent>> ReadStreamAsync(StreamId streamId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<RecordedEvent>>([.. log.Where(e => e.StreamId == streamId)]);
        }
    }

    public Task<IReadOnlyList<RecordedEvent>> ReadAllAsync(long afterPosition, int maxCount, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<RecordedEvent>>([.. log.Where(e => e.GlobalPosition > afterPosition).Take(maxCount)]);
        }
    }
}
