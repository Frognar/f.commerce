namespace FCommerce.Application.EventStore;

/// <summary>Context stored with every event, for tracing and audit.</summary>
public sealed record EventMetadata(string? CorrelationId = null, string? CausationId = null, string? Actor = null)
{
    public static EventMetadata None { get; } = new();
}

/// <summary>An event as persisted in the store.</summary>
/// <param name="StreamVersion">Position within its stream, starting at 1.</param>
/// <param name="GlobalPosition">Position in the store-wide log; increasing but may contain gaps.</param>
/// <param name="EventType">Stable registered type name, e.g. "ordering.order-placed.v1".</param>
public sealed record RecordedEvent(
    Guid EventId,
    StreamId StreamId,
    long StreamVersion,
    long GlobalPosition,
    string EventType,
    object Data,
    DateTimeOffset RecordedAt,
    EventMetadata Metadata);
