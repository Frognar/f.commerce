using FCommerce.Media.Domain;
using FCommerce.SharedKernel.Results;
using SkiaSharp;

namespace FCommerce.Media.Processing;

/// <summary>A decoded upload with its WebP display variants.</summary>
internal sealed record ProcessedImage(int Width, int Height, IReadOnlyList<(ImageVariant Variant, byte[] Content)> Variants);

/// <summary>Decodes JPEG/PNG/WebP, applies EXIF orientation and renders every <see cref="ImageVariant"/> as WebP.</summary>
internal static class ImageProcessor
{
    private const int WebpQuality = 82;

    public static Result<ProcessedImage, Error> Process(ReadOnlyMemory<byte> content)
    {
        using var data = SKData.CreateCopy(content.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
        {
            return Error.Validation("image.unsupported-format", "Only JPEG, PNG and WebP images are supported.");
        }

        var (width, height) = Oriented(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
        return ImageRules.CheckDimensions(width, height).Bind(_ => Render(codec, width, height));
    }

    private static Result<ProcessedImage, Error> Render(SKCodec codec, int width, int height)
    {
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return Error.Validation("image.corrupt", "The image could not be decoded.");
        }

        using var upright = ApplyOrigin(decoded, codec.EncodedOrigin);
        var variants = ImageVariant.All.Select(variant => (variant, Encode(upright, variant.Fit(width, height)))).ToList();
        return new ProcessedImage(width, height, variants);
    }

    private static byte[] Encode(SKBitmap source, (int Width, int Height) size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White); // flatten transparency for consistent display
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, size.Width, size.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        return encoded.ToArray();
    }

    private static (int Width, int Height) Oriented(int width, int height, SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
            ? (height, width)
            : (width, height);

    /// <summary>Rotates/flips pixels so the image is upright regardless of the camera's EXIF orientation.</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return source.Copy();
        }

        var (width, height) = Oriented(source.Width, source.Height, origin);
        var result = new SKBitmap(width, height);
        using var canvas = new SKCanvas(result);

        // Each case maps a source pixel (x, y) to its upright position in the result (width × height).
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // mirror horizontally: (W - x, y)
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // rotate 180°: (W - x, H - y)
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft: // mirror vertically: (x, H - y)
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop: // transpose: (y, x)
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop: // rotate 90° clockwise: (W - y, x)
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transverse: (W - y, H - x)
                canvas.Translate(width, height);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom: // rotate 90° counter-clockwise: (y, H - x)
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;
        }

        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }
}
