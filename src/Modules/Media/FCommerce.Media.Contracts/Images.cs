using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Media.Contracts;

/// <summary>
/// Stores an image (JPEG, PNG or WebP) and its display variants. The id is derived from the content, so uploading
/// the same file again returns the same id without storing anything.
/// </summary>
public sealed record UploadImage(ReadOnlyMemory<byte> Content) : ICommand<Result<UploadedImage, Error>>;

/// <param name="Id">Content-derived image id, used to reference the image from other modules (e.g. products).</param>
public sealed record UploadedImage(string Id, int Width, int Height, bool AlreadyStored);

/// <summary>Which of the given image ids are stored (read-only check for modules that reference images).</summary>
public sealed record FindStoredImages(IReadOnlyList<string> Ids) : IQuery<IReadOnlySet<string>>;

/// <summary>Opens a display variant of a stored image.</summary>
public sealed record OpenImage(string Id, string Variant) : IQuery<Result<ImageContent, Error>>;

public sealed record ImageContent(Stream Content, string ContentType);

/// <summary>Display variants, by name as used in URLs.</summary>
public static class ImageVariantNames
{
    public const string Thumbnail = "thumb";
    public const string Medium = "medium";
    public const string Large = "large";
}
