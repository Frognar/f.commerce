using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Persistence;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>
/// Trusted reading of stored values for persistence mappings (see <see cref="IPersistedValue{TSelf,TRaw}"/>): the rules
/// of the value are not checked again, its shape is (<see cref="StoredDataException"/> when it has none).
/// </summary>
public static class Persisted
{
    public static T Rehydrate<T, TRaw>(TRaw raw)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        T.IsWellFormed(raw) ? T.Rehydrate(raw) : throw Stored.Corrupt($"{raw} is not a {typeof(T).Name}.");

    public static TRaw Dehydrate<T, TRaw>(T value)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        value.Dehydrate();
}
