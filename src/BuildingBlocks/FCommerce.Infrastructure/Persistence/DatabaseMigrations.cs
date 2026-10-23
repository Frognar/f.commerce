using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Infrastructure.Persistence;

/// <summary>Marks a DbContext whose migrations are applied by <see cref="DatabaseMigrations.MigrateDatabasesAsync"/>.</summary>
public sealed record MigratedDbContext(Type DbContextType);

public static class DatabaseMigrations
{
    public static IServiceCollection AddMigratedDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext =>
        services.AddSingleton(new MigratedDbContext(typeof(TContext)));

    /// <summary>Applies pending migrations of every registered module DbContext, one after another.</summary>
    public static async Task MigrateDatabasesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        foreach (var migrated in services.GetServices<MigratedDbContext>())
        {
            await using var scope = services.CreateAsyncScope();
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(migrated.DbContextType);
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
