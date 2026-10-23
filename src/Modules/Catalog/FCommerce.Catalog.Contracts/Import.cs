using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Contracts;

/// <summary>
/// Upserts products sent by the ERP connector. Items are validated independently: valid ones are applied,
/// invalid ones are reported. Items not newer than the stored version (<see cref="ProductData.Version"/>) are ignored.
/// </summary>
public sealed record ImportProducts(IReadOnlyList<ProductData> Products) : ICommand<Result<ImportReport, Error>>;

/// <param name="Sku">ERP product code (index); identifies the product.</param>
/// <param name="Version">Monotonic version from the ERP (row version or change timestamp).</param>
/// <param name="Name">Translations by culture, e.g. { "pl": "Śruba M8", "en": "M8 bolt" }.</param>
/// <param name="ImageIds">Ids returned by the media upload (POST /integration/v1/media/images), in display order.</param>
/// <param name="Active">False hides the product from the catalog.</param>
public sealed record ProductData(
    string Sku,
    long Version,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string>? Description,
    string? CategoryCode,
    string Unit,
    string? Gtin,
    IReadOnlyList<string>? ImageIds,
    IReadOnlyList<AttributeData>? Attributes,
    bool Active = true);

public sealed record AttributeData(string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string> Value);

/// <summary>Upserts categories; a parent may be sent after its children.</summary>
public sealed record ImportCategories(IReadOnlyList<CategoryData> Categories) : ICommand<Result<ImportReport, Error>>;

public sealed record CategoryData(
    string Code,
    string? ParentCode,
    IReadOnlyDictionary<string, string> Name,
    long Version,
    int SortOrder = 0,
    bool Active = true);

public sealed record ImportReport(
    int Received,
    int Created,
    int Updated,
    int Unchanged,
    int Stale,
    IReadOnlyList<RejectedItem> Rejected);

/// <param name="Index">Position of the item in the request.</param>
public sealed record RejectedItem(int Index, string? Key, IReadOnlyList<FieldError> Errors);

/// <param name="Field">Path of the offending input, e.g. "name.pl" or "attributes[2].code".</param>
public sealed record FieldError(string? Field, string Code, string Message);
