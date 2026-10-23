using System.Collections.Concurrent;
using FCommerce.Application.Messaging;
using FCommerce.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Infrastructure.Messaging;

/// <summary>
/// Invokes every <see cref="IIntegrationEventHandler{TEvent}"/> registered for the event's type, in order,
/// within the caller's DI scope. A failing handler propagates, so the caller (outbox / subscription) retries.
/// </summary>
internal sealed class InProcessIntegrationEventPublisher(IServiceProvider services) : IIntegrationEventPublisher
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task>> Dispatchers = new();

    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        var dispatch = Dispatchers.GetOrAdd(integrationEvent.GetType(), CreateDispatcher);
        return dispatch(services, integrationEvent, cancellationToken);
    }

    private static Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task> CreateDispatcher(Type eventType) =>
        typeof(InProcessIntegrationEventPublisher)
            .GetMethod(nameof(DispatchAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(eventType)
            .CreateDelegate<Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task>>();

    private static async Task DispatchAsync<TEvent>(IServiceProvider services, IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        foreach (var handler in services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync((TEvent)integrationEvent, cancellationToken);
        }
    }
}
