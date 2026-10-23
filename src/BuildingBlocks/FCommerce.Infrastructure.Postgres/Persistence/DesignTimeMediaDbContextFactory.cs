using FCommerce.Media.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeMediaDbContextFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args) =>
        new(PostgresDatabaseProvider.DesignTimeOptions<MediaDbContext>(MediaDbContext.Schema));
}
