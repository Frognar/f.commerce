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

namespace FCommerce.Media;

/// <summary>Size check → content id → (already stored? done) → decode and render variants → store blobs, then metadata.</summary>
internal sealed class UploadImageHandler(MediaDbContext db, IBlobStore blobs, TimeProvider time)
    : ICommandHandler<UploadImage, Result<UploadedImage, Error>>
{
    public Task<Result<UploadedImage, Error>> HandleAsync(UploadImage command, CancellationToken cancellationToken = default) =>
        ImageRules.CheckSize(command.Content.Length)
            .Bind(_ => ImageId.Parse(Convert.ToHexStringLower(SHA256.HashData(command.Content.Span))))
            .BindAsync(id => StoreAsync(id, command.Content, cancellationToken));

    private async Task<Result<UploadedImage, Error>> StoreAsync(ImageId id, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var existing = await db.Images.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id.Value, cancellationToken);
        if (existing is not null)
        {
            return new UploadedImage(id.Value, existing.Width, existing.Height, AlreadyStored: true);
        }

        return await ImageProcessor.Process(content).MapAsync(async processed =>
        {
            // Blobs first: metadata only appears once every variant is readable.
            await blobs.PutAsync($"{id.StoragePrefix}/original", content, cancellationToken);
            foreach (var (variant, bytes) in processed.Variants)
            {
                await blobs.PutAsync(variant.StorageKey(id), bytes, cancellationToken);
            }

            db.Images.Add(new ImageRow { Id = id.Value, Width = processed.Width, Height = processed.Height, OriginalBytes = content.Length, CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            return new UploadedImage(id.Value, processed.Width, processed.Height, AlreadyStored: false);
        });
    }
}

internal sealed class FindStoredImagesHandler(MediaDbContext db) : IQueryHandler<FindStoredImages, IReadOnlySet<string>>
{
    public async Task<IReadOnlySet<string>> HandleAsync(FindStoredImages query, CancellationToken cancellationToken = default)
    {
        var ids = query.Ids.Select(raw => ImageId.Parse(raw).Match(id => (string?)id.Value, _ => null)).OfType<string>().Distinct().ToList();
        var stored = await db.Images.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken);
        return stored.ToHashSet(StringComparer.Ordinal);
    }
}

internal sealed class OpenImageHandler(IBlobStore blobs) : IQueryHandler<OpenImage, Result<ImageContent, Error>>
{
    public Task<Result<ImageContent, Error>> HandleAsync(OpenImage query, CancellationToken cancellationToken = default) =>
        (from id in ImageId.Parse(query.Id) from variant in ImageVariant.Parse(query.Variant) select variant.StorageKey(id))
            .BindAsync(async key => await blobs.OpenReadAsync(key, cancellationToken) is { } stream
                ? Result.Ok<ImageContent, Error>(new ImageContent(stream, "image/webp"))
                : Error.NotFound("image.not-found", $"Image '{query.Id}' does not exist."));
}
