using System.Text.Json;
using FCommerce.Search.Core.Analysis;
using FCommerce.Search.Core.Index;
using Microsoft.Extensions.Logging;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Search.Storage;

/// <summary>What is on disk for an index; replaced atomically after every change.</summary>
internal sealed record Manifest(int AnalyzerVersion, string Language, string Schema, int NextSegment, IReadOnlyList<ManifestSegment> Segments);

/// <param name="Live">File with the live-documents bit set; null when every document is live (stored JSON).</param>
[NullableEdge]
internal sealed record ManifestSegment(string File, int DocCount, string? Live);

/// <summary>
/// One index on disk: the shell around the pure <see cref="IndexSnapshot"/>. Writes are serialized; each write builds
/// a segment, persists it with a new manifest and only then publishes the new snapshot. Readers just take the current
/// snapshot. Small segments are merged in the background.
/// </summary>
internal sealed partial class IndexStore : IAsyncDisposable
{
    private const string ManifestFile = "manifest.json";

    private readonly string directory;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Lock mergeGate = new();
    private readonly ILogger logger;
    private Manifest manifest;
    private volatile IndexSnapshot snapshot;
    private Task merging = Task.CompletedTask;

    private IndexStore(string directory, Analyzer analyzer, Manifest manifest, IndexSnapshot snapshot, ILogger logger)
    {
        this.directory = directory;
        Analyzer = analyzer;
        this.manifest = manifest;
        this.snapshot = snapshot;
        this.logger = logger;
    }

    public Analyzer Analyzer { get; }

    public IndexSnapshot Snapshot => snapshot;

    /// <summary>
    /// Opens the index in <paramref name="directory"/>, or starts an empty one when there is none or when it was built
    /// with another schema, language or analyzer version (its content would be inconsistent; the owner re-indexes).
    /// </summary>
    public static async Task<IndexStore> OpenAsync(string directory, IndexSchema schema, string language, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var analyzer = Analyzers.For(language);
        var expected = new Manifest(Analyzer.Version, language, schema.Signature, 1, []);
        var path = Path.Combine(directory, ManifestFile);

        var stored = File.Exists(path)
            ? JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(path, cancellationToken)).ToMaybe()
            : Maybe<Manifest>.None;
        return await stored
            .Where(m => m.AnalyzerVersion == expected.AnalyzerVersion && m.Language == language && m.Schema == expected.Schema)
            .Match(
                manifest => LoadAsync(directory, analyzer, manifest, schema, logger, cancellationToken),
                async () =>
                {
                    stored.Switch(_ => LogReset(logger, directory), () => { });
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        File.Delete(file);
                    }

                    await WriteManifestAsync(directory, expected, cancellationToken);
                    return new IndexStore(directory, analyzer, expected, IndexSnapshot.Empty(schema), logger);
                });
    }

    private static async Task<IndexStore> LoadAsync(string directory, Analyzer analyzer, Manifest manifest, IndexSchema schema, ILogger logger, CancellationToken cancellationToken)
    {
        var segments = new List<SegmentState>();
        foreach (var entry in manifest.Segments)
        {
            var segment = await SegmentFile.ReadAsync(Path.Combine(directory, entry.File), cancellationToken);
            var live = await entry.Live.ToMaybe().Match(
                async file => LiveDocs.FromWords(await ReadWordsAsync(Path.Combine(directory, file), cancellationToken), segment.DocCount),
                () => Task.FromResult(LiveDocs.All(segment.DocCount)));
            segments.Add(new SegmentState(segment, live));
        }

        return new IndexStore(directory, analyzer, manifest, new IndexSnapshot(schema, [.. segments]), logger);
    }

    public Task UpsertAsync(IReadOnlyList<IndexDocument> documents, CancellationToken cancellationToken) =>
        documents.Count == 0
            ? Task.CompletedTask
            : WriteAsync(current => current.Add(SegmentBuilder.Build(current.Schema, Analyzer, documents)), cancellationToken);

    public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? Task.CompletedTask : WriteAsync(current => current.Without(ids).Compact(), cancellationToken);

    /// <summary>Waits for a running background merge (tests, shutdown).</summary>
    public Task WaitForMergesAsync() => merging;

    public async ValueTask DisposeAsync()
    {
        await merging;
        writeLock.Dispose();
    }

    private async Task WriteAsync(Func<IndexSnapshot, IndexSnapshot> change, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await PublishAsync(change(snapshot), cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }

        ScheduleMerge();
    }

    /// <summary>Persists every segment and live set the manifest does not know yet, then the manifest, then publishes.</summary>
    private async Task PublishAsync(IndexSnapshot next, CancellationToken cancellationToken)
    {
        var known = manifest.Segments.Zip(snapshot.Segments, (entry, state) => (state.Segment, entry)).ToDictionary(x => x.Segment, x => x.entry);
        var nextSegment = manifest.NextSegment;
        var entries = new List<ManifestSegment>();
        foreach (var state in next.Segments)
        {
            var entry = await known.GetValueOrNone(state.Segment).Match(Task.FromResult, async () =>
            {
                var name = $"seg-{nextSegment++:D6}";
                await SegmentFile.WriteAsync(Path.Combine(directory, $"{name}.bin"), state.Segment, cancellationToken);
                return new ManifestSegment($"{name}.bin", state.Segment.DocCount, Live: null);
            });

            var liveChanged = !snapshot.Segments.FirstOrNone(s => s.Segment == state.Segment).Exists(s => ReferenceEquals(s.Live, state.Live));
            if (state.Live.Count != state.Segment.DocCount && liveChanged)
            {
                var liveName = $"{Path.GetFileNameWithoutExtension(entry.File)}.live-{nextSegment++:D6}";
                await AtomicFile.WriteAsync(Path.Combine(directory, liveName), [.. System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Live.Words)], cancellationToken);
                entry = entry with { Live = liveName };
            }

            entries.Add(entry);
        }

        var nextManifest = manifest with { NextSegment = nextSegment, Segments = entries };
        await WriteManifestAsync(directory, nextManifest, cancellationToken);
        manifest = nextManifest;
        snapshot = next;
        DeleteUnreferencedFiles();
    }

    /// <summary>
    /// Starts a background merge unless one is running. The merge keeps going while writes made in the meantime
    /// leave too many segments. Only applying the result takes the write lock.
    /// </summary>
    private void ScheduleMerge()
    {
        lock (mergeGate)
        {
            if (!merging.IsCompleted || Merging.Plan(snapshot).Match(_ => false, () => true))
            {
                return;
            }

            merging = Task.Run(MergeWhileNeededAsync);
        }
    }

    private async Task MergeWhileNeededAsync()
    {
        try
        {
            await MergeAsync();
        }
        catch (Exception exception)
        {
            LogMergeFailed(logger, exception, directory);
        }
    }

    /// <summary>Merges as long as there are too many segments (writes may add segments while a merge runs).</summary>
    private Task MergeAsync() =>
        Merging.Plan(snapshot).Match(
            async plan =>
            {
                var merged = Merging.Build(snapshot, Analyzer, plan);
                await writeLock.WaitAsync();
                try
                {
                    await PublishAsync(Merging.Apply(snapshot, plan, merged), CancellationToken.None);
                }
                finally
                {
                    writeLock.Release();
                }

                await MergeAsync();
            },
            () => Task.CompletedTask);

    private void DeleteUnreferencedFiles()
    {
        var referenced = manifest.Segments.SelectMany(s => s.Live.ToMaybe().AsEnumerable().Prepend(s.File)).Append(ManifestFile).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!referenced.Contains(name) && !name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }
    }

    private static Task WriteManifestAsync(string directory, Manifest manifest, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(Path.Combine(directory, ManifestFile), JsonSerializer.SerializeToUtf8Bytes(manifest), cancellationToken);

    private static async Task<ulong[]> ReadWordsAsync(string path, CancellationToken cancellationToken) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(await File.ReadAllBytesAsync(path, cancellationToken)).ToArray();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search index in {Directory} was built differently (schema, language or analyzer changed); it starts empty and must be re-indexed")]
    private static partial void LogReset(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Merging segments of the search index in {Directory} failed; it keeps working unmerged")]
    private static partial void LogMergeFailed(ILogger logger, Exception exception, string directory);
}
