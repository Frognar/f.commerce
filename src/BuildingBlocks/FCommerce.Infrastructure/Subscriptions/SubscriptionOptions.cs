namespace FCommerce.Infrastructure.Subscriptions;

public sealed class SubscriptionOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int BatchSize { get; set; } = 500;

    /// <summary>How long a hole in the global log may stay open before it is treated as a rolled-back transaction.</summary>
    public TimeSpan GapTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Marks a subscription type for the <see cref="EventSubscriptionWorker"/> to run.</summary>
public sealed record SubscriptionRegistration(Type SubscriptionType);
