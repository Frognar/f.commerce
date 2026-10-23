using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Imports;

/// <summary>The shared rule for identifiers and short texts coming from a source system (ERP).</summary>
public static class SourceCodes
{
    /// <summary>Trimmed, non-empty, bounded and free of control characters.</summary>
    public static Result<string, Error> Parse(string? raw, int maxLength, string errorPrefix, string label)
    {
        var value = raw?.Trim();
        return value switch
        {
            null or "" => Error.Validation($"{errorPrefix}.empty", $"{label} is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{errorPrefix}.too-long", $"{label} must not exceed {maxLength} characters."),
            _ when value.Any(char.IsControl) => Error.Validation($"{errorPrefix}.invalid", $"{label} must not contain control characters."),
            _ => value,
        };
    }

    /// <summary>Like <see cref="Parse"/>, but blank input means "not set".</summary>
    public static Result<string?, Error> ParseOptional(string? raw, int maxLength, string errorPrefix, string label) =>
        string.IsNullOrWhiteSpace(raw) ? Result.Ok<string?, Error>(null) : Parse(raw, maxLength, errorPrefix, label).Map(v => (string?)v);
}
