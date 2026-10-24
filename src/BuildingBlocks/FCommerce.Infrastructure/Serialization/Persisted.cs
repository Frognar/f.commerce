using FCommerce.SharedKernel.Persistence;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>Trusted reading of stored values for persistence mappings (see <see cref="IPersistedValue{TSelf,TRaw}"/>).</summary>
public static class Persisted
{
    public static T Rehydrate<T, TRaw>(TRaw raw)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        T.Rehydrate(raw);

    public static TRaw Dehydrate<T, TRaw>(T value)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        value.Dehydrate();
}
