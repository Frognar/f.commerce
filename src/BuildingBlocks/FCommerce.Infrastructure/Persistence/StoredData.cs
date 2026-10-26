using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Infrastructure.Persistence;

/// <summary>
/// Stored data that cannot be read back into a valid model (a row or document changed outside the application, or
/// written by a bug). An infrastructure failure: reading never repairs such data with a default.
/// </summary>
public sealed class StoredDataException(string message) : Exception(message);

/// <summary>Strict reading of stored formats in persistence mappings (<c>ToDomain</c>): no silent fallbacks.</summary>
[NullableEdge]
public static class Stored
{
    public static T Required<T>(T? value, string what)
        where T : class =>
        value ?? throw Corrupt($"{what} is missing.");

    public static T Required<T>(T? value, string what)
        where T : struct =>
        value ?? throw Corrupt($"{what} is missing.");

    /// <summary>A declared name of <typeparamref name="T"/>; anything else (a number, an unknown name) is corrupt.</summary>
    public static T Enum<T>(string raw, string what)
        where T : struct, System.Enum =>
        Enums.Parse<T>(raw).Match(value => value, _ => throw Corrupt($"{what} '{raw}' is not a {typeof(T).Name}."));

    public static StoredDataException Corrupt(string detail) => new($"Corrupt stored data: {detail}");
}
