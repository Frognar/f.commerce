using FCommerce.Cart.Domain;

namespace FCommerce.Cart.Tests;

public sealed class QuickOrderTests
{
    [Fact]
    public void Lines_are_read_with_any_usual_separator_and_a_decimal_comma()
    {
        var pasted = QuickOrder.Parse("SR-M8x40-ZN\t100\r\nNK-M8-ZN;50\n  PD-8  2,5 \nOP-100,3\nBHP-OKUL\n\n");

        Assert.Equal(
            [new CartItem("SR-M8x40-ZN", 100), new CartItem("NK-M8-ZN", 50), new CartItem("PD-8", 2.5m), new CartItem("OP-100", 3), new CartItem("BHP-OKUL", 1)],
            pasted.Items);
        Assert.Empty(pasted.Errors);
    }

    [Fact]
    public void The_same_sku_on_several_lines_is_summed()
    {
        Assert.Equal([new CartItem("A", 5), new CartItem("B", 1)], QuickOrder.Parse("A 2\nB\nA 3").Items);
    }

    [Fact]
    public void Unreadable_lines_are_reported_with_their_numbers_and_the_rest_is_kept()
    {
        var pasted = QuickOrder.Parse("A 2\nB dużo\n\nC 1 2\nD 0\nE 1,0001");

        Assert.Equal([new CartItem("A", 2)], pasted.Items);
        Assert.Equal(
            [(2, "B dużo", "paste.quantity"), (4, "C 1 2", "paste.format"), (5, "D 0", "quantity.not-positive"), (6, "E 1,0001", "quantity.precision")],
            pasted.Errors.Select(e => (e.Line, e.Text, e.Error.Code)));
    }

    [Fact]
    public void At_most_two_hundred_lines_are_read_at_once()
    {
        var pasted = QuickOrder.Parse(string.Join('\n', Enumerable.Range(0, QuickOrder.MaxLines + 1).Select(i => $"P{i} 1")));

        Assert.Empty(pasted.Items);
        Assert.Equal("paste.too-many-lines", Assert.Single(pasted.Errors).Error.Code);
    }
}
