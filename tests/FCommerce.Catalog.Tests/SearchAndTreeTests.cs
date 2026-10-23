using FCommerce.SharedKernel.Imports;
using FCommerce.Catalog.Domain;
using FCommerce.Testing;
using static FCommerce.Catalog.Tests.TestProducts;

namespace FCommerce.Catalog.Tests;

public sealed class CatalogSearchTests
{
    private static Category Cat(string code, string? parent = null) =>
        Category.Validate(new UnvalidatedCategory(code, parent, new Dictionary<string, string> { ["pl"] = code.ToUpperInvariant() }, 0, 1, true)).ShouldBeValid();

    [Fact]
    public void Entry_resolves_texts_for_the_culture_and_lists_category_ancestors()
    {
        var path = CategoryTree.PathTo(CategoryTree.Build([Cat("fasteners"), Cat("bolts", "fasteners")]), CategoryCode.Parse("bolts").ShouldSucceed());
        var product = Valid() with { Content = Valid().Content with { Category = CategoryCode.Parse("bolts").ShouldSucceed() } };

        var entry = CatalogSearch.Entry(product, path, Values.Culture("en"), Values.Culture("pl"));

        Assert.Equal("M8x40 bolt", entry.Name);
        Assert.Equal("Ocynkowana", entry.Description); // no English text → default culture
        Assert.Equal(["fasteners", "bolts"], entry.Categories);
        Assert.Equal([CatalogSearch.FacetToken("material", "Stal")], entry.Facets);
        Assert.Equal([CatalogSearch.FacetToken("material", "Materiał")], entry.FacetNames);
        Assert.Equal(ImageA, entry.ImageId);
    }

    [Fact]
    public void Document_ids_are_engine_safe_and_unique_per_sku()
    {
        var id = CatalogSearch.DocumentId(Sku.Parse("A/1 ż").ShouldSucceed());

        Assert.Matches("^[0-9a-f]+$", id);
        Assert.NotEqual(id, CatalogSearch.DocumentId(Sku.Parse("A/1 z").ShouldSucceed()));
    }

    [Fact]
    public void Facets_need_two_values_or_a_selection_and_are_naturally_ordered()
    {
        var counts = new Dictionary<string, int>
        {
            [CatalogSearch.FacetToken("thread", "M10")] = 3,
            [CatalogSearch.FacetToken("thread", "M8")] = 5,
            [CatalogSearch.FacetToken("standard", "DIN 933")] = 8,
            [CatalogSearch.FacetToken("colour", "black")] = 2,
        };
        var names = new Dictionary<string, int>
        {
            [CatalogSearch.FacetToken("thread", "Gwint")] = 8,
            [CatalogSearch.FacetToken("standard", "Norma")] = 8,
            [CatalogSearch.FacetToken("colour", "Kolor")] = 2,
        };
        var selected = new HashSet<(string, string)> { ("colour", "black") };

        var facets = CatalogSearch.Facets(counts, names, selected);

        Assert.Equal(["Gwint", "Kolor"], facets.Select(f => f.Name));
        Assert.Equal([("M8", 5, false), ("M10", 3, false)], facets[0].Values.Select(v => (v.Value, v.Count, v.Selected)));
        Assert.True(facets[1].Values[0].Selected);
    }

    [Theory]
    [InlineData("M8", "M10", -1)]
    [InlineData("8 mm", "10 mm", -1)]
    [InlineData("10 mm", "10 mm", 0)]
    [InlineData("a2", "A10", -1)]
    [InlineData("007", "7", -1)]
    [InlineData("abc", "abd", -1)]
    public void Natural_order_compares_numbers_by_value(string x, string y, int expected)
    {
        Assert.Equal(expected, Math.Sign(NaturalComparer.Instance.Compare(x, y)));
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
