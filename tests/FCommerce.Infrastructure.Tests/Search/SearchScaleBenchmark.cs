using System.Diagnostics;
using FCommerce.Application.Search;
using FCommerce.Infrastructure.Search;
using FCommerce.SharedKernel.Optional;
using Microsoft.Extensions.Logging.Abstractions;

namespace FCommerce.Infrastructure.Tests.Search;

/// <summary>Scale check of the embedded engine; run explicitly: dotnet test -- --filter-method *Scale* --explicit only.</summary>
public sealed class SearchScaleBenchmark
{
    private sealed record Product(string Id, string Sku, string Name, string[] AttributeValues, string[] Categories, string[] Facets);

    private static readonly string[] Nouns = ["Śruba", "Nakrętka", "Wiertło", "Kołek", "Podkładka", "Klucz", "Opaska", "Łożysko", "Wkręt", "Nit", "Kotwa", "Zawias", "Uszczelka", "Taśma", "Rękawica"];
    private static readonly string[] Adjectives = ["ocynkowana", "nierdzewna", "stalowa", "mosiężna", "nylonowa", "kulkowe", "rozporowy", "samowiercący", "płaska", "sprężysta"];
    private static readonly string[] Materials = ["Stal", "Stal nierdzewna A2", "Stal nierdzewna A4", "Mosiądz", "Nylon", "Aluminium"];

    [Fact(Explicit = true)]
    public async Task Scale_half_a_million_products()
    {
        const int total = 500_000;
        const int batch = 10_000;
        var root = Path.Combine(Path.GetTempPath(), $"fcommerce-scale-{Guid.NewGuid():N}");
        var definition = new SearchIndexDefinition("products", "id", ["sku", "name", "attributeValues"], ["categories", "facets"], ["sku"], "pl");
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        try
        {
            var engine = new EmbeddedSearchEngine(root, NullLoggerFactory.Instance);
            await engine.ConfigureIndexAsync(definition, token);

            var indexing = Stopwatch.StartNew();
            for (var start = 0; start < total; start += batch)
            {
                await engine.UpsertAsync("products", [.. Enumerable.Range(start, batch).Select(Generate)], token);
            }

            await engine.WaitForMergesAsync();
            output.WriteLine($"indexed {total:N0} in {indexing.Elapsed.TotalSeconds:F1} s");
            output.WriteLine($"on disk: {Directory.GetFiles(Path.Combine(root, "products")).Sum(f => new FileInfo(f).Length) / 1024 / 1024:N0} MB, managed heap: {GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024:N0} MB");

            await MeasureAsync(engine, "srub nierdzewnych", output, token);
            await MeasureAsync(engine, "wierteł", output, token);
            await MeasureAsync(engine, "lozyska kulkowe", output, token);
            await MeasureAsync(engine, "szruba", output, token);
            await MeasureAsync(engine, "SR-123456", output, token);
            await MeasureAsync(engine, default, output, token);
            await engine.DisposeAsync();

            var reopening = Stopwatch.StartNew();
            var reopened = new EmbeddedSearchEngine(root, NullLoggerFactory.Instance);
            await reopened.ConfigureIndexAsync(definition, token);
            output.WriteLine($"reopened {await reopened.CountAsync("products", token):N0} documents in {reopening.Elapsed.TotalSeconds:F1} s");
            await reopened.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task MeasureAsync(EmbeddedSearchEngine engine, Maybe<string> text, ITestOutputHelper output, CancellationToken token)
    {
        var request = new SearchRequest(text, new SearchFilter.All([]), ["facets"], 1, 24);
        await engine.SearchAsync<Product>("products", request, token); // warm-up
        var timer = Stopwatch.StartNew();
        const int runs = 10;
        SearchResult<Product>? result = null;
        for (var i = 0; i < runs; i++)
        {
            result = await engine.SearchAsync<Product>("products", request, token);
        }

        output.WriteLine($"'{text}': {result!.TotalHits:N0} hits, {timer.Elapsed.TotalMilliseconds / runs:F1} ms");
    }

    private static Product Generate(int i)
    {
        var noun = Nouns[i % Nouns.Length];
        var adjective = Adjectives[(i / Nouns.Length) % Adjectives.Length];
        var material = Materials[(i / 7) % Materials.Length];
        var size = 3 + (i % 20);
        return new Product(
            $"p{i}",
            $"SR-{i:D6}",
            $"{noun} {adjective} M{size} x {10 + (i % 90)} mm",
            [material, $"M{size}", $"{10 + (i % 90)} mm"],
            [$"cat-{i % 50}", $"group-{i % 5}"],
            [$"material\u001f{material}", $"thread\u001fM{size}"]);
    }
}
