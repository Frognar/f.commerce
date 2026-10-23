namespace FCommerce.Search.Core.Querying;

/// <summary>Filter on filterable fields; values of one field are matched exactly.</summary>
public abstract record Filter
{
    private Filter()
    {
    }

    public sealed record Equal(string Field, string Value) : Filter;

    public sealed record AnyOf(string Field, IReadOnlyList<string> Values) : Filter;

    public sealed record All(IReadOnlyList<Filter> Filters) : Filter;
}

/// <param name="Text">Every word must match: as the word, a dictionary form, a prefix (last word) or with a typo.</param>
/// <param name="Facets">Filterable fields to count values of, over all matching documents.</param>
public sealed record SearchQuery(string? Text, Filter? Filter, IReadOnlyList<string> Facets, int Page, int PageSize);

public sealed record SearchResults(
    IReadOnlyList<ReadOnlyMemory<byte>> Sources,
    int Total,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Facets);
