using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Cart.Domain;

/// <summary>What the catalog says about a product in the cart.</summary>
/// <param name="Discontinued">No longer replenished: only the stock on hand can be ordered.</param>
public sealed record ProductFacts(bool Discontinued);

/// <summary>What can be ordered from stock now, and when more arrives.</summary>
public sealed record StockFacts(decimal Available, Maybe<DateOnly> NextDelivery)
{
    public static StockFacts None { get; } = new(0, Maybe<DateOnly>.None);
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
    public sealed record DiscontinuedShortage(decimal Available) : LineIssue;

    /// <summary>Replenished, but more than is on hand: the rest is delivered later.</summary>
    public sealed record Backorder(decimal Available, Maybe<DateOnly> NextDelivery) : LineIssue;

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
}

public sealed record LineReview(CartItem Item, EquatableArray<LineIssue> Issues)
{
    public bool Blocks => Issues.Any(issue => issue.Blocks);
}

/// <param name="CanOrder">No line and no cart issue prevents ordering.</param>
public sealed record CartReviewResult(EquatableArray<LineReview> Lines, EquatableArray<CartIssue> Issues, bool CanOrder);

/// <summary>Whether a cart can be ordered, and what the buyer should know about each line.</summary>
public static class CartReview
{
    /// <param name="product">The product in the catalog; none when it is gone or inactive.</param>
    /// <param name="priced">Whether the buyer has a price for the SKU.</param>
    /// <param name="stock">What can be ordered from stock.</param>
    public static CartReviewResult Review(
        CartState cart,
        Func<string, Maybe<ProductFacts>> product,
        Func<string, bool> priced,
        Func<string, StockFacts> stock,
        bool customerBlocked)
    {
        EquatableArray<LineReview> lines = [.. cart.Items.Select(item => new LineReview(item, product(item.Sku).Match(
            facts => LineIssues(item, facts, priced(item.Sku), stock(item.Sku)),
            () => [new LineIssue.Unavailable()])))];
        EquatableArray<CartIssue> issues =
        [
            .. cart.Items.IsEmpty ? [new CartIssue.Empty()] : Array.Empty<CartIssue>(),
            .. customerBlocked ? [new CartIssue.CustomerBlocked()] : Array.Empty<CartIssue>(),
        ];
        return new CartReviewResult(lines, issues, issues.IsEmpty && !lines.Any(line => line.Blocks));
    }

    private static EquatableArray<LineIssue> LineIssues(CartItem item, ProductFacts facts, bool priced, StockFacts stock) =>
    [
        .. priced ? Array.Empty<LineIssue>() : [new LineIssue.NoPrice()],
        .. item.Quantity <= stock.Available ? Array.Empty<LineIssue>()
            : facts.Discontinued ? [new LineIssue.DiscontinuedShortage(stock.Available)]
            : [new LineIssue.Backorder(stock.Available, stock.NextDelivery)],
    ];
}
