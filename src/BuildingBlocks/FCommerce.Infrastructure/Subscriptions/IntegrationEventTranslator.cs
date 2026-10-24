using FCommerce.Application.EventStore;
using FCommerce.Application.Messaging;
using FCommerce.Application.Subscriptions;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Subscriptions;

/// <summary>
/// Turns a module's domain events into its public integration events. The event store is the outbox:
/// an integration event is published after the domain event is durably stored, at least once.
/// </summary>
public abstract class IntegrationEventTranslator(IIntegrationEventPublisher publisher) : IEventSubscription
{
    public abstract string Name { get; }

    /// <summary>Pure mapping; none when the domain event has no public counterpart.</summary>
    protected abstract Maybe<IIntegrationEvent> Translate(RecordedEvent recordedEvent);

    public abstract bool Accepts(RecordedEvent recordedEvent);

    public async Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken)
    {
        foreach (var integrationEvent in Translate(recordedEvent).AsEnumerable())
        {
            await publisher.PublishAsync(integrationEvent, cancellationToken);
        }
    }
}
