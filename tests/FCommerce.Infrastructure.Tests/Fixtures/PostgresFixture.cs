using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Postgres;
using FCommerce.Infrastructure.Tests.EventStore;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FCommerce.Infrastructure.Tests.Fixtures.PostgresFixture))]

namespace FCommerce.Infrastructure.Tests.Fixtures;

/// <summary>One PostgreSQL container per test run, with the messaging schema migrated.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Database.MigrateAsync();

        // Create the event store schema once, so parallel tests don't race on DDL.
        await services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    /// <summary>Infrastructure wired exactly as the host does it, against the test container.</summary>
    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFCommerceInfrastructure();
        foreach (var registration in ContractEvents.Registrations)
        {
            services.AddSingleton(registration);
        }

        services.AddPostgres(ConnectionString, applySchemaChanges: true);

        // No generic host here: tests drive processors directly and the fixture applies the schema.
        services.RemoveAll<IHostedService>();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
