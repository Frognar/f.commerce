using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

/// <summary>A product as received from the ERP, before validation (primitives only).</summary>
public sealed record UnvalidatedProduct(
    string? Sku,
    long Version,
    IReadOnlyDictionary<string, string>? Name,
    IReadOnlyDictionary<string, string>? Description,
    string? CategoryCode,
    string? Unit,
    string? Gtin,
    IReadOnlyList<string>? ImageIds,
    IReadOnlyList<UnvalidatedAttribute>? Attributes,
    bool Active);

public sealed record UnvalidatedAttribute(
    string? Code,
    IReadOnlyDictionary<string, string>? Name,
    IReadOnlyDictionary<string, string>? Value);

public sealed record ProductAttribute(AttributeCode Code, LocalizedText Name, LocalizedText Value);

/// <summary>Everything about a product that buyers see; compared to detect real changes.</summary>
public sealed record ProductContent(
    ProductName Name,
    LocalizedText Description,
    CategoryCode? Category,
    UnitCode Unit,
    Gtin? Gtin,
    EquatableArray<ImageId> Images,
    EquatableArray<ProductAttribute> Attributes);

/// <summary>A catalog product. Built only from validated parts, so it is always valid.</summary>
public sealed record Product(Sku Sku, SourceVersion Version, ProductContent Content, bool Active)
{
    public const int MaxDescriptionLength = 20_000;
    public const int MaxImages = 20;
    public const int MaxAttributes = 100;
    public const int MaxAttributeTextLength = 500;

    private static readonly Func<Sku, SourceVersion, ProductContent, Func<bool, Product>> Create =
        (sku, version, content) => active => new Product(sku, version, content, active);

    private static readonly Func<ProductName, LocalizedText, CategoryCode?, UnitCode, Gtin?, EquatableArray<ImageId>, EquatableArray<ProductAttribute>, ProductContent> CreateContent =
        (name, description, category, unit, gtin, images, attributes) => new ProductContent(name, description, category, unit, gtin, images, attributes);

    private static readonly Func<AttributeCode, LocalizedText, LocalizedText, ProductAttribute> CreateAttribute =
        (code, name, value) => new ProductAttribute(code, name, value);

    /// <summary>Validates all fields at once; every problem is reported with its field path.</summary>
    public static Validated<Product> Validate(UnvalidatedProduct input) =>
        Create
            .Apply(Domain.Sku.Parse(input.Sku).ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(ValidateContent(input))
            .Map(complete => complete(input.Active));

    private static Validated<ProductContent> ValidateContent(UnvalidatedProduct input) =>
        CreateContent
            .Apply(ProductName.Parse(input.Name).AtField("name"))
            .Apply(Translations.Parse(input.Description, MaxDescriptionLength).AtField("description"))
            .Apply(Optional(input.CategoryCode, CategoryCode.Parse).ToValidated("categoryCode"))
            .Apply(UnitCode.Parse(input.Unit).ToValidated("unit"))
            .Apply(Optional(input.Gtin, Domain.Gtin.Parse).ToValidated("gtin"))
            .Apply(Bounded(input.ImageIds, MaxImages, "imageIds", id => ImageId.Parse(id).ToValidated()))
            .Apply(Bounded(input.Attributes, MaxAttributes, "attributes", ValidateAttribute));

    private static Validated<ProductAttribute> ValidateAttribute(UnvalidatedAttribute input) =>
        CreateAttribute
            .Apply(AttributeCode.Parse(input.Code).ToValidated("code"))
            .Apply(Translations.Parse(input.Name, MaxAttributeTextLength).AtField("name"))
            .Apply(Translations.Parse(input.Value, MaxAttributeTextLength).AtField("value"));

    /// <summary>Blank optional input means "not set".</summary>
    internal static Result<T?, Error> Optional<T>(string? raw, Func<string, Result<T, Error>> parse)
        where T : class =>
        string.IsNullOrWhiteSpace(raw) ? Result.Ok<T?, Error>(null) : parse(raw).Map(value => (T?)value);

    private static Validated<EquatableArray<TOut>> Bounded<TIn, TOut>(IReadOnlyList<TIn>? items, int max, string field, Func<TIn, Validated<TOut>> validate) =>
        (items ?? []).Count > max
            ? Validated.Invalid<EquatableArray<TOut>>(Error.Validation($"{field}.too-many", $"At most {max} items are allowed.") with { Field = field })
            : (items ?? []).Traverse(validate).Map(values => values.ToEquatableArray()).AtField(field);
}
