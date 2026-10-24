using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Documents.Contracts;

/// <summary>
/// Upserts document metadata sent by the ERP connector; items are validated independently and items not newer than
/// the stored version are ignored. Files are uploaded separately (<see cref="StoreDocumentFile"/>).
/// </summary>
public sealed record ImportDocuments(IReadOnlyList<DocumentData> Documents) : ICommand<Result<ImportReport, Error>>;

/// <param name="Id">ERP id of the document; identifies it.</param>
/// <param name="Version">Monotonic version from the ERP (row version or change timestamp).</param>
/// <param name="CustomerCode">ERP code of the customer the document was issued to.</param>
/// <param name="Type">"invoice", "correction-invoice", "delivery-note" or "proforma".</param>
/// <param name="Currency">ISO 4217 code; with net and gross required for invoices and corrections.</param>
/// <param name="Paid">Amount paid so far (zero when absent).</param>
/// <param name="OrderNumber">The order the document belongs to, if any.</param>
public sealed record DocumentData(
    string Id,
    long Version,
    string CustomerCode,
    string Type,
    string Number,
    DateOnly? IssueDate,
    DateOnly? SaleDate = null,
    DateOnly? DueDate = null,
    string? Currency = null,
    decimal? Net = null,
    decimal? Gross = null,
    decimal? Paid = null,
    string? OrderNumber = null);

/// <summary>Stores the PDF of an imported document, replacing a previous one.</summary>
public sealed record StoreDocumentFile(string DocumentId, ReadOnlyMemory<byte> Content) : ICommand<Result<Unit, Error>>;

/// <summary>
/// A customer's documents, newest first. Filters: <paramref name="Type"/> (see <see cref="DocumentData.Type"/>),
/// issue dates from/to (inclusive) and <paramref name="Status"/> ("paid", "unpaid", "overdue").
/// </summary>
public sealed record GetCustomerDocuments(
    string CustomerCode,
    string? Type = null,
    DateOnly? IssuedFrom = null,
    DateOnly? IssuedTo = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 25)
    : IQuery<Result<DocumentPage, Error>>;

/// <summary>What the customer owes per currency.</summary>
public sealed record GetReceivables(string CustomerCode) : IQuery<IReadOnlyList<ReceivableView>>;

/// <summary>Opens the PDF of a document of this customer; not found for other customers' documents.</summary>
public sealed record OpenDocumentFile(string CustomerCode, string DocumentId) : IQuery<Result<DocumentFile, Error>>;

public sealed record DocumentPage(IReadOnlyList<DocumentSummary> Items, int Page, int PageSize, int TotalCount)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <param name="Status">"paid", "unpaid", "overdue", or "none" for documents that are not paid (delivery notes, proformas).</param>
public sealed record DocumentSummary(
    string Id,
    string Type,
    string Number,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string? Currency,
    decimal? Gross,
    decimal Outstanding,
    string Status,
    string? OrderNumber,
    bool HasFile);

public sealed record ReceivableView(string Currency, decimal Outstanding, decimal Overdue, int OverdueCount);

/// <param name="FileName">A file name for the download, from the document number.</param>
public sealed record DocumentFile(Stream Content, string FileName);

public static class DocumentStatuses
{
    public const string Paid = "paid";
    public const string Unpaid = "unpaid";
    public const string Overdue = "overdue";
    public const string None = "none";
}
