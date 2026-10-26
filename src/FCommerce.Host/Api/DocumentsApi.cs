using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Pages.Company;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace FCommerce.Host.Api;

internal static partial class PublicApiEndpoints
{
    private static readonly string[] DocumentTypes = ["invoice", "correction-invoice", "delivery-note", "proforma"];

    private static readonly string[] DocumentStatusFilters = [DocumentStatuses.Unpaid, DocumentStatuses.Overdue, DocumentStatuses.Paid];

    private static void MapDocuments(RouteGroupBuilder api)
    {
        var documents = api.MapGroup("/documents").RequireFeature("documents");
        documents.MapGet("", ListDocumentsAsync)
            .WithSummary("The customer's documents from the ERP (invoices, delivery notes, …), newest first.");
        documents.MapGet("/{id}/file", GetDocumentFileAsync)
            .WithSummary("The PDF of a document.");
        api.MapGet("/receivables", GetReceivablesAsync)
            .RequireFeature("documents")
            .WithSummary("What the customer owes, per currency.");
    }

    private static async Task<IResult> ListDocumentsAsync(
        [AsParameters] DocumentSearch search,
        CompanyContext company,
        IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>> documents,
        CancellationToken cancellationToken)
    {
        if (search.Type is { } type && !DocumentTypes.Contains(type))
        {
            return ApiResults.BadRequest("api.invalid-filter", $"type must be one of: {string.Join(", ", DocumentTypes)}.", "type");
        }

        if (search.Status is { } status && !DocumentStatusFilters.Contains(status))
        {
            return ApiResults.BadRequest("api.invalid-filter", $"status must be one of: {string.Join(", ", DocumentStatusFilters)}.", "status");
        }

        return await (await DocumentsCustomerAsync(company, AccessAction.Read, cancellationToken)).Match(
            async customer =>
                (await documents.HandleAsync(
                    new GetCustomerDocuments(customer.Code, search.Type, search.From, search.To, search.Status, Math.Max(search.Page, 1), search.PageSize > 0 ? search.PageSize : 25),
                    cancellationToken)).ToHttpResult(page => Results.Ok(page)),
            () => Task.FromResult(ApiResults.Forbidden()));
    }

    private static async Task<IResult> GetDocumentFileAsync(
        string id,
        HttpContext http,
        CompanyContext company,
        IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>> files,
        CancellationToken cancellationToken) =>
        await (await DocumentsCustomerAsync(company, AccessAction.Download, cancellationToken)).Match(
            async customer =>
            {
                // Routing leaves an encoded "/" (%2F, as in ERP document ids) in the value.
                var file = await files.HandleAsync(new OpenDocumentFile(customer.Code, Uri.UnescapeDataString(id)), cancellationToken);
                return file.ToHttpResult(found =>
                {
                    http.Response.Headers.CacheControl = "private, no-store";
                    return Results.File(found.Content, "application/pdf", found.FileName);
                });
            },
            () => Task.FromResult(ApiResults.Forbidden()));

    private static async Task<IResult> GetReceivablesAsync(
        CompanyContext company,
        IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>> receivables,
        CancellationToken cancellationToken) =>
        await (await DocumentsCustomerAsync(company, AccessAction.Read, cancellationToken)).Match(
            async customer => Results.Ok(await receivables.HandleAsync(new GetReceivables(customer.Code), cancellationToken)),
            () => Task.FromResult(ApiResults.Forbidden()));

    /// <summary>The key's customer, when the access policies let its user see documents.</summary>
    private static async Task<Maybe<Customers.Contracts.CustomerDetails>> DocumentsCustomerAsync(CompanyContext company, AccessAction action, CancellationToken cancellationToken)
    {
        var active = await company.ActiveCustomerAsync(cancellationToken);
        var allowed = await active.Match(
            customer => company.CanAsync(action, (code, salesperson) => new Resource.CustomerDocuments(code, salesperson), customer, cancellationToken),
            () => Task.FromResult(false));
        return active.Where(_ => allowed);
    }
}

/// <summary>Query string of the document list.</summary>
/// <param name="Type">"invoice", "correction-invoice", "delivery-note" or "proforma".</param>
/// <param name="Status">"unpaid", "overdue" or "paid".</param>
/// <param name="From">Issued from this date (inclusive), ISO 8601.</param>
/// <param name="PageSize">1–100 (25 when absent).</param>
[NullableEdge]
internal sealed record DocumentSearch(
    [FromQuery(Name = "type")] string? Type,
    [FromQuery(Name = "status")] string? Status,
    [FromQuery(Name = "from")] DateOnly? From,
    [FromQuery(Name = "to")] DateOnly? To,
    [FromQuery(Name = "page")] int Page = 1,
    [FromQuery(Name = "pageSize")] int PageSize = 0);
