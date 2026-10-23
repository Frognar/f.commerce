using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FCommerce.Catalog.Search;

/// <summary>The category tree changed, so products' category paths may have changed (module-internal).</summary>
internal sealed record CategoriesChanged : IIntegrationEvent;

internal sealed class IndexChangedProduct(ProductSearchIndex index) : IIntegrationEventHandler<ProductChanged>
{
    public Task HandleAsync(ProductChanged integrationEvent, CancellationToken cancellationToken = default) =>
        index.IndexAsync([integrationEvent.Sku], cancellationToken);
}

internal sealed class ReindexOnCategoriesChanged(ProductSearchIndex index) : IIntegrationEventHandler<CategoriesChanged>
{
    public Task HandleAsync(CategoriesChanged integrationEvent, CancellationToken cancellationToken = default) =>
        index.ReindexAllAsync(cancellationToken);
}

/// <summary>On startup: applies index settings and rebuilds the index when it is empty (new engine, lost volume).</summary>
internal sealed partial class SearchIndexInitializer(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<SearchIndexInitializer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var index = scope.ServiceProvider.GetRequiredService<ProductSearchIndex>();
                await index.ConfigureAsync(stoppingToken);
                if (await index.IsEmptyAsync(stoppingToken))
                {
                    await index.ReindexAllAsync(stoppingToken);
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                await Task.Delay(RetryDelay, time, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Search index initialization failed; retrying")]
    private partial void LogFailure(Exception exception);
}
