using Microsoft.EntityFrameworkCore;

namespace FCommerce.Documents.Persistence;

internal sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : DbContext(options)
{
    public const string Schema = "documents";

    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DocumentRow>(document =>
        {
            document.ToTable("documents");
            document.HasKey(d => d.Id);
            document.Property(d => d.Id).HasColumnName("id").HasMaxLength(64);
            document.Property(d => d.Version).HasColumnName("version");
            document.Property(d => d.CustomerCode).HasColumnName("customer_code").HasMaxLength(64);
            document.Property(d => d.Type).HasColumnName("type").HasMaxLength(32).HasConversion<string>();
            document.Property(d => d.Number).HasColumnName("number").HasMaxLength(64);
            document.Property(d => d.IssueDate).HasColumnName("issue_date");
            document.Property(d => d.SaleDate).HasColumnName("sale_date");
            document.Property(d => d.DueDate).HasColumnName("due_date");
            document.Property(d => d.Currency).HasColumnName("currency").HasMaxLength(3);
            document.Property(d => d.Net).HasColumnName("net").HasPrecision(15, 2);
            document.Property(d => d.Gross).HasColumnName("gross").HasPrecision(15, 2);
            document.Property(d => d.Paid).HasColumnName("paid").HasPrecision(15, 2);
            document.Property(d => d.Outstanding).HasColumnName("outstanding").HasPrecision(15, 2);
            document.Property(d => d.OrderNumber).HasColumnName("order_number").HasMaxLength(64);
            document.Property(d => d.Lines).HasColumnName("lines").HasDefaultValue("[]");
            document.Property(d => d.ReorderableLines).HasColumnName("reorderable_lines");
            document.Property(d => d.FileSize).HasColumnName("file_size");
            document.Property(d => d.FileUpdatedAt).HasColumnName("file_updated_at");
            document.Property(d => d.UpdatedAt).HasColumnName("updated_at");
            document.HasIndex(d => new { d.CustomerCode, d.IssueDate }).HasDatabaseName("ix_documents_customer_issue_date");
        });
    }
}
