using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Application.Storage;
using FCommerce.Documents.Contracts;
using FCommerce.Documents.Domain;
using FCommerce.Documents.Persistence;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Documents;

/// <summary>"Today" for payment statuses: the date in the seller's time zone.</summary>
internal sealed class DocumentsClock(TimeProvider time, DocumentsOptions options)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
}

/// <summary>Validate every item (pure) → reconcile with stored documents (pure) → store the changes and their events.</summary>
internal sealed class ImportDocumentsHandler(DocumentsDbContext db, Outbox<DocumentsDbContext> outbox, DocumentsClock clock) : ICommandHandler<ImportDocuments, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportDocuments command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(command.Documents, d => d.Id.ToMaybe(), d => Domain.Documents.Validate(ToUnvalidated(d)));

        var ids = batch.Valid.Select(v => v.Item.Id.Value).ToList();
        var rows = await db.Documents.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);

        var changes = batch.Valid
            .Select(v => Domain.Documents.Reconcile(rows.GetValueOrNone(v.Item.Id.Value).Map(row => row.ToDomain()), v.Item))
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

        return await outbox.EnqueueAll(changes.Choose(Changed))
            .MapAsync(async _ =>
            {
                await db.SaveChangesAsync(cancellationToken);
                return ImportReport.From(command.Documents.Count, changes, batch.Rejected);
            });
    }

    private static Maybe<IIntegrationEvent> Changed(Change<Document> change) =>
        change switch
        {
            Change<Document>.Created created => Maybe.Some<IIntegrationEvent>(Event(created.Current)),
            Change<Document>.Updated updated => Maybe.Some<IIntegrationEvent>(Event(updated.Current)),
            _ => Maybe<IIntegrationEvent>.None,
        };

    private static DocumentChanged Event(Document document) =>
        new DocumentChanged(
            document.Id.Value,
            document.Content.CustomerCode,
            Domain.Documents.TypeNames.First(n => n.Value == document.Content.Type).Key,
            document.Content.Number,
            document.Content.Dates.Issued,
            [.. document.Content.OrderNumbers]);

    private static UnvalidatedDocument ToUnvalidated(DocumentData data) =>
        new(
            data.Id.OrEmpty(),
            data.Version,
            data.CustomerCode.OrEmpty(),
            data.Type.OrEmpty(),
            data.Number.OrEmpty(),
            data.IssueDate.ToMaybe(),
            data.SaleDate.ToMaybe(),
            data.DueDate.ToMaybe(),
            data.Currency.ToMaybe(),
            data.Net.ToMaybe(),
            data.Gross.ToMaybe(),
            data.Paid.ToMaybe(),
            data.OrderNumber.ToMaybe(),
            [.. data.Lines.OrEmpty().Select(line => new UnvalidatedDocumentLine(
                line.Number, line.Sku.ToMaybe(), line.Name.OrEmpty(), line.Quantity, line.Unit.OrEmpty(), line.NetPrice.ToMaybe(), line.NetAmount.ToMaybe()))],
            [.. data.OrderNumbers.OrEmpty().Select(number => number.OrEmpty())]);
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
            var row = await db.Documents.SingleOrNoneAsync(d => d.Id == id.Value, cancellationToken);
            return await row.Match(
                async stored =>
                {
                    await blobs.PutAsync(DocumentRow.FileKey(id), command.Content, cancellationToken);
                    stored.FileSize = command.Content.Length;
                    stored.FileUpdatedAt = clock.Now;
                    await db.SaveChangesAsync(cancellationToken);
                    return Result.Ok<Unit, Error>(Unit.Value);
                },
                () => Task.FromResult(Result.Fail<Unit, Error>(Error.NotFound("document.not-found", $"Document '{id}' has not been imported."))));
        });
}

internal sealed class GetCustomerDocumentsHandler(DocumentsDbContext db, DocumentsClock clock) : IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>>
{
    public const int MaxPageSize = 100;

    private static readonly DocumentType[] Payable = [.. Enum.GetValues<DocumentType>().Where(Domain.Documents.IsPayable)];

    public Task<Result<DocumentPage, Error>> HandleAsync(GetCustomerDocuments query, CancellationToken cancellationToken = default) =>
        (from type in Maybe.ParseOptional(query.Type.ToMaybe(), raw => ParseType(raw.Trim()))
         from status in Maybe.ParseOptional(query.Status.ToMaybe(), raw => ParseStatus(raw.Trim()))
         select (type, status))
        .MapAsync(filter => ListAsync(query, filter.type, filter.status, cancellationToken));

    private async Task<DocumentPage> ListAsync(GetCustomerDocuments query, Maybe<DocumentType> type, Maybe<PaymentStatus> status, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var today = clock.Today;

        var documents = db.Documents.AsNoTracking().Where(d => d.CustomerCode == query.CustomerCode);
        documents = type.Match(t => documents.Where(d => d.Type == t), () => documents);
        documents = query.IssuedFrom.ToMaybe().Match(from => documents.Where(d => d.IssueDate >= from), () => documents);
        documents = query.IssuedTo.ToMaybe().Match(to => documents.Where(d => d.IssueDate <= to), () => documents);

        // The status rules of Documents.StatusOn, as a query over the stored outstanding amount (row columns may be null).
        documents = status.Match(
            s => s switch
            {
                PaymentStatus.Paid => documents.Where(d => Payable.Contains(d.Type) && d.Outstanding == 0),
                PaymentStatus.Unpaid => documents.Where(d => d.Outstanding > 0 && (d.DueDate == null || d.DueDate >= today)),
                PaymentStatus.Overdue => documents.Where(d => d.Outstanding > 0 && d.DueDate < today),
                _ => documents,
            },
            () => documents);

        var total = await documents.CountAsync(cancellationToken);
        var rows = await documents
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new DocumentPage([.. rows.Select(row => Summary(row, today))], page, pageSize, total);
    }

    public static DocumentSummary Summary(DocumentRow row, DateOnly today)
    {
        var document = row.ToDomain();
        var content = document.Content;
        return new DocumentSummary(
            document.Id.Value,
            Domain.Documents.TypeNames.First(n => n.Value == content.Type).Key,
            content.Number,
            content.Dates.Issued,
            content.Dates.Due.OrNull(),
            content.Amounts.Map(a => a.Currency.Code).OrNull(),
            content.Amounts.Map(a => a.Gross).OrNull(),
            row.Outstanding,
            StatusName(Domain.Documents.StatusOn(content, today)),
            content.OrderNumbers.IsEmpty ? null : string.Join(", ", content.OrderNumbers),
            row.FileSize is not null,
            row.ReorderableLines,
            [.. content.OrderNumbers]);
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
}

internal sealed class GetOrderDocumentsHandler(DocumentsDbContext db, DocumentsClock clock) : IQueryHandler<GetOrderDocuments, IReadOnlyList<DocumentSummary>>
{
    public async Task<IReadOnlyList<DocumentSummary>> HandleAsync(GetOrderDocuments query, CancellationToken cancellationToken = default)
    {
        var ids = query.DocumentIds.OrEmpty().Take(GetOrderDocuments.MaxDocuments).ToList();
        var rows = ids.Count == 0
            ? []
            : await db.Documents.AsNoTracking()
                .Where(d => d.CustomerCode == query.CustomerCode && ids.Contains(d.Id))
                .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number)
                .ToListAsync(cancellationToken);
        var today = clock.Today;
        return [.. rows.Select(row => GetCustomerDocumentsHandler.Summary(row, today))];
    }
}

internal sealed class GetReceivablesHandler(DocumentsDbContext db, DocumentsClock clock) : IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>>
{
    public async Task<IReadOnlyList<ReceivableView>> HandleAsync(GetReceivables query, CancellationToken cancellationToken = default)
    {
        var open = await db.Documents.AsNoTracking()
            .Where(d => d.CustomerCode == query.CustomerCode && d.Outstanding > 0 && d.Currency != null)
            .Select(d => new { d.Currency, d.Outstanding, d.DueDate })
            .ToListAsync(cancellationToken);

        return [.. Domain.Documents.Receivables(open.Choose(d => d.Currency.ToMaybe().Map(code => new OpenItem(Persisted.Rehydrate<Currency, string>(code), d.Outstanding, d.DueDate.ToMaybe()))), clock.Today)
            .Select(r => new ReceivableView(r.Currency.Code, r.Outstanding, r.Overdue, r.OverdueCount))];
    }
}

/// <summary>The lines of a document of the given customer and what of them can be ordered again.</summary>
internal sealed class GetDocumentLinesHandler(DocumentsDbContext db) : IQueryHandler<GetDocumentLines, Result<DocumentLines, Error>>
{
    public Task<Result<DocumentLines, Error>> HandleAsync(GetDocumentLines query, CancellationToken cancellationToken = default) =>
        DocumentId.Parse(query.DocumentId.OrEmpty()).BindAsync(async id =>
            (await db.Documents.AsNoTracking().SingleOrNoneAsync(d => d.Id == id.Value && d.CustomerCode == query.CustomerCode, cancellationToken))
                .Map(row =>
                {
                    var content = row.ToDomain().Content;
                    var reorderable = Domain.Documents.Reorderable(content);
                    return new DocumentLines(
                        content.Number,
                        [.. content.Lines.Select(l => new DocumentLineView(l.Number, l.Sku.OrNull(), l.Name, l.Quantity, l.Unit, l.NetPrice.OrNull(), l.NetAmount.OrNull()))],
                        [.. reorderable.Items.Select(i => new ReorderableLine(i.Sku, i.Quantity))],
                        reorderable.Skipped);
                })
                .ToResult(() => Error.NotFound("document.not-found", "The document does not exist.")));
}

/// <summary>Only a document of the given customer, with a stored file, can be opened.</summary>
internal sealed class OpenDocumentFileHandler(DocumentsDbContext db, IBlobStore blobs) : IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>>
{
    public Task<Result<DocumentFile, Error>> HandleAsync(OpenDocumentFile query, CancellationToken cancellationToken = default) =>
        DocumentId.Parse(query.DocumentId).BindAsync(async id =>
        {
            var row = await db.Documents.AsNoTracking()
                .SingleOrNoneAsync(d => d.Id == id.Value && d.CustomerCode == query.CustomerCode && d.FileSize != null, cancellationToken);
            var file = await row.Match(
                async stored => (await blobs.OpenReadAsync(DocumentRow.FileKey(id), cancellationToken)).Map(stream => new DocumentFile(stream, FileName(stored.Number))),
                () => Task.FromResult(Maybe<DocumentFile>.None));
            return file.ToResult(() => Error.NotFound("document.not-found", "The document does not exist."));
        });

    /// <summary>"FV/2026/10/001" → "FV-2026-10-001.pdf".</summary>
    private static string FileName(string number) =>
        string.Concat(number.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')).Trim('-', '.') is { Length: > 0 } name
            ? name + ".pdf"
            : "document.pdf";
}
