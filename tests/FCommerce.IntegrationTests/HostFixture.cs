using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.SharedKernel.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FCommerce.IntegrationTests.HostFixture))]

namespace FCommerce.IntegrationTests;

/// <summary>The real host against a throw-away PostgreSQL, with schema and migrations applied on startup.</summary>
public partial class HostFixture : IAsyncLifetime
{
    public const string AdminPassword = "platform owner passphrase 1";
    private readonly SemaphoreSlim adminGate = new(1, 1);
    private bool adminActivated;
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string dataPath = Path.Combine(Path.GetTempPath(), $"fcommerce-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? factory;

    public const string IntegrationApiKey = "test-integration-key";

    /// <summary>Features on by default: what the settings form posts to restore the defaults.</summary>
    public static IReadOnlyList<string> DefaultFeatures { get; } =
        [.. FCommerce.SharedKernel.Features.Feature.All.Where(f => f.EnabledByDefault).Select(f => f.Id.Value)];
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
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => Configure(host
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString())
            .UseSetting("Database:ApplyChangesOnStartup", "true")
            .UseSetting("Integration:ApiKey", IntegrationApiKey)
            .UseSetting("Search:StoragePath", Path.Combine(dataPath, "search"))
            .UseSetting("Media:StoragePath", Path.Combine(dataPath, "media"))
            .UseSetting("Access:BootstrapAdminEmail", AdminEmail)
            .UseSetting("Security:AuthenticationAttemptsPerMinute", "10000")
            .UseSetting("Api:RequestsPerMinute", "100000")
            .UseSetting("Api:RequestsPerMinutePerAddress", "100000")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IIntegrationEventHandler<ProductChanged>>(Events);
                services.AddSingleton<IIntegrationEventHandler<CustomerChanged>>(Events);
                services.AddSingleton<IIntegrationEventHandler<PortalContactsChanged>>(Events);
                services.AddSingleton<IIntegrationEventHandler<DocumentChanged>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderPlaced>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderAccepted>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderRejected>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderCancellationRequested>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderCancellationRefused>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderCancelled>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderDispatched>>(Events);
                services.AddSingleton<IIntegrationEventHandler<OrderInvoiced>>(Events);
                services.AddSingleton<IEmailSender>(Mail);
            })));
        _ = Factory.Server; // start now, so failures surface in the fixture
    }

    /// <summary>Extra configuration of a derived (isolated) host.</summary>
    protected virtual IWebHostBuilder Configure(IWebHostBuilder host) => host;

    public Browser NewBrowser() => new(CreateClient());

    /// <summary>Opens the invitation link from the latest invitation e-mail and chooses a password.</summary>
    public async Task<(System.Net.HttpStatusCode Status, string? Location)> AcceptInvitationAsync(Browser browser, string email, string password)
    {
        var mail = await Mail.WaitForAsync(email, m => m.Text.Contains("/invitation?token=", StringComparison.Ordinal));
        var link = new Uri(InvitationLink().Match(mail.Text).Value);
        var (status, _, location) = await browser.SubmitAsync(link.PathAndQuery, new Dictionary<string, string> { ["Password"] = password, ["Confirmation"] = password });
        return (status, location);
    }

    public static Task<(System.Net.HttpStatusCode Status, string Html, string? Location)> SignInAsync(Browser browser, string email, string password) =>
        browser.SubmitAsync("/pl/sign-in", new Dictionary<string, string> { ["Email"] = email, ["Password"] = password });

    /// <summary>A browser signed in as the bootstrap administrator (activated on first use).</summary>
    public async Task<Browser> AdminAsync()
    {
        var browser = NewBrowser();
        await adminGate.WaitAsync();
        try
        {
            if (!adminActivated)
            {
                await AcceptInvitationAsync(browser, AdminEmail, AdminPassword);
                adminActivated = true;
                return browser;
            }
        }
        finally
        {
            adminGate.Release();
        }

        await SignInAsync(browser, AdminEmail, AdminPassword);
        return browser;
    }

    [GeneratedRegex(@"https?://\S+/invitation\?token=[A-Za-z0-9_%-]+")]
    private static partial Regex InvitationLink();

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

    public sealed class EventRecorder
        : IIntegrationEventHandler<ProductChanged>, IIntegrationEventHandler<CustomerChanged>, IIntegrationEventHandler<PortalContactsChanged>,
            IIntegrationEventHandler<DocumentChanged>, IIntegrationEventHandler<OrderPlaced>,
            IIntegrationEventHandler<OrderAccepted>, IIntegrationEventHandler<OrderRejected>, IIntegrationEventHandler<OrderCancellationRequested>,
            IIntegrationEventHandler<OrderCancellationRefused>, IIntegrationEventHandler<OrderCancelled>, IIntegrationEventHandler<OrderDispatched>,
            IIntegrationEventHandler<OrderInvoiced>
    {
        private readonly ConcurrentQueue<ProductChanged> productChanges = new();
        private readonly ConcurrentQueue<CustomerChanged> customerChanges = new();
        private readonly ConcurrentQueue<PortalContactsChanged> portalContactChanges = new();
        private readonly ConcurrentQueue<DocumentChanged> documentChanges = new();
        private readonly ConcurrentQueue<OrderPlaced> ordersPlaced = new();
        private readonly ConcurrentQueue<IIntegrationEvent> orderEvents = new();

        public IReadOnlyCollection<ProductChanged> ProductChanges => productChanges;

        public IReadOnlyCollection<CustomerChanged> CustomerChanges => customerChanges;

        public IReadOnlyCollection<PortalContactsChanged> PortalContactChanges => portalContactChanges;

        public IReadOnlyCollection<DocumentChanged> DocumentChanges => documentChanges;

        public IReadOnlyCollection<OrderPlaced> OrdersPlaced => ordersPlaced;

        /// <summary>What happened to orders after they were placed, in the order published.</summary>
        public IReadOnlyCollection<IIntegrationEvent> OrderEvents => orderEvents;

        public Task HandleAsync(ProductChanged integrationEvent, CancellationToken cancellationToken = default)
        {
            productChanges.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }

        public Task HandleAsync(CustomerChanged integrationEvent, CancellationToken cancellationToken = default)
        {
            customerChanges.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }

        public Task HandleAsync(PortalContactsChanged integrationEvent, CancellationToken cancellationToken = default)
        {
            portalContactChanges.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }

        public Task HandleAsync(DocumentChanged integrationEvent, CancellationToken cancellationToken = default)
        {
            documentChanges.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }

        public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken = default)
        {
            ordersPlaced.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }

        public Task HandleAsync(OrderAccepted integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderRejected integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderCancellationRequested integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderCancellationRefused integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderCancelled integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderDispatched integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        public Task HandleAsync(OrderInvoiced integrationEvent, CancellationToken cancellationToken = default) => Order(integrationEvent);

        private Task Order(IIntegrationEvent integrationEvent)
        {
            orderEvents.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
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
