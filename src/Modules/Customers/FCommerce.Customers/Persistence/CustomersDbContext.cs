using FCommerce.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Customers.Persistence;

internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public const string Schema = "customers";

    public DbSet<CustomerRow> Customers => Set<CustomerRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddOutbox();

        modelBuilder.Entity<CustomerRow>(customer =>
        {
            customer.ToTable("customers");
            customer.HasKey(c => c.Code);
            customer.Property(c => c.Code).HasColumnName("code").HasMaxLength(64);
            customer.Property(c => c.Version).HasColumnName("version");
            customer.Property(c => c.Name).HasColumnName("name").HasMaxLength(300);
            customer.Property(c => c.TaxId).HasColumnName("tax_id").HasMaxLength(14);
            customer.Property(c => c.City).HasColumnName("city").HasMaxLength(100);
            customer.Property(c => c.SalespersonEmail).HasColumnName("salesperson_email").HasMaxLength(254);
            customer.Property(c => c.Active).HasColumnName("active");
            customer.Property(c => c.Blocked).HasColumnName("blocked");
            customer.Property(c => c.Content).HasColumnName("content");
            customer.Property(c => c.SearchText).HasColumnName("search_text");
            customer.Property(c => c.UpdatedAt).HasColumnName("updated_at");
            customer.HasIndex(c => c.TaxId).HasDatabaseName("ix_customers_tax_id");
            customer.HasIndex(c => c.SalespersonEmail).HasDatabaseName("ix_customers_salesperson_email");
            customer.HasIndex(c => c.Name).HasDatabaseName("ix_customers_name");
        });
    }
}
