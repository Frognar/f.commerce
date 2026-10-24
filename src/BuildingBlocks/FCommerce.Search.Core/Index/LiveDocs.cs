using System.Numerics;

namespace FCommerce.Search.Core.Index;

/// <summary>Immutable bit set of the documents of a segment that are not deleted.</summary>
public sealed class LiveDocs
{
    private readonly ulong[] words;

    private LiveDocs(ulong[] words, int size, int count)
    {
        this.words = words;
        Size = size;
        Count = count;
    }

    public int Size { get; }

    public int Count { get; }

    public static LiveDocs All(int size)
    {
        var words = new ulong[(size + 63) / 64];
        Array.Fill(words, ulong.MaxValue);
        if (size % 64 != 0 && words.Length > 0)
        {
            words[^1] = (1UL << (size % 64)) - 1;
        }

        return new LiveDocs(words, size, size);
    }

    public static LiveDocs FromWords(ulong[] words, int size) =>
        new([.. words], size, words.Sum(w => BitOperations.PopCount(w)));

    public bool IsLive(int doc) => (words[doc >> 6] & (1UL << (doc & 63))) != 0;

    /// <summary>A copy with the given documents deleted (the same instance when nothing changes).</summary>
    public LiveDocs Without(IEnumerable<int> docs)
    {
        // Copy on the first change only; the copy then also tells whether a document was already deleted.
        var copy = words;
        var copied = false;
        var count = Count;
        foreach (var doc in docs)
        {
            if ((copy[doc >> 6] & (1UL << (doc & 63))) == 0)
            {
                continue;
            }

            if (!copied)
            {
                copy = [.. words];
                copied = true;
            }

            copy[doc >> 6] &= ~(1UL << (doc & 63));
            count--;
        }

        return copied ? new LiveDocs(copy, Size, count) : this;
    }

    public ReadOnlySpan<ulong> Words => words;

    public IEnumerable<int> Docs()
    {
        for (var doc = 0; doc < Size; doc++)
        {
            if (IsLive(doc))
            {
                yield return doc;
            }
        }
    }
}
