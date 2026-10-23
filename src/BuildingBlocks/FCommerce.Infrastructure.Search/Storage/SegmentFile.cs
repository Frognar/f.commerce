using System.Runtime.InteropServices;
using System.Text;
using FCommerce.Search.Core.Index;

namespace FCommerce.Infrastructure.Search.Storage;

/// <summary>
/// Binary file format of a <see cref="Segment"/> (little-endian, version-tagged). A file is read with one read; document
/// sources are slices of that buffer, so loading copies nothing twice.
/// </summary>
internal static class SegmentFile
{
    private const int Magic = 0x47534346; // "FCSG"
    private const int FormatVersion = 1;

    public static async Task WriteAsync(string path, Segment segment, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(segment.DocCount);
            foreach (var id in segment.Ids)
            {
                writer.Write(id);
            }

            foreach (var source in segment.Sources)
            {
                writer.Write(source.Length);
                writer.Write(source.Span);
            }

            writer.Write(segment.FieldLengths.Length);
            foreach (var lengths in segment.FieldLengths)
            {
                writer.Write(MemoryMarshal.AsBytes(lengths.AsSpan()));
            }

            writer.Write(segment.Terms.Length);
            for (var t = 0; t < segment.Terms.Length; t++)
            {
                var postings = segment.Postings[t];
                writer.Write(segment.Terms[t]);
                writer.Write(postings.Count);
                writer.Write(MemoryMarshal.AsBytes(postings.Docs.AsSpan()));
                writer.Write(postings.Fields);
                writer.Write(MemoryMarshal.AsBytes(postings.Kinds.AsSpan()));
                writer.Write(postings.Frequencies);
            }

            writer.Write(segment.FilterValues.Length);
            for (var f = 0; f < segment.FilterValues.Length; f++)
            {
                writer.Write(segment.FilterValues[f]);
                writer.Write(segment.FilterDocs[f].Length);
                writer.Write(MemoryMarshal.AsBytes(segment.FilterDocs[f].AsSpan()));
            }

            foreach (var values in segment.DocFilterValues)
            {
                writer.Write(values.Length);
                writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
            }
        }

        await AtomicFile.WriteAsync(path, stream.ToArray(), cancellationToken);
    }

    public static async Task<Segment> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = await File.ReadAllBytesAsync(path, cancellationToken);
        using var stream = new MemoryStream(buffer, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
        {
            throw new InvalidDataException($"'{path}' is not a segment file of a supported version.");
        }

        var docCount = reader.ReadInt32();
        var ids = new string[docCount];
        for (var doc = 0; doc < docCount; doc++)
        {
            ids[doc] = reader.ReadString();
        }

        var sources = new ReadOnlyMemory<byte>[docCount];
        for (var doc = 0; doc < docCount; doc++)
        {
            var length = reader.ReadInt32();
            sources[doc] = buffer.AsMemory((int)stream.Position, length);
            stream.Position += length;
        }

        var fieldLengths = new ushort[reader.ReadInt32()][];
        for (var field = 0; field < fieldLengths.Length; field++)
        {
            fieldLengths[field] = ReadArray<ushort>(reader, docCount);
        }

        var terms = new string[reader.ReadInt32()];
        var postings = new Postings[terms.Length];
        for (var t = 0; t < terms.Length; t++)
        {
            terms[t] = reader.ReadString();
            var count = reader.ReadInt32();
            postings[t] = new Postings(ReadArray<int>(reader, count), reader.ReadBytes(count), ReadArray<TermKind>(reader, count), reader.ReadBytes(count));
        }

        var filterValues = new string[reader.ReadInt32()];
        var filterDocs = new int[filterValues.Length][];
        for (var f = 0; f < filterValues.Length; f++)
        {
            filterValues[f] = reader.ReadString();
            filterDocs[f] = ReadArray<int>(reader, reader.ReadInt32());
        }

        var docFilterValues = new int[docCount][];
        for (var doc = 0; doc < docCount; doc++)
        {
            docFilterValues[doc] = ReadArray<int>(reader, reader.ReadInt32());
        }

        return new Segment(ids, sources, terms, postings, fieldLengths, filterValues, filterDocs, docFilterValues);
    }

    private static T[] ReadArray<T>(BinaryReader reader, int count)
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(reader.ReadBytes(count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())).ToArray();
}

internal static class AtomicFile
{
    /// <summary>Writes to a temporary file, flushes it to disk and renames it over the target.</summary>
    public static async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await file.WriteAsync(content, cancellationToken);
            await file.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
