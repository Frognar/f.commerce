using FCommerce.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Persistence;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";

    public DbSet<ProductRow> Products => Set<ProductRow>();

    public DbSet<CategoryRow> Categories => Set<CategoryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddOutbox();

        modelBuilder.Entity<ProductRow>(product =>
        {
            product.ToTable("products");
            product.HasKey(p => p.Sku);
            product.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(64);
            product.Property(p => p.Version).HasColumnName("version");
            product.Property(p => p.Name).HasColumnName("name");
            product.Property(p => p.Description).HasColumnName("description");
            product.Property(p => p.CategoryCode).HasColumnName("category_code").HasMaxLength(64);
            product.Property(p => p.Unit).HasColumnName("unit").HasMaxLength(16);
            product.Property(p => p.Gtin).HasColumnName("gtin").HasMaxLength(14);
            product.Property(p => p.Images).HasColumnName("images");
            product.Property(p => p.Attributes).HasColumnName("attributes");
            product.Property(p => p.Active).HasColumnName("active");
            product.Property(p => p.Discontinued).HasColumnName("discontinued");
            product.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            product.HasIndex(p => p.CategoryCode).HasDatabaseName("ix_products_category_code");
        });

        modelBuilder.Entity<CategoryRow>(category =>
        {
            category.ToTable("categories");
            category.HasKey(c => c.Code);
            category.Property(c => c.Code).HasColumnName("code").HasMaxLength(64);
            category.Property(c => c.Version).HasColumnName("version");
            category.Property(c => c.ParentCode).HasColumnName("parent_code").HasMaxLength(64);
            category.Property(c => c.Name).HasColumnName("name");
            category.Property(c => c.SortOrder).HasColumnName("sort_order");
            category.Property(c => c.Active).HasColumnName("active");
            category.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
