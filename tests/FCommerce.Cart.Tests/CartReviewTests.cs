using FCommerce.Cart.Domain;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Cart.Tests;

public sealed class CartReviewTests
{
    private static readonly DateOnly Delivery = new(2026, 11, 3);

    private static CartState Cart(params (string Sku, decimal Quantity)[] items) => new([.. items.Select(i => new CartItem(i.Sku, i.Quantity))]);

    /// <summary>Reviews one line: the product exists unless <paramref name="gone"/>, 5 on hand, a delivery on 3 Nov.</summary>
    private static LineReview Line(decimal quantity, bool priced = true, bool discontinued = false, bool gone = false) =>
        CartReview.Review(
            Cart(("A", quantity)),
            _ => gone ? Maybe<ProductFacts>.None : new ProductFacts(discontinued),
            _ => priced,
            _ => new StockFacts(5, Delivery),
            customerBlocked: false).Lines[0];

    [Fact]
    public void A_priced_product_in_stock_has_no_issues()
    {
        Assert.Empty(Line(5).Issues);
    }

    [Fact]
    public void Without_a_price_the_line_blocks_ordering()
    {
        var line = Line(1, priced: false);

        Assert.Equal([new LineIssue.NoPrice()], line.Issues);
        Assert.True(line.Blocks);
    }

    [Fact]
    public void More_than_on_hand_of_a_replenished_product_is_delivered_later()
    {
        var line = Line(8);

        Assert.Equal([new LineIssue.Backorder(5, Delivery)], line.Issues);
        Assert.False(line.Blocks);
    }

    [Fact]
    public void More_than_on_hand_of_a_discontinued_product_blocks_ordering()
    {
        Assert.Equal([new LineIssue.DiscontinuedShortage(5)], Line(8, discontinued: true).Issues);
        Assert.Empty(Line(5, discontinued: true).Issues);
        Assert.True(Line(5.001m, discontinued: true).Blocks);
    }

    [Fact]
    public void A_product_gone_from_the_catalog_blocks_and_hides_other_issues()
    {
        Assert.Equal([new LineIssue.Unavailable()], Line(80, priced: false, gone: true).Issues);
    }

    [Fact]
    public void Issues_of_a_line_add_up()
    {
        Assert.Equal([new LineIssue.NoPrice(), new LineIssue.Backorder(5, Delivery)], Line(9, priced: false).Issues);
    }

    [Fact]
    public void The_cart_can_be_ordered_only_without_blocking_issues()
    {
        var backorder = CartReview.Review(Cart(("A", 9)), _ => new ProductFacts(false), _ => true, _ => StockFacts.None, customerBlocked: false);
        var blocked = CartReview.Review(Cart(("A", 1)), _ => new ProductFacts(false), _ => true, _ => new StockFacts(5, default), customerBlocked: true);
        var empty = CartReview.Review(CartState.Empty, _ => new ProductFacts(false), _ => true, _ => StockFacts.None, customerBlocked: false);
        var noPrice = CartReview.Review(Cart(("A", 1), ("B", 1)), _ => new ProductFacts(false), sku => sku == "A", _ => new StockFacts(5, default), customerBlocked: false);

        Assert.True(backorder.CanOrder);
        Assert.Equal((false, (CartIssue)new CartIssue.CustomerBlocked()), (blocked.CanOrder, Assert.Single(blocked.Issues)));
        Assert.Equal((false, (CartIssue)new CartIssue.Empty()), (empty.CanOrder, Assert.Single(empty.Issues)));
        Assert.False(noPrice.CanOrder);
    }
}
