using System.Security.Cryptography;
using System.Text;
using FCommerce.Documents.Domain;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Documents.Persistence;

/// <summary>
/// Storage shape of a document: flat columns for filtering by type, dates and payment. <see cref="Outstanding"/> is
/// computed by the domain when written. Mapping back to the domain trusts stored data (it was validated when written).
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

    public string? OrderNumber { get; set; }

    /// <summary>JSON array of the lines ("[]" without lines).</summary>
    public string Lines { get; set; } = "[]";

    /// <summary>Products that can go to a cart, computed by the domain when written (for the list).</summary>
    public int ReorderableLines { get; set; }

    /// <summary>Size of the stored PDF; null while the ERP has not sent the file.</summary>
    public long? FileSize { get; set; }

    public DateTimeOffset? FileUpdatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

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
        Version = document.Version.Value;
        CustomerCode = content.CustomerCode;
        Type = content.Type;
        Number = content.Number;
        IssueDate = content.Dates.Issued;
        SaleDate = content.Dates.Sold.OrNull();
        DueDate = content.Dates.Due.OrNull();
        Currency = content.Amounts.Map(a => a.Currency.Code).OrNull();
        Net = content.Amounts.Map(a => a.Net).OrNull();
        Gross = content.Amounts.Map(a => a.Gross).OrNull();
        Paid = content.Amounts.Map(a => a.Paid).OrNull();
        Outstanding = Domain.Documents.Outstanding(content);
        OrderNumber = content.OrderNumber.OrNull();
        Lines = System.Text.Json.JsonSerializer.Serialize(content.Lines, PersistenceJson.Options);
        ReorderableLines = Domain.Documents.Reorderable(content).Items.Count;
        UpdatedAt = now;
    }

    public Document ToDomain() =>
        new(
            Persisted.Rehydrate<DocumentId, string>(Id),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new DocumentContent(
                CustomerCode,
                Type,
                Number,
                new DocumentDates(IssueDate, SaleDate.ToMaybe(), DueDate.ToMaybe()),
                Currency.ToMaybe().Map(code => new DocumentAmounts(Persisted.Rehydrate<SharedKernel.Monetary.Currency, string>(code), Net ?? 0m, Gross ?? 0m, Paid ?? 0m)),
                OrderNumber.ToMaybe(),
                System.Text.Json.JsonSerializer.Deserialize<SharedKernel.Collections.EquatableArray<DocumentLine>>(Lines, PersistenceJson.Options)));
}
