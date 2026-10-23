using FCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeMessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args) =>
        new(PostgresDatabaseProvider.DesignTimeOptions<MessagingDbContext>(MessagingDbContext.Schema));
}
