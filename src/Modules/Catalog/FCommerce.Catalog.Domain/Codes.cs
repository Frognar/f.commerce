using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>Shared rule for identifiers coming from the ERP: trimmed, non-empty, bounded, no control characters.</summary>
internal static class CodeRules
{
    public static Result<string, Error> Parse(string? raw, int maxLength, string errorPrefix, string label)
    {
        var value = raw?.Trim();
        return value switch
        {
            null or "" => Error.Validation($"{errorPrefix}.empty", $"{label} is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{errorPrefix}.too-long", $"{label} must not exceed {maxLength} characters."),
            _ when value.Any(char.IsControl) => Error.Validation($"{errorPrefix}.invalid", $"{label} must not contain control characters."),
            _ => value,
        };
    }
}

/// <summary>Product identifier from the ERP (the "index"), as shown to buyers.</summary>
public sealed record Sku : IPersistedValue<Sku, string>
{
    public const int MaxLength = 64;

    private Sku(string value) => Value = value;

    public string Value { get; }

    public static Result<Sku, Error> Parse(string? raw) =>
        CodeRules.Parse(raw, MaxLength, "sku", "SKU").Map(value => new Sku(value));

    static Sku IPersistedValue<Sku, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Sku, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

public sealed record CategoryCode : IPersistedValue<CategoryCode, string>
{
    public const int MaxLength = 64;

    private CategoryCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CategoryCode, Error> Parse(string? raw) =>
        CodeRules.Parse(raw, MaxLength, "category-code", "Category code").Map(value => new CategoryCode(value));

    static CategoryCode IPersistedValue<CategoryCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CategoryCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

public sealed record AttributeCode : IPersistedValue<AttributeCode, string>
{
    public const int MaxLength = 64;

    private AttributeCode(string value) => Value = value;

    public string Value { get; }

    public static Result<AttributeCode, Error> Parse(string? raw) =>
        CodeRules.Parse(raw, MaxLength, "attribute-code", "Attribute code").Map(value => new AttributeCode(value));

    static AttributeCode IPersistedValue<AttributeCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<AttributeCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>Unit of measure code from the ERP, e.g. "szt", "kg", "m".</summary>
public sealed record UnitCode : IPersistedValue<UnitCode, string>
{
    public const int MaxLength = 16;

    private UnitCode(string value) => Value = value;

    public string Value { get; }

    public static Result<UnitCode, Error> Parse(string? raw) =>
        CodeRules.Parse(raw, MaxLength, "unit", "Unit").Map(value => new UnitCode(value));

    static UnitCode IPersistedValue<UnitCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<UnitCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
