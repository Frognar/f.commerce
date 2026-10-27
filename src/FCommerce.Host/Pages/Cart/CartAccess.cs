using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Cart.Contracts;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Host.Pages.Cart;

/// <summary>
/// The cart of the current request: the signed-in user's cart for the active customer, when the access policies let
/// them order. The owner comes only from the session, so no request can reach another cart.
/// </summary>
public sealed class CartAccess(CurrentUser user, AccessControl access, IQueryHandler<GetCartSize, int> size)
{
    private Maybe<Maybe<CartOwner>> cartOwner;
    private Maybe<Maybe<CartOwner>> listsOwner;

    /// <summary>The owner of the cart; none when the user may not order for the active customer.</summary>
    public async Task<Maybe<CartOwner>> OwnerAsync(CancellationToken cancellationToken = default)
    {
        var known = await cartOwner.Match(Task.FromResult, () => PermittedAsync(code => new Resource.Cart(code), cancellationToken));
        cartOwner = Maybe.Some(known);
        return known;
    }

    /// <summary>The owner of shopping lists (the same person and customer, with the shopping lists feature on).</summary>
    public async Task<Maybe<CartOwner>> ShoppingListsOwnerAsync(CancellationToken cancellationToken = default)
    {
        var known = await listsOwner.Match(Task.FromResult, () => PermittedAsync(code => new Resource.ShoppingLists(code), cancellationToken));
        listsOwner = Maybe.Some(known);
        return known;
    }

    private async Task<Maybe<CartOwner>> PermittedAsync(Func<string, Resource> resource, CancellationToken cancellationToken)
    {
        var session = await user.SessionAsync(cancellationToken);
        var membership = await user.ActiveMembershipAsync(cancellationToken);
        var candidate = from s in session from m in membership select new CartOwner(s.UserId, m.CustomerCode);
        return await candidate.Match(
            async c => await access.CanAsync(AccessAction.Manage, resource(c.CustomerCode), cancellationToken) ? Maybe.Some(c) : Maybe<CartOwner>.None,
            () => Task.FromResult(Maybe<CartOwner>.None));
    }

    /// <summary>The order limit of the active membership; none when unlimited (a limit without its currency is a broken contract, not "unlimited").</summary>
    public async Task<Maybe<CartOrderLimit>> OrderLimitAsync(CancellationToken cancellationToken = default) =>
        (await user.ActiveMembershipAsync(cancellationToken)).Bind(m => (m.OrderLimit, m.OrderLimitCurrency) switch
        {
            (null, null) => Maybe<CartOrderLimit>.None,
            ({ } amount, { } currency) => new CartOrderLimit(amount, currency),
            _ => throw new InvalidOperationException("Access reports an order limit without its amount or currency."),
        });

    /// <summary>Products in the cart, for the header; none when the user has no cart.</summary>
    public async Task<Maybe<int>> SizeAsync(CancellationToken cancellationToken = default) =>
        await (await OwnerAsync(cancellationToken)).Match(
            async c => Maybe.Some(await size.HandleAsync(new GetCartSize(c), cancellationToken)),
            () => Task.FromResult(Maybe<int>.None));
}

/// <summary>The "add to cart" form of a product.</summary>
/// <param name="ReturnUrl">Where a request without htmx goes back to.</param>
public sealed record AddToCartForm(string Sku, string Unit, string Source, string ReturnUrl);

/// <summary>The cart link in the header; <paramref name="OutOfBand"/> replaces it from an htmx response.</summary>
public sealed record CartLink(int Count, bool OutOfBand);
