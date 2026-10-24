namespace FCommerce.SharedKernel.Persistence;

/// <summary>
/// A validated value type that is stored (in events, read models, the outbox) as its raw form.
/// Reading it back uses <see cref="Rehydrate"/>, which trusts the data: it was valid when written, and stored
/// facts must stay readable even if validation rules become stricter later (tighten old data with an upcaster).
/// Implement both members explicitly, so they are reachable only through this interface (serializers),
/// never as an ordinary factory next to <c>Parse</c>.
/// </summary>
public interface IPersistedValue<TSelf, TRaw>
    where TSelf : IPersistedValue<TSelf, TRaw>
    where TRaw : notnull
{
    static abstract TSelf Rehydrate(TRaw raw);

    TRaw Dehydrate();
}
