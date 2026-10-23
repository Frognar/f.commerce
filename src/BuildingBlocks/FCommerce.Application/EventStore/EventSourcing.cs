using FCommerce.Application.Results;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Application.EventStore;

/// <summary>Result of running a command through a decider.</summary>
public sealed record Decision<TState, TEvent>(TState State, long Version, EquatableArray<TEvent> NewEvents);

/// <summary>The imperative shell around a pure <see cref="Decider{TCommand,TState,TEvent}"/>.</summary>
public static class EventSourcing
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// Load → decide (pure) → append with optimistic concurrency. On a concurrency conflict the whole
    /// cycle is retried: safe because <c>Decide</c> is a pure function of the reloaded state.
    /// </summary>
    public static Task<Result<Decision<TState, TEvent>, Error>> DecideAsync<TCommand, TState, TEvent>(
        this IEventStore store,
        StreamId streamId,
        Decider<TCommand, TState, TEvent> decider,
        TCommand command,
        DecisionContext context,
        EventMetadata metadata,
        CancellationToken cancellationToken = default)
        where TEvent : notnull =>
        AttemptAsync(store, streamId, decider, command, context, metadata, attempt: 1, cancellationToken);

    /// <summary>Rebuilds the current state of a stream; version 0 means the stream does not exist.</summary>
    public static async Task<(TState State, long Version)> LoadAsync<TCommand, TState, TEvent>(
        this IEventStore store,
        StreamId streamId,
        Decider<TCommand, TState, TEvent> decider,
        CancellationToken cancellationToken = default)
    {
        var history = await store.ReadStreamAsync(streamId, cancellationToken);
        var state = decider.Fold(history.Select(e => (TEvent)e.Data));
        return (state, history.Count == 0 ? 0 : history[^1].StreamVersion);
    }

    private static async Task<Result<Decision<TState, TEvent>, Error>> AttemptAsync<TCommand, TState, TEvent>(
        IEventStore store,
        StreamId streamId,
        Decider<TCommand, TState, TEvent> decider,
        TCommand command,
        DecisionContext context,
        EventMetadata metadata,
        int attempt,
        CancellationToken cancellationToken)
        where TEvent : notnull
    {
        var (state, version) = await store.LoadAsync(streamId, decider, cancellationToken);

        var outcome = await decider.Decide(command, state, context).BindAsync(events => events.IsEmpty
            ? Task.FromResult(Result.Ok<Decision<TState, TEvent>, Error>(new Decision<TState, TEvent>(state, version, events)))
            : store.AppendAsync(streamId, ExpectedVersion.Exact(version), [.. events.Cast<object>()], metadata, cancellationToken)
                .Map(newVersion => new Decision<TState, TEvent>(decider.Fold(state, events), newVersion, events)));

        var retry = outcome.Match(_ => false, error => error.IsConcurrencyConflict() && attempt < MaxAttempts);
        return retry
            ? await AttemptAsync(store, streamId, decider, command, context, metadata, attempt + 1, cancellationToken)
            : outcome;
    }
}
