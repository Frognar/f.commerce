using Microsoft.EntityFrameworkCore;

namespace FCommerce.Inventory.Persistence;

internal sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public const string Schema = "inventory";

    public DbSet<WarehouseRow> Warehouses => Set<WarehouseRow>();

    public DbSet<StockRow> Stock => Set<StockRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WarehouseRow>(warehouse =>
        {
            warehouse.ToTable("warehouses");
            warehouse.HasKey(w => w.Code);
            warehouse.Property(w => w.Code).HasColumnName("code").HasMaxLength(64);
            warehouse.Property(w => w.Version).HasColumnName("version");
            warehouse.Property(w => w.Name).HasColumnName("name").HasMaxLength(200);
            warehouse.Property(w => w.Visible).HasColumnName("visible");
            warehouse.Property(w => w.SortOrder).HasColumnName("sort_order");
            warehouse.Property(w => w.Active).HasColumnName("active");
            warehouse.Property(w => w.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<StockRow>(stock =>
        {
            stock.ToTable("stock");
            stock.HasKey(s => new { s.WarehouseCode, s.Sku });
            stock.Property(s => s.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(64);
            stock.Property(s => s.Sku).HasColumnName("sku").HasMaxLength(64);
            stock.Property(s => s.Version).HasColumnName("version");
            stock.Property(s => s.Quantity).HasColumnName("quantity").HasPrecision(15, 3);
            stock.Property(s => s.NextDeliveryDate).HasColumnName("next_delivery_date");
            stock.Property(s => s.NextDeliveryQuantity).HasColumnName("next_delivery_quantity").HasPrecision(15, 3);
            stock.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            stock.HasOne<WarehouseRow>().WithMany().HasForeignKey(s => s.WarehouseCode).OnDelete(DeleteBehavior.Restrict);
            stock.HasIndex(s => s.Sku).HasDatabaseName("ix_stock_sku");
        });
    }
}
