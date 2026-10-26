using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Validation;

/// <summary>Reading enum values from text: only the declared names, never numbers or combinations of flags.</summary>
public static class Enums
{
    /// <summary>One of the names declared by <typeparamref name="T"/> (ignoring case and surrounding spaces).</summary>
    public static Result<T, Error> Parse<T>(string raw, Error unknown)
        where T : struct, Enum
    {
        var name = raw.Trim();
#pragma warning disable RS0030 // The one place that may call Enum.TryParse: the name is checked against the declared ones first.
        return Enum.GetNames<T>().Any(declared => string.Equals(declared, name, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse<T>(name, ignoreCase: true, out var value)
            ? value
            : unknown;
#pragma warning restore RS0030
    }

    public static Result<T, Error> Parse<T>(string raw)
        where T : struct, Enum =>
        Parse<T>(raw, Error.Validation("enum.unknown", $"'{raw}' is not one of: {string.Join(", ", Enum.GetNames<T>())}."));
}
