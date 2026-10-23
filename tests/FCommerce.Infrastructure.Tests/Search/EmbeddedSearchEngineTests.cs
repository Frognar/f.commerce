using FCommerce.Application.Search;
using FCommerce.Infrastructure.Search;
using FCommerce.Search.Core.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace FCommerce.Infrastructure.Tests.Search;

public sealed record TestDocument(string Id, string Sku, string Name, string[] Tags, string[] Groups);

public sealed class EmbeddedSearchEngineTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"fcommerce-search-{Guid.NewGuid():N}");
    private readonly List<EmbeddedSearchEngine> engines = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SearchIndexDefinition Definition(string language = "pl", params string[] searchable) =>
        new("products", "id", searchable.Length > 0 ? [.. searchable] : ["sku", "name", "tags"], ["tags", "groups"], ["sku"], language);

    private async Task<EmbeddedSearchEngine> OpenAsync(SearchIndexDefinition? definition = null)
    {
        var engine = new EmbeddedSearchEngine(root, NullLoggerFactory.Instance);
        engines.Add(engine);
        await engine.ConfigureIndexAsync(definition ?? Definition(), Token);
        return engine;
    }

    private static async Task<List<string>> IdsAsync(EmbeddedSearchEngine engine, string? text, SearchFilter? filter = null) =>
        [.. (await engine.SearchAsync<TestDocument>("products", new SearchRequest(text, filter, [], 1, 50), Token)).Hits.Select(h => h.Id)];

    [Fact]
    public async Task Finds_polish_inflections_typed_without_diacritics()
    {
        var engine = await OpenAsync();
        await engine.UpsertAsync("products", [new TestDocument("1", "WR-1", "Wiertło do betonu SDS-plus", [], []), new TestDocument("2", "KT-1", "Kołek rozporowy", [], [])], Token);

        Assert.Equal(["1"], await IdsAsync(engine, "wiertel do betonu"));
        Assert.Equal(["2"], await IdsAsync(engine, "kolkow"));
    }

    [Fact]
    public async Task Filters_with_special_characters_and_facets_count_every_match()
    {
        var engine = await OpenAsync();
        await engine.UpsertAsync(
            "products",
            [
                new TestDocument("1", "A", "rzecz", ["x \"quoted\" \\ value"], ["g1"]),
                new TestDocument("2", "B", "rzecz", ["y"], ["g1", "g2"]),
                new TestDocument("3", "C", "rzecz", ["y"], ["g3"]),
            ],
            Token);

        Assert.Equal(["1"], await IdsAsync(engine, null, new SearchFilter.Equal("tags", "x \"quoted\" \\ value")));
        Assert.Equal(["2", "3"], await IdsAsync(engine, null, new SearchFilter.AnyOf("groups", ["g2", "g3"])));
        Assert.Equal(["2"], await IdsAsync(engine, "rzeczy", new SearchFilter.All([new SearchFilter.Equal("tags", "y"), new SearchFilter.Equal("groups", "g1")])));

        var page = await engine.SearchAsync<TestDocument>("products", new SearchRequest(null, null, ["groups"], 1, 1), Token);
        Assert.Equal(3, page.TotalHits);
        Assert.Equal(2, page.Facets["groups"]["g1"]);
    }

    [Fact]
    public async Task Survives_a_restart_with_updates_deletes_and_merges()
    {
        var engine = await OpenAsync();
        for (var i = 0; i < Merging.MaxSegments * 2; i++)
        {
            await engine.UpsertAsync("products", [new TestDocument($"{i}", $"S{i}", $"Śruba numer {i}", [], [])], Token);
        }

        await engine.UpsertAsync("products", [new TestDocument("0", "S0", "Nakrętka zmieniona", [], [])], Token);
        await engine.DeleteAsync("products", ["1"], Token);
        await engine.WaitForMergesAsync();
        await engine.DisposeAsync();

        var reopened = await OpenAsync();

        Assert.Equal(Merging.MaxSegments * 2 - 1, await reopened.CountAsync("products", Token));
        Assert.Equal(["0"], await IdsAsync(reopened, "nakrętki"));
        Assert.DoesNotContain("1", await IdsAsync(reopened, "śruby"));
        Assert.True(Directory.GetFiles(Path.Combine(root, "products"), "seg-*.bin").Length <= Merging.MaxSegments);
    }

    [Fact]
    public async Task A_changed_definition_starts_an_empty_index()
    {
        var engine = await OpenAsync();
        await engine.UpsertAsync("products", [new TestDocument("1", "A", "Śruba", [], [])], Token);

        await engine.ConfigureIndexAsync(Definition("pl", "sku", "name"), Token);

        Assert.Equal(0, await engine.CountAsync("products", Token));
    }

    [Fact]
    public async Task Unconfigured_indexes_count_as_empty_and_cannot_be_written()
    {
        var engine = await OpenAsync();

        Assert.Equal(0, await engine.CountAsync("missing", Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UpsertAsync("missing", [new TestDocument("1", "A", "a", [], [])], Token));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in engines)
        {
            await engine.DisposeAsync();
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
