using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

[RawInput]
public sealed record UnvalidatedCategory(
    string Code,
    Maybe<string> ParentCode,
    IReadOnlyDictionary<string, string> Name,
    int SortOrder,
    long Version,
    bool Active);

public sealed record CategoryContent(Maybe<CategoryCode> Parent, CategoryName Name, int SortOrder);

/// <summary>
/// A node of the category tree, never its own parent; the parent may arrive later than the child (ERP order is not
/// guaranteed).
/// </summary>
public sealed record Category : IPersistedValue<Category, Category.Stored>
{
    private static readonly Func<CategoryCode, SourceVersion, CategoryContent, Func<bool, Stored>> Create =
        (code, version, content) => active => new Stored(code, version, content, active);

    private static readonly Func<Maybe<CategoryCode>, CategoryName, Func<int, CategoryContent>> CreateContent =
        (parent, name) => sortOrder => new CategoryContent(parent, name, sortOrder);

    private Category(Stored stored)
    {
        Code = stored.Code;
        Version = stored.Version;
        Content = stored.Content;
        Active = stored.Active;
    }

    public CategoryCode Code { get; }

    public SourceVersion Version { get; }

    public CategoryContent Content { get; }

    public bool Active { get; }

    public static Validated<Category> Validate(UnvalidatedCategory input) =>
        Create
            .Apply(CategoryCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(Maybe.ParseOptional(input.ParentCode, CategoryCode.Parse).ToValidated("parentCode"))
                .Apply(CategoryName.Parse(input.Name).AtField("name"))
                .Map(complete => complete(input.SortOrder)))
            .Map(complete => complete(input.Active))
            .Ensure(
                IsWellFormed,
                _ => Error.Validation("category.own-parent", "A category cannot be its own parent.") with { Field = "parentCode" })
            .Map(stored => new Category(stored));

    public sealed record Stored(CategoryCode Code, SourceVersion Version, CategoryContent Content, bool Active);

    private static bool IsWellFormed(Stored raw) => !raw.Content.Parent.Exists(parent => parent == raw.Code);

    static bool IPersistedValue<Category, Stored>.IsWellFormed(Stored raw) => IsWellFormed(raw);

    static Category IPersistedValue<Category, Stored>.Rehydrate(Stored raw) => new(raw);

    Stored IPersistedValue<Category, Stored>.Dehydrate() => new(Code, Version, Content, Active);
}
