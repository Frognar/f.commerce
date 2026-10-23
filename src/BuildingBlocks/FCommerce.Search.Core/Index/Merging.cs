using FCommerce.Search.Core.Analysis;

namespace FCommerce.Search.Core.Index;

/// <summary>Which segments to merge, chosen from a snapshot.</summary>
public sealed record MergePlan(IReadOnlyList<Segment> Segments);

/// <summary>
/// Keeps the number of segments low: every write adds a segment, and searching many small segments is slow.
/// Merging rebuilds the live documents of several segments into one (re-analyzing them, which also brings old
/// segments up to date with the current analyzer).
/// </summary>
public static class Merging
{
    public const int MaxSegments = 8;

    /// <summary>Merge the smallest segments when there are too many; null when no merge is needed.</summary>
    public static MergePlan? Plan(IndexSnapshot snapshot) =>
        snapshot.Segments.Length <= MaxSegments
            ? null
            : new MergePlan([.. snapshot.Segments.OrderBy(s => s.Live.Count).Take(snapshot.Segments.Length - (MaxSegments / 2) + 1).Select(s => s.Segment)]);

    /// <summary>Builds the merged segment from the planned segments' documents live in <paramref name="snapshot"/>.</summary>
    public static Segment Build(IndexSnapshot snapshot, Analyzer analyzer, MergePlan plan)
    {
        var documents = snapshot.Segments
            .Where(s => plan.Segments.Contains(s.Segment))
            .SelectMany(s => s.Live.Docs().Select(doc => s.Segment.Sources[doc]))
            .Select(source => DocumentReader.Read(snapshot.Schema, source))
            .OfType<IndexDocument>()
            .ToList();
        return SegmentBuilder.Build(snapshot.Schema, analyzer, documents);
    }

    /// <summary>
    /// Replaces the planned segments with the merged one in the current snapshot. Documents deleted or replaced while the
    /// merge was running (no longer live in their source segment) are deleted from the merged segment too.
    /// </summary>
    public static IndexSnapshot Apply(IndexSnapshot current, MergePlan plan, Segment merged)
    {
        var sources = current.Segments.Where(s => plan.Segments.Contains(s.Segment)).ToList();
        if (sources.Count == 0)
        {
            return current; // every source was emptied and dropped meanwhile: nothing of the merge is live
        }

        var stillLive = sources.SelectMany(s => s.Live.Docs().Select(doc => s.Segment.Ids[doc])).ToHashSet(StringComparer.Ordinal);
        var live = LiveDocs.All(merged.DocCount).Without(merged.Ids.Select((id, doc) => (id, doc)).Where(x => !stillLive.Contains(x.id)).Select(x => x.doc));

        // The merged segment takes the place of the oldest source, so newer segments still override it.
        var firstSource = current.Segments.IndexOf(sources[0]);
        var remaining = current.Segments.Where(s => !plan.Segments.Contains(s.Segment)).ToList();
        remaining.Insert(Math.Min(firstSource, remaining.Count), new SegmentState(merged, live));
        return (current with { Segments = [.. remaining] }).Compact();
    }
}
