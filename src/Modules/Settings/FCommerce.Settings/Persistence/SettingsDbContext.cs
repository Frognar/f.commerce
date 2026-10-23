using FCommerce.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Settings.Persistence;

internal sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options) : DbContext(options)
{
    public const string Schema = "settings";

    public DbSet<SettingsRow> Settings => Set<SettingsRow>();

    public DbSet<SettingsHistoryRow> History => Set<SettingsHistoryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddOutbox();

        modelBuilder.Entity<SettingsRow>(settings =>
        {
            settings.ToTable("platform_settings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            settings.Property(s => s.Version).HasColumnName("version").IsConcurrencyToken();
            settings.Property(s => s.Document).HasColumnName("document");
            settings.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            settings.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<SettingsHistoryRow>(history =>
        {
            history.ToTable("settings_history");
            history.HasKey(h => h.Id);
            history.Property(h => h.Id).HasColumnName("id").ValueGeneratedOnAdd();
            history.Property(h => h.Version).HasColumnName("version");
            history.Property(h => h.At).HasColumnName("at");
            history.Property(h => h.ActorId).HasColumnName("actor_id");
            history.Property(h => h.Changes).HasColumnName("changes");
            history.Property(h => h.Document).HasColumnName("document");
        });
    }
}

/// <summary>The single settings row (id 1); the document is JSON of <see cref="SettingsDocument"/>.</summary>
internal sealed class SettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;

    public long Version { get; set; }

    public required string Document { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>Every saved version, with what changed and the full document (who changed what, and back to which value).</summary>
internal sealed class SettingsHistoryRow
{
    public long Id { get; init; }

    public long Version { get; init; }

    public DateTimeOffset At { get; init; }

    public Guid? ActorId { get; init; }

    public required string Changes { get; init; }

    public required string Document { get; init; }
}
