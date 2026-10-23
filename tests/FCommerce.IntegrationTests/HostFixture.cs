using System.Collections.Concurrent;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FCommerce.IntegrationTests.HostFixture))]

namespace FCommerce.IntegrationTests;

/// <summary>The real host against a throw-away PostgreSQL, with schema and migrations applied on startup.</summary>
public sealed class HostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string dataPath = Path.Combine(Path.GetTempPath(), $"fcommerce-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? factory;

    public const string IntegrationApiKey = "test-integration-key";
    public const string AdminEmail = "admin@platform.test";

    /// <summary>E-mails the host sent (instead of SMTP).</summary>
    public Mailbox Mail { get; } = new();

    public WebApplicationFactory<Program> Factory => factory ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>Integration events delivered in-process (through the outbox) during the test run.</summary>
    public EventRecorder Events { get; } = new();

    public HttpClient CreateIntegrationClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", IntegrationApiKey);
        return client;
    }

    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString())
            .UseSetting("Database:ApplyChangesOnStartup", "true")
            .UseSetting("Integration:ApiKey", IntegrationApiKey)
            .UseSetting("Search:StoragePath", Path.Combine(dataPath, "search"))
            .UseSetting("Media:StoragePath", Path.Combine(dataPath, "media"))
            .UseSetting("Access:BootstrapAdminEmail", AdminEmail)
            .UseSetting("Security:AuthenticationAttemptsPerMinute", "10000")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IIntegrationEventHandler<ProductChanged>>(Events);
                services.AddSingleton<IEmailSender>(Mail);
            }));
        _ = Factory.Server; // start now, so failures surface in the fixture
    }

    public sealed class Mailbox : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> sent = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            sent.Enqueue(message);
            return Task.CompletedTask;
        }

        /// <summary>Waits for an e-mail to the address whose text matches, and returns it.</summary>
        public async Task<EmailMessage> WaitForAsync(string to, Func<EmailMessage, bool>? matching = null, int skip = 0)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var found = sent.Where(m => m.To.Value == to && (matching?.Invoke(m) ?? true)).Skip(skip).FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No e-mail to {to}.");
        }

        public int CountTo(string to) => sent.Count(m => m.To.Value == to);
    }

    public sealed class EventRecorder : IIntegrationEventHandler<ProductChanged>
    {
        private readonly ConcurrentQueue<ProductChanged> productChanges = new();

        public IReadOnlyCollection<ProductChanged> ProductChanges => productChanges;

        public Task HandleAsync(ProductChanged integrationEvent, CancellationToken cancellationToken = default)
        {
            productChanges.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
        if (Directory.Exists(dataPath))
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
