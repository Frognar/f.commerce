namespace FCommerce.SharedKernel.Optional;

/// <summary>
/// The edge between the wire (contracts, EF rows, HTTP binding — where values may be <c>null</c>) and the code,
/// which uses <see cref="Maybe{T}"/>. The only place outside the edge with nullable signatures
/// (allowed by the architecture tests); reference types here, value types in <see cref="NullableValues"/>.
/// </summary>
public static class Nullables
{
    public static Maybe<T> ToMaybe<T>(this T? value)
        where T : class =>
        value is null ? Maybe<T>.None : value;

    public static T? OrNull<T>(this Maybe<T> maybe)
        where T : class =>
        maybe.AsEnumerable().FirstOrDefault();

    /// <summary>A required text that may arrive as <c>null</c> from the wire; missing reads as empty (and fails validation).</summary>
    public static string OrEmpty(this string? raw) => raw ?? string.Empty;

    /// <summary>A collection that may arrive as <c>null</c> from the wire; missing reads as empty.</summary>
    public static IReadOnlyList<T> OrEmpty<T>(this IReadOnlyList<T>? items) => items ?? [];

    /// <summary>Texts per key (e.g. per culture) from the wire; a missing map reads as empty, missing texts as empty texts.</summary>
    public static IReadOnlyDictionary<string, string> OrEmpty(this IReadOnlyDictionary<string, string>? raw) =>
        raw is null ? new Dictionary<string, string>()
        : raw.Values.Any(text => text is null) ? raw.ToDictionary(pair => pair.Key, pair => pair.Value.OrEmpty())
        : raw;
}

/// <summary>Like <see cref="Nullables"/>, for value types (<see cref="Nullable{T}"/>).</summary>
public static class NullableValues
{
    public static Maybe<T> ToMaybe<T>(this T? value)
        where T : struct =>
        value is { } present ? present : Maybe<T>.None;

    public static T? OrNull<T>(this Maybe<T> maybe)
        where T : struct =>
        maybe.AsEnumerable().Select(value => (T?)value).FirstOrDefault();
}
