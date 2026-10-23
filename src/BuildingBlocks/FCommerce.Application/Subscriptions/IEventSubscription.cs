using FCommerce.Application.EventStore;

namespace FCommerce.Application.Subscriptions;

/// <summary>
/// A named, checkpointed consumer of the store-wide event log: projections into read models and
/// translators from domain events to integration events. Delivery is at-least-once (handlers must be
/// idempotent); events are delivered in global-position order.
/// </summary>
public interface IEventSubscription
{
    /// <summary>Stable name; used as the checkpoint key. Renaming it replays the whole log.</summary>
    string Name { get; }

    /// <summary>Whether the subscription wants this event; others are skipped without being handed over.</summary>
    bool Accepts(RecordedEvent recordedEvent);

    Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken);
}

/// <summary>Persists how far each subscription has processed the global log.</summary>
public interface ICheckpointStore
{
    Task<long> GetAsync(string subscriptionName, CancellationToken cancellationToken);

    Task SaveAsync(string subscriptionName, long position, CancellationToken cancellationToken);
}
