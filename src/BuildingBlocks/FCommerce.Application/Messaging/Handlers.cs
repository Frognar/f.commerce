using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Application.Messaging;

/// <summary>Handles a command from a module's public contract. Adapters (pages, API) depend on this, never on the module internals.</summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Handles a read-only query from a module's public contract.</summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reacts to an integration event published by another module. Delivery is at-least-once,
/// so handlers must be idempotent.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>Delivers an integration event to all its handlers in the process.</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
