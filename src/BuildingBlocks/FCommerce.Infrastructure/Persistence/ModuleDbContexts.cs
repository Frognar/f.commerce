using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Infrastructure.Persistence;

/// <summary>
/// Implemented by the database adapter (e.g. PostgreSQL): configures the EF Core provider for a module's
/// DbContext, including where its provider-specific migrations live.
/// </summary>
public interface IDatabaseProvider
{
    void Configure(DbContextOptionsBuilder options, string schema);
}

public static class ModuleDbContexts
{
    /// <summary>
    /// Registers a module's DbContext without naming a database: the registered <see cref="IDatabaseProvider"/>
    /// supplies the provider. Migrations of the context are applied by <see cref="DatabaseMigrations"/>.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext =>
        services
            .AddDbContext<TContext>((sp, options) => sp.GetRequiredService<IDatabaseProvider>().Configure(options, schema))
            .AddMigratedDbContext<TContext>();
}
