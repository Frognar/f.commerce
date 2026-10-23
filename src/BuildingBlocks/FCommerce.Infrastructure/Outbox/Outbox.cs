using System.Text.Json;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Infrastructure.Outbox;

/// <summary>
/// Transactional outbox of one module's DbContext: <see cref="Enqueue"/> adds the event to the context, so it is
/// stored by the same SaveChanges as the state change. <see cref="OutboxDispatcher{TContext}"/> publishes it later.
/// </summary>
public sealed class Outbox<TContext>(TContext db, MessageTypeRegistry registry, TimeProvider time)
    where TContext : DbContext
{
    public Result<Unit, Error> Enqueue(IIntegrationEvent integrationEvent) =>
        registry.NameOf(integrationEvent.GetType()).Map(type =>
        {
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Type = type,
                Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PersistenceJson.Options),
                OccurredAt = time.GetUtcNow(),
            });
            return Unit.Value;
        });

    public Result<Unit, Error> EnqueueAll(IEnumerable<IIntegrationEvent> integrationEvents) =>
        integrationEvents.Traverse(Enqueue).Map(_ => Unit.Value);
}
