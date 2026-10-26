namespace FCommerce.SharedKernel.Persistence;

/// <summary>
/// A validated value type that is stored (in events, read models, the outbox) as its raw form.
/// Reading it back uses <see cref="Rehydrate"/>, which trusts the data: it was valid when written, and stored
/// facts must stay readable even if validation rules become stricter later (tighten old data with an upcaster).
/// Implement both members explicitly, so they are reachable only through this interface (serializers),
/// never as an ordinary factory next to <c>Parse</c>.
/// <see cref="IsWellFormed"/> is the exception to trusting: a raw form the type cannot represent at all (an empty list
/// for a non-empty one) is corrupt data, which the reader reports as an infrastructure failure instead of rehydrating.
/// </summary>
public interface IPersistedValue<TSelf, TRaw>
    where TSelf : IPersistedValue<TSelf, TRaw>
    where TRaw : notnull
{
    static abstract TSelf Rehydrate(TRaw raw);

    /// <summary>Whether the raw form has the shape of a value at all (not whether it passes today's rules).</summary>
    static virtual bool IsWellFormed(TRaw raw) => true;

    TRaw Dehydrate();
}
