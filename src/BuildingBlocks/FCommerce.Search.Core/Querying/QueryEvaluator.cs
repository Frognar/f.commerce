using FCommerce.Search.Core.Analysis;
using FCommerce.Search.Core.Index;

namespace FCommerce.Search.Core.Querying;

/// <summary>
/// Evaluates a query against a snapshot (pure). Every query word must match a document in some searchable field;
/// a document's score is the sum over words of the best BM25 contribution, weighted by field importance and by how
/// the word matched (word > dictionary form > prefix > typo).
/// </summary>
public static class QueryEvaluator
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;
    private const int MaxPrefixTerms = 64;

    private enum Match
    {
        Word,
        Lemma,
        Prefix,
        Typo1,
        Typo2,
    }

    private sealed record QueryWord(string Surface, IReadOnlyList<string> Lemmas, bool AllowPrefix, int TypoBudget);

    private readonly record struct Candidate(int Segment, int Doc, float Score, string Id);

    public static SearchResults Search(IndexSnapshot snapshot, Analyzer analyzer, SearchQuery query)
    {
        var words = Plan(analyzer, query.Text);
        var schema = snapshot.Schema;
        var totalDocs = Math.Max(1, snapshot.Count);
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        int Df(string term) =>
            documentFrequency.TryGetValue(term, out var df)
                ? df
                : documentFrequency[term] = snapshot.Segments.Sum(s => s.Segment.TermIndex.TryGetValue(term, out var t) ? s.Segment.Postings[t].DocumentFrequency : 0);

        var facetFields = query.Facets.Select(f => (Name: f, Index: IndexOf(schema.FilterableFields, f))).Where(f => f.Index >= 0).ToList();
        var facets = facetFields.ToDictionary(f => f.Name, _ => new Dictionary<string, int>(StringComparer.Ordinal));
        var candidates = new List<Candidate>();

        for (var s = 0; s < snapshot.Segments.Length; s++)
        {
            var (segment, live) = (snapshot.Segments[s].Segment, snapshot.Segments[s].Live);
            var allowed = query.Filter is null ? null : Evaluate(segment, schema, query.Filter);

            IEnumerable<(int Doc, float Score)> matches = words.Count == 0
                ? live.Docs().Select(doc => (doc, 0f))
                : MatchAll(segment, live, schema, words, totalDocs, Df);

            foreach (var (doc, score) in matches)
            {
                if (allowed is not null && !allowed.Contains(doc))
                {
                    continue;
                }

                candidates.Add(new Candidate(s, doc, score, segment.Ids[doc]));
                foreach (var value in segment.DocFilterValues[doc])
                {
                    var field = segment.FilterValueField[value];
                    foreach (var facet in facetFields.Where(f => f.Index == field))
                    {
                        var counts = facets[facet.Name];
                        counts[segment.FilterValueText[value]] = counts.GetValueOrDefault(segment.FilterValueText[value]) + 1;
                    }
                }
            }
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Max(0, query.PageSize);
        var hits = Top(candidates, page * pageSize).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => snapshot.Segments[c.Segment].Segment.Sources[c.Doc])
            .ToList();

        return new SearchResults(hits, candidates.Count, facets.ToDictionary(f => f.Key, f => (IReadOnlyDictionary<string, int>)f.Value));
    }

    private static List<QueryWord> Plan(Analyzer analyzer, string? text)
    {
        var tokens = analyzer.Analyze(text);
        return [.. tokens.Select((token, i) => new QueryWord(
            token.Surface,
            token.Lemmas,
            AllowPrefix: i == tokens.Count - 1 && token.Surface.Length >= 2,
            TypoBudget: token.Surface.Any(char.IsDigit) ? 0 : token.Surface.Length >= 9 ? 2 : token.Surface.Length >= 5 ? 1 : 0))];
    }

    /// <summary>Documents matching every word, with their scores.</summary>
    private static IEnumerable<(int Doc, float Score)> MatchAll(Segment segment, LiveDocs live, IndexSchema schema, List<QueryWord> words, int totalDocs, Func<string, int> df)
    {
        var perWord = words.Select(word => MatchWord(segment, live, schema, word, totalDocs, df)).OrderBy(m => m.Count).ToList();
        foreach (var (doc, firstScore) in perWord[0])
        {
            var score = firstScore;
            var all = true;
            for (var i = 1; i < perWord.Count && all; i++)
            {
                all = perWord[i].TryGetValue(doc, out var wordScore);
                score += wordScore;
            }

            if (all)
            {
                yield return (doc, score);
            }
        }
    }

    private static Dictionary<int, float> MatchWord(Segment segment, LiveDocs live, IndexSchema schema, QueryWord word, int totalDocs, Func<string, int> df)
    {
        var best = new Dictionary<int, float>();
        var variants = word.Lemmas.Prepend(word.Surface).Distinct(StringComparer.Ordinal).ToList();

        foreach (var variant in variants)
        {
            if (segment.TermIndex.TryGetValue(variant, out var term))
            {
                Accumulate(segment, live, schema, term, variant == word.Surface ? Match.Word : Match.Lemma, totalDocs, df, best);
            }
        }

        if (word.AllowPrefix)
        {
            foreach (var term in TermsWithPrefix(segment.Terms, word.Surface).Where(t => !variants.Contains(segment.Terms[t])).Take(MaxPrefixTerms))
            {
                Accumulate(segment, live, schema, term, Match.Prefix, totalDocs, df, best);
            }
        }

        if (word.TypoBudget > 0)
        {
            // A typo in the first letter is not tolerated: it keeps candidates few and results sensible.
            foreach (var term in TermsWithPrefix(segment.Terms, word.Surface[..1]))
            {
                var candidate = segment.Terms[term];
                if (variants.Contains(candidate) || Math.Abs(candidate.Length - word.Surface.Length) > word.TypoBudget)
                {
                    continue;
                }

                var distance = EditDistance.Bounded(word.Surface, candidate, word.TypoBudget);
                if (distance <= word.TypoBudget)
                {
                    Accumulate(segment, live, schema, term, distance == 1 ? Match.Typo1 : Match.Typo2, totalDocs, df, best);
                }
            }
        }

        return best;
    }

    private static void Accumulate(Segment segment, LiveDocs live, IndexSchema schema, int term, Match match, int totalDocs, Func<string, int> df, Dictionary<int, float> best)
    {
        var postings = segment.Postings[term];
        var frequency = df(segment.Terms[term]);
        var idf = MathF.Log(1 + ((totalDocs - frequency + 0.5f) / (frequency + 0.5f)));

        for (var i = 0; i < postings.Count; i++)
        {
            var doc = postings.Docs[i];
            if (!live.IsLive(doc))
            {
                continue;
            }

            var field = postings.Fields[i];
            var kind = postings.Kinds[i];
            var factor = Factor(match, kind, schema.IsExact(field));
            if (factor <= 0)
            {
                continue;
            }

            var length = segment.FieldLengths[field][doc];
            var average = Math.Max(1f, segment.AverageFieldLength[field]);
            var tf = postings.Frequencies[i];
            var bm25 = tf * (K1 + 1) / (tf + (K1 * (1 - B + (B * length / average))));
            var score = idf * bm25 * FieldWeight(field) * factor;
            if (!best.TryGetValue(doc, out var current) || score > current)
            {
                best[doc] = score;
            }
        }
    }

    /// <summary>
    /// Exact fields (codes) accept only the word itself or a prefix of it; elsewhere a lemma match is nearly as good as
    /// the word, prefixes and typos count less.
    /// </summary>
    private static float Factor(Match match, TermKind kind, bool exactField) =>
        (match, exactField) switch
        {
            (Match.Word, true) => kind.HasFlag(TermKind.Surface) ? 1.2f : 0f,
            (Match.Prefix, true) => kind.HasFlag(TermKind.Surface) ? 0.8f : 0f,
            (_, true) => 0f,
            (Match.Word, false) => kind.HasFlag(TermKind.Surface) ? 1f : 0.9f,
            (Match.Lemma, false) => 0.9f,
            (Match.Prefix, false) => 0.7f,
            (Match.Typo1, false) => 0.5f,
            (Match.Typo2, false) => 0.35f,
            _ => 0f,
        };

    /// <summary>The first searchable field weighs most: 1, 0.62, 0.45, 0.36, …</summary>
    private static float FieldWeight(int field) => 1f / (1f + (0.6f * field));

    /// <summary>Indexes of the sorted terms starting with the prefix (binary search, then a contiguous range).</summary>
    private static IEnumerable<int> TermsWithPrefix(string[] terms, string prefix)
    {
        var low = 0;
        var high = terms.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (string.CompareOrdinal(terms[middle], prefix) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < terms.Length && terms[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            yield return i;
        }
    }

    private static HashSet<int> Evaluate(Segment segment, IndexSchema schema, Filter filter) =>
        filter switch
        {
            Filter.Equal equal => Docs(segment, schema, equal.Field, [equal.Value]),
            Filter.AnyOf anyOf => Docs(segment, schema, anyOf.Field, anyOf.Values),
            Filter.All all => all.Filters.Count == 0
                ? [.. Enumerable.Range(0, segment.DocCount)]
                : all.Filters.Select(f => Evaluate(segment, schema, f)).Aggregate((a, b) =>
                {
                    a.IntersectWith(b);
                    return a;
                }),
            _ => [],
        };

    private static HashSet<int> Docs(Segment segment, IndexSchema schema, string field, IReadOnlyList<string> values)
    {
        var fieldIndex = IndexOf(schema.FilterableFields, field);
        return [.. values
            .Select(value => segment.FilterValueIndex.GetValueOrDefault(Segment.FilterKey(fieldIndex, value), -1))
            .Where(index => index >= 0)
            .SelectMany(index => segment.FilterDocs[index])];
    }

    /// <summary>The best <paramref name="count"/> candidates in order: higher score first, then id.</summary>
    private static IEnumerable<Candidate> Top(List<Candidate> candidates, int count)
    {
        var comparer = Comparer<Candidate>.Create((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Id, b.Id);
        });

        if (count >= candidates.Count)
        {
            return candidates.Order(comparer);
        }

        // Keep the best `count` in a heap whose root is the worst kept candidate.
        var heap = new PriorityQueue<Candidate, Candidate>(Comparer<Candidate>.Create((a, b) => comparer.Compare(b, a)));
        foreach (var candidate in candidates)
        {
            if (heap.Count < count)
            {
                heap.Enqueue(candidate, candidate);
            }
            else if (count > 0 && comparer.Compare(candidate, heap.Peek()) < 0)
            {
                heap.EnqueueDequeue(candidate, candidate);
            }
        }

        return heap.UnorderedItems.Select(i => i.Element).Order(comparer);
    }

    private static int IndexOf(IReadOnlyList<string> fields, string field)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] == field)
            {
                return i;
            }
        }

        return -1;
    }
}
