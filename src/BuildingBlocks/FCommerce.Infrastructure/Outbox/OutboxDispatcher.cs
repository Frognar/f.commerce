using System.Text.Json;
using FCommerce.Application.Messaging;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FCommerce.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int BatchSize { get; set; } = 100;

    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Published messages are deleted after this time (they may carry links with one-time tokens).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Publishes pending outbox messages of one module in insertion order, at least once. A message whose
/// handlers fail stays pending (with the error recorded) and blocks later ones, preserving order.
/// Assumes a single running instance per database.
/// </summary>
internal sealed partial class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopes,
    OutboxOptions options,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    /// <summary>Publishes one batch; returns how many messages were published.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var registry = scope.ServiceProvider.GetRequiredService<MessageTypeRegistry>();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in pending)
        {
            try
            {
                var integrationEvent = registry.TypeOf(message.Type).Match(
                    type => (IIntegrationEvent)JsonSerializer.Deserialize(message.Payload, type, PersistenceJson.Options)!,
                    error => throw new InvalidOperationException(error.Message));
                await publisher.PublishAsync(integrationEvent, cancellationToken);
                message.ProcessedAt = time.GetUtcNow();
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
                LogFailure(exception, message.Id, message.Type);
                break;
            }
            finally
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return published;
    }

    /// <summary>Deletes published messages older than <see cref="OutboxOptions.Retention"/>; returns how many.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cutoff = time.GetUtcNow() - options.Retention;
        return await scope.ServiceProvider.GetRequiredService<TContext>().Set<OutboxMessage>()
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForStartAsync(stoppingToken))
        {
            return;
        }

        var nextCleanup = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (time.GetUtcNow() >= nextCleanup)
                {
                    await DeleteExpiredAsync(stoppingToken);
                    nextCleanup = time.GetUtcNow() + options.CleanupInterval;
                }

                if (await DispatchBatchAsync(stoppingToken) < options.BatchSize)
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
                LogFailure(exception, 0, typeof(TContext).Name);
                await Task.Delay(options.ErrorBackoff, time, stoppingToken);
            }
        }
    }

    private async Task<bool> WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({MessageType}) could not be published; retrying later")]
    private partial void LogFailure(Exception exception, long messageId, string messageType);
}

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Enables the outbox for a module DbContext (whose model must call <see cref="OutboxModel.AddOutbox"/>).</summary>
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<Outbox<TContext>>();
        services.AddSingleton<OutboxDispatcher<TContext>>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher<TContext>>());
        services.TryAddOutboxOptions();
        return services;
    }

    private static void TryAddOutboxOptions(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(OutboxOptions)))
        {
            services.AddSingleton(new OutboxOptions());
        }
    }
}
