using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Contracts;

/// <summary>Saves the items of the cart as a new shopping list of the owner (the cart stays as it is).</summary>
public sealed record SaveCartAsShoppingList(CartOwner Owner, string Name) : ICommand<Result<Guid, Error>>;

public sealed record RenameShoppingList(CartOwner Owner, Guid ListId, string Name) : ICommand<Result<Unit, Error>>;

public sealed record DeleteShoppingList(CartOwner Owner, Guid ListId) : ICommand<Result<Unit, Error>>;

/// <summary>Sets the quantity of a product on a list; zero removes it (the last product cannot be removed).</summary>
public sealed record ChangeShoppingListItem(CartOwner Owner, Guid ListId, string Sku, decimal Quantity) : ICommand<Result<Unit, Error>>;

/// <summary>Adds the list to the cart, or replaces the cart with it; products the catalog no longer offers are reported.</summary>
public sealed record ShoppingListToCart(CartOwner Owner, Guid ListId, bool Replace) : ICommand<Result<CartChange, Error>>;

/// <summary>The owner's lists, most recently changed first.</summary>
public sealed record GetShoppingLists(CartOwner Owner) : IQuery<IReadOnlyList<ShoppingListSummary>>;

public sealed record ShoppingListSummary(Guid Id, string Name, int ItemCount, DateTimeOffset UpdatedAt);

/// <summary>A list of the owner with product names in the first matching culture; not found for other owners' lists.</summary>
public sealed record GetShoppingList(CartOwner Owner, Guid ListId, IReadOnlyList<string> Cultures) : IQuery<Result<ShoppingListView, Error>>;

public sealed record ShoppingListView(Guid Id, string Name, DateTimeOffset UpdatedAt, IReadOnlyList<ShoppingListItemView> Items);

/// <param name="Name">None when the catalog no longer offers the product.</param>
public sealed record ShoppingListItemView(string Sku, decimal Quantity, string? Name, string? Unit, string? ImageId);
