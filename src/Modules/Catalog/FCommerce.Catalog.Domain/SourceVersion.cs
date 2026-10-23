using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>
/// Monotonic version of a record in the source system (ERP row version, change timestamp as ticks, …).
/// Updates with a version not newer than the stored one are ignored.
/// </summary>
public sealed record SourceVersion : IComparable<SourceVersion>, IPersistedValue<SourceVersion, long>
{
    private SourceVersion(long value) => Value = value;

    public long Value { get; }

    public static Result<SourceVersion, Error> Parse(long raw) =>
        raw >= 0
            ? new SourceVersion(raw)
            : Error.Validation("version.negative", "Source version must not be negative.");

    public int CompareTo(SourceVersion? other) => other is null ? 1 : Value.CompareTo(other.Value);

    public static bool operator <(SourceVersion left, SourceVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SourceVersion left, SourceVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SourceVersion left, SourceVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SourceVersion left, SourceVersion right) => left.CompareTo(right) >= 0;

    static SourceVersion IPersistedValue<SourceVersion, long>.Rehydrate(long raw) => new(raw);

    long IPersistedValue<SourceVersion, long>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
