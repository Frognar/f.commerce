using FCommerce.Cart.Domain;
using FCommerce.Testing;
using static FCommerce.Cart.Tests.CartValues;

namespace FCommerce.Cart.Tests;

public sealed class ShoppingListsTests
{
    private static readonly CartItem[] Items = [I("A", 2), I("B", 1)];

    private static ShoppingList List(params CartItem[] items) => ShoppingLists.New("Wola", items, []).ShouldSucceed();

    [Fact]
    public void A_new_list_has_a_free_trimmed_name_and_merged_items()
    {
        var list = ShoppingLists.New("  Budowa Wola ", [.. Items, I("A", 1)], ["MAGAZYN"]).ShouldSucceed();

        Assert.Equal(("Budowa Wola", "BUDOWA WOLA"), (list.Name.Value, list.Name.Key));
        Assert.Equal([("A", 3m), ("B", 1m)], Pairs(list.Items));
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
        var list = List(Items);

        Assert.Equal("WOLA", ShoppingLists.Rename(list, "WOLA", ["WOLA", "MOKOTÓW"]).ShouldSucceed().Name.Value);
        Assert.Equal("shopping-list.name-taken", ShoppingLists.Rename(list, "Mokotów", ["WOLA", "MOKOTÓW"]).ShouldFail().Code);
    }

    [Fact]
    public void Quantities_change_and_none_removes_but_never_the_last_product()
    {
        var list = List(Items);

        Assert.Equal([("A", 5m), ("B", 1m)], Pairs(ShoppingLists.ChangeQuantity(list, S("A"), To(5)).ShouldSucceed().Items));
        Assert.Equal([("B", 1m)], Pairs(ShoppingLists.ChangeQuantity(list, S("A"), To(0)).ShouldSucceed().Items));
        Assert.Equal("shopping-list.empty", ShoppingLists.ChangeQuantity(List(I("B", 1)), S("B"), To(0)).ShouldFail().Code);
        Assert.Equal("shopping-list.item-not-found", ShoppingLists.ChangeQuantity(list, S("C"), To(1)).ShouldFail().Code);
    }
}
