namespace FCommerce.Application.EventStore;

/// <summary>Optimistic concurrency expectation for an append. Stream versions start at 1 (first event).</summary>
public abstract record ExpectedVersion
{
    private ExpectedVersion()
    {
    }

    /// <summary>No concurrency check.</summary>
    public static ExpectedVersion Any { get; } = new AnyVersion();

    /// <summary>The stream must not exist yet.</summary>
    public static ExpectedVersion NoStream { get; } = new NoStreamVersion();

    /// <summary>
    /// The stream must currently be at exactly this version. Version 0 is the version of a stream that does
    /// not exist (as returned by <see cref="EventSourcing.LoadAsync{TCommand,TState,TEvent}"/>), so it means <see cref="NoStream"/>.
    /// </summary>
    public static ExpectedVersion Exact(long version) =>
        version > 0 ? new ExactVersion(version) : NoStream;

    public sealed record AnyVersion : ExpectedVersion;

    public sealed record NoStreamVersion : ExpectedVersion;

    public sealed record ExactVersion(long Version) : ExpectedVersion;
}
