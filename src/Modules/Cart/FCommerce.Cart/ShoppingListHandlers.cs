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
    public DateTimeOffset Now => time.GetUtcNow();

    public IQueryable<ShoppingListRow> Of(CartOwner owner) =>
        db.ShoppingLists.Where(l => l.UserId == owner.UserId && l.CustomerCode == owner.CustomerCode);

    public async Task<Result<ShoppingListRow, Error>> FindAsync(CartOwner owner, Guid id, CancellationToken cancellationToken) =>
        (await Of(owner).SingleOrNoneAsync(l => l.Id == id, cancellationToken))
            .ToResult(() => Error.NotFound("shopping-list.not-found", "The shopping list does not exist."));

    public Task<List<string>> KeysAsync(CartOwner owner, CancellationToken cancellationToken) =>
        Of(owner).Select(l => l.NameKey).ToListAsync(cancellationToken);

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

internal sealed class SaveCartAsShoppingListHandler(CartStore carts, ShoppingListStore lists, CartDbContext db)
    : ICommandHandler<SaveCartAsShoppingList, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(SaveCartAsShoppingList command, CancellationToken cancellationToken = default)
    {
        var cart = await carts.LoadAsync(command.Owner, cancellationToken);
        var keys = await lists.KeysAsync(command.Owner, cancellationToken);
        return await ShoppingLists.New(command.Name.OrEmpty(), cart.Items, keys).BindAsync(async list =>
        {
            var row = ShoppingListRow.From(Guid.CreateVersion7(), command.Owner, list, lists.Now);
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
            .Bind(row => ShoppingLists.Rename(row.ToDomain(), command.Name.OrEmpty(), keys).Map(renamed => (row, renamed)))
            .BindAsync(async changed =>
            {
                changed.row.Apply(changed.renamed, lists.Now);
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

/// <summary>Zero removes the item (the last one cannot be removed).</summary>
internal sealed class ChangeShoppingListItemHandler(ShoppingListStore lists) : ICommandHandler<ChangeShoppingListItem, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeShoppingListItem command, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken))
            .Bind(row =>
                from sku in Carts.Sku(command.Sku.OrEmpty())
                from quantity in command.Quantity == 0 ? Result.Ok<Maybe<CartQuantity>, Error>(Maybe<CartQuantity>.None) : CartQuantity.Parse(command.Quantity).Map(Maybe.Some)
                from changed in ShoppingLists.ChangeQuantity(row.ToDomain(), sku, quantity)
                select (row, changed))
            .BindAsync(async change =>
            {
                change.row.Apply(change.changed, lists.Now);
                return await lists.SaveAsync(cancellationToken);
            });
}

/// <summary>The list's items go to the cart (added or replacing it); products the catalog no longer offers are reported.</summary>
internal sealed class ShoppingListToCartHandler(ShoppingListStore lists, CartStore carts) : ICommandHandler<ShoppingListToCart, Result<CartChange, Error>>
{
    public async Task<Result<CartChange, Error>> HandleAsync(ShoppingListToCart command, CancellationToken cancellationToken = default) =>
        await (await lists.FindAsync(command.Owner, command.ListId, cancellationToken)).BindAsync(async row =>
            await (await carts.KnownAsync(row.ToDomain().Items.Select(i => new CartItemData(i.Sku.Value, i.Quantity.Value)), cancellationToken)).BindAsync(read =>
            {
                CartCommand decision = command.Replace
                    ? new CartCommand.ReplaceItems(read.Known, CartSource.ShoppingList)
                    : new CartCommand.AddItems(read.Known, CartSource.ShoppingList);
                return carts.DecideAsync(command.Owner, decision, read.Known.Count, read.Rejected, cancellationToken);
            }));
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
            var list = row.ToDomain();
            var offered = (await products.HandleAsync(new GetProductsBySku(query.Cultures.OrEmpty(), [.. list.Items.Select(i => i.Sku.Value)]), cancellationToken))
                .Match(found => found.ToDictionary(p => p.Sku), _ => []);
            return new ShoppingListView(
                row.Id,
                list.Name.Value,
                row.UpdatedAt,
                [.. list.Items.Select(item =>
                {
                    var product = offered.GetValueOrNone(item.Sku.Value);
                    return new ShoppingListItemView(item.Sku.Value, item.Quantity.Value, product.Map(p => p.Name).OrNull(), product.Map(p => p.Unit).OrNull(), product.Bind(p => p.ImageId.ToMaybe()).OrNull());
                })]);
        });
}
