using FCommerce.SharedKernel.Imports;
using System.Text.Json;
using FCommerce.Catalog.Domain;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Catalog.Persistence;

/// <summary>
/// Storage shape of a product: columns for filtering, JSON (in its persisted raw form) for nested content.
/// Mapping back to the domain trusts stored values (they were validated when written) but not their combination.
/// </summary>
internal sealed class ProductRow
{
    public required string Sku { get; init; }

    public long Version { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public string? CategoryCode { get; set; }

    public required string Unit { get; set; }

    public string? Gtin { get; set; }

    public required string Images { get; set; }

    public required string Attributes { get; set; }

    public bool Active { get; set; }

    public bool Discontinued { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static ProductRow From(Product product, DateTimeOffset now)
    {
        var row = new ProductRow
        {
            Sku = product.Sku.Value,
            Name = string.Empty,
            Description = string.Empty,
            Unit = string.Empty,
            Images = string.Empty,
            Attributes = string.Empty,
        };
        row.Apply(product, now);
        return row;
    }

    public void Apply(Product product, DateTimeOffset now)
    {
        Version = product.Version.Value;
        Name = Json.Write(product.Content.Name);
        Description = Json.Write(product.Content.Description);
        CategoryCode = product.Content.Category.Map(c => c.Value).OrNull();
        Unit = product.Content.Unit.Value;
        Gtin = product.Content.Gtin.Map(g => g.Value).OrNull();
        Images = Json.Write(product.Content.Images);
        Attributes = Json.Write(product.Content.Attributes);
        Active = product.Offered;
        Discontinued = product.Status == ProductStatus.Discontinued;
        UpdatedAt = now;
    }

    public Product ToDomain() =>
        new(
            Persisted.Rehydrate<Domain.Sku, string>(Sku),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new ProductContent(
                Json.Read<ProductName>(Name),
                Json.Read<LocalizedText>(Description),
                CategoryCode.ToMaybe().Map(Persisted.Rehydrate<Domain.CategoryCode, string>),
                Persisted.Rehydrate<UnitCode, string>(Unit),
                Gtin.ToMaybe().Map(Persisted.Rehydrate<Domain.Gtin, string>),
                Json.Read<EquatableArray<ImageId>>(Images),
                Json.Read<EquatableArray<ProductAttribute>>(Attributes)),
            (Active, Discontinued) switch
            {
                (false, true) => throw Stored.Corrupt($"product {Sku} is inactive and discontinued"),
                var (active, discontinued) => Product.StatusOf(active, discontinued),
            });
}

internal sealed class CategoryRow
{
    public required string Code { get; init; }

    public long Version { get; set; }

    public string? ParentCode { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public bool Active { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static CategoryRow From(Category category, DateTimeOffset now)
    {
        var row = new CategoryRow { Code = category.Code.Value, Name = string.Empty };
        row.Apply(category, now);
        return row;
    }

    public void Apply(Category category, DateTimeOffset now)
    {
        Version = category.Version.Value;
        ParentCode = category.Content.Parent.Map(c => c.Value).OrNull();
        Name = Json.Write(category.Content.Name);
        SortOrder = category.Content.SortOrder;
        Active = category.Active;
        UpdatedAt = now;
    }

    public Category ToDomain() =>
        Persisted.Rehydrate<Category, Category.Stored>(new(
            Persisted.Rehydrate<CategoryCode, string>(Code),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            new CategoryContent(
                ParentCode.ToMaybe().Map(Persisted.Rehydrate<CategoryCode, string>),
                Json.Read<CategoryName>(Name),
                SortOrder),
            Active));
}

internal static class Json
{
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, PersistenceJson.Options);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, PersistenceJson.Options) is { } value ? value : throw Stored.Corrupt($"{typeof(T).Name} is missing.");
}
