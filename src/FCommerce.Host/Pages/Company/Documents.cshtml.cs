using System.Globalization;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Company;

/// <summary>The active customer's documents from the ERP: filters, what is due, PDF downloads.</summary>
[RequiresFeature("documents")]
public sealed class DocumentsModel(
    CompanyContext company,
    IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>> documents,
    IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>> receivables,
    IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>> files) : PageModel
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

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await (await LoadAsync(AccessAction.Read, cancellationToken)).Match(Task.FromResult, () => ListAsync(cancellationToken));

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

    /// <summary>Loads the active customer; the result is the denial, none when loaded.</summary>
    private async Task<Maybe<IActionResult>> LoadAsync(AccessAction action, CancellationToken cancellationToken)
    {
        var active = await company.ActiveCustomerAsync(cancellationToken);
        var allowed = await active.Match(
            c => company.CanAsync(action, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), c, cancellationToken),
            () => Task.FromResult(false));
        customer = active.Where(_ => allowed);
        return allowed ? Maybe<IActionResult>.None : Forbid();
    }

    private static void Add(QueryBuilder query, string name, Maybe<string> value) =>
        value.Where(text => !string.IsNullOrWhiteSpace(text)).Switch(text => query.Add(name, text), () => { });
}
