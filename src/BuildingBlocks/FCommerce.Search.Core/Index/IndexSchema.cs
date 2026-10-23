namespace FCommerce.Search.Core.Index;

/// <summary>
/// How documents of an index are read: JSON property names of the primary key, the searchable fields (most important
/// first), the exact fields (searchable without stemming or typos, e.g. codes) and the filterable fields.
/// </summary>
public sealed record IndexSchema(
    string PrimaryKey,
    IReadOnlyList<string> SearchableFields,
    IReadOnlyList<string> ExactFields,
    IReadOnlyList<string> FilterableFields)
{
    public bool IsExact(int searchableField) => ExactFields.Contains(SearchableFields[searchableField]);

    /// <summary>Stable text form, to detect schema changes of a stored index.</summary>
    public string Signature =>
        $"{PrimaryKey}|{string.Join(',', SearchableFields)}|{string.Join(',', ExactFields)}|{string.Join(',', FilterableFields)}";
}

/// <summary>A document as the index sees it: id, searchable texts and filter values per field, and its JSON source.</summary>
public sealed record IndexDocument(
    string Id,
    IReadOnlyList<IReadOnlyList<string>> Searchable,
    IReadOnlyList<IReadOnlyList<string>> Filterable,
    ReadOnlyMemory<byte> Source);
