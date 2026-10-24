using System.Collections.Immutable;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Catalog.Domain;

public sealed record CategoryNode(Category Category, EquatableArray<CategoryNode> Children);

/// <summary>Builds the visible category tree; tolerant of ERP data arriving out of order.</summary>
public static class CategoryTree
{
    /// <summary>
    /// Active categories as a forest ordered by sort order. A category whose parent is missing or inactive is
    /// hidden together with its subtree; cycles are cut.
    /// </summary>
    public static EquatableArray<CategoryNode> Build(IEnumerable<Category> categories)
    {
        var active = categories.Where(c => c.Active).ToList();
        var byParent = active.ToLookup(c => c.Content.Parent);
        return Children(byParent, parent: Maybe<CategoryCode>.None, visited: ImmutableHashSet<CategoryCode>.Empty);
    }

    /// <summary>The category and all categories below it in the tree (for "products in this category" filters).</summary>
    public static EquatableArray<CategoryCode> SelfAndDescendants(EquatableArray<CategoryNode> forest, CategoryCode code) =>
        Find(forest, code).Match(node => Flatten(node).ToEquatableArray(), () => EquatableArray<CategoryCode>.Empty);

    /// <summary>Path from a root to the category (for breadcrumbs); empty when not in the tree.</summary>
    public static EquatableArray<Category> PathTo(EquatableArray<CategoryNode> forest, CategoryCode code)
    {
        foreach (var node in forest)
        {
            if (node.Category.Code == code)
            {
                return EquatableArray.Create(node.Category);
            }

            var below = PathTo(node.Children, code);
            if (!below.IsEmpty)
            {
                return EquatableArray.Create(node.Category).AddRange(below);
            }
        }

        return EquatableArray<Category>.Empty;
    }

    private static EquatableArray<CategoryNode> Children(ILookup<Maybe<CategoryCode>, Category> byParent, Maybe<CategoryCode> parent, ImmutableHashSet<CategoryCode> visited) =>
        byParent[parent]
            .Where(c => !visited.Contains(c.Code))
            .OrderBy(c => c.Content.SortOrder)
            .ThenBy(c => c.Code.Value, StringComparer.Ordinal)
            .Select(c => new CategoryNode(c, Children(byParent, c.Code, visited.Add(c.Code))))
            .ToEquatableArray();

    private static Maybe<CategoryNode> Find(IEnumerable<CategoryNode> forest, CategoryCode code) =>
        forest.Select(node => node.Category.Code == code ? node : Find(node.Children, code)).Choose().FirstOrNone();

    private static IEnumerable<CategoryCode> Flatten(CategoryNode node) =>
        node.Children.SelectMany(Flatten).Prepend(node.Category.Code);
}
