using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Domain;

/// <summary>The name of a shopping list: trimmed, 1–100 characters; names compare without case.</summary>
public sealed record ShoppingListName : IPersistedValue<ShoppingListName, string>
{
    public const int MaxLength = 100;

    private ShoppingListName(string value) => Value = value;

    public string Value { get; }

    /// <summary>What makes two names the same for one owner ("Budowa Wola" = "budowa wola").</summary>
    public string Key => Value.ToUpperInvariant();

    public static Result<ShoppingListName, Error> Parse(string raw)
    {
        var value = raw.Trim();
        return value switch
        {
            "" => Error.Validation("shopping-list.name-empty", "Name the list."),
            { Length: > MaxLength } => Error.Validation("shopping-list.name-too-long", $"At most {MaxLength} characters."),
            _ when value.Any(char.IsControl) => Error.Validation("shopping-list.name-invalid", "Control characters are not allowed."),
            _ => new ShoppingListName(value),
        };
    }

    static ShoppingListName IPersistedValue<ShoppingListName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ShoppingListName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A shopping list of an owner: a name and at least one item.</summary>
public sealed record ShoppingList(ShoppingListName Name, NonEmptyArray<CartItem> Items);

/// <summary>Rules of an owner's shopping lists (a user acting for a customer).</summary>
public static class ShoppingLists
{
    public const int MaxLists = 50;
    public const int MaxItems = Carts.MaxItems;

    /// <summary>A new list: items to keep (at least one, at most <see cref="MaxItems"/>), a free name, room for another list.</summary>
    public static Result<ShoppingList, Error> New(string name, IReadOnlyList<CartItem> items, IReadOnlyCollection<string> existingKeys) =>
        from parsed in ShoppingListName.Parse(name)
        from _ in Free(parsed, existingKeys)
        from __ in existingKeys.Count >= MaxLists
            ? Result.Fail<bool, Error>(Error.Validation("shopping-list.too-many", $"At most {MaxLists} shopping lists."))
            : Result.Ok<bool, Error>(true)
        from valid in Items(items)
        select new ShoppingList(parsed, valid);

    /// <summary>A new name for a list; the list's own name is free for it.</summary>
    public static Result<ShoppingList, Error> Rename(ShoppingList list, string name, IReadOnlyCollection<string> existingKeys) =>
        ShoppingListName.Parse(name).Bind(parsed => Free(parsed, [.. existingKeys.Where(key => key != list.Name.Key)]).Map(_ => list with { Name = parsed }));

    /// <summary>Items of a list: the same SKU summed, at least one and at most <see cref="MaxItems"/>.</summary>
    public static Result<NonEmptyArray<CartItem>, Error> Items(IReadOnlyList<CartItem> items) =>
        Carts.Normalize(items).Bind(valid => valid.Count > MaxItems
            ? Error.Validation("shopping-list.too-many-items", $"A shopping list holds at most {MaxItems} products.")
            : valid.ToNonEmptyArray().ToResult(() => Error.Validation("shopping-list.empty", "A shopping list needs at least one product.")));

    /// <summary>Sets the quantity of an item; none removes it (a list may not become empty).</summary>
    public static Result<ShoppingList, Error> ChangeQuantity(ShoppingList list, Sku sku, Maybe<CartQuantity> quantity) =>
        list.Items.Any(item => item.Sku == sku)
            ? quantity.Match(
                    valid => Items([.. list.Items.Select(item => item.Sku == sku ? item with { Quantity = valid } : item)]),
                    () => Items([.. list.Items.Where(item => item.Sku != sku)]))
                .Map(items => list with { Items = items })
            : Error.NotFound("shopping-list.item-not-found", $"'{sku}' is not on the list.");

    private static Result<bool, Error> Free(ShoppingListName name, IReadOnlyCollection<string> existingKeys) =>
        existingKeys.Contains(name.Key)
            ? Error.Conflict("shopping-list.name-taken", "A shopping list with this name already exists.")
            : true;
}
