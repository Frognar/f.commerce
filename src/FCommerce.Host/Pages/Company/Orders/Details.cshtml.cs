using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Cart.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Cart;
using FCommerce.Host.Pages.Company.Lists;
using FCommerce.Host.Security;
using FCommerce.Ordering.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Company.Orders;

/// <summary>
/// One order of the active customer: frozen lines and totals, its status history and ERP documents; cancelling it
/// before the ERP takes it over, and sending its products to the cart. Orders the user may not see are not found.
/// </summary>
[RequiresFeature("ordering")]
public sealed class DetailsModel(
    OrderAccess orders,
    CompanyContext company,
    CartAccess carts,
    IQueryHandler<GetOrder, Result<OrderView, Error>> getOrder,
    IQueryHandler<GetOrderDocuments, IReadOnlyList<DocumentSummary>> getDocuments,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    ICommandHandler<CancelOrder, Result<Unit, Error>> cancel,
    ICommandHandler<AddToCart, Result<CartChange, Error>> addToCart,
    ICommandHandler<ReplaceCart, Result<CartChange, Error>> replaceCart,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    private Maybe<OrderView> order;

    /// <summary>The order; read only when the page renders.</summary>
    public OrderView Order => order.Match(o => o, () => throw new InvalidOperationException("The order is not loaded."));

    /// <summary>The ERP documents of the order with their files, when the user may see documents.</summary>
    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = [];

    public bool CanSeeDocuments { get; private set; }

    public bool CanCancel { get; private set; }

    public bool CanReorder { get; private set; }

    /// <summary>The order has just been placed (after the checkout).</summary>
    [BindProperty(SupportsGet = true, Name = "placed")]
    public bool JustPlaced { get; set; }

    public ListOutcome Outcome { get; private set; } = ListOutcome.None;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        WithOrderAsync(id, (_, _) => Task.FromResult(ListOutcome.None), cancellationToken);

    public Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken) =>
        WithOrderAsync(id, async (viewer, found) =>
        {
            JustPlaced = false;
            if (!await orders.CanAsync(AccessAction.Manage, viewer, found, cancellationToken))
            {
                return ListOutcome.Failed(texts["Error.order.not-cancellable"]);
            }

            return (await cancel.HandleAsync(new CancelOrder(found.Id, viewer.UserId), cancellationToken)).Match(
                _ => ListOutcome.Done(texts["OrderCancelled"]),
                error => ListOutcome.Failed(texts.Describe(error)));
        }, cancellationToken);

    /// <summary>The products of the order go to the cart, added or replacing it.</summary>
    public Task<IActionResult> OnPostToCartAsync(Guid id, bool replace, CancellationToken cancellationToken) =>
        WithOrderAsync(id, async (_, found) =>
        {
            JustPlaced = false;
            var owner = (await carts.OwnerAsync(cancellationToken)).Where(_ => CanReorder);
            return await owner.Match(
                async cartOwner =>
                {
                    IReadOnlyList<CartItemData> items = [.. found.Lines.Select(l => new CartItemData(l.Sku, l.Quantity))];
                    return ListOutcome.Of(
                        replace
                            ? await replaceCart.HandleAsync(new ReplaceCart(cartOwner, items, CartSources.Order), cancellationToken)
                            : await addToCart.HandleAsync(new AddToCart(cartOwner, items, CartSources.Order), cancellationToken),
                        texts);
                },
                () => Task.FromResult(ListOutcome.Failed(texts["Error.cart.not-available"])));
        }, cancellationToken);

    public string FileHref(string documentId) =>
        Url.CulturePage("/Company/Documents") + new QueryBuilder { { "handler", "File" }, { "id", documentId } }.ToQueryString();

    /// <summary>Runs the action on a visible order, then loads it again (an action may have changed it).</summary>
    private async Task<IActionResult> WithOrderAsync(Guid id, Func<OrderViewer, OrderView, Task<ListOutcome>> action, CancellationToken cancellationToken) =>
        await (await orders.ViewerAsync(cancellationToken)).Match(
            async viewer =>
            {
                var visible = await VisibleAsync(viewer, id, cancellationToken);
                return await visible.Match<Task<IActionResult>>(
                    async found =>
                    {
                        await LoadRightsAsync(viewer, found, cancellationToken);
                        Outcome = await action(viewer, found);
                        order = await VisibleAsync(viewer, id, cancellationToken);
                        await order.Match(o => LoadAsync(viewer, o, cancellationToken), () => Task.CompletedTask);
                        return order.Match<IActionResult>(_ => Page(), NotFound);
                    },
                    () => Task.FromResult<IActionResult>(NotFound()));
            },
            () => Task.FromResult<IActionResult>(Forbid()));

    private async Task<Maybe<OrderView>> VisibleAsync(OrderViewer viewer, Guid id, CancellationToken cancellationToken)
    {
        var found = (await getOrder.HandleAsync(new GetOrder(id), cancellationToken)).ToMaybe();
        return await found.Match(
            async o => await orders.CanAsync(AccessAction.Read, viewer, o, cancellationToken) ? Maybe.Some(o) : Maybe<OrderView>.None,
            () => Task.FromResult(Maybe<OrderView>.None));
    }

    private async Task LoadRightsAsync(OrderViewer viewer, OrderView found, CancellationToken cancellationToken)
    {
        CanReorder = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.ReorderFromOrders.Value)
            && (await carts.OwnerAsync(cancellationToken)).Exists(owner => owner.CustomerCode == viewer.Customer.Code);
        CanSeeDocuments = await company.CanAsync(AccessAction.Read, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), viewer.Customer, cancellationToken);
        CanCancel = found.Status == OrderStatuses.Placed && await orders.CanAsync(AccessAction.Manage, viewer, found, cancellationToken);
    }

    private async Task LoadAsync(OrderViewer viewer, OrderView found, CancellationToken cancellationToken)
    {
        await LoadRightsAsync(viewer, found, cancellationToken);
        Documents = CanSeeDocuments && found.Documents.Count > 0
            ? await getDocuments.HandleAsync(new GetOrderDocuments(viewer.Customer.Code, [.. found.Documents.Select(d => d.DocumentId)]), cancellationToken)
            : [];
    }
}
