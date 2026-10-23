using System.Collections.Immutable;

namespace FCommerce.Search.Core.Index;

public sealed record SegmentState(Segment Segment, LiveDocs Live);

/// <summary>
/// An immutable, consistent view of an index: its segments with their live documents. Every change produces a new
/// snapshot, so searches never see a half-applied write and never need a lock.
/// </summary>
public sealed record IndexSnapshot(IndexSchema Schema, ImmutableArray<SegmentState> Segments)
{
    public static IndexSnapshot Empty(IndexSchema schema) => new(schema, []);

    public int Count => Segments.Sum(s => s.Live.Count);

    /// <summary>Adds a segment; older live copies of its documents are deleted.</summary>
    public IndexSnapshot Add(Segment segment) =>
        this with { Segments = [.. Without(segment.Ids).Segments, new SegmentState(segment, LiveDocs.All(segment.DocCount))] };

    /// <summary>Deletes documents by id (unknown ids are ignored).</summary>
    public IndexSnapshot Without(IEnumerable<string> ids)
    {
        var idList = ids as IReadOnlyCollection<string> ?? [.. ids];
        return this with
        {
            Segments = [.. Segments.Select(state => state with
            {
                Live = state.Live.Without(idList.Select(id => state.Segment.DocById.GetValueOrDefault(id, -1)).Where(doc => doc >= 0)),
            })],
        };
    }

    /// <summary>Drops segments without live documents.</summary>
    public IndexSnapshot Compact() => this with { Segments = [.. Segments.Where(s => s.Live.Count > 0)] };
}
