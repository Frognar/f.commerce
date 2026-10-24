using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

public sealed record UnvalidatedCategory(
    string Code,
    Maybe<string> ParentCode,
    IReadOnlyDictionary<string, string> Name,
    int SortOrder,
    long Version,
    bool Active);

public sealed record CategoryContent(Maybe<CategoryCode> Parent, CategoryName Name, int SortOrder);

/// <summary>A node of the category tree; the parent may arrive later than the child (ERP order is not guaranteed).</summary>
public sealed record Category(CategoryCode Code, SourceVersion Version, CategoryContent Content, bool Active)
{
    private static readonly Func<CategoryCode, SourceVersion, CategoryContent, Func<bool, Category>> Create =
        (code, version, content) => active => new Category(code, version, content, active);

    private static readonly Func<Maybe<CategoryCode>, CategoryName, CategoryContent> CreateContent =
        (parent, name) => new CategoryContent(parent, name, 0);

    public static Validated<Category> Validate(UnvalidatedCategory input) =>
        Create
            .Apply(CategoryCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(Maybe.ParseOptional(input.ParentCode, CategoryCode.Parse).ToValidated("parentCode"))
                .Apply(CategoryName.Parse(input.Name).AtField("name"))
                .Map(content => content with { SortOrder = input.SortOrder }))
            .Map(complete => complete(input.Active))
            .Ensure(
                category => !category.Content.Parent.Exists(parent => parent == category.Code),
                _ => Error.Validation("category.own-parent", "A category cannot be its own parent.") with { Field = "parentCode" });
}
