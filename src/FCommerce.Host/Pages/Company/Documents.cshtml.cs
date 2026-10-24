using System.Globalization;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Cart.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Pages.Cart;
using FCommerce.Host.Pages.Company.Lists;
using FCommerce.Host.Security;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Company;

/// <summary>The active customer's documents from the ERP: filters, what is due, PDF downloads.</summary>
[RequiresFeature("documents")]
public sealed class DocumentsModel(
    CompanyContext company,
    IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>> documents,
    IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>> receivables,
    IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>> files,
    IQueryHandler<GetDocumentLines, Result<DocumentLines, Error>> lines,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    ICommandHandler<AddToCart, Result<CartChange, Error>> addToCart,
    ICommandHandler<ReplaceCart, Result<CartChange, Error>> replaceCart,
    CartAccess carts,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public const string ResultsId = "document-results";

    public static readonly string[] Types = ["invoice", "correction-invoice", "delivery-note", "proforma"];

    public static readonly string[] Statuses = [DocumentStatuses.Unpaid, DocumentStatuses.Overdue, DocumentStatuses.Paid];

    [BindProperty(SupportsGet = true, Name = "type")]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    private Maybe<CustomerDetails> customer;

    /// <summary>The active customer; read only after a successful load (the view renders only then).</summary>
    public CustomerDetails Customer => customer.Match(c => c, () => throw new InvalidOperationException("The customer is not loaded."));

    public IReadOnlyList<ReceivableView> Receivables { get; private set; } = [];

    public DocumentPage Results { get; private set; } = new([], 1, 1, 0);

    /// <summary>Products of documents can go to the cart (feature on, the user may order).</summary>
    public bool CanReorder { get; private set; }

    /// <summary>What sending a document to the cart did.</summary>
    public ListOutcome Outcome { get; private set; } = ListOutcome.None;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Read, cancellationToken)).Match(Task.FromResult, () => ListAsync(cancellationToken));

    /// <summary>The products of a document go to the cart, added or replacing it (lines without a SKU are counted, not added).</summary>
    public async Task<IActionResult> OnPostToCartAsync(string id, bool replace, CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Read, cancellationToken)).Match(Task.FromResult, async () =>
        {
            var owner = (await carts.OwnerAsync(cancellationToken)).Where(o => CanReorder && o.CustomerCode == Customer.Code);
            if (!owner.Exists(_ => true))
            {
                return NotFound();
            }

            Outcome = await owner.Match(cartOwner => ToCartAsync(cartOwner, id, replace, cancellationToken), () => Task.FromResult(ListOutcome.None));
            return await ListAsync(cancellationToken);
        });

    private async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        // Unknown filter values (edited URLs) are ignored rather than reported.
        Type = Type.ToMaybe().Where(Types.Contains).OrNull();
        Status = Status.ToMaybe().Where(Statuses.Contains).OrNull();
        return await (await documents.HandleAsync(new GetCustomerDocuments(Customer.Code, Type, From, To, Status, PageNumber), cancellationToken)).Match<Task<IActionResult>>(
            async page =>
            {
                Results = page;
                if (Request.IsHtmxFor(ResultsId))
                {
                    return Partial("_DocumentResults", this);
                }

                Receivables = await receivables.HandleAsync(new GetReceivables(Customer.Code), cancellationToken);
                return Page();
            },
            error => Task.FromResult<IActionResult>(BadRequest(error.Message)));
    }

    /// <summary>The PDF of one of the active customer's documents (the customer never comes from the URL).</summary>
    public async Task<IActionResult> OnGetFileAsync(string id, CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Download, cancellationToken)).Match(Task.FromResult, () => FileAsync(id, cancellationToken));

    private async Task<IActionResult> FileAsync(string id, CancellationToken cancellationToken) =>
        (await files.HandleAsync(new OpenDocumentFile(Customer.Code, id), cancellationToken)).Match<IActionResult>(
            file =>
            {
                Response.Headers.CacheControl = "private, no-store";
                return File(file.Content, "application/pdf", file.FileName);
            },
            _ => NotFound());

    /// <summary>This list with the current filters.</summary>
    public string Href() => Href(Maybe<int>.None);

    /// <summary>A page of this list with the current filters.</summary>
    public string Href(int page) => Href(Maybe.Some(page));

    private string Href(Maybe<int> page)
    {
        var query = new QueryBuilder();
        Add(query, "type", Type.ToMaybe());
        Add(query, "status", Status.ToMaybe());
        Add(query, "from", From.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "to", To.ToMaybe().Map(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(query, "p", page.Where(number => number > 1).Map(number => number.ToString(CultureInfo.InvariantCulture)));
        return Url.CulturePage("/Company/Documents") + query.ToQueryString();
    }

    public string FileHref(string id) =>
        Url.CulturePage("/Company/Documents") + new QueryBuilder { { "handler", "File" }, { "id", id } }.ToQueryString();

    /// <summary>An amount of a document (its currency may be unknown: shown without it).</summary>
    public static string Amount(decimal amount, Maybe<string> currency) =>
        $"{Formats.Amount(amount)} {currency.ValueOr(string.Empty)}".TrimEnd();

    /// <summary>A date of a document; empty when it has none.</summary>
    public static string Date(Maybe<DateOnly> date) =>
        date.Map(d => d.ToString("d", CultureInfo.CurrentCulture)).ValueOr(string.Empty);

    private async Task<ListOutcome> ToCartAsync(CartOwner owner, string id, bool replace, CancellationToken cancellationToken) =>
        await (await lines.HandleAsync(new GetDocumentLines(Customer.Code, id), cancellationToken)).Match(
            async document =>
            {
                IReadOnlyList<CartItemData> items = [.. document.Reorderable.Select(l => new CartItemData(l.Sku, l.Quantity))];
                var change = replace
                    ? await replaceCart.HandleAsync(new ReplaceCart(owner, items, CartSources.Document), cancellationToken)
                    : await addToCart.HandleAsync(new AddToCart(owner, items, CartSources.Document), cancellationToken);
                var outcome = ListOutcome.Of(change, texts);
                return document.SkippedLines > 0
                    ? outcome with { NotAdded = [.. outcome.NotAdded, texts["LinesWithoutSku", document.SkippedLines].Value] }
                    : outcome;
            },
            error => Task.FromResult(ListOutcome.Failed(texts.Describe(error))));

    /// <summary>Loads the active customer; the result is the denial, none when loaded.</summary>
    private async Task<Maybe<IActionResult>> LoadAsync(AccessAction action, CancellationToken cancellationToken)
    {
        var active = await company.ActiveCustomerAsync(cancellationToken);
        var allowed = await active.Match(
            c => company.CanAsync(action, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), c, cancellationToken),
            () => Task.FromResult(false));
        customer = active.Where(_ => allowed);
        CanReorder = allowed
            && (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.ReorderFromDocuments.Value)
            && (await carts.OwnerAsync(cancellationToken)).Match(_ => true, () => false);
        return allowed ? Maybe<IActionResult>.None : Forbid();
    }

    private static void Add(QueryBuilder query, string name, Maybe<string> value) =>
        value.Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add(name, text), () => { });
}
