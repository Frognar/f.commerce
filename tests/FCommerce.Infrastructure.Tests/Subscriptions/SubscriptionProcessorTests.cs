using System.Collections.Concurrent;
using FCommerce.Application.EventStore;
using FCommerce.Application.Subscriptions;
using FCommerce.Infrastructure.Subscriptions;
using FCommerce.Infrastructure.Tests.EventStore;
using FCommerce.Infrastructure.Tests.Fixtures;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Infrastructure.Tests.Subscriptions;

public sealed class SubscriptionProcessorTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivers_accepted_events_once_and_resumes_from_checkpoint()
    {
        var category = $"sub{Guid.CreateVersion7():N}"[..20];
        await using var services = BuildServices(category);
        var store = services.GetRequiredService<IEventStore>();
        var processor = services.GetRequiredService<SubscriptionProcessor>();
        var received = services.GetRequiredService<Received>();

        var stream = Values.Stream(category, "1");
        await store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("a"), new ThingRenamed("b")], EventMetadata.None, Token);
        await store.AppendAsync(Values.NewStream("other"), ExpectedVersion.NoStream, [new ThingRenamed("ignored")], EventMetadata.None, Token);

        await DrainAsync(processor);
        await store.AppendAsync(stream, ExpectedVersion.Exact(2), [new ThingRenamed("c")], EventMetadata.None, Token);
        await DrainAsync(processor);

        Assert.Equal(["a", "b", "c"], received.Names(category));
    }

    [Fact]
    public async Task Failed_appends_do_not_block_the_subscription()
    {
        var category = $"gap{Guid.CreateVersion7():N}"[..20];
        await using var services = BuildServices(category);
        var store = services.GetRequiredService<IEventStore>();
        var processor = services.GetRequiredService<SubscriptionProcessor>();
        var received = services.GetRequiredService<Received>();

        var stream = Values.Stream(category, "1");
        await store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("a")], EventMetadata.None, Token);
        var conflict = await store.AppendAsync(stream, ExpectedVersion.NoStream, [new ThingRenamed("lost")], EventMetadata.None, Token);
        await store.AppendAsync(stream, ExpectedVersion.Exact(1), [new ThingRenamed("b")], EventMetadata.None, Token);

        conflict.ShouldFail();
        await DrainAsync(processor, until: () => received.Names(category).Count == 2);
        Assert.Equal(["a", "b"], received.Names(category));
    }

    private ServiceProvider BuildServices(string category) =>
        postgres.BuildServices(services =>
        {
            services.AddSingleton<Received>();
            services.AddSingleton(new SubscriptionOptions { GapTimeout = TimeSpan.FromMilliseconds(200), BatchSize = 1000 });
            services.AddSingleton(new CategoryFilter(category));
            services.AddEventSubscription<RecordingSubscription>();
        });

    /// <summary>Steps the subscription until it is caught up (and the optional condition holds).</summary>
    private static async Task DrainAsync(SubscriptionProcessor processor, Func<bool>? until = null)
    {
        var gap = Maybe<PendingGap>.None;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var step = await processor.StepAsync(typeof(RecordingSubscription), gap, Token);
            gap = step.Gap;
            if (step.CaughtUp && step.Gap.Match(_ => false, () => true) && (until?.Invoke() ?? true))
            {
                return;
            }

            await Task.Delay(50, Token);
        }

        throw new TimeoutException("Subscription did not catch up.");
    }

    private sealed record CategoryFilter(string Category);

    private sealed class Received
    {
        private readonly ConcurrentQueue<RecordedEvent> events = new();

        public void Add(RecordedEvent recordedEvent) => events.Enqueue(recordedEvent);

        public List<string> Names(string category) =>
            [.. events.Where(e => e.StreamId.Category == category).Select(e => ((ThingRenamed)e.Data).Name)];
    }

    private sealed class RecordingSubscription(CategoryFilter filter, Received received) : IEventSubscription
    {
        public string Name => $"test.{filter.Category}";

        public bool Accepts(RecordedEvent recordedEvent) => recordedEvent.StreamId.Category == filter.Category;

        public Task HandleAsync(RecordedEvent recordedEvent, CancellationToken cancellationToken)
        {
            received.Add(recordedEvent);
            return Task.CompletedTask;
        }
    }
}
