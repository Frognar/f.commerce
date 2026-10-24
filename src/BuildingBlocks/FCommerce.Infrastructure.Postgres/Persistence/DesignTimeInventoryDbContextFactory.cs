using FCommerce.Inventory.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeInventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(PostgresDatabaseProvider.DesignTimeOptions<InventoryDbContext>(InventoryDbContext.Schema));
}
