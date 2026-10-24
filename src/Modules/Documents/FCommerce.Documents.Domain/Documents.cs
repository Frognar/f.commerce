using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Documents.Domain;

public enum DocumentType
{
    Invoice,
    CorrectionInvoice,
    DeliveryNote,
    Proforma,
}

public enum PaymentStatus
{
    /// <summary>Not a document the customer pays (delivery note, proforma).</summary>
    NotApplicable,
    Paid,
    Unpaid,
    Overdue,
}

/// <summary>Document identifier from the ERP.</summary>
public sealed record DocumentId : IPersistedValue<DocumentId, string>
{
    public const int MaxLength = 64;

    private DocumentId(string value) => Value = value;

    public string Value { get; }

    public static Result<DocumentId, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "document-id", "Document id").Map(value => new DocumentId(value));

    static DocumentId IPersistedValue<DocumentId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<DocumentId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

public sealed record DocumentDates(DateOnly Issued, Maybe<DateOnly> Sold, Maybe<DateOnly> Due);

/// <param name="Paid">What the customer has paid so far, as the ERP knows it.</param>
public sealed record DocumentAmounts(Currency Currency, decimal Net, decimal Gross, decimal Paid);

/// <summary>A line of a document as the ERP printed it; the SKU is absent for services and free text lines.</summary>
/// <param name="Quantity">Negative on corrections that take goods back.</param>
public sealed record DocumentLine(int Number, Maybe<string> Sku, string Name, decimal Quantity, string Unit, Maybe<decimal> NetPrice, Maybe<decimal> NetAmount);

/// <param name="Amounts">Required for invoices and corrections; optional for other documents.</param>
/// <param name="OrderNumber">The order the document belongs to, if any.</param>
/// <param name="Lines">The lines, when the ERP sends them (for ordering the same again).</param>
public sealed record DocumentContent(
    string CustomerCode,
    DocumentType Type,
    string Number,
    DocumentDates Dates,
    Maybe<DocumentAmounts> Amounts,
    Maybe<string> OrderNumber,
    EquatableArray<DocumentLine> Lines);

/// <summary>A customer's document (invoice, correction, delivery note, proforma) as replicated from the ERP.</summary>
public sealed record Document(DocumentId Id, SourceVersion Version, DocumentContent Content);

/// <summary>A document as received from the ERP; missing required texts arrive as empty, optional fields are absent.</summary>
public sealed record UnvalidatedDocument(
    string Id,
    long Version,
    string CustomerCode,
    string Type,
    string Number,
    Maybe<DateOnly> IssueDate,
    Maybe<DateOnly> SaleDate,
    Maybe<DateOnly> DueDate,
    Maybe<string> Currency,
    Maybe<decimal> Net,
    Maybe<decimal> Gross,
    Maybe<decimal> Paid,
    Maybe<string> OrderNumber,
    EquatableArray<UnvalidatedDocumentLine> Lines = default);

public sealed record UnvalidatedDocumentLine(int Number, Maybe<string> Sku, string Name, decimal Quantity, string Unit, Maybe<decimal> NetPrice, Maybe<decimal> NetAmount);

/// <summary>What of a document can be ordered again: products with a positive quantity, summed per SKU.</summary>
/// <param name="Skipped">Lines with a positive quantity but without a SKU (services, free text).</param>
public sealed record ReorderableItems(EquatableArray<(string Sku, decimal Quantity)> Items, int Skipped);

/// <summary>Money still due on one document (see <see cref="Documents.Outstanding"/>).</summary>
public sealed record OpenItem(Currency Currency, decimal Outstanding, Maybe<DateOnly> Due);

/// <summary>What a customer owes in one currency, and how much of it is past its due date.</summary>
public sealed record Receivable(Currency Currency, decimal Outstanding, decimal Overdue, int OverdueCount);

public static class Documents
{
    public const int MaxNumber = 64;
    public const int MaxCustomerCode = 64;
    public const int MaxOrderNumber = 64;
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>Amounts are bounded well below what the storage column holds.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    public const int MaxLines = 500;
    public const int MaxLineName = 300;
    public const int MaxUnit = 20;
    public const int MaxSku = 64;
    public const decimal MaxQuantity = 999_999_999_999.999m;

    /// <summary>Names used in the integration API and in filters.</summary>
    public static IReadOnlyDictionary<string, DocumentType> TypeNames { get; } = new Dictionary<string, DocumentType>(StringComparer.OrdinalIgnoreCase)
    {
        ["invoice"] = DocumentType.Invoice,
        ["correction-invoice"] = DocumentType.CorrectionInvoice,
        ["delivery-note"] = DocumentType.DeliveryNote,
        ["proforma"] = DocumentType.Proforma,
    };

    private static readonly Func<DocumentId, SourceVersion, DocumentContent, Document> Create =
        (id, version, content) => new Document(id, version, content);

    private static readonly Func<string, DocumentType, string, DocumentDates, Maybe<DocumentAmounts>, Maybe<string>, EquatableArray<DocumentLine>, DocumentContent> CreateContent =
        (customer, type, number, dates, amounts, order, lines) => new DocumentContent(customer, type, number, dates, amounts, order, lines);

    /// <summary>Validates a document from the ERP, reporting every problem with its field path.</summary>
    public static Validated<Document> Validate(UnvalidatedDocument input) =>
        Create
            .Apply(DocumentId.Parse(input.Id).ToValidated("id"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(SourceCodes.Parse(input.CustomerCode, MaxCustomerCode, "customer-code", "Customer code").ToValidated("customerCode"))
                .Apply(ParseType(input.Type).ToValidated("type"))
                .Apply(SourceCodes.Parse(input.Number, MaxNumber, "number", "Document number").ToValidated("number"))
                .Apply(ValidateDates(input))
                .Apply(ValidateAmounts(input))
                .Apply(SourceCodes.ParseOptional(input.OrderNumber, MaxOrderNumber, "order-number", "Order number").ToValidated("orderNumber"))
                .Apply(ValidateLines(input.Lines))
                .EnsureAll(
                    (content => content.Amounts.Match(_ => true, () => !IsPayable(content.Type)), Error.Validation("amounts.missing", "Invoices and corrections need their amounts.") with { Field = "gross" }),
                    (content => content.Type == DocumentType.CorrectionInvoice || content.Amounts.Match(amounts => amounts.Net >= 0 && amounts.Gross >= 0, () => true), Error.Validation("amount.negative", "Only a correction may have negative amounts.") with { Field = "gross" })));

    public static Change<Document> Reconcile(Maybe<Document> stored, Document incoming) =>
        ImportBatch.Reconcile(stored, incoming, d => d.Version, (a, b) => a.Content == b.Content);

    /// <summary>Products of the document with a positive quantity, summed per SKU in the order of their first line.</summary>
    public static ReorderableItems Reorderable(DocumentContent content)
    {
        var positive = content.Lines.Where(line => line.Quantity > 0).ToList();
        return new ReorderableItems(
            positive
                .Choose(line => line.Sku.Map(sku => (Sku: sku, line.Quantity)))
                .GroupBy(line => line.Sku)
                .Select(group => (group.Key, group.Sum(line => line.Quantity)))
                .ToEquatableArray(),
            positive.Count(line => !line.Sku.Exists(_ => true)));
    }

    /// <summary>Invoices and corrections are paid; other documents only inform.</summary>
    public static bool IsPayable(DocumentType type) => type is DocumentType.Invoice or DocumentType.CorrectionInvoice;

    /// <summary>
    /// What the customer still has to pay: gross less paid, never below zero. A correction that lowers the amount
    /// owes the customer money, so nothing is due on it.
    /// </summary>
    public static decimal Outstanding(DocumentContent content) =>
        IsPayable(content.Type) ? content.Amounts.Match(amounts => Math.Max(amounts.Gross - amounts.Paid, 0m), () => 0m) : 0m;

    /// <summary>The due date itself is still on time; the day after, an unpaid document is overdue.</summary>
    public static bool IsOverdue(Maybe<DateOnly> due, DateOnly today) => due.Exists(date => date < today);

    public static PaymentStatus StatusOn(DocumentContent content, DateOnly today) =>
        (IsPayable(content.Type), Outstanding(content)) switch
        {
            (false, _) => PaymentStatus.NotApplicable,
            (true, 0m) => PaymentStatus.Paid,
            _ when IsOverdue(content.Dates.Due, today) => PaymentStatus.Overdue,
            _ => PaymentStatus.Unpaid,
        };

    /// <summary>Sums what is due per currency (currencies in alphabetical order).</summary>
    public static IReadOnlyList<Receivable> Receivables(IEnumerable<OpenItem> items, DateOnly today) =>
        [.. items
            .Where(item => item.Outstanding > 0)
            .GroupBy(item => item.Currency)
            .OrderBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(group => new Receivable(
                group.Key,
                group.Sum(item => item.Outstanding),
                group.Where(item => IsOverdue(item.Due, today)).Sum(item => item.Outstanding),
                group.Count(item => IsOverdue(item.Due, today))))];

    /// <summary>A document file is a non-empty PDF of bounded size.</summary>
    public static Result<Unit, Error> CheckFile(ReadOnlySpan<byte> content) =>
        content.Length switch
        {
            0 => Error.Validation("file.empty", "The file is empty."),
            > MaxFileBytes => Error.Validation("file.too-large", $"The file must not exceed {MaxFileBytes / (1024 * 1024)} MB."),
            _ when !content.StartsWith("%PDF-"u8) => Error.Validation("file.not-pdf", "The file is not a PDF document."),
            _ => Unit.Value,
        };

    private static Validated<EquatableArray<DocumentLine>> ValidateLines(EquatableArray<UnvalidatedDocumentLine> lines) =>
        lines.Count > MaxLines
            ? Validated.Invalid<EquatableArray<DocumentLine>>(Error.Validation("lines.too-many", $"At most {MaxLines} lines.") with { Field = "lines" })
            : lines.Traverse(ValidateLine).Map(valid => valid.ToEquatableArray()).AtField("lines");

    private static Validated<DocumentLine> ValidateLine(UnvalidatedDocumentLine line) =>
        ((Func<int, Maybe<string>, string, decimal, string, Maybe<decimal>, Maybe<decimal>, DocumentLine>)((number, sku, name, quantity, unit, price, amount) =>
            new DocumentLine(number, sku, name, quantity, unit, price, amount)))
            .Apply((line.Number > 0 ? Result.Ok<int, Error>(line.Number) : Error.Validation("line-number.invalid", "Line numbers start at 1.")).ToValidated("number"))
            .Apply(SourceCodes.ParseOptional(line.Sku, MaxSku, "sku", "SKU").ToValidated("sku"))
            .Apply(SourceCodes.Parse(line.Name, MaxLineName, "line-name", "Line name").ToValidated("name"))
            .Apply((Math.Abs(line.Quantity) > MaxQuantity ? Error.Validation("quantity.too-large", "The quantity is too large.")
                : decimal.Round(line.Quantity, 3) != line.Quantity ? Error.Validation("quantity.precision", "Quantities have at most 3 decimal places.")
                : Result.Ok<decimal, Error>(line.Quantity)).ToValidated("quantity"))
            .Apply(SourceCodes.Parse(line.Unit, MaxUnit, "unit", "Unit").ToValidated("unit"))
            .Apply(line.NetPrice.Match(
                price => (price < 0 || price >= MaxAmount ? Error.Validation("net-price.invalid", "The net price must be zero or more.")
                    : decimal.Round(price, 4) != price ? Error.Validation("net-price.precision", "Net prices have at most 4 decimal places.")
                    : Result.Ok<Maybe<decimal>, Error>(price)).ToValidated("netPrice"),
                () => Validated.Valid(Maybe<decimal>.None)))
            .Apply(line.NetAmount.Match(amount => Amount(amount).Map(Maybe.Some).ToValidated("netAmount"), () => Validated.Valid(Maybe<decimal>.None)));

    private static Result<DocumentType, Error> ParseType(string raw) =>
        TypeNames.TryGetValue(raw.Trim(), out var type)
            ? type
            : Error.Validation("type.unknown", $"Unknown document type '{raw}'; expected one of: {string.Join(", ", TypeNames.Keys)}.");

    private static Validated<DocumentDates> ValidateDates(UnvalidatedDocument input) =>
        input.IssueDate.Match(
                issued => Validated.Valid(new DocumentDates(issued, input.SaleDate, input.DueDate)),
                () => Validated.Invalid<DocumentDates>(Error.Validation("issue-date.missing", "The issue date is required.") with { Field = "issueDate" }))
            .Ensure(dates => !dates.Due.Exists(due => due < dates.Issued), _ => Error.Validation("due-date.before-issue", "The due date must not be before the issue date.") with { Field = "dueDate" });

    /// <summary>No amount given at all means "no amounts"; otherwise currency, net and gross are required (paid defaults to zero).</summary>
    private static Validated<Maybe<DocumentAmounts>> ValidateAmounts(UnvalidatedDocument input) =>
        Given(input.Currency) || Given(input.Net) || Given(input.Gross) || Given(input.Paid)
            ? ((Func<Currency, decimal, decimal, decimal, Maybe<DocumentAmounts>>)((currency, net, gross, paid) => new DocumentAmounts(currency, net, gross, paid)))
                .Apply(Currency.Parse(input.Currency.ValueOr(string.Empty)).ToValidated("currency"))
                .Apply(Amount(input.Net).ToValidated("net"))
                .Apply(Amount(input.Gross).ToValidated("gross"))
                .Apply(Amount(input.Paid.Or(0m)).ToValidated("paid"))
            : Validated.Valid(Maybe<DocumentAmounts>.None);

    private static bool Given<T>(Maybe<T> raw)
        where T : notnull =>
        raw.Match(_ => true, () => false);

    private static Result<decimal, Error> Amount(Maybe<decimal> raw) =>
        raw.Match(
            value => Math.Abs(value) >= MaxAmount ? Error.Validation("amount.too-large", "The amount is too large.")
                : Math.Round(value, 2) != value ? Error.Validation("amount.precision", "Amounts have at most two decimal places.")
                : Result.Ok<decimal, Error>(value),
            () => Error.Validation("amount.missing", "The amount is required."));
}
