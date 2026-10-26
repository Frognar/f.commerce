using System.Security.Cryptography;
using System.Text;
using FCommerce.Application.EventStore;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Cart.Domain;
using FCommerce.Catalog.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart;

/// <summary>The event stream category of carts.</summary>
internal sealed class CartStream : IStreamType
{
    public static string Category => "cart";

    /// <summary>
    /// One cart per user and customer, found without a lookup table: the stream id is derived from both
    /// (SHA-256, formatted as a version 8 UUID).
    /// </summary>
    public static StreamId For(CartOwner owner)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"cart:{owner.UserId:N}:{owner.CustomerCode}"));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return StreamId.For<CartStream>(new Guid(bytes, bigEndian: true));
    }
}

/// <summary>Loads and changes carts; checks SKUs against the catalog before the decider sees them.</summary>
internal sealed class CartStore(
    IEventStore store,
    TimeProvider time,
    CartOptions options,
    IQueryHandler<GetProductsBySku, Result<IReadOnlyList<ProductSummary>, Error>> products)
{
    public async Task<CartState> LoadAsync(CartOwner owner, CancellationToken cancellationToken) =>
        (await LoadWithVersionAsync(owner, cancellationToken)).State;

    public Task<(CartState State, long Version)> LoadWithVersionAsync(CartOwner owner, CancellationToken cancellationToken) =>
        store.LoadAsync(CartStream.For(owner), Carts.Decider, cancellationToken);

    public async Task<Result<CartChange, Error>> DecideAsync(CartOwner owner, CartCommand command, int added, IReadOnlyList<CartRejection> rejected, CancellationToken cancellationToken) =>
        (await store.DecideAsync(
            CartStream.For(owner),
            Carts.Decider,
            command,
            new DecisionContext(time.GetUtcNow()),
            new EventMetadata(Actor: owner.UserId.ToString("N")),
            cancellationToken))
        .Map(decision => new CartChange(decision.State.Items.Count, added, rejected));

    /// <summary>Valid items of products the catalog offers; the others as rejections.</summary>
    public async Task<(IReadOnlyList<CartItem> Known, IReadOnlyList<CartRejection> Rejected)> KnownAsync(IEnumerable<CartItemData> items, CancellationToken cancellationToken)
    {
        var checkedItems = items
            .Select(item => (Text: $"{item.Sku} {item.Quantity}", Item: from sku in Carts.Sku(item.Sku.OrEmpty()) from quantity in Carts.Quantity(item.Quantity) select new CartItem(sku, quantity)))
            .ToList();
        var valid = checkedItems.Choose(c => c.Item.ToMaybe()).ToList();
        var offered = await OfferedAsync(valid.Select(i => i.Sku).Distinct().ToList(), cancellationToken);
        return (
            [.. valid.Where(item => offered.Contains(item.Sku))],
            [
                .. checkedItems.Choose(c => c.Item.Match(_ => Maybe<CartRejection>.None, error => Rejection(Maybe<int>.None, c.Text, error))),
                .. valid.Where(item => !offered.Contains(item.Sku)).Select(item => Unknown(Maybe<int>.None, item.Sku)),
            ]);
    }

    public async Task<IReadOnlySet<string>> OfferedAsync(IReadOnlyList<string> skus, CancellationToken cancellationToken) =>
        skus.Count == 0
            ? new HashSet<string>()
            : (await products.HandleAsync(new GetProductsBySku([options.DefaultCulture], skus), cancellationToken))
                .Match(found => found.Select(p => p.Sku).ToHashSet(), _ => []);

    public static CartRejection Rejection(Maybe<int> line, string text, Error error) =>
        new(line.OrNull(), text, error.Details.IsEmpty ? error.Code : error.Details[0].Code, error.Details.IsEmpty ? error.Message : error.Details[0].Message);

    public static CartRejection Unknown(Maybe<int> line, string text) =>
        new(line.OrNull(), text, "product.unknown", "The catalog has no such product.");

    public static Result<CartSource, Error> Source(string raw) =>
        raw.OrEmpty() switch
        {
            CartSources.Product => CartSource.Product,
            CartSources.Catalog => CartSource.Catalog,
            CartSources.QuickOrder => CartSource.QuickOrder,
            CartSources.Paste => CartSource.Paste,
            CartSources.ShoppingList => CartSource.ShoppingList,
            CartSources.Document => CartSource.Document,
            CartSources.Order => CartSource.Order,
            CartSources.Api => CartSource.Api,
            var other => Error.Validation("cart.source", $"Unknown cart source '{other}'."),
        };
}

internal sealed class AddToCartHandler(CartStore carts) : ICommandHandler<AddToCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(AddToCart command, CancellationToken cancellationToken = default) =>
        CartStore.Source(command.Source).BindAsync(async source =>
        {
            var (known, rejected) = await carts.KnownAsync(command.Items.OrEmpty(), cancellationToken);
            return await carts.DecideAsync(command.Owner, new CartCommand.AddItems(known, source), known.Select(i => i.Sku).Distinct().Count(), rejected, cancellationToken);
        });
}

internal sealed class ReplaceCartHandler(CartStore carts) : ICommandHandler<ReplaceCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(ReplaceCart command, CancellationToken cancellationToken = default) =>
        CartStore.Source(command.Source).BindAsync(async source =>
        {
            var (known, rejected) = await carts.KnownAsync(command.Items.OrEmpty(), cancellationToken);
            return await carts.DecideAsync(command.Owner, new CartCommand.ReplaceItems(known, source), known.Select(i => i.Sku).Distinct().Count(), rejected, cancellationToken);
        });
}

/// <summary>Parse the pasted text (pure) → keep the products the catalog offers → add them.</summary>
internal sealed class AddPastedLinesHandler(CartStore carts) : ICommandHandler<AddPastedLines, Result<CartChange, Error>>
{
    public async Task<Result<CartChange, Error>> HandleAsync(AddPastedLines command, CancellationToken cancellationToken = default)
    {
        var pasted = QuickOrder.Parse(command.Text.OrEmpty());
        var offered = await carts.OfferedAsync([.. pasted.Items.Select(i => i.Sku)], cancellationToken);
        var known = pasted.Items.Where(item => offered.Contains(item.Sku)).ToList();
        IReadOnlyList<CartRejection> rejected =
        [
            .. pasted.Errors.Select(e => CartStore.Rejection(e.Line, e.Text, e.Error)),
            .. pasted.Items.Where(item => !offered.Contains(item.Sku)).Select(item => CartStore.Unknown(Maybe<int>.None, item.Sku)),
        ];
        return await carts.DecideAsync(command.Owner, new CartCommand.AddItems(known, CartSource.Paste), known.Count, rejected, cancellationToken);
    }
}

internal sealed class ChangeCartQuantityHandler(CartStore carts) : ICommandHandler<ChangeCartQuantity, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(ChangeCartQuantity command, CancellationToken cancellationToken = default) =>
        carts.DecideAsync(command.Owner, new CartCommand.ChangeQuantity(command.Sku.OrEmpty(), command.Quantity), 0, [], cancellationToken);
}

internal sealed class RemoveFromCartHandler(CartStore carts) : ICommandHandler<RemoveFromCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(RemoveFromCart command, CancellationToken cancellationToken = default) =>
        carts.DecideAsync(command.Owner, new CartCommand.RemoveItem(command.Sku.OrEmpty()), 0, [], cancellationToken);
}

internal sealed class ClearCartHandler(CartStore carts) : ICommandHandler<ClearCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(ClearCart command, CancellationToken cancellationToken = default) =>
        carts.DecideAsync(command.Owner, new CartCommand.Clear(), 0, [], cancellationToken);
}

internal sealed class RemoveOrderedItemsHandler(CartStore carts) : ICommandHandler<RemoveOrderedItems, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(RemoveOrderedItems command, CancellationToken cancellationToken = default) =>
        carts.DecideAsync(
            command.Owner,
            new CartCommand.RemoveOrdered(command.OrderId, [.. command.Items.OrEmpty().Select(item => new CartItem(item.Sku.OrEmpty(), item.Quantity))]),
            0,
            [],
            cancellationToken);
}

/// <summary>
/// A placed order takes its items out of the buyer's cart for that customer (also when the host already did it right
/// after placing: the cart changes once per order).
/// </summary>
internal sealed class TakeOrderedItemsOut(CartStore carts) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken = default)
    {
        var removed = await carts.DecideAsync(
            new CartOwner(integrationEvent.PlacedBy, integrationEvent.CustomerCode),
            new CartCommand.RemoveOrdered(integrationEvent.OrderId, [.. integrationEvent.Items.OrEmpty().Select(item => new CartItem(item.Sku.OrEmpty(), item.Quantity))]),
            0,
            [],
            cancellationToken);
        removed.Switch(_ => { }, error => throw new InvalidOperationException($"Taking order {integrationEvent.Number} out of the cart failed: {error.Code}"));
    }
}

internal sealed class GetCartSizeHandler(CartStore carts) : IQueryHandler<GetCartSize, int>
{
    public async Task<int> HandleAsync(GetCartSize query, CancellationToken cancellationToken = default) =>
        (await carts.LoadAsync(query.Owner, cancellationToken)).Items.Count;
}
