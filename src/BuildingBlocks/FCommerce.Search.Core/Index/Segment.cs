using System.Collections.Frozen;

namespace FCommerce.Search.Core.Index;

/// <summary>How a term occurs in a field: as the word itself, as a dictionary form of the word, or both.</summary>
[Flags]
public enum TermKind : byte
{
    Surface = 1,
    Lemma = 2,
}

/// <summary>Occurrences of one term: parallel arrays, ordered by document.</summary>
public sealed record Postings(int[] Docs, byte[] Fields, TermKind[] Kinds, byte[] Frequencies)
{
    public int Count => Docs.Length;

    /// <summary>Number of distinct documents (a document may occur once per field).</summary>
    public int DocumentFrequency { get; } = Docs.Distinct().Count();
}

/// <summary>
/// An immutable, self-contained part of an index. Documents are numbered 0..DocCount-1 within the segment.
/// Built by <see cref="SegmentBuilder"/>; deletions are tracked outside, in <see cref="LiveDocs"/>.
/// </summary>
public sealed class Segment
{
    public Segment(
        string[] ids,
        ReadOnlyMemory<byte>[] sources,
        string[] terms,
        Postings[] postings,
        ushort[][] fieldLengths,
        string[] filterValues,
        int[][] filterDocs,
        int[][] docFilterValues)
    {
        Ids = ids;
        Sources = sources;
        Terms = terms;
        Postings = postings;
        FieldLengths = fieldLengths;
        FilterValues = filterValues;
        FilterDocs = filterDocs;
        DocFilterValues = docFilterValues;
        DocById = ids.Select((id, doc) => (id, doc)).ToFrozenDictionary(x => x.id, x => x.doc, StringComparer.Ordinal);
        TermIndex = terms.Select((term, index) => (term, index)).ToFrozenDictionary(x => x.term, x => x.index, StringComparer.Ordinal);
        FilterValueIndex = filterValues.Select((value, index) => (value, index)).ToFrozenDictionary(x => x.value, x => x.index, StringComparer.Ordinal);
        AverageFieldLength = [.. fieldLengths.Select(lengths => lengths.Length == 0 ? 0f : (float)lengths.Average(l => l))];
        FilterValueField = [.. filterValues.Select(v => int.Parse(v.AsSpan(0, v.IndexOf('\u001f', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture))];
        FilterValueText = [.. filterValues.Select(v => v[(v.IndexOf('\u001f', StringComparison.Ordinal) + 1)..])];
    }

    public int DocCount => Ids.Length;

    public string[] Ids { get; }

    public ReadOnlyMemory<byte>[] Sources { get; }

    /// <summary>Sorted ordinally, for prefix and typo scans.</summary>
    public string[] Terms { get; }

    public Postings[] Postings { get; }

    /// <summary>[searchable field][doc] → number of tokens.</summary>
    public ushort[][] FieldLengths { get; }

    public float[] AverageFieldLength { get; }

    /// <summary>Filter values as "{field index}␟{value}".</summary>
    public string[] FilterValues { get; }

    /// <summary>[filter value] → documents having it, ascending.</summary>
    public int[][] FilterDocs { get; }

    /// <summary>[doc] → its filter value indexes (for facet counting).</summary>
    public int[][] DocFilterValues { get; }

    /// <summary>[filter value] → filterable field index.</summary>
    public int[] FilterValueField { get; }

    /// <summary>[filter value] → the value without its field.</summary>
    public string[] FilterValueText { get; }

    public FrozenDictionary<string, int> DocById { get; }

    public FrozenDictionary<string, int> TermIndex { get; }

    public FrozenDictionary<string, int> FilterValueIndex { get; }

    public static string FilterKey(int field, string value) => $"{field}\u001f{value}";
}
