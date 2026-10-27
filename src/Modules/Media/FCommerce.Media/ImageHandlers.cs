using System.Security.Cryptography;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Application.Storage;
using FCommerce.Media.Contracts;
using FCommerce.Media.Domain;
using FCommerce.Media.Persistence;
using FCommerce.Media.Processing;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using FCommerce.SharedKernel.Optional;
using FCommerce.Infrastructure.Persistence;

namespace FCommerce.Media;

/// <summary>Size check → content id → (already stored? done) → decode and render variants → store blobs, then metadata.</summary>
internal sealed class UploadImageHandler(MediaDbContext db, IBlobStore blobs, TimeProvider time)
    : ICommandHandler<UploadImage, Result<UploadedImage, Error>>
{
    public Task<Result<UploadedImage, Error>> HandleAsync(UploadImage command, CancellationToken cancellationToken = default) =>
        (from size in ImageRules.CheckSize(command.Content.Length)
         from id in ImageId.Parse(Convert.ToHexStringLower(SHA256.HashData(command.Content.Span)))
         select (id, size))
            .BindAsync(upload => StoreAsync(upload.id, upload.size, command.Content, cancellationToken));

    private async Task<Result<UploadedImage, Error>> StoreAsync(ImageId id, ImageFileSize size, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var existing = await db.Images.AsNoTracking().SingleOrNoneAsync(i => i.Id == id.Value, cancellationToken);
        return await existing.Match(
            stored => Task.FromResult(Result.Ok<UploadedImage, Error>(new UploadedImage(id.Value, stored.Width, stored.Height, AlreadyStored: true))),
            () => ProcessAsync(id, size, content, cancellationToken));
    }

    private Task<Result<UploadedImage, Error>> ProcessAsync(ImageId id, ImageFileSize size, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        ImageProcessor.Process(content).MapAsync(async processed =>
        {
            // Blobs first: metadata only appears once every variant is readable.
            await blobs.PutAsync($"{id.StoragePrefix}/original", content, cancellationToken);
            foreach (var (variant, bytes) in processed.Variants)
            {
                await blobs.PutAsync(variant.StorageKey(id), bytes, cancellationToken);
            }

            var image = new StoredImage(id, processed.Dimensions, size);
            db.Images.Add(ImageRow.From(image, time.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            return new UploadedImage(id.Value, image.Dimensions.Width, image.Dimensions.Height, AlreadyStored: false);
        });
}

internal sealed class FindStoredImagesHandler(MediaDbContext db) : IQueryHandler<FindStoredImages, IReadOnlySet<string>>
{
    public async Task<IReadOnlySet<string>> HandleAsync(FindStoredImages query, CancellationToken cancellationToken = default)
    {
        var ids = query.Ids.Choose(raw => ImageId.Parse(raw).ToMaybe().Map(id => id.Value)).Distinct().ToList();
        var stored = await db.Images.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken);
        return stored.ToHashSet(StringComparer.Ordinal);
    }
}

internal sealed class OpenImageHandler(IBlobStore blobs) : IQueryHandler<OpenImage, Result<ImageContent, Error>>
{
    public Task<Result<ImageContent, Error>> HandleAsync(OpenImage query, CancellationToken cancellationToken = default) =>
        (from id in ImageId.Parse(query.Id) from variant in ImageVariant.Parse(query.Variant) select variant.StorageKey(id))
            .BindAsync(async key => (await blobs.OpenReadAsync(key, cancellationToken)).Match(
                stream => Result.Ok<ImageContent, Error>(new ImageContent(stream, "image/webp")),
                () => Error.NotFound("image.not-found", $"Image '{query.Id}' does not exist.")));
}
