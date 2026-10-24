using Microsoft.EntityFrameworkCore;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Outbox;

/// <summary>An integration event stored in the same transaction as the state change that produced it.</summary>
[NullableEdge]
public sealed class OutboxMessage
{
    public long Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

public static class OutboxModel
{
    /// <summary>Adds the outbox table to a module's model (in the module's schema).</summary>
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(message =>
        {
            message.ToTable("outbox_messages");
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).HasColumnName("id").ValueGeneratedOnAdd();
            message.Property(m => m.Type).HasColumnName("type").HasMaxLength(200);
            message.Property(m => m.Payload).HasColumnName("payload");
            message.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            message.Property(m => m.ProcessedAt).HasColumnName("processed_at");
            message.Property(m => m.Attempts).HasColumnName("attempts");
            message.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(2000);
            message.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_outbox_messages_processed_at");
        });
        return modelBuilder;
    }
}
