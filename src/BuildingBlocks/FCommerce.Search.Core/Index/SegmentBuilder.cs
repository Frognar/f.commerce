using FCommerce.Search.Core.Analysis;

namespace FCommerce.Search.Core.Index;

/// <summary>Analyzes documents and builds an immutable <see cref="Segment"/>. A later document with the same id wins.</summary>
public static class SegmentBuilder
{
    public static Segment Build(IndexSchema schema, Analyzer analyzer, IReadOnlyList<IndexDocument> input)
    {
        var documents = input
            .Select((document, order) => (document, order))
            .GroupBy(d => d.document.Id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(d => d.order).document)
            .ToList();

        var occurrences = new Dictionary<string, Dictionary<(int Doc, byte Field), (TermKind Kind, int Frequency)>>(StringComparer.Ordinal);
        var fieldLengths = schema.SearchableFields.Select(_ => new ushort[documents.Count]).ToArray();
        var filterDocs = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var docFilterKeys = new List<string>[documents.Count];

        for (var doc = 0; doc < documents.Count; doc++)
        {
            var document = documents[doc];
            for (var field = 0; field < schema.SearchableFields.Count; field++)
            {
                var exact = schema.IsExact(field);
                var tokens = document.Searchable[field].SelectMany(text => exact ? Analyzer.AnalyzeExact(text) : analyzer.Analyze(text)).ToList();
                fieldLengths[field][doc] = (ushort)Math.Min(tokens.Count, ushort.MaxValue);
                foreach (var token in tokens)
                {
                    // Each occurrence counts once per distinct term, whether it is the word, a lemma or both.
                    foreach (var term in token.Lemmas.Prepend(token.Surface).Distinct(StringComparer.Ordinal))
                    {
                        var kind = (term == token.Surface ? TermKind.Surface : 0) | (token.Lemmas.Contains(term) ? TermKind.Lemma : 0);
                        Add(occurrences, term, doc, (byte)field, kind);
                    }
                }
            }

            docFilterKeys[doc] = [];
            for (var field = 0; field < schema.FilterableFields.Count; field++)
            {
                foreach (var value in document.Filterable[field].Distinct(StringComparer.Ordinal))
                {
                    var key = Segment.FilterKey(field, value);
                    docFilterKeys[doc].Add(key);
                    if (!filterDocs.TryGetValue(key, out var docs))
                    {
                        filterDocs[key] = docs = [];
                    }

                    docs.Add(doc);
                }
            }
        }

        var terms = occurrences.Keys.Order(StringComparer.Ordinal).ToArray();
        var postings = terms.Select(term =>
        {
            var entries = occurrences[term].OrderBy(e => e.Key.Doc).ThenBy(e => e.Key.Field).ToList();
            return new Postings(
                [.. entries.Select(e => e.Key.Doc)],
                [.. entries.Select(e => e.Key.Field)],
                [.. entries.Select(e => e.Value.Kind)],
                [.. entries.Select(e => (byte)Math.Min(e.Value.Frequency, byte.MaxValue))]);
        }).ToArray();

        var filterValues = filterDocs.Keys.Order(StringComparer.Ordinal).ToArray();
        var filterIndex = filterValues.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index, StringComparer.Ordinal);

        return new Segment(
            [.. documents.Select(d => d.Id)],
            [.. documents.Select(d => d.Source)],
            terms,
            postings,
            fieldLengths,
            filterValues,
            [.. filterValues.Select(value => filterDocs[value].ToArray())],
            [.. docFilterKeys.Select(keys => keys.Select(key => filterIndex[key]).ToArray())]);
    }

    private static void Add(Dictionary<string, Dictionary<(int Doc, byte Field), (TermKind Kind, int Frequency)>> occurrences, string term, int doc, byte field, TermKind kind)
    {
        if (!occurrences.TryGetValue(term, out var byDocField))
        {
            occurrences[term] = byDocField = [];
        }

        byDocField[(doc, field)] = byDocField.TryGetValue((doc, field), out var existing)
            ? (existing.Kind | kind, existing.Frequency + 1)
            : (kind, 1);
    }
}
