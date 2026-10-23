using FCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Infrastructure.Postgres.Persistence;

/// <summary>Npgsql for every module DbContext; migrations live in this assembly, history per module schema.</summary>
internal sealed class PostgresDatabaseProvider(string connectionString) : IDatabaseProvider
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public void Configure(DbContextOptionsBuilder options, string schema) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.GetName().Name)
            .MigrationsHistoryTable(MigrationsHistoryTable, schema));

    /// <summary>Options for design-time tools (<c>dotnet ef</c>); no connection is opened.</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        new PostgresDatabaseProvider("Host=localhost;Database=fcommerce_design").Configure(builder, schema);
        return builder.Options;
    }
}
