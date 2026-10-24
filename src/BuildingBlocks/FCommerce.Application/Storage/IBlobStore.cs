using FCommerce.SharedKernel.Optional;

namespace FCommerce.Application.Storage;

/// <summary>
/// Port for binary content (images, documents). Keys are slash-separated paths chosen by the owning module,
/// e.g. "images/ab/abcdef…/thumb.webp". Implementations: local file system today, object storage later.
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores the content under the key, replacing any previous content.</summary>
    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Opens the content for reading; none when the key does not exist.</summary>
    Task<Maybe<Stream>> OpenReadAsync(string key, CancellationToken cancellationToken = default);
}
