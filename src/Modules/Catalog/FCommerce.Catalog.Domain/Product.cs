using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

/// <summary>
/// A product as received from the ERP, before validation (primitives only). Missing required texts arrive as empty,
/// missing collections as empty ones; optional fields are absent.
/// </summary>
[RawInput]
public sealed record UnvalidatedProduct(
    string Sku,
    long Version,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    Maybe<string> CategoryCode,
    string Unit,
    Maybe<string> Gtin,
    IReadOnlyList<string> ImageIds,
    IReadOnlyList<UnvalidatedAttribute> Attributes,
    bool Active,
    bool Discontinued = false);

[RawInput]
public sealed record UnvalidatedAttribute(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Value);

public sealed record ProductAttribute(AttributeCode Code, LocalizedText Name, LocalizedText Value);

/// <summary>Everything about a product that buyers see; compared to detect real changes.</summary>
public sealed record ProductContent(
    ProductName Name,
    LocalizedText Description,
    Maybe<CategoryCode> Category,
    UnitCode Unit,
    Maybe<Gtin> Gtin,
    EquatableArray<ImageId> Images,
    EquatableArray<ProductAttribute> Attributes);

/// <summary>Whether a product is offered.</summary>
public enum ProductStatus
{
    Active,

    /// <summary>Offered, but no longer replenished: sold only while stock lasts.</summary>
    Discontinued,

    /// <summary>Not offered (whether it would be discontinued no longer matters).</summary>
    Inactive,
}

/// <summary>A catalog product. Built only from validated parts, so it is always valid.</summary>
public sealed record Product(Sku Sku, SourceVersion Version, ProductContent Content, ProductStatus Status)
{
    public const int MaxDescriptionLength = 20_000;
    public const int MaxImages = 20;
    public const int MaxAttributes = 100;
    public const int MaxAttributeTextLength = 500;

    private static readonly Func<Sku, SourceVersion, ProductContent, Func<ProductStatus, Product>> Create =
        (sku, version, content) => status => new Product(sku, version, content, status);

    private static readonly Func<ProductName, LocalizedText, Maybe<CategoryCode>, UnitCode, Maybe<Gtin>, EquatableArray<ImageId>, EquatableArray<ProductAttribute>, ProductContent> CreateContent =
        (name, description, category, unit, gtin, images, attributes) => new ProductContent(name, description, category, unit, gtin, images, attributes);

    private static readonly Func<AttributeCode, LocalizedText, LocalizedText, ProductAttribute> CreateAttribute =
        (code, name, value) => new ProductAttribute(code, name, value);

    /// <summary>Validates all fields at once; every problem is reported with its field path.</summary>
    public static Validated<Product> Validate(UnvalidatedProduct input) =>
        Create
            .Apply(Domain.Sku.Parse(input.Sku).ToValidated("sku"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(ValidateContent(input))
            .Map(complete => complete(StatusOf(input.Active, input.Discontinued)));

    /// <summary>The ERP's two flags as one status: an inactive product is not offered, discontinued or not.</summary>
    public static ProductStatus StatusOf(bool active, bool discontinued) =>
        (active, discontinued) switch
        {
            (false, _) => ProductStatus.Inactive,
            (true, true) => ProductStatus.Discontinued,
            (true, false) => ProductStatus.Active,
        };

    /// <summary>Offered to buyers (active or discontinued).</summary>
    public bool Offered => Status != ProductStatus.Inactive;

    private static Validated<ProductContent> ValidateContent(UnvalidatedProduct input) =>
        CreateContent
            .Apply(ProductName.Parse(input.Name).AtField("name"))
            .Apply(Translations.Parse(input.Description, MaxDescriptionLength).AtField("description"))
            .Apply(Maybe.ParseOptional(input.CategoryCode, CategoryCode.Parse).ToValidated("categoryCode"))
            .Apply(UnitCode.Parse(input.Unit).ToValidated("unit"))
            .Apply(Maybe.ParseOptional(input.Gtin, Domain.Gtin.Parse).ToValidated("gtin"))
            .Apply(Bounded(input.ImageIds, MaxImages, "imageIds", id => ImageId.Parse(id).ToValidated()))
            .Apply(Bounded(input.Attributes, MaxAttributes, "attributes", ValidateAttribute));

    private static Validated<ProductAttribute> ValidateAttribute(UnvalidatedAttribute input) =>
        CreateAttribute
            .Apply(AttributeCode.Parse(input.Code).ToValidated("code"))
            .Apply(Translations.Parse(input.Name, MaxAttributeTextLength).AtField("name"))
            .Apply(Translations.Parse(input.Value, MaxAttributeTextLength).AtField("value"));

    private static Validated<EquatableArray<TOut>> Bounded<TIn, TOut>(IReadOnlyList<TIn> items, int max, string field, Func<TIn, Validated<TOut>> validate)
        where TIn : notnull
        where TOut : notnull =>
        items.Count > max
            ? Validated.Invalid<EquatableArray<TOut>>(Error.Validation($"{field}.too-many", $"At most {max} items are allowed.") with { Field = field })
            : items.Traverse(validate).Map(values => values.ToEquatableArray()).AtField(field);
}
