using FCommerce.Cart.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using static FCommerce.Cart.Tests.CartValues;

namespace FCommerce.Cart.Tests;

public sealed class CartReviewTests
{
    private static readonly DateOnly Delivery = new(2026, 11, 3);

    private static CartState Cart(params (string Sku, decimal Quantity)[] items) =>
        Carts.Decider.Fold(items.Length == 0 ? [] : [new CartEvent.ItemsAdded([.. items.Select(i => I(i.Sku, i.Quantity))], CartSource.Product)]);

    private static StockFacts OnHand(decimal available, Maybe<DateOnly> delivery = default) => new(AvailableQuantity.Of(available), delivery);

    private static Money Pln(decimal amount) => new(amount, Currency.Parse("PLN").ShouldSucceed());

    private static Money Eur(decimal amount) => new(amount, Currency.Parse("EUR").ShouldSucceed());

    /// <summary>Reviews one line: the product exists unless <paramref name="gone"/>, 5 on hand, a delivery on 3 Nov.</summary>
    private static LineReview Line(decimal quantity, bool priced = true, bool discontinued = false, bool gone = false) =>
        CartReview.Review(
            Cart(("A", quantity)),
            _ => gone ? Maybe<ProductFacts>.None : new ProductFacts(discontinued),
            _ => priced,
            _ => OnHand(5, Delivery),
            [],
            OrderTerms.None).Lines[0];

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

        Assert.Equal([new LineIssue.Backorder(AvailableQuantity.Of(5), Delivery)], line.Issues);
        Assert.False(line.Blocks);
    }

    [Fact]
    public void More_than_on_hand_of_a_discontinued_product_blocks_ordering()
    {
        Assert.Equal([new LineIssue.DiscontinuedShortage(AvailableQuantity.Of(5))], Line(8, discontinued: true).Issues);
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
        Assert.Equal([new LineIssue.NoPrice(), new LineIssue.Backorder(AvailableQuantity.Of(5), Delivery)], Line(9, priced: false).Issues);
    }

    [Fact]
    public void The_cart_can_be_ordered_only_without_blocking_issues()
    {
        var backorder = CartReview.Review(Cart(("A", 9)), _ => new ProductFacts(false), _ => true, _ => StockFacts.None, [], OrderTerms.None);
        var blocked = CartReview.Review(Cart(("A", 1)), _ => new ProductFacts(false), _ => true, _ => OnHand(5), [], new OrderTerms(true, Maybe<Money>.None));
        var empty = CartReview.Review(CartState.Empty, _ => new ProductFacts(false), _ => true, _ => StockFacts.None, [], OrderTerms.None);
        var noPrice = CartReview.Review(Cart(("A", 1), ("B", 1)), _ => new ProductFacts(false), sku => sku == S("A"), _ => OnHand(5), [], OrderTerms.None);

        Assert.True(backorder.CanOrder);
        Assert.Equal((false, (CartIssue)new CartIssue.CustomerBlocked()), (blocked.CanOrder, Assert.Single(blocked.Issues)));
        Assert.Equal((false, (CartIssue)new CartIssue.Empty()), (empty.CanOrder, Assert.Single(empty.Issues)));
        Assert.False(noPrice.CanOrder);
    }

    private static CartReviewResult Totals(EquatableArray<Money> netTotals, Maybe<Money> limit) =>
        CartReview.Review(Cart(("A", 1)), _ => new ProductFacts(false), _ => true, _ => OnHand(5), netTotals, new OrderTerms(false, limit));

    [Fact]
    public void Lines_in_several_currencies_block_ordering()
    {
        var mixed = Totals([Eur(10), Pln(10)], Maybe<Money>.None);

        Assert.Equal((false, (CartIssue)new CartIssue.MixedCurrencies()), (mixed.CanOrder, Assert.Single(mixed.Issues)));
    }

    [Fact]
    public void The_net_value_may_reach_but_not_exceed_the_order_limit_in_its_currency()
    {
        Assert.True(Totals([Pln(1000)], Pln(1000)).CanOrder);
        Assert.Equal([new CartIssue.OverLimit(Pln(1000))], Totals([Pln(1000.01m)], Pln(1000)).Issues);
        Assert.Equal([new CartIssue.OverLimit(Pln(1000))], Totals([Eur(1)], Pln(1000)).Issues);
        Assert.True(Totals([Pln(1_000_000)], Maybe<Money>.None).CanOrder);
    }

    [Fact]
    public void Stock_below_zero_is_none_available()
    {
        Assert.Equal(AvailableQuantity.None, AvailableQuantity.Of(-3));
        Assert.Equal([new LineIssue.Backorder(AvailableQuantity.None, default)], CartReview.Review(Cart(("A", 1)), _ => new ProductFacts(false), _ => true, _ => OnHand(-3), [], OrderTerms.None).Lines[0].Issues);
    }
}
