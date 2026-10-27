using FCommerce.Cart.Domain;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Cart.Tests;

/// <summary>Cart values for the tests, read through their parsers; items compared as (SKU, quantity).</summary>
internal static class CartValues
{
    public static Sku S(string sku) => Sku.Parse(sku).ShouldSucceed();

    public static CartQuantity Q(decimal quantity) => CartQuantity.Parse(quantity).ShouldSucceed();

    public static CartItem I(string sku, decimal quantity) => new(S(sku), Q(quantity));

    public static Maybe<CartQuantity> To(decimal quantity) => quantity == 0 ? Maybe<CartQuantity>.None : Q(quantity);

    public static IEnumerable<(string Sku, decimal Quantity)> Pairs(IEnumerable<CartItem> items) => items.Select(item => (item.Sku.Value, item.Quantity.Value));
}
