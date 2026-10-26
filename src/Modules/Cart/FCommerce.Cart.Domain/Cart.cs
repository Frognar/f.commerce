using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Domain;

/// <summary>A product and how much of it; the SKU is trimmed and the quantity validated.</summary>
public sealed record CartItem(string Sku, decimal Quantity);

/// <summary>Where items came from (for analytics and the history of a cart).</summary>
public enum CartSource
{
    Product,
    Catalog,
    QuickOrder,
    Paste,
    ShoppingList,
    Document,
    Order,
    Api,
}

public enum ClearReason
{
    /// <summary>The buyer emptied the cart.</summary>
    Emptied,

    /// <summary>The items were replaced by others (a list, a document).</summary>
    Replaced,

    /// <summary>An order was placed from the cart.</summary>
    Ordered,
}

/// <summary>What happened to a cart. Persisted: change only additively, or add a new version.</summary>
public abstract record CartEvent
{
    private CartEvent()
    {
    }

    /// <summary>Items added to the cart; quantities add up with the ones already there.</summary>
    public sealed record ItemsAdded(EquatableArray<CartItem> Items, CartSource Source) : CartEvent;

    public sealed record ItemQuantityChanged(string Sku, decimal Quantity) : CartEvent;

    public sealed record ItemRemoved(string Sku) : CartEvent;

    public sealed record Cleared(ClearReason Reason) : CartEvent;

    /// <summary>An order was placed with these items: their quantities leave the cart.</summary>
    public sealed record ItemsOrdered(Guid OrderId, EquatableArray<CartItem> Items) : CartEvent;
}

public abstract record CartCommand
{
    private CartCommand()
    {
    }

    public sealed record AddItems(IReadOnlyList<CartItem> Items, CartSource Source) : CartCommand;

    /// <summary>Sets the quantity of an item in the cart; zero removes it.</summary>
    public sealed record ChangeQuantity(string Sku, decimal Quantity) : CartCommand;

    public sealed record RemoveItem(string Sku) : CartCommand;

    public sealed record Clear : CartCommand;

    /// <summary>Empties the cart and adds the items (one decision, one append).</summary>
    public sealed record ReplaceItems(IReadOnlyList<CartItem> Items, CartSource Source) : CartCommand;

    /// <summary>
    /// Takes the ordered quantities out of the cart (what was added meanwhile stays). Once per order: the same
    /// order again changes nothing.
    /// </summary>
    public sealed record RemoveOrdered(Guid OrderId, IReadOnlyList<CartItem> Items) : CartCommand;
}

/// <summary>The items of a cart in the order they were first added.</summary>
/// <param name="Orders">Orders whose items already left the cart (kept when the cart is cleared).</param>
public sealed record CartState(EquatableArray<CartItem> Items, EquatableArray<Guid> Orders)
{
    public static CartState Empty { get; } = new(EquatableArray<CartItem>.Empty, EquatableArray<Guid>.Empty);

    public Maybe<CartItem> Find(string sku) => Items.FirstOrNone(item => item.Sku == sku);
}

/// <summary>A cart as a pure decider: commands become events, events build the state.</summary>
public static class Carts
{
    public const int MaxItems = 200;
    public const int MaxSku = 64;
    public const int QuantityDecimals = 3;
    public const decimal MaxQuantity = 1_000_000m;

    public static Decider<CartCommand, CartState, CartEvent> Decider { get; } = new(Decide, Evolve, CartState.Empty);

    public static Result<EquatableArray<CartEvent>, Error> Decide(CartCommand command, CartState state, DecisionContext context) =>
        command switch
        {
            CartCommand.AddItems add => Normalize(add.Items).Bind(items => Add(state, items, add.Source).Map(events => events)),
            CartCommand.ChangeQuantity change => ChangeQuantity(state, change),
            CartCommand.RemoveItem remove => Sku(remove.Sku).Map(sku => state.Find(sku).Match(
                _ => (EquatableArray<CartEvent>)[new CartEvent.ItemRemoved(sku)],
                () => EquatableArray<CartEvent>.Empty)),
            CartCommand.Clear => state.Items.IsEmpty ? EquatableArray<CartEvent>.Empty : [new CartEvent.Cleared(ClearReason.Emptied)],
            CartCommand.ReplaceItems replace => Normalize(replace.Items).Bind(items =>
                Add(CartState.Empty, items, replace.Source).Map(added =>
                    (EquatableArray<CartEvent>)[.. state.Items.IsEmpty ? [] : new CartEvent[] { new CartEvent.Cleared(ClearReason.Replaced) }, .. added])),
            CartCommand.RemoveOrdered ordered => state.Orders.Contains(ordered.OrderId)
                ? EquatableArray<CartEvent>.Empty
                : Normalize(ordered.Items).Map(items => (EquatableArray<CartEvent>)[new CartEvent.ItemsOrdered(ordered.OrderId, items)]),
            _ => Error.Validation("cart.unknown-command", "Unknown cart command."),
        };

    public static CartState Evolve(CartState state, CartEvent @event) =>
        @event switch
        {
            CartEvent.ItemsAdded added => added.Items.Aggregate(state, (current, item) => current.Find(item.Sku).Match(
                existing => current with { Items = Replace(current.Items, existing with { Quantity = existing.Quantity + item.Quantity }) },
                () => current with { Items = current.Items.Add(item) })),
            CartEvent.ItemQuantityChanged changed => state.Find(changed.Sku).Match(
                existing => state with { Items = Replace(state.Items, existing with { Quantity = changed.Quantity }) },
                () => state),
            CartEvent.ItemRemoved removed => state with { Items = state.Items.RemoveAll(item => item.Sku == removed.Sku) },
            CartEvent.Cleared => state with { Items = EquatableArray<CartItem>.Empty },
            CartEvent.ItemsOrdered ordered => state with
            {
                Items = [.. state.Items
                    .Select(item => ordered.Items.FirstOrNone(o => o.Sku == item.Sku).Match(o => item with { Quantity = item.Quantity - o.Quantity }, () => item))
                    .Where(item => item.Quantity > 0)],
                Orders = state.Orders.Add(ordered.OrderId),
            },
            _ => state,
        };

    /// <summary>A quantity of a cart line: more than zero, at most 3 decimal places, bounded.</summary>
    public static Result<decimal, Error> Quantity(decimal quantity) =>
        quantity switch
        {
            <= 0 => Error.Validation("quantity.not-positive", "The quantity must be greater than zero."),
            > MaxQuantity => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(quantity, QuantityDecimals) != quantity => Error.Validation("quantity.precision", $"Quantities have at most {QuantityDecimals} decimal places."),
            _ => quantity,
        };

    public static Result<string, Error> Sku(string raw) => SourceCodes.Parse(raw, MaxSku, "sku", "SKU");

    /// <summary>Valid items with the same SKU summed, in the order of their first occurrence.</summary>
    public static Result<EquatableArray<CartItem>, Error> Normalize(IReadOnlyList<CartItem> items) =>
        items
            .Select((item, index) => (from sku in Sku(item.Sku) from quantity in Quantity(item.Quantity) select new CartItem(sku, quantity))
                .MapError(error => error.AtField($"items[{index}]")))
            .Traverse(item => item)
            .Map(valid => valid
                .GroupBy(item => item.Sku)
                .Select(group => new CartItem(group.Key, group.Sum(item => item.Quantity)))
                .ToEquatableArray());

    private static Result<EquatableArray<CartEvent>, Error> Add(CartState state, EquatableArray<CartItem> items, CartSource source)
    {
        if (items.IsEmpty)
        {
            return EquatableArray<CartEvent>.Empty;
        }

        var after = Evolve(state, new CartEvent.ItemsAdded(items, source));
        return after.Items.Count > MaxItems ? Error.Validation("cart.too-many-items", $"A cart holds at most {MaxItems} products.")
            : after.Items.Any(item => item.Quantity > MaxQuantity) ? Error.Validation("quantity.too-large", "The quantity is too large.")
            : (EquatableArray<CartEvent>)[new CartEvent.ItemsAdded(items, source)];
    }

    private static Result<EquatableArray<CartEvent>, Error> ChangeQuantity(CartState state, CartCommand.ChangeQuantity change) =>
        Sku(change.Sku).Bind(sku => state.Find(sku).Match(
            existing => change.Quantity == 0
                ? Result.Ok<EquatableArray<CartEvent>, Error>([new CartEvent.ItemRemoved(sku)])
                : Quantity(change.Quantity).Map(quantity => quantity == existing.Quantity
                    ? EquatableArray<CartEvent>.Empty
                    : [new CartEvent.ItemQuantityChanged(sku, quantity)]),
            () => Error.NotFound("cart.item-not-found", $"'{sku}' is not in the cart.")));

    private static EquatableArray<CartItem> Replace(EquatableArray<CartItem> items, CartItem item) =>
        [.. items.Select(existing => existing.Sku == item.Sku ? item : existing)];
}
