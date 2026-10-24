using System.Text.Json;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Cart.Contracts;
using FCommerce.Cart.Domain;
using FCommerce.Cart.Persistence;
using FCommerce.Catalog.Contracts;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Cart;

/// <summary>Reading and writing an owner's lists; another owner's list is never found.</summary>
internal sealed class ShoppingListStore(CartDbContext db, TimeProvider time)
{
    public IQueryable<ShoppingListRow> Of(CartOwner owner) =>
        db.ShoppingLists.Where(l => l.UserId == owner.UserId && l.CustomerCode == owner.CustomerCode);

    public async Task<Result<ShoppingListRow, Error>> FindAsync(CartOwner owner, Guid id, CancellationToken cancellationToken) =>
        (await Of(owner).SingleOrNoneAsync(l => l.Id == id, cancellationToken))
            .ToResult(() => Error.NotFound("shopping-list.not-found", "The shopping list does not exist."));

    public Task<List<string>> KeysAsync(CartOwner owner, CancellationToken cancellationToken) =>
        Of(owner).Select(l => l.NameKey).ToListAsync(cancellationToken);

    public static EquatableArray<CartItem> Items(ShoppingListRow row) =>
        JsonSerializer.Deserialize<EquatableArray<CartItem>>(row.Items, PersistenceJson.Options);

    public void Write(ShoppingListRow row, EquatableArray<CartItem> items)
    {
        row.Items = JsonSerializer.Serialize(items, PersistenceJson.Options);
        row.ItemCount = items.Count;
        row.UpdatedAt = time.GetUtcNow();
    }

    public async Task<Result<Unit, Error>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        }
        catch (DbUpdateException)
        {
            // The unique owner + name index: someone saved a list with this name in the meantime.
            return Error.Conflict("shopping-list.name-taken", "A shopping list with this name already exists.");
        }
    }
}

internal sealed class SaveCartAsShoppingListHandler(CartStore carts, ShoppingListStore lists, CartDbContext db, TimeProvider time)
    : ICommandHandler<SaveCartAsShoppingList, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(SaveCartAsShoppingList command, CancellationToken cancellationToken = default)
    {
        var cart = await carts.LoadAsync(command.Owner, cancellationToken);
        var keys = await lists.KeysAsync(command.Owner, cancellationToken);
        return await ShoppingLists.New(command.Name.OrEmpty(), cart.Items, keys).BindAsync(async list =>
        {
            var now = time.GetUtcNow();
            var row = new ShoppingListRow
            {
                Id = Guid.CreateVersion7(),
                UserId = command.Owner.UserId,
                CustomerCode = command.Owner.CustomerCode,
                Name = list.Name.Value,
                NameKey = list.Name.Key,
                Items = "[]",
                CreatedAt = now,
            };
            lists.Write(row, list.Items);
            db.ShoppingLists.Add(row);
            return (await lists.SaveAsync(cancellationToken)).Map(_ => row.Id);
        });
    }
}

internal sealed class RenameShoppingListHandler(ShoppingListStore lists) : ICommandHandler<RenameShoppingList, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RenameShoppingList command, CancellationToken cancellationToken = default)
    {
        var keys = await lists.KeysAsync(command.Owner, cancellationToken);
        return await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken))
            .Bind(row => ShoppingLists.Rename(command.Name.OrEmpty(), Persisted.Rehydrate<ShoppingListName, string>(row.Name), keys).Map(name => (row, name)))
            .BindAsync(async renamed =>
            {
                renamed.row.Name = renamed.name.Value;
                renamed.row.NameKey = renamed.name.Key;
                lists.Write(renamed.row, ShoppingListStore.Items(renamed.row));
                return await lists.SaveAsync(cancellationToken);
            });
    }
}

internal sealed class DeleteShoppingListHandler(ShoppingListStore lists, CartDbContext db) : ICommandHandler<DeleteShoppingList, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(DeleteShoppingList command, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken)).BindAsync(async row =>
        {
            db.ShoppingLists.Remove(row);
            return await lists.SaveAsync(cancellationToken);
        });
}

internal sealed class ChangeShoppingListItemHandler(ShoppingListStore lists) : ICommandHandler<ChangeShoppingListItem, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeShoppingListItem command, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken))
            .Bind(row => ShoppingLists.ChangeQuantity(ShoppingListStore.Items(row), command.Sku.OrEmpty(), command.Quantity).Map(items => (row, items)))
            .BindAsync(async changed =>
            {
                lists.Write(changed.row, changed.items);
                return await lists.SaveAsync(cancellationToken);
            });
}

/// <summary>The list's items go to the cart (added or replacing it); products the catalog no longer offers are reported.</summary>
internal sealed class ShoppingListToCartHandler(ShoppingListStore lists, CartStore carts) : ICommandHandler<ShoppingListToCart, Result<CartChange, Error>>
{
    public async Task<Result<CartChange, Error>> HandleAsync(ShoppingListToCart command, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken)).BindAsync(async row =>
        {
            var (known, rejected) = await carts.KnownAsync(ShoppingListStore.Items(row).Select(i => new CartItemData(i.Sku, i.Quantity)), cancellationToken);
            CartCommand decision = command.Replace
                ? new CartCommand.ReplaceItems(known, CartSource.ShoppingList)
                : new CartCommand.AddItems(known, CartSource.ShoppingList);
            return await carts.DecideAsync(command.Owner, decision, known.Count, rejected, cancellationToken);
        });
}

internal sealed class GetShoppingListsHandler(ShoppingListStore lists) : IQueryHandler<GetShoppingLists, IReadOnlyList<ShoppingListSummary>>
{
    public async Task<IReadOnlyList<ShoppingListSummary>> HandleAsync(GetShoppingLists query, CancellationToken cancellationToken = default) =>
        await lists.Of(query.Owner).AsNoTracking()
            .OrderByDescending(l => l.UpdatedAt)
            .Select(l => new ShoppingListSummary(l.Id, l.Name, l.ItemCount, l.UpdatedAt))
            .ToListAsync(cancellationToken);
}

internal sealed class GetShoppingListHandler(ShoppingListStore lists, IQueryHandler<GetProductsBySku, Result<IReadOnlyList<ProductSummary>, Error>> products)
    : IQueryHandler<GetShoppingList, Result<ShoppingListView, Error>>
{
    public async Task<Result<ShoppingListView, Error>> HandleAsync(GetShoppingList query, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(query.Owner, query.ListId, cancellationToken)).MapAsync(async row =>
        {
            var items = ShoppingListStore.Items(row);
            var offered = (await products.HandleAsync(new GetProductsBySku(query.Cultures.OrEmpty(), [.. items.Select(i => i.Sku)]), cancellationToken))
                .Match(found => found.ToDictionary(p => p.Sku), _ => []);
            return new ShoppingListView(
                row.Id,
                row.Name,
                row.UpdatedAt,
                [.. items.Select(item =>
                {
                    var product = offered.GetValueOrNone(item.Sku);
                    return new ShoppingListItemView(item.Sku, item.Quantity, product.Map(p => p.Name).OrNull(), product.Map(p => p.Unit).OrNull(), product.Bind(p => p.ImageId.ToMaybe()).OrNull());
                })]);
        });
}
