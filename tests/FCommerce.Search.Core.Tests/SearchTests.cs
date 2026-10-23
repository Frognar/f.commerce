using System.Text.Json;
using FCommerce.Search.Core.Analysis;
using FCommerce.Search.Core.Index;
using FCommerce.Search.Core.Querying;

namespace FCommerce.Search.Core.Tests;

public sealed class SearchTests
{
    private static readonly IndexSchema Schema = new("id", ["sku", "name", "description"], ["sku"], ["category", "tags"]);
    private static readonly Analyzer Polish = Analyzer.Polish(PolishDictionary.Instance);

    private sealed record Doc(string Id, string Sku, string Name, string Description = "", string Category = "", string[]? Tags = null);

    private static IndexDocument Read(Doc doc) =>
        DocumentReader.Read(Schema, JsonSerializer.SerializeToUtf8Bytes(doc with { Tags = doc.Tags ?? [] }, JsonSerializerOptions.Web))!;

    private static IndexSnapshot Index(params Doc[] docs) =>
        IndexSnapshot.Empty(Schema).Add(SegmentBuilder.Build(Schema, Polish, [.. docs.Select(Read)]));

    private static List<string> Ids(SearchResults results) =>
        [.. results.Sources.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("id").GetString()!)];

    private static List<string> Find(IndexSnapshot index, string? text, Filter? filter = null) =>
        Ids(QueryEvaluator.Search(index, Polish, new SearchQuery(text, filter, [], 1, 50)));

    private static readonly IndexSnapshot Catalog = Index(
        new Doc("1", "SR-M8x40-A2", "Śruba z łbem sześciokątnym M8x40 nierdzewna", "Śruba DIN 933 do konstrukcji stalowych", "bolts", ["thread:M8"]),
        new Doc("2", "WR-SDS-80", "Wiertło do betonu SDS-plus 8 mm", "Wiertło z węglikiem", "drills", ["shank:SDS"]),
        new Doc("3", "KT-NY-8", "Kołek rozporowy nylonowy 8 mm", "Do betonu i cegły", "anchors"),
        new Doc("4", "SR-M10x40-ZN", "Śruba z łbem sześciokątnym M10x40 ocynkowana", "Śruba rdzewna po latach", "bolts", ["thread:M10"]),
        new Doc("5", "LZ-6204", "Łożysko kulkowe 6204", "Łożysko do silników", "bearings"));

    [Theory]
    [InlineData("śruby")]
    [InlineData("sruby")]
    [InlineData("śrubami")]
    [InlineData("SRUBA")]
    public void Finds_inflected_forms_with_or_without_diacritics(string query)
    {
        Assert.Equal(["1", "4"], Find(Catalog, query));
    }

    [Theory]
    [InlineData("wierteł do betonu", "2")]
    [InlineData("kołków", "3")]
    [InlineData("kolki rozporowe", "3")]
    [InlineData("lozyska kulkowe", "5")]
    public void Matches_irregular_inflection(string query, string expected)
    {
        Assert.Equal([expected], Find(Catalog, query));
    }

    [Fact]
    public void Every_word_must_match()
    {
        Assert.Equal(["1"], Find(Catalog, "śruba nierdzewna"));
        Assert.Empty(Find(Catalog, "śruba wiertło"));
    }

    [Fact]
    public void Negation_is_not_conflated()
    {
        Assert.Equal(["1"], Find(Catalog, "nierdzewne"));
        Assert.Equal(["4"], Find(Catalog, "rdzewna"));
    }

    [Theory]
    [InlineData("łoży", "5")] // prefix of the last word
    [InlineData("szruba nierdzewna", "1")] // typo: extra letter
    [InlineData("wietlo", "2")] // typo: missing letter
    [InlineData("kulkwoe", "5")] // typo: transposition
    public void Tolerates_prefixes_and_typos(string query, string expected)
    {
        Assert.Contains(expected, Find(Catalog, query));
    }

    [Fact]
    public void Codes_match_exactly_or_by_prefix_but_not_with_typos()
    {
        Assert.Equal(["1"], Find(Catalog, "m8x40"));
        Assert.Equal(["1"], Find(Catalog, "SR-M8x40-A2"));
        Assert.Equal(["5"], Find(Catalog, "6204"));
        Assert.Empty(Find(Catalog, "m8x41"));
    }

    [Fact]
    public void Matches_in_more_important_fields_rank_higher()
    {
        // "betonu" is in the name of 2 but only in the description of 3.
        Assert.Equal(["2", "3"], Find(Catalog, "beton"));
    }

    [Fact]
    public void Filters_combine_and_facets_count_all_matches()
    {
        var bolts = new Filter.Equal("category", "bolts");
        var threads = new Filter.AnyOf("tags", ["thread:M8", "shank:SDS"]);

        Assert.Equal(["1", "4"], Find(Catalog, null, bolts));
        Assert.Equal(["1", "2"], Find(Catalog, null, threads));
        Assert.Equal(["1"], Find(Catalog, null, new Filter.All([bolts, threads])));

        var results = QueryEvaluator.Search(Catalog, Polish, new SearchQuery(null, null, ["category"], 1, 1));
        Assert.Equal(5, results.Total);
        Assert.Single(results.Sources);
        Assert.Equal(2, results.Facets["category"]["bolts"]);
        Assert.Equal(1, results.Facets["category"]["bearings"]);
    }

    [Fact]
    public void Pages_follow_score_then_id()
    {
        var all = Ids(QueryEvaluator.Search(Catalog, Polish, new SearchQuery(null, null, [], 1, 2)));
        var second = Ids(QueryEvaluator.Search(Catalog, Polish, new SearchQuery(null, null, [], 2, 2)));
        var last = Ids(QueryEvaluator.Search(Catalog, Polish, new SearchQuery(null, null, [], 3, 2)));

        Assert.Equal(["1", "2", "3", "4", "5"], [.. all, .. second, .. last]);
    }

    [Fact]
    public void Newer_documents_replace_older_ones_and_deletes_hide_them()
    {
        var updated = Catalog.Add(SegmentBuilder.Build(Schema, Polish, [Read(new Doc("5", "LZ-6204", "Łożysko stożkowe 6204"))]));
        var deleted = updated.Without(["2"]);

        Assert.Empty(Find(updated, "kulkowe"));
        Assert.Equal(["5"], Find(updated, "stożkowe"));
        Assert.Equal(5, updated.Count);
        Assert.Empty(Find(deleted, "wiertło"));
        Assert.Equal(4, deleted.Count);
    }

    [Fact]
    public void Merging_keeps_live_documents_and_deletions_made_during_the_merge()
    {
        var snapshot = IndexSnapshot.Empty(Schema);
        for (var i = 0; i < Merging.MaxSegments + 1; i++)
        {
            snapshot = snapshot.Add(SegmentBuilder.Build(Schema, Polish, [Read(new Doc($"{i}", $"S{i}", $"Śruba numer {i}"))]));
        }

        var plan = Merging.Plan(snapshot)!;
        var merged = Merging.Build(snapshot, Polish, plan);
        var concurrent = snapshot.Without([snapshot.Segments[0].Segment.Ids[0]]); // deleted while merging
        var result = Merging.Apply(concurrent, plan, merged);

        Assert.True(result.Segments.Length <= Merging.MaxSegments);
        Assert.Equal(Merging.MaxSegments, result.Count);
        Assert.Equal(Merging.MaxSegments, Find(result, "śruby").Count);
        Assert.Null(Merging.Plan(IndexSnapshot.Empty(Schema)));
    }

    [Fact]
    public void English_analyzer_matches_plurals()
    {
        var english = IndexSnapshot.Empty(Schema).Add(SegmentBuilder.Build(Schema, Analyzer.English(), [Read(new Doc("1", "X", "Hex head bolt"))]));

        Assert.Single(Ids(QueryEvaluator.Search(english, Analyzer.English(), new SearchQuery("bolts", null, [], 1, 10))));
    }
}

public sealed class EditDistanceTests
{
    [Theory]
    [InlineData("sruba", "sruba", 0)]
    [InlineData("sruba", "szruba", 1)]
    [InlineData("kulkowe", "kulkwoe", 1)]
    [InlineData("wiertlo", "wietlo", 1)]
    [InlineData("abc", "xyz", 2)] // bounded at max + 1
    public void Is_bounded_optimal_string_alignment(string a, string b, int expected)
    {
        Assert.Equal(expected, EditDistance.Bounded(a, b, 1));
    }
}
