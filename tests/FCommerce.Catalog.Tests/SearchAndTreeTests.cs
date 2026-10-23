using FCommerce.Catalog.Domain;
using FCommerce.Testing;
using static FCommerce.Catalog.Tests.TestProducts;

namespace FCommerce.Catalog.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("Śruba M8x40", "sruba m8x40")]
    [InlineData("  ŁOŻYSKO  kulkowe!! 6204-2RS ", "lozysko kulkowe 6204 2rs")]
    [InlineData("Straße Ø10", "strase o10")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalizes_to_lowercase_words_without_diacritics(string? input, string expected)
    {
        Assert.Equal(expected, SearchText.Normalize(input));
    }

    [Fact]
    public void Query_tokens_are_distinct_longest_first_and_bounded()
    {
        Assert.Equal(["sruba", "m8"], SearchText.Tokens("m8 Śruba śruba"));
        Assert.Equal(SearchText.MaxTokens, SearchText.Tokens("a b c d e f g h i j").Count);
    }

    [Fact]
    public void Product_document_contains_identifiers_names_and_attribute_values_framed_by_spaces()
    {
        var document = SearchText.Document(Valid());

        Assert.StartsWith(" sr m8 40 5901234123457 ", document, StringComparison.Ordinal);
        Assert.Contains(" sruba m8x40 ", document, StringComparison.Ordinal);
        Assert.Contains(" m8x40 bolt ", document, StringComparison.Ordinal);
        Assert.EndsWith(" stal ", document, StringComparison.Ordinal);
    }
}

public sealed class CategoryTreeTests
{
    private static Category Cat(string code, string? parent = null, int sort = 0, bool active = true) =>
        Category.Validate(new UnvalidatedCategory(code, parent, new Dictionary<string, string> { ["pl"] = code }, sort, 1, active))
            .ShouldBeValid();

    private static CategoryCode Code(string code) => CategoryCode.Parse(code).ShouldSucceed();

    [Fact]
    public void Builds_a_forest_ordered_by_sort_order_then_code()
    {
        var tree = CategoryTree.Build([Cat("tools", sort: 2), Cat("fasteners", sort: 1), Cat("bolts", "fasteners"), Cat("anchors", "fasteners")]);

        Assert.Equal(["fasteners", "tools"], tree.Select(n => n.Category.Code.Value));
        Assert.Equal(["anchors", "bolts"], tree[0].Children.Select(n => n.Category.Code.Value));
    }

    [Fact]
    public void Hides_orphans_inactive_subtrees_and_cycles()
    {
        var tree = CategoryTree.Build([
            Cat("root"),
            Cat("orphan", "missing"),
            Cat("hidden", "root", active: false),
            Cat("below-hidden", "hidden"),
            Cat("a", "b"),
            Cat("b", "a"),
        ]);

        var root = Assert.Single(tree);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Finds_a_category_with_its_descendants_and_its_path()
    {
        var tree = CategoryTree.Build([Cat("fasteners"), Cat("bolts", "fasteners"), Cat("hex", "bolts"), Cat("tools")]);

        Assert.Equal(["bolts", "hex"], CategoryTree.SelfAndDescendants(tree, Code("bolts")).Select(c => c.Value));
        Assert.Equal(["fasteners", "bolts", "hex"], CategoryTree.PathTo(tree, Code("hex")).Select(c => c.Code.Value));
        Assert.Empty(CategoryTree.SelfAndDescendants(tree, Code("unknown")));
    }
}
