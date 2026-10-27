using FCommerce.Media.Domain;
using FCommerce.Media.Processing;
using FCommerce.Testing;
using SkiaSharp;

namespace FCommerce.Media.Tests;

public sealed class ImageRulesTests
{
    [Theory]
    [InlineData(1200, 800, 240, 160)]
    [InlineData(800, 1200, 160, 240)]
    [InlineData(100, 50, 100, 50)] // never enlarged
    [InlineData(5000, 3, 240, 1)] // at least one pixel
    public void Thumbnail_keeps_the_aspect_ratio_within_its_bound(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ImageVariant.Thumbnail.Fit(width, height));
    }

    [Fact]
    public void Image_ids_are_lowercase_sha256_hex()
    {
        Assert.Equal(new string('a', 64), ImageId.Parse(new string('A', 64)).ShouldSucceed().Value);
        Assert.Equal("image-id.invalid", ImageId.Parse("abc").ShouldFail().Code);
        Assert.Equal("image-id.invalid", ImageId.Parse(new string('g', 64)).ShouldFail().Code);
    }

    [Fact]
    public void Storage_keys_are_sharded_by_id_prefix()
    {
        var id = ImageId.Parse("ab" + new string('0', 62)).ShouldSucceed();

        Assert.Equal($"images/ab/{id}/thumb.webp", ImageVariant.Thumbnail.StorageKey(id));
    }

    [Fact]
    public void Size_limits_are_enforced()
    {
        Assert.Equal("image.empty", ImageRules.CheckSize(0).ShouldFail().Code);
        Assert.Equal("image.empty", ImageRules.CheckSize(-1).ShouldFail().Code);
        Assert.Equal(ImageRules.MaxBytes, ImageRules.CheckSize(ImageRules.MaxBytes).ShouldSucceed().Bytes);
        Assert.Equal("image.too-large", ImageRules.CheckSize(ImageRules.MaxBytes + 1).ShouldFail().Code);
        Assert.Equal("image.too-small", ImageRules.CheckDimensions(10, 100).ShouldFail().Code);
    }
}

public sealed class ImageProcessorTests
{
    internal static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Renders_every_variant_as_webp_without_enlarging()
    {
        var processed = ImageProcessor.Process(Png(1200, 800)).ShouldSucceed();

        Assert.Equal((1200, 800), (processed.Dimensions.Width, processed.Dimensions.Height));
        var sizes = processed.Variants.Select(v =>
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(v.Content));
            return (v.Variant.Name, codec.EncodedFormat, codec.Info.Width, codec.Info.Height);
        });
        Assert.Equal(
            [("thumb", SKEncodedImageFormat.Webp, 240, 160), ("medium", SKEncodedImageFormat.Webp, 720, 480), ("large", SKEncodedImageFormat.Webp, 1200, 800)],
            sizes);
    }

    [Fact]
    public void Rejects_content_that_is_not_a_supported_image()
    {
        Assert.Equal("image.unsupported-format", ImageProcessor.Process("not an image"u8.ToArray()).ShouldFail().Code);
    }

    [Fact]
    public void Rejects_tiny_images()
    {
        Assert.Equal("image.too-small", ImageProcessor.Process(Png(8, 8)).ShouldFail().Code);
    }
}
