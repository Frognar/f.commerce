using FCommerce.Application.Subscriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FCommerce.Infrastructure.Subscriptions;

/// <summary>Runs every registered subscription in its own polling loop; a failing subscription does not stop the others.</summary>
internal sealed partial class EventSubscriptionWorker(
    IEnumerable<SubscriptionRegistration> registrations,
    SubscriptionProcessor processor,
    SubscriptionOptions options,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<EventSubscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until startup work (schema creation, migrations) has finished.
        var started = new TaskCompletionSource();
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await Task.WhenAll(registrations.Select(r => RunAsync(r.SubscriptionType, stoppingToken)));
    }

    private async Task RunAsync(Type subscriptionType, CancellationToken stoppingToken)
    {
        PendingGap? gap = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var step = await processor.StepAsync(subscriptionType, gap, stoppingToken);
                if (step.Gap is not null && step.Gap != gap)
                {
                    LogGap(subscriptionType.Name, step.Gap.Position);
                }

                gap = step.Gap;
                if (step.CaughtUp)
                {
                    await Task.Delay(options.PollInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailure(exception, subscriptionType.Name);
                await Task.Delay(options.ErrorBackoff, time, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {Subscription} waits for a gap in the event log at position {Position}")]
    private partial void LogGap(string subscription, long position);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription {Subscription} failed; retrying after back-off")]
    private partial void LogFailure(Exception exception, string subscription);
}
