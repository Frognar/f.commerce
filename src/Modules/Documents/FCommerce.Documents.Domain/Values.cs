using System.Globalization;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Documents.Domain;

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

/// <summary>A short text of a document from the ERP (codes, numbers, names, units): trimmed, bounded, no control characters.</summary>
public sealed record DocumentText : IPersistedValue<DocumentText, string>
{
    private DocumentText(string value) => Value = value;

    public string Value { get; }

    public static Result<DocumentText, Error> Parse(string raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.Parse(raw, maxLength, errorPrefix, label).Map(value => new DocumentText(value));

    public static Result<Maybe<DocumentText>, Error> ParseOptional(Maybe<string> raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.ParseOptional(raw, maxLength, errorPrefix, label).Map(value => value.Map(text => new DocumentText(text)));

    static DocumentText IPersistedValue<DocumentText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<DocumentText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A position on a document, from 1.</summary>
public sealed record LineNumber : IPersistedValue<LineNumber, int>
{
    private LineNumber(int value) => Value = value;

    public int Value { get; }

    public static Result<LineNumber, Error> Parse(int raw) =>
        raw > 0 ? new LineNumber(raw) : Error.Validation("line-number.invalid", "Line numbers start at 1.");

    static bool IPersistedValue<LineNumber, int>.IsWellFormed(int raw) => raw > 0;

    static LineNumber IPersistedValue<LineNumber, int>.Rehydrate(int raw) => new(raw);

    int IPersistedValue<LineNumber, int>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A quantity on a document line: at most 3 decimal places; negative on corrections that take goods back.</summary>
public sealed record DocumentQuantity : IPersistedValue<DocumentQuantity, decimal>
{
    public const decimal Max = 999_999_999_999.999m;

    private DocumentQuantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<DocumentQuantity, Error> Parse(decimal raw) =>
        Math.Abs(raw) > Max ? Error.Validation("quantity.too-large", "The quantity is too large.")
        : decimal.Round(raw, 3) != raw ? Error.Validation("quantity.precision", "Quantities have at most 3 decimal places.")
        : new DocumentQuantity(raw);

    static DocumentQuantity IPersistedValue<DocumentQuantity, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<DocumentQuantity, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A net unit price on a document line: zero or more, at most 4 decimal places.</summary>
public sealed record LinePrice : IPersistedValue<LinePrice, decimal>
{
    private LinePrice(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<LinePrice, Error> Parse(decimal raw) =>
        raw < 0 || raw >= Amount.Max ? Error.Validation("net-price.invalid", "The net price must be zero or more.")
        : decimal.Round(raw, 4) != raw ? Error.Validation("net-price.precision", "Net prices have at most 4 decimal places.")
        : new LinePrice(raw);

    static bool IPersistedValue<LinePrice, decimal>.IsWellFormed(decimal raw) => raw >= 0;

    static LinePrice IPersistedValue<LinePrice, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<LinePrice, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An amount of money on a document: at most 2 decimal places, bounded well below what the storage column holds; may be negative.</summary>
public sealed record Amount : IPersistedValue<Amount, decimal>
{
    public const decimal Max = 1_000_000_000_000m;

    private Amount(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<Amount, Error> Parse(decimal raw) =>
        Math.Abs(raw) >= Max ? Error.Validation("amount.too-large", "The amount is too large.")
        : Math.Round(raw, 2) != raw ? Error.Validation("amount.precision", "Amounts have at most two decimal places.")
        : new Amount(raw);

    static Amount IPersistedValue<Amount, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<Amount, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An <see cref="Amount"/> that is zero or more (what was paid, the values of documents other than corrections).</summary>
public sealed record NonNegativeAmount : IPersistedValue<NonNegativeAmount, decimal>
{
    private NonNegativeAmount(decimal value) => Value = value;

    public decimal Value { get; }

    public static NonNegativeAmount Zero { get; } = new(0m);

    /// <param name="code">The error when the amount is negative.</param>
    public static Result<NonNegativeAmount, Error> Parse(decimal raw, string code = "amount.negative") =>
        Amount.Parse(raw).Bind(amount => amount.Value < 0
            ? Error.Validation(code, "Only a correction may have negative amounts.")
            : Result.Ok<NonNegativeAmount, Error>(new NonNegativeAmount(amount.Value)));

    static bool IPersistedValue<NonNegativeAmount, decimal>.IsWellFormed(decimal raw) => raw >= 0;

    static NonNegativeAmount IPersistedValue<NonNegativeAmount, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<NonNegativeAmount, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
