using FCommerce.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Persistence;

internal sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public const string Schema = "access";

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<TokenRow> Tokens => Set<TokenRow>();

    public DbSet<MembershipRow> Memberships => Set<MembershipRow>();

    public DbSet<RegistrationRow> Registrations => Set<RegistrationRow>();

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

        modelBuilder.Entity<MembershipRow>(membership =>
        {
            membership.ToTable("memberships");
            membership.HasKey(m => new { m.UserId, m.CustomerCode });
            membership.Property(m => m.UserId).HasColumnName("user_id");
            membership.Property(m => m.CustomerCode).HasColumnName("customer_code").HasMaxLength(64);
            membership.Property(m => m.Attributes).HasColumnName("attributes");
            membership.Property(m => m.Source).HasColumnName("source").HasMaxLength(16);
            membership.Property(m => m.ErpContactId).HasColumnName("erp_contact_id").HasMaxLength(64);
            membership.Property(m => m.CreatedAt).HasColumnName("created_at");
            membership.HasIndex(m => m.CustomerCode).HasDatabaseName("ix_memberships_customer_code");
            membership.HasOne<AccountRow>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RegistrationRow>(registration =>
        {
            registration.ToTable("registration_requests");
            registration.HasKey(r => r.Id);
            registration.Property(r => r.Id).HasColumnName("id");
            registration.Property(r => r.Status).HasColumnName("status").HasMaxLength(16);
            registration.Property(r => r.CompanyName).HasColumnName("company_name").HasMaxLength(300);
            registration.Property(r => r.TaxId).HasColumnName("tax_id").HasMaxLength(14);
            registration.Property(r => r.Email).HasColumnName("email").HasMaxLength(254);
            registration.Property(r => r.Form).HasColumnName("form");
            registration.Property(r => r.SubmittedAt).HasColumnName("submitted_at");
            registration.Property(r => r.DecidedAt).HasColumnName("decided_at");
            registration.Property(r => r.DecidedBy).HasColumnName("decided_by");
            registration.Property(r => r.CustomerCode).HasColumnName("customer_code").HasMaxLength(64);
            registration.Property(r => r.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(1000);
            registration.HasIndex(r => new { r.Status, r.SubmittedAt }).HasDatabaseName("ix_registration_requests_status");
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
