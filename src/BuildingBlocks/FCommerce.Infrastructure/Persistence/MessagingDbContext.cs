using Microsoft.EntityFrameworkCore;

namespace FCommerce.Infrastructure.Persistence;

/// <summary>Shared messaging tables (schema "messaging"). Provider and migrations come from the database adapter project.</summary>
public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";

    internal DbSet<SubscriptionCheckpoint> Checkpoints => Set<SubscriptionCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<SubscriptionCheckpoint>(checkpoint =>
        {
            checkpoint.ToTable("subscription_checkpoints");
            checkpoint.HasKey(c => c.SubscriptionName);
            checkpoint.Property(c => c.SubscriptionName).HasColumnName("subscription_name").HasMaxLength(200);
            checkpoint.Property(c => c.Position).HasColumnName("position");
            checkpoint.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        });
    }
}

internal sealed class SubscriptionCheckpoint
{
    public required string SubscriptionName { get; init; }

    public long Position { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
