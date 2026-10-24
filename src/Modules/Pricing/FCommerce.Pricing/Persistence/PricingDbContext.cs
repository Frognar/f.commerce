using Microsoft.EntityFrameworkCore;

namespace FCommerce.Pricing.Persistence;

internal sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : DbContext(options)
{
    public const string Schema = "pricing";

    public DbSet<PriceListRow> PriceLists => Set<PriceListRow>();

    public DbSet<PriceRow> Prices => Set<PriceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<PriceListRow>(list =>
        {
            list.ToTable("price_lists");
            list.HasKey(l => l.Code);
            list.Property(l => l.Code).HasColumnName("code").HasMaxLength(64);
            list.Property(l => l.Version).HasColumnName("version");
            list.Property(l => l.Name).HasColumnName("name").HasMaxLength(200);
            list.Property(l => l.Currency).HasColumnName("currency").HasMaxLength(3);
            list.Property(l => l.Active).HasColumnName("active");
            list.Property(l => l.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<PriceRow>(price =>
        {
            price.ToTable("prices");
            price.HasKey(p => new { p.PriceListCode, p.Sku });
            price.Property(p => p.PriceListCode).HasColumnName("price_list_code").HasMaxLength(64);
            price.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(64);
            price.Property(p => p.Version).HasColumnName("version");
            price.Property(p => p.NetPrice).HasColumnName("net_price").HasPrecision(15, 4);
            price.Property(p => p.VatRate).HasColumnName("vat_rate").HasPrecision(5, 2);
            price.Property(p => p.Breaks).HasColumnName("breaks");
            price.Property(p => p.Active).HasColumnName("active");
            price.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            price.HasOne<PriceListRow>().WithMany().HasForeignKey(p => p.PriceListCode).OnDelete(DeleteBehavior.Restrict);
            price.HasIndex(p => p.Sku).HasDatabaseName("ix_prices_sku");
        });
    }
}
