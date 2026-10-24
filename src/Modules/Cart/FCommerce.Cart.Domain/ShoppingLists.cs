using FCommerce.SharedKernel.Collections;
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

/// <summary>Rules of an owner's shopping lists (a user acting for a customer).</summary>
public static class ShoppingLists
{
    public const int MaxLists = 50;
    public const int MaxItems = Carts.MaxItems;

    /// <summary>A new list: items to keep (at least one, at most <see cref="MaxItems"/>), a free name, room for another list.</summary>
    public static Result<(ShoppingListName Name, EquatableArray<CartItem> Items), Error> New(string name, IReadOnlyList<CartItem> items, IReadOnlyCollection<string> existingKeys) =>
        from parsed in ShoppingListName.Parse(name)
        from _ in Free(parsed, existingKeys)
        from __ in existingKeys.Count >= MaxLists
            ? Result.Fail<bool, Error>(Error.Validation("shopping-list.too-many", $"At most {MaxLists} shopping lists."))
            : Result.Ok<bool, Error>(true)
        from valid in Items(items)
        select (parsed, valid);

    /// <summary>A new name for a list; the list's own name is free for it.</summary>
    public static Result<ShoppingListName, Error> Rename(string name, ShoppingListName current, IReadOnlyCollection<string> existingKeys) =>
        ShoppingListName.Parse(name).Bind(parsed => Free(parsed, [.. existingKeys.Where(key => key != current.Key)]).Map(_ => parsed));

    /// <summary>Items of a list: the same SKU summed, valid quantities, at least one and at most <see cref="MaxItems"/>.</summary>
    public static Result<EquatableArray<CartItem>, Error> Items(IReadOnlyList<CartItem> items) =>
        Carts.Normalize(items).Bind(valid => valid.Count switch
        {
            0 => Result.Fail<EquatableArray<CartItem>, Error>(Error.Validation("shopping-list.empty", "A shopping list needs at least one product.")),
            > MaxItems => Error.Validation("shopping-list.too-many-items", $"A shopping list holds at most {MaxItems} products."),
            _ => valid,
        });

    /// <summary>Sets the quantity of an item; zero removes it (a list may not become empty).</summary>
    public static Result<EquatableArray<CartItem>, Error> ChangeQuantity(EquatableArray<CartItem> items, string sku, decimal quantity) =>
        Carts.Sku(sku).Bind(key => items.Any(item => item.Sku == key)
            ? quantity == 0
                ? Items([.. items.Where(item => item.Sku != key)])
                : Carts.Quantity(quantity).Map(valid => (EquatableArray<CartItem>)[.. items.Select(item => item.Sku == key ? item with { Quantity = valid } : item)])
            : Error.NotFound("shopping-list.item-not-found", $"'{key}' is not on the list."));

    private static Result<bool, Error> Free(ShoppingListName name, IReadOnlyCollection<string> existingKeys) =>
        existingKeys.Contains(name.Key)
            ? Error.Conflict("shopping-list.name-taken", "A shopping list with this name already exists.")
            : true;
}
