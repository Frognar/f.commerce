using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Application.Storage;
using FCommerce.Documents.Contracts;
using FCommerce.Documents.Domain;
using FCommerce.Documents.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Documents;

/// <summary>"Today" for payment statuses: the date in the seller's time zone.</summary>
internal sealed class DocumentsClock(TimeProvider time, DocumentsOptions options)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
}

/// <summary>Validate every item (pure) → reconcile with stored documents (pure) → store the changes.</summary>
internal sealed class ImportDocumentsHandler(DocumentsDbContext db, DocumentsClock clock) : ICommandHandler<ImportDocuments, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportDocuments command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(command.Documents, d => d.Id, d => Domain.Documents.Validate(ToUnvalidated(d)));

        var ids = batch.Valid.Select(v => v.Item.Id.Value).ToList();
        var rows = await db.Documents.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);

        var changes = batch.Valid
            .Select(v => Domain.Documents.Reconcile(rows.GetValueOrDefault(v.Item.Id.Value)?.ToDomain(), v.Item))
            .ToList();

        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<Document>.Created created:
                    db.Documents.Add(DocumentRow.From(created.Current, clock.Now));
                    break;
                case Change<Document>.Updated updated:
                    rows[updated.Current.Id.Value].Apply(updated.Current, clock.Now);
                    break;
                case Change<Document>.Unchanged unchanged:
                    rows[unchanged.Current.Id.Value].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ImportReport.From(command.Documents.Count, changes, batch.Rejected);
    }

    private static UnvalidatedDocument ToUnvalidated(DocumentData data) =>
        new(
            data.Id,
            data.Version,
            data.CustomerCode,
            data.Type,
            data.Number,
            data.IssueDate,
            data.SaleDate,
            data.DueDate,
            data.Currency,
            data.Net,
            data.Gross,
            data.Paid,
            data.OrderNumber);
}

/// <summary>The file is accepted only for a document whose metadata is already imported; the blob is written first.</summary>
internal sealed class StoreDocumentFileHandler(DocumentsDbContext db, IBlobStore blobs, DocumentsClock clock) : ICommandHandler<StoreDocumentFile, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(StoreDocumentFile command, CancellationToken cancellationToken = default) =>
        (from id in DocumentId.Parse(command.DocumentId)
         from _ in Domain.Documents.CheckFile(command.Content.Span)
         select id)
        .BindAsync(async id =>
        {
            var row = await db.Documents.SingleOrDefaultAsync(d => d.Id == id.Value, cancellationToken);
            if (row is null)
            {
                return Error.NotFound("document.not-found", $"Document '{id}' has not been imported.");
            }

            await blobs.PutAsync(DocumentRow.FileKey(id), command.Content, cancellationToken);
            row.FileSize = command.Content.Length;
            row.FileUpdatedAt = clock.Now;
            await db.SaveChangesAsync(cancellationToken);
            return Result.Ok<Unit, Error>(Unit.Value);
        });
}

internal sealed class GetCustomerDocumentsHandler(DocumentsDbContext db, DocumentsClock clock) : IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>>
{
    public const int MaxPageSize = 100;

    private static readonly DocumentType[] Payable = [.. Enum.GetValues<DocumentType>().Where(Domain.Documents.IsPayable)];

    public Task<Result<DocumentPage, Error>> HandleAsync(GetCustomerDocuments query, CancellationToken cancellationToken = default) =>
        (from type in Optional(query.Type, ParseType)
         from status in Optional(query.Status, ParseStatus)
         select (type, status))
        .MapAsync(filter => ListAsync(query, filter.type, filter.status, cancellationToken));

    private async Task<DocumentPage> ListAsync(GetCustomerDocuments query, DocumentType? type, PaymentStatus? status, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var today = clock.Today;

        var documents = db.Documents.AsNoTracking().Where(d => d.CustomerCode == query.CustomerCode);
        if (type is { } t)
        {
            documents = documents.Where(d => d.Type == t);
        }

        if (query.IssuedFrom is { } from)
        {
            documents = documents.Where(d => d.IssueDate >= from);
        }

        if (query.IssuedTo is { } to)
        {
            documents = documents.Where(d => d.IssueDate <= to);
        }

        // The status rules of Documents.StatusOn, as a query over the stored outstanding amount.
        documents = status switch
        {
            PaymentStatus.Paid => documents.Where(d => Payable.Contains(d.Type) && d.Outstanding == 0),
            PaymentStatus.Unpaid => documents.Where(d => d.Outstanding > 0 && (d.DueDate == null || d.DueDate >= today)),
            PaymentStatus.Overdue => documents.Where(d => d.Outstanding > 0 && d.DueDate < today),
            _ => documents,
        };

        var total = await documents.CountAsync(cancellationToken);
        var rows = await documents
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new DocumentPage([.. rows.Select(row => Summary(row, today))], page, pageSize, total);
    }

    private static DocumentSummary Summary(DocumentRow row, DateOnly today)
    {
        var document = row.ToDomain();
        var content = document.Content;
        return new DocumentSummary(
            document.Id.Value,
            Domain.Documents.TypeNames.First(n => n.Value == content.Type).Key,
            content.Number,
            content.Dates.Issued,
            content.Dates.Due,
            content.Amounts?.Currency.Code,
            content.Amounts?.Gross,
            row.Outstanding,
            StatusName(Domain.Documents.StatusOn(content, today)),
            content.OrderNumber,
            row.FileSize is not null);
    }

    private static Result<DocumentType, Error> ParseType(string raw) =>
        Domain.Documents.TypeNames.TryGetValue(raw, out var type)
            ? type
            : Error.Validation("type.unknown", $"Unknown document type '{raw}'.");

    private static Result<PaymentStatus, Error> ParseStatus(string raw) =>
        raw switch
        {
            DocumentStatuses.Paid => PaymentStatus.Paid,
            DocumentStatuses.Unpaid => PaymentStatus.Unpaid,
            DocumentStatuses.Overdue => PaymentStatus.Overdue,
            _ => Error.Validation("status.unknown", $"Unknown payment status '{raw}'."),
        };

    private static string StatusName(PaymentStatus status) =>
        status switch
        {
            PaymentStatus.Paid => DocumentStatuses.Paid,
            PaymentStatus.Unpaid => DocumentStatuses.Unpaid,
            PaymentStatus.Overdue => DocumentStatuses.Overdue,
            _ => DocumentStatuses.None,
        };

    private static Result<T?, Error> Optional<T>(string? raw, Func<string, Result<T, Error>> parse)
        where T : struct =>
        string.IsNullOrWhiteSpace(raw) ? Result.Ok<T?, Error>(null) : parse(raw.Trim()).Map(value => (T?)value);
}

internal sealed class GetReceivablesHandler(DocumentsDbContext db, DocumentsClock clock) : IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>>
{
    public async Task<IReadOnlyList<ReceivableView>> HandleAsync(GetReceivables query, CancellationToken cancellationToken = default)
    {
        var open = await db.Documents.AsNoTracking()
            .Where(d => d.CustomerCode == query.CustomerCode && d.Outstanding > 0 && d.Currency != null)
            .Select(d => new { d.Currency, d.Outstanding, d.DueDate })
            .ToListAsync(cancellationToken);

        return [.. Domain.Documents.Receivables(open.Select(d => new OpenItem(Persisted.Rehydrate<Currency, string>(d.Currency!), d.Outstanding, d.DueDate)), clock.Today)
            .Select(r => new ReceivableView(r.Currency.Code, r.Outstanding, r.Overdue, r.OverdueCount))];
    }
}

/// <summary>Only a document of the given customer, with a stored file, can be opened.</summary>
internal sealed class OpenDocumentFileHandler(DocumentsDbContext db, IBlobStore blobs) : IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>>
{
    public Task<Result<DocumentFile, Error>> HandleAsync(OpenDocumentFile query, CancellationToken cancellationToken = default) =>
        DocumentId.Parse(query.DocumentId).BindAsync(async id =>
        {
            var row = await db.Documents.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == id.Value && d.CustomerCode == query.CustomerCode && d.FileSize != null, cancellationToken);
            var stream = row is null ? null : await blobs.OpenReadAsync(DocumentRow.FileKey(id), cancellationToken);
            return stream is null
                ? Error.NotFound("document.not-found", "The document does not exist.")
                : Result.Ok<DocumentFile, Error>(new DocumentFile(stream, FileName(row!.Number)));
        });

    /// <summary>"FV/2026/10/001" → "FV-2026-10-001.pdf".</summary>
    private static string FileName(string number) =>
        string.Concat(number.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')).Trim('-', '.') is { Length: > 0 } name
            ? name + ".pdf"
            : "document.pdf";
}
