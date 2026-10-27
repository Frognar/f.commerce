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
    public async Task<Result<(IReadOnlyList<CartItem> Known, IReadOnlyList<CartRejection> Rejected), Error>> KnownAsync(IEnumerable<CartItemData> items, CancellationToken cancellationToken)
    {
        var checkedItems = items
            .Select(item => (Text: $"{item.Sku} {item.Quantity}", Item: Carts.Item(new UnvalidatedCartItem(item.Sku.OrEmpty(), item.Quantity))))
            .ToList();
        var valid = checkedItems.Choose(c => c.Item.ToMaybe()).ToList();
        return (await OfferedAsync(valid.Select(i => i.Sku).Distinct().ToList(), cancellationToken)).Map(offered =>
            ((IReadOnlyList<CartItem>)[.. valid.Where(item => offered.Contains(item.Sku))],
             (IReadOnlyList<CartRejection>)
             [
                 .. checkedItems.Choose(c => c.Item.Match(_ => Maybe<CartRejection>.None, error => Rejection(Maybe<int>.None, c.Text, error))),
                 .. valid.Where(item => !offered.Contains(item.Sku)).Select(item => Unknown(Maybe<int>.None, item.Sku.Value)),
             ]));
    }

    /// <summary>The SKUs the catalog offers; a catalog that cannot answer is an error, not "no such product".</summary>
    public async Task<Result<IReadOnlySet<Sku>, Error>> OfferedAsync(IReadOnlyList<Sku> skus, CancellationToken cancellationToken) =>
        skus.Count == 0
            ? new HashSet<Sku>()
            : (await products.HandleAsync(new GetProductsBySku([options.DefaultCulture], [.. skus.Select(sku => sku.Value)]), cancellationToken))
                .Map(found => (IReadOnlySet<Sku>)found.Choose(p => Carts.Sku(p.Sku).ToMaybe()).ToHashSet());

    /// <summary>The ordered items, read.</summary>
    public static Result<IReadOnlyList<CartItem>, Error> Ordered(IEnumerable<UnvalidatedCartItem> items) =>
        items.Select(Carts.Item).Traverse(item => item);

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
            await (await carts.KnownAsync(command.Items.OrEmpty(), cancellationToken)).BindAsync(read =>
                carts.DecideAsync(command.Owner, new CartCommand.AddItems(read.Known, source), read.Known.Select(i => i.Sku).Distinct().Count(), read.Rejected, cancellationToken)));
}

internal sealed class ReplaceCartHandler(CartStore carts) : ICommandHandler<ReplaceCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(ReplaceCart command, CancellationToken cancellationToken = default) =>
        CartStore.Source(command.Source).BindAsync(async source =>
            await (await carts.KnownAsync(command.Items.OrEmpty(), cancellationToken)).BindAsync(read =>
                carts.DecideAsync(command.Owner, new CartCommand.ReplaceItems(read.Known, source), read.Known.Select(i => i.Sku).Distinct().Count(), read.Rejected, cancellationToken)));
}

/// <summary>Parse the pasted text (pure) → keep the products the catalog offers → add them.</summary>
internal sealed class AddPastedLinesHandler(CartStore carts) : ICommandHandler<AddPastedLines, Result<CartChange, Error>>
{
    public async Task<Result<CartChange, Error>> HandleAsync(AddPastedLines command, CancellationToken cancellationToken = default)
    {
        var pasted = QuickOrder.Parse(command.Text.OrEmpty());
        return await (await carts.OfferedAsync([.. pasted.Items.Select(i => i.Sku)], cancellationToken)).BindAsync(offered =>
        {
            var known = pasted.Items.Where(item => offered.Contains(item.Sku)).ToList();
            IReadOnlyList<CartRejection> rejected =
            [
                .. pasted.Errors.Select(e => CartStore.Rejection(e.Line, e.Text, e.Error)),
                .. pasted.Items.Where(item => !offered.Contains(item.Sku)).Select(item => CartStore.Unknown(Maybe<int>.None, item.Sku.Value)),
            ];
            return carts.DecideAsync(command.Owner, new CartCommand.AddItems(known, CartSource.Paste), known.Count, rejected, cancellationToken);
        });
    }
}

internal sealed class ChangeCartQuantityHandler(CartStore carts) : ICommandHandler<ChangeCartQuantity, Result<CartChange, Error>>
{
    /// <summary>Zero removes the item.</summary>
    public Task<Result<CartChange, Error>> HandleAsync(ChangeCartQuantity command, CancellationToken cancellationToken = default) =>
        (from sku in Carts.Sku(command.Sku.OrEmpty())
         from quantity in command.Quantity == 0 ? Result.Ok<Maybe<CartQuantity>, Error>(Maybe<CartQuantity>.None) : CartQuantity.Parse(command.Quantity).Map(Maybe.Some)
         select (CartCommand)new CartCommand.ChangeQuantity(sku, quantity))
        .BindAsync(change => carts.DecideAsync(command.Owner, change, 0, [], cancellationToken));
}

internal sealed class RemoveFromCartHandler(CartStore carts) : ICommandHandler<RemoveFromCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(RemoveFromCart command, CancellationToken cancellationToken = default) =>
        Carts.Sku(command.Sku.OrEmpty()).BindAsync(sku => carts.DecideAsync(command.Owner, new CartCommand.RemoveItem(sku), 0, [], cancellationToken));
}

internal sealed class ClearCartHandler(CartStore carts) : ICommandHandler<ClearCart, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(ClearCart command, CancellationToken cancellationToken = default) =>
        carts.DecideAsync(command.Owner, new CartCommand.Clear(), 0, [], cancellationToken);
}

internal sealed class RemoveOrderedItemsHandler(CartStore carts) : ICommandHandler<RemoveOrderedItems, Result<CartChange, Error>>
{
    public Task<Result<CartChange, Error>> HandleAsync(RemoveOrderedItems command, CancellationToken cancellationToken = default) =>
        CartStore.Ordered(command.Items.OrEmpty().Select(item => new UnvalidatedCartItem(item.Sku.OrEmpty(), item.Quantity))).BindAsync(items =>
            carts.DecideAsync(command.Owner, new CartCommand.RemoveOrdered(command.OrderId, items), 0, [], cancellationToken));
}

/// <summary>
/// A placed order takes its items out of the buyer's cart for that customer (also when the host already did it right
/// after placing: the cart changes once per order).
/// </summary>
internal sealed class TakeOrderedItemsOut(CartStore carts) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken = default)
    {
        // Ordering published items it validated: an item that does not read is a broken contract.
        var removed = await CartStore.Ordered(integrationEvent.Items.OrEmpty().Select(item => new UnvalidatedCartItem(item.Sku.OrEmpty(), item.Quantity))).BindAsync(items => carts.DecideAsync(
            new CartOwner(integrationEvent.PlacedBy, integrationEvent.CustomerCode),
            new CartCommand.RemoveOrdered(integrationEvent.OrderId, items),
            0,
            [],
            cancellationToken));
        removed.Switch(_ => { }, error => throw new InvalidOperationException($"Taking order {integrationEvent.Number} out of the cart failed: {error.Code}"));
    }
}

internal sealed class GetCartSizeHandler(CartStore carts) : IQueryHandler<GetCartSize, int>
{
    public async Task<int> HandleAsync(GetCartSize query, CancellationToken cancellationToken = default) =>
        (await carts.LoadAsync(query.Owner, cancellationToken)).Items.Count;
}
