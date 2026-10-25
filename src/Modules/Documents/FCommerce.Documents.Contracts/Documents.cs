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
/// <param name="OrderNumber">The order the document belongs to, if any (platform or ERP number).</param>
/// <param name="Lines">The lines (optional): buyers can put the products of a document into their cart.</param>
/// <param name="OrderNumbers">More orders, for a document of several orders (merged with <paramref name="OrderNumber"/>).</param>
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
    string? OrderNumber = null,
    IReadOnlyList<DocumentLineData>? Lines = null,
    IReadOnlyList<string>? OrderNumbers = null);

/// <param name="Number">Position on the document, from 1.</param>
/// <param name="Sku">The product (ERP index); absent for services and free text lines.</param>
/// <param name="Quantity">At most 3 decimal places; negative on corrections that take goods back.</param>
/// <param name="NetPrice">Unit net price (at most 4 decimal places).</param>
/// <param name="NetAmount">Net value of the line (at most 2 decimal places).</param>
public sealed record DocumentLineData(int Number, string? Sku, string Name, decimal Quantity, string Unit, decimal? NetPrice = null, decimal? NetAmount = null);

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

/// <summary>The lines of a document of this customer; not found for other customers' documents.</summary>
public sealed record GetDocumentLines(string CustomerCode, string DocumentId) : IQuery<Result<DocumentLines, Error>>;

/// <param name="Reorderable">Products with a positive quantity, summed per SKU (what can go to a cart).</param>
/// <param name="SkippedLines">Lines with a positive quantity but no SKU (services, free text).</param>
public sealed record DocumentLines(string Number, IReadOnlyList<DocumentLineView> Lines, IReadOnlyList<ReorderableLine> Reorderable, int SkippedLines);

public sealed record DocumentLineView(int Number, string? Sku, string Name, decimal Quantity, string Unit, decimal? NetPrice, decimal? NetAmount);

public sealed record ReorderableLine(string Sku, decimal Quantity);

/// <summary>Opens the PDF of a document of this customer; not found for other customers' documents.</summary>
public sealed record OpenDocumentFile(string CustomerCode, string DocumentId) : IQuery<Result<DocumentFile, Error>>;

/// <summary>Documents of the customer with these ids (the ones linked to an order), newest first; unknown ids are left out.</summary>
public sealed record GetOrderDocuments(string CustomerCode, IReadOnlyList<string> DocumentIds) : IQuery<IReadOnlyList<DocumentSummary>>
{
    public const int MaxDocuments = 100;
}

public sealed record DocumentPage(IReadOnlyList<DocumentSummary> Items, int Page, int PageSize, int TotalCount)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <param name="Status">"paid", "unpaid", "overdue", or "none" for documents that are not paid (delivery notes, proformas).</param>
/// <param name="OrderNumber">The order numbers, separated by commas.</param>
/// <param name="ReorderableLines">Products of the document that can go to a cart (none without lines).</param>
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
    bool HasFile,
    int ReorderableLines = 0,
    IReadOnlyList<string>? OrderNumbers = null);

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
