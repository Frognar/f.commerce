using FCommerce.Catalog.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeCatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(PostgresDatabaseProvider.DesignTimeOptions<CatalogDbContext>(CatalogDbContext.Schema));
}
