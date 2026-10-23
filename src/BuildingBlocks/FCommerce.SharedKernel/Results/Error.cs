using FCommerce.SharedKernel.Collections;

namespace FCommerce.SharedKernel.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
}

/// <summary>An expected failure, returned as a value instead of thrown.</summary>
/// <remarks>
/// <see cref="Field"/> names the input the error is about ("email", "lines[2].quantity").
/// <see cref="Details"/> holds the individual errors when several were collected (see <see cref="Validation.Validated{T}"/>).
/// </remarks>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public const string ValidationFailedCode = "validation.failed";

    public string? Field { get; init; }

    public EquatableArray<Error> Details { get; init; }

    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    /// <summary>One validation error that carries several collected ones in <see cref="Details"/>.</summary>
    public static Error Invalid(EquatableArray<Error> details) =>
        details.Count == 1
            ? details[0]
            : new(ErrorKind.Validation, ValidationFailedCode, $"{details.Count} validation errors.") { Details = details };

    /// <summary>Places the error under a field: "email" → "customer.email"; "[2]" → "lines[2]".</summary>
    public Error AtField(string field) =>
        this with
        {
            Field = Field switch
            {
                null => field,
                _ when Field.StartsWith('[') => field + Field,
                _ => $"{field}.{Field}",
            },
            Details = Details.Select(d => d.AtField(field)).ToEquatableArray(),
        };
}
