using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Ordering.Contracts;
using FCommerce.Ordering.Domain;
using FCommerce.Ordering.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Ordering;

/// <summary>
/// Orders as event streams (one per order), placed from the cart. Reads the cart, customers and users through their
/// contracts (requires the Cart, Customers and Access modules) and an <see cref="Application.EventStore.IEventStore"/>.
/// The order list, the ERP feed and the number counters are rows in the module's own schema, kept by subscriptions.
/// </summary>
public static class OrderingModule
{
    public static IServiceCollection AddOrderingModule(this IServiceCollection services, OrderingOptions options)
    {
        var prefix = OrderNumbers.Prefix(options.NumberPrefix).Match(p => p, error => throw new InvalidOperationException($"Ordering:NumberPrefix: {error.Message}"));
        services.AddSingleton(new OrderingOptions { NumberPrefix = prefix, TimeZone = options.TimeZone });
        services.AddSingleton<OrderingClock>();

        services.AddMessageType<OrderEvent.OrderPlaced>("ordering.order-placed.v1");
        services.AddMessageType<OrderEvent.OrderAccepted>("ordering.order-accepted.v1");
        services.AddMessageType<OrderEvent.OrderRejected>("ordering.order-rejected.v1");
        services.AddMessageType<OrderEvent.OrderCancelled>("ordering.order-cancelled.v1");
        services.AddMessageType<OrderEvent.DocumentLinked>("ordering.document-linked.v1");

        services.AddModuleDbContext<OrderingDbContext>(OrderingDbContext.Schema);
        services.AddScoped<OrderStore>();
        services.AddScoped<OrderNumberCounter>();
        services.AddScoped<CheckoutReader>();

        services.AddScoped<IQueryHandler<GetCheckout, CheckoutView>, GetCheckoutHandler>();
        services.AddScoped<ICommandHandler<PlaceOrder, Result<OrderPlacement, Error>>, PlaceOrderHandler>();
        services.AddScoped<ICommandHandler<CancelOrder, Result<Unit, Error>>, CancelOrderHandler>();
        services.AddScoped<ICommandHandler<AcceptOrder, Result<Unit, Error>>, AcceptOrderHandler>();
        services.AddScoped<ICommandHandler<RejectOrder, Result<Unit, Error>>, RejectOrderHandler>();
        services.AddScoped<ICommandHandler<CancelOrderBySeller, Result<Unit, Error>>, CancelOrderBySellerHandler>();
        services.AddScoped<IQueryHandler<GetOrder, Result<OrderView, Error>>, GetOrderHandler>();
        services.AddScoped<IQueryHandler<GetOrders, Result<OrderPage, Error>>, GetOrdersHandler>();
        services.AddScoped<IQueryHandler<GetOrderFeed, OrderFeedPage>, GetOrderFeedHandler>();

        services.AddEventSubscription<OrderListProjection>();
        services.AddEventSubscription<OrderIntegrationEvents>();
        services.AddIntegrationEventHandler<DocumentChanged, LinkDocumentToOrders>();
        services.AddIntegrationEventHandler<CustomerChanged, FollowCustomerSalesperson>();
        return services;
    }
}
