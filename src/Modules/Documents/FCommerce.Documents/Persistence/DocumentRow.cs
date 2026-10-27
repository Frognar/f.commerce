using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCommerce.Documents.Domain;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Documents.Persistence;

/// <summary>
/// Storage shape of a document: flat columns for filtering by type, dates and payment. <see cref="Outstanding"/> is
/// computed by the domain when written (an index for the filters). Mapping back to the domain does not check the rules
/// of the values again, but every column group must fit the type: amounts all or none (and always for invoices and
/// corrections), a file with both its size and time.
/// </summary>
internal sealed class DocumentRow
{
    public required string Id { get; init; }

    public long Version { get; set; }

    public required string CustomerCode { get; set; }

    public DocumentType Type { get; set; }

    public required string Number { get; set; }

    public DateOnly IssueDate { get; set; }

    public DateOnly? SaleDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string? Currency { get; set; }

    public decimal? Net { get; set; }

    public decimal? Gross { get; set; }

    public decimal? Paid { get; set; }

    public decimal Outstanding { get; set; }

    /// <summary>JSON array of the order numbers ("[]" without orders).</summary>
    public string OrderNumbers { get; set; } = "[]";

    /// <summary>JSON array of the lines ("[]" without lines).</summary>
    public string Lines { get; set; } = "[]";

    /// <summary>Products that can go to a cart, computed by the domain when written (for the list).</summary>
    public int ReorderableLines { get; set; }

    /// <summary>Size of the stored PDF; null (with <see cref="FileUpdatedAt"/>) while the ERP has not sent the file.</summary>
    public long? FileSize { get; set; }

    public DateTimeOffset? FileUpdatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether the ERP sent the PDF.</summary>
    public bool HasFile =>
        (FileSize, FileUpdatedAt) switch
        {
            (null, null) => false,
            ({ } size, { }) when size > 0 => true,
            _ => throw Stored.Corrupt($"document {Id} has a file size without its time (or the reverse)"),
        };

    /// <summary>Private blob key of the PDF: derived from the id, so ids with any characters make safe paths.</summary>
    public static string FileKey(DocumentId id)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value)));
        return $"documents/{hash[..2]}/{hash}.pdf";
    }

    public static DocumentRow From(Document document, DateTimeOffset now)
    {
        var row = new DocumentRow { Id = document.Id.Value, CustomerCode = string.Empty, Number = string.Empty };
        row.Apply(document, now);
        return row;
    }

    public void Apply(Document document, DateTimeOffset now)
    {
        var content = document.Content;
        var money = content.Kind.Money;
        Version = document.Version.Value;
        CustomerCode = content.CustomerCode.Value;
        Type = content.Type;
        Number = content.Number.Value;
        IssueDate = content.Dates.Issued;
        SaleDate = content.Dates.Sold.OrNull();
        DueDate = content.Dates.Due.OrNull();
        Currency = money.Map(a => a.Currency.Code).OrNull();
        Net = money.Map(a => a.Net).OrNull();
        Gross = money.Map(a => a.Gross).OrNull();
        Paid = money.Map(a => a.Paid.Value).OrNull();
        Outstanding = Domain.Documents.Outstanding(content);
        OrderNumbers = JsonSerializer.Serialize(content.OrderNumbers, PersistenceJson.Options);
        Lines = JsonSerializer.Serialize(content.Lines, PersistenceJson.Options);
        ReorderableLines = Domain.Documents.Reorderable(content).Items.Count;
        UpdatedAt = now;
    }

    /// <summary>The PDF of the document was stored.</summary>
    public void AttachFile(long size, DateTimeOffset at) => (FileSize, FileUpdatedAt) = (size, at);

    public Document ToDomain() =>
        new(
            Persisted.Rehydrate<DocumentId, string>(Id),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new DocumentContent(
                Persisted.Rehydrate<DocumentText, string>(CustomerCode),
                Kind(),
                Persisted.Rehydrate<DocumentText, string>(Number),
                DocumentDates.Create(IssueDate, SaleDate.ToMaybe(), DueDate.ToMaybe()).Match(dates => dates, error => throw Stored.Corrupt($"document {Id}: {error.Message}")),
                JsonSerializer.Deserialize<EquatableArray<DocumentText>>(OrderNumbers, PersistenceJson.Options),
                JsonSerializer.Deserialize<EquatableArray<DocumentLine>>(Lines, PersistenceJson.Options)));

    private DocumentKind Kind() =>
        (Type, Raw()) switch
        {
            (DocumentType.Invoice, { } raw) => new DocumentKind.Invoice(Plain(raw)),
            (DocumentType.CorrectionInvoice, { } raw) => new DocumentKind.CorrectionInvoice(new CorrectionAmounts(
                raw.Currency, Persisted.Rehydrate<Amount, decimal>(raw.Net), Persisted.Rehydrate<Amount, decimal>(raw.Gross), Persisted.Rehydrate<NonNegativeAmount, decimal>(raw.Paid))),
            (DocumentType.DeliveryNote, var raw) => new DocumentKind.DeliveryNote(raw.ToMaybe().Map(Plain)),
            (DocumentType.Proforma, var raw) => new DocumentKind.Proforma(raw.ToMaybe().Map(Plain)),
            _ => throw Stored.Corrupt($"{Type} {Id} without its amounts"),
        };

    private static Amounts Plain(RawAmounts raw) =>
        new(raw.Currency, Persisted.Rehydrate<NonNegativeAmount, decimal>(raw.Net), Persisted.Rehydrate<NonNegativeAmount, decimal>(raw.Gross), Persisted.Rehydrate<NonNegativeAmount, decimal>(raw.Paid));

    /// <summary>The amount columns: all of them or none.</summary>
    private RawAmounts? Raw() =>
        (Currency, Net, Gross, Paid) switch
        {
            (null, null, null, null) => null,
            ({ } currency, { } net, { } gross, { } paid) => new RawAmounts(Persisted.Rehydrate<Currency, string>(currency), net, gross, paid),
            _ => throw Stored.Corrupt($"document {Id} has only some of its amounts"),
        };

    private sealed record RawAmounts(Currency Currency, decimal Net, decimal Gross, decimal Paid);
}
