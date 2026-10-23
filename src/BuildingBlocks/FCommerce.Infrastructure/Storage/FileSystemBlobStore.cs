using FCommerce.Application.Storage;

namespace FCommerce.Infrastructure.Storage;

/// <summary>Blobs as files under a root directory. Writes go to a temporary file first, so readers never see partial content.</summary>
internal sealed class FileSystemBlobStore(string rootPath) : IBlobStore
{
    private readonly string root = Path.GetFullPath(rootPath);

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathOf(key);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    /// <summary>Keys are module-built, but never allow one to escape the root.</summary>
    private string PathOf(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException($"Blob key '{key}' resolves outside the store.", nameof(key));
    }
}
