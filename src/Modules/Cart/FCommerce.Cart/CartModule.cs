using FCommerce.Application.Messaging;
using FCommerce.Cart.Contracts;
using FCommerce.Cart.Domain;
using FCommerce.Cart.Persistence;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Cart;

public sealed class CartOptions
{
    /// <summary>Culture of product names where no culture is given (checking that SKUs exist).</summary>
    public string DefaultCulture { get; set; } = "pl";
}

/// <summary>
/// Carts as event streams (one per user and customer). Reads products, prices, availability, customers and settings
/// through their contracts (requires the Catalog, Pricing, Inventory, Customers and Settings modules) and an
/// <see cref="Application.EventStore.IEventStore"/>. Shopping lists are rows in the module's own schema.
/// </summary>
public static class CartModule
{
    public static IServiceCollection AddCartModule(this IServiceCollection services, CartOptions options)
    {
        services.AddSingleton(options);
        services.AddMessageType<CartEvent.ItemsAdded>("cart.items-added.v1");
        services.AddMessageType<CartEvent.ItemQuantityChanged>("cart.item-quantity-changed.v1");
        services.AddMessageType<CartEvent.ItemRemoved>("cart.item-removed.v1");
        services.AddMessageType<CartEvent.Cleared>("cart.cleared.v1");

        services.AddScoped<CartStore>();
        services.AddScoped<ICommandHandler<AddToCart, Result<CartChange, Error>>, AddToCartHandler>();
        services.AddScoped<ICommandHandler<ReplaceCart, Result<CartChange, Error>>, ReplaceCartHandler>();
        services.AddScoped<ICommandHandler<AddPastedLines, Result<CartChange, Error>>, AddPastedLinesHandler>();
        services.AddScoped<ICommandHandler<ChangeCartQuantity, Result<CartChange, Error>>, ChangeCartQuantityHandler>();
        services.AddScoped<ICommandHandler<RemoveFromCart, Result<CartChange, Error>>, RemoveFromCartHandler>();
        services.AddScoped<ICommandHandler<ClearCart, Result<CartChange, Error>>, ClearCartHandler>();
        services.AddScoped<IQueryHandler<GetCartSize, int>, GetCartSizeHandler>();
        services.AddScoped<IQueryHandler<GetCart, CartView>, GetCartHandler>();

        services.AddModuleDbContext<CartDbContext>(CartDbContext.Schema);
        services.AddScoped<ShoppingListStore>();
        services.AddScoped<ICommandHandler<SaveCartAsShoppingList, Result<Guid, Error>>, SaveCartAsShoppingListHandler>();
        services.AddScoped<ICommandHandler<RenameShoppingList, Result<Unit, Error>>, RenameShoppingListHandler>();
        services.AddScoped<ICommandHandler<DeleteShoppingList, Result<Unit, Error>>, DeleteShoppingListHandler>();
        services.AddScoped<ICommandHandler<ChangeShoppingListItem, Result<Unit, Error>>, ChangeShoppingListItemHandler>();
        services.AddScoped<ICommandHandler<ShoppingListToCart, Result<CartChange, Error>>, ShoppingListToCartHandler>();
        services.AddScoped<IQueryHandler<GetShoppingLists, IReadOnlyList<ShoppingListSummary>>, GetShoppingListsHandler>();
        services.AddScoped<IQueryHandler<GetShoppingList, Result<ShoppingListView, Error>>, GetShoppingListHandler>();
        return services;
    }
}
