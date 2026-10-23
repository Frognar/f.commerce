using FCommerce.SharedKernel.Results;

namespace FCommerce.Media.Domain;

/// <summary>A display variant: the longest side is bounded; images are never enlarged.</summary>
public sealed record ImageVariant(string Name, int MaxSide)
{
    public static readonly ImageVariant Thumbnail = new("thumb", 240);
    public static readonly ImageVariant Medium = new("medium", 720);
    public static readonly ImageVariant Large = new("large", 1600);

    public static IReadOnlyList<ImageVariant> All { get; } = [Thumbnail, Medium, Large];

    public static Result<ImageVariant, Error> Parse(string? name) =>
        All.FirstOrDefault(v => v.Name == name) is { } variant
            ? variant
            : Error.NotFound("image-variant.unknown", $"Unknown image variant '{name}'.");

    /// <summary>Target size keeping the aspect ratio; at least 1 px per side.</summary>
    public (int Width, int Height) Fit(int width, int height)
    {
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public string StorageKey(ImageId id) => $"{id.StoragePrefix}/{Name}.webp";
}

/// <summary>Limits for uploaded images.</summary>
public static class ImageRules
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxSide = 10_000;
    public const int MinSide = 16;

    public static Result<int, Error> CheckSize(int bytes) =>
        bytes switch
        {
            0 => Error.Validation("image.empty", "The image is empty."),
            > MaxBytes => Error.Validation("image.too-large", $"The image must not exceed {MaxBytes / (1024 * 1024)} MB."),
            _ => bytes,
        };

    public static Result<(int Width, int Height), Error> CheckDimensions(int width, int height) =>
        width < MinSide || height < MinSide
            ? Error.Validation("image.too-small", $"The image must be at least {MinSide}×{MinSide} px.")
            : width > MaxSide || height > MaxSide
                ? Error.Validation("image.too-big", $"The image must not exceed {MaxSide}×{MaxSide} px.")
                : (width, height);
}
