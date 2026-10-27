using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Cart.Domain;

/// <summary>What the catalog says about a product in the cart.</summary>
/// <param name="Discontinued">No longer replenished: only the stock on hand can be ordered.</param>
public sealed record ProductFacts(bool Discontinued);

/// <summary>What can be ordered from stock now: zero or more, as stock shows it to buyers.</summary>
public sealed record AvailableQuantity
{
    private AvailableQuantity(decimal value) => Value = value;

    public static AvailableQuantity None { get; } = new(0m);

    public decimal Value { get; }

    /// <summary>A quantity on hand; below zero (an ERP correction in progress) there is none.</summary>
    public static AvailableQuantity Of(decimal raw) => raw > 0 ? new AvailableQuantity(raw) : None;
}

/// <summary>What can be ordered from stock now, and when more arrives.</summary>
public sealed record StockFacts(AvailableQuantity Available, Maybe<DateOnly> NextDelivery)
{
    public static StockFacts None { get; } = new(AvailableQuantity.None, Maybe<DateOnly>.None);
}

public abstract record LineIssue
{
    private LineIssue()
    {
    }

    /// <summary>The product is no longer in the catalog (or inactive).</summary>
    public sealed record Unavailable : LineIssue;

    /// <summary>No price for this buyer ("price on request").</summary>
    public sealed record NoPrice : LineIssue;

    /// <summary>Discontinued and more than is on hand.</summary>
    public sealed record DiscontinuedShortage(AvailableQuantity Available) : LineIssue;

    /// <summary>Replenished, but more than is on hand: the rest is delivered later.</summary>
    public sealed record Backorder(AvailableQuantity Available, Maybe<DateOnly> NextDelivery) : LineIssue;

    /// <summary>Whether the issue prevents ordering the cart.</summary>
    public bool Blocks => this is not Backorder;
}

public abstract record CartIssue
{
    private CartIssue()
    {
    }

    public sealed record Empty : CartIssue;

    /// <summary>The customer is blocked in the ERP (e.g. overdue payments).</summary>
    public sealed record CustomerBlocked : CartIssue;

    /// <summary>Lines priced in different currencies: one order has one currency.</summary>
    public sealed record MixedCurrencies : CartIssue;

    /// <summary>The net value is above the buyer's order limit (or in another currency than the limit).</summary>
    public sealed record OverLimit(Money Limit) : CartIssue;
}

/// <summary>What, besides the lines, decides whether the buyer may order.</summary>
/// <param name="CustomerBlocked">The customer is blocked in the ERP.</param>
/// <param name="OrderLimit">The highest net value of an order the buyer may place; none when unlimited.</param>
public sealed record OrderTerms(bool CustomerBlocked, Maybe<Money> OrderLimit)
{
    public static OrderTerms None { get; } = new(false, Maybe<Money>.None);
}

public sealed record LineReview(CartItem Item, EquatableArray<LineIssue> Issues)
{
    public bool Blocks => Issues.Any(issue => issue.Blocks);
}

public sealed record CartReviewResult(EquatableArray<LineReview> Lines, EquatableArray<CartIssue> Issues)
{
    /// <summary>No line and no cart issue prevents ordering.</summary>
    public bool CanOrder => Issues.IsEmpty && !Lines.Any(line => line.Blocks);
}

/// <summary>Whether a cart can be ordered, and what the buyer should know about each line.</summary>
public static class CartReview
{
    /// <param name="product">The product in the catalog; none when it is gone or inactive.</param>
    /// <param name="priced">Whether the buyer has a price for the SKU.</param>
    /// <param name="stock">What can be ordered from stock.</param>
    /// <param name="netTotals">Net value of the priced lines, per currency.</param>
    public static CartReviewResult Review(
        CartState cart,
        Func<Sku, Maybe<ProductFacts>> product,
        Func<Sku, bool> priced,
        Func<Sku, StockFacts> stock,
        EquatableArray<Money> netTotals,
        OrderTerms terms)
    {
        EquatableArray<LineReview> lines = [.. cart.Items.Select(item => new LineReview(item, product(item.Sku).Match(
            facts => LineIssues(item, facts, priced(item.Sku), stock(item.Sku)),
            () => [new LineIssue.Unavailable()])))];
        EquatableArray<CartIssue> issues =
        [
            .. cart.Items.IsEmpty ? [new CartIssue.Empty()] : Array.Empty<CartIssue>(),
            .. terms.CustomerBlocked ? [new CartIssue.CustomerBlocked()] : Array.Empty<CartIssue>(),
            .. netTotals.Count > 1 ? [new CartIssue.MixedCurrencies()] : Array.Empty<CartIssue>(),
            .. terms.OrderLimit.Where(limit => netTotals.Any(total => total.Currency != limit.Currency || total.Amount > limit.Amount))
                .Map(limit => (CartIssue)new CartIssue.OverLimit(limit)).AsEnumerable(),
        ];
        return new CartReviewResult(lines, issues);
    }

    private static EquatableArray<LineIssue> LineIssues(CartItem item, ProductFacts facts, bool priced, StockFacts stock) =>
    [
        .. priced ? Array.Empty<LineIssue>() : [new LineIssue.NoPrice()],
        .. item.Quantity.Value <= stock.Available.Value ? Array.Empty<LineIssue>()
            : facts.Discontinued ? [new LineIssue.DiscontinuedShortage(stock.Available)]
            : [new LineIssue.Backorder(stock.Available, stock.NextDelivery)],
    ];
}
