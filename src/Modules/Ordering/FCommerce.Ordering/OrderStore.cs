using FCommerce.Application.EventStore;
using FCommerce.Application.Results;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Ordering.Domain;
using FCommerce.Ordering.Persistence;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Ordering;

public sealed class OrderingOptions
{
    /// <summary>Starts every order number ("WWW/2026/000123"): letters and digits.</summary>
    public string NumberPrefix { get; set; } = "WWW";

    /// <summary>IANA time zone of the seller: the year of order numbers and "today" for requested dates.</summary>
    public string TimeZone { get; set; } = "Europe/Warsaw";
}

/// <summary>The event stream category of orders; one stream per order, its id chosen when the checkout is shown.</summary>
internal sealed class OrderStream : IStreamType
{
    public static string Category => "order";

    public static StreamId For(Guid orderId) => StreamId.For<OrderStream>(orderId);

    public static Maybe<Guid> OrderId(StreamId stream) =>
        stream.Category == Category && Guid.TryParseExact(stream.Id, "N", out var id) ? id : Maybe<Guid>.None;
}

/// <summary>Now and today in the seller's time zone.</summary>
internal sealed class OrderingClock(TimeProvider time, OrderingOptions options)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DayOf(time.GetUtcNow());

    public DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);
}

/// <summary>Loads and changes orders; orders known by number are found through the order list.</summary>
internal sealed class OrderStore(IEventStore store, OrderingDbContext db, OrderingClock clock)
{
    public async Task<OrderState> LoadAsync(Guid orderId, CancellationToken cancellationToken) =>
        (await store.LoadAsync(OrderStream.For(orderId), Orders.Decider, cancellationToken)).State;

    public async Task<Result<Decision<OrderState, OrderEvent>, Error>> DecideAsync(Guid orderId, OrderCommand command, string actor, CancellationToken cancellationToken) =>
        await store.DecideAsync(
            OrderStream.For(orderId),
            Orders.Decider,
            command,
            new DecisionContext(clock.Now),
            new EventMetadata(Actor: actor),
            cancellationToken);

    /// <summary>The order with this platform number.</summary>
    public async Task<Result<Guid, Error>> FindAsync(string number, CancellationToken cancellationToken) =>
        (await db.Orders.AsNoTracking().Where(o => o.Number == number.Trim()).Select(o => o.Id).ToListAsync(cancellationToken))
            .FirstOrNone()
            .ToResult(() => Error.NotFound("order.not-found", $"There is no order '{number}'."));

    public async Task<Result<Unit, Error>> DecideByNumberAsync(string number, OrderCommand command, CancellationToken cancellationToken) =>
        await (await FindAsync(number, cancellationToken)).BindAsync(async id =>
            (await DecideAsync(id, command, "erp", cancellationToken)).Map(_ => Unit.Value));
}

/// <summary>
/// Gives out order numbers, one counter per prefix and year. Two orders at the same moment both read the counter;
/// the optimistic concurrency token lets one of them write, the other retries.
/// </summary>
internal sealed class OrderNumberCounter(OrderingDbContext db, OrderingOptions options)
{
    private const int MaxAttempts = 10;

    public async Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var counter = await db.Counters.SingleOrNoneAsync(c => c.Prefix == options.NumberPrefix && c.Year == year, cancellationToken);
            var next = counter.Match(
                row => ++row.Last,
                () =>
                {
                    db.Counters.Add(new OrderCounterRow { Prefix = options.NumberPrefix, Year = year, Last = 1 });
                    return 1L;
                });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return OrderNumbers.Format(options.NumberPrefix, year, next);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Another order took this number (or created the counter) first: read it again.
            }
        }
    }
}
