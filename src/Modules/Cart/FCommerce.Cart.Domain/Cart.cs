using System.Globalization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Domain;

public sealed record Sku : IPersistedValue<Sku, string>
{
    public const int MaxLength = 64;

    private Sku(string value) => Value = value;

    public string Value { get; }

    public static Result<Sku, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "sku", "SKU").Map(value => new Sku(value));

    static Sku IPersistedValue<Sku, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Sku, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A quantity of a cart line: more than zero, at most 3 decimal places, bounded.</summary>
public sealed record CartQuantity : IPersistedValue<CartQuantity, decimal>
{
    public const int Decimals = 3;
    public const decimal Max = 1_000_000m;

    private CartQuantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static CartQuantity One { get; } = new(1m);

    public static Result<CartQuantity, Error> Parse(decimal raw) =>
        raw switch
        {
            <= 0 => Error.Validation("quantity.not-positive", "The quantity must be greater than zero."),
            > Max => Error.Validation("quantity.too-large", "The quantity is too large."),
            _ when decimal.Round(raw, Decimals) != raw => Error.Validation("quantity.precision", $"Quantities have at most {Decimals} decimal places."),
            _ => new CartQuantity(raw),
        };

    /// <summary>Both quantities together, still within the bound.</summary>
    public Result<CartQuantity, Error> Add(CartQuantity other) => Parse(Value + other.Value);

    /// <summary>What is left after taking <paramref name="other"/> away; none when nothing is.</summary>
    public Maybe<CartQuantity> Less(CartQuantity other) => Value > other.Value ? new CartQuantity(Value - other.Value) : Maybe<CartQuantity>.None;

    static bool IPersistedValue<CartQuantity, decimal>.IsWellFormed(decimal raw) => raw > 0;

    static CartQuantity IPersistedValue<CartQuantity, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<CartQuantity, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A product and how much of it.</summary>
public sealed record CartItem(Sku Sku, CartQuantity Quantity);

/// <summary>An item as a buyer or another module sent it, before it is read.</summary>
[SharedKernel.Validation.RawInput]
public sealed record UnvalidatedCartItem(string Sku, decimal Quantity);

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

    public sealed record ItemQuantityChanged(Sku Sku, CartQuantity Quantity) : CartEvent;

    public sealed record ItemRemoved(Sku Sku) : CartEvent;

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

    /// <summary>Sets the quantity of an item in the cart; none removes it.</summary>
    public sealed record ChangeQuantity(Sku Sku, Maybe<CartQuantity> Quantity) : CartCommand;

    public sealed record RemoveItem(Sku Sku) : CartCommand;

    public sealed record Clear : CartCommand;

    /// <summary>Empties the cart and adds the items (one decision, one append).</summary>
    public sealed record ReplaceItems(IReadOnlyList<CartItem> Items, CartSource Source) : CartCommand;

    /// <summary>
    /// Takes the ordered quantities out of the cart (what was added meanwhile stays). Once per order: the same
    /// order again changes nothing.
    /// </summary>
    public sealed record RemoveOrdered(Guid OrderId, IReadOnlyList<CartItem> Items) : CartCommand;
}

/// <summary>
/// The items of a cart in the order they were first added: each SKU once, at most <see cref="Carts.MaxItems"/>. Built
/// only by <see cref="Carts.Evolve"/>.
/// </summary>
public sealed record CartState
{
    private CartState(EquatableArray<CartItem> items, EquatableArray<Guid> orders)
    {
        Items = items;
        Orders = orders;
    }

    public static CartState Empty { get; } = new(EquatableArray<CartItem>.Empty, EquatableArray<Guid>.Empty);

    public EquatableArray<CartItem> Items { get; }

    /// <summary>Orders whose items already left the cart (kept when the cart is cleared).</summary>
    public EquatableArray<Guid> Orders { get; }

    public Maybe<CartItem> Find(Sku sku) => Items.FirstOrNone(item => item.Sku == sku);

    internal CartState With(EquatableArray<CartItem> items) => new(items, Orders);

    internal CartState WithOrder(EquatableArray<CartItem> items, Guid order) => new(items, Orders.Add(order));
}

/// <summary>A cart as a pure decider: commands become events, events build the state.</summary>
public static class Carts
{
    public const int MaxItems = 200;

    public static Decider<CartCommand, CartState, CartEvent> Decider { get; } = new(Decide, Evolve, CartState.Empty);

    public static Result<EquatableArray<CartEvent>, Error> Decide(CartCommand command, CartState state, DecisionContext context) =>
        command switch
        {
            CartCommand.AddItems add => Normalize(add.Items).Bind(items => Add(state, items, add.Source)),
            CartCommand.ChangeQuantity change => ChangeQuantity(state, change),
            CartCommand.RemoveItem remove => state.Find(remove.Sku).Match(
                _ => (EquatableArray<CartEvent>)[new CartEvent.ItemRemoved(remove.Sku)],
                () => EquatableArray<CartEvent>.Empty),
            CartCommand.Clear => state.Items.IsEmpty ? EquatableArray<CartEvent>.Empty : [new CartEvent.Cleared(ClearReason.Emptied)],
            CartCommand.ReplaceItems replace => Normalize(replace.Items).Bind(items =>
                Add(CartState.Empty, items, replace.Source).Map(added =>
                    (EquatableArray<CartEvent>)[.. state.Items.IsEmpty ? [] : new CartEvent[] { new CartEvent.Cleared(ClearReason.Replaced) }, .. added])),
            CartCommand.RemoveOrdered ordered => state.Orders.Contains(ordered.OrderId)
                ? EquatableArray<CartEvent>.Empty
                : Normalize(ordered.Items).Map(items => (EquatableArray<CartEvent>)[new CartEvent.ItemsOrdered(ordered.OrderId, items)]),
            _ => Error.Validation("cart.unknown-command", "Unknown cart command."),
        };

    /// <summary>An event that does not fit the state (Decide never emits one) changes nothing.</summary>
    public static CartState Evolve(CartState state, CartEvent @event) =>
        @event switch
        {
            CartEvent.ItemsAdded added => added.Items.Aggregate(state, (current, item) => current.Find(item.Sku).Match(
                existing => existing.Quantity.Add(item.Quantity).Match(sum => current.With(Replace(current.Items, existing with { Quantity = sum })), _ => current),
                () => current.Items.Count < MaxItems ? current.With(current.Items.Add(item)) : current)),
            CartEvent.ItemQuantityChanged changed => state.Find(changed.Sku).Match(
                existing => state.With(Replace(state.Items, existing with { Quantity = changed.Quantity })),
                () => state),
            CartEvent.ItemRemoved removed => state.With(state.Items.RemoveAll(item => item.Sku == removed.Sku)),
            CartEvent.Cleared => state.With(EquatableArray<CartItem>.Empty),
            CartEvent.ItemsOrdered ordered => state.WithOrder(
                [.. state.Items.Choose(item => ordered.Items.FirstOrNone(o => o.Sku == item.Sku).Match(
                    o => item.Quantity.Less(o.Quantity).Map(left => item with { Quantity = left }),
                    () => item))],
                ordered.OrderId),
            _ => state,
        };

    public static Result<Sku, Error> Sku(string raw) => Domain.Sku.Parse(raw);

    /// <summary>A raw item read: its SKU and quantity.</summary>
    public static Result<CartItem, Error> Item(UnvalidatedCartItem item) =>
        from sku in Sku(item.Sku)
        from quantity in CartQuantity.Parse(item.Quantity)
        select new CartItem(sku, quantity);

    /// <summary>Items with the same SKU summed (within the bound), in the order of their first occurrence.</summary>
    public static Result<EquatableArray<CartItem>, Error> Normalize(IEnumerable<CartItem> items) =>
        items
            .GroupBy(item => item.Sku)
            .Select(group => group.Skip(1).Aggregate(Result.Ok<CartQuantity, Error>(group.First().Quantity), (sum, item) => sum.Bind(total => total.Add(item.Quantity)))
                .Map(total => new CartItem(group.Key, total)))
            .Traverse(item => item)
            .Map(valid => valid.ToEquatableArray());

    private static Result<EquatableArray<CartEvent>, Error> Add(CartState state, EquatableArray<CartItem> items, CartSource source)
    {
        if (items.IsEmpty)
        {
            return EquatableArray<CartEvent>.Empty;
        }

        var sums = items.Traverse(item => state.Find(item.Sku).Match(existing => existing.Quantity.Add(item.Quantity).Map(_ => item), () => Result.Ok<CartItem, Error>(item)));
        var count = state.Items.Count + items.Count(item => !state.Find(item.Sku).Exists(_ => true));
        return sums.Bind(_ => count > MaxItems
            ? Error.Validation("cart.too-many-items", $"A cart holds at most {MaxItems} products.")
            : Result.Ok<EquatableArray<CartEvent>, Error>([new CartEvent.ItemsAdded(items, source)]));
    }

    private static Result<EquatableArray<CartEvent>, Error> ChangeQuantity(CartState state, CartCommand.ChangeQuantity change) =>
        state.Find(change.Sku).Match(
            existing => change.Quantity.Match<Result<EquatableArray<CartEvent>, Error>>(
                quantity => quantity == existing.Quantity
                    ? EquatableArray<CartEvent>.Empty
                    : (EquatableArray<CartEvent>)[new CartEvent.ItemQuantityChanged(change.Sku, quantity)],
                () => (EquatableArray<CartEvent>)[new CartEvent.ItemRemoved(change.Sku)]),
            () => Error.NotFound("cart.item-not-found", $"'{change.Sku}' is not in the cart."));

    private static EquatableArray<CartItem> Replace(EquatableArray<CartItem> items, CartItem item) =>
        [.. items.Select(existing => existing.Sku == item.Sku ? item : existing)];
}
