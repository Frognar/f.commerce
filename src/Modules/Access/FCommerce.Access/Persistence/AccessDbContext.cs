using FCommerce.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Persistence;

internal sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public const string Schema = "access";

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<TokenRow> Tokens => Set<TokenRow>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddOutbox();

        modelBuilder.Entity<AccountRow>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).HasColumnName("id");
            account.Property(a => a.Email).HasColumnName("email").HasMaxLength(254);
            account.HasIndex(a => a.Email).IsUnique().HasDatabaseName("ux_accounts_email");
            account.Property(a => a.Name).HasColumnName("name").HasMaxLength(200);
            account.Property(a => a.Culture).HasColumnName("culture").HasMaxLength(16);
            account.Property(a => a.Status).HasColumnName("status").HasMaxLength(16);
            account.Property(a => a.PasswordHash).HasColumnName("password_hash").HasMaxLength(200);
            account.Property(a => a.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(64);
            account.Property(a => a.FailedSignIns).HasColumnName("failed_sign_ins");
            account.Property(a => a.LockedUntil).HasColumnName("locked_until");
            account.Property(a => a.Operator).HasColumnName("operator");
            account.Property(a => a.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<TokenRow>(token =>
        {
            token.ToTable("one_time_tokens");
            token.HasKey(t => t.Id);
            token.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            token.Property(t => t.AccountId).HasColumnName("account_id");
            token.Property(t => t.Purpose).HasColumnName("purpose").HasMaxLength(32);
            token.Property(t => t.Hash).HasColumnName("hash").HasMaxLength(64);
            token.HasIndex(t => t.Hash).IsUnique().HasDatabaseName("ux_one_time_tokens_hash");
            token.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            token.Property(t => t.UsedAt).HasColumnName("used_at");
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("audit_log");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entry.Property(e => e.At).HasColumnName("at");
            entry.Property(e => e.ActorId).HasColumnName("actor_id");
            entry.Property(e => e.Action).HasColumnName("action").HasMaxLength(100);
            entry.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(200);
            entry.Property(e => e.Details).HasColumnName("details");
            entry.HasIndex(e => e.Subject).HasDatabaseName("ix_audit_log_subject");
        });
    }
}
