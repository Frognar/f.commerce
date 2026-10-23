using FCommerce.Application.Messaging;
using FCommerce.Application.Subscriptions;
using FCommerce.Infrastructure.Messaging;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Infrastructure.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FCommerce.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Database-independent infrastructure. A database adapter (e.g. AddPostgres) must also be registered.</summary>
    public static IServiceCollection AddFCommerceInfrastructure(this IServiceCollection services, Action<SubscriptionOptions>? configureSubscriptions = null)
    {
        var subscriptionOptions = new SubscriptionOptions();
        configureSubscriptions?.Invoke(subscriptionOptions);

        services.TryAddSingleton(TimeProvider.System);
        // Invalid registrations are a startup configuration error: fail fast at the composition root.
        services.TryAddSingleton<MessageTypeRegistry>(sp => MessageTypeRegistry.Create(sp.GetServices<MessageTypeRegistration>())
            .Match(registry => registry, error => throw new InvalidOperationException(error.Message)));
        services.TryAddScoped<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
        services.TryAddScoped<ICheckpointStore, EfCheckpointStore>();
        services.TryAddSingleton(subscriptionOptions);
        services.TryAddSingleton<SubscriptionProcessor>();
        services.AddHostedService<EventSubscriptionWorker>();
        services.AddModuleDbContext<MessagingDbContext>(MessagingDbContext.Schema);
        return services;
    }

    /// <summary>Blob storage on the local file system (single-instance deployments, development).</summary>
    public static IServiceCollection AddFileSystemBlobStore(this IServiceCollection services, string rootPath) =>
        services.AddSingleton<Application.Storage.IBlobStore>(new Storage.FileSystemBlobStore(rootPath));

    /// <summary>Registers a persisted name for an event or message type.</summary>
    public static IServiceCollection AddMessageType<T>(this IServiceCollection services, string name) =>
        services.AddSingleton(new MessageTypeRegistration(typeof(T), name));

    /// <summary>Runs the subscription in the background, from its last checkpoint.</summary>
    public static IServiceCollection AddEventSubscription<TSubscription>(this IServiceCollection services)
        where TSubscription : class, IEventSubscription
    {
        services.TryAddScoped<TSubscription>();
        services.AddSingleton(new SubscriptionRegistration(typeof(TSubscription)));
        return services;
    }

    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : SharedKernel.Messaging.IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent> =>
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
}
