using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FCommerce.Application.Search;
using FCommerce.Infrastructure.Search.Storage;
using FCommerce.Search.Core.Index;
using FCommerce.Search.Core.Querying;
using Microsoft.Extensions.Logging;
using CoreFilter = FCommerce.Search.Core.Querying.Filter;

namespace FCommerce.Infrastructure.Search;

/// <summary>
/// <see cref="ISearchEngine"/> on the embedded engine (FCommerce.Search.Core): every index lives in its own directory
/// under <paramref name="rootPath"/>, is loaded into memory on first use and persisted on every write.
/// </summary>
internal sealed class EmbeddedSearchEngine(string rootPath, ILoggerFactory loggers) : ISearchEngine, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<IndexStore>>> stores = new(StringComparer.Ordinal);
    private readonly ILogger logger = loggers.CreateLogger<EmbeddedSearchEngine>();

    public async Task ConfigureIndexAsync(SearchIndexDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidateName(definition.Name);
        var schema = new IndexSchema(
            definition.PrimaryKey,
            [.. definition.SearchableFields],
            [.. definition.ExactFields],
            [.. definition.FilterableFields]);
        var language = definition.Language ?? "und";

        // Re-configuring replaces the open store when the definition changed (the store resets itself on disk).
        if (stores.TryGetValue(definition.Name, out var open))
        {
            var current = await open.Value;
            if (current.Snapshot.Schema == schema && current.Analyzer.Language == Analyzers.For(language).Language)
            {
                return;
            }

            await current.DisposeAsync();
        }

        stores[definition.Name] = new Lazy<Task<IndexStore>>(() => IndexStore.OpenAsync(Path.Combine(rootPath, definition.Name), schema, language, logger, CancellationToken.None));
        await stores[definition.Name].Value;
    }

    public async Task UpsertAsync<TDocument>(string index, IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default)
    {
        var store = await StoreAsync(index);
        var schema = store.Snapshot.Schema;
        var read = documents
            .Select(document => DocumentReader.Read(schema, JsonSerializer.SerializeToUtf8Bytes(document, Json))
                ?? throw new ArgumentException($"A document has no '{schema.PrimaryKey}'.", nameof(documents)))
            .ToList();
        await store.UpsertAsync(read, cancellationToken);
    }

    public async Task DeleteAsync(string index, IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        await (await StoreAsync(index)).DeleteAsync(ids, cancellationToken);

    public async Task<long> CountAsync(string index, CancellationToken cancellationToken = default) =>
        stores.ContainsKey(index) ? (await StoreAsync(index)).Snapshot.Count : 0;

    public async Task<SearchResult<TDocument>> SearchAsync<TDocument>(string index, SearchRequest request, CancellationToken cancellationToken = default)
    {
        var store = await StoreAsync(index);
        var results = QueryEvaluator.Search(
            store.Snapshot,
            store.Analyzer,
            new SearchQuery(request.Text, request.Filter is null ? null : Map(request.Filter), [.. request.Facets], request.Page, request.PageSize));

        return new SearchResult<TDocument>(
            [.. results.Sources.Select(source => JsonSerializer.Deserialize<TDocument>(source.Span, Json)!)],
            results.Total,
            results.Facets);
    }

    /// <summary>Waits for background merges of every open index (tests).</summary>
    public async Task WaitForMergesAsync()
    {
        foreach (var store in stores.Values)
        {
            await (await store.Value).WaitForMergesAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in stores.Values.Where(s => s.IsValueCreated))
        {
            await (await store.Value).DisposeAsync();
        }
    }

    private Task<IndexStore> StoreAsync(string index) =>
        stores.TryGetValue(index, out var store)
            ? store.Value
            : throw new InvalidOperationException($"Search index '{index}' is not configured.");

    private static CoreFilter Map(SearchFilter filter) =>
        filter switch
        {
            SearchFilter.Equal equal => new CoreFilter.Equal(equal.Field, equal.Value),
            SearchFilter.AnyOf anyOf => new CoreFilter.AnyOf(anyOf.Field, [.. anyOf.Values]),
            SearchFilter.All all => new CoreFilter.All([.. all.Filters.Select(Map)]),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown filter."),
        };

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException($"'{name}' is not a valid index name (ASCII letters, digits, '-' and '_').", nameof(name));
        }
    }
}
