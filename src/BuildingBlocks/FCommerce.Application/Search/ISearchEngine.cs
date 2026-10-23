using FCommerce.SharedKernel.Collections;

namespace FCommerce.Application.Search;

/// <summary>
/// Port for a full-text search engine. Documents are plain records serialized as JSON (camelCase). Writes complete
/// before returning (the document is searchable afterwards). Failures of the engine surface as exceptions.
/// </summary>
public interface ISearchEngine
{
    /// <summary>Creates the index if needed and applies its settings (idempotent).</summary>
    Task ConfigureIndexAsync(SearchIndexDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Adds or fully replaces documents by primary key.</summary>
    Task UpsertAsync<TDocument>(string index, IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default);

    Task DeleteAsync(string index, IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    Task<long> CountAsync(string index, CancellationToken cancellationToken = default);

    Task<SearchResult<TDocument>> SearchAsync<TDocument>(string index, SearchRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Name">Index name: ASCII letters, digits, '-' and '_'.</param>
/// <param name="SearchableFields">Fields matched by the query text, most important first (affects ranking).</param>
/// <param name="FilterableFields">Fields usable in <see cref="SearchFilter"/> and as facets.</param>
/// <param name="ExactFields">Searchable fields matched without typo tolerance (codes, identifiers).</param>
/// <param name="Language">Language of the texts (BCP 47, e.g. "pl"), used for tokenization when the engine supports it.</param>
public sealed record SearchIndexDefinition(
    string Name,
    string PrimaryKey,
    EquatableArray<string> SearchableFields,
    EquatableArray<string> FilterableFields,
    EquatableArray<string> ExactFields,
    string? Language,
    int MaxFacetValues = 1000);

/// <summary>Filter expression; field names must be filterable fields of the index.</summary>
public abstract record SearchFilter
{
    private SearchFilter()
    {
    }

    public sealed record Equal(string Field, string Value) : SearchFilter;

    public sealed record AnyOf(string Field, EquatableArray<string> Values) : SearchFilter;

    public sealed record All(EquatableArray<SearchFilter> Filters) : SearchFilter;
}

/// <param name="Text">Query text; every word must match (typos tolerated, last word as prefix). Empty returns all documents matching the filter.</param>
/// <param name="Facets">Filterable fields whose value counts should be returned.</param>
/// <param name="Page">1-based page number.</param>
public sealed record SearchRequest(string? Text, SearchFilter? Filter, EquatableArray<string> Facets, int Page, int PageSize);

/// <param name="Facets">Field → value → number of matching documents.</param>
public sealed record SearchResult<TDocument>(
    IReadOnlyList<TDocument> Hits,
    int TotalHits,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Facets);
