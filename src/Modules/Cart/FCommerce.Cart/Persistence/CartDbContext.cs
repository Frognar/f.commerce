using Microsoft.EntityFrameworkCore;

namespace FCommerce.Cart.Persistence;

/// <summary>State-based data of the cart module (carts themselves are event streams).</summary>
internal sealed class CartDbContext(DbContextOptions<CartDbContext> options) : DbContext(options)
{
    public const string Schema = "cart";

    public DbSet<ShoppingListRow> ShoppingLists => Set<ShoppingListRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ShoppingListRow>(list =>
        {
            list.ToTable("shopping_lists");
            list.HasKey(l => l.Id);
            list.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            list.Property(l => l.UserId).HasColumnName("user_id");
            list.Property(l => l.CustomerCode).HasColumnName("customer_code").HasMaxLength(64);
            list.Property(l => l.Name).HasColumnName("name").HasMaxLength(100);
            list.Property(l => l.NameKey).HasColumnName("name_key").HasMaxLength(100);
            list.Property(l => l.Items).HasColumnName("items");
            list.Property(l => l.ItemCount).HasColumnName("item_count");
            list.Property(l => l.CreatedAt).HasColumnName("created_at");
            list.Property(l => l.UpdatedAt).HasColumnName("updated_at");
            list.HasIndex(l => new { l.UserId, l.CustomerCode, l.NameKey }).IsUnique().HasDatabaseName("ux_shopping_lists_owner_name");
        });
    }
}

/// <summary>A shopping list; the items are JSON (SKU and quantity, in order).</summary>
internal sealed class ShoppingListRow
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public required string CustomerCode { get; init; }

    public required string Name { get; set; }

    public required string NameKey { get; set; }

    public required string Items { get; set; }

    public int ItemCount { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
