using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Integration;
using FCommerce.Media.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace FCommerce.Host.Media;

internal static class MediaEndpoints
{
    /// <summary>Slightly above the media module's own limit, so it reports the size error instead of the server.</summary>
    private const long MaxRequestBytes = 16 * 1024 * 1024;

    public static string ImageUrl(string imageId, string variant) => $"/media/images/{imageId}/{variant}";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/integration/v1/media/images", UploadAsync)
            .AddEndpointFilter<IntegrationApiKeyFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .Accepts<byte[]>("image/jpeg", "image/png", "image/webp")
            .WithTags("Integration")
            .WithSummary("Uploads an image (raw body); returns its content-derived id for product imageIds.");

        // Content-addressed: a URL never changes its content, so it may be cached forever.
        app.MapGet("/media/images/{id}/{variant}", OpenAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, ICommandHandler<UploadImage, Result<UploadedImage, Error>> upload, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var result = await upload.HandleAsync(new UploadImage(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)), ct);
        return result.ToHttpResult(image => image.AlreadyStored ? Results.Ok(image) : Results.Created(ImageUrl(image.Id, ImageVariantNames.Large), image));
    }

    private static async Task<IResult> OpenAsync(string id, string variant, HttpContext context, IQueryHandler<OpenImage, Result<ImageContent, Error>> images, CancellationToken ct) =>
        (await images.HandleAsync(new OpenImage(id, variant), ct)).Match(
            image =>
            {
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.Stream(image.Content, image.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}-{variant}\""));
            },
            error => Results.NotFound());
}
