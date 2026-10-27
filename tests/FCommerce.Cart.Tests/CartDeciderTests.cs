using FCommerce.Cart.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Results;
using FCommerce.Testing;
using static FCommerce.Cart.Tests.CartValues;

namespace FCommerce.Cart.Tests;

public sealed class CartDeciderTests
{
    private static readonly DecisionContext Context = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private static CartState Given(params CartEvent[] events) => Carts.Decider.Fold(events);

    private static EquatableArray<CartEvent> When(CartState state, CartCommand command) =>
        Carts.Decide(command, state, Context).ShouldSucceed();

    private static Error Fails(CartState state, CartCommand command) =>
        Carts.Decide(command, state, Context).ShouldFail();

    private static CartEvent.ItemsAdded Added(params (string Sku, decimal Quantity)[] items) =>
        new([.. items.Select(i => I(i.Sku, i.Quantity))], CartSource.Product);

    [Fact]
    public void Adding_merges_the_same_sku_and_adds_to_the_quantity_in_the_cart()
    {
        var state = Given(Added(("A", 2)));

        var events = When(state, new CartCommand.AddItems([I(" A ", 1), I("B", 0.5m), I("A", 1.5m)], CartSource.Product));
        var after = Carts.Decider.Fold(state, events);

        Assert.Equal([Added(("A", 2.5m), ("B", 0.5m))], events);
        Assert.Equal([("A", 4.5m), ("B", 0.5m)], Pairs(after.Items));
    }

    [Theory]
    [InlineData(0, "quantity.not-positive")]
    [InlineData(-1, "quantity.not-positive")]
    [InlineData(1.0005, "quantity.precision")]
    [InlineData(1000001, "quantity.too-large")]
    public void Quantities_are_positive_with_at_most_three_decimal_places(decimal quantity, string code)
    {
        Assert.Equal(code, Carts.Item(new UnvalidatedCartItem("A", quantity)).ShouldFail().Code);
    }

    [Fact]
    public void A_sum_over_the_bound_is_refused_not_stored()
    {
        var state = Given(Added(("A", CartQuantity.Max)));

        Assert.Equal("quantity.too-large", Fails(state, new CartCommand.AddItems([I("A", 1)], CartSource.Product)).Code);
        Assert.Equal("quantity.too-large", Fails(CartState.Empty, new CartCommand.AddItems([I("A", CartQuantity.Max), I("A", 1)], CartSource.Product)).Code);
    }

    [Fact]
    public void A_cart_holds_at_most_two_hundred_products()
    {
        var full = Given(Added([.. Enumerable.Range(0, Carts.MaxItems).Select(i => ($"P{i}", 1m))]));

        Assert.Equal("cart.too-many-items", Fails(full, new CartCommand.AddItems([I("NEW", 1)], CartSource.Product)).Code);
        Assert.Single(When(full, new CartCommand.AddItems([I("P1", 1)], CartSource.Product))); // more of one already there
    }

    [Fact]
    public void Changing_the_quantity_sets_it_and_none_removes_the_item()
    {
        var state = Given(Added(("A", 2), ("B", 1)));

        Assert.Equal([new CartEvent.ItemQuantityChanged(S("A"), Q(7))], When(state, new CartCommand.ChangeQuantity(S("A"), To(7))));
        Assert.Equal([new CartEvent.ItemRemoved(S("B"))], When(state, new CartCommand.ChangeQuantity(S("B"), To(0))));
        Assert.Empty(When(state, new CartCommand.ChangeQuantity(S("A"), To(2))));
        Assert.Equal("cart.item-not-found", Fails(state, new CartCommand.ChangeQuantity(S("C"), To(1))).Code);
    }

    [Fact]
    public void Removing_and_clearing_are_idempotent()
    {
        var state = Given(Added(("A", 2)));

        Assert.Equal([new CartEvent.ItemRemoved(S("A"))], When(state, new CartCommand.RemoveItem(S("A"))));
        Assert.Empty(When(state, new CartCommand.RemoveItem(S("B"))));
        Assert.Equal([new CartEvent.Cleared(ClearReason.Emptied)], When(state, new CartCommand.Clear()));
        Assert.Empty(When(CartState.Empty, new CartCommand.Clear()));
    }

    [Fact]
    public void Replacing_clears_a_non_empty_cart_and_adds_the_items_in_one_decision()
    {
        var state = Given(Added(("A", 2)));
        var items = new[] { I("B", 3), I("C", 1) };

        var events = When(state, new CartCommand.ReplaceItems(items, CartSource.ShoppingList));

        Assert.Equal([new CartEvent.Cleared(ClearReason.Replaced), new CartEvent.ItemsAdded([.. items], CartSource.ShoppingList)], events);
        Assert.Equal([new CartEvent.ItemsAdded([.. items], CartSource.ShoppingList)], When(CartState.Empty, new CartCommand.ReplaceItems(items, CartSource.ShoppingList)));
        Assert.Equal(items, Carts.Decider.Fold(state, events).Items);
    }

    [Fact]
    public void Evolving_keeps_the_order_of_first_addition()
    {
        var state = Given(Added(("A", 1), ("B", 1)), new CartEvent.ItemQuantityChanged(S("A"), Q(5)), Added(("C", 1)), new CartEvent.ItemRemoved(S("B")), Added(("B", 2)));

        Assert.Equal([("A", 5m), ("C", 1m), ("B", 2m)], Pairs(state.Items));
        Assert.Empty(Carts.Decider.Fold(state, [new CartEvent.Cleared(ClearReason.Ordered)]).Items);
    }

    [Fact]
    public void An_order_takes_its_quantities_out_and_leaves_what_was_added_meanwhile()
    {
        var order = Guid.Parse("0199f1a0-0000-7000-8000-000000000001");
        var state = Given(Added(("A", 2), ("B", 1), ("C", 3)));

        var events = When(state, new CartCommand.RemoveOrdered(order, [I("A", 2), I("C", 1)]));
        var after = Carts.Decider.Fold(state, events);

        Assert.Equal([new CartEvent.ItemsOrdered(order, [I("A", 2), I("C", 1)])], events);
        Assert.Equal([("B", 1m), ("C", 2m)], Pairs(after.Items));
    }

    [Fact]
    public void The_same_order_leaves_the_cart_once_even_after_clearing()
    {
        var order = Guid.Parse("0199f1a0-0000-7000-8000-000000000002");
        var ordered = Given(Added(("A", 2)), new CartEvent.ItemsOrdered(order, [I("A", 2)]), new CartEvent.Cleared(ClearReason.Emptied), Added(("A", 5)));

        Assert.Empty(When(ordered, new CartCommand.RemoveOrdered(order, [I("A", 2)])));
        Assert.Equal([("A", 5m)], Pairs(ordered.Items));
    }
}
