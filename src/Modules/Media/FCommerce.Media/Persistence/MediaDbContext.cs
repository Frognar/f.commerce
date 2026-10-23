using Microsoft.EntityFrameworkCore;

namespace FCommerce.Media.Persistence;

internal sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    public const string Schema = "media";

    public DbSet<ImageRow> Images => Set<ImageRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ImageRow>(image =>
        {
            image.ToTable("images");
            image.HasKey(i => i.Id);
            image.Property(i => i.Id).HasColumnName("id").HasMaxLength(64).IsFixedLength();
            image.Property(i => i.Width).HasColumnName("width");
            image.Property(i => i.Height).HasColumnName("height");
            image.Property(i => i.OriginalBytes).HasColumnName("original_bytes");
            image.Property(i => i.CreatedAt).HasColumnName("created_at");
        });
    }
}

/// <summary>Metadata of a stored image; the content lives in the blob store under the image's storage prefix.</summary>
internal sealed class ImageRow
{
    public required string Id { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int OriginalBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
