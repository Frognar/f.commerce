using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FCommerce.IntegrationTests.HostFixture))]

namespace FCommerce.IntegrationTests;

/// <summary>The real host against a throw-away PostgreSQL, with schema and migrations applied on startup.</summary>
public sealed class HostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private WebApplicationFactory<Program>? factory;

    public WebApplicationFactory<Program> Factory => factory ?? throw new InvalidOperationException("Not initialized.");

    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString())
            .UseSetting("Database:ApplyChangesOnStartup", "true"));
        _ = Factory.Server; // start now, so failures surface in the fixture
    }

    public async ValueTask DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }
}
