using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Deciders;

/// <summary>Inputs from the outside world that a decision may depend on (passed in, never read ambiently).</summary>
public sealed record DecisionContext(DateTimeOffset Now);

/// <summary>
/// An event-sourced aggregate as three pure functions:
/// <c>Decide</c> turns a command and the current state into new events (or an error),
/// <c>Evolve</c> applies one event to a state, <c>Initial</c> is the state of an empty stream.
/// </summary>
public sealed record Decider<TCommand, TState, TEvent>(
    Func<TCommand, TState, DecisionContext, Result<EquatableArray<TEvent>, Error>> Decide,
    Func<TState, TEvent, TState> Evolve,
    TState Initial)
{
    public TState Fold(IEnumerable<TEvent> events) => events.Aggregate(Initial, Evolve);

    public TState Fold(TState state, IEnumerable<TEvent> events) => events.Aggregate(state, Evolve);
}
