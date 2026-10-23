using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace FCommerce.Infrastructure.Tests.Outbox;

public sealed class OutboxRetentionTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options, string schema) : DbContext(options)
    {
        public string Schema { get; } = schema;

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema(Schema).AddOutbox();
    }

    [Fact]
    public async Task Deletes_only_published_messages_older_than_the_retention()
    {
        var schema = $"retention{Guid.NewGuid():N}"[..20];
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        await using var services = postgres.BuildServices(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<IHostApplicationLifetime, StartedLifetime>();
            s.AddScoped(sp => new RetentionDbContext(
                new DbContextOptionsBuilder<RetentionDbContext>().UseNpgsql(postgres.ConnectionString).ReplaceService<IModelCacheKeyFactory, SchemaCacheKey>().Options,
                schema));
            s.AddOutbox<RetentionDbContext>();
        });

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetentionDbContext>();
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Token);
            db.Set<OutboxMessage>().AddRange(
                new OutboxMessage { Type = "old-published", Payload = "{}", OccurredAt = now.AddDays(-9), ProcessedAt = now.AddDays(-8) },
                new OutboxMessage { Type = "recent-published", Payload = "{}", OccurredAt = now.AddDays(-2), ProcessedAt = now.AddDays(-1) },
                new OutboxMessage { Type = "old-pending", Payload = "{}", OccurredAt = now.AddDays(-30) });
            await db.SaveChangesAsync(Token);
        }

        var deleted = await services.GetRequiredService<OutboxDispatcher<RetentionDbContext>>().DeleteExpiredAsync(Token);

        await using var check = services.CreateAsyncScope();
        var remaining = await check.ServiceProvider.GetRequiredService<RetentionDbContext>().Set<OutboxMessage>().Select(m => m.Type).OrderBy(t => t).ToListAsync(Token);
        Assert.Equal(1, deleted);
        Assert.Equal(["old-pending", "recent-published"], remaining);
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    /// <summary>EF caches models per context type; the schema differs per test, so it is part of the key.</summary>
    private sealed class SchemaCacheKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((RetentionDbContext)context).Schema, designTime);
    }
}
