using Microsoft.EntityFrameworkCore;

namespace FCommerce.Ordering.Persistence;

/// <summary>
/// Read models of orders (the orders themselves are event streams): the order list, the feed for the ERP, and the
/// counters of order numbers.
/// </summary>
internal sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public const string Schema = "ordering";

    public DbSet<OrderRow> Orders => Set<OrderRow>();

    public DbSet<OrderFeedRow> Feed => Set<OrderFeedRow>();

    public DbSet<OrderCounterRow> Counters => Set<OrderCounterRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<OrderRow>(order =>
        {
            order.ToTable("orders");
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            order.Property(o => o.Number).HasColumnName("number").HasMaxLength(32);
            order.Property(o => o.ErpNumber).HasColumnName("erp_number").HasMaxLength(64);
            order.Property(o => o.CustomerCode).HasColumnName("customer_code").HasMaxLength(64);
            order.Property(o => o.CustomerName).HasColumnName("customer_name").HasMaxLength(300);
            order.Property(o => o.SalespersonEmail).HasColumnName("salesperson_email").HasMaxLength(254);
            order.Property(o => o.PlacedBy).HasColumnName("placed_by");
            order.Property(o => o.PlacedByName).HasColumnName("placed_by_name").HasMaxLength(200);
            order.Property(o => o.PlacedByEmail).HasColumnName("placed_by_email").HasMaxLength(254);
            order.Property(o => o.PlacedAt).HasColumnName("placed_at");
            order.Property(o => o.PlacedOn).HasColumnName("placed_on");
            order.Property(o => o.Status).HasColumnName("status").HasMaxLength(20);
            order.Property(o => o.Currency).HasColumnName("currency").HasMaxLength(3);
            order.Property(o => o.Net).HasColumnName("net").HasPrecision(15, 2);
            order.Property(o => o.Gross).HasColumnName("gross").HasPrecision(15, 2);
            order.Property(o => o.LineCount).HasColumnName("line_count");
            order.Property(o => o.CustomerReference).HasColumnName("customer_reference").HasMaxLength(50);
            order.Property(o => o.UpdatedAt).HasColumnName("updated_at");
            order.HasIndex(o => o.Number).IsUnique().HasDatabaseName("ix_orders_number");
            order.HasIndex(o => o.ErpNumber).HasDatabaseName("ix_orders_erp_number");
            order.HasIndex(o => new { o.CustomerCode, o.PlacedAt }).HasDatabaseName("ix_orders_customer_placed_at");
            order.HasIndex(o => o.PlacedAt).HasDatabaseName("ix_orders_placed_at");
        });

        modelBuilder.Entity<OrderFeedRow>(feed =>
        {
            feed.ToTable("order_feed");
            feed.HasKey(f => f.Position);
            feed.Property(f => f.Position).HasColumnName("position").ValueGeneratedNever();
            feed.Property(f => f.OrderId).HasColumnName("order_id");
            feed.Property(f => f.Type).HasColumnName("type").HasMaxLength(32);
            feed.Property(f => f.At).HasColumnName("at");
        });

        modelBuilder.Entity<OrderCounterRow>(counter =>
        {
            counter.ToTable("order_counters");
            counter.HasKey(c => new { c.Prefix, c.Year });
            counter.Property(c => c.Prefix).HasColumnName("prefix").HasMaxLength(10);
            counter.Property(c => c.Year).HasColumnName("year");
            counter.Property(c => c.Last).HasColumnName("last").IsConcurrencyToken();
        });
    }
}

/// <summary>An order in the order list, kept up to date from its events.</summary>
internal sealed class OrderRow
{
    public Guid Id { get; set; }

    public required string Number { get; set; }

    public string? ErpNumber { get; set; }

    public required string CustomerCode { get; set; }

    /// <summary>Null while the customer could not be read (filled in by a later event of the order).</summary>
    public string? CustomerName { get; set; }

    /// <summary>Of the customer, for operators who see only their customers' orders.</summary>
    public string? SalespersonEmail { get; set; }

    public Guid PlacedBy { get; set; }

    /// <summary>Null while the buyer could not be read (filled in by a later event of the order).</summary>
    public string? PlacedByName { get; set; }

    public string? PlacedByEmail { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    /// <summary>The day of placing in the seller's time zone (for the date filters).</summary>
    public DateOnly PlacedOn { get; set; }

    public required string Status { get; set; }

    public required string Currency { get; set; }

    public decimal Net { get; set; }

    public decimal Gross { get; set; }

    public int LineCount { get; set; }

    public string? CustomerReference { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A feed entry for the ERP; the position is the global position of the event, so it only grows.</summary>
internal sealed class OrderFeedRow
{
    public long Position { get; set; }

    public Guid OrderId { get; set; }

    public required string Type { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>The last order number given out for a prefix and year.</summary>
internal sealed class OrderCounterRow
{
    public required string Prefix { get; set; }

    public int Year { get; set; }

    public long Last { get; set; }
}
