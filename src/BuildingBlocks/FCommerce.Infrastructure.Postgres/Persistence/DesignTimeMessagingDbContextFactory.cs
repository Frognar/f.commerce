using FCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>; no database connection is opened.</summary>
internal sealed class DesignTimeMessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MessagingDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=fcommerce_design",
                npgsql => npgsql
                    .MigrationsAssembly(typeof(DesignTimeMessagingDbContextFactory).Assembly.GetName().Name)
                    .MigrationsHistoryTable("__ef_migrations_history", MessagingDbContext.Schema))
            .Options);
}
