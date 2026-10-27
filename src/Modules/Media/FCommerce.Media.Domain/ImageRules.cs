using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Media.Domain;

/// <summary>A display variant: the longest side is bounded; images are never enlarged. Only the listed ones exist.</summary>
public sealed record ImageVariant
{
    public static readonly ImageVariant Thumbnail = new("thumb", 240);
    public static readonly ImageVariant Medium = new("medium", 720);
    public static readonly ImageVariant Large = new("large", 1600);

    private ImageVariant(string name, int maxSide)
    {
        Name = name;
        MaxSide = maxSide;
    }

    public string Name { get; }

    public int MaxSide { get; }

    public static IReadOnlyList<ImageVariant> All { get; } = [Thumbnail, Medium, Large];

    public static Result<ImageVariant, Error> Parse(string name) =>
        All.FirstOrNone(v => v.Name == name)
            .ToResult(() => Error.NotFound("image-variant.unknown", $"Unknown image variant '{name}'."));

    /// <summary>Target size keeping the aspect ratio; at least 1 px per side.</summary>
    public (int Width, int Height) Fit(int width, int height)
    {
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public (int Width, int Height) Fit(ImageDimensions dimensions) => Fit(dimensions.Width, dimensions.Height);

    public string StorageKey(ImageId id) => $"{id.StoragePrefix}/{Name}.webp";
}

/// <summary>The size of an uploaded file: not empty, at most <see cref="ImageRules.MaxBytes"/>.</summary>
public sealed record ImageFileSize
{
    private ImageFileSize(int bytes) => Bytes = bytes;

    public int Bytes { get; }

    internal static Result<ImageFileSize, Error> Parse(int bytes) =>
        bytes switch
        {
            <= 0 => Error.Validation("image.empty", "The image is empty."),
            > ImageRules.MaxBytes => Error.Validation("image.too-large", $"The image must not exceed {ImageRules.MaxBytes / (1024 * 1024)} MB."),
            _ => new ImageFileSize(bytes),
        };
}

/// <summary>The upright size of an image, each side within <see cref="ImageRules.MinSide"/>–<see cref="ImageRules.MaxSide"/> px.</summary>
public sealed record ImageDimensions
{
    private ImageDimensions(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    internal static Result<ImageDimensions, Error> Parse(int width, int height) =>
        width < ImageRules.MinSide || height < ImageRules.MinSide
            ? Error.Validation("image.too-small", $"The image must be at least {ImageRules.MinSide}×{ImageRules.MinSide} px.")
            : width > ImageRules.MaxSide || height > ImageRules.MaxSide
                ? Error.Validation("image.too-big", $"The image must not exceed {ImageRules.MaxSide}×{ImageRules.MaxSide} px.")
                : new ImageDimensions(width, height);
}

/// <summary>An image accepted into the store: its content id, upright size and the size of the original file.</summary>
public sealed record StoredImage(ImageId Id, ImageDimensions Dimensions, ImageFileSize Original);

/// <summary>Limits for uploaded images.</summary>
public static class ImageRules
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxSide = 10_000;
    public const int MinSide = 16;

    public static Result<ImageFileSize, Error> CheckSize(int bytes) => ImageFileSize.Parse(bytes);

    public static Result<ImageDimensions, Error> CheckDimensions(int width, int height) => ImageDimensions.Parse(width, height);
}
