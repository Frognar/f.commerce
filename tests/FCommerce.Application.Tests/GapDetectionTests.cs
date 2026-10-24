using FCommerce.Application.Subscriptions;

namespace FCommerce.Application.Tests;

public sealed class GapDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Contiguous_positions_are_all_safe()
    {
        var result = GapDetection.Evaluate(checkpoint: 10, [11, 12, 13], previousGap: default, T0, Timeout);

        Assert.Equal(new SafeBatch(3, default), result);
    }

    [Fact]
    public void Stops_before_a_new_gap_and_remembers_when_it_was_seen()
    {
        var result = GapDetection.Evaluate(checkpoint: 10, [11, 13, 14], previousGap: default, T0, Timeout);

        Assert.Equal(new SafeBatch(1, new PendingGap(12, T0)), result);
    }

    [Fact]
    public void Gap_directly_after_checkpoint_blocks_everything()
    {
        var result = GapDetection.Evaluate(checkpoint: 10, [12], previousGap: default, T0, Timeout);

        Assert.Equal(new SafeBatch(0, new PendingGap(11, T0)), result);
    }

    [Fact]
    public void Keeps_waiting_on_a_known_gap_until_timeout()
    {
        var gap = new PendingGap(11, T0);

        var result = GapDetection.Evaluate(checkpoint: 10, [12], gap, T0 + TimeSpan.FromSeconds(4), Timeout);

        Assert.Equal(new SafeBatch(0, gap), result);
    }

    [Fact]
    public void Skips_a_gap_that_stayed_open_past_the_timeout()
    {
        var result = GapDetection.Evaluate(checkpoint: 10, [12, 13], new PendingGap(11, T0), T0 + Timeout, Timeout);

        Assert.Equal(new SafeBatch(2, default), result);
    }

    [Fact]
    public void After_skipping_an_expired_gap_a_later_gap_starts_its_own_timer()
    {
        var now = T0 + Timeout;

        var result = GapDetection.Evaluate(checkpoint: 10, [12, 14], new PendingGap(11, T0), now, Timeout);

        Assert.Equal(new SafeBatch(1, new PendingGap(13, now)), result);
    }

    [Fact]
    public void A_filled_gap_is_processed_normally()
    {
        var result = GapDetection.Evaluate(checkpoint: 10, [11, 12], new PendingGap(11, T0), T0 + TimeSpan.FromSeconds(1), Timeout);

        Assert.Equal(new SafeBatch(2, default), result);
    }

    [Fact]
    public void Empty_batch_is_safe_and_clears_the_gap()
    {
        Assert.Equal(new SafeBatch(0, default), GapDetection.Evaluate(10, [], default, T0, Timeout));
    }
}
