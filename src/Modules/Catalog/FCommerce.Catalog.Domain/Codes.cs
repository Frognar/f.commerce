using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>Product identifier from the ERP (the "index"), as shown to buyers.</summary>
public sealed record Sku : IPersistedValue<Sku, string>
{
    public const int MaxLength = 64;

    private Sku(string value) => Value = value;

    public string Value { get; }

    public static Result<Sku, Error> Parse(string? raw) =>
        SourceCodes.Parse(raw, MaxLength, "sku", "SKU").Map(value => new Sku(value));

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
        SourceCodes.Parse(raw, MaxLength, "category-code", "Category code").Map(value => new CategoryCode(value));

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
        SourceCodes.Parse(raw, MaxLength, "attribute-code", "Attribute code").Map(value => new AttributeCode(value));

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
        SourceCodes.Parse(raw, MaxLength, "unit", "Unit").Map(value => new UnitCode(value));

    static UnitCode IPersistedValue<UnitCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<UnitCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
