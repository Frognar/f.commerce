using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Documents.Domain;

/// <summary>The dates of a document: the due date never precedes the issue date.</summary>
public sealed record DocumentDates
{
    private DocumentDates(DateOnly issued, Maybe<DateOnly> sold, Maybe<DateOnly> due)
    {
        Issued = issued;
        Sold = sold;
        Due = due;
    }

    public DateOnly Issued { get; }

    public Maybe<DateOnly> Sold { get; }

    public Maybe<DateOnly> Due { get; }

    public static Result<DocumentDates, Error> Create(DateOnly issued, Maybe<DateOnly> sold, Maybe<DateOnly> due) =>
        due.Exists(date => date < issued)
            ? Error.Validation("due-date.before-issue", "The due date must not be before the issue date.") with { Field = "dueDate" }
            : new DocumentDates(issued, sold, due);
}

/// <summary>What the customer is to pay for a document and has paid, as the ERP knows it.</summary>
public interface IDocumentAmounts
{
    Currency Currency { get; }

    decimal Net { get; }

    decimal Gross { get; }

    NonNegativeAmount Paid { get; }
}

/// <summary>The amounts of an ordinary document: never negative.</summary>
public sealed record Amounts(Currency Currency, NonNegativeAmount NetAmount, NonNegativeAmount GrossAmount, NonNegativeAmount Paid) : IDocumentAmounts
{
    public decimal Net => NetAmount.Value;

    public decimal Gross => GrossAmount.Value;
}

/// <summary>The amounts of a correction: lowering the value makes them negative (what was paid is still not).</summary>
public sealed record CorrectionAmounts(Currency Currency, Amount NetAmount, Amount GrossAmount, NonNegativeAmount Paid) : IDocumentAmounts
{
    public decimal Net => NetAmount.Value;

    public decimal Gross => GrossAmount.Value;
}

public enum DocumentType
{
    Invoice,
    CorrectionInvoice,
    DeliveryNote,
    Proforma,
}

/// <summary>What a document is: invoices and corrections are paid and always have their amounts; other documents only inform.</summary>
public abstract record DocumentKind
{
    private DocumentKind()
    {
    }

    public abstract DocumentType Type { get; }

    /// <summary>The amounts, when the document has them.</summary>
    public abstract Maybe<IDocumentAmounts> Money { get; }

    public sealed record Invoice(Amounts Amounts) : DocumentKind
    {
        public override DocumentType Type => DocumentType.Invoice;

        public override Maybe<IDocumentAmounts> Money => Amounts;
    }

    public sealed record CorrectionInvoice(CorrectionAmounts Amounts) : DocumentKind
    {
        public override DocumentType Type => DocumentType.CorrectionInvoice;

        public override Maybe<IDocumentAmounts> Money => Amounts;
    }

    public sealed record DeliveryNote(Maybe<Amounts> Amounts) : DocumentKind
    {
        public override DocumentType Type => DocumentType.DeliveryNote;

        public override Maybe<IDocumentAmounts> Money => Amounts.Map(a => (IDocumentAmounts)a);
    }

    public sealed record Proforma(Maybe<Amounts> Amounts) : DocumentKind
    {
        public override DocumentType Type => DocumentType.Proforma;

        public override Maybe<IDocumentAmounts> Money => Amounts.Map(a => (IDocumentAmounts)a);
    }
}

/// <summary>Payment of an invoice or a correction (other documents are not paid).</summary>
public enum PaymentStatus
{
    Paid,
    Unpaid,
    Overdue,
}

/// <summary>A line of a document as the ERP printed it; the SKU is absent for services and free text lines.</summary>
/// <param name="Quantity">Negative on corrections that take goods back.</param>
public sealed record DocumentLine(LineNumber Number, Maybe<DocumentText> Sku, DocumentText Name, DocumentQuantity Quantity, DocumentText Unit, Maybe<LinePrice> NetPrice, Maybe<Amount> NetAmount);

/// <param name="OrderNumbers">The orders the document belongs to (platform or ERP numbers), in ERP order, without repeats.</param>
/// <param name="Lines">The lines, when the ERP sends them (for ordering the same again).</param>
public sealed record DocumentContent(
    DocumentText CustomerCode,
    DocumentKind Kind,
    DocumentText Number,
    DocumentDates Dates,
    EquatableArray<DocumentText> OrderNumbers,
    EquatableArray<DocumentLine> Lines)
{
    public DocumentType Type => Kind.Type;
}

/// <summary>A customer's document (invoice, correction, delivery note, proforma) as replicated from the ERP.</summary>
public sealed record Document(DocumentId Id, SourceVersion Version, DocumentContent Content);

/// <summary>A document as received from the ERP; missing required texts arrive as empty, optional fields are absent.</summary>
[RawInput]
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
    EquatableArray<UnvalidatedDocumentLine> Lines = default,
    EquatableArray<string> OrderNumbers = default);

[RawInput]
public sealed record UnvalidatedDocumentLine(int Number, Maybe<string> Sku, string Name, decimal Quantity, string Unit, Maybe<decimal> NetPrice, Maybe<decimal> NetAmount);

/// <summary>A product of a document to order again: the summed positive quantity of its lines.</summary>
public sealed record ReorderableItem
{
    internal ReorderableItem(DocumentText sku, decimal quantity)
    {
        Sku = sku;
        Quantity = quantity;
    }

    public DocumentText Sku { get; }

    public decimal Quantity { get; }
}

/// <summary>What of a document can be ordered again: products with a positive quantity, summed per SKU.</summary>
public sealed record ReorderableItems
{
    internal ReorderableItems(EquatableArray<ReorderableItem> items, int skipped)
    {
        Items = items;
        Skipped = skipped;
    }

    public EquatableArray<ReorderableItem> Items { get; }

    /// <summary>Lines with a positive quantity but without a SKU (services, free text).</summary>
    public int Skipped { get; }
}

/// <summary>Money still due on one document (see <see cref="Documents.Outstanding"/>).</summary>
public sealed record OpenItem(Currency Currency, NonNegativeAmount Outstanding, Maybe<DateOnly> Due);

/// <summary>What a customer owes in one currency, and how much of it is past its due date.</summary>
public sealed record Receivable
{
    internal Receivable(Currency currency, decimal outstanding, decimal overdue, int overdueCount)
    {
        Currency = currency;
        Outstanding = outstanding;
        Overdue = overdue;
        OverdueCount = overdueCount;
    }

    public Currency Currency { get; }

    public decimal Outstanding { get; }

    public decimal Overdue { get; }

    public int OverdueCount { get; }
}

public static class Documents
{
    public const int MaxNumber = 64;
    public const int MaxCustomerCode = 64;
    public const int MaxOrderNumber = 64;
    public const int MaxOrderNumbers = 50;
    public const int MaxFileBytes = 20 * 1024 * 1024;

    public const int MaxLines = 500;
    public const int MaxLineName = 300;
    public const int MaxUnit = 20;
    public const int MaxSku = 64;

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

    private static readonly Func<DocumentText, DocumentKind, DocumentText, DocumentDates, EquatableArray<DocumentText>, EquatableArray<DocumentLine>, DocumentContent> CreateContent =
        (customer, kind, number, dates, orders, lines) => new DocumentContent(customer, kind, number, dates, orders, lines);

    /// <summary>Validates a document from the ERP, reporting every problem with its field path.</summary>
    public static Validated<Document> Validate(UnvalidatedDocument input) =>
        Create
            .Apply(DocumentId.Parse(input.Id).ToValidated("id"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(DocumentText.Parse(input.CustomerCode, MaxCustomerCode, "customer-code", "Customer code").ToValidated("customerCode"))
                .Apply(ValidateKind(input))
                .Apply(DocumentText.Parse(input.Number, MaxNumber, "number", "Document number").ToValidated("number"))
                .Apply(ValidateDates(input))
                .Apply(ValidateOrderNumbers(input))
                .Apply(ValidateLines(input.Lines)));

    public static Change<Document> Reconcile(Maybe<Document> stored, Document incoming) =>
        ImportBatch.Reconcile(stored, incoming, d => d.Version, (a, b) => a.Content == b.Content);

    /// <summary>Products of the document with a positive quantity, summed per SKU in the order of their first line.</summary>
    public static ReorderableItems Reorderable(DocumentContent content)
    {
        var positive = content.Lines.Where(line => line.Quantity.Value > 0).ToList();
        return new ReorderableItems(
            positive
                .Choose(line => line.Sku.Map(sku => (Sku: sku, Quantity: line.Quantity.Value)))
                .GroupBy(line => line.Sku)
                .Select(group => new ReorderableItem(group.Key, group.Sum(line => line.Quantity)))
                .ToEquatableArray(),
            positive.Count(line => !line.Sku.Exists(_ => true)));
    }

    /// <summary>Invoices and corrections are paid; other documents only inform.</summary>
    public static bool IsPayable(DocumentType type) => type is DocumentType.Invoice or DocumentType.CorrectionInvoice;

    /// <summary>
    /// What the customer still has to pay: gross less paid, never below zero. A correction that lowers the amount
    /// owes the customer money, so nothing is due on it; documents that are not paid owe nothing.
    /// </summary>
    public static decimal Outstanding(DocumentContent content) =>
        content.Kind switch
        {
            DocumentKind.Invoice invoice => Due(invoice.Amounts),
            DocumentKind.CorrectionInvoice correction => Due(correction.Amounts),
            _ => 0m,
        };

    /// <summary>The due date itself is still on time; the day after, an unpaid document is overdue.</summary>
    public static bool IsOverdue(Maybe<DateOnly> due, DateOnly today) => due.Exists(date => date < today);

    /// <summary>The payment status of an invoice or a correction; none for documents that are not paid.</summary>
    public static Maybe<PaymentStatus> StatusOn(DocumentContent content, DateOnly today) =>
        IsPayable(content.Type)
            ? Outstanding(content) == 0m ? PaymentStatus.Paid
                : IsOverdue(content.Dates.Due, today) ? PaymentStatus.Overdue
                : PaymentStatus.Unpaid
            : Maybe<PaymentStatus>.None;

    /// <summary>Sums what is due per currency (currencies in alphabetical order).</summary>
    public static IReadOnlyList<Receivable> Receivables(IEnumerable<OpenItem> items, DateOnly today) =>
        [.. items
            .Where(item => item.Outstanding.Value > 0)
            .GroupBy(item => item.Currency)
            .OrderBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(group => new Receivable(
                group.Key,
                group.Sum(item => item.Outstanding.Value),
                group.Where(item => IsOverdue(item.Due, today)).Sum(item => item.Outstanding.Value),
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

    private static decimal Due(IDocumentAmounts amounts) => Math.Max(amounts.Gross - amounts.Paid.Value, 0m);

    /// <summary>The single order number and the list together, without repeats; blank entries are ignored.</summary>
    private static Validated<EquatableArray<DocumentText>> ValidateOrderNumbers(UnvalidatedDocument input)
    {
        var listed = input.OrderNumbers.Where(number => !string.IsNullOrWhiteSpace(number)).ToList();
        return DocumentText.ParseOptional(input.OrderNumber, MaxOrderNumber, "order-number", "Order number").ToValidated("orderNumber")
            .Bind(single => listed.Count > MaxOrderNumbers
                ? Validated.Invalid<EquatableArray<DocumentText>>(Error.Validation("order-numbers.too-many", $"At most {MaxOrderNumbers} order numbers.") with { Field = "orderNumbers" })
                : listed.Traverse(raw => DocumentText.Parse(raw, MaxOrderNumber, "order-number", "Order number").ToValidated()).AtField("orderNumbers")
                    .Map(numbers => (EquatableArray<DocumentText>)[.. single.AsEnumerable().Concat(numbers).DistinctBy(number => number.Value, StringComparer.Ordinal)]));
    }

    private static Validated<EquatableArray<DocumentLine>> ValidateLines(EquatableArray<UnvalidatedDocumentLine> lines) =>
        lines.Count > MaxLines
            ? Validated.Invalid<EquatableArray<DocumentLine>>(Error.Validation("lines.too-many", $"At most {MaxLines} lines.") with { Field = "lines" })
            : lines.Traverse(ValidateLine).Map(valid => valid.ToEquatableArray()).AtField("lines");

    private static Validated<DocumentLine> ValidateLine(UnvalidatedDocumentLine line) =>
        ((Func<LineNumber, Maybe<DocumentText>, DocumentText, DocumentQuantity, DocumentText, Maybe<LinePrice>, Maybe<Amount>, DocumentLine>)((number, sku, name, quantity, unit, price, amount) =>
            new DocumentLine(number, sku, name, quantity, unit, price, amount)))
            .Apply(LineNumber.Parse(line.Number).ToValidated("number"))
            .Apply(DocumentText.ParseOptional(line.Sku, MaxSku, "sku", "SKU").ToValidated("sku"))
            .Apply(DocumentText.Parse(line.Name, MaxLineName, "line-name", "Line name").ToValidated("name"))
            .Apply(DocumentQuantity.Parse(line.Quantity).ToValidated("quantity"))
            .Apply(DocumentText.Parse(line.Unit, MaxUnit, "unit", "Unit").ToValidated("unit"))
            .Apply(Optional(line.NetPrice, LinePrice.Parse).ToValidated("netPrice"))
            .Apply(Optional(line.NetAmount, Amount.Parse).ToValidated("netAmount"));

    private static Result<Maybe<T>, Error> Optional<T>(Maybe<decimal> raw, Func<decimal, Result<T, Error>> parse)
        where T : notnull =>
        raw.Match(value => parse(value).Map(Maybe.Some), () => Result.Ok<Maybe<T>, Error>(Maybe<T>.None));

    private static Result<DocumentType, Error> ParseType(string raw) =>
        TypeNames.TryGetValue(raw.Trim(), out var type)
            ? type
            : Error.Validation("type.unknown", $"Unknown document type '{raw}'; expected one of: {string.Join(", ", TypeNames.Keys)}.");

    private static Validated<DocumentDates> ValidateDates(UnvalidatedDocument input) =>
        input.IssueDate.Match(
            issued => DocumentDates.Create(issued, input.SaleDate, input.DueDate).ToValidated(),
            () => Validated.Invalid<DocumentDates>(Error.Validation("issue-date.missing", "The issue date is required.") with { Field = "issueDate" }));

    /// <summary>
    /// The kind with its amounts. No amount given at all means "no amounts", which only documents that are not paid may
    /// have; otherwise currency, net and gross are required (paid defaults to zero). Only a correction may be negative.
    /// </summary>
    private static Validated<DocumentKind> ValidateKind(UnvalidatedDocument input) =>
        ParseType(input.Type).ToValidated("type").Bind(type => type switch
        {
            DocumentType.Invoice => Required(input, PlainAmounts).Map(amounts => (DocumentKind)new DocumentKind.Invoice(amounts)),
            DocumentType.CorrectionInvoice => Required(input, Correction).Map(amounts => (DocumentKind)new DocumentKind.CorrectionInvoice(amounts)),
            DocumentType.DeliveryNote => Optional(input).Map(amounts => (DocumentKind)new DocumentKind.DeliveryNote(amounts)),
            _ => Optional(input).Map(amounts => (DocumentKind)new DocumentKind.Proforma(amounts)),
        });

    private static Validated<T> Required<T>(UnvalidatedDocument input, Func<UnvalidatedDocument, Validated<T>> amounts)
        where T : notnull =>
        Given(input)
            ? amounts(input)
            : Validated.Invalid<T>(Error.Validation("amounts.missing", "Invoices and corrections need their amounts.") with { Field = "gross" });

    private static Validated<Maybe<Amounts>> Optional(UnvalidatedDocument input) =>
        Given(input) ? PlainAmounts(input).Map(Maybe.Some) : Validated.Valid(Maybe<Amounts>.None);

    private static Validated<Amounts> PlainAmounts(UnvalidatedDocument input) =>
        ((Func<Currency, NonNegativeAmount, NonNegativeAmount, NonNegativeAmount, Amounts>)((currency, net, gross, paid) => new Amounts(currency, net, gross, paid)))
            .Apply(Currency.Parse(input.Currency.ValueOr(string.Empty)).ToValidated("currency"))
            .Apply(Value(input.Net).Bind(net => NonNegativeAmount.Parse(net)).ToValidated("net"))
            .Apply(Value(input.Gross).Bind(gross => NonNegativeAmount.Parse(gross)).ToValidated("gross"))
            .Apply(NonNegativeAmount.Parse(input.Paid.ValueOr(0m), "paid.negative").ToValidated("paid"));

    private static Validated<CorrectionAmounts> Correction(UnvalidatedDocument input) =>
        ((Func<Currency, Amount, Amount, NonNegativeAmount, CorrectionAmounts>)((currency, net, gross, paid) => new CorrectionAmounts(currency, net, gross, paid)))
            .Apply(Currency.Parse(input.Currency.ValueOr(string.Empty)).ToValidated("currency"))
            .Apply(Value(input.Net).Bind(Amount.Parse).ToValidated("net"))
            .Apply(Value(input.Gross).Bind(Amount.Parse).ToValidated("gross"))
            .Apply(NonNegativeAmount.Parse(input.Paid.ValueOr(0m), "paid.negative").ToValidated("paid"));

    private static bool Given(UnvalidatedDocument input) =>
        input.Currency.Exists(_ => true) || input.Net.Exists(_ => true) || input.Gross.Exists(_ => true) || input.Paid.Exists(_ => true);

    private static Result<decimal, Error> Value(Maybe<decimal> raw) =>
        raw.ToResult(() => Error.Validation("amount.missing", "The amount is required."));
}
