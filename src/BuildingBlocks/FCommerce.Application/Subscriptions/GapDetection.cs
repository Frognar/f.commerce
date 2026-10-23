namespace FCommerce.Application.Subscriptions;

/// <summary>A hole in the global log first observed at <see cref="FirstSeenAt"/>.</summary>
public sealed record PendingGap(long Position, DateTimeOffset FirstSeenAt);

/// <summary>How many events of a batch are safe to process, and the gap (if any) that stopped us.</summary>
public sealed record SafeBatch(int Count, PendingGap? Gap);

/// <summary>
/// Global positions are allocated before commit, so a transaction that commits late can make a lower
/// position appear after higher ones were already visible. Processing past such a hole would lose that
/// event for good. A gap therefore blocks the subscription until it fills or until it has been open
/// longer than the timeout, after which it is treated as a rolled-back transaction and skipped.
/// </summary>
public static class GapDetection
{
    public static SafeBatch Evaluate(
        long checkpoint,
        IReadOnlyList<long> positions,
        PendingGap? previousGap,
        DateTimeOffset now,
        TimeSpan timeout)
    {
        var expected = checkpoint + 1;
        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            if (position == expected)
            {
                expected++;
                continue;
            }

            var gapExpired = previousGap is not null
                && previousGap.Position == expected
                && now - previousGap.FirstSeenAt >= timeout;

            if (gapExpired)
            {
                expected = position + 1;
                previousGap = null;
                continue;
            }

            var gap = previousGap is not null && previousGap.Position == expected
                ? previousGap
                : new PendingGap(expected, now);

            return new SafeBatch(i, gap);
        }

        return new SafeBatch(positions.Count, null);
    }
}
