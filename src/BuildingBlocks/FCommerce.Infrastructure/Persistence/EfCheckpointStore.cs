using FCommerce.Application.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Infrastructure.Persistence;

internal sealed class EfCheckpointStore(MessagingDbContext db, TimeProvider time) : ICheckpointStore
{
    public async Task<long> GetAsync(string subscriptionName, CancellationToken cancellationToken) =>
        await db.Checkpoints.AsNoTracking()
            .Where(c => c.SubscriptionName == subscriptionName)
            .Select(c => (long?)c.Position)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;

    public async Task SaveAsync(string subscriptionName, long position, CancellationToken cancellationToken)
    {
        var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(c => c.SubscriptionName == subscriptionName, cancellationToken);
        if (checkpoint is null)
        {
            db.Checkpoints.Add(new SubscriptionCheckpoint { SubscriptionName = subscriptionName, Position = position, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            checkpoint.Position = position;
            checkpoint.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
