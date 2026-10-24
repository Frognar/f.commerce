using FCommerce.Cart.Domain;
using FCommerce.Testing;

namespace FCommerce.Cart.Tests;

public sealed class ShoppingListsTests
{
    private static readonly CartItem[] Items = [new("A", 2), new("B", 1)];

    [Fact]
    public void A_new_list_has_a_free_trimmed_name_and_merged_items()
    {
        var list = ShoppingLists.New("  Budowa Wola ", [.. Items, new CartItem("A", 1)], ["MAGAZYN"]).ShouldSucceed();

        Assert.Equal(("Budowa Wola", "BUDOWA WOLA"), (list.Name.Value, list.Name.Key));
        Assert.Equal([new CartItem("A", 3), new CartItem("B", 1)], list.Items);
    }

    [Theory]
    [InlineData(" ", "shopping-list.name-empty")]
    [InlineData("budowa wola", "shopping-list.name-taken")]
    public void Names_are_required_and_unique_without_case(string name, string code)
    {
        Assert.Equal(code, ShoppingLists.New(name, Items, ["BUDOWA WOLA"]).ShouldFail().Code);
    }

    [Fact]
    public void A_list_needs_products_and_room()
    {
        Assert.Equal("shopping-list.empty", ShoppingLists.New("Pusta", [], []).ShouldFail().Code);
        Assert.Equal("shopping-list.too-many", ShoppingLists.New("Nowa", Items, [.. Enumerable.Range(0, ShoppingLists.MaxLists).Select(i => $"L{i}")]).ShouldFail().Code);
        Assert.Equal("shopping-list.name-too-long", ShoppingLists.New(new string('x', 101), Items, []).ShouldFail().Code);
    }

    [Fact]
    public void Renaming_may_keep_the_own_name_but_not_take_another()
    {
        var current = ShoppingListName.Parse("Wola").ShouldSucceed();

        Assert.Equal("WOLA", ShoppingLists.Rename("WOLA", current, ["WOLA", "MOKOTÓW"]).ShouldSucceed().Value);
        Assert.Equal("shopping-list.name-taken", ShoppingLists.Rename("Mokotów", current, ["WOLA", "MOKOTÓW"]).ShouldFail().Code);
    }

    [Fact]
    public void Quantities_change_and_zero_removes_but_never_the_last_product()
    {
        var items = ShoppingLists.Items(Items).ShouldSucceed();

        Assert.Equal([new CartItem("A", 5), new CartItem("B", 1)], ShoppingLists.ChangeQuantity(items, "A", 5).ShouldSucceed());
        Assert.Equal([new CartItem("B", 1)], ShoppingLists.ChangeQuantity(items, "A", 0).ShouldSucceed());
        Assert.Equal("shopping-list.empty", ShoppingLists.ChangeQuantity([new CartItem("B", 1)], "B", 0).ShouldFail().Code);
        Assert.Equal("shopping-list.item-not-found", ShoppingLists.ChangeQuantity(items, "C", 1).ShouldFail().Code);
    }
}
