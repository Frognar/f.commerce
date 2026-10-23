using System.Globalization;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
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

    public CustomerDetails? Customer { get; private set; }

    public IReadOnlyList<ReceivableView> Receivables { get; private set; } = [];

    public DocumentPage Results { get; private set; } = new([], 1, 1, 0);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(AccessAction.Read, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Unknown filter values (edited URLs) are ignored rather than reported.
        Type = Types.Contains(Type) ? Type : null;
        Status = Statuses.Contains(Status) ? Status : null;
        return await (await documents.HandleAsync(new GetCustomerDocuments(Customer!.Code, Type, From, To, Status, PageNumber), cancellationToken)).Match<Task<IActionResult>>(
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
    public async Task<IActionResult> OnGetFileAsync(string id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(AccessAction.Download, cancellationToken) is { } denied)
        {
            return denied;
        }

        return (await files.HandleAsync(new OpenDocumentFile(Customer!.Code, id), cancellationToken)).Match<IActionResult>(
            file =>
            {
                Response.Headers.CacheControl = "private, no-store";
                return File(file.Content, "application/pdf", file.FileName);
            },
            _ => NotFound());
    }

    public string Href(int? page = null)
    {
        var query = new QueryBuilder();
        Add(query, "type", Type);
        Add(query, "status", Status);
        Add(query, "from", From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(query, "to", To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (page is > 1)
        {
            query.Add("p", page.Value.ToString(CultureInfo.InvariantCulture));
        }

        return Url.CulturePage("/Company/Documents") + query.ToQueryString();
    }

    public string FileHref(string id) =>
        Url.CulturePage("/Company/Documents") + new QueryBuilder { { "handler", "File" }, { "id", id } }.ToQueryString();

    public static string Amount(decimal amount, string? currency) =>
        $"{amount.ToString("N2", CultureInfo.CurrentCulture)} {currency}".TrimEnd();

    public static string Date(DateOnly? date) =>
        date?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;

    private async Task<IActionResult?> LoadAsync(AccessAction action, CancellationToken cancellationToken)
    {
        var customer = await company.ActiveCustomerAsync(cancellationToken);
        if (customer is null || !await company.CanAsync(action, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), customer, cancellationToken))
        {
            return Forbid();
        }

        Customer = customer;
        return null;
    }

    private static void Add(QueryBuilder query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(name, value);
        }
    }
}
