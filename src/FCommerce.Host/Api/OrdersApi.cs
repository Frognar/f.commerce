using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Cart.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Cart;
using FCommerce.Host.Pages.Company.Orders;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace FCommerce.Host.Api;

internal static partial class PublicApiEndpoints
{
    private static void MapOrders(RouteGroupBuilder api)
    {
        var cart = api.MapGroup("/cart").RequireFeature("cart");
        cart.MapGet("", GetCartAsync)
            .WithSummary("The cart of the key's user with current prices and what prevents ordering it. Check canOrder, then GET /checkout.");
        cart.MapPost("/items", AddToCartAsync)
            .WithSummary("Adds products (quantities add up). Unknown SKUs are rejected and reported, the rest is added.");
        cart.MapPut("/items/{sku}", ChangeCartQuantityAsync)
            .WithSummary("Sets the quantity of a product in the cart; zero removes it.");
        cart.MapDelete("/items/{sku}", RemoveFromCartAsync)
            .WithSummary("Removes a product from the cart.");
        cart.MapDelete("", ClearCartAsync)
            .WithSummary("Empties the cart.");

        api.MapGet("/checkout", GetCheckoutAsync)
            .RequireFeature("ordering")
            .WithSummary("The cart as it would be ordered: frozen prices and totals, allowed delivery addresses, the cart version and gross total to send with the order.");

        var orders = api.MapGroup("/orders").RequireFeature("ordering");
        orders.MapPost("", PlaceOrderAsync)
            .WithSummary("Places an order from the cart. Send the cartVersion and expectedGross of GET /checkout and an Idempotency-Key header (a GUID); repeating a request with the same key returns the same order.");
        orders.MapGet("", ListOrdersAsync)
            .WithSummary("The orders the key's user may see (their own, or the company's with the company order scope), newest first.");
        orders.MapGet("/{id:guid}", GetOrderAsync)
            .WithSummary("One order with its lines, status history and ERP documents.");
        orders.MapPost("/{id:guid}/cancellation", RequestCancellationAsync)
            .WithSummary("Asks the seller to cancel an order that is not invoiced (202); the ERP decides, see the order's cancellationRequestedAt and history. Asking again changes nothing.");
    }

    private static async Task<IResult> GetCartAsync(CartAccess carts, IQueryHandler<GetCart, CartView> getCart, SupportedCultures cultures, CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(
            async owner => Results.Ok(await ReadCartAsync(owner, carts, getCart, cultures, cancellationToken)),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> AddToCartAsync(
        [FromBody] ApiCartItems? body,
        CartAccess carts,
        ICommandHandler<AddToCart, Result<CartChange, Error>> add,
        IQueryHandler<GetCart, CartView> getCart,
        SupportedCultures cultures,
        CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(
            owner => ChangedAsync(owner, carts, getCart, cultures, add.HandleAsync(new AddToCart(owner, body?.Items ?? [], CartSources.Api), cancellationToken), cancellationToken),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> ChangeCartQuantityAsync(
        string sku,
        [FromBody] ApiCartQuantity? body,
        CartAccess carts,
        ICommandHandler<ChangeCartQuantity, Result<CartChange, Error>> change,
        IQueryHandler<GetCart, CartView> getCart,
        SupportedCultures cultures,
        CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(
            owner => ChangedAsync(owner, carts, getCart, cultures, change.HandleAsync(new ChangeCartQuantity(owner, Uri.UnescapeDataString(sku), body?.Quantity ?? -1), cancellationToken), cancellationToken),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> RemoveFromCartAsync(
        string sku,
        CartAccess carts,
        ICommandHandler<RemoveFromCart, Result<CartChange, Error>> remove,
        IQueryHandler<GetCart, CartView> getCart,
        SupportedCultures cultures,
        CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(
            owner => ChangedAsync(owner, carts, getCart, cultures, remove.HandleAsync(new RemoveFromCart(owner, Uri.UnescapeDataString(sku)), cancellationToken), cancellationToken),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> ClearCartAsync(
        CartAccess carts,
        ICommandHandler<ClearCart, Result<CartChange, Error>> clear,
        IQueryHandler<GetCart, CartView> getCart,
        SupportedCultures cultures,
        CancellationToken cancellationToken) =>
        await (await carts.OwnerAsync(cancellationToken)).Match(
            owner => ChangedAsync(owner, carts, getCart, cultures, clear.HandleAsync(new ClearCart(owner), cancellationToken), cancellationToken),
            () => Task.FromResult(ApiResults.Forbidden()));

    /// <summary>The outcome of a cart command with the cart as it is now.</summary>
    private static async Task<IResult> ChangedAsync(
        CartOwner owner, CartAccess carts, IQueryHandler<GetCart, CartView> getCart, SupportedCultures cultures, Task<Result<CartChange, Error>> command, CancellationToken cancellationToken) =>
        await (await command).Match(
            async change => Results.Ok(new ApiCartChange(change, await ReadCartAsync(owner, carts, getCart, cultures, cancellationToken))),
            error => Task.FromResult(error.ToProblem()));

    private static async Task<CartView> ReadCartAsync(CartOwner owner, CartAccess carts, IQueryHandler<GetCart, CartView> getCart, SupportedCultures cultures, CancellationToken cancellationToken) =>
        await getCart.HandleAsync(new GetCart(owner, cultures.ForRequest(), (await carts.OrderLimitAsync(cancellationToken)).OrNull()), cancellationToken);

    private static async Task<IResult> GetCheckoutAsync(OrderAccess orders, IQueryHandler<GetCheckout, CheckoutView> getCheckout, SupportedCultures cultures, CancellationToken cancellationToken) =>
        await (await orders.BuyerAsync(cancellationToken)).Match(
            async buyer => Results.Ok(await getCheckout.HandleAsync(new GetCheckout(buyer, cultures.ForRequest()), cancellationToken)),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> PlaceOrderAsync(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ApiPlaceOrder? body,
        HttpContext http,
        OrderAccess orders,
        CartAccess carts,
        ICommandHandler<PlaceOrder, Result<OrderPlacement, Error>> place,
        ICommandHandler<RemoveOrderedItems, Result<CartChange, Error>> removeOrdered,
        IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder,
        SupportedCultures cultures,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var orderId) || orderId == Guid.Empty)
        {
            return ApiResults.BadRequest("api.idempotency-key", "Send a GUID in the Idempotency-Key header; it identifies the order, so repeating the request does not place another.", "Idempotency-Key");
        }

        if (body is null)
        {
            return ApiResults.BadRequest("api.body-missing", "The request body must contain the order.", "body");
        }

        return await (await orders.BuyerAsync(cancellationToken)).Match(
            async buyer =>
            {
                var placed = await place.HandleAsync(
                    new PlaceOrder(orderId, buyer, cultures.ForRequest(), body.CartVersion, body.ExpectedGross, body.CustomerReference, body.Notes, body.RequestedDate, body.AddressCode),
                    cancellationToken);
                return await placed.Match(
                    async order =>
                    {
                        // As the checkout page does: the ordered quantities leave the cart (idempotent per order).
                        await (await carts.OwnerAsync(cancellationToken)).Match(
                            owner => removeOrdered.HandleAsync(new RemoveOrderedItems(owner, order.OrderId, [.. order.Items.Select(i => new CartItemData(i.Sku, i.Quantity))]), cancellationToken),
                            () => Task.FromResult(Result.Ok<CartChange, Error>(new CartChange(0, 0, []))));
                        return (await getOrder.HandleAsync(new GetOrder(order.OrderId), cancellationToken)).Match(
                            view => order.AlreadyPlaced
                                ? Results.Ok(view)
                                : Results.Created($"{Prefix}/orders/{view.Id}", view),
                            error => error.ToProblem());
                    },
                    error => Task.FromResult(error.ToProblem()));
            },
            () => Task.FromResult(ApiResults.Forbidden()));
    }

    private static async Task<IResult> ListOrdersAsync(
        [AsParameters] OrderSearch search,
        OrderAccess orders,
        IQueryHandler<GetOrders, Result<OrderPage, Error>> getOrders,
        CancellationToken cancellationToken)
    {
        if (search.Status is { } status && !OrderStatuses.All.Contains(status))
        {
            return ApiResults.BadRequest("api.invalid-filter", $"status must be one of: {string.Join(", ", OrderStatuses.All)}.", "status");
        }

        return await (await orders.ViewerAsync(cancellationToken)).Match(
            async viewer =>
            {
                var placedBy = viewer.OnlyOwn || search.Mine ? viewer.UserId : Maybe<Guid>.None;
                return (await getOrders.HandleAsync(
                    new GetOrders(viewer.Customer.Code, placedBy.OrNull(), null, search.Status, search.From, search.To, search.Number, Math.Max(search.Page, 1), search.PageSize > 0 ? search.PageSize : 20),
                    cancellationToken)).ToHttpResult(page => Results.Ok(page));
            },
            () => Task.FromResult(ApiResults.Forbidden()));
    }

    private static async Task<IResult> GetOrderAsync(Guid id, OrderAccess orders, IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder, CancellationToken cancellationToken) =>
        await (await orders.ViewerAsync(cancellationToken)).Match(
            async viewer => (await VisibleOrderAsync(viewer, id, orders, getOrder, cancellationToken)).Match(Results.Ok, NotFoundOrder),
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> RequestCancellationAsync(
        Guid id,
        OrderAccess orders,
        IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder,
        ICommandHandler<RequestOrderCancellation, Result<Unit, Error>> requestCancellation,
        CancellationToken cancellationToken) =>
        await (await orders.ViewerAsync(cancellationToken)).Match(
            async viewer => await (await VisibleOrderAsync(viewer, id, orders, getOrder, cancellationToken)).Match(
                async found =>
                {
                    if (!await orders.CanAsync(AccessAction.Manage, viewer, found, cancellationToken))
                    {
                        return ApiResults.Forbidden("This key's user may not cancel this order.");
                    }

                    return await (await requestCancellation.HandleAsync(new RequestOrderCancellation(found.Id, viewer.UserId), cancellationToken)).Match(
                        async _ => (await getOrder.HandleAsync(new GetOrder(id), cancellationToken)).ToHttpResult(view => Results.Accepted(value: view)),
                        error => Task.FromResult(error.ToProblem()));
                },
                () => Task.FromResult(NotFoundOrder())),
            () => Task.FromResult(ApiResults.Forbidden()));

    /// <summary>An order of the viewer's customer that their access policies let them see; others do not exist for them.</summary>
    private static async Task<Maybe<OrderView>> VisibleOrderAsync(
        OrderViewer viewer, Guid id, OrderAccess orders, IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder, CancellationToken cancellationToken)
    {
        var found = (await getOrder.HandleAsync(new GetOrder(id), cancellationToken)).ToMaybe();
        return await found.Match(
            async order => await orders.CanAsync(AccessAction.Read, viewer, order, cancellationToken) ? Maybe.Some(order) : Maybe<OrderView>.None,
            () => Task.FromResult(Maybe<OrderView>.None));
    }

    private static IResult NotFoundOrder() => new Error(ErrorKind.NotFound, "order.not-found", "The order does not exist.").ToProblem();
}

/// <summary>Products to put into the cart.</summary>
[NullableEdge]
public sealed record ApiCartItems(IReadOnlyList<CartItemData> Items);

/// <summary>The new quantity of a product in the cart; zero removes it.</summary>
public sealed record ApiCartQuantity(decimal Quantity);

/// <summary>What a cart command did and the cart afterwards.</summary>
public sealed record ApiCartChange(CartChange Change, CartView Cart);

/// <summary>An order to place from the cart.</summary>
/// <param name="CartVersion">The cartVersion of GET /checkout: a cart changed since is not ordered unseen.</param>
/// <param name="ExpectedGross">The gross total of GET /checkout.</param>
/// <param name="CustomerReference">Your own order number (at most 50 characters).</param>
/// <param name="RequestedDate">The requested delivery date, ISO 8601.</param>
/// <param name="AddressCode">A delivery address code of GET /checkout; none when the customer has no addresses.</param>
[NullableEdge]
public sealed record ApiPlaceOrder(
    long CartVersion,
    decimal ExpectedGross,
    string? CustomerReference,
    string? Notes,
    DateOnly? RequestedDate,
    string? AddressCode);

/// <summary>Query string of the order list.</summary>
/// <param name="Status">"placed", "accepted", "in-progress", "completed", "rejected" or "cancelled".</param>
/// <param name="Number">A platform or ERP order number, or your own reference (exact).</param>
/// <param name="Mine">Only the key's user's orders (with the company order scope; the own scope always shows only those).</param>
/// <param name="PageSize">1–100 (20 when absent).</param>
[NullableEdge]
internal sealed record OrderSearch(
    [FromQuery(Name = "status")] string? Status,
    [FromQuery(Name = "from")] DateOnly? From,
    [FromQuery(Name = "to")] DateOnly? To,
    [FromQuery(Name = "number")] string? Number,
    [FromQuery(Name = "mine")] bool Mine = false,
    [FromQuery(Name = "page")] int Page = 1,
    [FromQuery(Name = "pageSize")] int PageSize = 0);
