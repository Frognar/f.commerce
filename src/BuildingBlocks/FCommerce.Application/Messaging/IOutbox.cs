using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Application.Messaging;

/// <summary>
/// Transactional outbox for state-based modules: events enqueued here are stored in the same
/// transaction as the module's state change and published afterwards by a background dispatcher.
/// Event-sourced modules do not need it: the event store itself acts as their outbox.
/// </summary>
public interface IOutbox
{
    void Enqueue(IIntegrationEvent integrationEvent);
}
