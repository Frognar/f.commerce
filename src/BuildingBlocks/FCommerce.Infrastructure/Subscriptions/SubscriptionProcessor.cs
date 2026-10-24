using FCommerce.Application.EventStore;
using FCommerce.Application.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Subscriptions;

/// <summary>Outcome of one processing step.</summary>
/// <param name="Processed">Events consumed (handed over or skipped) and checkpointed.</param>
/// <param name="CaughtUp">True when there was nothing more to do right now, so the caller should wait.</param>
internal sealed record StepResult(int Processed, bool CaughtUp, Maybe<PendingGap> Gap);

/// <summary>
/// Runs one batch of one subscription: read after checkpoint → keep only the gap-free prefix →
/// hand each accepted event over → advance the checkpoint. Each batch runs in its own DI scope.
/// </summary>
internal sealed class SubscriptionProcessor(IServiceScopeFactory scopes, TimeProvider time, SubscriptionOptions options)
{
    public async Task<StepResult> StepAsync(Type subscriptionType, Maybe<PendingGap> gap, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var subscription = (IEventSubscription)provider.GetRequiredService(subscriptionType);
        var store = provider.GetRequiredService<IEventStore>();
        var checkpoints = provider.GetRequiredService<ICheckpointStore>();

        var checkpoint = await checkpoints.GetAsync(subscription.Name, cancellationToken);
        var batch = await store.ReadAllAsync(checkpoint, options.BatchSize, cancellationToken);
        if (batch.Count == 0)
        {
            return new StepResult(0, CaughtUp: true, Gap: Maybe<PendingGap>.None);
        }

        var safe = GapDetection.Evaluate(
            checkpoint, [.. batch.Select(e => e.GlobalPosition)], gap, time.GetUtcNow(), options.GapTimeout);

        if (safe.Count == 0)
        {
            return new StepResult(0, CaughtUp: true, safe.Gap);
        }

        foreach (var recordedEvent in batch.Take(safe.Count))
        {
            if (subscription.Accepts(recordedEvent))
            {
                await subscription.HandleAsync(recordedEvent, cancellationToken);
            }
        }

        await checkpoints.SaveAsync(subscription.Name, batch[safe.Count - 1].GlobalPosition, cancellationToken);

        var caughtUp = safe.Gap.Match(_ => true, () => batch.Count < options.BatchSize);
        return new StepResult(safe.Count, caughtUp, safe.Gap);
    }
}
